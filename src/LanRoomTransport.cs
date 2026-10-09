using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChessGame
{
    /// <summary>
    /// A room on the local network: one hub (TcpListener) and up to ten clients, plain text lines over TCP.
    /// client -> hub: "CHESS 2 name\tkey" first, then "B line" (broadcast) or "T toId line" (private).
    /// hub -> client: "WELCOME id hubId" | "FULL" | "BAD" | "BADVERSION", then "F fromId line".
    /// Hub logic (RelayFilter, delivery, relaying) runs on the creating (UI) thread, like every event.
    /// </summary>
    public sealed class LanRoomTransport : IRoomTransport, IRelocatableTransport
    {
        public const int MaxClients = 10;
        const int MaxLine = 2048;
        const int MaxClientSideLine = MaxLine + 64;   // the hub adds "F id " in front
        const int FloodLines = 250;                   // per connection per second
        const int HandshakeMs = 6000;
        const int MaxQueued = 8000;                   // lines waiting for a slow connection
        const int MaxPendingGreetings = 32;
        const int MaxRememberedKeys = 500;
        const int FlushGraceMs = 400;
        const int ProbeMs = 3000;
        const string Greeting = "CHESS 2";   // version 1 is the two-player NetLink protocol
        const string MoveFailed = "Не удалось перейти к новому хосту";

        readonly SynchronizationContext ui;
        readonly object gate = new object();
        readonly string myName;
        readonly string myKey;

        // state guarded by 'gate' (the strings and flags are volatile because the properties read them without it)
        volatile string myId;
        volatile string hostId;
        volatile bool isHost;
        string previousHostId;   // the hub we were a client of before BecomeHub (needed if the hand-over is abandoned)
        bool connectedRaised;
        TcpListener listener;
        Link up;
        int upGen;
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        readonly Dictionary<string, string> keyToId = new Dictionary<string, string>();
        readonly Dictionary<string, string> idToKey = new Dictionary<string, string>();
        int counter;
        int pending;

        int closed;
        int started;
        ThreadStart starter;

        public event Action Connected;
        public event Action<string, string> PeerConnected;
        public event Action<string> PeerDisconnected;
        public event Action<string, string> LineReceived;
        public event Action<string> Closed;

        public Func<string, string, bool> RelayFilter { get; set; }

        LanRoomTransport(string name, string key)
        {
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            myName = NetLink.Sanitize(name);
            string k = CleanKey(key);
            myKey = k.Length > 0 ? k : Guid.NewGuid().ToString("N");
        }

        public bool IsHost { get { return isHost; } }
        public string MyId { get { return myId; } }
        public string HostId { get { return hostId; } }
        public string MyName { get { return myName; } }

        bool IsClosed
        {
            get { return Interlocked.CompareExchange(ref closed, 0, 0) != 0; }
        }

        // ------------------------------------------------------------ setup

        /// <summary>Binds the port now (throws SocketException if it is busy); accepting starts with Start().</summary>
        public static LanRoomTransport Host(int port, string name, string hubId)
        {
            LanRoomTransport t = new LanRoomTransport(name, null);
            t.myId = t.hostId = hubId;
            t.isHost = true;
            t.connectedRaised = true;   // the hub's Connected is raised by the accept thread
            TcpListener l = new TcpListener(IPAddress.Any, port);
            l.Start();
            t.listener = l;
            t.starter = delegate { t.Raise(delegate { t.RaiseConnected(); }); t.AcceptLoop(l); };
            return t;
        }

        /// <summary>Connects in the background after Start(); Connected or Closed follows. 'key' keeps our id across reconnects.</summary>
        public static LanRoomTransport Join(string address, int port, string name, string key)
        {
            LanRoomTransport t = new LanRoomTransport(name, key);
            int gen = ++t.upGen;
            t.starter = delegate { t.ClientThread(gen, address, port, false); };
            return t;
        }

        public void Start()
        {
            if (starter == null || Interlocked.Exchange(ref started, 1) != 0) return;
            Thread t = new Thread(starter);
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------ hub: accepting

        void AcceptLoop(TcpListener l)
        {
            while (true)
            {
                TcpClient c;
                try
                {
                    c = l.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (IsClosed || !IsCurrentListener(l)) return;   // stopped on purpose
                    Fail("Ожидание соединений прервано");
                    return;
                }
                if (Interlocked.Increment(ref pending) > MaxPendingGreetings)
                {
                    Interlocked.Decrement(ref pending);
                    try { c.Close(); } catch (Exception) { }
                    continue;
                }
                TcpClient client = c;
                Thread t = new Thread(delegate() { PeerThread(client); });
                t.IsBackground = true;
                t.Start();
            }
        }

        bool IsCurrentListener(TcpListener l)
        {
            lock (gate) { return listener == l; }
        }

        void PeerThread(TcpClient c)
        {
            Peer peer = null;
            bool greeting = true;
            try
            {
                c.NoDelay = true;
                KeepAlive(c);
                string address = "";
                try { address = RemoteAddress(c); } catch (Exception) { }
                c.ReceiveTimeout = HandshakeMs;
                StreamReader reader = new StreamReader(c.GetStream(), Encoding.UTF8);

                string first = ReadLine(reader, MaxLine);
                string name, key;
                if (first == null)
                {
                    // a probe (ProbeHub) or a port scanner: opened and closed without a word
                    try { c.Close(); } catch (Exception) { }
                    return;
                }
                if (!ParseHello(first, out name, out key))
                {
                    bool otherVersion = first.StartsWith("CHESS ", StringComparison.Ordinal) || first == "CHESS";
                    Reject(c, otherVersion ? "BADVERSION" : "BAD");
                    return;
                }

                Peer replaced;
                bool full;
                peer = Register(c, name, key, address, out replaced, out full);
                if (peer == null)
                {
                    if (full) Reject(c, "FULL");
                    else { try { c.Close(); } catch (Exception) { } }
                    return;
                }
                c.ReceiveTimeout = 0;
                greeting = false;
                Interlocked.Decrement(ref pending);

                // an old connection of the same client (it reconnected before we noticed the loss) ends first
                if (replaced != null) EndPeer(replaced, true);
                Peer p = peer;
                Post(delegate { RaisePeerConnected(p.Id, p.Name); });

                int count = 0;
                int windowStart = Environment.TickCount;
                string line;
                while ((line = ReadLine(reader, MaxLine)) != null)
                {
                    if (Interlocked.CompareExchange(ref p.Ended, 0, 0) != 0) break;
                    int now = Environment.TickCount;
                    if (now - windowStart >= 1000) { windowStart = now; count = 0; }
                    if (++count > FloodLines) break;   // flooding: drop the connection
                    Dispatch(p, line);
                }
            }
            catch (Exception)
            {
                // a broken or hostile connection simply ends
            }
            finally
            {
                if (greeting) Interlocked.Decrement(ref pending);
                if (peer != null) EndPeer(peer, true);
                else { try { c.Close(); } catch (Exception) { } }
            }
        }

        static void Reject(TcpClient c, string answer)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(answer + "\n");
                c.GetStream().Write(b, 0, b.Length);
                c.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception) { }
            try { c.Close(); } catch (Exception) { }
        }

        static bool ParseHello(string line, out string name, out string key)
        {
            name = null; key = null;
            if (!line.StartsWith(Greeting + " ", StringComparison.Ordinal)) return false;
            string rest = line.Substring(Greeting.Length + 1);
            int tab = rest.IndexOf('\t');
            string n = tab < 0 ? rest : rest.Substring(0, tab);
            key = tab < 0 ? "" : CleanKey(rest.Substring(tab + 1));
            name = NetLink.Sanitize(n);
            if (name.Length == 0) name = "Игрок";
            return true;
        }

        /// <summary>Assigns an id (the old one for a known key), enqueues WELCOME and adds the peer. Null: full or not a hub any more.</summary>
        Peer Register(TcpClient c, string name, string key, string address, out Peer replaced, out bool full)
        {
            replaced = null;
            full = false;
            lock (gate)
            {
                if (IsClosed || !isHost || listener == null) return null;

                string id = null;
                if (key.Length > 0)
                {
                    string known;
                    if (keyToId.TryGetValue(key, out known) && known != myId && key != myKey) id = known;
                }
                Peer old = null;
                if (id != null) peers.TryGetValue(id, out old);
                if (old == null && peers.Count >= MaxClients) { full = true; return null; }

                if (id == null)
                {
                    do { counter++; id = "p" + counter; }
                    while (id == myId || id == hostId || idToKey.ContainsKey(id) || peers.ContainsKey(id));
                    if (key.Length > 0 && keyToId.Count < MaxRememberedKeys)
                    {
                        keyToId[key] = id;
                        idToKey[id] = key;
                    }
                }

                Peer peer = new Peer();
                peer.Id = id;
                peer.Name = name;
                peer.Key = key;
                peer.Address = address;
                peer.Link = new Link(c, delegate(Link l) { EndPeer(peer, true); });
                peer.Link.Send("WELCOME " + id + " " + myId);
                peers[id] = peer;
                replaced = old;
                return peer;
            }
        }

        void Dispatch(Peer p, string line)
        {
            if (line.Length < 3 || line[1] != ' ') return;
            string from = p.Id;
            if (line[0] == 'B')
            {
                string text = line.Substring(2);
                Post(delegate { HubBroadcast(from, text); });
            }
            else if (line[0] == 'T')
            {
                string rest = line.Substring(2);
                int sp = rest.IndexOf(' ');
                if (sp <= 0 || sp == rest.Length - 1) return;
                string to = rest.Substring(0, sp);
                string text = rest.Substring(sp + 1);
                if (to == from) return;
                Post(delegate { HubPrivate(from, to, text); });
            }
            // anything else is ignored
        }

        // these two run on the UI thread
        void HubBroadcast(string from, string text)
        {
            bool relay = true;
            Func<string, string, bool> filter = RelayFilter;
            if (filter != null)
            {
                try { relay = filter(from, text); }
                catch (Exception) { relay = true; }
            }
            RaiseLine(from, text);
            if (relay) SendToPeers(null, from, text, from);
        }

        void HubPrivate(string from, string to, string text)
        {
            if (to == myId) { RaiseLine(from, text); return; }
            SendToPeers(to, from, text, from);
        }

        /// <summary>Sends "F from line" to one peer (only != null) or to everybody except 'skip'.</summary>
        bool SendToPeers(string only, string from, string text, string skip)
        {
            string wire = "F " + from + " " + Cut(text, MaxLine);
            List<Peer> targets = new List<Peer>();
            lock (gate)
            {
                if (only != null)
                {
                    Peer p;
                    if (peers.TryGetValue(only, out p)) targets.Add(p);
                }
                else
                {
                    foreach (Peer p in peers.Values)
                        if (p.Id != skip) targets.Add(p);
                }
            }
            foreach (Peer p in targets) p.Link.Send(wire);
            return targets.Count > 0;
        }

        /// <summary>Ends one connection exactly once; PeerDisconnected unless 'announce' is false.</summary>
        void EndPeer(Peer p, bool announce)
        {
            if (Interlocked.Exchange(ref p.Ended, 1) != 0) return;
            lock (gate)
            {
                Peer cur;
                if (peers.TryGetValue(p.Id, out cur) && cur == p) peers.Remove(p.Id);
            }
            p.Link.Close();
            if (announce) { string id = p.Id; Post(delegate { RaisePeerDisconnected(id); }); }
        }

        // ------------------------------------------------------------ client

        void ClientThread(int gen, string address, int port, bool moving)
        {
            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(HandshakeMs))
                {
                    c.Close();
                    FailUp(gen, moving ? MoveFailed : "Не удалось подключиться: сервер не отвечает");
                    return;
                }
                c.EndConnect(ar);
            }
            catch (Exception ex)
            {
                try { c.Close(); } catch (Exception) { }
                FailUp(gen, moving ? MoveFailed : "Не удалось подключиться: " + ex.Message);
                return;
            }

            bool welcomed = false;
            try
            {
                Link link;
                lock (gate)
                {
                    if (IsClosed || gen != upGen) { c.Close(); return; }
                    c.NoDelay = true;
                    KeepAlive(c);
                    link = new Link(c, delegate(Link l)
                    {
                        FailUp(gen, welcomed ? "Соединение потеряно" : (moving ? MoveFailed : "Не удалось подключиться: соединение потеряно"));
                    });
                    up = link;
                }
                link.Send(Greeting + " " + myName + "\t" + myKey);

                c.ReceiveTimeout = HandshakeMs;
                StreamReader reader = new StreamReader(c.GetStream(), Encoding.UTF8);
                string first = ReadLine(reader, MaxClientSideLine);
                string[] w = first == null ? new string[0] : first.Split(' ');
                if (w.Length == 3 && w[0] == "WELCOME" && ValidId(w[1]) && ValidId(w[2]))
                {
                    bool raise;
                    lock (gate)
                    {
                        if (IsClosed || gen != upGen) return;
                        myId = w[1];
                        hostId = w[2];
                        raise = !connectedRaised;
                        connectedRaised = true;
                    }
                    welcomed = true;
                    c.ReceiveTimeout = 0;
                    if (raise) Post(delegate { RaiseConnected(); });
                }
                else
                {
                    string why;
                    if (first == "FULL") why = "комната заполнена";
                    else if (first == "BAD") why = "хост не принял соединение";
                    else if (first == "BADVERSION" || first == "CHESS" || first.StartsWith("CHESS ", StringComparison.Ordinal))
                        why = "у хоста другая версия программы, обновите её";
                    else if (first == null) why = "хост закрыл соединение";
                    else why = "это не шахматная комната";
                    FailUp(gen, moving ? MoveFailed : "Не удалось подключиться: " + why);
                    return;
                }

                string line;
                while ((line = ReadLine(reader, MaxClientSideLine)) != null)
                {
                    if (gen != upGen) return;   // we moved on to another hub
                    if (line.Length < 3 || line[0] != 'F' || line[1] != ' ') continue;
                    string rest = line.Substring(2);
                    int sp = rest.IndexOf(' ');
                    string from = sp < 0 ? rest : rest.Substring(0, sp);
                    string text = sp < 0 ? "" : rest.Substring(sp + 1);
                    if (!ValidId(from)) continue;
                    Post(delegate { RaiseLine(from, text); });
                }
                FailUp(gen, "Хост закрыл комнату");
            }
            catch (InvalidDataException)
            {
                FailUp(gen, welcomed ? "Хост прислал некорректные данные" : (moving ? MoveFailed : "Не удалось подключиться: это не шахматная комната"));
            }
            catch (IOException)
            {
                FailUp(gen, welcomed ? "Соединение потеряно" : (moving ? MoveFailed : "Не удалось подключиться: сервер не отвечает"));
            }
            catch (Exception)
            {
                FailUp(gen, welcomed ? "Соединение потеряно" : (moving ? MoveFailed : "Не удалось подключиться: соединение потеряно"));
            }
        }

        /// <summary>The hub connection of generation 'gen' ended. An outdated one is ignored; a hub-in-the-making just forgets it.</summary>
        void FailUp(int gen, string reason)
        {
            Link old = null;
            lock (gate)
            {
                if (gen != upGen || IsClosed) return;
                if (isHost)
                {
                    // we are the new hub already; the old hub going away is expected
                    upGen++;
                    old = up;
                    up = null;
                }
            }
            if (old != null || isHost)
            {
                if (old != null) old.Close();
                return;
            }
            Fail(reason);
        }

        // ------------------------------------------------------------ sending

        public void Broadcast(string line)
        {
            if (line == null || IsClosed) return;
            line = OneLine(line);
            if (isHost) SendToPeers(null, myId, line, null);
            // a client always sends upstream; a new hub does too while the old hub is still connected (handover)
            Link l = up;
            if (l != null) l.Send("B " + Cut(line, MaxLine - 2));
        }

        public void SendTo(string id, string line)
        {
            if (line == null || IsClosed || !ValidId(id) || id == myId) return;
            line = OneLine(line);
            // a new hub talks to its own clients first; an unknown id may be the old hub, still reachable upstream
            if (isHost && SendToPeers(id, myId, line, null)) return;
            Link l = up;
            if (l != null) l.Send("T " + id + " " + Cut(line, MaxLine - 3 - id.Length));
        }

        public void Disconnect(string id)
        {
            Peer p;
            lock (gate)
            {
                if (!isHost || id == null || !peers.TryGetValue(id, out p)) return;
            }
            EndPeer(p, true);
        }

        public string AddressOf(string id)
        {
            lock (gate)
            {
                Peer p;
                if (isHost && id != null && peers.TryGetValue(id, out p)) return p.Address;
            }
            return null;
        }

        // ------------------------------------------------------------ closing

        void Fail(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            Cleanup();
            ui.Post(delegate(object state) { RaiseClosed(reason); }, null);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            Cleanup();
        }

        public void Dispose()
        {
            Close();
        }

        void Cleanup()
        {
            TcpListener l;
            Link u;
            List<Peer> all;
            lock (gate)
            {
                l = listener;
                listener = null;
                u = up;
                up = null;
                upGen++;
                all = new List<Peer>(peers.Values);
            }
            try { if (l != null) l.Stop(); } catch (Exception) { }
            if (u != null) u.Close();
            foreach (Peer p in all) EndPeer(p, false);
        }

        // ------------------------------------------------------------ handover (IRelocatableTransport)

        public IDictionary<string, string> ExportIdentities()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            lock (gate)
            {
                if (!isHost) return result;
                foreach (Peer p in peers.Values)
                    if (p.Key.Length > 0) result[p.Id] = p.Key;
                // the hub itself comes back as an ordinary client of the next hub and must keep its id
                result[myId] = myKey;
            }
            return result;
        }

        public int BecomeHub(IDictionary<string, string> identities, int preferredPort)
        {
            TcpListener l;
            lock (gate)
            {
                if (IsClosed || isHost || up == null || myId == null)
                    throw new InvalidOperationException("only a connected client can become the hub");
                l = new TcpListener(IPAddress.Any, preferredPort);
                l.Start();   // a busy port throws before anything changes

                keyToId.Clear();
                idToKey.Clear();
                counter = NumberOf(myId);
                if (identities != null)
                {
                    foreach (KeyValuePair<string, string> kv in identities)
                    {
                        string key = CleanKey(kv.Value);
                        if (!ValidId(kv.Key) || key.Length == 0) continue;
                        if (kv.Key == myId || key == myKey) continue;   // we are not a client of ourselves
                        keyToId[key] = kv.Key;
                        idToKey[kv.Key] = key;
                        counter = Math.Max(counter, NumberOf(kv.Key));
                    }
                }
                listener = l;
                isHost = true;
                previousHostId = hostId;
                hostId = myId;
            }
            Thread t = new Thread(delegate() { AcceptLoop(l); });
            t.IsBackground = true;
            t.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        public void AbandonHub()
        {
            TcpListener l;
            List<Peer> all;
            lock (gate)
            {
                // only a half-hub counts: a client that started a hub and still has its upstream link to the old one
                if (IsClosed || !isHost || up == null) return;
                l = listener;
                listener = null;
                isHost = false;
                hostId = previousHostId ?? hostId;
                all = new List<Peer>(peers.Values);
            }
            try { if (l != null) l.Stop(); } catch (Exception) { }
            foreach (Peer p in all) EndPeer(p, false);
        }

        public void DropUpstream()
        {
            Link old;
            lock (gate)
            {
                if (!isHost || up == null) return;
                upGen++;
                old = up;
                up = null;
            }
            old.Close();
        }

        public void MoveTo(string address, int port)
        {
            bool hub;
            int gen;
            Link old = null;
            TcpListener l = null;
            lock (gate)
            {
                if (IsClosed) return;
                hub = isHost;
                gen = ++upGen;   // outdates the current hub connection (and any attempt in progress)
                if (hub)
                {
                    l = listener;
                    listener = null;   // no more greetings are accepted
                }
                else
                {
                    old = up;
                    up = null;
                }
            }

            if (!hub)
            {
                if (old != null) old.Close();
                StartClient(gen, address, port);
                return;
            }

            try { if (l != null) l.Stop(); } catch (Exception) { }
            // let the queued relays reach their clients, then drop them quietly and follow the new hub
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(FlushGraceMs);
                List<Peer> all;
                Link upstream;
                lock (gate)
                {
                    if (IsClosed || gen != upGen) return;
                    all = new List<Peer>(peers.Values);
                    upstream = up;   // a hub that had an upstream of its own (handover not finished) loses it too
                    up = null;
                    isHost = false;
                }
                foreach (Peer p in all) EndPeer(p, false);
                if (upstream != null) upstream.Close();
                ClientThread(gen, address, port, true);
            });
            t.IsBackground = true;
            t.Start();
        }

        void StartClient(int gen, string address, int port)
        {
            Thread t = new Thread(delegate() { ClientThread(gen, address, port, true); });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Plain TCP connect, nothing is sent; done(true/false) comes on the UI thread (about 3 s at most).</summary>
        public void ProbeHub(string address, int port, Action<bool> done)
        {
            Thread t = new Thread(delegate()
            {
                bool ok = false;
                TcpClient c = new TcpClient();
                try
                {
                    IAsyncResult ar = c.BeginConnect(address, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(ProbeMs))
                    {
                        c.EndConnect(ar);
                        ok = true;
                    }
                }
                catch (Exception) { }
                try { c.Close(); } catch (Exception) { }
                if (done != null) ui.Post(delegate(object state) { done(ok); }, null);
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------ events (UI thread)

        void Post(Action action)
        {
            ui.Post(delegate(object state) { if (!IsClosed) action(); }, null);
        }

        void Raise(Action action)
        {
            Post(action);
        }

        void RaiseConnected() { if (Connected != null) Connected(); }
        void RaisePeerConnected(string id, string name) { if (PeerConnected != null) PeerConnected(id, name); }
        void RaisePeerDisconnected(string id) { if (PeerDisconnected != null) PeerDisconnected(id); }
        void RaiseLine(string from, string text) { if (LineReceived != null) LineReceived(from, text); }
        void RaiseClosed(string reason) { if (Closed != null) Closed(reason); }

        // ------------------------------------------------------------ helpers

        /// <summary>Reads one '\n'-terminated line of bounded length. Returns null at end of stream.</summary>
        static string ReadLine(StreamReader reader, int max)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int ch = reader.Read();
                if (ch < 0) return null;
                if (ch == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)ch);
                if (sb.Length > max) throw new InvalidDataException("line too long");
            }
        }

        static string OneLine(string s)
        {
            return s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0 ? s : s.Replace('\r', ' ').Replace('\n', ' ');
        }

        static string Cut(string s, int max)
        {
            if (max < 0) max = 0;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        static string CleanKey(string key)
        {
            if (key == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in key)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                if (sb.Length >= 40) break;
            }
            return sb.ToString();
        }

        static bool ValidId(string id)
        {
            if (id == null || id.Length == 0 || id.Length > 16) return false;
            foreach (char ch in id)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')) return false;
            return true;
        }

        /// <summary>The N of "pN", otherwise 0.</summary>
        static int NumberOf(string id)
        {
            int n;
            if (id != null && id.Length > 1 && id[0] == 'p' && int.TryParse(id.Substring(1), out n) && n > 0) return n;
            return 0;
        }

        static string RemoteAddress(TcpClient c)
        {
            IPAddress a = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            return a.ToString();
        }

        /// <summary>A silently dead peer (cable pulled, machine off) is noticed in about half a minute.</summary>
        static void KeepAlive(TcpClient c)
        {
            try
            {
                byte[] v = new byte[12];
                BitConverter.GetBytes(1).CopyTo(v, 0);
                BitConverter.GetBytes(10000).CopyTo(v, 4);
                BitConverter.GetBytes(3000).CopyTo(v, 8);
                c.Client.IOControl(IOControlCode.KeepAliveValues, v, null);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------ types

        sealed class Peer
        {
            public string Id;
            public string Name;
            public string Key;
            public string Address;
            public Link Link;
            public int Ended;
        }

        /// <summary>
        /// One TCP connection with its own sender thread, so that a slow receiver never blocks the others (or the UI).
        /// Send() only queues; a failed write closes the link and calls 'failed' once.
        /// </summary>
        sealed class Link
        {
            readonly TcpClient tcp;
            readonly Queue<string> queue = new Queue<string>();
            readonly Action<Link> failed;
            bool dead;

            public Link(TcpClient c, Action<Link> onFailed)
            {
                tcp = c;
                failed = onFailed;
                Thread t = new Thread(WriteLoop);
                t.IsBackground = true;
                t.Start();
            }

            public void Send(string line)
            {
                bool overflow = false;
                lock (queue)
                {
                    if (dead) return;
                    if (queue.Count >= MaxQueued) overflow = true;
                    else
                    {
                        queue.Enqueue(line);
                        Monitor.Pulse(queue);
                    }
                }
                if (overflow) Fail();
            }

            void WriteLoop()
            {
                try
                {
                    NetworkStream s = tcp.GetStream();
                    while (true)
                    {
                        string[] batch;
                        lock (queue)
                        {
                            while (queue.Count == 0 && !dead) Monitor.Wait(queue);
                            if (dead) return;
                            batch = queue.ToArray();
                            queue.Clear();
                        }
                        StringBuilder sb = new StringBuilder();
                        foreach (string line in batch) sb.Append(line).Append('\n');
                        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception)
                {
                    Fail();
                }
            }

            void Fail()
            {
                bool wasDead;
                lock (queue) { wasDead = dead; }
                Close();
                if (!wasDead && failed != null) failed(this);
            }

            public void Close()
            {
                lock (queue)
                {
                    if (dead) return;
                    dead = true;
                    queue.Clear();
                    Monitor.PulseAll(queue);
                }
                try { tcp.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                try { tcp.Close(); } catch (Exception) { }
            }
        }
    }
}
