

namespace FispurEngine.Fispur0_3_0
{
    using FispurEngine.API;
    using System;
    using System.IO;
    using System.Reflection;

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

        static NNUE()
        {
            Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("net0.3.0.bin");

            using var r = new BinaryReader(s);
            for (int i = 0; i < l0w.Length; i++) l0w[i] = r.ReadInt16();
            for (int i = 0; i < l0b.Length; i++) l0b[i] = r.ReadInt16();
            for (int i = 0; i < l1w.Length; i++) l1w[i] = r.ReadInt16();
            l1b = r.ReadInt16();
        }

        static int SCReLU(int x)
        {
            int c = x < 0 ? 0 : x > QA ? QA : x;
            return c * c;
        }

        public static int Evaluate(Board board)
        {
            Span<int> accW = stackalloc int[HL];
            Span<int> accB = stackalloc int[HL];
            for (int h = 0; h < HL; h++) { accW[h] = l0b[h]; accB[h] = l0b[h]; }

            for (int colour = 0; colour <= 1; colour++)
            {
                bool white = colour == 0;
                for (PieceType type = PieceType.Pawn; type <= PieceType.King; type++)
                {
                    int pc = (int)type - 1;                
                    ulong bb = board.GetPieceBitboard(type, white);
                    while (bb != 0)
                    {
                        int sq = BitboardHelper.ClearAndGetIndexOfLSB(ref bb);   
                        int wBase = (384 * colour + 64 * pc + sq) * HL;
                        int bBase = (384 * (1 - colour) + 64 * pc + (sq ^ 56)) * HL;
                        for (int h = 0; h < HL; h++)
                        {
                            accW[h] += l0w[wBase + h];
                            accB[h] += l0w[bBase + h];
                        }
                    }
                }
            }

            Span<int> boys = board.IsWhiteToMove ? accW : accB;
            Span<int> opps = board.IsWhiteToMove ? accB : accW;

            long sum = 0;
            for (int h = 0; h < HL; h++)
            {
                sum += (long)SCReLU(boys[h]) * l1w[h];
                sum += (long)SCReLU(opps[h]) * l1w[HL + h];
            }

            return (int)((sum / QA + l1b) * SCALE / QAB);
        }
    }
}