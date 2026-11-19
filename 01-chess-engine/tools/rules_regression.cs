using System;
using System.Collections.Generic;
using System.Linq;
using ChessDark.Core;
using ChessDark.Logic;

internal static class RulesRegression
{
    private static int _failures;

    private static void Main()
    {
        TestPawnDoubleStep();
        TestCaptureNoReveal();
        TestNoPositionalKingLeak();

        Console.WriteLine(_failures == 0 ? "ALL RULES CHECKS PASSED" : $"FAILURES: {_failures}");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void TestPawnDoubleStep()
    {
        var board = new Board();
        board.SetPiece(new Position(3, 3), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White));
        var moves = MoveGenerator.GeneratePseudoLegalMoves(board, PieceColor.White);
        Check("未动过的兵在 y=3 可以走两格",
            moves.Any(m => m.From == new Position(3, 3) && m.To == new Position(3, 5)));
        Check("未动过的兵在 y=3 可以走一格",
            moves.Any(m => m.From == new Position(3, 3) && m.To == new Position(3, 4)));
        Check("IsValidMoveForPieceType 认可 y=3 两格",
            MoveGenerator.IsValidMoveForPieceType(board, new Position(3, 3), new Position(3, 5),
                PieceType.Pawn, PieceColor.White));

        board.GetPiece(new Position(3, 3)).HasMoved = true;
        moves = MoveGenerator.GeneratePseudoLegalMoves(board, PieceColor.White);
        Check("动过的兵不能再走两格",
            !moves.Any(m => m.From == new Position(3, 3) && m.To == new Position(3, 5)));
        Check("IsValidMoveForPieceType 拒绝动过的兵两格",
            !MoveGenerator.IsValidMoveForPieceType(board, new Position(3, 3), new Position(3, 5),
                PieceType.Pawn, PieceColor.White));

        var blackBoard = new Board();
        blackBoard.SetPiece(new Position(5, 4), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.Black));
        moves = MoveGenerator.GeneratePseudoLegalMoves(blackBoard, PieceColor.Black);
        Check("黑兵未动过在 y=4 可以走两格",
            moves.Any(m => m.From == new Position(5, 4) && m.To == new Position(5, 2)));

        var blocked = new Board();
        blocked.SetPiece(new Position(2, 3), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White));
        blocked.SetPiece(new Position(2, 4), new Piece(PieceType.Knight, PieceType.Knight, PieceColor.Black));
        moves = MoveGenerator.GeneratePseudoLegalMoves(blocked, PieceColor.White);
        Check("两格路径被挡时不能走两格",
            !moves.Any(m => m.From == new Position(2, 3) && m.To == new Position(2, 5)));

        var movedAtStart = new Board();
        movedAtStart.SetPiece(new Position(1, 1), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White) { HasMoved = true });
        moves = MoveGenerator.GeneratePseudoLegalMoves(movedAtStart, PieceColor.White);
        Check("动过的兵即使还在起始行也不能走两格",
            !moves.Any(m => m.From == new Position(1, 1) && m.To == new Position(1, 3)));
    }

    private static void TestCaptureNoReveal()
    {
        var boardA = BuildShuffledBlackBoard(queenAtA8: true);
        var boardB = BuildShuffledBlackBoard(queenAtA8: false);

        var beliefA = new BeliefState(PieceColor.White);
        beliefA.ResetFromBoard(boardA);
        var beliefB = new BeliefState(PieceColor.White);
        beliefB.ResetFromBoard(boardB);

        var capture = new Position(0, 7);
        var kingSumBefore = SumOver(boardA, beliefA, PieceType.King);

        beliefA.NotifyCapture(capture);
        beliefB.NotifyCapture(capture);

        Check("被吃格不再有信念体", beliefA.GetProbability(capture, PieceType.King) == 0f);
        Check("棋局未结束即公开“被吃不是王”，王概率总和不降",
            SumOver(boardA, beliefA, PieceType.King) > kingSumBefore - 0.02f);

        // 两个世界只有被吃子的真型不同，可见标签与走子历史完全一致：吃后信念必须逐格逐型相同。
        bool identical = true;
        foreach (var (pos, _) in boardA.GetPiecesOfColor(PieceColor.Black))
        {
            if (pos == capture) continue;
            for (int t = 1; t <= 6; t++)
            {
                float pa = beliefA.GetProbability(pos, (PieceType)t);
                float pb = beliefB.GetProbability(pos, (PieceType)t);
                if (Math.Abs(pa - pb) > 1e-4f)
                {
                    identical = false;
                    Console.WriteLine($"  差异 @ {pos} 类型 {(PieceType)t}: {pa:F4} vs {pb:F4}");
                }
            }
        }
        Check("吃子结果不依赖被吃子的真型", identical);
    }

    private static Board BuildShuffledBlackBoard(bool queenAtA8)
    {
        var board = new Board();
        var labels = new[]
        {
            (new Position(0, 7), PieceType.Rook), (new Position(1, 7), PieceType.Knight),
            (new Position(2, 7), PieceType.Bishop), (new Position(3, 7), PieceType.Queen),
            (new Position(4, 7), PieceType.King), (new Position(5, 7), PieceType.Bishop),
            (new Position(6, 7), PieceType.Knight), (new Position(7, 7), PieceType.Rook)
        };
        // 两个世界只交换 a8/b8 的真型，可见标签完全相同。
        var trueTypes = queenAtA8
            ? new[] { PieceType.Queen, PieceType.Knight, PieceType.Bishop, PieceType.Rook,
                     PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook }
            : new[] { PieceType.Knight, PieceType.Queen, PieceType.Bishop, PieceType.Rook,
                     PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

        for (int i = 0; i < 8; i++)
            board.SetPiece(labels[i].Item1, new Piece(trueTypes[i], labels[i].Item2, PieceColor.Black));
        for (int x = 0; x < 8; x++)
            board.SetPiece(new Position(x, 6), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.Black));
        return board;
    }

    private static void TestNoPositionalKingLeak()
    {
        var board = BuildShuffledBlackBoard(queenAtA8: true);
        var belief = new BeliefState(PieceColor.White);
        belief.ResetFromBoard(board);

        var kingPos = new Position(0, 7);
        var otherPos = new Position(7, 7);
        float kingCorner = belief.GetProbability(kingPos, PieceType.King);
        float kingOther = belief.GetProbability(otherPos, PieceType.King);

        Check("真王所在角与其它位置初始王概率相同",
            Math.Abs(kingCorner - kingOther) < 1e-4f);
        Check("初始王概率只是均分先验（约 1/16）",
            Math.Abs(kingCorner - 1f / 16f) < 0.01f);
    }

    private static float SumOver(Board board, BeliefState belief, PieceType type)
    {
        float sum = 0f;
        foreach (var (pos, _) in board.GetPiecesOfColor(PieceColor.Black))
            sum += belief.GetProbability(pos, type);
        return sum;
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            Console.WriteLine($"PASS {name}");
            return;
        }
        _failures++;
        Console.WriteLine($"FAIL {name}");
    }
}