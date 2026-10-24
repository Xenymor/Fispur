using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
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

        const short NO_EVAL = short.MinValue;

        public string GetAuthor()
        {
            return "Xenymor";
        }

        public string GetName()
        {
            return "dev";
        }

        public Fispur()
        {
            SetHashSize(DEFAULT_HASH_MB);
        }

        private static string ScoreToUCI(int score)
        {
            if (Math.Abs(score) <= MATE_BOUND)
                return "cp " + score;

            int plies = MATE - Math.Abs(score);
            int moves = (plies + 1) / 2;

            return "mate " + (score > 0 ? moves : -moves);
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

            long entries = (long)megabytes * 1024 * 1024 / Unsafe.SizeOf<TTEntry>();

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
    }
}