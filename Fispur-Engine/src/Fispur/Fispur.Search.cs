using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove)[] ppStack = new (int staticEval, byte pieceMoved, byte targetSquare, ushort excludedMove)[MAX_DEPTH];

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
            TTEntry ttEntry = probeTT(zobrist, out bool hasEntry);
            int ttScore = hasEntry ? scoreFromTT(ttEntry.score, ply) : -INFINITY;

            if (!pvNode && !inSE && hasEntry && ply > 0 && ttEntry.depth >= depthLeft && isTTCutoff(ttEntry.bound, ttScore, alpha, beta))
            {
                return ttScore;
            }

            if (!inSE && !hasEntry && depthLeft >= MinIIRDepth)
            {
                depthLeft--;
            }

            int rawEval = getRawEval(board, inCheck, hasEntry, in ttEntry);
            int eval = correctEval(rawEval, corrHistEntry);


            ppEntry.staticEval = inCheck ? -INFINITY : eval;

            bool improving = !inCheck && ply >= 2 ? eval > tpPrevPPE.staticEval : false;

            int rfpMargin = RfpMargin * depthLeft - (improving ? RFPImpMargin : 0);

            if (!inSE && !inCheck && !pvNode && depthLeft <= RfpMaxDepth && Math.Abs(beta) < MATE_BOUND && eval >= beta + rfpMargin)
            {
                return eval;
            }

            if (!inSE && eval >= beta && !pvNode && !inCheck && canNull && depthLeft >= NMPMinDepth && beta < MATE_BOUND)
            {
                bool isPawnEndgame = board.IsWhiteToMove ? ((board.WhitePiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, true) ^ board.GetPieceBitboard(PieceType.King, true)) == 0)
                                                         : ((board.BlackPiecesBitboard ^ board.GetPieceBitboard(PieceType.Pawn, false) ^ board.GetPieceBitboard(PieceType.King, false)) == 0);
                if (!isPawnEndgame)
                {
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

            if (!inSE && ply <= rootDepth * 2 && hasEntry && ply > SEMinPly && depthLeft >= SEMinDepth && ttEntry.depth >= depthLeft - SEDepthMargin && !ttEntry.move.IsNull && (ttEntry.bound == BOUND_LOWER || ttEntry.bound == BOUND_EXACT) && Math.Abs(ttScore) < MATE_BOUND)
            {
                int sBeta = ttScore - depthLeft * SEMargin / 16;
                ppStack[ply].excludedMove = ttEntry.move.RawValue;
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
                }
                else if (score >= beta)
                {
                    return score;
                }
                else if (ttScore >= beta)
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
            scoreMoves(board, ply, mv, scores, moveCount, ttEntry.move, prevPPE.pieceMoved, prevPPE.targetSquare, sideToMove);

            Move bestMove = Move.NullMove;
            int bestScore = -INFINITY;
            int fpMargin = FpMargin * depthLeft;

            for (int i = 0; i < moveCount; i++)
            {
                Move move = pickMove(mv, scores, i, moveCount);

                if (shouldPrune(board, depthLeft, alpha, inCheck, ppEntry.excludedMove, prevPPE.pieceMoved, prevPPE.targetSquare, pvNode, inSE, eval, improving, searchedCount, sideToMove, bestScore, fpMargin, move))
                {
                    continue;
                }

                int quietHist = move.IsCapture ? 0 : quietHistory(board, move, sideToMove, prevPPE.pieceMoved, prevPPE.targetSquare);

                long nodesBefore = nodes;

                makeMove(board, move, ply);

                int score;
                int baseSearchDepth = depthLeft - 1 + (move.Equals(ttEntry.move) ? seExtension : 0);

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
        private bool shouldPrune(Board board, int depthLeft, int alpha, bool inCheck, int excludedMove, int prevPiece, int prevTo, bool pvNode, bool inSE, int eval, bool improving, int searchedCount, int sideToMove, int bestScore, int fpMargin, Move move)
        {
            if (move.RawValue == excludedMove)
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
                        int seepHist = quietHistory(board, move, sideToMove, prevPiece, prevTo);
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

            TTEntry ttEntry = probeTT(board.ZobristKey, out bool hasEntry);
            int ttScore = hasEntry ? scoreFromTT(ttEntry.score, ply) : -INFINITY;

            if (!pvNode && hasEntry && isTTCutoff(ttEntry.bound, ttScore, alpha, beta))
            {
                return ttScore;
            }

            int eval = correctEval(getRawEval(board, inCheck, hasEntry, in ttEntry), corrHistEntry);

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
            scoreMoves(board, ply, mv, scores, moveCount, ttEntry.move, prevPPE.pieceMoved, prevPPE.targetSquare, sideToMove);

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
        {
            return board.IsFiftyMoveDraw() || board.IsRepeatedPosition() || board.IsInsufficientMaterial();
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
    }
}
