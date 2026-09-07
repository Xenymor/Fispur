using System;

namespace FispurEngine.API
{
    public sealed class Timer
    {
        /// <summary>
        /// The amount of time (in milliseconds) that each player started the game with
        /// </summary>
        public readonly int GameStartTimeMilliseconds;

        /// <summary>
        /// The amount of time (in milliseconds) that gets added to the clock after each turn
        /// </summary>
        public readonly int IncrementMilliseconds;

        /// <summary>
        /// Amount of time elapsed since the current player started thinking (in milliseconds)
        /// </summary>
        public int MillisecondsElapsedThisTurn => (int)sw.ElapsedMilliseconds;

        /// <summary>
        /// Amount of time left on the clock for the current player (in milliseconds)
        /// </summary>
        public int MillisecondsRemaining => Math.Max(0, millisRemainingAtStartOfTurn - MillisecondsElapsedThisTurn);

        public bool isInfinite { get; }
        public int moveTime { get; }

        /// <summary>
        /// Amount of time left on the clock for the other player (in milliseconds)
        /// </summary>
        public readonly int OpponentMillisecondsRemaining;
        
        readonly System.Diagnostics.Stopwatch sw;
        readonly int millisRemainingAtStartOfTurn;

        public Timer(int millisRemaining)
        {
            millisRemainingAtStartOfTurn = millisRemaining;
            sw = System.Diagnostics.Stopwatch.StartNew();
        }

        public Timer(int remainingMs, int opponentRemainingMs, int startingMs, int incrementMs = 0)
        {
            millisRemainingAtStartOfTurn = remainingMs;
            sw = System.Diagnostics.Stopwatch.StartNew();
            GameStartTimeMilliseconds = startingMs;
            OpponentMillisecondsRemaining = opponentRemainingMs;
            IncrementMilliseconds = incrementMs;
            this.moveTime = -1;
            this.isInfinite = false;
        }

        public Timer(int remainingMs, int opponentRemainingMs, int startingMs, int incrementMs = 0, int time = -1, bool infinite = false) : this(remainingMs, opponentRemainingMs, startingMs, incrementMs)
        {
            if (infinite)
            {
                remainingMs = -1;
            }
            this.isInfinite = infinite;
            this.moveTime = time;
            millisRemainingAtStartOfTurn = remainingMs;
            sw = System.Diagnostics.Stopwatch.StartNew();
            GameStartTimeMilliseconds = startingMs;
            OpponentMillisecondsRemaining = opponentRemainingMs;
            IncrementMilliseconds = incrementMs;

        }

        public override string ToString()
        {
            return $"Game start time: {GameStartTimeMilliseconds} ms. Turn elapsed time: {MillisecondsElapsedThisTurn} ms. My time remaining: {MillisecondsRemaining} Opponent time remaining: {OpponentMillisecondsRemaining} ms.";
        }
    }
}