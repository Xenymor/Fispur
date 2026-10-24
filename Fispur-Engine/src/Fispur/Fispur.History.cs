using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;
using static FispurEngine.Fispur.Tunables;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        int[] historyHeuristic = new int[2 * 64 * 64];
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref int getCorrHistEntry(Board board)
        {
            ulong x = board.GetPieceBitboard(PieceType.Pawn, true) * 0x9E3779B97F4A7C15UL ^ board.GetPieceBitboard(PieceType.Pawn, false) * 0xC2B2AE3D27D4EB4FUL;
            uint idx = (uint)(x >> (64 - CORR_HIST_EXP));

            return ref correctionHist[(board.IsWhiteToMove ? 0 : CORR_HIST_ENTRIES) + (int)idx];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int correctEval(int rawEval, int corrHistEntry)
        {
            return rawEval + (rawEval > -INFINITY ? corrHistEntry / CorrHistGrain : 0);
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
            updateHist(ref historyHeuristic[getHistoryHeuristicIndex(board, move)], hhBonus, HistoryDivisor, malus);
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
            int diff = (score - rawEval) * CorrHistGrain;
            int w = Math.Min(depth + 1, 16);
            entry = (entry * (CorrHistScale - w) + diff * w) / CorrHistScale;
            entry = Math.Clamp(entry, -CorrHistMax, CorrHistMax);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int getHistoryHeuristicIndex(Board board, Move move)
        {
            return (board.IsWhiteToMove ? 0 : 1) * 64 * 64 + (move.StartSquare.Index | move.TargetSquare.Index << 6);
        }
    }
}
