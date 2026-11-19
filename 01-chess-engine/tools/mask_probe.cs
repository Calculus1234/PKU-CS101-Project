using System;
using System.Collections.Generic;
using System.Reflection;
using ChessDark.AI;
using ChessDark.Core;

namespace Probe
{
    internal static class Program
    {
        private static readonly Position[] AllDirs =
        {
            new Position(1, 0), new Position(-1, 0), new Position(0, 1), new Position(0, -1),
            new Position(1, 1), new Position(1, -1), new Position(-1, 1), new Position(-1, -1)
        };

        private static readonly Position[] KnightOffsets =
        {
            new Position(1, 2), new Position(2, 1), new Position(2, -1), new Position(1, -2),
            new Position(-1, -2), new Position(-2, -1), new Position(-2, 1), new Position(-1, 2)
        };

        private const int Straight = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Rook);
        private const int Diagonal = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Bishop);
        private const int AnySlider = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Rook) | (1 << (int)PieceType.Bishop);

        private static MethodInfo _old;

        private static int Main(string[] args)
        {
            foreach (var type in typeof(ExpectiminimaxAI).Assembly.GetTypes())
            foreach (var m in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                if (m.Name == "MaskKingExposure")
                    _old = m;
            if (_old == null)
            {
                Console.WriteLine("MaskKingExposure not found");
                return 2;
            }
            Console.WriteLine($"method: {_old} static={_old.IsStatic}");

            var ai = new ExpectiminimaxAI(PieceColor.White, 1, 1, 1, 1000);
            var rng = new Random(20261111);
            int sameA = 0, sameB = 0, sameRay = 0, total = 0;
            int mismatchA = 0, mismatchB = 0, mismatchRay = 0;
            for (int i = 0; i < 20000; i++)
            {
                var board = RandomBoard(rng, out var king, out var enemy);
                var argsArr = new object[] { board, king, enemy };
                int oldValue = (int)_old.Invoke(_old.IsStatic ? null : ai, argsArr);

                int scanDir = Scan(board, king, enemy, false);
                int scanAny = Scan(board, king, enemy, true);
                int ray = Ray(board, king, enemy);
                total++;
                if (oldValue == scanDir) sameA++; else { mismatchA++; if (mismatchA <= 5) Dump("A", board, king, enemy, oldValue, scanDir, scanAny, ray); }
                if (oldValue == scanAny) sameB++; else mismatchB++;
                if (oldValue == ray) sameRay++; else mismatchRay++;
            }
            Console.WriteLine($"total={total}");
            Console.WriteLine($"scan-directional: same={sameA} mismatch={mismatchA}");
            Console.WriteLine($"scan-any-slider: same={sameB} mismatch={mismatchB}");
            Console.WriteLine($"ray(current):    same={sameRay} mismatch={mismatchRay}");
            return 0;
        }

        private static void Dump(string tag, Board board, Position king, PieceColor enemy, int oldValue, int scanDir, int scanAny, int ray)
        {
            Console.WriteLine($"-- mismatch {tag}: old={oldValue} scanDir={scanDir} scanAny={scanAny} ray={ray} king=({king.X},{king.Y}) enemy={enemy}");
            for (int y = 7; y >= 0; y--)
            {
                var sb = new System.Text.StringBuilder();
                for (int x = 0; x < 8; x++)
                {
                    var p = board.GetPiece(x, y);
                    if (p == null) { sb.Append(" .       "); continue; }
                    int mask = MaskOf(p);
                    sb.Append($" {p.Color.ToString()[0]}{(int)p.TrueType}:[{mask:X}] ");
                }
                Console.WriteLine(sb.ToString());
            }
        }

        private static int MaskOf(Piece p)
        {
            var prop = typeof(Piece).GetProperty("PossibleTypeMask", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)prop.GetValue(p);
        }

        private static Board RandomBoard(Random rng, out Position king, out PieceColor enemy)
        {
            var board = new Board();
            enemy = rng.Next(2) == 0 ? PieceColor.White : PieceColor.Black;
            var own = enemy == PieceColor.White ? PieceColor.Black : PieceColor.White;
            king = new Position(rng.Next(8), rng.Next(8));

            int count = 1 + rng.Next(12);
            var used = new HashSet<int> { king.Y * 8 + king.X };
            for (int i = 0; i < count; i++)
            {
                int pos = rng.Next(64);
                if (!used.Add(pos)) { i--; continue; }
                var color = rng.Next(4) == 0 ? own : enemy;
                int mask = RandomMask(rng);
                var piece = new Piece(PieceType.Pawn, PieceType.Pawn, color);
                SetMask(piece, mask);
                board.SetPiece(pos % 8, pos / 8, piece);
            }
            return board;
        }

        private static int RandomMask(Random rng)
        {
            // 偏向窄掩码，制造“方向/类型不匹配”的边界情况。
            int mask = 0;
            if (rng.Next(3) != 0)
            {
                for (int t = 1; t <= 6; t++)
                    if (rng.Next(t == (int)PieceType.Queen ? 6 : 3) == 0)
                        mask |= 1 << t;
            }
            if (rng.Next(5) == 0) mask = 0;
            return mask;
        }

        private static void SetMask(Piece p, int mask)
        {
            var prop = typeof(Piece).GetProperty("PossibleTypeMask", BindingFlags.NonPublic | BindingFlags.Instance);
            prop.SetValue(p, mask);
            prop = typeof(Piece).GetProperty("BeliefTypeMask", BindingFlags.NonPublic | BindingFlags.Instance);
            prop.SetValue(p, mask);
        }

        private static int Scan(Board board, Position king, PieceColor enemy, bool anySlider)
        {
            int attackers = 0, latent = 0;
            for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                var p = board.GetPiece(x, y);
                if (p == null || p.Color != enemy) continue;
                int mask = MaskOf(p);
                if (mask == 0) continue;
                var pos = new Position(x, y);

                if ((mask & (1 << (int)PieceType.Knight)) != 0 && IsKnight(pos, king)) { attackers++; continue; }
                if ((mask & (1 << (int)PieceType.Pawn)) != 0 && IsPawn(pos, enemy, king)) { attackers++; continue; }
                if ((mask & (1 << (int)PieceType.King)) != 0 && Math.Abs(pos.X - king.X) <= 1 && Math.Abs(pos.Y - king.Y) <= 1) { attackers++; continue; }

                bool straight = pos.X == king.X || pos.Y == king.Y;
                bool diagonal = Math.Abs(pos.X - king.X) == Math.Abs(pos.Y - king.Y);
                int need = anySlider ? AnySlider : (straight ? Straight : Diagonal);
                if (!straight && !diagonal) continue;
                int dirX = Math.Sign(king.X - pos.X);
                int dirY = Math.Sign(king.Y - pos.Y);
                int blockers = 0;
                for (int cx = x + dirX, cy = y + dirY; cx != king.X || cy != king.Y; cx += dirX, cy += dirY)
                    if (board.GetPiece(cx, cy) != null) blockers++;
                if (blockers == 0 && (mask & need) != 0) attackers++;
                else if (blockers == 1 && (mask & need) != 0) latent++;
            }
            if (attackers == 0 && latent == 0) return 0;
            return Math.Min(1400, attackers * 420 + latent * 150);
        }

        private static int Ray(Board board, Position king, PieceColor enemy)
        {
            int attackers = 0, latent = 0;
            for (int d = 0; d < AllDirs.Length; d++)
            {
                bool diagonal = AllDirs[d].X != 0 && AllDirs[d].Y != 0;
                int sliderMask = diagonal ? Diagonal : Straight;
                var first = RayPiece(board, king, AllDirs[d], 1);
                if (first == null) continue;
                if (first.Color == enemy && (MaskOf(first) & sliderMask) != 0) { attackers++; continue; }
                var second = RayPiece(board, king, AllDirs[d], 2);
                if (second != null && second.Color == enemy && (MaskOf(second) & sliderMask) != 0) latent++;
            }
            for (int k = 0; k < KnightOffsets.Length; k++)
            {
                var p = board.GetPiece(king + KnightOffsets[k]);
                if (p != null && p.Color == enemy && (MaskOf(p) & (1 << (int)PieceType.Knight)) != 0) attackers++;
            }
            int pawnDir = enemy == PieceColor.White ? 1 : -1;
            for (int dx = -1; dx <= 1; dx += 2)
            {
                var p = board.GetPiece(new Position(king.X + dx, king.Y - pawnDir));
                if (p != null && p.Color == enemy && (MaskOf(p) & (1 << (int)PieceType.Pawn)) != 0) attackers++;
            }
            foreach (var dir in AllDirs)
            {
                var p = board.GetPiece(king + dir);
                if (p != null && p.Color == enemy && (MaskOf(p) & (1 << (int)PieceType.King)) != 0) attackers++;
            }
            if (attackers == 0 && latent == 0) return 0;
            return Math.Min(1400, attackers * 420 + latent * 150);
        }

        private static Piece RayPiece(Board board, Position king, Position dir, int index)
        {
            int n = 0;
            for (int x = king.X + dir.X, y = king.Y + dir.Y; x >= 0 && x < 8 && y >= 0 && y < 8; x += dir.X, y += dir.Y)
            {
                var p = board.GetPiece(x, y);
                if (p == null) continue;
                if (++n == index) return p;
            }
            return null;
        }

        private static bool IsKnight(Position from, Position to)
        {
            int dx = Math.Abs(to.X - from.X), dy = Math.Abs(to.Y - from.Y);
            return (dx == 1 && dy == 2) || (dx == 2 && dy == 1);
        }

        private static bool IsPawn(Position from, PieceColor color, Position to)
        {
            int dir = color == PieceColor.White ? 1 : -1;
            return to.Y - from.Y == dir && Math.Abs(to.X - from.X) == 1;
        }
    }
}
