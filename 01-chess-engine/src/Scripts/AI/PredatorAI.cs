using System;
using System.Collections.Generic;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // Predator v5：战术核心仍是 Hybrid 的全宽期望极小极大，外面只保留两项“人类视角”修正：
    //   1. 隐匿：真王候选集一旦收窄，人类就更容易锁定；主动挪王额外扣分。
    //   2. 人类视角下的真王被吃概率：用镜像信念（玩家看我们）乘几何攻击概率，
    //      而不是用我们自己的最坏情况估计。玩家只会在“值得赌”时才用便宜子扑王。
    // 子力风险/诱饵/通用猎王全部删除：前两者与采样搜索重复计算，后者在 6% 全候选阶段只是噪声，
    // v4 实测已证明它们把胜率从 48% 拖到 32%。真正的猎王交给核心的“吃王期望 = P × 胜局”。
    public sealed class PredatorAI : HybridAI
    {
        private const double KingConcealWeight = 1200.0;
        private const double KingMoveAbsolutePenalty = 350.0;
        private const double KingMoveDeltaPenalty = 800.0;
        private const double KingMoveTempoPenalty = 80.0;
        // 挪王暴露位置的固定代价。实测 2400 会把“王吃叉子骑士”这种净赚 320 的强制手压成 -2500，
        // 导致宁可送后被叉；保留为软税，真正的位置风险交给下面的 EstimateHumanKingLoss。
        private const double KingRevealFixedPenalty = 600.0;
        private const double CastlingRevealPenalty = 9000.0;
        private const double OwnKingLossWeight = 9000.0;
        private const double MaxTotalBonus = 2500.0;
        private const double VetoThreshold = -400.0;
        // 王磁铁：人类信念把某个大子当成王时（P_King≥15%），它的一层贪心会不计代价来吃
        //（绝杀通道阈值 20%、安全阀关闭）。掉后/掉车不是小概率噪声，按整子价值计。
        private const float KingMagnetThreshold = 0.15f;
        // HumanModel 绝杀通道的固定门槛：P_King 一旦越过它，任何能落到的子都会无条件来吃，
        // 赌错也只是拿走这个子本身。越过门槛后按“整子几乎必丢”定价，而不是按 pKing 折算。
        private const float KingMagnetCertainThreshold = 0.19f;
        private const double KingMagnetCaptureWeight = 0.9;
        private const double KingMagnetWeight = 1.0;
        // 玩家是单层贪心：P(王)×可吃概率足够高时，它一定会来吃。这个惩罚不设上限，
        // 因为它对应的是“下一手直接终局”，而不是普通子力损失。
        private const double GreedyKingCapturePenalty = 12000.0;

        // L1 信息账本：每一步都按走子几何收窄对手对我方这枚子的可能集（与伪装标签无关）。
        // 隐藏价值 = 真值 − 对手以为的值，只在“会被交易/被吃”的接触点兑现；
        // 告知成本 = 识别度增量 × 真值 × 暴露度。王不走这里（conceal 已单独计价）。
        private const double HiddenValueWeight = 0.35;
        private const double HiddenValueCap = 400.0;
        private const double RevealCostWeight = 0.25;
        private const double RevealCostCap = 300.0;

        // L2 对手模型：预测单层贪婪对手按它自己的信念会怎么吃，结算真实交换差。
        private const double ExploitWeight = 0.5;
        private const double ExploitCap = 500.0;

        // A/B 开关：L1 信息账本默认开（v115），L2 对手模型默认关（v116 用 oppmodel 打开）。
        public static bool EnableInfoLedger = true;
        public static bool EnableRevealCost = true;
        public static bool EnableOpponentModel = false;

        // 诊断开关：输出根节点各项评分的分布，用于离线校准权重（默认关闭）。
        public static bool DebugScores = false;

        private readonly BeliefState _mirror;
        private bool _mirrorReady;
        private readonly Dictionary<Move, double> _bonuses = new Dictionary<Move, double>();
        private readonly Dictionary<Move, double> _kingRisks = new Dictionary<Move, double>();
        private Func<Board, Move, double> _bonusLookup;

        public PredatorAI(PieceColor color, int seed = 42, int reviewSamples = 16,
            int reviewDepth = 6, int reviewTimeLimitMs = FastResponseMs)
            : base(color, seed, reviewSamples, reviewDepth, reviewTimeLimitMs)
        {
            _mirror = new BeliefState(OpponentColor(color));
            _bonusLookup = LookupBonus;
        }

        public override void ObserveGameStart(Board board)
        {
            base.ObserveGameStart(board);
            _mirror.ResetFromBoard(board, knownTruth: false);
            _mirrorReady = true;
            _bonuses.Clear();
            Log("[Predator] 几何推断模型初始化：按玩家视角维护我方棋子的可能本态集合（不含真型）。");
        }

        public override void NotifyOwnMove(Move move, bool wasCapture, Board boardBefore = null)
        {
            base.NotifyOwnMove(move, wasCapture, boardBefore);
            if (boardBefore == null || !_mirrorReady)
                return;

            var mover = boardBefore.GetPiece(move.From);
            if (mover == null) return;

            // baitMove 必须和人类那条路径算得一模一样：受威胁仍不撤的走法会保住“这可能是真王”的
            // 概率（0.25 而不是 0.02）。镜像永远传 false 会把概率压到人类实际值的几十分之一，
            // 实战里表现为镜像说吃王风险 0%、人类下一步却直接扑王。
            _mirror.NotifyMove(move, mover.VisibleType, LeakFor(boardBefore, move, mover),
                boardBefore, HumanSeesBait(boardBefore, move));
            if (move.PromotionType.HasValue)
                _mirror.NotifyPromotion(move.To, move.PromotionType);
        }

        // 人类视角的“空城计”判定：人类知道自己每个子的本态，但那是隐藏信息，
        // 我们只能用公开信念去估计“它的这一步像不像受威胁仍不撤”。
        private bool HumanSeesBait(Board boardBefore, Move move)
        {
            if (!HumanAttacksSquare(boardBefore, move.From)) return false;

            var after = boardBefore.Clone();
            after.ExecuteMove(move);
            return HumanAttacksSquare(after, move.To);
        }

        private bool HumanAttacksSquare(Board board, Position target)
        {
            PieceColor human = OpponentColor(_color);
            foreach (var (from, _) in board.GetPiecesOfColor(human))
            {
                if (from == target) continue;
                if (BeliefAttackProbability(board, from, target, human) >= 0.5f)
                    return true;
            }
            return false;
        }

        // 不读对手本态：按信念分布累加“哪些本态能攻击到该格”的概率。
        private float BeliefAttackProbability(Board board, Position from, Position target, PieceColor attacker)
        {
            float sum = 0f;
            for (int t = 1; t < 7; t++)
            {
                float p = _beliefState.GetProbability(from, (PieceType)t);
                if (p < 0.05f) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, from, (PieceType)t, attacker, target))
                    sum += p;
            }
            return sum;
        }

        public override void NotifyOpponentMove(Move move, PieceType visibleType, MoveLeakType leakType,
            Board boardBeforeMove)
        {
            base.NotifyOpponentMove(move, visibleType, leakType, boardBeforeMove);

            if (!_mirrorReady || boardBeforeMove == null) return;
            var victim = boardBeforeMove.GetPiece(move.To);
            if (victim != null && victim.Color == _color)
                _mirror.NotifyCapture(move.To);
        }

        public override void ResetAfterRollback(BeliefState restoredBelief)
        {
            base.ResetAfterRollback(restoredBelief);
            _mirrorReady = false;
            _bonuses.Clear();
            _kingRisks.Clear();
            _reviewer.RootMoveBonus = null;
            _reviewer.KingRescueRisk = null;
        }

        protected override void PrepareReviewer(Board board)
        {
            if (board == null) return;

            if (!_mirrorReady)
            {
                _mirror.ResetFromBoard(board, knownTruth: false);
                _mirrorReady = true;
            }

            ComputeRootBonuses(board);
            _reviewer.RootMoveBonus = _bonusLookup;
            _reviewer.KingRescueRisk = LookupKingRisk;
        }

        private double LookupKingRisk(Move move)
        {
            double risk;
            return _kingRisks.TryGetValue(move, out risk) ? risk : 1.0;
        }

        // Ponder 缓存来自不带 Predator 评分的基线搜索：根节点风险过高的答案直接否决，其余照常复用。
        protected override bool AcceptPonderAnswer(Board board, Move move)
        {
            if (!base.AcceptPonderAnswer(board, move)) return false;

            double bonus;
            if (_bonuses.TryGetValue(move, out bonus) && bonus < VetoThreshold)
            {
                Log($"[Predator] Ponder 答案 {move} 触发根节点风险否决（{bonus:F0}），改为现场评分搜索。");
                return false;
            }

            return true;
        }

        private double LookupBonus(Board board, Move move)
        {
            double bonus;
            return _bonuses.TryGetValue(move, out bonus) ? bonus : 0.0;
        }

        private static MoveLeakType LeakFor(Board board, Move move, Piece mover)
        {
            return move.IsCastling || MoveGenerator.IsValidMoveForPieceType(
                    board, move.From, move.To, mover.VisibleType, mover.Color)
                ? MoveLeakType.Disguised
                : MoveLeakType.Suspicious;
        }

        private void ComputeRootBonuses(Board board)
        {
            _bonuses.Clear();
            _kingRisks.Clear();

            var moves = MoveGenerator.GeneratePseudoLegalMoves(board, _color);
            if (moves.Count == 0) return;

            float ownKingBefore = MaxOwnKingProbability(_mirror, board);
            PieceColor enemyColor = OpponentColor(_color);

            // 当前这一刻真王是否已经被便宜子咬住：只有这时挪王才值得付出“暴露位置”的代价。
            double kingLossNow = EstimateHumanKingLoss(board, _mirror, enemyColor);

            Move bestMove = default;
            double bestTotal = double.NegativeInfinity;
            double bestConceal = 0.0;
            double bestRisk = 0.0;

            var diag = DebugScores
                ? new List<(Move move, double total, double conceal, double risk, double info)>()
                : null;

            foreach (var move in moves)
            {
                var mover = board.GetPiece(move.From);
                if (mover == null) continue;

                var after = board.Clone();
                after.ExecuteMove(move);

                // 玩家视角：这一步之后，我方这枚棋子的可能本态集合被几何合法性收窄。
                // 诱饵判定必须与持久镜像一致，否则“受威胁仍不撤”的王证据会被漏算。
                var humanView = _mirror.Clone();
                humanView.NotifyMove(move, mover.VisibleType, LeakFor(board, move, mover),
                    board, HumanSeesBait(board, move));
                if (move.PromotionType.HasValue)
                    humanView.NotifyPromotion(move.To, move.PromotionType);

                // 1) 隐匿：真王候选集越集中，人类越可能已经锁定；主动挪王额外重罚。
                float ownKingAfter = MaxOwnKingProbability(humanView, after);
                double conceal = -Math.Max(0.0, ownKingAfter - ownKingBefore) * KingConcealWeight;
                if (mover.TrueType == PieceType.King)
                {
                    float kp = humanView.GetProbability(move.To, PieceType.King);
                    float kpBefore = _mirror.GetProbability(move.From, PieceType.King);
                    conceal -= kp * KingMoveAbsolutePenalty;
                    conceal -= Math.Max(0.0, kp - kpBefore) * KingMoveDeltaPenalty;
                    conceal -= KingMoveTempoPenalty;

                    // 王的最大价值是“人类不知道它在哪”。没有即时攻击时挪王就是主动报点，
                    // 而且每挪一次都会让“只有王能一直这么走”的证据更硬，最后必然被锁死。
                    if (kingLossNow < 0.05)
                        conceal -= KingRevealFixedPenalty;

                    if (_reviewer.IsKingBaitCapture(board, move))
                        conceal -= 3000.0;
                }

                // 易位在信念层等同于直接宣布“这个子是王”（Castle 更新会把 P_King 抬到 1）。
                // 王还没暴露时，易位省下的那点安全感远不值得把位置送出去。
                if (move.IsCastling && ownKingBefore < 0.55f)
                    conceal -= CastlingRevealPenalty;

                // 2) 人类视角下，这回合真王被便宜子吃的概率（镜像信念 × 几何攻击概率）。
                double risk = EstimateHumanKingLoss(after, humanView, enemyColor);
                double destiny = EstimateGreedyKingCapture(after, humanView, enemyColor);
                double magnet = EstimateHeavyPieceKingMagnet(after, humanView, enemyColor);

                double total = conceal - risk * OwnKingLossWeight;
                if (total > MaxTotalBonus) total = MaxTotalBonus;
                else if (total < -MaxTotalBonus) total = -MaxTotalBonus;
                total -= destiny;
                total -= magnet * KingMagnetWeight;

                // L1 信息账本：普通子的“对手低估了多少 / 这一步告诉了对手多少”。
                // 放在截断之后，避免和高危手的 ±2500 同分坍缩混在一起。
                double info = 0.0;
                if (EnableInfoLedger && mover.TrueType != PieceType.King)
                {
                    info = HiddenValueFlow(board, _mirror, after, humanView, move);
                    if (EnableRevealCost)
                        info -= RevealCost(after, humanView, mover, move);
                    total += info;
                }

                // L2 对手模型：它因误判而走出对我们更有利的交换时，给小额外收益。
                if (EnableOpponentModel)
                {
                    double exploit = OpponentModel.ExploitationGain(after, humanView, _beliefState, _color);
                    total += Math.Max(0.0, Math.Min(ExploitCap, exploit * ExploitWeight));
                }

                _bonuses[move] = total;
                _kingRisks[move] = risk;
                diag?.Add((move, total, conceal, risk, info));

                if (total > bestTotal)
                {
                    bestTotal = total;
                    bestMove = move;
                    bestConceal = conceal;
                    bestRisk = risk;
                }
            }

            float ownKingNow = MaxOwnKingProbability(_mirror, board);
            Log($"[Predator] 人类视角评分 {moves.Count} 手：我方王最高 {ownKingNow:P0}；最佳 {bestMove} " +
                $"（隐匿 {bestConceal:F0} / 玩家吃王风险 {bestRisk:P0} → {-bestRisk * OwnKingLossWeight:F0}）。");

            if (diag != null && diag.Count > 0)
            {
                double cMin = double.MaxValue, cMax = double.MinValue, cSum = 0;
                double rMin = double.MaxValue, rMax = double.MinValue, rSum = 0;
                double tMin = double.MaxValue, tMax = double.MinValue, tSum = 0;
                foreach (var d in diag)
                {
                    cMin = Math.Min(cMin, d.conceal); cMax = Math.Max(cMax, d.conceal); cSum += d.conceal;
                    rMin = Math.Min(rMin, d.risk); rMax = Math.Max(rMax, d.risk); rSum += d.risk;
                    tMin = Math.Min(tMin, d.total); tMax = Math.Max(tMax, d.total); tSum += d.total;
                }
                int n = diag.Count;
                Log($"[PredDiag] 隐匿[{cMin:F0},{cMax:F0}]均{cSum / n:F0} " +
                    $"吃王风险[{rMin:P0},{rMax:P0}]均{rSum / n:P0} 总分[{tMin:F0},{tMax:F0}]均{tSum / n:F0}");

                diag.Sort((a, b) => b.total.CompareTo(a.total));
                int show = Math.Min(3, diag.Count);
                for (int i = 0; i < show; i++)
                {
                    var d = diag[i];
                    Log($"[PredDiag] +{i + 1} {d.move} 总{d.total:F0} 隐匿{d.conceal:F0} " +
                        $"信息{d.info:F0} 吃王险-{d.risk * OwnKingLossWeight:F0}（{d.risk:P0}）");
                }
                for (int i = 0; i < show; i++)
                {
                    var d = diag[n - 1 - i];
                    Log($"[PredDiag] -{i + 1} {d.move} 总{d.total:F0} 隐匿{d.conceal:F0} " +
                        $"信息{d.info:F0} 吃王险-{d.risk * OwnKingLossWeight:F0}（{d.risk:P0}）");
                }
            }
        }

        private float MaxOwnKingProbability(BeliefState view, Board board)
        {
            float max = 0f;
            if (view == null || board == null) return max;
            foreach (var (pos, _) in board.GetPiecesOfColor(_color))
            {
                float p = view.GetProbability(pos, PieceType.King);
                if (p > max) max = p;
            }
            return max;
        }

        // 人类不会用王去扑子（吃王本身就是终局，不需要冒险）；其余类型都可能。
        // 概率取“玩家视角看到我方这枚子是王”的镜像信念，攻击几何取各方可能本态的并集。
        private double EstimateHumanKingLoss(Board after, BeliefState humanView, PieceColor enemyColor)
        {
            var kingPos = after.FindKing(_color);
            if (!kingPos.HasValue) return 1.0;

            float pAtKing = humanView.GetProbability(kingPos.Value, PieceType.King);
            if (pAtKing < 0.02f) return 0.0;

            double survive = 1.0;
            foreach (var (epos, _) in after.GetPiecesOfColor(enemyColor))
            {
                float pAttack = BeliefAttackProbability(after, epos, kingPos.Value, enemyColor);
                if (pAttack > 0.0)
                    survive *= 1.0 - Math.Min(0.95, pAttack);
            }

            return pAtKing * (1.0 - survive);
        }

        // 玩家不会按“概率期望”犹豫：它清楚自己手里是什么子，只要镜像里“这枚子可能是王”
        // 有一点苗头、且能落到那个格子，吃王彩金+表面子力就足以让它伸手。
        // 所以这里按“可能性”而不是我们自己的概率期望估，宁可高估（G0：3% 的后偷走边角真王）。
        private double EstimateGreedyKingCapture(Board after, BeliefState humanView, PieceColor enemyColor)
        {
            var kingPos = after.FindKing(_color);
            if (!kingPos.HasValue) return 0.0;

            float pAtKing = humanView.GetProbability(kingPos.Value, PieceType.King);
            if (pAtKing < 0.01f) return 0.0;

            bool canStrike = false;
            foreach (var (epos, _) in after.GetPiecesOfColor(enemyColor))
            {
                if (epos == kingPos.Value) continue;
                if (AnyPossibleAttack(after, epos, kingPos.Value, enemyColor))
                {
                    canStrike = true;
                    break;
                }
            }

            if (!canStrike) return 0.0;

            double urge = Math.Min(1.0, 0.4 + pAtKing * 6.0);
            return urge * GreedyKingCapturePenalty;
        }

        // 王磁铁：玩家的信念会把我方“长期不动、可能本态里还有王”的大子当王候选。
        // 它的一层贪心只要 P_King 够高，就会用任意子（哪怕象/兵）来吃；真身是后/车时
        // 等于白送一个重子（G39：b8 真后被 h2 象以“可能是王”为由直接吃掉）。
        // 越过绝杀门槛后玩家几乎必然伸手，而且猜错对玩家没有额外成本（照样拿走这个子），
        // 所以按整子定价；有保护时只扣掉“兑回最便宜攻击子”的残值。
        // 镜像只由公开信息推导，不读取任何真实本态。
        private double EstimateHeavyPieceKingMagnet(Board after, BeliefState humanView, PieceColor enemyColor)
        {
            if (after == null || humanView == null) return 0.0;

            double penalty = 0.0;
            foreach (var (pos, piece) in after.GetPiecesOfColor(_color))
            {
                if (piece.TrueType == PieceType.King) continue;
                int value = piece.TrueType.GetValue();
                if (value < 300) continue;

                float pKing = humanView.GetProbability(pos, PieceType.King);
                if (pKing < KingMagnetThreshold) continue;

                int cheapestAttacker = int.MaxValue;
                foreach (var (epos, _) in after.GetPiecesOfColor(enemyColor))
                {
                    if (!AnyPossibleAttack(after, epos, pos, enemyColor)) continue;
                    int attackerValue = CheapestPossibleValue(epos);
                    if (attackerValue < cheapestAttacker) cheapestAttacker = attackerValue;
                }
                if (cheapestAttacker == int.MaxValue) continue;

                double expected = pKing * value;
                double certain = 0.0;
                if (pKing >= KingMagnetCertainThreshold)
                {
                    bool defended = IsSquareAttackedByAI(after, pos);
                    double swing = defended ? Math.Max(100.0, value - cheapestAttacker) : value;
                    certain = swing * KingMagnetCaptureWeight;
                }
                penalty += Math.Max(expected, certain);
            }
            return penalty;
        }

        // 敌子可能本态里的最小面值：玩家用哪个子来试王由它自己的真型决定，
        // 我们只能按公开掩码取最保守（对我们最便宜）的反吃价值。
        private int CheapestPossibleValue(Position pos)
        {
            int mask = _beliefState.GetSafetyMask(pos);
            if (mask == 0) mask = 0x7E;

            int cheapest = int.MaxValue;
            for (int t = 1; t < 7; t++)
            {
                if ((mask & (1 << t)) == 0) continue;
                int v = ((PieceType)t).GetValue();
                if (v < cheapest) cheapest = v;
            }
            return cheapest == int.MaxValue ? 100 : cheapest;
        }

        // 只读信念掩码：这枚敌子“有可能是”某种能攻击目标格的类型。
        private bool AnyPossibleAttack(Board board, Position from, Position target, PieceColor attacker)
        {
            var piece = board.GetPiece(from);
            if (piece == null) return false;

            // 与王安全扫描同一口径：按走子几何的可能集，而不是计数推断出的概率高低。
            int mask = _beliefState.GetSafetyMask(from);
            if (mask == 0) mask = 0x7E;

            for (int t = 1; t < 7; t++)
            {
                if ((mask & (1 << t)) == 0) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, from, (PieceType)t, attacker, target))
                    return true;
            }
            return false;
        }

        // 隐藏价值流：这一步让“对手低估我方子力”的可兑现总量变化了多少。
        // 只算非王棋子；接触度 = 对手按它自己的信念能打到这枚子的概率，
        // 或这枚子正压着对手的子（对手低估它时，这些攻击更容易兑现）。
        private double HiddenValueFlow(Board before, BeliefState viewBefore, Board after,
            BeliefState viewAfter, Move move)
        {
            double flow = 0.0;
            foreach (var (pos, piece) in after.GetPiecesOfColor(_color))
            {
                if (piece.TrueType == PieceType.King) continue;
                double hiddenAfter = Math.Min(piece.TrueType.GetValue(), 900)
                    - viewAfter.GetExpectedMaterialValue(pos);
                if (hiddenAfter < 20.0) continue;

                double realizedAfter = hiddenAfter * ContactProbability(after, pos, piece.TrueType);

                Position beforePos = pos == move.To ? move.From : pos;
                var bp = before.GetPiece(beforePos);
                double realizedBefore = 0.0;
                if (bp != null && bp.Color == _color && bp.TrueType != PieceType.King)
                {
                    double hiddenBefore = Math.Min(bp.TrueType.GetValue(), 900)
                        - viewBefore.GetExpectedMaterialValue(beforePos);
                    if (hiddenBefore > 0.0)
                        realizedBefore = hiddenBefore * ContactProbability(before, beforePos, bp.TrueType);
                }

                flow += realizedAfter - realizedBefore;
            }
            return Math.Max(-HiddenValueCap, Math.Min(HiddenValueCap, flow * HiddenValueWeight));
        }

        // 接触度：对手下一手有多大概率与这枚子发生交易/吃子。
        private double ContactProbability(Board board, Position target, PieceType trueType)
        {
            PieceColor enemy = OpponentColor(_color);
            double contact = 0.0;
            foreach (var (epos, _) in board.GetPiecesOfColor(enemy))
            {
                if (epos == target) continue;
                contact += BeliefAttackProbability(board, epos, target, enemy);
            }
            contact = Math.Min(1.0, contact);
            if (contact < 0.35)
            {
                foreach (var (epos, _) in board.GetPiecesOfColor(enemy))
                {
                    if (MoveGenerator.CanPieceAttackSquare(board, target, trueType, _color, epos))
                    {
                        contact = 0.35;
                        break;
                    }
                }
            }
            return contact;
        }

        // 告知成本：这一步把“这枚子其实是 T”的识别度抬高了多少，按真值和暴露度计价。
        // 早就被认出来（>80%）时没有新信息成本；小子泄密便宜，大子泄密贵。
        private double RevealCost(Board after, BeliefState humanView, Piece mover, Move move)
        {
            float before = _mirror.GetProbability(move.From, mover.TrueType);
            if (before > 0.8f) return 0.0;
            float afterP = humanView.GetProbability(move.To, mover.TrueType);
            double delta = afterP - before;
            if (delta <= 0.01) return 0.0;

            int value = Math.Min(mover.TrueType.GetValue(), 900);
            double exposure = 0.5 + 0.5 * ContactProbability(after, move.To, mover.TrueType);
            return Math.Min(RevealCostCap, delta * value * RevealCostWeight * exposure);
        }
    }
}
