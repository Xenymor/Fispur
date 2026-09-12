
using FispurEngine.API;
using FispurEngine;

internal class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            string[] argTokens = string.Join(' ', args).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int benchIdx = Array.FindIndex(argTokens, t => t.Equals("bench", StringComparison.OrdinalIgnoreCase));

            if (benchIdx != -1)
            {
                Bench.Run(benchIdx + 1 < argTokens.Length && int.TryParse(argTokens[benchIdx + 1], out int argDepth)
                    ? argDepth
                    : Bench.DEFAULT_DEPTH);
                return;
            }
        }

        Type botType = fispur.GetType();
        int hashMb = Fispur.DEFAULT_HASH_MB;
        FispurEngine.Chess.Board tempBoard = new FispurEngine.Chess.Board();
        tempBoard.LoadStartPosition();
        Board board = new Board(tempBoard);
        while (true)
        {
            string command = Console.ReadLine();

            if (string.IsNullOrEmpty(command))
                continue;

            string[] tokens = command.Split(' ');

            switch (tokens[0])
            {
                case "uci":
                    Console.WriteLine("id name " + fispur.GetName());
                    Console.WriteLine("id author " + fispur.GetAuthor());
                    Console.WriteLine("option name Hash type spin default "
                        + Fispur.DEFAULT_HASH_MB
                        + " min 1 max " + Fispur.MAX_HASH_MB);
                    Console.WriteLine("option name Threads type spin default 1 min 1 max 1");
                    foreach (SpsaOption option in SpsaOptions)
                    {
                        Console.WriteLine("option name " + option.Name + " type spin default "
                            + option.Get() + " min " + option.Min + " max " + option.Max);
                    }
                    Console.WriteLine("uciok");
                    break;

                case "setoption":
                    StopAndWait();
                    int nameIdx = Array.IndexOf(tokens, "name");
                    int valueIdx = Array.IndexOf(tokens, "value");

                    if (nameIdx != -1 && valueIdx > nameIdx)
                    {
                        string optionName = string.Join(" ", tokens, nameIdx + 1, valueIdx - nameIdx - 1);
                        string optionValue = string.Join(" ", tokens, valueIdx + 1, tokens.Length - valueIdx - 1);

                        if (optionName.Equals("Hash", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(optionValue, out int mb))
                        {
                            hashMb = Math.Clamp(mb, 1, Fispur.MAX_HASH_MB);
                            fispur.SetHashSize(hashMb);
                        }
                        else if (int.TryParse(optionValue, out int tuned))
                        {
                            foreach (SpsaOption option in SpsaOptions)
                            {
                                if (optionName.Equals(option.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    option.Set(Math.Clamp(tuned, option.Min, option.Max));
                                    break;
                                }
                            }
                        }
                    }
                    break;

                case "ucinewgame":
                    StopAndWait();
                    fispur.NewGame();
                    tempBoard = new FispurEngine.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new Board(tempBoard);
                    break;

                case "isready":
                    Console.WriteLine("readyok");
                    break;

                case "bench":
                    StopAndWait();
                    Bench.Run(tokens.Length > 1 && int.TryParse(tokens[1], out int benchDepth)
                        ? benchDepth
                        : Bench.DEFAULT_DEPTH);
                    break;

                case "spsa":
                    PrintSpsaInputs();
                    break;

                case "quit":
                    StopAndWait();
                    Environment.Exit(0);
                    break;

                case "position":
                    StopAndWait();
                    int moveStart = Array.IndexOf(tokens, "moves");

                    tempBoard = new FispurEngine.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new Board(tempBoard);

                    if (tokens.Length >= 2 && tokens[1].Equals("startpos"))
                    {
                        board.board.LoadStartPosition();
                    }
                    else if (tokens.Length >= 3 && tokens[1].Equals("fen"))
                    {
                        int fenEnd = moveStart == -1 ? tokens.Length : moveStart;
                        string fen = string.Join(" ", tokens, 2, fenEnd - 2);
                        board.board.LoadPosition(fen);
                    }

                    if (moveStart != -1)
                    {
                        for (int i = moveStart + 1; i < tokens.Length; i++)
                        {
                            board.MakeMove(new Move(tokens[i], board));
                        }
                    }
                    break;

                case "go":
                    int wtime = 60_000;
                    int btime = 60_000;
                    int winc = 0;
                    int binc = 0;
                    int time = -1;
                    int depth = -1;
                    int nodes = -1;
                    bool infinite = false;

                    for (int i = 1; i < tokens.Length; i++)
                    {
                        bool hasValue = i + 1 < tokens.Length;
                        switch (tokens[i])
                        {
                            case "wtime": 
                                if (hasValue)
                                {
                                    wtime = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "btime":
                                if (hasValue)
                                {
                                    btime = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "winc":
                                if (hasValue)
                                {
                                    winc = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "binc":
                                if (hasValue)
                                {
                                    binc = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "time":
                                if (hasValue)
                                {
                                    time = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "movetime":
                                if (hasValue)
                                {
                                    time = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "depth":
                                if (hasValue)
                                {
                                    depth = int.Parse(tokens[i + 1]);
                                }
                                break;
                            case "infinite":
                                infinite = true;
                                break;
                            
                        }
                    }

                    bool whiteToMove = board.IsWhiteToMove;
                    int remaining = time != -1 ? -1 : (whiteToMove ? wtime : btime);
                    int oppRemaining = whiteToMove ? btime : wtime;
                    int increment = time != -1 ? 0 : (whiteToMove ? winc : binc);

                    var timer = new FispurEngine.API.Timer(remaining, oppRemaining, remaining, increment, time, infinite || depth != -1);

                    StartSearch(board, timer, depth);

                    break;

                case "stop":
                    StopAndWait();
                    break;
            }
        }
    }

    private sealed class SpsaOption
    {
        public string Name { get; }
        public int Min { get; }
        public int Max { get; }
        public Func<int> Get { get; }
        public Action<int> Set { get; }
        public bool IsPoisoned { get; }

        public SpsaOption(string name, int min, int max, Func<int> get, Action<int> set, bool isPoisoned = false)
        {
            Name = name;
            Min = min;
            Max = max;
            Get = get;
            Set = set;
            IsPoisoned = isPoisoned;
        }

    }

    private static readonly SpsaOption[] SpsaOptions =
    {
        new("LmrMinDepth",      1,      16,     () => Fispur.LmrMinDepth,       v => Fispur.LmrMinDepth = v,    true),
        new("LmrMinMoves",      1,      16,     () => Fispur.LmrMinMoves,       v => Fispur.LmrMinMoves = v,    true),
        new("LmrBase",          0,      400,    () => Fispur.LmrBase,           v => Fispur.LmrBase = v),
        new("LmrDivisor",       50,     1000,   () => Fispur.LmrDivisor,        v => Fispur.LmrDivisor = v),
        new("RfpMaxDepth",      1,      16,     () => Fispur.RfpMaxDepth,       v => Fispur.RfpMaxDepth = v,    true),
        new("RfpMargin",        10,     500,    () => Fispur.RfpMargin,         v => Fispur.RfpMargin = v),
        new("HistoryDivisor",   512,    65536,  () => Fispur.HistoryDivisor,    v => Fispur.HistoryDivisor = v),
        new("MaxHistBonus",     10,     16000,  () => Fispur.MaxHistBonus,      v => Fispur.MaxHistBonus = v),
        new("HistBonusMult",    50,     800,    () => Fispur.HistBonusMult,     v => Fispur.HistBonusMult = v),
        new("HistBonusBase",    -600,   200,    () => Fispur.HistBonusBase,     v => Fispur.HistBonusBase = v),
        new("NMPMinDepth",      1,      8,      () => Fispur.NMPMinDepth,       v => Fispur.NMPMinDepth = v,    true),
        new("NMPReductionB",    1,      6,      () => Fispur.NMPReductionB,     v => Fispur.NMPReductionB = v,  true),
        new("NMPReductionDiv",  2,      10,     () => Fispur.NMPReductionDiv,   v => Fispur.NMPReductionDiv = v,true),
        new("ASPWindowDelta",   10,     200,    () => Fispur.ASPWindowDelta,    v => Fispur.ASPWindowDelta = v),
        new("ASPWindowReset",   100,    2000,   () => Fispur.ASPWindowReset,    v => Fispur.ASPWindowReset = v),
    };

    static void PrintSpsaInputs()
    {
        foreach (SpsaOption option in SpsaOptions)
        {
            double cEnd = option.IsPoisoned ? 0.1 : (option.Max - option.Min) / 20.0;
            double rEnd = option.IsPoisoned ? 0.001 : 0.002;
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}, int, {1:0.0}, {2:0.0}, {3:0.0}, {4:0.0##}, {5:0.0##}",
                option.Name, option.Get(), option.Min, option.Max, cEnd, rEnd));
        }
    }

    static readonly object outLock = new();
    static Fispur fispur = new Fispur();
    static Thread? searchThread;

    static void Say(string s)
    {
        lock (outLock)
        {
            Console.WriteLine(s);
        }
    }

    static bool Searching
    {
        get
        {
            return searchThread is { IsAlive: true };
        }
    }

    static void StopAndWait()
    {
        if (!Searching) return;
        fispur.Stop();
        searchThread!.Join();
        searchThread = null;
    }

    static void StartSearch(Board board, FispurEngine.API.Timer timer, int depth)
    {
        StopAndWait();
        fispur.PrepareSearch();

        searchThread = new Thread(() =>
        {
            try
            {
                var result = fispur.Think(board, timer, depth);
                Say("bestmove " + Uci(result.move));
            }
            catch (Exception e)
            {
                Say("info string search crashed: " + e);
                Say("bestmove 0000");
            }
        }, 32 * 1024 * 1024)
        { IsBackground = true };

        searchThread.Start();
    }

    private static string Uci(Move move)
    {
        string bestMoveString = move.ToString();
        string bestMoveFormattedString = bestMoveString.Substring(7, bestMoveString.Length - 8);
        return bestMoveFormattedString;
    }
}

