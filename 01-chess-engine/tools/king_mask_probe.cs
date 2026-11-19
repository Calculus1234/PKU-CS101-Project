using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Bench
{
    // 探针：对方视角下我方“王”逐步走子后，PossibleMask 为何会变成 0x06（后|车）。
    internal static class KingMaskProbe
    {
        private static void Main()
        {
            var board = new Board();
            board.SetPiece(new Position(4, 0), new Piece(PieceType.King, PieceType.King, PieceColor.White));
            board.SetPiece(new Position(4, 7), new Piece(PieceType.King, PieceType.King, PieceColor.Black));
            board.SetPiece(new Position(0, 0), new Piece(PieceType.Rook, PieceType.Rook, PieceColor.White));
            board.SetPiece(new Position(7, 7), new Piece(PieceType.Rook, PieceType.Rook, PieceColor.Black));

            var belief = new BeliefState(PieceColor.Black); // 黑方视角，跟踪白子
            belief.ResetFromBoard(board);
            Print(belief, new Position(4, 0), "初始");

            var moves = new[]
            {
                new Move(new Position(4, 0), new Position(4, 1)),
                new Move(new Position(4, 1), new Position(5, 2)),
                new Move(new Position(5, 2), new Position(5, 3)),
                new Move(new Position(5, 3), new Position(6, 4)),
                new Move(new Position(6, 4), new Position(5, 4))
            };

            var current = board.Clone();
            foreach (var move in moves)
            {
                var pre = current.Clone();
                var allowed = AllowedMask(pre, move.From, move.To);
                current.ExecuteMove(move);
                var reflected = ReflectAllowed(belief, pre, move.From, move.To);
                belief.NotifyMove(move, PieceType.King, MoveLeakType.Disguised, pre);
                Console.WriteLine($"  allowedMask=0x{allowed:X2} reflected={reflected}");
                Print(belief, move.To, $"走 {move}");
            }

            // 对照 1：两格直线（易位未标记时会得到 后|车）
            var b2 = new Board();
            b2.SetPiece(new Position(4, 0), new Piece(PieceType.King, PieceType.King, PieceColor.White));
            b2.SetPiece(new Position(4, 7), new Piece(PieceType.King, PieceType.King, PieceColor.Black));
            var belief2 = new BeliefState(PieceColor.Black);
            belief2.ResetFromBoard(b2);
            var pre2 = b2.Clone();
            var straight = new Move(new Position(4, 0), new Position(4, 2));
            b2.ExecuteMove(straight);
            belief2.NotifyMove(straight, PieceType.King, MoveLeakType.Disguised, pre2);
            Print(belief2, new Position(4, 2), "两格直线(未标记易位)");

            // 对照 2：吃子后的两格直线
            var b3 = new Board();
            b3.SetPiece(new Position(4, 0), new Piece(PieceType.King, PieceType.King, PieceColor.White));
            b3.SetPiece(new Position(4, 7), new Piece(PieceType.King, PieceType.King, PieceColor.Black));
            b3.SetPiece(new Position(4, 2), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.Black));
            var belief3 = new BeliefState(PieceColor.Black);
            belief3.ResetFromBoard(b3);
            var pre3 = b3.Clone();
            var capture = new Move(new Position(4, 0), new Position(4, 2));
            b3.ExecuteMove(capture);
            belief3.NotifyMove(capture, PieceType.King, MoveLeakType.Disguised, pre3);
            Print(belief3, new Position(4, 2), "两格直线吃子");
        }

        private static string ReflectAllowed(BeliefState belief, Board board, Position from, Position to)
        {
            var mi = typeof(BeliefState).GetMethod("GetTypesThatCanMove",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var set = (System.Collections.Generic.HashSet<PieceType>)mi.Invoke(belief, new object[] { board, from, to });
            var sb = new System.Text.StringBuilder();
            int mask = 0;
            foreach (var t in set) { sb.Append(t).Append(','); mask |= 1 << (int)t; }
            return $"0x{mask:X2}[{sb}]";
        }

        private static int AllowedMask(Board board, Position from, Position to)
        {
            int mask = 0;
            foreach (PieceType t in Enum.GetValues(typeof(PieceType)))
            {
                if (t == PieceType.None) continue;
                if (MoveGenerator.IsValidMoveForPieceType(board, from, to, t, PieceColor.White))
                    mask |= 1 << (int)t;
            }
            return mask;
        }

        private static void Print(BeliefState belief, Position pos, string tag)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (type, p) in belief.GetTopTypes(pos, 6))
                if (p > 0.01f) sb.Append($"{type}:{p:F3} ");
            Console.WriteLine($"{tag} mask=0x{belief.GetSafetyMask(pos):X2} top={sb}");
        }
    }
}
