using FispurEngine.API;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FispurEngine
{
    public unsafe partial class NNUE
    {
        // The net: read once per process by the static constructor and shared by every instance
        static readonly int INPUT;
        static readonly int HL;
        static readonly int L1;
        static readonly int L2;
        private static readonly int OUTPUT_BUCKETS;
        const int QA = 255;
        const int QB = 64;
        const int QAB = QA * QB;
        const int SCALE = 400;

        static readonly short* ftW;
        static readonly short* ftB;

        static readonly int PLY_STRIDE;
        static readonly bool PAIRWISE;

        static readonly int KING_BUCKETS;
        static readonly int[] KING_BUCKET = new int[64];

        static readonly int FINNY_ENTRIES;
        private const string FILE_NAME = "net.bin";
        // pairwise + input format, king buckets, HL / 128, L1, L2, output buckets
        const int HEADER_BYTES = 6;

        // Search state: one per instance, freed by the finalizer
        // Accumulator stack, one entry per ply: [ply][perspective (0 = white, 1 = black)][HL]
        readonly short* acc;
        int currPly = 0;

        readonly short* finnyAcc;
        readonly ulong[] finnyBB;

        static NNUE()
        {
            if (!Avx2.IsSupported)
            {
                throw new Exception("Avx2 is not supported on your device");
            }

            var kingBucketLayouts = new Dictionary<int, int[]>();

            kingBucketLayouts[1] =
                [
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                ];

            kingBucketLayouts[10] =
                [
                    0, 1, 2, 3,
                    4, 4, 5, 5,
                    6, 6, 6, 6,
                    7, 7, 7, 7,
                    8, 8, 8, 8,
                    8, 8, 8, 8,
                    9, 9, 9, 9,
                    9, 9, 9, 9,
                ];

            kingBucketLayouts[16] =
                [
                    0, 1, 2, 3,
                    4, 5, 6, 7,
                    8, 8, 9, 9,
                   10,10,11,11,
                   12,12,12,12,
                   13,13,13,13,
                   14,14,14,14,
                   15,15,15,15,
                ];

            kingBucketLayouts[32] = 
                [
                    0, 1, 2, 3,
                    4, 5, 6, 7,
                    8, 9,10,11,
                   12,13,14,15,
                   16,17,18,19,
                   20,21,22,23,
                   24,25,26,27,
                   28,29,30,31,
                ];

            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(FILE_NAME)!;
            using var r = new BinaryReader(s);

            byte pairwiseInput = r.ReadByte();
            PAIRWISE = (pairwiseInput >> 4) switch
            {
                0 => false,
                1 => true,
                _ => throw new Exception("Invalid Pairwise marker in NNUE-file")
            };
            INPUT = (pairwiseInput & 15) switch
            {
                0 => 768,
                _ => throw new Exception("Unknown input format in NNUE-file")
            };

            byte kingBuckets = r.ReadByte();
            if (!kingBucketLayouts.TryGetValue(kingBuckets, out int[]? layout))
            {
                throw new Exception("Unknown King-Bucket count in NNUE-file");
            }

            KING_BUCKETS = kingBuckets;
            FINNY_ENTRIES = 2 * KING_BUCKETS * 2;

            byte ftSize = r.ReadByte();
            HL = ftSize * 128;
            PLY_STRIDE = 2 * HL;

            L1 = r.ReadByte();
            L2 = r.ReadByte();

            OUTPUT_BUCKETS = r.ReadByte();

            int maxBucket = 0;
            foreach (int b in layout)
            {
                maxBucket = Math.Max(maxBucket, b);
            }

            if (layout.Length != 32 || maxBucket + 1 != KING_BUCKETS)
            {
                throw new InvalidOperationException("King-bucket layout does not match KING_BUCKETS");
            }

            for (int sq = 0; sq < 64; sq++)
            {
                int file = sq & 7;
                int mirroredFile = file > 3 ? 7 - file : file;
                KING_BUCKET[sq] = layout[(sq >> 3) * 4 + mirroredFile];
            }

            ftW = (short*)NativeMemory.AlignedAlloc((nuint)(KING_BUCKETS * INPUT * HL       * sizeof(short)), 64);
            ftB = (short*)NativeMemory.AlignedAlloc((nuint)(HL                              * sizeof(short)), 64);

            long expected = HEADER_BYTES + ((long)KING_BUCKETS * INPUT * HL + HL) * sizeof(short) + tailBytes();
            if (s.Length < expected || s.Length - expected >= 64)
            {
                throw new InvalidDataException($"{FILE_NAME}: {s.Length} bytes, expected {expected} (+ <64 padding)");
            }

            for (int i = 0; i < KING_BUCKETS * INPUT * HL; i++)
            {
                ftW[i] = r.ReadInt16();
            }

            for (int i = 0; i < HL; i++)
            {
                ftB[i] = r.ReadInt16();
            }

            loadHead(r);

            // The calibration only needs an instance for its accumulators
            NNUE calibrator = new NNUE();
            calibration = calibrator.calibrate();
            GC.KeepAlive(calibrator);
            evalScale = calibration.positions > 0
                ? Math.Clamp((int)Math.Round(calibration.fittedScale * EVAL_SCALE_ONE), MIN_EVAL_SCALE, MAX_EVAL_SCALE)
                : EVAL_SCALE_ONE;
        }

        public NNUE()
        {
            acc = (short*)NativeMemory.AlignedAlloc((nuint)(Fispur.MAX_DEPTH * PLY_STRIDE   * sizeof(short)), 64);
            finnyAcc = (short*)NativeMemory.AlignedAlloc((nuint)(FINNY_ENTRIES * HL         * sizeof(short)), 64);
            finnyBB = new ulong[FINNY_ENTRIES * 12];

            for (int i = 0; i < FINNY_ENTRIES; i++)
            {
                copy(finnyAcc + i * HL, ftB);
            }

            if (L1 > 0)
            {
                int inputs = tailInputs();
                act = (byte*)NativeMemory.AlignedAlloc((nuint)inputs, 64);
                nnz = (ushort*)NativeMemory.AlignedAlloc((nuint)((inputs / 4 + 8)           * sizeof(ushort)), 64);
                h1 = (float*)NativeMemory.AlignedAlloc((nuint)(L1                           * sizeof(float)), 64);
                h2 = (float*)NativeMemory.AlignedAlloc((nuint)(Math.Max(L2, 8)              * sizeof(float)), 64);
            }
        }

        // The buffers are native memory, the GC does not know them. AlignedFree(null) does nothing.
        ~NNUE()
        {
            NativeMemory.AlignedFree(acc);
            NativeMemory.AlignedFree(finnyAcc);
            NativeMemory.AlignedFree(act);
            NativeMemory.AlignedFree(nnz);
            NativeMemory.AlignedFree(h1);
            NativeMemory.AlignedFree(h2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        short* getAcc(int ply, int perspective)
        {
            return acc + ply * PLY_STRIDE + perspective * HL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int relKingSq(Board board, int perspective)
        {
            return board.GetKingSquare(perspective == 0).Index ^ (perspective * 56);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int flipOf(int relKingSq)
        {
            return (relKingSq & 7) > 3 ? 7 : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int feature(int perspective, int bucket, int flip, int color, int pc, int sq)
        {
            int relColor = color ^ perspective;
            int relSq = sq ^ (perspective * 56) ^ flip;
            return (bucket * INPUT + 384 * relColor + 64 * pc + relSq) * HL;
        }

        public int evaluate(Board board)
        {
            long scaled = output(board) * SCALE * evalScale / ((long)QAB * EVAL_SCALE_ONE);
            return (int)Math.Clamp(scaled, -MAX_EVAL, MAX_EVAL);
        }

        // Net output of the side to move in units of 1 / QAB, so that evaluate() scales every kind of tail the same way
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        long output(Board board)
        {
            short* boys = getAcc(currPly, board.IsWhiteToMove ? 0 : 1);
            short* opps = getAcc(currPly, board.IsWhiteToMove ? 1 : 0);
            return outputHead(boys, opps, outputBucket(board));
        }

        #region tail

        static short* owQ;
        static short* obQ;

        static sbyte* l1w;     // [bucket][block of 4 inputs][output][4]
        static float* l1b;     // [bucket][output]
        static float* l2w;     // [bucket][input][output]
        static float* l2b;     // [bucket][output]
        static float* ow;      // [bucket][input]
        static float* ob;      // [bucket]

        static int bucketDivisor;
        readonly byte* act;              // L1 inputs of the current position: ft outputs as 0..127
        readonly ushort* nnz;            // indices of the 4-byte blocks of act that are not zero
        static ushort* nnzTable;         // for every 8-bit mask the positions of its set bits
        readonly float* h1;              // L1 outputs
        readonly float* h2;              // L2 outputs

        static int tailInputs()
        {
            return PAIRWISE ? HL : 2 * HL;
        }

        // Size of the tail
        static long tailBytes()
        {
            long buckets = OUTPUT_BUCKETS;
            if (L1 == 0)
            {
                return (buckets * tailInputs() + buckets) * sizeof(short);
            }

            long last = L2 > 0 ? L2 : L1;
            long floats = buckets * L1 + (L2 > 0 ? buckets * L2 * L1 + buckets * L2 : 0) + buckets * last + buckets;
            return buckets * L1 * tailInputs() + floats * sizeof(float);
        }

        static int packedOffset(int i)
        {
            return i < 8 || i >= 24 ? i : i < 16 ? i + 8 : i - 8;
        }

        static void loadHead(BinaryReader r)
        {
            if (L1 == 0 && L2 > 0)
            {
                throw new Exception("L2 exists without L1 in NNUE-file");
            }

            if (L1 % 8 != 0 || L2 % 8 != 0)
            {
                throw new Exception("L1 and L2 have to be multiples of 8");
            }

            int inputs = tailInputs();
            bucketDivisor = (32 + OUTPUT_BUCKETS - 1) / OUTPUT_BUCKETS;

            if (L1 == 0)
            {
                owQ = (short*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * inputs     * sizeof(short)), 64);
                obQ = (short*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS              * sizeof(short)), 64);

                for (int i = 0; i < OUTPUT_BUCKETS * inputs; i++)
                {
                    owQ[i] = r.ReadInt16();
                }

                for (int i = 0; i < OUTPUT_BUCKETS; i++)
                {
                    obQ[i] = r.ReadInt16();
                }

                return;
            }

            int blocks = inputs / 4;
            l1w = (sbyte*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * inputs * L1), 64);
            for (int b = 0; b < OUTPUT_BUCKETS; b++)
            {
                for (int o = 0; o < L1; o++)
                {
                    for (int i = 0; i < inputs; i++)
                    {
                        int pos = (i & ~31) + packedOffset(i & 31);
                        l1w[((b * blocks + pos / 4) * L1 + o) * 4 + pos % 4] = r.ReadSByte();
                    }
                }
            }

            l1b = (float*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * L1             * sizeof(float)), 64);
            for (int i = 0; i < OUTPUT_BUCKETS * L1; i++)
            {
                l1b[i] = r.ReadSingle();
            }

            if (L2 > 0)
            {
                // [bucket][input][output], so that the outputs of one input can be loaded as vectors
                l2w = (float*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * L1 * L2    * sizeof(float)), 64);
                for (int b = 0; b < OUTPUT_BUCKETS; b++)
                {
                    for (int o = 0; o < L2; o++)
                    {
                        for (int i = 0; i < L1; i++)
                        {
                            l2w[(b * L1 + i) * L2 + o] = r.ReadSingle();
                        }
                    }
                }

                l2b = (float*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * L2         * sizeof(float)), 64);
                for (int i = 0; i < OUTPUT_BUCKETS * L2; i++)
                {
                    l2b[i] = r.ReadSingle();
                }
            }

            int last = L2 > 0 ? L2 : L1;
            ow = (float*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS * last            * sizeof(float)), 64);
            for (int i = 0; i < OUTPUT_BUCKETS * last; i++)
            {
                ow[i] = r.ReadSingle();
            }

            ob = (float*)NativeMemory.AlignedAlloc((nuint)(OUTPUT_BUCKETS                   * sizeof(float)), 64);
            for (int i = 0; i < OUTPUT_BUCKETS; i++)
            {
                ob[i] = r.ReadSingle();
            }

            nnzTable = (ushort*)NativeMemory.AlignedAlloc(256 * 8                           * sizeof(ushort), 64);
            for (int mask = 0; mask < 256; mask++)
            {
                int k = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                    {
                        nnzTable[mask * 8 + k++] = (ushort)bit;
                    }
                }

                while (k < 8)
                {
                    nnzTable[mask * 8 + k++] = 0;
                }
            }
        }

        long outputHead(short* boys, short* opps, int bucket)
        {
            if (L1 > 0)
            {
                return outputLayers(boys, opps, bucket);
            }

            return PAIRWISE ? outputPairwise(boys, opps, bucket) : outputScrelu(boys, opps, bucket);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int sum(Vector256<int> v)
        {
            Vector128<int> s = Sse2.Add(v.GetLower(), v.GetUpper());
            s = Ssse3.HorizontalAdd(s, s);
            s = Ssse3.HorizontalAdd(s, s);
            return s.ToScalar();
        }

        // No small layers, SCReLU: sum of v * v * w over both sides
        long outputScrelu(short* boys, short* opps, int bucket)
        {
            short* w = owQ + bucket * 2 * HL;

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

            long total = sum(Avx2.Add(sum0, sum1));
            return total / QA + obQ[bucket];
        }

        // No small layers, pairwise: sum of a * b * w
        long outputPairwise(short* boys, short* opps, int bucket)
        {
            int half = HL / 2;
            short* w = owQ + bucket * HL;

            Vector256<short> zero = Vector256<short>.Zero;
            Vector256<short> qa = Vector256.Create((short)QA);
            Vector256<int> sum0 = Vector256<int>.Zero;
            Vector256<int> sum1 = Vector256<int>.Zero;

            for (int h = 0; h < half; h += 16)
            {
                Vector256<short> a = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(boys + h), zero), qa);
                Vector256<short> b = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(boys + half + h), zero), qa);
                sum0 = Avx2.Add(sum0, Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(a, Avx.LoadAlignedVector256(w + h)), b));

                Vector256<short> c = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(opps + h), zero), qa);
                Vector256<short> d = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(opps + half + h), zero), qa);
                sum1 = Avx2.Add(sum1, Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(c, Avx.LoadAlignedVector256(w + half + h)), d));
            }

            long total = sum(Avx2.Add(sum0, sum1));
            return total / QA + obQ[bucket];
        }

        void activate(short* boys, short* opps)
        {
            Vector256<short> zero = Vector256<short>.Zero;
            Vector256<short> qa = Vector256.Create((short)QA);

            if (PAIRWISE)
            {
                int half = HL / 2;
                for (int p = 0; p < 2; p++)
                {
                    short* a = p == 0 ? boys : opps;
                    byte* dst = act + p * half;
                    for (int h = 0; h < half; h += 32)
                    {
                        Vector256<short> a0 = Avx2.ShiftLeftLogical(Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(a + h), zero), qa), 7);
                        Vector256<short> a1 = Avx2.ShiftLeftLogical(Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(a + h + 16), zero), qa), 7);
                        Vector256<short> b0 = Avx2.Min(Avx.LoadAlignedVector256(a + half + h), qa);
                        Vector256<short> b1 = Avx2.Min(Avx.LoadAlignedVector256(a + half + h + 16), qa);
                        Avx.StoreAligned(dst + h, Avx2.PackUnsignedSaturate(Avx2.MultiplyHigh(a0, b0), Avx2.MultiplyHigh(a1, b1)));
                    }
                }
            }
            else
            {
                for (int p = 0; p < 2; p++)
                {
                    short* a = p == 0 ? boys : opps;
                    byte* dst = act + p * HL;
                    for (int h = 0; h < HL; h += 32)
                    {
                        Vector256<short> v0 = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(a + h), zero), qa);
                        Vector256<short> v1 = Avx2.Min(Avx2.Max(Avx.LoadAlignedVector256(a + h + 16), zero), qa);
                        Avx.StoreAligned(dst + h, Avx2.PackUnsignedSaturate(
                            Avx2.MultiplyHigh(Avx2.ShiftLeftLogical(v0, 7), v0), Avx2.MultiplyHigh(Avx2.ShiftLeftLogical(v1, 7), v1)));
                    }
                }
            }
        }

        // Collects the indices of the 4-byte blocks of act that are not zero in nnz and returns how many there are
        int findNonZero()
        {
            int inputs = tailInputs();
            int count = 0;
            Vector128<ushort> first = Vector128<ushort>.Zero;
            Vector128<ushort> eight = Vector128.Create((ushort)8);

            for (int i = 0; i < inputs; i += 32)
            {
                Vector256<int> x = Avx.LoadAlignedVector256(act + i).AsInt32();
                int mask = Avx.MoveMask(Avx2.CompareGreaterThan(x, Vector256<int>.Zero).AsSingle());
                Sse2.Store(nnz + count, Sse2.Add(Sse2.LoadAlignedVector128(nnzTable + mask * 8), first));
                count += System.Numerics.BitOperations.PopCount((uint)mask);
                first = Sse2.Add(first, eight);
            }

            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector256<float> mulAdd(Vector256<float> a, Vector256<float> b, Vector256<float> c)
        {
            return Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : Avx.Add(Avx.Multiply(a, b), c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector256<float> screlu(Vector256<float> v)
        {
            v = Avx.Min(Avx.Max(v, Vector256<float>.Zero), Vector256.Create(1f));
            return Avx.Multiply(v, v);
        }

        // 8 outputs of L1 from their integer sums: back to float, bias, SCReLU
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void finishL1(int output, int bucket, Vector256<int> sums)
        {
            // an activation byte u stands for u * 512 / (QA * QA), a weight for w / QB
            Vector256<float> scale = Vector256.Create(512f / (QA * QA * QB));
            Avx.StoreAligned(h1 + output, screlu(mulAdd(Avx.ConvertToVector256Single(sums), scale, Avx.LoadAlignedVector256(l1b + bucket * L1 + output))));
        }

        // With small layers: L1 in integers over the blocks that are not zero, everything behind it in float
        long outputLayers(short* boys, short* opps, int bucket)
        {
            activate(boys, opps);
            int count = findNonZero();

            int blockBytes = 4 * L1;
            sbyte* w = l1w + (long)bucket * tailInputs() * L1;
            Vector256<short> ones = Vector256.Create((short)1);

            // 16 outputs at a time, so that the input block is only spread out once for two weight vectors
            int o = 0;
            for (; o + 16 <= L1; o += 16)
            {
                Vector256<int> s0 = Vector256<int>.Zero;
                Vector256<int> s1 = Vector256<int>.Zero;
                sbyte* wo = w + 4 * o;
                for (int k = 0; k < count; k++)
                {
                    int block = nnz[k];
                    Vector256<byte> x = Vector256.Create(*(int*)(act + 4 * block)).AsByte();
                    sbyte* wb = wo + block * blockBytes;
                    // u8 * i8 summed in pairs (at most 2 * 127 * 127, no saturation), then in fours: one int per output
                    s0 = Avx2.Add(s0, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, Avx.LoadAlignedVector256(wb)), ones));
                    s1 = Avx2.Add(s1, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, Avx.LoadAlignedVector256(wb + 32)), ones));
                }

                finishL1(o, bucket, s0);
                finishL1(o + 8, bucket, s1);
            }

            if (o < L1)
            {
                Vector256<int> s0 = Vector256<int>.Zero;
                sbyte* wo = w + 4 * o;
                for (int k = 0; k < count; k++)
                {
                    int block = nnz[k];
                    Vector256<byte> x = Vector256.Create(*(int*)(act + 4 * block)).AsByte();
                    s0 = Avx2.Add(s0, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, Avx.LoadAlignedVector256(wo + block * blockBytes)), ones));
                }

                finishL1(o, bucket, s0);
            }

            float* last = h1;
            int width = L1;
            if (L2 > 0)
            {
                float* w2 = l2w + bucket * L1 * L2;
                float* b2 = l2b + bucket * L2;
                for (int v = 0; v < L2; v += 8)
                {
                    Vector256<float> g = Avx.LoadAlignedVector256(b2 + v);
                    for (int k = 0; k < L1; k++)
                    {
                        g = mulAdd(Avx.BroadcastScalarToVector256(h1 + k), Avx.LoadAlignedVector256(w2 + k * L2 + v), g);
                    }

                    Avx.StoreAligned(h2 + v, screlu(g));
                }

                last = h2;
                width = L2;
            }

            float* wOut = ow + bucket * width;
            Vector256<float> r = Vector256<float>.Zero;
            for (int v = 0; v < width; v += 8)
            {
                r = mulAdd(Avx.LoadAlignedVector256(last + v), Avx.LoadAlignedVector256(wOut + v), r);
            }

            Vector128<float> q = Sse.Add(r.GetLower(), r.GetUpper());
            q = Sse3.HorizontalAdd(q, q);
            q = Sse3.HorizontalAdd(q, q);
            return (long)MathF.Round((q.ToScalar() + ob[bucket]) * QAB);
        }

        #endregion

        public void updateAccumulators(Board board)
        {
            currPly = 0;
            refresh(board, 0, getAcc(0, 0));
            refresh(board, 1, getAcc(0, 1));
        }

        void refresh(Board board, int perspective, short* dst)
        {
            int ksq = relKingSq(board, perspective);
            int bucket = KING_BUCKET[ksq];
            int flip = flipOf(ksq);

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
                        addInPlace(cached, ftW + feature(perspective, bucket, flip, color, pc, sq));
                    }
                    while (removed != 0)
                    {
                        int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref removed);
                        subInPlace(cached, ftW + feature(perspective, bucket, flip, color, pc, sq));
                    }

                    finnyBB[idx] = curr;
                }
            }

            copy(dst, cached);
        }

        internal void makeMove(Board board, Move move, bool isWhite)
        {
            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;
            int from = move.StartSquare.Index, to = move.TargetSquare.Index;

            short* src0 = getAcc(currPly, 0), src1 = getAcc(currPly, 1);
            currPly++;
            short* dst0 = getAcc(currPly, 0), dst1 = getAcc(currPly, 1);

            bool refreshMover = false;
            if (move.MovePieceType == PieceType.King)
            {
                int oldK = from ^ (color * 56), newK = to ^ (color * 56);
                refreshMover = KING_BUCKET[oldK] != KING_BUCKET[newK] || flipOf(oldK) != flipOf(newK);
            }

            for (int p = 0; p <= 1; p++)
            {
                short* src = p == 0 ? src0 : src1;
                short* dst = p == 0 ? dst0 : dst1;

                if (refreshMover && p == color)
                {
                    refresh(board, p, dst);
                    continue;
                }

                int ksq = relKingSq(board, p);
                int bucket = KING_BUCKET[ksq];
                int flip = flipOf(ksq);

                int sub = feature(p, bucket, flip, color, pc, from);
                int add = feature(p, bucket, flip, color, destPc, to);

                if (move.IsCastles)
                {
                    bool kingside = to > from;
                    int rookFrom = kingside ? to + 1 : to - 2;
                    int rookTo = kingside ? to - 1 : to + 1;
                    int rpc = (int)PieceType.Rook - 1;
                    addAddSubSub(dst, src,
                        ftW + add, ftW + feature(p, bucket, flip, color, rpc, rookTo),
                        ftW + sub, ftW + feature(p, bucket, flip, color, rpc, rookFrom));
                }
                else if (move.IsCapture)
                {
                    int capSq = move.IsEnPassant ? to + (isWhite ? -8 : 8) : to;
                    int capPc = (int)move.CapturePieceType - 1;
                    addSubSub(dst, src, ftW + add, ftW + sub, ftW + feature(p, bucket, flip, 1 - color, capPc, capSq));
                }
                else
                {
                    addSub(dst, src, ftW + add, ftW + sub);
                }
            }
        }

        internal void undoMove()
        {
            currPly--;
        }

        internal bool verifyAccumulators(Board board)
        {
            short* tmp = stackalloc short[HL];
            for (int p = 0; p <= 1; p++)
            {
                int ksq = relKingSq(board, p);
                int bucket = KING_BUCKET[ksq];
                int flip = flipOf(ksq);

                for (int h = 0; h < HL; h++) tmp[h] = ftB[h];
                for (int color = 0; color <= 1; color++)
                {
                    for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
                    {
                        int pc = (int)type - 1;
                        ulong bb = board.GetPieceBitboard(type, color == 0);
                        while (bb != 0)
                        {
                            int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref bb);
                            short* wf = ftW + feature(p, bucket, flip, color, pc, sq);
                            for (int h = 0; h < HL; h++) tmp[h] += wf[h];
                        }
                    }
                }

                short* a = getAcc(currPly, p);
                for (int h = 0; h < HL; h++)
                {
                    if (a[h] != tmp[h]) return false;
                }
            }
            return true;
        }

        void copy(short* dst, short* src)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(dst + i, Avx.LoadAlignedVector256(src + i));
            }
        }

        void addInPlace(short* acc, short* a)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(acc + i, Avx2.Add(Avx.LoadAlignedVector256(acc + i), Avx.LoadAlignedVector256(a + i)));
            }
        }

        void subInPlace(short* acc, short* s)
        {
            for (int i = 0; i < HL; i += 16)
            {
                Avx.StoreAligned(acc + i, Avx2.Subtract(Avx.LoadAlignedVector256(acc + i), Avx.LoadAlignedVector256(s + i)));
            }
        }

        // dst = src + a - s   (one pass: read src once, write dst once)
        void addSub(short* dst, short* src, short* a, short* s)
        {
            for (int i = 0; i < HL; i += 16)
            {
                var v = Avx.LoadAlignedVector256(src + i);
                v = Avx2.Add(v, Avx.LoadAlignedVector256(a + i));
                v = Avx2.Subtract(v, Avx.LoadAlignedVector256(s + i));
                Avx.StoreAligned(dst + i, v);
            }
        }

        void addSubSub(short* dst, short* src, short* a, short* s1, short* s2)
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

        void addAddSubSub(short* dst, short* src, short* a1, short* a2, short* s1, short* s2)
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
