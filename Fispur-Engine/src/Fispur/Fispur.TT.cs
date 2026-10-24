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
        struct TTEntry
        {
            public uint key; //first 32 bit
            public int score;
            public Move move;
            public byte depth;
            public byte bound;
            public short staticEval; // NNUE eval of this position, NO_EVAL if unknown
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TTEntry probeTT(ulong zobrist, out bool hasEntry)
        {
            ref TTEntry entry = ref transpositionTable[zobrist & ttMask];
            hasEntry = entry.bound != BOUND_NONE && entry.key == (uint)(zobrist >> 32);
            return hasEntry ? entry : default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool isTTCutoff(byte bound, int ttScore, int alpha, int beta)
        {
            return bound == BOUND_EXACT
                    || (bound == BOUND_LOWER && ttScore >= beta)
                    || (bound == BOUND_UPPER && ttScore <= alpha);
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
