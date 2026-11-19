using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // 融合方案 A+C：彻底放弃 MCTS 候选门。
    // Ponder 后台对玩家 3–5 个预测落点直接跑完整 1400ms Expectiminimax 并缓存答案；
    // 命中则 0ms 取用，未命中现场全宽搜索，同样 1400ms 封顶。
    public class HybridAI : AIPlayer
    {
        public const int FastResponseMs = 1400;

        protected readonly ExpectiminimaxAI _reviewer;
        private readonly int _sampleCount;
        private readonly int _searchDepth;
        private readonly int _baseBudgetMs;

        public int SampleCount => _sampleCount;
        public int SearchDepth => _searchDepth;

        // 无头 A/B 用：把评审引擎的两个搜索优化开关透传出去，便于隔离各自贡献。
        public bool LateMoveReductionEnabled
        {
            get => _reviewer.UseLateMoveReduction;
            set => _reviewer.UseLateMoveReduction = value;
        }

        public bool ConfidentSamplingEnabled
        {
            get => _reviewer.UseConfidentSampling;
            set => _reviewer.UseConfidentSampling = value;
        }

        // 报表兼容字段：新架构不再含 MCTS，三项恒为 0。
        public int LastIterations { get; private set; }
        public int LastRollouts { get; private set; }
        public int LastWorlds { get; private set; }
        public int LastReviewedMoves { get; private set; }
        public Move? LastChosenMove { get; private set; }
        public long LastResponseMs { get; private set; }
        public bool LastPonderHit { get; private set; }
        public int LastBudgetMs { get; private set; } = FastResponseMs;

        private bool _lastEndgame;
        private int _lastTargetDepth = 1;
        private int _lastSamples;
        private double _lastNps;
        private double _lastThinkMs;

        public bool LastEndgame => _lastEndgame;
        public int LastTargetDepth => _lastTargetDepth;
        public int LastEffectiveSamples => _lastSamples;
        public double LastNodesPerSecond => _lastNps;
        public double LastThinkMs => _lastThinkMs;

        public int PonderRequests { get; private set; }
        public int PonderHits { get; private set; }
        public double PonderHitRate => PonderRequests <= 0 ? 0.0 : PonderHits / (double)PonderRequests;

        public HybridAI(PieceColor color, int seed = 42, int reviewSamples = 64,
            int reviewDepth = 3, int reviewTimeLimitMs = FastResponseMs)
            : base(color, seed, reviewTimeLimitMs)
        {
            _sampleCount = Math.Max(1, reviewSamples);
            _searchDepth = Math.Max(1, reviewDepth);
            _baseBudgetMs = Math.Max(150, reviewTimeLimitMs);
            _reviewer = new ExpectiminimaxAI(color, seed + 97, _sampleCount, _searchDepth,
                _baseBudgetMs);
            _reviewer.AttachBeliefState(_beliefState);
            _reviewer.OpeningPlanSource = this;
            _reviewer.SearchTimeBudgetMs = AIPlayer.MobileProfile
                ? AIPlayer.MobileTimeBudgetMs
                : _baseBudgetMs;
        }

        public override void SetPlayerTendency(PlayerTendency tendency, bool announce = true)
        {
            base.SetPlayerTendency(tendency, announce);
            _reviewer.SetPlayerTendency(tendency, false);
        }

        public override void ResetAfterRollback(BeliefState restoredBelief)
        {
            base.ResetAfterRollback(restoredBelief);
            _reviewer.ResetAfterRollback(restoredBelief);
            LastPonderHit = false;
            LastChosenMove = null;
            LastResponseMs = 0;
        }

        public override AISearchSession CreateSearchSession(Board board)
        {
            return CreateSearchSession(board, null);
        }

        public override AISearchSession CreateSearchSession(Board board, PonderResult ponder)
        {
            _beliefState.EnsureInitialized(board);
            _reviewer.AttachBeliefState(_beliefState);
            _reviewer._movesSinceLastCapture = _movesSinceLastCapture;
            _reviewer._lastOpponentTo = _lastOpponentTo;
            _reviewer._lastOwnMove = _lastOwnMove;
            // 三次重复判负是宿主规则，复核器自己看不到对局历史，必须每回合转发一次。
            _reviewer.WouldCauseThreefold = WouldCauseThreefold;

            // 绝杀快速通道：优先于 Ponder 缓存与一切搜索——能吃王就立刻吃，0ms 出手。
            var instantKill = FindImmediateKingCapture(board);
            if (instantKill.HasValue)
            {
                LastPonderHit = false;
                LastChosenMove = instantKill.Value;
                LastResponseMs = 0;
                LastReviewedMoves = 0;
                LastIterations = 0;
                LastRollouts = 0;
                LastWorlds = 0;
                LastSearchDepth = 1;
                LastQuiescenceDepth = 0;
                LastSearchNodes = 0;
                LastBudgetMs = 0;
                _lastEndgame = false;
                _lastTargetDepth = 1;
                _lastSamples = 0;
                _lastNps = 0;
                _lastThinkMs = 0;
                Log($"[Hybrid] 绝杀快速通道：最可能王位 {instantKill.Value.To} 可直接吃，立即 {instantKill.Value}（0ms，跳过 Ponder 与搜索）。");
                return new AIPlayer.ImmediateMoveSession(instantKill.Value);
            }

            PrepareReviewer(board);

            // 真王被真威胁时不信任旧缓存：危险局面的最佳手会随对方一手完全改变，
            // 现场深算比 0ms 取一个几层前的答案更可靠。
            double kingDanger = ponder != null
                ? _reviewer.KingDangerScore(_reviewer.BuildFairCheckBoard(board))
                : 0.0;
            bool urgentKing = kingDanger > ExpectiminimaxAI.KingSafetyUrgentDanger;

            if (ponder != null && urgentKing)
                Log($"[Hybrid] 真王受威胁（危险度 {kingDanger:F2}），忽略 Ponder 缓存，改为现场深算。");

            if (ponder != null && !urgentKing)
            {
                PonderRequests++;
                // Ponder 缓存也必须过王安全红线：宁可多花 1400ms，也不能直接取一个送王的答案。
                if (ponder.BestMove.HasValue && AcceptPonderAnswer(board, ponder.BestMove.Value))
                {
                    PonderHits++;
                    LastPonderHit = true;
                    return new CachedAnswerSession(this, ponder);
                }

                Log($"[Hybrid] Ponder 缓存答案 {ponder.BestMove} 触发王安全红线，丢弃缓存改为现场搜索。");
            }

            LastPonderHit = false;
            int budget = ComputeBudgetMs(board);
            LastBudgetMs = budget;
            _reviewer.SearchTimeBudgetMs = budget;
            return new LiveSession(this, budget, _reviewer.CreateSearchSession(board));
        }

        // 复核器每次现场搜索前的注入点：Predator 用它挂上镜像信念的根节点评分。
        protected virtual void PrepareReviewer(Board board)
        {
        }

        // Ponder 缓存能否直接使用：默认只过王安全红线；Predator 还会否决会泄漏真王的答案。
        protected virtual bool AcceptPonderAnswer(Board board, Move move)
        {
            if (WouldCauseThreefold != null && WouldCauseThreefold(move))
            {
                Log($"[Hybrid] Ponder 答案 {move} 会构成第三次重复（走出者判负），丢弃缓存改为现场搜索。");
                return false;
            }

            return _reviewer.IsMoveKingSafe(board, move);
        }

        // 深度完全由时间换：残局给足预算做 10~12 层穷举；正常 1400ms；
        // 落后或真王被咬时加时，让迭代加深尽量多完成一层去找玩家的失误。
        // 移动端统一压到 900ms 以内，避免单个回合把手机卡死。
        private int ComputeBudgetMs(Board board)
        {
            int budget;
            if (_reviewer.IsEndgamePosition(board))
            {
                budget = ExpectiminimaxAI.EndgameTimeBudgetMs;
            }
            else
            {
                bool kingExposed = _reviewer.IsOwnKingCapturablePessimistic(
                    _reviewer.BuildFairCheckBoard(board), out _);
                if (kingExposed)
                {
                    budget = (int)Math.Round(_baseBudgetMs * 1.9);
                }
                else
                {
                    int lead = _reviewer.GetMaterialLead(board);
                    if (lead <= -600) budget = (int)Math.Round(_baseBudgetMs * 1.7);
                    else if (lead < 0) budget = (int)Math.Round(_baseBudgetMs * 1.3);
                    else budget = _baseBudgetMs;
                }
            }

            if (AIPlayer.MobileProfile) budget = Math.Min(budget, AIPlayer.MobileTimeBudgetMs);
            return budget;
        }

        // 2 层浅搜的盲区只在战术窗口内致命：刚发生吃子时，第 3 层才看得到“吃回/被吃回”的两步交换。
        // 非战术窗口不加时——全局加时会把每步都拖长，整局轨迹改变后反而出现消极和棋。
        private const int ShallowSearchCaptureWindow = 4;

        private bool ShouldExtendShallowSearch()
        {
            return !AIPlayer.MobileProfile && _movesSinceLastCapture <= ShallowSearchCaptureWindow;
        }

        private void ExtendSearchBudget(int budget, int completedDepth)
        {
            int target = Math.Min((int)Math.Round(budget * 1.8), ExpectiminimaxAI.EndgameTimeBudgetMs);
            if (target <= budget) return;
            LastBudgetMs = target;
            _reviewer.SearchTimeBudgetMs = target;
            Log($"[Hybrid] 浅层保护（吃子窗口 {_movesSinceLastCapture} 手）：{budget}ms 内只完成 {completedDepth} 层，" +
                $"预算追加到 {target}ms 继续搜索。");
        }

        private void RecordCached(PonderResult ponder)
        {
            LastChosenMove = ponder.BestMove;
            LastResponseMs = 0;
            LastReviewedMoves = 0;
            LastIterations = 0;
            LastRollouts = 0;
            LastWorlds = 0;
            LastSearchDepth = Math.Max(1, ponder.SearchDepth);
            LastQuiescenceDepth = ponder.QuiescenceDepth;
            LastSearchNodes = ponder.Nodes;
            _lastEndgame = ponder.EndgameMode;
            _lastTargetDepth = Math.Max(1, ponder.TargetDepth);
            _lastSamples = Math.Max(1, ponder.EffectiveSamples);
            _lastNps = ponder.NodesPerSecond;
            _lastThinkMs = ponder.ThinkMs;

            Log($"[Hybrid] Ponder 命中：直接取缓存答案 {ponder.BestMove}（主深度 {LastSearchDepth} 层，" +
                $"静态延伸 {LastQuiescenceDepth:F1} 层，节点 {LastSearchNodes}，0ms 出招）。");
        }

        private void RecordLive(Move? move, long elapsedMs)
        {
            LastChosenMove = move;
            LastResponseMs = elapsedMs;
            LastReviewedMoves = 0;
            LastIterations = 0;
            LastRollouts = 0;
            LastWorlds = 0;
            LastSearchDepth = Math.Max(1, _reviewer.LastSearchDepth);
            LastQuiescenceDepth = _reviewer.LastQuiescenceDepth;
            LastSearchNodes = _reviewer.LastSearchNodes;
            _lastEndgame = _reviewer.LastSearchWasEndgame;
            _lastTargetDepth = _reviewer.LastTargetDepth;
            _lastSamples = _reviewer.LastEffectiveSamples;
            _lastNps = _reviewer.LastNodesPerSecond;
            _lastThinkMs = elapsedMs;

            Log($"[Hybrid] Ponder 未命中：现场 {LastBudgetMs}ms 全宽 Expectiminimax → {move}（主深度 {LastSearchDepth} 层，" +
                $"静态延伸 {LastQuiescenceDepth:F1} 层，节点 {LastSearchNodes}，响应 {LastResponseMs}ms）。");
        }

        // 缓存答案路径：搜索已在后台完成，这里只做一次 O(1) 交接。
        private sealed class CachedAnswerSession : AISearchSession
        {
            private readonly HybridAI _owner;
            private readonly PonderResult _ponder;

            internal CachedAnswerSession(HybridAI owner, PonderResult ponder)
            {
                _owner = owner;
                _ponder = ponder;
                _owner.RecordCached(ponder);
            }

            public override bool IsFinished => true;

            public override Move? Result => _ponder.BestMove;

            public override bool Advance(int sliceMs) => true;
        }

        // 未命中路径：完整的 1400ms 全宽搜索，不再受 MCTS 视野限制。
        private sealed class LiveSession : AISearchSession
        {
            private readonly HybridAI _owner;
            private readonly int _budget;
            private readonly AISearchSession _inner;
            private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
            private bool _extended;
            private bool _recorded;

            internal LiveSession(HybridAI owner, int budget, AISearchSession inner)
            {
                _owner = owner;
                _budget = budget;
                _inner = inner;
                _watch.Start();
                if (_inner.IsFinished) Record();
            }

            public override bool IsFinished => _inner.IsFinished;

            public override Move? Result => _inner.Result;

            public override int CompletedDepth => _inner.CompletedDepth;

            // 宿主可能一次要求推进 200ms，而内层在跨过预算的当次推进里就会自行超时收尾；
            // 只用整个 slice 推进，90% 检查点会落在内层已结束后，永远没有机会加时。
            // 这里把 slice 切成小段转发，保证检查点在内层仍存活时被反复求值。
            private const int ExtensionCheckSliceMs = 24;

            public override bool Advance(int sliceMs)
            {
                long entry = _watch.ElapsedMilliseconds;
                do
                {
                    if (!_extended && _inner.CompletedDepth <= 2 &&
                        _watch.ElapsedMilliseconds >= _budget * 9L / 10L &&
                        _owner.ShouldExtendShallowSearch())
                    {
                        _extended = true;
                        _owner.ExtendSearchBudget(_budget, _inner.CompletedDepth);
                    }

                    if (_inner.Advance(Math.Min(sliceMs, ExtensionCheckSliceMs)))
                    {
                        Record();
                        return true;
                    }
                }
                while (_watch.ElapsedMilliseconds - entry < sliceMs);

                return false;
            }

            private void Record()
            {
                if (_recorded) return;
                _recorded = true;
                _watch.Stop();
                _owner.RecordLive(_inner.Result, _watch.ElapsedMilliseconds);
            }
        }
    }
}
