
using FispurEngine.API;
using System.Reflection;

internal class Program
{
    /// <summary>
    /// One selectable engine snapshot. The bot is only instantiated when the
    /// version is actually selected, so heavy versions (NNUE loading) cost
    /// nothing until they are used.
    /// </summary>
    private sealed class EngineVersion
    {
        public readonly string Name;
        public readonly Type Type;

        public EngineVersion(string name, Type type)
        {
            Name = name;
            Type = type;
        }

        public IChessBot Create() => (IChessBot)Activator.CreateInstance(Type)!;

        public int DefaultHashMb => GetIntConst("DEFAULT_HASH_MB") ?? FALLBACK_DEFAULT_HASH_MB;

        public int MaxHashMb => GetIntConst("MAX_HASH_MB") ?? FALLBACK_MAX_HASH_MB;

        private int? GetIntConst(string fieldName)
        {
            FieldInfo? field = Type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field == null || field.FieldType != typeof(int))
                return null;
            return (int)field.GetValue(null)!;
        }
    }

    // Used for versions that do not expose hash constants at all.
    private const int FALLBACK_DEFAULT_HASH_MB = 256;
    private const int FALLBACK_MAX_HASH_MB = 1024;

    private const string DEFAULT_VERSION = "0.13.2";

    private static readonly EngineVersion[] Versions =
    [
        new EngineVersion("0.0.2", typeof(FispurEngine.src.Fispur.Fispur0_0_2)),
        new EngineVersion("0.0.3", typeof(Fispur0_0_3)),
        new EngineVersion("0.1.1", typeof(Fispur0_1_1)),
        new EngineVersion("0.2.0", typeof(Fispur0_2_0)),
        new EngineVersion("0.2.1", typeof(Fispur0_2_1)),
        new EngineVersion("0.3.0", typeof(FispurEngine.Fispur0_3_0.Fispur0_3_0)),
        new EngineVersion("0.3.1", typeof(FispurEngine.Fispur0_3_1.Fispur0_3_1)),
        new EngineVersion("0.3.2", typeof(FispurEngine.Fispur0_3_2.Fispur0_3_2)),
        new EngineVersion("0.3.3", typeof(FispurEngine.Fispur0_3_3.Fispur0_3_3)),
        new EngineVersion("0.3.4", typeof(FispurEngine.Fispur0_3_4.Fispur0_3_4)),
        new EngineVersion("0.4.0", typeof(FispurEngine.Fispur0_4_0.Fispur0_4_0)),
        new EngineVersion("0.5.0", typeof(FispurEngine.Fispur0_5_0.Fispur0_5_0)),
        new EngineVersion("0.5.1", typeof(FispurEngine.Fispur0_5_1.Fispur0_5_1)),
        new EngineVersion("0.6.0", typeof(FispurEngine.Fispur0_6_0.Fispur0_6_0)),
        new EngineVersion("0.7.0", typeof(FispurEngine.Fispur0_7_0.Fispur0_7_0)),
        new EngineVersion("0.8.0", typeof(FispurEngine.Fispur0_8_0.Fispur0_8_0)),
        new EngineVersion("0.9.0", typeof(FispurEngine.Fispur0_9_0.Fispur0_9_0)),
        new EngineVersion("0.10.0", typeof(FispurEngine.Fispur0_10_0.Fispur0_10_0)),
        new EngineVersion("0.10.1", typeof(FispurEngine.Fispur0_10_1.Fispur0_10_1)),
        new EngineVersion("0.10.2", typeof(FispurEngine.Fispur0_10_2.Fispur0_10_2)),
        new EngineVersion("0.10.3", typeof(FispurEngine.Fispur0_10_3.Fispur0_10_3)),
        new EngineVersion("0.11.0", typeof(FispurEngine.Fispur0_11_0.Fispur0_11_0)),
        new EngineVersion("0.12.0", typeof(FispurEngine.Fispur0_12_0.Fispur0_12_0)),
        new EngineVersion("0.13.0", typeof(FispurEngine.Fispur0_13_0.Fispur0_13_0)),
        new EngineVersion("0.13.1", typeof(FispurEngine.Fispur0_13_1.Fispur0_13_1)),
        new EngineVersion("0.13.2", typeof(FispurEngine.Fispur0_13_2.Fispur0_13_2)),
    ];

    private static void Main(string[] args)
    {
        EngineVersion version = FindVersion(DEFAULT_VERSION) ?? Versions[^1];
        IChessBot fispur = version.Create();
        int hashMb = version.DefaultHashMb;
        ApplyHashSize(fispur, hashMb);

        FispurEngine.Chess.Board tempBoard = new FispurEngine.Chess.Board();
        tempBoard.LoadStartPosition();
        FispurEngine.API.Board board = new FispurEngine.API.Board(tempBoard);
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
                        + version.DefaultHashMb
                        + " min 1 max " + version.MaxHashMb);
                    Console.WriteLine("option name Version type combo default " + version.Name
                        + " var " + string.Join(" var ", Versions.Select(v => v.Name)));
                    Console.WriteLine("uciok");
                    break;

                case "setoption":
                    StopAndWait();
                    {
                        int nameIdx = Array.IndexOf(tokens, "name");
                        int valueIdx = Array.IndexOf(tokens, "value");

                        if (nameIdx != -1 && valueIdx > nameIdx)
                        {
                            string optionName = string.Join(" ", tokens, nameIdx + 1, valueIdx - nameIdx - 1);
                            string optionValue = string.Join(" ", tokens, valueIdx + 1, tokens.Length - valueIdx - 1);

                            if (optionName.Equals("Hash", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(optionValue, out int mb))
                            {
                                hashMb = Math.Clamp(mb, 1, version.MaxHashMb);
                                ApplyHashSize(fispur, hashMb);
                            }
                            else if (optionName.Equals("Version", StringComparison.OrdinalIgnoreCase))
                            {
                                EngineVersion? selected = FindVersion(optionValue);

                                if (selected != null)
                                {
                                    version = selected;
                                    fispur = version.Create();
                                    hashMb = Math.Clamp(hashMb, 1, version.MaxHashMb);
                                    ApplyHashSize(fispur, hashMb);

                                    tempBoard = new FispurEngine.Chess.Board();
                                    tempBoard.LoadStartPosition();
                                    board = new FispurEngine.API.Board(tempBoard);
                                }
                                else
                                {
                                    Console.WriteLine("info string unknown Version '" + optionValue
                                        + "' - keeping " + version.Name);
                                }
                            }
                        }
                    }
                    break;

                case "ucinewgame":
                    StopAndWait();
                    if (!InvokeNewGame(fispur))
                    {
                        fispur = version.Create();
                        ApplyHashSize(fispur, hashMb);
                    }
                    tempBoard = new FispurEngine.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new FispurEngine.API.Board(tempBoard);
                    break;

                case "isready":
                    Console.WriteLine("readyok");
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
                    board = new FispurEngine.API.Board(tempBoard);

                    if (tokens.Length >= 2 && tokens[1].Equals("startpos"))
                    {
                        board.board.LoadStartPosition();
                    }
                    else if (tokens.Length >= 3 && tokens[1].Equals("fen"))
                    {
                        // FEN is the fields after "fen", up to "moves" (if present)
                        int fenEnd = moveStart == -1 ? tokens.Length : moveStart;
                        string fen = string.Join(" ", tokens, 2, fenEnd - 2);
                        board.board.LoadPosition(fen);
                    }

                    // Apply the moves listed after "moves", if any
                    if (moveStart != -1)
                    {
                        for (int i = moveStart + 1; i < tokens.Length; i++)
                        {
                            board.MakeMove(new FispurEngine.API.Move(tokens[i], board));
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

                    FispurEngine.API.Timer timer = new FispurEngine.API.Timer(
                        remaining, oppRemaining, remaining, increment, time, infinite || depth != -1);

                    StartSearch(fispur, board, timer, depth);
                    break;

                case "stop":
                    StopAndWait();
                    break;
            }
        }
    }

    private static readonly object outLock = new();
    private static Thread? searchThread;
    private static IChessBot? searchingBot;

    private static bool Searching => searchThread is { IsAlive: true };

    private static void Say(string s)
    {
        lock (outLock)
        {
            Console.WriteLine(s);
        }
    }

    /// <summary>
    /// Stops a running search and waits for its thread to finish. Snapshots
    /// older than 0.10.3 have no Stop() - there the search is simply awaited.
    /// </summary>
    private static void StopAndWait()
    {
        if (!Searching)
        {
            searchThread = null;
            searchingBot = null;
            return;
        }

        InvokeVoid(searchingBot!, "Stop");
        searchThread!.Join();
        searchThread = null;
        searchingBot = null;
    }

    private static void StartSearch(IChessBot bot, FispurEngine.API.Board board, FispurEngine.API.Timer timer, int depth)
    {
        StopAndWait();
        InvokeVoid(bot, "PrepareSearch");

        searchingBot = bot;
        searchThread = new Thread(() =>
        {
            try
            {
                (FispurEngine.API.Move move, int eval) result = bot.Think(board, timer, depth);
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

    private static string Uci(FispurEngine.API.Move move)
    {
        string bestMoveString = move.ToString();
        return bestMoveString.Substring(7, bestMoveString.Length - 8);
    }

    /// <summary>
    /// Calls a parameterless void method if the snapshot has one - older
    /// versions expose neither Stop() nor PrepareSearch().
    /// </summary>
    private static void InvokeVoid(IChessBot bot, string methodName)
    {
        MethodInfo? method = bot.GetType().GetMethod(methodName, Type.EmptyTypes);
        method?.Invoke(bot, []);
    }

    /// <summary>
    /// Accepts "0.9.0" as well as the class-name spelling "Fispur0_9_0"
    /// and the bare "0_9_0".
    /// </summary>
    private static EngineVersion? FindVersion(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string wanted = name.Trim().Replace('_', '.');

        if (wanted.StartsWith("Fispur", StringComparison.OrdinalIgnoreCase))
            wanted = wanted.Substring("Fispur".Length);

        return Versions.FirstOrDefault(v => v.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Versions before 0.7.0 have no configurable hash - silently ignored there.
    /// </summary>
    private static void ApplyHashSize(IChessBot bot, int megabytes)
    {
        MethodInfo? setHashSize = bot.GetType().GetMethod("SetHashSize", [typeof(int)]);
        setHashSize?.Invoke(bot, [megabytes]);
    }

    /// <summary>
    /// Returns false when the bot has no NewGame method, in which case the
    /// caller should re-create the bot instead.
    /// </summary>
    private static bool InvokeNewGame(IChessBot bot)
    {
        MethodInfo? newGame = bot.GetType().GetMethod("NewGame", Type.EmptyTypes);
        if (newGame == null)
            return false;
        newGame.Invoke(bot, []);
        return true;
    }

    private static FispurEngine.Chess.Move GetMove(string v)
    {
        return new FispurEngine.Chess.Move(GetSquareIndex(v[0] + "" + v[1]), GetSquareIndex(v[2] + "" + v[3]));
    }

    private static int GetSquareIndex(string fieldString)
    {
        return new Square(fieldString).Index;
    }
}
