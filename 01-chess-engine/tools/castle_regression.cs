using System;
using System.Collections.Generic;
using System.Linq;
using ChessDark.Core;
using ChessDark.Logic;

internal static class CastleRegression
{
    private static int _failures;

    private static void Main()
    {
        TestCastle("标准白王翼 e1/h1", PieceColor.White,
            new[] { (new Position(4, 0), PieceType.King), (new Position(7, 0), PieceType.Rook) },
            new Position(4, 0), new Position(6, 0), new Position(7, 0), new Position(5, 0));

        TestCastle("标准白后翼 e1/a1", PieceColor.White,
            new[] { (new Position(4, 0), PieceType.King), (new Position(0, 0), PieceType.Rook) },
            new Position(4, 0), new Position(2, 0), new Position(0, 0), new Position(3, 0));

        TestCastle("自定义黑王翼 f8/g8（王目标格=车原格）", PieceColor.Black,
            new[] { (new Position(5, 7), PieceType.King), (new Position(6, 7), PieceType.Rook) },
            new Position(5, 7), new Position(7, 7), new Position(6, 7), new Position(6, 7));

        TestCastle("自定义黑王翼 f8/h8（隔一格）", PieceColor.Black,
            new[] { (new Position(5, 7), PieceType.King), (new Position(7, 7), PieceType.Rook) },
            new Position(5, 7), new Position(7, 7), new Position(7, 7), new Position(6, 7));

        TestCastle("自定义白后翼 d1/a1", PieceColor.White,
            new[] { (new Position(3, 0), PieceType.King), (new Position(0, 0), PieceType.Rook) },
            new Position(3, 0), new Position(1, 0), new Position(0, 0), new Position(2, 0));

        TestBeliefCastling();

        Console.WriteLine(_failures == 0 ? "ALL CASTLE CHECKS PASSED" : $"FAILURES: {_failures}");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void TestCastle(string name, PieceColor color,
        (Position pos, PieceType type)[] setup,
        Position kingFrom, Position kingTo, Position rookFrom, Position rookTo)
    {
        var board = new Board();
        foreach (var (pos, type) in setup)
            board.SetPiece(pos, new Piece(type, type, color));

        var before = Snapshot(board);
        var moves = MoveGenerator.GenerateLegalMoves(board, color);
        bool generated = moves.Any(m => m.IsCastling && m.From == kingFrom && m.To == kingTo);
        Check($"{name} 生成易位 {kingFrom}->{kingTo}", generated);
        if (!generated) return;

        board.ExecuteMove(new Move(kingFrom, kingTo, isCastling: true));

        var king = board.GetPiece(kingTo);
        Check($"{name} 王落 {kingTo}", king != null && king.TrueType == PieceType.King && king.HasMoved);
        Check($"{name} 王原点已空", board.GetPiece(kingFrom) == null);

        var rook = board.GetPiece(rookTo);
        Check($"{name} 车在 {rookTo}", rook != null && rook.TrueType == PieceType.Rook && rook.HasMoved);
        if (rookFrom != rookTo && rookFrom != kingTo)
            Check($"{name} 车原点 {rookFrom} 已空", board.GetPiece(rookFrom) == null);
        Check($"{name} 车目标格与王不重叠", rookTo != kingTo);
        Check($"{name} 只有 2 枚子", board.GetAllPieces().Count() == 2);

        var board2 = new Board();
        foreach (var (pos, type) in setup)
            board2.SetPiece(pos, new Piece(type, type, color));
        var undo = board2.ExecuteMoveWithUndo(new Move(kingFrom, kingTo, isCastling: true));
        board2.UndoMove(new Move(kingFrom, kingTo, isCastling: true), undo);
        Check($"{name} 撤销后完全还原", Snapshot(board2) == before);
    }

    private static void TestBeliefCastling()
    {
        // 后翼易位：王 d8、车 b8、a8 还有一枚子。旧代码把角落 a8 当车丢到 c8，并顺手删掉 b8 的真车。
        var board = new Board();
        var kingPos = new Position(3, 7);
        var rookPos = new Position(1, 7);
        var cornerPos = new Position(0, 7);
        var rookTo = new Position(2, 7);

        var backRank = new[]
        {
            (cornerPos, PieceType.Knight), (rookPos, PieceType.Rook), (kingPos, PieceType.King),
            (new Position(4, 7), PieceType.Bishop), (new Position(5, 7), PieceType.Knight),
            (new Position(6, 7), PieceType.Bishop), (new Position(7, 7), PieceType.Rook),
        };
        var labels = new[]
        {
            PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
            PieceType.King, PieceType.Bishop, PieceType.Knight
        };
        for (int i = 0; i < backRank.Length; i++)
            board.SetPiece(backRank[i].Item1, new Piece(backRank[i].Item2, labels[i], PieceColor.Black));
        for (int x = 0; x < 8; x++)
            board.SetPiece(new Position(x, 6), new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.Black));

        var belief = new BeliefState(PieceColor.White);
        belief.VisibleLabelTrust = 0f;
        belief.ResetFromBoard(board);

        var castle = new Move(kingPos, rookPos, isCastling: true);
        belief.NotifyMove(castle, PieceType.Bishop, MoveLeakType.Disguised, board);

        float kingAtTarget = belief.GetProbability(rookPos, PieceType.King);
        float rookAtTarget = belief.GetProbability(rookTo, PieceType.Rook);
        float cornerKept = belief.GetProbability(cornerPos, PieceType.Pawn);
        float originKing = belief.GetProbability(kingPos, PieceType.King);

        Console.WriteLine($"  [diag] P(King@b8)={kingAtTarget:F4} P(Rook@c8)={rookAtTarget:F4} " +
                          $"P(Pawn@a8)={cornerKept:F4} P(King@d8)={originKing:F4}");

        Check("信念: 王跟到 b8", kingAtTarget > 0.4f);
        Check("信念: 车移到 c8", rookAtTarget > 0.05f);
        Check("信念: a8 身体没被顶走", cornerKept > 0.05f);
        Check("信念: 王原点 d8 已清空", originKing <= 0.01f);
    }

    private static string Snapshot(Board board)
    {
        return string.Join("|", board.GetAllPieces()
            .OrderBy(p => p.pos.Y * 8 + p.pos.X)
            .Select(p => $"{p.pos.X},{p.pos.Y}:{p.piece.TrueType},{p.piece.VisibleType},{(p.piece.HasMoved ? 1 : 0)}"));
    }

    private static void Check(string name, bool condition)
    {
        Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
        if (!condition) _failures++;
    }
}