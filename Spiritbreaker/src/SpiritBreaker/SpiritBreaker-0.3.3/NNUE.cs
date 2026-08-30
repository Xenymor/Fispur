

namespace Spiritbreaker.SpiritBreaker0_3_3
{
    using Spiritbreaker.API;
    using System;
    using System.ComponentModel;
    using System.Drawing;
    using System.IO;
    using System.Reflection;
    using System.Runtime.Intrinsics.X86;

    public static class NNUE
    {
        const int INPUT = 768;
        const int HL = 128;              
        const int QA = 255;
        const int QB = 64;
        const int QAB = QA * QB;          
        const int SCALE = 400;

        static readonly short[] l0w = new short[INPUT * HL];  
        static readonly short[] l0b = new short[HL];
        static readonly short[] l1w = new short[2 * HL];
        static readonly short l1b;

        static short[] accWhite;
        static short[] accBlack;

        static NNUE()
        {
            Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("net0.3.0.bin");

            using var r = new BinaryReader(s);
            for (int i = 0; i < l0w.Length; i++) l0w[i] = r.ReadInt16();
            for (int i = 0; i < l0b.Length; i++) l0b[i] = r.ReadInt16();
            for (int i = 0; i < l1w.Length; i++) l1w[i] = r.ReadInt16();
            l1b = r.ReadInt16();

            accWhite = new short[HL];
            accBlack = new short[HL];
        }

        static int SCReLU(int x)
        {
            int c = x < 0 ? 0 : x > QA ? QA : x;
            return c * c;
        }


        public static int Evaluate(Board board)
        {
            Span<short> boys = board.IsWhiteToMove ? accWhite : accBlack;
            Span<short> opps = board.IsWhiteToMove ? accBlack : accWhite;

            long sum = 0;
            for (int h = 0; h < HL; h++)
            {
                sum += (long)SCReLU(boys[h]) * l1w[h];
                sum += (long)SCReLU(opps[h]) * l1w[HL + h];
            }

            return (int)((sum / QA + l1b) * SCALE / QAB);
        }

        public static void UpdateAccumulators(Board board)
        {
            for (int h = 0; h < HL; h++) { accWhite[h] = l0b[h]; accBlack[h] = l0b[h]; }

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
                            accWhite[h] += l0w[wBase + h];
                            accBlack[h] += l0w[bBase + h];
                        }
                    }
                }
            }
        }

        internal static void makeMove(Move move, bool isWhite)
        {
            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;

            // Move the piece itself (start -> target), applying promotion via destPc.
            getIndexes(color, pc, move.StartSquare.Index, out int wSrc, out int bSrc);
            getIndexes(color, destPc, move.TargetSquare.Index, out int wDest, out int bDest);

            removePiece(wSrc, bSrc);
            addPiece(wDest, bDest);

            if (move.IsEnPassant)
            {
                // The captured pawn sits on the mover's start rank, on the target file.
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
                // King move is already applied above; also relocate the rook.
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

        internal static void undoMove(Move move, bool isWhite)
        {
            int color = isWhite ? 0 : 1;
            int pc = (int)move.MovePieceType - 1;
            int destPc = (int)(move.IsPromotion ? move.PromotionPieceType : move.MovePieceType) - 1;

            // Reverse the piece move (target -> start), applying promotion via destPc.
            getIndexes(color, pc, move.StartSquare.Index, out int wSrc, out int bSrc);
            getIndexes(color, destPc, move.TargetSquare.Index, out int wDest, out int bDest);
            addPiece(wSrc, bSrc);
            removePiece(wDest, bDest);

            if (move.IsEnPassant)
            {
                // Restore the captured pawn on the mover's start rank, target file.
                int capSq = move.TargetSquare.Index + (isWhite ? -8 : 8);
                getIndexes(1 - color, (int)PieceType.Pawn - 1, capSq, out int wCap, out int bCap);
                addPiece(wCap, bCap);
            }
            else if (move.IsCapture)
            {
                getIndexes(1 - color, (int)move.CapturePieceType - 1, move.TargetSquare.Index, out int wCap, out int bCap);
                addPiece(wCap, bCap);
            }

            if (move.IsCastles)
            {
                // King move is already reversed above; also move the rook back.
                bool kingside = move.TargetSquare.Index > move.StartSquare.Index;
                int rookFrom = kingside ? move.TargetSquare.Index + 1 : move.TargetSquare.Index - 2;
                int rookTo = kingside ? move.TargetSquare.Index - 1 : move.TargetSquare.Index + 1;
                int rpc = (int)PieceType.Rook - 1;
                getIndexes(color, rpc, rookFrom, out int wRookFrom, out int bRookFrom);
                getIndexes(color, rpc, rookTo, out int wRookTo, out int bRookTo);
                addPiece(wRookFrom, bRookFrom);
                removePiece(wRookTo, bRookTo);
            }
        }

        // Computes the flattened accumulator input offsets for a piece of the given
        // color/type on the given square, from both the white and black perspectives.
        private static void getIndexes(int color, int pc, int sq, out int wPos, out int bPos)
        {
            wPos = (384 * color + 64 * pc + sq) * HL;
            bPos = (384 * (1 - color) + 64 * pc + (sq ^ 56)) * HL;
        }

        private static unsafe void addPiece(int wPos, int bPos)
        {
            fixed (short* aw = accWhite, ab = accBlack, w = l0w)
            {
                AddInPlaceAvx2(&aw[0], &w[wPos], HL);
                AddInPlaceAvx2(&ab[0], &w[bPos], HL);
            }
        }

        private static unsafe void removePiece(int wPos, int bPos)
        {
            fixed (short* aw = accWhite, ab = accBlack, w = l0w)
            {
                SubtractInPlaceAvx2(&aw[0], &w[wPos], HL);
                SubtractInPlaceAvx2(&ab[0], &w[bPos], HL);
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
