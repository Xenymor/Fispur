namespace FispurEngine.Chess
{
    using System.Linq;
    using System.Numerics;

    public static class Arbiter
    {
        public static bool IsDrawResult(GameResult result)
        {
            return result is GameResult.DrawByArbiter or GameResult.FiftyMoveRule or
                GameResult.Repetition or GameResult.Stalemate or GameResult.InsufficientMaterial;
        }

        public static bool IsWinResult(GameResult result)
        {
            return IsWhiteWinsResult(result) || IsBlackWinsResult(result);
        }

        public static bool IsWhiteWinsResult(GameResult result)
        {
            return result is GameResult.BlackIsMated or GameResult.BlackTimeout or GameResult.BlackIllegalMove;
        }

        public static bool IsBlackWinsResult(GameResult result)
        {
            return result is GameResult.WhiteIsMated or GameResult.WhiteTimeout or GameResult.WhiteIllegalMove;
        }


        public static GameResult GetGameState(Board board)
        {
            MoveGenerator moveGenerator = new MoveGenerator();
            var moves = moveGenerator.GenerateMoves(board);

            // Look for mate/stalemate
            if (moves.Length == 0)
            {
                if (moveGenerator.InCheck())
                {
                    return (board.IsWhiteToMove) ? GameResult.WhiteIsMated : GameResult.BlackIsMated;
                }
                return GameResult.Stalemate;
            }

            // Fifty move rule
            if (board.currentGameState.fiftyMoveCounter >= 100)
            {
                return GameResult.FiftyMoveRule;
            }

            // Threefold repetition
            int repCount = board.RepetitionPositionHistory.Count((x => x == board.currentGameState.zobristKey));
            if (repCount == 3)
            {
                return GameResult.Repetition;
            }

            // Look for insufficient material
            if (InsufficentMaterial(board))
            {
                return GameResult.InsufficientMaterial;
            }
            return GameResult.InProgress;
        }

        // Test for insufficient material (Note: not all cases are implemented)
        public static bool InsufficentMaterial(Board board)
        {
            ulong[] bb = board.pieceBitboards;

            // Can't have insufficient material with pawns on the board
            if ((bb[PieceHelper.WhitePawn] | bb[PieceHelper.BlackPawn]) != 0)
            {
                return false;
            }

            // Can't have insufficient material with queens/rooks on the board
            if (board.FriendlyOrthogonalSliders != 0 || board.EnemyOrthogonalSliders != 0)
            {
                return false;
            }

            // If no pawns, queens, or rooks on the board, then consider knight and bishop cases
            ulong whiteBishops = bb[PieceHelper.WhiteBishop];
            ulong blackBishops = bb[PieceHelper.BlackBishop];
            ulong minors = whiteBishops | blackBishops | bb[PieceHelper.WhiteKnight] | bb[PieceHelper.BlackKnight];
            int numMinors = BitOperations.PopCount(minors);

            // Lone kings or King vs King + single minor: is insuffient
            if (numMinors <= 1)
            {
                return true;
            }

            // Bishop vs bishop: is insufficient when bishops are same colour complex
            if (numMinors == 2 && BitOperations.PopCount(whiteBishops) == 1 && BitOperations.PopCount(blackBishops) == 1)
            {
                bool whiteBishopIsLightSquare = BoardHelper.LightSquare(BitOperations.TrailingZeroCount(whiteBishops));
                bool blackBishopIsLightSquare = BoardHelper.LightSquare(BitOperations.TrailingZeroCount(blackBishops));
                return whiteBishopIsLightSquare == blackBishopIsLightSquare;
            }

            return false;


        }
    }
}