// In-memory stand-in for Steam lobbies, used by the tests. It deliberately behaves like the real thing in
// the awkward ways: operations complete asynchronously and chat messages are echoed back to their sender.
using System;
using System.Collections.Generic;
using System.Threading;
using ChessGame;

sealed class FakeHub
{
    public sealed class Lobby
    {
        public ulong Id;
        public bool Joinable = true;
        public int Max = 2;
        public ulong Owner;
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        public readonly List<FakeLobbyService> Members = new List<FakeLobbyService>();
    }

    public readonly Dictionary<ulong, Lobby> Lobbies = new Dictionary<ulong, Lobby>();
    public readonly HashSet<string> Friends = new HashSet<string>();   // "a-b" pairs
    ulong nextId = 1000;
    ulong nextUser = 76561190000000000UL;

    public ulong NewLobbyId() { return nextId++; }
    public ulong NewUserId() { return nextUser++; }
    public bool AreFriends(ulong a, ulong b) { return Friends.Contains(a + "-" + b) || Friends.Contains(b + "-" + a); }
}

sealed class FakeLobbyService : ILobbyService
{
    readonly FakeHub hub;
    readonly SynchronizationContext ui = SynchronizationContext.Current;

    public ulong MySteamId { get; private set; }
    public string MyName { get; private set; }
    public event Action<ulong, ulong, string> ChatReceived;
    public event Action<ulong, ulong, bool> MemberChanged;

    /// <summary>When true the service silently drops chat (a peer that never answers).</summary>
    public bool Mute;

    /// <summary>When true SendChat reports failure although we are still in the lobby (Steam's rate limit or size refusal).</summary>
    public bool RejectChat;

    public FakeLobbyService(FakeHub hub, string name)
    {
        this.hub = hub;
        MyName = name;
        MySteamId = hub.NewUserId();
    }

    void Later(Action a)
    {
        ui.Post(delegate(object s) { a(); }, null);
    }

    public void CreateLobby(int maxMembers, Action<bool, ulong> done)
    {
        Later(delegate
        {
            FakeHub.Lobby l = new FakeHub.Lobby();
            l.Max = maxMembers;
            l.Owner = MySteamId;
            l.Id = hub.NewLobbyId();
            l.Members.Add(this);
            hub.Lobbies[l.Id] = l;
            done(true, l.Id);
        });
    }

    public void FindLobbies(Action<List<LobbyInfo>> done)
    {
        Later(delegate
        {
            List<LobbyInfo> list = new List<LobbyInfo>();
            foreach (FakeHub.Lobby l in hub.Lobbies.Values)
            {
                string v;
                if (!l.Data.TryGetValue(SteamLink.LobbyKey, out v) || v != SteamLink.LobbyValue) continue;   // the key filter
                if (l.Members.Contains(this)) continue;   // like the real list, never mind our own lobby
                if (!l.Joinable && !l.Members.Contains(this)) continue;
                LobbyInfo info = new LobbyInfo();
                info.Id = l.Id;
                l.Data.TryGetValue("host", out info.HostName);
                info.Members = l.Members.Count;
                info.IsFriend = hub.AreFriends(MySteamId, l.Members[0].MySteamId);
                string n;
                int count;
                if (l.Data.TryGetValue("players", out n) && int.TryParse(n, out count)) info.Players = count;
                if (l.Data.TryGetValue("watchers", out n) && int.TryParse(n, out count)) info.Watchers = count;
                list.Add(info);
            }
            done(list);
        });
    }

    public void JoinLobby(ulong lobby, Action<bool> done)
    {
        Later(delegate
        {
            FakeHub.Lobby l;
            if (!hub.Lobbies.TryGetValue(lobby, out l) || !l.Joinable || l.Members.Count >= l.Max) { done(false); return; }
            l.Members.Add(this);
            foreach (FakeLobbyService m in l.Members.ToArray())
            {
                FakeLobbyService target = m;
                target.Later(delegate { if (target.MemberChanged != null) target.MemberChanged(lobby, MySteamId, true); });
            }
            done(true);
        });
    }

    public void LeaveLobby(ulong lobby)
    {
        FakeHub.Lobby l;
        if (!hub.Lobbies.TryGetValue(lobby, out l) || !l.Members.Remove(this)) return;
        if (l.Members.Count == 0) hub.Lobbies.Remove(lobby);
        foreach (FakeLobbyService m in l.Members.ToArray())
        {
            FakeLobbyService target = m;
            target.Later(delegate { if (target.MemberChanged != null) target.MemberChanged(lobby, MySteamId, false); });
        }
    }

    public ulong[] GetMembers(ulong lobby)
    {
        FakeHub.Lobby l;
        if (!hub.Lobbies.TryGetValue(lobby, out l)) return new ulong[0];
        ulong[] ids = new ulong[l.Members.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = l.Members[i].MySteamId;
        return ids;
    }

    public ulong GetOwner(ulong lobby)
    {
        FakeHub.Lobby l;
        return hub.Lobbies.TryGetValue(lobby, out l) ? l.Owner : 0;
    }

    public bool SetOwner(ulong lobby, ulong newOwner)
    {
        FakeHub.Lobby l;
        if (!hub.Lobbies.TryGetValue(lobby, out l) || l.Owner != MySteamId) return false;
        foreach (FakeLobbyService m in l.Members) if (m.MySteamId == newOwner) { l.Owner = newOwner; return true; }
        return false;
    }

    public void SetLobbyData(ulong lobby, string key, string value)
    {
        FakeHub.Lobby l;
        if (hub.Lobbies.TryGetValue(lobby, out l)) l.Data[key] = value;
    }

    public void SetJoinable(ulong lobby, bool joinable)
    {
        FakeHub.Lobby l;
        if (hub.Lobbies.TryGetValue(lobby, out l)) l.Joinable = joinable;
    }

    public bool SendChat(ulong lobby, string text)
    {
        FakeHub.Lobby l;
        if (Mute) return true;
        if (RejectChat) return false;
        if (!hub.Lobbies.TryGetValue(lobby, out l) || !l.Members.Contains(this)) return false;
        foreach (FakeLobbyService m in l.Members.ToArray())   // everybody, the sender included
        {
            FakeLobbyService target = m;
            target.Later(delegate { if (target.ChatReceived != null) target.ChatReceived(lobby, MySteamId, text); });
        }
        return true;
    }
}

/// <summary>
/// Checks of SteamRoomTransport that need nothing but the fake lobbies: events leave through the UI queue, one rejected line does
/// not close the room, the byte limit of a chat message, a vanished lobby. Run() is called by the "steam" group of NetTests.
/// </summary>
static class SteamTransportChecks
{
    sealed class Side
    {
        public readonly FakeLobbyService Svc;
        public readonly SteamRoomTransport T;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Closed = new List<string>();
        public readonly List<string> Gone = new List<string>();
        public int Connected;
        public bool InsideCall;       // an event was raised while a call of ours was running
        public bool Calling;

        public Side(FakeHub hub, string name, bool host, ulong lobby)
        {
            Svc = new FakeLobbyService(hub, name);
            T = host ? SteamRoomTransport.Host(Svc, name) : SteamRoomTransport.Join(Svc, lobby, name);
            T.Connected += delegate { Note(); Connected++; };
            T.LineReceived += delegate(string from, string line) { Note(); Lines.Add(line); };
            T.PeerDisconnected += delegate(string id) { Note(); Gone.Add(id); };
            T.Closed += delegate(string reason) { Note(); Closed.Add(reason); };
            T.Start();
        }

        void Note() { if (Calling) InsideCall = true; }
    }

    public static void Run()
    {
        Console.WriteLine("Steam room transport: rejected lines, limits, event delivery");
        TestKit.InstallContext();
        FakeHub hub = new FakeHub();
        Side host = new Side(hub, "Host", true, 0);
        TestKit.Check(TestKit.WaitFor(delegate { return host.Connected == 1; }, 2000), "host is up");
        ulong lobby = 0;
        foreach (ulong id in hub.Lobbies.Keys) lobby = id;
        Side guest = new Side(hub, "Guest", false, lobby);
        TestKit.Check(TestKit.WaitFor(delegate { return guest.Connected == 1; }, 2000), "guest is in");

        // a message Steam refuses (rate limit) costs that line only
        guest.Svc.RejectChat = true;
        guest.T.Broadcast("lost line");
        TestKit.Pump(200);
        TestKit.Check(guest.Closed.Count == 0 && host.Lines.Count == 0, "one refused line does not close the room");
        guest.Svc.RejectChat = false;
        guest.T.Broadcast("next line");
        TestKit.Check(TestKit.WaitFor(delegate { return host.Lines.Count == 1; }, 2000) && host.Lines[0] == "next line", "and the room keeps working");

        // the limit is in bytes: 1300 three-byte characters fit, 1500 do not (although both are under 2048 characters)
        string fits = new string('\u20AC', 1300), tooBig = new string('\u20AC', 1500);
        guest.T.Broadcast(tooBig);
        guest.T.Broadcast(fits);
        TestKit.Check(TestKit.WaitFor(delegate { return host.Lines.Count == 2; }, 2000), "a line within the byte limit arrives");
        TestKit.Pump(200);
        TestKit.Check(host.Lines.Count == 2 && host.Lines[1] == fits && guest.Closed.Count == 0, "a line over the byte limit is dropped, the room stays: " + host.Lines.Count);

        // events are never raised inside the call that causes them
        host.Calling = true;
        host.T.Disconnect(guest.T.MyId);
        host.Calling = false;
        TestKit.Check(host.Gone.Count == 0, "Disconnect returns before PeerDisconnected is raised");
        TestKit.Check(TestKit.WaitFor(delegate { return host.Gone.Count >= 1; }, 2000) && !host.InsideCall, "and it arrives through the queue");
        TestKit.Check(TestKit.WaitFor(delegate { return guest.Closed.Count == 1; }, 2000), "the kicked member is told");

        // the lobby itself is gone: now the room is lost, and Closed is posted, not raised inside Broadcast
        Side g2 = new Side(hub, "G2", false, lobby);
        TestKit.Check(TestKit.WaitFor(delegate { return g2.Connected == 1; }, 2000), "a second guest joins");
        hub.Lobbies.Remove(lobby);
        g2.Calling = true;
        g2.T.Broadcast("anyone?");
        g2.Calling = false;
        TestKit.Check(g2.Closed.Count == 0, "Closed is not raised inside Broadcast");
        TestKit.Check(TestKit.WaitFor(delegate { return g2.Closed.Count == 1; }, 2000) && !g2.InsideCall, "a vanished lobby ends the room (after the call returned)");
        TestKit.Pump(100);
        TestKit.Check(g2.Closed.Count == 1, "exactly once");

        // no context, no transport
        string msg = null;
        System.Threading.Thread t = new System.Threading.Thread(delegate()
        {
            try { SteamRoomTransport.Host(new FakeLobbyService(hub, "X"), "X"); msg = "no exception"; }
            catch (InvalidOperationException ex) { msg = ex.Message; }
        });
        t.Start();
        t.Join();
        TestKit.Check(msg != null && msg.Contains("message loop"), "a thread without a UI context cannot create one: " + msg);
        host.T.Close(); guest.T.Close(); g2.T.Close();
        TestKit.Pump(50);
    }
}
