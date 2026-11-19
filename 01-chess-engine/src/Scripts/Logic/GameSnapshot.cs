using System.Collections.Generic;
using ChessDark.Core;

namespace ChessDark.Logic
{
    // 底层时光回溯快照：棋盘全貌、双王本态/伪装态、双方信念矩阵、回合数与重复局面计数。
    // 不依赖 Unity；当前只暴露给代码，后续剧情系统可直接调用 GameEngine 的存取接口。
    public sealed class GameSnapshot
    {
        internal readonly Board BoardState;
        internal readonly BeliefState WhiteBelief;
        internal readonly BeliefState BlackBelief;
        internal readonly Dictionary<ulong, int> PositionCounts;

        public int TurnNumber { get; }
        public PieceColor CurrentTurn { get; }
        public bool IsGameOver { get; }
        public PieceColor? Winner { get; }
        public GameEndReason EndReason { get; }

        internal GameSnapshot(Board board, PieceColor turn, int turnNumber, bool isGameOver,
            PieceColor? winner, GameEndReason endReason, BeliefState whiteBelief,
            BeliefState blackBelief, Dictionary<ulong, int> positionCounts)
        {
            BoardState = board;
            CurrentTurn = turn;
            TurnNumber = turnNumber;
            IsGameOver = isGameOver;
            Winner = winner;
            EndReason = endReason;
            WhiteBelief = whiteBelief;
            BlackBelief = blackBelief;
            PositionCounts = positionCounts;
        }
    }
}
