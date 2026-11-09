using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        TTEntry[] transpositionTable;
        ulong ttMask;

        enum Bound : byte
        {
            BOUND_NONE, BOUND_LOWER, BOUND_UPPER, BOUND_EXACT
        }

        struct TTEntry
        {
            public uint key; //first 32 bit
            public int score;
            public Move move;
            public ushort depthBound;
            public short staticEval; // NNUE eval of this position, NO_EVAL if unknown

            public readonly Bound bound
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => (Bound)(depthBound & 3);
            }

            public readonly int depth
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => (depthBound >> 2) << TT_DEPTH_SHIFT;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ushort pack(int depthLeft, Bound bound)
            {
                return (ushort)((Math.Clamp(depthLeft, 0, MAX_DEPTH * ONE_PLY - 1) >> TT_DEPTH_SHIFT) << 2 | (int)bound);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TTEntry probeTT(ulong zobrist, out bool hasEntry)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            hasEntry = entry.bound != Bound.BOUND_NONE && entry.key == (uint)(zobrist >> 32);
            return hasEntry ? entry : default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool isTTCutoff(Bound bound, int ttScore, int alpha, int beta)
        {
            return bound == Bound.BOUND_EXACT
                    || (bound == Bound.BOUND_LOWER && ttScore >= beta)
                    || (bound == Bound.BOUND_UPPER && ttScore <= alpha);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int getRawEval(Board board, bool inCheck, bool hasEntry, in TTEntry entry)
        {
            return inCheck ? -INFINITY
                     : hasEntry && entry.staticEval != NO_EVAL ? entry.staticEval
                     : NNUE.evaluate(board);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void prefetchTT(ulong zobrist)
        {
            if (Sse.IsSupported)
                Sse.Prefetch0(Unsafe.AsPointer(ref transpositionTable[zobrist & ttMask]));
        }

        private void storeTT(ulong zobrist, int score, int depthLeft, int ply, Bound bound, Move move, int staticEval)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            uint key = (uint)(zobrist >> 32);
            ushort depthBound = TTEntry.pack(depthLeft, bound);

            if (entry.bound != Bound.BOUND_NONE && entry.key == key && (depthBound >> 2) < (entry.depthBound >> 2))
            {
                return;
            }

            entry.key = key;
            entry.score = scoreToTT(score, ply);
            entry.move = move;
            entry.depthBound = depthBound;
            entry.staticEval = staticEval > short.MinValue && staticEval <= short.MaxValue ? (short)staticEval : NO_EVAL;
        }
        private static int scoreToTT(int score, int ply)
        {
            return score > MATE_BOUND ? score + ply
                     : score < -MATE_BOUND ? score - ply
                     : score;
        }

        private static int scoreFromTT(int score, int ply)
        {
            return score > MATE_BOUND ? score - ply
                     : score < -MATE_BOUND ? score + ply
                     : score;
        }
    }
}
