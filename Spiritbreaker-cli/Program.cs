
using Spiritbreaker.API;
using Spiritbreaker.Application;
using Spiritbreaker.Chess;
using Spiritbreaker.Example;
using Spiritbreaker.src.SpiritBreaker;

internal class Program
    {
        private static void Main(string[] args)
        {
            IChessBot spiritBreaker = new SpiritBreaker0_2_0();
            Type botType = spiritBreaker.GetType();
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
                        Console.WriteLine("uciok");
                        break;

                    case "ucinewgame":
                        spiritBreaker = (IChessBot)botType.GetConstructor([]).Invoke([]);
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
                        // Parse time controls and other parameters
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

                        // Call engine to calculate and return best move
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

