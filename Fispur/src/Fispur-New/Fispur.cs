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
            return "Fispur 0.16.1";
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

        public static int LmrMinDepth = 2;
        public static int LmrMinMoves = 3;
        public static int LmrBase = 90;
        public static int LmrDivisor = 200;

        public static int RfpMaxDepth = 8;
        public static int RfpMargin = 81;
        public static int FpMaxDepth = 8;
        public static int FpMargin = 153;
        public static int HistoryDivisor = 30168;
        public static int MaxHistBonus = 3847;
        public static int HistBonusMult = 539;
        public static int HistBonusBase = -320;
        public static int NMPMinDepth = 3;
        public static int NMPReductionB = 3;
        public static int NMPReductionDiv = 4;
        public static int ASPWindowDelta = 80;
        public static int ASPWindowReset = 1456;
        public static int SEEPMaxDepth = 3;
        public static int SEEPThreshold = 0;
        public static int SEEPCaptureThreshold = 103;

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
            Array.Clear(historyHeuristic, 0, historyHeuristic.Length);
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

            if (ply >= MAX_DEPTH - 1)
            {
                return NNUE.Evaluate(board);
            }

            int ogAlpha = alpha;
            Move[] moves;
            bool inCheck = board.IsInCheck();
            if (inCheck)
            {
                depthLeft++;
            }
            bool qSearch = depthLeft <= 0;
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

            int eval = inCheck ? -int.MaxValue : NNUE.Evaluate(board);
            int rfpMargin = RfpMargin * depthLeft;

            if (!qSearch && !inCheck && !pvNode && depthLeft <= RfpMaxDepth && Math.Abs(beta) < MATE_BOUND && eval >= beta + rfpMargin)
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

            Move bestMove = Move.NullMove;
            int bestScore = qSearch && !inCheck ? eval : -INFINITY;
            int fpMargin = FpMargin * depthLeft;
            int movesSearched = 0;


            for (int i = 0; i < moves.Length; i++)
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

                if (!pvNode && !inCheck && !qSearch)
                {
                    if (depthLeft <= FpMaxDepth && !move.IsCapture && !move.IsPromotion && bestScore > -INFINITY && Math.Abs(alpha) < MATE_BOUND && eval + fpMargin <= alpha)
                    {
                        continue;
                    }
                    if (depthLeft <= SEEPMaxDepth && movesSearched > 0)
                    {
                        if (move.IsCapture && !SEE(board, move, -SEEPCaptureThreshold * depthLeft))
                        {
                            continue;
                        }
                        if (!move.IsCapture && !SEE(board, move, -SEEPThreshold * depthLeft))
                        {
                            continue;
                        }
                    }
                }

                if (qSearch && !inCheck && scores[i] < -500_000) // score lower than -500_000 is losing capture
                {
                    continue;
                }

                board.MakeMove(move);
                NNUE.makeMove(move, !board.IsWhiteToMove);

                int score;
                if (movesSearched == 0)
                {
                    score = -AlphaBeta(board, ply + 1, depthLeft - 1, -beta, -alpha);
                }
                else
                {
                    int reduction = 0;
                    if (depthLeft >= LmrMinDepth && movesSearched >= LmrMinMoves && !inCheck && !move.IsCapture && !move.IsPromotion)
                    {
                        reduction = Math.Clamp((int)(LmrBase / 100.0 + Math.Log(depthLeft) * Math.Log(movesSearched) / (LmrDivisor / 100.0)), 0, depthLeft);
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

                movesSearched++;

                if (stopSearch)
                    return ply == 0 ? bestScore : 0;

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

                    if (ply == 0)
                    {
                        rootBestMove = move;
                    }

                    return score;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;

                    if (ply == 0)
                    {
                        rootBestMove = move;
                    }
                }
                if (score > alpha)
                {
                    alpha = score;
                }
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
                if (SEE(board, move, 0))
                { 
                    return 1_000_000 + 100 * (int)move.CapturePieceType - (int)move.MovePieceType;
                } else
                {
                    return -1_000_000 + 100 * (int)move.CapturePieceType - (int)move.MovePieceType;
                }
            }

            return historyHeuristic[getHistoryHeuristicInd(board, move)];
        }

        static readonly int[] pieceVals = new int[] { 0, 100, 300, 350, 500, 900, 100_000 };

        private bool SEE(Board board, Move move, int threshold)
        {
            ulong from = 1UL << move.StartSquare.Index, to = 1UL << move.TargetSquare.Index;

            if (move.IsCastles || move.IsPromotion || move.IsEnPassant)
            {
                return 0 >= threshold;
            }

            int swap = pieceVals[(int)move.CapturePieceType] - threshold;
            if (swap < 0)
            {
                return false;
            }

            swap = pieceVals[(int)move.MovePieceType] - swap;
            if (swap <= 0)
            {
                return true;
            }

            ulong occ = board.AllPiecesBitboard ^ from ^ to;

            ulong wPawns = board.GetPieceBitboard(PieceType.Pawn, true);
            ulong bPawns = board.GetPieceBitboard(PieceType.Pawn, false);
            ulong knights = board.GetPieceBitboard(PieceType.Knight, true) | board.GetPieceBitboard(PieceType.Knight, false);
            ulong kings = board.GetPieceBitboard(PieceType.King, true) | board.GetPieceBitboard(PieceType.King, false);
            ulong bishops = board.GetPieceBitboard(PieceType.Bishop, true) | board.GetPieceBitboard(PieceType.Bishop, false);
            ulong rooks = board.GetPieceBitboard(PieceType.Rook, true) | board.GetPieceBitboard(PieceType.Rook, false);
            ulong queens = board.GetPieceBitboard(PieceType.Queen, true) | board.GetPieceBitboard(PieceType.Queen, false);

            ulong attackers = AttackersTo(move.TargetSquare, occ, wPawns, bPawns, knights, kings, bishops, rooks, queens);
            bool stm = board.IsWhiteToMove;
            ulong boys = board.WhitePiecesBitboard, opps = board.BlackPiecesBitboard;
            if (!stm)
            {
                (boys, opps) = (opps, boys);
            }
            int res = 1;

            while (true)
            {
                stm = !stm;
                (boys, opps) = (opps, boys);
                attackers &= occ;
                ulong stmAttackers = attackers & boys;
                if (stmAttackers == 0)
                {
                    break;
                }

                res ^= 1;
                PieceType pt = getLowestPiece(wPawns, bPawns, knights, kings, bishops, rooks, queens, stmAttackers);

                if (pt == PieceType.King)
                {
                    return (attackers & opps) != 0 ? res == 0 : res != 0;
                }

                swap = pieceVals[(int)pt] - swap;
                if (swap < res)
                {
                    break;
                }

                ulong bb = stmAttackers & getPieceBitboard(pt, wPawns | bPawns, knights, bishops, rooks, queens, kings);
                ulong lsb = bb & (0UL - bb);
                occ ^= lsb;
                if (pt == PieceType.Pawn || pt == PieceType.Bishop || pt == PieceType.Queen)
                {
                    attackers |= BitboardHelper.GetSliderAttacks(PieceType.Bishop, move.TargetSquare, occ) & (bishops | queens);
                }
                if (pt == PieceType.Rook || pt == PieceType.Queen)
                {
                    attackers |= BitboardHelper.GetSliderAttacks(PieceType.Rook, move.TargetSquare, occ) & (rooks | queens);
                }
            }

            return res != 0;
        }

        private ulong getPieceBitboard(PieceType pt, ulong pawns, ulong knights, ulong bishops, ulong rooks, ulong queens, ulong kings)
        {
            switch (pt)
            {
                case PieceType.Pawn:
                    return pawns;
                case PieceType.Knight:
                    return knights;
                case PieceType.Bishop:
                    return bishops;
                case PieceType.Rook:
                    return rooks;
                case PieceType.Queen:
                    return queens;
                case PieceType.King:
                    return kings;
                default:
                    return 0;
            }
        }

        private static PieceType getLowestPiece(ulong wPawns, ulong bPawns, ulong knights, ulong kings, ulong bishops, ulong rooks, ulong queens, ulong boys)
        {
            if ((boys & (wPawns | bPawns)) > 0)
            {
                return PieceType.Pawn;
            }
            else if ((boys & knights) > 0)
            {
                return PieceType.Knight;
            }
            else if ((boys & bishops) > 0)
            {
                return PieceType.Bishop;
            }
            else if ((boys & rooks) > 0)
            {
                return PieceType.Rook;
            }
            else if ((boys & queens) > 0)
            {
                return PieceType.Queen;
            }
            else if ((boys & kings) > 0)
            {
                return PieceType.King;
            }

            return PieceType.None;
        }

        private static ulong AttackersTo(Square to, ulong occ, ulong wPawns, ulong bPawns, ulong knights, ulong kings, ulong bishops, ulong rooks, ulong queens)
        {
            return (BitboardHelper.GetPawnAttacks(to, true) & bPawns)
                 | (BitboardHelper.GetPawnAttacks(to, false) & wPawns)
                 | (BitboardHelper.GetKnightAttacks(to) & knights)
                 | (BitboardHelper.GetKingAttacks(to) & kings)
                 | (BitboardHelper.GetSliderAttacks(PieceType.Bishop, to, occ) & (bishops | queens))
                 | (BitboardHelper.GetSliderAttacks(PieceType.Rook, to, occ) & (rooks | queens));
        }

        private static int getHistoryHeuristicInd(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}