using System;
using System.Diagnostics;

namespace ChessGame
{
    /// <summary>
    /// Computer opponent: iterative deepening negamax with alpha-beta, a transposition table,
    /// killer/history move ordering, quiescence search and a tapered piece-square evaluation.
    /// </summary>
    public sealed class Engine
    {
        public const int Mate = 30000;
        const int Inf = 32000;
        const int MaxPly = 64;
        const int TTBits = 19;
        const int TTMask = (1 << TTBits) - 1;
        const byte FlagExact = 0, FlagLower = 1, FlagUpper = 2;

        struct TTEntry
        {
            public ulong Key;
            public int Score;
            public short Depth;
            public byte Flag;
            public Move Best;
        }

        readonly TTEntry[] tt = new TTEntry[1 << TTBits];
        readonly Move[][] moveBuf = new Move[MaxPly + 2][];
        readonly int[][] scoreBuf = new int[MaxPly + 2][];
        readonly Move[,] killers = new Move[MaxPly + 2, 2];
        readonly int[,] history = new int[64, 64];
        readonly Random rng = new Random();

        Position pos;
        ulong[] stack;
        int sp;
        Stopwatch sw;
        long timeLimit;
        long nodes;
        bool stop;
        volatile bool cancel;

        public long LastNodes { get { return nodes; } }

        /// <summary>Depth of the last fully completed iteration of the previous search.</summary>
        public int LastDepth { get; private set; }

        public Engine()
        {
            for (int i = 0; i < moveBuf.Length; i++)
            {
                moveBuf[i] = new Move[256];
                scoreBuf[i] = new int[256];
            }
        }

        /// <summary>Forget everything learned (call when a new game starts).</summary>
        public void Reset()
        {
            Array.Clear(tt, 0, tt.Length);
        }

        /// <summary>Asks a running search to stop as soon as possible (thread-safe).</summary>
        public void Cancel()
        {
            cancel = true;
        }

        // ---------------------------------------------------------------- evaluation

        static readonly int[] Value = { 0, 100, 320, 330, 500, 900, 0 };
        static readonly int[] PhaseW = { 0, 0, 1, 1, 2, 4, 0 };
        static readonly int[] PassedBonus = { 0, 5, 10, 20, 35, 60, 100, 0 };

        // Tables are written from white's point of view, first row = rank 8.
        static readonly int[] PawnT = {
              0,  0,  0,  0,  0,  0,  0,  0,
             50, 50, 50, 50, 50, 50, 50, 50,
             10, 10, 20, 30, 30, 20, 10, 10,
              5,  5, 10, 25, 25, 10,  5,  5,
              0,  0,  0, 20, 20,  0,  0,  0,
              5, -5,-10,  0,  0,-10, -5,  5,
              5, 10, 10,-20,-20, 10, 10,  5,
              0,  0,  0,  0,  0,  0,  0,  0 };
        static readonly int[] KnightT = {
            -50,-40,-30,-30,-30,-30,-40,-50,
            -40,-20,  0,  0,  0,  0,-20,-40,
            -30,  0, 10, 15, 15, 10,  0,-30,
            -30,  5, 15, 20, 20, 15,  5,-30,
            -30,  0, 15, 20, 20, 15,  0,-30,
            -30,  5, 10, 15, 15, 10,  5,-30,
            -40,-20,  0,  5,  5,  0,-20,-40,
            -50,-40,-30,-30,-30,-30,-40,-50 };
        static readonly int[] BishopT = {
            -20,-10,-10,-10,-10,-10,-10,-20,
            -10,  0,  0,  0,  0,  0,  0,-10,
            -10,  0,  5, 10, 10,  5,  0,-10,
            -10,  5,  5, 10, 10,  5,  5,-10,
            -10,  0, 10, 10, 10, 10,  0,-10,
            -10, 10, 10, 10, 10, 10, 10,-10,
            -10,  5,  0,  0,  0,  0,  5,-10,
            -20,-10,-10,-10,-10,-10,-10,-20 };
        static readonly int[] RookT = {
              0,  0,  0,  0,  0,  0,  0,  0,
              5, 10, 10, 10, 10, 10, 10,  5,
             -5,  0,  0,  0,  0,  0,  0, -5,
             -5,  0,  0,  0,  0,  0,  0, -5,
             -5,  0,  0,  0,  0,  0,  0, -5,
             -5,  0,  0,  0,  0,  0,  0, -5,
             -5,  0,  0,  0,  0,  0,  0, -5,
              0,  0,  0,  5,  5,  0,  0,  0 };
        static readonly int[] QueenT = {
            -20,-10,-10, -5, -5,-10,-10,-20,
            -10,  0,  0,  0,  0,  0,  0,-10,
            -10,  0,  5,  5,  5,  5,  0,-10,
             -5,  0,  5,  5,  5,  5,  0, -5,
              0,  0,  5,  5,  5,  5,  0, -5,
            -10,  5,  5,  5,  5,  5,  0,-10,
            -10,  0,  5,  0,  0,  0,  0,-10,
            -20,-10,-10, -5, -5,-10,-10,-20 };
        static readonly int[] KingMg = {
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -20,-30,-30,-40,-40,-30,-30,-20,
            -10,-20,-20,-20,-20,-20,-20,-10,
             20, 20,  0,  0,  0,  0, 20, 20,
             20, 30, 10,  0,  0, 10, 30, 20 };
        static readonly int[] KingEg = {
            -50,-40,-30,-20,-20,-30,-40,-50,
            -30,-20,-10,  0,  0,-10,-20,-30,
            -30,-10, 20, 30, 30, 20,-10,-30,
            -30,-10, 30, 40, 40, 30,-10,-30,
            -30,-10, 30, 40, 40, 30,-10,-30,
            -30,-10, 20, 30, 30, 20,-10,-30,
            -30,-30,  0,  0,  0,  0,-30,-30,
            -50,-30,-30,-30,-30,-30,-30,-50 };

        static readonly int[][] Pst = { null, PawnT, KnightT, BishopT, RookT, QueenT, null };

        readonly int[] wPawns = new int[10];
        readonly int[] bPawns = new int[10];

        /// <summary>Static evaluation in centipawns from the point of view of the side to move.</summary>
        int Evaluate()
        {
            int[] sq = pos.Sq;
            int score = 0, mgK = 0, egK = 0, phase = 0;
            int wMat = 0, bMat = 0, wBish = 0, bBish = 0, wpn = 0, bpn = 0;

            for (int s = 0; s < 64; s++)
            {
                int p = sq[s];
                if (p == 0) continue;
                if (p > 0)
                {
                    if (p == Pc.King) { mgK += KingMg[s ^ 56]; egK += KingEg[s ^ 56]; continue; }
                    score += Value[p] + Pst[p][s ^ 56];
                    wMat += Value[p];
                    phase += PhaseW[p];
                    if (p == Pc.Bishop) wBish++;
                    else if (p == Pc.Pawn && wpn < 10) wPawns[wpn++] = s;
                }
                else
                {
                    int q = -p;
                    if (q == Pc.King) { mgK -= KingMg[s]; egK -= KingEg[s]; continue; }
                    score -= Value[q] + Pst[q][s];
                    bMat += Value[q];
                    phase += PhaseW[q];
                    if (q == Pc.Bishop) bBish++;
                    else if (q == Pc.Pawn && bpn < 10) bPawns[bpn++] = s;
                }
            }
            if (phase > 24) phase = 24;
            score += (mgK * phase + egK * (24 - phase)) / 24;

            if (wBish >= 2) score += 30;
            if (bBish >= 2) score -= 30;

            // passed pawns
            for (int i = 0; i < wpn; i++)
            {
                int f = wPawns[i] & 7, r = wPawns[i] >> 3;
                bool passed = true;
                for (int rr = r + 1; rr < 7 && passed; rr++)
                    for (int ff = Math.Max(0, f - 1); ff <= Math.Min(7, f + 1); ff++)
                        if (sq[rr * 8 + ff] == -Pc.Pawn) { passed = false; break; }
                if (passed) score += PassedBonus[r];
            }
            for (int i = 0; i < bpn; i++)
            {
                int f = bPawns[i] & 7, r = bPawns[i] >> 3;
                bool passed = true;
                for (int rr = r - 1; rr > 0 && passed; rr--)
                    for (int ff = Math.Max(0, f - 1); ff <= Math.Min(7, f + 1); ff++)
                        if (sq[rr * 8 + ff] == Pc.Pawn) { passed = false; break; }
                if (passed) score -= PassedBonus[7 - r];
            }

            // endgame: when clearly ahead, drive the enemy king to the edge and bring our king closer
            int diff = wMat - bMat;
            if (diff >= 200 || diff <= -200)
            {
                bool whiteAhead = diff > 0;
                int lk = whiteAhead ? pos.BlackKing : pos.WhiteKing;
                int wk = whiteAhead ? pos.WhiteKing : pos.BlackKing;
                int cf = Math.Max(3 - (lk & 7), (lk & 7) - 4);
                int cr = Math.Max(3 - (lk >> 3), (lk >> 3) - 4);
                int dist = Math.Abs((lk & 7) - (wk & 7)) + Math.Abs((lk >> 3) - (wk >> 3));
                int bonus = ((cf + cr) * 10 + (14 - dist) * 4) * (24 - phase) / 24;
                score += whiteAhead ? bonus : -bonus;
            }

            return (pos.WhiteToMove ? score : -score) + 10;
        }

        // ---------------------------------------------------------------- search

        void CheckTime()
        {
            if (cancel || sw.ElapsedMilliseconds >= timeLimit) stop = true;
        }

        bool IsRepetition()
        {
            int limit = Math.Max(0, sp - pos.Half);
            for (int i = sp - 2; i >= limit; i -= 2)
                if (stack[i] == pos.Hash) return true;
            return false;
        }

        static int ToTT(int score, int ply)
        {
            if (score > Mate - 1000) return score + ply;
            if (score < -Mate + 1000) return score - ply;
            return score;
        }

        static int FromTT(int score, int ply)
        {
            if (score > Mate - 1000) return score - ply;
            if (score < -Mate + 1000) return score + ply;
            return score;
        }

        void ScoreMoves(Move[] moves, int[] scores, int n, Move ttMove, int ply)
        {
            int[] sq = pos.Sq;
            for (int i = 0; i < n; i++)
            {
                Move m = moves[i];
                int s;
                if (!ttMove.IsNull && m.Same(ttMove))
                {
                    s = 1000000;
                }
                else
                {
                    int victim = Math.Abs(sq[m.To]);
                    if ((m.Flags & Move.EnPassant) != 0) victim = Pc.Pawn;
                    if (victim != 0) s = 100000 + victim * 10 - Math.Abs(sq[m.From]) + m.Promo;
                    else if (m.Promo != 0) s = 90000 + m.Promo;
                    else if (m.Same(killers[ply, 0])) s = 80000;
                    else if (m.Same(killers[ply, 1])) s = 79000;
                    else s = history[m.From, m.To];
                }
                scores[i] = s;
            }
        }

        static void PickBest(Move[] moves, int[] scores, int start, int n)
        {
            int best = start;
            for (int i = start + 1; i < n; i++)
                if (scores[i] > scores[best]) best = i;
            if (best != start)
            {
                Move m = moves[start]; moves[start] = moves[best]; moves[best] = m;
                int s = scores[start]; scores[start] = scores[best]; scores[best] = s;
            }
        }

        int Quiesce(int alpha, int beta, int ply)
        {
            if (stop) return 0;
            if ((++nodes & 1023) == 0) CheckTime();
            if (ply >= MaxPly) return Evaluate();

            int stand = Evaluate();
            if (stand >= beta) return stand;
            if (stand > alpha) alpha = stand;
            int best = stand;

            Move[] moves = moveBuf[ply];
            int[] scores = scoreBuf[ply];
            int n = pos.GenerateMoves(moves, true);
            ScoreMoves(moves, scores, n, default(Move), ply);

            for (int i = 0; i < n; i++)
            {
                PickBest(moves, scores, i, n);
                UndoInfo u;
                if (!pos.MakeIfLegal(moves[i], out u)) continue;
                int score = -Quiesce(-beta, -alpha, ply + 1);
                pos.UnmakeMove(moves[i], u);
                if (stop) return 0;
                if (score > best)
                {
                    best = score;
                    if (score > alpha)
                    {
                        alpha = score;
                        if (alpha >= beta) break;
                    }
                }
            }
            return best;
        }

        int Search(int depth, int alpha, int beta, int ply)
        {
            if (stop) return 0;
            if ((++nodes & 1023) == 0) CheckTime();
            if (pos.Half >= 100 || IsRepetition()) return 0;
            if (ply >= MaxPly) return Evaluate();

            bool inCheck = pos.InCheck(pos.WhiteToMove);
            if (inCheck) depth++;
            if (depth <= 0) return Quiesce(alpha, beta, ply);

            int origAlpha = alpha;
            Move ttMove = default(Move);
            int idx = (int)(pos.Hash & TTMask);
            if (tt[idx].Key == pos.Hash)
            {
                ttMove = tt[idx].Best;
                if (tt[idx].Depth >= depth)
                {
                    int s = FromTT(tt[idx].Score, ply);
                    byte flag = tt[idx].Flag;
                    if (flag == FlagExact) return s;
                    if (flag == FlagLower && s >= beta) return s;
                    if (flag == FlagUpper && s <= alpha) return s;
                }
            }

            Move[] moves = moveBuf[ply];
            int[] scores = scoreBuf[ply];
            int n = pos.GenerateMoves(moves, false);
            ScoreMoves(moves, scores, n, ttMove, ply);

            int legal = 0;
            int best = -Inf;
            Move bestMove = default(Move);

            for (int i = 0; i < n; i++)
            {
                PickBest(moves, scores, i, n);
                Move m = moves[i];
                UndoInfo u;
                if (!pos.MakeIfLegal(m, out u)) continue;
                legal++;
                stack[++sp] = pos.Hash;
                int score = -Search(depth - 1, -beta, -alpha, ply + 1);
                sp--;
                pos.UnmakeMove(m, u);
                if (stop) return 0;

                if (score > best)
                {
                    best = score;
                    bestMove = m;
                    if (score > alpha)
                    {
                        alpha = score;
                        if (alpha >= beta)
                        {
                            if (u.Captured == 0 && m.Promo == 0)
                            {
                                if (!m.Same(killers[ply, 0]))
                                {
                                    killers[ply, 1] = killers[ply, 0];
                                    killers[ply, 0] = m;
                                }
                                history[m.From, m.To] += depth * depth;
                                if (history[m.From, m.To] > 60000) DecayHistory();
                            }
                            break;
                        }
                    }
                }
            }

            if (legal == 0) return inCheck ? -Mate + ply : 0;

            tt[idx].Key = pos.Hash;
            tt[idx].Score = ToTT(best, ply);
            tt[idx].Depth = (short)depth;
            tt[idx].Best = bestMove;
            tt[idx].Flag = best <= origAlpha ? FlagUpper : (best >= beta ? FlagLower : FlagExact);
            return best;
        }

        void DecayHistory()
        {
            for (int a = 0; a < 64; a++)
                for (int b = 0; b < 64; b++)
                    history[a, b] >>= 1;
        }

        /// <summary>
        /// Picks a move for the side to move. Returns a null move if there is no legal move or the search was cancelled.
        /// </summary>
        /// <param name="gameHashes">Hashes of all positions in the game so far, last one = <paramref name="root"/>.</param>
        /// <param name="noise">Random +/- centipawn noise added to root move scores (weaker play).</param>
        public Move FindBestMove(Position root, ulong[] gameHashes, int maxDepth, int timeMs, int noise)
        {
            cancel = false;
            stop = false;
            nodes = 0;
            pos = root.Clone();
            stack = new ulong[gameHashes.Length + MaxPly + 8];
            Array.Copy(gameHashes, stack, gameHashes.Length);
            sp = gameHashes.Length - 1;
            if (sp < 0) { stack[0] = pos.Hash; sp = 0; }
            Array.Clear(killers, 0, killers.Length);
            Array.Clear(history, 0, history.Length);
            sw = Stopwatch.StartNew();
            timeLimit = timeMs;

            // legal root moves
            Move[] all = new Move[256];
            int total = pos.GenerateMoves(all, false);
            Move[] rootMoves = new Move[total];
            int count = 0;
            for (int i = 0; i < total; i++)
            {
                UndoInfo u;
                if (pos.MakeIfLegal(all[i], out u))
                {
                    pos.UnmakeMove(all[i], u);
                    rootMoves[count++] = all[i];
                }
            }
            if (count == 0) return default(Move);
            if (count == 1) return rootMoves[0];

            int[] rs = new int[count];
            ScoreMoves(rootMoves, rs, count, default(Move), 0);
            SortDescending(rootMoves, rs, count);

            Move best = rootMoves[0];
            LastDepth = 0;
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                int alpha = -Inf;
                int bestScore = -Inf;
                int bestIdx = 0;
                bool completed = true;
                int[] sc = new int[count];

                for (int i = 0; i < count; i++)
                {
                    Move m = rootMoves[i];
                    UndoInfo u = pos.MakeMove(m);
                    stack[++sp] = pos.Hash;
                    // with noise every move needs an exact score, so keep the window open
                    int a = noise > 0 ? -Inf : alpha;
                    int score = -Search(depth - 1, -Inf, -a, 1);
                    sp--;
                    pos.UnmakeMove(m, u);
                    if (stop) { completed = false; break; }

                    if (noise > 0) score += rng.Next(-noise, noise + 1);
                    sc[i] = score;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                        if (score > alpha) alpha = score;
                    }
                }

                if (!completed) break;
                best = rootMoves[bestIdx];
                LastDepth = depth;
                sc[bestIdx] = int.MaxValue;   // keep the best move first for the next iteration
                SortDescending(rootMoves, sc, count);

                if (bestScore >= Mate - 100) break;                           // found a forced mate
                if (depth < maxDepth && sw.ElapsedMilliseconds * 2 > timeLimit) break;   // next depth won't finish
            }

            if (cancel) return default(Move);
            return best;
        }

        static void SortDescending(Move[] moves, int[] scores, int n)
        {
            for (int i = 1; i < n; i++)
            {
                Move m = moves[i];
                int s = scores[i];
                int j = i - 1;
                while (j >= 0 && scores[j] < s)
                {
                    moves[j + 1] = moves[j];
                    scores[j + 1] = scores[j];
                    j--;
                }
                moves[j + 1] = m;
                scores[j + 1] = s;
            }
        }
    }
}
