

namespace Spiritbreaker.SpiritBreaker0_6_0
{

    using Spiritbreaker;
    using Spiritbreaker.API;
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Numerics;

    public class SpiritBreaker0_6_0 : IChessBot
    {

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "Spiritbreaker 0.6.0";
        }


        Move bestMove;
        Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)> transpositionTable = new Dictionary<ulong, (int score, int alpha, int beta, int depthLeft, Move move)>();
        int[] historyHeuristic = new int[2 * 64 * 64];

        Timer timer;
        bool stopSearch;
        long nodes;
        long hardLimit;
        Move rootBestMove;

        public (Move move, int eval) Think(Board board, Timer timer)
        {
            this.timer = timer;
            stopSearch = false;
            nodes = 0;


            Array.Fill(historyHeuristic, 0);

            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];
            rootBestMove = bestMove;

            long softLimit = timer.MillisecondsRemaining / 20 + timer.IncrementMilliseconds / 2;
            hardLimit = Math.Min(softLimit * 4, timer.MillisecondsRemaining - 50);

            int eval = 0;

            NNUE.UpdateAccumulators(board);

            for (int depth = 1; depth <= 128; depth++)
            {
                int score = AlphaBeta(board, 0, depth, -10_000_00, 10_000_00);

                if (stopSearch)
                    break;

                eval = score;
                bestMove = rootBestMove;
                Console.WriteLine("info depth " + depth + " score cp " + eval
                    + " nodes " + nodes + " time " + timer.MillisecondsElapsedThisTurn
                    + " pv " + Chess.MoveUtility.GetMoveNameUCI(bestMove.move));

                if (timer.MillisecondsElapsedThisTurn >= softLimit / 2)
                    break;
            }

            return (bestMove, eval);
        }

        private int AlphaBeta(Board board, int ply, int depthLeft, int alpha, int beta)
        {
            if (stopSearch)
            {
                return 0;
            }

            if ((++nodes & 2047) == 0 && timer.MillisecondsElapsedThisTurn >= hardLimit)
            {
                stopSearch = true;
                return 0;
            }

            int ogAlpha = alpha;

            if (ply > 0)
            {
                if (board.IsInCheckmate())
                    return -1000_00 + ply;
                if (board.IsDraw())
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

            moves = board.GetLegalMoves(qSearch && !inCheck);

            moves = moves.OrderByDescending(move => hasEntry && entry.move.Equals(move) ? long.MaxValue : move.IsCapture ? (long.MaxValue / 2 + (int)move.CapturePieceType * 1_000L - (int)move.MovePieceType) : historyHeuristic[getHistoryHeuristicInd(board, move)]).ToArray();
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
                }
                else
                {
                    score = -AlphaBeta(board, ply + 1, depthLeft - 1, -(alpha + 1), -alpha);
                    if (score > alpha && score < beta)
                    {
                        score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
                    }
                }

                NNUE.undoMove();
                board.UndoMove(move);

                if (stopSearch)
                    return 0;

                if (score >= beta)
                {
                    if (!qSearch && !move.IsCapture)
                    {
                        int bonus = Math.Min(1536, 300 * depthLeft - 250);

                        ref int h = ref historyHeuristic[getHistoryHeuristicInd(board, move)];
                        h += bonus - h * bonus / 16384;

                        for (int j = 0; j < i; j++)
                        {
                            if (moves[j].IsCapture) continue;
                            ref int p = ref historyHeuristic[getHistoryHeuristicInd(board, moves[j])];
                            p += -bonus - p * bonus / 16384;
                        }
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
                rootBestMove = bestMove;
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
