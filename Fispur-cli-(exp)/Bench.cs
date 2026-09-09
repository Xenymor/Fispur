using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ApiBoard = FispurEngine.API.Board;
using ApiTimer = FispurEngine.API.Timer;

/// <summary>
/// OpenBench-compatible benchmark.
///
/// Searches a fixed set of positions to a fixed depth and prints the total node count
/// plus the resulting NPS. OpenBench uses the node count as a fingerprint of the build:
/// every worker benches the engine after compiling it and all of them must arrive at the
/// exact same number, otherwise the workload is rejected with "Wrong Bench".
///
/// Three things keep that number reproducible and none of them may be softened:
///   * the search runs to a fixed depth and never looks at the clock (infinite timer),
///   * the bench uses its own fixed hash size, independent of the Hash UCI option,
///   * every position starts from a fully reset engine (TT, history, aspiration seed).
/// </summary>
internal static class Bench
{
    /// <summary>Depth used when the caller does not pass one explicitly.</summary>
    public const int DEFAULT_DEPTH = 9;

    /// <summary>
    /// Hash size for the bench. Deliberately fixed: if this followed the Hash UCI option
    /// the bench value would change with the caller's settings and stop being a fingerprint.
    /// </summary>
    private const int HASH_MB = 16;

    /// <summary>
    /// The search recurses deeply, so the bench gets the same oversized stack that
    /// Program.StartSearch gives a normal search.
    /// </summary>
    private const int STACK_BYTES = 32 * 1024 * 1024;

    private static long totalNodes;
    private static long searchMillis;

    /// <summary>
    /// The benched positions. This list is part of the fingerprint: changing it, its order,
    /// or the default depth changes the bench value of every future commit.
    /// </summary>
    private static readonly string[] Positions =
    {
        // Openings and standard reference positions
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5Q2/PPPP1PPP/RNB1K1NR b KQkq - 3 3",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",

        // Middlegames (sampled from Fispur/resources/Fens.txt)
        "rn1q1rk1/pp2b1pp/3pbn2/4p3/8/1N1BB3/PPPN1PPP/R2Q1RK1 w - - 8 11",
        "r2qk2r/ppp1ppbp/1n2b1pn/4P3/3P4/2P1BN2/PP4PP/RN1QKB1R w KQkq - 1 9",
        "r1bq1rk1/1pp2ppp/2np1n2/2b1p1B1/p1B1P3/P1PP1Q2/1P1N1PPP/R3K1NR w KQ - 0 9",
        "r2q1rk1/ppp2p2/2np1n1p/2bNp1p1/2B1P1b1/3P1NB1/PPP2PPP/R2QK2R w KQ - 4 10",
        "r2q1rk1/pbp1bppp/1p1p1n2/4p3/2BP4/2N1P3/PPP1QPPP/R1B2RK1 w - - 0 11",
        "rn3rk1/ppb2ppp/2p2q2/3p3b/1P1P4/P1N1PN1P/4BPP1/R2QK2R w KQ - 1 14",
        "r1b1kb1r/2qn1ppp/p2ppn2/1p4B1/3NPP2/2N2Q2/PPP3PP/2KR1B1R w kq - 0 10",
        "2rqrnk1/pb3ppp/1p1bpn2/1P6/3P4/1BN2N2/PB3PPP/R2QR1K1 w - - 5 15",
        "2rq1rk1/3bbp1p/ppnppnp1/1Np5/2PP4/4PN2/PPQBBPPP/1R3RK1 w - - 0 13",
        "r2q1rk1/pp1n1ppp/4p3/3pP3/PbBP2b1/5N2/1P2QPPP/R1B2RK1 w - - 0 13",
        "rn2k2r/pb3pp1/1ppbpq1p/1B6/3P4/4BN2/PPPQ1PPP/R3K2R w KQkq - 0 11",
        "rnbq1rk1/ppp2pbp/3p1np1/8/8/2N2NP1/PPPPQPBP/R1B1K2R w KQ - 2 9",

        // Endgames
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "8/k7/3p4/p2P1p2/P2P1P2/8/8/K7 w - - 0 1",
        "8/8/8/3k4/8/8/3K4/3R4 w - - 0 1",
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
        "6k1/5ppp/8/8/8/8/5PPP/1Q4K1 w - - 0 1",
        "8/8/4k3/8/8/4K3/4P3/4R3 w - - 0 1",
    };

    /// <summary>
    /// Runs the benchmark and prints the summary line OpenBench parses.
    /// </summary>
    public static void Run(int depth)
    {
        if (depth <= 0)
            depth = DEFAULT_DEPTH;

        // Own thread: the default 1 MB stack is not enough for the search recursion.
        Thread searchThread = new Thread(() => SearchAll(depth), STACK_BYTES);
        searchThread.Start();
        searchThread.Join();

        long millis = Math.Max(1, searchMillis);
        long nps = totalNodes * 1000L / millis;

        Console.WriteLine();
        Console.WriteLine("Positions : " + Positions.Length);
        Console.WriteLine("Depth     : " + depth);
        Console.WriteLine("Time      : " + millis + " ms");

        // Must be the last line: OpenBench scans stdout backwards for "<n> nodes" / "<n> nps".
        Console.WriteLine(totalNodes + " nodes " + nps + " nps");
    }

    private static void SearchAll(int depth)
    {
        FispurEngine.Fispur engine = new FispurEngine.Fispur();
        engine.SetHashSize(HASH_MB);

        Stopwatch stopwatch = new Stopwatch();
        long nodes = 0;

        // The engine prints "info depth ..." while searching. Keep it out of the bench
        // output, but hold on to the real stdout for the per-position progress lines.
        TextWriter stdout = Console.Out;
        Console.SetOut(TextWriter.Null);

        try
        {
            for (int i = 0; i < Positions.Length; i++)
            {
                ApiBoard board = ApiBoard.CreateBoardFromFEN(Positions[i]);

                engine.ResetState();
                engine.PrepareSearch();

                // The three time values are irrelevant: "infinite" makes Think ignore the
                // clock entirely, so the search stops exactly at the requested depth.
                ApiTimer timer = new ApiTimer(-1, -1, -1, 0, -1, true);

                stopwatch.Start();
                engine.Think(board, timer, depth);
                stopwatch.Stop();

                nodes += engine.Nodes;

                stdout.WriteLine("[" + (i + 1).ToString().PadLeft(2) + "/" + Positions.Length + "]"
                    + engine.Nodes.ToString().PadLeft(11) + " nodes   " + Positions[i]);
            }
        }
        finally
        {
            Console.SetOut(stdout);
        }

        totalNodes = nodes;
        searchMillis = stopwatch.ElapsedMilliseconds;
    }
}
