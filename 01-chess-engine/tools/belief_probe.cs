using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Bench
{
    internal static class BeliefProbe
    {
        private static void Dump(BeliefState belief, Position pos, string tag)
        {
            Console.Write($"{tag} {pos}: ");
            for (int t = 1; t < 7; t++)
                Console.Write($"{((PieceType)t).ToChinese()}={belief.GetProbability(pos, (PieceType)t):P0} ");
            Console.WriteLine($" mask=0x{belief.GetSafetyMask(pos):X3}");
        }

        private static void Main()
        {
            // 极简残局：黑方真后藏在 d7（公示“兵”），白方兵在 d2。
            // 黑后 d7xd2 竖直吃五格，几何上只可能是后/车。
            var board = new Board();
            board.SetPiece(new Position(3, 6), new Piece(PieceType.Queen, PieceType.Pawn, PieceColor.Black));
            board.SetPiece(new Position(3, 1), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White));

            var belief = new BeliefState(PieceColor.White);
            belief.ResetFromBoard(board);
            Dump(belief, new Position(3, 6), "初始");

            var move = new Move(new Position(3, 6), new Position(3, 1));
            belief.NotifyMove(move, PieceType.Pawn, MoveLeakType.Suspicious, board);
            Dump(belief, new Position(3, 1), "走子后");

            // 再吃掉白方多个子，制造数量缺口，观察是否把“兵”灌回几何上不可能的身体。
            for (int i = 0; i < 4; i++)
            {
                var b = new Board();
                var from = new Position(i, 6);
                b.SetPiece(from, new Piece(PieceType.Bishop, PieceType.Pawn, PieceColor.Black));
                b.SetPiece(new Position(i, 1), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White));
                var m = new Move(from, new Position(i, 1));
                belief.NotifyMove(m, PieceType.Pawn, MoveLeakType.Suspicious, b);
                belief.NotifyCapture(new Position(i, 1));
                Dump(belief, new Position(i, 1), $"第{i + 1}轮吃子后");
            }
        }
    }
}
