
using Spiritbreaker.API;
using Spiritbreaker.Application;
using Spiritbreaker.Chess;

internal class Program
{
    private static void Main(string[] args)
    {
        IChessBot spiritBreaker = new Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0();
        Type botType = spiritBreaker.GetType();
        int hashMb = Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0.DEFAULT_HASH_MB;
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
                        + Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0.DEFAULT_HASH_MB
                        + " min 1 max " + Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0.MAX_HASH_MB);
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
                                hashMb = Math.Clamp(mb, 1, Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0.MAX_HASH_MB);
                                (spiritBreaker as Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0)?.SetHashSize(hashMb);
                            }
                        }
                    }
                    break;

                case "ucinewgame":
                    if (spiritBreaker is Spiritbreaker.SpiritBreaker0_9_0.SpiritBreaker0_9_0 currentBot)
                    {
                        currentBot.NewGame();
                    }
                    else
                    {
                        spiritBreaker = (IChessBot)botType.GetConstructor([]).Invoke([]);
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

    private static Spiritbreaker.Chess.Move GetMove(string v)
    {
        return new Spiritbreaker.Chess.Move(GetSquareIndex(v[0] + "" + v[1]), GetSquareIndex(v[2] + "" + v[3]));
    }

    private static int GetSquareIndex(string fieldString)
    {
        return new Square(fieldString).Index;
    }
}

