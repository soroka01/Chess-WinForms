// LAN room transport tests: one hub and several clients over TCP loopback (no external network).
// Build & run:  tests\run-lan-room-tests.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using ChessGame;

static class LanRoomTests
{
    static int failures;
    static int keyCounter;
    static Form anchor;
    static WindowsFormsSynchronizationContext context;
    static int mainThread;
    static int offThread;   // events that were not raised on the creating thread
    static readonly List<Node> all = new List<Node>();

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
            InstallContext();
            if (condition()) return true;
            System.Threading.Thread.Sleep(5);
        }
        Application.DoEvents();
        InstallContext();
        return condition();
    }

    static void Pump(int ms)
    {
        WaitFor(delegate { return false; }, ms);
    }

    /// <summary>
    /// Application.Run does this in the real program. Application.DoEvents puts a plain context back
    /// afterwards, so the test installs its one context again whenever it has been replaced.
    /// </summary>
    static void InstallContext()
    {
        if (anchor == null) { anchor = new Form(); anchor.Show(); context = new WindowsFormsSynchronizationContext(); }
        if (System.Threading.SynchronizationContext.Current != context)
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
    }

    /// <summary>A transport with everything it raises written down.</summary>
    sealed class Node
    {
        public readonly LanRoomTransport T;
        public int Connected;
        public readonly List<string> Peers = new List<string>();     // "id|name"
        public readonly List<string> Gone = new List<string>();
        public readonly List<string> Log = new List<string>();       // "+id" / "-id" in order
        public readonly List<string> Lines = new List<string>();     // "from:line"
        public readonly List<string> Closed = new List<string>();

        public Node(LanRoomTransport t)
        {
            T = t;
            t.Connected += delegate { OnThread(); Connected++; };
            t.PeerConnected += delegate(string id, string name) { OnThread(); Peers.Add(id + "|" + name); Log.Add("+" + id); };
            t.PeerDisconnected += delegate(string id) { OnThread(); Gone.Add(id); Log.Add("-" + id); };
            t.LineReceived += delegate(string from, string line) { OnThread(); Lines.Add(from + ":" + line); };
            t.Closed += delegate(string reason) { OnThread(); Closed.Add(reason); };
            all.Add(this);
        }

        static void OnThread()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread) System.Threading.Interlocked.Increment(ref offThread);
        }

        public int Count(string s)
        {
            int n = 0;
            foreach (string l in Lines) if (l == s) n++;
            return n;
        }

        public bool HasPeer(string id)
        {
            foreach (string p in Peers) if (p.StartsWith(id + "|", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    static Node Hub(int port, string name)
    {
        InstallContext();
        Node n = new Node(LanRoomTransport.Host(port, name, "h"));
        n.T.Start();
        return n;
    }

    static string NewKey() { return "key" + (++keyCounter) + "x" + Guid.NewGuid().ToString("N").Substring(0, 8); }

    static Node Client(int port, string name, string key)
    {
        InstallContext();
        Node n = new Node(LanRoomTransport.Join("127.0.0.1", port, name, key));
        n.T.Start();
        return n;
    }

    /// <summary>Joins one client and waits until it is welcomed (so that ids follow the call order).</summary>
    static Node Join(int port, string name)
    {
        Node n = Client(port, name, NewKey());
        Check(WaitFor(delegate { return n.Connected == 1; }, 5000), name + " connected");
        return n;
    }

    static void CloseAll()
    {
        foreach (Node n in all) n.T.Close();
        all.Clear();
        Pump(30);
    }

    /// <summary>A hand-made TCP client for protocol abuse.</summary>
    sealed class Raw
    {
        public readonly TcpClient C;
        readonly StreamReader reader;

        public Raw(int port)
        {
            C = new TcpClient();
            C.Connect("127.0.0.1", port);
            C.ReceiveTimeout = 4000;
            reader = new StreamReader(C.GetStream(), Encoding.UTF8);
        }

        public void Send(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            C.GetStream().Write(b, 0, b.Length);
        }

        public string ReadLine()
        {
            try { return reader.ReadLine(); }
            catch (IOException) { return "<error>"; }
        }

        /// <summary>True when the other side closes the connection (end of stream or reset) rather than staying silent.</summary>
        public bool ClosedByPeer()
        {
            try
            {
                while (reader.Read() >= 0) { }
                return true;
            }
            catch (IOException ex)
            {
                SocketException se = ex.InnerException as SocketException;
                return se == null || se.SocketErrorCode != SocketError.TimedOut;
            }
        }

        public string Hello(string name, string key)
        {
            Send("CHESS 2 " + name + "\t" + key + "\n");
            return ReadLine();
        }

        public void Close() { try { C.Close(); } catch (Exception) { } }
    }

    // ------------------------------------------------------------ tests

    static void TestBasics()
    {
        Console.WriteLine("ids, names, broadcast, private lines");
        const int port = 5801;
        Node hub = Hub(port, "Host");
        Check(WaitFor(delegate { return hub.Connected == 1; }, 2000), "hub raises Connected after Start");
        Check(hub.T.IsHost && hub.T.MyId == "h" && hub.T.HostId == "h" && hub.T.MyName == "Host", "hub identity");

        Node c1 = Join(port, "Alice");
        Node c2 = Join(port, "A<b>c d");
        Check(WaitFor(delegate { return hub.Peers.Count == 2; }, 2000), "hub saw both clients");
        Check(c1.T.MyId == "p1" && c2.T.MyId == "p2", "ids in connection order: " + c1.T.MyId + "," + c2.T.MyId);
        Check(c1.T.HostId == "h" && !c1.T.IsHost && c1.T.MyName == "Alice", "client identity");
        Check(hub.Peers[0] == "p1|Alice" && hub.Peers[1] == "p2|Abc d", "names (sanitized): " + string.Join(",", hub.Peers.ToArray()));
        Check(hub.Connected == 1 && c1.Connected == 1, "Connected exactly once");
        Check(c1.T.AddressOf("p2") == null, "a client has no addresses");
        Check(hub.T.AddressOf("p1") == "127.0.0.1" && hub.T.AddressOf("p2") == "127.0.0.1", "AddressOf is the remote IP without port");
        Check(hub.T.AddressOf("p9") == null && hub.T.AddressOf(null) == null, "AddressOf unknown id is null");

        // broadcast from a client: hub app + the other client, once, not the sender
        c1.T.Broadcast("hello all");
        Check(WaitFor(delegate { return hub.Count("p1:hello all") == 1 && c2.Count("p1:hello all") == 1; }, 2000), "broadcast delivered to hub and other client");
        Pump(250);
        Check(hub.Count("p1:hello all") == 1 && c2.Count("p1:hello all") == 1 && c1.Lines.Count == 0, "exactly once, never back to the sender");

        // broadcast from the hub
        hub.T.Broadcast("from hub");
        Check(WaitFor(delegate { return c1.Count("h:from hub") == 1 && c2.Count("h:from hub") == 1; }, 2000), "hub broadcast reaches every client");
        Pump(150);
        Check(c1.Count("h:from hub") == 1 && c2.Count("h:from hub") == 1 && hub.Count("h:from hub") == 0, "once each, not the hub itself");

        // private lines
        hub.Lines.Clear(); c1.Lines.Clear(); c2.Lines.Clear();
        c1.T.SendTo("p2", "secret");
        Check(WaitFor(delegate { return c2.Count("p1:secret") == 1; }, 2000), "private line reaches its target through the hub");
        c1.T.SendTo("h", "to the hub");
        Check(WaitFor(delegate { return hub.Count("p1:to the hub") == 1; }, 2000), "private line to the hub's app");
        hub.T.SendTo("p2", "from hub only");
        Check(WaitFor(delegate { return c2.Count("h:from hub only") == 1; }, 2000), "hub private line");
        c1.T.SendTo("p1", "to myself");
        c1.T.SendTo("nobody", "lost");
        c1.T.SendTo("p 2", "bad id");
        hub.T.SendTo("h", "hub to itself");
        hub.T.SendTo("p7", "unknown");
        c2.T.Broadcast("a\nb");   // line breaks cannot split a line into two
        Check(WaitFor(delegate { return hub.Count("p2:a b") == 1; }, 2000), "line breaks are flattened");
        Pump(250);
        Check(hub.Lines.Count == 2 && c1.Lines.Count == 1 && c2.Lines.Count == 2, "private lines went nowhere else: hub " + hub.Lines.Count + ", c1 " + c1.Lines.Count + ", c2 " + c2.Lines.Count);

        // the longest allowed line passes, longer ones are cut by the sender
        string big = new string('y', 3000);
        c1.T.Broadcast(big);
        Check(WaitFor(delegate { return hub.Lines.Count == 3; }, 2000), "very long line still arrives");
        Check(hub.Lines[2].Length <= 2 + 2048, "and was cut to the limit: " + hub.Lines[2].Length);
        Pump(100);
        Check(hub.Gone.Count == 0, "the connection survived");
        CloseAll();
    }

    static void TestRelayFilter()
    {
        Console.WriteLine("RelayFilter");
        const int port = 5802;
        Node hub = Hub(port, "Host");
        Node c1 = Join(port, "A");
        Node c2 = Join(port, "B");
        hub.T.RelayFilter = delegate(string from, string line) { return from != "p1"; };
        c1.T.Broadcast("muted words");
        c2.T.Broadcast("free words");
        Check(WaitFor(delegate { return c1.Count("p2:free words") == 1 && hub.Count("p2:free words") == 1; }, 2000), "an allowed sender is relayed");
        Pump(250);
        Check(hub.Count("p1:muted words") == 1, "the hub's own app still sees the muted sender");
        Check(c2.Count("p1:muted words") == 0, "but the other client does not");
        hub.T.RelayFilter = null;
        c1.T.Broadcast("again");
        Check(WaitFor(delegate { return c2.Count("p1:again") == 1; }, 2000), "without a filter everything is relayed");
        CloseAll();
    }

    static void TestDisconnects()
    {
        Console.WriteLine("Disconnect, lost client, lost hub");
        const int port = 5803;
        Node hub = Hub(port, "Host");
        Node c1 = Join(port, "A");
        Node c2 = Join(port, "B");
        Node c3 = Join(port, "C");
        Check(WaitFor(delegate { return hub.Peers.Count == 3; }, 2000), "three clients");

        hub.T.Disconnect("p2");
        Check(WaitFor(delegate { return c2.Closed.Count == 1; }, 2000), "the dropped client is told the room is gone");
        Check(c2.Closed.Count == 1 && c2.Closed[0].Length > 0, "with a reason: " + (c2.Closed.Count > 0 ? c2.Closed[0] : ""));
        Pump(250);
        Check(hub.Gone.Count == 1 && hub.Gone[0] == "p2", "PeerDisconnected exactly once after Disconnect: " + string.Join(",", hub.Gone.ToArray()));
        Check(hub.T.AddressOf("p2") == null, "no address for a dropped client");
        hub.T.Disconnect("p2");
        hub.T.Disconnect("zzz");
        hub.T.Disconnect(null);
        Pump(100);
        Check(hub.Gone.Count == 1, "Disconnect of an unknown or finished id does nothing");

        c3.T.Broadcast("still here");
        Check(WaitFor(delegate { return c1.Count("p3:still here") == 1; }, 2000), "others keep working");
        Check(c2.Lines.Count == 0, "the dropped client gets nothing any more");

        c1.T.Close();
        Check(WaitFor(delegate { return hub.Gone.Count == 2; }, 2000), "a client that closes is noticed");
        Pump(250);
        Check(hub.Gone.Count == 2 && hub.Gone[1] == "p1", "exactly once: " + string.Join(",", hub.Gone.ToArray()));
        Check(c1.Closed.Count == 0, "the closer gets no Closed event");

        hub.T.Close();
        Check(WaitFor(delegate { return c3.Closed.Count == 1; }, 3000), "clients notice the hub closing");
        Pump(250);
        Check(c3.Closed.Count == 1 && c3.Closed[0] == "Хост закрыл комнату", "reason: " + (c3.Closed.Count > 0 ? c3.Closed[0] : ""));
        Check(hub.Closed.Count == 0, "the closer (hub) gets no Closed event");
        Check(hub.Gone.Count == 2, "a closing hub raises no PeerDisconnected");
        CloseAll();
    }

    static void TestFull()
    {
        Console.WriteLine("the room is full after ten clients");
        const int port = 5804;
        Node hub = Hub(port, "Host");
        List<Node> cs = new List<Node>();
        for (int i = 0; i < 10; i++) cs.Add(Join(port, "C" + i));
        Check(WaitFor(delegate { return hub.Peers.Count == 10; }, 3000), "ten clients fit");

        Raw raw = new Raw(port);
        Check(raw.Hello("Eleven", "eleven1") == "FULL", "the 11th gets FULL");
        Check(raw.ClosedByPeer(), "and is disconnected");
        raw.Close();

        Node late = Client(port, "Late", NewKey());
        Check(WaitFor(delegate { return late.Closed.Count == 1; }, 3000), "the 11th transport is told");
        Check(late.Closed.Count == 1 && late.Closed[0].StartsWith("Не удалось подключиться") && late.Closed[0].Contains("заполнена"), "reason: " + (late.Closed.Count > 0 ? late.Closed[0] : ""));
        Check(late.Connected == 0 && hub.Peers.Count == 10, "nothing else happened");

        cs[2].T.Close();
        Check(WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "a client leaves");
        Node again = Join(port, "Again");
        Check(again.T.MyId == "p11", "a free slot is usable, ids are never reused: " + again.T.MyId);
        CloseAll();
    }

    static void TestKeys()
    {
        Console.WriteLine("identity across reconnects");
        const int port = 5805;
        Node hub = Hub(port, "Host");
        Node a = Client(port, "A", "keyA");
        Check(WaitFor(delegate { return a.Connected == 1; }, 3000), "A connected");
        Check(a.T.MyId == "p1", "first id");
        a.T.Close();
        Check(WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "A left");
        Node b = Join(port, "B");
        Check(b.T.MyId == "p2", "a newcomer gets a fresh id, p1 is not reused: " + b.T.MyId);
        Node a2 = Client(port, "A", "keyA");
        Check(WaitFor(delegate { return a2.Connected == 1; }, 3000), "A is back");
        Check(a2.T.MyId == "p1", "the known key gets the old id back: " + a2.T.MyId);

        // the same key while the old connection still looks alive: the old one is replaced, in a clean order
        Check(WaitFor(delegate { return hub.Peers.Count == 3; }, 2000), "hub saw A's return");
        hub.Log.Clear();
        Node a3 = Client(port, "A again", "keyA");
        Check(WaitFor(delegate { return a3.Connected == 1; }, 3000), "second connection with the same key");
        Check(a3.T.MyId == "p1", "same id");
        Check(WaitFor(delegate { return a2.Closed.Count == 1; }, 3000), "the old connection is closed");
        Pump(250);
        Check(string.Join(",", hub.Log.ToArray()) == "-p1,+p1", "PeerDisconnected then PeerConnected: " + string.Join(",", hub.Log.ToArray()));
        a3.T.Broadcast("works");
        Check(WaitFor(delegate { return hub.Count("p1:works") == 1 && b.Count("p1:works") == 1; }, 2000), "the new connection works");

        // exported identities
        IDictionary<string, string> ids = hub.T.ExportIdentities();
        Check(ids.ContainsKey("p1") && ids["p1"] == "keyA" && ids.ContainsKey("p2") && ids.ContainsKey("h") && ids.Count == 3, "ExportIdentities lists clients and the hub itself: " + ids.Count);
        CloseAll();
    }

    static void TestAbuse()
    {
        Console.WriteLine("garbage, oversize lines, floods, invalid commands");
        const int port = 5806;
        Node hub = Hub(port, "Host");

        Raw r = new Raw(port);
        r.Send("GET / HTTP/1.1\n");
        Check(r.ReadLine() == "BAD", "garbage greeting is answered with BAD");
        Check(r.ClosedByPeer(), "and closed");
        r.Close();

        r = new Raw(port);
        r.Send("CHESS 2 " + new string('n', 5000) + "\n");
        Check(r.ClosedByPeer(), "an over-long greeting closes the connection");
        r.Close();

        r = new Raw(port);
        r.Send("CHESS 1 OldClient\n");
        Check(r.ReadLine() == "BADVERSION", "the old two-player greeting is refused as a wrong version");
        Check(r.ClosedByPeer(), "and closed");
        r.Close();

        // opened and closed without a word (a probe): no member, no error
        r = new Raw(port);
        r.Close();
        Pump(200);

        r = new Raw(port);
        r.Send("hello lower case\n");
        Check(r.ReadLine() == "BAD", "wrong greeting word");
        r.Close();
        Pump(100);
        Check(hub.Peers.Count == 0 && hub.Gone.Count == 0, "none of them became a member");

        // a proper greeting: all kinds of nonsense afterwards is ignored
        r = new Raw(port);
        string welcome = r.Hello("Raw Name", "raw1");
        Check(welcome == "WELCOME p1 h", "welcome line: " + welcome);
        Check(WaitFor(delegate { return hub.Peers.Count == 1; }, 2000) && hub.Peers[0] == "p1|Raw Name", "peer announced with a clean name: " + (hub.Peers.Count > 0 ? hub.Peers[0] : ""));
        r.Send("X foo\nB\nB \nT\nT p1\nT p1 hi\nT zz hi\nT zz\nCHESS 2 again\nBx\nF h forged\n");
        r.Send("B alive\n");
        Check(WaitFor(delegate { return hub.Lines.Count >= 1; }, 2000), "the valid line after the nonsense arrives");
        Pump(250);
        Check(hub.Lines.Count == 1 && hub.Lines[0] == "p1:alive", "only the valid line was delivered: " + string.Join("|", hub.Lines.ToArray()));
        Check(hub.Gone.Count == 0, "and the connection is still there");

        // exactly 2048 chars is fine, 2049 is not
        string edge = new string('e', 2046);
        r.Send("B " + edge + "\n");
        Check(WaitFor(delegate { return hub.Lines.Count == 2; }, 2000) && hub.Lines[1] == "p1:" + edge, "a line of 2048 chars is accepted");
        r.Send("B " + new string('z', 2047) + "\n");
        Check(WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "a line of 2049 chars drops the connection");
        Check(r.ClosedByPeer(), "and it is really closed");
        r.Close();
        Check(hub.Lines.Count == 2, "the over-long line was not delivered");

        // flood
        hub.Lines.Clear(); hub.Gone.Clear();
        r = new Raw(port);
        Check(r.Hello("Flood", "flood1").StartsWith("WELCOME p2 "), "next id");
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append("B x").Append(i).Append('\n');
        try { r.Send(sb.ToString()); } catch (IOException) { }
        Check(WaitFor(delegate { return hub.Gone.Count == 1; }, 3000), "a flood drops the connection");
        Pump(200);
        Check(hub.Gone.Count == 1 && hub.Lines.Count > 0 && hub.Lines.Count <= 250, "only a bounded part got through: " + hub.Lines.Count);
        r.Close();

        // a greeting that never comes: the hub gives up after its timeout
        r = new Raw(port);
        Stopwatch sw = Stopwatch.StartNew();
        r.C.ReceiveTimeout = 9000;
        Check(r.ClosedByPeer() && sw.ElapsedMilliseconds < 8500, "a silent connection is dropped by the hub (" + sw.ElapsedMilliseconds + " ms)");
        r.Close();

        // the hub is fine afterwards
        Node c = Join(port, "Normal");
        c.T.Broadcast("ok");
        Check(WaitFor(delegate { return hub.Count(c.T.MyId + ":ok") == 1; }, 2000), "regular clients still work");
        CloseAll();
    }

    static void TestPortsAndClose()
    {
        Console.WriteLine("busy port, failed connection, Close");
        LanRoomTransport first = LanRoomTransport.Host(5807, "One", "h");
        bool threw = false;
        try { LanRoomTransport.Host(5807, "Two", "h"); }
        catch (SocketException) { threw = true; }
        Check(threw, "a busy port throws from the factory");
        first.Close();
        LanRoomTransport second = null;
        try { second = LanRoomTransport.Host(5807, "Two", "h"); }
        catch (SocketException) { }
        Check(second != null, "the port is free again after Close");
        if (second != null) second.Close();

        // nobody listens
        Node n = Client(5899, "Lonely", NewKey());
        Check(WaitFor(delegate { return n.Closed.Count == 1; }, 9000), "connection refused is reported");
        Check(n.Closed.Count == 1 && n.Closed[0].StartsWith("Не удалось подключиться"), "reason: " + (n.Closed.Count > 0 ? n.Closed[0] : ""));
        Check(n.Connected == 0, "never connected");

        // something that is not a room
        System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5808);
        l.Start();
        Node j = Client(5808, "Bob", NewKey());
        TcpClient c = l.AcceptTcpClient();
        byte[] junk = Encoding.ASCII.GetBytes("GET / HTTP/1.1\n");
        c.GetStream().Write(junk, 0, junk.Length);
        Check(WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "a foreign server is rejected: " + (j.Closed.Count > 0 ? j.Closed[0] : ""));
        c.Close(); l.Stop();

        // a two-player (version 1) host answers with its own greeting
        l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5813);
        l.Start();
        j = Client(5813, "Bob", NewKey());
        c = l.AcceptTcpClient();
        byte[] v1 = Encoding.ASCII.GetBytes("CHESS 1 Host\n");
        c.GetStream().Write(v1, 0, v1.Length);
        Check(WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "an old host is refused");
        Check(j.Closed.Count == 1 && j.Closed[0].Contains("версия") && j.Closed[0].StartsWith("Не удалось подключиться"), "with a readable reason: " + (j.Closed.Count > 0 ? j.Closed[0] : ""));
        Check(j.Connected == 0, "never connected");
        c.Close(); l.Stop();

        // a hub that sends an over-long line to a client
        l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5809);
        l.Start();
        j = Client(5809, "Bob", NewKey());
        c = l.AcceptTcpClient();
        byte[] big = Encoding.ASCII.GetBytes("WELCOME p1 h\nF h " + new string('x', 5000) + "\n");
        c.GetStream().Write(big, 0, big.Length);
        Check(WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "an over-long line from the hub ends the connection");
        c.Close(); l.Stop();

        // Close is idempotent and silent
        Node h = Hub(5810, "Host");
        Node k = Join(5810, "K");
        k.T.Close(); k.T.Dispose(); k.T.Close();
        h.T.Close(); h.T.Close(); h.T.Dispose();
        h.T.Broadcast("after close"); h.T.SendTo("p1", "x"); k.T.Broadcast("after close"); h.T.Disconnect("p1");
        Pump(300);
        Check(h.Closed.Count == 0 && k.Closed.Count == 0 && h.Gone.Count == 0, "closing is silent");
        InstallContext();
        Node never = new Node(LanRoomTransport.Join("127.0.0.1", 5810, "X", NewKey()));
        never.T.Close();
        never.T.Start();   // Start after Close must not connect or raise anything
        Pump(300);
        Check(never.Connected == 0 && never.Closed.Count == 0, "Start after Close does nothing");
        // a hub that was never started still releases its port
        LanRoomTransport idle = LanRoomTransport.Host(5811, "Idle", "h");
        idle.Dispose();
        LanRoomTransport again = LanRoomTransport.Host(5811, "Idle", "h");
        again.Dispose();
        Check(true, "-");
        CloseAll();
    }

    static void TestProbe()
    {
        Console.WriteLine("ProbeHub");
        const int port = 5814;
        Node hub = Hub(port, "Host");
        Node c = Join(port, "C");
        bool? open = null, closedPort = null;
        c.T.ProbeHub("127.0.0.1", port, delegate(bool ok) { open = ok; });
        c.T.ProbeHub("127.0.0.1", 5897, delegate(bool ok) { closedPort = ok; });
        Check(WaitFor(delegate { return open.HasValue && closedPort.HasValue; }, 5000), "both probes finish");
        Check(open == true, "a listening hub is reachable");
        Check(closedPort == false, "a closed port is not");
        Pump(300);
        Check(hub.Peers.Count == 1 && hub.Gone.Count == 0 && hub.Closed.Count == 0 && c.Closed.Count == 0, "the hub took no notice of the probe");
        CloseAll();
    }

    static void TestStress()
    {
        Console.WriteLine("stress: 5 clients and the hub, 200 lines each");
        const int port = 5812;
        const int perSender = 200;
        Node hub = Hub(port, "Host");
        List<Node> cs = new List<Node>();
        for (int i = 0; i < 5; i++) cs.Add(Join(port, "S" + i));
        Check(WaitFor(delegate { return hub.Peers.Count == 5; }, 3000), "five clients");
        for (int i = 0; i < perSender; i++)
        {
            foreach (Node c in cs) c.T.Broadcast("m" + i);
            hub.T.Broadcast("m" + i);
        }
        Check(WaitFor(delegate
        {
            if (hub.Lines.Count != 5 * perSender) return false;
            foreach (Node c in cs) if (c.Lines.Count != 5 * perSender) return false;
            return true;
        }, 20000), "everything arrived: hub " + hub.Lines.Count + ", clients " + cs[0].Lines.Count + "/" + cs[4].Lines.Count);
        Pump(300);
        Check(OrderedPerSender(hub, 5, perSender), "hub: per-sender order kept, nothing lost or doubled");
        for (int i = 0; i < cs.Count; i++)
            Check(OrderedPerSender(cs[i], 5, perSender), "client " + i + ": per-sender order kept");
        Check(hub.Gone.Count == 0, "no flood protection false alarm");
        CloseAll();
    }

    /// <summary>The node got 'perSender' lines "m0".."mN" from each of 5 senders (not itself), each in order.</summary>
    static bool OrderedPerSender(Node n, int senders, int perSender)
    {
        Dictionary<string, int> next = new Dictionary<string, int>();
        foreach (string l in n.Lines)
        {
            int colon = l.IndexOf(':');
            string from = l.Substring(0, colon);
            string text = l.Substring(colon + 1);
            int seq;
            if (!text.StartsWith("m") || !int.TryParse(text.Substring(1), out seq)) return false;
            int expected;
            next.TryGetValue(from, out expected);
            if (seq != expected) return false;
            next[from] = expected + 1;
        }
        if (next.Count != senders) return false;
        foreach (int v in next.Values) if (v != perSender) return false;
        return true;
    }

    static void TestHandover()
    {
        Console.WriteLine("host handover");
        const int port = 5820;
        Node a = Hub(port, "Alpha");
        Node b = Join(port, "Bravo");
        Node c = Join(port, "Charlie");
        Check(WaitFor(delegate { return a.Peers.Count == 2; }, 2000), "A has two clients");
        string idB = b.T.MyId, idC = c.T.MyId;
        Check(idB == "p1" && idC == "p2", "ids before: " + idB + "," + idC);

        IDictionary<string, string> ids = a.T.ExportIdentities();
        Check(ids.Count == 3 && ids.ContainsKey("h") && ids.ContainsKey(idB) && ids.ContainsKey(idC), "export has everybody");

        int newPort = b.T.BecomeHub(ids, 0);
        Check(newPort > 0 && newPort != port, "B listens on a free port: " + newPort);
        Check(b.T.IsHost && b.T.HostId == idB && b.T.MyId == idB, "B is the hub and keeps its id");
        Check(b.Connected == 1, "no second Connected");

        // while the old hub is still connected, B talks to it through the upstream
        b.T.SendTo("h", "XFER OK " + newPort);
        Check(WaitFor(delegate { return a.Count(idB + ":XFER OK " + newPort) == 1; }, 2000), "B's private line reaches the old hub A");
        b.T.Broadcast("early");
        Check(WaitFor(delegate { return a.Count(idB + ":early") == 1 && c.Count(idB + ":early") == 1; }, 2000), "B's broadcast goes through the old hub to A and C");
        a.T.Broadcast("from old hub");
        Check(WaitFor(delegate { return b.Count("h:from old hub") == 1 && c.Count("h:from old hub") == 1; }, 2000), "lines from the old hub still reach B's app and C");

        a.T.MoveTo("127.0.0.1", newPort);
        c.T.MoveTo("127.0.0.1", newPort);
        b.T.DropUpstream();

        Check(WaitFor(delegate { return b.HasPeer("h") && b.HasPeer(idC); }, 6000), "B sees A and C arrive: " + string.Join(",", b.Peers.ToArray()));
        Check(WaitFor(delegate { return !a.T.IsHost && a.T.HostId == idB && c.T.HostId == idB; }, 3000), "A became a client, both know the new hub");
        Check(a.T.MyId == "h" && c.T.MyId == idC && b.T.MyId == idB, "ids unchanged: " + a.T.MyId + "," + c.T.MyId + "," + b.T.MyId);
        Check(a.T.HostId == idB && b.T.HostId == idB && b.T.IsHost && !c.T.IsHost, "roles");
        Check(b.Peers.Count == 2 && b.Peers.Contains("h|Alpha") && b.Peers.Contains(idC + "|Charlie"), "B's peers with names: " + string.Join(",", b.Peers.ToArray()));
        Check(b.T.AddressOf("h") == "127.0.0.1" && b.T.AddressOf(idC) == "127.0.0.1", "addresses");
        Check(a.Connected == 1 && c.Connected == 1 && b.Connected == 1, "no Connected is raised again");

        a.Lines.Clear(); b.Lines.Clear(); c.Lines.Clear();
        a.T.Broadcast("fromA");
        b.T.Broadcast("fromB");
        c.T.Broadcast("fromC");
        Check(WaitFor(delegate
        {
            return b.Count("h:fromA") == 1 && c.Count("h:fromA") == 1
                && a.Count(idB + ":fromB") == 1 && c.Count(idB + ":fromB") == 1
                && a.Count(idC + ":fromC") == 1 && b.Count(idC + ":fromC") == 1;
        }, 4000), "broadcasts work in all directions");
        a.T.SendTo(idC, "A to C");
        c.T.SendTo("h", "C to A");
        b.T.SendTo("h", "B to A");
        a.T.SendTo(idB, "A to B");
        Check(WaitFor(delegate
        {
            return c.Count("h:A to C") == 1 && a.Count(idC + ":C to A") == 1 && a.Count(idB + ":B to A") == 1 && b.Count("h:A to B") == 1;
        }, 4000), "private lines work in all directions (also through the new hub)");
        Pump(500);
        Check(a.Lines.Count == 4 && b.Lines.Count == 3 && c.Lines.Count == 3, "nothing doubled or stray: " + a.Lines.Count + "," + b.Lines.Count + "," + c.Lines.Count);
        Check(a.Closed.Count == 0 && b.Closed.Count == 0 && c.Closed.Count == 0, "no Closed anywhere");
        Check(b.Gone.Count == 0, "B saw no departures");

        // the old upstream is really gone: B's own id as seen from A's old room does not matter, but B must not reach A twice
        b.T.Broadcast("once");
        Check(WaitFor(delegate { return a.Count(idB + ":once") == 1 && c.Count(idB + ":once") == 1; }, 2000), "B reaches both of its clients");
        Pump(300);
        Check(a.Count(idB + ":once") == 1 && c.Count(idB + ":once") == 1, "exactly once each");

        // the new hub's identities work for the next move as well
        IDictionary<string, string> ids2 = b.T.ExportIdentities();
        Check(ids2.Count == 3 && ids2.ContainsKey(idB) && ids2.ContainsKey("h") && ids2.ContainsKey(idC), "B exports everybody including itself");

        // a move that fails
        Node e = Hub(5821, "Echo");
        Node d = Join(5821, "Delta");
        d.T.MoveTo("127.0.0.1", 5898);
        Check(WaitFor(delegate { return d.Closed.Count == 1; }, 9000), "a failed move is reported");
        Check(d.Closed.Count == 1 && d.Closed[0] == "Не удалось перейти к новому хосту", "reason: " + (d.Closed.Count > 0 ? d.Closed[0] : ""));
        Pump(200);
        Check(d.Closed.Count == 1 && e.Gone.Count == 1, "once; the old hub saw the client leave");
        CloseAll();
    }

    static void TestAbandonedHandover()
    {
        Console.WriteLine("a hand-over that does not happen");
        const int port = 5830;
        Node a = Hub(port, "Alpha");
        Node b = Join(port, "Bravo");
        Node c = Join(port, "Charlie");
        Check(WaitFor(delegate { return a.Peers.Count == 2; }, 2000), "A has two clients");
        string idB = b.T.MyId;

        int hubPort = b.T.BecomeHub(a.T.ExportIdentities(), 0);
        Check(b.T.IsHost && hubPort > 0, "B started a hub");
        b.T.AbandonHub();
        Check(!b.T.IsHost && b.T.HostId == "h" && b.T.MyId == idB, "B is a plain client of A again: host " + b.T.HostId + ", id " + b.T.MyId);

        // the abandoned port is closed
        bool reachable = true;
        try { using (TcpClient probe = new TcpClient()) { probe.Connect("127.0.0.1", hubPort); } }
        catch (SocketException) { reachable = false; }
        Check(!reachable, "the abandoned hub no longer listens");

        // and the room works as before: both directions, no stray Closed, no departures
        a.Lines.Clear(); b.Lines.Clear(); c.Lines.Clear();
        b.T.Broadcast("after");
        a.T.SendTo(idB, "to B");
        Check(WaitFor(delegate { return a.Count(idB + ":after") == 1 && c.Count(idB + ":after") == 1 && b.Count("h:to B") == 1; }, 3000), "traffic flows through A as before");
        Pump(300);
        Check(a.Closed.Count == 0 && b.Closed.Count == 0 && c.Closed.Count == 0 && a.Gone.Count == 0, "nobody was dropped or closed");

        // a new attempt with the same person works (the old failure used to leave BecomeHub throwing)
        int again = b.T.BecomeHub(a.T.ExportIdentities(), 0);
        Check(again > 0 && b.T.IsHost, "B can become the hub again");
        b.T.AbandonHub();
        Check(!b.T.IsHost, "and abandon it again");

        // AbandonHub on an ordinary client or on a hub does nothing
        c.T.AbandonHub();
        a.T.AbandonHub();
        Check(a.T.IsHost && !c.T.IsHost, "an ordinary client and the real hub are left alone");
        CloseAll();
    }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        InstallContext();
        TestBasics();
        TestRelayFilter();
        TestDisconnects();
        TestFull();
        TestKeys();
        TestAbuse();
        TestPortsAndClose();
        TestProbe();
        TestStress();
        TestHandover();
        TestAbandonedHandover();
        Check(offThread == 0, "every event was raised on the creating thread (" + offThread + " were not)");
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL LAN ROOM TESTS PASSED" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}
