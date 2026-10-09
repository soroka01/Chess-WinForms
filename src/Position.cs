using System;
using System.Collections.Generic;
using System.Text;

namespace ChessGame
{
    /// <summary>Piece types. On the board white pieces are positive, black pieces negative.</summary>
    public static class Pc
    {
        public const int None = 0;
        public const int Pawn = 1;
        public const int Knight = 2;
        public const int Bishop = 3;
        public const int Rook = 4;
        public const int Queen = 5;
        public const int King = 6;
    }

    public static class Sqr
    {
        // Square index = rank * 8 + file, a1 = 0, h1 = 7, a8 = 56, h8 = 63.
        public static string Name(int sq)
        {
            return "" + (char)('a' + (sq & 7)) + (char)('1' + (sq >> 3));
        }

        public static int Parse(string s)
        {
            return (s[1] - '1') * 8 + (s[0] - 'a');
        }
    }

    public struct Move
    {
        public const int EnPassant = 1;
        public const int CastleK = 2;
        public const int CastleQ = 4;
        public const int DoublePush = 8;

        public byte From;
        public byte To;
        public byte Promo;   // promotion piece type, or 0
        public byte Flags;

        public Move(int from, int to, int promo, int flags)
        {
            From = (byte)from;
            To = (byte)to;
            Promo = (byte)promo;
            Flags = (byte)flags;
        }

        public bool IsNull { get { return From == To; } }

        public bool Same(Move o)
        {
            return From == o.From && To == o.To && Promo == o.Promo;
        }

        public override string ToString()
        {
            if (IsNull) return "(none)";
            return Sqr.Name(From) + Sqr.Name(To) + (Promo != 0 ? ("pnbrqk"[Promo - 1]).ToString() : "");
        }
    }

    public struct UndoInfo
    {
        public int Captured;
        public int Castle;
        public int Ep;
        public int Half;
        public ulong Hash;
    }

    public sealed class Position
    {
        public const int CastleWK = 1;
        public const int CastleWQ = 2;
        public const int CastleBK = 4;
        public const int CastleBQ = 8;

        public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        public readonly int[] Sq = new int[64];
        public bool WhiteToMove = true;
        public int Castle;
        public int Ep = -1;      // en passant target square, or -1
        public int Half;         // halfmove clock (plies since last pawn move / capture)
        public int Full = 1;
        public int WhiteKing = 4;
        public int BlackKing = 60;
        public ulong Hash;

        // ---------- static tables ----------

        static readonly int[][] KnightTo = new int[64][];
        static readonly int[][] KingTo = new int[64][];
        static readonly int[] CastleMask = new int[64];
        static readonly ulong[,] ZPiece = new ulong[12, 64];
        static readonly ulong[] ZCastle = new ulong[16];
        static readonly ulong[] ZEp = new ulong[8];
        static readonly ulong ZSide;
        // directions 0..3 are rook-like, 4..7 are bishop-like
        static readonly int[] DirF = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DirR = { 0, 0, 1, -1, 1, -1, 1, -1 };

        static ulong rngState = 0x9E3779B97F4A7C15UL;

        static ulong NextRand()
        {
            rngState ^= rngState << 13;
            rngState ^= rngState >> 7;
            rngState ^= rngState << 17;
            return rngState;
        }

        static Position()
        {
            int[] kdf = { 1, 2, 2, 1, -1, -2, -2, -1 };
            int[] kdr = { 2, 1, -1, -2, -2, -1, 1, 2 };
            int[] adf = { 1, 1, 1, 0, -1, -1, -1, 0 };
            int[] adr = { 1, 0, -1, -1, -1, 0, 1, 1 };

            for (int s = 0; s < 64; s++)
            {
                int f = s & 7, r = s >> 3;
                List<int> kn = new List<int>();
                List<int> kg = new List<int>();
                for (int i = 0; i < 8; i++)
                {
                    int nf = f + kdf[i], nr = r + kdr[i];
                    if (nf >= 0 && nf < 8 && nr >= 0 && nr < 8) kn.Add(nr * 8 + nf);
                    nf = f + adf[i]; nr = r + adr[i];
                    if (nf >= 0 && nf < 8 && nr >= 0 && nr < 8) kg.Add(nr * 8 + nf);
                }
                KnightTo[s] = kn.ToArray();
                KingTo[s] = kg.ToArray();
                CastleMask[s] = 15;
            }
            CastleMask[4] = 15 & ~(CastleWK | CastleWQ);
            CastleMask[0] = 15 & ~CastleWQ;
            CastleMask[7] = 15 & ~CastleWK;
            CastleMask[60] = 15 & ~(CastleBK | CastleBQ);
            CastleMask[56] = 15 & ~CastleBQ;
            CastleMask[63] = 15 & ~CastleBK;

            for (int p = 0; p < 12; p++)
                for (int s = 0; s < 64; s++)
                    ZPiece[p, s] = NextRand();
            for (int i = 0; i < 16; i++) ZCastle[i] = NextRand();
            for (int i = 0; i < 8; i++) ZEp[i] = NextRand();
            ZSide = NextRand();
        }

        static int PIdx(int piece)
        {
            return piece > 0 ? piece - 1 : 5 - piece;
        }

        // ---------- construction ----------

        public static Position Start()
        {
            return FromFen(StartFen);
        }

        public static Position FromFen(string fen)
        {
            string[] parts = fen.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) throw new FormatException("Empty FEN");
            Position p = new Position();
            string[] rows = parts[0].Split('/');
            if (rows.Length != 8) throw new FormatException("Bad FEN board");
            for (int i = 0; i < 8; i++)
            {
                int r = 7 - i, f = 0;
                foreach (char c in rows[i])
                {
                    if (c >= '1' && c <= '8') { f += c - '0'; continue; }
                    int idx = "PNBRQK".IndexOf(char.ToUpper(c));
                    if (idx < 0 || f > 7) throw new FormatException("Bad FEN piece");
                    p.Sq[r * 8 + f] = char.IsUpper(c) ? idx + 1 : -(idx + 1);
                    f++;
                }
            }
            p.WhiteToMove = parts.Length < 2 || parts[1] == "w";
            if (parts.Length > 2)
            {
                if (parts[2].IndexOf('K') >= 0) p.Castle |= CastleWK;
                if (parts[2].IndexOf('Q') >= 0) p.Castle |= CastleWQ;
                if (parts[2].IndexOf('k') >= 0) p.Castle |= CastleBK;
                if (parts[2].IndexOf('q') >= 0) p.Castle |= CastleBQ;
            }
            if (parts.Length > 3 && parts[3] != "-") p.Ep = Sqr.Parse(parts[3]);
            if (parts.Length > 4) int.TryParse(parts[4], out p.Half);
            if (parts.Length > 5) int.TryParse(parts[5], out p.Full);
            p.FindKings();
            p.Hash = p.ComputeHash();
            return p;
        }

        public string ToFen()
        {
            StringBuilder sb = new StringBuilder();
            for (int r = 7; r >= 0; r--)
            {
                int empty = 0;
                for (int f = 0; f < 8; f++)
                {
                    int p = Sq[r * 8 + f];
                    if (p == 0) { empty++; continue; }
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    char c = "pnbrqk"[Math.Abs(p) - 1];
                    sb.Append(p > 0 ? char.ToUpper(c) : c);
                }
                if (empty > 0) sb.Append(empty);
                if (r > 0) sb.Append('/');
            }
            sb.Append(WhiteToMove ? " w " : " b ");
            string c2 = "";
            if ((Castle & CastleWK) != 0) c2 += "K";
            if ((Castle & CastleWQ) != 0) c2 += "Q";
            if ((Castle & CastleBK) != 0) c2 += "k";
            if ((Castle & CastleBQ) != 0) c2 += "q";
            sb.Append(c2.Length == 0 ? "-" : c2);
            sb.Append(' ');
            sb.Append(Ep >= 0 ? Sqr.Name(Ep) : "-");
            sb.Append(' ').Append(Half).Append(' ').Append(Full);
            return sb.ToString();
        }

        void FindKings()
        {
            for (int s = 0; s < 64; s++)
            {
                if (Sq[s] == Pc.King) WhiteKing = s;
                else if (Sq[s] == -Pc.King) BlackKing = s;
            }
        }

        public ulong ComputeHash()
        {
            ulong h = 0;
            for (int s = 0; s < 64; s++)
                if (Sq[s] != 0) h ^= ZPiece[PIdx(Sq[s]), s];
            if (!WhiteToMove) h ^= ZSide;
            h ^= ZCastle[Castle];
            if (Ep >= 0) h ^= ZEp[Ep & 7];
            return h;
        }

        public Position Clone()
        {
            Position p = new Position();
            Array.Copy(Sq, p.Sq, 64);
            p.WhiteToMove = WhiteToMove;
            p.Castle = Castle;
            p.Ep = Ep;
            p.Half = Half;
            p.Full = Full;
            p.WhiteKing = WhiteKing;
            p.BlackKing = BlackKing;
            p.Hash = Hash;
            return p;
        }

        // ---------- attacks ----------

        public bool IsAttacked(int sq, bool byWhite)
        {
            int f = sq & 7, r = sq >> 3;
            if (byWhite)
            {
                if (r > 0)
                {
                    if (f > 0 && Sq[sq - 9] == Pc.Pawn) return true;
                    if (f < 7 && Sq[sq - 7] == Pc.Pawn) return true;
                }
            }
            else
            {
                if (r < 7)
                {
                    if (f > 0 && Sq[sq + 7] == -Pc.Pawn) return true;
                    if (f < 7 && Sq[sq + 9] == -Pc.Pawn) return true;
                }
            }

            int sign = byWhite ? 1 : -1;
            int[] kn = KnightTo[sq];
            for (int i = 0; i < kn.Length; i++)
                if (Sq[kn[i]] == Pc.Knight * sign) return true;
            int[] kg = KingTo[sq];
            for (int i = 0; i < kg.Length; i++)
                if (Sq[kg[i]] == Pc.King * sign) return true;

            for (int d = 0; d < 8; d++)
            {
                int nf = f + DirF[d], nr = r + DirR[d];
                while ((nf & ~7) == 0 && (nr & ~7) == 0)
                {
                    int p = Sq[nr * 8 + nf] * sign;
                    if (p != 0)
                    {
                        if (p > 0)
                        {
                            if (p == Pc.Queen) return true;
                            if (d < 4) { if (p == Pc.Rook) return true; }
                            else if (p == Pc.Bishop) return true;
                        }
                        break;
                    }
                    nf += DirF[d];
                    nr += DirR[d];
                }
            }
            return false;
        }

        public bool InCheck(bool white)
        {
            return IsAttacked(white ? WhiteKing : BlackKing, !white);
        }

        // ---------- move generation (pseudo-legal) ----------

        public int GenerateMoves(Move[] buf, bool capsOnly)
        {
            int n = 0;
            bool white = WhiteToMove;
            int sign = white ? 1 : -1;
            for (int from = 0; from < 64; from++)
            {
                int pc = Sq[from] * sign;
                if (pc <= 0) continue;
                switch (pc)
                {
                    case Pc.Pawn: n = GenPawn(buf, n, from, white, capsOnly); break;
                    case Pc.Knight: n = GenStep(buf, n, from, KnightTo[from], sign, capsOnly); break;
                    case Pc.King:
                        n = GenStep(buf, n, from, KingTo[from], sign, capsOnly);
                        if (!capsOnly) n = GenCastle(buf, n, white);
                        break;
                    case Pc.Bishop: n = GenSlide(buf, n, from, 4, 8, sign, capsOnly); break;
                    case Pc.Rook: n = GenSlide(buf, n, from, 0, 4, sign, capsOnly); break;
                    case Pc.Queen: n = GenSlide(buf, n, from, 0, 8, sign, capsOnly); break;
                }
            }
            return n;
        }

        int GenStep(Move[] buf, int n, int from, int[] targets, int sign, bool capsOnly)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                int t = targets[i];
                int target = Sq[t] * sign;
                if (target > 0) continue;
                if (target < 0 || !capsOnly) buf[n++] = new Move(from, t, 0, 0);
            }
            return n;
        }

        int GenSlide(Move[] buf, int n, int from, int d0, int d1, int sign, bool capsOnly)
        {
            int f0 = from & 7, r0 = from >> 3;
            for (int d = d0; d < d1; d++)
            {
                int f = f0 + DirF[d], r = r0 + DirR[d];
                while ((f & ~7) == 0 && (r & ~7) == 0)
                {
                    int t = r * 8 + f;
                    int target = Sq[t] * sign;
                    if (target > 0) break;
                    if (target < 0) { buf[n++] = new Move(from, t, 0, 0); break; }
                    if (!capsOnly) buf[n++] = new Move(from, t, 0, 0);
                    f += DirF[d];
                    r += DirR[d];
                }
            }
            return n;
        }

        int GenPawn(Move[] buf, int n, int from, bool white, bool capsOnly)
        {
            int dir = white ? 8 : -8;
            int startRank = white ? 1 : 6;
            int promoRank = white ? 7 : 0;
            int sign = white ? 1 : -1;
            int f = from & 7, r = from >> 3;
            int one = from + dir;
            bool promo = (one >> 3) == promoRank;

            if (Sq[one] == 0)
            {
                if (promo)
                {
                    n = AddPromos(buf, n, from, one, capsOnly);
                }
                else if (!capsOnly)
                {
                    buf[n++] = new Move(from, one, 0, 0);
                    if (r == startRank && Sq[one + dir] == 0)
                        buf[n++] = new Move(from, one + dir, 0, Move.DoublePush);
                }
            }

            for (int df = -1; df <= 1; df += 2)
            {
                int nf = f + df;
                if (nf < 0 || nf > 7) continue;
                int t = one + df;
                int target = Sq[t] * sign;
                if (target < 0)
                {
                    if (promo) n = AddPromos(buf, n, from, t, capsOnly);
                    else buf[n++] = new Move(from, t, 0, 0);
                }
                else if (target == 0 && t == Ep)
                {
                    buf[n++] = new Move(from, t, 0, Move.EnPassant);
                }
            }
            return n;
        }

        static int AddPromos(Move[] buf, int n, int from, int to, bool queenOnly)
        {
            buf[n++] = new Move(from, to, Pc.Queen, 0);
            if (!queenOnly)
            {
                buf[n++] = new Move(from, to, Pc.Rook, 0);
                buf[n++] = new Move(from, to, Pc.Bishop, 0);
                buf[n++] = new Move(from, to, Pc.Knight, 0);
            }
            return n;
        }

        int GenCastle(Move[] buf, int n, bool white)
        {
            int b = white ? 0 : 56;           // rank offset
            int rook = white ? Pc.Rook : -Pc.Rook;
            int king = white ? Pc.King : -Pc.King;
            if (Sq[b + 4] != king) return n;
            int kBit = white ? CastleWK : CastleBK;
            int qBit = white ? CastleWQ : CastleBQ;
            bool byWhite = !white;

            if ((Castle & kBit) != 0 && Sq[b + 5] == 0 && Sq[b + 6] == 0 && Sq[b + 7] == rook
                && !IsAttacked(b + 4, byWhite) && !IsAttacked(b + 5, byWhite) && !IsAttacked(b + 6, byWhite))
                buf[n++] = new Move(b + 4, b + 6, 0, Move.CastleK);

            if ((Castle & qBit) != 0 && Sq[b + 3] == 0 && Sq[b + 2] == 0 && Sq[b + 1] == 0 && Sq[b + 0] == rook
                && !IsAttacked(b + 4, byWhite) && !IsAttacked(b + 3, byWhite) && !IsAttacked(b + 2, byWhite))
                buf[n++] = new Move(b + 4, b + 2, 0, Move.CastleQ);
            return n;
        }

        // ---------- make / unmake ----------

        public UndoInfo MakeMove(Move m)
        {
            UndoInfo u;
            u.Captured = Sq[m.To];
            u.Castle = Castle;
            u.Ep = Ep;
            u.Half = Half;
            u.Hash = Hash;

            int from = m.From, to = m.To;
            int piece = Sq[from];
            bool white = piece > 0;
            int abs = white ? piece : -piece;

            ulong h = Hash ^ ZSide ^ ZCastle[Castle];
            if (Ep >= 0) h ^= ZEp[Ep & 7];

            h ^= ZPiece[PIdx(piece), from];
            Sq[from] = 0;
            if (u.Captured != 0) h ^= ZPiece[PIdx(u.Captured), to];

            int placed = piece;
            if (m.Promo != 0) placed = white ? m.Promo : -m.Promo;

            if ((m.Flags & Move.EnPassant) != 0)
            {
                int cap = white ? to - 8 : to + 8;
                u.Captured = Sq[cap];
                Sq[cap] = 0;
                h ^= ZPiece[PIdx(u.Captured), cap];
            }

            Sq[to] = placed;
            h ^= ZPiece[PIdx(placed), to];

            if ((m.Flags & (Move.CastleK | Move.CastleQ)) != 0)
            {
                int rook = white ? Pc.Rook : -Pc.Rook;
                int rf, rt;
                if ((m.Flags & Move.CastleK) != 0) { rf = to + 1; rt = to - 1; }
                else { rf = to - 2; rt = to + 1; }
                Sq[rf] = 0;
                Sq[rt] = rook;
                h ^= ZPiece[PIdx(rook), rf] ^ ZPiece[PIdx(rook), rt];
            }

            if (abs == Pc.King)
            {
                if (white) WhiteKing = to; else BlackKing = to;
            }

            Ep = -1;
            if ((m.Flags & Move.DoublePush) != 0)
            {
                // only record the en passant square if an enemy pawn can actually try to capture
                int enemyPawn = white ? -Pc.Pawn : Pc.Pawn;
                int tf = to & 7;
                if ((tf > 0 && Sq[to - 1] == enemyPawn) || (tf < 7 && Sq[to + 1] == enemyPawn))
                    Ep = (from + to) / 2;
            }

            Castle &= CastleMask[from] & CastleMask[to];
            h ^= ZCastle[Castle];
            if (Ep >= 0) h ^= ZEp[Ep & 7];

            if (abs == Pc.Pawn || u.Captured != 0) Half = 0; else Half++;
            if (!white) Full++;
            WhiteToMove = !WhiteToMove;
            Hash = h;
            return u;
        }

        public void UnmakeMove(Move m, UndoInfo u)
        {
            WhiteToMove = !WhiteToMove;
            bool white = WhiteToMove;
            int from = m.From, to = m.To;
            int piece = Sq[to];
            if (m.Promo != 0) piece = white ? Pc.Pawn : -Pc.Pawn;
            Sq[from] = piece;

            if ((m.Flags & Move.EnPassant) != 0)
            {
                Sq[to] = 0;
                Sq[white ? to - 8 : to + 8] = u.Captured;
            }
            else
            {
                Sq[to] = u.Captured;
            }

            if ((m.Flags & Move.CastleK) != 0)
            {
                Sq[to + 1] = Sq[to - 1];
                Sq[to - 1] = 0;
            }
            else if ((m.Flags & Move.CastleQ) != 0)
            {
                Sq[to - 2] = Sq[to + 1];
                Sq[to + 1] = 0;
            }

            if (piece == Pc.King) WhiteKing = from;
            else if (piece == -Pc.King) BlackKing = from;

            Castle = u.Castle;
            Ep = u.Ep;
            Half = u.Half;
            Hash = u.Hash;
            if (!white) Full--;
        }

        // ---------- legal moves ----------

        /// <summary>Makes the pseudo-legal move and reports whether the mover's king is safe. The move stays made.</summary>
        public bool MakeIfLegal(Move m, out UndoInfo u)
        {
            bool white = WhiteToMove;
            u = MakeMove(m);
            if (IsAttacked(white ? WhiteKing : BlackKing, !white))
            {
                UnmakeMove(m, u);
                return false;
            }
            return true;
        }

        public List<Move> LegalMoves()
        {
            Move[] buf = new Move[256];
            int n = GenerateMoves(buf, false);
            List<Move> list = new List<Move>(n);
            for (int i = 0; i < n; i++)
            {
                UndoInfo u;
                if (MakeIfLegal(buf[i], out u))
                {
                    UnmakeMove(buf[i], u);
                    list.Add(buf[i]);
                }
            }
            return list;
        }

        public bool HasLegalMove()
        {
            Move[] buf = new Move[256];
            int n = GenerateMoves(buf, false);
            for (int i = 0; i < n; i++)
            {
                UndoInfo u;
                if (MakeIfLegal(buf[i], out u))
                {
                    UnmakeMove(buf[i], u);
                    return true;
                }
            }
            return false;
        }

        public bool InsufficientMaterial()
        {
            int minors = 0, knights = 0, lightB = 0, darkB = 0;
            for (int s = 0; s < 64; s++)
            {
                int p = Math.Abs(Sq[s]);
                switch (p)
                {
                    case Pc.Pawn:
                    case Pc.Rook:
                    case Pc.Queen:
                        return false;
                    case Pc.Knight:
                        minors++; knights++;
                        break;
                    case Pc.Bishop:
                        minors++;
                        if ((((s & 7) + (s >> 3)) & 1) == 0) darkB++; else lightB++;
                        break;
                }
            }
            if (minors <= 1) return true;
            // only bishops, all on squares of one colour
            if (knights == 0 && (lightB == 0 || darkB == 0)) return true;
            return false;
        }
    }
}
