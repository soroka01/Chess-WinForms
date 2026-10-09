using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace ChessGame
{
    public enum Seat { None = 0, White = 1, Black = 2 }

    public sealed class RoomMember
    {
        public string Id = "";
        public string Name = "";
        public Seat Seat;
        public bool ChatMuted;
        public bool VoiceMuted;

        public RoomMember Copy()
        {
            RoomMember m = new RoomMember();
            m.Id = Id; m.Name = Name; m.Seat = Seat; m.ChatMuted = ChatMuted; m.VoiceMuted = VoiceMuted;
            return m;
        }
    }

    /// <summary>
    /// The social side of a network game, independent of the user interface: who is in the room, who sits where, who may talk,
    /// who is the host, how people are let in and how the host role moves. The host is the single writer of the replicated state
    /// (the ROOM line) and of everything that changes the game: players send their requests to the host only, the host validates
    /// them and broadcasts the result, so every replica sees the same order of events. Chat and voice are the only lines that
    /// members broadcast themselves; they are filtered by the replicated permissions at every receiver (and by the LAN hub).
    ///
    /// Lines handled here:  REQ JOIN play|watch, REQ SEAT W|B|ANY, REQ STAND  (to the host)   WELCOME, REJECTED text, KICKED  (from the host)
    ///                      ROOM rev host flags members   (broadcast by the host)   XFER ASK|OK|NO|GO   (LAN host hand-over)
    /// Verbs for the game layer:  to the host: MOVE RESIGN DRAW DRAW_YES DRAW_NO NEW NEW_YES NEW_NO (from seat holders only), RESYNC (any member)
    ///                            from the host: PLAYED RESULT OFFER NEWGAME SYNC      from anybody: CHAT VOICE (if permitted)
    /// </summary>
    public sealed class RoomSession : IDisposable
    {
        public const int MaxMembers = 10;
        const int DeferMs = 3000;

        sealed class PendingPeer
        {
            public string Name = "";
            public bool Asked;
            public DateTime Since = DateTime.UtcNow;
        }

        sealed class Deferred
        {
            public string From, Verb, Line;
            public DateTime Expires;
        }

        enum Auth { Allow, Deny, Defer }

        readonly IRoomTransport t;
        readonly string myName;
        readonly bool wantsToPlay;
        readonly Seat startSeat;
        readonly Timer housekeeping = new Timer();
        readonly List<RoomMember> members = new List<RoomMember>();
        readonly Dictionary<string, PendingPeer> pending = new Dictionary<string, PendingPeer>();
        readonly List<Deferred> deferred = new List<Deferred>();
        readonly Dictionary<string, Seat> seatMemory = new Dictionary<string, Seat>();   // host: where a member sat when it left or stood up
        readonly HashSet<string> banned = new HashSet<string>();                          // host: removed by the host, may not come back

        int rev;
        string hostId = "";
        bool specChat = true, specVoice = true;
        bool ready;
        bool welcomed;
        bool closed;
        bool transportClosed;
        bool started;
        DateTime waitingSince = DateTime.UtcNow;

        // LAN host hand-over
        string xferTarget;                 // old host: the member we are handing over to
        bool xferAsking;
        DateTime xferStarted;
        string xferNewHost;                // everybody: the member that is about to become host (its ROOM is accepted)
        DateTime xferDeadline;
        string offerFrom, offerTable;      // a pending offer to become host
        string offerName;
        bool hubOpen;                      // we said yes and started a hub; it is dropped again if the hand-over does not happen
        DateTime hubOpenSince;
        DateTime lastListing = DateTime.UtcNow;
        HashSet<string> returning;         // new host: members that have not reconnected yet
        DateTime returningDeadline;
        bool movingAway;                   // old host: our hub is closing, we are a client now

        public event Action Changed;
        public event Action Welcomed;
        public event Action<string, string, bool> JoinRequested;   // host: id, name, wants to play
        public event Action<string, Seat> SeatRequested;           // host: a spectator asks to sit (Seat.None = any free seat)
        public event Action<string> MemberJoined;                  // everybody: somebody appeared in the room
        public event Action<string> MemberLeft;                    // everybody: somebody is gone (the member is already removed)
        public event Action<string> NeedsSync;                     // host: this member must be sent the whole game state
        public event Action<string, string> GameLine;              // fromId, line (already permitted)
        public event Action<string, string> HostChanged;           // oldHostId, newHostId
        public event Action<string, string> HostOffered;           // fromId, name: the host asks us to take over
        public event Action<string> Rejected;
        public event Action<string> Removed;
        public event Action<string> Closed;
        public event Action<string> Notice;                        // something the user should be told

        RoomSession(IRoomTransport transport, string name, bool wantsPlay, Seat seat)
        {
            t = transport;
            myName = NetLink.Sanitize(name);
            wantsToPlay = wantsPlay;
            startSeat = seat;
            t.Connected += OnConnected;
            t.PeerConnected += OnPeerConnected;
            t.PeerDisconnected += OnPeerDisconnected;
            t.LineReceived += OnLine;
            t.Closed += OnTransportClosed;
            t.RelayFilter = RelayAllowed;
            housekeeping.Interval = 400;
            housekeeping.Tick += delegate { Housekeeping(); };
        }

        /// <summary>The creator of a room: it is the host and sits at 'seat' (None = watches).</summary>
        public static RoomSession CreateHost(IRoomTransport transport, string name, Seat seat)
        {
            return new RoomSession(transport, name, true, seat);
        }

        /// <summary>Somebody who joins a room and asks to play or to watch.</summary>
        public static RoomSession CreateClient(IRoomTransport transport, string name, bool wantsToPlay)
        {
            return new RoomSession(transport, name, wantsToPlay, Seat.None);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            housekeeping.Start();
            t.Start();
        }

        // ---------------------------------------------------------------- state

        public string MyId { get { return t.MyId; } }
        public string MyName { get { return myName; } }
        public string HostId { get { return hostId; } }
        public int Revision { get { return rev; } }
        public bool Ready { get { return ready; } }
        public bool IsClosed { get { return closed; } }
        public bool SpectatorChatAllowed { get { return specChat; } }
        public bool SpectatorVoiceAllowed { get { return specVoice; } }
        public IRoomTransport Transport { get { return t; } }

        /// <summary>This process is the host: it decides, it is the only writer of the room state.</summary>
        public bool IsHost { get { return ready && !movingAway && hostId.Length > 0 && hostId == t.MyId; } }

        /// <summary>The host role is moving to somebody else right now.</summary>
        public bool Transferring { get { return xferTarget != null || xferNewHost != null || returning != null; } }

        public RoomMember[] Members
        {
            get
            {
                RoomMember[] copy = new RoomMember[members.Count];
                for (int i = 0; i < copy.Length; i++) copy[i] = members[i].Copy();
                return copy;
            }
        }

        public int MemberCount { get { return members.Count; } }

        /// <summary>The name of the host that offers us to take its place (LAN); null when there is no open offer.</summary>
        public string HostOfferName { get { return offerFrom == null ? null : offerName; } }

        public RoomMember Find(string id)
        {
            foreach (RoomMember m in members) if (m.Id == id) return m;
            return null;
        }

        public RoomMember SeatHolder(Seat seat)
        {
            if (seat == Seat.None) return null;
            foreach (RoomMember m in members) if (m.Seat == seat) return m;
            return null;
        }

        public Seat MySeat
        {
            get { RoomMember me = Find(t.MyId); return me == null ? Seat.None : me.Seat; }
        }

        public bool CanChat(string id)
        {
            RoomMember m = Find(id);
            if (m == null) return false;
            if (m.Id == hostId || m.Seat != Seat.None) return true;   // the host and the players are never muted
            return specChat && !m.ChatMuted;
        }

        public bool CanSpeak(string id)
        {
            RoomMember m = Find(id);
            if (m == null) return false;
            if (m.Id == hostId || m.Seat != Seat.None) return true;
            return specVoice && !m.VoiceMuted;
        }

        public int PlayerCount
        {
            get { int n = 0; foreach (RoomMember m in members) if (m.Seat != Seat.None) n++; return n; }
        }

        /// <summary>Host: the seat this member had before it left or stood up (None if unknown).</summary>
        public Seat RememberedSeat(string id)
        {
            Seat s;
            return seatMemory.TryGetValue(id, out s) ? s : Seat.None;
        }

        // ---------------------------------------------------------------- game traffic

        /// <summary>Sends a line to everybody. Members may broadcast only CHAT and VOICE (and only if they are allowed to); the host anything.</summary>
        public void Broadcast(string line)
        {
            if (!ready || closed || movingAway || !MaySend(line)) return;
            t.Broadcast(line);
        }

        public void SendTo(string id, string line)
        {
            if (!ready || closed || movingAway) return;
            t.SendTo(id, line);
        }

        /// <summary>A player's request to the host (MOVE, RESIGN, DRAW...). The host itself calls its own handlers instead.</summary>
        public void SendToHost(string line)
        {
            if (!ready || closed || movingAway || IsHost) return;
            t.SendTo(hostId, line);
        }

        static string VerbOf(string line)
        {
            int sp = line.IndexOf(' ');
            return sp < 0 ? line : line.Substring(0, sp);
        }

        bool MaySend(string line)
        {
            string verb = VerbOf(line);
            if (verb == "CHAT") return CanChat(t.MyId);
            if (verb == "VOICE") return CanSpeak(t.MyId);
            return IsHost;
        }

        // ---------------------------------------------------------------- transport events

        void OnConnected()
        {
            if (closed) return;
            if (t.IsHost && hostId.Length == 0)
            {
                hostId = t.MyId;
                RoomMember me = new RoomMember();
                me.Id = t.MyId;
                me.Name = myName;
                me.Seat = startSeat;
                members.Add(me);
                ready = true;
                Publish();
                if (Welcomed != null) Welcomed();
            }
            else if (!t.IsHost && !ready)
            {
                hostId = t.HostId;
                waitingSince = DateTime.UtcNow;
                t.SendTo(hostId, "REQ JOIN " + (wantsToPlay ? "play" : "watch"));
            }
        }

        void OnPeerConnected(string id, string name)
        {
            if (closed) return;
            // a member that comes back after the host moved: no new approval
            if (returning != null && Find(id) != null)
            {
                t.SendTo(id, "WELCOME");
                returning.Remove(id);
                RoomMember back = Find(id);
                if (back != null && name.Length > 0) back.Name = NetLink.Sanitize(name);
                Publish();
                if (NeedsSync != null) NeedsSync(id);
                if (returning.Count == 0) FinishReturning();
                return;
            }
            if (!IsHost) return;
            if (banned.Contains(id)) { SendRejection(id, "Хост исключил вас из комнаты."); return; }
            if (members.Count + pending.Count >= MaxMembers) { SendRejection(id, "Комната заполнена."); return; }
            PendingPeer p = new PendingPeer();
            p.Name = string.IsNullOrEmpty(name) ? "Игрок" : NetLink.Sanitize(name);
            pending[id] = p;
        }

        void OnPeerDisconnected(string id)
        {
            if (closed) return;
            pending.Remove(id);
            if (returning != null && returning.Contains(id)) return;   // it may still come back
            if (IsHost && Find(id) != null) RemoveMember(id);
        }

        void OnTransportClosed(string reason)
        {
            transportClosed = true;   // the transport has shut itself down
            if (closed) return;
            closed = true;
            housekeeping.Stop();
            if (Closed != null) Closed(reason);
        }

        void OnLine(string from, string line)
        {
            if (closed) return;
            string verb = VerbOf(line);
            string rest = verb.Length >= line.Length ? "" : line.Substring(verb.Length + 1);
            switch (verb)
            {
                case "REQ":
                    if (IsHost) HandleRequest(from, rest);
                    return;
                case "WELCOME":
                    // the room we are in arrives right behind this line; we are in when we can see ourselves in it
                    if (!IsHost && from == hostId && !ready)
                    {
                        welcomed = true;
                        if (Find(t.MyId) != null) BecomeReady();
                    }
                    return;
                case "REJECTED":
                    if (!IsHost && from == hostId && !ready)
                    {
                        closed = true;
                        housekeeping.Stop();
                        if (Rejected != null) Rejected(rest);
                    }
                    else if (!IsHost && from == hostId && ready && rest == "seat")
                    {
                        RaiseNotice("Это место уже занято.");
                    }
                    return;
                case "KICKED":
                    if (from == hostId)
                    {
                        closed = true;
                        housekeeping.Stop();
                        if (Removed != null) Removed("Хост исключил вас из комнаты.");
                    }
                    return;
                case "ROOM":
                    if (!IsHost && (from == hostId || from == xferNewHost)) ApplyRoom(line);
                    return;
                case "XFER":
                    HandleTransfer(from, rest);
                    return;
            }
            if (!ready) return;
            Route(from, verb, line);
        }

        // ---------------------------------------------------------------- permissions of game traffic

        static bool IsPlayerRequest(string verb)
        {
            switch (verb)
            {
                case "MOVE": case "RESIGN": case "DRAW": case "DRAW_YES": case "DRAW_NO":
                case "NEW": case "NEW_YES": case "NEW_NO":
                    return true;
            }
            return false;
        }

        static bool IsHostStamped(string verb)
        {
            switch (verb)
            {
                case "PLAYED": case "RESULT": case "OFFER": case "NEWGAME": case "SYNC":
                    return true;
            }
            return false;
        }

        Auth Authorize(string from, string verb)
        {
            if (verb == "CHAT")
            {
                if (Find(from) == null) return IsHost ? Auth.Deny : Auth.Defer;   // the ROOM that adds this member may still be on its way
                return CanChat(from) ? Auth.Allow : Auth.Deny;
            }
            if (verb == "VOICE") return CanSpeak(from) ? Auth.Allow : Auth.Deny;   // never worth waiting for
            if (verb == "RESYNC") return (IsHost && Find(from) != null) ? Auth.Allow : Auth.Deny;   // anybody may ask for the game again
            if (IsPlayerRequest(verb))
            {
                if (!IsHost) return Auth.Deny;
                RoomMember m = Find(from);
                return (m != null && m.Seat != Seat.None) ? Auth.Allow : Auth.Deny;
            }
            if (IsHostStamped(verb)) return (!IsHost && from == hostId) ? Auth.Allow : Auth.Deny;
            return Auth.Deny;
        }

        void Route(string from, string verb, string line)
        {
            Auth a = Authorize(from, verb);
            if (a == Auth.Allow)
            {
                if (GameLine != null) GameLine(from, line);
            }
            else if (a == Auth.Defer && deferred.Count < 64)
            {
                Deferred d = new Deferred();
                d.From = from; d.Verb = verb; d.Line = line; d.Expires = DateTime.UtcNow.AddMilliseconds(DeferMs);
                deferred.Add(d);
            }
        }

        /// <summary>The state changed: lines that waited for it may be allowed now.</summary>
        void RetryDeferred()
        {
            if (deferred.Count == 0) return;
            Deferred[] snapshot = deferred.ToArray();
            deferred.Clear();
            foreach (Deferred d in snapshot)
            {
                Auth a = Authorize(d.From, d.Verb);
                if (a == Auth.Allow) { if (GameLine != null) GameLine(d.From, d.Line); }
                else if (a == Auth.Defer && d.Expires > DateTime.UtcNow) deferred.Add(d);
            }
        }

        /// <summary>LAN hub: should this broadcast of a client be passed on to the other clients? Only chat and voice are.</summary>
        bool RelayAllowed(string from, string line)
        {
            string verb = VerbOf(line);
            if (verb != "CHAT" && verb != "VOICE") return false;
            return Authorize(from, verb) != Auth.Deny;
        }

        // ---------------------------------------------------------------- requests to the host

        void HandleRequest(string from, string rest)
        {
            string[] a = rest.Split(' ');
            switch (a[0])
            {
                case "JOIN":
                {
                    PendingPeer p;
                    if (!pending.TryGetValue(from, out p) || p.Asked) return;
                    p.Asked = true;
                    bool wantsPlay = a.Length > 1 && a[1] == "play";
                    // somebody who dropped out of a game and comes back sits down again without a question
                    Seat before = RememberedSeat(from);
                    if (wantsPlay && before != Seat.None && SeatHolder(before) == null)
                    {
                        Approve(from, before);
                        return;
                    }
                    if (JoinRequested != null) JoinRequested(from, p.Name, wantsPlay);
                    return;
                }
                case "SEAT":
                {
                    RoomMember m = Find(from);
                    if (m == null || m.Seat != Seat.None) return;
                    Seat wanted = Seat.None;
                    if (a.Length > 1) wanted = a[1] == "W" ? Seat.White : (a[1] == "B" ? Seat.Black : Seat.None);
                    if (wanted != Seat.None && SeatHolder(wanted) != null) { t.SendTo(from, "REJECTED seat"); return; }
                    if (wanted == Seat.None && FirstFreeSeat() == Seat.None) { t.SendTo(from, "REJECTED seat"); return; }
                    // going back to the seat one has just left is not worth a question
                    Seat before = RememberedSeat(from);
                    if (before != Seat.None && (wanted == Seat.None || wanted == before) && SeatHolder(before) == null)
                    {
                        SetSeat(from, before);
                        return;
                    }
                    if (SeatRequested != null) SeatRequested(from, wanted);
                    return;
                }
                case "STAND":
                    ChangeSeat(from, Seat.None, true);
                    return;
            }
        }

        // ---------------------------------------------------------------- client actions

        /// <summary>Ask to take a seat (host: sits down at once if the seat is free; Seat.None = any free seat).</summary>
        public void RequestSeat(Seat seat)
        {
            if (!ready || closed) return;
            if (IsHost) { SetSeat(t.MyId, seat == Seat.None ? FirstFreeSeat() : seat); return; }
            t.SendTo(hostId, "REQ SEAT " + (seat == Seat.White ? "W" : (seat == Seat.Black ? "B" : "ANY")));
        }

        public void RequestStand()
        {
            if (!ready || closed) return;
            if (IsHost) { ChangeSeat(t.MyId, Seat.None, true); return; }
            t.SendTo(hostId, "REQ STAND");
        }

        public Seat FirstFreeSeat()
        {
            if (SeatHolder(Seat.White) == null) return Seat.White;
            if (SeatHolder(Seat.Black) == null) return Seat.Black;
            return Seat.None;
        }

        // ---------------------------------------------------------------- host actions

        public void Approve(string id, Seat seat)
        {
            if (!IsHost) return;
            PendingPeer p;
            if (!pending.TryGetValue(id, out p)) return;
            pending.Remove(id);
            if (members.Count >= MaxMembers) { SendRejection(id, "Комната заполнена."); return; }
            RoomMember m = new RoomMember();
            m.Id = id;
            m.Name = p.Name;
            m.Seat = (seat != Seat.None && SeatHolder(seat) == null) ? seat : Seat.None;
            members.Add(m);
            t.SendTo(id, "WELCOME");
            Publish();
            if (MemberJoined != null) MemberJoined(id);
            if (NeedsSync != null) NeedsSync(id);
        }

        public void Reject(string id, string reason)
        {
            if (!IsHost) return;
            pending.Remove(id);
            SendRejection(id, reason);
        }

        public bool IsPending(string id)
        {
            return pending.ContainsKey(id);
        }

        public string[] PendingIds
        {
            get { string[] ids = new string[pending.Count]; pending.Keys.CopyTo(ids, 0); return ids; }
        }

        public string PendingName(string id)
        {
            PendingPeer p;
            return pending.TryGetValue(id, out p) ? p.Name : "";
        }

        void SendRejection(string id, string reason)
        {
            t.SendTo(id, "REJECTED " + (string.IsNullOrEmpty(reason) ? "Хост не принял вас в игру." : reason));
            Disconnect(id);
        }

        /// <summary>Lets the last line leave before the connection goes.</summary>
        void Disconnect(string id)
        {
            DateTime stamp = DateTime.UtcNow;
            Timer later = new Timer();
            later.Interval = 500;
            later.Tick += delegate
            {
                later.Stop();
                later.Dispose();
                if (closed) return;
                // the same person may have come straight back (a refused player trying again): that is a new connection, leave it alone
                PendingPeer again;
                if (pending.TryGetValue(id, out again) && again.Since > stamp) return;
                if (Find(id) != null) return;
                t.Disconnect(id);
            };
            later.Start();
        }

        /// <summary>Puts a member at a free seat, or lets it stand up (Seat.None). Returns false if the seat is taken.</summary>
        public bool SetSeat(string id, Seat seat)
        {
            return ChangeSeat(id, seat, false);
        }

        /// <summary>
        /// 'voluntary': the person stood up by itself, so it may sit back at that seat without being asked. A seat that the host took
        /// away is forgotten: coming back needs the host's word again.
        /// </summary>
        bool ChangeSeat(string id, Seat seat, bool voluntary)
        {
            if (!IsHost) return false;
            RoomMember m = Find(id);
            if (m == null) return false;
            if (seat != Seat.None)
            {
                RoomMember holder = SeatHolder(seat);
                if (holder != null && holder.Id != id) return false;
            }
            if (m.Seat == seat) return true;
            if (m.Seat != Seat.None)
            {
                if (voluntary) seatMemory[id] = m.Seat; else seatMemory.Remove(id);
            }
            m.Seat = seat;
            Publish();
            return true;
        }

        /// <summary>After a rematch the two players change colours.</summary>
        public void SwapSeats()
        {
            if (!IsHost) return;
            RoomMember w = SeatHolder(Seat.White), b = SeatHolder(Seat.Black);
            if (w == null || b == null) return;
            w.Seat = Seat.Black;
            b.Seat = Seat.White;
            Publish();
        }

        public void SetPolicy(bool chat, bool voice)
        {
            if (!IsHost || (specChat == chat && specVoice == voice)) return;
            specChat = chat;
            specVoice = voice;
            Publish();
        }

        public void SetMute(string id, bool chatMuted, bool voiceMuted)
        {
            if (!IsHost) return;
            RoomMember m = Find(id);
            if (m == null || (m.ChatMuted == chatMuted && m.VoiceMuted == voiceMuted)) return;
            m.ChatMuted = chatMuted;
            m.VoiceMuted = voiceMuted;
            Publish();
        }

        public void Kick(string id)
        {
            if (!IsHost || id == t.MyId) return;
            if (Find(id) == null) return;
            banned.Add(id);
            t.SendTo(id, "KICKED");
            RemoveMember(id);
            Disconnect(id);
        }

        void RemoveMember(string id)
        {
            RoomMember m = Find(id);
            if (m == null) return;
            if (m.Seat != Seat.None) seatMemory[id] = m.Seat;
            members.Remove(m);
            Publish();
            if (MemberLeft != null) MemberLeft(id);
        }

        void RejectAllPending(string reason)
        {
            foreach (string id in new List<string>(pending.Keys)) SendRejection(id, reason);
            pending.Clear();
        }

        // ---------------------------------------------------------------- the replicated state

        string BuildRoom()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("ROOM ").Append(rev).Append(' ').Append(hostId).Append(' ');
            sb.Append(specChat ? '1' : '0').Append(specVoice ? '1' : '0').Append(' ');
            for (int i = 0; i < members.Count; i++)
            {
                RoomMember m = members[i];
                if (i > 0) sb.Append(';');
                sb.Append(m.Id).Append(':').Append(m.Name).Append(':');
                sb.Append(m.Seat == Seat.White ? 'W' : (m.Seat == Seat.Black ? 'B' : 'S')).Append(':');
                sb.Append(m.ChatMuted ? '1' : '0').Append(m.VoiceMuted ? '1' : '0');
            }
            return sb.ToString();
        }

        /// <summary>Host: a change was made; number it, tell everybody, refresh what depends on it.</summary>
        void Publish()
        {
            rev++;
            try { t.Broadcast(BuildRoom()); } catch (Exception) { }
            RaiseChanged();
            RetryDeferred();
            IListingTransport listing = t as IListingTransport;
            if (listing != null)
            {
                int players = PlayerCount;
                listing.SetListing(players, members.Count - players);
            }
        }

        void ApplyRoom(string line)
        {
            string[] p = line.Split(new char[] { ' ' }, 5);
            int newRev;
            if (p.Length < 5 || !int.TryParse(p[1], out newRev) || newRev <= rev || p[3].Length != 2) return;

            List<RoomMember> next = new List<RoomMember>();
            foreach (string item in p[4].Split(';'))
            {
                string[] f = item.Split(':');
                if (f.Length != 4 || f[0].Length == 0 || f[3].Length != 2) return;   // malformed: ignore the whole line
                RoomMember m = new RoomMember();
                m.Id = f[0];
                m.Name = NetLink.Sanitize(f[1]);
                m.Seat = f[2] == "W" ? Seat.White : (f[2] == "B" ? Seat.Black : Seat.None);
                m.ChatMuted = f[3][0] == '1';
                m.VoiceMuted = f[3][1] == '1';
                next.Add(m);
            }
            if (next.Count == 0 || next.Count > MaxMembers) return;

            List<string> joined = new List<string>(), left = new List<string>();
            foreach (RoomMember m in next) if (Find(m.Id) == null) joined.Add(m.Id);
            foreach (RoomMember m in members)
            {
                bool still = false;
                foreach (RoomMember n in next) if (n.Id == m.Id) still = true;
                if (!still) left.Add(m.Id);
            }

            string oldHost = hostId;
            rev = newRev;
            hostId = p[2];
            specChat = p[3][0] == '1';
            specVoice = p[3][1] == '1';
            members.Clear();
            members.AddRange(next);
            if (xferNewHost != null && hostId == xferNewHost) { xferNewHost = null; xferTarget = null; movingAway = false; }

            bool hostMoved = oldHost != hostId;
            if (hostMoved)
            {
                IOwnershipTransport own = t as IOwnershipTransport;
                if (own != null) own.NoteHost(hostId);
            }
            foreach (string id in joined) if (MemberJoined != null) MemberJoined(id);
            foreach (string id in left) if (MemberLeft != null) MemberLeft(id);

            if (ready && Find(t.MyId) == null)
            {
                closed = true;
                housekeeping.Stop();
                if (Removed != null) Removed("Вас больше нет в комнате.");
                return;
            }
            if (!ready && welcomed && Find(t.MyId) != null)
            {
                BecomeReady();
                return;
            }
            if (hostMoved && HostChanged != null) HostChanged(oldHost, hostId);
            RaiseChanged();
            RetryDeferred();

            if (hostMoved && hostId == t.MyId)
            {
                // the room was handed to us (Steam): we are the single writer from now on
                pending.Clear();
                Publish();
                RaiseNotice("Вы теперь хост комнаты.");
            }
        }

        void BecomeReady()
        {
            ready = true;
            if (Welcomed != null) Welcomed();
            RaiseChanged();
            RetryDeferred();
        }

        void RaiseChanged()
        {
            if (Changed != null) Changed();
        }

        // ---------------------------------------------------------------- moving the host role

        /// <summary>Host only: hands the room over to another member. Returns false if that is impossible right now.</summary>
        public bool TransferHost(string newId)
        {
            if (!IsHost || Transferring || newId == t.MyId || Find(newId) == null) return false;

            IOwnershipTransport own = t as IOwnershipTransport;
            if (own != null)
            {
                // Steam: the lobby changes hands at once
                string old = hostId;
                if (!own.TransferOwnership(newId))
                {
                    RaiseNotice("Не удалось передать хост: Steam не передал комнату.");
                    return false;
                }
                RejectAllPending("Хост сменился. Подключитесь заново.");
                hostId = newId;
                rev++;
                try { t.Broadcast(BuildRoom()); } catch (Exception) { }
                own.NoteHost(newId);
                if (HostChanged != null) HostChanged(old, newId);
                RaiseChanged();
                return true;
            }

            IRelocatableTransport rel = t as IRelocatableTransport;
            if (rel == null) return false;
            xferTarget = newId;
            xferAsking = true;
            xferStarted = DateTime.UtcNow;
            t.SendTo(newId, "XFER ASK " + FormatTable(rel.ExportIdentities()));
            RaiseNotice("Передача хоста: ждём ответа «" + Find(newId).Name + "»…");
            return true;
        }

        void HandleTransfer(string from, string rest)
        {
            string[] a = rest.Split(new char[] { ' ' }, 4);
            IRelocatableTransport rel = t as IRelocatableTransport;
            switch (a[0])
            {
                case "ASK":   // the host offers us the room
                    if (from != hostId || IsHost || rel == null) return;
                    offerFrom = from;
                    offerTable = a.Length > 1 ? a[1] : "";
                    RoomMember offering = Find(from);
                    offerName = offering == null ? "хост" : offering.Name;
                    RaiseChanged();
                    if (HostOffered != null) HostOffered(from, offerName);
                    return;

                case "NO":    // the member refused (old host), or the host called the hand-over off (the one that was asked)
                    if (xferTarget == from && xferAsking)
                    {
                        string who = Find(from) == null ? "Участник" : Find(from).Name;
                        xferTarget = null;
                        xferAsking = false;
                        RaiseNotice("«" + who + "» не принял роль хоста.");
                    }
                    else if (from == hostId && !IsHost && (offerFrom != null || hubOpen))
                    {
                        offerFrom = offerTable = null;
                        AbandonHubIfOpen();
                        RaiseChanged();
                        RaiseNotice("Хост отменил передачу.");
                    }
                    return;

                case "OK":    // the member runs a hub now: test that it can be reached, then tell everybody where to go
                {
                    if (!IsHost || xferTarget != from || !xferAsking || rel == null) return;
                    int port;
                    string ip = t.AddressOf(from);
                    if (a.Length < 2 || !int.TryParse(a[1], out port) || ip == null)
                    {
                        AbortTransfer(from, "Не удалось передать хост: новый хост недоступен.");
                        return;
                    }
                    xferAsking = false;
                    string target = from;
                    int targetPort = port;
                    rel.ProbeHub(ip, port, delegate(bool reachable)
                    {
                        if (closed || xferTarget != target) return;
                        if (!reachable)
                        {
                            AbortTransfer(target, "Не удалось передать хост: к новому хосту нельзя подключиться (брандмауэр?).");
                            return;
                        }
                        // people still waiting for a decision cannot follow the room: they are told to come back later
                        RejectAllPending("Хост сменился. Подключитесь заново.");
                        xferNewHost = target;
                        xferDeadline = DateTime.UtcNow.AddSeconds(15);
                        t.Broadcast("XFER GO " + target + " " + ip + " " + targetPort);
                        movingAway = true;
                        RaiseNotice("Переход к новому хосту…");
                        rel.MoveTo(ip, targetPort);
                    });
                    return;
                }

                case "GO":    // everybody: the room moves
                {
                    if (from != hostId || a.Length < 4 || rel == null) return;
                    string target = a[1], ip = a[2];
                    int port;
                    if (!int.TryParse(a[3], out port)) return;
                    xferNewHost = target;
                    xferDeadline = DateTime.UtcNow.AddSeconds(15);
                    if (target == t.MyId)
                    {
                        BecomeHostAfterMove(from);
                    }
                    else
                    {
                        RaiseNotice("Переход к новому хосту…");
                        rel.MoveTo(ip, port);
                    }
                    return;
                }
            }
        }

        void AbortTransfer(string target, string message)
        {
            t.SendTo(target, "XFER NO");   // tells it to stop listening for us
            xferTarget = null;
            xferAsking = false;
            RaiseNotice(message);
        }

        /// <summary>The user decided on an offer to become the host.</summary>
        public void AnswerHostOffer(bool accept)
        {
            if (offerFrom == null) return;
            string from = offerFrom, table = offerTable;
            offerFrom = offerTable = null;
            RaiseChanged();
            IRelocatableTransport rel = t as IRelocatableTransport;
            if (!accept || rel == null) { t.SendTo(from, "XFER NO"); return; }
            int port;
            try { port = rel.BecomeHub(ParseTable(table), 0); }
            catch (Exception) { t.SendTo(from, "XFER NO"); return; }
            hubOpen = true;
            hubOpenSince = DateTime.UtcNow;
            t.SendTo(from, "XFER OK " + port);
        }

        /// <summary>A hub that was started for a hand-over that did not happen is shut down again.</summary>
        void AbandonHubIfOpen()
        {
            if (!hubOpen) return;
            hubOpen = false;
            IRelocatableTransport rel = t as IRelocatableTransport;
            if (rel != null) rel.AbandonHub();
        }

        /// <summary>We are the new host: the others are on their way to our hub.</summary>
        void BecomeHostAfterMove(string oldHost)
        {
            hubOpen = false;   // the hub we started is the room now
            IRelocatableTransport rel = t as IRelocatableTransport;
            if (rel != null) rel.DropUpstream();
            hostId = t.MyId;
            pending.Clear();
            returning = new HashSet<string>();
            foreach (RoomMember m in members) if (m.Id != t.MyId) returning.Add(m.Id);
            returningDeadline = DateTime.UtcNow.AddSeconds(8);
            xferNewHost = null;
            IOwnershipTransport own = t as IOwnershipTransport;
            if (own != null) own.NoteHost(hostId);
            if (HostChanged != null) HostChanged(oldHost, hostId);
            Publish();
            RaiseNotice("Вы теперь хост комнаты.");
            if (returning.Count == 0) FinishReturning();
        }

        void FinishReturning()
        {
            if (returning == null) return;
            bool lost = false;
            foreach (string id in new List<string>(returning))
            {
                RoomMember m = Find(id);
                if (m != null)
                {
                    if (m.Seat != Seat.None) seatMemory[id] = m.Seat;
                    members.Remove(m);
                    lost = true;
                    if (MemberLeft != null) MemberLeft(id);
                }
            }
            returning = null;
            if (lost) Publish();
        }

        static string FormatTable(IDictionary<string, string> table)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in table)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        static IDictionary<string, string> ParseTable(string text)
        {
            Dictionary<string, string> table = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text) || text == "-") return table;
            foreach (string pair in text.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && eq < pair.Length - 1) table[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
            return table;
        }

        void RaiseNotice(string text)
        {
            if (Notice != null) Notice(text);
        }

        // ---------------------------------------------------------------- clock

        void Housekeeping()
        {
            if (closed) return;
            DateTime now = DateTime.UtcNow;

            for (int i = deferred.Count - 1; i >= 0; i--)
                if (deferred[i].Expires <= now) deferred.RemoveAt(i);

            if (IsHost)
            {
                foreach (string id in new List<string>(pending.Keys))
                {
                    PendingPeer p = pending[id];
                    if (!p.Asked && (now - p.Since).TotalSeconds > 10) { pending.Remove(id); t.Disconnect(id); }
                }
                if (xferTarget != null && xferAsking && (now - xferStarted).TotalSeconds > 60)
                {
                    string target = xferTarget;
                    AbortTransfer(target, "Не удалось передать хост: нет ответа.");
                }
                // keep the lobby listing fresh: a new owner's first write may have come before Steam moved the ownership
                if ((now - lastListing).TotalSeconds > 10)
                {
                    lastListing = now;
                    IListingTransport listing = t as IListingTransport;
                    if (listing != null)
                    {
                        int players = PlayerCount;
                        listing.SetListing(players, members.Count - players);
                    }
                }
            }
            if (hubOpen && (now - hubOpenSince).TotalSeconds > 30)
            {
                // we agreed to host, but the old host never moved the room here
                AbandonHubIfOpen();
                RaiseNotice("Передача хоста не состоялась.");
            }
            else if (!ready && !t.IsHost && (now - waitingSince).TotalSeconds > 90)
            {
                closed = true;
                housekeeping.Stop();
                if (Closed != null) Closed("Хост не ответил на запрос.");
                return;
            }

            if (returning != null && now > returningDeadline) FinishReturning();

            if (xferNewHost != null && now > xferDeadline)
            {
                xferNewHost = null;
                closed = true;
                housekeeping.Stop();
                if (Closed != null) Closed("Не удалось перейти к новому хосту.");
            }
        }

        // ---------------------------------------------------------------- shutdown

        public void Close()
        {
            closed = true;   // (it may be set already: a refusal or a removal ends the session but not the connection)
            housekeeping.Stop();
            if (transportClosed) return;
            transportClosed = true;
            t.Close();
        }

        public void Dispose()
        {
            Close();
            housekeeping.Dispose();
        }
    }
}
