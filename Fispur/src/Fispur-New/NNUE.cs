using FispurEngine.API;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FispurEngine
{
    public static unsafe class NNUE
    {
        const int INPUT = 768;
        const int HL = 1_024;
        private const int OUTPUT_BUCKETS = 8;
        const int QA = 255;
        const int QB = 64;
        const int QAB = QA * QB;
        const int SCALE = 400;

        // All weights and accumulators live in 32-byte aligned native memory:
        // no bounds checks, no pinning via `fixed`, aligned AVX2 loads/stores.
        static readonly short* l0w;
        static readonly short* l0b;
        static readonly short* l1w;
        static readonly short* l1b;

        // Accumulator stack, one entry per ply: [ply][perspective (0 = white, 1 = black)][HL]
        static readonly short* acc;
        const int PLY_STRIDE = 2 * HL;
        static int currPly = 0;

        static readonly int[] KING_BUCKET_LAYOUT =
        {
            0, 1, 2, 3,
            4, 4, 5, 5,
            6, 6, 6, 6,
            7, 7, 7, 7,
            8, 8, 8, 8,
            8, 8, 8, 8,
            9, 9, 9, 9,
            9, 9, 9, 9,
        };
        const int KING_BUCKETS = 10;

        static readonly int[] KING_BUCKET = new int[64];

        const int FINNY_ENTRIES = 2 * KING_BUCKETS * 2;
        private const string FILE_NAME = "10kb-1024hl-8ob.bin";
        static readonly short* finnyAcc;
        static readonly ulong[] finnyBB = new ulong[FINNY_ENTRIES * 12];

        static NNUE()
        {
            int maxBucket = 0;
            foreach (int b in KING_BUCKET_LAYOUT)
            {
                maxBucket = Math.Max(maxBucket, b);
            }

            if (KING_BUCKET_LAYOUT.Length != 32 || maxBucket + 1 != KING_BUCKETS)
            {
                throw new InvalidOperationException("KING_BUCKET_LAYOUT does not match KING_BUCKETS");
            }

            for (int sq = 0; sq < 64; sq++)
            {
                int file = sq & 7;
                int mirroredFile = file > 3 ? 7 - file : file;
                KING_BUCKET[sq] = KING_BUCKET_LAYOUT[(sq >> 3) * 4 + mirroredFile];
            }

            l0w = (short*)NativeMemory.AlignedAlloc(KING_BUCKETS * INPUT * HL       * sizeof(short), 64);
            l0b = (short*)NativeMemory.AlignedAlloc(HL                              * sizeof(short), 64);
            l1w = (short*)NativeMemory.AlignedAlloc(OUTPUT_BUCKETS * 2 * HL         * sizeof(short), 64);
            l1b = (short*)NativeMemory.AlignedAlloc(OUTPUT_BUCKETS                  * sizeof(short), 64);
            acc = (short*)NativeMemory.AlignedAlloc(Fispur.MAX_DEPTH * PLY_STRIDE   * sizeof(short), 64);
            finnyAcc = (short*)NativeMemory.AlignedAlloc(FINNY_ENTRIES * HL         * sizeof(short), 64);

            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(FILE_NAME)!;
            long expected = ((long)KING_BUCKETS * INPUT * HL + HL + OUTPUT_BUCKETS * 2 * HL + OUTPUT_BUCKETS) * sizeof(short);
            if (s.Length < expected || s.Length - expected >= 64)
            {
                throw new InvalidDataException($"{FILE_NAME}: {s.Length} bytes, expected {expected} (+ <64 padding)");
            }

            using var r = new BinaryReader(s);
            for (int i = 0; i < KING_BUCKETS * INPUT * HL; i++)
            {
                l0w[i] = r.ReadInt16();
            }

            for (int i = 0; i < HL; i++)
            {
                l0b[i] = r.ReadInt16();
            }

            for (int i = 0; i < OUTPUT_BUCKETS * 2 * HL; i++)
            {
                l1w[i] = r.ReadInt16();
            }

            for (int i = 0; i < OUTPUT_BUCKETS; i++)
            {
                l1b[i] = r.ReadInt16();
            }

            for (int i = 0; i < FINNY_ENTRIES; i++)
            {
                Copy(finnyAcc + i * HL, l0b);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static short* Acc(int ply, int perspective)
        {
            return acc + ply * PLY_STRIDE + perspective * HL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int RelKingSq(Board board, int perspective)
        {
            return board.GetKingSquare(perspective == 0).Index ^ (perspective * 56);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FlipOf(int relKingSq)
        {
            return (relKingSq & 7) > 3 ? 7 : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Feature(int perspective, int bucket, int flip, int color, int pc, int sq)
        {
            int relColor = color ^ perspective;
            int relSq = sq ^ (perspective * 56) ^ flip;
            return (bucket * INPUT + 384 * relColor + 64 * pc + relSq) * HL;
        }

        public static int Evaluate(Board board)
        {
            short bucket = (short)((ulong.PopCount(board.AllPiecesBitboard) - 2) / 4);

            short* boys = Acc(currPly, board.IsWhiteToMove ? 0 : 1);
            short* opps = Acc(currPly, board.IsWhiteToMove ? 1 : 0);
            short* w = l1w + bucket * 2 * HL;

            Vector256<short> zero = Vector256<short>.Zero;
            Vector256<short> qa = Vector256.Create((short)QA);
            Vector256<int> sum0 = Vector256<int>.Zero;
            Vector256<int> sum1 = Vector256<int>.Zero;


            for (int h = 0; h < HL; h += 16)
            {
                Vector256<short> v = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(boys + h), zero), qa);
                sum0 = Avx2.Add(sum0, Avx2.MultiplyAddAdjacent(v, Avx2.MultiplyLow(v, Avx.LoadAlignedVector256(w + h))));

                Vector256<short> o = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(opps + h), zero), qa);
                sum1 = Avx2.Add(sum1, Avx2.MultiplyAddAdjacent(o, Avx2.MultiplyLow(o, Avx.LoadAlignedVector256(w + HL + h))));
            }

            Vector256<int> sum = Avx2.Add(sum0, sum1);
            Vector128<int> s = Sse2.Add(sum.GetLower(), sum.GetUpper());
            s = Ssse3.HorizontalAdd(s, s);
            s = Ssse3.HorizontalAdd(s, s);

            long total = s.ToScalar();
            return (int)((total / QA + l1b[bucket]) * SCALE / QAB);
        }

        public static void UpdateAccumulators(Board board)
        {
            currPly = 0;
            Refresh(board, 0, Acc(0, 0));
            Refresh(board, 1, Acc(0, 1));
        }

        static void Refresh(Board board, int perspective, short* dst)
        {
            int ksq = RelKingSq(board, perspective);
            int bucket = KING_BUCKET[ksq];
            int flip = FlipOf(ksq);

            int entry = (perspective * KING_BUCKETS + bucket) * 2 + (flip != 0 ? 1 : 0);
            short* cached = finnyAcc + entry * HL;
            int bbBase = entry * 12;

            for (int color = 0; color <= 1; color++)
            {
                for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
                {
                    int pc = (int)type - 1;
                    int idx = bbBase + color * 6 + pc;

                    ulong curr = board.GetPieceBitboard(type, color == 0);
                    ulong prev = finnyBB[idx];
                    ulong added = curr & ~prev;
                    ulong removed = prev & ~curr;

                    while (added != 0)
                    {
                        int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref added);
                        AddInPlace(cached, l0w + Feature(perspective, bucket, flip, color, pc, sq));
                    }
                    while (removed != 0)
                    {
                        int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref removed);
                        SubInPlace(cached, l0w + Feature(perspective, bucket, flip, color, pc, sq));
                    }

                    finnyBB[idx] = curr;
                }
            }

            Copy(dst, cached);
        }

        internal static void makeMove(Board board, Move move, bool isWhite)
        {
            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;
            int from = move.StartSquare.Index, to = move.TargetSquare.Index;

            short* src0 = Acc(currPly, 0), src1 = Acc(currPly, 1);
            currPly++;
            short* dst0 = Acc(currPly, 0), dst1 = Acc(currPly, 1);

            bool refreshMover = false;
            if (move.MovePieceType == PieceType.King)
            {
                int oldK = from ^ (color * 56), newK = to ^ (color * 56);
                refreshMover = KING_BUCKET[oldK] != KING_BUCKET[newK] || FlipOf(oldK) != FlipOf(newK);
            }

            for (int p = 0; p <= 1; p++)
            {
                short* src = p == 0 ? src0 : src1;
                short* dst = p == 0 ? dst0 : dst1;

                if (refreshMover && p == color)
                {
                    Refresh(board, p, dst);
                    continue;
                }

                int ksq = RelKingSq(board, p);
                int bucket = KING_BUCKET[ksq];
                int flip = FlipOf(ksq);

                int sub = Feature(p, bucket, flip, color, pc, from);
                int add = Feature(p, bucket, flip, color, destPc, to);

                if (move.IsCastles)
                {
                    bool kingside = to > from;
                    int rookFrom = kingside ? to + 1 : to - 2;
                    int rookTo = kingside ? to - 1 : to + 1;
                    int rpc = (int)PieceType.Rook - 1;
                    AddAddSubSub(dst, src,
                        l0w + add, l0w + Feature(p, bucket, flip, color, rpc, rookTo),
                        l0w + sub, l0w + Feature(p, bucket, flip, color, rpc, rookFrom));
                }
                else if (move.IsCapture)
                {
                    int capSq = move.IsEnPassant ? to + (isWhite ? -8 : 8) : to;
                    int capPc = (int)move.CapturePieceType - 1;
                    AddSubSub(dst, src, l0w + add, l0w + sub, l0w + Feature(p, bucket, flip, 1 - color, capPc, capSq));
                }
                else
                {
                    AddSub(dst, src, l0w + add, l0w + sub);
                }
            }
        }

        internal static void undoMove()
        {
            currPly--;
        }

        internal static bool VerifyAccumulators(Board board)
        {
            short* tmp = stackalloc short[HL];
            for (int p = 0; p <= 1; p++)
            {
                int ksq = RelKingSq(board, p);
                int bucket = KING_BUCKET[ksq];
                int flip = FlipOf(ksq);

                for (int h = 0; h < HL; h++) tmp[h] = l0b[h];
                for (int color = 0; color <= 1; color++)
                {
                    for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
                    {
                        int pc = (int)type - 1;
                        ulong bb = board.GetPieceBitboard(type, color == 0);
                        while (bb != 0)
                        {
                            int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref bb);
                            short* wf = l0w + Feature(p, bucket, flip, color, pc, sq);
                            for (int h = 0; h < HL; h++) tmp[h] += wf[h];
                        }
                    }
                }

                short* a = Acc(currPly, p);
                for (int h = 0; h < HL; h++)
                {
                    if (a[h] != tmp[h]) return false;
                }
            }
            return true;
        }

        static void Copy(short* dst, short* src)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(dst + i, Avx.LoadAlignedVector256(src + i));
            }
        }

        static void AddInPlace(short* acc, short* a)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(acc + i, Avx2.Add(Avx.LoadAlignedVector256(acc + i), Avx.LoadAlignedVector256(a + i)));
            }
        }

        static void SubInPlace(short* acc, short* s)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(acc + i, Avx2.Subtract(Avx.LoadAlignedVector256(acc + i), Avx.LoadAlignedVector256(s + i)));
            }
        }

        // dst = src + a - s   (one pass: read src once, write dst once)
        static void AddSub(short* dst, short* src, short* a, short* s)
        {
            for (int i = 0; i < HL; i += 16)
            {
                var v = Avx.LoadAlignedVector256(src + i);
                v = Avx2.Add(v, Avx.LoadAlignedVector256(a + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s + i));
                Avx.StoreAligned(dst + i, v);
            }
        }

        static void AddSubSub(short* dst, short* src, short* a, short* s1, short* s2)
        {
            for (int i = 0; i < HL; i += 16)
            {
                var v = Avx.LoadAlignedVector256(src + i);
                v = Avx2.Add(v, Avx.LoadAlignedVector256(a + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s1 + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s2 + i));
                Avx.StoreAligned(dst + i, v);
            }
        }

        static void AddAddSubSub(short* dst, short* src, short* a1, short* a2, short* s1, short* s2)
        {
            for (int i = 0; i < HL; i += 16)
            {
                var v = Avx.LoadAlignedVector256(src + i);
                v = Avx2.Add(v, Avx.LoadAlignedVector256(a1 + i));
                v = Avx2.Add(v, Avx.LoadAlignedVector256(a2 + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s1 + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s2 + i));
                Avx.StoreAligned(dst + i, v);
            }
        }
    }
}
