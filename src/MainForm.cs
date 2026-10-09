using System;
using ChessMove = ChessGame.Move;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChessGame
{
    public sealed partial class MainForm : Form
    {
        sealed class Level
        {
            public readonly string Name;
            public readonly int Depth;
            public readonly int TimeMs;
            public readonly int Noise;

            public Level(string name, int depth, int timeMs, int noise)
            {
                Name = name; Depth = depth; TimeMs = timeMs; Noise = noise;
            }
        }

        static readonly Level[] Levels =
        {
            new Level("Новичок", 1, 1000, 160),
            new Level("Любитель", 2, 2000, 60),
            new Level("Средний", 3, 3000, 15),
            new Level("Сильный", 6, 2500, 0),
            new Level("Мастер", 10, 6000, 0),
        };

        /// <summary>Keeps the player strips exactly as wide as the board and centres the whole block.</summary>
        sealed class BoardArea : BufferedPanel
        {
            public PlayerBar TopBar, BottomBar;
            public BoardControl Board;

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (TopBar == null || Board == null) return;
                int pad = Ui.S(18), gap = Ui.S(8), barH = TopBar.Height, m = Ui.S(8);
                int availW = ClientSize.Width - pad * 2;
                int availH = ClientSize.Height - pad * 2 - 2 * (barH + gap);
                int cell = Math.Max(24, (Math.Min(availW, availH) - 2 * m) / 8);
                int boardSize = cell * 8 + 2 * m;
                int totalH = barH + gap + cell * 8 + gap + barH;
                int x0 = (ClientSize.Width - boardSize) / 2;
                int y0 = Math.Max(pad / 2, (ClientSize.Height - totalH) / 2);
                TopBar.SetBounds(x0 + m, y0, cell * 8, barH);
                Board.SetBounds(x0, y0 + barH + gap - m, boardSize, boardSize);
                BottomBar.SetBounds(x0 + m, y0 + barH + gap + cell * 8 + gap, cell * 8, barH);
            }
        }

        readonly Game game = new Game();
        readonly Engine engine = new Engine();
        Engine hintEngine;

        enum Mode { Computer, TwoPlayers, Network }
        Mode mode = Mode.Computer;

        // layout
        BoardArea boardArea;
        BoardControl board;
        PlayerBar topBar, bottomBar;
        Panel side;
        BufferedTable sideTable, headerTable, actionsTable, settingsTable;
        Label lblTitle, capSide, capLevel, capTransport, lblTwoHint, movesTitle;
        FlatButton btnBoardStyle, btnSound, btnTheme;
        Segmented segMode, segSide;
        StepSlider sliderLevel;
        StatusCard status;
        CardPanel settingsCard, movesCard;
        MoveListControl moveList;
        FlatButton btnNew, btnUndo, btnHint, btnFlip;
        readonly List<Control> onCard = new List<Control>();   // controls that sit on a card and need the card colour

        readonly Timer premoveTimer = new Timer();   // a short pause so that the opponent's move is seen before ours follows

        bool aiBusy;
        int aiToken;
        bool closing;
        bool loadingSettings;
        bool hintBusy;
        int hintToken;
        int lastSideIndex;
        GameStatus lastStatus = GameStatus.Playing;

        // looking at an earlier position (read-only); -1 = following the live game
        int viewPly = -1;
        Position viewPos;

        public MainForm()
        {
            Text = "Шахматы";
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Ui.S(1040), Ui.S(700));
            ClientSize = new Size(Ui.S(1180), Ui.S(780));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // saved choices first, so that nothing below reacts to them as if the user had clicked
            loadingSettings = true;
            Theme.Set(Theme.FromName(Settings.Get("theme", "dark")));
            BoardSchemes.Set(Settings.GetInt("board", 0));
            Sounds.Muted = !Settings.GetBool("sound", true);
            int savedW = Settings.GetInt("w", 0), savedH = Settings.GetInt("h", 0);
            if (savedW >= MinimumSize.Width && savedH >= MinimumSize.Height && savedW < 6000 && savedH < 4000)
                ClientSize = new Size(savedW, savedH);

            BuildUi();

            int savedMode = Math.Max(0, Math.Min(2, Settings.GetInt("mode", 0)));
            mode = (Mode)savedMode;
            segMode.SelectSilently(savedMode);
            segSide.SelectSilently(Math.Max(0, Math.Min(1, Settings.GetInt("side", 0))));
            lastSideIndex = segSide.SelectedIndex;
            sliderLevel.SetSilently(Math.Max(0, Math.Min(Levels.Length - 1, Settings.GetInt("level", 2))));
            segTransport.SelectSilently(Math.Max(0, Math.Min(1, Settings.GetInt("transport", 0))));
            segHostSeat.SelectSilently(Math.Max(0, Math.Min(2, Settings.GetInt("hostseat", 0))));

            board.Game = game;
            board.UserMove += OnUserMove;
            board.ResultActionClicked += delegate { OnNewGameClicked(); };
            board.PremoveChanged += delegate { UpdateStatus(); };
            premoveTimer.Interval = 170;
            premoveTimer.Tick += delegate { premoveTimer.Stop(); ExecutePremove(); };
            game.MovePlayed += OnMoveSound;
            game.MovePlayed += delegate { hintToken++; };
            game.PositionReset += OnGamePositionReset;
            moveList.PlySelected += delegate(int ply) { SetViewPly(ply); };

            segMode.SelectedIndexChanged += OnModeChanged;
            segSide.SelectedIndexChanged += OnSideChanged;
            sliderLevel.ValueChanged += delegate { if (!loadingSettings) { Settings.Set("level", sliderLevel.Value); RefreshAll(); } };
            segTransport.SelectedIndexChanged += delegate { if (!loadingSettings) { Settings.Set("transport", segTransport.SelectedIndex); } UpdateNetUi(); };
            segHostSeat.SelectedIndexChanged += delegate { if (!loadingSettings) Settings.Set("hostseat", segHostSeat.SelectedIndex); };
            btnNew.Click += delegate { OnNewGameClicked(); };
            btnUndo.Click += delegate { DoUndo(); };
            btnHint.Click += delegate { RequestHint(); };
            btnFlip.Click += delegate { FlipBoard(); };
            btnTheme.Click += delegate { ToggleTheme(); };
            btnSound.Click += delegate { ToggleSound(); };
            btnBoardStyle.Click += delegate { BoardSchemes.Set(BoardSchemes.Index + 1); Settings.Set("board", BoardSchemes.Index); UpdateHeaderButtons(); };
            Theme.Changed += ApplyTheme;
            FormClosing += delegate
            {
                closing = true;
                premoveTimer.Stop();
                engine.Cancel();
                if (hintEngine != null) hintEngine.Cancel();
                NetShutdown("");
                SteamLobbyService.Release();
                SaveSettings();
            };
            FormClosed += delegate { Theme.Changed -= ApplyTheme; };

            loadingSettings = false;
            ApplyTheme();
            ApplyMode();
            if (VsComputer && ComputerIsWhite) board.Flipped = true;   // the user always sits at the bottom
            RefreshAll();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme t = Theme.Current;
            NativeUi.StyleTitleBar(Handle, t.IsDark, t.Window, t.Text);
        }

        // ---------------------------------------------------------------- UI construction

        void BuildUi()
        {
            Theme t = Theme.Current;
            BackColor = t.Window;

            // ---- left: board between two player strips
            boardArea = new BoardArea();
            boardArea.Dock = DockStyle.Fill;
            topBar = new PlayerBar();
            bottomBar = new PlayerBar();
            board = new BoardControl();
            boardArea.TopBar = topBar;
            boardArea.BottomBar = bottomBar;
            boardArea.Board = board;
            boardArea.Controls.Add(topBar);
            boardArea.Controls.Add(board);
            boardArea.Controls.Add(bottomBar);

            // ---- right: control panel
            side = new BufferedPanel();
            side.Dock = DockStyle.Right;
            side.Width = Ui.S(392);
            side.Padding = new Padding(Ui.S(6), Ui.S(18), Ui.S(20), Ui.S(18));

            sideTable = new BufferedTable();
            sideTable.Dock = DockStyle.Fill;
            sideTable.ColumnCount = 1;
            sideTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            sideTable.RowCount = 6;
            sideTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(46)));    // header
            sideTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(52)));    // mode switch
            sideTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(86)));    // status
            sideTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));              // settings
            sideTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));         // moves
            sideTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(54)));    // actions

            // header: title + three icon buttons
            headerTable = new BufferedTable();
            headerTable.Dock = DockStyle.Fill;
            headerTable.ColumnCount = 4;
            headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(44)));
            headerTable.RowCount = 1;
            headerTable.Margin = new Padding(0, 0, 0, Ui.S(8));
            lblTitle = new Label();
            lblTitle.Text = "Шахматы";
            lblTitle.Font = Ui.Font(19f, FontStyle.Bold);
            lblTitle.AutoSize = false;
            lblTitle.Dock = DockStyle.Fill;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.Padding = new Padding(Ui.S(4), 0, 0, 0);
            headerTable.Controls.Add(lblTitle, 0, 0);
            btnBoardStyle = IconButton("", "▦", "Стиль доски");
            btnSound = IconButton("", "♪", "Звук (M)");
            btnTheme = IconButton("", "◐", "Тема (T)");
            btnBoardStyle.Style = btnSound.Style = btnTheme.Style = ButtonStyle.Ghost;
            headerTable.Controls.Add(btnBoardStyle, 1, 0);
            headerTable.Controls.Add(btnSound, 2, 0);
            headerTable.Controls.Add(btnTheme, 3, 0);
            sideTable.Controls.Add(headerTable, 0, 0);

            segMode = new Segmented();
            segMode.Items = new string[] { "Компьютер", "Вдвоём", "По сети" };
            segMode.Prominent = true;
            segMode.Dock = DockStyle.Fill;
            segMode.Margin = new Padding(0, 0, 0, Ui.S(12));
            sideTable.Controls.Add(segMode, 0, 1);

            status = new StatusCard();
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0, 0, 0, Ui.S(12));
            sideTable.Controls.Add(status, 0, 2);

            // settings card: rows appear and disappear with the mode
            settingsCard = new CardPanel();
            settingsCard.Dock = DockStyle.Fill;
            settingsCard.AutoSize = true;
            settingsCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settingsCard.Padding = new Padding(Ui.S(16), Ui.S(14), Ui.S(16), Ui.S(8));
            settingsCard.Margin = new Padding(0, 0, 0, Ui.S(12));
            settingsTable = new BufferedTable();
            settingsTable.Dock = DockStyle.Top;
            settingsTable.AutoSize = true;
            settingsTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settingsTable.ColumnCount = 1;
            settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            onCard.Add(settingsTable);

            capSide = Caption("Вы играете");
            segSide = new Segmented();
            segSide.Items = new string[] { "Белыми", "Чёрными" };
            segSide.Dock = DockStyle.Fill;
            segSide.Margin = new Padding(0, 0, 0, Ui.S(12));
            capLevel = Caption("Уровень компьютера");
            sliderLevel = new StepSlider();
            sliderLevel.Labels = new string[Levels.Length];
            for (int i = 0; i < Levels.Length; i++) sliderLevel.Labels[i] = Levels[i].Name;
            sliderLevel.Dock = DockStyle.Fill;
            sliderLevel.Margin = new Padding(0, 0, 0, Ui.S(8));
            lblTwoHint = new Label();
            lblTwoHint.Text = "Играйте вдвоём за одним компьютером: белые и чёрные ходят по очереди. Доску можно повернуть кнопкой справа внизу.";
            lblTwoHint.Font = Ui.Font(9.5f);
            lblTwoHint.AutoSize = false;
            lblTwoHint.Height = Ui.S(78);
            lblTwoHint.Dock = DockStyle.Fill;
            lblTwoHint.Margin = new Padding(0, 0, 0, Ui.S(8));
            onCard.Add(lblTwoHint);

            AddRow(settingsTable, capSide);
            AddRow(settingsTable, segSide);
            AddRow(settingsTable, capLevel);
            AddRow(settingsTable, sliderLevel);
            AddRow(settingsTable, lblTwoHint);
            foreach (Control c in CreateNetControls()) AddRow(settingsTable, c);

            settingsCard.Controls.Add(settingsTable);
            sideTable.Controls.Add(settingsCard, 0, 3);

            // moves card
            movesCard = new CardPanel();
            movesCard.Dock = DockStyle.Fill;
            movesCard.Padding = new Padding(Ui.S(10), Ui.S(10), Ui.S(8), Ui.S(10));
            movesCard.Margin = new Padding(0, 0, 0, Ui.S(12));
            moveList = new MoveListControl();
            moveList.Dock = DockStyle.Fill;
            movesTitle = new Label();
            movesTitle.Text = "Ходы";
            movesTitle.Font = Ui.Font(9.5f, FontStyle.Bold);
            movesTitle.AutoSize = false;
            movesTitle.Height = Ui.S(28);
            movesTitle.Dock = DockStyle.Top;
            movesTitle.Padding = new Padding(Ui.S(6), Ui.S(2), 0, 0);
            onCard.Add(movesTitle);
            BuildChatUi(movesCard, moveList, movesTitle);   // the chat is a second tab of this card
            sideTable.Controls.Add(movesCard, 0, 4);

            // actions
            actionsTable = new BufferedTable();
            actionsTable.Dock = DockStyle.Fill;
            actionsTable.ColumnCount = 4;
            actionsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) actionsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(50)));
            actionsTable.RowCount = 1;
            btnNew = new FlatButton();
            btnNew.Text = "Новая партия";
            btnNew.Glyph = "";
            btnNew.Fallback = "+";
            btnNew.Style = ButtonStyle.Primary;
            btnNew.Dock = DockStyle.Fill;
            btnNew.Margin = new Padding(0, Ui.S(4), Ui.S(8), Ui.S(4));
            btnNew.Tip = "Новая партия (Ctrl+N)";
            btnUndo = IconButton("", "↶", "Отменить ход (Ctrl+Z)");
            btnHint = IconButton("", "?", "Подсказка (H)");
            btnFlip = IconButton("", "⇅", "Повернуть доску (F)");
            actionsTable.Controls.Add(btnNew, 0, 0);
            actionsTable.Controls.Add(btnUndo, 1, 0);
            actionsTable.Controls.Add(btnHint, 2, 0);
            actionsTable.Controls.Add(btnFlip, 3, 0);
            sideTable.Controls.Add(actionsTable, 0, 5);

            side.Controls.Add(sideTable);
            Controls.Add(boardArea);
            Controls.Add(side);
        }

        static FlatButton IconButton(string glyph, string fallback, string tip)
        {
            FlatButton b = new FlatButton();
            b.Glyph = glyph;
            b.Fallback = fallback;
            b.Tip = tip;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(Ui.S(2), Ui.S(4), Ui.S(2), Ui.S(4));
            return b;
        }

        Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.Font(8.75f, FontStyle.Bold);
            l.AutoSize = false;
            l.Height = Ui.S(22);
            l.Dock = DockStyle.Fill;
            l.Margin = new Padding(0, 0, 0, Ui.S(2));
            onCard.Add(l);
            return l;
        }

        static void AddRow(TableLayoutPanel table, Control c)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = table.RowStyles.Count;
            table.Controls.Add(c, 0, table.RowCount - 1);
        }

        // ---------------------------------------------------------------- theme and settings

        void ApplyTheme()
        {
            Theme t = Theme.Current;
            BackColor = t.Window;
            foreach (Control c in new Control[] { boardArea, side, sideTable, headerTable, actionsTable })
                c.BackColor = t.Window;
            foreach (Control c in onCard) c.BackColor = t.Card;
            lblTitle.BackColor = t.Window;
            lblTitle.ForeColor = t.Text;
            foreach (Label l in new Label[] { capSide, capLevel, capTransport, capHostSeat, movesTitle }) l.ForeColor = t.Muted;
            lblTwoHint.ForeColor = t.Muted;
            lblNet.ForeColor = t.Muted;
            if (IsHandleCreated) NativeUi.StyleTitleBar(Handle, t.IsDark, t.Window, t.Text);
            UpdateHeaderButtons();
            Invalidate(true);
        }

        void UpdateHeaderButtons()
        {
            Theme t = Theme.Current;
            btnTheme.Glyph = t.IsDark ? "" : "";   // sun when dark (switch to light), moon when light
            btnTheme.Fallback = t.IsDark ? "☀" : "☾";
            btnTheme.Tip = t.IsDark ? "Светлая тема (T)" : "Тёмная тема (T)";
            btnSound.Glyph = Sounds.Muted ? "" : "";
            btnSound.Fallback = Sounds.Muted ? "×" : "♪";
            btnSound.Tip = Sounds.Muted ? "Включить звук (M)" : "Выключить звук (M)";
            btnBoardStyle.Tip = "Стиль доски: " + BoardSchemes.Current.Name;
        }

        void ToggleTheme()
        {
            Theme.Set(Theme.Current.IsDark ? Theme.Light : Theme.Dark);
            Settings.Set("theme", Theme.Current.Name);
            Settings.Save();
        }

        void ToggleSound()
        {
            Sounds.Muted = !Sounds.Muted;
            Settings.Set("sound", !Sounds.Muted);
            UpdateHeaderButtons();
            if (!Sounds.Muted) Sounds.Play(Sfx.Move);
        }

        void SaveSettings()
        {
            if (loadingSettings) return;
            Settings.Set("mode", (int)mode);
            Settings.Set("side", segSide.SelectedIndex);
            Settings.Set("level", sliderLevel.Value);
            Settings.Set("transport", segTransport.SelectedIndex);
            Settings.Set("hostseat", segHostSeat.SelectedIndex);
            Settings.Set("theme", Theme.Current.Name);
            Settings.Set("board", BoardSchemes.Index);
            Settings.Set("sound", !Sounds.Muted);
            if (WindowState == FormWindowState.Normal)
            {
                Settings.Set("w", ClientSize.Width);
                Settings.Set("h", ClientSize.Height);
            }
            Settings.Save();
        }

        // ---------------------------------------------------------------- game flow

        bool VsComputer { get { return mode == Mode.Computer; } }
        bool ComputerIsWhite { get { return segSide.SelectedIndex == 1; } }

        bool IsComputerTurn()
        {
            return VsComputer && game.Status == GameStatus.Playing && game.Pos.WhiteToMove == ComputerIsWhite;
        }

        void ApplyMode()
        {
            switch (mode)
            {
                case Mode.Computer: board.HumanMask = ComputerIsWhite ? 2 : 1; break;
                case Mode.TwoPlayers: board.HumanMask = 3; break;
                default:
                    // a room change must not throw away a queued move, so only a real change of the mask is applied
                    int netMask = NetHumanMask();
                    if (board.HumanMask != netMask) board.HumanMask = netMask;
                    break;
            }
            UpdateNetUi();
        }

        bool GameInProgress { get { return game.PlyCount > 0 && game.Status == GameStatus.Playing; } }

        void OnModeChanged(object sender, EventArgs e)
        {
            if (loadingSettings) return;
            Mode now = (Mode)segMode.SelectedIndex;
            if (now == mode) return;
            Mode old = mode;

            bool resets = now == Mode.Network || old == Mode.Network;
            if (resets && GameInProgress && !Ask("Сменить режим?", "Текущая партия будет прервана. Продолжить?"))
            {
                segMode.SelectSilently((int)mode);   // stay where we were
                return;
            }

            mode = now;
            CancelAi();
            if (old == Mode.Network) NetShutdown("");
            if (resets)
            {
                // a network game and a local game never share a board
                game.NewGame();
                engine.Reset();
                board.Flipped = now == Mode.Computer && ComputerIsWhite;
            }
            ApplyMode();
            RefreshAll();
            SaveSettings();
        }

        void OnSideChanged(object sender, EventArgs e)
        {
            if (loadingSettings) return;
            if (mode == Mode.Computer)
            {
                if (GameInProgress && !Ask("Сменить цвет?", "Текущая партия будет прервана и начнётся новая. Продолжить?"))
                {
                    segSide.SelectSilently(lastSideIndex);
                    return;
                }
                StartNewGame();
            }
            lastSideIndex = segSide.SelectedIndex;
            SaveSettings();
        }

        void OnNewGameClicked()
        {
            if (mode == Mode.Network) { NetRequestNewGame(); return; }
            if (GameInProgress && !Ask("Новая партия", "Текущая партия будет потеряна. Начать новую?")) return;
            StartNewGame();
        }

        void StartNewGame()
        {
            CancelAi();
            game.NewGame();
            engine.Reset();
            ApplyMode();
            if (VsComputer) board.Flipped = ComputerIsWhite;   // user always sits at the bottom
            RefreshAll();
        }

        void DoUndo()
        {
            if (!CanUndo()) return;
            CancelAi();
            game.Undo();
            if (VsComputer)
            {
                // roll back until it is the user's turn again
                while (game.PlyCount > 0 && game.Pos.WhiteToMove == ComputerIsWhite) game.Undo();
            }
            board.ResetSelection();
            RefreshAll();
        }

        bool CanUndo()
        {
            if (mode == Mode.Network) return false;
            int minPlies = (VsComputer && ComputerIsWhite) ? 1 : 0;   // the computer's opening move stays
            return game.PlyCount > minPlies;
        }

        void FlipBoard()
        {
            board.Flipped = !board.Flipped;
            RefreshAll();
        }

        void CancelAi()
        {
            aiToken++;
            engine.Cancel();
        }

        void OnUserMove(ChessMove m)
        {
            game.Play(m);
            if (mode == Mode.Network) NetAfterLocalMove(m);
            RefreshAll();
        }

        /// <summary>The position was replaced (new game, undo ...): a look at an old position no longer makes sense.</summary>
        void OnGamePositionReset()
        {
            hintToken++;
            viewPly = -1;
            viewPos = null;
            board.SetView(null, -1, -1);
        }

        void OnMoveSound(ChessMove m, int captured)
        {
            if (suppressFx || game.Status != GameStatus.Playing) return;   // a replayed game is silent; the game-over chime takes over
            if (game.InCheck) Sounds.Play(Sfx.Check);
            else if (captured != 0) Sounds.Play(Sfx.Capture);
            else Sounds.Play(Sfx.Move);
        }

        // ---------------------------------------------------------------- looking at earlier positions

        void SetViewPly(int ply)
        {
            ply = Math.Max(0, Math.Min(game.PlyCount, ply));
            if (ply == game.PlyCount) { ExitReview(); return; }
            viewPly = ply;
            viewPos = game.PositionAfter(ply);
            int from = -1, to = -1;
            if (ply > 0) { ChessMove m = game.MoveAt(ply - 1); from = m.From; to = m.To; }
            board.SetView(viewPos, from, to);
            RefreshAll();
        }

        void ExitReview()
        {
            if (viewPly < 0) return;
            viewPly = -1;
            viewPos = null;
            board.SetView(null, -1, -1);
            RefreshAll();
        }

        void StepView(int delta)
        {
            if (game.PlyCount == 0) return;
            int current = viewPly >= 0 ? viewPly : game.PlyCount;
            int next = Math.Max(0, Math.Min(game.PlyCount, current + delta));
            if (next == current) return;
            Sounds.Play(Sfx.Move);
            SetViewPly(next);
        }

        // ---------------------------------------------------------------- hint

        bool CanHint()
        {
            if (mode == Mode.Network || hintBusy || aiBusy || closing) return false;
            if (viewPly >= 0 || game.Status != GameStatus.Playing) return false;
            return (board.HumanMask & (game.Pos.WhiteToMove ? 1 : 2)) != 0;
        }

        async void RequestHint()
        {
            if (!CanHint()) return;
            hintBusy = true;
            int token = ++hintToken;
            Position snapshot = game.Pos.Clone();
            ulong[] hashes = game.HashHistory();
            if (hintEngine == null) hintEngine = new Engine();   // never shares state with the opponent's engine
            Engine e = hintEngine;
            btnHint.Active = true;
            ChessMove best = default(ChessMove);
            try
            {
                best = await Task.Run(() => e.FindBestMove(snapshot, hashes, 6, 1800, 0));
            }
            catch (Exception) { }
            hintBusy = false;
            if (closing) return;
            btnHint.Active = false;
            try
            {
                if (token == hintToken && viewPly < 0 && !best.IsNull) board.SetHint(best.From, best.To);
                RefreshAll();
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- computer opponent

        // ---------------------------------------------------------------- queued move (premove)

        /// <summary>It is our turn in a game where we play one side: a queued move may be played now.</summary>
        bool PremoveReady()
        {
            if (game.Status != GameStatus.Playing || viewPly >= 0) return false;
            int mask = board.HumanMask;
            if (mask != 1 && mask != 2) return false;
            return game.Pos.WhiteToMove == (mask == 1);
        }

        void SchedulePremove()
        {
            if (!board.HasPremove || !PremoveReady()) return;
            premoveTimer.Stop();
            premoveTimer.Start();
        }

        /// <summary>Plays the queued move if it is still legal; otherwise it is simply dropped.</summary>
        void ExecutePremove()
        {
            if (closing || !board.HasPremove || !PremoveReady()) return;
            int from, to;
            board.TakePremove(out from, out to);
            foreach (ChessMove m in game.Legal)
            {
                if (m.From == from && m.To == to && (m.Promo == 0 || m.Promo == Pc.Queen))   // a queued pawn promotes to a queen
                {
                    OnUserMove(m);
                    return;
                }
            }
            RefreshAll();   // not possible any more (the position changed): forget it
        }

        void RefreshAll()
        {
            if (closing) return;
            if (game.Status != GameStatus.Playing) board.ClearPremove();
            board.Invalidate();
            UpdateMoveList();
            UpdatePlayers();
            UpdateStatus();
            btnUndo.Enabled = CanUndo();
            btnHint.Enabled = CanHint();
            UpdateNetUi();
            UpdateResultCard();
            if (lastStatus == GameStatus.Playing && game.Status != GameStatus.Playing) Sounds.Play(Sfx.GameOver);
            lastStatus = game.Status;
            SchedulePremove();
            MaybeStartAi();
        }

        async void MaybeStartAi()
        {
            if (aiBusy || closing || !IsComputerTurn()) return;
            aiBusy = true;
            int token = aiToken;
            Position snapshot = game.Pos.Clone();
            ulong[] hashes = game.HashHistory();
            Level level = Levels[sliderLevel.Value];
            UpdateStatus();
            UpdatePlayers();
            btnHint.Enabled = false;

            ChessMove best = default(ChessMove);
            try
            {
                best = await Task.Run(() => engine.FindBestMove(snapshot, hashes, level.Depth, level.TimeMs, level.Noise));
            }
            catch (Exception)
            {
                best = default(ChessMove);
            }
            aiBusy = false;
            if (closing) return;

            try
            {
                if (token != aiToken || !IsComputerTurn())
                {
                    // the position changed while the engine was thinking: discard its answer
                    RefreshAll();
                    return;
                }

                ChessMove chosen = default(ChessMove);
                bool found = false;
                foreach (ChessMove l in game.Legal)
                {
                    if (!best.IsNull && l.Same(best)) { chosen = l; found = true; break; }
                }
                if (!found && game.Legal.Count > 0) { chosen = game.Legal[0]; found = true; }
                if (!found) { RefreshAll(); return; }

                game.Play(chosen);
                RefreshAll();
            }
            catch (Exception)
            {
                // an async void must never throw: a broken refresh must not take the program down
            }
        }

        // ---------------------------------------------------------------- view updates

        void StatusTexts(out StatusKind kind, out string title, out string sub, out bool busy)
        {
            kind = StatusKind.Normal;
            sub = "";
            busy = false;
            Position shown = game.Pos;
            bool whiteToMove = shown.WhiteToMove;
            switch (game.Status)
            {
                case GameStatus.Checkmate:
                    kind = StatusKind.Over; title = "Мат!"; sub = "Победили " + (whiteToMove ? "чёрные" : "белые"); break;
                case GameStatus.Stalemate:
                    kind = StatusKind.Over; title = "Пат"; sub = "Ничья"; break;
                case GameStatus.DrawInsufficient:
                    kind = StatusKind.Over; title = "Ничья"; sub = "Недостаточно материала для мата"; break;
                case GameStatus.DrawFiftyMoves:
                    kind = StatusKind.Over; title = "Ничья"; sub = "Правило 50 ходов"; break;
                case GameStatus.DrawRepetition:
                    kind = StatusKind.Over; title = "Ничья"; sub = "Троекратное повторение позиции"; break;
                case GameStatus.WhiteResigned:
                    kind = StatusKind.Over; title = "Белые сдались"; sub = "Победили чёрные"; break;
                case GameStatus.BlackResigned:
                    kind = StatusKind.Over; title = "Чёрные сдались"; sub = "Победили белые"; break;
                case GameStatus.DrawAgreed:
                    kind = StatusKind.Over; title = "Ничья"; sub = "По соглашению сторон"; break;
                default:
                    if (VsComputer)
                    {
                        if (aiBusy && IsComputerTurn()) { title = "Компьютер думает"; busy = true; }
                        else title = whiteToMove == ComputerIsWhite ? "Ход компьютера" : "Ваш ход";
                    }
                    else title = "Ход " + (whiteToMove ? "белых" : "чёрных");
                    if (game.InCheck) { kind = StatusKind.Check; sub = "Шах!"; }
                    break;
            }
        }

        void UpdateStatus()
        {
            StatusKind kind;
            string title, sub;
            bool busy;
            StatusTexts(out kind, out title, out sub, out busy);
            if (mode == Mode.Network) NetStatus(ref kind, ref title, ref sub, ref busy);
            if (board.HasPremove && game.Status == GameStatus.Playing)
                sub = (sub.Length > 0 ? sub + " · " : "") + "В ожидании: " + board.PremoveText + " (Esc — отмена)";
            if (viewPly >= 0)
            {
                kind = StatusKind.Waiting;
                busy = false;
                title = "Просмотр партии";
                sub = "Ход " + viewPly + " из " + game.PlyCount + ". Нажмите End, чтобы вернуться к игре.";
            }
            status.Set(kind, title, sub, busy);
        }

        void UpdateResultCard()
        {
            if (game.Status == GameStatus.Playing || viewPly >= 0 || (mode == Mode.Network && !InRoom))
            {
                board.SetResult(null, null, null);
                return;
            }
            StatusKind kind;
            string title, sub;
            bool busy;
            StatusTexts(out kind, out title, out sub, out busy);
            if (mode == Mode.Network) NetStatus(ref kind, ref title, ref sub, ref busy);
            string action = btnNew.Enabled ? (mode == Mode.Network ? "Реванш" : "Новая партия") : null;
            board.SetResult(title, sub, action);
        }

        void UpdateMoveList()
        {
            moveList.SetMoves(game.San, viewPly >= 0 ? viewPly : game.PlyCount);
        }

        string NameFor(bool white)
        {
            switch (mode)
            {
                case Mode.Computer: return white == ComputerIsWhite ? "Компьютер" : "Вы";
                case Mode.TwoPlayers: return white ? "Белые" : "Чёрные";
                default:
                    if (!InRoom) return white ? "Белые" : "Чёрные";
                    RoomMember holder = room.SeatHolder(white ? Seat.White : Seat.Black);
                    if (holder == null) return "Место свободно";
                    return holder.Id == room.MyId ? "Вы" : holder.Name;
            }
        }

        string SubtitleFor(bool white)
        {
            string color = white ? "белые" : "чёрные";
            if (mode == Mode.TwoPlayers) return "";
            if (mode == Mode.Computer && white == ComputerIsWhite) return color + " · " + Levels[sliderLevel.Value].Name;
            if (mode == Mode.Network && InRoom)
            {
                RoomMember holder = room.SeatHolder(white ? Seat.White : Seat.Black);
                if (holder == null) return color + " · ждём игрока";
                string s = color;
                if (holder.Id == room.HostId) s += " · хост";
                if (holder.Id == room.MyId && holder.Name.Length > 0) s += " · " + holder.Name;
                return s;
            }
            return color;
        }

        void UpdatePlayers()
        {
            Position shown = viewPos ?? game.Pos;
            bool bottomWhite = !board.Flipped;
            int[] start = { 0, 8, 2, 2, 2, 1, 1 };
            int[] value = { 0, 1, 3, 3, 5, 9, 0 };
            int[] w = new int[7], b = new int[7];
            int matW = 0, matB = 0;
            for (int s = 0; s < 64; s++)
            {
                int p = shown.Sq[s];
                if (p > 0) { w[p]++; matW += value[p]; }
                else if (p < 0) { b[-p]++; matB += value[-p]; }
            }
            int[] byWhite = new int[7], byBlack = new int[7];
            for (int tpe = Pc.Pawn; tpe <= Pc.Queen; tpe++)
            {
                byWhite[tpe] = Math.Max(0, start[tpe] - b[tpe]);   // black pieces missing = taken by White
                byBlack[tpe] = Math.Max(0, start[tpe] - w[tpe]);
            }
            int lead = matW - matB;
            bool playing = game.Status == GameStatus.Playing && viewPly < 0 && (mode != Mode.Network || RoomFull);
            bool thinking = aiBusy && IsComputerTurn();

            foreach (PlayerBar bar in new PlayerBar[] { topBar, bottomBar })
            {
                bool white = bar == bottomBar ? bottomWhite : !bottomWhite;
                bar.IsWhite = white;
                bar.PlayerName = NameFor(white);
                bar.Subtitle = SubtitleFor(white);
                bar.Vacant = mode == Mode.Network && InRoom && room.SeatHolder(white ? Seat.White : Seat.Black) == null;
                bar.Active = playing && shown.WhiteToMove == white;
                bar.Thinking = thinking && white == ComputerIsWhite && VsComputer;
                bar.SetCaptured(white ? byWhite : byBlack, white ? Math.Max(0, lead) : Math.Max(0, -lead));
            }
        }

        // ---------------------------------------------------------------- keyboard

        bool ArrowKeysBelongToFocusedControl
        {
            get { return ActiveControl is Segmented || ActiveControl is StepSlider || ActiveControl is TextBox; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox)
            {
                // typing a chat message: letters are letters, not shortcuts; Esc leaves the box
                if (keyData == Keys.Escape) { board.Focus(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == (Keys.Control | Keys.Z)) { DoUndo(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { OnNewGameClicked(); return true; }
            if (!ArrowKeysBelongToFocusedControl)
            {
                switch (keyData)
                {
                    case Keys.Left: StepView(-1); return true;
                    case Keys.Right: StepView(1); return true;
                    case Keys.Home: if (game.PlyCount > 0) { Sounds.Play(Sfx.Move); SetViewPly(0); } return true;
                    case Keys.End: if (viewPly >= 0) { Sounds.Play(Sfx.Move); ExitReview(); } return true;
                }
            }
            switch (keyData)
            {
                case Keys.F: FlipBoard(); return true;
                case Keys.H: RequestHint(); return true;
                case Keys.T: ToggleTheme(); return true;
                case Keys.M: ToggleSound(); return true;
                case Keys.V: ToggleMic(); return true;
                case Keys.Escape:
                    if (board.HasPremove) board.ClearPremove();
                    else if (viewPly >= 0) ExitReview();
                    else board.ClearHint();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------------------------------------------------------- test access (the tests are compiled into the same assembly)

        internal void TestSetMode(int index) { segMode.SelectedIndex = index; }
        internal void TestSetTransport(int index) { segTransport.SelectedIndex = index; }
        internal void TestSetSide(int index) { segSide.SelectedIndex = index; }
        internal string StatusText { get { return status.FullText; } }
        internal int ViewPlyForTest { get { return viewPly; } }
        internal void TestSetViewPly(int ply) { SetViewPly(ply); }
        internal void TestExitReview() { ExitReview(); }
    }
}
