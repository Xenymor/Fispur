using FispurEngine.API;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FispurEngine
{
    public static class NNUE
    {
        const int INPUT = 768;
        const int HL = 128;              
        const int QA = 255;
        const int QB = 64;
        const int QAB = QA * QB;          
        const int SCALE = 400;

        const int MAX_PLY = 256;

        static readonly short[] l0w = new short[INPUT * HL];  
        static readonly short[] l0b = new short[HL];
        static readonly short[] l1w = new short[2 * HL];
        static readonly short l1b;

        static short[][] accWhite;
        static short[][] accBlack;

        static int currPly = 0;

        static NNUE()
        {
            Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("net0.3.0.bin");

            using var r = new BinaryReader(s);
            for (int i = 0; i < l0w.Length; i++) l0w[i] = r.ReadInt16();
            for (int i = 0; i < l0b.Length; i++) l0b[i] = r.ReadInt16();
            for (int i = 0; i < l1w.Length; i++) l1w[i] = r.ReadInt16();
            l1b = r.ReadInt16();

            accWhite = new short[MAX_PLY][];
            accBlack = new short[MAX_PLY][];

            for (int i = 0; i < MAX_PLY; i++)
            {
                accWhite[i] = new short[HL];
                accBlack[i] = new short[HL];
            }
        }

        static int SCReLU(int x)
        {
            int c = x < 0 ? 0 : x > QA ? QA : x;
            return c * c;
        }


        public static unsafe int Evaluate(Board board)
        {
            short[] boysArr = board.IsWhiteToMove ? accWhite[currPly] : accBlack[currPly];
            short[] oppsArr = board.IsWhiteToMove ? accBlack[currPly] : accWhite[currPly];

            fixed (short* boys = boysArr, opps = oppsArr, w = l1w)
            {
                Vector256<short> zero = Vector256<short>.Zero;
                Vector256<short> qa = Vector256.Create((short)QA);
                Vector256<int> sum = Vector256<int>.Zero;

                for (int h = 0; h < HL; h += 16)
                {
                    Vector256<short> v = Avx2.Min(Avx2.Max(Avx.LoadVector256(boys + h), zero), qa);
                    sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(v, Avx2.MultiplyLow(v, Avx.LoadVector256(w + h))));

                    Vector256<short> o = Avx2.Min(Avx2.Max(Avx.LoadVector256(opps + h), zero), qa);
                    sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(o, Avx2.MultiplyLow(o, Avx.LoadVector256(w + HL + h))));
                }

                Vector128<int> s = Sse2.Add(sum.GetLower(), sum.GetUpper());
                s = Ssse3.HorizontalAdd(s, s);
                s = Ssse3.HorizontalAdd(s, s);

                long total = s.ToScalar();
                return (int)((total / QA + l1b) * SCALE / QAB);
            }
        }

        public static void UpdateAccumulators(Board board)
        {
            currPly = 0;
            short[] currAccWhite = accWhite[currPly];
            short[] currAccBlack = accBlack[currPly];

            for (int h = 0; h < HL; h++) { currAccWhite[h] = l0b[h]; currAccBlack[h] = l0b[h]; }

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
                        int wBase = (384 * color + 64 * pc + sq) * HL;
                        int bBase = (384 * (1 - color) + 64 * pc + (sq ^ 56)) * HL;
                        for (int h = 0; h < HL; h++)
                        {
                            currAccWhite[h] += l0w[wBase + h];
                            currAccBlack[h] += l0w[bBase + h];
                        }
                    }
                }
            }
        }

        internal static void makeMove(Move move, bool isWhite)
        {
            accWhite[currPly].CopyTo(accWhite[currPly + 1], 0);
            accBlack[currPly].CopyTo(accBlack[currPly + 1], 0);
            currPly++;

            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;

            getIndexes(color, pc, move.StartSquare.Index, out int wSrc, out int bSrc);
            getIndexes(color, destPc, move.TargetSquare.Index, out int wDest, out int bDest);

            removePiece(wSrc, bSrc);
            addPiece(wDest, bDest);

            if (move.IsEnPassant)
            {
                int capSq = move.TargetSquare.Index + (isWhite ? -8 : 8);
                getIndexes(1 - color, (int)PieceType.Pawn - 1, capSq, out int wCap, out int bCap);
                removePiece(wCap, bCap);
            }
            else if (move.IsCapture)
            {
                getIndexes(1 - color, (int)move.CapturePieceType - 1, move.TargetSquare.Index, out int wCap, out int bCap);
                removePiece(wCap, bCap);
            }

            if (move.IsCastles)
            {
                bool kingside = move.TargetSquare.Index > move.StartSquare.Index;
                int rookFrom = kingside ? move.TargetSquare.Index + 1 : move.TargetSquare.Index - 2;
                int rookTo = kingside ? move.TargetSquare.Index - 1 : move.TargetSquare.Index + 1;
                int rpc = (int)PieceType.Rook - 1;

                getIndexes(color, rpc, rookFrom, out int wRookFrom, out int bRookFrom);
                getIndexes(color, rpc, rookTo, out int wRookTo, out int bRookTo);
                removePiece(wRookFrom, bRookFrom);
                addPiece(wRookTo, bRookTo);
            }
        }

        internal static void undoMove()
        {
            currPly--;
        }

        private static void getIndexes(int color, int pc, int sq, out int wPos, out int bPos)
        {
            wPos = (384 * color + 64 * pc + sq) * HL;
            bPos = (384 * (1 - color) + 64 * pc + (sq ^ 56)) * HL;
        }

        private static unsafe void addPiece(int wPos, int bPos)
        {
            fixed (short* aw = accWhite[currPly], ab = accBlack[currPly], w = l0w)
            {
                AddInPlaceAvx2(aw, &w[wPos], HL);
                AddInPlaceAvx2(ab, &w[bPos], HL);
            }
        }

        private static unsafe void removePiece(int wPos, int bPos)
        {
            fixed (short* aw = accWhite[currPly], ab = accBlack[currPly], w = l0w)
            {
                SubtractInPlaceAvx2(aw, &w[wPos], HL);
                SubtractInPlaceAvx2(ab, &w[bPos], HL);
            }
        }

        static unsafe void SubtractInPlaceAvx2(short* dst, short* src, int length)
        {
            int i = 0;
            for (; i <= length - 16; i += 16)
            {
                var a = Avx.LoadVector256(dst + i);
                var b = Avx.LoadVector256(src + i);
                Avx2.Store(dst + i, Avx2.Subtract(a, b));
            }
        }

        static unsafe void AddInPlaceAvx2(short* dst, short* src, int length)
        {
            int i = 0;
            for (; i <= length - 16; i += 16)
            {
                var a = Avx.LoadVector256(dst + i);
                var b = Avx.LoadVector256(src + i);
                Avx2.Store(dst + i, Avx2.Add(a, b));
            }
        }
    }
}