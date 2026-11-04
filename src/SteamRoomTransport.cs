using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ChessGame
{
    /// <summary>
    /// A room on top of one Steam lobby. Lobby chat is a broadcast that every member receives and whose sender Steam
    /// authenticates; the ids of this transport are the members' SteamIDs. Wire lines (inside lobby chat messages):
    ///   HELLO name       a member introduces itself (everybody learns the names)
    ///   B line           a broadcast
    ///   T id line        a private line (only member 'id' reacts)
    ///   T id !KICK       the host drops a member (the member leaves by itself)
    /// There is no relay: nobody forwards anything, so RelayFilter has no meaning here (receivers filter instead).
    /// Like the LAN transport, every event is posted to the context of the creating (UI) thread, never raised inside a call of ours.
    /// </summary>
    public sealed class SteamRoomTransport : IRoomTransport, IOwnershipTransport, IListingTransport
    {
        public const int MaxMembers = 10;
        const int MaxLine = 2048;
        const int MaxChatBytes = 4096;   // Steam refuses a lobby chat message larger than this

        readonly ILobbyService svc;
        readonly SynchronizationContext ui;
        readonly string myName;
        readonly bool hostMode;
        readonly ulong joinLobby;
        readonly HashSet<string> introduced = new HashSet<string>();   // members whose HELLO we have heard
        ulong lobby;
        string myId = "";
        string hostId = "";
        bool closed;
        bool started;

        public event Action Connected;
        public event Action<string, string> PeerConnected;
        public event Action<string> PeerDisconnected;
        public event Action<string, string> LineReceived;
        public event Action<string> Closed;

        SteamRoomTransport(ILobbyService service, string name, bool host, ulong joinId)
        {
            ui = UiContext.Capture("SteamRoomTransport");
            svc = service;
            myName = NetLink.Sanitize(name);
            hostMode = host;
            joinLobby = joinId;
            svc.ChatReceived += OnChat;
            svc.MemberChanged += OnMember;
        }

        /// <summary>Opens a lobby for a room of up to MaxMembers people.</summary>
        public static SteamRoomTransport Host(ILobbyService service, string name)
        {
            return new SteamRoomTransport(service, name, true, 0);
        }

        /// <summary>Joins somebody's lobby.</summary>
        public static SteamRoomTransport Join(ILobbyService service, ulong lobbyId, string name)
        {
            return new SteamRoomTransport(service, name, false, lobbyId);
        }

        public bool IsHost { get { return myId.Length > 0 && myId == hostId; } }
        public string MyId { get { return myId; } }
        public string HostId { get { return hostId; } }
        public string MyName { get { return myName; } }

        /// <summary>Not used: nothing is relayed through a lobby.</summary>
        public Func<string, string, bool> RelayFilter { get; set; }

        public void Start()
        {
            if (started || closed) return;
            started = true;
            myId = svc.MySteamId.ToString();
            if (hostMode)
            {
                svc.CreateLobby(MaxMembers, delegate(bool ok, ulong id)
                {
                    if (closed) { if (ok) svc.LeaveLobby(id); return; }
                    if (!ok) { Fail("Steam не смог создать лобби."); return; }
                    lobby = id;
                    hostId = myId;
                    svc.SetLobbyData(id, SteamLink.LobbyKey, SteamLink.LobbyValue);
                    svc.SetLobbyData(id, "host", myName);
                    svc.SetLobbyData(id, "players", "1");
                    svc.SetLobbyData(id, "watchers", "0");
                    Post(delegate { if (Connected != null) Connected(); });
                });
            }
            else
            {
                svc.JoinLobby(joinLobby, delegate(bool ok)
                {
                    if (closed) { if (ok) svc.LeaveLobby(joinLobby); return; }
                    if (!ok) { Fail("Не удалось войти в лобби: оно закрыто или заполнено."); return; }
                    lobby = joinLobby;
                    hostId = svc.GetOwner(lobby).ToString();
                    svc.SendChat(lobby, "HELLO " + myName);
                    Post(delegate { if (Connected != null) Connected(); });
                });
            }
        }

        // ---------------------------------------------------------------- sending

        public void Broadcast(string line)
        {
            Send("B " + line);
        }

        public void SendTo(string id, string line)
        {
            Send("T " + id + " " + line);
        }

        void Send(string text)
        {
            if (closed || lobby == 0 || text.Length > MaxLine + 24 || Encoding.UTF8.GetByteCount(text) > MaxChatBytes) return;   // too long: the line is dropped
            if (svc.SendChat(lobby, text)) return;
            // Steam refused this message (rate limit, size). That costs one line, not the room; the room is lost only if the lobby is
            if (Array.IndexOf(svc.GetMembers(lobby), svc.MySteamId) < 0) Fail("Соединение потеряно.");
        }

        public void Disconnect(string id)
        {
            if (!IsHost) return;
            SendTo(id, "!KICK");
            RaiseGone(id);
        }

        public string AddressOf(string id) { return null; }

        // ---------------------------------------------------------------- receiving

        void OnChat(ulong fromLobby, ulong sender, string text)
        {
            if (closed || fromLobby != lobby || sender == svc.MySteamId || text.Length > MaxLine + 24) return;
            string from = sender.ToString();

            if (text.StartsWith("HELLO ", StringComparison.Ordinal))
            {
                string name = NetLink.Sanitize(text.Substring(6));
                if (name.Length == 0) name = "Игрок";
                bool known = !introduced.Add(from);
                if (!known && IsHost) Post(delegate { if (PeerConnected != null) PeerConnected(from, name); });
                return;
            }
            // Who may do what is decided by the room (RoomSession) from the member list, not here: after the lobby changed hands the
            // new owner has not heard the introductions of the people who were already inside.
            if (text.StartsWith("B ", StringComparison.Ordinal))
            {
                string line = text.Substring(2);
                Post(delegate { if (LineReceived != null) LineReceived(from, line); });
            }
            else if (text.StartsWith("T ", StringComparison.Ordinal))
            {
                int space = text.IndexOf(' ', 2);
                if (space < 0 || text.Substring(2, space - 2) != myId) return;
                string rest = text.Substring(space + 1);
                if (rest == "!KICK")
                {
                    if (from == hostId) Fail("Хост исключил вас из комнаты.");
                    return;
                }
                Post(delegate { if (LineReceived != null) LineReceived(from, rest); });
            }
        }

        void OnMember(ulong inLobby, ulong member, bool present)
        {
            if (closed || inLobby != lobby || present) return;
            string id = member.ToString();
            if (id == hostId && !IsHost)
            {
                // ownership may have been handed over just now; only a really missing host closes the room
                if (Array.IndexOf(svc.GetMembers(lobby), member) < 0) Fail("Хост вышел из комнаты.");
                return;
            }
            RaiseGone(id);
        }

        void RaiseGone(string id)
        {
            introduced.Remove(id);
            // also for people whose introduction we never heard: after the lobby changed hands the new owner knows nobody by name,
            // and the room (RoomSession) tolerates ids it does not know
            if (IsHost) Post(delegate { if (PeerDisconnected != null) PeerDisconnected(id); });
        }

        // ---------------------------------------------------------------- ownership and listing

        public bool TransferOwnership(string newHostId)
        {
            if (!IsHost || closed || lobby == 0) return false;
            ulong id;
            if (!ulong.TryParse(newHostId, out id)) return false;
            return svc.SetOwner(lobby, id);
        }

        public void NoteHost(string newHostId)
        {
            hostId = newHostId ?? "";
        }

        public void SetListing(int players, int watchers)
        {
            if (closed || lobby == 0 || !IsHost) return;
            svc.SetLobbyData(lobby, "players", players.ToString());
            svc.SetLobbyData(lobby, "watchers", watchers.ToString());
            svc.SetLobbyData(lobby, "host", myName);
            svc.SetJoinable(lobby, svc.GetMembers(lobby).Length < MaxMembers);
        }

        // ---------------------------------------------------------------- shutdown

        void Fail(string reason)
        {
            if (closed) return;
            Shutdown();
            ui.Post(delegate(object state) { if (Closed != null) Closed(reason); }, null);   // after Shutdown, so not through Post
        }

        /// <summary>Events leave through the UI queue, in order, and not at all once the transport is closed.</summary>
        void Post(Action action)
        {
            ui.Post(delegate(object state) { if (!closed) action(); }, null);
        }

        public void Close()
        {
            if (closed) return;
            Shutdown();
        }

        void Shutdown()
        {
            closed = true;
            svc.ChatReceived -= OnChat;
            svc.MemberChanged -= OnMember;
            if (lobby != 0) svc.LeaveLobby(lobby);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
