using System;
using System.Collections.Generic;

namespace ChessGame
{
    public sealed class LobbyInfo
    {
        public ulong Id;
        public string HostName = "";
        public int Members;
        public bool IsFriend;
        public int Players = -1;     // from the lobby listing; -1 = the host did not say
        public int Watchers = -1;
    }

    /// <summary>
    /// The few lobby operations the chess game needs. SteamLobbyService implements them on top of the Steam client
    /// (Spacewar, AppID 480); tests use an in-memory implementation. Everything runs on the UI thread.
    /// </summary>
    public interface ILobbyService
    {
        ulong MySteamId { get; }
        string MyName { get; }

        void CreateLobby(int maxMembers, Action<bool, ulong> done);

        /// <summary>Everybody who is in the lobby right now (including ourselves).</summary>
        ulong[] GetMembers(ulong lobby);

        ulong GetOwner(ulong lobby);

        /// <summary>Hands the lobby to another member (only the owner may do this).</summary>
        bool SetOwner(ulong lobby, ulong newOwner);
        /// <summary>Lists open chess lobbies (only ours: filtered by LobbyKey).</summary>
        void FindLobbies(Action<List<LobbyInfo>> done);
        void JoinLobby(ulong lobby, Action<bool> done);
        void LeaveLobby(ulong lobby);
        void SetLobbyData(ulong lobby, string key, string value);
        void SetJoinable(ulong lobby, bool joinable);
        bool SendChat(ulong lobby, string text);

        /// <summary>lobby, sender, text</summary>
        event Action<ulong, ulong, string> ChatReceived;
        /// <summary>lobby, member, true = joined / false = left or disconnected</summary>
        event Action<ulong, ulong, bool> MemberChanged;
    }

    /// <summary>
    /// What marks a Steam lobby as a chess room. The value carries the protocol generation: lobbies of an older version of the
    /// game (two players, no spectators) are not listed, so that nobody joins a room it cannot talk to.
    /// </summary>
    public static class SteamLink
    {
        public const string LobbyKey = "chess_game";
        public const string LobbyValue = "claude-chess-2";
    }
}
