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
        if (!hub.Lobbies.TryGetValue(lobby, out l) || !l.Members.Contains(this)) return false;
        foreach (FakeLobbyService m in l.Members.ToArray())   // everybody, the sender included
        {
            FakeLobbyService target = m;
            target.Later(delegate { if (target.ChatReceived != null) target.ChatReceived(lobby, MySteamId, text); });
        }
        return true;
    }
}
