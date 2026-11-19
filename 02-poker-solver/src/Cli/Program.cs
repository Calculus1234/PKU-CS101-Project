using System;
using System.Collections.Generic;
using System.Text;
using PokerSolver;

namespace PokerSolver.Cli
{
    /// <summary>
    /// 命令行入口。
    ///
    ///   dotnet run --project src/Cli -- "333 44" "55 66"
    ///   dotnet run --project src/Cli -- --test
    ///
    /// 参数：--lead my|opp（先手）、--threads N、--table-log N、--time 毫秒
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                PrintHelp();
                return 0;
            }

            if (args[0] == "--test")
                return RunTests();

            return RunSolve(args);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("斗地主单挑残局解牌器");
            Console.WriteLine();
            Console.WriteLine("  dotnet run --project src/Cli -- \"<我方手牌>\" \"<对方手牌>\" [选项]");
            Console.WriteLine("  dotnet run --project src/Cli -- --test");
            Console.WriteLine();
            Console.WriteLine("手牌写法：连写或空格分隔，支持 3-10 / J Q K A 2 / 小王 / 大王");
            Console.WriteLine("  例：\"333 7 88 99 1010 JJ QQ KK AA\"");
            Console.WriteLine();
            Console.WriteLine("选项：");
            Console.WriteLine("  --lead my|opp     谁先手（默认 my）");
            Console.WriteLine("  --threads N       并行线程数（默认 CPU-1）");
            Console.WriteLine("  --table-log N     置换表 2^N 条（默认 24）");
            Console.WriteLine("  --time MS         时间上限毫秒（默认 180000）");
        }

        private static int RunSolve(string[] args)
        {
            string myText = null;
            string oppText = null;
            bool iLead = true;
            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            int tableLog = 24;
            int timeLimitMs = 180000;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--lead")
                {
                    if (i + 1 < args.Length) iLead = args[++i] != "opp";
                }
                else if (a == "--threads")
                {
                    int v;
                    if (i + 1 < args.Length && int.TryParse(args[++i], out v)) threads = Math.Max(1, v);
                }
                else if (a == "--table-log")
                {
                    int v;
                    if (i + 1 < args.Length && int.TryParse(args[++i], out v)) tableLog = v;
                }
                else if (a == "--time")
                {
                    int v;
                    if (i + 1 < args.Length && int.TryParse(args[++i], out v)) timeLimitMs = Math.Max(100, v);
                }
                else if (myText == null) myText = a;
                else if (oppText == null) oppText = a;
            }

            if (myText == null || oppText == null)
            {
                Console.WriteLine("缺少手牌参数。用 --help 看用法。");
                return 1;
            }

            ulong my, opp;
            string err;
            if (!CardUtil.TryParse(myText, out my, out err))
            {
                Console.WriteLine("我方手牌解析失败：" + err);
                return 1;
            }
            if (!CardUtil.TryParse(oppText, out opp, out err))
            {
                Console.WriteLine("对方手牌解析失败：" + err);
                return 1;
            }

            int myCount = CardUtil.TotalCount(my);
            int oppCount = CardUtil.TotalCount(opp);

            Console.WriteLine("我方（" + myCount + " 张）：" + CardUtil.ToDisplayString(my));
            Console.WriteLine("对方（" + oppCount + " 张）：" + CardUtil.ToDisplayString(opp));
            Console.WriteLine("先手：" + (iLead ? "我方" : "对方")
                + "　线程 " + threads + "　置换表 2^" + tableLog);
            Console.WriteLine();

            bool useParallel = myCount + oppCount >= 22;
            bool myWin;
            bool completed;
            bool timedOut;
            double elapsedMs;
            long nodes;
            Move first;

            if (useParallel)
            {
                ulong firstHand = iLead ? my : opp;
                ulong secondHand = iLead ? opp : my;

                ParallelSolveResult pr = ParallelSolver.Solve(firstHand, secondHand,
                    new ParallelSolveOptions
                    {
                        TimeLimitMs = timeLimitMs,
                        ThreadCount = threads,
                        MaxTableLog2 = tableLog,
                        TotalMemoryLimitBytes = 3_000_000_000L
                    });

                completed = pr.Completed;
                timedOut = pr.TimedOut;
                elapsedMs = pr.ElapsedMs;
                nodes = pr.TotalNodes;
                first = pr.FirstMove;
                myWin = iLead ? pr.CurrentWins : !pr.CurrentWins;
            }
            else
            {
                var solver = new DDSolver(tableLog);
                SolveResult sr = solver.Solve(my, opp, iLead,
                    new SolveOptions { TimeLimitMs = timeLimitMs });

                completed = sr.Completed;
                timedOut = sr.TimedOut;
                elapsedMs = sr.ElapsedMs;
                nodes = sr.Nodes;
                first = sr.FirstMove;
                myWin = sr.CanWin;
            }

            if (!completed)
            {
                Console.WriteLine(timedOut
                    ? "超时：在 " + timeLimitMs + " 毫秒内未得出结论。"
                    : "未能得出确定结论。");
                return 2;
            }

            Console.WriteLine(myWin ? "✔ 我方必胜" : "✘ 我方无解（对方有必胜策略）");

            if (!first.IsPass && myWin)
                Console.WriteLine("首手：" + MoveUtil.Describe(first));

            Console.WriteLine("耗时：" + (elapsedMs / 1000.0).ToString("F2") + " 秒");
            Console.WriteLine("节点：" + nodes.ToString("N0"));
            if (elapsedMs > 0)
                Console.WriteLine("速度：" + (nodes / (elapsedMs / 1000.0) / 1_000_000.0).ToString("F2") + " M 节点/秒");

            return 0;
        }

        // ------------------------------------------------------------ 内置回归测试

        private static int RunTests()
        {
            // 用例来源：课上/群里同学给的残局，期望值都经过人工核对或小程序交叉验证
            string[] myHands = new string[]
            {
                "333 44",
                "3 4",
                "334455",
                "AA KK QQ JJ 1010 99 88 7 333",            // 图1：同学故意坑人的无解局
                "AA KK QQ JJ 1010 99 88 7 444 333",        // 图2
                "AAA KK QQ JJ 1010 99 8 333",              // 图3
                "大王 小王 KK Q J 8 7 6 5 4",              // 图4：小程序 58 秒判死局
                "44 66 10 J QQQ K A 22 大王"               // 20v14：唯一解，首手 10
            };
            string[] oppHands = new string[]
            {
                "55 66",
                "2 小王",
                "6677 8",
                "大王 2222 10 99 8 77 66 5 4 3",
                "大王 2222 10 99 8 77 66 5 4 3",
                "大王 2222 10 99 8 77 66 5 4 3",
                "A K QQ J 10 9999 77 6 55 44 3333",
                "3333 4 555 6 777 8 1010 JJ KK 小王"
            };
            bool[] expectWin = new bool[]
            {
                true, false, true, false, true, true, false, true
            };
            string[] names = new string[]
            {
                "示例1 三带一后剩一对",
                "示例2 两张都是大牌",
                "示例3 连对 vs 连对加单张",
                "图1 无解（同学设的坑）",
                "图2 有解",
                "图3 有解",
                "图4 死局",
                "20v14 唯一解"
            };

            int pass = 0;
            int fail = 0;

            for (int i = 0; i < myHands.Length; i++)
            {
                ulong my, opp;
                string err;
                if (!CardUtil.TryParse(myHands[i], out my, out err) ||
                    !CardUtil.TryParse(oppHands[i], out opp, out err))
                {
                    Console.WriteLine("[FAIL] " + names[i] + " 解析失败：" + err);
                    fail++;
                    continue;
                }

                int myCount = CardUtil.TotalCount(my);
                int oppCount = CardUtil.TotalCount(opp);
                bool useParallel = myCount + oppCount >= 22;

                bool win;
                double ms;

                if (useParallel)
                {
                    var pr = ParallelSolver.Solve(my, opp, new ParallelSolveOptions
                    {
                        TimeLimitMs = 120000,
                        ThreadCount = Math.Max(2, Math.Min(4, Environment.ProcessorCount - 1)),
                        MaxTableLog2 = 24,
                        TotalMemoryLimitBytes = 3_000_000_000L
                    });
                    if (!pr.Completed)
                    {
                        Console.WriteLine("[FAIL] " + names[i] + " 未完成");
                        fail++;
                        continue;
                    }
                    win = pr.CurrentWins;
                    ms = pr.ElapsedMs;
                }
                else
                {
                    var solver = new DDSolver(24);
                    var sr = solver.Solve(my, opp, true, new SolveOptions { TimeLimitMs = 120000 });
                    if (!sr.Completed)
                    {
                        Console.WriteLine("[FAIL] " + names[i] + " 未完成");
                        fail++;
                        continue;
                    }
                    win = sr.CanWin;
                    ms = sr.ElapsedMs;
                }

                bool ok = win == expectWin[i];
                if (ok) pass++; else fail++;

                Console.WriteLine((ok ? "[PASS] " : "[FAIL] ")
                    + names[i] + " 结果=" + (win ? "必胜" : "无解")
                    + " 期望=" + (expectWin[i] ? "必胜" : "无解")
                    + " 耗时=" + (ms / 1000.0).ToString("F2") + "s");
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + pass + " / " + (pass + fail));
            return fail == 0 ? 0 : 1;
        }
    }
}
