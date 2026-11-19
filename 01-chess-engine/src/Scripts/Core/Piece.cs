namespace ChessDark.Core
{
    public class Piece
    {
        public PieceType TrueType { get; set; }
        public PieceType VisibleType { get; set; }
        public PieceColor Color { get; }
        public bool HasMoved { get; set; }

        // AI 专用：该敌子“可能的本态”位掩码（bit i = PieceType i），0 表示未标记。
        // 王安全判定必须按可能集合悲观检查，不能只看单个采样世界抽到的本态。
        internal int BeliefTypeMask { get; set; }

        // AI 专用：仅由公开走子几何推出的完整可能集（不受概率阈值裁剪）。
        // 采样世界只兑现其中一种本态，但王安全必须对“任何一种”都成立。
        internal int PossibleTypeMask { get; set; }

        public Piece(PieceType trueType, PieceType visibleType, PieceColor color)
        {
            TrueType = trueType;
            VisibleType = visibleType;
            Color = color;
            HasMoved = false;
        }

        public Piece Clone()
        {
            return new Piece(TrueType, VisibleType, Color)
            {
                HasMoved = HasMoved,
                BeliefTypeMask = BeliefTypeMask,
                PossibleTypeMask = PossibleTypeMask
            };
        }

        public override string ToString()
        {
            return $"{Color}:{TrueType.ToSymbol()}(伪装:{VisibleType.ToSymbol()})";
        }
    }
}
