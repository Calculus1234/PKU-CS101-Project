using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Bench
{
    // 一次性探针：复现 G7 的“伪装象真后 c1->e3->h3 吃子”后的可能集，
    // 检查王安全掩码是否仍保留“后/车”（h3->h6 直线攻击能否被看见）。
    internal static class BeliefGeomProbe2
    {
        private static void Main()
        {
            var board = new Board();
            board.SetPiece(new Position(4, 7), new Piece(PieceType.King, PieceType.King, PieceColor.Black));

            var queen = new Piece(PieceType.Queen, PieceType.Bishop, PieceColor.White) { HasMoved = true };
            board.SetPiece(new Position(2, 0), queen);

            var belief = new BeliefState(PieceColor.Black);
            belief.ResetFromBoard(board);

            // c1->e3：两格斜走（非吃子），合法本态只有 象/后。
            belief.NotifyMove(new Move(new Position(2, 0), new Position(4, 2)), PieceType.Bishop,
                MoveLeakType.Suspicious, board);
            Print(belief, new Position(4, 2), "c1->e3");

            var afterFirst = board.Clone();
            afterFirst.ClearPiece(new Position(2, 0));
            afterFirst.SetPiece(new Position(4, 2),
                new Piece(PieceType.Queen, PieceType.Bishop, PieceColor.White) { HasMoved = true });

            // 放一枚黑象在 h3 作为被吃目标。
            afterFirst.SetPiece(new Position(7, 2), new Piece(PieceType.Bishop, PieceType.Bishop, PieceColor.Black));

            // e3->h3：三格横走吃子，合法本态只有 车/后；若历史可能集为 {象,后}，交集应只剩 后。
            belief.NotifyMove(new Move(new Position(4, 2), new Position(7, 2)), PieceType.Bishop,
                MoveLeakType.Suspicious, afterFirst);
            Print(belief, new Position(7, 2), "e3->h3 吃子");

            // 再走一格横线（h3->h4），检查可能集是否稳定保留 后。
            var afterSecond = afterFirst.Clone();
            afterSecond.ClearPiece(new Position(4, 2));
            afterSecond.ClearPiece(new Position(7, 2));
            afterSecond.SetPiece(new Position(7, 3),
                new Piece(PieceType.Queen, PieceType.Bishop, PieceColor.White) { HasMoved = true });
            belief.NotifyMove(new Move(new Position(7, 2), new Position(7, 3)), PieceType.Bishop,
                MoveLeakType.Suspicious, afterSecond);
            Print(belief, new Position(7, 3), "h3->h4");
        }

        private static void Print(BeliefState belief, Position pos, string tag)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (type, p) in belief.GetTopTypes(pos, 6))
                if (p > 0.01f) sb.Append($"{type}:{p:F3} ");
            Console.WriteLine($"{tag} top={sb} mask=0x{belief.GetSafetyMask(pos):X2} " +
                              $"P(K)={belief.GetProbability(pos, PieceType.King):F3} " +
                              $"P(Q)={belief.GetProbability(pos, PieceType.Queen):F3} " +
                              $"P(R)={belief.GetProbability(pos, PieceType.Rook):F3} " +
                              $"P(B)={belief.GetProbability(pos, PieceType.Bishop):F3}");
        }
    }
}
