using FispurEngine;
using System.Globalization;
using System.Runtime.InteropServices;
using ApiBoard = FispurEngine.API.Board;

internal static class EvalScale
{
    private const int PER_BUCKET = 512;
    private const double MAX_REF_EVAL = 1500;
    private const int OUTPUT_BUCKETS = 8;
    private const int SEED = 1_024;

    public static void Run(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("gen", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: evalscale gen <out> <positions file> [<positions file>...]");
                return;
            }

            Generate(args[1], args[2..]);
            return;
        }

        Print();
    }

    private static void Print()
    {
        NNUE.CalibrationResult c = NNUE.calibration;

        if (c.positions == 0)
        {
            Console.WriteLine("info string eval scale 1.000 (no calibration reference embedded)");
            return;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"info string eval scale {NNUE.evalScale / (double)NNUE.EVAL_SCALE_ONE:0.000} ({NNUE.evalScale}/{NNUE.EVAL_SCALE_ONE})"
            + $" fitted {c.fittedScale:0.0000} on {c.positions} positions, correlation {c.correlation:0.0000}, rmse {c.rmse:0.0} cp"));
    }

    private static void Generate(string output, string[] inputs)
    {
        var seen = new HashSet<string>();
        var fens = new List<string>();
        foreach (string input in inputs)
        {
            foreach (string line in File.ReadLines(input))
            {
                if (ParseFen(line) is string fen && seen.Add(fen))
                {
                    fens.Add(fen);
                }
            }
        }

        new Random(SEED).Shuffle(CollectionsMarshal.AsSpan(fens));

        ApiBoard board = ApiBoard.CreateBoardFromFEN(FispurEngine.Chess.FenUtility.StartPositionFEN);
        var buckets = new List<string>[OUTPUT_BUCKETS];
        for (int i = 0; i < OUTPUT_BUCKETS; i++)
        {
            buckets[i] = new List<string>();
        }

        foreach (string fen in fens)
        {
            try
            {
                board.board.LoadPosition(fen);
            }
            catch (Exception)
            {
                continue;
            }

            int bucket = NNUE.outputBucket(board);
            if (bucket < 0 || bucket >= OUTPUT_BUCKETS || buckets[bucket].Count >= PER_BUCKET || board.board.IsInCheck())
            {
                continue;
            }

            double eval = NNUE.exactEval(board);
            if (Math.Abs(eval) > MAX_REF_EVAL)
            {
                continue;
            }

            buckets[bucket].Add(string.Create(CultureInfo.InvariantCulture, $"{fen} | {eval:0.00}"));
        }

        using (var writer = new StreamWriter(output))
        {
            foreach (List<string> bucket in buckets)
            {
                foreach (string line in bucket)
                {
                    writer.WriteLine(line);
                }
            }
        }

        Console.WriteLine("info string wrote " + buckets.Sum(b => b.Count) + " positions to " + output
            + ", per output bucket: " + string.Join(" ", buckets.Select(b => b.Count)));
    }

    private static string? ParseFen(string line)
    {
        int sep = line.IndexOf('|');
        string[] fields = (sep < 0 ? line : line[..sep]).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length >= 6 && int.TryParse(fields[4], out _) && int.TryParse(fields[5], out _))
        {
            return string.Join(' ', fields, 0, 6);
        }

        return fields.Length >= 4 ? string.Join(' ', fields, 0, 4) + " 0 1" : null;
    }
}
