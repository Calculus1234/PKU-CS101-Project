using System;
using System.Collections.Generic;
using System.Diagnostics;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.AI
{
    // 实验组：Determined MCTS（PIMC）。每次思考从 BeliefState 采样多个虚构世界，
    // 每个世界独立建树，用 UCB1 分配迭代，最终按跨世界胜率选择根走法。
    public sealed class MctsAI : AIPlayer
    {
        private const double TrapLossValue = 0.02;

        private readonly int _determinizationCount;
        private readonly int _rolloutPlies;
        private readonly double _exploration;
        private readonly Random _searchRng;

        public int LastIterations { get; internal set; }
        public int LastRollouts { get; internal set; }
        public int LastWorlds { get; internal set; }

        // Hybrid 可注入 Baseline 的静态评估，让 rollout 叶节点与 Expectiminimax 完全对齐。
        internal Func<Board, PieceColor, double> LeafEvaluator;

        public MctsAI(PieceColor color, int seed = 42, int determinizationCount = 64,
            int timeLimitMs = 3500, int rolloutPlies = 36, double exploration = 1.4)
            : base(color, seed, timeLimitMs)
        {
            _determinizationCount = Math.Max(2, determinizationCount);
            _rolloutPlies = Math.Max(8, rolloutPlies);
            _exploration = Math.Max(0.1, exploration);
            _searchRng = new Random(seed * 31 + 17);
        }

        public override void SetPlayerTendency(PlayerTendency tendency, bool announce = true)
        {
            base.SetPlayerTendency(tendency, announce);
            if (announce && tendency != null && tendency.HasHistory)
                Log("[MCTS] 已成功载入玩家心理画像，采样权重已倾斜。");
        }

        public static new void ApplyPlatformPreset(ref int determinizationCount, ref int timeLimitMs)
        {
            if (AIPlayer.MobileProfile)
            {
                determinizationCount = Math.Min(6, Math.Max(4, determinizationCount));
                timeLimitMs = Math.Min(AIPlayer.MobileTimeBudgetMs, Math.Max(400, timeLimitMs));
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            determinizationCount = Math.Min(8, Math.Max(4, determinizationCount));
            timeLimitMs = Math.Min(1200, Math.Max(800, timeLimitMs / 3));
#else
            determinizationCount = Math.Min(128, Math.Max(64, determinizationCount));
            timeLimitMs = Math.Min(4000, Math.Max(3000, timeLimitMs));
#endif
        }

        public override AISearchSession CreateSearchSession(Board board)
        {
            _beliefState.EnsureInitialized(board);
            return new MctsSession(this, board);
        }

        // 纯 MCTS 的 Ponder 快路径：缓存已经包含根候选与决策，可直接出招。
        public override AISearchSession CreateSearchSession(Board board, PonderResult ponder)
        {
            // 绝杀快速通道优先于一切缓存：能吃王就不看 Ponder。
            var instantKill = FindImmediateKingCapture(board);
            if (instantKill.HasValue)
            {
                LastIterations = 0;
                LastRollouts = 0;
                LastWorlds = 0;
                Log($"[MCTS] 绝杀快速通道：直接吃掉最可能的王位 {instantKill.Value.To}，立即 {instantKill.Value}（0ms）。");
                return new AIPlayer.ImmediateMoveSession(instantKill.Value);
            }

            if (ponder == null || !ponder.MctsChoice.HasValue)
                return CreateSearchSession(board);

            LastIterations = ponder.Iterations;
            LastRollouts = ponder.Rollouts;
            LastWorlds = ponder.Worlds;
            return new CachedMctsSession(ponder.MctsChoice, ponder.TopMoves);
        }

        private sealed class Node
        {
            public Move Move;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public List<Move> Untried = new List<Move>();
            public int Visits;
            public double Wins;
            public bool Terminal;
            public double TerminalValue;
            public PieceColor SideToMove;
        }

        private sealed class World
        {
            public Board Board;
            public Node Root;
            public Random Rng;
        }

        private sealed class MctsSession : AISearchSession
        {
            private static readonly Position[] KnightOffsets =
            {
                new Position(-2, -1), new Position(-2, 1), new Position(-1, -2), new Position(-1, 2),
                new Position(1, -2), new Position(1, 2), new Position(2, -1), new Position(2, 1)
            };

            private static readonly Position[] KingOffsets =
            {
                new Position(-1, -1), new Position(-1, 0), new Position(-1, 1),
                new Position(0, -1), new Position(0, 1),
                new Position(1, -1), new Position(1, 0), new Position(1, 1)
            };

            // 与 Baseline 对齐的 PST：Rollout 叶节点不再只认子力，也要认位置质量。
            private static readonly int[,] PstPawn =
            {
                { 0,  0,  0,  0,  0,  0,  0,  0 },
                { 50, 50, 50, 50, 50, 50, 50, 50 },
                { 10, 10, 20, 30, 30, 20, 10, 10 },
                { 5,  5, 10, 25, 25, 10,  5,  5 },
                { 0,  0,  0, 20, 20,  0,  0,  0 },
                { 5, -5,-10,  0,  0,-10, -5,  5 },
                { 5, 10, 10,-20,-20, 10, 10,  5 },
                { 0,  0,  0,  0,  0,  0,  0,  0 }
            };

            private static readonly int[,] PstKnight =
            {
                {-50,-40,-30,-30,-30,-30,-40,-50 },
                {-40,-20,  0,  0,  0,  0,-20,-40 },
                {-30,  0, 10, 15, 15, 10,  0,-30 },
                {-30,  5, 15, 20, 20, 15,  5,-30 },
                {-30,  0, 15, 20, 20, 15,  0,-30 },
                {-30,  5, 10, 15, 15, 10,  5,-30 },
                {-40,-20,  0,  5,  5,  0,-20,-40 },
                {-50,-40,-30,-30,-30,-30,-40,-50 }
            };

            private static readonly int[,] PstBishop =
            {
                {-20,-10,-10,-10,-10,-10,-10,-20 },
                {-10,  0,  0,  0,  0,  0,  0,-10 },
                {-10,  0, 10, 10, 10, 10,  0,-10 },
                {-10,  5,  5, 10, 10,  5,  5,-10 },
                {-10,  0,  5, 10, 10,  5,  0,-10 },
                {-10, 10,  5, 10, 10,  5, 10,-10 },
                {-10,  5,  0,  0,  0,  0,  5,-10 },
                {-20,-10,-10,-10,-10,-10,-10,-20 }
            };

            private static readonly int[,] PstRook =
            {
                { 0,  0,  0,  0,  0,  0,  0,  0 },
                { 5, 10, 10, 10, 10, 10, 10,  5 },
                {-5,  0,  0,  0,  0,  0,  0, -5 },
                {-5,  0,  0,  0,  0,  0,  0, -5 },
                {-5,  0,  0,  0,  0,  0,  0, -5 },
                {-5,  0,  0,  0,  0,  0,  0, -5 },
                {-5,  0,  0,  0,  0,  0,  0, -5 },
                { 0,  0,  0,  5,  5,  0,  0,  0 }
            };

            private static readonly int[,] PstQueen =
            {
                {-20,-10,-10, -5, -5,-10,-10,-20 },
                {-10,  0,  0,  0,  0,  0,  0,-10 },
                {-10,  0,  5,  5,  5,  5,  0,-10 },
                { -5,  0,  5,  5,  5,  5,  0, -5 },
                {  0,  0,  5,  5,  5,  5,  0, -5 },
                {-10,  5,  5,  5,  5,  5,  0,-10 },
                {-10,  0,  5,  0,  0,  0,  0,-10 },
                {-20,-10,-10, -5, -5,-10,-10,-20 }
            };

            private static readonly int[,] PstKing =
            {
                {-30,-40,-40,-50,-50,-40,-40,-30 },
                {-30,-40,-40,-50,-50,-40,-40,-30 },
                {-30,-40,-40,-50,-50,-40,-40,-30 },
                {-30,-40,-40,-50,-50,-40,-40,-30 },
                {-20,-30,-30,-40,-40,-30,-30,-20 },
                {-10,-20,-20,-20,-20,-20,-20,-10 },
                { 20, 20,  0,  0,  0,  0, 20, 20 },
                { 20, 30, 10,  0,  0, 10, 30, 20 }
            };

            private readonly MctsAI _owner;
            private readonly Board _board;
            private readonly List<Move> _rootMoves;
            private readonly Dictionary<Move, int> _rootIndex = new Dictionary<Move, int>();
            private readonly List<World> _worlds = new List<World>();
            private readonly Stopwatch _watch = new Stopwatch();
            private readonly double _timeLimitMs;
            private readonly int _determinizationCount;
            private readonly float _profileAggression;

            private readonly List<Node> _visited = new List<Node>(64);
            private readonly List<Board.MoveUndo> _undos = new List<Board.MoveUndo>(64);

            private int[] _aggVisits;
            private double[] _aggWins;
            private double[] _worldRateSum;
            private int[] _worldSeen;
            private int[] _safeWorlds;
            private bool[] _kingCaptureWins;
            private double[] _adjustedRates;
            private double[] _beliefRisks;
            private List<RootMoveStat> _topMoves;
            private int _nextWorld;
            private int _iterations;
            private int _rollouts;
            private int _kingHorizonHits;
            private int _trapDisasters;
            private bool _finished;
            private Move? _result;

            internal MctsSession(MctsAI owner, Board board)
            {
                _owner = owner;
                _board = board;
                _timeLimitMs = owner.SearchTimeBudgetMs;
                _determinizationCount = owner._determinizationCount;
                owner.LastIterations = 0;
                owner.LastRollouts = 0;
                owner.LastWorlds = 0;

                var profile = owner.PlayerProfile;
                if (profile != null && profile.HasHistory)
                    _profileAggression = profile.Aggression * profile.Confidence;

                _rootMoves = MoveGenerator.GeneratePseudoLegalMoves(board, owner._color);
                for (int i = 0; i < _rootMoves.Count; i++)
                    _rootIndex[_rootMoves[i]] = i;

                if (_rootMoves.Count == 0)
                {
                    Log("[MCTS] 无任何可行走法，跳过本回合（不判负）。");
                    _finished = true;
                    return;
                }

                // 绝杀快速通道：只用信念概率（最可能的王位），绝不读取玩家真实本态。
                Move? immediateKing = owner.FindImmediateKingCapture(board);
                if (immediateKing.HasValue)
                {
                    _kingHorizonHits++;
                    _result = immediateKing.Value;
                    PublishSingleTop(_result.Value, 1.0);
                    _finished = true;
                    Log($"[MCTS] 绝杀视界：根节点可直接吃王，立即选择 {_result.Value}。");
                    return;
                }

                if (_rootMoves.Count == 1)
                {
                    _result = _rootMoves[0];
                    PublishSingleTop(_result.Value, 0.5);
                    _finished = true;
                    Log($"[MCTS] 唯一可行走法 {_result.Value}。");
                    return;
                }

                _aggVisits = new int[_rootMoves.Count];
                _aggWins = new double[_rootMoves.Count];
                _worldRateSum = new double[_rootMoves.Count];
                _worldSeen = new int[_rootMoves.Count];

                var samplingBias = BuildSamplingBias(owner);
                for (int i = 0; i < _determinizationCount; i++)
                {
                    var sampled = owner._beliefState.SampleBoard(owner._rng, i, _determinizationCount,
                        samplingBias);
                    if (sampled == null) continue;

                    var merged = owner.MergeBoard(board, sampled);
                    var root = CreateNode(merged, owner._color, default, null);
                    // 根节点是决策节点：即使某个虚构世界里能吃王，也要把这次吃王记到对应走法上，
                    // 不能把整棵树提前判成终局，否则该世界不会贡献任何根走法统计。
                    root.Terminal = false;
                    root.TerminalValue = 0.0;
                    var world = new World
                    {
                        Board = merged,
                        Root = root,
                        Rng = new Random(owner._searchRng.Next())
                    };
                    _worlds.Add(world);
                }

                if (_worlds.Count == 0)
                {
                    _result = FallbackMove();
                    if (_result.HasValue) PublishSingleTop(_result.Value, 0.5);
                    _finished = true;
                    return;
                }

                owner.LastWorlds = _worlds.Count;
                _watch.Start();
                Log($"[MCTS] 开始搜索：{_worlds.Count} 个虚构世界，限时 {_timeLimitMs:F0}ms。");
            }

            public override bool IsFinished => _finished;

            public override Move? Result => _result;

            // Hybrid 复核入口：返回按“安全加权胜率”排序的根候选。MCTS 只负责发现，
            // 最终取舍由外部 Expectiminimax 复核完成。
            public override IReadOnlyList<RootMoveStat> GetTopMoves(int count)
            {
                if (_topMoves == null || count <= 0) return null;
                if (count >= _topMoves.Count) return _topMoves;

                var sliced = new List<RootMoveStat>(count);
                for (int i = 0; i < count; i++)
                    sliced.Add(_topMoves[i]);
                return sliced;
            }

            private void PublishSingleTop(Move move, double rate)
            {
                _topMoves = new List<RootMoveStat>
                {
                    new RootMoveStat
                    {
                        Move = move,
                        Visits = 1,
                        WinRate = rate,
                        WorldRate = rate,
                        Rank = rate
                    }
                };
            }

            public override bool Advance(int sliceMs)
            {
                if (_finished) return true;

                long sliceStart = _watch.ElapsedMilliseconds;
                while (!_finished)
                {
                    if (_timeLimitMs > 0 && _watch.ElapsedMilliseconds >= _timeLimitMs)
                    {
                        Finish();
                        break;
                    }

                    var world = _worlds[_nextWorld];
                    _nextWorld++;
                    if (_nextWorld >= _worlds.Count) _nextWorld = 0;

                    Iterate(world);
                    _iterations++;

                    if (_watch.ElapsedMilliseconds - sliceStart >= sliceMs)
                        break;
                }

                return _finished;
            }

            private void Iterate(World world)
            {
                var board = world.Board.Clone();
                var node = world.Root;
                var side = node.SideToMove;

                _visited.Clear();
                _visited.Add(node);
                _undos.Clear();

                double value;
                while (true)
                {
                    if (node.Terminal)
                    {
                        value = node.TerminalValue;
                        break;
                    }

                    if (node.Untried.Count > 0)
                    {
                        int pick = PickUntriedIndex(node.Untried, board, world.Rng);
                        Move move = node.Untried[pick];
                        node.Untried[pick] = node.Untried[node.Untried.Count - 1];
                        node.Untried.RemoveAt(node.Untried.Count - 1);

                        bool trapDisaster = side == _owner._color && IsDecoyTrapDisaster(board, move);
                        var target = board.GetPiece(move.To);
                        bool kingCapture = target != null && target.TrueType == PieceType.King;

                        var undo = board.ExecuteMoveWithUndo(move);
                        var child = CreateNode(board, OpponentColor(side), move, node);
                        node.Children.Add(child);
                        _undos.Add(undo);

                        if (kingCapture)
                        {
                            child.Terminal = true;
                            child.TerminalValue = side == _owner._color ? 1.0 : 0.0;
                            _kingHorizonHits++;
                        }
                        else if (trapDisaster)
                        {
                            child.Terminal = true;
                            child.TerminalValue = TrapLossValue;
                            _trapDisasters++;
                        }
                        else if (child.Terminal)
                        {
                            _kingHorizonHits++;
                        }

                        _visited.Add(child);
                        node = child;
                        side = child.SideToMove;

                        value = child.Terminal ? child.TerminalValue : Rollout(board, side);
                        break;
                    }

                    if (node.Children.Count == 0)
                    {
                        value = Rollout(board, side);
                        break;
                    }

                    var next = SelectChild(node);
                    var nextUndo = board.ExecuteMoveWithUndo(next.Move);
                    _undos.Add(nextUndo);
                    _visited.Add(next);
                    node = next;
                    side = next.SideToMove;
                }

                for (int i = 0; i < _visited.Count; i++)
                {
                    _visited[i].Visits++;
                    _visited[i].Wins += value;
                }

                for (int i = _undos.Count - 1; i >= 0; i--)
                    board.UndoMove(_visited[i + 1].Move, _undos[i]);
            }

            private Node CreateNode(Board board, PieceColor side, Move move, Node parent)
            {
                var node = new Node
                {
                    Move = move,
                    Parent = parent,
                    SideToMove = side
                };

                node.Untried = MoveGenerator.GeneratePseudoLegalMoves(board, side);
                OrderMoves(board, node.Untried);

                for (int i = 0; i < node.Untried.Count; i++)
                {
                    var target = board.GetPiece(node.Untried[i].To);
                    if (target != null && target.TrueType == PieceType.King)
                    {
                        node.Terminal = true;
                        node.TerminalValue = side == _owner._color ? 1.0 : 0.0;
                        break;
                    }
                }

                return node;
            }

            private Node SelectChild(Node node)
            {
                double logVisits = Math.Log(Math.Max(1, node.Visits));
                bool aiTurn = node.SideToMove == _owner._color;
                Node best = null;
                double bestScore = double.NegativeInfinity;

                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    double rate = child.Visits > 0 ? child.Wins / child.Visits : 0.5;
                    double exploit = aiTurn ? rate : 1.0 - rate;
                    double explore = _owner._exploration * Math.Sqrt(logVisits / Math.Max(1, child.Visits));
                    double score = exploit + explore;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = child;
                    }
                }

                return best ?? node.Children[0];
            }

            private int PickUntriedIndex(List<Move> untried, Board board, Random rng)
            {
                var firstTarget = board.GetPiece(untried[0].To);
                if (firstTarget != null && firstTarget.TrueType == PieceType.King)
                    return 0;

                int limit = Math.Min(3, untried.Count);
                return rng.Next(limit);
            }

            private double Rollout(Board board, PieceColor side)
            {
                _rollouts++;

                for (int ply = 0; ply < _owner._rolloutPlies; ply++)
                {
                    if (_timeLimitMs > 0 && _watch.ElapsedMilliseconds >= _timeLimitMs)
                        break;

                    var moves = MoveGenerator.GeneratePseudoLegalMoves(board, side);
                    if (moves.Count == 0)
                    {
                        side = OpponentColor(side);
                        continue;
                    }

                    Move? kingCapture = FindKingCapture(board, moves);
                    if (kingCapture.HasValue)
                    {
                        _kingHorizonHits++;
                        return side == _owner._color ? 1.0 : 0.0;
                    }

                    Move move = ChooseRolloutMove(board, moves, side, _owner._searchRng);
                    if (side == _owner._color && IsDecoyTrapDisaster(board, move))
                    {
                        _trapDisasters++;
                        return TrapLossValue;
                    }

                    board.ExecuteMove(move);
                    side = OpponentColor(side);
                }

                return EvaluateLeaf(board, side);
            }

            // 把玩家画像翻成采样倾角：高进攻倾向时，深处的敌方棋子更可能是大子，
            // 中路更可能是重子，中路兵被推空的概率也更高（大子前置、中路空虚）。
            private static Func<Position, PieceType, float> BuildSamplingBias(MctsAI owner)
            {
                var tendency = owner.PlayerProfile;
                if (tendency == null || !tendency.HasHistory) return null;

                float confidence = tendency.Confidence;
                float aggression = tendency.Aggression * confidence;
                float center = tendency.CenterBias * confidence;
                float flank = tendency.FlankBias * confidence;
                float defense = tendency.Defensiveness * confidence;
                // 爱弃子的玩家会把大子藏在后场当诱饵，不能默认“重子一定顶在前面”。
                float sacrifice = tendency.SacrificeRate * confidence;
                float trade = (tendency.TradeWillingness - 0.5f) * 2f * confidence;
                if (aggression + center + flank + defense + sacrifice < 0.05f) return null;

                PieceColor enemy = OpponentColor(owner._color);
                return (pos, type) =>
                {
                    float bias = 1f;
                    bool heavy = type == PieceType.Queen || type == PieceType.Rook ||
                                 type == PieceType.Bishop || type == PieceType.Knight;
                    float advance = enemy == PieceColor.White ? pos.Y / 7f : (7 - pos.Y) / 7f;
                    bool centerFile = pos.X == 3 || pos.X == 4;
                    bool flankFile = pos.X <= 1 || pos.X >= 6;

                    if (heavy)
                    {
                        bias *= 1f + 2.4f * aggression * advance;
                        if (centerFile) bias *= 1f + 1.4f * (aggression + center);
                        if (flankFile) bias *= 1f + 0.7f * flank;
                        if (advance < 0.34f) bias *= 1f + 1.2f * defense;
                        if (sacrifice > 0.02f)
                        {
                            // 弃子型玩家：后场“重子”可能是诱饵，前线大子反而可能是小兵。
                            bias *= Math.Max(0.35f, 1f - 0.45f * sacrifice * advance);
                            if (advance < 0.34f) bias *= 1f + 0.6f * sacrifice;
                        }
                        if (trade > 0.02f && advance < 0.5f) bias *= 1f + 0.25f * trade;
                    }
                    else if (type == PieceType.Pawn)
                    {
                        if (advance > 0.6f) bias *= 1f + 0.9f * aggression;
                        if (centerFile && advance > 0.5f) bias *= 1f - 0.5f * aggression;
                        if (advance < 0.34f) bias *= 1f + 0.4f * defense;
                        if (sacrifice > 0.02f && advance > 0.5f) bias *= 1f + 0.5f * sacrifice;
                    }
                    else if (type == PieceType.King && aggression > 0.6f)
                    {
                        if (advance > 0.5f) bias *= 1f + 0.7f * aggression;
                        if (flankFile) bias *= 1f + 0.5f * aggression;
                    }

                    return bias;
                };
            }

            private Move ChooseRolloutMove(Board board, List<Move> moves, PieceColor side, Random rng)
            {
                Move best = moves[0];
                double bestScore = double.NegativeInfinity;
                PieceColor enemy = OpponentColor(side);

                for (int i = 0; i < moves.Count; i++)
                {
                    var move = moves[i];
                    var mover = board.GetPiece(move.From);
                    var target = board.GetPiece(move.To);

                    double score = rng.NextDouble() * 2.0;

                    if (target != null)
                    {
                        score += 20.0 + target.TrueType.GetValue() / 100.0;
                        // 面对高进攻玩家：更愿意吃子反杀，尤其是对方脱节的子。
                        if (side == _owner._color && _profileAggression > 0.05f)
                        {
                            bool defended = IsAttacked(board, move.To, enemy);
                            score += _profileAggression * (defended ? 5.0 : 16.0);
                        }
                    }

                    if (move.PromotionType.HasValue)
                        score += 15.0 + move.PromotionType.Value.GetValue() / 100.0;

                    int moverValue = mover != null ? mover.TrueType.GetValue() : 100;
                    if (IsAttacked(board, move.To, enemy))
                        score -= moverValue / 60.0 * (1.0 - 0.4 * _profileAggression);
                    else
                        score += 1.5;

                    if (side == _owner._color && _profileAggression > 0.05f)
                    {
                        int direction = side == PieceColor.White ? 1 : -1;
                        int advance = (move.To.Y - move.From.Y) * direction;
                        score += advance > 0 ? 2.0 * _profileAggression : -1.2 * _profileAggression;
                    }

                    score += 0.2 * (3.5 - Math.Abs(move.To.X - 3.5))
                                 + 0.2 * (3.5 - Math.Abs(move.To.Y - 3.5));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = move;
                    }
                }

                return best;
            }

            // 方格式攻击检测：只沿 8 个方向反查，避免每次生成整张走法表。
            internal static bool IsAttacked(Board board, Position target, PieceColor attackerColor)
            {
                int pawnY = attackerColor == PieceColor.White ? target.Y - 1 : target.Y + 1;
                if (pawnY >= 0 && pawnY < 8)
                {
                    if (target.X > 0)
                    {
                        var p = board.GetPiece(target.X - 1, pawnY);
                        if (p != null && p.Color == attackerColor && p.TrueType == PieceType.Pawn) return true;
                    }
                    if (target.X < 7)
                    {
                        var p = board.GetPiece(target.X + 1, pawnY);
                        if (p != null && p.Color == attackerColor && p.TrueType == PieceType.Pawn) return true;
                    }
                }

                for (int i = 0; i < KnightOffsets.Length; i++)
                {
                    var pos = target + KnightOffsets[i];
                    if (!pos.IsValid) continue;
                    var p = board.GetPiece(pos);
                    if (p != null && p.Color == attackerColor && p.TrueType == PieceType.Knight) return true;
                }

                for (int i = 0; i < KingOffsets.Length; i++)
                {
                    var pos = target + KingOffsets[i];
                    if (!pos.IsValid) continue;
                    var p = board.GetPiece(pos);
                    if (p != null && p.Color == attackerColor && p.TrueType == PieceType.King) return true;
                }

                for (int d = 0; d < 4; d++)
                {
                    int dx = d == 0 ? 1 : d == 1 ? -1 : 0;
                    int dy = d == 2 ? 1 : d == 3 ? -1 : 0;
                    int x = target.X + dx;
                    int y = target.Y + dy;

                    while (x >= 0 && x < 8 && y >= 0 && y < 8)
                    {
                        var p = board.GetPiece(x, y);
                        if (p != null)
                        {
                            if (p.Color == attackerColor &&
                                (p.TrueType == PieceType.Rook || p.TrueType == PieceType.Queen))
                                return true;
                            break;
                        }
                        x += dx;
                        y += dy;
                    }
                }

                for (int d = 0; d < 4; d++)
                {
                    int dx = d < 2 ? 1 : -1;
                    int dy = d == 0 || d == 2 ? 1 : -1;
                    int x = target.X + dx;
                    int y = target.Y + dy;

                    while (x >= 0 && x < 8 && y >= 0 && y < 8)
                    {
                        var p = board.GetPiece(x, y);
                        if (p != null)
                        {
                            if (p.Color == attackerColor &&
                                (p.TrueType == PieceType.Bishop || p.TrueType == PieceType.Queen))
                                return true;
                            break;
                        }
                        x += dx;
                        y += dy;
                    }
                }

                return false;
            }

            private double EvaluateLeaf(Board board, PieceColor sideToMove)
            {
                var evaluator = _owner.LeafEvaluator;
                if (evaluator != null)
                    return evaluator(board, sideToMove);

                int aiScore = 0;
                int enemyScore = 0;
                int aiBishops = 0;
                int enemyBishops = 0;
                int aiRooks = 0;
                int enemyRooks = 0;
                var aiRookFiles = new bool[8];
                var enemyRookFiles = new bool[8];
                var aiPawnFiles = new bool[8];
                var enemyPawnFiles = new bool[8];
                Position? aiKing = null;
                Position? enemyKing = null;

                foreach (var (pos, piece) in board.GetAllPieces())
                {
                    if (piece.TrueType == PieceType.King)
                    {
                        if (piece.Color == _owner._color) aiKing = pos;
                        else enemyKing = pos;
                        continue;
                    }

                    int value = piece.TrueType.GetValue() + GetPieceSquareValue(piece.TrueType, pos, piece.Color);
                    bool own = piece.Color == _owner._color;

                    if (piece.TrueType == PieceType.Pawn)
                    {
                        value += (piece.Color == PieceColor.White ? pos.Y : 7 - pos.Y) * 6;
                        if (own) aiPawnFiles[pos.X] = true;
                        else enemyPawnFiles[pos.X] = true;
                    }
                    else if (piece.TrueType == PieceType.Bishop)
                    {
                        if (own) aiBishops++; else enemyBishops++;
                    }
                    else if (piece.TrueType == PieceType.Rook)
                    {
                        if (own) { aiRooks++; aiRookFiles[pos.X] = true; }
                        else { enemyRooks++; enemyRookFiles[pos.X] = true; }
                    }

                    if (piece.TrueType != piece.VisibleType) value += 12; // 伪装本身也是信息优势

                    if (own) aiScore += value;
                    else enemyScore += value;
                }

                if (!aiKing.HasValue) return 0.0;
                if (!enemyKing.HasValue) return 1.0;

                PieceColor enemy = OpponentColor(_owner._color);
                int diff = aiScore - enemyScore;

                // 子力联动：双象、双车、车占开放线、兵保护轻子。
                if (aiBishops >= 2) diff += 30;
                if (enemyBishops >= 2) diff -= 30;
                if (aiRooks >= 2) diff += 15;
                if (enemyRooks >= 2) diff -= 15;
                diff += CountOpenFiles(aiRookFiles, aiPawnFiles) * 12;
                diff -= CountOpenFiles(enemyRookFiles, enemyPawnFiles) * 12;
                diff += CountSupportedMinors(board, _owner._color) * 12;
                diff -= CountSupportedMinors(board, enemy) * 12;

                // 王安全与 Baseline 对齐：被攻击时按先后手给不同权重，再加贴身护盾。
                bool enemyToMove = sideToMove == enemy;
                bool aiKingAttacked = IsAttacked(board, aiKing.Value, enemy);
                bool enemyKingAttacked = IsAttacked(board, enemyKing.Value, _owner._color);

                diff += GetPieceSquareValue(PieceType.King, aiKing.Value, _owner._color);
                diff -= GetPieceSquareValue(PieceType.King, enemyKing.Value, enemy);

                if (aiKingAttacked) diff -= enemyToMove ? 2200 : 380;
                if (enemyKingAttacked) diff += enemyToMove ? 380 : 2200;

                diff += EvaluateKingShield(board, aiKing.Value, _owner._color);
                diff -= (int)(EvaluateKingShield(board, enemyKing.Value, enemy) * 0.6f);

                // 踩在对手“受威胁却不逃”的陷阱格上，随时可能被反吃。
                foreach (var (pos, piece) in board.GetPiecesOfColor(_owner._color))
                {
                    float trapRisk = _owner._beliefState.GetTrapRisk(pos);
                    if (trapRisk <= 0.01f) continue;
                    diff -= (int)(trapRisk * Math.Min(piece.TrueType.GetValue(), 900) * 0.45f);
                }

                double scaled = diff / 900.0;
                return 1.0 / (1.0 + Math.Exp(-scaled));
            }

            private static int CountOpenFiles(bool[] rookFiles, bool[] pawnFiles)
            {
                int count = 0;
                for (int file = 0; file < 8; file++)
                    if (rookFiles[file] && !pawnFiles[file]) count++;
                return count;
            }

            private static int CountSupportedMinors(Board board, PieceColor color)
            {
                int count = 0;
                int dir = color == PieceColor.White ? -1 : 1;
                foreach (var (pos, piece) in board.GetPiecesOfColor(color))
                {
                    if (piece.TrueType != PieceType.Knight && piece.TrueType != PieceType.Bishop) continue;
                    if (IsPawnAt(board, pos.X - 1, pos.Y + dir, color) ||
                        IsPawnAt(board, pos.X + 1, pos.Y + dir, color))
                        count++;
                }
                return count;
            }

            private static bool IsPawnAt(Board board, int x, int y, PieceColor color)
            {
                if (x < 0 || x > 7 || y < 0 || y > 7) return false;
                var piece = board.GetPiece(x, y);
                return piece != null && piece.Color == color && piece.TrueType == PieceType.Pawn;
            }

            private static int EvaluateKingShield(Board board, Position king, PieceColor color)
            {
                int shield = 0;
                for (int i = 0; i < KingOffsets.Length; i++)
                {
                    var pos = king + KingOffsets[i];
                    if (!pos.IsValid) continue;
                    var piece = board.GetPiece(pos);
                    if (piece == null || piece.Color != color) continue;
                    shield += piece.VisibleType != piece.TrueType ? 32 : 20;
                }

                if (color == PieceColor.White && king.Y <= 1) shield += 35;
                if (color == PieceColor.Black && king.Y >= 6) shield += 35;
                return shield;
            }

            private static int GetPieceSquareValue(PieceType type, Position pos, PieceColor color)
            {
                int row = color == PieceColor.White ? 7 - pos.Y : pos.Y;
                int col = pos.X;
                if (row < 0 || row > 7 || col < 0 || col > 7) return 0;

                switch (type)
                {
                    case PieceType.Pawn: return PstPawn[row, col];
                    case PieceType.Knight: return PstKnight[row, col];
                    case PieceType.Bishop: return PstBishop[row, col];
                    case PieceType.Rook: return PstRook[row, col];
                    case PieceType.Queen: return PstQueen[row, col];
                    case PieceType.King: return PstKing[row, col];
                    default: return 0;
                }
            }

            private bool IsDecoyTrapDisaster(Board board, Move move)
            {
                var target = board.GetPiece(move.To);
                if (target == null || target.Color == _owner._color) return false;
                if (target.TrueType == PieceType.King) return false;

                float trapRisk = _owner._beliefState.GetTrapRisk(move.To);
                float kingProbability = _owner._beliefState.GetProbability(move.To, PieceType.King);
                int visibleValue = target.VisibleType.GetValue();
                bool likelyDecoy = trapRisk >= 0.45f
                                   || (kingProbability < 0.25f && visibleValue >= 300);
                if (!likelyDecoy) return false;

                var attacker = board.GetPiece(move.From);
                if (attacker == null || attacker.TrueType.GetValue() < 300) return false;

                var after = board.Clone();
                after.ExecuteMove(move);
                int enemyAttackers = CountAttackers(after, move.To, OpponentColor(_owner._color));
                if (enemyAttackers < 2) return false;

                int ownDefenders = CountAttackers(after, move.To, _owner._color);
                return ownDefenders == 0;
            }

            private static int CountAttackers(Board board, Position target, PieceColor color)
            {
                int count = 0;
                foreach (var (from, piece) in board.GetPiecesOfColor(color))
                {
                    if (from == target) continue;
                    if (MoveGenerator.CanPieceAttackSquare(board, from, piece.TrueType, piece.Color, target))
                        count++;
                }
                return count;
            }

            private static Move? FindKingCapture(Board board, List<Move> moves)
            {
                for (int i = 0; i < moves.Count; i++)
                {
                    var target = board.GetPiece(moves[i].To);
                    if (target != null && target.TrueType == PieceType.King)
                        return moves[i];
                }
                return null;
            }

            private void ScanRootMoveSafety(Move move, PieceColor enemy, out int safeWorlds, out bool kingCaptureWin)
            {
                safeWorlds = 0;
                kingCaptureWin = false;

                for (int w = 0; w < _worlds.Count; w++)
                {
                    var board = _worlds[w].Board;
                    var target = board.GetPiece(move.To);
                    if (target != null && target.TrueType == PieceType.King)
                    {
                        safeWorlds++;
                        kingCaptureWin = true;
                        continue;
                    }

                    var undo = board.ExecuteMoveWithUndo(move);
                    bool exposed = HasKingCapture(board, enemy);
                    board.UndoMove(move, undo);

                    if (!exposed) safeWorlds++;
                }
            }

            // BeliefState 根节点安全网：采样世界可能恰好把“贴着王的隐藏重子”洗成小兵。
            // 这里不读采样结果，直接按真实概率询问信念：走完这步后，哪个敌子可能立刻吃王。
            private double ComputeBeliefKingRisk(Board board, Move move, PieceColor enemy)
            {
                var after = board.Clone();
                after.ExecuteMove(move);

                var ownKing = after.FindKing(_owner._color);
                if (!ownKing.HasValue) return 1.0;
                var kingPos = ownKing.Value;

                double risk = 0.0;
                foreach (var (from, _) in after.GetPiecesOfColor(enemy))
                {
                    double attackProbability = 0.0;
                    for (int t = (int)PieceType.King; t <= (int)PieceType.Pawn; t++)
                    {
                        var type = (PieceType)t;
                        if (!MoveGenerator.CanPieceAttackSquare(after, from, type, enemy, kingPos)) continue;
                        attackProbability += _owner._beliefState.GetProbability(from, type);
                    }

                    if (attackProbability <= 0.001) continue;
                    if (attackProbability > 1.0) attackProbability = 1.0;
                    risk = 1.0 - (1.0 - risk) * (1.0 - attackProbability);
                }

                return Math.Min(1.0, risk);
            }

            // 概率越接近“几乎确定”，惩罚越陡：让 MCTS 染上 Baseline 的疑心病。
            private static double BeliefSafetyFactor(double risk)
            {
                if (risk >= 0.55) return 0.02;
                if (risk <= 0.02) return 1.0;
                return Math.Max(0.05, 1.0 - 1.75 * risk);
            }

            private void BuildTopMoves()
            {
                var indices = new List<int>();
                for (int i = 0; i < _rootMoves.Count; i++)
                    if (_aggVisits[i] > 0) indices.Add(i);

                indices.Sort((a, b) =>
                {
                    int byRate = _adjustedRates[b].CompareTo(_adjustedRates[a]);
                    return byRate != 0 ? byRate : _aggVisits[b].CompareTo(_aggVisits[a]);
                });

                _topMoves = new List<RootMoveStat>(indices.Count);
                foreach (int i in indices)
                {
                    int visits = _aggVisits[i];
                    _topMoves.Add(new RootMoveStat
                    {
                        Move = _rootMoves[i],
                        Visits = visits,
                        WinRate = visits > 0 ? _aggWins[i] / visits : 0.0,
                        WorldRate = (_worldRateSum[i] + 0.5 * (_worlds.Count - _worldSeen[i])) / _worlds.Count,
                        Rank = _adjustedRates[i]
                    });
                }
            }

            // 硬性兜底：首选在足够多的虚构世界里会被立即吃王时，改用已知安全的次优根走法。
            private Move TacticalGuard(Move mctsBest, double mctsAdjustedRate, out double bestGuardRate)
            {
                bestGuardRate = mctsAdjustedRate;
                int worlds = _worlds.Count;
                if (worlds == 0 || _safeWorlds == null) return mctsBest;

                int bestIndex;
                if (!_rootIndex.TryGetValue(mctsBest, out bestIndex)) return mctsBest;
                if (_kingCaptureWins[bestIndex]) return mctsBest;

                int safe = _safeWorlds[bestIndex];
                if (safe >= worlds) return mctsBest;

                double unsafeFraction = 1.0 - (double)safe / worlds;
                if (unsafeFraction < 0.2) return mctsBest;

                int alternative = -1;
                double alternativeRate = double.NegativeInfinity;

                for (int i = 0; i < _rootMoves.Count; i++)
                {
                    if (i == bestIndex || _aggVisits[i] <= 0 || _safeWorlds[i] < worlds) continue;

                    double worldRate = (_worldRateSum[i] + 0.5 * (worlds - _worldSeen[i])) / worlds;
                    if (worldRate > alternativeRate + 1e-9 ||
                        (Math.Abs(worldRate - alternativeRate) <= 1e-9 &&
                         alternative >= 0 && _aggVisits[i] > _aggVisits[alternative]))
                    {
                        alternativeRate = worldRate;
                        alternative = i;
                    }
                }

                if (alternative >= 0 && alternativeRate >= mctsAdjustedRate - 0.05)
                {
                    bestGuardRate = alternativeRate;
                    Log($"[MCTS] 战术复核：首选 {mctsBest} 在 {unsafeFraction * 100.0:F0}% 的世界里下一手会被吃王，改用更安全的 {_rootMoves[alternative]}。");
                    return _rootMoves[alternative];
                }

                return mctsBest;
            }

            private static bool HasKingCapture(Board board, PieceColor sideToMove)
            {
                PieceColor victim = OpponentColor(sideToMove);
                foreach (var (pos, piece) in board.GetPiecesOfColor(victim))
                {
                    if (piece.TrueType != PieceType.King) continue;
                    return IsAttacked(board, pos, sideToMove);
                }
                return false;
            }

            private static void OrderMoves(Board board, List<Move> moves)
            {
                for (int i = 1; i < moves.Count; i++)
                {
                    Move current = moves[i];
                    int currentScore = MovePriority(board, current);
                    int j = i - 1;

                    while (j >= 0 && MovePriority(board, moves[j]) < currentScore)
                    {
                        moves[j + 1] = moves[j];
                        j--;
                    }

                    moves[j + 1] = current;
                }
            }

            private static int MovePriority(Board board, Move move)
            {
                int score = 0;
                var target = board.GetPiece(move.To);
                if (target != null)
                {
                    if (target.TrueType == PieceType.King) return int.MaxValue;
                    score += 1000 + target.TrueType.GetValue();
                }
                if (move.PromotionType.HasValue)
                    score += 800 + move.PromotionType.Value.GetValue();
                return score;
            }

            private void Finish()
            {
                if (_finished) return;
                _finished = true;

                for (int w = 0; w < _worlds.Count; w++)
                {
                    var children = _worlds[w].Root.Children;
                    for (int i = 0; i < children.Count; i++)
                    {
                        int index;
                        if (!_rootIndex.TryGetValue(children[i].Move, out index)) continue;
                        var child = children[i];
                        _aggVisits[index] += child.Visits;
                        _aggWins[index] += child.Wins;
                        if (child.Visits > 0)
                        {
                            _worldRateSum[index] += child.Wins / child.Visits;
                            _worldSeen[index]++;
                        }
                    }
                }

                Move? best = null;
                double bestRate = -1.0;
                double bestWorldRate = -1.0;
                int bestVisits = -1;
                int worlds = _worlds.Count;

                // 一次性的根走法安全体检：在每个虚构世界里试走一步，只看王是否会被立即反吃。
                // 再用 BeliefState 补一层不依赖采样世界的“疑心病”风险。
                _safeWorlds = new int[_rootMoves.Count];
                _kingCaptureWins = new bool[_rootMoves.Count];
                _adjustedRates = new double[_rootMoves.Count];
                _beliefRisks = new double[_rootMoves.Count];
                PieceColor enemy = OpponentColor(_owner._color);
                for (int i = 0; i < _rootMoves.Count; i++)
                {
                    ScanRootMoveSafety(_rootMoves[i], enemy, out _safeWorlds[i], out _kingCaptureWins[i]);
                    _beliefRisks[i] = ComputeBeliefKingRisk(_board, _rootMoves[i], enemy);
                }

                // PIMC 的根本偏差是“乐观虚构世界”会获得更多访问。这里按世界等权平均，
                // 未访问的世界按中性 0.5 计，避免单个世界的高胜率支配根决策。
                for (int i = 0; i < _rootMoves.Count; i++)
                {
                    if (_aggVisits[i] <= 0) continue;
                    double rate = _aggWins[i] / _aggVisits[i];
                    double worldRate = (_worldRateSum[i] + 0.5 * (worlds - _worldSeen[i])) / worlds;
                    double safety = (double)_safeWorlds[i] / worlds;
                    double adjustedRate = worldRate * (0.35 + 0.65 * safety)
                                          * BeliefSafetyFactor(_beliefRisks[i]);
                    _adjustedRates[i] = adjustedRate;

                    if (adjustedRate > bestWorldRate + 1e-9 ||
                        (Math.Abs(adjustedRate - bestWorldRate) <= 1e-9 &&
                         (_aggVisits[i] > bestVisits ||
                          (_aggVisits[i] == bestVisits && rate > bestRate + 1e-9))))
                    {
                        bestRate = rate;
                        bestWorldRate = adjustedRate;
                        bestVisits = _aggVisits[i];
                        best = _rootMoves[i];
                    }
                }

                if (!best.HasValue)
                    best = FallbackMove();

                if (best.HasValue)
                {
                    double guardRate;
                    best = TacticalGuard(best.Value, bestWorldRate, out guardRate);
                    bestWorldRate = guardRate;
                }

                BuildTopMoves();
                if (_topMoves != null && best.HasValue && !_topMoves.Exists(s => s.Move.Equals(best.Value)))
                {
                    int idx = _rootIndex[best.Value];
                    _topMoves.Insert(0, new RootMoveStat
                    {
                        Move = best.Value,
                        Visits = _aggVisits[idx],
                        WinRate = _aggVisits[idx] > 0 ? _aggWins[idx] / _aggVisits[idx] : 0.0,
                        WorldRate = worlds > 0
                            ? (_worldRateSum[idx] + 0.5 * (worlds - _worldSeen[idx])) / worlds
                            : 0.0,
                        Rank = _adjustedRates[idx]
                    });
                }

                _owner.LastIterations = _iterations;
                _owner.LastRollouts = _rollouts;
                _result = best;

                int bestIndex = best.HasValue && _rootIndex.TryGetValue(best.Value, out int idx2) ? idx2 : -1;
                double bestBeliefRisk = bestIndex >= 0 ? _beliefRisks[bestIndex] : 0.0;
                Log($"[MCTS] 决策 {best.Value} | 迭代 {_iterations}，物理模拟 {_rollouts}，虚构世界 {_worlds.Count}，胜率 {bestRate * 100.0:F1}%（安全加权 {bestWorldRate * 100.0:F1}%，{bestVisits} 次访问，信念王险 {bestBeliefRisk * 100.0:F0}%），绝杀视界 {_kingHorizonHits} 次，防欺诈惩罚 {_trapDisasters} 次，耗时 {_watch.ElapsedMilliseconds}ms。");
            }

            private Move FallbackMove()
            {
                Move best = _rootMoves[0];
                double bestScore = double.NegativeInfinity;

                for (int i = 0; i < _rootMoves.Count; i++)
                {
                    var move = _rootMoves[i];
                    var target = _board.GetPiece(move.To);
                    double score = target != null ? 10.0 + target.TrueType.GetValue() / 100.0 : 0.0;
                    if (move.PromotionType.HasValue) score += 8.0;
                    if (IsDecoyTrapDisaster(_board, move)) score -= 50.0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = move;
                    }
                }

                return best;
            }
        }
    }
}
