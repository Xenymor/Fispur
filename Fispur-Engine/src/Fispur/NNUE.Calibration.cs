using FispurEngine.API;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace FispurEngine
{
    public static unsafe partial class NNUE
    {
        public const int EVAL_SCALE_ONE = 1024;
        const int MIN_EVAL_SCALE = EVAL_SCALE_ONE / 4;
        const int MAX_EVAL_SCALE = EVAL_SCALE_ONE * 4;

        const int MAX_EVAL = 32_000;

        const string CALIBRATION_FILE = "eval-scale-ref.txt";

        public static readonly int evalScale;
        public static readonly CalibrationResult calibration;

        public readonly record struct CalibrationResult(int positions, double fittedScale, double correlation, double rmse);

        static CalibrationResult calibrate()
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream(CALIBRATION_FILE);
            if (s == null)
            {
                return new CalibrationResult(0, 1.0, double.NaN, double.NaN);
            }

            Board board = Board.CreateBoardFromFEN(FispurEngine.Chess.FenUtility.StartPositionFEN);
            int n = 0;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;

            using var reader = new StreamReader(s);
            while (reader.ReadLine() is string line)
            {
                int sep = line.IndexOf('|');
                if (sep < 0)
                {
                    continue;
                }

                board.board.LoadPosition(line[..sep].Trim());
                double x = rawEval(board);
                double y = double.Parse(line[(sep + 1)..], CultureInfo.InvariantCulture);

                n++;
                sx += x;
                sy += y;
                sxx += x * x;
                syy += y * y;
                sxy += x * y;
            }

            if (n == 0 || sxx == 0)
            {
                return new CalibrationResult(0, 1.0, double.NaN, double.NaN);
            }

            double scale = sxy / sxx;
            double correlation = (n * sxy - sx * sy) / Math.Sqrt((n * sxx - sx * sx) * (n * syy - sy * sy));
            double rmse = Math.Sqrt(Math.Max(0, (syy - 2 * scale * sxy + scale * scale * sxx) / n));

            return new CalibrationResult(n, scale, correlation, rmse);
        }

        static double rawEval(Board board)
        {
            updateAccumulators(board);
            return output(board) * (double)SCALE / QAB;
        }

        public static double exactEval(Board board)
        {
            return rawEval(board) * evalScale / EVAL_SCALE_ONE;
        }

        public static int outputBucket(Board board)
        {
            return (int)((ulong.PopCount(board.AllPiecesBitboard) - 2) / 4);
        }
    }
}
