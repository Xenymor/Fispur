namespace FispurEngine.Chess
{
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using static PrecomputedMagics;

    // Helper class for magic bitboards.
    // This is a technique where bishop and rook moves are precomputed
    // for any configuration of origin square and blocking pieces.
    public static class Magic
    {
        // Rook and bishop mask bitboards for each origin square.
        // A mask is simply the legal moves available to the piece from the origin square
        // (on an empty board), except that the moves stop 1 square before the edge of the board.
        public static readonly ulong[] RookMask;
        public static readonly ulong[] BishopMask;

        public static readonly ulong[][] RookAttacks;
        public static readonly ulong[][] BishopAttacks;

        // Everything needed for a lookup of one square packed together (one cache line access instead of four arrays)
        struct MagicEntry
        {
            public ulong Mask;
            public ulong Magic;
            public int Shift;
            public int Offset; // start of this square's attacks inside AttackTable
        }

        static readonly MagicEntry[] RookEntries;
        static readonly MagicEntry[] BishopEntries;
        // Rook and bishop attack tables of all squares, stored back to back in a single array
        static readonly ulong[] AttackTable;


        public static ulong GetSliderAttacks(int square, ulong blockers, bool ortho)
        {
            return ortho ? GetRookAttacks(square, blockers) : GetBishopAttacks(square, blockers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetRookAttacks(int square, ulong blockers)
        {
            return Lookup(in RookEntries[square], blockers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetBishopAttacks(int square, ulong blockers)
        {
            return Lookup(in BishopEntries[square], blockers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Lookup(in MagicEntry entry, ulong blockers)
        {
            ulong key = ((blockers & entry.Mask) * entry.Magic) >> entry.Shift;
            // key < 2^(64 - shift) by construction, so it always lies within this square's table: skip the bounds check
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(AttackTable), entry.Offset + (int)key);
        }


        static Magic()
        {
            RookMask = new ulong[64];
            BishopMask = new ulong[64];

            for (int squareIndex = 0; squareIndex < 64; squareIndex++)
            {
                RookMask[squareIndex] = MagicHelper.CreateMovementMask(squareIndex, true);
                BishopMask[squareIndex] = MagicHelper.CreateMovementMask(squareIndex, false);
            }

            RookAttacks = new ulong[64][];
            BishopAttacks = new ulong[64][];

            for (int i = 0; i < 64; i++)
            {
                RookAttacks[i] = CreateTable(i, true, RookMagics[i], RookShifts[i]);
                BishopAttacks[i] = CreateTable(i, false, BishopMagics[i], BishopShifts[i]);
            }

            // Pack all tables into one flat array
            int totalSize = 0;
            for (int i = 0; i < 64; i++)
            {
                totalSize += RookAttacks[i].Length + BishopAttacks[i].Length;
            }

            AttackTable = new ulong[totalSize];
            RookEntries = new MagicEntry[64];
            BishopEntries = new MagicEntry[64];
            int offset = 0;

            for (int i = 0; i < 64; i++)
            {
                RookEntries[i] = new MagicEntry { Mask = RookMask[i], Magic = RookMagics[i], Shift = RookShifts[i], Offset = offset };
                RookAttacks[i].CopyTo(AttackTable, offset);
                offset += RookAttacks[i].Length;
            }
            for (int i = 0; i < 64; i++)
            {
                BishopEntries[i] = new MagicEntry { Mask = BishopMask[i], Magic = BishopMagics[i], Shift = BishopShifts[i], Offset = offset };
                BishopAttacks[i].CopyTo(AttackTable, offset);
                offset += BishopAttacks[i].Length;
            }

            ulong[] CreateTable(int square, bool rook, ulong magic, int leftShift)
            {
                int numBits = 64 - leftShift;
                int lookupSize = 1 << numBits;
                ulong[] table = new ulong[lookupSize];

                ulong movementMask = MagicHelper.CreateMovementMask(square, rook);
                ulong[] blockerPatterns = MagicHelper.CreateAllBlockerBitboards(movementMask);

                foreach (ulong pattern in blockerPatterns)
                {
                    ulong index = (pattern * magic) >> leftShift;
                    ulong moves = MagicHelper.LegalMoveBitboardFromBlockers(square, pattern, rook);
                    table[index] = moves;
                }

                return table;
            }
        }

    }
}
