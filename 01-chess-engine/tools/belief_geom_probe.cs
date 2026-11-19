using System;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Bench
{
    // 一次性探针：复现 G35 里“敌后沿 e 线 5 格吃子，信念仍保留王/兵概率”的现象。
    internal static class BeliefGeomProbe
    {
        private static void Main()
        {
            var board = new Board();
            board.SetPiece(new Position(0, 7), new Piece(PieceType.King, PieceType.King, PieceColor.White));
            board.SetPiece(new Position(3, 7), new Piece(PieceType.King, PieceType.King, PieceColor.Black));
            board.SetPiece(new Position(4, 6), new Piece(PieceType.Queen, PieceType.Queen, PieceColor.Black));

            var queen = new Piece(PieceType.Queen, PieceType.Pawn, PieceColor.White) { HasMoved = true };
            board.SetPiece(new Position(3, 1), queen);

            var belief = new BeliefState(PieceColor.Black);
            belief.ResetFromBoard(board);

            // 先走一格横线（王/车/后都合法），复刻真实对局的先验混乱。
            belief.NotifyMove(new Move(new Position(3, 1), new Position(4, 1)), PieceType.Pawn,
                MoveLeakType.Suspicious, board);
            Print(belief, new Position(4, 1), "d2->e2 后");

            var afterFirst = board.Clone();
            afterFirst.ClearPiece(new Position(3, 1));
            afterFirst.SetPiece(new Position(4, 1),
                new Piece(PieceType.Queen, PieceType.Pawn, PieceColor.White) { HasMoved = true });

            // 再沿 e 线走 5 格吃掉黑后（只有车/后合法）。
            belief.NotifyMove(new Move(new Position(4, 1), new Position(4, 6)), PieceType.Pawn,
                MoveLeakType.Suspicious, afterFirst);
            Print(belief, new Position(4, 6), "e2->e7 后");
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
                              $"P(P)={belief.GetProbability(pos, PieceType.Pawn):F3}");
        }
    }
}
