using System;
using System.Collections.Generic;
using ChessDark.Core;

namespace ChessDark.Logic
{
    public static class MoveGenerator
    {
        private static readonly Position[] KnightOffsets = {
            new(-2,-1), new(-2,1), new(-1,-2), new(-1,2),
            new(1,-2), new(1,2), new(2,-1), new(2,1)
        };

        private static readonly Position[] KingOffsets = {
            new(-1,-1), new(-1,0), new(-1,1),
            new(0,-1),           new(0,1),
            new(1,-1),  new(1,0), new(1,1)
        };

        private static readonly Position[] BishopDirs = { new(-1,-1), new(-1,1), new(1,-1), new(1,1) };
        private static readonly Position[] RookDirs = { new(-1,0), new(1,0), new(0,-1), new(0,1) };
        private static readonly Position[] QueenDirs = { new(-1,-1), new(-1,0), new(-1,1), new(0,-1), new(0,1), new(1,-1), new(1,0), new(1,1) };

        public static List<Move> GeneratePseudoLegalMoves(Board board, PieceColor color)
        {
            var moves = new List<Move>(64);
            GeneratePseudoLegalMoves(board, color, moves);
            return moves;
        }

        public static void GeneratePseudoLegalMoves(Board board, PieceColor color, List<Move> moves)
        {
            moves.Clear();
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece != null && piece.Color == color)
                        GeneratePieceMoves(board, new Position(x, y), piece, moves);
                }
            }
        }

        public static List<Move> GenerateLegalMoves(Board board, PieceColor color)
        {
            // 本变体没有将军/被将规则：只要走法几何合法即可走，王被真正吃掉才结束对局。
            return GeneratePseudoLegalMoves(board, color);
        }

        public static bool IsSquareAttacked(Board board, Position target, PieceColor defenderColor)
        {
            PieceColor attackerColor = defenderColor == PieceColor.White ? PieceColor.Black : PieceColor.White;

            foreach (var (pos, piece) in board.GetPiecesOfColor(attackerColor))
            {
                if (CanPieceAttack(board, pos, piece, target))
                    return true;
            }

            return false;
        }

        private static bool CanPieceAttack(Board board, Position from, Piece piece, Position target)
        {
            return CanPieceAttackSquare(board, from, piece.TrueType, piece.Color, target);
        }

        // 不要求 from 上真的站着棋子，供“走完之后这个格子能否攻击王室/关键格”的排序启发式使用。
        public static bool CanPieceAttackSquare(Board board, Position from, PieceType type, PieceColor color, Position target)
        {
            if (!from.IsValid || !target.IsValid || from == target) return false;

            return type switch
            {
                PieceType.Pawn => CanPawnAttack(from, color, target),
                PieceType.Knight => CanKnightAttack(from, target),
                PieceType.Bishop => CanSlidingAttack(board, from, BishopDirs, target),
                PieceType.Rook => CanSlidingAttack(board, from, RookDirs, target),
                PieceType.Queen => CanSlidingAttack(board, from, QueenDirs, target),
                PieceType.King => CanKingAttack(from, target),
                _ => false
            };
        }

        private static bool CanPawnAttack(Position from, PieceColor color, Position target)
        {
            int dir = color == PieceColor.White ? 1 : -1;
            int dx = target.X - from.X;
            int dy = target.Y - from.Y;
            return dy == dir && Math.Abs(dx) == 1;
        }

        private static bool CanKnightAttack(Position from, Position target)
        {
            int dx = Math.Abs(target.X - from.X);
            int dy = Math.Abs(target.Y - from.Y);
            return (dx == 1 && dy == 2) || (dx == 2 && dy == 1);
        }

        private static bool CanSlidingAttack(Board board, Position from, Position[] dirs, Position target)
        {
            foreach (var dir in dirs)
            {
                int x = from.X + dir.X;
                int y = from.Y + dir.Y;
                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    if (x == target.X && y == target.Y) return true;
                    if (board.GetPiece(x, y) != null) break;
                    x += dir.X;
                    y += dir.Y;
                }
            }
            return false;
        }

        private static bool CanKingAttack(Position from, Position target)
        {
            return Math.Abs(target.X - from.X) <= 1 && Math.Abs(target.Y - from.Y) <= 1;
        }

        private static void GeneratePieceMoves(Board board, Position from, Piece piece, List<Move> moves)
        {
            switch (piece.TrueType)
            {
                case PieceType.Pawn:
                    GeneratePawnMoves(board, from, piece, moves);
                    break;
                case PieceType.Knight:
                    GenerateKnightMoves(board, from, piece, moves);
                    break;
                case PieceType.Bishop:
                    GenerateSlidingMoves(board, from, piece, BishopDirs, moves);
                    break;
                case PieceType.Rook:
                    GenerateSlidingMoves(board, from, piece, RookDirs, moves);
                    break;
                case PieceType.Queen:
                    GenerateSlidingMoves(board, from, piece, QueenDirs, moves);
                    break;
                case PieceType.King:
                    GenerateKingMoves(board, from, piece, moves);
                    break;
            }
        }

        private static void GeneratePawnMoves(Board board, Position from, Piece piece, List<Move> moves)
        {
            int dir = piece.Color == PieceColor.White ? 1 : -1;
            int promoRow = piece.Color == PieceColor.White ? 7 : 0;

            Position oneStep = new Position(from.X, from.Y + dir);
            if (oneStep.IsValid && board.GetPiece(oneStep) == null)
            {
                if (oneStep.Y == promoRow)
                    AddPromotions(oneStep, from, moves);
                else
                    moves.Add(new Move(from, oneStep));

                if (!piece.HasMoved)
                {
                    Position twoStep = new Position(from.X, from.Y + 2 * dir);
                    if (twoStep.IsValid && board.GetPiece(twoStep) == null)
                        moves.Add(new Move(from, twoStep));
                }
            }

            for (int i = 0; i < 2; i++)
            {
                int dx = i == 0 ? -1 : 1;
                Position capture = new Position(from.X + dx, from.Y + dir);
                if (!capture.IsValid) continue;
                var target = board.GetPiece(capture);
                if (target != null && target.Color != piece.Color)
                {
                    if (capture.Y == promoRow)
                        AddPromotions(capture, from, moves);
                    else
                        moves.Add(new Move(from, capture));
                }
            }
        }

        private static void AddPromotions(Position to, Position from, List<Move> moves)
        {
            moves.Add(new Move(from, to, PieceType.Queen));
            moves.Add(new Move(from, to, PieceType.Rook));
            moves.Add(new Move(from, to, PieceType.Bishop));
            moves.Add(new Move(from, to, PieceType.Knight));
        }

        private static void GenerateKnightMoves(Board board, Position from, Piece piece, List<Move> moves)
        {
            foreach (var offset in KnightOffsets)
            {
                Position to = from + offset;
                if (!to.IsValid) continue;
                var target = board.GetPiece(to);
                if (target == null || target.Color != piece.Color)
                    moves.Add(new Move(from, to));
            }
        }

        private static void GenerateSlidingMoves(Board board, Position from, Piece piece, Position[] dirs, List<Move> moves)
        {
            foreach (var dir in dirs)
            {
                int x = from.X + dir.X;
                int y = from.Y + dir.Y;
                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    var target = board.GetPiece(x, y);
                    if (target == null)
                    {
                        moves.Add(new Move(from, new Position(x, y)));
                    }
                    else
                    {
                        if (target.Color != piece.Color)
                            moves.Add(new Move(from, new Position(x, y)));
                        break;
                    }
                    x += dir.X;
                    y += dir.Y;
                }
            }
        }

        private static void GenerateKingMoves(Board board, Position from, Piece piece, List<Move> moves)
        {
            foreach (var offset in KingOffsets)
            {
                Position to = from + offset;
                if (!to.IsValid) continue;
                var target = board.GetPiece(to);
                if (target == null || target.Color != piece.Color)
                    moves.Add(new Move(from, to));
            }

            if (!piece.HasMoved)
            {
                GenerateCastlingMoves(board, from, piece, moves);
            }
        }

        private static void GenerateCastlingMoves(Board board, Position from, Piece piece, List<Move> moves)
        {
            int y = from.Y;

            for (int rookX = 0; rookX < 8; rookX++)
            {
                var rook = board.GetPiece(rookX, y);
                if (rook == null || rook.Color != piece.Color || rook.TrueType != PieceType.Rook || rook.HasMoved)
                    continue;

                bool isKingSide = rookX > from.X;
                int kingToX = isKingSide ? from.X + 2 : from.X - 2;
                int rookToX = isKingSide ? kingToX - 1 : kingToX + 1;
                if (kingToX < 0 || kingToX > 7 || rookToX < 0 || rookToX > 7) continue;

                int minX = Math.Min(from.X, Math.Min(kingToX, Math.Min(rookX, rookToX)));
                int maxX = Math.Max(from.X, Math.Max(kingToX, Math.Max(rookX, rookToX)));

                bool pathClear = true;
                for (int x = minX; x <= maxX; x++)
                {
                    if (x == from.X || x == rookX) continue;
                    if (board.GetPiece(x, y) != null)
                    {
                        pathClear = false;
                        break;
                    }
                }
                if (!pathClear) continue;

                moves.Add(new Move(from, new Position(kingToX, y), isCastling: true));
            }
        }

        public static bool IsValidMoveForPieceType(Board board, Position from, Position to, PieceType pieceType, PieceColor color)
        {
            var piece = board.GetPiece(from);
            if (piece == null || piece.Color != color) return false;

            switch (pieceType)
            {
                case PieceType.Pawn:
                    return IsValidPawnMove(board, from, to, color);
                case PieceType.Knight:
                    return IsValidKnightMove(board, from, to, color);
                case PieceType.Bishop:
                    return IsValidBishopMove(board, from, to, color);
                case PieceType.Rook:
                    return IsValidRookMove(board, from, to, color);
                case PieceType.Queen:
                    return IsValidQueenMove(board, from, to, color);
                case PieceType.King:
                    return IsValidKingMove(board, from, to, color);
                default:
                    return false;
            }
        }

        private static bool IsValidPawnMove(Board board, Position from, Position to, PieceColor color)
        {
            int dir = color == PieceColor.White ? 1 : -1;
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;

            if (dx == 0 && dy == dir && board.GetPiece(to) == null)
                return true;

            var pawn = board.GetPiece(from);
            if (dx == 0 && pawn != null && !pawn.HasMoved && dy == 2 * dir)
            {
                Position mid = new Position(from.X, from.Y + dir);
                return board.GetPiece(mid) == null && board.GetPiece(to) == null;
            }

            if (Math.Abs(dx) == 1 && dy == dir)
            {
                var target = board.GetPiece(to);
                if (target != null && target.Color != color)
                    return true;
            }

            return false;
        }

        private static bool IsValidKnightMove(Board board, Position from, Position to, PieceColor color)
        {
            int dx = Math.Abs(to.X - from.X);
            int dy = Math.Abs(to.Y - from.Y);
            if (!((dx == 1 && dy == 2) || (dx == 2 && dy == 1))) return false;

            var target = board.GetPiece(to);
            return target == null || target.Color != color;
        }

        private static bool IsValidBishopMove(Board board, Position from, Position to, PieceColor color)
        {
            return IsValidSlidingMove(board, from, to, color, BishopDirs);
        }

        private static bool IsValidRookMove(Board board, Position from, Position to, PieceColor color)
        {
            return IsValidSlidingMove(board, from, to, color, RookDirs);
        }

        private static bool IsValidQueenMove(Board board, Position from, Position to, PieceColor color)
        {
            return IsValidSlidingMove(board, from, to, color, QueenDirs);
        }

        private static bool IsValidSlidingMove(Board board, Position from, Position to, PieceColor color, Position[] dirs)
        {
            foreach (var dir in dirs)
            {
                int x = from.X + dir.X;
                int y = from.Y + dir.Y;
                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    if (x == to.X && y == to.Y)
                    {
                        var target = board.GetPiece(to);
                        return target == null || target.Color != color;
                    }
                    if (board.GetPiece(x, y) != null) break;
                    x += dir.X;
                    y += dir.Y;
                }
            }
            return false;
        }

        private static bool IsValidKingMove(Board board, Position from, Position to, PieceColor color)
        {
            int dx = Math.Abs(to.X - from.X);
            int dy = Math.Abs(to.Y - from.Y);
            if (dx > 1 || dy > 1) return false;

            var target = board.GetPiece(to);
            return target == null || target.Color != color;
        }
    }
}
