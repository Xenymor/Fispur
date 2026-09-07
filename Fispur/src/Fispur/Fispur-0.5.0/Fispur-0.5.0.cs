

namespace FispurEngine.Fispur0_5_0
{

    using FispurEngine;
    using FispurEngine.API;
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Numerics;

    public class Fispur0_5_0 : IChessBot
    {

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "Fispur 0.5.0";
        }


        Move bestMove;
        Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)> transpositionTable = new Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)>();
        long[] historyHeuristic = new long[2*64*64];

        public (Move move, int eval) Think(Board board, Timer timer, int maxDepth = -1)
        {
            Array.Fill(historyHeuristic, 0);

            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];

            long time = timer.MillisecondsRemaining / 20 + timer.IncrementMilliseconds / 2;
            int depth = 1;
            int eval = 0;

            NNUE.UpdateAccumulators(board);

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
            int ogAlpha = alpha;

            if (board.IsInCheckmate())
            {
                return -1000_00 + ply;
            }

            if (board.IsDraw())
            {
                return 0;
            }

            Move[] moves;
            bool qSearch = depthLeft <= 0;
            int eval = 0;
            bool inCheck = board.IsInCheck();

            if (qSearch)
            {
                eval = NNUE.Evaluate(board);
                if (!inCheck)
                {
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

            moves = moves.OrderByDescending(move => hasEntry && entry.move.Equals(move) ? long.MaxValue : move.IsCapture ? (long.MaxValue/2 + (int)move.CapturePieceType*1_000L - (int)move.MovePieceType) : historyHeuristic[getHistoryHeuristicInd(board, move)]).ToArray();
            Move bestMove = Move.NullMove;
            int bestScore = qSearch && !inCheck ? eval : -10_000_00;

            for (int i = 0; i < moves.Length; i++)
            {
                Move move = moves[i];
                board.MakeMove(move);
                NNUE.makeMove(move, !board.IsWhiteToMove);

                int score;
                if (i == 0)
                {
                    score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
                } else
                {
                    score = -AlphaBeta(board, ply + 1, depthLeft - 1, -(alpha + 1), -alpha);
                    if (score > alpha && score < beta)
                    {
                        score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
                    }
                }

                NNUE.undoMove();
                board.UndoMove(move);

                if (score >= beta)
                {
                    if (!qSearch && !move.IsCapture)
                    {
                        historyHeuristic[getHistoryHeuristicInd(board, move)] += depthLeft * depthLeft;
                    }

                    transpositionTable[board.ZobristKey] = (score, ogAlpha, beta, depthLeft, move);
                    return score;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;
                }
                if (score > alpha)
                {
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
                        transpositionTable[board.ZobristKey] = (bestScore, ogAlpha, beta, depthLeft, bestMove);
                    }
                }
                else
                {
                    transpositionTable[board.ZobristKey] = (bestScore, ogAlpha, beta, depthLeft, bestMove);
                }

            }

            return bestScore;
        }

        private static int getHistoryHeuristicInd(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}
