
using Spiritbreaker.API;
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

    private const string DEFAULT_VERSION = "0.9.0";

    private static readonly EngineVersion[] Versions =
    [
        new EngineVersion("0.0.2", typeof(Spiritbreaker.src.SpiritBreaker.SpiritBreaker0_0_2)),
        new EngineVersion("0.0.3", typeof(SpiritBreaker0_0_3)),
        new EngineVersion("0.1.1", typeof(SpiritBreaker0_1_1)),
        new EngineVersion("0.2.0", typeof(SpiritBreaker0_2_0)),
        new EngineVersion("0.2.1", typeof(SpiritBreaker0_2_1)),
        new EngineVersion("0.3.0", typeof(Spiritbreaker.SpiritBreaker0_3_0.SpiritBreaker0_3_0)),
        new EngineVersion("0.3.1", typeof(Spiritbreaker.SpiritBreaker0_3_1.SpiritBreaker0_3_1)),
        new EngineVersion("0.3.2", typeof(Spiritbreaker.SpiritBreaker0_3_2.SpiritBreaker0_3_2)),
        new EngineVersion("0.3.3", typeof(Spiritbreaker.SpiritBreaker0_3_3.SpiritBreaker0_3_3)),
        new EngineVersion("0.3.4", typeof(Spiritbreaker.SpiritBreaker0_3_4.SpiritBreaker0_3_4)),
        new EngineVersion("0.4.0", typeof(Spiritbreaker.SpiritBreaker0_4_0.SpiritBreaker0_4_0)),
        new EngineVersion("0.5.0", typeof(Spiritbreaker.SpiritBreaker0_5_0.SpiritBreaker0_5_0)),
        new EngineVersion("0.5.1", typeof(Spiritbreaker.SpiritBreaker0_5_1.SpiritBreaker0_5_1)),
        new EngineVersion("0.6.0", typeof(Spiritbreaker.SpiritBreaker0_6_0.SpiritBreaker0_6_0)),
        new EngineVersion("0.7.0", typeof(Spiritbreaker.SpiritBreaker0_7_0.SpiritBreaker0_7_0)),
        new EngineVersion("0.8.0", typeof(Spiritbreaker.SpiritBreaker0_8_0.SpiritBreaker0_8_0)),
        new EngineVersion("0.9.0", typeof(Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0)),
    ];

    private static void Main(string[] args)
    {
        EngineVersion version = FindVersion(DEFAULT_VERSION) ?? Versions[^1];
        IChessBot spiritBreaker = version.Create();
        int hashMb = version.DefaultHashMb;
        ApplyHashSize(spiritBreaker, hashMb);

        Spiritbreaker.Chess.Board tempBoard = new Spiritbreaker.Chess.Board();
        tempBoard.LoadStartPosition();
        Spiritbreaker.API.Board board = new Spiritbreaker.API.Board(tempBoard);
        while (true)
        {
            string command = Console.ReadLine();

            if (string.IsNullOrEmpty(command))
                continue;

            string[] tokens = command.Split(' ');

            switch (tokens[0])
            {
                case "uci":
                    Console.WriteLine("id name " + spiritBreaker.GetName());
                    Console.WriteLine("id author " + spiritBreaker.GetAuthor());
                    Console.WriteLine("option name Hash type spin default "
                        + version.DefaultHashMb
                        + " min 1 max " + version.MaxHashMb);
                    Console.WriteLine("option name Version type combo default " + version.Name
                        + " var " + string.Join(" var ", Versions.Select(v => v.Name)));
                    Console.WriteLine("uciok");
                    break;

                case "setoption":
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
                                ApplyHashSize(spiritBreaker, hashMb);
                            }
                            else if (optionName.Equals("Version", StringComparison.OrdinalIgnoreCase))
                            {
                                EngineVersion? selected = FindVersion(optionValue);

                                if (selected != null)
                                {
                                    version = selected;
                                    spiritBreaker = version.Create();
                                    hashMb = Math.Clamp(hashMb, 1, version.MaxHashMb);
                                    ApplyHashSize(spiritBreaker, hashMb);

                                    tempBoard = new Spiritbreaker.Chess.Board();
                                    tempBoard.LoadStartPosition();
                                    board = new Spiritbreaker.API.Board(tempBoard);
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
                    if (!InvokeNewGame(spiritBreaker))
                    {
                        spiritBreaker = version.Create();
                        ApplyHashSize(spiritBreaker, hashMb);
                    }
                    tempBoard = new Spiritbreaker.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new Spiritbreaker.API.Board(tempBoard);
                    break;

                case "isready":
                    Console.WriteLine("readyok");
                    break;

                case "quit":
                    Environment.Exit(0);
                    break;

                case "position":
                    int moveStart = Array.IndexOf(tokens, "moves");

                    tempBoard = new Spiritbreaker.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new Spiritbreaker.API.Board(tempBoard);

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
                            board.MakeMove(new Spiritbreaker.API.Move(tokens[i], board));
                        }
                    }
                    break;

                case "go":
                    int wtime = 60_000;
                    int btime = 60_000;
                    int time = -1;
                    for (int i = 1; i < tokens.Length - 1; i++)
                    {
                        if (tokens[i] == "wtime")
                            wtime = int.Parse(tokens[i + 1]);
                        else if (tokens[i] == "btime")
                            btime = int.Parse(tokens[i + 1]);
                        else if (tokens[i] == "time")
                            time = int.Parse(tokens[i + 1]);
                        else if (tokens[i] == "movetime")
                            time = int.Parse(tokens[i + 1]) * 12;

                    }

                    (Spiritbreaker.API.Move move, int eval) result = spiritBreaker.Think(board, new Spiritbreaker.API.Timer(time != -1 ? time : (board.IsWhiteToMove ? wtime : btime)));
                    string bestMoveString = result.move.ToString();
                    string bestMoveFormattedString = bestMoveString.Substring(7, bestMoveString.Length - 8);

                    //Console.WriteLine("info score cp " + result.eval);
                    Console.WriteLine("bestmove " + bestMoveFormattedString);
                    break;

                default:
                    Console.WriteLine("?");
                    break;
            }
        }
    }

    /// <summary>
    /// Accepts "0.9.0" as well as the class-name spelling "SpiritBreaker0_9_0"
    /// and the bare "0_9_0".
    /// </summary>
    private static EngineVersion? FindVersion(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string wanted = name.Trim().Replace('_', '.');

        if (wanted.StartsWith("SpiritBreaker", StringComparison.OrdinalIgnoreCase))
            wanted = wanted.Substring("SpiritBreaker".Length);

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

    private static Spiritbreaker.Chess.Move GetMove(string v)
    {
        return new Spiritbreaker.Chess.Move(GetSquareIndex(v[0] + "" + v[1]), GetSquareIndex(v[2] + "" + v[3]));
    }

    private static int GetSquareIndex(string fieldString)
    {
        return new Square(fieldString).Index;
    }
}
