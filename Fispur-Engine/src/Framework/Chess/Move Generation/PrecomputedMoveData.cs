namespace FispurEngine.Chess
{
    using System.Collections.Generic;
    using static System.Math;

    public static class PrecomputedMoveData
    {


        // Full line through two squares (0 if not aligned). Flattened: index = squareA * 64 + squareB
        public static readonly ulong[] AlignMaskFlat;
        // Ray from a square in one direction (including the square). Flattened: index = dirIndex * 64 + square
        public static readonly ulong[] dirRayMask;
        // Squares strictly between two squares on a shared rank, file or diagonal (0 if not aligned).
        // Flattened: index = squareA * 64 + squareB
        public static readonly ulong[] BetweenMask;

        // First 4 are orthogonal, last 4 are diagonals (N, S, W, E, NW, SE, NE, SW)
        public static readonly int[] directionOffsets = { 8, -8, -1, 1, 7, -7, 9, -9 };

        static readonly Coord[] dirOffsets2D =
        {
             new Coord(0, 1),
             new Coord(0, -1),
             new Coord(-1, 0),
             new Coord(1, 0),
             new Coord(-1, 1),
             new Coord(1, -1),
             new Coord(1, 1),
             new Coord(-1, -1)
        };


        // Number of squares to the edge in each of the 8 directions (N, S, W, E, NW, SE, NE, SW).
        // Flattened: index = square * 8 + dirIndex, e.g. numSquaresToEdge[1 * 8 + 0] == 7 (7 squares north of b1)
        public static readonly int[] numSquaresToEdge;

        public static readonly int[] directionLookup;

        public static readonly ulong[] kingAttackBitboards;
        public static readonly ulong[] knightAttackBitboards;
        // Flattened: index = square * 2 + colourIndex
        public static readonly ulong[] pawnAttackBitboards;

        public static readonly ulong[] rookMoves;
        public static readonly ulong[] bishopMoves;
        public static readonly ulong[] queenMoves;

        // Aka manhattan distance (answers how many moves for a rook to get from square a to square b)
        // Flattened: index = squareA * 64 + squareB
        public static int[] OrthogonalDistance;
        // Aka chebyshev distance (answers how many moves for a king to get from square a to square b)
        // Flattened: index = squareA * 64 + squareB
        public static int[] kingDistance;
        public static int[] CentreManhattanDistance;

        public static int NumRookMovesToReachSquare(int startSquare, int targetSquare)
        {
            return OrthogonalDistance[startSquare * 64 + targetSquare];
        }

        public static int NumKingMovesToReachSquare(int startSquare, int targetSquare)
        {
            return kingDistance[startSquare * 64 + targetSquare];
        }

        // Initialize lookup data
        static PrecomputedMoveData()
        {
            numSquaresToEdge = new int[64 * 8];

            rookMoves = new ulong[64];
            bishopMoves = new ulong[64];
            queenMoves = new ulong[64];

            // Calculate knight jumps and available squares for each square on the board.
            // See comments by variable definitions for more info.
            int[] allKnightJumps = { 15, 17, -17, -15, 10, -6, 6, -10 };
            knightAttackBitboards = new ulong[64];
            kingAttackBitboards = new ulong[64];
            pawnAttackBitboards = new ulong[64 * 2];

            for (int squareIndex = 0; squareIndex < 64; squareIndex++)
            {

                int y = squareIndex / 8;
                int x = squareIndex - y * 8;

                int north = 7 - y;
                int south = y;
                int west = x;
                int east = 7 - x;
                numSquaresToEdge[squareIndex * 8 + 0] = north;
                numSquaresToEdge[squareIndex * 8 + 1] = south;
                numSquaresToEdge[squareIndex * 8 + 2] = west;
                numSquaresToEdge[squareIndex * 8 + 3] = east;
                numSquaresToEdge[squareIndex * 8 + 4] = System.Math.Min(north, west);
                numSquaresToEdge[squareIndex * 8 + 5] = System.Math.Min(south, east);
                numSquaresToEdge[squareIndex * 8 + 6] = System.Math.Min(north, east);
                numSquaresToEdge[squareIndex * 8 + 7] = System.Math.Min(south, west);

                // Calculate all squares knight can jump to from current square
                ulong knightBitboard = 0;
                foreach (int knightJumpDelta in allKnightJumps)
                {
                    int knightJumpSquare = squareIndex + knightJumpDelta;
                    if (knightJumpSquare >= 0 && knightJumpSquare < 64)
                    {
                        int knightSquareY = knightJumpSquare / 8;
                        int knightSquareX = knightJumpSquare - knightSquareY * 8;
                        // Ensure knight has moved max of 2 squares on x/y axis (to reject indices that have wrapped around side of board)
                        int maxCoordMoveDst = System.Math.Max(System.Math.Abs(x - knightSquareX), System.Math.Abs(y - knightSquareY));
                        if (maxCoordMoveDst == 2)
                        {
                            knightBitboard |= 1ul << knightJumpSquare;
                        }
                    }
                }
                knightAttackBitboards[squareIndex] = knightBitboard;

                // Calculate all squares king can move to from current square (not including castling)
                foreach (int kingMoveDelta in directionOffsets)
                {
                    int kingMoveSquare = squareIndex + kingMoveDelta;
                    if (kingMoveSquare >= 0 && kingMoveSquare < 64)
                    {
                        int kingSquareY = kingMoveSquare / 8;
                        int kingSquareX = kingMoveSquare - kingSquareY * 8;
                        // Ensure king has moved max of 1 square on x/y axis (to reject indices that have wrapped around side of board)
                        int maxCoordMoveDst = System.Math.Max(System.Math.Abs(x - kingSquareX), System.Math.Abs(y - kingSquareY));
                        if (maxCoordMoveDst == 1)
                        {
                            kingAttackBitboards[squareIndex] |= 1ul << kingMoveSquare;
                        }
                    }
                }

                // Calculate legal pawn captures for white and black
                if (x > 0)
                {
                    if (y < 7)
                    {
                        pawnAttackBitboards[squareIndex * 2 + Board.WhiteIndex] |= 1ul << (squareIndex + 7);
                    }
                    if (y > 0)
                    {
                        pawnAttackBitboards[squareIndex * 2 + Board.BlackIndex] |= 1ul << (squareIndex - 9);
                    }
                }
                if (x < 7)
                {
                    if (y < 7)
                    {
                        pawnAttackBitboards[squareIndex * 2 + Board.WhiteIndex] |= 1ul << (squareIndex + 9);
                    }
                    if (y > 0)
                    {
                        pawnAttackBitboards[squareIndex * 2 + Board.BlackIndex] |= 1ul << (squareIndex - 7);
                    }
                }

                // Rook moves
                for (int directionIndex = 0; directionIndex < 4; directionIndex++)
                {
                    int currentDirOffset = directionOffsets[directionIndex];
                    for (int n = 0; n < numSquaresToEdge[squareIndex * 8 + directionIndex]; n++)
                    {
                        int targetSquare = squareIndex + currentDirOffset * (n + 1);
                        rookMoves[squareIndex] |= 1ul << targetSquare;
                    }
                }
                // Bishop moves
                for (int directionIndex = 4; directionIndex < 8; directionIndex++)
                {
                    int currentDirOffset = directionOffsets[directionIndex];
                    for (int n = 0; n < numSquaresToEdge[squareIndex * 8 + directionIndex]; n++)
                    {
                        int targetSquare = squareIndex + currentDirOffset * (n + 1);
                        bishopMoves[squareIndex] |= 1ul << targetSquare;
                    }
                }
                queenMoves[squareIndex] = rookMoves[squareIndex] | bishopMoves[squareIndex];
            }

            directionLookup = new int[127];
            for (int i = 0; i < 127; i++)
            {
                int offset = i - 63;
                int absOffset = System.Math.Abs(offset);
                int absDir = 1;
                if (absOffset % 9 == 0)
                {
                    absDir = 9;
                }
                else if (absOffset % 8 == 0)
                {
                    absDir = 8;
                }
                else if (absOffset % 7 == 0)
                {
                    absDir = 7;
                }

                directionLookup[i] = absDir * System.Math.Sign(offset);
            }

            // Distance lookup
            OrthogonalDistance = new int[64 * 64];
            kingDistance = new int[64 * 64];
            CentreManhattanDistance = new int[64];
            for (int squareA = 0; squareA < 64; squareA++)
            {
                Coord coordA = BoardHelper.CoordFromIndex(squareA);
                int fileDstFromCentre = Max(3 - coordA.fileIndex, coordA.fileIndex - 4);
                int rankDstFromCentre = Max(3 - coordA.rankIndex, coordA.rankIndex - 4);
                CentreManhattanDistance[squareA] = fileDstFromCentre + rankDstFromCentre;

                for (int squareB = 0; squareB < 64; squareB++)
                {

                    Coord coordB = BoardHelper.CoordFromIndex(squareB);
                    int rankDistance = Abs(coordA.rankIndex - coordB.rankIndex);
                    int fileDistance = Abs(coordA.fileIndex - coordB.fileIndex);
                    OrthogonalDistance[squareA * 64 + squareB] = fileDistance + rankDistance;
                    kingDistance[squareA * 64 + squareB] = Max(fileDistance, rankDistance);
                }
            }

            AlignMaskFlat = new ulong[64 * 64];
            for (int squareA = 0; squareA < 64; squareA++)
            {
                for (int squareB = 0; squareB < 64; squareB++)
                {
                    Coord cA = BoardHelper.CoordFromIndex(squareA);
                    Coord cB = BoardHelper.CoordFromIndex(squareB);
                    Coord delta = cB - cA;
                    Coord dir = new Coord(System.Math.Sign(delta.fileIndex), System.Math.Sign(delta.rankIndex));
                    //Coord dirOffset = dirOffsets2D[dirIndex];

                    for (int i = -8; i < 8; i++)
                    {
                        Coord coord = BoardHelper.CoordFromIndex(squareA) + dir * i;
                        if (coord.IsValidSquare())
                        {
                            AlignMaskFlat[squareA * 64 + squareB] |= 1ul << (BoardHelper.IndexFromCoord(coord));
                        }
                    }
                }
            }


            BetweenMask = new ulong[64 * 64];
            for (int squareA = 0; squareA < 64; squareA++)
            {
                for (int squareB = 0; squareB < 64; squareB++)
                {
                    Coord cA = BoardHelper.CoordFromIndex(squareA);
                    Coord cB = BoardHelper.CoordFromIndex(squareB);
                    int df = cB.fileIndex - cA.fileIndex;
                    int dr = cB.rankIndex - cA.rankIndex;
                    bool aligned = squareA != squareB && (df == 0 || dr == 0 || Abs(df) == Abs(dr));
                    if (!aligned)
                    {
                        continue;
                    }
                    Coord dir = new Coord(Sign(df), Sign(dr));
                    ulong between = 0;
                    for (Coord c = cA + dir; c.fileIndex != cB.fileIndex || c.rankIndex != cB.rankIndex; c = c + dir)
                    {
                        between |= 1ul << BoardHelper.IndexFromCoord(c);
                    }
                    BetweenMask[squareA * 64 + squareB] = between;
                }
            }


            dirRayMask = new ulong[8 * 64];
            for (int dirIndex = 0; dirIndex < dirOffsets2D.Length; dirIndex++)
            {
                for (int squareIndex = 0; squareIndex < 64; squareIndex++)
                {
                    Coord square = BoardHelper.CoordFromIndex(squareIndex);

                    for (int i = 0; i < 8; i++)
                    {
                        Coord coord = square + dirOffsets2D[dirIndex] * i;
                        if (coord.IsValidSquare())
                        {
                            dirRayMask[dirIndex * 64 + squareIndex] |= 1ul << (BoardHelper.IndexFromCoord(coord));
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
}