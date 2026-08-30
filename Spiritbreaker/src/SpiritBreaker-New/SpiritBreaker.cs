using Spiritbreaker.API;
using System;
using System.Data;
using System.Numerics;

public class SpiritBreaker : IChessBot
{
    public Move Think(Board board, Timer timer)
    {
        Move[] moves = board.GetLegalMoves();

        Move bestMove = Move.NullMove;
        int bestScore = -1000_00;

        foreach (Move move in moves)
        {
            board.MakeMove(move);
            if (board.IsInCheckmate())
            {
                bestMove = move;
                break;
            }
            int score = eval(board);
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }
            board.UndoMove(move);
        }

        return bestMove;
    }

    int[] pieceVal = { 1_00, 3_00, 3_50, 5_00, 9_00, 100_00 };
    private int eval(Board board)
    {
        int score = 0;
        for (int c = 0; c <= 1; c++) {
            bool isWhite = board.IsWhiteToMove ? c == 1 : c == 0;
            for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++) {
                ulong bitBoard = board.GetPieceBitboard(type, isWhite);
                score += BitOperations.PopCount(bitBoard) * pieceVal[(int)type - 1];
            }
            score = -score;
        }

        return score;
    }
}