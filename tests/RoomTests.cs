// Unit tests of the room layer (members, seats, permissions, approval, host transfer) on an in-memory transport.
// Build & run:  tests\run-room-tests.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using ChessGame;

static class RoomTests
{
    static int failures;
    static Form anchor;

    static void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (condition()) return true;
            System.Threading.Thread.Sleep(3);
        }
        Application.DoEvents();
        return condition();
    }

    static void Pump(int ms) { WaitFor(delegate { return false; }, ms); }

    static void InstallContext()
    {
        if (anchor == null) { anchor = new Form(); anchor.Show(); }
        System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }

    /// <summary>One person in a test room with a log of what its session reported.</summary>
    sealed class Peer
    {
        public RoomSession S;
        public FakeRoomTransport T;
        public List<string> Lines = new List<string>();      // game lines that were allowed through: "from|line"
        public List<string> Notices = new List<string>();
        public List<string> Asked = new List<string>();      // JoinRequested ids
        public List<string> SeatAsked = new List<string>();
        public List<string> Synced = new List<string>();
        public string RejectedText, RemovedText, ClosedText;
        public bool Welcomed;
        public List<string> HostEvents = new List<string>();

        public Peer(RoomSession s, FakeRoomTransport t)
        {
            S = s; T = t;
            s.GameLine += delegate(string from, string line) { Lines.Add(from + "|" + line); };
            s.Notice += delegate(string n) { Notices.Add(n); };
            s.JoinRequested += delegate(string id, string name, bool play) { Asked.Add(id + (play ? ":play" : ":watch")); };
            s.SeatRequested += delegate(string id, Seat seat) { SeatAsked.Add(id + ":" + seat); };
            s.NeedsSync += delegate(string id) { Synced.Add(id); };
            s.Rejected += delegate(string r) { RejectedText = r; };
            s.Removed += delegate(string r) { RemovedText = r; };
            s.Closed += delegate(string r) { ClosedText = r; };
            s.Welcomed += delegate { Welcomed = true; };
            s.HostChanged += delegate(string a, string b) { HostEvents.Add(a + ">" + b); };
        }

        public bool Got(string needle)
        {
            foreach (string l in Lines) if (l.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    static FakeRoomHub hub;
    static List<Peer> all;

    static Peer NewHost(string name, Seat seat)
    {
        InstallContext();
        hub = new FakeRoomHub();
        all = new List<Peer>();
        FakeRoomTransport t = FakeRoomTransport.Host(hub, name, "host-" + name);
        RoomSession s = RoomSession.CreateHost(t, name, seat);
        Peer p = new Peer(s, t);
        all.Add(p);
        s.Start();
        WaitFor(delegate { return p.S.Ready; }, 1000);
        return p;
    }

    static Peer NewClient(string name, bool play)
    {
        FakeRoomTransport t = FakeRoomTransport.Join(hub, name);
        RoomSession s = RoomSession.CreateClient(t, name, play);
        Peer p = new Peer(s, t);
        all.Add(p);
        s.Start();
        return p;
    }

    /// <summary>Joins and gets approved with the given seat; returns when the client is in.</summary>
    static Peer Join(Peer host, string name, Seat seat)
    {
        Peer p = NewClient(name, seat != Seat.None);
        WaitFor(delegate { return host.S.PendingIds.Length > 0 && host.Asked.Count > 0; }, 1500);
        string id = p.T.MyId;
        Check(host.S.IsPending(id), name + " is pending at the host");
        host.Asked.Clear();
        host.S.Approve(id, seat);
        WaitFor(delegate { return p.Welcomed && p.S.Find(p.S.MyId) != null; }, 1500);
        Check(p.Welcomed, name + " welcomed");
        return p;
    }

    static void Settle() { Pump(120); }

    static void Cleanup()
    {
        foreach (Peer p in all) p.S.Dispose();
        Pump(30);
    }

    static string SeatsOf(Peer p)
    {
        List<string> parts = new List<string>();
        foreach (RoomMember m in p.S.Members) parts.Add(m.Name + "=" + m.Seat + (m.ChatMuted ? "c" : "") + (m.VoiceMuted ? "v" : ""));
        return string.Join(",", parts.ToArray()) + "/h=" + p.S.HostId + "/" + (p.S.SpectatorChatAllowed ? "C" : "-") + (p.S.SpectatorVoiceAllowed ? "V" : "-");
    }

    static void CheckSame(string what)
    {
        string first = null;
        foreach (Peer p in all)
        {
            if (p.S.IsClosed || !p.S.Ready) continue;
            string s = SeatsOf(p);
            if (first == null) first = s;
            else Check(first == s, what + ": replicas agree (" + first + " vs " + s + ")");
        }
    }

    // ------------------------------------------------------------------ scenarios

    static void TestJoinAndSeats()
    {
        Console.WriteLine("join, approval, seats");
        Peer a = NewHost("Alice", Seat.White);
        Check(a.S.IsHost && a.S.MySeat == Seat.White, "host sits at white");

        Peer b = NewClient("Bob", true);
        WaitFor(delegate { return a.Asked.Count > 0; }, 1500);
        Check(a.Asked.Count == 1 && a.Asked[0].EndsWith(":play"), "host is asked about Bob (wants to play)");
        Check(!b.Welcomed, "Bob waits for the decision");
        a.S.Approve(b.T.MyId, Seat.Black);
        WaitFor(delegate { return b.Welcomed; }, 1500);
        Check(b.S.MySeat == Seat.Black && !b.S.IsHost, "Bob plays black");
        Check(a.Synced.Contains(b.T.MyId), "host is told to send the game to Bob");

        Peer c = Join(a, "Carol", Seat.None);
        Settle();
        Check(c.S.MySeat == Seat.None, "Carol watches");
        Check(a.S.PlayerCount == 2 && a.S.MemberCount == 3, "2 players 3 members");
        Check(a.T.ListedPlayers == 2 && a.T.ListedWatchers == 1, "listing says 2 + 1");
        CheckSame("after joins");

        // a seat that is taken cannot be given
        Check(!a.S.SetSeat(c.T.MyId, Seat.White), "white is taken");

        // a spectator asks for a seat: the host is asked (nobody sat there before)
        b.S.RequestStand();
        Settle();
        Check(b.S.MySeat == Seat.None && a.S.SeatHolder(Seat.Black) == null, "Bob stood up");
        c.S.RequestSeat(Seat.Black);
        Settle();
        Check(a.SeatAsked.Count == 1, "host is asked when Carol wants the black seat");
        a.S.SetSeat(c.T.MyId, Seat.Black);
        Settle();
        Check(c.S.MySeat == Seat.Black && b.S.Find(c.T.MyId).Seat == Seat.Black, "Carol sits black");

        // Bob had sat there: with the seat free again he gets it back without a question
        c.S.RequestStand();
        Settle();
        a.SeatAsked.Clear();
        b.S.RequestSeat(Seat.None);
        Settle();
        Check(a.SeatAsked.Count == 0 && b.S.MySeat == Seat.Black, "Bob returns to his old seat without asking");

        // someone asks for an occupied seat -> notice, nothing changes
        c.Notices.Clear();
        c.S.RequestSeat(Seat.Black);
        Settle();
        Check(c.S.MySeat == Seat.None && c.Notices.Count == 1, "occupied seat is refused with a notice");

        // the host's own seat changes at once
        a.S.RequestStand();
        Check(a.S.MySeat == Seat.None && a.S.SeatHolder(Seat.White) == null, "host stands up");
        a.S.RequestSeat(Seat.None);
        Check(a.S.MySeat == Seat.White, "host takes the free seat");
        Settle();
        CheckSame("after seat changes");

        // swapping colours
        a.S.SwapSeats();
        Settle();
        Check(a.S.MySeat == Seat.Black && b.S.MySeat == Seat.White, "colours swapped");
        CheckSame("after swap");
        Cleanup();
    }

    static void TestReject()
    {
        Console.WriteLine("rejection");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = NewClient("Bob", true);
        WaitFor(delegate { return a.Asked.Count > 0; }, 1500);
        a.S.Reject(b.T.MyId, "Нет мест.");
        WaitFor(delegate { return b.RejectedText != null; }, 1500);
        Check(b.RejectedText == "Нет мест.", "reason arrives: " + b.RejectedText);
        Check(a.S.MemberCount == 1, "host room unchanged");
        Cleanup();
    }

    static void TestPermissions()
    {
        Console.WriteLine("who may talk");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        Peer c = Join(a, "Carol", Seat.None);
        Peer d = Join(a, "Dave", Seat.None);
        Settle();

        c.S.Broadcast("CHAT hello");
        Settle();
        Check(a.Got("CHAT hello") && b.Got("CHAT hello") && d.Got("CHAT hello"), "a spectator's chat reaches everybody");

        // policy: spectators may not write
        a.S.SetPolicy(false, true);
        Settle();
        Check(!c.S.SpectatorChatAllowed && c.S.SpectatorVoiceAllowed, "policy is replicated");
        a.Lines.Clear(); b.Lines.Clear(); d.Lines.Clear();
        c.S.Broadcast("CHAT muted?");
        Settle();
        Check(!a.Got("muted?") && !b.Got("muted?") && !d.Got("muted?"), "muted spectators cannot chat (sender side and receivers)");
        c.S.Broadcast("VOICE AAAA");
        Settle();
        Check(a.Got("VOICE AAAA") && d.Got("VOICE AAAA"), "voice is still allowed");
        b.S.Broadcast("CHAT players write");
        a.S.Broadcast("CHAT host writes");
        Settle();
        Check(c.Got("players write") && c.Got("host writes"), "players and host always may");

        // a spectator that ignores its own state: receivers still drop the line
        c.S.SetPolicy(true, true);   // not the host: no effect
        Settle();
        Check(!c.S.SpectatorChatAllowed && !a.S.SpectatorChatAllowed == true, "only the host changes the policy");

        // personal mutes
        a.S.SetPolicy(true, true);
        a.S.SetMute(d.T.MyId, true, false);
        Settle();
        a.Lines.Clear(); c.Lines.Clear();
        d.S.Broadcast("CHAT x1");
        c.S.Broadcast("CHAT x2");
        Settle();
        Check(!a.Got("x1") && !c.Got("x1"), "Dave is muted personally");
        Check(a.Got("x2"), "Carol is not");
        a.S.SetMute(d.T.MyId, false, true);
        Settle();
        a.Lines.Clear();
        d.S.Broadcast("CHAT x3");
        d.S.Broadcast("VOICE BBBB");
        Settle();
        Check(a.Got("x3") && !a.Got("BBBB"), "chat back, voice off for Dave");
        CheckSame("after mutes");

        // players are never muted, even by name
        a.S.SetMute(b.T.MyId, true, true);
        Settle();
        a.Lines.Clear();
        b.S.Broadcast("CHAT still here");
        Settle();
        Check(a.Got("still here"), "a seated player can always talk");

        // standing up makes the mute apply
        b.S.RequestStand();
        Settle();
        a.Lines.Clear();
        b.S.Broadcast("CHAT now muted");
        Settle();
        Check(!a.Got("now muted"), "the mute applies once Bob watches");
        Cleanup();
    }

    static void TestGameTraffic()
    {
        Console.WriteLine("game lines go through the host");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        Peer c = Join(a, "Carol", Seat.None);
        Settle();

        b.S.SendToHost("MOVE 0 e7e5");
        c.S.SendToHost("MOVE 0 d7d5");
        Settle();
        Check(a.Got("MOVE 0 e7e5"), "a player's request reaches the host");
        Check(!a.Got("d7d5"), "a spectator's request is dropped by the host");

        a.S.Broadcast("PLAYED 0 e2e4");
        Settle();
        Check(b.Got("PLAYED 0 e2e4") && c.Got("PLAYED 0 e2e4"), "stamped lines reach everybody");

        // forgeries: a client broadcasts a stamped line, nobody believes it
        b.Lines.Clear(); c.Lines.Clear(); a.Lines.Clear();
        b.S.Broadcast("PLAYED 1 e7e5");
        b.T.Broadcast("PLAYED 1 e7e5");     // around the session
        b.T.Broadcast("RESULT 1 RESIGN B");
        Settle();
        Check(a.Lines.Count == 0 && c.Lines.Count == 0, "client-made PLAYED/RESULT are ignored (" + a.Lines.Count + "," + c.Lines.Count + ")");

        // a request line that is not from a seat holder never reaches the game
        a.S.SendToHost("MOVE 1 e7e5");     // the host cannot send to itself: no effect
        Settle();
        Check(!c.Got("MOVE"), "requests do not leak to spectators");
        Cleanup();
    }

    static void TestKickAndLeave()
    {
        Console.WriteLine("kick and leave");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        Peer c = Join(a, "Carol", Seat.None);
        Settle();
        List<string> left = new List<string>();
        a.S.MemberLeft += delegate(string id) { left.Add(id); };
        List<string> leftAtB = new List<string>();
        b.S.MemberLeft += delegate(string id) { leftAtB.Add(id); };

        a.S.Kick(c.T.MyId);
        WaitFor(delegate { return c.RemovedText != null; }, 1500);
        Check(c.RemovedText != null, "Carol is told");
        Settle();
        Check(a.S.MemberCount == 2 && b.S.MemberCount == 2, "Carol is gone for everybody");
        Check(left.Count == 1 && leftAtB.Count == 1, "leave events at host and at Bob");
        Check(a.S.RememberedSeat(c.T.MyId) == Seat.None, "a spectator leaves no seat memory");

        // a player who closes: the seat is free, remembered for a return
        string bobId = b.T.MyId;
        b.S.Close();
        Settle();
        Check(a.S.MemberCount == 1 && a.S.SeatHolder(Seat.Black) == null, "Bob's seat is vacant");
        Check(a.S.RememberedSeat(bobId) == Seat.Black, "his seat is remembered");
        Cleanup();
    }

    static void TestFull()
    {
        Console.WriteLine("room is full");
        Peer a = NewHost("Alice", Seat.White);
        for (int i = 1; i < RoomSession.MaxMembers; i++) Join(a, "P" + i, i == 1 ? Seat.Black : Seat.None);
        Settle();
        Check(a.S.MemberCount == RoomSession.MaxMembers, "room holds " + RoomSession.MaxMembers);
        Peer late = NewClient("Late", false);
        WaitFor(delegate { return late.RejectedText != null; }, 2000);
        Check(late.RejectedText != null && late.RejectedText.IndexOf("заполн", StringComparison.Ordinal) >= 0, "the eleventh is turned away: " + late.RejectedText);
        CheckSame("full room");
        Cleanup();
    }

    static void TestHostTransfer()
    {
        Console.WriteLine("handing the host role over");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        Peer c = Join(a, "Carol", Seat.None);
        Settle();

        Check(!b.S.TransferHost(c.T.MyId), "only the host may hand over");
        Check(!a.S.TransferHost(a.T.MyId), "not to oneself");
        Check(a.S.TransferHost(c.T.MyId), "handing over to a spectator");
        Settle();
        Check(a.S.HostId == c.T.MyId && b.S.HostId == c.T.MyId && c.S.HostId == c.T.MyId, "everyone agrees on the new host");
        Check(c.S.IsHost && !a.S.IsHost && !b.S.IsHost, "only Carol hosts now");
        Check(a.HostEvents.Count == 1 && b.HostEvents.Count == 1 && c.HostEvents.Count == 1, "host-change events: " + a.HostEvents.Count + b.HostEvents.Count + c.HostEvents.Count);
        Check(a.S.MySeat == Seat.White && b.S.MySeat == Seat.Black, "players keep their seats");
        Check(c.T.ListedPlayers == 2, "the new host maintains the listing");

        // the old host is an ordinary member now
        Check(!a.S.SetSeat(a.T.MyId, Seat.None), "Alice cannot decide any more");
        a.S.RequestStand();
        Settle();
        Check(a.S.MySeat == Seat.None && c.S.SeatHolder(Seat.White) == null, "her request is served by Carol");
        c.S.SetPolicy(false, false);
        Settle();
        Check(!a.S.SpectatorChatAllowed && !b.S.SpectatorVoiceAllowed, "the new host sets the policy");
        CheckSame("after the transfer");

        // requests now go to the new host
        b.S.SendToHost("MOVE 0 e7e5");
        Settle();
        Check(c.Got("MOVE 0 e7e5") && !a.Got("MOVE 0 e7e5"), "requests reach the new host");
        // and only the new host's stamped lines count
        a.Lines.Clear(); b.Lines.Clear();
        c.S.Broadcast("PLAYED 0 e2e4");
        a.T.Broadcast("PLAYED 0 d2d4");
        Settle();
        Check(b.Got("e2e4") && !b.Got("d2d4"), "the old host's lines are no longer believed");

        // and back
        Check(c.S.TransferHost(a.T.MyId), "handing back");
        Settle();
        Check(a.S.IsHost && b.S.HostId == a.T.MyId, "Alice is the host again");
        CheckSame("after handing back");
        Cleanup();
    }

    static void TestPendingDuringTransfer()
    {
        Console.WriteLine("a joiner waiting while the host changes");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        Peer d = NewClient("Dave", false);
        WaitFor(delegate { return a.Asked.Count > 0; }, 1500);
        Check(a.S.TransferHost(b.T.MyId), "hand over with Dave waiting");
        WaitFor(delegate { return d.RejectedText != null; }, 2000);
        Check(d.RejectedText != null, "Dave is told to try again: " + d.RejectedText);
        Cleanup();
    }

    static void TestSeatMemory()
    {
        Console.WriteLine("a seat taken away by the host is not remembered");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        a.S.SetSeat(b.T.MyId, Seat.None);   // the host's decision
        Settle();
        a.SeatAsked.Clear();
        b.S.RequestSeat(Seat.None);
        Settle();
        Check(a.SeatAsked.Count == 1 && b.S.MySeat == Seat.None, "sitting back after being removed needs the host's word (" + a.SeatAsked.Count + ")");

        a.S.SetSeat(b.T.MyId, Seat.Black);
        Settle();
        b.S.RequestStand();   // his own decision
        Settle();
        a.SeatAsked.Clear();
        b.S.RequestSeat(Seat.None);
        Settle();
        Check(a.SeatAsked.Count == 0 && b.S.MySeat == Seat.Black, "a seat one left by oneself can be taken again");
        Cleanup();
    }

    static void TestRefusedHandOver()
    {
        Console.WriteLine("the platform refuses a hand-over");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        a.T.RefuseOwnership = true;
        Check(!a.S.TransferHost(b.T.MyId), "TransferHost reports the failure");
        Settle();
        Check(a.S.IsHost && !b.S.IsHost && b.S.HostId == a.T.MyId, "nothing changed");
        Check(a.Notices.Count == 1, "the host is told why");
        Cleanup();
    }

    static void TestHostCloses()
    {
        Console.WriteLine("host closes the room");
        Peer a = NewHost("Alice", Seat.White);
        Peer b = Join(a, "Bob", Seat.Black);
        a.S.Close();
        WaitFor(delegate { return b.ClosedText != null; }, 1500);
        Check(b.ClosedText != null, "Bob is told: " + b.ClosedText);
        Cleanup();
    }

    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        try
        {
            TestJoinAndSeats();
            TestReject();
            TestPermissions();
            TestGameTraffic();
            TestKickAndLeave();
            TestFull();
            TestHostTransfer();
            TestPendingDuringTransfer();
            TestSeatMemory();
            TestRefusedHandOver();
            TestHostCloses();
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine("EXCEPTION: " + e);
        }
        Console.WriteLine(failures == 0 ? "ALL ROOM TESTS PASSED" : failures + " ROOM TEST FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
