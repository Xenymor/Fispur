using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

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
            return "dev";
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
        public static int LmrBase = 93;
        public static int LmrDivisor = 236;

        public static int RfpMaxDepth = 9;
        public static int RfpMargin = 107;
        public static int RFPImpMargin = 52;

        public static int FpMaxDepth = 7;
        public static int FpMargin = 211;

        public static int HistDivisor = 30168;
        public static int HistMaxBonus = 3847;
        public static int HistBonusMult = 539;
        public static int HistBonusBase = -320;

        public static int CHistoryDivisor = 32695;
        public static int MaxCHistBonus = 4148;
        public static int CHistBonusMult = 529;
        public static int CHistBonusBase = -324;

        public static int NMPMinDepth = 3;
        public static int NMPReductionB = 3;
        public static int NMPReductionDiv = 4;

        public static int ASPWindowDelta = 80;
        public static int ASPWindowReset = 1456;

        public static int SEEPMaxDepth = 4;
        public static int SEEPThreshold = 18;
        public static int SEEPCaptureThreshold = 90;
        public static int SEEPCaptureHistMult = 96;
        public static int SEEPHistMult = 58;

        public static int LMPMaxDepth = 4;
        public static int LMPBase = 9;

        public static int MinIIRDepth = 4;

        public static int CorrGrain = 256;
        public static int CorrScale = 256;
        public static int CorrMax = 64 * CorrGrain;

        public static int CaptureHistDivisor = 32961;
        public static int CaptureHistMaxBonus = 3825;
        public static int CaptureHistBonusMult = 571;
        public static int CaptureHistBonusBase = -324;
        public static int CaptureHistSEEMult = 89;
        public static int MvvMult = 15;

        public static int HHLMRDiv = 10262;

        public static int MoveOverhead = 30;
        public static int TmBaseDiv = 18;
        public static int TmIncPct = 92;
        public static int TmSoftPct = 62;
        public static int TmHardPct = 369;
        public static int TmNodeBase = 152;
        public static int TmNodeMult = 127;
        public static readonly int[] StabScale = { 208, 144, 107, 98, 80 };

        public static int SEMinPly = 0;
        public static int SEMinDepth = 7;
        public static int SEMargin = 24;
        public static int SEDepthMargin = 3;
        public static int SEDMargin = 17;

        struct TTEntry
        {
            public uint key; //first 32 bit
            public int score;
            public Move move;
            public byte depth;
            public byte bound;
            public short staticEval; // NNUE eval of this position, NO_EVAL if unknown
        }

        const short NO_EVAL = short.MinValue;

        Move bestMove;
        TTEntry[] transpositionTable;
        ulong ttMask;
        int[] historyHeuristic = new int[2 * 64 * 64];
        Move[] killers = new Move[MAX_DEPTH];
        private const int CORR_HIST_EXP = 14;
        private const int CORR_HIST_ENTRIES = 1 << CORR_HIST_EXP;
        int[] correctionHist = new int[2 * CORR_HIST_ENTRIES]; // [stm][pawnKey]

        int[] continuationHist = new int[2 * 7 * 64 * 7 * 64]; // [stm][prevPiece][prevTo][piece][to]
        int[] captureHist = new int[2 * 7 * 64 * 7]; // [stm][movingPiece][to][captured]

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int getContinuationHistoryIndex(int stm, int prevPiece, int prevTo, int piece, int to)
        {
            return (((stm * 7 + prevPiece) * 64 + prevTo) * 7 + piece) * 64 + to;
        }

        (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove)[] ppStack = new (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove)[MAX_DEPTH];

        long[] rootNodes = new long[64 * 64]; // [from][to]

        Timer timer;
        long nodes;
        long hardLimit;
        Move rootBestMove;

        Move[,] pvTable = new Move[MAX_DEPTH, MAX_DEPTH];
        int[] pvLength = new int[MAX_DEPTH];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void updatePV(int ply, Move move)
        {
            pvTable[ply, ply] = move;

            int childLength = pvLength[ply + 1];
            for (int i = ply + 1; i < childLength; i++)
            {
                pvTable[ply, i] = pvTable[ply + 1, i];
            }

            pvLength[ply] = Math.Max(childLength, ply + 1);
        }

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

            transpositionTable = GC.AllocateArray<TTEntry>(count, pinned: true);
            ttMask = (ulong)(count - 1);
        }

        public void NewGame()
        {
            Array.Clear(transpositionTable);
            Array.Clear(historyHeuristic);
            Array.Clear(killers);
            Array.Clear(correctionHist);
            Array.Clear(continuationHist);
            Array.Clear(captureHist);
            eval = 0;
        }

        public long Nodes => nodes;

        public void ResetState()
        {
            NewGame();
        }


        int eval = 0;
        int stability = 0;
        int selDepth = 0;
        int rootDepth = 0;
        public (Move move, int eval) Think(Board board, Timer timer, int maxDepth)
        {
            selDepth = 0;
            this.timer = timer;
            ensureLmrTable();
            Array.Clear(rootNodes);
            nodes = 0;
            stability = 0;

            for (int i = 0; i < historyHeuristic.Length; i++)
            {
                historyHeuristic[i] /= 2;
            }
            Array.Clear(killers);

            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];
            rootBestMove = bestMove;

            long softLimit;

            if (timer.isInfinite)
            {
                softLimit = hardLimit = long.MaxValue;
            } else if (timer.moveTime != -1)
            {
                softLimit = hardLimit = Math.Max(1, timer.moveTime - MoveOverhead);
            }
            else
            {
                long remaining = Math.Max(1, timer.MillisecondsRemaining - MoveOverhead);
                int movesToGo = TmBaseDiv;
                long baseTime = remaining / movesToGo + timer.IncrementMilliseconds * TmIncPct / 100;
                hardLimit = Math.Min(baseTime * TmHardPct / 100, remaining * 3 / 4);
                softLimit = Math.Min(baseTime * TmSoftPct / 100, hardLimit);
            }

            NNUE.updateAccumulators(board);

            if (moves.Length == 1 && !timer.isInfinite)
            {
                return (bestMove, NNUE.evaluate(board));
            }

            int currMaxDepth = maxDepth < 0 ? MAX_DEPTH : Math.Min(MAX_DEPTH, maxDepth);
            int dl, dh;
            for (int depth = 1; depth <= currMaxDepth; depth++)
            {
                dl = -ASPWindowDelta; dh = ASPWindowDelta;
                int score;
                bool failed = false;

                rootDepth = depth;

                if (depth >= 5)
                {
                    while (true)
                    {
                        int alpha = dl <= -ASPWindowReset ? -INFINITY : eval + dl, beta = dh >= ASPWindowReset ? INFINITY : eval + dh;
                        score = alphaBeta(board, 0, depth, alpha, beta);

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
                        if (stopSearch)
                        {
                            failed = true;
                            break;
                        }
                    }
                } else
                {
                    score = alphaBeta(board, 0, depth, -INFINITY, INFINITY);
                }

                if (stopSearch || failed)
                    break;

                eval = score;


                stability = bestMove.Equals(rootBestMove) ? Math.Min(stability + 1, 4) : 0;
                bestMove = rootBestMove;

                var pvString = new StringBuilder();
                for (int i = 0; i < pvLength[0]; i++)
                {
                    pvString.Append(' ').Append(Chess.MoveUtility.GetMoveNameUCI(pvTable[0, i].move));
                }

                Console.WriteLine("info depth " + depth + " score " + ScoreToUCI(eval) + " seldepth " + selDepth
                    + " nodes " + nodes + " time " + timer.MillisecondsElapsedThisTurn + " nps " + (nodes * 1000 / Math.Max(1, timer.MillisecondsElapsedThisTurn))
                    + " pv" + pvString);

                if (timer.isInfinite && stopSearch)
                {
                    break;
                }

                if (!timer.isInfinite)
                {
                    if (Math.Abs(eval) >= MATE_BOUND)
                    {
                        break;
                    }

                    if (timer.moveTime == -1)
                    {
                        double scale = 1.0;
                        if (depth >= 6)
                        {
                            double bmFrac = rootNodes[bestMove.StartSquare.Index * 64 + bestMove.TargetSquare.Index] / (double)Math.Max(1, nodes);
                            scale = (TmNodeBase / 100.0 - bmFrac) * (TmNodeMult / 100.0) * (StabScale[stability] / 100.0);
                        }

                        if (timer.MillisecondsElapsedThisTurn >= softLimit * scale)
                        { 
                            break;
                        }
                    }
                    else if (timer.MillisecondsElapsedThisTurn >= timer.moveTime)
                    {
                        break;
                    }
                }

            }

            return (bestMove, eval);
        }


        [SkipLocalsInit]
        private unsafe int alphaBeta(Board board, int ply, int depthLeft, int alpha, int beta, bool canNull = true, bool cutNode = false)
        {
            bool inCheck = board.IsInCheck();

            if (inCheck && ply <= rootDepth * 2)
            {
                depthLeft++;
            }

            if (depthLeft <= 0)
            {
                return qSearch(board, ply, alpha, beta, inCheck);
            }

            if (enterNode(ply))
            {
                return 0;
            }

            ref int corrHistEntry = ref getCorrHistEntry(board);

            ref var ppEntry = ref ppStack[ply];
            var prevPPE = ply > 0 ? ppStack[ply - 1] : default;
            var tpPrevPPE = ply > 1 ? ppStack[ply - 2] : default;

            if (ply >= MAX_DEPTH - 1)
            {
                return NNUE.evaluate(board) + corrHistEntry / CorrGrain;
            }

            int ogAlpha = alpha;
            bool pvNode = beta - alpha > 1;

            if (ply > 0 && isDraw(board))
            {
                return 0;
            }

            bool inSE = ppEntry.excludedMove != 0;

            ulong zobrist = board.ZobristKey;
            ref TTEntry entry = ref probeTT(zobrist, out bool hasEntry);
            int ttScore = hasEntry ? scoreFromTT(entry.score, ply) : -INFINITY;
            int ttDepth = hasEntry ? entry.depth : -1;
            int ttBound = hasEntry ? entry.bound : BOUND_NONE;
            Move ttMove = hasEntry ? entry.move : Move.NullMove;

            if (!pvNode && !inSE && hasEntry && ply > 0 && ttDepth >= depthLeft && isTTCutoff(entry.bound, ttScore, alpha, beta))
            {
                return ttScore;
            }

            if (!inSE && !hasEntry && depthLeft >= MinIIRDepth)
            {
                depthLeft--;
            }

            int rawEval = getRawEval(board, inCheck, hasEntry, in entry);
            int eval = correctEval(rawEval, corrHistEntry);


            ppEntry.staticEval = inCheck ? -INFINITY : eval;

            bool improving = !inCheck && ply >= 2 ? eval > tpPrevPPE.staticEval : false;

            int rfpMargin = RfpMargin * depthLeft - (improving ? RFPImpMargin : 0);

            if (!inSE && !inCheck && !pvNode && depthLeft <= RfpMaxDepth && Math.Abs(beta) < MATE_BOUND && eval >= beta + rfpMargin)
            {
                return eval;
            }

            if (!inSE && eval >= beta && !pvNode && !inCheck && canNull && depthLeft >= NMPMinDepth && beta < MATE_BOUND) {
                bool isPawnEndgame = board.IsWhiteToMove ? ((board.WhitePiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, true) ^ board.GetPieceBitboard(PieceType.King, true)) == 0)
                                                         : ((board.BlackPiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, false) ^ board.GetPieceBitboard(PieceType.King, false)) == 0);
                if (!isPawnEndgame) {
                    int reduction = NMPReductionB + depthLeft / NMPReductionDiv;

                    board.ForceSkipTurn();
                    ppEntry.pieceMoved = 0;
                    ppEntry.targetSquare = 1;
                    int score = -alphaBeta(board, ply + 1, depthLeft - reduction - 1, -beta, -beta + 1, canNull: false, cutNode: !cutNode);
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

            int seExtension = 0;

            if (!inSE && ply <= rootDepth * 2 && hasEntry && ply > SEMinPly && depthLeft >= SEMinDepth && ttDepth >= depthLeft - SEDepthMargin && !ttMove.IsNull && (ttBound == BOUND_LOWER || ttBound == BOUND_EXACT) && Math.Abs(ttScore) < MATE_BOUND)
            {
                int sBeta = ttScore - depthLeft * SEMargin / 16;
                ppStack[ply].excludedMove = ttMove.RawValue;
                int score = alphaBeta(board, ply, (depthLeft - 1) / 2, sBeta - 1, sBeta, canNull, cutNode);
                ppStack[ply].excludedMove = 0;

                if (stopSearch)
                {
                    return 0;
                }

                if (score < sBeta)
                {
                    if (!pvNode && score < sBeta - SEDMargin)
                    {
                        seExtension = 2;
                    }
                    else
                    {
                        seExtension = 1;
                    }
                } else if (score >= beta)
                {
                    return score;
                } else if (ttScore >= beta)
                {
                    seExtension = -1;
                }
            }

            Move* mv = stackalloc Move[218];
            Span<Move> moves = new Span<Move>(mv, 218);
            board.GetLegalMovesNonAlloc(ref moves);
            int moveCount = moves.Length;

            if (moveCount == 0)
            {
                return inCheck ? -MATE + ply : 0;
            }

            Move* srchd = stackalloc Move[moveCount];
            Span<Move> searched = new Span<Move>(srchd, moveCount);
            int searchedCount = 0;

            int sideToMove = board.IsWhiteToMove ? 0 : 1;

            int* scores = stackalloc int[moveCount];
            scoreMoves(board, ply, mv, scores, moveCount, ttMove, prevPPE.pieceMoved, prevPPE.targetSquare, sideToMove);

            Move bestMove = Move.NullMove;
            int bestScore = -INFINITY;
            int fpMargin = FpMargin * depthLeft;

            for (int i = 0; i < moveCount; i++)
            {
                Move move = pickMove(mv, scores, i, moveCount);

                if (shouldPrune(board, depthLeft, alpha, inCheck, ppEntry, prevPPE, pvNode, inSE, eval, improving, searchedCount, sideToMove, bestScore, fpMargin, move))
                {
                    continue;
                }

                int quietHist = move.IsCapture ? 0 : quietHistory(board, move, sideToMove, prevPPE.pieceMoved, prevPPE.targetSquare);

                long nodesBefore = nodes;

                makeMove(board, move, ply);

                int score;
                int baseSearchDepth = depthLeft - 1 + (move.Equals(ttMove) ? seExtension : 0);

                if (searchedCount == 0)
                {
                    score = -alphaBeta(board, ply + 1, baseSearchDepth, -beta, -alpha, cutNode: !pvNode && !cutNode);
                }
                else
                {
                    int reduction = 0;
                    if (depthLeft >= LmrMinDepth && searchedCount >= LmrMinMoves && !inCheck && !move.IsCapture && !move.IsPromotion)
                    {
                        reduction = lmrReduction(depthLeft, searchedCount) - quietHist / HHLMRDiv;
                        reduction = Math.Clamp(reduction + (improving ? 0 : 1), 0, depthLeft);
                    }
                    score = -alphaBeta(board, ply + 1, baseSearchDepth - reduction, -(alpha + 1), -alpha, cutNode: pvNode || !cutNode);
                    if (reduction > 0 && score > alpha)
                    {
                        score = -alphaBeta(board, ply + 1, baseSearchDepth, -(alpha + 1), -alpha, cutNode: pvNode || !cutNode);
                    }
                    if (score > alpha && score < beta)
                    {
                        score = -alphaBeta(board, ply + 1, baseSearchDepth, -beta, -alpha, cutNode: false);
                    }
                }

                undoMove(board, move);

                if (ply == 0)
                {
                    rootNodes[move.StartSquare.Index * 64 + move.TargetSquare.Index] += nodes - nodesBefore;
                }

                if (stopSearch)
                    return ply == 0 ? bestScore : 0;

                if (score >= beta)
                {
                    if (!inSE)
                    {
                        int hhBonus = Math.Min(HistMaxBonus, HistBonusMult * depthLeft + HistBonusBase);
                        int contBonus = Math.Min(MaxCHistBonus, CHistBonusMult * depthLeft + CHistBonusBase);
                        int capBonus = Math.Min(CaptureHistMaxBonus, CaptureHistBonusMult * depthLeft + CaptureHistBonusBase);

                        if (!move.IsCapture)
                        {
                            updateQuietHist(board, move, sideToMove, prevPPE.pieceMoved, prevPPE.targetSquare, hhBonus, contBonus, malus: false);
                            killers[ply] = move;
                        }
                        else
                        {
                            updateCaptHist(sideToMove, move, capBonus, malus: false);
                        }

                        for (int j = 0; j < searchedCount; j++)
                        {
                            Move currMove = searched[j];

                            if (currMove.IsCapture)
                            {
                                updateCaptHist(sideToMove, currMove, capBonus, malus: true);
                            }
                            else if (!move.IsCapture)
                            {
                                updateQuietHist(board, currMove, sideToMove, prevPPE.pieceMoved, prevPPE.targetSquare, hhBonus, contBonus, malus: true);
                            }
                        }

                        storeTT(zobrist, score, depthLeft, ply, BOUND_LOWER, move, rawEval);

                        if (!inCheck && !move.IsCapture && !move.IsPromotion && Math.Abs(score) < MATE_BOUND && score > eval)
                        {
                            updateCorrHist(ref corrHistEntry, score, rawEval, depthLeft);
                        }
                    }

                    if (ply == 0)
                    {
                        rootBestMove = move;
                    }

                    return score;
                }

                searched[searchedCount++] = move;

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
                    updatePV(ply, move);
                }
            }

            if (!inSE)
            {
                byte bound = bestScore <= ogAlpha ? BOUND_UPPER : BOUND_EXACT;
                storeTT(zobrist, bestScore, depthLeft, ply, bound, bestMove, rawEval);
                if (!inCheck && !bestMove.IsCapture && !bestMove.IsPromotion && Math.Abs(bestScore) < MATE_BOUND
                    && ((bound == BOUND_UPPER && bestScore < eval) || bound == BOUND_EXACT))
                {
                    updateCorrHist(ref corrHistEntry, bestScore, rawEval, depthLeft);
                }
            }

            return bestScore;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool shouldPrune(Board board, int depthLeft, int alpha, bool inCheck, (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove) ppEntry, (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove) prevPPE, bool pvNode, bool inSE, int eval, bool improving, int searchedCount, int sideToMove, int bestScore, int fpMargin, Move move)
        {
            if (inSE && move.RawValue == ppEntry.excludedMove)
            {
                return true;
            }

            if (!pvNode && !inCheck)
            {
                if (depthLeft <= FpMaxDepth && !move.IsCapture && !move.IsPromotion && searchedCount > 0 && Math.Abs(alpha) < MATE_BOUND && eval + fpMargin <= alpha)
                {
                    return true;
                }

                if (depthLeft <= LMPMaxDepth && !move.IsCapture && !move.IsPromotion && bestScore > -MATE_BOUND && searchedCount >= (LMPBase + (depthLeft * depthLeft)) / (improving ? 1 : 2))
                {
                    return true;
                }

                if (depthLeft <= SEEPMaxDepth && searchedCount > 0)
                {
                    if (move.IsCapture && !see(board, move, Math.Min(0, -SEEPCaptureThreshold * depthLeft - captureHist[getCaptureHistIndex(sideToMove, move)] * SEEPCaptureHistMult / 32_768)))
                    {
                        return true;
                    }
                    if (!move.IsCapture)
                    {
                        int seepHist = quietHistory(board, move, sideToMove, prevPPE.pieceMoved, prevPPE.targetSquare);
                        if (!see(board, move, Math.Min(0, -SEEPThreshold * depthLeft - seepHist * SEEPHistMult / 32_768)))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        [SkipLocalsInit]
        private unsafe int qSearch(Board board, int ply, int alpha, int beta, bool inCheck)
        {
            if (enterNode(ply))
            {
                return 0;
            }

            ref int corrHistEntry = ref getCorrHistEntry(board);

            var prevPPE = ppStack[ply - 1];

            if (ply >= MAX_DEPTH - 1)
            {
                return NNUE.evaluate(board) + corrHistEntry / CorrGrain;
            }

            bool pvNode = beta - alpha > 1;

            if (isDraw(board))
            {
                return 0;
            }

            ref TTEntry entry = ref probeTT(board.ZobristKey, out bool hasEntry);
            int ttScore = hasEntry ? scoreFromTT(entry.score, ply) : -INFINITY;
            Move ttMove = hasEntry ? entry.move : Move.NullMove;

            if (!pvNode && hasEntry && isTTCutoff(entry.bound, ttScore, alpha, beta))
            {
                return ttScore;
            }

            int eval = correctEval(getRawEval(board, inCheck, hasEntry, in entry), corrHistEntry);

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

            Move* mv = stackalloc Move[218];
            Span<Move> moves = new Span<Move>(mv, 218);
            board.GetLegalMovesNonAlloc(ref moves, !inCheck);
            int moveCount = moves.Length;

            if (moveCount == 0)
            {
                return inCheck ? -MATE + ply : eval;
            }

            int sideToMove = board.IsWhiteToMove ? 0 : 1;

            int* scores = stackalloc int[moveCount];
            scoreMoves(board, ply, mv, scores, moveCount, ttMove, prevPPE.pieceMoved, prevPPE.targetSquare, sideToMove);

            int bestScore = inCheck ? -INFINITY : eval;

            for (int i = 0; i < moveCount; i++)
            {
                Move move = pickMove(mv, scores, i, moveCount);

                // score lower than -500_000 is losing capture
                if (!inCheck && scores[i] < -500_000)
                {
                    break;
                }

                makeMove(board, move, ply);
                bool givesCheck = board.IsInCheck();

                int score;
                if (i == 0)
                {
                    score = -qSearch(board, ply + 1, -beta, -alpha, givesCheck);
                }
                else
                {
                    score = -qSearch(board, ply + 1, -(alpha + 1), -alpha, givesCheck);
                    if (score > alpha && score < beta)
                    {
                        score = -qSearch(board, ply + 1, -beta, -alpha, givesCheck);
                    }
                }

                undoMove(board, move);

                if (stopSearch)
                    return 0;

                if (score >= beta)
                {
                    return score;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                }
                if (score > alpha)
                {
                    alpha = score;
                    updatePV(ply, move);
                }
            }

            return bestScore;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool enterNode(int ply)
        {
            pvLength[ply] = ply;
            if (stopSearch)
            {
                return true;
            }

            if ((++nodes & 2047) == 0 && timer.MillisecondsElapsedThisTurn >= hardLimit)
            {
                stopSearch = true;
                return true;
            }

            if (ply + 1 > selDepth)
            {
                selDepth = ply + 1;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool isDraw(Board board)
            => board.IsFiftyMoveDraw() || board.IsRepeatedPosition() || board.IsInsufficientMaterial();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref int getCorrHistEntry(Board board)
        {
            ulong x = board.GetPieceBitboard(PieceType.Pawn, true) * 0x9E3779B97F4A7C15UL ^ board.GetPieceBitboard(PieceType.Pawn, false) * 0xC2B2AE3D27D4EB4FUL;
            uint idx = (uint)(x >> (64 - CORR_HIST_EXP));

            return ref correctionHist[(board.IsWhiteToMove ? 0 : CORR_HIST_ENTRIES) + (int)idx];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref TTEntry probeTT(ulong zobrist, out bool hasEntry)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            hasEntry = entry.bound != BOUND_NONE && entry.key == (uint)(zobrist >> 32);
            return ref entry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool isTTCutoff(byte bound, int ttScore, int alpha, int beta)
            => bound == BOUND_EXACT
            || (bound == BOUND_LOWER && ttScore >= beta)
            || (bound == BOUND_UPPER && ttScore <= alpha);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int getRawEval(Board board, bool inCheck, bool hasEntry, in TTEntry entry)
            => inCheck ? -INFINITY
             : hasEntry && entry.staticEval != NO_EVAL ? entry.staticEval
             : NNUE.evaluate(board);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int correctEval(int rawEval, int corrHistEntry)
            => rawEval + (rawEval > -INFINITY ? corrHistEntry / CorrGrain : 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void scoreMoves(Board board, int ply, Move* moves, int* scores, int moveCount, Move ttMove, int prevPiece, int prevTo, int sideToMove)
        {
            for (int i = 0; i < moveCount; i++)
            {
                scores[i] = scoreMove(board, ply, moves[i], ttMove, prevPiece, prevTo, sideToMove);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe Move pickMove(Move* moves, int* scores, int i, int moveCount)
        {
            int best = i;
            int bestVal = scores[i];
            for (int j = i + 1; j < moveCount; j++)
            {
                int v = scores[j];
                if (v > bestVal)
                {
                    best = j;
                    bestVal = v;
                }
            }

            (moves[i], moves[best]) = (moves[best], moves[i]);
            scores[best] = scores[i];
            scores[i] = bestVal;

            return moves[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void makeMove(Board board, Move move, int ply)
        {
            board.MakeMove(move);
            prefetchTT(board.ZobristKey);
            NNUE.makeMove(board, move, !board.IsWhiteToMove);

            ppStack[ply].pieceMoved = (byte)move.MovePieceType;
            ppStack[ply].targetSquare = (byte)move.TargetSquare.Index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void undoMove(Board board, Move move)
        {
            NNUE.undoMove();
            board.UndoMove(move);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int quietHistory(Board board, Move move, int sideToMove, int prevPiece, int prevTo)
        {
            return historyHeuristic[getHistoryHeuristicIndex(board, move)]
                 + continuationHist[getContinuationHistoryIndex(sideToMove, prevPiece, prevTo, (int)move.MovePieceType, move.TargetSquare.Index)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void updateHist(ref int entry, int bonus, int divisor, bool malus)
        {
            entry += (malus ? -bonus : bonus) - entry * bonus / divisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void updateQuietHist(Board board, Move move, int sideToMove, int prevPiece, int prevTo, int hhBonus, int contBonus, bool malus)
        {
            updateHist(ref historyHeuristic[getHistoryHeuristicIndex(board, move)], hhBonus, HistDivisor, malus);
            updateHist(ref continuationHist[getContinuationHistoryIndex(sideToMove, prevPiece, prevTo, (int)move.MovePieceType, move.TargetSquare.Index)], contBonus, CHistoryDivisor, malus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void updateCaptHist(int sideToMove, Move move, int capBonus, bool malus)
        {
            updateHist(ref captureHist[getCaptureHistIndex(sideToMove, move)], capBonus, CaptureHistDivisor, malus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int getCaptureHistIndex(int sideToMove, Move move)
        {
            return ((sideToMove * 7 + (int)move.MovePieceType) * 64 + move.TargetSquare.Index) * 7 + (int)move.CapturePieceType;
        }

        void updateCorrHist(ref int entry, int score, int rawEval, int depth)
        {
            int diff = (score - rawEval) * CorrGrain;
            int w = Math.Min(depth + 1, 16);
            entry = (entry * (CorrScale - w) + diff * w) / CorrScale;
            entry = Math.Clamp(entry, -CorrMax, CorrMax);
        }

        const int LMR_DEPTHS = MAX_DEPTH, LMR_MOVES = 218;
        static int[] lmrTable = new int[LMR_DEPTHS * LMR_MOVES]; // [depth][moves]
        static int lmrBuiltBase = int.MinValue, lmrBuiltDiv = int.MinValue;

        static void ensureLmrTable()
        {
            if (lmrBuiltBase == LmrBase && lmrBuiltDiv == LmrDivisor)
            {
                return;
            }

            for (int d = 1; d < LMR_DEPTHS; d++)
            {
                for (int m = 1; m < LMR_MOVES; m++)
                {
                    lmrTable[d * LMR_MOVES + m] = (int)(LmrBase / 100.0 + Math.Log(d) * Math.Log(m) / (LmrDivisor / 100.0));
                }
            }

            lmrBuiltBase = LmrBase;
            lmrBuiltDiv = LmrDivisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int lmrReduction(int depth, int moves)
        {
            return depth < LMR_DEPTHS && moves < LMR_MOVES
                        ? lmrTable[depth * LMR_MOVES + moves]
                        : (int)(LmrBase / 100.0 + Math.Log(depth) * Math.Log(moves) / (LmrDivisor / 100.0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void prefetchTT(ulong zobrist)
        {
            if (Sse.IsSupported)
                Sse.Prefetch0(Unsafe.AsPointer(ref transpositionTable[zobrist & ttMask]));
        }

        private void storeTT(ulong zobrist, int score, int depthLeft, int ply, byte bound, Move move, int staticEval)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            uint key = (uint)(zobrist >> 32);
            byte depth = (byte)Math.Clamp(depthLeft, 0, 255);

            if (entry.bound != BOUND_NONE && entry.key == key && depth < entry.depth)
            {
                return;
            }

            entry.key = key;
            entry.score = scoreToTT(score, ply);
            entry.move = move;
            entry.depth = depth;
            entry.bound = bound;
            entry.staticEval = staticEval > short.MinValue && staticEval <= short.MaxValue ? (short)staticEval : NO_EVAL;
        }
        private static int scoreToTT(int score, int ply)
            => score > MATE_BOUND ? score + ply
             : score < -MATE_BOUND ? score - ply
             : score;

        private static int scoreFromTT(int score, int ply)
            => score > MATE_BOUND ? score - ply
             : score < -MATE_BOUND ? score + ply
             : score;

        private int scoreMove(Board board, int ply, Move move, Move ttMove, int prevPiece, int prevTo, int sideToMove)
        {
            if (move.Equals(ttMove))
            {
                return int.MaxValue;
            }

            if (move.IsCapture)
            {
                int captureHistScore = captureHist[getCaptureHistIndex(sideToMove, move)];
                int seeThreshold = move.IsPromotion || move.IsEnPassant ? 0 : -captureHistScore * CaptureHistSEEMult / 32_768;
                return (see(board, move, seeThreshold) ? 1_000_000 : -1_000_000) 
                    + MvvMult * pieceVals[(int)move.CapturePieceType] 
                    + captureHistScore;
            }

            if (move.Equals(killers[ply]))
            {
                return 900_000;
            }

            return quietHistory(board, move, sideToMove, prevPiece, prevTo);
        }

        public static readonly int[] pieceVals = [0, 100, 290, 337, 504, 926, 1_000_000];

        private bool see(Board board, Move move, int threshold)
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

            ulong attackers = attackersTo(move.TargetSquare, occ, wPawns, bPawns, knights, kings, bishops, rooks, queens);
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

        private static ulong attackersTo(Square to, ulong occ, ulong wPawns, ulong bPawns, ulong knights, ulong kings, ulong bishops, ulong rooks, ulong queens)
        {
            return (BitboardHelper.GetPawnAttacks(to, true) & bPawns)
                 | (BitboardHelper.GetPawnAttacks(to, false) & wPawns)
                 | (BitboardHelper.GetKnightAttacks(to) & knights)
                 | (BitboardHelper.GetKingAttacks(to) & kings)
                 | (BitboardHelper.GetSliderAttacks(PieceType.Bishop, to, occ) & (bishops | queens))
                 | (BitboardHelper.GetSliderAttacks(PieceType.Rook, to, occ) & (rooks | queens));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int getHistoryHeuristicIndex(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}