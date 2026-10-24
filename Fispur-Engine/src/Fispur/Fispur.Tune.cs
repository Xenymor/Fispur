using FispurEngine.API;
using System;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        public static int LmrMinDepth = 2;
        public static int LmrMinMoves = 3;
        public static int LmrBase = 93;
        public static int LmrDivisor = 236;

        public static int RfpMaxDepth = 9;
        public static int RfpMargin = 107;
        public static int RFPImpMargin = 52;

        public static int FpMaxDepth = 7;
        public static int FpMargin = 211;

        public static int HistDivisor = 30168;
        public static int HistMaxBonus = 3847;
        public static int HistBonusMult = 539;
        public static int HistBonusBase = -320;

        public static int CHistoryDivisor = 32695;
        public static int MaxCHistBonus = 4148;
        public static int CHistBonusMult = 529;
        public static int CHistBonusBase = -324;

        public static int NMPMinDepth = 3;
        public static int NMPReductionB = 3;
        public static int NMPReductionDiv = 4;

        public static int ASPWindowDelta = 80;
        public static int ASPWindowReset = 1456;

        public static int SEEPMaxDepth = 4;
        public static int SEEPThreshold = 18;
        public static int SEEPCaptureThreshold = 90;
        public static int SEEPCaptureHistMult = 96;
        public static int SEEPHistMult = 58;

        public static int LMPMaxDepth = 4;
        public static int LMPBase = 9;

        public static int MinIIRDepth = 4;

        public static int CorrGrain = 256;
        public static int CorrScale = 256;
        public static int CorrMax = 64 * CorrGrain;

        public static int CaptureHistDivisor = 32961;
        public static int CaptureHistMaxBonus = 3825;
        public static int CaptureHistBonusMult = 571;
        public static int CaptureHistBonusBase = -324;
        public static int CaptureHistSEEMult = 89;
        public static int MvvMult = 15;

        public static int HHLMRDiv = 10262;

        public static int MoveOverhead = 30;
        public static int TmBaseDiv = 18;
        public static int TmIncPct = 92;
        public static int TmSoftPct = 62;
        public static int TmHardPct = 369;
        public static int TmNodeBase = 152;
        public static int TmNodeMult = 127;
        public static readonly int[] StabScale = { 208, 144, 107, 98, 80 };

        public static int SEMinPly = 0;
        public static int SEMinDepth = 7;
        public static int SEMargin = 24;
        public static int SEDepthMargin = 3;
        public static int SEDMargin = 17;

        [AttributeUsage(AttributeTargets.Field)]
        public sealed class TunableAttribute(int min, int max) : Attribute
        {
            public int min { get; } = min;
            public int max { get; } = max;
            public bool poisoned { get; init; }
        }
    }
}