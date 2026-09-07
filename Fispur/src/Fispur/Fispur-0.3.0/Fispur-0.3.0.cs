

namespace FispurEngine.Fispur0_3_0
{

    using FispurEngine;
    using FispurEngine.API;
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;

    public class Fispur0_3_0 : IChessBot
    {

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "Fispur 0.3.0";
        }


        Move bestMove;
        Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)> transpositionTable = new Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)>();

        public (Move move, int eval) Think(Board board, Timer timer, int maxDepth = -1)
        {
            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];

            long time = timer.MillisecondsRemaining / 20 + timer.IncrementMilliseconds / 2;
            int depth = 1;
            int eval = 0;

            while (timer.MillisecondsElapsedThisTurn < time / 2)
            {
                eval = AlphaBeta(board, 0, depth, -10_000_00, 10_000_00);
                Console.WriteLine("info currmove " + Chess.MoveUtility.GetMoveNameUCI(bestMove.move) + " depth " + depth + " score cp " + eval + " time " + timer.MillisecondsElapsedThisTurn);
                depth++;
            }

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
                    eval = NNUE.Evaluate(board);
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

            (int score, int alpha, int beta, int depthLeft, Move move) entry;
            bool hasEntry = transpositionTable.TryGetValue(board.ZobristKey, out entry);

            if (
                hasEntry && ply != 0 && depthLeft <= entry.depthLeft
                && (entry.score >= entry.beta && entry.score >= beta
                    || entry.score > entry.alpha && entry.score < entry.beta
                    || entry.score < entry.alpha && entry.score < alpha)
                )
            {
                return entry.score;
            }

            moves = board.GetLegalMoves(qSearch && !inCheck);

            moves = moves.OrderByDescending(move => hasEntry && entry.move.Equals(move) ? 10_000_000 : move.IsCapture ? (int)move.CapturePieceType * 1_000 - (int)move.MovePieceType : move.MovePieceType == PieceType.King ? 0 : (int)move.MovePieceType).ToArray();
            Move bestMove = Move.NullMove;
            int bestScore = -10_000_00;

            foreach (Move move in moves)
            {
                board.MakeMove(move);
                int score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
                board.UndoMove(move);

                if (score >= beta)
                {
                    transpositionTable[board.ZobristKey] = (score, alpha, beta, depthLeft, move);
                    return score;
                }
                bestScore = Math.Max(score, bestScore);
                if (score > alpha)
                {
                    bestMove = move;
                    alpha = score;
                }
            }

            if (ply == 0)
            {
                this.bestMove = bestMove;
            }

            if (!qSearch)
            {
                if (hasEntry)
                {
                    if (depthLeft > entry.depthLeft)
                    {
                        transpositionTable[board.ZobristKey] = (bestScore, alpha, beta, depthLeft, bestMove);
                    }
                }
                else
                {
                    transpositionTable[board.ZobristKey] = (bestScore, alpha, beta, depthLeft, bestMove);
                }

            }

            return alpha;
        }
    }
}
