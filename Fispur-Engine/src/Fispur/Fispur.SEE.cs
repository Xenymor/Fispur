using FispurEngine.API;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        public static readonly int[] pieceVals = [0, 100, 290, 337, 504, 926, 1_000_000];

        private bool see(Board board, Move move, int threshold)
        {
            ulong from = 1UL << move.StartSquare.Index, to = 1UL << move.TargetSquare.Index;

            if (move.IsCastles || move.IsPromotion || move.IsEnPassant)
            {
                return 0 >= threshold;
            }

            int swap = pieceVals[(int)move.CapturePieceType] - threshold;
            if (swap < 0)
            {
                return false;
            }

            swap = pieceVals[(int)move.MovePieceType] - swap;
            if (swap <= 0)
            {
                return true;
            }

            ulong occ = board.AllPiecesBitboard ^ from ^ to;

            ulong wPawns = board.GetPieceBitboard(PieceType.Pawn, true);
            ulong bPawns = board.GetPieceBitboard(PieceType.Pawn, false);
            ulong knights = board.GetPieceBitboard(PieceType.Knight, true) | board.GetPieceBitboard(PieceType.Knight, false);
            ulong kings = board.GetPieceBitboard(PieceType.King, true) | board.GetPieceBitboard(PieceType.King, false);
            ulong bishops = board.GetPieceBitboard(PieceType.Bishop, true) | board.GetPieceBitboard(PieceType.Bishop, false);
            ulong rooks = board.GetPieceBitboard(PieceType.Rook, true) | board.GetPieceBitboard(PieceType.Rook, false);
            ulong queens = board.GetPieceBitboard(PieceType.Queen, true) | board.GetPieceBitboard(PieceType.Queen, false);

            ulong attackers = attackersTo(move.TargetSquare, occ, wPawns, bPawns, knights, kings, bishops, rooks, queens);
            bool stm = board.IsWhiteToMove;
            ulong boys = board.WhitePiecesBitboard, opps = board.BlackPiecesBitboard;
            if (!stm)
            {
                (boys, opps) = (opps, boys);
            }
            int res = 1;

            while (true)
            {
                stm = !stm;
                (boys, opps) = (opps, boys);
                attackers &= occ;
                ulong stmAttackers = attackers & boys;
                if (stmAttackers == 0)
                {
                    break;
                }

                res ^= 1;
                PieceType pt = getLowestPiece(wPawns, bPawns, knights, kings, bishops, rooks, queens, stmAttackers);

                if (pt == PieceType.King)
                {
                    return (attackers & opps) != 0 ? res == 0 : res != 0;
                }

                swap = pieceVals[(int)pt] - swap;
                if (swap < res)
                {
                    break;
                }

                ulong bb = stmAttackers & getPieceBitboard(pt, wPawns | bPawns, knights, bishops, rooks, queens, kings);
                ulong lsb = bb & (0UL - bb);
                occ ^= lsb;
                if (pt == PieceType.Pawn || pt == PieceType.Bishop || pt == PieceType.Queen)
                {
                    attackers |= BitboardHelper.GetSliderAttacks(PieceType.Bishop, move.TargetSquare, occ) & (bishops | queens);
                }
                if (pt == PieceType.Rook || pt == PieceType.Queen)
                {
                    attackers |= BitboardHelper.GetSliderAttacks(PieceType.Rook, move.TargetSquare, occ) & (rooks | queens);
                }
            }

            return res != 0;
        }

        private ulong getPieceBitboard(PieceType pt, ulong pawns, ulong knights, ulong bishops, ulong rooks, ulong queens, ulong kings)
        {
            switch (pt)
            {
                case PieceType.Pawn:
                    return pawns;
                case PieceType.Knight:
                    return knights;
                case PieceType.Bishop:
                    return bishops;
                case PieceType.Rook:
                    return rooks;
                case PieceType.Queen:
                    return queens;
                case PieceType.King:
                    return kings;
                default:
                    return 0;
            }
        }

        private static PieceType getLowestPiece(ulong wPawns, ulong bPawns, ulong knights, ulong kings, ulong bishops, ulong rooks, ulong queens, ulong boys)
        {
            if ((boys & (wPawns | bPawns)) > 0)
            {
                return PieceType.Pawn;
            }
            else if ((boys & knights) > 0)
            {
                return PieceType.Knight;
            }
            else if ((boys & bishops) > 0)
            {
                return PieceType.Bishop;
            }
            else if ((boys & rooks) > 0)
            {
                return PieceType.Rook;
            }
            else if ((boys & queens) > 0)
            {
                return PieceType.Queen;
            }
            else if ((boys & kings) > 0)
            {
                return PieceType.King;
            }

            return PieceType.None;
        }

        private static ulong attackersTo(Square to, ulong occ, ulong wPawns, ulong bPawns, ulong knights, ulong kings, ulong bishops, ulong rooks, ulong queens)
        {
            return (BitboardHelper.GetPawnAttacks(to, true) & bPawns)
                 | (BitboardHelper.GetPawnAttacks(to, false) & wPawns)
                 | (BitboardHelper.GetKnightAttacks(to) & knights)
                 | (BitboardHelper.GetKingAttacks(to) & kings)
                 | (BitboardHelper.GetSliderAttacks(PieceType.Bishop, to, occ) & (bishops | queens))
                 | (BitboardHelper.GetSliderAttacks(PieceType.Rook, to, occ) & (rooks | queens));
        }
    }
}
