// Console test harness for the chess engine (no UI).
// Build & run:  tests\run-tests.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ChessGame;

static class Tests
{
    static int failures;

    static void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
    }

    static long Perft(Position p, int depth, Move[][] bufs)
    {
        if (depth == 0) return 1;
        Move[] buf = bufs[depth];
        int n = p.GenerateMoves(buf, false);
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            UndoInfo u;
            if (!p.MakeIfLegal(buf[i], out u)) continue;
            if (p.Hash != p.ComputeHash()) { failures++; Console.WriteLine("  FAIL: hash mismatch after " + buf[i]); }
            total += Perft(p, depth - 1, bufs);
            p.UnmakeMove(buf[i], u);
        }
        return total;
    }

    static void RunPerft(string name, string fen, params long[] expected)
    {
        Console.WriteLine("perft " + name);
        Move[][] bufs = new Move[10][];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new Move[256];
        for (int d = 1; d <= expected.Length; d++)
        {
            Position p = Position.FromFen(fen);
            string before = p.ToFen();
            ulong h = p.Hash;
            long got = Perft(p, d, bufs);
            Check(got == expected[d - 1], name + " depth " + d + ": expected " + expected[d - 1] + " got " + got);
            Check(p.ToFen() == before && p.Hash == h, name + ": position restored after unmake");
            Console.WriteLine("  depth " + d + " = " + got + (got == expected[d - 1] ? "  ok" : "  MISMATCH"));
        }
    }

    static Move Find(Game g, string uci)
    {
        foreach (Move m in g.Legal)
            if (m.ToString() == uci) return m;
        throw new Exception("illegal move in test: " + uci);
    }

    static void Play(Game g, params string[] ucis)
    {
        foreach (string u in ucis) g.Play(Find(g, u));
    }

    static void TestGameRules()
    {
        Console.WriteLine("game rules");

        // fool's mate + SAN
        Game g = new Game();
        Play(g, "f2f3", "e7e5", "g2g4", "d8h4");
        Check(g.Status == GameStatus.Checkmate, "fool's mate detected");
        Check(string.Join(" ", g.San.ToArray()) == "f3 e5 g4 Qh4#", "fool's mate SAN: " + string.Join(" ", g.San.ToArray()));
        g.Undo();
        Check(g.Status == GameStatus.Playing && g.San.Count == 3, "undo restores play");

        // castling, en passant, promotion SAN
        g = new Game();
        Play(g, "e2e4", "a7a6", "e4e5", "d7d5", "e5d6", "a6a5", "g1f3", "a5a4", "f1c4", "a4a3", "e1g1");
        Check(g.San[4] == "exd6", "en passant SAN: " + g.San[4]);
        Check(g.San[10] == "O-O", "castle SAN: " + g.San[10]);
        Check(g.Pos.Sq[6] == Pc.King && g.Pos.Sq[5] == Pc.Rook, "castle placed king and rook");

        // promotion with capture and disambiguation
        Position pos = Position.FromFen("1n2k3/P7/8/8/8/8/8/R3K2R w KQ - 0 1");
        int promos = 0;
        foreach (Move m in pos.LegalMoves()) if (m.Promo != 0) promos++;
        Check(promos == 8, "a7 pawn: 4 promotions on a8 + 4 capturing b8: " + promos);

        // stalemate
        Position st = Position.FromFen("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1");
        Check(!st.HasLegalMove() && !st.InCheck(false), "stalemate position");

        // insufficient material
        Check(Position.FromFen("8/8/4k3/8/8/3K4/8/8 w - - 0 1").InsufficientMaterial(), "K vs K");
        Check(Position.FromFen("8/8/4k3/8/8/3KB3/8/8 w - - 0 1").InsufficientMaterial(), "K+B vs K");
        Check(Position.FromFen("8/8/4k3/8/8/3KN3/8/8 w - - 0 1").InsufficientMaterial(), "K+N vs K");
        Check(!Position.FromFen("8/8/4k3/8/8/3KBB2/8/8 w - - 0 1").InsufficientMaterial(), "K+B+B (opposite colours) can mate");
        Check(!Position.FromFen("8/8/4k3/8/8/3KP3/8/8 w - - 0 1").InsufficientMaterial(), "K+P vs K");

        // threefold repetition
        g = new Game();
        Play(g, "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1");
        Check(g.Status == GameStatus.Playing, "not yet repetition");
        Play(g, "f6g8");
        Check(g.Status == GameStatus.DrawRepetition, "threefold repetition: " + g.Status);
        g.Undo();
        Check(g.Status == GameStatus.Playing, "undo leaves repetition");

        // castling rights are lost after a rook move and cannot castle through check
        Position c = Position.FromFen("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        int castles = 0;
        foreach (Move m in c.LegalMoves()) if ((m.Flags & (Move.CastleK | Move.CastleQ)) != 0) castles++;
        Check(castles == 2, "both castles available: " + castles);
        Position c2 = Position.FromFen("r3k2r/8/8/8/8/8/5r2/R3K2R w KQkq - 0 1");
        castles = 0;
        foreach (Move m in c2.LegalMoves()) if ((m.Flags & (Move.CastleK | Move.CastleQ)) != 0) castles++;
        Check(castles == 1, "king side castle blocked by attacked f1: " + castles);
    }

    static void TestEngine()
    {
        Console.WriteLine("engine");
        Engine e = new Engine();

        // mate in one: Qh5# style -- white Ra1 + Kg6 vs Kg8: Ra8#
        Position p = Position.FromFen("6k1/8/6K1/8/8/8/8/R7 w - - 0 1");
        Move m = e.FindBestMove(p, new ulong[] { p.Hash }, 4, 3000, 0);
        Check(m.ToString() == "a1a8", "mate in one found: " + m);

        // mate in two (Lasker): Q+K vs K
        p = Position.FromFen("7k/8/5K2/8/8/8/8/6Q1 w - - 0 1");
        m = e.FindBestMove(p, new ulong[] { p.Hash }, 6, 5000, 0);
        Check(m.ToString() == "g1g7", "Q+K v K: mate in one found: " + m);
        UndoInfo u = p.MakeMove(m);
        Check(p.InCheck(false) && !p.HasLegalMove(), "g1g7 is checkmate");
        p.UnmakeMove(m, u);

        // avoids hanging the queen: black can win a free queen
        p = Position.FromFen("rnb1kbnr/pppp1ppp/8/4p3/4P2q/5N2/PPPP1PPP/RNBQKB1R w KQkq - 0 1");
        m = e.FindBestMove(p, new ulong[] { p.Hash }, 4, 3000, 0);
        Console.WriteLine("  move vs. queen attack: " + m + " nodes=" + e.LastNodes);

        // speed (fresh engine, middlegame position, 3s budget)
        e = new Engine();
        Stopwatch sw = Stopwatch.StartNew();
        p = Position.FromFen("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10");
        m = e.FindBestMove(p, new ulong[] { p.Hash }, 12, 3000, 0);
        sw.Stop();
        Console.WriteLine("  startpos depth " + e.LastDepth + ": " + m + "  nodes=" + e.LastNodes + "  time=" + sw.ElapsedMilliseconds + "ms  nps=" + (e.LastNodes * 1000 / Math.Max(1, sw.ElapsedMilliseconds)));
    }

    static void SelfPlay(int depthWhite, int depthBlack, int maxPlies)
    {
        Console.WriteLine("self-play (depth " + depthWhite + " vs " + depthBlack + ")");
        Game g = new Game();
        Engine e = new Engine();
        Stopwatch sw = Stopwatch.StartNew();
        int plies = 0;
        while (g.Status == GameStatus.Playing && plies < maxPlies)
        {
            int d = g.Pos.WhiteToMove ? depthWhite : depthBlack;
            Move m = e.FindBestMove(g.Pos.Clone(), g.HashHistory(), d, 4000, 0);
            bool legal = false;
            foreach (Move l in g.Legal) if (l.Same(m)) { legal = true; m = l; }
            Check(legal, "engine returned an illegal move " + m + " in " + g.Pos.ToFen());
            if (!legal) break;
            g.Play(m);
            plies++;
        }
        Console.WriteLine("  " + plies + " plies, status " + g.Status + ", " + sw.ElapsedMilliseconds + "ms");
        Console.WriteLine("  " + string.Join(" ", g.San.ToArray()));
        Check(g.Pos.Hash == g.Pos.ComputeHash(), "hash consistent at end of game");
        while (g.Undo()) { }
        Check(g.Pos.ToFen() == Position.StartFen, "undoing the whole game restores the start position");
    }

    static int Main(string[] args)
    {
        // well-known perft positions (chessprogramming.org)
        RunPerft("startpos", Position.StartFen, 20, 400, 8902, 197281, 4865609);
        RunPerft("kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 48, 2039, 97862, 4085603);
        RunPerft("position3", "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 14, 191, 2812, 43238, 674624);
        RunPerft("position4", "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 6, 264, 9467, 422333);
        RunPerft("position5", "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 44, 1486, 62379, 2103487);
        RunPerft("position6", "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", 46, 2079, 89890);

        TestGameRules();
        TestEngine();
        SelfPlay(3, 2, 120);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}
