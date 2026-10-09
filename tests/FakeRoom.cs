// An in-memory room transport for the tests: star topology like the LAN hub, asynchronous like the real ones.
using System;
using System.Collections.Generic;
using System.Threading;
using ChessGame;

sealed class FakeRoomHub
{
    public readonly List<FakeRoomTransport> Nodes = new List<FakeRoomTransport>();
    public FakeRoomTransport Host;
    int nextId = 1;

    public string NewId() { return "p" + (nextId++); }
}

sealed class FakeRoomTransport : IRoomTransport, IOwnershipTransport, IListingTransport
{
    readonly FakeRoomHub hub;
    readonly SynchronizationContext ui = SynchronizationContext.Current;
    readonly string name;
    string myId = "";
    string hostId = "";
    bool closed;
    bool started;
    readonly bool hostRole;

    public int ListedPlayers = -1, ListedWatchers = -1;
    /// <summary>When set, lines sent to the host are silently lost (to simulate a dead link in tests).</summary>
    public bool DropOutgoing;

    public event Action Connected;
    public event Action<string, string> PeerConnected;
    public event Action<string> PeerDisconnected;
    public event Action<string, string> LineReceived;
    public event Action<string> Closed;

    FakeRoomTransport(FakeRoomHub hub, string name, bool host, string hubId)
    {
        this.hub = hub;
        this.name = name;
        hostRole = host;
        if (host) { myId = hubId; hostId = hubId; }
    }

    public static FakeRoomTransport Host(FakeRoomHub hub, string name, string hubId)
    {
        FakeRoomTransport t = new FakeRoomTransport(hub, name, true, hubId);
        hub.Host = t;
        return t;
    }

    public static FakeRoomTransport Join(FakeRoomHub hub, string name)
    {
        return new FakeRoomTransport(hub, name, false, null);
    }

    public bool IsHost { get { return myId.Length > 0 && myId == hostId; } }
    public string MyId { get { return myId; } }
    public string HostId { get { return hostId; } }
    public string MyName { get { return name; } }
    public Func<string, string, bool> RelayFilter { get; set; }

    void Later(Action a) { ui.Post(delegate(object s) { if (!closed) a(); }, null); }

    public void Start()
    {
        if (started) return;
        started = true;
        if (hostRole)
        {
            hub.Nodes.Add(this);
            Later(delegate { if (Connected != null) Connected(); });
        }
        else
        {
            FakeRoomTransport h = hub.Host;
            if (h == null || h.closed) { Later(delegate { if (Closed != null) Closed("Не удалось подключиться: никого нет"); }); return; }
            myId = hub.NewId();
            hostId = h.myId;
            hub.Nodes.Add(this);
            string id = myId;
            Later(delegate { if (Connected != null) Connected(); });
            h.Later(delegate { if (h.PeerConnected != null) h.PeerConnected(id, name); });
        }
    }

    FakeRoomTransport Find(string id)
    {
        foreach (FakeRoomTransport n in hub.Nodes) if (n.myId == id && !n.closed) return n;
        return null;
    }

    FakeRoomTransport CurrentHub { get { return Find(hostId); } }

    void Deliver(FakeRoomTransport to, string from, string line)
    {
        to.Later(delegate { if (to.LineReceived != null) to.LineReceived(from, line); });
    }

    public void Broadcast(string line)
    {
        if (closed) return;
        string me = myId;
        // like a Steam lobby: everybody hears everybody, nothing is relayed or filtered on the way
        if (!IsHost && DropOutgoing) return;
        foreach (FakeRoomTransport n in hub.Nodes.ToArray()) if (n != this && !n.closed) Deliver(n, me, line);
    }

    public void SendTo(string id, string line)
    {
        if (closed) return;
        FakeRoomTransport to = Find(id);
        if (to == null || to == this) return;
        if (!IsHost && DropOutgoing) return;
        Deliver(to, myId, line);
    }

    public void Disconnect(string id)
    {
        FakeRoomTransport n = Find(id);
        if (n == null || !IsHost) return;
        n.closed = true;
        hub.Nodes.Remove(n);
        FakeRoomTransport target = n;
        Later(delegate { if (PeerDisconnected != null) PeerDisconnected(target.myId); });
    }

    public string AddressOf(string id) { return "127.0.0.1"; }

    public void Close()
    {
        if (closed) return;
        closed = true;
        hub.Nodes.Remove(this);
        if (IsHost)
        {
            foreach (FakeRoomTransport n in hub.Nodes.ToArray())
                n.Later(delegate { if (n.Closed != null) n.Closed("Хост закрыл комнату"); });
        }
        else
        {
            FakeRoomTransport h = CurrentHub;
            string id = myId;
            if (h != null) h.Later(delegate { if (h.PeerDisconnected != null) h.PeerDisconnected(id); });
        }
    }

    public void Dispose() { Close(); }

    // ---- Steam-like instant ownership change

    /// <summary>Set to make the platform refuse the hand-over.</summary>
    public bool RefuseOwnership;

    public bool TransferOwnership(string newHostId)
    {
        if (!IsHost || RefuseOwnership) return false;
        FakeRoomTransport target = Find(newHostId);
        if (target == null) return false;
        hub.Host = target;   // the lobby has a new owner; every member learns the name from the room (NoteHost)
        return true;
    }

    public void NoteHost(string id) { hostId = id; }

    public void SetListing(int players, int watchers) { ListedPlayers = players; ListedWatchers = watchers; }
}
