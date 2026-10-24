using FispurEngine.API;
using System;
using System.Runtime.CompilerServices;
using System.Text;
using static FispurEngine.Fispur.Tunables;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        int eval = 0;
        int stability = 0;
        int selDepth = 0;
        int rootDepth = 0;

        Move bestMove;

        long[] rootNodes = new long[64 * 64]; // [from][to]

        Timer timer;
        long nodes;
        long hardLimit;
        Move rootBestMove;

        Move[,] pvTable = new Move[MAX_DEPTH, MAX_DEPTH];
        int[] pvLength = new int[MAX_DEPTH];

        public (Move move, int eval) Think(Board board, Timer timer, int maxDepth)
        {
            selDepth = 0;
            this.timer = timer;
            ensureLmrTable();
            Array.Clear(rootNodes);
            nodes = 0;
            stability = 0;

            for (int i = 0; i < historyHeuristic.Length; i++)
            {
                historyHeuristic[i] /= 2;
            }
            Array.Clear(killers);

            Move[] moves = board.GetLegalMoves();
            bestMove = moves.Length == 0 ? Move.NullMove : moves[0];
            rootBestMove = bestMove;

            long softLimit;

            if (timer.isInfinite)
            {
                softLimit = hardLimit = long.MaxValue;
            }
            else if (timer.moveTime != -1)
            {
                softLimit = hardLimit = Math.Max(1, timer.moveTime - MoveOverhead);
            }
            else
            {
                long remaining = Math.Max(1, timer.MillisecondsRemaining - MoveOverhead);
                int movesToGo = TmBaseDiv;
                long baseTime = remaining / movesToGo + timer.IncrementMilliseconds * TmIncPct / 100;
                hardLimit = Math.Min(baseTime * TmHardPct / 100, remaining * 3 / 4);
                softLimit = Math.Min(baseTime * TmSoftPct / 100, hardLimit);
            }

            NNUE.updateAccumulators(board);

            if (moves.Length == 1 && !timer.isInfinite)
            {
                return (bestMove, NNUE.evaluate(board));
            }

            int currMaxDepth = maxDepth < 0 ? MAX_DEPTH : Math.Min(MAX_DEPTH, maxDepth);
            int dl, dh;
            for (int depth = 1; depth <= currMaxDepth; depth++)
            {
                dl = -ASPWindowDelta; dh = ASPWindowDelta;
                int score;
                bool failed = false;

                rootDepth = depth;

                if (depth >= 5)
                {
                    while (true)
                    {
                        int alpha = dl <= -ASPWindowReset ? -INFINITY : eval + dl, beta = dh >= ASPWindowReset ? INFINITY : eval + dh;
                        score = alphaBeta(board, 0, depth, alpha, beta);

                        if (score <= alpha)
                        {
                            dl *= 2;
                        }
                        else if (score >= beta)
                        {
                            dh *= 2;
                        }
                        else
                        {
                            break;
                        }
                        if (stopSearch)
                        {
                            failed = true;
                            break;
                        }
                    }
                }
                else
                {
                    score = alphaBeta(board, 0, depth, -INFINITY, INFINITY);
                }

                if (stopSearch || failed)
                    break;

                eval = score;


                stability = bestMove.Equals(rootBestMove) ? Math.Min(stability + 1, 4) : 0;
                bestMove = rootBestMove;

                var pvString = new StringBuilder();
                for (int i = 0; i < pvLength[0]; i++)
                {
                    pvString.Append(' ').Append(Chess.MoveUtility.GetMoveNameUCI(pvTable[0, i].move));
                }

                Console.WriteLine("info depth " + depth + " score " + ScoreToUCI(eval) + " seldepth " + selDepth
                    + " nodes " + nodes + " time " + timer.MillisecondsElapsedThisTurn + " nps " + (nodes * 1000 / Math.Max(1, timer.MillisecondsElapsedThisTurn))
                    + " pv" + pvString);

                if (timer.isInfinite && stopSearch)
                {
                    break;
                }

                if (!timer.isInfinite)
                {
                    if (Math.Abs(eval) >= MATE_BOUND)
                    {
                        break;
                    }

                    if (timer.moveTime == -1)
                    {
                        double scale = 1.0;
                        if (depth >= 6)
                        {
                            double bmFrac = rootNodes[bestMove.StartSquare.Index * 64 + bestMove.TargetSquare.Index] / (double)Math.Max(1, nodes);
                            scale = (TmNodeBase / 100.0 - bmFrac) * (TmNodeMult / 100.0) * (StabScale[stability] / 100.0);
                        }

                        if (timer.MillisecondsElapsedThisTurn >= softLimit * scale)
                        {
                            break;
                        }
                    }
                    else if (timer.MillisecondsElapsedThisTurn >= timer.moveTime)
                    {
                        break;
                    }
                }

            }

            return (bestMove, eval);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void updatePV(int ply, Move move)
        {
            pvTable[ply, ply] = move;

            int childLength = pvLength[ply + 1];
            for (int i = ply + 1; i < childLength; i++)
            {
                pvTable[ply, i] = pvTable[ply + 1, i];
            }

            pvLength[ply] = Math.Max(childLength, ply + 1);
        }
    }
}
