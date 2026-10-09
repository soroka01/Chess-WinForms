using System;
using System.Collections.Generic;

namespace ChessGame
{
    /// <summary>
    /// How the members of a room are connected. The room is a star: one hub (the host) and any number of clients.
    /// A client talks to everybody through the hub; the hub relays. Lines are plain text without line breaks (at most 2048 chars).
    /// Every event is raised on the thread that created the transport (the UI thread). Subscribe first, then call Start().
    /// </summary>
    public interface IRoomTransport : IDisposable
    {
        /// <summary>This process runs the hub (LAN) or owns the lobby (Steam).</summary>
        bool IsHost { get; }

        /// <summary>Our own id in the room (valid after Connected).</summary>
        string MyId { get; }

        /// <summary>The id of the hub / host (valid after Connected; changes when the room moves to another host).</summary>
        string HostId { get; }

        string MyName { get; }

        /// <summary>Client: connected to the hub and identified. Host: ready to accept connections.</summary>
        event Action Connected;

        /// <summary>Host only: a connection finished its greeting. Arguments: id, name.</summary>
        event Action<string, string> PeerConnected;

        /// <summary>Host only: a connection ended. Argument: id.</summary>
        event Action<string> PeerDisconnected;

        /// <summary>A line arrived. Arguments: id of the sender, the line. Raised for broadcasts and for lines sent to us alone.</summary>
        event Action<string, string> LineReceived;

        /// <summary>The room is gone for us (client: the hub is lost; host: the hub failed). Argument: reason for the user.</summary>
        event Action<string> Closed;

        void Start();

        /// <summary>Sends to every other member of the room (a client reaches them through the hub).</summary>
        void Broadcast(string line);

        /// <summary>Sends to one member. The id may be the host's.</summary>
        void SendTo(string id, string line);

        /// <summary>Host only: drops one connection (no PeerDisconnected is required for it, but allowed).</summary>
        void Disconnect(string id);

        /// <summary>Silent shutdown: no Closed event.</summary>
        void Close();

        /// <summary>Host only: network address of a connected member (LAN: the remote IP as text; Steam: null).</summary>
        string AddressOf(string id);

        /// <summary>
        /// Host only (LAN hub): called for every broadcast a client sent, before it is relayed to the other clients.
        /// Return false to swallow it. Arguments: sender id, line. Null relays everything.
        /// </summary>
        Func<string, string, bool> RelayFilter { get; set; }
    }

    /// <summary>LAN rooms can move the hub to another machine (everybody reconnects, ids are kept).</summary>
    public interface IRelocatableTransport
    {
        /// <summary>Host only: id -> key of every connected client; the new hub needs it to recognise returning clients.</summary>
        IDictionary<string, string> ExportIdentities();

        /// <summary>
        /// Client that is going to become the host: starts a hub on this machine (port 0 = any free port) and returns the port.
        /// Keeps its id; HostId becomes MyId, IsHost becomes true. The old connection stays until DropUpstream().
        /// </summary>
        int BecomeHub(IDictionary<string, string> identities, int preferredPort);

        /// <summary>The new host is in charge now: close the connection to the old hub.</summary>
        void DropUpstream();

        /// <summary>
        /// The hand-over did not happen: a client that had started a hub (BecomeHub) stops it, drops whoever has connected to it
        /// and goes back to being an ordinary client of the old hub (HostId is the old hub's again).
        /// </summary>
        void AbandonHub();

        /// <summary>
        /// Plain TCP connect test of address:port (about 3 s, asynchronous, nothing is sent); done(true/false) on the UI thread.
        /// The old host checks that the new hub is reachable before it tells everybody to move.
        /// </summary>
        void ProbeHub(string address, int port, Action<bool> done);

        /// <summary>
        /// Everybody else (and the old host): leave the current hub and join the new one; the id is kept.
        /// An old hub stops accepting connections, flushes its queues, closes after a short grace period and
        /// becomes an ordinary client. No Closed event is raised for this planned move.
        /// </summary>
        void MoveTo(string address, int port);
    }

    /// <summary>Steam lobbies move the room by handing the lobby over to another member.</summary>
    public interface IOwnershipTransport
    {
        /// <summary>Host only: make 'newHostId' the owner of the room. False if the platform refused (nothing changed).</summary>
        bool TransferOwnership(string newHostId);

        /// <summary>The room announced a new host (every member calls this): HostId / IsHost follow it.</summary>
        void NoteHost(string hostId);
    }

    /// <summary>A room that is advertised in a public list (Steam lobbies) shows how many people sit and watch.</summary>
    public interface IListingTransport
    {
        void SetListing(int players, int watchers);
    }
}
