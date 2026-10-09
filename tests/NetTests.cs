// In-process network tests: several game windows meet in a room over TCP loopback (LAN) or over fake Steam lobbies.
// Build & run:  tests\run-net-tests.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using ChessGame;

static class NetTests
{
    const int Port = 5791;
    static int failures;

    static void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            progress++;
            Application.DoEvents();
            KeepContext();
            if (condition()) return true;
            System.Threading.Thread.Sleep(5);
        }
        Application.DoEvents();
        KeepContext();
        return condition();
    }

    static void Pump(int ms)
    {
        WaitFor(delegate { return false; }, ms);
    }

    /// <summary>Application.DoEvents can leave a plain synchronization context behind; objects created next would post to the pool.</summary>
    static void KeepContext()
    {
        if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }

    const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static T Get<T>(object o, string field)
    {
        FieldInfo f = o.GetType().GetField(field, All);
        if (f == null) throw new Exception("no field " + field);
        return (T)f.GetValue(o);
    }

    static object Call(object o, string method, params object[] args)
    {
        MethodInfo m = o.GetType().GetMethod(method, All);
        if (m == null) throw new Exception("no method " + method);
        return m.Invoke(o, args);
    }

    static string State(MainForm f) { return f.NetStateForTest; }
    static Game GameOf(MainForm f) { return Get<Game>(f, "game"); }
    static BoardControl BoardOf(MainForm f) { return Get<BoardControl>(f, "board"); }
    static string StatusText(MainForm f) { return f.StatusText; }
    static string Id(MainForm f) { return f.RoomForTest.MyId; }

    static Form anchor;

    /// <summary>Application.Run does this in the real program; closing windows in a test can undo it.</summary>
    static void InstallContext()
    {
        if (anchor == null) { anchor = new Form(); Offscreen(anchor); anchor.Show(); }
        System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }

    static bool useSteam;
    static FakeHub hub = new FakeHub();
    static int windowCounter;
    static readonly string[] Names = { "Alice", "Bob", "Carol", "Dave", "Erin" };

    static MainForm NewWindow()
    {
        MainForm f = new MainForm();
        f.NetPort = Port;
        int n = windowCounter++;
        f.PlayerNameForTest = Names[n % Names.Length];
        f.LanKeyForTest = "testkey" + n + "-" + new string('k', 24);
        if (useSteam)
        {
            FakeLobbyService svc = new FakeLobbyService(hub, Names[n % Names.Length]);
            f.LobbyServiceFactory = delegate { return svc; };
            f.TestSetTransport(1);
        }
        else
        {
            // saved choices leak between windows inside one test process, so be explicit
            // and make it impossible for a test to reach the real Steam client
            f.TestSetTransport(0);
            f.LobbyServiceFactory = delegate { throw new SteamUnavailableException("tests must never use the real Steam"); };
        }
        f.AskYesNo = delegate(string t, string q) { return true; };
        Offscreen(f);
        f.Show();
        BoardOf(f).AnimationsEnabled = false;
        f.TestSetMode(2);
        Pump(50);
        return f;
    }

    /// <summary>Test windows must not appear on (or steal the focus from) the screen of whoever runs the tests.</summary>
    static void Offscreen(Form f)
    {
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new System.Drawing.Point(-6000, -6000);
    }

    static void PlayMove(MainForm f, string uci)
    {
        foreach (Move m in GameOf(f).Legal)
            if (m.ToString() == uci) { Call(f, "OnUserMove", m); return; }
        throw new Exception("illegal test move " + uci);
    }

    static void ResetWorld(bool steam)
    {
        useSteam = steam;
        if (steam) hub = new FakeHub();
        windowCounter = 0;
        InstallContext();
    }

    static ulong FirstChessLobby()
    {
        foreach (FakeHub.Lobby l in hub.Lobbies.Values)
        {
            string v;
            if (l.Data.TryGetValue(SteamLink.LobbyKey, out v) && v == SteamLink.LobbyValue && l.Joinable) return l.Id;
        }
        return 0;
    }

    static void OpenRoom(MainForm host, int seatIndex)
    {
        host.TestSetHostSeat(seatIndex);
        Call(host, "NetHostClicked");
        Check(WaitFor(delegate { return host.InRoomForTest; }, 4000), "host room is open (" + State(host) + ")");
        if (useSteam) Check(WaitFor(delegate { return FirstChessLobby() != 0; }, 3000), "host lobby is published");
    }

    /// <summary>Starts a join and lets the host accept; returns when the newcomer is in the room.</summary>
    static void JoinRoom(MainForm host, MainForm guest, bool play)
    {
        if (useSteam) Call(guest, "NetStartSteamJoin", FirstChessLobby(), play);
        else Call(guest, "NetStartJoin", "127.0.0.1", Port, play);
        Check(WaitFor(delegate { return host.TestAnswerRequest("join:", true); }, 5000), "the host was asked about the newcomer");
        Check(WaitFor(delegate { return guest.InRoomForTest; }, 5000), "the newcomer is in the room (" + State(guest) + ", " + guest.NetInfoForTest + ")");
    }

    /// <summary>host plays white or black, guest sits at the other seat; returns once both see a running game.</summary>
    static void Connect(MainForm host, MainForm guest, bool hostWhite)
    {
        OpenRoom(host, hostWhite ? 0 : 1);
        JoinRoom(host, guest, true);
        Check(WaitFor(delegate { return host.netStarted && guest.netStarted; }, 5000),
            "both sides see two players (host " + State(host) + "/" + host.netStarted + ", guest " + State(guest) + "/" + guest.netStarted
            + ", host status '" + host.StatusText.Replace("\n", " | ") + "')");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    /// <summary>The raw Steam layer, as far as it can be checked without a running Steam client.</summary>
    static void TestSteamNative()
    {
        Console.WriteLine("Steam native layer (no Steam client needed)");
        try
        {
            SteamNative.Load();
        }
        catch (SteamUnavailableException ex)
        {
            Console.WriteLine("  skipped: " + ex.Message);
            return;
        }
        Console.WriteLine("  steam_api64.dll: " + SteamNative.LoadedFrom);
        IntPtr module = GetModuleHandle("steam_api64.dll");
        Check(module != IntPtr.Zero, "the DLL is loaded under its own name (so DllImport finds it)");

        int checkedImports = 0;
        foreach (MethodInfo m in typeof(SteamNative).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            object[] attrs = m.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false);
            if (attrs.Length == 0) continue;
            checkedImports++;
            Check(GetProcAddress(module, m.Name) != IntPtr.Zero, "export exists: " + m.Name);
        }
        Check(checkedImports > 25, "checked " + checkedImports + " imports");

        // the loader's own acceptance test: a real Steam DLL passes, any other module is rejected
        Check(SteamNative.MissingExports(module).Count == 0, "the loaded DLL has everything the game needs");
        IntPtr user32 = GetModuleHandle("user32.dll");
        System.Collections.Generic.List<string> missingInOther = SteamNative.MissingExports(user32);
        Check(missingInOther.Count > 20, "a foreign DLL is rejected (missing " + missingInOther.Count + " functions)");

        // candidate list: nonexistent and non-Valve files are skipped, the next good one is used
        string good = SteamNative.LoadedFrom;
        System.Collections.Generic.List<string> tried = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> rejected = new System.Collections.Generic.List<string>();
        string notValve = System.IO.Path.Combine(Environment.SystemDirectory, "user32.dll");
        bool picked = SteamNative.TryLoadAnyForTest(new string[] { @"C:\no\such\steam_api64.dll", notValve, good }, tried, rejected);
        Check(picked && SteamNative.LoadedFrom == good, "skips bad candidates and loads the good one");

        // Without a running Steam the user must get a readable message, not a crash.
        if (System.Diagnostics.Process.GetProcessesByName("steam").Length > 0)
        {
            Console.WriteLine("  Steam is running: skipping the 'Steam not running' check");
            return;
        }
        string cwdFile = System.IO.Path.Combine(Environment.CurrentDirectory, "steam_appid.txt");
        string exeFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
        bool hadCwd = System.IO.File.Exists(cwdFile), hadExe = System.IO.File.Exists(exeFile);
        string message = null;
        try { SteamLobbyService.Acquire(); }
        catch (SteamUnavailableException ex) { message = ex.Message; }
        Check(message != null, "Acquire() reports 'Steam unavailable' when Steam is not running");
        Console.WriteLine("  message shown to the user: " + message);
        Check(System.IO.File.Exists(exeFile) && System.IO.File.ReadAllText(exeFile).Trim() == "480", "steam_appid.txt (480) written next to the exe");
        if (!hadCwd) try { System.IO.File.Delete(cwdFile); } catch (Exception) { }
        if (!hadExe) try { System.IO.File.Delete(exeFile); } catch (Exception) { }
    }

    static void TestDiscovery()
    {
        Console.WriteLine("LAN discovery");
        string foundName = null, foundAddr = null; int foundPort = 0;
        using (HostFinder finder = new HostFinder())
        {
            Check(finder.IsRunning, "finder socket bound");
            finder.Found += delegate(string name, string addr, int port) { foundName = name; foundAddr = addr; foundPort = port; };
            using (HostAnnouncer ann = new HostAnnouncer(Port, "TestHost"))
            {
                Check(WaitFor(delegate { return foundName != null; }, 4000), "announcement received");
            }
        }
        Check(foundName == "TestHost" && foundPort == Port, "announcement content: " + foundName + " " + foundAddr + ":" + foundPort);
        string h; int p;
        Check(JoinDialog.TryParseAddress("192.168.0.5", out h, out p) && h == "192.168.0.5" && p == 5757, "parse ip");
        Check(JoinDialog.TryParseAddress(" 10.0.0.1:6000 ", out h, out p) && h == "10.0.0.1" && p == 6000, "parse ip:port");
        Check(!JoinDialog.TryParseAddress("1.2.3.4:99999", out h, out p), "reject bad port");
        Check(!JoinDialog.TryParseAddress("", out h, out p), "reject empty");
    }

    /// <summary>The Steam transport on fake lobbies: ids are SteamIDs, listing counts, ownership hand-over.</summary>
    static void TestSteamTransport()
    {
        Console.WriteLine("Steam room transport over fake lobbies");
        ResetWorld(true);
        FakeLobbyService sa = new FakeLobbyService(hub, "Alice"), sb = new FakeLobbyService(hub, "Bob"), sc = new FakeLobbyService(hub, "Carol");
        SteamRoomTransport host = SteamRoomTransport.Host(sa, "Alice");
        List<string> hostGot = new List<string>(), guestGot = new List<string>();
        bool hostUp = false, guestUp = false;
        string guestClosed = null, joined = null;
        host.Connected += delegate { hostUp = true; };
        host.LineReceived += delegate(string from, string line) { hostGot.Add(from + "|" + line); };
        host.PeerConnected += delegate(string id, string name) { joined = id + "=" + name; };
        host.Start();
        Check(WaitFor(delegate { return hostUp; }, 2000), "host transport connected");
        Check(host.IsHost && host.MyId == sa.MySteamId.ToString() && host.HostId == host.MyId, "host ids");

        ulong lobby = FirstChessLobby();
        Check(lobby != 0 && hub.Lobbies[lobby].Max == 10, "the lobby holds ten people");
        List<LobbyInfo> found = null;
        sb.FindLobbies(delegate(List<LobbyInfo> l) { found = l; });
        Check(WaitFor(delegate { return found != null; }, 2000) && found.Count == 1 && found[0].HostName == "Alice", "guest finds the lobby");
        Check(found[0].Players == 1 && found[0].Watchers == 0, "the listing shows who plays and who watches: " + found[0].Players + "/" + found[0].Watchers);

        SteamRoomTransport guest = SteamRoomTransport.Join(sb, lobby, "Bob");
        guest.Connected += delegate { guestUp = true; };
        guest.LineReceived += delegate(string from, string line) { guestGot.Add(from + "|" + line); };
        guest.Closed += delegate(string r) { guestClosed = r; };
        guest.Start();
        Check(WaitFor(delegate { return guestUp && joined != null; }, 3000), "guest connected and the host heard its name");
        Check(joined == sb.MySteamId + "=Bob", "host learns id and name: " + joined);
        Check(!guest.IsHost && guest.HostId == host.MyId, "guest knows who the host is");

        SteamRoomTransport third = SteamRoomTransport.Join(sc, lobby, "Carol");
        List<string> thirdGot = new List<string>();
        third.LineReceived += delegate(string from, string line) { thirdGot.Add(from + "|" + line); };
        third.Start();
        Check(WaitFor(delegate { return hub.Lobbies[lobby].Members.Count == 3; }, 2000), "a third member can join (it is a room, not a duel)");

        host.Broadcast("ROOM 1");
        guest.SendTo(host.MyId, "REQ JOIN play");
        guest.Broadcast("CHAT hi");
        Check(WaitFor(delegate { return guestGot.Count == 1 && hostGot.Count == 2 && thirdGot.Count >= 2; }, 3000), "lines delivered");
        Pump(100);
        Check(guestGot.Count == 1 && guestGot[0] == host.MyId + "|ROOM 1", "broadcast reaches others, not the sender again: " + string.Join(";", guestGot.ToArray()));
        Check(thirdGot.Contains(guest.MyId + "|CHAT hi") && !thirdGot.Contains(guest.MyId + "|REQ JOIN play"), "private lines stay private, broadcasts reach everyone");

        // hand the lobby over
        host.TransferOwnership(guest.MyId);
        Check(hub.Lobbies[lobby].Owner == sb.MySteamId, "the lobby has a new owner");
        guest.NoteHost(guest.MyId);
        Check(guest.IsHost, "the new owner knows it is the host now");
        host.NoteHost(guest.MyId);
        Check(!host.IsHost, "and the old one knows it is not");

        guest.Close();
        host.Close();
        third.Close();
        Pump(50);
        Check(hub.Lobbies.Count == 0, "the empty lobby is gone");
    }

    // ------------------------------------------------------------------ the game

    static void TestGames()
    {
        Console.WriteLine("two windows, one game (" + (useSteam ? "Steam lobby" : "LAN") + ")");
        ResetWorld(useSteam);
        MainForm a = NewWindow(), b = NewWindow();
        bool answerA = true;
        a.AskYesNo = delegate(string t, string q) { return answerA; };

        Connect(a, b, true);
        Check(a.netIsWhite && !b.netIsWhite, "host white, guest black");
        Check(BoardOf(b).Flipped && !BoardOf(a).Flipped, "boards face their owners");
        Check(StatusText(a).StartsWith("Ваш ход") && StatusText(b).StartsWith("Ход соперника"), "turn texts: '" + StatusText(a) + "' / '" + StatusText(b) + "'");

        PlayMove(a, "e2e4");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 1; }, 2000), "e4 arrived at guest");
        Check(StatusText(b).StartsWith("Ваш ход"), "guest is on move: " + StatusText(b));
        PlayMove(b, "e7e5");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 2; }, 2000), "e5 arrived at host");
        PlayMove(a, "g1f3");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 3; }, 2000), "Nf3 arrived at guest");
        PlayMove(b, "b8c6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 4 && GameOf(b).PlyCount == 4; }, 2000), "four plies on both sides");
        Check(GameOf(a).Pos.ToFen() == GameOf(b).Pos.ToFen(), "positions are identical");

        // draw offer declined, then accepted
        Call(a, "NetDrawClicked");
        Check(WaitFor(delegate { return b.TestAnswerRequest("draw", false); }, 3000), "the guest was asked about a draw and declined");
        Check(WaitFor(delegate { return a.DrawOfferForTest == null && b.DrawOfferForTest == null; }, 2000), "decline clears the offer on both sides");
        Check(a.NetInfoForTest.Contains("отклонил"), "the offering side is told: " + a.NetInfoForTest);
        Check(GameOf(a).Status == GameStatus.Playing && GameOf(b).Status == GameStatus.Playing, "game goes on after decline");
        Call(a, "NetDrawClicked");
        Check(WaitFor(delegate { return b.TestAnswerRequest("draw", true); }, 3000), "asked again");
        Check(WaitFor(delegate { return GameOf(a).Status == GameStatus.DrawAgreed && GameOf(b).Status == GameStatus.DrawAgreed; }, 2000), "draw by agreement on both sides");

        // both ask at once: that is an agreement (checked later with rematch)
        // rematch declined, then accepted: colours swap
        Call(a, "NetRequestNewGame");
        Check(WaitFor(delegate { return b.TestAnswerRequest("new", false); }, 3000), "the guest declined the rematch");
        Check(WaitFor(delegate { return !a.NetInfoForTest.Contains("ждём"); }, 2000) && GameOf(a).PlyCount == 4, "old game still shown after decline: " + a.NetInfoForTest);
        Call(a, "NetRequestNewGame");
        Check(WaitFor(delegate { return b.TestAnswerRequest("new", true); }, 3000), "asked again and accepted");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 0 && GameOf(b).PlyCount == 0; }, 2000), "rematch started");
        Check(WaitFor(delegate { return !a.netIsWhite && b.netIsWhite; }, 2000), "colours swapped after rematch");
        Check(BoardOf(a).Flipped && !BoardOf(b).Flipped, "boards turned for the new colours");

        // resignation: now b is white and to move
        PlayMove(b, "d2d4");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 1; }, 2000), "d4 arrived");
        Call(a, "NetResignClicked");
        Check(WaitFor(delegate { return GameOf(b).Status == GameStatus.BlackResigned; }, 2000), "black resigned seen by white: " + GameOf(b).Status);
        Check(GameOf(a).Status == GameStatus.BlackResigned, "resigner sees the same result");
        Check(StatusText(b).Contains("Вы победили") && StatusText(a).Contains("Вы проиграли"), "result texts: '" + StatusText(b) + "' / '" + StatusText(a) + "'");

        // checkmate (fool's mate): rematch asked by the other side, colours swap back (a white)
        Call(b, "NetRequestNewGame");
        Check(WaitFor(delegate { return a.TestAnswerRequest("new", true); }, 3000), "second rematch accepted");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 0 && GameOf(b).PlyCount == 0 && a.netIsWhite && !b.netIsWhite; }, 2000), "second rematch");
        PlayMove(a, "f2f3"); Check(WaitFor(delegate { return GameOf(b).PlyCount == 1; }, 2000), "f3");
        PlayMove(b, "e7e5"); Check(WaitFor(delegate { return GameOf(a).PlyCount == 2; }, 2000), "e5");
        PlayMove(a, "g2g4"); Check(WaitFor(delegate { return GameOf(b).PlyCount == 3; }, 2000), "g4");
        PlayMove(b, "d8h4"); Check(WaitFor(delegate { return GameOf(a).PlyCount == 4; }, 2000), "Qh4");
        Check(GameOf(a).Status == GameStatus.Checkmate && GameOf(b).Status == GameStatus.Checkmate, "mate detected on both sides");
        Check(StatusText(b).Contains("Вы победили") && StatusText(a).Contains("Вы проиграли"), "mate texts: '" + StatusText(b) + "'");

        // both sides ask for a rematch at the same moment
        Call(a, "NetRequestNewGame");
        Call(b, "NetRequestNewGame");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 0 && GameOf(b).PlyCount == 0 && !a.netIsWhite; }, 3000), "simultaneous requests are an agreement");

        // a cheating guest: out-of-turn, illegal and forged lines do nothing
        // (now a is black, b white and to move)
        a.RoomForTest.SendToHost("MOVE 0 e7e5");
        a.RoomForTest.Transport.Broadcast("PLAYED 0 e7e5");
        a.RoomForTest.Transport.Broadcast("RESULT 0 BR");
        Pump(500);
        Check(GameOf(a).PlyCount == 0 && GameOf(b).PlyCount == 0 && GameOf(a).Status == GameStatus.Playing && GameOf(b).Status == GameStatus.Playing,
            "illegal, out-of-turn and forged lines change nothing (" + GameOf(a).PlyCount + "/" + GameOf(b).PlyCount + ")");
        Check(a.InRoomForTest && b.InRoomForTest, "and nobody is thrown out");
        PlayMove(b, "e2e4");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 1; }, 2000), "the game goes on normally");

        // a player leaves: the seat is free and the game waits
        Call(b, "NetDisconnectClicked");
        Check(WaitFor(delegate { return a.RoomForTest.MemberCount == 1; }, 3000), "the host sees the guest leave");
        Check(StatusText(a).Contains("приостановлена"), "and says the game is paused: " + StatusText(a).Replace("\n", " | "));
        Check(!a.netStarted && BoardOf(a).HumanMask == 0, "nobody can move while a seat is empty");

        // leaving network mode closes everything
        a.TestSetMode(0);
        Check(State(a) == "Idle", "switching away from network mode closes the room");

        a.Close();
        b.Close();
    }

    static int Sq(string name) { return Sqr.Parse(name); }

    static void TestPremove()
    {
        Console.WriteLine("queued move (premove)");
        ResetWorld(false);

        // geometry only: blockers are ignored, own pieces are not targets
        Position start = Position.Start();
        System.Collections.Generic.List<int> rook = Premoves.Targets(start, Sq("a8") ^ 56 ^ 56);   // black rook a8
        Check(rook.Contains(Sq("a5")) && !rook.Contains(Sq("a7")) && !rook.Contains(Sq("b7")), "rook a8 may be queued to a5 through its own pawn, not onto it");
        System.Collections.Generic.List<int> knight = Premoves.Targets(start, Sq("g1"));
        Check(knight.Count == 2 && knight.Contains(Sq("f3")) && knight.Contains(Sq("h3")), "knight g1: f3 and h3 only (e2 is its own pawn)");
        System.Collections.Generic.List<int> pawn = Premoves.Targets(start, Sq("e2"));
        Check(pawn.Count == 2 && pawn.Contains(Sq("e3")) && pawn.Contains(Sq("e4")), "pawn e2: one or two squares ahead");
        Check(Premoves.Targets(start, Sq("e1")).Count == 0, "the king is boxed in: no targets");
        Position castle = Position.FromFen("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        Check(Premoves.Targets(castle, Sq("e1")).Contains(Sq("g1")), "the king may be queued two squares sideways (castling)");

        MainForm a = NewWindow(), b = NewWindow();
        Connect(a, b, true);   // a = white, b = black
        BoardControl ab = BoardOf(a), bb = BoardOf(b);

        Check(!ab.CanPremove() && bb.CanPremove(), "only the side that is waiting may queue a move");
        Check(!ab.TrySetPremove(Sq("e2"), Sq("e4")), "no queueing on your own turn");
        Check(!bb.TrySetPremove(Sq("e7"), Sq("d6")), "a pawn may not be queued diagonally onto an empty square");
        Check(!bb.TrySetPremove(Sq("e5"), Sq("e4")), "nothing to queue from an empty square");
        Check(bb.TrySetPremove(Sq("e7"), Sq("e5")), "black queues e7-e5 during white's turn");
        Check(bb.HasPremove && bb.PremoveText == "e7-e5", "the queued move is remembered: " + bb.PremoveText);
        Check(b.StatusText.Contains("e7-e5"), "the status card mentions it: " + b.StatusText.Replace("\n", " | "));

        PlayMove(a, "e2e4");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 2 && GameOf(b).PlyCount == 2; }, 4000), "the queued move was played by itself and reached the opponent");
        Check(!bb.HasPremove, "the queue is empty afterwards");

        // a queued move that has become impossible is dropped, not forced
        PlayMove(a, "g1f3");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 3; }, 3000), "white's second move arrived");
        PlayMove(b, "b8c6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 4; }, 3000), "black moved normally");
        PlayMove(a, "f1b5");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 5; }, 3000), "white's bishop move arrived");
        // the rook a1 can never jump over its pawn: a1-a5 passes the geometry test but is illegal when it comes to playing it
        Check(ab.TrySetPremove(Sq("a1"), Sq("a5")), "white queues a1-a5 while waiting");
        PlayMove(b, "g8f6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 6; }, 3000), "black's reply arrived");
        Check(WaitFor(delegate { return !ab.HasPremove; }, 3000), "an impossible queued move disappears");
        Pump(400);
        Check(GameOf(a).PlyCount == 6 && GameOf(b).PlyCount == 6, "and nothing was played in its place");

        // cancelling: black waits, queues, changes its mind
        Check(bb.TrySetPremove(Sq("a7"), Sq("a6")), "black queues a7-a6");
        bb.ClearPremove();
        Check(!bb.HasPremove, "a queued move can be cancelled");
        PlayMove(a, "d2d3");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 7; }, 3000), "white's d3 arrived");
        Pump(400);
        Check(GameOf(b).PlyCount == 7, "a cancelled move is not played");

        // the queue does not survive a new game
        Check(ab.TrySetPremove(Sq("h2"), Sq("h3")), "white queues h2-h3 (black is on move)");
        PlayMove(b, "a7a6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 8; }, 3000), "black played; the queued h3 follows");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 9 && GameOf(b).PlyCount == 9; }, 3000), "the queued h2-h3 was played");
        Check(ab.TrySetPremove(Sq("g2"), Sq("g3")), "white queues g2-g3 (black is on move)");
        Call(a, "NetRequestNewGame");
        Check(WaitFor(delegate { return b.TestAnswerRequest("new", true); }, 3000), "rematch accepted");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 0 && GameOf(b).PlyCount == 0; }, 3000), "rematch started");
        Check(!bb.HasPremove && !ab.HasPremove, "a new game starts with an empty queue");

        a.Close();
        b.Close();

        // against the computer: queue a move while it thinks
        MainForm c = new MainForm();
        c.LobbyServiceFactory = delegate { throw new SteamUnavailableException("tests must never use the real Steam"); };
        Offscreen(c);
        c.Show();
        BoardOf(c).AnimationsEnabled = false;
        c.TestSetMode(0);
        c.TestSetSide(0);
        Get<StepSlider>(c, "sliderLevel").Value = 0;   // the quickest opponent
        BoardControl cb = BoardOf(c);
        PlayMove(c, "e2e4");                            // from here on the computer is to move
        Check(cb.CanPremove(), "we may queue a move while the computer thinks");
        Check(cb.TrySetPremove(Sq("g1"), Sq("f3")), "queue g1-f3 against the computer");
        Check(WaitFor(delegate { return GameOf(c).PlyCount >= 3 && !cb.HasPremove; }, 10000), "the computer replied and the queued move followed");
        Check(GameOf(c).San.Count >= 3 && GameOf(c).San[2] == "Nf3", "the third ply is the queued Nf3: " + string.Join(" ", GameOf(c).San.ToArray()));
        c.Close();
    }

    static void TestApproval(bool steam)
    {
        Console.WriteLine("the host approves each person (" + (steam ? "Steam lobby" : "LAN") + ")");
        ResetWorld(steam);
        MainForm a = NewWindow(), b = NewWindow();

        OpenRoom(a, 0);
        Check(a.netIsWhite && a.IAmPlayerForTest, "the host sits down as white");
        Check(StatusText(a).Contains("Ожидание игрока"), "and waits for an opponent: " + StatusText(a).Replace("\n", " | "));

        // first the host says no
        if (steam) Call(b, "NetStartSteamJoin", FirstChessLobby(), true); else Call(b, "NetStartJoin", "127.0.0.1", Port, true);
        Check(WaitFor(delegate { return a.RequestCountForTest > 0; }, 5000), "the host is asked about the newcomer");
        string text = a.RequestTextForTest(0);
        Check(text != null && text.Contains("хочет играть") && text.Contains(b.PlayerNameForTest), "the request names the player: " + text);
        Check(b.NetStateForTest == "Connecting", "the newcomer waits for the decision: " + State(b));
        Check(a.TestAnswerRequest("join:", false), "the host declines");
        Check(WaitFor(delegate { return State(b) == "Idle"; }, 4000), "a declined player is sent away");
        Check(b.NetInfoForTest.Contains("не принял"), "and told why: " + b.NetInfoForTest);
        Check(a.InRoomForTest && a.RoomForTest.MemberCount == 1 && !a.netStarted, "the room stays open, nothing started");

        // then yes
        if (steam) Call(b, "NetStartSteamJoin", FirstChessLobby(), true); else Call(b, "NetStartJoin", "127.0.0.1", Port, true);
        Check(WaitFor(delegate { return a.TestAnswerRequest("join:", true); }, 5000),
            "asked again and accepted (b " + State(b) + " '" + b.NetInfoForTest + "', pending at host " + a.RoomForTest.PendingIds.Length + ")");
        Check(WaitFor(delegate { return a.netStarted && b.netStarted; }, 6000), "an approved player gets a seat and the game starts");
        Check(!b.netIsWhite && b.IAmPlayerForTest, "the newcomer got the free seat (black)");

        // a spectator is asked as well, and gets no seat
        MainForm c = NewWindow();
        if (steam) Call(c, "NetStartSteamJoin", FirstChessLobby(), false); else Call(c, "NetStartJoin", "127.0.0.1", Port, false);
        Check(WaitFor(delegate { return a.RequestCountForTest > 0; }, 5000), "the host is asked about the spectator");
        text = a.RequestTextForTest(0);
        Check(text != null && text.Contains("наблюдать"), "the request says it is a spectator: " + text);
        Check(a.TestAnswerRequest("join:", true), "accepted");
        Check(WaitFor(delegate { return c.InRoomForTest && c.netStarted; }, 5000), "the spectator is in and sees the running game");
        Check(!c.IAmPlayerForTest && BoardOf(c).HumanMask == 0, "and cannot move");
        Check(a.RoomForTest.MemberCount == 3, "three people in the room");

        // the host leaves a room with people in it: all are told
        Call(a, "NetDisconnectClicked");
        Check(WaitFor(delegate { return State(b) == "Idle" && State(c) == "Idle"; }, 4000), "everyone is told when the host closes the room");
        a.Close();
        b.Close();
        c.Close();
    }

    static void TestRoom(bool steam)
    {
        Console.WriteLine("players, spectators, permissions, host hand-over (" + (steam ? "Steam lobby" : "LAN") + ")");
        ResetWorld(steam);
        MainForm a = NewWindow(), b = NewWindow(), c = NewWindow();
        FakeCapture capA = new FakeCapture(), capB = new FakeCapture(), capC = new FakeCapture();
        FakePlayback playA = new FakePlayback(), playB = new FakePlayback(), playC = new FakePlayback();
        a.VoiceFactory = delegate { return new VoiceChannel(delegate { return capA; }, delegate { return playA; }); };
        b.VoiceFactory = delegate { return new VoiceChannel(delegate { return capB; }, delegate { return playB; }); };
        c.VoiceFactory = delegate { return new VoiceChannel(delegate { return capC; }, delegate { return playC; }); };

        Connect(a, b, true);
        JoinRoom(a, c, false);
        Check(WaitFor(delegate { return c.netStarted; }, 3000), "the spectator sees two players");
        Check(!c.IAmPlayerForTest && c.RoomForTest.MySeat == Seat.None && BoardOf(c).HumanMask == 0, "the spectator has no seat");
        Check(a.RoomForTest.PlayerCount == 2 && a.RoomForTest.MemberCount == 3, "2 + 1");
        if (steam)
            Check(WaitFor(delegate { return hub.Lobbies[FirstChessLobby()].Data["watchers"] == "1" && hub.Lobbies[FirstChessLobby()].Data["players"] == "2"; }, 3000), "the lobby listing shows 2 players and 1 watcher");

        // the spectator sees the game
        PlayMove(a, "e2e4");
        Check(WaitFor(delegate { return GameOf(b).PlyCount == 1 && GameOf(c).PlyCount == 1; }, 3000), "e4 reaches the guest and the spectator");
        PlayMove(b, "e7e5");
        Check(WaitFor(delegate { return GameOf(c).PlyCount == 2 && GameOf(a).PlyCount == 2; }, 3000), "e5 too");
        Check(StatusText(c).Contains("наблюдаете"), "the spectator's status says so: " + StatusText(c).Replace("\n", " | "));
        Check(c.TestPeopleText().Contains("Alice:White:host") && c.TestPeopleText().Contains("Carol:None"), "the people list: " + c.TestPeopleText());

        // a spectator cannot move, even by forging lines
        c.RoomForTest.SendToHost("MOVE 2 g1f3");
        c.RoomForTest.Transport.Broadcast("PLAYED 2 g1f3");
        Pump(400);
        Check(GameOf(a).PlyCount == 2 && GameOf(b).PlyCount == 2 && GameOf(c).PlyCount == 2, "forged moves of a spectator change nothing");

        // ---- text chat for everybody
        c.TestSendChat("Привет из зала!");
        Check(WaitFor(delegate { return a.ChatForTest.Count >= 2 && b.ChatForTest.Count >= 2; }, 3000), "a spectator's message reaches both players");
        a.TestSendChat("Добро пожаловать");
        Check(WaitFor(delegate { return c.ChatForTest.Count >= 3; }, 3000), "and the players' messages reach the spectator");

        // ---- the host switches chat off for all spectators
        a.RoomForTest.SetPolicy(false, true);
        Check(WaitFor(delegate { return !c.RoomForTest.SpectatorChatAllowed; }, 3000), "the policy reaches the spectator");
        Check(!c.ChatInputEnabledForTest, "the spectator's chat box is disabled");
        int chatBefore = a.ChatForTest.Count;
        c.TestSendChat("можно?");
        c.RoomForTest.Transport.Broadcast("CHAT sneaky");
        Pump(400);
        Check(a.ChatForTest.Count == chatBefore, "nothing written by the muted spectator appears at the host (" + a.ChatForTest.Count + " vs " + chatBefore + ")");
        b.TestSendChat("игроки пишут всегда");
        Check(WaitFor(delegate { return a.ChatForTest.Count == chatBefore + 1; }, 3000), "players can still write");
        a.RoomForTest.SetPolicy(true, true);
        Check(WaitFor(delegate { return c.ChatInputEnabledForTest; }, 3000), "and the spectator may write again");

        // ---- voice: everybody can talk, the host can silence one person
        capC.Feed(FakeCapture.Silence());
        c.TestToggleMic();
        Check(capC.Started, "the spectator's microphone is on");
        capA.Feed(FakeCapture.Silence());
        a.TestToggleMic();
        Speak(capC, c);
        Speak(capA, a);
        Check(WaitFor(delegate { return b.VoiceForTest != null && b.VoiceForTest.IsSpeaking(Id(a)) && b.VoiceForTest.IsSpeaking(Id(c)); }, 3000),
            "the player hears the host and the spectator at once");
        Check(WaitFor(delegate { return a.VoiceForTest.IsSpeaking(Id(c)); }, 2000),
            "the host hears the spectator (c mic " + (c.VoiceForTest != null && c.VoiceForTest.MicOpen) + ", chat allowed " + c.RoomForTest.CanSpeak(Id(c))
            + ", b voice " + (b.VoiceForTest != null) + ", a voice " + (a.VoiceForTest != null) + ", c ready " + c.RoomForTest.Ready + ", a sees c " + (a.RoomForTest.Find(Id(c)) != null) + ")");

        a.RoomForTest.SetMute(Id(c), false, true);
        Check(WaitFor(delegate { return !c.RoomForTest.CanSpeak(Id(c)); }, 3000), "the host silences the spectator's voice");
        Check(WaitFor(delegate { return !capC.Started; }, 2000), "which closes the spectator's microphone");
        Pump(700);
        Speak(capC, c);
        Pump(500);
        Check(!b.VoiceForTest.IsSpeaking(Id(c)), "nobody hears a silenced spectator any more");
        a.TestToggleMic();   // the host's microphone off
        a.RoomForTest.SetMute(Id(c), false, false);
        Pump(300);

        // ---- seats: the second player steps down, the game waits, the spectator is seated
        BoardOf(b);
        Call(b, "StandUp");
        Check(WaitFor(delegate { return !b.IAmPlayerForTest && !a.netStarted; }, 3000), "the second player stood up and the table has a free seat");
        Check(StatusText(a).Contains("приостановлена") && StatusText(a).Contains("чёрные"), "the host's status names the missing side: " + StatusText(a).Replace("\n", " | "));
        Check(BoardOf(a).HumanMask == 0, "nobody can move while a seat is empty");
        Check(b.TestSeatButtonText.Contains("Сесть"), "the former player gets the 'sit down' button: " + b.TestSeatButtonText);

        Call(c, "SitDown", Seat.None);
        Check(WaitFor(delegate { return a.TestAnswerRequest("seat:", true); }, 4000), "the host is asked about the spectator's wish to sit");
        Check(WaitFor(delegate { return c.IAmPlayerForTest && c.netStarted && a.netStarted; }, 3000), "the spectator sits down: the game goes on");
        Check(!c.netIsWhite && BoardOf(c).Flipped, "with the black pieces, board turned to them");
        Check(GameOf(c).PlyCount == 2, "and the position is where it was");
        PlayMove(a, "g1f3");
        Check(WaitFor(delegate { return GameOf(c).PlyCount == 3 && GameOf(b).PlyCount == 3; }, 3000), "the host's move reaches the new player and the former one");
        PlayMove(c, "b8c6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 4 && GameOf(b).PlyCount == 4; }, 3000), "the new player's move reaches the host and the spectator");

        // the former player asks to sit again while the table is full: refused politely
        Call(b, "SitDown", Seat.None);
        Pump(300);
        Check(!b.IAmPlayerForTest, "no free seat: he stays a spectator");

        // ---- handing the host over
        if (!steam)
        {
            // first the future host says no: nothing moves, and the same person can be asked again
            Call(a, "TransferHostTo", Id(b));
            Check(WaitFor(delegate { return b.TestAnswerRequest("host", false); }, 5000), "on a LAN the future host is asked, and declines");
            Check(WaitFor(delegate { return a.NetInfoForTest.Contains("не принял"); }, 5000), "the host is told: " + a.NetInfoForTest);
            Check(a.RoomForTest.IsHost && !b.RoomForTest.IsHost && a.RoomForTest.Transport.IsHost, "nothing moved");
            Check(!a.RoomForTest.Transferring, "and a new attempt is possible");
        }
        Call(a, "TransferHostTo", Id(b));
        if (!steam)
            Check(WaitFor(delegate { return b.TestAnswerRequest("host", true); }, 5000), "on a LAN the future host is asked");
        Check(WaitFor(delegate { return b.RoomForTest.IsHost && !a.RoomForTest.IsHost && c.RoomForTest.HostId == Id(b); }, 15000),
            "the host role moved (" + a.RoomForTest.HostId + "/" + b.RoomForTest.HostId + "/" + c.RoomForTest.HostId + ", " + a.NetInfoForTest + ")");
        Check(WaitFor(delegate { return a.InRoomForTest && b.InRoomForTest && c.InRoomForTest; }, 10000), "everybody is still in the room");
        Check(WaitFor(delegate { return a.RoomForTest.MemberCount == 3 && b.RoomForTest.MemberCount == 3 && c.RoomForTest.MemberCount == 3; }, 12000), "nobody was lost on the way");
        Check(a.RoomForTest.MySeat == Seat.White && c.RoomForTest.MySeat == Seat.Black, "the players keep their seats");
        PlayMove(a, "f1b5");
        Check(WaitFor(delegate { return GameOf(c).PlyCount == 5 && GameOf(b).PlyCount == 5; }, 5000), "the old host's move goes through the new host");
        PlayMove(c, "g8f6");
        Check(WaitFor(delegate { return GameOf(a).PlyCount == 6 && GameOf(b).PlyCount == 6; }, 5000), "and the other player's");

        // the new host decides now
        b.RoomForTest.SetPolicy(false, false);
        Check(WaitFor(delegate { return !a.RoomForTest.SpectatorChatAllowed && !c.RoomForTest.SpectatorVoiceAllowed; }, 3000), "the new host's policy is replicated");
        Check(!a.RoomForTest.SetSeat(Id(c), Seat.None), "the old host cannot change seats any more");

        // a new spectator joins and is asked by the new host
        MainForm d = NewWindow();
        if (steam) Call(d, "NetStartSteamJoin", FirstChessLobby(), false);
        else Call(d, "NetStartJoin", "127.0.0.1", b.RoomForTest.Transport is LanRoomTransport ? NewHostPort(b) : Port, false);
        Check(WaitFor(delegate { return b.TestAnswerRequest("join:", true); }, 6000), "the new host is asked about the newcomer");
        Check(WaitFor(delegate { return d.InRoomForTest && d.netStarted && GameOf(d).PlyCount == 6; }, 6000), "the newcomer gets the whole game from the new host");

        // kicking
        b.RoomForTest.Kick(Id(d));
        Check(WaitFor(delegate { return State(d) == "Idle"; }, 4000), "a removed spectator is out");
        Check(d.NetInfoForTest.Contains("исключил"), "and told why: " + d.NetInfoForTest);
        Check(WaitFor(delegate { return a.RoomForTest.MemberCount == 3; }, 3000), "others see him go");

        // the old host leaves after the hand-over: the new host notices, the white seat is free and the game waits
        Call(a, "NetDisconnectClicked");
        Check(WaitFor(delegate { return b.RoomForTest.MemberCount == 2 && c.RoomForTest.MemberCount == 2; }, 5000),
            "the new host notices the old host leaving (" + b.RoomForTest.MemberCount + "/" + c.RoomForTest.MemberCount + ")");
        Check(WaitFor(delegate { return !b.netStarted && !c.netStarted; }, 3000), "the table has a free seat now");
        Check(StatusText(b).Contains("приостановлена"), "and the new host sees the game paused: " + StatusText(b).Replace("\n", " | "));

        a.Close();
        b.Close();
        c.Close();
        d.Close();
    }

    /// <summary>The port of the hub that a window runs after the host role moved to it (LAN).</summary>
    static int NewHostPort(MainForm f)
    {
        object t = f.RoomForTest.Transport;
        FieldInfo listener = t.GetType().GetField("listener", All);
        if (listener == null) return Port;
        System.Net.Sockets.TcpListener l = listener.GetValue(t) as System.Net.Sockets.TcpListener;
        return l == null ? Port : ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>A short burst of speech from a fake microphone, followed by silence so that the voice gate closes and everything is sent.</summary>
    static void Speak(FakeCapture cap, MainForm f)
    {
        for (int i = 0; i < 2; i++) cap.Feed(FakeCapture.Silence());   // the gate learns the room noise first
        for (int i = 0; i < 3; i++) cap.Feed(FakeCapture.Loud());
        for (int i = 0; i < 8; i++) cap.Feed(FakeCapture.Silence());
        f.TestPumpVoice();
    }

    static void TestChatAndVoice(bool steam)
    {
        Console.WriteLine("text and voice chat (" + (steam ? "Steam lobby" : "LAN") + ")");
        ResetWorld(steam);
        MainForm a = NewWindow(), b = NewWindow();
        FakeCapture capA = new FakeCapture(), capB = new FakeCapture();
        FakePlayback playA = new FakePlayback(), playB = new FakePlayback();
        a.VoiceFactory = delegate { return new VoiceChannel(delegate { return capA; }, delegate { return playA; }); };
        b.VoiceFactory = delegate { return new VoiceChannel(delegate { return capB; }, delegate { return playB; }); };
        Connect(a, b, true);

        ChatControl ca = a.ChatForTest, cb = b.ChatForTest;
        Check(ca.Count >= 1 && cb.Count >= 1, "both chats start with a greeting from the program");
        int baseA = ca.Count, baseB = cb.Count;

        // ---- text
        a.TestSendChat("Привет! Играем?");
        Check(WaitFor(delegate { return cb.Count == baseB + 1; }, 3000), "a message reaches the opponent");
        Check(ca.Count == baseA + 1, "and shows up in our own chat");
        Check(b.UnreadChatForTest == 1, "the opponent has one unread message");
        b.TestSelectChatTab(true);
        Check(b.UnreadChatForTest == 0, "opening the chat tab clears the unread counter");
        b.TestSendChat("   ");
        Pump(100);
        Check(cb.Count == baseB + 1, "an empty message is not sent (" + cb.Count + ")");

        RoomSession ra = a.RoomForTest;
        ra.Broadcast("CHAT    many\t   blanks   and \u0007 a control char");
        Check(WaitFor(delegate { return cb.Count == baseB + 2; }, 3000), "messy text still arrives (cleaned up)");
        ra.Broadcast("CHAT ");
        ra.Broadcast("CHAT    ");
        Pump(200);
        Check(cb.Count == baseB + 2, "empty CHAT lines are ignored");

        int before = cb.Count;
        for (int i = 0; i < 14; i++) ra.Broadcast("CHAT flood " + i);
        Pump(800);
        Check(cb.Count - before <= 8 && cb.Count - before >= 1, "a flood is cut off after a few messages: " + (cb.Count - before));

        // ---- voice
        capA.Feed(FakeCapture.Silence());
        a.TestToggleMic();
        Check(capA.Started && a.VoiceForTest != null && a.VoiceForTest.MicOpen, "the microphone is switched on");
        capA.Feed(FakeCapture.Silence());
        a.TestPumpVoice();
        Pump(150);
        Check(playB.Count == 0, "silence is never sent");

        for (int i = 0; i < 3; i++) capA.Feed(FakeCapture.Loud());
        for (int i = 0; i < 8; i++) capA.Feed(FakeCapture.Silence());
        a.TestPumpVoice();
        Check(WaitFor(delegate { return b.VoiceForTest != null && b.VoiceForTest.IsSpeaking(Id(a)); }, 3000), "the opponent's indicator shows that somebody is talking");
        Check(WaitFor(delegate { return playB.Count >= 2; }, 3000), "speech reaches the opponent's speaker: " + playB.Count);

        b.VoiceForTest.SpeakerMuted = true;
        Pump(200);
        int heard = playB.Count;
        capA.Feed(FakeCapture.Loud());
        a.TestPumpVoice();
        Pump(300);
        Check(playB.Count == heard, "a muted speaker plays nothing");

        ra.Broadcast("VOICE !!!not base64!!!");
        ra.Broadcast("VOICE AAAA");
        Pump(200);
        Check(State(b) == "InRoom" && playB.Count == heard, "garbage voice lines are ignored");

        // ---- the end of the room takes the chat and the voice with it
        a.TestToggleMic();
        Check(!capA.Started, "the microphone can be switched off again");
        a.TestToggleMic();
        Call(a, "NetDisconnectClicked");
        Check(WaitFor(delegate { return State(b) == "Idle"; }, 3000), "disconnected");
        Check(a.VoiceForTest == null && WaitFor(delegate { return capA.Disposed && playB.Disposed; }, 3000), "voice devices are released");
        Check(ca.Count == 0 && cb.Count == 0, "the chat history is cleared");

        a.Close();
        b.Close();
    }

    static void TestReview()
    {
        Console.WriteLine("review of earlier positions");
        InstallContext();
        MainForm f = new MainForm();
        Offscreen(f);
        f.Show();
        BoardOf(f).AnimationsEnabled = false;
        f.TestSetMode(1);   // two players: no computer moves in the way
        PlayMove(f, "e2e4"); PlayMove(f, "e7e5"); PlayMove(f, "g1f3");
        Check(f.ViewPlyForTest == -1, "following the live game at first");

        f.TestSetViewPly(1);
        BoardControl board = BoardOf(f);
        Check(f.ViewPlyForTest == 1 && board.IsViewing, "viewing the position after the first move");
        Check(StatusText(f).StartsWith("Просмотр"), "status says we are only looking: " + StatusText(f));

        // a move arriving while we look must not pull the view away or change the board we see
        foreach (Move m in GameOf(f).Legal) if (m.ToString() == "b8c6") { GameOf(f).Play(m); break; }
        Check(f.ViewPlyForTest == 1 && GameOf(f).PlyCount == 4, "an incoming move leaves the view where it was");

        f.TestSetViewPly(99);                       // past the end = back to live
        Check(f.ViewPlyForTest == -1 && !board.IsViewing, "going past the last move returns to the live game");

        f.TestSetViewPly(2);
        Call(f, "DoUndo");
        Check(f.ViewPlyForTest == -1 && !board.IsViewing, "undo ends the review");

        f.TestSetViewPly(0);
        Check(f.ViewPlyForTest == 0, "ply 0 is the starting position");
        Call(f, "StartNewGame");
        Check(f.ViewPlyForTest == -1, "a new game ends the review");
        f.Close();
    }

    /// <summary>
    /// If the UI thread stops making progress for a minute (a hang in the program or a modal dialog nobody answers),
    /// print where it is stuck and stop, instead of hanging the whole test run.
    /// </summary>
    static void StartWatchdog()
    {
        System.Threading.Thread main = System.Threading.Thread.CurrentThread;
        System.Threading.Thread dog = new System.Threading.Thread(delegate()
        {
            int last = -1, still = 0;
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
                int now = progress;
                if (now != last) { last = now; still = 0; continue; }
                if (++still < 45) continue;
#pragma warning disable 618
                main.Suspend();
                Console.WriteLine("WATCHDOG: the UI thread made no progress for " + still + " s. It is here:");
                Console.WriteLine(new StackTrace(main, true).ToString());
#pragma warning restore 618
                Environment.Exit(2);
            }
        });
        dog.IsBackground = true;
        dog.Start();
    }

    static int progress;

    [STAThread]
    static int Main(string[] args)
    {
        Sounds.Muted = true;          // no beeps while testing
        Settings.Persist = false;     // and never touch the real settings file
        // an exception must stop the run with its text, never raise a "program has stopped working" dialog nobody answers
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine("UNHANDLED EXCEPTION: " + e.ExceptionObject);
            Environment.Exit(3);
        };
        Application.EnableVisualStyles();
        // Application.Run installs this automatically in the real program
        System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        StartWatchdog();
        string only = args.Length > 0 ? args[0] : "";
        Func<string, bool> run = delegate(string name) { return only.Length == 0 || only == name; };

        if (run("discovery")) TestDiscovery();
        if (run("steam")) { TestSteamNative(); TestSteamTransport(); }
        if (run("games")) { useSteam = false; TestGames(); useSteam = true; TestGames(); }
        if (run("review")) TestReview();
        if (run("premove")) TestPremove();
        if (run("approval")) { TestApproval(false); TestApproval(true); }
        if (run("chat")) { TestChatAndVoice(false); TestChatAndVoice(true); }
        if (run("room")) { TestRoom(true); TestRoom(false); }
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL NETWORK TESTS PASSED" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}
