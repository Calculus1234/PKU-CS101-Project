using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // 后台预判缓存：玩家落子前直接算完整 EMM 答案，落子后 0ms 取用。
    public sealed class PonderResult
    {
        public Move PlayerMove { get; internal set; }
        public string Signature { get; internal set; }

        // 终版 Ponder 的产物：完整的 Expectiminimax 答案与它的搜索画像。
        public Move? BestMove { get; internal set; }
        public int SearchDepth { get; internal set; }
        public double QuiescenceDepth { get; internal set; }
        public long Nodes { get; internal set; }

        // 旧 MCTS 缓存字段：仅保留给 MCTS 引擎模式读取，终版 Ponder 不再填充。
        public Move? MctsChoice { get; internal set; }
        public IReadOnlyList<RootMoveStat> TopMoves { get; internal set; }
        public int Iterations { get; internal set; }
        public int Rollouts { get; internal set; }
        public int Worlds { get; internal set; }
        public long ThinkMs { get; internal set; }

        // 该预测落点对 AI 的公开威胁分：用于日志说明“不仅猜落点，还预判最强进攻线”。
        public double PlayerThreatScore { get; internal set; }

        // 缓存答案的搜索画像：命中后报表仍能统计残局深度与节点率。
        public bool EndgameMode { get; internal set; }
        public int TargetDepth { get; internal set; }
        public int EffectiveSamples { get; internal set; }
        public double NodesPerSecond { get; internal set; }
    }

    // 已经算完的搜索句柄：Hybrid 快路径直接把它当作 MCTS 阶段的结果。
    public sealed class CachedMctsSession : AISearchSession
    {
        private readonly Move? _result;
        private readonly List<RootMoveStat> _top;

        public CachedMctsSession(Move? result, IReadOnlyList<RootMoveStat> top)
        {
            _result = result;
            _top = top != null ? new List<RootMoveStat>(top) : null;
        }

        public override bool IsFinished => true;

        public override Move? Result => _result;

        public override bool Advance(int sliceMs) => true;

        public override IReadOnlyList<RootMoveStat> GetTopMoves(int count)
        {
            if (_top == null || count <= 0) return null;
            if (count >= _top.Count) return _top;

            var sliced = new List<RootMoveStat>(count);
            for (int i = 0; i < count; i++) sliced.Add(_top[i]);
            return sliced;
        }
    }

    // 局面签名只用于“预测走法是否命中”，不参与任何评估，避免跨线程比较时泄漏额外信息。
    public static class BoardSignature
    {
        public static string Of(Board board)
        {
            var chars = new char[64];
            for (int i = 0; i < 64; i++)
            {
                var piece = board.GetPiece(i & 7, i >> 3);
                // 只用公开的伪装态做签名：隐藏本态不参与缓存键，避免 Ponder 偷看真实阵容。
                chars[i] = piece == null
                    ? '.'
                    : (char)((piece.Color == PieceColor.White ? 'A' : 'a') + (int)piece.VisibleType);
            }
            return new string(chars);
        }
    }

    // Ponder：玩家思考期间，后台线程按“最可能的若干落子点”直接跑完整 Expectiminimax。
    // 只使用 AI 自己的信念快照，不读取玩家本态；玩家落子后按局面签名 0ms 取用。
    public sealed class PonderPlanner
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const bool ThreadingSupported = false;
#else
        private const bool ThreadingSupported = true;
#endif

        private readonly PieceColor _aiColor;
        private readonly int _sampleCount;
        private readonly int _searchDepth;
        private readonly int _seed;
        private readonly int _candidateCount;
        private readonly int _perCandidateMs;
        private readonly int _maxPonderMs;

        private readonly object _lock = new object();
        private Dictionary<string, PonderResult> _results = new Dictionary<string, PonderResult>();
        private PlayerTendency _tendency;

        private volatile bool _cancel;
        private int _generation;
        private Thread _worker;

        public string LastError { get; private set; }
        public bool IsRunning => _worker != null && _worker.IsAlive;

        public int Requests { get; private set; }
        public int Hits { get; private set; }
        public double HitRate => Requests <= 0 ? 0.0 : Hits / (double)Requests;
        public int LastSearchedCandidates { get; private set; }

        public PonderPlanner(PieceColor aiColor, int sampleCount, int seed,
            int candidateCount = 4, int perCandidateMs = 2000, int searchDepth = 8)
        {
            _aiColor = aiColor;
            _sampleCount = Math.Max(1, sampleCount);
            _searchDepth = Math.Max(1, searchDepth);
            _seed = seed;
            _candidateCount = Math.Max(2, candidateCount);
            _perCandidateMs = Math.Max(120, perCandidateMs);
            _maxPonderMs = _candidateCount * _perCandidateMs;
        }

        public void SetPlayerTendency(PlayerTendency tendency)
        {
            _tendency = tendency;
        }

        // Unity 实时入口：玩家回合开始时调用，后台线程会一直跑到被 Consume/Stop 打断。
        public void Start(Board board, BeliefState belief)
        {
            if (!ThreadingSupported || board == null || belief == null) return;

            Stop();

            int generation = ++_generation;
            _cancel = false;
            var jobBoard = board.Clone();
            var jobBelief = belief.Clone();

            lock (_lock)
            {
                _results = new Dictionary<string, PonderResult>();
                LastError = null;
            }

            _worker = new Thread(() => RunThread(jobBoard, jobBelief, generation))
            {
                IsBackground = true,
                Name = "ChessDarkPonder"
            };
            _worker.Start();
        }

        // 无头测试用：没有真实玩家思考时间，直接同步跑满一个预算窗口。
        public void WarmUp(Board board, BeliefState belief, int windowMs)
        {
            if (board == null || belief == null) return;

            Stop();
            int generation = ++_generation;
            _cancel = false;

            lock (_lock)
            {
                _results = new Dictionary<string, PonderResult>();
                LastError = null;
            }

            AIPlayer.MuteCurrentThread = true;
            try
            {
                RunCandidates(board.Clone(), belief.Clone(), generation, Math.Max(0, windowMs));
            }
            catch (Exception e)
            {
                LastError = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                AIPlayer.MuteCurrentThread = false;
            }
        }

        // 玩家已经落子：按“落子后局面”取预测结果。
        // 命中后不打断后台线程：它会继续把剩余候选算完；下一回合 Start 会统一停掉旧线程。
        public PonderResult Consume(Board boardAfterMove)
        {
            if (boardAfterMove == null) return null;
            string signature = BoardSignature.Of(boardAfterMove);

            lock (_lock)
            {
                Requests++;
                PonderResult result;
                if (_results.TryGetValue(signature, out result))
                {
                    _results.Remove(signature);
                    Hits++;
                    return result;
                }
            }

            return null;
        }

        public void Stop()
        {
            _cancel = true;
            JoinWorker(25);
        }

        // 时光回溯：旧时间线的预判结果必须整体丢弃，否则玩家重下同一步会命中过期缓存。
        public void ClearCache()
        {
            lock (_lock)
            {
                _results = new Dictionary<string, PonderResult>();
            }
        }

        private void JoinWorker(int timeoutMs)
        {
            var worker = _worker;
            if (worker == null) return;
            worker.Join(timeoutMs);
        }

        private void RunThread(Board board, BeliefState belief, int generation)
        {
            AIPlayer.MuteCurrentThread = true;
            try
            {
                RunCandidates(board, belief, generation, _maxPonderMs);
            }
            catch (Exception e)
            {
                lock (_lock)
                {
                    LastError = $"{e.GetType().Name}: {e.Message}";
                }
            }
            finally
            {
                AIPlayer.MuteCurrentThread = false;
            }
        }

        private void RunCandidates(Board board, BeliefState belief, int generation, int windowMs)
        {
            var watch = Stopwatch.StartNew();
            var moves = PredictMoves(board);
            int index = 0;

            foreach (var move in moves)
            {
                if (_cancel || generation != _generation) return;
                if (watch.ElapsedMilliseconds >= windowMs) return;

                var result = SearchCandidate(board, belief, move, generation, index++);
                LastSearchedCandidates = index;
                if (result == null) continue;

                lock (_lock)
                {
                    if (generation == _generation && !_cancel)
                        _results[result.Signature] = result;
                }
            }
        }

        // 直接跑完整的 Expectiminimax：缓存的是“最终答案”，不再缓存 MCTS 候选。
        private PonderResult SearchCandidate(Board board, BeliefState belief,
            Move move, int generation, int index)
        {
            var pre = board.Clone();
            var moving = pre.GetPiece(move.From);
            if (moving == null) return null;

            PieceType visible = moving.VisibleType;
            var leak = MoveGenerator.IsValidMoveForPieceType(pre, move.From, move.To, visible, moving.Color)
                ? MoveLeakType.Disguised
                : MoveLeakType.Suspicious;

            var after = board.Clone();
            after.ExecuteMove(move);

            var engine = new ExpectiminimaxAI(_aiColor, _seed + generation * 131 + index * 17,
                _sampleCount, _searchDepth, _perCandidateMs);
            engine.SetPlayerTendency(_tendency, false);
            engine._beliefState.CopyFrom(belief);
            engine.NotifyOpponentMove(move, visible, leak, pre);
            engine.SearchTimeBudgetMs = _perCandidateMs;

            var session = engine.CreateSearchSession(after);
            while (!session.IsFinished)
            {
                if (_cancel || generation != _generation) return null;
                session.Advance(20);
            }

            if (!session.Result.HasValue) return null;

            return new PonderResult
            {
                PlayerMove = move,
                Signature = BoardSignature.Of(after),
                BestMove = session.Result,
                SearchDepth = engine.LastSearchDepth,
                QuiescenceDepth = engine.LastQuiescenceDepth,
                Nodes = engine.LastSearchNodes,
                ThinkMs = _perCandidateMs,
                PlayerThreatScore = ScorePlayerThreat(board, move),
                EndgameMode = engine.LastSearchWasEndgame,
                TargetDepth = engine.LastTargetDepth,
                EffectiveSamples = engine.LastEffectiveSamples,
                NodesPerSecond = engine.LastNodesPerSecond
            };
        }

        // 只用公开信息估玩家这一步对 AI 的威胁：是否直攻 AI 王、是否咬住 AI 大子。
        // 这让 Ponder 不只猜“落点”，还按“玩家的最强进攻线”排序候选。
        private double ScorePlayerThreat(Board board, Move move)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null) return 0.0;

            var after = board.Clone();
            after.ExecuteMove(move);
            PieceColor playerColor = AIPlayer.OpponentColor(_aiColor);
            double threat = 0.0;

            var king = board.FindKing(_aiColor);
            if (king.HasValue)
            {
                if (MoveGenerator.CanPieceAttackSquare(after, move.To, piece.VisibleType, playerColor, king.Value))
                {
                    threat += 90.0;
                }
                else
                {
                    int dist = Math.Abs(move.To.X - king.Value.X) + Math.Abs(move.To.Y - king.Value.Y);
                    if (dist <= 3) threat += 18.0;
                }
            }

            int bestValue = 0;
            foreach (var (pos, own) in board.GetPiecesOfColor(_aiColor))
            {
                if (own.TrueType == PieceType.King) continue;
                if (MoveGenerator.CanPieceAttackSquare(after, move.To, piece.VisibleType, playerColor, pos))
                    bestValue = Math.Max(bestValue, own.TrueType.GetValue());
            }
            if (bestValue > 0) threat += bestValue / 10.0;

            return threat;
        }

        // 预测只用公开信息：先把玩家棋子全部按伪装态铺成一张“公平棋盘”，
        // 走法生成与打分都不碰隐藏本态，避免 Ponder 偷看玩家真实阵容。
        private static Board BuildPredictionBoard(Board board, PieceColor playerColor)
        {
            var fair = board.Clone();
            foreach (var (pos, piece) in board.GetPiecesOfColor(playerColor))
            {
                fair.SetPiece(pos, new Piece(piece.VisibleType, piece.VisibleType, piece.Color)
                {
                    HasMoved = piece.HasMoved
                });
            }
            return fair;
        }

        // 预测玩家最可能的走法：吃子、前压、占中路，再叠加历史性格偏置与最强进攻线。
        private List<Move> PredictMoves(Board board)
        {
            PieceColor playerColor = AIPlayer.OpponentColor(_aiColor);
            var fair = BuildPredictionBoard(board, playerColor);
            var moves = MoveGenerator.GeneratePseudoLegalMoves(fair, playerColor);
            if (moves.Count == 0) return moves;

            var tendency = _tendency;
            float confidence = tendency != null ? tendency.Confidence : 0f;
            float aggression = tendency != null ? tendency.Aggression * confidence : 0f;
            float center = tendency != null ? tendency.CenterBias * confidence : 0f;
            float flank = tendency != null ? tendency.FlankBias * confidence : 0f;
            float defense = tendency != null ? tendency.Defensiveness * confidence : 0f;

            var scored = new List<(Move move, double score)>(moves.Count);
            foreach (var move in moves)
            {
                var piece = fair.GetPiece(move.From);
                if (piece == null) continue;

                double score = 0.0;
                var target = fair.GetPiece(move.To);
                if (target != null)
                    score += 60.0 + target.VisibleType.GetValue() / 15.0;

                int direction = playerColor == PieceColor.White ? 1 : -1;
                int advance = (move.To.Y - move.From.Y) * direction;
                score += advance * (2.0 + 3.0 * aggression);
                if (advance < 0) score += 2.0 * defense;

                score += (8.0 - Math.Abs(move.To.X - 3.5)) * (1.0 + 1.5 * center);
                if (move.To.X <= 1 || move.To.X >= 6) score += 8.0 * flank;

                if (move.PromotionType.HasValue) score += 40.0;
                if (move.IsCastling) score += 8.0;

                score += ScorePlayerThreat(fair, move);
                scored.Add((move, score));
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));

            var predicted = new List<Move>(_candidateCount);
            var signatures = new HashSet<string>();
            foreach (var item in scored)
            {
                var after = fair.Clone();
                after.ExecuteMove(item.move);
                if (!signatures.Add(BoardSignature.Of(after))) continue;

                predicted.Add(item.move);
                if (predicted.Count >= _candidateCount) break;
            }

            return predicted;
        }
    }
}
