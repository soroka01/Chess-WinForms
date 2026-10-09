using System;
using System.Collections.Generic;

namespace ChessGame
{
    /// <summary>
    /// Where a piece may be sent as a queued move (premove). While the opponent is thinking the position is going to change,
    /// so only the piece's geometry is checked here: blockers and checks are judged when the move is finally played.
    /// </summary>
    public static class Premoves
    {
        public static List<int> Targets(Position pos, int from)
        {
            List<int> result = new List<int>();
            int piece = pos.Sq[from];
            if (piece == 0) return result;
            bool white = piece > 0;
            int type = Math.Abs(piece);
            int f0 = from & 7, r0 = from >> 3;

            for (int to = 0; to < 64; to++)
            {
                if (to == from) continue;
                int dest = pos.Sq[to];
                if (dest != 0 && (dest > 0) == white) continue;   // never onto one of our own pieces

                int df = (to & 7) - f0, dr = (to >> 3) - r0;
                int adf = Math.Abs(df), adr = Math.Abs(dr);
                bool ok;
                switch (type)
                {
                    case Pc.Knight:
                        ok = (adf == 1 && adr == 2) || (adf == 2 && adr == 1);
                        break;
                    case Pc.Bishop:
                        ok = adf == adr;
                        break;
                    case Pc.Rook:
                        ok = df == 0 || dr == 0;
                        break;
                    case Pc.Queen:
                        ok = adf == adr || df == 0 || dr == 0;
                        break;
                    case Pc.King:
                        ok = (adf <= 1 && adr <= 1)
                            || (adr == 0 && adf == 2 && f0 == 4 && r0 == (white ? 0 : 7));   // castling
                        break;
                    default:   // pawn
                        int dir = white ? 1 : -1;
                        int startRank = white ? 1 : 6;
                        ok = (df == 0 && dr == dir)
                            || (df == 0 && dr == 2 * dir && r0 == startRank)
                            || (adf == 1 && dr == dir && (dest != 0 || to == pos.Ep));
                        break;
                }
                if (ok) result.Add(to);
            }
            return result;
        }
    }
}
