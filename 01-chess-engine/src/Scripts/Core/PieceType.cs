namespace ChessDark.Core
{
    public enum PieceType
    {
        None = 0,
        King = 1,
        Queen = 2,
        Rook = 3,
        Bishop = 4,
        Knight = 5,
        Pawn = 6
    }

    public enum PieceColor
    {
        White = 0,
        Black = 1
    }

    public static class PieceTypeExtensions
    {
        public static char ToSymbol(this PieceType type)
        {
            return type switch
            {
                PieceType.King => 'K',
                PieceType.Queen => 'Q',
                PieceType.Rook => 'R',
                PieceType.Bishop => 'B',
                PieceType.Knight => 'N',
                PieceType.Pawn => 'P',
                _ => '.'
            };
        }

        public static string ToChinese(this PieceType type)
        {
            return type switch
            {
                PieceType.King => "王",
                PieceType.Queen => "后",
                PieceType.Rook => "车",
                PieceType.Bishop => "象",
                PieceType.Knight => "马",
                PieceType.Pawn => "兵",
                _ => "无"
            };
        }

        public static int GetValue(this PieceType type)
        {
            return type switch
            {
                PieceType.Pawn => 100,
                PieceType.Knight => 320,
                PieceType.Bishop => 330,
                PieceType.Rook => 500,
                PieceType.Queen => 900,
                PieceType.King => 20000,
                _ => 0
            };
        }
    }
}
