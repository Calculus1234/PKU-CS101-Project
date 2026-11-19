using System;

namespace ChessDark.Core
{
    public struct Move : IEquatable<Move>
    {
        public Position From { get; }
        public Position To { get; }
        public PieceType? PromotionType { get; }
        public bool IsCastling { get; }

        public Move(Position from, Position to, PieceType? promotionType = null, bool isCastling = false)
        {
            From = from;
            To = to;
            PromotionType = promotionType;
            IsCastling = isCastling;
        }

        public bool Equals(Move other) =>
            From == other.From && To == other.To && PromotionType == other.PromotionType && IsCastling == other.IsCastling;

        public override bool Equals(object obj) => obj is Move m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(From, To, PromotionType, IsCastling);
        public static bool operator ==(Move a, Move b) => a.Equals(b);
        public static bool operator !=(Move a, Move b) => !a.Equals(b);

        public override string ToString()
        {
            string promo = PromotionType.HasValue ? $"={PromotionType.Value.ToSymbol()}" : "";
            string castle = IsCastling ? " (O-O)" : "";
            return $"{Position.ToAlgebraic(From)}->{Position.ToAlgebraic(To)}{promo}{castle}";
        }
    }
}
