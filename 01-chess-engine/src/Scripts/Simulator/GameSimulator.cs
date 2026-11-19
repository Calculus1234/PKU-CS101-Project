using System;
using System.Diagnostics;
using ChessDark.Core;
using ChessDark.Logic;
using ChessDark.AI;

namespace ChessDark.Simulator
{
    public class GameSimulator
    {
        private readonly GameEngine _engine;
        private readonly AIPlayer _whiteAI;
        private readonly AIPlayer _blackAI;
        private readonly int _maxMoves;

        public GameSimulator(int seed = 42, int maxMoves = 300)
        {
            _engine = new GameEngine(seed);
            _whiteAI = new ExpectiminimaxAI(PieceColor.White, seed: seed + 1, sampleCount: 16, searchDepth: 3,
                timeLimitMs: 1500);
            _blackAI = new ExpectiminimaxAI(PieceColor.Black, seed: seed + 2, sampleCount: 16, searchDepth: 3,
                timeLimitMs: 1500);
            _maxMoves = maxMoves;
        }

        public void Run()
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  国际象棋怪谈 - 暗棋 AI 对弈模拟器");
            Console.WriteLine("========================================\n");

            _engine.InitializeGame();
            // 可见标签只是公开外壳，不能当先验。
            _whiteAI.SetVisibleLabelTrust(0f);
            _blackAI.SetVisibleLabelTrust(0f);
            _whiteAI.ObserveGameStart(_engine.Board);
            _blackAI.ObserveGameStart(_engine.Board);
            _engine.PrintState(showAll: true);

            Console.WriteLine("\n--- 对弈开始 ---\n");

            var totalStopwatch = Stopwatch.StartNew();

            while (!_engine.IsGameOver && _engine.MoveCount < _maxMoves)
            {
                var ai = _engine.CurrentTurn == PieceColor.White ? _whiteAI : _blackAI;
                var opponentAI = _engine.CurrentTurn == PieceColor.White ? _blackAI : _whiteAI;
                var aiName = _engine.CurrentTurn == PieceColor.White ? "白方AI" : "黑方AI";

                Console.WriteLine($"--- {aiName} 思考中 (第 {_engine.MoveCount + 1} 步) ---");

                var moveStopwatch = Stopwatch.StartNew();
                Move? bestMove;
                try
                {
                    bestMove = ai.GetBestMove(_engine.Board);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  AI 搜索异常: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    break;
                }
                moveStopwatch.Stop();

                if (!bestMove.HasValue)
                {
                    Console.WriteLine($"  {aiName} 无子可动，跳过回合");
                    _engine.SkipTurn();
                    continue;
                }

                Move move = bestMove.Value;
                var preBoard = _engine.Board.Clone();
                var movingVisible = preBoard.GetPiece(move.From)?.VisibleType ?? PieceType.Pawn;

                MoveResult result;
                try
                {
                    result = _engine.MakeMove(move);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  走法执行失败: {ex.Message}");
                    break;
                }

                ai.NotifyOwnMove(move, result.CapturedPiece != null, preBoard);
                if (result.CapturedPiece != null)
                    ai.NotifyCapture(move.To);

                opponentAI.NotifyOpponentMove(move, movingVisible, result.LeakType, preBoard);
                if (move.PromotionType.HasValue)
                    opponentAI.NotifyPromotion(move.To, move.PromotionType);

                var movingPiece = result.MovingPiece;
                string leakStr = result.LeakType == MoveLeakType.Suspicious ? "True" : "False";
                string captureStr = result.CapturedPiece != null
                    ? $", 吃子: 伪装={result.CapturedPiece.VisibleType.ToChinese()}/真实={result.CapturedPiece.TrueType.ToChinese()}"
                    : "";
                string promoStr = move.PromotionType.HasValue
                    ? $", 升变: {move.PromotionType.Value.ToChinese()}"
                    : "";

                Console.WriteLine($"  {aiName}移动: {move.From} -> {move.To}, " +
                    $"伪装态: {movingPiece.VisibleType.ToChinese()}, " +
                    $"真实态: {movingPiece.TrueType.ToChinese()}, " +
                    $"是否泄露: {leakStr}{captureStr}{promoStr} " +
                    $"[{moveStopwatch.ElapsedMilliseconds}ms]");

                if (result.GameOver)
                {
                    Console.WriteLine($"\n*** 游戏结束! {result.Winner} 方获胜! (吃掉对方王) ***");
                    break;
                }

                if ((_engine.MoveCount) % 10 == 0)
                    _engine.PrintState(showAll: false);
            }

            totalStopwatch.Stop();

            if (!_engine.IsGameOver)
                Console.WriteLine($"\n达到最大步数限制 ({_maxMoves})，对弈结束。");

            Console.WriteLine($"\n总步数: {_engine.MoveCount}");
            Console.WriteLine($"总耗时: {totalStopwatch.Elapsed.TotalSeconds:F1} 秒");

            _engine.PrintState(showAll: true);
        }
    }
}
