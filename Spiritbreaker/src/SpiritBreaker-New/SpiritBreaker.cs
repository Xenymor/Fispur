using Spiritbreaker.API;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Spiritbreaker
{
    public class SpiritBreaker : IChessBot
    {

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "Spiritbreaker 0.9.0";
        }

        private static string ScoreToUCI(int score)
        {
            if (Math.Abs(score) <= MATE_BOUND)
                return "cp " + score;

            int plies = MATE - Math.Abs(score);
            int moves = (plies + 1) / 2;

            return "mate " + (score > 0 ? moves : -moves);
        }

        const int INFINITY = 10_000_00;
        const int MATE = 1000_00;

        const int MATE_BOUND = MATE - 256;

        const byte BOUND_NONE = 0;
        const byte BOUND_LOWER = 1;  
        const byte BOUND_UPPER = 2;
        const byte BOUND_EXACT = 3;

        public const int DEFAULT_HASH_MB = 256;
        public const int MAX_HASH_MB = 1024;

        struct TTEntry
        {
            public uint key; //first 32 bit
            public int score;
            public Move move;
            public byte depth;
            public byte bound;
        }

        Move bestMove;
        TTEntry[] transpositionTable;
        ulong ttMask;
        int[] historyHeuristic = new int[2 * 64 * 64];

        Timer timer;
        bool stopSearch;
        long nodes;
        long hardLimit;
        Move rootBestMove;

        public SpiritBreaker()
        {
            SetHashSize(DEFAULT_HASH_MB);
        }

        public void SetHashSize(int megabytes)
        {
            megabytes = Math.Clamp(megabytes, 1, MAX_HASH_MB);

            long entries = ((long)megabytes * 1024 * 1024) / Unsafe.SizeOf<TTEntry>();

            int bits = 0;
            while (1L << (bits + 1) <= entries)
                bits++;
            int count = 1 << bits;

            if (transpositionTable != null && transpositionTable.Length == count)
            {
                NewGame();
                return;
            }

            transpositionTable = new TTEntry[count];
            ttMask = (ulong)(count - 1);
        }

        public void NewGame()
        {
            Array.Clear(transpositionTable, 0, transpositionTable.Length);
        }

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
                int score = AlphaBeta(board, 0, depth, -INFINITY, INFINITY);

                if (stopSearch)
                    break;

                eval = score;
                bestMove = rootBestMove;
                Console.WriteLine("info depth " + depth + " score " + ScoreToUCI(eval)
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
                    return -MATE + ply;
                if (board.IsDraw())
                    return 0;
            }

            ulong zobrist = board.ZobristKey;
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            bool hasEntry = entry.bound != BOUND_NONE && entry.key == (uint)(zobrist >> 32);

            if (hasEntry && ply > 0 && entry.depth >= depthLeft)
            {
                int ttScore = ScoreFromTT(entry.score, ply);
                if (entry.bound == BOUND_EXACT
                    || (entry.bound == BOUND_LOWER && ttScore >= beta)
                    || (entry.bound == BOUND_UPPER && ttScore <= alpha))
                {
                    return ttScore;
                }
            }

            Move[] moves;
            bool qSearch = depthLeft <= 0;
            bool inCheck = board.IsInCheck();
            int eval = inCheck ? -int.MaxValue : NNUE.Evaluate(board);
            bool pvNode = beta - alpha > 1;

            int margin = 100 * depthLeft;

            if (!qSearch && !inCheck && !pvNode && depthLeft <= 8 && Math.Abs(beta) < MATE_BOUND && eval >= beta + margin)
            {
                return eval;
            }

            if (qSearch)
            {
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

            Move ttMove = hasEntry ? entry.move : Move.NullMove;

            moves = board.GetLegalMoves(qSearch && !inCheck);

            Span<int> scores = stackalloc int[moves.Length];
            for (int i = 0; i < moves.Length; i++)
                scores[i] = scoreMove(board, moves[i], ttMove);

            Move bestMove = Move.NullMove;
            int bestScore = qSearch && !inCheck ? eval : -INFINITY;

            for (int i = 0; i < moves.Length; i++)
            {
                int best = i;
                for (int j = i + 1; j < moves.Length; j++)
                    if (scores[j] > scores[best]) best = j;

                (moves[i], moves[best]) = (moves[best], moves[i]);
                (scores[i], scores[best]) = (scores[best], scores[i]);

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
                    int reduction = 0;
                    if (depthLeft >= 3 && i >= 3 && !inCheck && !move.IsCapture && !move.IsPromotion)
                    {
                        reduction = Math.Clamp((int)(0.99 + Math.Log(depthLeft) * Math.Log(i) / 3.14), 0, depthLeft - 2);
                    }
                    score = -AlphaBeta(board, ply + 1, depthLeft - 1 - reduction, -(alpha + 1), -alpha);
                    if (reduction > 0 && score > alpha)
                    {
                        score = -AlphaBeta(board, ply + 1, depthLeft - 1, -(alpha + 1), -alpha);
                    }
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

                    StoreTT(zobrist, score, depthLeft, ply, BOUND_LOWER, move);
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
                StoreTT(zobrist, bestScore, depthLeft, ply,
                    bestScore <= ogAlpha ? BOUND_UPPER : BOUND_EXACT, bestMove);
            }

            return bestScore;
        }

        private void StoreTT(ulong zobrist, int score, int depthLeft, int ply, byte bound, Move move)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            uint key = (uint)(zobrist >> 32);
            byte depth = (byte)Math.Clamp(depthLeft, 0, 255);

            if (entry.bound != BOUND_NONE && entry.key == key && depth < entry.depth)
            {
                return;
            }

            entry.key = key;
            entry.score = ScoreToTT(score, ply);
            entry.move = move;
            entry.depth = depth;
            entry.bound = bound;
        }

        private static int ScoreToTT(int score, int ply)
            => score > MATE_BOUND ? score + ply
             : score < -MATE_BOUND ? score - ply
             : score;

        private static int ScoreFromTT(int score, int ply)
            => score > MATE_BOUND ? score - ply
             : score < -MATE_BOUND ? score + ply
             : score;

        private int scoreMove(Board board, Move move, Move ttMove)
        {
            if (move.Equals(ttMove))
                return int.MaxValue;
            if (move.IsCapture)
                return 1_000_000 + (int)move.CapturePieceType * 100 - (int)move.MovePieceType;
            return historyHeuristic[getHistoryHeuristicInd(board, move)];
        }

        private static int getHistoryHeuristicInd(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}