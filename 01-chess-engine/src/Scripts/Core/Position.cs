using System;

namespace ChessDark.Core
{
    public struct Position : IEquatable<Position>
    {
        public int X { get; }
        public int Y { get; }

        public Position(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool IsValid => X >= 0 && X < 8 && Y >= 0 && Y < 8;

        public static Position operator +(Position a, Position b) => new Position(a.X + b.X, a.Y + b.Y);
        public static Position operator -(Position a, Position b) => new Position(a.X - b.X, a.Y - b.Y);

        public bool Equals(Position other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Position p && Equals(p);
        public override int GetHashCode() => X * 8 + Y;
        public static bool operator ==(Position a, Position b) => a.Equals(b);
        public static bool operator !=(Position a, Position b) => !a.Equals(b);

        public override string ToString() => $"({X},{Y})";

        public static string ToAlgebraic(Position pos)
        {
            char file = (char)('a' + pos.X);
            int rank = pos.Y + 1;
            return $"{file}{rank}";
        }
    }
}
