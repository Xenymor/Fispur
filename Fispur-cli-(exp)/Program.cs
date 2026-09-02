
using FispurEngine.API;
using FispurEngine.Application;
using FispurEngine.Chess;

internal class Program
{
    private static void Main(string[] args)
    {
        IChessBot fispur = new FispurEngine.Fispur();
        Type botType = fispur.GetType();
        int hashMb = FispurEngine.Fispur.DEFAULT_HASH_MB;
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
                        + FispurEngine.Fispur.DEFAULT_HASH_MB
                        + " min 1 max " + FispurEngine.Fispur.MAX_HASH_MB);
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
                                hashMb = Math.Clamp(mb, 1, FispurEngine.Fispur.MAX_HASH_MB);
                                (fispur as FispurEngine.Fispur)?.SetHashSize(hashMb);
                            }
                        }
                    }
                    break;

                case "ucinewgame":
                    if (fispur is FispurEngine.Fispur currentBot)
                    {
                        currentBot.NewGame();
                    }
                    else
                    {
                        fispur = (IChessBot)botType.GetConstructor([]).Invoke([]);
                    }
                    tempBoard = new FispurEngine.Chess.Board();
                    tempBoard.LoadStartPosition();
                    board = new FispurEngine.API.Board(tempBoard);
                    break;

                case "isready":
                    Console.WriteLine("readyok");
                    break;

                case "quit":
                    Environment.Exit(0);
                    break;

                case "position":
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
                    for (int i = 1; i < tokens.Length - 1; i++)
                    {
                        switch (tokens[i])
                        {
                            case "wtime": wtime = int.Parse(tokens[i + 1]); break;
                            case "btime": btime = int.Parse(tokens[i + 1]); break;
                            case "winc": winc = int.Parse(tokens[i + 1]); break;
                            case "binc": binc = int.Parse(tokens[i + 1]); break;
                            case "time": time = int.Parse(tokens[i + 1]); break;
                            case "movetime": time = int.Parse(tokens[i + 1]) * 20; break;
                        }
                    }

                    bool whiteToMove = board.IsWhiteToMove;
                    int remaining = time != -1 ? time : (whiteToMove ? wtime : btime);
                    int oppRemaining = whiteToMove ? btime : wtime;
                    int increment = time != -1 ? 0 : (whiteToMove ? winc : binc);

                    var timer = new FispurEngine.API.Timer(remaining, oppRemaining, remaining, increment);
                    (FispurEngine.API.Move move, int eval) result = fispur.Think(board, timer);
                    string bestMoveString = result.move.ToString();
                    string bestMoveFormattedString = bestMoveString.Substring(7, bestMoveString.Length - 8);

                    Console.WriteLine("bestmove " + bestMoveFormattedString);
                    break;

                default:
                    Console.WriteLine("?");
                    break;
            }
        }
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

