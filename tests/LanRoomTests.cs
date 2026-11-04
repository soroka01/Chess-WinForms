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
    static int keyCounter;
    static int mainThread;
    static int offThread;   // events that were not raised on the creating thread
    static readonly List<Node> all = new List<Node>();

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
        TestKit.InstallContext();
        Node n = new Node(LanRoomTransport.Host(port, name, "h"));
        n.T.Start();
        return n;
    }

    static string NewKey() { return "key" + (++keyCounter) + "x" + Guid.NewGuid().ToString("N").Substring(0, 8); }

    static Node Client(int port, string name, string key)
    {
        TestKit.InstallContext();
        Node n = new Node(LanRoomTransport.Join("127.0.0.1", port, name, key));
        n.T.Start();
        return n;
    }

    /// <summary>Joins one client and waits until it is welcomed (so that ids follow the call order).</summary>
    static Node Join(int port, string name)
    {
        Node n = Client(port, name, NewKey());
        TestKit.Check(TestKit.WaitFor(delegate { return n.Connected == 1; }, 5000), name + " connected");
        return n;
    }

    static void CloseAll()
    {
        foreach (Node n in all) n.T.Close();
        all.Clear();
        TestKit.Pump(30);
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
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Connected == 1; }, 2000), "hub raises Connected after Start");
        TestKit.Check(hub.T.IsHost && hub.T.MyId == "h" && hub.T.HostId == "h" && hub.T.MyName == "Host", "hub identity");

        Node c1 = Join(port, "Alice");
        Node c2 = Join(port, "A<b>c d");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 2; }, 2000), "hub saw both clients");
        TestKit.Check(c1.T.MyId == "p1" && c2.T.MyId == "p2", "ids in connection order: " + c1.T.MyId + "," + c2.T.MyId);
        TestKit.Check(c1.T.HostId == "h" && !c1.T.IsHost && c1.T.MyName == "Alice", "client identity");
        TestKit.Check(hub.Peers[0] == "p1|Alice" && hub.Peers[1] == "p2|Abc d", "names (sanitized): " + string.Join(",", hub.Peers.ToArray()));
        TestKit.Check(hub.Connected == 1 && c1.Connected == 1, "Connected exactly once");
        TestKit.Check(c1.T.AddressOf("p2") == null, "a client has no addresses");
        TestKit.Check(hub.T.AddressOf("p1") == "127.0.0.1" && hub.T.AddressOf("p2") == "127.0.0.1", "AddressOf is the remote IP without port");
        TestKit.Check(hub.T.AddressOf("p9") == null && hub.T.AddressOf(null) == null, "AddressOf unknown id is null");

        // broadcast from a client: hub app + the other client, once, not the sender
        c1.T.Broadcast("hello all");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Count("p1:hello all") == 1 && c2.Count("p1:hello all") == 1; }, 2000), "broadcast delivered to hub and other client");
        TestKit.Pump(250);
        TestKit.Check(hub.Count("p1:hello all") == 1 && c2.Count("p1:hello all") == 1 && c1.Lines.Count == 0, "exactly once, never back to the sender");

        // broadcast from the hub
        hub.T.Broadcast("from hub");
        TestKit.Check(TestKit.WaitFor(delegate { return c1.Count("h:from hub") == 1 && c2.Count("h:from hub") == 1; }, 2000), "hub broadcast reaches every client");
        TestKit.Pump(150);
        TestKit.Check(c1.Count("h:from hub") == 1 && c2.Count("h:from hub") == 1 && hub.Count("h:from hub") == 0, "once each, not the hub itself");

        // private lines
        hub.Lines.Clear(); c1.Lines.Clear(); c2.Lines.Clear();
        c1.T.SendTo("p2", "secret");
        TestKit.Check(TestKit.WaitFor(delegate { return c2.Count("p1:secret") == 1; }, 2000), "private line reaches its target through the hub");
        c1.T.SendTo("h", "to the hub");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Count("p1:to the hub") == 1; }, 2000), "private line to the hub's app");
        hub.T.SendTo("p2", "from hub only");
        TestKit.Check(TestKit.WaitFor(delegate { return c2.Count("h:from hub only") == 1; }, 2000), "hub private line");
        c1.T.SendTo("p1", "to myself");
        c1.T.SendTo("nobody", "lost");
        c1.T.SendTo("p 2", "bad id");
        hub.T.SendTo("h", "hub to itself");
        hub.T.SendTo("p7", "unknown");
        c2.T.Broadcast("a\nb");   // line breaks cannot split a line into two
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Count("p2:a b") == 1; }, 2000), "line breaks are flattened");
        TestKit.Pump(250);
        TestKit.Check(hub.Lines.Count == 2 && c1.Lines.Count == 1 && c2.Lines.Count == 2, "private lines went nowhere else: hub " + hub.Lines.Count + ", c1 " + c1.Lines.Count + ", c2 " + c2.Lines.Count);

        // the longest allowed line passes, longer ones are cut by the sender
        string big = new string('y', 3000);
        c1.T.Broadcast(big);
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Lines.Count == 3; }, 2000), "very long line still arrives");
        TestKit.Check(hub.Lines[2].Length <= 2 + 2048, "and was cut to the limit: " + hub.Lines[2].Length);
        TestKit.Pump(100);
        TestKit.Check(hub.Gone.Count == 0, "the connection survived");
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
        TestKit.Check(TestKit.WaitFor(delegate { return c1.Count("p2:free words") == 1 && hub.Count("p2:free words") == 1; }, 2000), "an allowed sender is relayed");
        TestKit.Pump(250);
        TestKit.Check(hub.Count("p1:muted words") == 1, "the hub's own app still sees the muted sender");
        TestKit.Check(c2.Count("p1:muted words") == 0, "but the other client does not");
        hub.T.RelayFilter = null;
        c1.T.Broadcast("again");
        TestKit.Check(TestKit.WaitFor(delegate { return c2.Count("p1:again") == 1; }, 2000), "without a filter everything is relayed");
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
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 3; }, 2000), "three clients");

        hub.T.Disconnect("p2");
        TestKit.Check(TestKit.WaitFor(delegate { return c2.Closed.Count == 1; }, 2000), "the dropped client is told the room is gone");
        TestKit.Check(c2.Closed.Count == 1 && c2.Closed[0].Length > 0, "with a reason: " + (c2.Closed.Count > 0 ? c2.Closed[0] : ""));
        TestKit.Pump(250);
        TestKit.Check(hub.Gone.Count == 1 && hub.Gone[0] == "p2", "PeerDisconnected exactly once after Disconnect: " + string.Join(",", hub.Gone.ToArray()));
        TestKit.Check(hub.T.AddressOf("p2") == null, "no address for a dropped client");
        hub.T.Disconnect("p2");
        hub.T.Disconnect("zzz");
        hub.T.Disconnect(null);
        TestKit.Pump(100);
        TestKit.Check(hub.Gone.Count == 1, "Disconnect of an unknown or finished id does nothing");

        c3.T.Broadcast("still here");
        TestKit.Check(TestKit.WaitFor(delegate { return c1.Count("p3:still here") == 1; }, 2000), "others keep working");
        TestKit.Check(c2.Lines.Count == 0, "the dropped client gets nothing any more");

        c1.T.Close();
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Gone.Count == 2; }, 2000), "a client that closes is noticed");
        TestKit.Pump(250);
        TestKit.Check(hub.Gone.Count == 2 && hub.Gone[1] == "p1", "exactly once: " + string.Join(",", hub.Gone.ToArray()));
        TestKit.Check(c1.Closed.Count == 0, "the closer gets no Closed event");

        hub.T.Close();
        TestKit.Check(TestKit.WaitFor(delegate { return c3.Closed.Count == 1; }, 3000), "clients notice the hub closing");
        TestKit.Pump(250);
        TestKit.Check(c3.Closed.Count == 1 && c3.Closed[0] == "Хост закрыл комнату", "reason: " + (c3.Closed.Count > 0 ? c3.Closed[0] : ""));
        TestKit.Check(hub.Closed.Count == 0, "the closer (hub) gets no Closed event");
        TestKit.Check(hub.Gone.Count == 2, "a closing hub raises no PeerDisconnected");
        CloseAll();
    }

    static void TestFull()
    {
        Console.WriteLine("the room is full after ten clients");
        const int port = 5804;
        Node hub = Hub(port, "Host");
        List<Node> cs = new List<Node>();
        for (int i = 0; i < 10; i++) cs.Add(Join(port, "C" + i));
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 10; }, 3000), "ten clients fit");

        Raw raw = new Raw(port);
        TestKit.Check(raw.Hello("Eleven", "eleven1") == "FULL", "the 11th gets FULL");
        TestKit.Check(raw.ClosedByPeer(), "and is disconnected");
        raw.Close();

        Node late = Client(port, "Late", NewKey());
        TestKit.Check(TestKit.WaitFor(delegate { return late.Closed.Count == 1; }, 3000), "the 11th transport is told");
        TestKit.Check(late.Closed.Count == 1 && late.Closed[0].StartsWith("Не удалось подключиться") && late.Closed[0].Contains("заполнена"), "reason: " + (late.Closed.Count > 0 ? late.Closed[0] : ""));
        TestKit.Check(late.Connected == 0 && hub.Peers.Count == 10, "nothing else happened");

        cs[2].T.Close();
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "a client leaves");
        Node again = Join(port, "Again");
        TestKit.Check(again.T.MyId == "p11", "a free slot is usable, ids are never reused: " + again.T.MyId);
        CloseAll();
    }

    static void TestKeys()
    {
        Console.WriteLine("identity across reconnects");
        const int port = 5805;
        Node hub = Hub(port, "Host");
        Node a = Client(port, "A", "keyA");
        TestKit.Check(TestKit.WaitFor(delegate { return a.Connected == 1; }, 3000), "A connected");
        TestKit.Check(a.T.MyId == "p1", "first id");
        a.T.Close();
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "A left");
        Node b = Join(port, "B");
        TestKit.Check(b.T.MyId == "p2", "a newcomer gets a fresh id, p1 is not reused: " + b.T.MyId);
        Node a2 = Client(port, "A", "keyA");
        TestKit.Check(TestKit.WaitFor(delegate { return a2.Connected == 1; }, 3000), "A is back");
        TestKit.Check(a2.T.MyId == "p1", "the known key gets the old id back: " + a2.T.MyId);

        // the same key while the old connection still looks alive: the old one is replaced, in a clean order
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 3; }, 2000), "hub saw A's return");
        hub.Log.Clear();
        Node a3 = Client(port, "A again", "keyA");
        TestKit.Check(TestKit.WaitFor(delegate { return a3.Connected == 1; }, 3000), "second connection with the same key");
        TestKit.Check(a3.T.MyId == "p1", "same id");
        TestKit.Check(TestKit.WaitFor(delegate { return a2.Closed.Count == 1; }, 3000), "the old connection is closed");
        TestKit.Pump(250);
        TestKit.Check(string.Join(",", hub.Log.ToArray()) == "-p1,+p1", "PeerDisconnected then PeerConnected: " + string.Join(",", hub.Log.ToArray()));
        a3.T.Broadcast("works");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Count("p1:works") == 1 && b.Count("p1:works") == 1; }, 2000), "the new connection works");

        // exported identities
        IDictionary<string, string> ids = hub.T.ExportIdentities();
        TestKit.Check(ids.ContainsKey("p1") && ids["p1"] == "keyA" && ids.ContainsKey("p2") && ids.ContainsKey("h") && ids.Count == 3, "ExportIdentities lists clients and the hub itself: " + ids.Count);
        CloseAll();
    }

    static void TestAbuse()
    {
        Console.WriteLine("garbage, oversize lines, floods, invalid commands");
        const int port = 5806;
        Node hub = Hub(port, "Host");

        Raw r = new Raw(port);
        r.Send("GET / HTTP/1.1\n");
        TestKit.Check(r.ReadLine() == "BAD", "garbage greeting is answered with BAD");
        TestKit.Check(r.ClosedByPeer(), "and closed");
        r.Close();

        r = new Raw(port);
        r.Send("CHESS 2 " + new string('n', 5000) + "\n");
        TestKit.Check(r.ClosedByPeer(), "an over-long greeting closes the connection");
        r.Close();

        r = new Raw(port);
        r.Send("CHESS 1 OldClient\n");
        TestKit.Check(r.ReadLine() == "BADVERSION", "the old two-player greeting is refused as a wrong version");
        TestKit.Check(r.ClosedByPeer(), "and closed");
        r.Close();

        // opened and closed without a word (a probe): no member, no error
        r = new Raw(port);
        r.Close();
        TestKit.Pump(200);

        r = new Raw(port);
        r.Send("hello lower case\n");
        TestKit.Check(r.ReadLine() == "BAD", "wrong greeting word");
        r.Close();
        TestKit.Pump(100);
        TestKit.Check(hub.Peers.Count == 0 && hub.Gone.Count == 0, "none of them became a member");

        // a proper greeting: all kinds of nonsense afterwards is ignored
        r = new Raw(port);
        string welcome = r.Hello("Raw Name", "raw1");
        TestKit.Check(welcome == "WELCOME p1 h", "welcome line: " + welcome);
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 1; }, 2000) && hub.Peers[0] == "p1|Raw Name", "peer announced with a clean name: " + (hub.Peers.Count > 0 ? hub.Peers[0] : ""));
        r.Send("X foo\nB\nB \nT\nT p1\nT p1 hi\nT zz hi\nT zz\nCHESS 2 again\nBx\nF h forged\n");
        r.Send("B alive\n");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Lines.Count >= 1; }, 2000), "the valid line after the nonsense arrives");
        TestKit.Pump(250);
        TestKit.Check(hub.Lines.Count == 1 && hub.Lines[0] == "p1:alive", "only the valid line was delivered: " + string.Join("|", hub.Lines.ToArray()));
        TestKit.Check(hub.Gone.Count == 0, "and the connection is still there");

        // exactly 2048 chars is fine, 2049 is not
        string edge = new string('e', 2046);
        r.Send("B " + edge + "\n");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Lines.Count == 2; }, 2000) && hub.Lines[1] == "p1:" + edge, "a line of 2048 chars is accepted");
        r.Send("B " + new string('z', 2047) + "\n");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Gone.Count == 1; }, 2000), "a line of 2049 chars drops the connection");
        TestKit.Check(r.ClosedByPeer(), "and it is really closed");
        r.Close();
        TestKit.Check(hub.Lines.Count == 2, "the over-long line was not delivered");

        // flood
        hub.Lines.Clear(); hub.Gone.Clear();
        r = new Raw(port);
        TestKit.Check(r.Hello("Flood", "flood1").StartsWith("WELCOME p2 "), "next id");
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append("B x").Append(i).Append('\n');
        try { r.Send(sb.ToString()); } catch (IOException) { }
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Gone.Count == 1; }, 3000), "a flood drops the connection");
        TestKit.Pump(200);
        TestKit.Check(hub.Gone.Count == 1 && hub.Lines.Count > 0 && hub.Lines.Count <= 250, "only a bounded part got through: " + hub.Lines.Count);
        r.Close();

        // a greeting that never comes: the hub gives up after its timeout
        r = new Raw(port);
        Stopwatch sw = Stopwatch.StartNew();
        r.C.ReceiveTimeout = 9000;
        TestKit.Check(r.ClosedByPeer() && sw.ElapsedMilliseconds < 8500, "a silent connection is dropped by the hub (" + sw.ElapsedMilliseconds + " ms)");
        r.Close();

        // the hub is fine afterwards
        Node c = Join(port, "Normal");
        c.T.Broadcast("ok");
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Count(c.T.MyId + ":ok") == 1; }, 2000), "regular clients still work");
        CloseAll();
    }

    static void TestPortsAndClose()
    {
        Console.WriteLine("busy port, failed connection, Close");
        LanRoomTransport first = LanRoomTransport.Host(5807, "One", "h");
        bool threw = false;
        try { LanRoomTransport.Host(5807, "Two", "h"); }
        catch (SocketException) { threw = true; }
        TestKit.Check(threw, "a busy port throws from the factory");
        first.Close();
        LanRoomTransport second = null;
        try { second = LanRoomTransport.Host(5807, "Two", "h"); }
        catch (SocketException) { }
        TestKit.Check(second != null, "the port is free again after Close");
        if (second != null) second.Close();

        // nobody listens
        Node n = Client(5899, "Lonely", NewKey());
        TestKit.Check(TestKit.WaitFor(delegate { return n.Closed.Count == 1; }, 9000), "connection refused is reported");
        TestKit.Check(n.Closed.Count == 1 && n.Closed[0].StartsWith("Не удалось подключиться"), "reason: " + (n.Closed.Count > 0 ? n.Closed[0] : ""));
        TestKit.Check(n.Connected == 0, "never connected");

        // something that is not a room
        System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5808);
        l.Start();
        Node j = Client(5808, "Bob", NewKey());
        TcpClient c = l.AcceptTcpClient();
        byte[] junk = Encoding.ASCII.GetBytes("GET / HTTP/1.1\n");
        c.GetStream().Write(junk, 0, junk.Length);
        TestKit.Check(TestKit.WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "a foreign server is rejected: " + (j.Closed.Count > 0 ? j.Closed[0] : ""));
        c.Close(); l.Stop();

        // a two-player (version 1) host answers with its own greeting
        l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5813);
        l.Start();
        j = Client(5813, "Bob", NewKey());
        c = l.AcceptTcpClient();
        byte[] v1 = Encoding.ASCII.GetBytes("CHESS 1 Host\n");
        c.GetStream().Write(v1, 0, v1.Length);
        TestKit.Check(TestKit.WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "an old host is refused");
        TestKit.Check(j.Closed.Count == 1 && j.Closed[0].Contains("версия") && j.Closed[0].StartsWith("Не удалось подключиться"), "with a readable reason: " + (j.Closed.Count > 0 ? j.Closed[0] : ""));
        TestKit.Check(j.Connected == 0, "never connected");
        c.Close(); l.Stop();

        // a hub that sends an over-long line to a client
        l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5809);
        l.Start();
        j = Client(5809, "Bob", NewKey());
        c = l.AcceptTcpClient();
        byte[] big = Encoding.ASCII.GetBytes("WELCOME p1 h\nF h " + new string('x', 5000) + "\n");
        c.GetStream().Write(big, 0, big.Length);
        TestKit.Check(TestKit.WaitFor(delegate { return j.Closed.Count == 1; }, 3000), "an over-long line from the hub ends the connection");
        c.Close(); l.Stop();

        // Close is idempotent and silent
        Node h = Hub(5810, "Host");
        Node k = Join(5810, "K");
        k.T.Close(); k.T.Dispose(); k.T.Close();
        h.T.Close(); h.T.Close(); h.T.Dispose();
        h.T.Broadcast("after close"); h.T.SendTo("p1", "x"); k.T.Broadcast("after close"); h.T.Disconnect("p1");
        TestKit.Pump(300);
        TestKit.Check(h.Closed.Count == 0 && k.Closed.Count == 0 && h.Gone.Count == 0, "closing is silent");
        TestKit.InstallContext();
        Node never = new Node(LanRoomTransport.Join("127.0.0.1", 5810, "X", NewKey()));
        never.T.Close();
        never.T.Start();   // Start after Close must not connect or raise anything
        TestKit.Pump(300);
        TestKit.Check(never.Connected == 0 && never.Closed.Count == 0, "Start after Close does nothing");
        // a hub that was never started still releases its port
        LanRoomTransport idle = LanRoomTransport.Host(5811, "Idle", "h");
        idle.Dispose();
        LanRoomTransport again = LanRoomTransport.Host(5811, "Idle", "h");
        again.Dispose();
        TestKit.Check(true, "-");
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
        TestKit.Check(TestKit.WaitFor(delegate { return open.HasValue && closedPort.HasValue; }, 5000), "both probes finish");
        TestKit.Check(open == true, "a listening hub is reachable");
        TestKit.Check(closedPort == false, "a closed port is not");
        TestKit.Pump(300);
        TestKit.Check(hub.Peers.Count == 1 && hub.Gone.Count == 0 && hub.Closed.Count == 0 && c.Closed.Count == 0, "the hub took no notice of the probe");
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
        TestKit.Check(TestKit.WaitFor(delegate { return hub.Peers.Count == 5; }, 3000), "five clients");
        for (int i = 0; i < perSender; i++)
        {
            foreach (Node c in cs) c.T.Broadcast("m" + i);
            hub.T.Broadcast("m" + i);
        }
        TestKit.Check(TestKit.WaitFor(delegate
        {
            if (hub.Lines.Count != 5 * perSender) return false;
            foreach (Node c in cs) if (c.Lines.Count != 5 * perSender) return false;
            return true;
        }, 20000), "everything arrived: hub " + hub.Lines.Count + ", clients " + cs[0].Lines.Count + "/" + cs[4].Lines.Count);
        TestKit.Pump(300);
        TestKit.Check(OrderedPerSender(hub, 5, perSender), "hub: per-sender order kept, nothing lost or doubled");
        for (int i = 0; i < cs.Count; i++)
            TestKit.Check(OrderedPerSender(cs[i], 5, perSender), "client " + i + ": per-sender order kept");
        TestKit.Check(hub.Gone.Count == 0, "no flood protection false alarm");
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
        TestKit.Check(TestKit.WaitFor(delegate { return a.Peers.Count == 2; }, 2000), "A has two clients");
        string idB = b.T.MyId, idC = c.T.MyId;
        TestKit.Check(idB == "p1" && idC == "p2", "ids before: " + idB + "," + idC);

        IDictionary<string, string> ids = a.T.ExportIdentities();
        TestKit.Check(ids.Count == 3 && ids.ContainsKey("h") && ids.ContainsKey(idB) && ids.ContainsKey(idC), "export has everybody");

        int newPort = b.T.BecomeHub(ids, 0);
        TestKit.Check(newPort > 0 && newPort != port, "B listens on a free port: " + newPort);
        TestKit.Check(b.T.IsHost && b.T.HostId == idB && b.T.MyId == idB, "B is the hub and keeps its id");
        TestKit.Check(b.Connected == 1, "no second Connected");

        // while the old hub is still connected, B talks to it through the upstream
        b.T.SendTo("h", "XFER OK " + newPort);
        TestKit.Check(TestKit.WaitFor(delegate { return a.Count(idB + ":XFER OK " + newPort) == 1; }, 2000), "B's private line reaches the old hub A");
        b.T.Broadcast("early");
        TestKit.Check(TestKit.WaitFor(delegate { return a.Count(idB + ":early") == 1 && c.Count(idB + ":early") == 1; }, 2000), "B's broadcast goes through the old hub to A and C");
        a.T.Broadcast("from old hub");
        TestKit.Check(TestKit.WaitFor(delegate { return b.Count("h:from old hub") == 1 && c.Count("h:from old hub") == 1; }, 2000), "lines from the old hub still reach B's app and C");

        a.T.MoveTo("127.0.0.1", newPort);
        c.T.MoveTo("127.0.0.1", newPort);
        b.T.DropUpstream();

        TestKit.Check(TestKit.WaitFor(delegate { return b.HasPeer("h") && b.HasPeer(idC); }, 6000), "B sees A and C arrive: " + string.Join(",", b.Peers.ToArray()));
        TestKit.Check(TestKit.WaitFor(delegate { return !a.T.IsHost && a.T.HostId == idB && c.T.HostId == idB; }, 3000), "A became a client, both know the new hub");
        TestKit.Check(a.T.MyId == "h" && c.T.MyId == idC && b.T.MyId == idB, "ids unchanged: " + a.T.MyId + "," + c.T.MyId + "," + b.T.MyId);
        TestKit.Check(a.T.HostId == idB && b.T.HostId == idB && b.T.IsHost && !c.T.IsHost, "roles");
        TestKit.Check(b.Peers.Count == 2 && b.Peers.Contains("h|Alpha") && b.Peers.Contains(idC + "|Charlie"), "B's peers with names: " + string.Join(",", b.Peers.ToArray()));
        TestKit.Check(b.T.AddressOf("h") == "127.0.0.1" && b.T.AddressOf(idC) == "127.0.0.1", "addresses");
        TestKit.Check(a.Connected == 1 && c.Connected == 1 && b.Connected == 1, "no Connected is raised again");

        a.Lines.Clear(); b.Lines.Clear(); c.Lines.Clear();
        a.T.Broadcast("fromA");
        b.T.Broadcast("fromB");
        c.T.Broadcast("fromC");
        TestKit.Check(TestKit.WaitFor(delegate
        {
            return b.Count("h:fromA") == 1 && c.Count("h:fromA") == 1
                && a.Count(idB + ":fromB") == 1 && c.Count(idB + ":fromB") == 1
                && a.Count(idC + ":fromC") == 1 && b.Count(idC + ":fromC") == 1;
        }, 4000), "broadcasts work in all directions");
        a.T.SendTo(idC, "A to C");
        c.T.SendTo("h", "C to A");
        b.T.SendTo("h", "B to A");
        a.T.SendTo(idB, "A to B");
        TestKit.Check(TestKit.WaitFor(delegate
        {
            return c.Count("h:A to C") == 1 && a.Count(idC + ":C to A") == 1 && a.Count(idB + ":B to A") == 1 && b.Count("h:A to B") == 1;
        }, 4000), "private lines work in all directions (also through the new hub)");
        TestKit.Pump(500);
        TestKit.Check(a.Lines.Count == 4 && b.Lines.Count == 3 && c.Lines.Count == 3, "nothing doubled or stray: " + a.Lines.Count + "," + b.Lines.Count + "," + c.Lines.Count);
        TestKit.Check(a.Closed.Count == 0 && b.Closed.Count == 0 && c.Closed.Count == 0, "no Closed anywhere");
        TestKit.Check(b.Gone.Count == 0, "B saw no departures");

        // the old upstream is really gone: B's own id as seen from A's old room does not matter, but B must not reach A twice
        b.T.Broadcast("once");
        TestKit.Check(TestKit.WaitFor(delegate { return a.Count(idB + ":once") == 1 && c.Count(idB + ":once") == 1; }, 2000), "B reaches both of its clients");
        TestKit.Pump(300);
        TestKit.Check(a.Count(idB + ":once") == 1 && c.Count(idB + ":once") == 1, "exactly once each");

        // the new hub's identities work for the next move as well
        IDictionary<string, string> ids2 = b.T.ExportIdentities();
        TestKit.Check(ids2.Count == 3 && ids2.ContainsKey(idB) && ids2.ContainsKey("h") && ids2.ContainsKey(idC), "B exports everybody including itself");

        // a move that fails
        Node e = Hub(5821, "Echo");
        Node d = Join(5821, "Delta");
        d.T.MoveTo("127.0.0.1", 5898);
        TestKit.Check(TestKit.WaitFor(delegate { return d.Closed.Count == 1; }, 9000), "a failed move is reported");
        TestKit.Check(d.Closed.Count == 1 && d.Closed[0] == "Не удалось перейти к новому хосту", "reason: " + (d.Closed.Count > 0 ? d.Closed[0] : ""));
        TestKit.Pump(200);
        TestKit.Check(d.Closed.Count == 1 && e.Gone.Count == 1, "once; the old hub saw the client leave");
        CloseAll();
    }

    static void TestAbandonedHandover()
    {
        Console.WriteLine("a hand-over that does not happen");
        const int port = 5830;
        Node a = Hub(port, "Alpha");
        Node b = Join(port, "Bravo");
        Node c = Join(port, "Charlie");
        TestKit.Check(TestKit.WaitFor(delegate { return a.Peers.Count == 2; }, 2000), "A has two clients");
        string idB = b.T.MyId;

        int hubPort = b.T.BecomeHub(a.T.ExportIdentities(), 0);
        TestKit.Check(b.T.IsHost && hubPort > 0, "B started a hub");
        b.T.AbandonHub();
        TestKit.Check(!b.T.IsHost && b.T.HostId == "h" && b.T.MyId == idB, "B is a plain client of A again: host " + b.T.HostId + ", id " + b.T.MyId);

        // the abandoned port is closed
        bool reachable = true;
        try { using (TcpClient probe = new TcpClient()) { probe.Connect("127.0.0.1", hubPort); } }
        catch (SocketException) { reachable = false; }
        TestKit.Check(!reachable, "the abandoned hub no longer listens");

        // and the room works as before: both directions, no stray Closed, no departures
        a.Lines.Clear(); b.Lines.Clear(); c.Lines.Clear();
        b.T.Broadcast("after");
        a.T.SendTo(idB, "to B");
        TestKit.Check(TestKit.WaitFor(delegate { return a.Count(idB + ":after") == 1 && c.Count(idB + ":after") == 1 && b.Count("h:to B") == 1; }, 3000), "traffic flows through A as before");
        TestKit.Pump(300);
        TestKit.Check(a.Closed.Count == 0 && b.Closed.Count == 0 && c.Closed.Count == 0 && a.Gone.Count == 0, "nobody was dropped or closed");

        // a new attempt with the same person works (the old failure used to leave BecomeHub throwing)
        int again = b.T.BecomeHub(a.T.ExportIdentities(), 0);
        TestKit.Check(again > 0 && b.T.IsHost, "B can become the hub again");
        b.T.AbandonHub();
        TestKit.Check(!b.T.IsHost, "and abandon it again");

        // AbandonHub on an ordinary client or on a hub does nothing
        c.T.AbandonHub();
        a.T.AbandonHub();
        TestKit.Check(a.T.IsHost && !c.T.IsHost, "an ordinary client and the real hub are left alone");
        CloseAll();
    }

    // ------------------------------------------------------------ hardening

    /// <summary>A raw connection that sends one byte of a greeting every 'everyMs' until the hub closes it.</summary>
    sealed class Drip
    {
        public readonly Raw R;
        public readonly Stopwatch Age = Stopwatch.StartNew();
        volatile bool stop;

        public Drip(int port, int everyMs)
        {
            R = new Raw(port);
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                byte[] b = new byte[] { (byte)'C' };
                while (!stop)
                {
                    try { R.C.GetStream().Write(b, 0, 1); } catch (Exception) { return; }
                    System.Threading.Thread.Sleep(everyMs);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Waits for the hub to hang up; the elapsed time at that moment, or -1 if it did not within the limit.</summary>
        public long WaitDropped(int limitMs)
        {
            R.C.ReceiveTimeout = limitMs;
            bool closed = R.ClosedByPeer();
            stop = true;
            return closed ? Age.ElapsedMilliseconds : -1;
        }
    }

    static void TestHandshakeDeadline()
    {
        Console.WriteLine("greeting deadline, per-address cap on pending greetings");
        const int port = 5840;
        Node hub = Hub(port, "Host");

        // a byte every second never trips the per-read timeout, only the overall deadline ends it
        List<Drip> drips = new List<Drip>();
        for (int i = 0; i < 3; i++) drips.Add(new Drip(port, 1000));
        Stopwatch sw = Stopwatch.StartNew();
        Node c = Join(port, "Real");
        TestKit.Check(c.T.MyId == "p1" && sw.ElapsedMilliseconds < 3000, "a real client joins while slow ones hang about (" + sw.ElapsedMilliseconds + " ms)");
        foreach (Drip d in drips)
        {
            long ms = d.WaitDropped(12000);
            TestKit.Check(ms >= 5000 && ms <= 9000, "a slow greeting is dropped by the overall deadline (" + ms + " ms)");
            d.R.Close();
        }
        TestKit.Check(hub.Peers.Count == 1 && hub.Gone.Count == 0, "none of them became a member");

        // as many silent connections as a single address may keep: the next one is refused at once
        List<Raw> silent = new List<Raw>();
        for (int i = 0; i < 6; i++) silent.Add(new Raw(port));
        TestKit.Pump(200);
        Raw extra = new Raw(port);
        extra.C.ReceiveTimeout = 2000;
        sw.Reset(); sw.Start();
        TestKit.Check(extra.ClosedByPeer() && sw.ElapsedMilliseconds < 1500, "the seventh pending greeting from one address is turned away (" + sw.ElapsedMilliseconds + " ms)");
        extra.Close();

        // when the silent ones time out the slots are free again
        silent[0].C.ReceiveTimeout = 10000;
        TestKit.Check(silent[0].ClosedByPeer(), "the silent ones are dropped after the deadline");
        foreach (Raw r in silent) r.Close();
        TestKit.Pump(300);
        Node d2 = Join(port, "Later");
        TestKit.Check(d2.T.MyId == "p2", "and the slots are free again: " + d2.T.MyId);
        CloseAll();
    }

    static void TestHubClosesAtOnce()
    {
        Console.WriteLine("a hub that hangs up after the greeting");
        System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5841);
        l.Start();
        Node j = Client(5841, "Bob", NewKey());
        TcpClient c = l.AcceptTcpClient();
        StreamReader r = new StreamReader(c.GetStream(), Encoding.UTF8);
        TestKit.Check((r.ReadLine() ?? "").StartsWith("CHESS 2 Bob"), "the client greeted first");
        c.Close();   // end of stream, nothing else was said
        TestKit.Check(TestKit.WaitFor(delegate { return j.Closed.Count == 1; }, 4000), "the client gives up");
        TestKit.Check(j.Closed.Count == 1 && j.Closed[0] == "Не удалось подключиться: хост закрыл соединение", "and says so: " + (j.Closed.Count > 0 ? j.Closed[0] : ""));
        l.Stop();
        CloseAll();
    }

    static void TestAcceptErrors()
    {
        Console.WriteLine("accept errors");
        try
        {
            LanRoomTransport.InjectAcceptFaults = 3;   // transient: the room must survive them
            Node hub = Hub(5844, "Host");
            Node c = Join(5844, "Alice");
            TestKit.Check(c.T.MyId == "p1" && LanRoomTransport.InjectAcceptFaults == 0, "transient accept errors are retried");
            TestKit.Check(hub.Closed.Count == 0, "the room stays open");
            c.T.Broadcast("still works");
            TestKit.Check(TestKit.WaitFor(delegate { return hub.Count("p1:still works") == 1; }, 2000), "and works");
            CloseAll();

            LanRoomTransport.InjectAcceptFaults = 1000;   // hopeless: after a few tries the room is closed with a reason
            Node dead = Hub(5845, "Host");
            TestKit.Check(TestKit.WaitFor(delegate { return dead.Closed.Count == 1; }, 9000), "a listener that keeps failing ends the room");
            TestKit.Check(dead.Closed.Count == 1 && dead.Closed[0] == "Ожидание соединений прервано", "with a reason: " + (dead.Closed.Count > 0 ? dead.Closed[0] : ""));
        }
        finally { LanRoomTransport.InjectAcceptFaults = 0; }
        CloseAll();
    }

    static void TestPeerEventOrder()
    {
        Console.WriteLine("PeerConnected always precedes PeerDisconnected");
        const int port = 5846;
        Node hub = Hub(port, "Host");
        // hello and an abortive close, again and again: the link may fail before the arrival was even announced
        for (int i = 0; i < 60; i++)
        {
            TcpClient c = new TcpClient();
            c.Connect("127.0.0.1", port);
            c.LingerState = new LingerOption(true, 0);   // close sends a reset
            byte[] hello = Encoding.UTF8.GetBytes("CHESS 2 N" + i + "\tchurn" + i + "x" + Guid.NewGuid().ToString("N").Substring(0, 8) + "\n");
            c.GetStream().Write(hello, 0, hello.Length);
            c.Close();
            if (i % 10 == 9) TestKit.Pump(60);
        }
        TestKit.Pump(800);
        HashSet<string> present = new HashSet<string>();
        string wrong = null;
        foreach (string e in hub.Log)
        {
            string id = e.Substring(1);
            if (e[0] == '+') { if (!present.Add(id)) wrong = wrong ?? "double " + e; }
            else if (!present.Remove(id)) wrong = wrong ?? "departure before arrival: " + e;
        }
        TestKit.Check(wrong == null, "order of events: " + (wrong ?? "ok") + " (" + hub.Log.Count + " events)");
        TestKit.Check(hub.Log.Count > 0, "some of them really got in");
        TestKit.Check(present.Count == 0, "no ghost member is left: " + string.Join(",", new List<string>(present).ToArray()));
        CloseAll();
    }

    static void TestExclusivePort()
    {
        Console.WriteLine("the hub port cannot be shared");
        const int port = 5847;
        LanRoomTransport hub = LanRoomTransport.Host(port, "Host", "h");
        foreach (System.Net.IPAddress a in new System.Net.IPAddress[] { System.Net.IPAddress.Any, System.Net.IPAddress.Loopback })
        {
            bool bound = false;
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);   // what a hijacker would ask for
                try { s.Bind(new System.Net.IPEndPoint(a, port)); bound = true; }
                catch (SocketException) { }
            }
            TestKit.Check(!bound, "another process cannot bind " + a + ":" + port + " even with ReuseAddress");
        }
        bool plain = false;
        try { System.Net.Sockets.TcpListener t = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port); t.Start(); t.Stop(); plain = true; }
        catch (SocketException) { }
        TestKit.Check(!plain, "nor an ordinary listener");
        hub.Close();

        // the exclusive bind must not make a quick restart impossible
        Node h = Hub(5848, "Host");
        Node c = Join(5848, "A");
        c.T.Broadcast("x");
        TestKit.Check(TestKit.WaitFor(delegate { return h.Count("p1:x") == 1; }, 2000), "a connection was made");
        h.T.Close();
        c.T.Close();
        TestKit.Pump(100);
        LanRoomTransport again = null;
        try { again = LanRoomTransport.Host(5848, "Host", "h"); }
        catch (SocketException ex) { Console.WriteLine("  restart failed: " + ex.Message); }
        TestKit.Check(again != null, "the port can be taken again right after a room with members was closed");
        if (again != null) again.Close();
        CloseAll();
    }

    static void TestNeedsMessageLoop()
    {
        Console.WriteLine("a transport needs a UI context");
        string noContext = null, plainContext = null, join = null;
        System.Threading.Thread t = new System.Threading.Thread(delegate()
        {
            try { LanRoomTransport.Host(5849, "X", "h"); noContext = "no exception"; }
            catch (InvalidOperationException ex) { noContext = ex.Message; }
            try { LanRoomTransport.Join("127.0.0.1", 5849, "X", "k"); join = "no exception"; }
            catch (InvalidOperationException ex) { join = ex.Message; }
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());
            try { LanRoomTransport.Host(5849, "X", "h"); plainContext = "no exception"; }
            catch (InvalidOperationException ex) { plainContext = ex.Message; }
        });
        t.Start();
        t.Join();
        TestKit.Check(noContext != null && noContext.Contains("message loop"), "Host without a context fails fast: " + noContext);
        TestKit.Check(join != null && join.Contains("message loop"), "Join without a context fails fast: " + join);
        TestKit.Check(plainContext != null && plainContext.Contains("message loop"), "a plain context (it posts to the thread pool) is refused too: " + plainContext);
        LanRoomTransport ok = LanRoomTransport.Host(5849, "X", "h");   // the port was not taken by the failed attempts
        ok.Close();
    }

    [STAThread]
    static int Main(string[] args)
    {
        TestKit.Guard();
        Application.EnableVisualStyles();
        mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        TestKit.InstallContext();
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
        TestHandshakeDeadline();
        TestHubClosesAtOnce();
        TestAcceptErrors();
        TestPeerEventOrder();
        TestExclusivePort();
        TestNeedsMessageLoop();
        TestKit.Check(offThread == 0, "every event was raised on the creating thread (" + offThread + " were not)");
        return TestKit.Finish("LAN ROOM TESTS");
    }
}
