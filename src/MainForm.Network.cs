using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using ChessMove = ChessGame.Move;

namespace ChessGame
{
    // Network play. People meet in a room (see RoomSession): two sit at the board, everybody else watches, the host decides.
    // The host is the single writer of the game: players send their requests to it, it validates them and tells everybody.
    //
    //   player -> host   MOVE <ply> <uci>   RESIGN   DRAW / DRAW_YES / DRAW_NO   NEW / NEW_YES / NEW_NO   RESYNC (anybody)
    //   host -> everyone PLAYED <ply> <uci>          a move was made (the sender of the move gets it back and ignores it)
    //                    RESULT <ply> WR|BR|DA       white / black resigned, draw agreed
    //                    OFFER DRAW|NEW W|B          that side offers a draw / a rematch;  OFFER NONE;  OFFER DECLINED DRAW|NEW
    //                    NEWGAME                     a new game starts (the seats were swapped just before)
    //                    SYNC <startPly> <moves...>  the whole game so far (several lines for a long game), then RESULT / OFFER
    //   anybody          CHAT <text>   VOICE <base64>   (checked against the permissions of the room)
    public sealed partial class MainForm
    {
        enum NetState { Idle, Connecting, InRoom }

        RoomSession room;
        HostAnnouncer announcer;
        NetState netState = NetState.Idle;
        bool roomIsSteam;
        string netInfo = "";
        string connectTarget = "";
        string hostAddresses = "";
        string steamName = "";

        // the game as the host announced it
        string drawOfferBy, newOfferBy;      // "W" / "B" or null
        string pendingMove;                  // our move "ply uci" that the host has not confirmed yet
        bool suppressFx;                     // a whole game is being replayed: no sounds, no slide animation
        bool wasFull;                        // both seats were held at the last change
        string seatSig = "";
        readonly Dictionary<string, string> knownNames = new Dictionary<string, string>();
        readonly Dictionary<string, bool> joinWants = new Dictionary<string, bool>();      // host: id -> wants to play
        readonly Dictionary<string, Seat> seatWants = new Dictionary<string, Seat>();      // host: id -> the seat that spectator asked for

        Label lblNet, capHostSeat;
        FlatButton btnHost, btnJoin, btnDisconnect, btnResign, btnDraw, btnSeat;
        BufferedTable netRowStart, netRowRoom, netRowGame;
        Segmented segTransport, segHostSeat;
        RequestStrip requests;

        internal int NetPort = NetLink.DefaultPort;

#pragma warning disable 649   // assigned only by tests
        /// <summary>Creates the Steam lobby service; tests substitute an in-memory one.</summary>
        internal Func<ILobbyService> LobbyServiceFactory;

        /// <summary>Question dialogs go through here so that tests can answer them.</summary>
        internal Func<string, string, bool> AskYesNo;
#pragma warning restore 649

        bool UseSteam { get { return segTransport.SelectedIndex == 1; } }

        // ---------------------------------------------------------------- what the room says

        bool InRoom { get { return netState == NetState.InRoom && room != null && room.Ready; } }
        bool IAmPlayer { get { return InRoom && room.MySeat != Seat.None; } }
        bool RoomFull { get { return InRoom && room.SeatHolder(Seat.White) != null && room.SeatHolder(Seat.Black) != null; } }

        /// <summary>Both seats are taken: the game can be played (otherwise it waits or is paused).</summary>
        internal bool netStarted { get { return RoomFull; } }

        internal bool netIsWhite { get { return InRoom && room.MySeat == Seat.White; } }

        /// <summary>Our colour in a network game (a spectator looks from white's side unless the board is turned).</summary>
        bool NetMineWhite
        {
            get
            {
                if (InRoom && room.MySeat != Seat.None) return room.MySeat == Seat.White;
                return !board.Flipped;
            }
        }

        int NetHumanMask()
        {
            if (!RoomFull || !IAmPlayer) return 0;
            return room.MySeat == Seat.White ? 1 : 2;
        }

        string SeatName(Seat seat)
        {
            RoomMember m = InRoom ? room.SeatHolder(seat) : null;
            return m == null ? null : m.Name;
        }

        // ---------------------------------------------------------------- UI

        /// <summary>The controls of the network part of the settings card, in display order.</summary>
        Control[] CreateNetControls()
        {
            capTransport = Caption("Соединение");
            segTransport = new Segmented();
            segTransport.Items = new string[] { "Локальная сеть", "Steam" };
            segTransport.Dock = DockStyle.Fill;
            segTransport.Margin = new Padding(0, 0, 0, Ui.S(12));

            capHostSeat = Caption("Ваше место за столом");
            segHostSeat = new Segmented();
            segHostSeat.Items = new string[] { "Белые", "Чёрные", "Зритель" };
            segHostSeat.Dock = DockStyle.Fill;
            segHostSeat.Margin = new Padding(0, 0, 0, Ui.S(12));

            lblNet = new Label();
            lblNet.Font = Ui.Font(9.25f);
            lblNet.AutoSize = false;
            lblNet.Height = Ui.S(88);
            lblNet.Dock = DockStyle.Fill;
            lblNet.Margin = new Padding(0, 0, 0, Ui.S(8));
            onCard.Add(lblNet);

            requests = new RequestStrip();
            requests.Dock = DockStyle.Fill;
            requests.Margin = new Padding(0, 0, 0, Ui.S(8));
            onCard.Add(requests);

            netRowStart = TwoColumnRow();
            btnHost = new FlatButton();
            btnHost.Text = "Создать игру";
            btnHost.Style = ButtonStyle.Primary;
            btnJoin = new FlatButton();
            btnJoin.Text = "Подключиться…";
            foreach (FlatButton b in new FlatButton[] { btnHost, btnJoin }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 0, Ui.S(4), Ui.S(8)); }
            btnJoin.Margin = new Padding(Ui.S(4), 0, 0, Ui.S(8));
            netRowStart.Controls.Add(btnHost, 0, 0);
            netRowStart.Controls.Add(btnJoin, 1, 0);

            btnDisconnect = new FlatButton();
            btnDisconnect.Text = "Выйти";
            btnDisconnect.Dock = DockStyle.Fill;
            btnDisconnect.Margin = new Padding(0, 0, Ui.S(4), Ui.S(8));

            btnSeat = new FlatButton();
            btnSeat.Text = "Сесть за стол";
            btnSeat.Dock = DockStyle.Fill;
            btnSeat.Margin = new Padding(Ui.S(4), 0, 0, Ui.S(8));

            netRowRoom = TwoColumnRow();
            netRowRoom.Controls.Add(btnDisconnect, 0, 0);
            netRowRoom.Controls.Add(btnSeat, 1, 0);

            netRowGame = TwoColumnRow();
            btnResign = new FlatButton();
            btnResign.Text = "Сдаться";
            btnResign.Glyph = "";
            btnResign.Style = ButtonStyle.Danger;
            btnDraw = new FlatButton();
            btnDraw.Text = "Предложить ничью";
            foreach (FlatButton b in new FlatButton[] { btnResign, btnDraw }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 0, Ui.S(4), Ui.S(8)); }
            btnDraw.Margin = new Padding(Ui.S(4), 0, 0, Ui.S(8));
            netRowGame.Controls.Add(btnResign, 0, 0);
            netRowGame.Controls.Add(btnDraw, 1, 0);

            btnHost.Click += delegate { NetHostClicked(); };
            btnJoin.Click += delegate { NetJoinClicked(); };
            btnDisconnect.Click += delegate { NetDisconnectClicked(); };
            btnResign.Click += delegate { NetResignClicked(); };
            btnDraw.Click += delegate { NetDrawClicked(); };
            btnSeat.Click += delegate { SeatButtonClicked(); };

            return new Control[] { capTransport, segTransport, capHostSeat, segHostSeat, lblNet, requests, netRowStart, netRowRoom, netRowGame };
        }

        BufferedTable TwoColumnRow()
        {
            BufferedTable t = new BufferedTable();
            t.Dock = DockStyle.Fill;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 2;
            t.RowCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            t.Margin = new Padding(0);
            onCard.Add(t);
            return t;
        }

        /// <summary>Shows exactly the rows of the settings card that make sense for the current mode and connection state.</summary>
        void UpdateNetUi()
        {
            if (lblNet == null) return;
            bool net = mode == Mode.Network;
            bool comp = mode == Mode.Computer;
            bool idle = netState == NetState.Idle;
            bool inRoom = net && InRoom;

            capSide.Visible = segSide.Visible = comp;
            capLevel.Visible = sliderLevel.Visible = comp;
            lblTwoHint.Visible = mode == Mode.TwoPlayers;
            capTransport.Visible = segTransport.Visible = net && idle;
            capHostSeat.Visible = segHostSeat.Visible = net && idle;
            lblNet.Visible = net;
            netRowStart.Visible = net && idle;
            netRowRoom.Visible = net && !idle;
            netRowRoom.SetColumnSpan(btnDisconnect, inRoom ? 1 : 2);
            btnDisconnect.Margin = inRoom ? new Padding(0, 0, Ui.S(4), Ui.S(8)) : new Padding(0, 0, 0, Ui.S(8));
            btnDisconnect.Text = inRoom ? "Выйти" : "Отмена";
            btnDisconnect.Tip = inRoom ? "Выйти из комнаты" : "";

            bool playing = inRoom && IAmPlayer && RoomFull && game.Status == GameStatus.Playing;
            netRowGame.Visible = playing;
            btnResign.Enabled = playing;
            btnDraw.Enabled = playing && drawOfferBy == null;

            btnSeat.Visible = inRoom;
            if (inRoom)
            {
                if (IAmPlayer) { btnSeat.Text = "В зрители"; btnSeat.Tip = "Встать из-за стола и смотреть"; btnSeat.Enabled = true; }
                else
                {
                    bool free = room.FirstFreeSeat() != Seat.None;
                    btnSeat.Text = free ? "Сесть за стол" : "Мест нет";
                    btnSeat.Tip = free ? "Сесть за свободное место (нужно согласие хоста)" : "За столом нет свободных мест";
                    btnSeat.Enabled = free;
                }
            }

            btnNew.Enabled = !net || (inRoom && IAmPlayer && RoomFull);
            btnNew.Text = (net && inRoom) ? "Реванш" : "Новая партия";

            lblNet.Height = inRoom ? Ui.S(64) : Ui.S(88);   // leave room for the chat
            if (net) lblNet.Text = NetDescription();
            UpdateRequests();
            UpdateChatUi();
        }

        string NetDescription()
        {
            switch (netState)
            {
                case NetState.Idle:
                    return netInfo.Length > 0
                        ? netInfo
                        : "Создайте игру на этом компьютере или подключитесь к чужой. Играют двое, остальные смотрят; хост решает, кого пустить.";
                case NetState.Connecting:
                    if (room != null && room.IsHost)
                        return roomIsSteam ? "Открываем комнату в Steam…" : "Открываем комнату…";
                    return roomIsSteam ? "Подключение через Steam…\nЖдём решения хоста." : "Подключение к " + connectTarget + "…\nЖдём решения хоста.";
            }
            if (!InRoom) return "";

            StringBuilder sb = new StringBuilder();
            sb.Append(IAmPlayer ? "Вы играете " + (room.MySeat == Seat.White ? "белыми" : "чёрными") : "Вы наблюдаете за игрой");
            if (room.IsHost) sb.Append(" · вы хост");
            sb.Append("\nВ комнате: ").Append(room.MemberCount).Append(roomIsSteam ? " (Steam)" : "");
            if (room.IsHost && !roomIsSteam && hostAddresses.Length > 0)
                sb.Append("\nАдрес для друзей: ").Append(hostAddresses);
            else if (room.IsHost && roomIsSteam)
                sb.Append("\nКомната открыта в Steam как «").Append(steamName).Append("»");
            if (netInfo.Length > 0) sb.Append("\n").Append(netInfo);
            return sb.ToString();
        }

        /// <summary>Replaces the generic status with the network wording ("Ваш ход", "Вы победили!").</summary>
        void NetStatus(ref StatusKind kind, ref string title, ref string sub, ref bool busy)
        {
            switch (netState)
            {
                case NetState.Idle:
                    kind = StatusKind.Normal; title = "Игра по сети"; sub = "Создайте игру или подключитесь"; busy = false; return;
                case NetState.Connecting:
                    kind = StatusKind.Waiting; busy = true;
                    if (room != null && room.IsHost) { title = "Открываем комнату"; sub = roomIsSteam ? "Игра открывается в Steam" : "Готовим подключения по сети"; }
                    else { title = "Подключение"; sub = "Ждём решения хоста…"; }
                    return;
            }
            if (!InRoom) return;

            if (!RoomFull)
            {
                bool white = room.SeatHolder(Seat.White) == null, black = room.SeatHolder(Seat.Black) == null;
                kind = StatusKind.Waiting;
                busy = true;
                title = (game.PlyCount > 0 && game.Status == GameStatus.Playing) ? "Партия приостановлена" : "Ожидание игрока";
                if (white && black) sub = "За столом свободны оба места";
                else sub = "Свободно место: " + (white ? "белые" : "чёрные");
                if (!IAmPlayer && room.FirstFreeSeat() != Seat.None) sub += ". Можно сесть за стол.";
                return;
            }

            if (game.Status == GameStatus.Playing)
            {
                if (IAmPlayer)
                {
                    bool myTurn = game.Pos.WhiteToMove == netIsWhite;
                    title = myTurn ? "Ваш ход" : "Ход соперника";
                    sub = game.InCheck ? "Шах!" : "";
                    kind = game.InCheck ? StatusKind.Check : StatusKind.Normal;
                    busy = !myTurn;
                }
                else
                {
                    string who = SeatName(game.Pos.WhiteToMove ? Seat.White : Seat.Black);
                    title = "Ход " + (game.Pos.WhiteToMove ? "белых" : "чёрных");
                    sub = (game.InCheck ? "Шах! " : "") + (who != null ? who + " · " : "") + "вы наблюдаете";
                    kind = game.InCheck ? StatusKind.Check : StatusKind.Normal;
                    busy = false;
                }
                return;
            }

            if (!IAmPlayer) return;   // spectators keep the generic wording of the result
            bool whiteWon;
            switch (game.Status)
            {
                case GameStatus.Checkmate: whiteWon = !game.Pos.WhiteToMove; break;
                case GameStatus.WhiteResigned: whiteWon = false; break;
                case GameStatus.BlackResigned: whiteWon = true; break;
                default: return;   // draws keep the generic wording
            }
            sub = whiteWon == netIsWhite ? "Вы победили!" : "Вы проиграли.";
        }

        bool Ask(string title, string question)
        {
            return Ask(title, question, "Да", "Нет");
        }

        bool Ask(string title, string question, string yes, string no)
        {
            if (AskYesNo != null) return AskYesNo(title, question);
            return AskDialog.Ask(this, title, question, yes, no);
        }

        // ---------------------------------------------------------------- opening and joining a room

#pragma warning disable 649   // assigned only by tests
        internal string PlayerNameForTest;
#pragma warning restore 649

        string PlayerName { get { return PlayerNameForTest ?? Environment.MachineName; } }

        /// <summary>
        /// The transports raise their events through the synchronization context that is current when they are created.
        /// Application.Run provides the Windows Forms one; if it is ever missing the events would arrive on a pool thread
        /// and touch the controls from there, so make sure it is in place before a room is opened.
        /// </summary>
        static void EnsureUiContext()
        {
            if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        void NetHostClicked()
        {
            if (netState != NetState.Idle) return;
            EnsureUiContext();
            int choice = segHostSeat.SelectedIndex;
            Seat seat = choice == 0 ? Seat.White : (choice == 1 ? Seat.Black : Seat.None);

            IRoomTransport transport;
            if (UseSteam)
            {
                ILobbyService svc = GetLobbyService();
                if (svc == null) return;
                steamName = svc.MyName;
                transport = SteamRoomTransport.Host(svc, svc.MyName);
                roomIsSteam = true;
            }
            else
            {
                try
                {
                    transport = LanRoomTransport.Host(NetPort, PlayerName, "h");
                }
                catch (Exception ex)
                {
                    AskDialog.Info(this, "Игра по сети", "Не удалось открыть комнату на порту " + NetPort + ":\n" + ex.Message);
                    return;
                }
                roomIsSteam = false;
                try { announcer = new HostAnnouncer(NetPort, PlayerName); }
                catch (Exception) { announcer = null; }

                List<string> addresses = new List<string>();
                foreach (System.Net.IPAddress ip in Lan.LocalAddresses())
                    addresses.Add(ip.ToString() + (NetPort != NetLink.DefaultPort ? ":" + NetPort : ""));
                hostAddresses = addresses.Count > 0 ? string.Join(", ", addresses.ToArray()) : "не удалось определить";
            }
            BeginRoom(RoomSession.CreateHost(transport, roomIsSteam ? steamName : PlayerName, seat), true);
        }

        ILobbyService GetLobbyService()
        {
            try
            {
                return LobbyServiceFactory != null ? LobbyServiceFactory() : SteamLobbyService.Acquire();
            }
            catch (SteamUnavailableException ex)
            {
                AskDialog.Info(this, "Steam", ex.Message);
                return null;
            }
        }

        void NetJoinClicked()
        {
            if (netState != NetState.Idle) return;
            if (UseSteam)
            {
                ILobbyService svc = GetLobbyService();
                if (svc == null) return;
                ulong lobby;
                bool play;
                using (SteamJoinDialog dlg = new SteamJoinDialog(svc))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    lobby = dlg.SelectedLobby;
                    play = dlg.WantsToPlay;
                }
                NetStartSteamJoin(lobby, play);
                return;
            }
            string host;
            int port;
            bool wantsPlay;
            using (JoinDialog dlg = new JoinDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                host = dlg.HostName;
                port = dlg.Port;
                wantsPlay = dlg.WantsToPlay;
            }
            NetStartJoin(host, port, wantsPlay);
        }

        internal void NetStartSteamJoin(ulong lobbyId, bool wantsPlay)
        {
            if (netState != NetState.Idle) return;
            EnsureUiContext();
            ILobbyService svc = GetLobbyService();
            if (svc == null) return;
            roomIsSteam = true;
            steamName = svc.MyName;
            BeginRoom(RoomSession.CreateClient(SteamRoomTransport.Join(svc, lobbyId, svc.MyName), svc.MyName, wantsPlay), false);
        }

        internal void NetStartJoin(string host, int port, bool wantsPlay)
        {
            if (netState != NetState.Idle) return;
            EnsureUiContext();
            roomIsSteam = false;
            connectTarget = host + ":" + port;
            BeginRoom(RoomSession.CreateClient(LanRoomTransport.Join(host, port, PlayerName, LanKey()), PlayerName, wantsPlay), false);
        }

        /// <summary>Tests run several windows in one process, each of which must look like its own installation.</summary>
#pragma warning disable 649   // assigned only by tests
        internal string LanKeyForTest;
#pragma warning restore 649

        /// <summary>A random secret that stays with this installation: the host recognises a returning player by it.</summary>
        string LanKey()
        {
            if (LanKeyForTest != null) return LanKeyForTest;
            string key = Settings.Get("lankey", "");
            if (key.Length < 16)
            {
                key = Guid.NewGuid().ToString("N");
                Settings.Set("lankey", key);
                Settings.Save();
            }
            return key;
        }

        void BeginRoom(RoomSession s, bool hosting)
        {
            room = s;
            netState = NetState.Connecting;
            netInfo = "";
            drawOfferBy = newOfferBy = null;
            pendingMove = null;
            joinWants.Clear();
            seatWants.Clear();
            knownNames.Clear();
            wasFull = false;
            seatSig = "";
            game.NewGame();
            board.Flipped = false;
            WireRoom(s);
            s.Start();
            ApplyMode();
            RefreshAll();
        }

        void WireRoom(RoomSession s)
        {
            s.Welcomed += delegate { if (s == room) OnRoomWelcomed(); };
            s.Changed += delegate { if (s == room) OnRoomChanged(); };
            s.JoinRequested += delegate(string id, string name, bool play) { if (s == room) OnJoinRequested(id, play); };
            s.SeatRequested += delegate(string id, Seat seat) { if (s == room) OnSeatRequested(id, seat); };
            s.MemberJoined += delegate(string id) { if (s == room) OnMemberJoined(id); };
            s.MemberLeft += delegate(string id) { if (s == room) OnMemberLeft(id); };
            s.NeedsSync += delegate(string id) { if (s == room && room.IsHost) HostSendState(id); };
            s.GameLine += delegate(string from, string line) { if (s == room) OnRoomLine(from, line); };
            s.HostChanged += delegate(string oldId, string newId) { if (s == room) OnHostChanged(oldId, newId); };
            s.HostOffered += delegate(string from, string name) { if (s == room) OnHostOffered(name); };
            s.Notice += delegate(string text) { if (s == room) OnRoomNotice(text); };
            s.Rejected += delegate(string reason) { if (s == room) RoomEnded(reason.Length > 0 ? reason : "Хост не принял вас в игру."); };
            s.Removed += delegate(string reason) { if (s == room) RoomEnded(reason); };
            s.Closed += delegate(string reason) { if (s == room) RoomEnded(reason); };
        }

        void NetDisconnectClicked()
        {
            if (InRoom && room.IsHost && room.MemberCount > 1
                && !Ask("Закрыть комнату?", "Вы хост: если выйти, комната закроется для всех. Чтобы остальные продолжили, сначала передайте хост (вкладка «Люди»). Выйти?",
                        "Выйти", "Остаться")) return;
            NetShutdown(InRoom ? "Вы вышли из комнаты." : "");
            ApplyMode();
            RefreshAll();
        }

        /// <summary>The room is gone (closed by the host, we were removed, the connection failed...).</summary>
        void RoomEnded(string reason)
        {
            NetShutdown(reason);
            ApplyMode();
            RefreshAll();
        }

        /// <summary>Closes the room and returns to the idle state. Callers refresh the UI.</summary>
        void NetShutdown(string info)
        {
            RoomSession r = room;
            room = null;
            if (r != null) r.Dispose();
            if (announcer != null) { announcer.Dispose(); announcer = null; }
            Popup.CloseAll();
            ResetChat();
            roomIsSteam = false;
            netState = NetState.Idle;
            drawOfferBy = newOfferBy = null;
            pendingMove = null;
            joinWants.Clear();
            seatWants.Clear();
            knownNames.Clear();
            wasFull = false;
            seatSig = "";
            netInfo = info;
            UpdatePeople();
        }

        // ---------------------------------------------------------------- room events

        string SeatSignature()
        {
            RoomMember w = room.SeatHolder(Seat.White), b = room.SeatHolder(Seat.Black);
            return (w == null ? "-" : w.Id) + "|" + (b == null ? "-" : b.Id);
        }

        void RememberNames()
        {
            foreach (RoomMember m in room.Members) knownNames[m.Id] = m.Name;
        }

        string NameOf(string id)
        {
            string n;
            if (knownNames.TryGetValue(id, out n)) return n;
            return "участник";
        }

        void OnRoomWelcomed()
        {
            netState = NetState.InRoom;
            netInfo = "";
            RememberNames();
            seatSig = SeatSignature();
            wasFull = RoomFull;
            FaceMySide();
            if (IAmPlayer)
                chat.AddSystem("Вы в комнате и играете " + (room.MySeat == Seat.White ? "белыми" : "чёрными")
                    + ". Переписывайтесь здесь; кнопка с микрофоном включает голос.");
            else
                chat.AddSystem("Вы в комнате как зритель. Писать и говорить можно, пока хост не запретил.");
            if (announcer != null && room.MemberCount >= RoomSession.MaxMembers) { announcer.Dispose(); announcer = null; }
            ApplyMode();
            RefreshAll();
        }

        void OnRoomChanged()
        {
            if (netState != NetState.InRoom) return;
            RememberNames();
            bool full = RoomFull;
            string sig = SeatSignature();
            bool seatsChanged = sig != seatSig;
            seatSig = sig;

            if (room.IsHost)
            {
                if (seatsChanged && (drawOfferBy != null || newOfferBy != null)) HostClearOffers();
                if (full && !wasFull && game.Status != GameStatus.Playing) HostStartNewGame();   // somebody sat down after a finished game
            }
            wasFull = full;
            if (seatsChanged)
            {
                if (IAmPlayer) FaceMySide();
                drawOfferBy = drawOfferBy != null && full ? drawOfferBy : null;
            }

            int mask = NetHumanMask();
            if (board.HumanMask != mask) board.HumanMask = mask;
            if (announcer != null && room.MemberCount >= RoomSession.MaxMembers) { announcer.Dispose(); announcer = null; }
            UpdateChatPermissions();
            RefreshAll();
        }

        /// <summary>Our pieces at the bottom.</summary>
        void FaceMySide()
        {
            if (InRoom && room.MySeat != Seat.None) board.Flipped = room.MySeat == Seat.Black;
        }

        void OnMemberJoined(string id)
        {
            if (netState != NetState.InRoom || id == room.MyId) return;
            RememberNames();
            chat.AddSystem("Участник «" + NameOf(id) + "» вошёл в комнату");
            if (Form.ActiveForm != this) Sounds.Play(Sfx.Notify);
        }

        void OnMemberLeft(string id)
        {
            if (netState != NetState.InRoom) return;
            chat.AddSystem("Участник «" + NameOf(id) + "» покинул комнату");
            if (voice != null) voice.ForgetSender(id);
            joinWants.Remove(id);
            seatWants.Remove(id);
            knownNames.Remove(id);
        }

        void OnHostChanged(string oldId, string newId)
        {
            joinWants.Clear();
            seatWants.Clear();
            RememberNames();
            if (netState == NetState.InRoom)
            {
                if (newId == room.MyId) chat.AddSystem("Вы теперь хост комнаты");
                else chat.AddSystem("Новый хост комнаты: «" + NameOf(newId) + "»");
            }
            // a move that the old host never answered goes to the new one
            if (pendingMove != null && room != null && !room.IsHost && game.PlyCount > 0)
            {
                string[] pm = pendingMove.Split(' ');
                int ply;
                if (int.TryParse(pm[0], out ply) && ply == game.PlyCount - 1) room.SendToHost("MOVE " + pendingMove);
                else pendingMove = null;
            }
            if (roomIsSteam && room != null && room.IsHost) steamName = room.MyName;
        }

        void OnRoomNotice(string text)
        {
            netInfo = text;
            if (chat != null) chat.AddSystem(text);
            if (mode == Mode.Network) RefreshAll();
        }

        void OnHostOffered(string name)
        {
            Sounds.Play(Sfx.Notify);
            UpdateRequests();
        }

        void OnJoinRequested(string id, bool wantsPlay)
        {
            joinWants[id] = wantsPlay;
            Sounds.Play(Sfx.Notify);
            RefreshAll();
        }

        void OnSeatRequested(string id, Seat wanted)
        {
            seatWants[id] = wanted;
            Sounds.Play(Sfx.Notify);
            RefreshAll();
        }

        // ---------------------------------------------------------------- game lines

        void OnRoomLine(string from, string line)
        {
            if (mode != Mode.Network || netState != NetState.InRoom) return;
            string[] p = line.Split(' ');
            switch (p[0])
            {
                case "CHAT": OnNetChat(from, line); return;
                case "VOICE": OnNetVoice(from, line); return;
            }
            if (room.IsHost) HostProcess(from, p); else ReplicaApply(p);
        }

        /// <summary>A player's action: the host handles it itself, everybody else asks the host.</summary>
        void SubmitPlayerLine(string line)
        {
            if (!InRoom) return;
            if (room.IsHost) HostProcess(room.MyId, line.Split(' '));
            else room.SendToHost(line);
        }

        // ---- host: validates and announces

        void HostProcess(string from, string[] p)
        {
            RoomMember m = room.Find(from);
            if (m == null) return;
            if (p[0] == "RESYNC") { HostSendState(from); return; }
            if (m.Seat == Seat.None) return;
            bool white = m.Seat == Seat.White;
            string side = white ? "W" : "B";
            string other = white ? "B" : "W";

            switch (p[0])
            {
                case "MOVE":
                    HostMove(from, white, p);
                    return;

                case "RESIGN":
                    if (!RoomFull || game.Status != GameStatus.Playing) return;
                    game.Terminate(white ? GameStatus.WhiteResigned : GameStatus.BlackResigned);
                    VoidOffers();
                    room.Broadcast("RESULT " + game.PlyCount + " " + (white ? "WR" : "BR"));
                    RefreshAll();
                    return;

                case "DRAW":
                    if (!RoomFull || game.Status != GameStatus.Playing) return;
                    if (drawOfferBy == other) { HostAgreeDraw(); return; }   // both asked: that is an agreement
                    if (drawOfferBy != null) return;
                    drawOfferBy = side;
                    room.Broadcast("OFFER DRAW " + side);
                    RefreshAll();
                    return;

                case "DRAW_YES":
                    if (RoomFull && game.Status == GameStatus.Playing && drawOfferBy == other) HostAgreeDraw();
                    return;

                case "DRAW_NO":
                    if (drawOfferBy == other)
                    {
                        if (IAmPlayer && drawOfferBy == (room.MySeat == Seat.White ? "W" : "B")) netInfo = "Соперник отклонил ничью.";   // our own offer
                        drawOfferBy = null;
                        room.Broadcast("OFFER DECLINED DRAW");
                        RefreshAll();
                    }
                    return;

                case "NEW":
                    if (!RoomFull) return;
                    if (newOfferBy == other) { HostRematch(); return; }
                    if (newOfferBy != null) return;
                    newOfferBy = side;
                    room.Broadcast("OFFER NEW " + side);
                    RefreshAll();
                    return;

                case "NEW_YES":
                    if (RoomFull && newOfferBy == other) HostRematch();
                    return;

                case "NEW_NO":
                    if (newOfferBy == other)
                    {
                        if (IAmPlayer && newOfferBy == (room.MySeat == Seat.White ? "W" : "B")) netInfo = "Соперник отказался от новой партии.";
                        newOfferBy = null;
                        room.Broadcast("OFFER DECLINED NEW");
                        RefreshAll();
                    }
                    return;
            }
        }

        void HostMove(string from, bool white, string[] p)
        {
            int ply;
            if (p.Length != 3 || !int.TryParse(p[1], out ply)) { HostSendState(from); return; }
            if (!RoomFull || game.Status != GameStatus.Playing || ply != game.PlyCount || game.Pos.WhiteToMove != white)
            {
                // a late or duplicated request: the sender's board is out of step, show it the real game
                if (from != room.MyId) HostSendState(from);
                return;
            }
            foreach (ChessMove m in game.Legal)
            {
                if (m.ToString() == p[2])
                {
                    game.Play(m);
                    VoidOffers();
                    netInfo = "";
                    room.Broadcast("PLAYED " + ply + " " + m);
                    RefreshAll();
                    return;
                }
            }
            if (from != room.MyId) HostSendState(from);
        }

        void HostAgreeDraw()
        {
            game.Terminate(GameStatus.DrawAgreed);
            VoidOffers();
            room.Broadcast("RESULT " + game.PlyCount + " DA");
            RefreshAll();
        }

        void HostRematch()
        {
            VoidOffers();
            room.SwapSeats();       // the colours change first, then the new game starts
            HostStartNewGame();
        }

        void HostStartNewGame()
        {
            VoidOffers();
            game.NewGame();
            board.ResetSelection();
            netInfo = "";
            room.Broadcast("NEWGAME");
            if (chat != null) chat.AddSystem("Новая партия");
            FaceMySide();
            RefreshAll();
        }

        /// <summary>Seats changed: an offer made to somebody who is no longer at the table lapses.</summary>
        void HostClearOffers()
        {
            VoidOffers();
            room.Broadcast("OFFER NONE");
        }

        void VoidOffers()
        {
            drawOfferBy = newOfferBy = null;
        }

        /// <summary>Sends the whole game to one member (after it joined, or when it lost the thread).</summary>
        void HostSendState(string id)
        {
            if (!InRoom || id == room.MyId) return;
            List<string> moves = new List<string>();
            for (int i = 0; i < game.PlyCount; i++) moves.Add(game.MoveAt(i).ToString());
            int start = 0;
            StringBuilder sb = new StringBuilder();
            do
            {
                sb.Length = 0;
                sb.Append("SYNC ").Append(start);
                int taken = 0;
                while (start + taken < moves.Count && sb.Length < 1700)
                {
                    sb.Append(' ').Append(moves[start + taken]);
                    taken++;
                }
                room.SendTo(id, sb.ToString());
                start += taken;
            }
            while (start < moves.Count);

            switch (game.Status)
            {
                case GameStatus.WhiteResigned: room.SendTo(id, "RESULT " + game.PlyCount + " WR"); break;
                case GameStatus.BlackResigned: room.SendTo(id, "RESULT " + game.PlyCount + " BR"); break;
                case GameStatus.DrawAgreed: room.SendTo(id, "RESULT " + game.PlyCount + " DA"); break;
            }
            if (drawOfferBy != null) room.SendTo(id, "OFFER DRAW " + drawOfferBy);
            if (newOfferBy != null) room.SendTo(id, "OFFER NEW " + newOfferBy);
        }

        // ---- everybody else: applies what the host announced

        void AskForResync()
        {
            if (InRoom && !room.IsHost) room.SendToHost("RESYNC");
        }

        void ReplicaApply(string[] p)
        {
            switch (p[0])
            {
                case "PLAYED":
                {
                    int ply;
                    if (p.Length != 3 || !int.TryParse(p[1], out ply)) return;
                    if (ply < game.PlyCount)
                    {
                        // our own move coming back, or one we already know
                        if (game.MoveAt(ply).ToString() != p[2]) AskForResync();
                        else if (pendingMove != null && pendingMove.StartsWith(ply + " ", StringComparison.Ordinal)) pendingMove = null;
                        return;
                    }
                    if (ply > game.PlyCount || game.Status != GameStatus.Playing) { AskForResync(); return; }
                    foreach (ChessMove m in game.Legal)
                    {
                        if (m.ToString() == p[2])
                        {
                            game.Play(m);
                            VoidOffers();
                            netInfo = "";
                            RefreshAll();
                            return;
                        }
                    }
                    AskForResync();
                    return;
                }

                case "RESULT":
                {
                    int ply;
                    if (p.Length != 3 || !int.TryParse(p[1], out ply)) return;
                    if (ply != game.PlyCount) { AskForResync(); return; }
                    GameStatus status;
                    switch (p[2])
                    {
                        case "WR": status = GameStatus.WhiteResigned; break;
                        case "BR": status = GameStatus.BlackResigned; break;
                        case "DA": status = GameStatus.DrawAgreed; break;
                        default: return;
                    }
                    if (game.Status == GameStatus.Playing) game.Terminate(status);
                    VoidOffers();
                    pendingMove = null;
                    RefreshAll();
                    return;
                }

                case "OFFER":
                    ApplyOffer(p);
                    return;

                case "NEWGAME":
                    VoidOffers();
                    pendingMove = null;
                    game.NewGame();
                    board.ResetSelection();
                    netInfo = "";
                    chat.AddSystem("Новая партия");
                    FaceMySide();
                    RefreshAll();
                    return;

                case "SYNC":
                    ApplySync(p);
                    return;
            }
        }

        void ApplyOffer(string[] p)
        {
            if (p.Length < 2) return;
            switch (p[1])
            {
                case "DRAW": if (p.Length == 3 && (p[2] == "W" || p[2] == "B")) drawOfferBy = p[2]; break;
                case "NEW": if (p.Length == 3 && (p[2] == "W" || p[2] == "B")) newOfferBy = p[2]; break;
                case "NONE": VoidOffers(); break;
                case "DECLINED":
                    if (p.Length == 3 && p[2] == "DRAW")
                    {
                        bool mine = IAmPlayer && drawOfferBy == (room.MySeat == Seat.White ? "W" : "B");
                        drawOfferBy = null;
                        if (mine) netInfo = "Соперник отклонил ничью.";
                    }
                    else if (p.Length == 3 && p[2] == "NEW")
                    {
                        bool mine = IAmPlayer && newOfferBy == (room.MySeat == Seat.White ? "W" : "B");
                        newOfferBy = null;
                        if (mine) netInfo = "Соперник отказался от новой партии.";
                    }
                    break;
            }
            RefreshAll();
        }

        void ApplySync(string[] p)
        {
            int start;
            if (p.Length < 2 || !int.TryParse(p[1], out start)) return;
            if (start == 0)
            {
                VoidOffers();
                pendingMove = null;
            }
            else if (start != game.PlyCount) return;

            bool animations = board.AnimationsEnabled;
            suppressFx = true;
            board.AnimationsEnabled = false;
            try
            {
                if (start == 0) game.NewGame();
                for (int i = 2; i < p.Length; i++)
                {
                    bool found = false;
                    foreach (ChessMove m in game.Legal)
                    {
                        if (m.ToString() == p[i]) { game.Play(m); found = true; break; }
                    }
                    if (!found) break;
                }
            }
            finally
            {
                suppressFx = false;
                board.AnimationsEnabled = animations;
            }
            board.ResetSelection();
            RefreshAll();
        }

        // ---------------------------------------------------------------- our own actions

        void NetAfterLocalMove(ChessMove m)
        {
            if (!InRoom) return;
            int ply = game.PlyCount - 1;
            netInfo = "";
            VoidOffers();
            if (room.IsHost)
            {
                room.Broadcast("PLAYED " + ply + " " + m);
            }
            else
            {
                pendingMove = ply + " " + m;
                room.SendToHost("MOVE " + pendingMove);
            }
        }

        void NetResignClicked()
        {
            if (!InRoom || !IAmPlayer || !RoomFull || game.Status != GameStatus.Playing) return;
            if (!Ask("Сдаться", "Вы действительно хотите сдаться?")) return;
            if (!InRoom || !RoomFull || game.Status != GameStatus.Playing) return;
            netInfo = "";
            SubmitPlayerLine("RESIGN");   // the result arrives from the host (a resignation only counts when the host has seen it)
            RefreshAll();
        }

        void NetDrawClicked()
        {
            if (!InRoom || !IAmPlayer || !RoomFull || game.Status != GameStatus.Playing || drawOfferBy != null) return;
            netInfo = "";
            SubmitPlayerLine("DRAW");
            RefreshAll();
        }

        void NetRequestNewGame()
        {
            if (!InRoom || !IAmPlayer || !RoomFull) return;
            string mySide = room.MySeat == Seat.White ? "W" : "B";
            if (newOfferBy == mySide) return;
            netInfo = "Новая партия предложена, ждём ответа…";
            SubmitPlayerLine("NEW");
            RefreshAll();
        }

        void SeatButtonClicked()
        {
            if (!InRoom) return;
            if (IAmPlayer) StandUp(); else SitDown(Seat.None);
        }

        void StandUp()
        {
            if (!IAmPlayer) return;
            if (RoomFull && game.PlyCount > 0 && game.Status == GameStatus.Playing
                && !Ask("Встать из-за стола", "Вы перейдёте в зрители, партия будет приостановлена. Продолжить?")) return;
            if (!InRoom || !IAmPlayer) return;
            room.RequestStand();
        }

        void SitDown(Seat seat)
        {
            if (!InRoom || IAmPlayer) return;
            room.RequestSeat(seat);
        }

        // ---------------------------------------------------------------- requests (non-modal)

        void UpdateRequests()
        {
            if (requests == null) return;
            List<RequestItem> items = new List<RequestItem>();
            if (mode == Mode.Network && InRoom)
            {
                if (room.IsHost)
                {
                    foreach (string id in room.PendingIds)
                    {
                        bool wants;
                        if (!joinWants.TryGetValue(id, out wants)) continue;
                        string who = room.PendingName(id);
                        RequestItem it = new RequestItem();
                        it.Key = "join:" + id;
                        string target = id;
                        if (wants && room.FirstFreeSeat() != Seat.None)
                            it.Text = "«" + who + "» хочет играть. Принять?";
                        else if (wants)
                            it.Text = "«" + who + "» хочет играть, но за столом нет мест. Принять зрителем?";
                        else
                            it.Text = "«" + who + "» хочет наблюдать. Принять?";
                        it.YesText = "Принять";
                        it.NoText = "Отклонить";
                        it.Yes = delegate { ApproveJoin(target); };
                        it.No = delegate { RejectJoin(target); };
                        items.Add(it);
                    }
                    foreach (KeyValuePair<string, Seat> kv in new List<KeyValuePair<string, Seat>>(seatWants))
                    {
                        RoomMember m = room.Find(kv.Key);
                        Seat target = kv.Value != Seat.None ? kv.Value : room.FirstFreeSeat();
                        if (m == null || m.Seat != Seat.None || target == Seat.None || room.SeatHolder(target) != null)
                        {
                            seatWants.Remove(kv.Key);
                            continue;
                        }
                        RequestItem it = new RequestItem();
                        it.Key = "seat:" + kv.Key;
                        string id = kv.Key;
                        it.Text = "«" + m.Name + "» хочет сесть за стол (" + (target == Seat.White ? "белые" : "чёрные") + ").";
                        it.YesText = "Посадить";
                        it.NoText = "Отказать";
                        it.Yes = delegate { ApproveSeat(id); };
                        it.No = delegate { seatWants.Remove(id); RefreshAll(); };
                        items.Add(it);
                    }
                }
                string offeredBy = room.HostOfferName;
                if (offeredBy != null && !room.IsHost)
                {
                    RequestItem it = new RequestItem();
                    it.Key = "host";
                    it.Text = "«" + offeredBy + "» предлагает вам стать хостом комнаты. Игра переедет на ваш компьютер.";
                    it.YesText = "Согласиться";
                    it.NoText = "Отказаться";
                    it.Yes = delegate { room.AnswerHostOffer(true); RefreshAll(); };
                    it.No = delegate { room.AnswerHostOffer(false); RefreshAll(); };
                    items.Add(it);
                }
                if (IAmPlayer && RoomFull)
                {
                    string mySide = room.MySeat == Seat.White ? "W" : "B";
                    if (drawOfferBy != null && drawOfferBy != mySide && game.Status == GameStatus.Playing)
                    {
                        RequestItem it = new RequestItem();
                        it.Key = "draw";
                        it.Text = "Соперник предлагает ничью.";
                        it.YesText = "Принять";
                        it.NoText = "Отклонить";
                        it.Yes = delegate { SubmitPlayerLine("DRAW_YES"); RefreshAll(); };
                        it.No = delegate { SubmitPlayerLine("DRAW_NO"); RefreshAll(); };
                        items.Add(it);
                    }
                    if (newOfferBy != null && newOfferBy != mySide)
                    {
                        RequestItem it = new RequestItem();
                        it.Key = "new";
                        it.Text = "Соперник предлагает сыграть ещё партию (цвета поменяются).";
                        it.YesText = "Принять";
                        it.NoText = "Отказаться";
                        it.Yes = delegate { SubmitPlayerLine("NEW_YES"); RefreshAll(); };
                        it.No = delegate { SubmitPlayerLine("NEW_NO"); RefreshAll(); };
                        items.Add(it);
                    }
                }
            }
            shownRequests = items;
            requests.SetItems(items);
        }

        List<RequestItem> shownRequests = new List<RequestItem>();

        void ApproveJoin(string id)
        {
            if (!InRoom || !room.IsHost || !room.IsPending(id)) return;
            bool wants;
            joinWants.TryGetValue(id, out wants);
            joinWants.Remove(id);
            room.Approve(id, wants ? room.FirstFreeSeat() : Seat.None);
            RefreshAll();
        }

        void RejectJoin(string id)
        {
            if (!InRoom || !room.IsHost) return;
            joinWants.Remove(id);
            room.Reject(id, "Хост не принял вас в игру.");
            RefreshAll();
        }

        void ApproveSeat(string id)
        {
            if (!InRoom || !room.IsHost) return;
            Seat wanted;
            if (!seatWants.TryGetValue(id, out wanted)) return;
            seatWants.Remove(id);
            Seat seat = wanted != Seat.None ? wanted : room.FirstFreeSeat();
            if (seat != Seat.None) room.SetSeat(id, seat);
            RefreshAll();
        }

        // ---------------------------------------------------------------- test access

        internal void TestSetHostSeat(int index) { segHostSeat.SelectedIndex = index; }
        internal bool IAmPlayerForTest { get { return IAmPlayer; } }
        internal bool InRoomForTest { get { return InRoom; } }
        internal string NetInfoForTest { get { return netInfo; } }
        internal string RequestTextForTest(int index) { return index < shownRequests.Count ? shownRequests[index].Text : null; }
        internal string TestSeatButtonText { get { return btnSeat.Text; } }
        internal RoomSession RoomForTest { get { return room; } }
        internal string NetStateForTest { get { return netState.ToString(); } }
        internal int RequestCountForTest { get { return shownRequests.Count; } }

        /// <summary>Presses Yes / No of the first request whose key starts with 'keyStart' (join:, seat:, draw, new, host).</summary>
        internal bool TestAnswerRequest(string keyStart, bool yes)
        {
            foreach (RequestItem it in shownRequests.ToArray())
            {
                if (!it.Key.StartsWith(keyStart, StringComparison.Ordinal)) continue;
                Action a = yes ? it.Yes : it.No;
                if (a != null) a();
                return true;
            }
            return false;
        }
        internal string DrawOfferForTest { get { return drawOfferBy; } }
    }
}
