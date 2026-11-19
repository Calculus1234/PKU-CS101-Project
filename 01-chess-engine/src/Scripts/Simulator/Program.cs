using System;
using ChessDark.AI;
using ChessDark.Simulator;

namespace ChessDark
{
    class Program
    {
        static void Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception) { }

            if (args.Length > 0 && string.Equals(args[0], "battle", StringComparison.OrdinalIgnoreCase))
            {
                var settings = new BattleSimulator.Settings();
                int i = 1;

                if (i < args.Length && !int.TryParse(args[i], out _))
                {
                    settings.Mode = args[i].ToLowerInvariant();
                    i++;
                }

                if (i < args.Length && int.TryParse(args[i++], out int games)) settings.Games = games;
                if (i < args.Length && int.TryParse(args[i++], out int moveMs)) settings.MctsTimeLimitMs = moveMs;
                if (i < args.Length && int.TryParse(args[i++], out int parallel)) settings.ParallelGames = parallel;

                var numbers = new System.Collections.Generic.List<int>();
                for (; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "verbose", StringComparison.OrdinalIgnoreCase))
                        settings.Verbose = true;
                    else if (string.Equals(args[i], "outcomes", StringComparison.OrdinalIgnoreCase))
                        settings.PrintOutcomes = true;
                    else if (string.Equals(args[i], "noponder", StringComparison.OrdinalIgnoreCase))
                        settings.UsePonder = false;
                    else if (string.Equals(args[i], "ponder", StringComparison.OrdinalIgnoreCase))
                        settings.UsePonder = true;
                    else if (string.Equals(args[i], "fastonly", StringComparison.OrdinalIgnoreCase))
                        settings.ForceSessionPath = true;
                    else if (string.Equals(args[i], "aiparallel", StringComparison.OrdinalIgnoreCase))
                        settings.AiParallel = true;
                    else if (string.Equals(args[i], "humanopp", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(args[i], "humanmodel", StringComparison.OrdinalIgnoreCase))
                        settings.OpponentHumanModel = true;
                    else if (string.Equals(args[i], "nobait", StringComparison.OrdinalIgnoreCase))
                        AIPlayer.EnableBaitPenalty = false;
                    else if (string.Equals(args[i], "noinfo", StringComparison.OrdinalIgnoreCase))
                        PredatorAI.EnableInfoLedger = false;
                    else if (string.Equals(args[i], "noreveal", StringComparison.OrdinalIgnoreCase))
                        PredatorAI.EnableRevealCost = false;
                    else if (string.Equals(args[i], "oppmodel", StringComparison.OrdinalIgnoreCase))
                        PredatorAI.EnableOpponentModel = true;
                    else if (string.Equals(args[i], "noext", StringComparison.OrdinalIgnoreCase))
                        settings.OpponentNoKingExtension = true;
                    else if (string.Equals(args[i], "noconf", StringComparison.OrdinalIgnoreCase))
                        settings.OpponentNoConfidentSampling = true;
                    else if (string.Equals(args[i], "nohmr", StringComparison.OrdinalIgnoreCase))
                        settings.OpponentNoLateMoveReduction = true;
                    else if (string.Equals(args[i], "cnohmr", StringComparison.OrdinalIgnoreCase))
                        settings.ContestantNoLateMoveReduction = true;
                    else if (string.Equals(args[i], "cnoconf", StringComparison.OrdinalIgnoreCase))
                        settings.ContestantNoConfidentSampling = true;
                    else if (args[i].StartsWith("only=", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(args[i].Substring(5), out int onlyIndex))
                        settings.OnlyGame = onlyIndex;
                    else if (args[i].StartsWith("style=", StringComparison.OrdinalIgnoreCase) &&
                             Enum.TryParse(args[i].Substring(6), true, out HumanStyle style))
                    {
                        settings.OpponentHumanModel = true;
                        settings.OpponentHumanStyle = style;
                    }
                    else if (string.Equals(args[i], "shuffled", StringComparison.OrdinalIgnoreCase))
                        settings.StandardOpening = false;
                    else if (string.Equals(args[i], "aiformation", StringComparison.OrdinalIgnoreCase))
                        settings.ContestantFormation = true;
                    else if (string.Equals(args[i], "debugscores", StringComparison.OrdinalIgnoreCase))
                        PredatorAI.DebugScores = true;
                    else if (int.TryParse(args[i], out int value))
                        numbers.Add(value);
                }

                // 依次为：Baseline 时限、Fast EMM 时限、MCTS 世界数、Fast EMM 采样数、Fast EMM 深度上限。
                if (numbers.Count > 0) settings.ExpectiminimaxTimeLimitMs = numbers[0];
                if (numbers.Count > 1) settings.HybridReviewTimeLimitMs = numbers[1];
                if (numbers.Count > 2) settings.MctsDeterminizations = numbers[2];
                if (numbers.Count > 3) settings.HybridReviewSamples = numbers[3];
                if (numbers.Count > 4) settings.HybridReviewDepth = numbers[4];

                new BattleSimulator(settings).Run();
#if PERF_PROBE
                ChessDark.AI.PerfProbe.Dump();
#endif
                return;
            }

            int seed = 42;
            if (args.Length > 0 && int.TryParse(args[0], out int customSeed))
            {
                seed = customSeed;
            }

            Console.WriteLine($"使用随机种子: {seed}");
            Console.WriteLine();

            try
            {
                var simulator = new GameSimulator(seed: seed, maxMoves: 300);
                simulator.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n!!! 程序异常: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }
    }
}
