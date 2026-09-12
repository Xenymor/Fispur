using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;

namespace FispurEngine
{
    public class Fispur : IChessBot
    {

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "Fispur 0.13.2";
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


        public const int MAX_DEPTH = 256;

        public static int LmrMinDepth = 3;
        public static int LmrMinMoves = 3;
        public static int LmrBase = 99;
        public static int LmrDivisor = 314;
        public static int RfpMaxDepth = 8;
        public static int RfpMargin = 100;
        public static int HistoryDivisor = 16384;
        public static int MaxHistBonus = 1536;
        public static int HistBonusMult = 300;
        public static int HistBonusBase = -250;
        public static int NMPMinDepth = 3;
        public static int NMPReductionB = 3;
        public static int NMPReductionDiv = 4;
        public static int ASPWindowDelta = 50;
        public static int ASPWindowReset = 500;

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
        long nodes;
        long hardLimit;
        Move rootBestMove;

        public Fispur()
        {
            SetHashSize(DEFAULT_HASH_MB);
        }

        volatile bool stopSearch;
        public void Stop()
        {
            stopSearch = true;
        }

        public void PrepareSearch()
        {
            stopSearch = false;
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
            eval = 0;
        }

        public long Nodes => nodes;

        public void ResetState()
        {
            NewGame();
            Array.Clear(historyHeuristic, 0, historyHeuristic.Length);
        }


        int eval = 0;
        public (Move move, int eval) Think(Board board, Timer timer, int maxDepth)
        {
            this.timer = timer;
            stopSearch = false;
            nodes = 0;

            for (int i = 0; i < historyHeuristic.Length; i++)
            {
                historyHeuristic[i] /= 2;
            }

            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];
            rootBestMove = bestMove;

            long softLimit = timer.moveTime != -1 ? timer.moveTime : timer.MillisecondsRemaining / 20 + timer.IncrementMilliseconds / 2;
            hardLimit = timer.moveTime != -1 ? timer.moveTime : Math.Min(softLimit * 4, timer.MillisecondsRemaining - 50);

            if (timer.isInfinite)
            {
                softLimit = long.MaxValue;
                hardLimit = long.MaxValue;
            }

            NNUE.UpdateAccumulators(board);

            if (moves.Length == 1 && !timer.isInfinite)
            {
                return (bestMove, NNUE.Evaluate(board));
            }

            int currMaxDepth = maxDepth < 0 ? MAX_DEPTH : Math.Min(MAX_DEPTH, maxDepth);
            int dl, dh;
            for (int depth = 1; depth <= currMaxDepth; depth++)
            {
                dl = -ASPWindowDelta; dh = ASPWindowDelta;
                int score;
                bool failed = false;

                if (depth >= 5)
                {
                    while (true)
                    {
                        int alpha = dl <= -ASPWindowReset ? -int.MaxValue : eval + dl, beta = dh >= ASPWindowReset ? int.MaxValue : eval + dh;
                        score = AlphaBeta(board, 0, depth, alpha, beta);

                        if (score <= alpha)
                        {
                            dl *= 2;
                        }
                        else if (score >= beta)
                        {
                            dh *= 2;
                        }
                        else
                        {
                            break;
                        }
                        if (timer.MillisecondsElapsedThisTurn >= softLimit || stopSearch)
                        {
                            failed = true;
                            break;
                        }
                    }
                } else
                {
                    score = AlphaBeta(board, 0, depth, -int.MaxValue, int.MaxValue);
                }

                if (stopSearch || failed)
                    break;

                eval = score;
                bestMove = rootBestMove;
                Console.WriteLine("info depth " + depth + " score " + ScoreToUCI(eval)
                    + " nodes " + nodes + " time " + timer.MillisecondsElapsedThisTurn
                    + " pv " + Chess.MoveUtility.GetMoveNameUCI(bestMove.move));

                if (timer.isInfinite)
                {
                    softLimit = long.MaxValue;
                    hardLimit = long.MaxValue;
                }

                if (timer.isInfinite && stopSearch 
                    || !timer.isInfinite && ((timer.moveTime == -1 && timer.MillisecondsElapsedThisTurn >= softLimit / 2) 
                    || (timer.moveTime != -1 && timer.MillisecondsElapsedThisTurn >= timer.moveTime))
                    || Math.Abs(eval) >= MATE_BOUND && !timer.isInfinite)
                    break;
            }

            return (bestMove, eval);
        }

        private int AlphaBeta(Board board, int ply, int depthLeft, int alpha, int beta, bool canNull = true)
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
            Move[] moves;
            bool qSearch = depthLeft <= 0;
            bool inCheck = board.IsInCheck();
            int eval = inCheck ? -int.MaxValue : NNUE.Evaluate(board);
            bool pvNode = beta - alpha > 1;

            if (ply > 0)
            {
                if (board.IsFiftyMoveDraw() || board.IsRepeatedPosition() || board.IsInsufficientMaterial())
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

            int margin = RfpMargin * depthLeft;

            if (!qSearch && !inCheck && !pvNode && depthLeft <= RfpMaxDepth && Math.Abs(beta) < MATE_BOUND && eval >= beta + margin)
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


            if (eval >= beta && !pvNode && !inCheck && canNull && depthLeft >= NMPMinDepth && beta < MATE_BOUND) {
                bool isPawnEndgame = board.IsWhiteToMove ? ((board.WhitePiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, true) ^ board.GetPieceBitboard(PieceType.King, true)) == 0)
                                                         : ((board.BlackPiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, false) ^ board.GetPieceBitboard(PieceType.King, false)) == 0);
                if (!isPawnEndgame) {
                    int reduction = NMPReductionB + depthLeft / NMPReductionDiv;

                    board.ForceSkipTurn();
                    int score = -AlphaBeta(board, ply + 1, depthLeft - reduction - 1, -beta, -beta + 1, canNull: false);
                    board.UndoSkipTurn();

                    if (stopSearch)
                    {
                        return 0;
                    }
                    if (score >= beta)
                    {
                        return score >= MATE_BOUND ? beta : score;
                    }
                }
            }

            Move ttMove = hasEntry ? entry.move : Move.NullMove;
            
            Move bestMove = Move.NullMove;
            int bestScore = qSearch && !inCheck ? eval : -INFINITY;

            if (!qSearch && hasEntry)
            {
                board.MakeMove(ttMove);
                NNUE.makeMove(ttMove, !board.IsWhiteToMove);

                int score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);

                NNUE.undoMove();
                board.UndoMove(ttMove);

                if (score >= beta)
                {
                    if (!ttMove.IsCapture)
                    {
                        int bonus = Math.Min(MaxHistBonus, HistBonusMult * depthLeft + HistBonusBase);

                        ref int h = ref historyHeuristic[getHistoryHeuristicInd(board, ttMove)];
                        h += bonus - h * bonus / HistoryDivisor;
                    }

                    StoreTT(zobrist, score, depthLeft, ply, BOUND_LOWER, ttMove);

                    return score;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = ttMove;
                }
                if (score > alpha)
                {
                    alpha = score;
                }
            }

            moves = board.GetLegalMoves(qSearch && !inCheck);

            if (moves.Length == 0)
                return inCheck ? -MATE + ply
                     : qSearch ? eval
                     : 0;

            Span<int> scores = stackalloc int[moves.Length];
            for (int i = 0; i < moves.Length; i++)
            {
                scores[i] = scoreMove(board, ply, moves[i], ttMove);
            }

            for (int i = hasEntry && !qSearch ? 1 : 0; i < moves.Length; i++)
            {
                int best = i;
                for (int j = i + 1; j < moves.Length; j++)
                {
                    if (scores[j] > scores[best])
                    {
                        best = j;
                    }
                }

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
                    if (depthLeft >= LmrMinDepth && i >= LmrMinMoves && !inCheck && !move.IsCapture && !move.IsPromotion)
                    {
                        reduction = Math.Clamp((int)(LmrBase / 100.0 + Math.Log(depthLeft) * Math.Log(i) / (LmrDivisor / 100.0)), 0, depthLeft - 2);
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
                        int bonus = Math.Min(MaxHistBonus, HistBonusMult * depthLeft + HistBonusBase);

                        ref int h = ref historyHeuristic[getHistoryHeuristicInd(board, move)];
                        h += bonus - h * bonus / HistoryDivisor;

                        for (int j = 0; j < i; j++)
                        {
                            if (moves[j].IsCapture) continue;
                            ref int p = ref historyHeuristic[getHistoryHeuristicInd(board, moves[j])];
                            p += -bonus - p * bonus / HistoryDivisor;
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

        private int scoreMove(Board board, int ply, Move move, Move ttMove)
        {
            if (move.Equals(ttMove))
            {
                return int.MaxValue;
            }

            if (move.IsCapture)
            {
                int victim = (int)move.CapturePieceType;
                int attacker = (int)move.MovePieceType;
                
                if (victim >= attacker)
                {
                    return 1_000_000 + 100 * victim - attacker;
                } else
                {
                    return 1_000_000 + 100 * victim - attacker;
                }
            }

            return historyHeuristic[getHistoryHeuristicInd(board, move)];
        }


        private static int getHistoryHeuristicInd(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}