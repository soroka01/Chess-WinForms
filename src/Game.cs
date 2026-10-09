using System;
using System.Collections.Generic;
using System.Text;

namespace ChessGame
{
    public enum GameStatus
    {
        Playing,
        Checkmate,
        Stalemate,
        DrawInsufficient,
        DrawFiftyMoves,
        DrawRepetition,
        WhiteResigned,
        BlackResigned,
        DrawAgreed
    }

    /// <summary>A game in progress: the position plus move history, SAN notation and result detection.</summary>
    public sealed class Game
    {
        readonly List<Move> moves = new List<Move>();
        readonly List<UndoInfo> undos = new List<UndoInfo>();
        readonly List<string> san = new List<string>();
        readonly List<ulong> hashes = new List<ulong>();
        GameStatus forced = GameStatus.Playing;   // result set from outside (resignation, agreed draw)
        string startFen = Position.StartFen;

        public Position Pos { get; private set; }
        public List<Move> Legal { get; private set; }
        public GameStatus Status { get; private set; }

        /// <summary>Raised after a move was played: the move and the signed piece it captured (0 if none).</summary>
        public event Action<Move, int> MovePlayed;

        /// <summary>Raised when the position was replaced or rolled back (new game, undo, FEN load).</summary>
        public event Action PositionReset;

        public Game()
        {
            NewGame();
        }

        public void NewGame()
        {
            LoadFen(Position.StartFen);
        }

        /// <summary>Starts a fresh game from an arbitrary position.</summary>
        public void LoadFen(string fen)
        {
            startFen = fen;
            Pos = Position.FromFen(fen);
            moves.Clear();
            undos.Clear();
            san.Clear();
            hashes.Clear();
            hashes.Add(Pos.Hash);
            forced = GameStatus.Playing;
            Refresh();
            if (PositionReset != null) PositionReset();
        }

        public int PlyCount { get { return moves.Count; } }
        public List<string> San { get { return san; } }
        public bool HasLastMove { get { return moves.Count > 0; } }
        public Move LastMove { get { return moves[moves.Count - 1]; } }

        /// <summary>The move number 'index' (0-based) of the game so far.</summary>
        public Move MoveAt(int index) { return moves[index]; }

        /// <summary>The signed piece that move 'index' captured (0 = none).</summary>
        public int CapturedAt(int index) { return undos[index].Captured; }

        /// <summary>A fresh copy of the position after the first 'ply' moves (0 = the starting position).</summary>
        public Position PositionAfter(int ply)
        {
            Position p = Position.FromFen(startFen);
            for (int i = 0; i < ply && i < moves.Count; i++) p.MakeMove(moves[i]);
            return p;
        }
        public bool InCheck { get { return Pos.InCheck(Pos.WhiteToMove); } }

        /// <summary>Zobrist hashes of every position so far (the last one is the current position).</summary>
        public ulong[] HashHistory()
        {
            return hashes.ToArray();
        }

        public void Play(Move m)
        {
            string text = BaseSan(m);
            UndoInfo u = Pos.MakeMove(m);
            moves.Add(m);
            undos.Add(u);
            hashes.Add(Pos.Hash);
            Refresh();
            if (Status == GameStatus.Checkmate) text += "#";
            else if (InCheck) text += "+";
            san.Add(text);
            if (MovePlayed != null) MovePlayed(m, u.Captured);
        }

        /// <summary>Ends the game by agreement or resignation (see the Resigned / DrawAgreed statuses).</summary>
        public void Terminate(GameStatus result)
        {
            forced = result;
            Status = result;
        }

        public bool Undo()
        {
            if (moves.Count == 0) return false;
            int i = moves.Count - 1;
            Pos.UnmakeMove(moves[i], undos[i]);
            moves.RemoveAt(i);
            undos.RemoveAt(i);
            san.RemoveAt(i);
            hashes.RemoveAt(hashes.Count - 1);
            forced = GameStatus.Playing;
            Refresh();
            if (PositionReset != null) PositionReset();
            return true;
        }

        void Refresh()
        {
            Legal = Pos.LegalMoves();
            if (forced != GameStatus.Playing)
                Status = forced;
            else if (Legal.Count == 0)
                Status = InCheck ? GameStatus.Checkmate : GameStatus.Stalemate;
            else if (Pos.InsufficientMaterial())
                Status = GameStatus.DrawInsufficient;
            else if (Pos.Half >= 100)
                Status = GameStatus.DrawFiftyMoves;
            else if (CountRepetitions() >= 3)
                Status = GameStatus.DrawRepetition;
            else
                Status = GameStatus.Playing;
        }

        int CountRepetitions()
        {
            int count = 0;
            ulong h = Pos.Hash;
            // positions before the last irreversible move cannot repeat
            int start = Math.Max(0, hashes.Count - 1 - Pos.Half);
            for (int i = hashes.Count - 1; i >= start; i -= 2)
                if (hashes[i] == h) count++;
            return count;
        }

        /// <summary>Standard algebraic notation for a legal move in the current position (without check suffix).</summary>
        string BaseSan(Move m)
        {
            if ((m.Flags & Move.CastleK) != 0) return "O-O";
            if ((m.Flags & Move.CastleQ) != 0) return "O-O-O";

            Position p = Pos;
            int piece = Math.Abs(p.Sq[m.From]);
            bool capture = p.Sq[m.To] != 0 || (m.Flags & Move.EnPassant) != 0;
            StringBuilder sb = new StringBuilder();

            if (piece == Pc.Pawn)
            {
                if (capture) sb.Append((char)('a' + (m.From & 7)));
            }
            else
            {
                sb.Append(" PNBRQK"[piece]);
                bool ambiguous = false, sameFile = false, sameRank = false;
                foreach (Move o in Legal)
                {
                    if (o.To != m.To || o.From == m.From || Math.Abs(p.Sq[o.From]) != piece) continue;
                    ambiguous = true;
                    if ((o.From & 7) == (m.From & 7)) sameFile = true;
                    if ((o.From >> 3) == (m.From >> 3)) sameRank = true;
                }
                if (ambiguous)
                {
                    if (!sameFile) sb.Append((char)('a' + (m.From & 7)));
                    else if (!sameRank) sb.Append((char)('1' + (m.From >> 3)));
                    else sb.Append(Sqr.Name(m.From));
                }
            }

            if (capture) sb.Append('x');
            sb.Append(Sqr.Name(m.To));
            if (m.Promo != 0)
            {
                sb.Append('=');
                sb.Append(" PNBRQK"[m.Promo]);
            }
            return sb.ToString();
        }
    }
}
