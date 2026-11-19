using System;
using System.Collections.Generic;

namespace ChessDark.Core
{
    public class Board
    {
        private readonly Piece[,] _squares = new Piece[8, 8];

        public Piece GetPiece(int x, int y)
        {
            if (x < 0 || x >= 8 || y < 0 || y >= 8) return null;
            return _squares[x, y];
        }

        public Piece GetPiece(Position pos) => GetPiece(pos.X, pos.Y);

        public void SetPiece(int x, int y, Piece piece)
        {
            if (x >= 0 && x < 8 && y >= 0 && y < 8)
                _squares[x, y] = piece;
        }

        public void SetPiece(Position pos, Piece piece) => SetPiece(pos.X, pos.Y, piece);

        public void ClearPiece(Position pos) => SetPiece(pos, null);

        public Board Clone()
        {
            var clone = new Board();
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 8; y++)
                    if (_squares[x, y] != null)
                        clone._squares[x, y] = _squares[x, y].Clone();
            return clone;
        }

        public void InitializeShuffled(PieceColor color, Random rng)
        {
            PieceType[] backRankTemplate = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                             PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

            PieceType[] allPieces = new PieceType[16];
            Array.Copy(backRankTemplate, allPieces, 8);
            for (int i = 0; i < 8; i++)
                allPieces[8 + i] = PieceType.Pawn;

            ShuffleArray(allPieces, rng);

            int[] yRows = color == PieceColor.White ? new[] { 0, 1 } : new[] { 7, 6 };
            PieceType[] visibleBackRank = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

            for (int x = 0; x < 8; x++)
            {
                for (int rowIdx = 0; rowIdx < 2; rowIdx++)
                {
                    int y = yRows[rowIdx];
                    int pieceIdx = rowIdx * 8 + x;
                    PieceType trueType = allPieces[pieceIdx];

                    PieceType visibleType;
                    if (rowIdx == 0)
                        visibleType = visibleBackRank[x];
                    else
                        visibleType = PieceType.Pawn;

                    _squares[x, y] = new Piece(trueType, visibleType, color);
                }
            }
        }

        public void InitializeStandard()
        {
            PieceType[] backRank = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                     PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

            for (int x = 0; x < 8; x++)
            {
                _squares[x, 0] = new Piece(backRank[x], backRank[x], PieceColor.White);
                _squares[x, 1] = new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.White);
                _squares[x, 6] = new Piece(PieceType.Pawn, PieceType.Pawn, PieceColor.Black);
                _squares[x, 7] = new Piece(backRank[x], backRank[x], PieceColor.Black);
            }
        }

        private static void ShuffleArray<T>(T[] array, Random rng)
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (array[i], array[j]) = (array[j], array[i]);
            }
        }

        public IEnumerable<(Position pos, Piece piece)> GetAllPieces()
        {
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 8; y++)
                    if (_squares[x, y] != null)
                        yield return (new Position(x, y), _squares[x, y]);
        }

        public IEnumerable<(Position pos, Piece piece)> GetPiecesOfColor(PieceColor color)
        {
            foreach (var (pos, piece) in GetAllPieces())
                if (piece.Color == color)
                    yield return (pos, piece);
        }

        public Position? FindKing(PieceColor color)
        {
            foreach (var (pos, piece) in GetAllPieces())
                if (piece.Color == color && piece.TrueType == PieceType.King)
                    return pos;
            return null;
        }

        public void ExecuteMove(Move move)
        {
            var piece = GetPiece(move.From);
            if (piece == null) return;

            int rookFromX = move.IsCastling ? FindCastlingRookX(move, piece) : -1;

            _squares[move.From.X, move.From.Y] = null;

            if (move.PromotionType.HasValue)
            {
                piece.TrueType = move.PromotionType.Value;
            }

            piece.HasMoved = true;

            // 先移车再落王：自定义阵型里王的目标格可能正是车的原格。
            if (move.IsCastling && rookFromX >= 0)
            {
                int rookToX = move.To.X > move.From.X ? move.To.X - 1 : move.To.X + 1;
                var rook = GetPiece(rookFromX, move.From.Y);
                if (rook != null)
                {
                    if (rookFromX != rookToX)
                    {
                        _squares[rookFromX, move.From.Y] = null;
                        _squares[rookToX, move.From.Y] = rook;
                    }
                    rook.HasMoved = true;
                }
            }

            _squares[move.To.X, move.To.Y] = piece;
        }

        // 易位车不再假定在角落：自定义阵型允许车站在王的任意一侧列。
        private int FindCastlingRookX(Move move, Piece king)
        {
            int dir = move.To.X > move.From.X ? 1 : -1;
            for (int x = move.From.X + dir; x >= 0 && x < 8; x += dir)
            {
                var candidate = _squares[x, move.From.Y];
                if (candidate == null || candidate.Color != king.Color) continue;
                if (candidate.TrueType != PieceType.Rook || candidate.HasMoved) continue;
                return x;
            }
            return -1;
        }

        public struct MoveUndo
        {
            public Piece Moved;
            public bool MovedHadMoved;
            public PieceType MovedOriginalType;
            public Piece Captured;
            public int CapturedX;
            public int CapturedY;
            public Piece Rook;
            public int RookFromX;
            public int RookToX;
            public bool RookHadMoved;
        }

        public MoveUndo ExecuteMoveWithUndo(Move move)
        {
            var undo = new MoveUndo();
            var piece = GetPiece(move.From);
            if (piece == null) return undo;

            undo.Moved = piece;
            undo.MovedHadMoved = piece.HasMoved;
            undo.MovedOriginalType = piece.TrueType;
            undo.Captured = move.IsCastling ? null : GetPiece(move.To);
            undo.CapturedX = move.To.X;
            undo.CapturedY = move.To.Y;
            undo.RookFromX = move.IsCastling ? FindCastlingRookX(move, piece) : -1;

            _squares[move.From.X, move.From.Y] = null;

            if (move.PromotionType.HasValue)
                piece.TrueType = move.PromotionType.Value;

            piece.HasMoved = true;

            if (move.IsCastling && undo.RookFromX >= 0)
            {
                undo.RookToX = move.To.X > move.From.X ? move.To.X - 1 : move.To.X + 1;
                var rook = GetPiece(undo.RookFromX, move.From.Y);
                if (rook != null)
                {
                    undo.Rook = rook;
                    undo.RookHadMoved = rook.HasMoved;
                    if (undo.RookFromX != undo.RookToX)
                    {
                        _squares[undo.RookFromX, move.From.Y] = null;
                        _squares[undo.RookToX, move.From.Y] = rook;
                    }
                    rook.HasMoved = true;
                }
            }

            _squares[move.To.X, move.To.Y] = piece;

            return undo;
        }

        public void UndoMove(Move move, MoveUndo undo)
        {
            var piece = undo.Moved;
            if (piece == null) return;

            _squares[move.To.X, move.To.Y] = null;
            piece.TrueType = undo.MovedOriginalType;
            piece.HasMoved = undo.MovedHadMoved;
            _squares[move.From.X, move.From.Y] = piece;

            if (move.IsCastling && undo.Rook != null)
            {
                if (undo.RookFromX != undo.RookToX)
                    _squares[undo.RookToX, move.From.Y] = null;
                undo.Rook.HasMoved = undo.RookHadMoved;
                _squares[undo.RookFromX, move.From.Y] = undo.Rook;
            }

            if (undo.Captured != null)
                _squares[undo.CapturedX, undo.CapturedY] = undo.Captured;
        }

        public Piece RemovePiece(Position pos)
        {
            var piece = GetPiece(pos);
            _squares[pos.X, pos.Y] = null;
            return piece;
        }

        public void PrintBoard(bool showTrueTypes = false)
        {
            Console.WriteLine("  a b c d e f g h");
            for (int y = 7; y >= 0; y--)
            {
                Console.Write($"{y + 1} ");
                for (int x = 0; x < 8; x++)
                {
                    var piece = _squares[x, y];
                    if (piece == null)
                    {
                        Console.Write(". ");
                    }
                    else
                    {
                        char symbol;
                        if (showTrueTypes)
                            symbol = piece.TrueType.ToSymbol();
                        else
                            symbol = piece.VisibleType.ToSymbol();

                        if (piece.Color == PieceColor.Black)
                            symbol = char.ToLower(symbol);
                        Console.Write($"{symbol} ");
                    }
                }
                Console.WriteLine($"{y + 1}");
            }
            Console.WriteLine("  a b c d e f g h");
        }
    }
}
