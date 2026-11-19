using UnityEngine;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Unity
{
    // 只记录玩家自己的行棋统计，供开局选阵使用；不保存任何棋局内隐藏本态。
    public class PlayerProfileStore
    {
        private const string Prefix = "ChessDark.Profile.";

        private readonly PlayerTendency _tendency = new PlayerTendency();

        public PlayerTendency Tendency => _tendency;

        public static PlayerProfileStore Load()
        {
            var store = new PlayerProfileStore();
            var t = store._tendency;
            t.Games = PlayerPrefs.GetInt(Prefix + "Games", 0);
            t.Wins = PlayerPrefs.GetInt(Prefix + "Wins", 0);
            t.Losses = PlayerPrefs.GetInt(Prefix + "Losses", 0);
            t.Moves = PlayerPrefs.GetInt(Prefix + "Moves", 0);
            t.ForwardMoves = PlayerPrefs.GetInt(Prefix + "ForwardMoves", 0);
            t.Captures = PlayerPrefs.GetInt(Prefix + "Captures", 0);
            t.CenterMoves = PlayerPrefs.GetInt(Prefix + "CenterMoves", 0);
            t.FlankMoves = PlayerPrefs.GetInt(Prefix + "FlankMoves", 0);
            t.DefensiveMoves = PlayerPrefs.GetInt(Prefix + "DefensiveMoves", 0);
            t.PawnRushes = PlayerPrefs.GetInt(Prefix + "PawnRushes", 0);
            t.PiecesLost = PlayerPrefs.GetInt(Prefix + "PiecesLost", 0);
            return store;
        }

        public PlayerTendency CreateSnapshot()
        {
            var t = _tendency;
            return new PlayerTendency
            {
                Games = t.Games,
                Wins = t.Wins,
                Losses = t.Losses,
                Moves = t.Moves,
                ForwardMoves = t.ForwardMoves,
                Captures = t.Captures,
                CenterMoves = t.CenterMoves,
                FlankMoves = t.FlankMoves,
                DefensiveMoves = t.DefensiveMoves,
                PawnRushes = t.PawnRushes,
                PiecesLost = t.PiecesLost
            };
        }

        public void RecordMove(Move move, Piece movingPiece, bool wasCapture)
        {
            if (move == null || movingPiece == null) return;

            var t = _tendency;
            t.Moves++;

            int direction = movingPiece.Color == PieceColor.White ? 1 : -1;
            int advance = (move.To.Y - move.From.Y) * direction;
            if (advance > 0) t.ForwardMoves++;
            else if (advance < 0) t.DefensiveMoves++;

            if (wasCapture) t.Captures++;

            int file = move.To.X;
            if (file >= 2 && file <= 5) t.CenterMoves++;
            if (file <= 1 || file >= 6) t.FlankMoves++;

            if (movingPiece.TrueType == PieceType.Pawn)
            {
                bool inEnemyHalf = movingPiece.Color == PieceColor.White ? move.To.Y >= 4 : move.To.Y <= 3;
                if (advance >= 2 || inEnemyHalf) t.PawnRushes++;
            }
        }

        // AI 吃掉玩家棋子：这是“换子意愿/弃子倾向”的另一半，必须实时记账。
        public void RecordPieceLost()
        {
            _tendency.PiecesLost++;
        }

        public void RecordGame(bool? playerWon)
        {
            _tendency.Games++;
            if (playerWon == true) _tendency.Wins++;
            else if (playerWon == false) _tendency.Losses++;
        }

        public void Save()
        {
            var t = _tendency;
            PlayerPrefs.SetInt(Prefix + "Games", t.Games);
            PlayerPrefs.SetInt(Prefix + "Wins", t.Wins);
            PlayerPrefs.SetInt(Prefix + "Losses", t.Losses);
            PlayerPrefs.SetInt(Prefix + "Moves", t.Moves);
            PlayerPrefs.SetInt(Prefix + "ForwardMoves", t.ForwardMoves);
            PlayerPrefs.SetInt(Prefix + "Captures", t.Captures);
            PlayerPrefs.SetInt(Prefix + "CenterMoves", t.CenterMoves);
            PlayerPrefs.SetInt(Prefix + "FlankMoves", t.FlankMoves);
            PlayerPrefs.SetInt(Prefix + "DefensiveMoves", t.DefensiveMoves);
            PlayerPrefs.SetInt(Prefix + "PawnRushes", t.PawnRushes);
            PlayerPrefs.SetInt(Prefix + "PiecesLost", t.PiecesLost);
            PlayerPrefs.Save();
        }
    }
}
