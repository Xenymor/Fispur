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
        const int HL = 512;
        const int QA = 255;
        const int QB = 64;
        const int QAB = QA * QB;
        const int SCALE = 400;

        // All weights and accumulators live in 32-byte aligned native memory:
        // no bounds checks, no pinning via `fixed`, aligned AVX2 loads/stores.
        static readonly short* l0w;
        static readonly short* l0b;
        static readonly short* l1w;
        static readonly short l1b;

        // Accumulator stack, one entry per ply: [ply][perspective (0 = white, 1 = black)][HL]
        static readonly short* acc;
        const int PLY_STRIDE = 2 * HL;

        static int currPly = 0;

        static NNUE()
        {
            l0w = (short*)NativeMemory.AlignedAlloc((nuint)(INPUT * HL * sizeof(short)), 64);
            l0b = (short*)NativeMemory.AlignedAlloc((nuint)(HL * sizeof(short)), 64);
            l1w = (short*)NativeMemory.AlignedAlloc((nuint)(2 * HL * sizeof(short)), 64);
            acc = (short*)NativeMemory.AlignedAlloc((nuint)(Fispur.MAX_DEPTH * PLY_STRIDE * sizeof(short)), 64);

            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("net0.12.0.bin")!;
            using var r = new BinaryReader(s);
            for (int i = 0; i < INPUT * HL; i++)
            {
                l0w[i] = r.ReadInt16();
            }

            for (int i = 0; i < HL; i++)
            {
                l0b[i] = r.ReadInt16();
            }

            for (int i = 0; i < 2 * HL; i++)
            {
                l1w[i] = r.ReadInt16();
            }

            l1b = r.ReadInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static short* Acc(int ply, int perspective)
        {
            return acc + ply * PLY_STRIDE + perspective * HL;
        }

        public static int Evaluate(Board board)
        {
            short* boys = Acc(currPly, board.IsWhiteToMove ? 0 : 1);
            short* opps = Acc(currPly, board.IsWhiteToMove ? 1 : 0);
            short* w = l1w;

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
            return (int)((total / QA + l1b) * SCALE / QAB);
        }

        public static void UpdateAccumulators(Board board)
        {
            currPly = 0;
            short* aw = Acc(0, 0);
            short* ab = Acc(0, 1);

            for (int h = 0; h < HL; h++) { aw[h] = l0b[h]; ab[h] = l0b[h]; }

            for (int color = 0; color <= 1; color++)
            {
                bool white = color == 0;
                for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
                {
                    int pc = (int)type - 1;
                    ulong bb = board.GetPieceBitboard(type, white);
                    while (bb != 0)
                    {
                        int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref bb);
                        short* wf = l0w + (384 * color + 64 * pc + sq) * HL;
                        short* bf = l0w + (384 * (1 - color) + 64 * pc + (sq ^ 56)) * HL;
                        for (int h = 0; h < HL; h++)
                        {
                            aw[h] += wf[h];
                            ab[h] += bf[h];
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FeatureW(int color, int pc, int sq)
        {
            return (384 * color + 64 * pc + sq) * HL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FeatureB(int color, int pc, int sq)
        {
            return (384 * (1 - color) + 64 * pc + (sq ^ 56)) * HL;
        }

        internal static void makeMove(Move move, bool isWhite)
        {
            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;
            int from = move.StartSquare.Index, to = move.TargetSquare.Index;

            short* srcW = Acc(currPly, 0), srcB = Acc(currPly, 1);
            currPly++;
            short* dstW = Acc(currPly, 0), dstB = Acc(currPly, 1);

            int subW = FeatureW(color, pc, from), subB = FeatureB(color, pc, from);
            int addW = FeatureW(color, destPc, to), addB = FeatureB(color, destPc, to);

            if (move.IsCastles)
            {
                bool kingside = to > from;
                int rookFrom = kingside ? to + 1 : to - 2;
                int rookTo = kingside ? to - 1 : to + 1;
                int rpc = (int)PieceType.Rook - 1;
                AddAddSubSub(dstW, srcW, l0w + addW, l0w + FeatureW(color, rpc, rookTo), l0w + subW, l0w + FeatureW(color, rpc, rookFrom));
                AddAddSubSub(dstB, srcB, l0w + addB, l0w + FeatureB(color, rpc, rookTo), l0w + subB, l0w + FeatureB(color, rpc, rookFrom));
            }
            else if (move.IsCapture)
            {
                int capSq = move.IsEnPassant ? to + (isWhite ? -8 : 8) : to;
                int capPc = (int)move.CapturePieceType - 1;
                AddSubSub(dstW, srcW, l0w + addW, l0w + subW, l0w + FeatureW(1 - color, capPc, capSq));
                AddSubSub(dstB, srcB, l0w + addB, l0w + subB, l0w + FeatureB(1 - color, capPc, capSq));
            }
            else
            {
                AddSub(dstW, srcW, l0w + addW, l0w + subW);
                AddSub(dstB, srcB, l0w + addB, l0w + subB);
            }
        }

        internal static void undoMove()
        {
            currPly--;
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
