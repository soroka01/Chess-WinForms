using System;
using System.Collections.Generic;
using System.Text;
using ChessMove = ChessGame.Move;

namespace ChessGame
{
    /// <summary>What the window should tell the user about the game, as a code (the wording belongs to the window).</summary>
    public enum GameNotice
    {
        /// <summary>Whatever was said before no longer applies.</summary>
        None,
        /// <summary>Our offer of a draw was declined.</summary>
        DrawDeclined,
        /// <summary>Our offer of a new game was declined.</summary>
        RematchDeclined
    }

    /// <summary>
    /// The game of a room, without any user interface. The host is the single writer of the game: players send it their requests,
    /// it validates them and tells everybody; every other copy only applies what the host announced.
    ///
    ///   player -> host   MOVE &lt;ply&gt; &lt;uci&gt;   RESIGN   DRAW / DRAW_YES / DRAW_NO   NEW / NEW_YES / NEW_NO   RESYNC (anybody)
    ///   host -> everyone PLAYED &lt;ply&gt; &lt;uci&gt;          a move was made (the sender of the move gets it back and ignores it)
    ///                    RESULT &lt;ply&gt; WR|BR|DA       white / black resigned, draw agreed
    ///                    OFFER DRAW|NEW W|B          that side offers a draw / a rematch;  OFFER NONE;  OFFER DECLINED DRAW|NEW
    ///                    NEWGAME                     a new game starts (the seats were swapped just before)
    ///                    SYNC &lt;startPly&gt; &lt;moves...&gt;  the whole game so far (several lines for a long game), then RESULT / OFFER
    ///
    /// Who may send what is decided by RoomSession (requests only from seated players, stamped lines only from the host); this class
    /// decides what the lines mean for the game. Events are raised on the thread that calls into it (the UI thread).
    /// </summary>
    public sealed class RoomGame
    {
        const int MaxSyncLine = 1700;   // a SYNC line stays far below the 2048 characters of a line

        readonly RoomSession room;
        readonly Game game;
        Seat drawOfferBy = Seat.None, newOfferBy = Seat.None;
        string pendingMove;     // our move as "ply uci" that the host has not confirmed yet
        bool wasFull;           // both seats were held at the last room change
        string seatSig = "";    // who held the seats at the last room change

        /// <summary>The game or the offers changed: show them again. Raised after the state is consistent.</summary>
        public event Action Changed;

        /// <summary>Raised before Changed when the user has something to be told (or nothing any more).</summary>
        public event Action<GameNotice> Notice;

        /// <summary>A new game has started (the position is the start position, the offers are gone).</summary>
        public event Action NewGameStarted;

        /// <summary>A whole game is being replayed from a SYNC: true before the first move, false after the last. Moves made meanwhile are history, not news.</summary>
        public event Action<bool> Replaying;

        public RoomGame(RoomSession room, Game game)
        {
            this.room = room;
            this.game = game;
        }

        // ---------------------------------------------------------------- what the room and the game say

        /// <summary>Both seats are taken: the game can be played (otherwise it waits or is paused).</summary>
        public bool Full
        {
            get { return room.Ready && room.SeatHolder(Seat.White) != null && room.SeatHolder(Seat.Black) != null; }
        }

        bool IAmPlayer { get { return room.Ready && room.MySeat != Seat.None; } }

        /// <summary>The side that has offered a draw (None = no open offer).</summary>
        public Seat DrawOfferedBy { get { return drawOfferBy; } }

        /// <summary>The side that has offered a new game (None = no open offer).</summary>
        public Seat RematchOfferedBy { get { return newOfferBy; } }

        static string Letter(Seat seat) { return seat == Seat.White ? "W" : "B"; }

        static Seat SeatOf(string letter) { return letter == "W" ? Seat.White : (letter == "B" ? Seat.Black : Seat.None); }

        void RaiseChanged() { if (Changed != null) Changed(); }

        void RaiseNotice(GameNotice notice) { if (Notice != null) Notice(notice); }

        // ---------------------------------------------------------------- the room changed

        /// <summary>The room changed (seats, members, permissions). Returns true when somebody else sits at a seat now.</summary>
        public bool RoomChanged()
        {
            bool full = Full;
            string sig = SeatSignature();
            bool seatsChanged = sig != seatSig;
            seatSig = sig;

            if (room.IsHost)
            {
                if (seatsChanged && (drawOfferBy != Seat.None || newOfferBy != Seat.None)) HostClearOffers();
                if (full && !wasFull && game.Status != GameStatus.Playing) HostStartNewGame();   // somebody sat down after a finished game
            }
            wasFull = full;
            if (seatsChanged && !(drawOfferBy != Seat.None && full)) drawOfferBy = Seat.None;
            return seatsChanged;
        }

        /// <summary>We have just been welcomed into the room: what we see now is the starting point for noticing changes.</summary>
        public void Welcomed()
        {
            seatSig = SeatSignature();
            wasFull = Full;
        }

        string SeatSignature()
        {
            RoomMember w = room.SeatHolder(Seat.White), b = room.SeatHolder(Seat.Black);
            return (w == null ? "-" : w.Id) + "|" + (b == null ? "-" : b.Id);
        }

        /// <summary>The host changed: a move that the old host never answered goes to the new one.</summary>
        public void HostChanged()
        {
            if (!room.Ready) return;
            if (room.IsHost) { pendingMove = null; return; }   // we are the host now: there is nobody left to confirm it
            if (pendingMove == null || game.PlyCount == 0) return;
            string[] pm = pendingMove.Split(' ');
            int ply;
            if (int.TryParse(pm[0], out ply) && ply == game.PlyCount - 1) room.SendToHost("MOVE " + pendingMove);
            else pendingMove = null;
        }

        // ---------------------------------------------------------------- lines from the room

        /// <summary>A game line that RoomSession has already let through: a request (we are the host) or an announcement.</summary>
        public void Receive(string from, string line)
        {
            string[] p = line.Split(' ');
            if (room.IsHost) HostProcess(from, p); else ReplicaApply(p);
        }

        /// <summary>A player's action: the host handles it itself, everybody else asks the host.</summary>
        void Submit(string line)
        {
            if (!room.Ready) return;
            if (room.IsHost) HostProcess(room.MyId, line.Split(' '));
            else room.SendToHost(line);
        }

        // ---------------------------------------------------------------- our own actions

        /// <summary>We moved on our board (game.Play was already called): tell the host, or everybody if we are the host.</summary>
        public void AfterLocalMove(ChessMove m)
        {
            if (!room.Ready) return;
            int ply = game.PlyCount - 1;
            RaiseNotice(GameNotice.None);
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

        /// <summary>The result arrives from the host: a resignation only counts when the host has seen it.</summary>
        public void Resign()
        {
            if (CanAct()) { Submit("RESIGN"); RaiseChanged(); }
        }

        public bool CanOfferDraw { get { return CanAct() && drawOfferBy == Seat.None; } }

        public void OfferDraw()
        {
            if (!CanOfferDraw) return;
            Submit("DRAW");
            RaiseChanged();
        }

        public void AnswerDraw(bool accept)
        {
            Submit(accept ? "DRAW_YES" : "DRAW_NO");
            RaiseChanged();
        }

        /// <summary>We sit at the table with an opponent and have not asked yet (an open offer of ours is waiting for the answer).</summary>
        public bool CanRequestRematch
        {
            get { return room.Ready && IAmPlayer && Full && newOfferBy != room.MySeat; }
        }

        public void RequestRematch()
        {
            if (!CanRequestRematch) return;
            Submit("NEW");
            RaiseChanged();
        }

        public void AnswerRematch(bool accept)
        {
            Submit(accept ? "NEW_YES" : "NEW_NO");
            RaiseChanged();
        }

        /// <summary>We sit at the table, both seats are taken and the game is on.</summary>
        bool CanAct()
        {
            return room.Ready && IAmPlayer && Full && game.Status == GameStatus.Playing;
        }

        // ---------------------------------------------------------------- the host validates and announces

        void HostProcess(string from, string[] p)
        {
            RoomMember m = room.Find(from);
            if (m == null) return;
            if (p[0] == "RESYNC") { SendState(from); return; }
            if (m.Seat == Seat.None) return;
            bool white = m.Seat == Seat.White;
            Seat side = m.Seat;
            Seat other = white ? Seat.Black : Seat.White;

            switch (p[0])
            {
                case "MOVE":
                    HostMove(from, white, p);
                    return;

                case "RESIGN":
                    if (!Full || game.Status != GameStatus.Playing) return;
                    game.Terminate(white ? GameStatus.WhiteResigned : GameStatus.BlackResigned);
                    VoidOffers();
                    room.Broadcast("RESULT " + game.PlyCount + " " + (white ? "WR" : "BR"));
                    RaiseChanged();
                    return;

                case "DRAW":
                    if (!Full || game.Status != GameStatus.Playing) return;
                    if (drawOfferBy == other) { HostAgreeDraw(); return; }   // both asked: that is an agreement
                    if (drawOfferBy != Seat.None) return;
                    drawOfferBy = side;
                    room.Broadcast("OFFER DRAW " + Letter(side));
                    RaiseChanged();
                    return;

                case "DRAW_YES":
                    if (Full && game.Status == GameStatus.Playing && drawOfferBy == other) HostAgreeDraw();
                    return;

                case "DRAW_NO":
                    if (drawOfferBy == other)
                    {
                        if (IAmPlayer && drawOfferBy == room.MySeat) RaiseNotice(GameNotice.DrawDeclined);   // our own offer
                        drawOfferBy = Seat.None;
                        room.Broadcast("OFFER DECLINED DRAW");
                        RaiseChanged();
                    }
                    return;

                case "NEW":
                    if (!Full) return;
                    if (newOfferBy == other) { HostRematch(); return; }
                    if (newOfferBy != Seat.None) return;
                    newOfferBy = side;
                    room.Broadcast("OFFER NEW " + Letter(side));
                    RaiseChanged();
                    return;

                case "NEW_YES":
                    if (Full && newOfferBy == other) HostRematch();
                    return;

                case "NEW_NO":
                    if (newOfferBy == other)
                    {
                        if (IAmPlayer && newOfferBy == room.MySeat) RaiseNotice(GameNotice.RematchDeclined);
                        newOfferBy = Seat.None;
                        room.Broadcast("OFFER DECLINED NEW");
                        RaiseChanged();
                    }
                    return;
            }
        }

        void HostMove(string from, bool white, string[] p)
        {
            int ply;
            if (p.Length != 3 || !int.TryParse(p[1], out ply)) { SendState(from); return; }
            if (!Full || game.Status != GameStatus.Playing || ply != game.PlyCount || game.Pos.WhiteToMove != white)
            {
                // a late or duplicated request: the sender's board is out of step, show it the real game
                if (from != room.MyId) SendState(from);
                return;
            }
            foreach (ChessMove m in game.Legal)
            {
                if (m.ToString() == p[2])
                {
                    game.Play(m);
                    VoidOffers();
                    RaiseNotice(GameNotice.None);
                    room.Broadcast("PLAYED " + ply + " " + m);
                    RaiseChanged();
                    return;
                }
            }
            if (from != room.MyId) SendState(from);
        }

        void HostAgreeDraw()
        {
            game.Terminate(GameStatus.DrawAgreed);
            VoidOffers();
            room.Broadcast("RESULT " + game.PlyCount + " DA");
            RaiseChanged();
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
            RaiseNotice(GameNotice.None);
            room.Broadcast("NEWGAME");
            if (NewGameStarted != null) NewGameStarted();
            RaiseChanged();
        }

        /// <summary>Seats changed: an offer made to somebody who is no longer at the table lapses.</summary>
        void HostClearOffers()
        {
            VoidOffers();
            room.Broadcast("OFFER NONE");
        }

        void VoidOffers()
        {
            drawOfferBy = newOfferBy = Seat.None;
        }

        /// <summary>Host: sends the whole game to one member (after it joined, or when it lost the thread).</summary>
        public void SendState(string id)
        {
            if (!room.Ready || !room.IsHost || id == room.MyId) return;
            List<string> moves = new List<string>();
            for (int i = 0; i < game.PlyCount; i++) moves.Add(game.MoveAt(i).ToString());
            int start = 0;
            StringBuilder sb = new StringBuilder();
            do
            {
                sb.Length = 0;
                sb.Append("SYNC ").Append(start);
                int taken = 0;
                while (start + taken < moves.Count && sb.Length < MaxSyncLine)
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
            if (drawOfferBy != Seat.None) room.SendTo(id, "OFFER DRAW " + Letter(drawOfferBy));
            if (newOfferBy != Seat.None) room.SendTo(id, "OFFER NEW " + Letter(newOfferBy));
        }

        // ---------------------------------------------------------------- everybody else applies what the host announced

        void AskForResync()
        {
            if (room.Ready && !room.IsHost) room.SendToHost("RESYNC");
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
                            RaiseNotice(GameNotice.None);
                            RaiseChanged();
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
                    RaiseChanged();
                    return;
                }

                case "OFFER":
                    ApplyOffer(p);
                    return;

                case "NEWGAME":
                    VoidOffers();
                    pendingMove = null;
                    game.NewGame();
                    RaiseNotice(GameNotice.None);
                    if (NewGameStarted != null) NewGameStarted();
                    RaiseChanged();
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
                case "DRAW": if (p.Length == 3 && (p[2] == "W" || p[2] == "B")) drawOfferBy = SeatOf(p[2]); break;
                case "NEW": if (p.Length == 3 && (p[2] == "W" || p[2] == "B")) newOfferBy = SeatOf(p[2]); break;
                case "NONE": VoidOffers(); break;
                case "DECLINED":
                    if (p.Length == 3 && p[2] == "DRAW")
                    {
                        bool mine = IAmPlayer && drawOfferBy == room.MySeat;
                        drawOfferBy = Seat.None;
                        if (mine) RaiseNotice(GameNotice.DrawDeclined);
                    }
                    else if (p.Length == 3 && p[2] == "NEW")
                    {
                        bool mine = IAmPlayer && newOfferBy == room.MySeat;
                        newOfferBy = Seat.None;
                        if (mine) RaiseNotice(GameNotice.RematchDeclined);
                    }
                    break;
            }
            RaiseChanged();
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

            if (Replaying != null) Replaying(true);
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
                if (Replaying != null) Replaying(false);
            }
            RaiseChanged();
        }
    }
}
