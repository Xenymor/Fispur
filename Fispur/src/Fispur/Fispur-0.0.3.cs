using FispurEngine.API;
using System;
using System.Data;
using System.Linq;
using System.Numerics;

public class Fispur0_0_3 : IChessBot
{
    public string GetAuthor()
    {
        return "Xenymor";
    }

    public string GetName()
    {
        return "Fispur 0.0.3";
    }


    Move bestMove;
    public (Move move, int eval) Think(Board board, Timer timer)
    {
        Move[] moves = board.GetLegalMoves();
        bestMove = moves.Length == 0 ? Move.NullMove : moves[0];
        int eval = AlphaBeta(board, 0, 4, -10_000_00, 10_000_00);
        return (bestMove, eval);
    }

    private int AlphaBeta(Board board, int ply, int depthLeft, int alpha, int beta)
    {
        if (board.IsInCheckmate())
        {
            return -1000_00 + ply;
        }

        if (board.IsDraw())
        {
            return 0;
        }

        Move[] moves;
        bool qSearch = false;
        int eval;
        bool inCheck = board.IsInCheck();

        if (depthLeft <= 0)
        {
            qSearch = true;
            if (!inCheck)
            {
                eval = Eval(board);
                if (eval >= beta)
                {
                    return eval;
                }
                if (eval > alpha)
                {
                    alpha = eval;
                }
            }
        }

        moves = board.GetLegalMoves(qSearch && !inCheck);

        moves = moves.OrderByDescending(move => move.IsCapture ? (int)move.CapturePieceType * 1_000 - (int)move.MovePieceType : (int)move.MovePieceType).ToArray();


        foreach (Move move in moves)
        {
            board.MakeMove(move);
            int score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
            board.UndoMove(move);

            if (score >= beta)
            {
                return score;
            }
            if (score > alpha)
            {
                if (ply == 0)
                {
                    bestMove = move;
                }
                alpha = score;
            }
        }

        return alpha;
    }

    int[] pieceVal = { 1_00, 3_00, 3_50, 5_00, 9_00, 100_00 };
    private int Eval(Board board)
    {
        int score = 0;
        for (int c = 0; c <= 1; c++)
        {
            bool isWhite = board.IsWhiteToMove ? c == 0 : c == 1;
            for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
            {
                ulong bitBoard = board.GetPieceBitboard(type, isWhite);
                score += BitOperations.PopCount(bitBoard) * pieceVal[(int)type - 1];
            }
            score = -score;
        }

        return score;
    }


}