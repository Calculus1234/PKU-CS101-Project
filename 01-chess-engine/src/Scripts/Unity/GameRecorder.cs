using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Unity
{
    // 赛后复盘专用：整局结束后一次性落盘，包含双方真实本态（上帝视角）。
    // 在线决策从不读取本文件；记录器只在本地人机对局启用。
    public sealed class GameRecorder
    {
        private sealed class MoveRecord
        {
            public int Ply;
            public PieceColor Color;
            public string From;
            public string To;
            public PieceType Visible;
            public PieceType True;
            public PieceType? CapturedVisible;
            public PieceType? CapturedTrue;
            public PieceType? Promotion;
            public bool Castling;
            public bool KingCapture;
            public string Engine;
            public string Leak;
        }

        private readonly List<MoveRecord> _moves = new List<MoveRecord>();
        private string _startedAt;
        private PieceColor _playerColor;
        private string _aiName;
        private string _formation;
        private string _profile;

        public bool HasMoves => _moves.Count > 0;

        public void Begin(PieceColor playerColor, string aiName, string formation, string profile)
        {
            _moves.Clear();
            _startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _playerColor = playerColor;
            _aiName = aiName;
            _formation = formation;
            _profile = profile;
        }

        public void RecordMove(int ply, PieceColor color, Move move, Board before, MoveResult result, string engine)
        {
            if (move == null || before == null) return;

            var moving = before.GetPiece(move.From);
            var captured = result != null ? result.CapturedPiece : null;

            _moves.Add(new MoveRecord
            {
                Ply = ply,
                Color = color,
                From = Position.ToAlgebraic(move.From),
                To = Position.ToAlgebraic(move.To),
                Visible = moving != null ? moving.VisibleType : PieceType.None,
                True = moving != null ? moving.TrueType : PieceType.None,
                CapturedVisible = captured != null ? captured.VisibleType : (PieceType?)null,
                CapturedTrue = captured != null ? captured.TrueType : (PieceType?)null,
                Promotion = move.PromotionType,
                Castling = move.IsCastling,
                KingCapture = result != null && result.IsKingCapture,
                Engine = engine ?? "",
                Leak = result != null ? result.LeakType.ToString() : ""
            });
        }

        public void Finish(PieceColor? winner, string reason, string detail)
        {
            if (_moves.Count == 0) return;

            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "ChessDarkGames");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "game_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                File.WriteAllText(file, BuildJson(winner, reason, detail), new UTF8Encoding(false));
                Debug.Log("[GameRecorder] 棋谱已保存（赛后复盘用）：" + file);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameRecorder] 棋谱保存失败：" + e.Message);
            }
        }

        private string BuildJson(PieceColor? winner, string reason, string detail)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"game\": {");
            sb.Append("\n    \"startedAt\": \"").Append(Escape(_startedAt)).Append("\",");
            sb.Append("\n    \"playerColor\": \"").Append(_playerColor).Append("\",");
            sb.Append("\n    \"ai\": \"").Append(Escape(_aiName)).Append("\",");
            sb.Append("\n    \"formation\": \"").Append(Escape(_formation)).Append("\",");
            sb.Append("\n    \"profileAtStart\": \"").Append(Escape(_profile)).Append("\"");
            sb.Append("\n  },\n  \"moves\": [");

            for (int i = 0; i < _moves.Count; i++)
            {
                var m = _moves[i];
                if (i > 0) sb.Append(',');
                sb.Append("\n    {");
                sb.Append("\"ply\":").Append(m.Ply);
                sb.Append(",\"color\":\"").Append(m.Color).Append('"');
                sb.Append(",\"from\":\"").Append(m.From).Append('"');
                sb.Append(",\"to\":\"").Append(m.To).Append('"');
                sb.Append(",\"visible\":\"").Append(m.Visible).Append('"');
                sb.Append(",\"true\":\"").Append(m.True).Append('"');
                sb.Append(",\"capturedVisible\":").Append(m.CapturedVisible.HasValue ? "\"" + m.CapturedVisible.Value + "\"" : "null");
                sb.Append(",\"capturedTrue\":").Append(m.CapturedTrue.HasValue ? "\"" + m.CapturedTrue.Value + "\"" : "null");
                sb.Append(",\"promotion\":").Append(m.Promotion.HasValue ? "\"" + m.Promotion.Value + "\"" : "null");
                sb.Append(",\"castling\":").Append(m.Castling ? "true" : "false");
                sb.Append(",\"kingCapture\":").Append(m.KingCapture ? "true" : "false");
                sb.Append(",\"leak\":\"").Append(Escape(m.Leak)).Append('"');
                sb.Append(",\"engine\":\"").Append(Escape(m.Engine)).Append("\"}");
            }

            sb.Append("\n  ],\n  \"result\": {");
            sb.Append("\n    \"winner\":").Append(winner.HasValue ? "\"" + winner.Value + "\"" : "null");
            sb.Append(",\n    \"reason\":\"").Append(Escape(reason)).Append('"');
            sb.Append(",\n    \"detail\":\"").Append(Escape(detail)).Append('"');
            sb.Append(",\n    \"plies\":").Append(_moves.Count);
            sb.Append("\n  }\n}\n");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
