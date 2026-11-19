using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChessDark.Core;
using ChessDark.Logic;
using ChessDark.AI;

namespace ChessDark.Simulator
{
    // 三方对比实验：Baseline(Expectiminimax) / 纯 MCTS / Hybrid，全部对阵 Expectiminimax。
    public sealed class BattleSimulator
    {
        public enum EngineKind
        {
            Baseline,
            Mcts,
            Hybrid,
            Predator
        }

        public sealed class Settings
        {
            public string Mode = "trio"; // trio / baseline / mcts / hybrid
            public int Games = 50;
            public int MaxMoves = 240;
            public int MctsTimeLimitMs = 3000;
            public int ExpectiminimaxTimeLimitMs = 400;
            public int MctsDeterminizations = 64;
            public int ExpectiminimaxSamples = 16;
            public int ExpectiminimaxDepth = 3;
            // Fast EMM（Hybrid）：不再有 MCTS 候选门，直接跑完整 Expectiminimax。
            public int HybridTopK = 5;
            public int HybridReviewSamples = 16;
            public int HybridReviewDepth = 6;
            public int HybridReviewTimeLimitMs = 1400;
            public int BaseSeed = 20261107;
            public int ParallelGames = Math.Max(1, Environment.ProcessorCount - 2);
            public bool StandardOpening = true;
            // 与生产一致：随机布阵模式下，参赛方用开局规划器选阵型，对手保持随机。
            public bool ContestantFormation = false;
            public bool SimulatePlayerProfile = true;
            public bool Verbose = false;
            // 只列出每局胜负与结束原因，便于定位失败局（不打印逐步走法）。
            public bool PrintOutcomes = false;

            // 单局内 AI 搜索并行：批量默认关闭防线程超订；实机深度校验时打开（配合 ParallelGames=1）。
            public bool AiParallel = false;

            // Ponder：玩家思考期间后台直接缓存完整 EMM 答案；无头环境用 WarmUp 同步模拟等长窗口。
            public bool UsePonder = true;

            // 对手换成“人类行为模型”：几何推断 + 便宜子吃 + 王不乱动，用来做更接近真人的对照。
            public bool OpponentHumanModel = false;

            // HumanModel 的打法风格：验证 AI 对不同人类风格的通用性。
            public HumanStyle OpponentHumanStyle = HumanStyle.Aggressive;

            // 对照用：关闭基线 EMM 一侧的王线延伸，单独量化该改动的贡献。
            public bool OpponentNoKingExtension = false;

            // 对照用：关闭基线一侧的“信念确定时减半采样”。
            public bool OpponentNoConfidentSampling = false;

            // 对照用：关闭基线一侧的分级 LMR。
            public bool OpponentNoLateMoveReduction = false;

            // 对照用：关闭被测 Predator 一侧的 LMR / 确定减采样，隔离单侧贡献。
            public bool ContestantNoLateMoveReduction = false;
            public bool ContestantNoConfidentSampling = false;

            // 单局诊断：只跑指定局号（-1 = 全部）。配合 verbose 复现某一局的败因。
            public int OnlyGame = -1;

            // 关闭 Ponder 时仍走实时 Session（Hybrid 直接跑 1400ms 全宽搜索），用于单独评估主路径强度。
            public bool ForceSessionPath = false;

            public int PonderCandidates => Math.Max(3, Math.Min(5, HybridTopK));
            public int PonderPerCandidateMs => HybridReviewTimeLimitMs;
            public int PonderWindowMs => PonderCandidates * PonderPerCandidateMs;
        }

        private sealed class GameOutcome
        {
            public int Index;
            public int Moves;
            public PieceColor? Winner;
            public PieceColor ContestantColor;
            public long ContestantThinkMs;
            public long BaselineThinkMs;
            public long MctsIterations;
            public long MctsRollouts;
            public long MctsWorlds;
            public int ContestantMoves;
            public int BaselineMoves;
            public int Passes;
            public int InvalidMoves;
            public bool TimedOutDraw;
            public string EndReason;
            public string Error;

            public long PonderWarmupMs;
            public int PonderWarmups;
            public int PonderHits;
            public int PonderMisses;
            public long HitResponseMs;
            public long MissResponseMs;
            public long MaxResponseMs;

            // 搜索画像：主深度、静态延伸（×10 累加避免浮点精度损失）、节点数。
            public long SearchDepthSumX10;
            public long QuiescenceDepthSumX10;
            public long NodeSum;
            public long StatMoves;
        }

        private sealed class BatchResult
        {
            public EngineKind Kind;
            public GameOutcome[] Outcomes;
            public TimeSpan Elapsed;
        }

        private readonly Settings _settings;

        // 并发对局时给每条 AI 日志标上局号，否则 16 局日志交错后无法定位问题手。
        private static readonly ThreadLocal<int> CurrentGame = new ThreadLocal<int>(() => -1);

        public BattleSimulator(Settings settings = null)
        {
            _settings = settings ?? new Settings();
        }

        public void Run()
        {
            var kinds = ParseKinds(_settings.Mode);
            PrintHeader(kinds);

            var originalLogger = AIPlayer.Logger;
            bool originalParallel = AIPlayer.EnableParallelSearch;
            if (!_settings.Verbose)
            {
                AIPlayer.Logger = null;
            }
            else
            {
                AIPlayer.Logger = msg =>
                {
                    int idx = CurrentGame.Value;
                    Console.WriteLine(idx >= 0 ? $"[G{idx}] {msg}" : msg);
                };
            }
            AIPlayer.EnableParallelSearch = _settings.AiParallel;

            var batches = new List<BatchResult>();
            try
            {
                foreach (var kind in kinds)
                    batches.Add(RunBatch(kind));
            }
            finally
            {
                AIPlayer.Logger = originalLogger;
                AIPlayer.EnableParallelSearch = originalParallel;
            }

            foreach (var batch in batches)
                PrintBatchReport(batch);

            if (batches.Count > 1)
                PrintComparison(batches);
        }

        private static List<EngineKind> ParseKinds(string mode)
        {
            if (string.Equals(mode, "baseline", StringComparison.OrdinalIgnoreCase))
                return new List<EngineKind> { EngineKind.Baseline };
            if (string.Equals(mode, "mcts", StringComparison.OrdinalIgnoreCase))
                return new List<EngineKind> { EngineKind.Mcts };
            if (string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase))
                return new List<EngineKind> { EngineKind.Hybrid };
            if (string.Equals(mode, "predator", StringComparison.OrdinalIgnoreCase))
                return new List<EngineKind> { EngineKind.Predator };

            return new List<EngineKind> { EngineKind.Baseline, EngineKind.Mcts, EngineKind.Hybrid, EngineKind.Predator };
        }

        private BatchResult RunBatch(EngineKind kind)
        {
            var totalWatch = Stopwatch.StartNew();
            var outcomes = new GameOutcome[_settings.Games];
            int completed = 0;

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _settings.ParallelGames)
            };

            int[] indices;
            if (_settings.OnlyGame >= 0)
            {
                indices = new[] { _settings.OnlyGame };
            }
            else
            {
                indices = new int[_settings.Games];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
            }

            Parallel.ForEach(indices, options, index =>
            {
                outcomes[index] = RunSingleGame(index, kind);
                int done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == indices.Length)
                {
                    Console.WriteLine($"[进度] {EngineName(kind)} {done}/{indices.Length} 局 · 已用时 {totalWatch.Elapsed.TotalSeconds:F0}s");
                }
            });

            totalWatch.Stop();
            return new BatchResult { Kind = kind, Outcomes = outcomes, Elapsed = totalWatch.Elapsed };
        }

        private GameOutcome RunSingleGame(int index, EngineKind kind)
        {
            CurrentGame.Value = index;
            var settings = _settings;
            var outcome = new GameOutcome
            {
                Index = index,
                ContestantColor = index % 2 == 0 ? PieceColor.White : PieceColor.Black
            };
            PieceColor baselineColor = OpponentColor(outcome.ContestantColor);
            int seed = settings.BaseSeed + index * 7919;

            var engine = new GameEngine(seed);
            engine.InitializeGame(settings.StandardOpening);

            // 生产对局里 AI 的阵型由规划器决定（GameManager.ApplyFormation）。基准若只做纯随机，
            // 等于把真后/真车摆到第一排让对手开局白吃，衡量不到真实强度。
            if (settings.ContestantFormation && !settings.StandardOpening)
            {
                var planner = new FormationPlanner(outcome.ContestantColor,
                    settings.SimulatePlayerProfile ? CreateAggressiveProfile() : new PlayerTendency(),
                    seed + 401);
                var formation = planner.GetBestAvailableAgainst(engine.Board);
                engine.ApplyFormation(formation, outcome.ContestantColor);
            }

            AIPlayer contestant = CreateContestant(kind, outcome.ContestantColor, seed);
            AIPlayer baseline = settings.OpponentHumanModel
                ? (AIPlayer)new HumanModelAI(baselineColor, seed + 202,
                    settings.ExpectiminimaxTimeLimitMs, settings.OpponentHumanStyle)
                : new ExpectiminimaxAI(baselineColor, seed + 202,
                    settings.ExpectiminimaxSamples, settings.ExpectiminimaxDepth,
                    settings.ExpectiminimaxTimeLimitMs);
            if (settings.OpponentNoKingExtension && baseline is ExpectiminimaxAI baselineEmm)
                baselineEmm.KingLineExtensionEnabled = false;
            if (settings.OpponentNoConfidentSampling && baseline is ExpectiminimaxAI baselineEmm2)
                baselineEmm2.UseConfidentSampling = false;
            if (settings.OpponentNoLateMoveReduction && baseline is ExpectiminimaxAI baselineEmm3)
                baselineEmm3.UseLateMoveReduction = false;

            if (contestant is HybridAI contestantHybrid)
            {
                if (settings.ContestantNoLateMoveReduction)
                    contestantHybrid.LateMoveReductionEnabled = false;
                if (settings.ContestantNoConfidentSampling)
                    contestantHybrid.ConfidentSamplingEnabled = false;
            }

            PlayerTendency profile = settings.SimulatePlayerProfile ? CreateAggressiveProfile() : null;
            if (profile != null)
            {
                contestant.SetPlayerTendency(profile);
                baseline.SetPlayerTendency(profile);
            }

            contestant.WouldCauseThreefold = m => engine.WouldCauseThreefold(m);
            baseline.WouldCauseThreefold = m => engine.WouldCauseThreefold(m);

            // 标签只是公开外壳（乱序布阵下与真型无关），任何一侧都不能把可见标签当先验；先验只看剩余子数。
            contestant.SetVisibleLabelTrust(0f);

            contestant.ObserveGameStart(engine.Board);
            baseline.ObserveGameStart(engine.Board);

            // Ponder 直接缓存完整 EMM 答案，只有 Hybrid 使用。
            PonderPlanner ponder = null;
            if (settings.UsePonder && (kind == EngineKind.Hybrid || kind == EngineKind.Predator))
            {
                ponder = new PonderPlanner(outcome.ContestantColor, settings.HybridReviewSamples,
                    seed + 301, settings.PonderCandidates, settings.PonderPerCandidateMs,
                    settings.HybridReviewDepth);
                ponder.SetPlayerTendency(profile);
            }

            try
            {
                int safety = settings.MaxMoves * 2;
                while (!engine.IsGameOver && engine.MoveCount < settings.MaxMoves && safety-- > 0)
                {
                    bool contestantTurn = engine.CurrentTurn == outcome.ContestantColor;
                    AIPlayer ai = contestantTurn ? contestant : (AIPlayer)baseline;
                    AIPlayer opponent = contestantTurn ? (AIPlayer)baseline : contestant;

                    Move? chosen;
                    try
                    {
                        chosen = contestantTurn
                            ? SearchContestant(contestant, ponder, engine.Board, outcome)
                            : SearchBaseline(baseline, ponder, contestant, engine.Board, outcome);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = $"第 {engine.MoveCount} 步 {EngineName(contestantTurn ? kind : EngineKind.Baseline)} 搜索异常：{ex.GetType().Name}: {ex.Message}";
                        break;
                    }

                    if (!chosen.HasValue)
                    {
                        outcome.Passes++;
                        engine.SkipTurn();
                        continue;
                    }

                    Move move = chosen.Value;
                    var legal = engine.GetLegalMoves();
                    if (!legal.Contains(move))
                    {
                        outcome.InvalidMoves++;
                        if (legal.Count == 0)
                        {
                            outcome.Passes++;
                            engine.SkipTurn();
                            continue;
                        }
                        move = legal[0];
                    }

                    var preBoard = engine.Board.Clone();
                    var visible = preBoard.GetPiece(move.From)?.VisibleType ?? PieceType.Pawn;

                    MoveResult result;
                    try
                    {
                        result = engine.MakeMove(move);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = $"第 {engine.MoveCount} 步执行 {move} 失败：{ex.GetType().Name}: {ex.Message}";
                        break;
                    }

                    if (_settings.Verbose)
                    {
                        string capture = result.CapturedPiece != null
                            ? $" 吃[{result.CapturedPiece.VisibleType.ToChinese()}/{result.CapturedPiece.TrueType.ToChinese()}]"
                            : string.Empty;
                        Console.WriteLine($"[G{index}] {engine.MoveCount,3}. {EngineName(contestantTurn ? kind : EngineKind.Baseline),15} {move} " +
                            $"伪装[{visible.ToChinese()}] 真[{result.MovingPiece.TrueType.ToChinese()}]{capture}");
                    }

                    ai.NotifyOwnMove(move, result.CapturedPiece != null, preBoard);
                    opponent.NotifyOpponentMove(move, visible, result.LeakType, preBoard);
                    if (result.CapturedPiece != null)
                        ai.NotifyCapture(move.To);
                    if (move.PromotionType.HasValue)
                        opponent.NotifyPromotion(move.To, move.PromotionType);
                }
            }
            finally
            {
                ponder?.Stop();
            }

            outcome.Moves = engine.MoveCount;
            if (engine.IsGameOver)
            {
                outcome.Winner = engine.Winner;
                outcome.EndReason = engine.EndReason.ToString();
                if (_settings.Verbose)
                {
                    string reason = engine.EndReason == GameEndReason.KingCaptured ? "真王被吃" : "三次重复判负";
                    int contestantMaterial = 0, baselineMaterial = 0;
                    foreach (var (_, piece) in engine.Board.GetAllPieces())
                    {
                        if (piece.Color == outcome.ContestantColor) contestantMaterial += piece.TrueType.GetValue();
                        else baselineMaterial += piece.TrueType.GetValue();
                    }
                    Console.WriteLine($"[G{index}] 终局：{reason} · 胜者 {engine.Winner} · 子力 参赛方 {contestantMaterial} / 对手 {baselineMaterial} · {engine.MoveCount} 步");
                }
            }
            else
            {
                outcome.TimedOutDraw = true;
                if (_settings.Verbose)
                    Console.WriteLine($"[G{index}] 终局：{engine.MoveCount} 步上限，未分胜负。");
            }

            return outcome;
        }

        // 参赛方出招：Ponder 命中直接取缓存答案；未命中现场跑完整 1400ms Expectiminimax。
        private Move? SearchContestant(AIPlayer contestant, PonderPlanner ponder,
            Board board, GameOutcome outcome)
        {
            var watch = Stopwatch.StartNew();
            PonderResult cached = ponder != null ? ponder.Consume(board) : null;
            Move? chosen;

            if (ponder != null || _settings.ForceSessionPath)
            {
                var session = contestant.CreateSearchSession(board, cached);
                while (!session.IsFinished)
                    session.Advance(20);
                chosen = session.Result;
            }
            else
            {
                chosen = contestant.GetBestMove(board);
            }

            watch.Stop();
            long ms = watch.ElapsedMilliseconds;
            outcome.ContestantThinkMs += ms;
            outcome.ContestantMoves++;
            if (ms > outcome.MaxResponseMs) outcome.MaxResponseMs = ms;

            if (ponder != null)
            {
                if (cached != null)
                {
                    outcome.PonderHits++;
                    outcome.HitResponseMs += ms;
                }
                else
                {
                    outcome.PonderMisses++;
                    outcome.MissResponseMs += ms;
                }
            }

            AccumulateStats(contestant, outcome);
            return chosen;
        }

        // 对手（模拟玩家）走棋前先替参赛方预热后台预判；这段耗时属于玩家思考窗口，不计入出招等待。
        private Move? SearchBaseline(AIPlayer baseline, PonderPlanner ponder,
            AIPlayer contestant, Board board, GameOutcome outcome)
        {
            if (ponder != null)
            {
                var warmWatch = Stopwatch.StartNew();
                ponder.WarmUp(board, contestant.GetBeliefState(), _settings.PonderWindowMs);
                warmWatch.Stop();
                outcome.PonderWarmupMs += warmWatch.ElapsedMilliseconds;
                outcome.PonderWarmups++;
            }

            var thinkWatch = Stopwatch.StartNew();
            var chosen = baseline.GetBestMove(board);
            thinkWatch.Stop();
            outcome.BaselineThinkMs += thinkWatch.ElapsedMilliseconds;
            outcome.BaselineMoves++;
            return chosen;
        }

        private AIPlayer CreateContestant(EngineKind kind, PieceColor color, int seed)
        {
            switch (kind)
            {
                case EngineKind.Mcts:
                    return new MctsAI(color, seed + 101, _settings.MctsDeterminizations,
                        _settings.MctsTimeLimitMs);
                case EngineKind.Hybrid:
                    return new HybridAI(color, seed + 101,
                        _settings.HybridReviewSamples, _settings.HybridReviewDepth,
                        _settings.HybridReviewTimeLimitMs);
                case EngineKind.Predator:
                    return new PredatorAI(color, seed + 101,
                        _settings.HybridReviewSamples, _settings.HybridReviewDepth,
                        _settings.HybridReviewTimeLimitMs);
                default:
                    return new ExpectiminimaxAI(color, seed + 101,
                        _settings.ExpectiminimaxSamples, _settings.ExpectiminimaxDepth,
                        _settings.ExpectiminimaxTimeLimitMs);
            }
        }

        private static void AccumulateStats(AIPlayer contestant, GameOutcome outcome)
        {
            if (contestant is MctsAI mcts)
            {
                outcome.MctsIterations += mcts.LastIterations;
                outcome.MctsRollouts += mcts.LastRollouts;
                outcome.MctsWorlds += mcts.LastWorlds;
            }

            outcome.SearchDepthSumX10 += (long)Math.Round(contestant.LastSearchDepth * 10.0);
            outcome.QuiescenceDepthSumX10 += (long)Math.Round(contestant.LastQuiescenceDepth * 10.0);
            outcome.NodeSum += contestant.LastSearchNodes;
            outcome.StatMoves++;
        }

        private void PrintHeader(List<EngineKind> kinds)
        {
            string opponentLabel = _settings.OpponentHumanModel ? "HumanModel（人类行为模型）" : "Expectiminimax";
            Console.WriteLine("============================================================");
            Console.WriteLine("     Baseline / MCTS / Hybrid / Predator   AI 引擎对战实验");
            Console.WriteLine($"           每组 {_settings.Games} 局，全部对阵 {opponentLabel}");
            Console.WriteLine("============================================================");
            Console.WriteLine("测试配置");
            Console.WriteLine($"  测试组        : {string.Join(" → ", kinds.ConvertAll(EngineName))}");
            Console.WriteLine($"  开局库        : {(_settings.StandardOpening ? "标准国际象棋初始局面（双方一致）" : "随机暗棋初始局面")}");
            Console.WriteLine($"  随机种子      : {_settings.BaseSeed} + 局号×7919（三组共享，便于横向比较）");
            Console.WriteLine($"  MCTS          : {_settings.MctsDeterminizations} 个虚构世界 / 每步 {_settings.MctsTimeLimitMs}ms 上限");
            Console.WriteLine($"  Hybrid        : Fast Expectiminimax（无 MCTS 候选门）· {_settings.HybridReviewSamples} 采样 / 深度上限 {_settings.HybridReviewDepth} / 每步 {_settings.HybridReviewTimeLimitMs}ms");
            Console.WriteLine($"  Predator      : 同 Hybrid 的搜索核心 + 镜像信念隐匿/诱饵/猎王根节点评分 · {_settings.HybridReviewSamples} 采样 / 深度上限 {_settings.HybridReviewDepth} / 每步 {_settings.HybridReviewTimeLimitMs}ms");
            if (_settings.OpponentHumanModel)
                Console.WriteLine($"  对手模型      : HumanModel（几何推断 + 便宜子吃 + 王不轻动，不读真型）· 风格 {_settings.OpponentHumanStyle}");
            else
                Console.WriteLine($"  Baseline      : {_settings.ExpectiminimaxSamples} 采样 / 深度 {_settings.ExpectiminimaxDepth} / 每步 {_settings.ExpectiminimaxTimeLimitMs}ms 上限");
            Console.WriteLine($"  AI 并行       : {(_settings.AiParallel ? $"单局 {AIPlayer.ParallelThreads} 线程 × {_settings.ParallelGames} 局" : "关闭（批量防线程超订）")}");
            bool hasHybrid = kinds.Contains(EngineKind.Hybrid);
            bool hasPredator = kinds.Contains(EngineKind.Predator);
            if (_settings.UsePonder && (hasHybrid || hasPredator))
            {
                Console.WriteLine($"  Ponder        : 玩家回合 {_settings.PonderCandidates} 候选 × {_settings.PonderPerCandidateMs}ms 完整 EMM（后台），命中 0ms / 未命中 ≤{_settings.HybridReviewTimeLimitMs}ms");
            }
            else if (hasPredator)
            {
                Console.WriteLine("  Ponder        : 关闭（Predator 每步按镜像信念现场评分，不复用基线缓存）");
            }
            else
            {
                Console.WriteLine("  Ponder        : 关闭（对照组：每步完整搜索）");
            }
            Console.WriteLine($"  并发局数      : {_settings.ParallelGames}（CPU 核心 {Environment.ProcessorCount}）");
            Console.WriteLine($"  单局步数上限  : {_settings.MaxMoves}");
            if (_settings.OpponentNoKingExtension)
                Console.WriteLine("  王线延伸      : 基线关闭（A/B 对照）");
            if (_settings.OpponentNoConfidentSampling)
                Console.WriteLine("  确定减采样    : 基线关闭（A/B 对照）");
            if (_settings.OpponentNoLateMoveReduction)
                Console.WriteLine("  分级 LMR      : 基线关闭（A/B 对照）");
            if (_settings.ContestantNoLateMoveReduction)
                Console.WriteLine("  分级 LMR      : 被测 Predator 关闭（A/B 对照）");
            if (_settings.ContestantNoConfidentSampling)
                Console.WriteLine("  确定减采样    : 被测 Predator 关闭（A/B 对照）");

            if (_settings.SimulatePlayerProfile)
            {
                var profile = CreateAggressiveProfile();
                Console.WriteLine($"  玩家画像      : {profile.Describe()}（所有引擎共享）");
                Console.WriteLine("[MCTS] 已成功载入玩家心理画像，采样权重已倾斜。");
            }
            else
            {
                Console.WriteLine("  玩家画像      : 未载入（中性采样）");
            }
            Console.WriteLine();
        }

        // 无头模拟器不依赖 PlayerPrefs：构造一份接近“进攻倾向 99%”的历史画像。
        private static PlayerTendency CreateAggressiveProfile()
        {
            return new PlayerTendency
            {
                Games = 24,
                Wins = 15,
                Losses = 9,
                Moves = 480,
                ForwardMoves = 410,
                Captures = 96,
                CenterMoves = 300,
                FlankMoves = 80,
                DefensiveMoves = 40,
                PawnRushes = 120
            };
        }

        private void PrintBatchReport(BatchResult batch)
        {
            int wins = 0;
            int losses = 0;
            int draws = 0;
            int errors = 0;
            int invalidMoves = 0;
            int passes = 0;
            long totalMoves = 0;
            long contestantThinkMs = 0;
            long baselineThinkMs = 0;
            long iterations = 0;
            long rollouts = 0;
            long worlds = 0;
            long contestantMoveCount = 0;
            long baselineMoveCount = 0;

            long ponderWarmupMs = 0;
            int ponderWarmups = 0;
            int ponderHits = 0;
            int ponderMisses = 0;
            long hitResponseMs = 0;
            long missResponseMs = 0;
            long maxResponseMs = 0;

            long depthSumX10 = 0;
            long quiescenceSumX10 = 0;
            long nodeSum = 0;
            long statMoves = 0;

            int contestantWhiteGames = 0, contestantWhiteWins = 0;
            int contestantBlackGames = 0, contestantBlackWins = 0;

            var failures = new List<GameOutcome>();

            foreach (var o in batch.Outcomes)
            {
                if (o == null) { errors++; continue; }
                if (!string.IsNullOrEmpty(o.Error)) { errors++; failures.Add(o); continue; }

                totalMoves += o.Moves;
                contestantThinkMs += o.ContestantThinkMs;
                baselineThinkMs += o.BaselineThinkMs;
                iterations += o.MctsIterations;
                rollouts += o.MctsRollouts;
                worlds += o.MctsWorlds;
                contestantMoveCount += o.ContestantMoves;
                baselineMoveCount += o.BaselineMoves;
                invalidMoves += o.InvalidMoves;
                passes += o.Passes;

                ponderWarmupMs += o.PonderWarmupMs;
                ponderWarmups += o.PonderWarmups;
                ponderHits += o.PonderHits;
                ponderMisses += o.PonderMisses;
                hitResponseMs += o.HitResponseMs;
                missResponseMs += o.MissResponseMs;
                if (o.MaxResponseMs > maxResponseMs) maxResponseMs = o.MaxResponseMs;

                depthSumX10 += o.SearchDepthSumX10;
                quiescenceSumX10 += o.QuiescenceDepthSumX10;
                nodeSum += o.NodeSum;
                statMoves += o.StatMoves;

                bool isWhite = o.ContestantColor == PieceColor.White;
                if (isWhite) contestantWhiteGames++; else contestantBlackGames++;

                if (o.Winner.HasValue)
                {
                    if (o.Winner.Value == o.ContestantColor)
                    {
                        wins++;
                        if (isWhite) contestantWhiteWins++; else contestantBlackWins++;
                    }
                    else
                    {
                        losses++;
                    }
                }
                else
                {
                    draws++;
                }
            }

            int validGames = Math.Max(1, batch.Outcomes.Length - errors);
            double winRate = 100.0 * wins / validGames;
            double lossRate = 100.0 * losses / validGames;
            double drawRate = 100.0 * draws / validGames;
            double avgMoves = totalMoves / (double)validGames;
            double avgThink = contestantMoveCount > 0 ? contestantThinkMs / (double)contestantMoveCount : 0;
            double avgBaseline = baselineMoveCount > 0 ? baselineThinkMs / (double)baselineMoveCount : 0;

            string name = EngineName(batch.Kind);
            string modelLabel = _settings.OpponentHumanModel ? "HumanModel" : "Expectiminimax";
            string opponentName = batch.Kind == EngineKind.Baseline ? $"对手 {modelLabel}" : modelLabel;
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine($"      {name}  vs  {modelLabel}   结果");
            Console.WriteLine("============================================================");
            Console.WriteLine($"  有效对局        : {validGames} / {batch.Outcomes.Length}");
            Console.WriteLine($"  {name,-16} 胜 : {wins,3} 局    ({winRate,5:F1}%)");
            Console.WriteLine($"  {opponentName,-16} 胜 : {losses,3} 局    ({lossRate,5:F1}%)");
            Console.WriteLine($"  平局 / 超步数    : {draws,3} 局    ({drawRate,5:F1}%)");
            Console.WriteLine();
            Console.WriteLine("  先手 / 后手拆分");
            Console.WriteLine($"    {name,-16} 执白 : {contestantWhiteWins}/{contestantWhiteGames} 胜  ({Percent(contestantWhiteWins, contestantWhiteGames)})");
            Console.WriteLine($"    {name,-16} 执黑 : {contestantBlackWins}/{contestantBlackGames} 胜  ({Percent(contestantBlackWins, contestantBlackGames)})");
            Console.WriteLine();
            Console.WriteLine("  效率统计");
            Console.WriteLine($"    平均每局步数      : {avgMoves:F1} 步");
            Console.WriteLine($"    {name,-16} 平均耗时 : {avgThink:F1} ms/步（共 {contestantMoveCount} 步）");
            Console.WriteLine($"    {modelLabel,-12} 平均耗时 : {avgBaseline:F1} ms/步（共 {baselineMoveCount} 步）");
            if (contestantMoveCount > 0 && iterations > 0)
            {
                Console.WriteLine($"    MCTS 平均迭代     : {iterations / (double)contestantMoveCount:F0} 次/步，rollout {rollouts / (double)contestantMoveCount:F0} 次/步，世界 {worlds / (double)contestantMoveCount:F0} 个/步");
            }

            // 深度画像：新 EMM 的 PVS + 静态搜索收益，按每步实际完成的迭代加深层数统计。
            if (statMoves > 0 && batch.Kind != EngineKind.Mcts)
            {
                double avgDepth = depthSumX10 / (10.0 * statMoves);
                double avgQuiescence = quiescenceSumX10 / (10.0 * statMoves);
                double avgNodes = nodeSum / (double)statMoves;
                Console.WriteLine();
                Console.WriteLine("  搜索深度画像");
                Console.WriteLine($"    平均主搜索深度    : {avgDepth:F1} 层（迭代加深完成层数）");
                Console.WriteLine($"    平均静态延伸      : {avgQuiescence:F1} 层（Quiescence Search 额外推进）");
                Console.WriteLine($"    平均节点数        : {avgNodes:F0} 节点/步（含静态搜索）");
            }

            int ponderSamples = ponderHits + ponderMisses;
            if (ponderSamples > 0)
            {
                double avgWarmup = ponderWarmups > 0 ? ponderWarmupMs / (double)ponderWarmups : 0;
                double hitRate = 100.0 * ponderHits / ponderSamples;
                double avgHit = ponderHits > 0 ? hitResponseMs / (double)ponderHits : 0;
                double avgMiss = ponderMisses > 0 ? missResponseMs / (double)ponderMisses : 0;
                double avgWait = (hitResponseMs + missResponseMs) / (double)ponderSamples;

                Console.WriteLine();
                Console.WriteLine("  Ponder 预判");
                Console.WriteLine($"    预热次数          : {ponderWarmups} 次，平均 {avgWarmup:F0} ms/次（后台窗口，不计入等待）");
                Console.WriteLine($"    命中率            : {ponderHits}/{ponderSamples} 步 ({hitRate:F1}%)");
                Console.WriteLine($"    Ponder 命中响应   : {avgHit:F0} ms/步（{ponderHits} 步）");
                Console.WriteLine($"    Ponder 未命中响应 : {avgMiss:F0} ms/步（{ponderMisses} 步）");
                Console.WriteLine($"    理论出招等待      : {avgWait:F0} ms/步（上限 {_settings.HybridReviewTimeLimitMs + 100}ms，实测最大 {maxResponseMs} ms）");
            }
            Console.WriteLine($"    总耗时            : {batch.Elapsed.TotalSeconds:F1} s（并发 {_settings.ParallelGames}）");
            Console.WriteLine($"    跳过回合          : {passes} 次");
            Console.WriteLine($"    非法走法纠正      : {invalidMoves} 次");

            if (errors > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  异常对局          : {errors} 局");
                for (int i = 0; i < failures.Count && i < 10; i++)
                    Console.WriteLine($"    - #{failures[i].Index}: {failures[i].Error}");
            }

            if (_settings.PrintOutcomes)
            {
                Console.WriteLine();
                Console.WriteLine("  逐局结果（#局号 我方颜色 胜者 步数 结束原因）");
                foreach (var o in batch.Outcomes)
                {
                    if (o == null) continue;
                    string winner = (!o.Winner.HasValue) ? "和" : (o.Winner.Value == o.ContestantColor ? "胜" : "负");
                    string reason = string.IsNullOrEmpty(o.Error) ? (o.EndReason ?? "超步数") : "异常";
                    Console.WriteLine($"    #{o.Index,2} {(o.ContestantColor == PieceColor.White ? "白" : "黑")} {winner} {o.Moves,3}步 {reason}");
                }
            }

            Console.WriteLine("============================================================");
        }

        private void PrintComparison(List<BatchResult> batches)
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("                    三 方 对 比 汇 总");
            Console.WriteLine("============================================================");
            Console.WriteLine("  引擎              胜/负/平          胜率       平均步数   平均耗时/步");
            Console.WriteLine("  ----------------------------------------------------------");

            foreach (var batch in batches)
            {
                int wins = 0, losses = 0, draws = 0, errors = 0;
                long moves = 0;
                long thinkMs = 0;
                long moveCount = 0;

                foreach (var o in batch.Outcomes)
                {
                    if (o == null || !string.IsNullOrEmpty(o.Error)) { errors++; continue; }
                    moves += o.Moves;
                    thinkMs += o.ContestantThinkMs;
                    moveCount += o.ContestantMoves;
                    if (!o.Winner.HasValue) draws++;
                    else if (o.Winner.Value == o.ContestantColor) wins++;
                    else losses++;
                }

                int valid = Math.Max(1, batch.Outcomes.Length - errors);
                double rate = 100.0 * wins / valid;
                double avgMoves = moves / (double)valid;
                double avgThink = moveCount > 0 ? thinkMs / (double)moveCount : 0;

                string name = batch.Kind == EngineKind.Baseline
                    ? "Baseline（镜像）"
                    : batch.Kind == EngineKind.Mcts ? "纯 MCTS"
                    : batch.Kind == EngineKind.Predator ? "Predator" : "Hybrid";

                Console.WriteLine($"  {name,-16}  {wins,3}/{losses,3}/{draws,3}        {rate,6:F1}%    {avgMoves,6:F1}     {avgThink,7:F1} ms");
            }

            Console.WriteLine("  ----------------------------------------------------------");
            Console.WriteLine($"  每组 {_settings.Games} 局；MCTS {_settings.MctsTimeLimitMs}ms/{_settings.MctsDeterminizations} 世界；" +
                              $"Fast EMM / Predator = {_settings.HybridReviewSamples} 采样 / 深度上限 {_settings.HybridReviewDepth} / 每步 {_settings.HybridReviewTimeLimitMs}ms；" +
                              $"Baseline {_settings.ExpectiminimaxTimeLimitMs}ms。");
            Console.WriteLine("  说明：Baseline 行为镜像对照，理论胜率 50%；Fast EMM 与 Baseline 的差值即新搜索优化的净收益。");
            Console.WriteLine("============================================================");
        }

        private static string Percent(int part, int total)
        {
            if (total <= 0) return "-";
            return $"{100.0 * part / total:F1}%";
        }

        private static string EngineName(EngineKind kind)
        {
            switch (kind)
            {
                case EngineKind.Mcts: return "MCTS";
                case EngineKind.Hybrid: return "Hybrid";
                case EngineKind.Predator: return "Predator";
                default: return "Expectiminimax";
            }
        }

        private static PieceColor OpponentColor(PieceColor color)
        {
            return color == PieceColor.White ? PieceColor.Black : PieceColor.White;
        }
    }
}
