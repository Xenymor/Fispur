using FispurEngine.API;
using System.Runtime.CompilerServices;
using static FispurEngine.Fispur.Tunables;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        Move[] killers = new Move[MAX_DEPTH];

        const int TT_SCORE = int.MaxValue;
        const int GOOD_CAPTURE_SCORE = 1_000_000;
        const int BAD_CAPTURE_SCORE = -1_000_000;
        const int BAD_CAPTURE_CUTOFF = -500_000;
        const int KILLER_SCORE = 900_000;

        private int scoreMove(Board board, int ply, Move move, Move ttMove, int prevPiece, int prevTo, int sideToMove)
        {
            if (move.Equals(ttMove))
            {
                return TT_SCORE;
            }

            if (move.IsCapture)
            {
                int captureHistScore = captureHist[getCaptureHistIndex(sideToMove, move)];
                int seeThreshold = move.IsPromotion || move.IsEnPassant ? 0 : -captureHistScore * CaptureHistSEEMult / 32_768;
                return (see(board, move, seeThreshold) ? GOOD_CAPTURE_SCORE : BAD_CAPTURE_SCORE)
                    + MvvMult * pieceVals[(int)move.CapturePieceType]
                    + captureHistScore;
            }

            if (move.Equals(killers[ply]))
            {
                return KILLER_SCORE;
            }

            return quietHistory(board, move, sideToMove, prevPiece, prevTo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void scoreMoves(Board board, int ply, Move* moves, int* scores, int moveCount, Move ttMove, int prevPiece, int prevTo, int sideToMove)
        {
            for (int i = 0; i < moveCount; i++)
            {
                scores[i] = scoreMove(board, ply, moves[i], ttMove, prevPiece, prevTo, sideToMove);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe Move pickMove(Move* moves, int* scores, int i, int moveCount)
        {
            int best = i;
            int bestVal = scores[i];
            for (int j = i + 1; j < moveCount; j++)
            {
                int v = scores[j];
                if (v > bestVal)
                {
                    best = j;
                    bestVal = v;
                }
            }

            (moves[i], moves[best]) = (moves[best], moves[i]);
            scores[best] = scores[i];
            scores[i] = bestVal;

            return moves[i];
        }
    }
}