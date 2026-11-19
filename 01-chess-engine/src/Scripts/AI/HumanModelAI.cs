using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // 人类行为模型：只用于离线对照，不当成正式引擎。
    // 它复刻的是“按走法几何推断本态 + 便宜子吃未知目标 + 王不轻动”的人类决策方式：
    //   1. 敌方信息只来自 BeliefState（几何合法集 + 数量约束），绝不读取真型；
    //   2. 吃子先看感知价值，用大于目标的子去吃未知目标会犹豫；
    //   3. 王不主动走进火力，只在被咬时逃；
    //   4. 不白送子：走完若自己的子落到更便宜的敌对子嘴里要扣分；
    //   5. 对没动过的敌子更愿意用兵/马/象试探（玩家认为那更像王）。
    // 标定目标是“无失误、会算账、不会被伪装吓住”的普通强手，用来检验 AI 的通用强度。
    // 风格参数只调同一套人类启发式的权重，不换决策结构：用来验证 AI 对不同打法的通用性。
    public enum HumanStyle
    {
        Aggressive,
        Defensive,
        Materialist,
        Positional,
        Reckless
    }

    public sealed class HumanModelAI : AIPlayer
    {
        private const double KingLossWeight = 9000.0;
        private const double KingMovePenalty = 2500.0;
        private const double ProbeWeight = 700.0;
        private const double KingJackpot = 12000.0;

        private readonly double _kingLossScale = 1.0;
        private readonly double _kingMoveScale = 1.0;
        private readonly double _probeScale = 1.0;
        private readonly double _jackpotScale = 1.0;
        private readonly double _captureGreed = 1.0;
        private readonly double _tradeReluctance = 1.0;
        private readonly double _exposureScale = 1.0;
        private readonly double _centerScale = 1.0;

        public HumanModelAI(PieceColor color, int seed = 42, int thinkMs = 300,
            HumanStyle style = HumanStyle.Aggressive)
            : base(color, seed, Math.Max(200, thinkMs))
        {
            switch (style)
            {
                case HumanStyle.Defensive:
                    _kingLossScale = 1.9;
                    _kingMoveScale = 2.4;
                    _probeScale = 0.35;
                    _jackpotScale = 0.45;
                    _captureGreed = 1.2;
                    _tradeReluctance = 1.3;
                    _exposureScale = 1.8;
                    break;
                case HumanStyle.Materialist:
                    _kingLossScale = 1.2;
                    _kingMoveScale = 1.0;
                    _probeScale = 0.4;
                    _jackpotScale = 0.35;
                    _captureGreed = 1.6;
                    _tradeReluctance = 0.5;
                    _exposureScale = 1.4;
                    _centerScale = 0.6;
                    break;
                case HumanStyle.Positional:
                    _kingLossScale = 1.3;
                    _kingMoveScale = 1.5;
                    _probeScale = 0.6;
                    _jackpotScale = 0.6;
                    _captureGreed = 0.9;
                    _exposureScale = 1.2;
                    _centerScale = 2.5;
                    break;
                case HumanStyle.Reckless:
                    _kingLossScale = 0.5;
                    _kingMoveScale = 0.3;
                    _probeScale = 1.5;
                    _jackpotScale = 1.4;
                    _captureGreed = 1.1;
                    _tradeReluctance = 0.4;
                    _exposureScale = 0.35;
                    _centerScale = 0.8;
                    break;
            }
        }

        // 对照基准固定成改造前的激进绝杀策略：对手不变，A/B 才能只归因到被测引擎。
        internal override bool InstantKillSafetyEnabled => false;

        public override AISearchSession CreateSearchSession(Board board)
        {
            return new DecisionSession(this, board);
        }

        private sealed class DecisionSession : AISearchSession
        {
            private readonly HumanModelAI _owner;
            private readonly Board _board;
            private bool _done;
            private Move? _result;

            internal DecisionSession(HumanModelAI owner, Board board)
            {
                _owner = owner;
                _board = board;
            }

            public override bool IsFinished => _done;
            public override Move? Result => _result;

            public override bool Advance(int sliceMs)
            {
                if (_done) return true;
                _result = _owner.ChooseMove(_board);
                _done = true;
                return true;
            }
        }

        private Move? ChooseMove(Board board)
        {
            var moves = MoveGenerator.GenerateLegalMoves(board, _color);
            if (moves.Count == 0) return null;

            Move best = moves[0];
            double bestScore = double.NegativeInfinity;
            foreach (var move in moves)
            {
                double score = ScoreMove(board, move);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = move;
                }
            }

            LastSearchDepth = 1;
            LastSearchNodes = moves.Count;
            return best;
        }

        private double ScoreMove(Board board, Move move)
        {
            var mover = board.GetPiece(move.From);
            if (mover == null) return double.NegativeInfinity;

            var after = board.Clone();
            after.ExecuteMove(move);

            double score = 0.0;
            score += EvaluateKingSafety(after, mover);
            score += EvaluateCapture(board, after, move, mover);
            score += EvaluateProbe(after, move, mover);
            score += EvaluateMaterialExposure(after);
            score += EvaluateDevelopment(board, move, mover);

            if (_lastOwnMove.HasValue)
            {
                if (move.From.Equals(_lastOwnMove.Value.From)) score -= 35.0;
                if (move.To.Equals(_lastOwnMove.Value.From)) score -= 70.0;
            }

            return score + _rng.NextDouble() * 0.01;
        }

        // 人类只知道自己的真王在哪，所以这里只看“我的王会不会被任何可能本态吃掉”。
        private double EvaluateKingSafety(Board after, Piece mover)
        {
            var kingPos = after.FindKing(_color);
            if (!kingPos.HasValue) return -KingLossWeight;

            double pAttack = KingAttackProbability(after, kingPos.Value);
            double score = -pAttack * KingLossWeight * _kingLossScale;

            if (mover.TrueType == PieceType.King && pAttack <= 0.15)
                score -= KingMovePenalty * _kingMoveScale;

            return score;
        }

        private double KingAttackProbability(Board board, Position kingPos)
        {
            PieceColor enemy = OpponentColor(_color);
            double survive = 1.0;

            foreach (var (pos, _) in board.GetPiecesOfColor(enemy))
            {
                for (int t = 1; t < 7; t++)
                {
                    float p = _beliefState.GetProbability(pos, (PieceType)t);
                    if (p < 0.04f) continue;
                    if (!MoveGenerator.CanPieceAttackSquare(board, pos, (PieceType)t, enemy, kingPos))
                        continue;

                    // 敌王亲自来吃同样致命，但概率按半权计：人类很少拿王当攻城子。
                    double w = (PieceType)t == PieceType.King ? p * 0.5 : p;
                    survive *= 1.0 - Math.Min(0.9, w);
                }
            }

            return 1.0 - survive;
        }

        private double EvaluateCapture(Board board, Board after, Move move, Piece mover)
        {
            var target = board.GetPiece(move.To);
            if (target == null) return 0.0;

            float pKing = _beliefState.GetProbability(move.To, PieceType.King);
            float perceived = _beliefState.GetExpectedMaterialValue(move.To);
            if (perceived < 1f) perceived = 200f;

            int attackerValue = Math.Min(mover.TrueType.GetValue(), 900);
            double gain = pKing * KingJackpot * _jackpotScale + perceived * _captureGreed;
            double recapture = RecaptureProbability(after, move.To);
            double score = gain - recapture * attackerValue;

            // 用大于目标感知价值的子去吃一个大概率不是王的目标，人类会先收手。
            if (attackerValue > perceived + 80f && pKing < 0.15f)
                score -= (attackerValue - perceived) * 0.65 * _tradeReluctance;

            return score;
        }

        private double RecaptureProbability(Board after, Position square)
        {
            PieceColor enemy = OpponentColor(_color);
            int threats = 0;

            foreach (var (pos, _) in after.GetPiecesOfColor(enemy))
            {
                int mask = _beliefState.GetTypeMask(pos, 0.2f);
                if (mask == 0) continue;

                for (int t = 1; t < 7; t++)
                {
                    if ((mask & (1 << t)) == 0) continue;
                    if (MoveGenerator.CanPieceAttackSquare(after, pos, (PieceType)t, enemy, square))
                    {
                        threats++;
                        break;
                    }
                }
            }

            return Math.Min(0.9, threats * 0.3);
        }

        private double EvaluateProbe(Board after, Move move, Piece mover)
        {
            if (move.IsCastling) return 0.0;

            var attackerType = mover.TrueType;
            if (attackerType != PieceType.Pawn && attackerType != PieceType.Knight &&
                attackerType != PieceType.Bishop)
                return 0.0;

            PieceColor enemy = OpponentColor(_color);
            double best = 0.0;

            foreach (var (pos, piece) in after.GetPiecesOfColor(enemy))
            {
                if (pos == move.To) continue;

                float p = _beliefState.GetProbability(pos, PieceType.King);
                if (!piece.HasMoved) p *= 1.3f;
                if (p < 0.05f) continue;
                if (!MoveGenerator.CanPieceAttackSquare(after, move.To, attackerType, _color, pos))
                    continue;

                double v = p * ProbeWeight * _probeScale;
                if (v > best) best = v;
            }

            return best;
        }

        // 走完后不能把自己的子白送到更便宜或等价的敌子嘴里。
        private double EvaluateMaterialExposure(Board after)
        {
            PieceColor enemy = OpponentColor(_color);
            double risk = 0.0;

            foreach (var (pos, piece) in after.GetPiecesOfColor(_color))
            {
                if (piece.TrueType == PieceType.King) continue;

                bool defended = IsDefended(after, pos);
                int ownValue = Math.Min(piece.TrueType.GetValue(), 900);
                double chance = 0.0;
                int cheapest = int.MaxValue;

                foreach (var (epos, _) in after.GetPiecesOfColor(enemy))
                {
                    int mask = _beliefState.GetTypeMask(epos, 0.15f);
                    if (mask == 0) continue;

                    for (int t = 1; t < 7; t++)
                    {
                        if ((mask & (1 << t)) == 0) continue;

                        var type = (PieceType)t;
                        if (type == PieceType.King) continue;
                        if (!MoveGenerator.CanPieceAttackSquare(after, epos, type, enemy, pos)) continue;

                        float p = _beliefState.GetProbability(epos, type);
                        int value = Math.Min(type.GetValue(), 900);
                        if (value < cheapest) cheapest = value;
                        chance += p * (defended ? 0.55 : 0.9) * 0.6;
                    }
                }

                if (chance <= 0.0) continue;
                if (cheapest == int.MaxValue) cheapest = 100;

                double swing = ownValue - (defended ? 0.7 : 0.05) * cheapest;
                if (swing > 0.0) risk += Math.Min(0.95, chance) * swing;
            }

            return -risk * _exposureScale;
        }

        private bool IsDefended(Board board, Position target)
        {
            foreach (var (pos, piece) in board.GetPiecesOfColor(_color))
            {
                if (pos == target) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, pos, piece.TrueType, _color, target))
                    return true;
            }
            return false;
        }

        private double EvaluateDevelopment(Board board, Move move, Piece mover)
        {
            int centerDist = Math.Abs(move.To.X * 2 - 7) + Math.Abs(move.To.Y * 2 - 7);

            switch (mover.TrueType)
            {
                case PieceType.Knight:
                case PieceType.Bishop:
                    return ((!mover.HasMoved ? 40.0 : 0.0) + (14 - centerDist) * 2.0) * _centerScale;
                case PieceType.Pawn:
                    int forward = _color == PieceColor.White
                        ? move.To.Y - move.From.Y
                        : move.From.Y - move.To.Y;
                    return ((forward > 0 ? 8.0 : 0.0) + (14 - centerDist) * 1.0) * _centerScale;
                case PieceType.Rook:
                case PieceType.Queen:
                    return (14 - centerDist) * 0.5 * _centerScale;
                default:
                    return 0.0;
            }
        }
    }
}
