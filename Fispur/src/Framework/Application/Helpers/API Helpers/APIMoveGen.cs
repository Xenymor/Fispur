using FispurEngine.Chess;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FispurEngine.Chess.PrecomputedMoveData;

namespace FispurEngine.Application.APIHelpers
{

    public class APIMoveGen
    {

        public const int MaxMoves = 218;

        public MoveGenerator.PromotionMode promotionsToGenerate = MoveGenerator.PromotionMode.All;

        // ---- Instance variables ----
        bool isWhiteToMove;
        int friendlyColour;
        int friendlyKingSquare;
        int friendlyIndex;
        int enemyIndex;

        bool inCheck;
        bool inDoubleCheck;

        // If in check, this bitboard contains squares in line from checking piece up to king
        // If not in check, all bits are set to 1
        ulong checkRayBitmask;

        ulong pinRays;
        ulong notPinRays;
        ulong opponentAttackMapNoPawns;
        public ulong opponentAttackMap;
        public ulong opponentPawnAttackMap;
        ulong opponentSlidingAttackMap;

        bool generateNonCapture;
        Board board;
        // board.Square of the current board
        int[] squares;
        int currMoveIndex;

        ulong enemyPieces;
        ulong friendlyPieces;
        ulong allPieces;
        ulong emptySquares;
        ulong emptyOrEnemySquares;
        // If only captures should be generated, this will have 1s only in positions of enemy pieces.
        // Otherwise it will have 1s everywhere.
        ulong moveTypeMask;
        bool hasInitializedCurrentPosition;

        public APIMoveGen()
        {
            board = new Board();
            squares = Array.Empty<int>();
        }

        public bool IsInitialized => hasInitializedCurrentPosition;

        // Movegen needs to know when position has changed to allow for some caching optims in api
        public void NotifyPositionChanged()
        {
            hasInitializedCurrentPosition = false;
        }

        public ulong GetOpponentAttackMap(Board board)
        {
            Init(board);
            return opponentAttackMap;
        }

        public bool NoLegalMovesInPosition(Board board)
        {
            Span<API.Move> moves = stackalloc API.Move[MaxMoves];
            generateNonCapture = true;
            Init(board);
            ref API.Move dest = ref MemoryMarshal.GetReference(moves);
            GenerateKingMoves(ref dest);
            if (currMoveIndex > 0) { return false; }

            if (!inDoubleCheck)
            {
                GenerateKnightMoves(ref dest);
                if (currMoveIndex > 0) { return false; }
                GeneratePawnMoves(ref dest);
                if (currMoveIndex > 0) { return false; }
                GenerateSlidingMoves(ref dest, true);
                if (currMoveIndex > 0) { return false; }
            }

            return true;
        }

        // Generates list of legal moves in current position.
        // Quiet moves (non captures) can optionally be excluded. This is used in quiescence search.
        public void GenerateMoves(ref Span<API.Move> moves, Board board, bool includeQuietMoves = true)
        {
            // Moves are written without bounds checks, which requires room for the maximum number of moves.
            // Smaller spans are served through a temporary buffer (throws if the moves don't fit, like before).
            if (moves.Length < MaxMoves)
            {
                Span<API.Move> buffer = stackalloc API.Move[MaxMoves];
                GenerateMovesUnchecked(buffer, board, includeQuietMoves);
                buffer.Slice(0, currMoveIndex).CopyTo(moves);
            }
            else
            {
                GenerateMovesUnchecked(moves, board, includeQuietMoves);
            }

            moves = moves.Slice(0, currMoveIndex);
        }

        // Requires moves.Length >= MaxMoves
        void GenerateMovesUnchecked(Span<API.Move> moves, Board board, bool includeQuietMoves)
        {
            generateNonCapture = includeQuietMoves;

            Init(board);

            ref API.Move dest = ref MemoryMarshal.GetReference(moves);
            GenerateKingMoves(ref dest);

            // Only king moves are valid in a double check position, so can return early.
            if (!inDoubleCheck)
            {
                GenerateSlidingMoves(ref dest);
                GenerateKnightMoves(ref dest);
                GeneratePawnMoves(ref dest);
            }
        }

        // Note, this will only return correct value after GenerateMoves() has been called in the current position
        public bool InCheck()
        {
            return inCheck;
        }

        public void Init(Board board)
        {
            this.board = board;
            squares = board.Square;
            currMoveIndex = 0;


            if (hasInitializedCurrentPosition)
            {
                moveTypeMask = generateNonCapture ? ulong.MaxValue : enemyPieces;
                return;
            }

            hasInitializedCurrentPosition = true;

            // Reset state

            inCheck = false;
            inDoubleCheck = false;
            checkRayBitmask = 0;
            pinRays = 0;

            // Store some info for convenience
            isWhiteToMove = board.IsWhiteToMove;
            friendlyColour = board.MoveColour;
            friendlyIndex = board.MoveColourIndex;
            friendlyKingSquare = board.KingSquare[friendlyIndex];
            enemyIndex = 1 - friendlyIndex;

            // Store some bitboards for convenience
            enemyPieces = board.colourBitboards[enemyIndex];
            friendlyPieces = board.colourBitboards[friendlyIndex];
            allPieces = board.allPiecesBitboard;
            emptySquares = ~allPieces;
            emptyOrEnemySquares = emptySquares | enemyPieces;
            moveTypeMask = generateNonCapture ? ulong.MaxValue : enemyPieces;



            CalculateAttackData();


        }

        // Type of the piece on the given square (0..63). No bounds check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int PieceTypeOnSquare(int square)
        {
            return PieceHelper.PieceType(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(squares), square));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        API.Move CreateAPIMove(int startSquare, int targetSquare, int flag, int movePieceType)
        {
            int capturePieceType = flag == Move.EnPassantCaptureFlag ? PieceHelper.Pawn : PieceTypeOnSquare(targetSquare);
            return new API.Move(new Move(startSquare, targetSquare, flag), movePieceType, capturePieceType);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddMove(ref API.Move dest, int startSquare, int targetSquare, int flag, int movePieceType)
        {
            Unsafe.Add(ref dest, currMoveIndex++) = CreateAPIMove(startSquare, targetSquare, flag, movePieceType);
        }

        void GenerateKingMoves(ref API.Move dest)
        {
            int moveCount = currMoveIndex;
            ulong legalMask = ~(opponentAttackMap | friendlyPieces);
            ulong kingMoves = Bits.KingMoves[friendlyKingSquare] & legalMask & moveTypeMask;
            while (kingMoves != 0)
            {
                int targetSquare = BitBoardUtility.PopLSB(ref kingMoves);
                Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(friendlyKingSquare, targetSquare, 0, PieceHelper.King);
            }
            currMoveIndex = moveCount;

            // Castling
            if (!inCheck && generateNonCapture)
            {
                ulong castleBlockers = opponentAttackMap | allPieces;
                if (board.currentGameState.HasKingsideCastleRight(isWhiteToMove))
                {
                    ulong castleMask = isWhiteToMove ? Bits.WhiteKingsideMask : Bits.BlackKingsideMask;
                    if ((castleMask & castleBlockers) == 0)
                    {
                        int targetSquare = isWhiteToMove ? BoardHelper.g1 : BoardHelper.g8;
                        AddMove(ref dest, friendlyKingSquare, targetSquare, Move.CastleFlag, PieceHelper.King);
                    }
                }
                if (board.currentGameState.HasQueensideCastleRight(isWhiteToMove))
                {
                    ulong castleMask = isWhiteToMove ? Bits.WhiteQueensideMask2 : Bits.BlackQueensideMask2;
                    ulong castleBlockMask = isWhiteToMove ? Bits.WhiteQueensideMask : Bits.BlackQueensideMask;
                    if ((castleMask & castleBlockers) == 0 && (castleBlockMask & allPieces) == 0)
                    {
                        int targetSquare = isWhiteToMove ? BoardHelper.c1 : BoardHelper.c8;
                        AddMove(ref dest, friendlyKingSquare, targetSquare, Move.CastleFlag, PieceHelper.King);
                    }
                }
            }
        }

        void GenerateSlidingMoves(ref API.Move dest, bool exitEarly = false)
        {
            // Limit movement to empty or enemy squares, and must block check if king is in check.
            ulong moveMask = emptyOrEnemySquares & checkRayBitmask & moveTypeMask;

            ulong othogonalSliders = board.FriendlyOrthogonalSliders;
            ulong diagonalSliders = board.FriendlyDiagonalSliders;

            // Pinned pieces cannot move if king is in check
            if (inCheck)
            {
                othogonalSliders &= ~pinRays;
                diagonalSliders &= ~pinRays;
            }

            int alignBase = friendlyKingSquare;
            int moveCount = currMoveIndex;

            // Ortho
            while (othogonalSliders != 0)
            {
                int startSquare = BitBoardUtility.PopLSB(ref othogonalSliders);
                ulong moveSquares = Magic.GetRookAttacks(startSquare, allPieces) & moveMask;

                // If piece is pinned, it can only move along the pin ray
                if (IsPinned(startSquare))
                {
                    moveSquares &= AlignMaskFlat[startSquare * 64 + alignBase];
                }

                int movePieceType = PieceTypeOnSquare(startSquare);
                while (moveSquares != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref moveSquares);
                    Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(startSquare, targetSquare, 0, movePieceType);
                    if (exitEarly)
                    {
                        currMoveIndex = moveCount;
                        return;
                    }
                }
            }

            // Diag
            while (diagonalSliders != 0)
            {
                int startSquare = BitBoardUtility.PopLSB(ref diagonalSliders);
                ulong moveSquares = Magic.GetBishopAttacks(startSquare, allPieces) & moveMask;

                // If piece is pinned, it can only move along the pin ray
                if (IsPinned(startSquare))
                {
                    moveSquares &= AlignMaskFlat[startSquare * 64 + alignBase];
                }

                int movePieceType = PieceTypeOnSquare(startSquare);
                while (moveSquares != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref moveSquares);
                    Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(startSquare, targetSquare, 0, movePieceType);
                    if (exitEarly)
                    {
                        currMoveIndex = moveCount;
                        return;
                    }
                }
            }

            currMoveIndex = moveCount;
        }


        void GenerateKnightMoves(ref API.Move dest)
        {
            int friendlyKnightPiece = PieceHelper.MakePiece(PieceHelper.Knight, friendlyColour);
            // bitboard of all non-pinned knights
            ulong knights = board.pieceBitboards[friendlyKnightPiece] & notPinRays;
            ulong moveMask = emptyOrEnemySquares & checkRayBitmask & moveTypeMask;
            int moveCount = currMoveIndex;

            while (knights != 0)
            {
                int knightSquare = BitBoardUtility.PopLSB(ref knights);
                ulong moveSquares = Bits.KnightAttacks[knightSquare] & moveMask;

                while (moveSquares != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref moveSquares);
                    Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(knightSquare, targetSquare, 0, PieceHelper.Knight);
                }
            }

            currMoveIndex = moveCount;
        }

        // A pawn may move from start to target if it is not pinned, or if it stays on the pin ray
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PawnMoveRespectsPin(int startSquare, int targetSquare)
        {
            return !IsPinned(startSquare) || AlignMaskFlat[startSquare * 64 + friendlyKingSquare] == AlignMaskFlat[targetSquare * 64 + friendlyKingSquare];
        }

        void GeneratePawnMoves(ref API.Move dest)
        {
            int pushDir = isWhiteToMove ? 1 : -1;
            int pushOffset = pushDir * 8;

            int friendlyPawnPiece = PieceHelper.MakePiece(PieceHelper.Pawn, friendlyColour);
            ulong pawns = board.pieceBitboards[friendlyPawnPiece];

            ulong promotionRankMask = isWhiteToMove ? Bits.Rank8 : Bits.Rank1;

            ulong singlePush = (BitBoardUtility.Shift(pawns, pushOffset)) & emptySquares;

            ulong pushPromotions = singlePush & promotionRankMask & checkRayBitmask;


            ulong captureEdgeFileMask = isWhiteToMove ? Bits.NotAFile : Bits.NotHFile;
            ulong captureEdgeFileMask2 = isWhiteToMove ? Bits.NotHFile : Bits.NotAFile;
            ulong captureA = BitBoardUtility.Shift(pawns & captureEdgeFileMask, pushDir * 7) & enemyPieces;
            ulong captureB = BitBoardUtility.Shift(pawns & captureEdgeFileMask2, pushDir * 9) & enemyPieces;

            ulong singlePushNoPromotions = singlePush & ~promotionRankMask & checkRayBitmask;

            ulong capturePromotionsA = captureA & promotionRankMask & checkRayBitmask;
            ulong capturePromotionsB = captureB & promotionRankMask & checkRayBitmask;

            captureA &= checkRayBitmask & ~promotionRankMask;
            captureB &= checkRayBitmask & ~promotionRankMask;

            int moveCount = currMoveIndex;

            // Single / double push
            if (generateNonCapture)
            {
                // Generate single pawn pushes
                while (singlePushNoPromotions != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref singlePushNoPromotions);
                    int startSquare = targetSquare - pushOffset;
                    if (PawnMoveRespectsPin(startSquare, targetSquare))
                    {
                        Unsafe.Add(ref dest, moveCount++) = new API.Move(new Move(startSquare, targetSquare, 0), PieceHelper.Pawn, PieceHelper.None);
                    }
                }

                // Generate double pawn pushes
                ulong doublePushTargetRankMask = isWhiteToMove ? Bits.Rank4 : Bits.Rank5;
                ulong doublePush = BitBoardUtility.Shift(singlePush, pushOffset) & emptySquares & doublePushTargetRankMask & checkRayBitmask;

                while (doublePush != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref doublePush);
                    int startSquare = targetSquare - pushOffset * 2;
                    if (PawnMoveRespectsPin(startSquare, targetSquare))
                    {
                        Unsafe.Add(ref dest, moveCount++) = new API.Move(new Move(startSquare, targetSquare, Move.PawnTwoUpFlag), PieceHelper.Pawn, PieceHelper.None);
                    }
                }
            }

            // Captures
            while (captureA != 0)
            {
                int targetSquare = BitBoardUtility.PopLSB(ref captureA);
                int startSquare = targetSquare - pushDir * 7;

                if (PawnMoveRespectsPin(startSquare, targetSquare))
                {
                    Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(startSquare, targetSquare, 0, PieceHelper.Pawn);
                }
            }

            while (captureB != 0)
            {
                int targetSquare = BitBoardUtility.PopLSB(ref captureB);
                int startSquare = targetSquare - pushDir * 9;

                if (PawnMoveRespectsPin(startSquare, targetSquare))
                {
                    Unsafe.Add(ref dest, moveCount++) = CreateAPIMove(startSquare, targetSquare, 0, PieceHelper.Pawn);
                }
            }

            currMoveIndex = moveCount;

            // Promotions
            if (generateNonCapture)
            {
                while (pushPromotions != 0)
                {
                    int targetSquare = BitBoardUtility.PopLSB(ref pushPromotions);
                    int startSquare = targetSquare - pushOffset;
                    if (!IsPinned(startSquare))
                    {
                        GeneratePromotions(ref dest, startSquare, targetSquare);
                    }
                }
            }


            while (capturePromotionsA != 0)
            {
                int targetSquare = BitBoardUtility.PopLSB(ref capturePromotionsA);
                int startSquare = targetSquare - pushDir * 7;

                if (PawnMoveRespectsPin(startSquare, targetSquare))
                {
                    GeneratePromotions(ref dest, startSquare, targetSquare);
                }
            }

            while (capturePromotionsB != 0)
            {
                int targetSquare = BitBoardUtility.PopLSB(ref capturePromotionsB);
                int startSquare = targetSquare - pushDir * 9;

                if (PawnMoveRespectsPin(startSquare, targetSquare))
                {
                    GeneratePromotions(ref dest, startSquare, targetSquare);
                }
            }

            // En passant
            if (board.currentGameState.enPassantFile > 0)
            {
                int epFileIndex = board.currentGameState.enPassantFile - 1;
                int epRankIndex = isWhiteToMove ? 5 : 2;
                int targetSquare = epRankIndex * 8 + epFileIndex;
                int capturedPawnSquare = targetSquare - pushOffset;

                if (BitBoardUtility.ContainsSquare(checkRayBitmask, capturedPawnSquare))
                {
                    ulong pawnsThatCanCaptureEp = pawns & BitBoardUtility.PawnAttacks(1ul << targetSquare, !isWhiteToMove);

                    while (pawnsThatCanCaptureEp != 0)
                    {
                        int startSquare = BitBoardUtility.PopLSB(ref pawnsThatCanCaptureEp);
                        if (PawnMoveRespectsPin(startSquare, targetSquare))
                        {
                            if (!InCheckAfterEnPassant(startSquare, targetSquare, capturedPawnSquare))
                            {
                                AddMove(ref dest, startSquare, targetSquare, Move.EnPassantCaptureFlag, PieceHelper.Pawn);
                            }
                        }
                    }
                }
            }
        }

        void GeneratePromotions(ref API.Move dest, int startSquare, int targetSquare)
        {
            AddMove(ref dest, startSquare, targetSquare, Move.PromoteToQueenFlag, PieceHelper.Pawn);
            // Don't generate non-queen promotions in q-search
            if (generateNonCapture)
            {
                if (promotionsToGenerate == MoveGenerator.PromotionMode.All)
                {
                    AddMove(ref dest, startSquare, targetSquare, Move.PromoteToKnightFlag, PieceHelper.Pawn);
                    AddMove(ref dest, startSquare, targetSquare, Move.PromoteToRookFlag, PieceHelper.Pawn);
                    AddMove(ref dest, startSquare, targetSquare, Move.PromoteToBishopFlag, PieceHelper.Pawn);
                }
                else if (promotionsToGenerate == MoveGenerator.PromotionMode.QueenAndKnight)
                {
                    AddMove(ref dest, startSquare, targetSquare, Move.PromoteToKnightFlag, PieceHelper.Pawn);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsPinned(int square)
        {
            return ((pinRays >> square) & 1) != 0;
        }

        void GenSlidingAttackMap()
        {
            ulong attacks = 0;
            ulong blockers = allPieces & ~(1ul << friendlyKingSquare);

            ulong orthogonalSliders = board.EnemyOrthogonalSliders;
            while (orthogonalSliders != 0)
            {
                attacks |= Magic.GetRookAttacks(BitBoardUtility.PopLSB(ref orthogonalSliders), blockers);
            }

            ulong diagonalSliders = board.EnemyDiagonalSliders;
            while (diagonalSliders != 0)
            {
                attacks |= Magic.GetBishopAttacks(BitBoardUtility.PopLSB(ref diagonalSliders), blockers);
            }

            opponentSlidingAttackMap = attacks;
        }

        void CalculateAttackData()
        {
            GenSlidingAttackMap();

            // Find checks and pins by enemy sliding pieces (queen, rook, bishop).
            // Using only the enemy pieces as blockers gives, along every ray from the king, the first enemy piece on that ray.
            // If that piece is a slider able to move along the ray, it either gives check (no friendly piece in between)
            // or pins a friendly piece (exactly one friendly piece in between).
            ulong enemyOrthogonalSliders = board.EnemyOrthogonalSliders;
            ulong enemyDiagonalSliders = board.EnemyDiagonalSliders;
            ulong potentialPinners = (Magic.GetRookAttacks(friendlyKingSquare, enemyPieces) & enemyOrthogonalSliders)
                                   | (Magic.GetBishopAttacks(friendlyKingSquare, enemyPieces) & enemyDiagonalSliders);
            int betweenBase = friendlyKingSquare * 64;

            while (potentialPinners != 0)
            {
                int sliderSquare = BitBoardUtility.PopLSB(ref potentialPinners);
                ulong rayMask = BetweenMask[betweenBase + sliderSquare] | 1ul << sliderSquare;
                ulong friendlyBlockers = rayMask & friendlyPieces;

                // No friendly piece blocks the attack, so this is a check
                if (friendlyBlockers == 0)
                {
                    checkRayBitmask |= rayMask;
                    inDoubleCheck = inCheck; // if already in check, then this is double check
                    inCheck = true;
                }
                // Exactly one friendly piece blocks the check, so this is a pin
                else if ((friendlyBlockers & (friendlyBlockers - 1)) == 0)
                {
                    pinRays |= rayMask;
                }
            }

            notPinRays = ~pinRays;

            int enemyColour = friendlyColour ^ PieceHelper.Black;
            ulong knights = board.pieceBitboards[PieceHelper.MakePiece(PieceHelper.Knight, enemyColour)];
            ulong opponentKnightAttacks = 0;

            while (knights != 0)
            {
                opponentKnightAttacks |= Bits.KnightAttacks[BitBoardUtility.PopLSB(ref knights)];
            }

            // Knight checks
            ulong checkingKnights = Bits.KnightAttacks[friendlyKingSquare] & board.pieceBitboards[PieceHelper.MakePiece(PieceHelper.Knight, enemyColour)];
            if (checkingKnights != 0)
            {
                // (more than one checking knight is only possible in positions set up from a fen, but handle it anyway)
                inDoubleCheck = inCheck || (checkingKnights & (checkingKnights - 1)) != 0;
                inCheck = true;
                checkRayBitmask |= checkingKnights;
            }

            // Pawn attacks
            ulong opponentPawnsBoard = board.pieceBitboards[PieceHelper.MakePiece(PieceHelper.Pawn, enemyColour)];
            opponentPawnAttackMap = BitBoardUtility.PawnAttacks(opponentPawnsBoard, !isWhiteToMove);
            if (BitBoardUtility.ContainsSquare(opponentPawnAttackMap, friendlyKingSquare))
            {
                inDoubleCheck = inCheck; // if already in check, then this is double check
                inCheck = true;
                ulong possiblePawnAttackOrigins = isWhiteToMove ? Bits.WhitePawnAttacks[friendlyKingSquare] : Bits.BlackPawnAttacks[friendlyKingSquare];
                ulong pawnCheckMap = opponentPawnsBoard & possiblePawnAttackOrigins;
                checkRayBitmask |= pawnCheckMap;
            }

            int enemyKingSquare = board.KingSquare[enemyIndex];

            opponentAttackMapNoPawns = opponentSlidingAttackMap | opponentKnightAttacks | Bits.KingMoves[enemyKingSquare];
            opponentAttackMap = opponentAttackMapNoPawns | opponentPawnAttackMap;

            if (!inCheck)
            {
                checkRayBitmask = ulong.MaxValue;
            }
        }

        // Test if capturing a pawn with en-passant reveals a sliding piece attack against the king
        // Note: this is only used for cases where pawn appears to not be pinned due to opponent pawn being on same rank
        // (therefore only need to check orthogonal sliders)
        bool InCheckAfterEnPassant(int startSquare, int targetSquare, int epCaptureSquare)
        {
            ulong enemyOrtho = board.EnemyOrthogonalSliders;

            if (enemyOrtho != 0)
            {
                ulong maskedBlockers = (allPieces ^ (1ul << epCaptureSquare | 1ul << startSquare | 1ul << targetSquare));
                ulong rookAttacks = Magic.GetRookAttacks(friendlyKingSquare, maskedBlockers);
                return (rookAttacks & enemyOrtho) != 0;
            }

            return false;
        }



    }
}
