using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Bench
{
    // 一次性诊断：验证“捕获计数约束”会不会把按走子几何本应可能的类型清零。
    internal static class BeliefSoundnessTest
    {
        private static void Main()
        {
            var board = new Board();
            board.InitializeShuffled(PieceColor.Black, new Random(7));

            var belief = new BeliefState(PieceColor.White);
            belief.ResetFromBoard(board);

            // 走一格横线/斜线都会让"后"概率集中，随后被我们吃掉时按比例扣减
            // _remaining[后]；这里连续吃掉 3 个"像后"的子，模拟真实对局中的计数漂移。
            var diagonalMoves = new[]
            {
                (new Position(3, 6), new Position(3, 2)),
                (new Position(6, 6), new Position(6, 2)),
                (new Position(0, 6), new Position(2, 4)),
                (new Position(7, 6), new Position(5, 4)),
                (new Position(2, 6), new Position(4, 4)),
            };

            foreach (var (from, to) in diagonalMoves)
            {
                var b = board.Clone();
                var piece = b.GetPiece(from);
                if (piece == null)
                {
                    Console.WriteLine($"[skip] no piece at {from}");
                    continue;
                }

                var move = new Move(from, to);
                belief.NotifyMove(move, piece.VisibleType, MoveLeakType.Suspicious, b);
                Console.WriteLine($"capture {from}->{to} mask={MaskStr(belief.GetTypeMask(to))} " +
                                  $"P(Q)={belief.GetProbability(to, PieceType.Queen):F3}");
                belief.NotifyCapture(to);
            }

            // 再让另一枚敌子走一条"只能车/后"的长横线。
            var from2 = new Position(1, 6);
            var to2 = new Position(1, 2);
            var board2 = board.Clone();
            var p2 = board2.GetPiece(from2);
            if (p2 != null)
            {
                belief.NotifyMove(new Move(from2, to2), p2.VisibleType, MoveLeakType.Suspicious, board2);
                Console.WriteLine($"rooks-like {from2}->{to2} safetyMask={MaskStr(belief.GetSafetyMask(to2))} " +
                                  $"probMask={MaskStr(belief.GetTypeMask(to2))} " +
                                  $"P(R)={belief.GetProbability(to2, PieceType.Rook):F3} " +
                                  $"P(Q)={belief.GetProbability(to2, PieceType.Queen):F3}");
                PrintTop(belief, to2);
            }
        }

        private static void PrintTop(BeliefState belief, Position pos)
        {
            foreach (var (type, p) in belief.GetTopTypes(pos, 6))
                Console.Write($"{type}:{p:F3} ");
            Console.WriteLine();
        }

        private static string MaskStr(int mask)
        {
            var s = "";
            for (int t = 1; t < 7; t++)
                if ((mask & (1 << t)) != 0) s += (PieceType)t + ",";
            return s;
        }
    }
}
