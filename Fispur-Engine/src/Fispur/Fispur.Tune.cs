using FispurEngine.API;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace FispurEngine
{
    public partial class Fispur : IChessBot
    {
        public static class Tunables
        {
            [Tunable(    2,     1,    16, poisoned = true)]
            public static readonly int LmrMinDepth = Tuning.resolve();

            [Tunable(    3,     1,    16, poisoned = true)]
            public static readonly int LmrMinMoves = Tuning.resolve();

            [Tunable(   93,     0,   400)]
            public static readonly int LmrBase = Tuning.resolve();

            [Tunable(  236,    50,  1000)]
            public static readonly int LmrDivisor = Tuning.resolve();


            [Tunable(    9,     1,    16, poisoned = true)]
            public static readonly int RfpMaxDepth = Tuning.resolve();

            [Tunable(  107,    10,   500)]
            public static readonly int RfpMargin = Tuning.resolve();

            [Tunable(   52,    10,   500)]
            public static readonly int RFPImpMargin = Tuning.resolve();


            [Tunable(    7,     1,    16, poisoned = true)]
            public static readonly int FpMaxDepth = Tuning.resolve();

            [Tunable(  211,    10,   500)]
            public static readonly int FpMargin = Tuning.resolve();


            [Tunable(30168,   512, 65536)]
            public static readonly int HistoryDivisor = Tuning.resolve();

            [Tunable( 3847,    10, 16000)]
            public static readonly int MaxHistBonus = Tuning.resolve();

            [Tunable(  539,    50,   800)]
            public static readonly int HistBonusMult = Tuning.resolve();

            [Tunable( -320,  -600,   200)]
            public static readonly int HistBonusBase = Tuning.resolve();


            [Tunable(32961,   512, 65536)]
            public static readonly int CaptureHistDivisor = Tuning.resolve();

            [Tunable( 3825,  1500,  8000)]
            public static readonly int CaptureMaxHistBonus = Tuning.resolve();

            [Tunable(  571,    50,   800)]
            public static readonly int CaptureHistBonusMult = Tuning.resolve();

            [Tunable( -324,  -600,   200)]
            public static readonly int CaptureHistBonusBase = Tuning.resolve();

            [Tunable(   89,    20,   330)]
            public static readonly int CaptureHistSEEMult = Tuning.resolve();

            [Tunable(   15,     4,    40)]
            public static readonly int MvvMult = Tuning.resolve();


            [Tunable(32695,   512, 65536)]
            public static readonly int CHistoryDivisor = Tuning.resolve();

            [Tunable( 4148,    10, 16000)]
            public static readonly int MaxCHistBonus = Tuning.resolve();

            [Tunable(  529,    50,   800)]
            public static readonly int CHistBonusMult = Tuning.resolve();

            [Tunable( -324,  -600,   200)]
            public static readonly int CHistBonusBase = Tuning.resolve();


            [Tunable(    3,     1,     8, poisoned = true)]
            public static readonly int NMPMinDepth = Tuning.resolve();

            [Tunable(    3,     1,     6, poisoned = true)]
            public static readonly int NMPReductionB = Tuning.resolve();

            [Tunable(    4,     2,    10, poisoned = true)]
            public static readonly int NMPReductionDiv = Tuning.resolve();


            [Tunable(   80,     5,   200)]
            public static readonly int ASPWindowDelta = Tuning.resolve();

            [Tunable( 1456,   100,  2000)]
            public static readonly int ASPWindowReset = Tuning.resolve();


            [Tunable(    4,     1,     8, poisoned = true)]
            public static readonly int SEEPMaxDepth = Tuning.resolve();

            [Tunable(   18,     0,   500)]
            public static readonly int SEEPThreshold = Tuning.resolve();

            [Tunable(   90,     0,   500)]
            public static readonly int SEEPCaptureThreshold = Tuning.resolve();

            [Tunable(   96,     0,   320)]
            public static readonly int SEEPCaptureHistMult = Tuning.resolve();

            [Tunable(   58,     0,   256)]
            public static readonly int SEEPHistMult = Tuning.resolve();


            [Tunable(  290,   150,   500)]
            public static readonly int KnightValue = Tuning.resolve();

            [Tunable(  337,   150,   550)]
            public static readonly int BishopValue = Tuning.resolve();

            [Tunable(  504,   300,   800)]
            public static readonly int RookValue = Tuning.resolve();

            [Tunable(  926,   600,  1400)]
            public static readonly int QueenValue = Tuning.resolve();

            public static readonly int[] pieceVals = [0, 100, KnightValue, BishopValue, RookValue, QueenValue, 1_000_000];


            [Tunable(    4,     1,     8, poisoned = true)]
            public static readonly int LMPMaxDepth = Tuning.resolve();

            [Tunable(    9,     1,    20, poisoned = true)]
            public static readonly int LMPBase = Tuning.resolve();


            [Tunable(    4,     0,     8, poisoned = true)]
            public static readonly int MinIIRDepth = Tuning.resolve();


            public static readonly int SEMinPly = 0;

            [Tunable(    7,     4,    12, poisoned = true)]
            public static readonly int SEMinDepth = Tuning.resolve();

            [Tunable(   24,     8,    56)]
            public static readonly int SEMargin = Tuning.resolve();

            [Tunable(   17,     0,    60)]
            public static readonly int SEDMargin = Tuning.resolve();

            [Tunable(    3,     1,     6, poisoned = true)]
            public static readonly int SEDepthMargin = Tuning.resolve();


            [Tunable(  256,     1,   500)]
            public static readonly int CorrHistGrain = Tuning.resolve();

            [Tunable(  256,    16,   500)]
            public static readonly int CorrHistScale = Tuning.resolve();

            [Tunable(16384,     0, 50000)]
            public static readonly int CorrHistMax = Tuning.resolve();


            [Tunable(10262,   512, 49152)]
            public static readonly int HHLMRDiv = Tuning.resolve();


            [Tunable(   18,    10,    40)]
            public static readonly int TmBaseDiv = Tuning.resolve();

            [Tunable(   92,    25,   150)]
            public static readonly int TmIncPct = Tuning.resolve();

            [Tunable(   62,    20,   150)]
            public static readonly int TmSoftPct = Tuning.resolve();

            [Tunable(  369,   100,   600)]
            public static readonly int TmHardPct = Tuning.resolve();

            [Tunable(  152,   100,   250)]
            public static readonly int TmNodeBase = Tuning.resolve();

            [Tunable(  127,    50,   300)]
            public static readonly int TmNodeMult = Tuning.resolve();


            [Tunable(  208,   100,   400)]
            public static readonly int StabScale0 = Tuning.resolve();

            [Tunable(  144,    80,   300)]
            public static readonly int StabScale1 = Tuning.resolve();

            [Tunable(  107,    60,   200)]
            public static readonly int StabScale2 = Tuning.resolve();

            [Tunable(   98,    50,   150)]
            public static readonly int StabScale3 = Tuning.resolve();

            [Tunable(   80,    40,   150)]
            public static readonly int StabScale4 = Tuning.resolve();

            public static readonly int[] StabScale = [StabScale0, StabScale1, StabScale2, StabScale3, StabScale4];
        }

        public static class Tuning
        {
            static readonly Dictionary<string, int> newValues = new(StringComparer.OrdinalIgnoreCase);
            public static bool frozen { get; private set; }

            public static bool trySet(string name, int value)
            {
                if (frozen)
                {
                    return false;
                }

                newValues[name] = value;

                return true;
            }

            internal static int resolve([CallerMemberName] string name = "")
            {
                frozen = true;

                return current(name);
            }

            public static int current(string name)
            {
                var attribute = typeof(Tunables).GetField(name)!.GetCustomAttribute<TunableAttribute>()!;

                return newValues.TryGetValue(name, out int v) ? Math.Clamp(v, attribute.min, attribute.max) : attribute.value;
            }

            public static IEnumerable<(string name, TunableAttribute attribute)> all()
            {
                foreach (FieldInfo field in typeof(Tunables).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var attribute = field.GetCustomAttribute<TunableAttribute>();

                    if (attribute != null)
                    {
                        yield return (field.Name, attribute);
                    }
                }
            }
            
            public static void Freeze() => RuntimeHelpers.RunClassConstructor(typeof(Tunables).TypeHandle);
        }

        [AttributeUsage(AttributeTargets.Field)]
        public sealed class TunableAttribute(int value, int min, int max) : Attribute
        {
            public int value { get; } = value;
            public int min { get; } = min;
            public int max { get; } = max;
            public bool poisoned { get; init; }
        }
    }
}