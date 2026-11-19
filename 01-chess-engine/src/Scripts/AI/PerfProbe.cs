#if PERF_PROBE
using System;
using System.Diagnostics;
using System.Threading;

namespace ChessDark.AI
{
    // 仅诊断编译启用（-define:PERF_PROBE）。Unity 正式包不包含任何探针代码。
    internal static class PerfProbe
    {
        public static long EvalTicks, EvalCalls;
        public static long DangerTicks, DangerCalls;
        public static long ZoneTicks, ZoneCalls;
        public static long ShieldTicks, ShieldCalls;
        public static long ExitTicks, ExitCalls;
        public static long TrapTicks, TrapCalls;
        public static long OrderTicks, OrderCalls;
        public static long HashTicks, HashCalls;
        public static long PessTicks, PessCalls;
        public static long NegamaxCalls, QNodeCalls;
        public static long GenerateTicks, GenerateCalls;
        public static long ExecuteTicks, ExecuteCalls;
        public static long UndoTicks, UndoCalls;

        public static long Now => Stopwatch.GetTimestamp();

        public static void Add(ref long target, long delta) => Interlocked.Add(ref target, delta);

        public static void Hit(ref long counter) => Interlocked.Increment(ref counter);

        private static string Line(string name, long ticks, long calls)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            double per = calls > 0 ? ticks * 1000.0 / Stopwatch.Frequency / calls : 0.0;
            return $"{name,-14} {ms,10:F0} ms  calls={calls,12}  {per,8:F2} us/call";
        }

        public static void Dump()
        {
            Console.WriteLine("==== PERF PROBE ====");
            Console.WriteLine(Line("Evaluate", EvalTicks, EvalCalls));
            Console.WriteLine(Line("  KingDanger", DangerTicks, DangerCalls));
            Console.WriteLine(Line("  ScanZones", ZoneTicks, ZoneCalls));
            Console.WriteLine(Line("  KingShield", ShieldTicks, ShieldCalls));
            Console.WriteLine(Line("  KingExits", ExitTicks, ExitCalls));
            Console.WriteLine(Line("  TrapRisk", TrapTicks, TrapCalls));
            Console.WriteLine(Line("OrderMoves", OrderTicks, OrderCalls));
            Console.WriteLine(Line("HashBoard", HashTicks, HashCalls));
            Console.WriteLine(Line("PessKingCap", PessTicks, PessCalls));
            Console.WriteLine(Line("Generate", GenerateTicks, GenerateCalls));
            Console.WriteLine(Line("Execute", ExecuteTicks, ExecuteCalls));
            Console.WriteLine(Line("Undo", UndoTicks, UndoCalls));
            Console.WriteLine($"NegamaxCalls={NegamaxCalls} QNodeCalls={QNodeCalls}");
        }
    }
}
#endif
