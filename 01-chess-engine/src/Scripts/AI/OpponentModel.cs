using System;
using System.Collections.Generic;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // L2：对手是单层贪婪，且只按它自己的信念（镜像）估值。
    // 预测它在我方走完后的最佳吃子，用真实局面结算这次交换，
    // 与“它若全知时的最优吃子”之差就是可利用收益（它因误判而走了对我们更有利的交换）。
    // 绝不读取对手真实本态：敌方真型一律来自信念抽样。
    internal static class OpponentModel
    {
        private const double KingJackpot = 20000.0;
        private const double CaptureGreed = 1.0;
        private const double TradeReluctance = 1.0;

        public static double ExploitationGain(Board after, BeliefState mirrorAfter,
            BeliefState enemyBelief, PieceColor us)
        {
            if (after == null || mirrorAfter == null || enemyBelief == null) return 0.0;
            PieceColor them = us == PieceColor.White ? PieceColor.Black : PieceColor.White;

            // 抽一个对手真型世界（最可能，稳定），覆盖克隆盘上的敌子真型。
            var world = after.Clone();
            var sampled = enemyBelief.SampleMostLikelyBoard();
            foreach (var (pos, piece) in world.GetPiecesOfColor(them))
                piece.TrueType = sampled.GetPiece(pos)?.TrueType ?? PieceType.Pawn;

            var captures = new List<Move>();
            foreach (var move in MoveGenerator.GeneratePseudoLegalMoves(world, them))
            {
                if (move.IsCastling) continue;
                var victim = world.GetPiece(move.To);
                if (victim == null || victim.Color != us || victim.TrueType == PieceType.King) continue;
                var attacker = world.GetPiece(move.From);
                if (attacker == null || attacker.TrueType == PieceType.King) continue;
                captures.Add(move);
            }
            if (captures.Count == 0) return 0.0;

            double greedyNet = double.NegativeInfinity;
            double greedyScore = double.NegativeInfinity;
            double trueBestNet = double.PositiveInfinity;

            foreach (var move in captures)
            {
                var attacker = world.GetPiece(move.From);
                var victim = world.GetPiece(move.To);
                int attackerValue = Math.Min(attacker.TrueType.GetValue(), 900);
                int victimTrue = Math.Min(victim.TrueType.GetValue(), 900);

                bool weRecapture = CanRecapture(world, move.To, us);
                double net = weRecapture ? attackerValue - victimTrue : -victimTrue;
                if (net < trueBestNet) trueBestNet = net;

                // 对手按它的信念给这次吃子打分（与 HumanModel.EvaluateCapture 同口径）。
                float perceived = mirrorAfter.GetExpectedMaterialValue(move.To);
                if (perceived < 1f) perceived = 200f;
                float pKing = mirrorAfter.GetProbability(move.To, PieceType.King);
                double gain = pKing * KingJackpot + perceived * CaptureGreed;
                double recapture = PerceivedRecapture(after, mirrorAfter, move.To, us);
                double score = gain - recapture * attackerValue;
                if (attackerValue > perceived + 80f && pKing < 0.15f)
                    score -= (attackerValue - perceived) * 0.65 * TradeReluctance;

                if (score > greedyScore)
                {
                    greedyScore = score;
                    greedyNet = net;
                }
            }

            if (double.IsInfinity(greedyNet) || double.IsInfinity(trueBestNet)) return 0.0;
            return greedyNet - trueBestNet;
        }

        // 真实几何：我方是否有子能吃回该格（用我方真型，我们知道自己）。
        private static bool CanRecapture(Board board, Position square, PieceColor us)
        {
            foreach (var (pos, piece) in board.GetPiecesOfColor(us))
            {
                if (pos == square) continue;
                if (MoveGenerator.CanPieceAttackSquare(board, pos, piece.TrueType, us, square))
                    return true;
            }
            return false;
        }

        // 对手视角的回吃概率：它按镜像里我方子的可能本态数威胁（与 HumanModel.RecaptureProbability 同口径）。
        private static double PerceivedRecapture(Board after, BeliefState mirror, Position square, PieceColor us)
        {
            int threats = 0;
            foreach (var (pos, _) in after.GetPiecesOfColor(us))
            {
                if (pos == square) continue;
                int mask = mirror.GetTypeMask(pos, 0.2f);
                if (mask == 0) continue;
                for (int t = 1; t < 7; t++)
                {
                    if ((mask & (1 << t)) == 0) continue;
                    if (MoveGenerator.CanPieceAttackSquare(after, pos, (PieceType)t, us, square))
                    {
                        threats++;
                        break;
                    }
                }
            }
            return Math.Min(0.9, threats * 0.3);
        }
    }
}