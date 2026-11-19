using System;
using System.Collections.Generic;
using System.Linq;
using ChessDark.Core;

namespace ChessDark.Logic
{
    public class BeliefState
    {
        private const int PT_COUNT = 7; // None + 6 piece types
        private const float TYPE_EPSILON = 0.02f;

        // 王安全悲观判定阈值：概率高于它的本态都按“可能是它”参与攻击线检查。
        // 阈值过高会漏掉“3% 可能是后”这类尾部风险，而暗棋里真王被吃就是终局。
        private const float BeliefMaskThreshold = 0.02f;

        private static readonly PieceType[] AllTypes =
        {
            PieceType.King, PieceType.Queen, PieceType.Rook,
            PieceType.Bishop, PieceType.Knight, PieceType.Pawn
        };

        // 全部本态的位掩码（bit i = PieceType i，不含 None）。
        private const int AllTypesMask = 0x7E;

        private class Body
        {
            public Position Pos;
            public PieceType VisibleType;
            public float TrapRisk;
            public bool HasMoved;
            public readonly float[] Probs = new float[PT_COUNT];

            // 逻辑可能集：只按“这枚子实际走过的步子几何”收窄，永不因捕获计数估计而放宽或清零。
            // 计数推断可能出错（吃掉一个“像后”的子不等于真后已死），王安全必须以此为准。
            public int PossibleMask = AllTypesMask;
        }

        private readonly PieceColor _selfColor;
        private readonly PieceColor _enemyColor;
        private readonly Dictionary<Position, Body> _bodies = new Dictionary<Position, Body>();
        private readonly float[] _remaining = new float[PT_COUNT];

        public bool IsEmpty => _bodies.Count == 0;

        public BeliefState(PieceColor selfColor)
        {
            _selfColor = selfColor;
            _enemyColor = selfColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            ResetCounts();
        }

        private void ResetCounts()
        {
            for (int i = 0; i < PT_COUNT; i++) _remaining[i] = 0;
            _remaining[(int)PieceType.King] = 1;
            _remaining[(int)PieceType.Queen] = 1;
            _remaining[(int)PieceType.Rook] = 2;
            _remaining[(int)PieceType.Bishop] = 2;
            _remaining[(int)PieceType.Knight] = 2;
            _remaining[(int)PieceType.Pawn] = 8;
        }

        public BeliefState Clone()
        {
            var clone = new BeliefState(_selfColor);
            clone.VisibleLabelTrust = VisibleLabelTrust;
            Array.Copy(_remaining, clone._remaining, PT_COUNT);

            foreach (var body in _bodies.Values)
            {
                var copy = new Body
                {
                    Pos = body.Pos, VisibleType = body.VisibleType,
                    TrapRisk = body.TrapRisk, HasMoved = body.HasMoved,
                    PossibleMask = body.PossibleMask
                };
                Array.Copy(body.Probs, copy.Probs, PT_COUNT);
                clone._bodies[copy.Pos] = copy;
            }

            return clone;
        }

        // 快照回滚用：把另一份同色信念整体覆盖到当前实例，不改变已有引用。
        public void CopyFrom(BeliefState source)
        {
            if (source == null) return;
            VisibleLabelTrust = source.VisibleLabelTrust;
            _bodies.Clear();
            Array.Copy(source._remaining, _remaining, PT_COUNT);
            foreach (var body in source._bodies.Values)
            {
                var copy = new Body
                {
                    Pos = body.Pos, VisibleType = body.VisibleType,
                    TrapRisk = body.TrapRisk, HasMoved = body.HasMoved,
                    PossibleMask = body.PossibleMask
                };
                Array.Copy(body.Probs, copy.Probs, PT_COUNT);
                _bodies[copy.Pos] = copy;
            }
        }

        // 伪装标签先验：标签只是公开外壳，真型藏在它后面，游戏/基准一律保持 0。
        // 非零值只用于受控实验；该值只信任公开可见标签，不读取任何真型，几何走法证据仍可推翻它。
        public float VisibleLabelTrust { get; set; }

        public void ResetFromBoard(Board board, bool knownTruth = false)
        {
            _bodies.Clear();
            ResetCounts();
            if (board == null) return;

            var enemies = board.GetPiecesOfColor(_enemyColor).ToList();
            if (enemies.Count == 0) return;

            if (knownTruth)
            {
                foreach (var (pos, piece) in enemies)
                {
                    var known = new Body
                    {
                        Pos = pos, VisibleType = piece.VisibleType, HasMoved = piece.HasMoved,
                        PossibleMask = 1 << (int)piece.TrueType
                    };
                    known.Probs[(int)piece.TrueType] = 1f;
                    _bodies[pos] = known;
                }
                return;
            }

            float trust = Math.Max(0f, Math.Min(0.95f, VisibleLabelTrust));
            float bodyCount = enemies.Count;
            foreach (var (pos, piece) in enemies)
            {
                var body = new Body
                {
                    Pos = pos, VisibleType = piece.VisibleType, HasMoved = piece.HasMoved,
                    PossibleMask = MaskFromRemaining()
                };
                for (int t = 1; t < PT_COUNT; t++)
                    body.Probs[t] = _remaining[t] / bodyCount * (1f - trust);

                int visible = (int)piece.VisibleType;
                if (trust > 0f && visible > 0 && visible < PT_COUNT)
                    body.Probs[visible] += trust;

                _bodies[pos] = body;
            }

            EnforceCountConstraint();
        }

        public void EnsureInitialized(Board board)
        {
            if (_bodies.Count == 0) ResetFromBoard(board);
        }

        public float GetProbability(Position pos, PieceType type)
        {
            if (!pos.IsValid || type == PieceType.None) return 0f;
            if (_bodies.TryGetValue(pos, out Body body)) return body.Probs[(int)type];
            return 0f;
        }

        // 信念是否已经足够确定：每个敌子都有一个高概率本态，且王位也基本锁定。
        // 只有这种情况下，“最大似然世界”才接近真实局面，残局单世界深搜才有意义；
        // 否则单世界会把“猜出来的假王”当成真王，搜出根本不存在的绝杀/被绝杀线路。
        public bool IsConfident(float threshold = 0.7f)
        {
            if (_bodies.Count == 0) return true;

            float maxKing = 0f;
            double sumMax = 0.0;
            foreach (var body in _bodies.Values)
            {
                float max = 0f;
                for (int t = 1; t < PT_COUNT; t++)
                    if (body.Probs[t] > max) max = body.Probs[t];
                sumMax += max;

                float king = body.Probs[(int)PieceType.King];
                if (king > maxKing) maxKing = king;
            }

            return maxKing >= threshold && sumMax / _bodies.Count >= threshold;
        }

        // 王的本态身份是否已锁定（某个敌子“就是王”的概率足够高）。
        // 只有身份锁定后，采样世界里的王位才可信，深搜才允许把“吃采样王”当成真绝杀；
        // 位置概率不能用于此判定：假设走子线里王已离开原位，按位置查永远是 0。
        public bool IsKingIdentityKnown(float threshold = 0.8f)
        {
            if (_bodies.Count == 0) return true;

            foreach (var body in _bodies.Values)
                if (body.Probs[(int)PieceType.King] >= threshold) return true;
            return false;
        }

        // 敌子“可能本态”的位掩码：概率达到阈值即视为可能，用于王安全的悲观红线。
        public int GetTypeMask(Position pos, float threshold = BeliefMaskThreshold)
        {
            if (!pos.IsValid || !_bodies.TryGetValue(pos, out Body body)) return 0;

            int mask = 0;
            for (int t = 1; t < PT_COUNT; t++)
                if (body.Probs[t] >= threshold) mask |= 1 << t;
            return mask;
        }

        // 王安全专用：按“逻辑可能集”取掩码，不看计数推断出来的概率大小。
        // 计数会漂移（吃掉一个像后的子可能把“后”清空），但真型不会因此消失。
        public int GetSafetyMask(Position pos)
        {
            if (!pos.IsValid) return 0;
            return _bodies.TryGetValue(pos, out Body body) ? body.PossibleMask : AllTypesMask;
        }

        private int MaskFromRemaining()
        {
            int mask = 0;
            for (int t = 1; t < PT_COUNT; t++)
                if (_remaining[t] > 0.0001f) mask |= 1 << t;
            return mask == 0 ? AllTypesMask : mask;
        }

        public float GetExpectedValue(Position pos)
        {
            if (!pos.IsValid || !_bodies.TryGetValue(pos, out Body body)) return 0f;
            float value = 0f;
            for (int t = 1; t < PT_COUNT; t++)
                value += body.Probs[t] * ((PieceType)t).GetValue();
            return value;
        }

        // 只算可交易子力，王不参与：王的价值（20000）会把物质差彻底淹没。
        public float GetExpectedMaterialValue(Position pos)
        {
            if (!pos.IsValid || !_bodies.TryGetValue(pos, out Body body)) return 0f;
            float value = 0f;
            for (int t = 1; t < PT_COUNT; t++)
            {
                if ((PieceType)t == PieceType.King) continue;
                value += body.Probs[t] * Math.Min(((PieceType)t).GetValue(), 900);
            }
            return value;
        }

        public List<(PieceType type, float probability)> GetTopTypes(Position pos, int count)
        {
            var result = new List<(PieceType, float)>();
            if (!pos.IsValid || !_bodies.TryGetValue(pos, out Body body)) return result;
            for (int t = 1; t < PT_COUNT; t++)
                result.Add(((PieceType)t, body.Probs[t]));
            return result.OrderByDescending(x => x.Item2).Take(count).ToList();
        }

        public float GetTrapRisk(Position pos)
        {
            if (!pos.IsValid) return 0f;
            Body body;
            return _bodies.TryGetValue(pos, out body) ? body.TrapRisk : 0f;
        }

        public void RaiseTrapRisk(Position pos, float risk)
        {
            if (!pos.IsValid) return;
            Body body;
            if (!_bodies.TryGetValue(pos, out body)) return;
            body.TrapRisk = Math.Max(body.TrapRisk, Math.Max(0f, Math.Min(1f, risk)));
        }

        public void DecayTrapRisk(float factor)
        {
            foreach (var body in _bodies.Values)
            {
                body.TrapRisk *= factor;
                if (body.TrapRisk < 0.02f) body.TrapRisk = 0f;
            }
        }

        public Position? GetLikelyKingPosition()
        {
            Body best = null;
            float bestProb = -1f;
            foreach (var body in _bodies.Values)
            {
                if (body.Probs[(int)PieceType.King] > bestProb)
                {
                    bestProb = body.Probs[(int)PieceType.King];
                    best = body;
                }
            }
            return best != null && bestProb > 0.001f ? best.Pos : (Position?)null;
        }

        public void NotifyMove(Move move, PieceType visibleType, MoveLeakType leakType, Board boardBefore,
            bool baitMove = false)
        {
            if (boardBefore == null || !move.From.IsValid || !move.To.IsValid) return;
            EnsureInitialized(boardBefore);

            Body body = GetOrCreateBody(move.From, visibleType);
            if (body == null) return;

            if (move.IsCastling)
            {
                body.PossibleMask = 1 << (int)PieceType.King;
                ApplyWeights(body, PieceType.King);
                int rookToX = move.To.X > move.From.X ? move.To.X - 1 : move.To.X + 1;
                int dir = move.To.X > move.From.X ? 1 : -1;

                // 车不一定在角落：在本方信念里挑这一侧最可能是车的身体，挑不到就不动，绝不猜角落。
                Body rookBody = null;
                float bestRook = -1f;
                for (int x = move.From.X + dir; x >= 0 && x < 8; x += dir)
                {
                    if (!_bodies.TryGetValue(new Position(x, move.From.Y), out var candidate)) continue;
                    float p = candidate.Probs[(int)PieceType.Rook];
                    if (p > bestRook)
                    {
                        bestRook = p;
                        rookBody = candidate;
                    }
                }
                if (rookBody != null)
                    MoveBody(rookBody.Pos, new Position(rookToX, move.From.Y), PieceType.Rook);
            }
            else
            {
                var allowed = GetTypesThatCanMove(boardBefore, move.From, move.To);
                if (allowed.Count > 0)
                {
                    int allowedMask = 0;
                    foreach (var type in allowed) allowedMask |= 1 << (int)type;
                    // 可能集只收紧不放宽：若与历史证据冲突，以本次实际走子为准。
                    body.PossibleMask = (body.PossibleMask & allowedMask) != 0
                        ? body.PossibleMask & allowedMask
                        : allowedMask;

                    // 受威胁仍不撤：玩家完全可能是在用真王钓大鱼，不能把“王”这一项直接按噪音压掉。
                    ApplyWeights(body, allowed, baitMove ? 0.25f : TYPE_EPSILON);
                }
                // 走法几何无法识别（规则特例）时保持原信念，绝不按可见标签武断收窄。
            }

            RelocateBody(body, move.To, visibleType);
            EnforceCountConstraint();
        }

        // 吃子只公开“少了一个子”，不公开真型。
        // 棋局没有因此结束是公开信息：被吃的必定不是王，按“非王”条件化后再扣减期望数量。
        public void NotifyCapture(Position pos)
        {
            if (pos.IsValid && _bodies.TryGetValue(pos, out Body body))
            {
                float remainingMass = 0f;
                for (int t = 1; t < PT_COUNT; t++)
                {
                    if (t == (int)PieceType.King) continue;
                    remainingMass += body.Probs[t];
                }

                if (remainingMass > 0.0001f)
                {
                    for (int t = 1; t < PT_COUNT; t++)
                    {
                        float share = t == (int)PieceType.King ? 0f : body.Probs[t] / remainingMass;
                        _remaining[t] = Math.Max(0f, _remaining[t] - share);
                    }
                }
                else
                {
                    float share = 1f / (PT_COUNT - 1);
                    for (int t = 1; t < PT_COUNT; t++)
                    {
                        if (t == (int)PieceType.King) continue;
                        _remaining[t] = Math.Max(0f, _remaining[t] - share);
                    }
                }

                _bodies.Remove(pos);
            }

            EnforceCountConstraint();
        }

        public void NotifyPromotion(Position pos, PieceType? promotionType = null)
        {
            if (!pos.IsValid || !_bodies.TryGetValue(pos, out Body body)) return;

            // 升变类型是公开信息（棋谱与联机包都带 =Q/R/B/N），不是隐藏本态。
            // 旧实现把车/马/象/后各记 0.25，真后升变后全盘“后”的期望数量长期偏低，
            // 王安全和挂子判定都会把真后当成 3% 的路人（G24/G35）。
            if (promotionType.HasValue &&
                (promotionType.Value == PieceType.Queen || promotionType.Value == PieceType.Rook ||
                 promotionType.Value == PieceType.Bishop || promotionType.Value == PieceType.Knight))
            {
                var promoted = promotionType.Value;
                for (int t = 1; t < PT_COUNT; t++)
                    body.Probs[t] = t == (int)promoted ? 1f : 0f;
                body.PossibleMask = 1 << (int)promoted;

                _remaining[(int)PieceType.Pawn] = Math.Max(0f, _remaining[(int)PieceType.Pawn] - 1f);
                _remaining[(int)promoted] += 1f;

                EnforceCountConstraint();
                return;
            }

            body.PossibleMask &= (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Rook) |
                                 (1 << (int)PieceType.Bishop) | (1 << (int)PieceType.Knight);
            if (body.PossibleMask == 0)
                body.PossibleMask = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Rook) |
                                    (1 << (int)PieceType.Bishop) | (1 << (int)PieceType.Knight);

            body.Probs[(int)PieceType.King] = 0f;
            body.Probs[(int)PieceType.Pawn] *= 0.01f;
            body.Probs[(int)PieceType.Queen] += 0.30f;
            body.Probs[(int)PieceType.Rook] += 0.25f;
            body.Probs[(int)PieceType.Bishop] += 0.225f;
            body.Probs[(int)PieceType.Knight] += 0.225f;

            _remaining[(int)PieceType.Pawn] = Math.Max(0f, _remaining[(int)PieceType.Pawn] - 1f);
            _remaining[(int)PieceType.Queen] += 0.25f;
            _remaining[(int)PieceType.Rook] += 0.25f;
            _remaining[(int)PieceType.Bishop] += 0.25f;
            _remaining[(int)PieceType.Knight] += 0.25f;

            EnforceCountConstraint();
        }

        private Body GetOrCreateBody(Position pos, PieceType visibleType)
        {
            if (_bodies.TryGetValue(pos, out Body body)) return body;

            body = new Body { Pos = pos, VisibleType = visibleType };
            float count = Math.Max(1, _bodies.Count + 1);
            for (int t = 1; t < PT_COUNT; t++)
                body.Probs[t] = _remaining[t] / count;
            NormalizeRow(body);
            _bodies[pos] = body;
            return body;
        }

        private void ApplyWeights(Body body, PieceType onlyType)
        {
            for (int t = 1; t < PT_COUNT; t++)
                body.Probs[t] *= t == (int)onlyType ? 1f : TYPE_EPSILON;
            NormalizeRow(body);
        }

        private void ApplyWeights(Body body, HashSet<PieceType> allowed)
        {
            ApplyWeights(body, allowed, TYPE_EPSILON);
        }

        private void ApplyWeights(Body body, HashSet<PieceType> allowed, float kingDownFactor)
        {
            for (int t = 1; t < PT_COUNT; t++)
            {
                if (allowed.Contains((PieceType)t)) continue;
                float factor = t == (int)PieceType.King ? kingDownFactor : TYPE_EPSILON;
                body.Probs[t] *= factor;
            }
            NormalizeRow(body);
        }

        private void RelocateBody(Body body, Position to, PieceType visibleType)
        {
            _bodies.Remove(body.Pos);
            _bodies.Remove(to);
            body.Pos = to;
            body.VisibleType = visibleType;
            body.TrapRisk = 0f;
            body.HasMoved = true;
            _bodies[to] = body;
        }

        private void MoveBody(Position from, Position to, PieceType visibleType)
        {
            if (!_bodies.TryGetValue(from, out Body body)) return;
            _bodies.Remove(from);
            _bodies.Remove(to);
            body.Pos = to;
            body.VisibleType = visibleType;
            body.HasMoved = true;
            body.PossibleMask &= 1 << (int)PieceType.Rook;
            if (body.PossibleMask == 0) body.PossibleMask = 1 << (int)PieceType.Rook;
            _bodies[to] = body;
        }

        private HashSet<PieceType> GetTypesThatCanMove(Board board, Position from, Position to)
        {
            var types = new HashSet<PieceType>();
            var piece = board.GetPiece(from);
            if (piece == null || piece.Color != _enemyColor) return types;

            foreach (var type in AllTypes)
            {
                try
                {
                    if (MoveGenerator.IsValidMoveForPieceType(board, from, to, type, _enemyColor))
                        types.Add(type);
                }
                catch
                {
                }
            }
            return types;
        }

        private void EnforceCountConstraint()
        {
            if (_bodies.Count == 0) return;

            // 数量只约束“期望总数”，不能把走子证据排除掉的本态按缺口重新灌回去。
            // 旧实现无条件把每个本态的总和缩放到剩余数量：吃到一个“像后”的子后，
            // _remaining[后] 会被扣到 0，然后全盘清零“后”，连按步子只能是车/后的真后都消失
            // （G1：e1 真后沿 e 线吃王，王安全扫描却看不到它）。
            // 现在：超出剩余数量才缩；不足时只在“可能集允许该本态”的子里补缺口。
            for (int iteration = 0; iteration < 6; iteration++)
            {
                for (int t = 1; t < PT_COUNT; t++)
                {
                    float sum = 0f;
                    foreach (var body in _bodies.Values) sum += Math.Max(0f, body.Probs[t]);

                    float remaining = Math.Max(0f, _remaining[t]);
                    if (remaining <= 0.0001f)
                    {
                        // 数量已确定耗尽（只有“确定被吃”才会到这里，见 NotifyCapture 的保底）。
                        foreach (var body in _bodies.Values) body.Probs[t] = 0f;
                        continue;
                    }

                    if (sum > remaining)
                    {
                        float scale = remaining / sum;
                        foreach (var body in _bodies.Values) body.Probs[t] *= scale;
                        continue;
                    }

                    float deficit = remaining - sum;
                    if (deficit <= 0.0001f) continue;

                    float weightSum = 0f;
                    foreach (var body in _bodies.Values)
                    {
                        if ((body.PossibleMask & (1 << t)) == 0) continue;
                        weightSum += 0.01f + Math.Max(0f, body.Probs[t]);
                    }
                    if (weightSum <= 0.000001f) continue;

                    foreach (var body in _bodies.Values)
                    {
                        if ((body.PossibleMask & (1 << t)) == 0) continue;
                        body.Probs[t] += deficit * (0.01f + Math.Max(0f, body.Probs[t])) / weightSum;
                    }
                }

                foreach (var body in _bodies.Values) NormalizeRow(body);
            }
        }

        private void NormalizeRow(Body body)
        {
            float sum = 0f;
            for (int t = 1; t < PT_COUNT; t++) sum += Math.Max(0f, body.Probs[t]);

            if (sum <= 0.0001f)
            {
                int available = 0;
                for (int t = 1; t < PT_COUNT; t++)
                    if (_remaining[t] > 0.0001f) available++;

                for (int t = 1; t < PT_COUNT; t++)
                    body.Probs[t] = _remaining[t] > 0.0001f && available > 0 ? 1f / available : 0f;
                return;
            }

            for (int t = 1; t < PT_COUNT; t++)
                body.Probs[t] = Math.Max(0f, body.Probs[t]) / sum;
        }

        // typeBias(pos, type) 用于把玩家画像转成采样倾角；返回 1 表示中性。
        public Board SampleBoard(Random rng, int sampleIndex = -1, int sampleCount = 0,
            Func<Position, PieceType, float> typeBias = null)
        {
            var board = new Board();
            var bodies = _bodies.Values.ToList();
            if (bodies.Count == 0) return board;

            int kingIndex = PickKingBody(bodies, rng, sampleIndex, sampleCount);

            var budget = (float[])_remaining.Clone();
            budget[(int)PieceType.King] = 0f;

            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                PieceType type;
                if (i == kingIndex)
                {
                    type = PieceType.King;
                }
                else
                {
                    type = SampleType(body.Pos, body, budget, rng, typeBias);
                    budget[(int)type] = Math.Max(0f, budget[(int)type] - 1f);
                }

                var piece = new Piece(type, body.VisibleType, _enemyColor)
                {
                    HasMoved = body.HasMoved,
                    BeliefTypeMask = BuildTypeMask(body, type),
                    PossibleTypeMask = body.PossibleMask
                };
                board.SetPiece(body.Pos, piece);
            }

            return board;
        }

        // 残局穷举专用：不做随机抽样，直接把每个位置判给当前概率最高的本态。
        // 这仍然是“信念塌缩”，不是读取玩家真实本态；数量约束与唯一王保证始终成立。
        public Board SampleMostLikelyBoard()
        {
            var board = new Board();
            var bodies = _bodies.Values.ToList();
            if (bodies.Count == 0) return board;

            // 越自信的位置越先挑选稀缺本态，避免低概率位置抢走最后一个后/车。
            bodies.Sort((a, b) => MaxProbability(b).CompareTo(MaxProbability(a)));

            int kingIndex = -1;
            float bestKing = -1f;
            for (int i = 0; i < bodies.Count; i++)
            {
                float p = bodies[i].Probs[(int)PieceType.King];
                if (p > bestKing)
                {
                    bestKing = p;
                    kingIndex = i;
                }
            }

            var budget = (float[])_remaining.Clone();
            budget[(int)PieceType.King] = 0f;

            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                PieceType type;
                if (i == kingIndex)
                {
                    type = PieceType.King;
                }
                else
                {
                    type = MostLikelyType(body, budget);
                    budget[(int)type] = Math.Max(0f, budget[(int)type] - 1f);
                }

                var piece = new Piece(type, body.VisibleType, _enemyColor)
                {
                    HasMoved = body.HasMoved,
                    BeliefTypeMask = BuildTypeMask(body, type),
                    PossibleTypeMask = body.PossibleMask
                };
                board.SetPiece(body.Pos, piece);
            }

            return board;
        }

        // 对抗采样：在数量约束内，把“最可能威胁我方真王”的可能本态分配给敌子。
        // 16 个随机世界很难同时抽中“两枚藏车都在王线上”这类联合配置，尾部风险因此看不见杀棋。
        // 这里用公开概率与可能集构造一个最坏世界（棋盘只用于几何遮挡，绝不读敌子 TrueType），
        // 放进采样首位，让根搜索至少在一个世界里面对最危险的合法配置。
        // 对抗世界只摆“可信”的危险本态：低于该概率的类型不参与贪心分配，
        // 否则 1% 的后也会被摆出来判我们丢王，变成过度防守。
        private const float ThreatAssignMinProb = 0.05f;

        public Board SampleKingThreatBoard(Board board, Position ownKing)
        {
            var result = new Board();
            var bodies = _bodies.Values.ToList();
            if (bodies.Count == 0) return result;

            int kingIndex = 0;
            float bestKing = -1f;
            for (int i = 0; i < bodies.Count; i++)
            {
                float p = bodies[i].Probs[(int)PieceType.King];
                if (p > bestKing) { bestKing = p; kingIndex = i; }
            }

            var budget = (float[])_remaining.Clone();
            budget[(int)PieceType.King] = 0f;

            var candidates = new List<(int bodyIndex, PieceType type, double weight)>();
            for (int i = 0; i < bodies.Count; i++)
            {
                if (i == kingIndex) continue;
                var body = bodies[i];
                foreach (var type in AllTypes)
                {
                    if (type == PieceType.King) continue;
                    if ((body.PossibleMask & (1 << (int)type)) == 0) continue;
                    float p = Math.Max(0f, body.Probs[(int)type]);
                    if (p < ThreatAssignMinProb) continue;
                    if (budget[(int)type] <= 0.0001f) continue;

                    double weight = p * TypeThreatValue(type);
                    if (MoveGenerator.CanPieceAttackSquare(board, body.Pos, type, _enemyColor, ownKing))
                        weight += 0.9 * TypeThreatValue(type);
                    else if (TypeAlignedWithKing(body.Pos, ownKing, type))
                        weight += 0.35 * TypeThreatValue(type);

                    int dist = Math.Max(Math.Abs(body.Pos.X - ownKing.X), Math.Abs(body.Pos.Y - ownKing.Y));
                    if (dist <= 2) weight += 0.2 * TypeThreatValue(type);
                    candidates.Add((i, type, weight));
                }
            }

            candidates.Sort((a, b) => b.weight.CompareTo(a.weight));

            var assigned = new PieceType[bodies.Count];
            for (int i = 0; i < assigned.Length; i++) assigned[i] = PieceType.None;
            assigned[kingIndex] = PieceType.King;

            foreach (var cand in candidates)
            {
                if (assigned[cand.bodyIndex] != PieceType.None) continue;
                if (budget[(int)cand.type] <= 0.0001f) continue;
                assigned[cand.bodyIndex] = cand.type;
                budget[(int)cand.type] = Math.Max(0f, budget[(int)cand.type] - 1f);
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                var type = assigned[i];
                if (type == PieceType.None)
                {
                    int t = MostLikelyAllowedType(body);
                    type = t > 0 ? (PieceType)t : PieceType.Pawn;
                }
                var piece = new Piece(type, body.VisibleType, _enemyColor)
                {
                    HasMoved = body.HasMoved,
                    BeliefTypeMask = BuildTypeMask(body, type),
                    PossibleTypeMask = body.PossibleMask
                };
                result.SetPiece(body.Pos, piece);
            }

            return result;
        }

        private int MostLikelyAllowedType(Body body)
        {
            int bestType = -1;
            float best = -1f;
            for (int t = 1; t < PT_COUNT; t++)
            {
                if (t == (int)PieceType.King) continue;
                if ((body.PossibleMask & (1 << t)) == 0) continue;
                float p = Math.Max(0f, body.Probs[t]);
                if (p > best) { best = p; bestType = t; }
            }
            return bestType;
        }

        private static double TypeThreatValue(PieceType type)
        {
            switch (type)
            {
                case PieceType.Queen: return 1.0;
                case PieceType.Rook: return 0.9;
                case PieceType.Knight: return 0.55;
                case PieceType.Bishop: return 0.5;
                case PieceType.Pawn: return 0.25;
                default: return 0.0;
            }
        }

        private static bool TypeAlignedWithKing(Position from, Position king, PieceType type)
        {
            if (type != PieceType.Queen && type != PieceType.Rook && type != PieceType.Bishop)
                return false;
            int dx = king.X - from.X;
            int dy = king.Y - from.Y;
            if (type == PieceType.Rook) return dx == 0 || dy == 0;
            if (type == PieceType.Bishop) return Math.Abs(dx) == Math.Abs(dy);
            return dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy);
        }

        private static float MaxProbability(Body body)
        {
            float max = 0f;
            for (int t = 1; t < PT_COUNT; t++)
                if (body.Probs[t] > max) max = body.Probs[t];
            return max;
        }

        private static PieceType MostLikelyType(Body body, float[] budget)
        {
            int bestType = -1;
            float bestWeight = -1f;
            for (int t = 1; t < PT_COUNT; t++)
            {
                if (t == (int)PieceType.King) continue;
                float weight = body.Probs[t] * (budget[t] > 0.0001f ? 1f : 0.001f);
                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    bestType = t;
                }
            }
            return bestType > 0 ? (PieceType)bestType : PieceType.Pawn;
        }

        private static int BuildTypeMask(Body body, PieceType sampledType)
        {
            int mask = 0;
            for (int t = 1; t < PT_COUNT; t++)
                if (body.Probs[t] >= BeliefMaskThreshold) mask |= 1 << t;
            // 采样到的本态本身就是一种可能，即使概率低于阈值也要纳入。
            mask |= 1 << (int)sampledType;
            return mask;
        }

        private int PickKingBody(List<Body> bodies, Random rng, int sampleIndex, int sampleCount)
        {
            float total = 0f;
            for (int i = 0; i < bodies.Count; i++)
                total += Math.Max(0f, bodies[i].Probs[(int)PieceType.King]);

            if (total <= 0.0001f)
                return rng.Next(bodies.Count);

            float unit = (float)rng.NextDouble();
            if (sampleIndex >= 0 && sampleCount > 1)
            {
                // 低差异序列（黄金比）代替等距分层：迭代加深经常在中途超时，只评估前 k 个采样。
                // 等距分层会让前 k 个世界的“王”集中在棋子列表的前 k/N 段（空间偏置），
                // 黄金比把前 k 个样本均匀铺满整张棋盘，部分采样下的吃王期望不再系统性虚高。
                unit = (sampleIndex * 0.6180339887f + unit) % 1f;
            }

            float roll = (float)unit * total;
            float acc = 0f;
            for (int i = 0; i < bodies.Count; i++)
            {
                acc += Math.Max(0f, bodies[i].Probs[(int)PieceType.King]);
                if (roll <= acc) return i;
            }

            return bodies.Count - 1;
        }

        private PieceType SampleType(Position pos, Body body, float[] budget, Random rng,
            Func<Position, PieceType, float> typeBias)
        {
            float total = 0f;
            for (int t = 1; t < PT_COUNT; t++)
            {
                if (t == (int)PieceType.King && budget[t] <= 0.0001f) continue;
                if (budget[t] > 0.0001f && body.Probs[t] > 0.00001f)
                    total += Weight(t);
            }

            if (total > 0.0001f)
            {
                float roll = (float)rng.NextDouble() * total;
                float cumulative = 0f;
                for (int t = 1; t < PT_COUNT; t++)
                {
                    if (t == (int)PieceType.King && budget[t] <= 0.0001f) continue;
                    if (budget[t] <= 0.0001f || body.Probs[t] <= 0.00001f) continue;
                    cumulative += Weight(t);
                    if (roll <= cumulative) return (PieceType)t;
                }
            }

            int bestType = -1;
            float bestProb = -1f;
            for (int t = 1; t < PT_COUNT; t++)
            {
                if (t == (int)PieceType.King && budget[t] <= 0.0001f) continue;
                float weight = body.Probs[t] * (budget[t] > 0.0001f ? 1f : 0.001f);
                if (typeBias != null) weight *= Math.Max(0.01f, typeBias(pos, (PieceType)t));
                if (weight > bestProb)
                {
                    bestProb = weight;
                    bestType = t;
                }
            }
            return bestType > 0 ? (PieceType)bestType : PieceType.Pawn;

            float Weight(int t)
            {
                float weight = body.Probs[t];
                if (typeBias != null) weight *= Math.Max(0.01f, typeBias(pos, (PieceType)t));
                return weight;
            }
        }

        private static void Shuffle<T>(List<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public void PrintBeliefs()
        {
            Console.WriteLine("信念概率分布 (显示最高概率棋子):");
            for (int y = 7; y >= 0; y--)
            {
                Console.Write($"{y + 1} ");
                for (int x = 0; x < 8; x++)
                {
                    var pos = new Position(x, y);
                    if (!_bodies.TryGetValue(pos, out Body body))
                    {
                        Console.Write(".  ");
                        continue;
                    }

                    float maxProb = 0f;
                    PieceType bestType = PieceType.None;
                    for (int t = 1; t < PT_COUNT; t++)
                    {
                        if (body.Probs[t] > maxProb)
                        {
                            maxProb = body.Probs[t];
                            bestType = (PieceType)t;
                        }
                    }

                    Console.Write($"{bestType.ToSymbol()}{(int)(maxProb * 100)} ");
                }
                Console.WriteLine();
            }
        }
    }
}
