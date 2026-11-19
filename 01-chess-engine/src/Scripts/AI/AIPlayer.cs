using System;
using System.Collections.Generic;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    public enum AIEngineType
    {
        Expectiminimax,
        MCTS,
        Hybrid,
        Predator
    }

    // 根节点候选统计：供 Hybrid 的 Top-K 复核使用。
    public struct RootMoveStat
    {
        public Move Move;
        public int Visits;
        public double WinRate;
        public double WorldRate;
        public double Rank;
    }

    // AI 引擎基类：负责信念状态、观测通知与平台算力开关；具体搜索由派生引擎实现。
    public enum OpeningTactic
    {
        None,
        BaitDeep,
        ExchangeSimplify,
        SpacePressure
    }

    public abstract class AIPlayer
    {
        public static Action<string> Logger = Console.WriteLine;

        // WebGL/演示版强制单线程；桌面完整版允许按采样并行。
        public static bool EnableParallelSearch = DefaultParallelSearch();

        // 实机并行线程数：桌面完整版按 CPU 核心数取，封顶 12 线程（本机 12 逻辑核）。
        public static int ParallelThreads = DefaultParallelThreads();

        // 多疑惩罚（Anti-Bluffing）总开关：用于 A/B 对照，实机默认开启。
        public static bool EnableBaitPenalty = true;

        // 移动端算力保护：由 GameManager 按 Application.platform 置位。
        // Android 上采样 6、深度 4、每步 900ms，并强制单线程，避免手机过热或卡死。
        public static bool MobileProfile = false;
        public const int MobileSampleCount = 6;
        public const int MobileSearchDepth = 4;
        public const int MobileTimeBudgetMs = 900;

        // 绝杀优先级：P_King 超过该阈值即按“可能是真王”处理，吃王/攻王压过一切物质诱惑。
        // 数值来自实战事故复盘：吃王只给 500、吃后给 900，AI 会为了一个后放过绝杀。
        // 20% 的猜测本身保留收益（50 局对照 0.5 门槛反而少赢 6 局），
        // 但绝杀快速通道必须过“反吃红线”，不能猜错一次就把真王留在对手嘴里。
        internal const float KingKillProbabilityThreshold = 0.2f;
        internal const int KingKillPrize = 999999;
        internal const int KingAttackPrize = 900000;

        // Ponder 线程里要跑完整的观测/搜索日志链，用线程级静音避免字符串拼装和 Unity 跨线程输出。
        [ThreadStatic] internal static bool MuteCurrentThread;

        internal readonly PieceColor _color;
        internal BeliefState _beliefState;
        internal readonly Random _rng;
        internal readonly int _timeLimitMs;
        internal int _movesSinceLastCapture;

        // 玩家最后一步的落点：只有“刚主动送到嘴边”的孤子才按诱饵多疑处理。
        internal Position? _lastOpponentTo;

        // AI 自己上一步：用于通用地惩罚“同一子来回挪/原地转圈”，与玩家风格无关。
        internal Move? _lastOwnMove;

        // 宿主注入的规则查询：这步走完是否构成第三次重复（走出者判负）。
        // 搜索本身看不到对局历史，必须由 GameEngine/模拟器补这条信息，否则残局会把和棋下成判负。
        public Func<Move, bool> WouldCauseThreefold;

        // 开局前三步战术：阵型在布阵阶段锁定，这里只切换应对策略，绝不重排棋子。
        public OpeningFormation LockedFormation { get; private set; }
        public OpeningTactic CurrentOpeningTactic { get; private set; }
        public int OwnMoveCount { get; private set; }
        public int OpponentMoveCount { get; private set; }

        // Hybrid 的组合引擎不重复观测，只共享主引擎的开局计数器。
        internal AIPlayer OpeningPlanSource;

        private bool _openingPlanActive;
        private int _openingForward;
        private int _openingCaptures;
        private int _openingBack;

        // 每步实际可用预算：正常思考取构造时的时限，Ponder 快路径/复核会临时调低。
        public int SearchTimeBudgetMs { get; internal set; }

        // 最近一次搜索的深度画像：主迭代层数、静态搜索平均延伸、节点数（供报表统计）。
        public int LastSearchDepth { get; internal set; } = 1;
        public double LastQuiescenceDepth { get; internal set; }
        public long LastSearchNodes { get; internal set; }

        protected AIPlayer(PieceColor color, int seed, int timeLimitMs)
        {
            _color = color;
            _beliefState = new BeliefState(color);
            _rng = new Random(seed);
            _timeLimitMs = Math.Max(200, timeLimitMs);
            SearchTimeBudgetMs = _timeLimitMs;
            _movesSinceLastCapture = 0;
        }

        public PieceColor Color => _color;

        // 跨局玩家画像：只含玩家自己的走法统计，不含本局隐藏本态。
        public PlayerTendency PlayerProfile { get; private set; }

        public virtual void SetPlayerTendency(PlayerTendency tendency, bool announce = true)
        {
            PlayerProfile = tendency;
            if (announce && tendency != null && tendency.HasHistory)
                Log($"[AI] 玩家心理画像已载入：{tendency.Describe()}");
        }

        public BeliefState GetBeliefState() => _beliefState;

        // 标准开局里对手的伪装标签是诚实的（升变除外）：给可见标签 0.85 先验，
        // 清掉“每个敌子都可能是后/车/王”的采样幻觉。乱序布阵/自定义布阵传 0。
        public void SetVisibleLabelTrust(float trust)
        {
            _beliefState.VisibleLabelTrust = Math.Max(0f, Math.Min(0.95f, trust));
        }

        // 悔棋快照：AI 私有信念与引擎侧信念不是同一份，必须单独保存/恢复。
        public BeliefState CaptureBeliefState() => _beliefState.Clone();

        // 回滚后彻底清空当前搜索上下文；派生引擎若有额外缓存需覆写。
        public virtual void ResetAfterRollback(BeliefState restoredBelief)
        {
            if (restoredBelief != null) _beliefState.CopyFrom(restoredBelief);
            _movesSinceLastCapture = 0;
            _lastOpponentTo = null;
            ResetOpeningPlanCounters();
        }

        // Hybrid 组合引擎共享同一份信念，避免子引擎各记一套导致观测不同步。
        internal void AttachBeliefState(BeliefState belief)
        {
            if (belief != null) _beliefState = belief;
        }

        public abstract AISearchSession CreateSearchSession(Board board);

        // 带 Ponder 缓存的入口：默认忽略缓存，支持缓存的引擎（Hybrid）走极速复核。
        public virtual AISearchSession CreateSearchSession(Board board, PonderResult ponder)
        {
            return CreateSearchSession(board);
        }

        // 同步入口保留给控制台模拟器；Unity 里请用 CreateSearchSession + 协程分帧推进。
        public Move? GetBestMove(Board board)
        {
            var instantKill = FindImmediateKingCapture(board);
            if (instantKill.HasValue)
            {
                Log($"[AI] 绝杀快速通道：直接吃掉最可能的王位 {instantKill.Value.To}，跳过全部搜索。");
                return instantKill.Value;
            }

            var session = CreateSearchSession(board);
            while (!session.IsFinished)
                session.Advance(200);
            return session.Result;
        }

        // 绝杀通道的自我保险：反吃红线 + 便宜子试探。
        // HumanModel 基准对手覆写为 false，固定成改造前的激进策略，
        // 否则同一份代码会把对手一起改强，50 局 A/B 无法归因到被测引擎。
        internal virtual bool InstantKillSafetyEnabled => true;

        // 绝杀快速通道：遍历全部合法走法，只用信念概率（绝不读取玩家真实本态）找“可能的真王”。
        // 只要存在一步吃掉 P_King > 20% 目标的走法，就直接返回概率最高的那一步；
        // 吃王立即终局，任何期望搜索/MCTS 模拟都是浪费。
        internal Move? FindImmediateKingCapture(Board board)
        {
            if (board == null) return null;

            _beliefState.EnsureInitialized(board);
            var moves = MoveGenerator.GeneratePseudoLegalMoves(board, _color);
            Move? best = null;
            float bestProb = KingKillProbabilityThreshold;
            int bestValue = -1;

            for (int i = 0; i < moves.Count; i++)
            {
                var target = board.GetPiece(moves[i].To);
                if (target == null || target.Color == _color) continue;

                float pKing = _beliefState.GetProbability(moves[i].To, PieceType.King);
                if (pKing <= KingKillProbabilityThreshold) continue;

                if (InstantKillSafetyEnabled)
                {
                    // 不是十分确定时，先保证吃子后真王不会被对手直接反吃；
                    // 否则猜错一次就直接终局，连搜索补救的机会都没有。
                    if (pKing < 0.9f && WouldLoseOwnKing(board, moves[i])) continue;

                    // 猜王不能顺手丢己方真后/真车：G0 复盘里 1/3 概率的赌王，
                    // 吃了伪装象却放任对方马吃掉 a1 的真后，就算赌赢也不够本。
                    if (pKing < 0.9f && WouldHangOwnHeavy(board, moves[i]))
                    {
                        Log($"[AI] 绝杀通道放弃 {moves[i]}：P_King={pKing:F2} 且会暴露己方重子，改走完整搜索。");
                        continue;
                    }

                    // 低置信度的“可能是王”只用便宜子去试：后/车/王拿去赌伪装兵，
                    // 猜错就是白送一个大子（50 局复盘里被对手真后反吃）。
                    // 最坏也只是一次等价交换，符合人类“用不大于象的子探王”的直觉。
                    if (pKing < 0.8f)
                    {
                        var attacker = board.GetPiece(moves[i].From);
                        if (attacker != null && attacker.TrueType.GetValue() > 330) continue;
                    }
                }

                int value = target.VisibleType.GetValue();
                // 概率更高优先；概率打平时吃面值更大的目标，避免在均势猜测里选亏。
                if (pKing > bestProb + 1e-4f || (Math.Abs(pKing - bestProb) <= 1e-4f && value > bestValue))
                {
                    best = moves[i];
                    bestProb = pKing;
                    bestValue = value;
                }
            }
            return best;
        }

        // 立即完成的分帧会话：把“绝杀快速通道”的答案交给 Unity 的 Advance 流程。
        internal sealed class ImmediateMoveSession : AISearchSession
        {
            private readonly Move _move;

            internal ImmediateMoveSession(Move move)
            {
                _move = move;
            }

            public override bool IsFinished => true;

            public override Move? Result => _move;

            public override bool Advance(int sliceMs) => true;
        }

        // 对手本态永远不进入 AI：此入口只按棋盘上的伪装态初始化概率分布。
        public virtual void ObserveGameStart(Board board)
        {
            _beliefState.ResetFromBoard(board, knownTruth: false);
            _lastOpponentTo = null;
            _lastOwnMove = null;
            CurrentOpeningTactic = OpeningTactic.None;
            ResetOpeningPlanCounters();
            int tracked = 0;
            foreach (var _ in board.GetPiecesOfColor(OpponentColor(_color)))
                tracked++;
            Log($"[AI] 开局信念初始化完成：对手 {tracked} 个棋子进入概率追踪。");
        }

        public virtual void NotifyOwnMove(Move move, bool wasCapture, Board boardBefore = null)
        {
            _movesSinceLastCapture = wasCapture ? 0 : _movesSinceLastCapture + 1;
            _lastOwnMove = move;
            OwnMoveCount++;
        }

        // 布阵阶段结束后调用一次：此时阵型已经 ApplyFormation 锁定，后续只切换战术，不再动棋子。
        public void BeginOpeningPlan(OpeningFormation formation)
        {
            LockedFormation = formation;
            _openingPlanActive = true;
            _lastOwnMove = null;
            OwnMoveCount = 0;
            OpponentMoveCount = 0;
            _openingForward = 0;
            _openingCaptures = 0;
            _openingBack = 0;
            CurrentOpeningTactic = OpeningTactic.None;
        }

        private void ResetOpeningPlanCounters()
        {
            OwnMoveCount = 0;
            OpponentMoveCount = 0;
            _openingForward = 0;
            _openingCaptures = 0;
            _openingBack = 0;
            _openingPlanActive = false;
        }

        // 只统计玩家前 3 步的公开信息：前进/后退/吃子，决定 AI 的应对套路。
        private void ObserveOpeningMove(Board boardBefore, Move move)
        {
            if (!_openingPlanActive || OpponentMoveCount >= 3) return;
            OpponentMoveCount++;

            PieceColor playerColor = OpponentColor(_color);
            int direction = playerColor == PieceColor.White ? 1 : -1;
            int advance = (move.To.Y - move.From.Y) * direction;

            if (advance > 0) _openingForward++;
            else if (advance < 0) _openingBack++;
            if (boardBefore != null && boardBefore.GetPiece(move.To) != null) _openingCaptures++;

            float aggression = PlayerProfile != null ? PlayerProfile.Aggression : 0f;
            if (_openingCaptures > 0 || _openingForward >= 2)
                CurrentOpeningTactic = aggression >= 0.55f ? OpeningTactic.BaitDeep : OpeningTactic.ExchangeSimplify;
            else if (_openingBack > 0 || (_openingForward == 0 && OpponentMoveCount >= 2))
                CurrentOpeningTactic = OpeningTactic.SpacePressure;
            else
                CurrentOpeningTactic = OpeningTactic.None;

            Log($"[AI] 开局战术：玩家前{OpponentMoveCount}步（前进{_openingForward} 后退{_openingBack} 吃子{_openingCaptures}）" +
                $"→ {TacticName(CurrentOpeningTactic)}，阵型保持锁定不换。");
        }

        private static string TacticName(OpeningTactic tactic)
        {
            switch (tactic)
            {
                case OpeningTactic.BaitDeep: return "诱敌深入";
                case OpeningTactic.ExchangeSimplify: return "兑子简化";
                case OpeningTactic.SpacePressure: return "空间压迫";
                default: return "常规";
            }
        }

        // 只在前 3 步生效的轻量偏置：不改变搜索结论，只在同分附近给战术方向让路。
        internal int GetOpeningPlanBonus(Board board, Move move)
        {
            var source = OpeningPlanSource ?? this;
            return source.ComputeOpeningPlanBonus(board, move);
        }

        private int ComputeOpeningPlanBonus(Board board, Move move)
        {
            if (!_openingPlanActive || OwnMoveCount >= 3 || CurrentOpeningTactic == OpeningTactic.None) return 0;

            var moving = board.GetPiece(move.From);
            if (moving == null) return 0;
            var target = board.GetPiece(move.To);
            bool capture = target != null && target.Color != _color;
            int advance = (move.To.Y - move.From.Y) * _colorDirection;
            int centerDistance = Math.Abs(move.To.X * 2 - 7) + Math.Abs(move.To.Y * 2 - 7);

            switch (CurrentOpeningTactic)
            {
                case OpeningTactic.BaitDeep:
                    // 让出空间，把玩家放进来，再用搜索确认过的反吃收网。
                    return (advance <= 0 ? 45 : 0) + (capture ? 25 : 0) +
                           (centerDistance <= 3 ? 10 : 0);

                case OpeningTactic.ExchangeSimplify:
                    // 龟缩/对攻都可以用等值交换简化；不等值交换仍交给搜索判断。
                    if (!capture) return centerDistance <= 4 ? 12 : 0;
                    int diff = Math.Abs(moving.TrueType.GetValue() - target.VisibleType.GetValue());
                    return diff <= 150 ? 55 : 15;

                case OpeningTactic.SpacePressure:
                    // 兵推进 + 占中路，压缩玩家活动空间。
                    int pawnPush = moving.TrueType == PieceType.Pawn && advance > 0 ? 40 : 0;
                    return pawnPush + (centerDistance <= 4 ? 28 : 0) + (advance > 0 ? 10 : 0);

                default:
                    return 0;
            }
        }

        // 反龟缩：玩家长期不前进/高防守时，用等值兑子和中心兵推进强行简化局面。
        internal int GetAntiTurtleBonus(Board board, Move move)
        {
            var profile = PlayerProfile;
            if (profile == null || profile.LiveConfidence < 0.3f || profile.Defensiveness < 0.45f) return 0;

            var target = board.GetPiece(move.To);
            if (target != null && target.Color != _color) return 35;

            var moving = board.GetPiece(move.From);
            if (moving != null && moving.TrueType == PieceType.Pawn)
            {
                int advance = (move.To.Y - move.From.Y) * _colorDirection;
                if (advance > 0) return 30;
            }

            int center = Math.Abs(move.To.X * 2 - 7) + Math.Abs(move.To.Y * 2 - 7);
            return center <= 4 ? 15 : 0;
        }

        private int _colorDirection => _color == PieceColor.White ? 1 : -1;

        public virtual void NotifyOpponentMove(Move move, PieceType visibleType, MoveLeakType leakType, Board boardBeforeMove)
        {
            _beliefState.EnsureInitialized(boardBeforeMove);

            var boardAfterMove = boardBeforeMove.Clone();
            boardAfterMove.ExecuteMove(move);

            bool wasThreatened = IsSquareAttackedByAI(boardBeforeMove, move.From);
            bool stillThreatened = IsSquareAttackedByAI(boardAfterMove, move.To);
            bool baitMove = wasThreatened && stillThreatened;

            _beliefState.DecayTrapRisk(0.85f);
            _beliefState.NotifyMove(move, visibleType, leakType, boardBeforeMove, baitMove);
            _movesSinceLastCapture++;
            _lastOpponentTo = move.To;
            ObserveOpeningMove(boardBeforeMove, move);

            if (baitMove)
            {
                _beliefState.RaiseTrapRisk(move.To, 0.85f);
                Log($"[AI] 玩家受威胁仍走到 ({move.To.X},{move.To.Y})，判定该格大概率有陷阱/保护，不轻易咬饵。");
            }

            if (ExpectiminimaxAI.KingSafetyDebug)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (pos, piece) in boardAfterMove.GetPiecesOfColor(OpponentColor(_color)))
                {
                    sb.Append($" ({pos.X},{pos.Y}){piece.VisibleType.ToChinese()}[");
                    var top = _beliefState.GetTopTypes(pos, 4);
                    bool first = true;
                    foreach (var item in top)
                    {
                        if (item.probability <= 0.02f) continue;
                        if (!first) sb.Append(' ');
                        sb.Append($"{item.type.ToChinese()}{item.probability * 100f:F0}");
                        first = false;
                    }
                    sb.Append($"|m{_beliefState.GetSafetyMask(pos):X2}]");
                }
                Log($"[BELIEFDBG] {move}{sb}");
            }

            // 已经被攻击却原地不动，也视作“空城计”信号：真王/诱饵都可能故意不逃。
            foreach (var (pos, piece) in boardAfterMove.GetPiecesOfColor(OpponentColor(_color)))
            {
                if (pos == move.To) continue;
                if (!IsSquareAttackedByAI(boardBeforeMove, pos)) continue;
                if (!IsSquareAttackedByAI(boardAfterMove, pos)) continue;

                _beliefState.RaiseTrapRisk(pos, 0.6f);
                Log($"[AI] 玩家 {piece.VisibleType.ToChinese()} @ ({pos.X},{pos.Y}) 受威胁仍不撤离，判定为陷阱/有保护。");
            }

            if (leakType == MoveLeakType.Suspicious)
            {
                var tops = _beliefState.GetTopTypes(move.To, 3);
                var formatted = new List<string>();
                foreach (var item in tops)
                {
                    if (item.probability <= 0.01f) continue;
                    formatted.Add($"{item.type.ToChinese()} {item.probability * 100f:F0}%");
                }
                Log($"[AI] 识别到玩家可疑走法 {move}，更新概率：{string.Join("，", formatted)}");
            }
        }

        public virtual void NotifyCapture(Position capturePos)
        {
            _beliefState.NotifyCapture(capturePos);
            _movesSinceLastCapture = 0;
            Log($"[AI] 吃掉玩家一个子 @ ({capturePos.X},{capturePos.Y})，本态未知，仅按信念分布扣减数量。");
        }

        public virtual void NotifyPromotion(Position pos, PieceType? promotionType = null)
        {
            _beliefState.NotifyPromotion(pos, promotionType);
        }

        internal static PieceColor OpponentColor(PieceColor color)
        {
            return color == PieceColor.White ? PieceColor.Black : PieceColor.White;
        }

        internal static void Log(string message)
        {
            if (MuteCurrentThread) return;
            var logger = Logger;
            if (logger != null) logger(message);
        }

        internal Board MergeBoard(Board ownBoard, Board sampledEnemyBoard)
        {
            var merged = new Board();

            foreach (var (pos, piece) in ownBoard.GetAllPieces())
                if (piece.Color == _color)
                    merged.SetPiece(pos, piece.Clone());

            foreach (var (pos, piece) in sampledEnemyBoard.GetAllPieces())
                if (piece.Color != _color)
                    merged.SetPiece(pos, piece.Clone());

            return merged;
        }

        // 王安全检查棋盘：位置与占子取自真实棋盘，敌子只挂“可能本态”掩码，
        // 不用真实本态参与判定，避免 AI 全知泄漏。
        internal Board BuildFairCheckBoard(Board board)
        {
            var fair = board.Clone();
            foreach (var (pos, piece) in fair.GetPiecesOfColor(OpponentColor(_color)))
            {
                // 安全掩码只认“走子几何允许的本态”，不认计数推断出来的概率高低：
                // 吃掉一个像后的子可能让计数以为后再无，但真后未必死（G1 教训）。
                int mask = _beliefState.GetSafetyMask(pos);
                if (mask == 0) mask = 0x7E;
                piece.BeliefTypeMask = mask;
                piece.PossibleTypeMask = mask;
            }
            return fair;
        }

        internal bool IsSquareAttackedByAI(Board board, Position target)
        {
            if (board == null || !target.IsValid) return false;
            foreach (var (from, piece) in board.GetPiecesOfColor(_color))
            {
                if (from == target) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, from, piece.TrueType, piece.Color, target))
                    return true;
            }
            return false;
        }

        internal bool IsSquareAttackedByEnemyVisible(Board board, Position target)
        {
            if (board == null || !target.IsValid) return false;
            PieceColor enemy = OpponentColor(_color);
            foreach (var (from, piece) in board.GetPiecesOfColor(enemy))
            {
                if (from == target) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, from, piece.VisibleType, piece.Color, target))
                    return true;
            }
            return false;
        }

        internal bool IsOwnKingAttackedByEnemy(Board board)
        {
            var kingPos = board.FindKing(_color);
            return kingPos.HasValue && IsSquareAttackedByEnemyVisible(board, kingPos.Value);
        }

        // 走完这一步后，己方真王是否会被敌方“可能本态”直接吃掉。
        // 只读信念掩码，不读对手真实本态；供绝杀快速通道做反吃红线。
        // paranoid=true 时按“任意敌子可能是任意类型”兜底，用于真王亲自赌王。
        internal virtual bool WouldLoseOwnKing(Board board, Move move, bool paranoid = false)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            var kingPos = fair.FindKing(_color);
            bool lost = kingPos.HasValue && (paranoid
                ? IsSquareAttackedByAnyPossibleEnemy(fair, kingPos.Value)
                : IsSquareAttackedByMaskedEnemy(fair, kingPos.Value));
            fair.UndoMove(move, undo);
            return lost;
        }

        // 超保守判定：不管信念掩码怎么写，任何敌子都按“任意可能类型”计算攻击。
        // G7：56% 赌王后 h3 敌后反吃真王，掩码路径漏判过一次；赌王手必须按全类型兜底。
        internal bool IsSquareAttackedByAnyPossibleEnemy(Board board, Position target)
        {
            PieceColor enemy = OpponentColor(_color);
            foreach (var (from, piece) in board.GetPiecesOfColor(enemy))
            {
                if (from == target) continue;
                for (int t = 1; t < 7; t++)
                {
                    if (MoveGenerator.CanPieceAttackSquare(board, from, (PieceType)t, piece.Color, target))
                        return true;
                }
            }
            return false;
        }

        // 走完这一步后，己方真后/真车是否暴露在敌方“可能本态”的攻击下。
        // 与王的反吃红线同源：只用几何安全掩码，不读对手真实本态。
        internal bool WouldHangOwnHeavy(Board board, Move move)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            bool hangs = false;
            foreach (var (pos, piece) in fair.GetPiecesOfColor(_color))
            {
                if (piece.TrueType != PieceType.Queen && piece.TrueType != PieceType.Rook) continue;
                if (IsSquareAttackedByMaskedEnemy(fair, pos)) { hangs = true; break; }
            }
            fair.UndoMove(move, undo);
            return hangs;
        }

        private bool IsSquareAttackedByMaskedEnemy(Board board, Position target)
        {
            PieceColor enemy = OpponentColor(_color);
            foreach (var (from, piece) in board.GetPiecesOfColor(enemy))
            {
                if (from == target) continue;
                int mask = piece.BeliefTypeMask;
                if (mask == 0) mask = 1 << (int)piece.VisibleType;

                for (int t = 1; t < 7; t++)
                {
                    if ((mask & (1 << t)) == 0) continue;
                    if (MoveGenerator.CanPieceAttackSquare(board, from, (PieceType)t, piece.Color, target))
                        return true;
                }
            }
            return false;
        }

        private static bool DefaultParallelSearch()
        {
#if UNITY_WEBGL || DEMO_VERSION
            return false;
#else
            return true;
#endif
        }

        private static int DefaultParallelThreads()
        {
#if UNITY_WEBGL || DEMO_VERSION
            return 1;
#else
            return Math.Max(2, Math.Min(12, Environment.ProcessorCount));
#endif
        }

        // 平台算力隔离：移动端 6 采样/深度 4/单线程；WebGL 32 采样/深度 3 单线程；
        // 桌面完整版按调参值放大到 64~128 采样/深度 5~6。
        public static void ApplyPlatformPreset(ref int sampleCount, ref int searchDepth)
        {
            if (MobileProfile)
            {
                sampleCount = Math.Min(Math.Max(1, sampleCount), MobileSampleCount);
                searchDepth = Math.Min(Math.Max(1, searchDepth), MobileSearchDepth);
                EnableParallelSearch = false;
                ParallelThreads = 1;
                return;
            }

#if UNITY_WEBGL || DEMO_VERSION
            sampleCount = Math.Min(Math.Max(1, sampleCount), 32);
            searchDepth = Math.Min(Math.Max(1, searchDepth), 3);
            EnableParallelSearch = false;
#else
            sampleCount = Math.Min(128, Math.Max(64, sampleCount * 3));
            searchDepth = Math.Min(6, Math.Max(5, searchDepth + 2));
            EnableParallelSearch = true;
#endif
        }
    }

    // 引擎无关的分帧搜索句柄：Unity 每帧调用 Advance(sliceMs)，控制台循环到 IsFinished。
    public abstract class AISearchSession
    {
        public abstract bool IsFinished { get; }

        public abstract Move? Result { get; }

        public abstract bool Advance(int sliceMs);

        // 已完成的主搜索层数；0 表示该引擎不报告。Hybrid 据此判断“只搜了 2 层就超时”的战术盲区。
        public virtual int CompletedDepth => 0;

        // MCTS 会在搜索完成后提供根节点候选；其它引擎默认不提供。
        public virtual IReadOnlyList<RootMoveStat> GetTopMoves(int count) => null;
    }
}
