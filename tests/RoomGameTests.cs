// Tests of the game of a room (RoomGame) on the in-memory transport: the host validates and announces, everybody else applies.
// Three or more copies of the game (host, players, spectators) each with its own Game; no windows. Part of tests\run-room-tests.bat.
using System;
using System.Collections.Generic;
using ChessGame;

static partial class RoomTests
{
    /// <summary>A person in the room together with its copy of the game and a log of what the game reported.</summary>
    sealed class GamePeer
    {
        public readonly Peer P;
        public readonly Game G = new Game();
        public readonly RoomGame RG;
        public readonly List<GameNotice> Notices = new List<GameNotice>();
        public int NewGames, Changes, ReplayOn, ReplayOff;
        bool inRoom;

        public GamePeer(Peer p)
        {
            P = p;
            RG = new RoomGame(p.S, G);
            RG.Changed += delegate { Changes++; };
            RG.Notice += delegate(GameNotice n) { Notices.Add(n); };
            RG.NewGameStarted += delegate { NewGames++; };
            RG.Replaying += delegate(bool on) { if (on) ReplayOn++; else ReplayOff++; };
            // the window forwards the room's events in exactly this way
            p.S.Welcomed += delegate { inRoom = true; RG.Welcomed(); };
            p.S.Changed += delegate { if (inRoom) RG.RoomChanged(); };
            p.S.HostChanged += delegate(string oldId, string newId) { RG.HostChanged(); };
            p.S.NeedsSync += delegate(string id) { if (p.S.IsHost) RG.SendState(id); };
            p.S.GameLine += delegate(string from, string line) { RG.Receive(from, line); };
            if (p.S.Ready) { inRoom = true; RG.Welcomed(); }   // the host is in the room before anybody can wrap it
        }

        public string Id { get { return P.T.MyId; } }
    }

    static GamePeer NewGameHost(string name, Seat seat)
    {
        return new GamePeer(NewHost(name, seat));
    }

    /// <summary>Joins and gets approved with the given seat; the copy of the game is wired before the host sends the game.</summary>
    static GamePeer JoinGame(GamePeer host, string name, Seat seat)
    {
        Peer p = NewClient(name, seat != Seat.None);
        GamePeer gp = new GamePeer(p);
        TestKit.WaitFor(delegate { return host.P.S.PendingIds.Length > 0 && host.P.Asked.Count > 0; }, 1500);
        host.P.Asked.Clear();
        host.P.S.Approve(p.T.MyId, seat);
        TestKit.WaitFor(delegate { return p.Welcomed && p.S.Find(p.S.MyId) != null; }, 1500);
        TestKit.Check(p.Welcomed, name + " welcomed");
        return gp;
    }

    /// <summary>What the player does on the board: the move is played locally, then the game is told.</summary>
    static void PlayAs(GamePeer gp, string uci)
    {
        foreach (Move m in gp.G.Legal)
        {
            if (m.ToString() == uci) { gp.G.Play(m); gp.RG.AfterLocalMove(m); return; }
        }
        throw new Exception("illegal test move " + uci);
    }

    static string Describe(GamePeer gp)
    {
        return gp.G.PlyCount + "|" + gp.G.Status + "|" + gp.G.Pos.ToFen();
    }

    static bool SameGame(params GamePeer[] peers)
    {
        string first = Describe(peers[0]);
        foreach (GamePeer p in peers) if (Describe(p) != first) return false;
        return true;
    }

    static void CheckSameGame(string what, params GamePeer[] peers)
    {
        TestKit.WaitFor(delegate { return SameGame(peers); }, 2000);
        List<string> d = new List<string>();
        foreach (GamePeer p in peers) d.Add(p.P.S.MyName + "=" + Describe(p));
        TestKit.Check(SameGame(peers), what + ": every copy shows the same game (" + string.Join(" / ", d.ToArray()) + ")");
    }

    // ------------------------------------------------------------------ scenarios

    static void TestGameMoves()
    {
        Console.WriteLine("game: moves go through the host");
        GamePeer a = NewGameHost("Alice", Seat.White);
        GamePeer b = JoinGame(a, "Bob", Seat.Black);
        GamePeer c = JoinGame(a, "Carol", Seat.None);
        Settle();
        int syncsAtStart = b.ReplayOn;   // the game that the host sends to everybody who joins
        TestKit.Check(a.RG.Full && b.RG.Full && c.RG.Full, "everybody sees both seats taken");

        PlayAs(a, "e2e4");
        CheckSameGame("the host's move", a, b, c);
        PlayAs(b, "e7e5");
        CheckSameGame("a player's move (validated by the host)", a, b, c);
        TestKit.Check(a.G.PlyCount == 2 && b.ReplayOn == syncsAtStart, "no resync was needed so far (the echo of our own move is ignored)");

        // a request that is out of turn: the host shows the sender the real game
        int before = b.ReplayOn;
        b.P.S.SendToHost("MOVE 2 d7d6");
        TestKit.WaitFor(delegate { return b.ReplayOn > before; }, 2000);
        TestKit.Check(b.ReplayOn == before + 1 && b.ReplayOff == b.ReplayOn, "an out-of-turn request is answered with the whole game, once");
        CheckSameGame("after the out-of-turn request", a, b, c);

        // a duplicated request: the second copy is late
        PlayAs(a, "g1f3");
        b.P.S.SendToHost("MOVE 2 g1f3");   // not black's move and a stale ply
        Settle();
        CheckSameGame("after a duplicate", a, b, c);

        // nonsense from a player: illegal move text, malformed line
        before = b.ReplayOn;
        b.P.S.SendToHost("MOVE 3 a1a8");
        b.P.S.SendToHost("MOVE x e7e5");
        TestKit.WaitFor(delegate { return b.ReplayOn >= before + 2; }, 2000);
        TestKit.Check(b.ReplayOn == before + 2, "an illegal or malformed request gets the whole game back");
        CheckSameGame("after nonsense", a, b, c);

        // a spectator cannot move, a client cannot announce
        before = c.ReplayOn;
        c.P.S.SendToHost("MOVE 3 b8c6");
        b.P.T.Broadcast("PLAYED 3 b8c6");
        b.P.T.Broadcast("RESULT 3 WR");
        Settle();
        TestKit.Check(a.G.PlyCount == 3 && b.G.PlyCount == 3 && c.G.PlyCount == 3 && c.ReplayOn == before, "a spectator's request and a client's announcements change nothing");

        PlayAs(b, "b8c6");
        CheckSameGame("black's move", a, b, c);
        Cleanup();
    }

    static void TestGameOffers()
    {
        Console.WriteLine("game: draws, resignation, rematch");
        GamePeer a = NewGameHost("Alice", Seat.White);
        GamePeer b = JoinGame(a, "Bob", Seat.Black);
        GamePeer c = JoinGame(a, "Carol", Seat.None);
        Settle();
        PlayAs(a, "e2e4");
        CheckSameGame("white moved", a, b, c);
        PlayAs(b, "e7e5");
        CheckSameGame("start", a, b, c);

        // declined draw
        TestKit.Check(a.RG.CanOfferDraw, "white may offer a draw");
        a.RG.OfferDraw();
        TestKit.WaitFor(delegate { return b.RG.DrawOfferedBy == Seat.White && c.RG.DrawOfferedBy == Seat.White; }, 2000);
        TestKit.Check(a.RG.DrawOfferedBy == Seat.White && b.RG.DrawOfferedBy == Seat.White && c.RG.DrawOfferedBy == Seat.White, "the offer is shown to everybody, spectators too");
        TestKit.Check(!a.RG.CanOfferDraw, "a second offer is not possible while one is open");
        a.Notices.Clear();
        b.RG.AnswerDraw(false);
        TestKit.WaitFor(delegate { return a.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None; }, 2000);
        TestKit.Check(a.RG.DrawOfferedBy == Seat.None && b.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None, "a declined offer is gone everywhere");
        TestKit.Check(a.Notices.Contains(GameNotice.DrawDeclined) && !b.Notices.Contains(GameNotice.DrawDeclined), "only the one who offered is told that it was declined");
        TestKit.Check(a.G.Status == GameStatus.Playing, "the game goes on");

        // an offer lapses with the next move
        b.RG.OfferDraw();
        TestKit.WaitFor(delegate { return a.RG.DrawOfferedBy == Seat.Black; }, 2000);
        PlayAs(a, "g1f3");
        TestKit.WaitFor(delegate { return b.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None; }, 2000);
        TestKit.Check(a.RG.DrawOfferedBy == Seat.None && b.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None, "a move voids an open offer everywhere");
        PlayAs(b, "b8c6");
        CheckSameGame("after the lapsed offer", a, b, c);

        // both ask: that is an agreement
        a.RG.OfferDraw();
        b.RG.OfferDraw();
        TestKit.WaitFor(delegate { return a.G.Status == GameStatus.DrawAgreed && b.G.Status == GameStatus.DrawAgreed && c.G.Status == GameStatus.DrawAgreed; }, 2000);
        TestKit.Check(a.G.Status == GameStatus.DrawAgreed && b.G.Status == GameStatus.DrawAgreed && c.G.Status == GameStatus.DrawAgreed, "two offers at once are an agreement");

        // rematch declined: the old game stays
        a.Notices.Clear();
        TestKit.Check(a.RG.CanRequestRematch, "a rematch can be asked for");
        a.RG.RequestRematch();
        TestKit.WaitFor(delegate { return b.RG.RematchOfferedBy == Seat.White && c.RG.RematchOfferedBy == Seat.White; }, 2000);
        TestKit.Check(!a.RG.CanRequestRematch, "and only once");
        b.RG.AnswerRematch(false);
        TestKit.WaitFor(delegate { return a.RG.RematchOfferedBy == Seat.None; }, 2000);
        TestKit.Check(a.Notices.Contains(GameNotice.RematchDeclined) && a.G.PlyCount == 4 && a.G.Status == GameStatus.DrawAgreed, "declined: the asker is told, the old game stays");

        // rematch accepted: colours swap, a new game starts everywhere
        int games = c.NewGames;
        a.RG.RequestRematch();
        TestKit.WaitFor(delegate { return b.RG.RematchOfferedBy == Seat.White; }, 2000);
        b.RG.AnswerRematch(true);
        TestKit.WaitFor(delegate { return a.G.PlyCount == 0 && b.G.PlyCount == 0 && c.G.PlyCount == 0 && a.P.S.MySeat == Seat.Black; }, 2000);
        TestKit.Check(a.P.S.MySeat == Seat.Black && b.P.S.MySeat == Seat.White, "the colours were swapped");
        TestKit.Check(a.G.PlyCount == 0 && b.G.PlyCount == 0 && c.G.PlyCount == 0 && a.G.Status == GameStatus.Playing, "a new game on every copy");
        TestKit.Check(c.NewGames == games + 1 && a.NewGames >= 1 && b.NewGames >= 1, "every copy was told once that a new game started");
        TestKit.Check(a.RG.RematchOfferedBy == Seat.None && b.RG.RematchOfferedBy == Seat.None, "no offer survives");
        TestKit.Check(a.Notices[a.Notices.Count - 1] == GameNotice.None, "the old notices are cleared");

        // resignation, from the player who is not the host
        PlayAs(b, "e2e4");
        CheckSameGame("the new game: white moved", a, b, c);
        PlayAs(a, "e7e5");
        a.RG.Resign();
        TestKit.WaitFor(delegate { return a.G.Status == GameStatus.BlackResigned && b.G.Status == GameStatus.BlackResigned && c.G.Status == GameStatus.BlackResigned; }, 2000);
        TestKit.Check(a.G.Status == GameStatus.BlackResigned && b.G.Status == GameStatus.BlackResigned && c.G.Status == GameStatus.BlackResigned, "a resignation reaches everybody");
        CheckSameGame("after the resignation", a, b, c);

        // both ask for a rematch at once: agreement
        a.RG.RequestRematch();
        b.RG.RequestRematch();
        TestKit.WaitFor(delegate { return a.G.PlyCount == 0 && b.G.PlyCount == 0 && a.P.S.MySeat == Seat.White; }, 2000);
        TestKit.Check(a.G.PlyCount == 0 && b.G.PlyCount == 0 && a.P.S.MySeat == Seat.White, "two requests at once start the game (colours swapped back)");
        Cleanup();
    }

    static void TestGameSeats()
    {
        Console.WriteLine("game: seats, pause, new game after a finished one");
        GamePeer a = NewGameHost("Alice", Seat.White);
        GamePeer b = JoinGame(a, "Bob", Seat.Black);
        GamePeer c = JoinGame(a, "Carol", Seat.None);
        Settle();
        PlayAs(a, "e2e4");
        CheckSameGame("start", a, b, c);

        // an offer to somebody who leaves the table lapses; the game waits
        a.RG.OfferDraw();
        TestKit.WaitFor(delegate { return b.RG.DrawOfferedBy == Seat.White; }, 2000);
        b.P.S.RequestStand();
        TestKit.WaitFor(delegate { return !a.RG.Full && a.RG.DrawOfferedBy == Seat.None; }, 2000);
        TestKit.Check(!a.RG.Full && !c.RG.Full, "a free seat: the game waits");
        TestKit.WaitFor(delegate { return b.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None; }, 2000);
        TestKit.Check(a.RG.DrawOfferedBy == Seat.None && b.RG.DrawOfferedBy == Seat.None && c.RG.DrawOfferedBy == Seat.None, "the offer lapsed on every copy");
        TestKit.Check(a.G.PlyCount == 1 && a.G.Status == GameStatus.Playing, "and the position is kept");

        // somebody sits down at the free seat of an unfinished game: it just goes on
        int games = c.NewGames;
        a.P.S.SetSeat(c.Id, Seat.Black);
        TestKit.WaitFor(delegate { return a.RG.Full; }, 2000);
        Settle();
        TestKit.Check(c.NewGames == games, "sitting down at an unfinished game does not restart it");

        // a finished game and a change of players: a new game starts when the table is full again
        c.RG.Resign();
        TestKit.WaitFor(delegate { return a.G.Status == GameStatus.BlackResigned; }, 2000);
        TestKit.Check(a.G.Status == GameStatus.BlackResigned, "Carol resigned");
        c.P.S.RequestStand();
        TestKit.WaitFor(delegate { return !a.RG.Full; }, 2000);
        games = b.NewGames;
        b.P.S.RequestSeat(Seat.Black);
        TestKit.WaitFor(delegate { return a.RG.Full; }, 2000);
        TestKit.WaitFor(delegate { return a.G.PlyCount == 0 && b.G.PlyCount == 0 && c.G.PlyCount == 0; }, 2000);
        TestKit.Check(a.G.PlyCount == 0 && b.G.PlyCount == 0 && c.G.PlyCount == 0 && a.G.Status == GameStatus.Playing,
            "a full table after a finished game starts a new game everywhere");
        TestKit.Check(b.NewGames == games + 1, "once");
        Cleanup();
    }

    static void TestGameSync()
    {
        Console.WriteLine("game: the whole game for people who come late");

        // a long game (more than one SYNC line), played on the host's copy only
        GamePeer a = NewGameHost("Alice", Seat.White);
        GamePeer b = JoinGame(a, "Bob", Seat.Black);
        Settle();
        for (int seed = 1; seed < 50; seed++)
        {
            Random rnd = new Random(seed);
            a.G.NewGame();
            while (a.G.PlyCount < 420 && a.G.Legal.Count > 0) a.G.Play(a.G.Legal[rnd.Next(a.G.Legal.Count)]);
            if (a.G.PlyCount >= 420) break;
        }
        TestKit.Check(a.G.PlyCount >= 420, "a long game exists for the test");
        GamePeer c = JoinGame(a, "Carol", Seat.None);
        CheckSameGame("a late spectator gets the long game (several SYNC lines)", a, c);
        TestKit.Check(c.ReplayOn >= 2 && c.ReplayOff == c.ReplayOn, "every SYNC line is one balanced replay block (" + c.ReplayOn + " lines for the long game)");
        Cleanup();

        // a resigned game, an open offer
        a = NewGameHost("Alice", Seat.White);
        b = JoinGame(a, "Bob", Seat.Black);
        Settle();
        PlayAs(a, "e2e4");
        CheckSameGame("white moved", a, b);
        PlayAs(b, "e7e5");
        CheckSameGame("black moved", a, b);
        b.RG.Resign();
        TestKit.WaitFor(delegate { return a.G.Status == GameStatus.BlackResigned; }, 2000);
        GamePeer d = JoinGame(a, "Dave", Seat.None);
        CheckSameGame("a late joiner gets a resigned game (RESULT after SYNC)", a, d);

        a.RG.RequestRematch();
        TestKit.WaitFor(delegate { return b.RG.RematchOfferedBy == Seat.White; }, 2000);
        GamePeer e = JoinGame(a, "Erin", Seat.None);
        TestKit.WaitFor(delegate { return e.RG.RematchOfferedBy == Seat.White; }, 2000);
        TestKit.Check(e.RG.RematchOfferedBy == Seat.White, "an open offer is sent to a late joiner");

        // SYNC with an unknown move stops quietly, in one replay block; garbage and lines that do not fit are ignored
        int on = d.ReplayOn;
        a.P.S.SendTo(d.Id, "SYNC 0 e2e4 zzzz e7e5");
        TestKit.WaitFor(delegate { return d.ReplayOn > on; }, 2000);
        TestKit.Check(d.ReplayOn == on + 1 && d.ReplayOff == d.ReplayOn && d.G.PlyCount == 1, "an unknown move ends the replay: " + d.G.PlyCount + " plies");
        a.P.S.SendTo(d.Id, "SYNC x e2e4");
        a.P.S.SendTo(d.Id, "SYNC 5 e2e4");   // does not continue the game this copy has
        Settle();
        TestKit.Check(d.ReplayOn == on + 1 && d.G.PlyCount == 1, "a SYNC that does not fit is ignored");
        Cleanup();
    }
    static void TestGameHostChange()
    {
        Console.WriteLine("game: a move that the old host never answered goes to the new host");
        GamePeer a = NewGameHost("Alice", Seat.White);
        GamePeer b = JoinGame(a, "Bob", Seat.Black);
        GamePeer c = JoinGame(a, "Carol", Seat.None);
        Settle();
        PlayAs(a, "e2e4");
        CheckSameGame("start", a, b, c);

        // Bob's move is lost on the way to the old host
        b.P.T.DropOutgoing = true;
        PlayAs(b, "e7e5");
        Settle();
        TestKit.Check(a.G.PlyCount == 1 && b.G.PlyCount == 2, "the old host never saw it");
        b.P.T.DropOutgoing = false;

        TestKit.Check(a.P.S.TransferHost(c.Id), "the host role goes to Carol");
        TestKit.WaitFor(delegate { return c.P.S.IsHost; }, 2000);
        CheckSameGame("the new host got the unconfirmed move", a, b, c);
        TestKit.Check(c.G.PlyCount == 2, "Carol applied Bob's move: " + c.G.PlyCount);

        // the new host validates and announces from now on
        PlayAs(a, "g1f3");
        CheckSameGame("a move through the new host", a, b, c);
        a.RG.OfferDraw();
        TestKit.WaitFor(delegate { return b.RG.DrawOfferedBy == Seat.White && c.RG.DrawOfferedBy == Seat.White; }, 2000);
        b.RG.AnswerDraw(true);
        TestKit.WaitFor(delegate { return a.G.Status == GameStatus.DrawAgreed && c.G.Status == GameStatus.DrawAgreed; }, 2000);
        TestKit.Check(a.G.Status == GameStatus.DrawAgreed && b.G.Status == GameStatus.DrawAgreed && c.G.Status == GameStatus.DrawAgreed, "offers work through the new host");
        Cleanup();
    }
}
