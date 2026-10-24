namespace FispurEngine.Chess
{
    using System;
    using System.Numerics;

    // List of the squares occupied by one piece type/colour.
    // This is a view onto the piece's bitboard in the board (so it never has to be updated while making/undoing moves).
    // Squares are listed in ascending order of their index.
    public class PieceList
    {
        readonly Board board;
        readonly int piece;

        public PieceList(Board board, int piece)
        {
            this.board = board;
            this.piece = piece;
        }

        ulong Bitboard => board.pieceBitboards[piece];

        public int Count => BitOperations.PopCount(Bitboard);

        // Indices of squares occupied by given piece type
        public int[] occupiedSquares
        {
            get
            {
                ulong bitboard = Bitboard;
                int[] squares = new int[BitOperations.PopCount(bitboard)];
                for (int i = 0; i < squares.Length; i++)
                {
                    squares[i] = BitBoardUtility.PopLSB(ref bitboard);
                }
                return squares;
            }
        }

        public int this[int index]
        {
            get
            {
                ulong bitboard = Bitboard;
                if ((uint)index >= (uint)BitOperations.PopCount(bitboard))
                {
                    throw new IndexOutOfRangeException();
                }
                // Clear the lowest set bits until the requested one is the lowest
                for (int i = 0; i < index; i++)
                {
                    bitboard &= bitboard - 1;
                }
                return BitOperations.TrailingZeroCount(bitboard);
            }
        }

    }
}
