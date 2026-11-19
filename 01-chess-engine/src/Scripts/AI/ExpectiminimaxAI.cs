using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ChessDark.Core;
using ChessDark.Logic;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Threading.Tasks;
#endif

namespace ChessDark.AI
{
    // Baseline 对照组：原采样期望极小化极大实现（保留原有估值、置换表与欺诈启发）。
    public sealed class ExpectiminimaxAI : AIPlayer
    {
        private const int INF = 1000000;
        private const int MATE = 10000;

        // 静态搜索上限：吃子链最多再延伸 6 层，配合 delta 剪枝防止时间被无意义反吃拖死。
        private const int MaxQuiescenceDepth = 6;

        // 王的动态概率价值：几乎确定是王时鼓励绝杀，模糊区间只给温和期望。
        private const int KingCertainWeight = 50000;
        private const int KingMildWeight = 500;
        private const float KingCertainThreshold = 0.8f;
        private const float KingMildThreshold = 0.2f;

        // 多疑惩罚：无保护送子在被判定为“已经赢定”之前一律按诱饵处理。
        private const int DecisiveScore = 3000;
        private const int MaxBaitPenalty = 950;

        // 悲观王暴露：不等同于将死，但在评估里必须明显超过普通物质收益。
        // 取值过大会制造“悬崖”：所有走法都被扣成同一个极低分，迭代加深失去区分度。
        private const int KingExposurePenalty = 1800;

        // “可能本态”王暴露：采样世界只兑现一种本态，16 个世界里通常只有 1 个抽到 5% 的藏后；
        // 期望值因此会低估王线风险。这里对每一种公开信息允许的本态都按真实威胁计一笔，
        // 让搜索在叉形成前就避开王线，而不是等根节点红线在王被咬住后才反应。
        private const int MaskKingAttackerPenalty = 420;
        // 只隔一个挡子的王线：挡子随时可能被吃/被挪开。G35 的王在 d7、d5 兵是唯一挡子，
        // 兵被吃掉后 d2 的 34% 车/8% 后立刻兑现；只看“当前直接攻击”的评估看不见这一步。
        private const int MaskKingLatentPenalty = 150;
        private const int MaskKingExposureCap = 1400;
        private const int SliderStraightMask = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Rook);
        private const int SliderDiagonalMask = (1 << (int)PieceType.Queen) | (1 << (int)PieceType.Bishop);

        private readonly int _sampleCount;
        private readonly int _searchDepth;

        // 残局穷举：子力很少时关闭随机抽样，改在“信念塌缩后的最大似然世界”里做确定性深搜。
        private const int EndgameMaxPieces = 12;
        private const int EndgameMaxHeavy = 3;
        private const int EndgameDepth = 12;
        internal const int EndgameTimeBudgetMs = 3600;

        // 王安全的概率阈值：用“被将吃的概率”代替“掩码里有没有攻击型”。
        // 掩码阈值 0.04 会把低概率的滑子漏掉，而真王被吃是终局，必须按概率而非布尔值处理。
        internal const double KingSafetySafeDanger = 0.12;
        private const double KingSafetyKingMoveDanger = 0.06;
        internal const double KingSafetyUrgentDanger = 0.20;

        // 二步王威胁的折算系数：敌子先吃我方挡子、再从新位置攻击王格。
        // 中间还隔着我们的一手（可逃/可反吃），所以不是“下一步就死”，按半权计入危险度。
        // G20/G42 两盘执白败局都是该形态：伪装成兵的真车吃 f1 横扫 d1，吃 g2 沿 g 线打 g1。
        private const double KingConditionalDangerWeight = 0.5;

        // 吃王“身份锁定”门槛：信念常把王概率平摊给多个从未移动的棋子
        //（实测四个候选各 38.6%），这时 >20% 只代表“值得试探”，
        // 不代表可以无视自身王的安全去赌一把。
        internal const float KingKillCertainThreshold = 0.8f;

        // 王亲自吃掉“可能是王”的贴脸子：概率达到该值就不再按悲观掩码当诱饵排除。
        // G35：e7 有 51% 可能是王，王吃 e7 被“有保护”红线排掉，下一手真后就在同一格吃王。
        // 吃中真王直接终局，吃空也是高价值交换，风险交给采样世界与丢王否决结算。
        private const float KingCaptureGambleProb = 0.25f;

        // 残局评估开关：由搜索会话设置，叶节点据此启用升变/王中心化等残局项。
        internal bool EndgameEvalActive;

        // 子力暴露风险厌恶系数：人类知道自己的本态，掉大子的真实概率高于按信念平均的估计。
        private const double MaterialRiskWeight = 2.0;

        // 重子挂子阈值：真后零容忍（10%）、真车 25%。
        // 伪装标签让对手常以“吃便宜子”的心态扑后：v63 三场平局全是车换掉伪装成兵的真后，
        // 之后只能靠王吃回子力、以 K vs K 结构性和棋收场。真后被吃的期望损失远大于任何位置收益，
        // 只要存在不挂子的走法，就不允许把真后留在“任何可能本态”10% 以上的火力里。
        private const double QueenHangingThreshold = 0.10;
        private const double RookHangingThreshold = 0.25;
        // 真后已被攻击时，至少要降低这么多危险度才算“有效的救援手”；
        // 若有能直接逃到 10% 以下的手，则只认这些手。
        private const double QueenEvacImprovement = 0.15;

        // 重子救援阈值：真后/真车被高置信度敌子攻击时，允许在“王危险度不变差”的前提下逃子。
        // 王红线要求每一步都把王留在绝对安全格，G0/G32 因此把全部保后手排除，白送真后并导致崩盘。
        private const double HeavyRescueAttackThreshold = 0.50;
        private const double HeavyRescueSafeAttack = 0.25;
        private const double HeavyRescueRiskLimit = 0.10;

        // 尾部风险厌恶：采样平均会把“16 个世界里 1 个丢后/丢王”摊成几十~几百分，
        // 但真实对局里那一个世界就是整局。按最坏世界相对平均的缺口收保险费，
        // 宁可平均少赢一点，也不要 50 局里稳定送掉一两局。
        private const double TailRiskAllowance = 450.0;
        private const double TailRiskWeight = 0.4;

        // 临时诊断：EMM_ROOT_DEBUG=1 时在最终复核层打印根候选评分明细。
        internal static readonly bool RootDebug =
            Environment.GetEnvironmentVariable("EMM_ROOT_DEBUG") == "1";

        // 临时诊断：ENDGAME_DBG=1 时打印搜索树里每一次吃王，用于定位残局假绝望。
        internal static readonly bool KingDebug =
            Environment.GetEnvironmentVariable("ENDGAME_DBG") == "1";

        // 临时诊断：KINGSAFETY_DBG=1 时打印根节点每手的威胁计数与危险度。
        internal static readonly bool KingSafetyDebug =
            Environment.GetEnvironmentVariable("KINGSAFETY_DBG") == "1";

        // 根节点附加分：Predator 用它注入“人类视角下的隐匿/诱饵/猎王”倾向。
        // 只参与根节点选子，不进入搜索树，保证搜索本身仍是公平的期望极小极大。
        internal Func<Board, Move, double> RootMoveBonus;

        // 重子救援时的“玩家视角吃王风险”查询：Predator 用镜像信念注入。
        // 为空时不启用救援豁免，保持其他引擎原有的严格王红线。
        internal Func<Move, double> KingRescueRisk;

        // 王线延伸开关：默认开启；无头对照用它关掉基线一侧的延伸，隔离该改动的贡献。
        public bool KingLineExtensionEnabled { get; set; } = true;

        // 信念确定时把采样世界减半（换取更深一层）；无头对照可关。
        public bool UseConfidentSampling { get; set; } = true;

        // 分级 Late Move Reduction；攻击敌王格的走法永远不减。
        // 50 局单侧 A/B（Predator 对 EMM）：开 LMR 76%，关 LMR 92%，默认关闭。
        public bool UseLateMoveReduction { get; set; } = false;

        // 王威胁对抗世界：在随机采样世界之外，额外构造一个“敌人可能本态最危险”的世界，
        // 用来覆盖 16 个世界抽不到的联合杀棋配置（只用公开信念，不读敌子真型）。
        public bool UseKingThreatWorld { get; set; } = true;

        public bool LastSearchWasEndgame { get; private set; }
        public int LastTargetDepth { get; private set; } = 1;
        public int LastEffectiveSamples { get; private set; }
        public double LastNodesPerSecond { get; private set; }
        public double LastSearchMs { get; private set; }

        // 杀手走法 + 历史启发：每次搜索开始时清空。只影响排序，并发写入的最坏情况是排序略偏，不影响正确性。
        private const int MaxPly = 64;
        private readonly Move[,] _killers = new Move[MaxPly, 2];
        private readonly bool[,] _hasKiller = new bool[MaxPly, 2];
        private readonly int[] _history = new int[2 * 64 * 64];
        private long _nodeCount;
        private long _qNodeCount;
        private long _qDepthSum;

        private static readonly ulong[,,] ZobristPiece = new ulong[2, 7, 64];
        private static readonly ulong[] ZobristMoved = new ulong[64];
        private static readonly ulong ZobristSide;

        static ExpectiminimaxAI()
        {
            var rng = new Random(20261102);
            for (int color = 0; color < 2; color++)
                for (int type = 1; type < 7; type++)
                    for (int square = 0; square < 64; square++)
                        ZobristPiece[color, type, square] = NextUlong(rng);

            for (int square = 0; square < 64; square++)
                ZobristMoved[square] = NextUlong(rng);

            ZobristSide = NextUlong(rng);
        }

        private static ulong NextUlong(Random rng)
        {
            ulong high = (ulong)(uint)rng.Next() << 32;
            ulong low = (ulong)(uint)rng.Next();
            return high ^ low;
        }

        private static readonly int[,] PstPawn = {
            { 0,  0,  0,  0,  0,  0,  0,  0 },
            { 50, 50, 50, 50, 50, 50, 50, 50 },
            { 10, 10, 20, 30, 30, 20, 10, 10 },
            { 5,  5, 10, 25, 25, 10,  5,  5 },
            { 0,  0,  0, 20, 20,  0,  0,  0 },
            { 5, -5,-10,  0,  0,-10, -5,  5 },
            { 5, 10, 10,-20,-20, 10, 10,  5 },
            { 0,  0,  0,  0,  0,  0,  0,  0 }
        };

        private static readonly int[,] PstKnight = {
            {-50,-40,-30,-30,-30,-30,-40,-50 },
            {-40,-20,  0,  0,  0,  0,-20,-40 },
            {-30,  0, 10, 15, 15, 10,  0,-30 },
            {-30,  5, 15, 20, 20, 15,  5,-30 },
            {-30,  0, 15, 20, 20, 15,  0,-30 },
            {-30,  5, 10, 15, 15, 10,  5,-30 },
            {-40,-20,  0,  5,  5,  0,-20,-40 },
            {-50,-40,-30,-30,-30,-30,-40,-50 }
        };

        private static readonly int[,] PstBishop = {
            {-20,-10,-10,-10,-10,-10,-10,-20 },
            {-10,  0,  0,  0,  0,  0,  0,-10 },
            {-10,  0, 10, 10, 10, 10,  0,-10 },
            {-10,  5,  5, 10, 10,  5,  5,-10 },
            {-10,  0,  5, 10, 10,  5,  0,-10 },
            {-10, 10,  5, 10, 10,  5, 10,-10 },
            {-10,  5,  0,  0,  0,  0,  5,-10 },
            {-20,-10,-10,-10,-10,-10,-10,-20 }
        };

        private static readonly int[,] PstRook = {
            { 0,  0,  0,  0,  0,  0,  0,  0 },
            { 5, 10, 10, 10, 10, 10, 10,  5 },
            {-5,  0,  0,  0,  0,  0,  0, -5 },
            {-5,  0,  0,  0,  0,  0,  0, -5 },
            {-5,  0,  0,  0,  0,  0,  0, -5 },
            {-5,  0,  0,  0,  0,  0,  0, -5 },
            {-5,  0,  0,  0,  0,  0,  0, -5 },
            { 0,  0,  0,  5,  5,  0,  0,  0 }
        };

        private static readonly int[,] PstQueen = {
            {-20,-10,-10, -5, -5,-10,-10,-20 },
            {-10,  0,  0,  0,  0,  0,  0,-10 },
            {-10,  0,  5,  5,  5,  5,  0,-10 },
            { -5,  0,  5,  5,  5,  5,  0, -5 },
            {  0,  0,  5,  5,  5,  5,  0, -5 },
            {-10,  5,  5,  5,  5,  5,  0,-10 },
            {-10,  0,  5,  0,  0,  0,  0,-10 },
            {-20,-10,-10, -5, -5,-10,-10,-20 }
        };

        private static readonly int[,] PstKing = {
            {-30,-40,-40,-50,-50,-40,-40,-30 },
            {-30,-40,-40,-50,-50,-40,-40,-30 },
            {-30,-40,-40,-50,-50,-40,-40,-30 },
            {-30,-40,-40,-50,-50,-40,-40,-30 },
            {-20,-30,-30,-40,-40,-30,-30,-20 },
            {-10,-20,-20,-20,-20,-20,-20,-10 },
            { 20, 20,  0,  0,  0,  0, 20, 20 },
            { 20, 30, 10,  0,  0, 10, 30, 20 }
        };

        private static readonly Position[] KnightOffsets = {
            new Position(-2,-1), new Position(-2,1), new Position(-1,-2), new Position(-1,2),
            new Position(1,-2), new Position(1,2), new Position(2,-1), new Position(2,1)
        };

        private static readonly Position[] KingOffsets = {
            new Position(-1,-1), new Position(-1,0), new Position(-1,1),
            new Position(0,-1),                    new Position(0,1),
            new Position(1,-1),  new Position(1,0), new Position(1,1)
        };

        private static readonly Position[] BishopDirs = {
            new Position(-1,-1), new Position(-1,1), new Position(1,-1), new Position(1,1)
        };

        private static readonly Position[] RookDirs = {
            new Position(-1,0), new Position(1,0), new Position(0,-1), new Position(0,1)
        };

        private static readonly Position[] AllDirs = {
            new Position(-1,-1), new Position(-1,0), new Position(-1,1), new Position(0,-1),
            new Position(0,1), new Position(1,-1), new Position(1,0), new Position(1,1)
        };

        private struct TTEntry
        {
            public int Depth;
            public int Value;
            public byte Flag; // 0 exact, 1 lower bound, 2 upper bound
            public Move BestMove;
            public bool HasBestMove;
        }

        private class MoveState
        {
            public Move Move;
            public double Selection;
            public double Average;
            public int Count;
        }

        public ExpectiminimaxAI(PieceColor color, int seed = 42, int sampleCount = 32, int searchDepth = 3,
            int timeLimitMs = 3000) : base(color, seed, timeLimitMs)
        {
            _sampleCount = Math.Max(1, sampleCount);
            _searchDepth = Math.Max(1, searchDepth);
        }

        public override AISearchSession CreateSearchSession(Board board)
        {
            _beliefState.EnsureInitialized(board);

            // 绝杀快速通道：最可能的王位存在一步合法吃子时直接出手，不再做任何期望搜索。
            var instantKill = FindImmediateKingCapture(board);
            if (instantKill.HasValue)
            {
                LastSearchWasEndgame = false;
                LastSearchDepth = 1;
                LastQuiescenceDepth = 0;
                LastSearchNodes = 0;
                LastSearchMs = 0;
                LastNodesPerSecond = 0;
                LastEffectiveSamples = 0;
                LastTargetDepth = 1;
                Log($"[AI] 绝杀快速通道：最可能王位 {instantKill.Value.To} 可直接吃，立即走 {instantKill.Value}（0ms，跳过期望搜索）。");
                return new AIPlayer.ImmediateMoveSession(instantKill.Value);
            }

            return new SearchSession(this, board);
        }

        // Hybrid 复核专用：强制只评估 forcedMove，静默日志，并共享同一批抽样世界。
        public AISearchSession CreateReviewSession(Board board, Move forcedMove, int samplingSeed)
        {
            _beliefState.EnsureInitialized(board);
            return new SearchSession(this, board, forcedMove, true, samplingSeed);
        }

        public sealed class SearchSession : AISearchSession
        {
            private readonly ExpectiminimaxAI _owner;
            private readonly Board _board;
            private readonly List<Move> _rootMoves;
            private readonly List<Board> _samples = new List<Board>();
            private readonly List<Move> _ordered = new List<Move>();
            private readonly Dictionary<Move, double> _sums = new Dictionary<Move, double>();
            private readonly Dictionary<Move, int> _counts = new Dictionary<Move, int>();
            // 尾部风险：每个根走法在本层所有采样世界里的最差分。概率平均会掩盖“少数世界崩盘”，
            // 选子时按最坏缺口额外收保险费。
            private readonly Dictionary<Move, int> _minScores = new Dictionary<Move, int>();
            private readonly Dictionary<Move, int> _kingLossCounts = new Dictionary<Move, int>();
            // 对抗世界（最危险的可信本态配置）的根分，仅用于诊断输出。
            private readonly Dictionary<Move, int> _threatScores = new Dictionary<Move, int>();
            // 在最坏可信配置下会丢王的根手。只要还有一手不丢王，就先全部排除——
            // 这就是 G35 的教训：e7 有 51% 可能是王，王吃 e7 被“有保护”悲观红线排掉，
            // 结果王贴着那颗子被真后吃。丢王是终局，必须用可信世界做硬否决。
            private readonly HashSet<Move> _threatKingLoss = new HashSet<Move>();
            private int _threatWorldIndex = -1;
            private int _threatVetoLoggedDepth = -1;
            private readonly Dictionary<Move, int> _kingDanger = new Dictionary<Move, int>();
            private readonly Dictionary<Move, double> _kingDangerScore = new Dictionary<Move, double>();
            private readonly HashSet<Move> _kingRedLine = new HashSet<Move>();
            private readonly HashSet<Position> _kingAttackers = new HashSet<Position>();
            private readonly HashSet<Move> _threatResolvers = new HashSet<Move>();
            private readonly HashSet<Move> _kingBaitCaptures = new HashSet<Move>();
            private readonly Dictionary<Move, double> _materialRisk = new Dictionary<Move, double>();
            private readonly Dictionary<Move, double> _materialExposure = new Dictionary<Move, double>();
            private readonly HashSet<Move> _hangingHeavy = new HashSet<Move>();
            private double _kingDangerNow;
            private readonly Dictionary<Move, double> _previousAverages = new Dictionary<Move, double>();
            private readonly Dictionary<Move, int> _rootIndex = new Dictionary<Move, int>();
            private readonly Dictionary<ulong, TTEntry>[] _sampleTts;
            private readonly List<Move>[][] _sampleBuffers;
            private readonly List<Move>[] _buffers;
            private readonly Stopwatch _stopwatch = new Stopwatch();

            private Board _merged;
            private Dictionary<ulong, TTEntry> _tt;
            private bool _endgameMode;
            private bool _endgameParallel;
            private int _targetDepth;
            private int _endgameBufferCount;
            // 临时诊断：零评分兜底时统计每一步被哪道根过滤挡掉。
            private int _skipMissing, _skipRed, _skipHanging, _skipAborted;

            // 残局按根走法并行：每个参与线程必须独占一套置换表与 move 缓冲区。
            // 之前用 i % 槽位数把并发迭代映射到固定槽，迭代数多于槽位时两个线程会共写同一组
            // List<Move>，触发 "Collection was modified; enumeration operation may not execute"。
            // 现改为 Parallel.For 的 localInit/localFinally：由线程本地状态保证独占，跑完归还池子复用。
#if !UNITY_WEBGL || UNITY_EDITOR
            private readonly ConcurrentBag<EndgameWorkerState> _endgamePool =
                new ConcurrentBag<EndgameWorkerState>();
#endif
            private readonly Dictionary<Move, double> _deepCaptureScores = new Dictionary<Move, double>();
            private readonly Dictionary<Move, int> _replyCounts = new Dictionary<Move, int>();
            private bool _deepVerified;
            private double _deepNonCaptureScore;
            private bool _hasDeepNonCapture;
            private int _depth = 1;
            private int _sampleIndex;
            private int _moveIndex;
            private int _processedSamples;
            private int _completedDepths;
            private MoveState _bestCompleted;
            private bool _finished;
            private Move? _result;
            private readonly bool _quiet;

            internal SearchSession(ExpectiminimaxAI owner, Board board)
                : this(owner, board, null, false, null)
            {
            }

            // forcedMove 用于 Hybrid 的 Top-K 复核：只评估指定根走法，不参与根选择。
            // samplingSeed 让多个候选共享同一批虚构世界，保证复核分数可比。
            internal SearchSession(ExpectiminimaxAI owner, Board board, Move? forcedMove,
                bool quiet, int? samplingSeed)
            {
                _owner = owner;
                _board = board;
                _quiet = quiet;

                if (forcedMove.HasValue)
                {
                    var legal = MoveGenerator.GeneratePseudoLegalMoves(board, owner._color);
                    _rootMoves = legal.Contains(forcedMove.Value)
                        ? new List<Move> { forcedMove.Value }
                        : new List<Move>();
                }
                else
                {
                    _rootMoves = MoveGenerator.GeneratePseudoLegalMoves(board, owner._color);
                }

                // 规则级自杀：三次重复由走出者判负。搜索看不到历史，这里按宿主提供的查询直接剔除；
                // 只有在所有走法都会重复时才保留（此时已经无路可走，交给正常选择）。
                if (owner.WouldCauseThreefold != null && _rootMoves.Count > 1)
                {
                    var safeMoves = new List<Move>(_rootMoves.Count);
                    foreach (var move in _rootMoves)
                        if (!owner.WouldCauseThreefold(move)) safeMoves.Add(move);

                    if (safeMoves.Count > 0)
                    {
                        if (!_quiet && safeMoves.Count < _rootMoves.Count)
                            Log($"[AI] 三次重复红线：{_rootMoves.Count - safeMoves.Count} 手会让走出者判负，已剔除，保留 {safeMoves.Count} 手。");
                        _rootMoves = safeMoves;
                    }
                }

                for (int i = 0; i < _rootMoves.Count; i++)
                    _rootIndex[_rootMoves[i]] = i;

                if (_rootMoves.Count == 0)
                {
                    if (!_quiet) Log("[AI] 无任何可行走法，跳过本回合（不判负）。");
                    _finished = true;
                    return;
                }

                if (!forcedMove.HasValue && _rootMoves.Count == 1)
                {
                    if (!_quiet) owner.LogMove(_rootMoves[0], board, 1, 1, owner._searchDepth);
                    _result = _rootMoves[0];
                    _finished = true;
                    return;
                }

                _endgameMode = !forcedMove.HasValue && owner.IsEndgamePosition(board);
                _targetDepth = _endgameMode
                    ? (AIPlayer.MobileProfile ? AIPlayer.MobileSearchDepth : EndgameDepth)
                    : owner._searchDepth;

                // 每局的敌子采样只生成一次，迭代加深与并行评估复用同一批局面，保证跨深度可比。
                // 残局只有“信念足够确定”时才用最大似然单世界做确定性穷举；否则单世界会把猜出来的
                // 假王当真王，搜出根本不存在的绝杀/被绝杀线路（实战：全盘 -9995 后开始原地挪王）。
                if (_endgameMode && owner._beliefState.IsConfident())
                {
                    _samples.Add(owner._beliefState.SampleMostLikelyBoard());
                }
                else
                {
                    // 信念已高度确定（几何推断把本态基本锁死）时，16 个世界
                    // 大多在重复同一批可能本态，平均几乎是白算；减半后单世界时间翻倍，实测多推一层。
                    int worldCount = owner._sampleCount;
                    if (owner.UseConfidentSampling && owner._beliefState.IsConfident(0.8f))
                        worldCount = Math.Max(6, owner._sampleCount / 2);

                    // 威胁世界放第一位：时间预算通常只够搜完前几个世界，最坏配置必须一定被看到。
                    if (owner.UseKingThreatWorld)
                    {
                        var ownKing = _board.FindKing(owner._color);
                        if (ownKing.HasValue)
                        {
                            _samples.Add(owner._beliefState.SampleKingThreatBoard(_board, ownKing.Value));
                            _threatWorldIndex = _samples.Count - 1;
                        }
                    }

                    var samplingRng = samplingSeed.HasValue ? new Random(samplingSeed.Value) : owner._rng;
                    for (int i = 0; i < worldCount; i++)
                        _samples.Add(owner._beliefState.SampleBoard(samplingRng, i, worldCount));
                }

                owner.LastSearchWasEndgame = _endgameMode;
                owner.EndgameEvalActive = _endgameMode;
                owner.LastEffectiveSamples = _samples.Count;
                owner.LastTargetDepth = _targetDepth;

                ComputeKingSafety();
                ComputeMaterialRisk();

                _sampleTts = new Dictionary<ulong, TTEntry>[_samples.Count];
                for (int i = 0; i < _sampleTts.Length; i++)
                    _sampleTts[i] = new Dictionary<ulong, TTEntry>(1024);

                // 每个虚构世界一套复用缓冲，避免每次迭代为 64 个采样重建几百个 List。
                int bufferCount = _targetDepth + MaxQuiescenceDepth + 4;
                _sampleBuffers = new List<Move>[_samples.Count][];
                for (int s = 0; s < _sampleBuffers.Length; s++)
                {
                    var buffers = new List<Move>[bufferCount];
                    for (int i = 0; i < buffers.Length; i++)
                        buffers[i] = new List<Move>(64);
                    _sampleBuffers[s] = buffers;
                }

                _buffers = new List<Move>[bufferCount];
                for (int i = 0; i < _buffers.Length; i++)
                    _buffers[i] = new List<Move>(64);

                // 单世界穷举时按根走法并行：每个线程独占棋盘/置换表/缓冲，避免共享棋盘被并发改写。
                _endgameBufferCount = bufferCount;
                _endgameParallel = _endgameMode && _samples.Count == 1 && AIPlayer.EnableParallelSearch;

                _stopwatch.Start();
                owner.ResetSearchStats();
                owner.LastSearchDepth = 1;
                owner.LastQuiescenceDepth = 0;
                owner.LastSearchNodes = 0;
            }

            public override bool IsFinished => _finished;

            public override Move? Result => _result;

            public int ProcessedSamples => _processedSamples;

            public int CurrentDepth => _depth;

            public int CompletedDepths => _completedDepths;

            public override int CompletedDepth => _completedDepths;

            // 复核会话的最终期望分：所有采样世界、最终深度下的平均 minimax 评分。
            public double? FinalScore => _bestCompleted?.Average;

            // 与 Baseline 根选择完全一致的“杀气”分：平均分 + 杀王/陷阱/欺诈/紧迫/施压修正。
            public double? FinalSelection => _bestCompleted?.Selection;

            // 只占用 sliceMs 左右的主线程时间，返回 true 表示搜索已结束。
            public override bool Advance(int sliceMs)
            {
                if (_finished) return true;

#if UNITY_WEBGL && !UNITY_EDITOR
                AdvanceSequential(sliceMs);
#else
                if (AIPlayer.EnableParallelSearch)
                    AdvanceParallel(sliceMs);
                else
                    AdvanceSequential(sliceMs);
#endif

                return _finished;
            }

            private bool TimedOut()
            {
                return _owner.SearchTimeBudgetMs > 0 && _stopwatch.ElapsedMilliseconds > _owner.SearchTimeBudgetMs;
            }

            private void AdvanceSequential(int sliceMs)
            {
                long sliceStart = _stopwatch.ElapsedMilliseconds;

                while (!_finished)
                {
                    if (TimedOut())
                    {
                        Finish();
                        break;
                    }

                    if (_sampleIndex >= _samples.Count)
                    {
                        CompleteDepth();
                        continue;
                    }

                    if (_moveIndex == 0)
                        BeginSample();

                    if (_moveIndex >= _ordered.Count)
                    {
                        _sampleIndex++;
                        _moveIndex = 0;
                        _processedSamples++;
                        continue;
                    }

                    EvaluateRootMove(_ordered[_moveIndex], _depth - 1);
                    _moveIndex++;

                    if (_stopwatch.ElapsedMilliseconds - sliceStart >= sliceMs)
                        break;
                }
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            private void AdvanceParallel(int sliceMs)
            {
                if (_endgameParallel)
                {
                    AdvanceEndgameParallel(sliceMs);
                    return;
                }

                long sliceStart = _stopwatch.ElapsedMilliseconds;
                int batch = Math.Max(2, AIPlayer.ParallelThreads);
                var options = new ParallelOptions { MaxDegreeOfParallelism = batch };

                while (!_finished)
                {
                    if (TimedOut())
                    {
                        Finish();
                        break;
                    }

                    if (_sampleIndex >= _samples.Count)
                    {
                        CompleteDepth();
                        continue;
                    }

                    int start = _sampleIndex;
                    int chunk = Math.Min(batch, _samples.Count - start);
                    var scoreRows = new int[chunk][];
                    var kingLossRows = new bool[chunk][];

                    Parallel.For(0, chunk, options, i =>
                    {
                        EvaluateSampleParallel(start + i, _depth - 1, _previousAverages,
                            out scoreRows[i], out kingLossRows[i]);
                    });

                    for (int i = 0; i < chunk; i++)
                    {
                        var scores = scoreRows[i];
                        if (scores == null) continue;
                        var kingLoss = kingLossRows[i];

                        bool threatWorld = start + i == _threatWorldIndex;
                        for (int j = 0; j < scores.Length; j++)
                        {
                            if (scores[j] == int.MinValue) continue;
                            var move = _rootMoves[j];
                            AddScore(move, scores[j], threatWorld);
                            if (!threatWorld && kingLoss[j]) AddKingLoss(move);
                            else if (threatWorld && kingLoss[j]) _threatKingLoss.Add(move);
                        }
                    }

                    _sampleIndex += chunk;
                    _processedSamples += chunk;

                    if (_stopwatch.ElapsedMilliseconds - sliceStart >= sliceMs)
                        break;
                }
            }

            // 残局单世界：按根走法切分到多线程，每个线程独立棋盘 + 置换表 + 缓冲。
            // 同一深度内多线程互不共享可写局面，只共享只读的根走法/红线集合。
            private void AdvanceEndgameParallel(int sliceMs)
            {
                long sliceStart = _stopwatch.ElapsedMilliseconds;
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, AIPlayer.ParallelThreads) };

                while (!_finished)
                {
                    if (TimedOut())
                    {
                        Finish();
                        break;
                    }

                    if (_sampleIndex >= _samples.Count)
                    {
                        CompleteDepth();
                        continue;
                    }

                    if (_moveIndex == 0)
                        BeginSample();

                    var moves = _ordered.ToArray();
                    var scores = new int[moves.Length];
                    var kingLosses = new bool[moves.Length];
                    var baseBoard = _merged;

                    Parallel.For(
                        0, moves.Length, options,
                        RentEndgameWorker,
                        (i, _, worker) =>
                        {
                            scores[i] = EvaluateRootMoveOnClone(baseBoard, moves[i], _depth - 1,
                                worker.Tt, worker.Buffers, out bool loss);
                            kingLosses[i] = loss;
                            return worker;
                        },
                        worker => _endgamePool.Add(worker));

                    for (int i = 0; i < moves.Length; i++)
                    {
                        if (scores[i] == int.MinValue) continue;
                        AddScore(moves[i], scores[i]);
                        if (kingLosses[i]) AddKingLoss(moves[i]);
                    }

                    _sampleIndex = _samples.Count;
                    _moveIndex = 0;
                    _processedSamples = _samples.Count;

                    if (_stopwatch.ElapsedMilliseconds - sliceStart >= sliceMs)
                        break;
                }
            }

            private EndgameWorkerState RentEndgameWorker()
            {
                EndgameWorkerState state;
                if (_endgamePool.TryTake(out state)) return state;
                return new EndgameWorkerState(_endgameBufferCount);
            }

            // Parallel.For 线程本地状态：置换表与 move 缓冲都归单个线程独占，跨深度复用。
            private sealed class EndgameWorkerState
            {
                internal readonly Dictionary<ulong, TTEntry> Tt =
                    new Dictionary<ulong, TTEntry>(4096);
                internal readonly List<Move>[] Buffers;

                internal EndgameWorkerState(int bufferCount)
                {
                    Buffers = new List<Move>[bufferCount];
                    for (int i = 0; i < bufferCount; i++)
                        Buffers[i] = new List<Move>(64);
                }
            }

            private int EvaluateRootMoveOnClone(Board baseBoard, Move move, int depth,
                Dictionary<ulong, TTEntry> tt, List<Move>[] buffers, out bool kingLoss)
            {
                kingLoss = false;
                var board = baseBoard.Clone();
                if (board.GetPiece(move.From) == null) return int.MinValue;
                if (_kingRedLine.Contains(move))
                {
                    kingLoss = true;
                    return int.MinValue;
                }

                var undo = board.ExecuteMoveWithUndo(move);
                int score;
                bool capturedSampledKing = undo.Captured != null && undo.Captured.TrueType == PieceType.King;
                if (capturedSampledKing)
                {
                    score = _owner.SampleKingCaptureScore(move.To);
                }
                else
                {
                    if (_owner.IsOwnKingCapturable(board)) kingLoss = true;
                    score = -_owner.Negamax(board, depth, -INF, INF,
                        OpponentColor(_owner._color), _stopwatch, tt, 1, buffers);
                }
                board.UndoMove(move, undo);
                if (_owner.SearchAborted) return int.MinValue;
                return score;
            }

            private void EvaluateSampleParallel(int sampleIndex, int depth,
                Dictionary<Move, double> previousAverages, out int[] scores, out bool[] kingLosses)
            {
                var merged = _owner.MergeBoard(_board, _samples[sampleIndex]);
                var tt = _sampleTts[sampleIndex];
                var buffers = _sampleBuffers[sampleIndex];

                var ordered = new List<Move>(_rootMoves);
                _owner.OrderRootMoves(merged, ordered, previousAverages);

                scores = new int[_rootMoves.Count];
                kingLosses = new bool[_rootMoves.Count];
                for (int i = 0; i < scores.Length; i++) scores[i] = int.MinValue;

                foreach (var move in ordered)
                {
                    int index;
                    if (!_rootIndex.TryGetValue(move, out index)) continue;
                    if (merged.GetPiece(move.From) == null) continue;
                    if (_kingRedLine.Contains(move))
                    {
                        kingLosses[index] = true;
                        continue;
                    }

                    var undo = merged.ExecuteMoveWithUndo(move);
                    int score;
                    bool capturedSampledKing = undo.Captured != null && undo.Captured.TrueType == PieceType.King;
                    if (capturedSampledKing)
                    {
                        score = _owner.SampleKingCaptureScore(move.To);
                    }
                    else
                    {
                        if (_owner.IsOwnKingCapturable(merged)) kingLosses[index] = true;
                        score = -_owner.Negamax(merged, depth, -INF, INF,
                            OpponentColor(_owner._color), _stopwatch, tt, 1, buffers);
                    }
                    merged.UndoMove(move, undo);
                    if (_owner.SearchAborted) score = int.MinValue;
                    scores[index] = score;
                }
            }
#endif

            private void BeginSample()
            {
                _merged = _owner.MergeBoard(_board, _samples[_sampleIndex]);
                _tt = _sampleTts[_sampleIndex];

                _ordered.Clear();
                _ordered.AddRange(_rootMoves);
                _owner.OrderRootMoves(_merged, _ordered, _previousAverages);
            }

            // 每个根走法走完后，我方大子暴露在“敌方可能本态攻击”下的期望损失。
            // 只在根节点算一次，不进入搜索树，避免拖慢叶评估。
            private void ComputeMaterialRisk()
            {
                _materialRisk.Clear();
                _materialExposure.Clear();
                _hangingHeavy.Clear();
                bool anyClean = false;
                foreach (var move in _rootMoves)
                {
                    double risk = _owner.EstimateOwnMaterialExposure(_board, move);
                    _materialExposure[move] = risk;
                    if (risk > 0.5) _materialRisk[move] = risk;
                    bool hanging = _owner.HasHangingHeavyPiece(_board, move);
                    if (hanging)
                        _hangingHeavy.Add(move);
                    else
                        anyClean = true;
                }
                // 只有存在“不挂大子”的走法时才做硬过滤；全盘都挂子时按期望损失分层。
                if (!anyClean)
                {
                    // v104：旧逻辑在这里直接清空过滤，等于“全盘都挂子就随便走”。
                    // G7 第 2 手就是被这一条放过的：真后被判 47%（多枚掩码滑子概率叠加的虚警），
                    // 强制救后把 31 手砍到 4 手，4 手全部留下 90% 会被吃的真车；因为没有一手
                    // “完全不挂”，过滤被整体清空，搜索为救后把车送了。
                    // 现在改为按期望物质损失分层：只保留损失最小的一层（15% + 50 分容差，
                    // 让“兑掉最便宜攻击子”的近似等价手不被一刀切掉）。
                    double minExposure = double.MaxValue;
                    foreach (var move in _rootMoves)
                    {
                        double exposure;
                        if (!_materialExposure.TryGetValue(move, out exposure)) continue;
                        if (exposure < minExposure) minExposure = exposure;
                    }
                    if (minExposure < double.MaxValue)
                    {
                        double tierLimit = minExposure * 1.15 + 50.0;
                        // v109 冒烟（该变体已回退）：这里不能改成“重建集合”。实测把分层变成重建
                        // 会让开局全掩码下的幻影暴露（最低 246 分）永久砍掉 3/4 的根手，G3/G4 从胜变 240 步和棋。
                        // 分层只负责把明显更差的挂子手排除，真正封死全部根手时由末尾的校验放开。
                        int kept = 0;
                        foreach (var move in _rootMoves)
                        {
                            double exposure;
                            if (!_materialExposure.TryGetValue(move, out exposure)) continue;
                            if (exposure > tierLimit) _hangingHeavy.Add(move);
                            else kept++;
                        }
                        if (!_quiet && kept > 0 && kept < _rootMoves.Count)
                            Log($"[AI] 全盘挂子：按期望物质损失分层，保留 {kept}/{_rootMoves.Count} 手（最低暴露 {minExposure:F0}）。");
                    }
                }
                else
                {
                    // 王安全红线与挂子过滤可能联手封死全部根走法（G32：42 手里 14 手红线、
                    // 其余 28 手全挂子），搜索 0 节点退化成静态兜底，反而丢掉皇后。
                    // 丢王立即终局、丢大子只是劣势：冲突时优先保王，放开挂子让搜索自己权衡。
                    bool anySearchable = false;
                    for (int i = 0; i < _rootMoves.Count; i++)
                    {
                        var m = _rootMoves[i];
                        if (!_kingRedLine.Contains(m) && !_hangingHeavy.Contains(m))
                        {
                            anySearchable = true;
                            break;
                        }
                    }
                    if (!anySearchable)
                    {
                        _hangingHeavy.Clear();
                        if (!_quiet)
                            Log("[AI] 王红线与挂子过滤封死了全部根走法：优先保王，放开挂子过滤交给期望搜索。");
                    }
                }
                // 真后已在火力下时强制救援（v65）。旧的“走完后完全不挂”硬过滤在多条可能射线叠加时
                // 会把所有走法都判挂、随后整体清空（G6：真后已被敌马 100% 咬住，引擎仍挪王送后）。
                // 这里改为比较“走前/走后”的真后危险度：只要存在能逃到安全线或显著降险的走法，
                // 就只保留这些救援手（王红线内的救援手不计数，保证搜索还有根）。
                double queenNow = CurrentQueenAttackChance(_board);
                if (queenNow >= QueenHangingThreshold)
                {
                    var ideal = new List<Move>();
                    var better = new List<Move>();
                    foreach (var move in _rootMoves)
                    {
                        if (_board.GetPiece(move.From) == null) continue;
                        double after = QueenAttackChanceAfter(_board, move);
                        if (after < QueenHangingThreshold) ideal.Add(move);
                        else if (after < queenNow - QueenEvacImprovement) better.Add(move);
                    }

                    var rescue = ideal.Count > 0 ? ideal : better;

                    // v104：救后不能白送真车。G7 第 2 手，真后被判 47% 危险（多枚掩码滑子的
                    // 概率叠加，实际上是虚警），强制救援把 31 手砍到 4 手，没有一手去管 f7 上
                    // ~90% 确定会被吃的伪装“兵”真象；下一手象吃车、王吃回、敌真车沿 f 线吃王。
                    // 规则：救后手必须同时不留下“被高置信攻击”的真车；若全部救后手都要丢车，
                    // 说明这波救后本质是拿车换后，取消强制救援，交给期望损失分层和搜索比较。
                    if (rescue.Count > 0)
                    {
                        var noRookLoss = new List<Move>();
                        foreach (var m in rescue)
                            if (!_owner.CreatesRookLoss(_board, m)) noRookLoss.Add(m);

                        if (noRookLoss.Count > 0)
                        {
                            if (noRookLoss.Count < rescue.Count && !_quiet)
                                Log($"[AI] 救后仲裁：剔除 {rescue.Count - noRookLoss.Count} 手会白丢真车的救后手，保留 {noRookLoss.Count} 手。");
                            rescue = noRookLoss;
                        }
                        else
                        {
                            if (!_quiet)
                                Log("[AI] 救后仲裁：全部救后手都会留下被高置信攻击的真车，取消强制救后，交给期望损失分层与搜索比较。");
                            rescue = new List<Move>();
                        }
                    }

                    bool anyRescueSearchable = false;
                    foreach (var m in rescue)
                    {
                        if (!_kingRedLine.Contains(m)) { anyRescueSearchable = true; break; }
                    }

                    if (rescue.Count > 0 && rescue.Count < _rootMoves.Count && anyRescueSearchable)
                    {
                        var rescueSet = new HashSet<Move>(rescue);
                        foreach (var move in _rootMoves)
                        {
                            if (!rescueSet.Contains(move))
                                _hangingHeavy.Add(move);
                        }
                        if (!_quiet)
                            Log($"[AI] 真后已在火力下（危险度 {queenNow:P0}）：强制救援，保留 {rescue.Count}/{_rootMoves.Count} 手。");
                    }
                }
            }

            // 真后危险度（0~1）：所有敌子“可能本态”攻击到己方任一真后的概率和。
            private double CurrentQueenAttackChance(Board board)
            {
                if (board == null) return 0.0;
                return MaxQueenAttackChanceOn(_owner.BuildFairCheckBoard(board));
            }

            private double QueenAttackChanceAfter(Board board, Move move)
            {
                var fair = _owner.BuildFairCheckBoard(board);
                if (fair.GetPiece(move.From) == null) return 0.0;

                var undo = fair.ExecuteMoveWithUndo(move);
                double chance = MaxQueenAttackChanceOn(fair);
                fair.UndoMove(move, undo);
                return chance;
            }

            private double MaxQueenAttackChanceOn(Board fair)
            {
                PieceColor enemy = OpponentColor(_owner._color);
                double max = 0.0;

                foreach (var (pos, piece) in fair.GetPiecesOfColor(_owner._color))
                {
                    if (piece.TrueType != PieceType.Queen) continue;

                    double chance = 0.0;
                    foreach (var (epos, epiece) in fair.GetPiecesOfColor(enemy))
                    {
                        int mask = epiece.BeliefTypeMask;
                        if (mask == 0) continue;

                        for (int t = 2; t < 7; t++)
                        {
                            if ((mask & (1 << t)) == 0) continue;
                            if (!MoveGenerator.CanPieceAttackSquare(fair, epos, (PieceType)t, enemy, pos)) continue;

                            double p = _owner.TypeProbability(epos, epiece, (PieceType)t);
                            if (p <= 0.001) continue;
                            chance += p;
                        }
                    }

                    if (chance > max) max = chance;
                }
                return Math.Min(1.0, max);
            }

            // 信念口径：目标格被敌方“可能本态”攻击的概率之和（只用公开信息）。
            // board 必须是 BuildFairCheckBoard 的产物，敌子才带 BeliefTypeMask。
            private float BeliefAttackProbability(Board board, Position target)
            {
                var enemy = OpponentColor(_owner._color);
                double sum = 0.0;
                foreach (var (from, piece) in board.GetPiecesOfColor(enemy))
                {
                    if (from.Equals(target)) continue;
                    double p = 0.0;
                    for (int t = 1; t < 7; t++)
                    {
                        var type = (PieceType)t;
                        if (!MoveGenerator.CanPieceAttackSquare(board, from, type, enemy, target)) continue;
                        p += _owner.TypeProbability(from, piece, type);
                    }
                    sum += Math.Min(1.0, p);
                }
                return (float)Math.Min(1.0, sum);
            }

            // 王安全红线：逐手看“走完之后真王被敌方可能本态将吃的概率”。
            // 旧实现按掩码布尔值一刀切，低概率的滑子会被漏掉；而且无安全手时只挑危险最低的一手，
            // 经常变成沿后排挪王送死。现在改为概率阈值 + 强制保留“吃掉威胁源”的拆火力手。
            private void ComputeKingSafety()
            {
                var fair = _owner.BuildFairCheckBoard(_board);
                _kingAttackers.Clear();
                foreach (var pos in _owner.FindKingAttackers(fair))
                    _kingAttackers.Add(pos);
                _kingDangerNow = _owner.KingDangerScore(fair)
                    + KingConditionalDangerWeight * _owner.ConditionalKingDanger(fair);

                _threatResolvers.Clear();
                foreach (var move in _rootMoves)
                    if (_kingAttackers.Contains(move.To))
                        _threatResolvers.Add(move);

                // 确定性重子救援：先找出被高置信度攻击的真后/真车（只用公开信念）。
                // 只有 Predator 注入了镜像风险时才启用，其他引擎保持原有严格红线。
                var heavyUnderAttack = new HashSet<Position>();
                if (_owner.KingRescueRisk != null)
                {
                    foreach (var (pos, piece) in _board.GetPiecesOfColor(_owner._color))
                    {
                        if (piece.TrueType != PieceType.Queen && piece.TrueType != PieceType.Rook) continue;
                        if (BeliefAttackProbability(fair, pos) >= HeavyRescueAttackThreshold)
                            heavyUnderAttack.Add(pos);
                    }
                }

                var heavyRescueMoves = new HashSet<Move>();
                if (heavyUnderAttack.Count > 0 && _owner.KingRescueRisk != null)
                {
                    foreach (var move in _rootMoves)
                    {
                        if (!heavyUnderAttack.Contains(move.From)) continue;
                        var rescueUndo = fair.ExecuteMoveWithUndo(move);
                        float stillAttacked = BeliefAttackProbability(fair, move.To);
                        fair.UndoMove(move, rescueUndo);
                        if (stillAttacked < HeavyRescueSafeAttack)
                            heavyRescueMoves.Add(move);
                    }
                }

                double minDangerScore = double.MaxValue;

                // 吃王手不是无条件豁免：身份锁定（P≥75%）才算“这一手结束对局”；
                // 低置信度的赌王要按 (1-p) 折算残余危险——38% 的赌注有 62% 的世界里
                // 玩家的车/后会照常吃掉我们的王（G27 a4->b2 的教训）。
                var kingShotProb = new Dictionary<Move, float>();
                var exemptMoves = new HashSet<Move>();
                foreach (var move in _rootMoves)
                {
                    float shot = _owner.KingKillProbability(_board, move);
                    if (shot <= 0f) continue;
                    kingShotProb[move] = shot;
                    if (shot >= KingKillCertainThreshold) exemptMoves.Add(move);
                }

                foreach (var move in _rootMoves)
                {
                    if (fair.GetPiece(move.From) == null)
                    {
                        _kingDanger[move] = 99;
                        _kingDangerScore[move] = double.MaxValue;
                        continue;
                    }

                    var undo = fair.ExecuteMoveWithUndo(move);
                    _owner.IsOwnKingCapturablePessimistic(fair, out int threats);
                    double dangerScore = _owner.KingDangerScore(fair)
                        + KingConditionalDangerWeight * _owner.ConditionalKingDanger(fair);
                    fair.UndoMove(move, undo);

                    float shot;
                    if (kingShotProb.TryGetValue(move, out shot))
                        dangerScore *= 1.0 - shot;

                    _kingDanger[move] = threats;
                    _kingDangerScore[move] = dangerScore;
                    if (dangerScore < minDangerScore) minDangerScore = dangerScore;
                }

                // v97：王已在紧迫危险中时，记录“最安全的王走法”作为对照基准。
                // 王阈值 0.06 比静手阈值 0.12 更严，旧逻辑会丢掉“只略微超线”的王走法（G35 王走 e7 仅 0.08），
                // 却放行危险度 0.12 的静手/拆火力手，结果王留在敌 Q/R 掩码子的开放线上被下一手吃。
                double bestKingEscapeDanger = double.MaxValue;
                if (_kingDangerNow > KingSafetyUrgentDanger)
                {
                    foreach (var pair in _kingDangerScore)
                    {
                        if (exemptMoves.Contains(pair.Key)) continue;
                        var escapeMover = _board.GetPiece(pair.Key.From);
                        if (escapeMover == null || escapeMover.TrueType != PieceType.King) continue;
                        if (pair.Value < bestKingEscapeDanger) bestKingEscapeDanger = pair.Value;
                    }
                }
                bool kingEscapeIsSafer = bestKingEscapeDanger <= KingSafetySafeDanger;

                // 王吃“很可能就是王”的贴脸子不受悲观掩码红线约束：
                // 掩码会把任何可能的滑子都当成保护，导致唯一解被误杀（G35）。
                // 这类手由采样平均和“最坏可信配置丢王否决”来定价，不能一刀切排除。
                foreach (var pair in kingShotProb)
                {
                    if (pair.Value < KingCaptureGambleProb || exemptMoves.Contains(pair.Key)) continue;
                    var gambleMover = _board.GetPiece(pair.Key.From);
                    if (gambleMover == null || gambleMover.TrueType != PieceType.King) continue;
                    exemptMoves.Add(pair.Key);
                }

                // 重子救援豁免：王危险度不变差（本来就在可能火力下时救子不算加害），
                // 且镜像信念下玩家吃王风险可接受，就允许逃子手绕过王红线。
                if (heavyRescueMoves.Count > 0)
                {
                    int rescued = 0;
                    foreach (var move in heavyRescueMoves)
                    {
                        double danger;
                        if (!_kingDangerScore.TryGetValue(move, out danger)) continue;
                        if (danger > Math.Max(_kingDangerNow, KingSafetySafeDanger) + 0.03) continue;

                        // 王已在紧迫危险中、救子后王仍然危险：这不是解法。允许它绕过红线会让
                        // “唯一幸存手”变成不救王的逃子手（G35：后从 d 线逃开，王还留在 d 线，下一手被真后吃）。
                        if (_kingDangerNow > KingSafetyUrgentDanger && danger > KingSafetySafeDanger)
                        {
                            if (!_quiet)
                                Log($"[AI] 重子救援被否决 {move}：王仍处于危险（{danger:F2}），先保王。");
                            continue;
                        }

                        double risk = _owner.KingRescueRisk(move);
                        if (risk > HeavyRescueRiskLimit)
                        {
                            if (!_quiet)
                                Log($"[AI] 重子救援被否决 {move}：玩家视角吃王风险 {risk:P0}");
                            continue;
                        }

                        exemptMoves.Add(move);
                        rescued++;
                    }

                    if (rescued > 0 && !_quiet)
                        Log($"[AI] 重子救援豁免：{rescued} 手保子走法通过王红线（重子被确定性攻击，王危险度不变差）。");
                }

                // 拆火力手只有在走完真的解除威胁时才豁免红线；
                // 王吃有保护的诱饵、或吃完仍被反吃的手，必须继续接受安全检查。
                foreach (var move in _threatResolvers)
                {
                    double resolved;
                    if (!_kingDangerScore.TryGetValue(move, out resolved)) continue;
                    var resolver = _board.GetPiece(move.From);
                    double limit = resolver != null && resolver.TrueType == PieceType.King
                        ? KingSafetyKingMoveDanger
                        : KingSafetySafeDanger;
                    // v97：王在急险且存在更安全的王走法时，非王拆火力手不能用 0.12 的宽阈值绕过红线。
                    // G35：后吃 f6 马后王仍在敌 Q/R 掩码子的开放线上（危险 0.12）得以豁免，
                    // 而王走 e7（0.08）反被王阈值 0.06 排除，下一手真后沿 d 线吃王。
                    if (kingEscapeIsSafer && limit > bestKingEscapeDanger + 0.001)
                        limit = bestKingEscapeDanger + 0.001;
                    if (resolved <= limit)
                        exemptMoves.Add(move);
                }

                _kingBaitCaptures.Clear();
                foreach (var move in _rootMoves)
                    if (!exemptMoves.Contains(move) && _owner.IsKingBaitCapture(_board, move))
                        _kingBaitCaptures.Add(move);

                // 分别统计“非诱饵最佳”和“诱饵最佳”：诱饵默认不作安全手，但可能是唯一解。
                double nonBaitBest = double.MaxValue;
                double baitBest = double.MaxValue;
                foreach (var pair in _kingDangerScore)
                {
                    if (exemptMoves.Contains(pair.Key)) continue;
                    if (_kingBaitCaptures.Contains(pair.Key))
                        baitBest = Math.Min(baitBest, pair.Value);
                    else
                        nonBaitBest = Math.Min(nonBaitBest, pair.Value);
                }

                bool anySafe = nonBaitBest <= KingSafetySafeDanger;

                // 只要存在“任何可能本态都够不到真王”的走法，就不允许把真王留在可能被吃的格子上。
                // 暗棋里吃王直接终局，3% 的后也足以在 50 局里偷走一两局（G0：e5 挡子被挪开，
                // 对方 f6 只剩 3% 后概率，红线按 0.12 阈值放行，下一手 a1 真王被吃）。
                // 但直接安全还不够：还要危险度（含两步枢轴）达标，才是真正可用的安全手。
                // 否则会像 G48 那样——静手确实没把王暴露给直接攻击，可王贴着可被吃的挡子，
                // 对手一手枢轴贴脸；真正能降危险的挪王却因“某个可能本态够得到”被当成威胁手排掉。
                bool anyThreatFree = false;
                foreach (var pair in _kingDanger)
                {
                    if (exemptMoves.Contains(pair.Key) || _kingBaitCaptures.Contains(pair.Key)) continue;
                    if (pair.Value > 0) continue;
                    anyThreatFree = true;
                    break;
                }

                // v66：只要存在“掩码零威胁”的走法，就排除全部“掩码有威胁”的走法，不再用
                // 0.12 概率阈值给低概率滑子开口子。G18/G32 都是 5%~7% 的隐藏后/车下一手直接吃王：
                // 期望值口径下这手“略优”，但丢王是终局，不能用位置分去换尾部风险。
                // 干净手之间不再按危险度二次筛选，交给搜索和尾部风险项比较。
                if (anyThreatFree)
                {
                    foreach (var pair in _kingDangerScore)
                    {
                        if (exemptMoves.Contains(pair.Key)) continue;
                        int threats;
                        _kingDanger.TryGetValue(pair.Key, out threats);
                        if (_kingBaitCaptures.Contains(pair.Key) || threats > 0)
                            _kingRedLine.Add(pair.Key);
                    }
                }
                else if (anySafe)
                {
                    foreach (var pair in _kingDangerScore)
                    {
                        if (exemptMoves.Contains(pair.Key)) continue;
                        var mover = _board.GetPiece(pair.Key.From);
                        bool safeKingMove = mover != null && mover.TrueType == PieceType.King;
                        double limit = safeKingMove ? KingSafetyKingMoveDanger : KingSafetySafeDanger;
                        // v97：存在更安全的王走法时，统一以它作为上限——王走法本身要能过线，
                        // 危险度更高的非王手一律排除，不让“王留在开放线上 + 0.12 静手”抢走逃生手（G35）。
                        if (kingEscapeIsSafer)
                            limit = bestKingEscapeDanger + 0.001;
                        if (_kingBaitCaptures.Contains(pair.Key) || pair.Value > limit)
                            _kingRedLine.Add(pair.Key);
                    }
                }
                else
                {
                    // 没有安全的非诱饵手：按“实际最低危险度”决策，而不是无条件只留挪王/拆火力。
                    // G20：唯一挪王 d1 反而被真车吃 f1 后横扫（危险度 0.86），静手留守只有 0.22。
                    // 诱饵默认排除，但“略好”不等于“解法”：G48 王被 d2 的隐藏后咬住，挪到 c1
                    // 危险 0.99（仍在该后斜线上），只因比当前 1.28 小就被当成非诱饵解法，把真正能
                    // 活命的王吃后（危险 0.19）判成诱饵否决。只有把危险度压到可接受区间才算真解法。
                    bool nonBaitImproves = nonBaitBest < _kingDangerNow - 0.10 &&
                                           nonBaitBest <= KingSafetyUrgentDanger;
                    bool useBait = !nonBaitImproves && baitBest < nonBaitBest - 0.05;
                    double floor = useBait ? baitBest : nonBaitBest;

                    if (floor < double.MaxValue)
                    {
                        bool desperate = floor > KingSafetyUrgentDanger;
                        foreach (var pair in _kingDangerScore)
                        {
                            if (exemptMoves.Contains(pair.Key)) continue;
                            bool bait = _kingBaitCaptures.Contains(pair.Key);

                            var mover = _board.GetPiece(pair.Key.From);
                            bool kingMove = mover != null && mover.TrueType == PieceType.King;
                            bool resolver = _threatResolvers.Contains(pair.Key);

                            // 绝境下的“吃威胁源”哪怕是诱饵也不能直接排除：王被吃是立即终局，
                            // 吃诱饵只是概率性损失。G48 王吃贴脸后（真后）危险度 0.09，
                            // 却被诱饵标签一票否决，改挪到同一只后仍控制的 c1 当场被杀。
                            bool urgentRescue = desperate && (kingMove || resolver) &&
                                                pair.Value <= KingSafetySafeDanger &&
                                                pair.Value <= floor - 0.20;
                            if (bait && !useBait && !urgentRescue)
                            {
                                _kingRedLine.Add(pair.Key);
                                continue;
                            }

                            if (pair.Value <= floor + 0.03) continue;
                            if (desperate && (kingMove || resolver) && pair.Value <= floor + 0.10) continue;
                            _kingRedLine.Add(pair.Key);
                        }
                    }
                }

                // 防御性兜底：绝不允许红线吞掉全部走法（吃王手永不吞）。
                // 优先保住“挪王/拆火力”这类唯一解——哪怕它是诱饵手。
                // G27：王被自家子围死、唯一的解是吃掉贴脸的象（f7 可能被 f 线车/后反吃，
                // 故被判诱饵），旧兜底反而保留了 18 手必死静手，王原地被吃。
                // 吃诱饵只是 18% 风险，原地不动是 97% 死亡，代价完全不同。
                if (_kingRedLine.Count >= _rootMoves.Count)
                {
                    _kingRedLine.Clear();

                    var lastResorts = new List<Move>();
                    var baitResorts = new List<Move>();
                    foreach (var pair in _kingDangerScore)
                    {
                        if (exemptMoves.Contains(pair.Key)) continue;
                        var mover = _board.GetPiece(pair.Key.From);
                        bool kingMove = mover != null && mover.TrueType == PieceType.King;
                        if (!kingMove && !_threatResolvers.Contains(pair.Key)) continue;
                        if (_kingBaitCaptures.Contains(pair.Key)) baitResorts.Add(pair.Key);
                        else lastResorts.Add(pair.Key);
                    }

                    // 诱饵王吃只在没有任何其他解法时才启用（G29：叠车诱饵吃下去立刻被反吃，
                    // 同批还有非诱饵王走 c8，必须先选它）。
                    if (lastResorts.Count == 0) lastResorts.AddRange(baitResorts);

                    if (lastResorts.Count > 0)
                    {
                        // 有真正的解法（挪王/拆火力）时，静手全部作废；解法本身即使被判诱饵也保留。
                        foreach (var pair in _kingDangerScore)
                            if (!exemptMoves.Contains(pair.Key) && !lastResorts.Contains(pair.Key))
                                _kingRedLine.Add(pair.Key);
                    }
                    else
                    {
                        double panicFloor = double.MaxValue;
                        foreach (var pair in _kingDangerScore)
                            if (!exemptMoves.Contains(pair.Key) && !_kingBaitCaptures.Contains(pair.Key))
                                panicFloor = Math.Min(panicFloor, pair.Value);

                        if (panicFloor == double.MaxValue)
                        {
                            double rawFloor = double.MaxValue;
                            foreach (var pair in _kingDangerScore)
                                if (!exemptMoves.Contains(pair.Key))
                                    rawFloor = Math.Min(rawFloor, pair.Value);

                            foreach (var pair in _kingDangerScore)
                                if (!exemptMoves.Contains(pair.Key) && pair.Value > rawFloor)
                                    _kingRedLine.Add(pair.Key);
                        }
                        else
                        {
                            foreach (var pair in _kingDangerScore)
                                if (!exemptMoves.Contains(pair.Key) &&
                                    (_kingBaitCaptures.Contains(pair.Key) || pair.Value > panicFloor + 0.03))
                                    _kingRedLine.Add(pair.Key);
                        }
                    }

                    if (_kingRedLine.Count >= _rootMoves.Count)
                        _kingRedLine.Clear();
                }

                // 真王走子按“任意敌子按任意可能类型”避让：对手可以用伪装标签允许的走法
                // 把掩码一直压在窄集合里（伪装马的真车只走马步），直到最后一手才露真型吃王。
                // G7/G19/G32/G43 的吃王手在发生前都不在掩码射程内，靠概率红线永远拦不住。
                // 只约束王自己的根走法；若会封死全部根手，撤回本次新增（交给搜索兜底）。
                // v103 试过“不撤回、改放概率红线”，冒烟 G2/G7 仍在同一手数被吃（王早已没有安全格），
                // 且会牺牲“唯一幸存王走法其实全类型安全”的局面，已回退（详见 HANDOFF §4.10）。
                var paranoidBlocked = new List<Move>();
                foreach (var move in _rootMoves)
                {
                    if (exemptMoves.Contains(move) || _kingRedLine.Contains(move)) continue;
                    var kingMover = _board.GetPiece(move.From);
                    if (kingMover == null || kingMover.TrueType != PieceType.King) continue;
                    if (fair.GetPiece(move.From) == null) continue;

                    var parUndo = fair.ExecuteMoveWithUndo(move);
                    var parKingPos = fair.FindKing(_owner.Color);
                    bool paranoidHit = parKingPos.HasValue &&
                        _owner.IsSquareAttackedByAnyPossibleEnemy(fair, parKingPos.Value);
                    fair.UndoMove(move, parUndo);

                    if (paranoidHit) paranoidBlocked.Add(move);
                }

                if (paranoidBlocked.Count > 0)
                {
                    int before = _kingRedLine.Count;
                    foreach (var move in paranoidBlocked) _kingRedLine.Add(move);
                    if (_kingRedLine.Count >= _rootMoves.Count)
                    {
                        // 全盘只剩被全类型火力覆盖的王走法：不能因此放行既有红线，
                        // 只撤回本步新增的部分。
                        int withdrawn = 0;
                        foreach (var move in paranoidBlocked)
                        {
                            if (_kingDangerScore.ContainsKey(move) && _kingRedLine.Count > before)
                            {
                                _kingRedLine.Remove(move);
                                withdrawn++;
                            }
                        }
                        // 这里必须留痕：撤回后王走法会重新回到搜索里，日志里看不到就等于事后无法归因（G7）。
                        if (withdrawn > 0 && !_quiet)
                            Log($"[AI] 王走子全类型避让封死全盘：撤回 {withdrawn} 手新增避让，" +
                                $"其余 {_kingRedLine.Count} 手红线保留，王吃/走由搜索定价。");
                    }
                    else if (!_quiet)
                    {
                        Log($"[AI] 王走子全类型避让：排除 {_kingRedLine.Count - before} 手会进入“任意可能射程”的王走法。");
                    }
                }

                if (KingSafetyDebug)
                {
                    var kingPos = fair.FindKing(_owner.Color);
                    Log($"[KSDbg] 王位 {kingPos} 当前危险 {_kingDangerNow:F2} 威胁源 {_kingAttackers.Count} anyThreatFree={anyThreatFree} anySafe={anySafe} minDanger={minDangerScore:F2}");
                    foreach (var pair in _kingDangerScore)
                    {
                        int threats;
                        _kingDanger.TryGetValue(pair.Key, out threats);
                        Log($"[KSDbg] {pair.Key} risks={threats} danger={pair.Value:F2} bait={_kingBaitCaptures.Contains(pair.Key)} exempt={exemptMoves.Contains(pair.Key)} red={_kingRedLine.Contains(pair.Key)}");
                    }
                }

                if (_kingRedLine.Count > 0 && !_quiet)
                {
                    int usable = _rootMoves.Count - _kingRedLine.Count;
                    Log(anySafe
                        ? $"[AI] 王安全红线：排除 {_kingRedLine.Count}/{_rootMoves.Count} 手（真王被吃概率超阈值），保留 {usable} 手。"
                        : $"[AI] 王安全红线：没有完全安全的手（当前危险度 {_kingDangerNow:F2}，威胁源 {_kingAttackers.Count}），" +
                          $"按概率保留 {usable}/{_rootMoves.Count} 手，强制保留 {_threatResolvers.Count} 手拆火力。");
                }
            }

            private void EvaluateRootMove(Move move, int depth)
            {
                if (_merged.GetPiece(move.From) == null) { _skipMissing++; return; }

                // 王安全红线：这一步会让真王进入敌方可能的攻击范围，整手弃用。
                if (_kingRedLine.Contains(move)) { _skipRed++; return; }
                // 大子暴露硬约束：存在“不挂后/挂车”的走法时，不选会挂大子的手。
                // 掉后/掉车在期望搜索里只按信念概率折价，50 局里必然漏掉一两局。
                if (_hangingHeavy.Contains(move)) { _skipHanging++; return; }

                var undo = _merged.ExecuteMoveWithUndo(move);
                int score;
                bool capturedSampledKing = undo.Captured != null && undo.Captured.TrueType == PieceType.King;
                if (capturedSampledKing)
                {
                    score = _owner.SampleKingCaptureScore(move.To);
                }
                else
                {
                    bool ownKingExposed = _owner.IsOwnKingCapturable(_merged);
                    if (_sampleIndex == _threatWorldIndex)
                    {
                        if (ownKingExposed) _threatKingLoss.Add(move);
                    }
                    else if (ownKingExposed)
                    {
                        AddKingLoss(move);
                    }

                    score = -_owner.Negamax(_merged, depth, -INF, INF,
                        OpponentColor(_owner._color), _stopwatch, _tt, 1, _buffers);
                }
                _merged.UndoMove(move, undo);

                // 预算耗尽的走法只有静态估值，不能进根平均。
                if (_owner.SearchAborted) { _skipAborted++; return; }
                AddScore(move, score, _sampleIndex == _threatWorldIndex);
            }

            private void AddScore(Move move, int score, bool threatWorld = false)
            {
                if (threatWorld)
                {
                    // 最坏可信配置只用来做“丢王否决”，不进平均分，也不做软性尾部扣分：
                    // 软扣分会把常规决策压成过度防守（50 局实测 82%）。
                    int threat;
                    if (!_threatScores.TryGetValue(move, out threat) || score < threat)
                        _threatScores[move] = score;
                    if (score <= -MATE + 500) _threatKingLoss.Add(move);
                    return;
                }

                double sum;
                _sums.TryGetValue(move, out sum);
                _sums[move] = sum + score;

                int count;
                _counts.TryGetValue(move, out count);
                _counts[move] = count + 1;

                int min;
                if (!_minScores.TryGetValue(move, out min) || score < min)
                    _minScores[move] = score;
            }

            private void AddKingLoss(Move move)
            {
                int loss;
                _kingLossCounts.TryGetValue(move, out loss);
                _kingLossCounts[move] = loss + 1;
            }

            private void CompleteDepth()
            {
                // 预算在本层中途耗尽：后面的走法只有静态估值，整层混合了两种口径，不能当结论。
                // 直接收尾，沿用上一个完全搜完的深度（全部走法都搜过、未被截断）。
                if (_owner.SearchAborted)
                {
                    if (!_quiet)
                        Log($"[AI] 搜索预算在深度 {_depth} 中途耗尽，本层作废，沿用 {_completedDepths} 层结论。");
                    Finish();
                    return;
                }

                bool allLoseKing;
                var best = SelectBestAtCurrentDepth(out allLoseKing);

                if (allLoseKing && !_quiet)
                {
                    // 所有走法都可能丢王时也必须继续走：跳过回合等于白送一步，还会误报“无子可动”。
                    Log("[AI] 采样中所有走法都可能丢王，仍选择最能拖延/反吃的一手，不跳过回合。");
                }

                if (best != null)
                {
                    _bestCompleted = best;
                    _completedDepths++;
                    UpdatePreviousAverages();

                    if (!_quiet)
                    {
                        int winRate = (int)Math.Round(100.0 * WinProbability(best.Average));
                        Log($"[AI] 迭代加深 深度 {_depth}/{_targetDepth} 完成：{best.Move}，平均评分 {best.Average:F0}，胜率预估 {winRate}%，采样 {_processedSamples}/{_samples.Count}，耗时 {_stopwatch.ElapsedMilliseconds}ms。");
                    }

                    float pKing = _owner._beliefState.GetProbability(best.Move.To, PieceType.King);
                    if (pKing > 0.05f && !_quiet)
                    {
                        int killGain = _owner.GetKingCaptureExpectation(_board, best.Move);
                        int trapLoss = _owner.GetTrapExpectedLoss(_board, best.Move);
                        Log($"[AI] 王概率期望：目标 P_King={pKing * 100f:F0}%，绝杀收益 {killGain}，诱饵反杀损失 {trapLoss}，净期望 {killGain - trapLoss}。");
                    }
                }

                if (best == null)
                {
                    Finish();
                    return;
                }

                if (_depth >= _targetDepth || TimedOut())
                {
                    Finish();
                    return;
                }

                _depth++;
                _sums.Clear();
                _counts.Clear();
                _minScores.Clear();
                _threatScores.Clear();
                _threatKingLoss.Clear();
                _threatVetoLoggedDepth = -1;
                _kingLossCounts.Clear();
                _sampleIndex = 0;
                _moveIndex = 0;
                _processedSamples = 0;
            }

            private void UpdatePreviousAverages()
            {
                foreach (var move in _rootMoves)
                {
                    int count;
                    if (!_counts.TryGetValue(move, out count) || count <= 0) continue;

                    double sum;
                    _sums.TryGetValue(move, out sum);
                    _previousAverages[move] = sum / count;
                }
            }

            private void Finish()
            {
                _finished = true;

                _owner.LastSearchDepth = Math.Max(1, _completedDepths);
                _owner.LastQuiescenceDepth = _owner._qNodeCount > 0 ? (double)_owner._qDepthSum / _owner._qNodeCount : 0;
                _owner.LastSearchNodes = _owner._nodeCount + _owner._qNodeCount;
                _owner.LastSearchMs = _stopwatch.ElapsedMilliseconds;
                _owner.LastNodesPerSecond = _stopwatch.ElapsedMilliseconds > 0
                    ? _owner.LastSearchNodes * 1000.0 / _stopwatch.ElapsedMilliseconds
                    : 0;

                // 超时防抖：当前层若只跑了一部分样本，单世界平均值会完全盖过已完成层的全样本平均值。
                // 实战事故：深度 3 只完成 1/16 样本，那一局里 b1 恰好不是马，于是把后送到 b1 马口，
                // 平均评分 4397、胜率 99%。覆盖不足时直接退回最后一个完整层的全样本结论。
                // 只看 _processedSamples 还不够：走法按样本动态排序，截断时不同走法的覆盖数会差很多
                //（实测 b5->a7 只被 3 个样本算到，其中一局 a7 恰好抽样成王，平均分被 9999 拉成 +2979）。
                // 因此要求每个可评走法的样本数都追平已处理样本数。
                bool incompleteCoverage = false;
                foreach (var move in _rootMoves)
                {
                    if (_kingRedLine.Contains(move) || _hangingHeavy.Contains(move)) continue;
                    int covered;
                    if (!_counts.TryGetValue(move, out covered) || covered < _processedSamples)
                    {
                        incompleteCoverage = true;
                        break;
                    }
                }

                bool underSampled = _completedDepths > 0 && _bestCompleted != null &&
                                    _samples.Count > 1 &&
                                    (_processedSamples < _samples.Count || incompleteCoverage);
                if (underSampled && !_quiet)
                    Log($"[AI] 本层样本覆盖不足（{_processedSamples}/{_samples.Count}），" +
                        $"放弃单世界结论，沿用 {_completedDepths} 层全样本最优手 {_bestCompleted.Move}。");

                // 最终复核：玩家“主动送到无保护格”的吃子必须多算 2 层。
                // 只有真正完成过一轮评分才值得复核，否则纯属浪费残局预算。
                var verified = !underSampled && _completedDepths > 0
                    ? SelectBestAtCurrentDepth(out _, finalVerification: true)
                    : null;

                if (verified != null)
                {
                    _result = verified.Move;
                    int depth = Math.Max(1, _completedDepths);
                    LogBaitDecline();
                    if (!_quiet)
                    {
                        _owner.LogMove(_result.Value, _board, _processedSamples, _samples.Count, depth);
                        _owner.LogKingIntent(_result.Value, _board);
                        LogDecoySacrifice(_result.Value);

                        int winRate = (int)Math.Round(100.0 * WinProbability(verified.Average));
                        Log($"[AI] 迭代加深完成 {depth} 层（目标 {_targetDepth}），静态延伸 {_owner.LastQuiescenceDepth:F1} 层，" +
                            $"节点 {_owner.LastSearchNodes}（{_owner.LastNodesPerSecond / 1000.0:F0}k/s），" +
                            $"采样 {_processedSamples}/{_samples.Count} 局，平均评分 {verified.Average:F0}，" +
                            $"胜率预估 {winRate}%，耗时 {_stopwatch.ElapsedMilliseconds}ms。" +
                            (_endgameMode ? (_samples.Count == 1 ? " 残局穷举：信念已确定，使用最大似然世界。" : $" 残局深算：信念未定，保留 {_samples.Count} 个采样世界。") : string.Empty));
                    }
                    return;
                }

                if (_bestCompleted != null)
                {
                    _result = _bestCompleted.Move;                    int depth = Math.Max(1, _completedDepths);
                    LogBaitDecline();
                    if (!_quiet)
                    {
                        _owner.LogMove(_result.Value, _board, _processedSamples, _samples.Count, depth);
                        _owner.LogKingIntent(_result.Value, _board);
                        LogDecoySacrifice(_result.Value);

                        int winRate = (int)Math.Round(100.0 * WinProbability(_bestCompleted.Average));
                        Log($"[AI] 迭代加深完成 {depth} 层（目标 {_targetDepth}），静态延伸 {_owner.LastQuiescenceDepth:F1} 层，" +
                            $"节点 {_owner.LastSearchNodes}（{_owner.LastNodesPerSecond / 1000.0:F0}k/s），" +
                            $"采样 {_processedSamples}/{_samples.Count} 局，平均评分 {_bestCompleted.Average:F0}，" +
                            $"胜率预估 {winRate}%，耗时 {_stopwatch.ElapsedMilliseconds}ms。" +
                            (_endgameMode ? (_samples.Count == 1 ? " 残局穷举：信念已确定，使用最大似然世界。" : $" 残局深算：信念未定，保留 {_samples.Count} 个采样世界。") : string.Empty));
                    }
                    return;
                }

                // 一层都没搜完时，“部分走法的搜索分”和“整盘静态分”不可比：
                // 只对全部根走法做一次静态估值兜底，避免因漏搜而选中送子手。
                if (_completedDepths == 0)
                {
                    var safe = SelectStaticFallback();
                    if (safe.HasValue)
                    {
                        _result = safe.Value;
                        LogBaitDecline();
                        if (!_quiet)
                        {
                            _owner.LogMove(_result.Value, _board, _processedSamples, _owner._sampleCount, 1);
                            _owner.LogKingIntent(_result.Value, _board);
                            Log($"[AI] 预算耗尽且未完成完整一层，改用全走法静态估值兜底 {_result.Value}（耗时 {_stopwatch.ElapsedMilliseconds}ms）。");
                            Log($"[AI][ZeroScore] 根手 {_rootMoves.Count} 红线 {_kingRedLine.Count} 挂子 {_hangingHeavy.Count} " +
                                $"| 跳过 缺子{_skipMissing} 红线{_skipRed} 挂子{_skipHanging} 截断{_skipAborted} " +
                                $"| 样本 {_processedSamples}/{_samples.Count} 深度 {_depth}");
                        }
                        return;
                    }
                }

                bool allLoseKing;
                var partial = SelectBestAtCurrentDepth(out allLoseKing);
                if (allLoseKing && !_quiet)
                    Log("[AI] 采样中所有走法都可能丢王，仍返回当前最顽强的一手，不跳过回合。");

                if (partial != null)
                {
                    _bestCompleted = partial;
                    _result = partial.Move;
                    LogBaitDecline();
                    if (!_quiet)
                    {
                        _owner.LogMove(partial.Move, _board, _processedSamples, _owner._sampleCount, 1);
                        _owner.LogKingIntent(partial.Move, _board);
                        LogDecoySacrifice(partial.Move);
                        Log($"[AI] 时间预算耗尽，返回当前最好走法 {partial.Move}（平均评分 {partial.Average:F0}，采样 {_processedSamples}/{_owner._sampleCount}，耗时 {_stopwatch.ElapsedMilliseconds}ms）。");
                    }
                    return;
                }

                if (!_quiet) Log("[AI] 时间预算耗尽且无任何评分样本，采用静态根评估选子（非随机）。");
                _result = SelectStaticFallback() ?? _rootMoves[0];
            }

            // 未完成任何一层时的兜底：全部根走法按同一口径做静态估值，再扣除子力暴露风险。
            private Move? SelectStaticFallback()
            {
                if (_samples.Count == 0) return null;

                var merged = _owner.MergeBoard(_board, _samples[0]);
                Move? best = null;
                double bestScore = double.NegativeInfinity;

                foreach (var move in _rootMoves)
                {
                    if (_kingRedLine.Contains(move)) continue;
                    if (merged.GetPiece(move.From) == null) continue;

                    var undo = merged.ExecuteMoveWithUndo(move);
                    int score = _owner.Evaluate(merged, _owner._color);
                    merged.UndoMove(move, undo);

                    double risk;
                    _materialRisk.TryGetValue(move, out risk);

                    double selection = score - risk * MaterialRiskWeight;
                    if (selection > bestScore)
                    {
                        bestScore = selection;
                        best = move;
                    }
                }

                return best;
            }

            private void LogDecoySacrifice(Move move)
            {
                if (_quiet) return;
                if (_owner.GetDecoySacrificeBonus(_board, move) <= 0) return;

                var piece = _board.GetPiece(move.From);
                if (piece == null) return;
                Log($"[AI] 真王被咬住：让伪装为{piece.VisibleType.ToChinese()}的{piece.TrueType.ToChinese()}主动走进火力 {move}，" +
                    "用一次替死把玩家的注意力从真王身上引开。");
            }

            private void LogBaitDecline()
            {
                if (_quiet || !_result.HasValue) return;

                Move? bait = null;
                int bestPenalty = 0;
                foreach (var move in _rootMoves)
                {
                    int count;
                    if (!_counts.TryGetValue(move, out count) || count == 0) continue;

                    double sum;
                    _sums.TryGetValue(move, out sum);
                    int penalty = _owner.GetLoneBaitPenalty(_board, move, sum / count, Math.Max(1, _depth));
                    if (penalty <= bestPenalty) continue;

                    bestPenalty = penalty;
                    bait = move;
                }

                if (bait.HasValue && !bait.Value.Equals(_result.Value))
                {
                    var target = _board.GetPiece(bait.Value.To);
                    string name = target != null ? target.VisibleType.ToChinese() : "子";
                    Log($"[AI] 多疑：{bait.Value.To} 的{name}无保护，疑似诱饵（惩罚 {bestPenalty}），忍住不吃，改走 {_result.Value}。");
                }
            }

            private MoveState SelectBestAtCurrentDepth(out bool allLoseKing, bool finalVerification = false)
            {
                allLoseKing = false;
                if (_counts.Count == 0) return null;

                allLoseKing = _owner.AllRootMovesLoseKing(_rootMoves, _counts, _kingLossCounts);

                var scored = new List<(Move move, double selection, double average, int count, bool kingKill)>(_rootMoves.Count);
                double bestHoldSelection = double.NegativeInfinity;

                foreach (var move in _rootMoves)
                {
                    int count;
                    if (!_counts.TryGetValue(move, out count) || count == 0) continue;

                    double sum;
                    _sums.TryGetValue(move, out sum);
                    double avg = sum / count;

                    // 绝杀覆盖多疑：只有身份基本锁定（P_King ≥ 75%）的吃王才拉到绝杀分。
                    // 低置信度的“四选一赌王”必须回到搜索期望里结算——抽样世界平均分
                    // 已经按 p×MATE 计过吃王收益，也计过另外 (1-p) 丢掉自己王的代价。
                    bool kingKill = _owner.IsConfidentKingCapture(_board, move);
                    double selection;
                    if (kingKill)
                    {
                        selection = _owner.WouldLoseOwnKing(_board, move)
                            ? AIPlayer.KingKillPrize - 1
                            : AIPlayer.KingKillPrize;
                    }
                    else
                    {
                        // 期望值结算：吃王收益已由根节点抽样平均（p × MATE）体现，这里不再重复加；
                        // 只扣“疑似诱饵被反杀”的期望损失。
                        int trapLoss = _owner.GetTrapExpectedLoss(_board, move);
                        var captured = _board.GetPiece(move.To);

                        // 真王被真威胁时，欺诈/诱饵/替死/拖延这类花招必须给防守让路，
                        // 否则会为了“骗一手”把王留在火力里。
                        double trickWeight = _kingDangerNow > KingSafetyUrgentDanger ? 0.15 : 1.0;

                        selection = avg
                            + _owner.GetUrgencyBonus(_board, move)
                            + _owner.GetKingPressureBonus(_board, move)
                            + trickWeight * (
                                _owner.GetDeceptionBonus(move, _board)
                                + _owner.GetProbeBonus(move, _board)
                                + _owner.GetBluffBonus(move, _board)
                                + _owner.GetDecoySacrificeBonus(_board, move)
                                + _owner.GetOpeningPlanBonus(_board, move)
                                + _owner.GetAntiTurtleBonus(_board, move))
                            - trapLoss;

                        if (_owner.RootMoveBonus != null)
                            selection += _owner.RootMoveBonus(_board, move);

                        // 子力暴露：把“可能被便宜子吃”的大子按风险厌恶结算，避免单世界采样把小概率的
                        // 掉后/掉车平均成噪声（实战：后走到 c8 象的斜线上，搜索只按 10% 概率计损失）。
                        double exposureRisk;
                        if (_materialRisk.TryGetValue(move, out exposureRisk))
                            selection -= exposureRisk * MaterialRiskWeight;

                        // 尾部风险：最坏采样世界比平均差太多时收保险费。
                        // 16 个世界里 1 个丢后/丢王只拉低平均值 1/16，但真实对局里那就是一局。
                        int worstScore;
                        if (_minScores.TryGetValue(move, out worstScore))
                        {
                            double tailGap = avg - worstScore;
                            if (tailGap > TailRiskAllowance)
                                selection -= (tailGap - TailRiskAllowance) * TailRiskWeight;
                        }

                        // 通用防守优先：吃掉正在威胁真王的子，或明显降低被吃概率的手，直接加权。
                        var mover = _board.GetPiece(move.From);
                        bool kingMover = mover != null && mover.TrueType == PieceType.King;
                        if (_kingDangerNow > KingSafetyUrgentDanger)
                        {
                            double afterDanger;
                            bool knownAfterDanger = _kingDangerScore.TryGetValue(move, out afterDanger);
                            double safeLimit = kingMover ? KingSafetyKingMoveDanger : KingSafetySafeDanger;

                            // 只有走完真的解除威胁才加防守权重。
                            // 否则王亲自吃有保护的诱饵、或只是把危险度从 1.0 降到 0.5 时，
                            // 奖励会盖过搜索里 -10000 的丢王代价。王本人走子用更严格的阈值。
                            if (knownAfterDanger && afterDanger <= safeLimit)
                            {
                                if (_threatResolvers.Contains(move))
                                    selection += 40000;

                                double drop = _kingDangerNow - afterDanger;
                                if (drop > 0.01)
                                    selection += Math.Min(15000.0, drop * 25000.0);
                            }

                            if (knownAfterDanger && afterDanger > _kingDangerNow + 0.05)
                                selection -= 8000;
                        }

                        // 通用反原地转圈：同一子连挪、或走回上一步起点都要付成本。
                        var previousOwn = _owner._lastOwnMove;
                        if (previousOwn.HasValue)
                        {
                            if (move.From.Equals(previousOwn.Value.From))
                                selection -= 150;
                            if (move.To.Equals(previousOwn.Value.From))
                                selection -= 300;
                        }

                        if (mover != null)
                        {
                            // 王不在危险中时挪王浪费节奏；但王吃子有实打实的物质收益，不该再罚。
                            if (mover.TrueType == PieceType.King && captured == null &&
                                _kingDangerNow <= KingSafetyUrgentDanger)
                                selection -= 250;

                            // 通用开局纪律：前 8 手无吃子时，不鼓励早出后白送 tempo。
                            if (_owner.OwnMoveCount < 8 && _owner._movesSinceLastCapture < 4 &&
                                mover.TrueType == PieceType.Queen && captured == null)
                                selection -= 180;
                        }
                    }

                    scored.Add((move, selection, avg, count, kingKill));

                    var target = _board.GetPiece(move.To);
                    if (target == null || target.Color == _owner._color)
                        bestHoldSelection = Math.Max(bestHoldSelection, selection);
                }

                if (finalVerification && !_deepVerified)
                    RunDeepCaptureVerification(scored);

                // 最坏可信配置下会丢王的根手，只要还存在不丢王的活路，就直接排除。
                // 概率平均会让 51% 可能的贴脸让王被“平均分差一点”掩盖掉，这一步把丢王当终局处理。
                if (_threatKingLoss.Count > 0 && scored.Count > 1)
                {
                    bool anySurvives = false;
                    foreach (var entry in scored)
                        if (!_threatKingLoss.Contains(entry.move)) { anySurvives = true; break; }

                    if (anySurvives)
                    {
                        int removed = scored.RemoveAll(e => _threatKingLoss.Contains(e.move));
                        if (removed > 0 && !_quiet && _threatVetoLoggedDepth != _depth)
                        {
                            _threatVetoLoggedDepth = _depth;
                            Log($"[AI] 丢王否决（最坏可信配置）：排除 {removed} 手会丢王的走法，保留 {scored.Count} 手。");
                        }
                    }
                }

                MoveState best = null;
                foreach (var entry in scored)
                {
                    double selection = entry.selection;

                    // 多疑只在与“不吃”的最佳方案接近时生效；绝杀级吃王与危局拆火力直接跳过诱饵逻辑。
                    bool defending = _kingDangerNow > KingSafetyUrgentDanger && _threatResolvers.Contains(entry.move);
                    var target = _board.GetPiece(entry.move.To);
                    if (!entry.kingKill && !defending && target != null && target.Color != _owner._color)
                    {
                        int value = target.VisibleType.GetValue();
                        double dominance = bestHoldSelection > double.NegativeInfinity
                            ? entry.selection - bestHoldSelection
                            : double.PositiveInfinity;
                        if (entry.selection < DecisiveScore && dominance <= value * 0.6)
                            selection -= _owner.GetLoneBaitPenalty(_board, entry.move, entry.average, _depth);

                        // 吃子期望门：+/−2 层复核后若胜率不升反降，直接判为诱饵，扣 5000 放弃。
                        double deepScore;
                        if (_deepCaptureScores.TryGetValue(entry.move, out deepScore))
                        {
                            double captureWin = WinProbability(deepScore);
                            double holdWin = _hasDeepNonCapture ? WinProbability(_deepNonCaptureScore) : 0.5;
                            if (captureWin < holdWin - 0.015)
                            {
                                selection -= 5000;
                                if (!_quiet)
                                    Log($"[AI] 吃子期望否决：吃 {entry.move.To} 后深算胜率 {captureWin:P0} < 不吃 {holdWin:P0}，" +
                                        "判定为诱饵，扣 5000 分放弃。");
                            }
                        }
                    }

                    // 残局机动性：把对手的应手数压到越少越好；0 应手=强制跳过，等价于一次免费 tempo。
                    if (_endgameMode && !entry.kingKill)
                    {
                        int replies = CountRepliesAfter(entry.move);
                        selection += replies == 0 ? 600 : -replies * 1.2;
                    }

                    if (best == null || selection > best.Selection)
                        best = new MoveState
                        {
                            Move = entry.move,
                            Selection = selection,
                            Average = entry.average,
                            Count = entry.count
                        };
                }

                if (RootDebug && finalVerification && best != null && !_quiet)
                {
                    var ranked = new List<(Move move, double selection, double average, int count, bool kingKill)>(scored);
                    ranked.Sort((a, b) => b.selection.CompareTo(a.selection));
                    Log($"[ROOTDBG] 采样={_samples.Count} 深度={_depth} 候选={ranked.Count} 最优={best.Move}");
                    for (int i = 0; i < Math.Min(40, ranked.Count); i++)
                    {
                        double risk;
                        _materialRisk.TryGetValue(ranked[i].move, out risk);
                        int threat;
                        _threatScores.TryGetValue(ranked[i].move, out threat);
                        Log($"[ROOTDBG]  {i + 1}. {ranked[i].move} avg={ranked[i].average:F0} cnt={ranked[i].count} risk={risk:F0} threat={threat} tloss={_threatKingLoss.Contains(ranked[i].move)} sel={ranked[i].selection:F0}");
                    }
                }

                return best;
            }

            // 只复核“玩家刚主动送到无保护格”的吃子；最多 3 个，避免诱饵验证本身拖垮预算。
            private void RunDeepCaptureVerification(List<(Move move, double selection, double average, int count, bool kingKill)> scored)
            {
                _deepVerified = true;
                if (_samples.Count == 0) return;

                var fair = _owner.BuildFairCheckBoard(_board);
                var suspects = new List<Move>();
                foreach (var entry in scored)
                {
                    // 绝杀级吃王与危局拆火力不做诱饵复核：这两类手的目的不是吃子收益，而是解决王。
                    if (entry.kingKill) continue;
                    if (_kingDangerNow > KingSafetyUrgentDanger && _threatResolvers.Contains(entry.move)) continue;

                    var target = _board.GetPiece(entry.move.To);
                    if (target == null || target.Color == _owner._color) continue;
                    if (_kingRedLine.Contains(entry.move)) continue;

                    bool justMoved = _owner._lastOpponentTo.HasValue &&
                                     _owner._lastOpponentTo.Value.Equals(entry.move.To);
                    bool undefended = !_owner.IsSquareDefendedByBelief(fair, entry.move.To);
                    if (!justMoved && !undefended) continue;
                    if (!justMoved && target.VisibleType.GetValue() < 100) continue;

                    suspects.Add(entry.move);
                    if (suspects.Count >= 3) break;
                }

                if (suspects.Count == 0) return;

                Move? bestHold = null;
                double bestHoldSelection = double.NegativeInfinity;
                foreach (var entry in scored)
                {
                    var target = _board.GetPiece(entry.move.To);
                    if (target != null && target.Color != _owner._color) continue;
                    if (entry.selection > bestHoldSelection)
                    {
                        bestHoldSelection = entry.selection;
                        bestHold = entry.move;
                    }
                }

                var baseBoard = _owner.MergeBoard(_board, _samples[0]);
                int verifyDepth = _depth - 1 + 2;

                foreach (var move in suspects)
                    _deepCaptureScores[move] = EvaluateDeepOnWorld(baseBoard, move, verifyDepth);

                if (bestHold.HasValue)
                {
                    _deepNonCaptureScore = EvaluateDeepOnWorld(baseBoard, bestHold.Value, verifyDepth);
                    _hasDeepNonCapture = true;
                }

                if (!_quiet)
                    Log($"[AI] 吃子期望复核：{suspects.Count} 个送上门的目标额外加深 2 层至 {_depth + 1} 层" +
                        (_hasDeepNonCapture ? "，并对比最佳不吃方案。" : "。"));
            }

            private double EvaluateDeepOnWorld(Board baseBoard, Move move, int depth)
            {
                if (baseBoard.GetPiece(move.From) == null) return -INF;
                var board = baseBoard.Clone();
                var undo = board.ExecuteMoveWithUndo(move);
                int score;
                if (undo.Captured != null && undo.Captured.TrueType == PieceType.King)
                {
                    score = MATE;
                }
                else
                {
                    score = -_owner.Negamax(board, depth, -INF, INF,
                        OpponentColor(_owner._color), _stopwatch, _sampleTts[0], 1, _sampleBuffers[0]);
                }
                board.UndoMove(move, undo);
                return score;
            }

            private int CountRepliesAfter(Move move)
            {
                int cached;
                if (_replyCounts.TryGetValue(move, out cached)) return cached;

                var board = _owner.MergeBoard(_board, _samples[0]);
                if (board.GetPiece(move.From) == null)
                {
                    _replyCounts[move] = 99;
                    return 99;
                }

                board.ExecuteMove(move);
                int count = MoveGenerator.GeneratePseudoLegalMoves(board, OpponentColor(_owner._color)).Count;
                _replyCounts[move] = count;
                return count;
            }
        }

        private void LogKingIntent(Move move, Board board)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null || piece.TrueType != PieceType.King || piece.VisibleType == PieceType.King) return;

            var after = board.Clone();
            after.ExecuteMove(move);
            bool attacked = IsSquareAttackedByEnemyVisible(after, move.To);
            Log(attacked
                ? $"[AI] 真王伪装为{piece.VisibleType.ToChinese()}，面对威胁选择留在/顶到火力下装死。"
                : $"[AI] 真王伪装为{piece.VisibleType.ToChinese()}，保持低调，不做夸张逃王。");
        }

        private bool IsOwnKingCapturable(Board board)
        {
            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return true;
            return MoveGenerator.IsSquareAttacked(board, kingPos.Value, _color);
        }

        // 吃王目标的信念概率（非吃子或吃己方返回 0）。
        internal float KingKillProbability(Board board, Move move)
        {
            var target = board.GetPiece(move.To);
            if (target == null || target.Color == _color) return 0f;
            return _beliefState.GetProbability(move.To, PieceType.King);
        }

        // 身份基本锁定的吃王：只有这种才配享受“绝杀分 / 免王安全红线 / 免诱饵复核”。
        internal bool IsConfidentKingCapture(Board board, Move move)
        {
            return KingKillProbability(board, move) >= KingKillCertainThreshold;
        }

        // 这一步走完后，己方真王是否会被敌方下一步直接吃掉（按可能本态悲观估计）。
        // paranoid=true：按“任意敌子可能是任意类型”兜底，用于真王亲自赌王。
        internal override bool WouldLoseOwnKing(Board board, Move move, bool paranoid = false)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return true;

            var undo = fair.ExecuteMoveWithUndo(move);
            var kingPos = fair.FindKing(_color);
            bool lost = paranoid
                ? kingPos.HasValue && IsSquareAttackedByAnyPossibleEnemy(fair, kingPos.Value)
                : IsOwnKingCapturablePessimistic(fair, out _);
            fair.UndoMove(move, undo);
            return lost;
        }

        // 残局判定只用公开信息：棋盘占子数 + 己方真实大子 + 敌方大子的信念期望。
        // 绝不读取敌方隐藏本态。
        internal bool IsEndgamePosition(Board board)
        {
            if (board == null) return false;

            int total = 0;
            int ownHeavy = 0;
            foreach (var (pos, piece) in board.GetAllPieces())
            {
                total++;
                if (piece.Color != _color) continue;
                if (piece.TrueType == PieceType.Queen || piece.TrueType == PieceType.Rook) ownHeavy++;
            }

            float enemyHeavy = 0f;
            foreach (var (pos, _) in board.GetPiecesOfColor(OpponentColor(_color)))
            {
                enemyHeavy += _beliefState.GetProbability(pos, PieceType.Queen);
                enemyHeavy += _beliefState.GetProbability(pos, PieceType.Rook);
            }

            return total <= EndgameMaxPieces || ownHeavy + enemyHeavy < EndgameMaxHeavy - 0.001f;
        }

        // 目标格是否被敌方“可能本态”保护：与王安全红线同一套概率掩码，不偷看真实本态。
        internal bool IsSquareDefendedByBelief(Board board, Position target)
        {
            if (board == null || !target.IsValid) return false;
            PieceColor enemy = OpponentColor(_color);

            for (int d = 0; d < AllDirs.Length; d++)
            {
                int dx = AllDirs[d].X;
                int dy = AllDirs[d].Y;
                int x = target.X + dx;
                int y = target.Y + dy;
                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece != null)
                    {
                        if (piece.Color == enemy && MaskAttacksAlong(piece, dx != 0 && dy != 0)) return true;
                        break;
                    }
                    x += dx;
                    y += dy;
                }
            }

            foreach (var offset in KnightOffsets)
            {
                var pos = target + offset;
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.Knight)) return true;
            }

            int pawnDir = enemy == PieceColor.White ? 1 : -1;
            for (int dx = -1; dx <= 1; dx += 2)
            {
                var pos = new Position(target.X + dx, target.Y - pawnDir);
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.Pawn)) return true;
            }

            for (int d = 0; d < AllDirs.Length; d++)
            {
                var pos = target + AllDirs[d];
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.King)) return true;
            }

            return false;
        }

        internal static double WinProbability(double score)
        {
            return 1.0 / (1.0 + Math.Exp(-score / 900.0));
        }

        // 悲观王安全：不信任单个采样世界抽到的敌子本态，按“概率上可能是哪些子”检查真王。
        // 返回 true 表示至少有一种可能本态能在下一步吃到真王；threats 为威胁源数量。
        internal bool IsOwnKingCapturablePessimistic(Board board, out int threats)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            bool r = IsOwnKingCapturablePessimisticCore(board, out threats);
            PerfProbe.Add(ref PerfProbe.PessTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.PessCalls);
            return r;
#else
            return IsOwnKingCapturablePessimisticCore(board, out threats);
#endif
        }

        private bool IsOwnKingCapturablePessimisticCore(Board board, out int threats)
        {
            threats = ScanKingThreats(board, null);
            return threats > 0;
        }

        // 当前正在威胁真王的敌子位置：根节点用它区分“逃王”与“直接拆火力”。
        internal List<Position> FindKingAttackers(Board board)
        {
            var attackers = new List<Position>();
            ScanKingThreats(board, attackers);
            return attackers;
        }

        // 单一扫描源：attackers 非空时顺便记录威胁源坐标，避免两套判定漂移。
        private int ScanKingThreats(Board board, List<Position> attackers)
        {
            if (board == null) return 0;

            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return 0;

            int threats = 0;
            Position king = kingPos.Value;
            PieceColor enemy = OpponentColor(_color);

            // 直线/斜线：每条射线只看第一个敌子，被己方挡子隔开的不算直接威胁。
            for (int d = 0; d < AllDirs.Length; d++)
            {
                int dx = AllDirs[d].X;
                int dy = AllDirs[d].Y;
                bool diagonal = dx != 0 && dy != 0;
                int x = king.X + dx;
                int y = king.Y + dy;

                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece != null)
                    {
                        if (piece.Color == enemy && MaskAttacksAlong(piece, diagonal))
                        {
                            threats++;
                            attackers?.Add(new Position(x, y));
                        }
                        break;
                    }
                    x += dx;
                    y += dy;
                }
            }

            // 马腿不会被阻挡。
            foreach (var offset in KnightOffsets)
            {
                Position pos = king + offset;
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.Knight))
                {
                    threats++;
                    attackers?.Add(pos);
                }
            }

            // 兵只斜吃：按敌方推进方向反推会攻击王格的兵位。
            int pawnDir = enemy == PieceColor.White ? 1 : -1;
            for (int dx = -1; dx <= 1; dx += 2)
            {
                var pos = new Position(king.X + dx, king.Y - pawnDir);
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.Pawn))
                {
                    threats++;
                    attackers?.Add(pos);
                }
            }

            // 贴身的敌子可能是伪装的王。
            for (int d = 0; d < AllDirs.Length; d++)
            {
                Position pos = king + AllDirs[d];
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy && MaskHas(piece, PieceType.King))
                {
                    threats++;
                    attackers?.Add(pos);
                }
            }

            return threats;
        }

        // 期望危险度：把“敌子可能是哪种攻击型”的概率加权求和。
        // 与 threats 计数不同，它能区分“90% 是后”和“4% 可能是马”，
        // 用于全危险兜底时挑出最不坏的一手（而不是按威胁个数随手挑）。
        internal double KingDangerScore(Board board)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            double r = KingDangerScoreCore(board);
            PerfProbe.Add(ref PerfProbe.DangerTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.DangerCalls);
            return r;
#else
            return KingDangerScoreCore(board);
#endif
        }

        private double KingDangerScoreCore(Board board)
        {
            if (board == null) return double.MaxValue;

            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return double.MaxValue;

            Position king = kingPos.Value;
            PieceColor enemy = OpponentColor(_color);
            double danger = 0.0;

            for (int d = 0; d < AllDirs.Length; d++)
            {
                int dx = AllDirs[d].X;
                int dy = AllDirs[d].Y;
                bool diagonal = dx != 0 && dy != 0;
                for (int x = king.X + dx, y = king.Y + dy; x >= 0 && x < 8 && y >= 0 && y < 8; x += dx, y += dy)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;

                    var pos = new Position(x, y);
                    if (piece.Color == enemy)
                        danger += AttackProbability(pos, piece, diagonal);
                    break;
                }
            }

            foreach (var offset in KnightOffsets)
            {
                Position pos = king + offset;
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy)
                    danger += TypeProbability(pos, piece, PieceType.Knight);
            }

            int pawnDir = enemy == PieceColor.White ? 1 : -1;
            for (int dx = -1; dx <= 1; dx += 2)
            {
                var pos = new Position(king.X + dx, king.Y - pawnDir);
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy)
                    danger += TypeProbability(pos, piece, PieceType.Pawn);
            }

            for (int d = 0; d < AllDirs.Length; d++)
            {
                Position pos = king + AllDirs[d];
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == enemy)
                    danger += TypeProbability(pos, piece, PieceType.King);
            }

            return danger;
        }

        // 搜索叶节点的“可能本态”王暴露：只看公开掩码，不看概率，也不读 TrueType。
        // 逐格扫描敌方棋子：直达王线记直接威胁，隔一个挡子记潜在线。
        // 滑子必须按方向过滤掩码（直线只认后/车，斜线只认后/象）：合并判断会把纯象
        // 当成同线车，凭空放大王危险；射线版还会漏掉同线多子，实测 50 局回退 2 胜。
        private int MaskKingExposure(Board board, Position king, PieceColor enemy)
        {
            int attackers = 0;
            int latent = 0;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null || piece.Color != enemy) continue;
                    int mask = PieceMask(piece);
                    if (mask == 0) continue;

                    int dx = king.X - x;
                    int dy = king.Y - y;
                    int adx = Math.Abs(dx);
                    int ady = Math.Abs(dy);

                    if ((mask & (1 << (int)PieceType.Knight)) != 0 &&
                        ((adx == 1 && ady == 2) || (adx == 2 && ady == 1)))
                    {
                        attackers++;
                        continue;
                    }

                    int pawnDir = enemy == PieceColor.White ? 1 : -1;
                    if ((mask & (1 << (int)PieceType.Pawn)) != 0 &&
                        dy == pawnDir && adx == 1)
                    {
                        attackers++;
                        continue;
                    }

                    if ((mask & (1 << (int)PieceType.King)) != 0 && adx <= 1 && ady <= 1)
                    {
                        attackers++;
                        continue;
                    }

                    bool straight = dx == 0 || dy == 0;
                    bool diagonal = adx == ady;
                    if (!straight && !diagonal) continue;

                    int sliderMask = diagonal ? SliderDiagonalMask : SliderStraightMask;
                    if ((mask & sliderMask) == 0) continue;

                    int stepX = Math.Sign(dx);
                    int stepY = Math.Sign(dy);
                    int blockers = 0;
                    for (int cx = x + stepX, cy = y + stepY;
                         cx != king.X || cy != king.Y;
                         cx += stepX, cy += stepY)
                    {
                        if (board.GetPiece(cx, cy) != null) blockers++;
                    }

                    if (blockers == 0) attackers++;
                    else if (blockers == 1) latent++;
                }
            }

            if (attackers == 0 && latent == 0) return 0;
            int exposure = attackers * MaskKingAttackerPenalty + latent * MaskKingLatentPenalty;
            return Math.Min(MaskKingExposureCap, exposure);
        }

        private static int PieceMask(Piece piece)
        {
            int mask = piece.PossibleTypeMask;
            return mask != 0 ? mask : piece.BeliefTypeMask;
        }

        private double AttackProbability(Position pos, Piece piece, bool diagonal)
        {
            double prob = TypeProbability(pos, piece, PieceType.Queen);
            return diagonal
                ? prob + TypeProbability(pos, piece, PieceType.Bishop)
                : prob + TypeProbability(pos, piece, PieceType.Rook);
        }

        private double TypeProbability(Position pos, Piece piece, PieceType type)
        {
            // 信念里没有该子时不能当“安全”，按未知本态等概率处理。
            int mask = piece.BeliefTypeMask;
            if (mask == 0) return 1.0 / 6.0;

            // 走子几何已经排除的本态绝不计入威胁；其余在可能集内条件化。
            if ((mask & (1 << (int)type)) == 0) return 0.0;

            double inSet = 0.0;
            for (int t = 1; t < 7; t++)
            {
                if ((mask & (1 << t)) == 0) continue;
                double p = _beliefState.GetProbability(pos, (PieceType)t);
                if (!double.IsNaN(p) && p > 0.0) inSet += p;
            }

            // 计数近似把质量挤到可能集之外时，按集内均匀处理——不能因此把真威胁当不存在。
            if (inSet < 0.02)
            {
                int count = 0;
                for (int t = 1; t < 7; t++)
                    if ((mask & (1 << t)) != 0) count++;
                return count > 0 ? 1.0 / count : 0.0;
            }

            double prob = _beliefState.GetProbability(pos, type);
            if (double.IsNaN(prob) || prob < 0.0) return 0.0;
            return prob / inSet;
        }

        // 二步王威胁：敌子先吃掉我方一颗挡子，再从落点攻击王格。
        // 直接扫描（KingDangerScore）只看“当前是否已瞄准王格”，漏掉这种“吃子转向”的杀法：
        //   G20：伪装成兵的真车 f7->f2，再吃 f1 象，落到 f1 后沿一横排吃 d1 的王；
        //   G42：同一套路吃 g2 象，落到 g2 后沿 g 线吃易位到 g1 的王。
        // 判定只用信念概率（不读真实本态）；中间还隔我们一手（可逃/可反吃），由调用方折半计权。
        internal double ConditionalKingDanger(Board board)
        {
            if (board == null) return 0.0;

            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return 0.0;

            Position king = kingPos.Value;
            PieceColor enemy = OpponentColor(_color);

            var ownPieces = new List<(Position pos, Piece piece)>();
            foreach (var (pos, piece) in board.GetPiecesOfColor(_color))
                if (!pos.Equals(king)) ownPieces.Add((pos, piece));

            double danger = 0.0;
            foreach (var (from, piece) in board.GetPiecesOfColor(enemy))
            {
                if (from.Equals(king) || ownPieces.Count == 0) continue;

                double pieceDanger = 0.0;
                for (int t = 1; t < 7; t++)
                {
                    var type = (PieceType)t;
                    double prob = TypeProbability(from, piece, type);
                    if (prob <= 0.0) continue;

                    foreach (var (target, _) in ownPieces)
                    {
                        if (!CouldAttackKingFrom(target, king, type, enemy)) continue;
                        if (!MoveGenerator.CanPieceAttackSquare(board, from, type, enemy, target)) continue;
                        if (!MoveGenerator.CanPieceAttackSquare(board, target, type, enemy, king)) continue;
                        pieceDanger += prob;
                        break;
                    }
                }

                danger += pieceDanger;
            }

            return danger;
        }

        // 该兵种若站在 target 格，几何上能不能攻击到王格（只做形状预筛，阻挡由走法生成器判定）。
        private static bool CouldAttackKingFrom(Position target, Position king, PieceType type, PieceColor attacker)
        {
            int dx = king.X - target.X;
            int dy = king.Y - target.Y;
            int adx = Math.Abs(dx);
            int ady = Math.Abs(dy);

            switch (type)
            {
                case PieceType.Queen:
                    return (adx == 0 && ady > 0) || (ady == 0 && adx > 0) || (adx == ady && adx > 0);
                case PieceType.Rook:
                    return (adx == 0 && ady > 0) || (ady == 0 && adx > 0);
                case PieceType.Bishop:
                    return adx == ady && adx > 0;
                case PieceType.Knight:
                    return (adx == 1 && ady == 2) || (adx == 2 && ady == 1);
                case PieceType.King:
                    return adx <= 1 && ady <= 1 && (adx + ady) > 0;
                case PieceType.Pawn:
                    int dir = attacker == PieceColor.White ? 1 : -1;
                    return adx == 1 && dy == dir;
                default:
                    return false;
            }
        }

        // 对外的单步安全校验：供 Hybrid 丢弃不安全的 Ponder 缓存答案。
        internal bool IsMoveKingSafe(Board board, Move move)
        {
            if (board == null) return false;

            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            bool unsafeKing = IsOwnKingCapturablePessimistic(fair, out _);
            fair.UndoMove(move, undo);
            return !unsafeKing;
        }

        // 王吃“有保护的诱饵”：目标格走完后仍被敌方可能本态攻击。
        // 王被吃立即终局，所以这类手哪怕期望危险度不高，也不能当安全手用。
        internal bool IsKingBaitCapture(Board board, Move move)
        {
            if (board == null) return false;

            var mover = board.GetPiece(move.From);
            if (mover == null || mover.Color != _color || mover.TrueType != PieceType.King) return false;

            var target = board.GetPiece(move.To);
            if (target == null || target.Color == _color) return false;

            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            bool defended = IsSquareAttackedByMask(fair, move.To, OpponentColor(_color));
            fair.UndoMove(move, undo);
            return defended;
        }

        // 目标格是否被指定方按“可能本态”掩码攻击：王安全判定专用，不读真实本态。
        private bool IsSquareAttackedByMask(Board board, Position target, PieceColor attacker)
        {
            if (board == null || !target.IsValid) return false;

            for (int d = 0; d < AllDirs.Length; d++)
            {
                int dx = AllDirs[d].X;
                int dy = AllDirs[d].Y;
                for (int x = target.X + dx, y = target.Y + dy; x >= 0 && x < 8 && y >= 0 && y < 8; x += dx, y += dy)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;
                    if (piece.Color == attacker && MaskAttacksAlong(piece, dx != 0 && dy != 0)) return true;
                    break;
                }
            }

            foreach (var offset in KnightOffsets)
            {
                var pos = target + offset;
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == attacker && MaskHas(piece, PieceType.Knight)) return true;
            }

            int pawnDir = attacker == PieceColor.White ? 1 : -1;
            for (int dx = -1; dx <= 1; dx += 2)
            {
                var pos = new Position(target.X + dx, target.Y - pawnDir);
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == attacker && MaskHas(piece, PieceType.Pawn)) return true;
            }

            for (int d = 0; d < AllDirs.Length; d++)
            {
                var pos = target + AllDirs[d];
                if (!pos.IsValid) continue;
                var piece = board.GetPiece(pos);
                if (piece != null && piece.Color == attacker && MaskHas(piece, PieceType.King)) return true;
            }

            return false;
        }

        private static bool MaskHas(Piece piece, PieceType type)
        {
            return (piece.BeliefTypeMask & (1 << (int)type)) != 0;
        }

        // 王格沿该方向是否可能被滑子吃到；掩码为空时退化为未知，按不威胁处理。
        private static bool MaskAttacksAlong(Piece piece, bool diagonal)
        {
            int mask = piece.BeliefTypeMask;
            if (mask == 0) return false;

            int queen = 1 << (int)PieceType.Queen;
            if (diagonal)
                return (mask & queen) != 0 || (mask & (1 << (int)PieceType.Bishop)) != 0;

            return (mask & queen) != 0 || (mask & (1 << (int)PieceType.Rook)) != 0;
        }

        private bool AllRootMovesLoseKing(List<Move> rootMoves, Dictionary<Move, int> counts,
            Dictionary<Move, int> kingLossCounts)
        {
            bool anyScored = false;
            foreach (var move in rootMoves)
            {
                int count;
                if (!counts.TryGetValue(move, out count) || count <= 0) continue;

                anyScored = true;
                int loss;
                if (!kingLossCounts.TryGetValue(move, out loss)) loss = 0;
                if (loss < count) return false;
            }
            return anyScored;
        }

        private static int Chebyshev(Position a, Position b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private void LogMove(Move move, Board board, int processed, int requested, int depth)
        {
            var piece = board.GetPiece(move.From);
            string vis = piece != null ? piece.VisibleType.ToChinese() : "?";
            string tru = piece != null ? piece.TrueType.ToChinese() : "?";
            Log($"[AI] 决定走 ({move.From.X},{move.From.Y})->({move.To.X},{move.To.Y}) [{move}]，伪装为：{vis}，实际为：{tru}（采样 {processed}/{requested}，深度 {depth}）");
        }

        // 搜索预算耗尽时 Negamax 只能返回静态估值（没看到对手应手）。这种截断值一旦被当成
        // 根走法评分，就会把“送后手”评成 +2000。标记后由根节点整手作废、整层作废。
        internal volatile bool SearchAborted;

        internal void ResetSearchStats()
        {
            _nodeCount = 0;
            _qNodeCount = 0;
            _qDepthSum = 0;
            SearchAborted = false;
            Array.Clear(_hasKiller, 0, _hasKiller.Length);
            Array.Clear(_history, 0, _history.Length);
        }

        private int Negamax(Board board, int depth, int alpha, int beta, PieceColor side,
            Stopwatch stopwatch, Dictionary<ulong, TTEntry> tt, int ply, List<Move>[] buffers)
        {
            if (stopwatch.ElapsedMilliseconds > SearchTimeBudgetMs)
            {
                SearchAborted = true;
                return Evaluate(board, side);
            }

            TTEntry entry = default;
            bool ttHit = false;
            ulong key = 0UL;
            bool useTt = tt != null && depth >= 2;
            if (useTt)
            {
                key = HashBoard(board, side);
                if (tt.TryGetValue(key, out entry))
                {
                    ttHit = true;
                    if (entry.Depth >= depth)
                    {
                        if (entry.Flag == 0) return entry.Value;
                        if (entry.Flag == 1 && entry.Value >= beta) return entry.Value;
                        if (entry.Flag == 2 && entry.Value <= alpha) return entry.Value;
                    }
                }
            }

            if (depth <= 0)
                return Quiescence(board, alpha, beta, side, stopwatch, ply, 0, buffers);

            _nodeCount++;
#if PERF_PROBE
            PerfProbe.Hit(ref PerfProbe.NegamaxCalls);
#endif

            var moves = buffers[ply];
#if PERF_PROBE
            long genTs = PerfProbe.Now;
            MoveGenerator.GeneratePseudoLegalMoves(board, side, moves);
            PerfProbe.Add(ref PerfProbe.GenerateTicks, PerfProbe.Now - genTs);
            PerfProbe.Hit(ref PerfProbe.GenerateCalls);
#else
            MoveGenerator.GeneratePseudoLegalMoves(board, side, moves);
#endif
            // 王身份未锁定时禁止 AI 在深层追“采样敌王”：那只是信念抽样，不是已知事实，
            // 否则会看见大量虚假 MATE。身份锁定（残局只剩孤王）后必须放行，否则采样王无敌。
            // 对手吃 AI 真王始终保留为终局，否则 AI 会无视王的即时危险。
            if (side == _color)
                RemoveKingCaptures(board, moves);
            if (moves.Count == 0)
                return Evaluate(board, side);

            OrderMoves(board, moves, ply, side);
            if (ttHit && entry.HasBestMove)
                PromoteMove(moves, entry.BestMove);

            int alphaOriginal = alpha;
            int best = -INF;
            Move? bestMove = null;
            bool firstMove = true;
            int quietTried = 0;

            // 王线延伸：暗棋没有“将军”可以终止搜索，对手一步静着把子送到真王格旁（典型是
            // 自己人堵死王路后的马叉），威胁正好落在视野外一层，2 层搜索只看到平静局面。
            // 只在深度 1~2 的濒视界层给这种着法补一层，让下一手的吃王进入静态搜索。
            Position? extendKing = KingLineExtensionEnabled && depth >= 1 && depth <= 2 && side != _color
                ? board.FindKing(_color)
                : (Position?)null;

            foreach (var move in moves)
            {
                if (stopwatch.ElapsedMilliseconds > SearchTimeBudgetMs)
                {
                    SearchAborted = true;
                    break;
                }

                var undo = board.ExecuteMoveWithUndo(move);
                int childDepth = depth - 1;                if (extendKing.HasValue && undo.Captured == null)
                {
                    var moved = board.GetPiece(move.To);
                    if (moved != null && moved.Color == side &&
                        MoveGenerator.CanPieceAttackSquare(board, move.To, moved.TrueType, moved.Color, extendKing.Value))
                        childDepth = depth;
                }

                // 分级 LMR：零窗口节点里，第 5 个及以后的安静走法先减 1 层，第 9 个起在深节点减 2 层，
                // 失败立即全宽重搜。攻击敌王格的走法不减，避免又把“叉王/吃王”威胁压回视野外。
                int reduction = 0;
                if (UseLateMoveReduction && beta - alpha == 1 && depth >= 3 &&
                    undo.Captured == null && !move.PromotionType.HasValue &&
                    childDepth == depth - 1 && quietTried >= 4 && childDepth > 1)
                {
                    var enemyKing = board.FindKing(OpponentColor(side));
                    var movedPiece = board.GetPiece(move.To);
                    bool attacksEnemyKing = enemyKing.HasValue && movedPiece != null &&
                        MoveGenerator.CanPieceAttackSquare(board, move.To, movedPiece.TrueType, side, enemyKing.Value);
                    if (!attacksEnemyKing)
                        reduction = (quietTried >= 8 && depth >= 5) ? 2 : 1;
                }
                int score;
                if (undo.Captured != null && undo.Captured.TrueType == PieceType.King)
                {
                    score = MATE - ply;
                    if (KingDebug)
                        Log($"[KDBG] negamax 吃王 ply={ply} side={side} move={move} capturedPos={move.To}");
                }
                else if (firstMove)
                {
                    score = -Negamax(board, childDepth, -beta, -alpha,
                        OpponentColor(side), stopwatch, tt, ply + 1, buffers);
                }
                else
                {
                    // PVS：零窗口先确认“这步不可能优于当前最优”，失败才用全窗口重搜一次。
                    int searchDepth = childDepth - reduction;
                    score = -Negamax(board, searchDepth, -(alpha + 1), -alpha,
                        OpponentColor(side), stopwatch, tt, ply + 1, buffers);
                    if (score > alpha && (reduction > 0 || score < beta))
                        score = -Negamax(board, childDepth, -beta, -alpha,
                            OpponentColor(side), stopwatch, tt, ply + 1, buffers);
                }
                board.UndoMove(move, undo);
                if (undo.Captured == null && !move.PromotionType.HasValue) quietTried++;
                firstMove = false;

                if (score > best)
                {
                    best = score;
                    bestMove = move;
                }
                if (best > alpha) alpha = best;
                if (alpha >= beta)
                {
                    RecordCutoff(move, undo.Captured != null, ply, side, depth);
                    break;
                }
            }

            if (best == -INF)
                best = Evaluate(board, side);

            if (useTt && stopwatch.ElapsedMilliseconds <= SearchTimeBudgetMs && Math.Abs(best) < MATE - 100)
            {
                byte flag = best <= alphaOriginal ? (byte)2 : (best >= beta ? (byte)1 : (byte)0);
                tt[key] = new TTEntry
                {
                    Depth = depth,
                    Value = best,
                    Flag = flag,
                    BestMove = bestMove ?? default,
                    HasBestMove = bestMove.HasValue
                };
            }

            return best;
        }

        private static void PromoteMove(List<Move> moves, Move move)
        {
            int index = moves.IndexOf(move);
            if (index <= 0) return;

            var first = moves[index];
            moves.RemoveAt(index);
            moves.Insert(0, first);
        }

        private int Quiescence(Board board, int alpha, int beta, PieceColor side,
            Stopwatch stopwatch, int ply, int qDepth, List<Move>[] buffers)
        {
            _qNodeCount++;
            _qDepthSum += qDepth;
#if PERF_PROBE
            PerfProbe.Hit(ref PerfProbe.QNodeCalls);
#endif

            int standPat = Evaluate(board, side);
            if (qDepth >= MaxQuiescenceDepth) return standPat;
            if (stopwatch.ElapsedMilliseconds > SearchTimeBudgetMs)
            {
                SearchAborted = true;
                return standPat;
            }

            if (standPat >= beta) return beta;

            // Delta 剪枝：就算下一手白吃对方最大的子也够不到 alpha，这棵子树可以整体放弃。
            if (standPat + 900 + 200 < alpha) return standPat;
            if (standPat > alpha) alpha = standPat;

            var buffer = buffers[ply + qDepth];
            MoveGenerator.GeneratePseudoLegalMoves(board, side, buffer);
            bool ownKingCaptureAllowed =
                side == _color && _beliefState.IsKingIdentityKnown(KingCertainThreshold);
            int write = 0;
            for (int i = 0; i < buffer.Count; i++)
            {
                var m = buffer[i];
                var target = board.GetPiece(m.To);
                if (side == _color && !ownKingCaptureAllowed && target != null &&
                    target.TrueType == PieceType.King)
                    continue;
                if ((target != null && target.Color != side) || m.PromotionType.HasValue)
                    buffer[write++] = m;
            }
            if (write < buffer.Count) buffer.RemoveRange(write, buffer.Count - write);
            OrderMoves(board, buffer, ply + qDepth, side);

            foreach (var move in buffer)
            {
                if (stopwatch.ElapsedMilliseconds > SearchTimeBudgetMs)
                {
                    SearchAborted = true;
                    break;
                }

                var target = board.GetPiece(move.To);
                if (target != null && standPat + target.TrueType.GetValue() + 200 < alpha)
                    continue;

                var undo = board.ExecuteMoveWithUndo(move);
                int score;
                if (undo.Captured != null && undo.Captured.TrueType == PieceType.King)
                    score = MATE - ply;
                else if (side == _color && IsOwnKingCapturablePessimistic(board, out _))
                {
                    // 静态搜索也不许“贪一口”：吃完把真王交出去，直接截断为负绝杀。
                    score = -MATE + ply;
                }
                else
                    score = -Quiescence(board, -beta, -alpha,
                        OpponentColor(side), stopwatch, ply, qDepth + 1, buffers);
                board.UndoMove(move, undo);

                if (score > alpha) alpha = score;
                if (alpha >= beta)
                {
                    RecordCutoff(move, undo.Captured != null, ply + qDepth, side, 1);
                    break;
                }
            }

            return alpha;
        }

        // 信念几乎确定那里就是王时，深层也必须允许吃：否则采样王在搜索里变成无敌，
        // 会贴身逼死真王，整棵子树都读成负绝杀（残局 K+Q 巡逻就是这个假象）。
        // 概率不足时仍然剪掉，避免把“也许是王”当成真绝杀去赌博。
        private void RemoveKingCaptures(Board board, List<Move> moves)
        {
            bool identityKnown = _beliefState.IsKingIdentityKnown(KingCertainThreshold);
            int write = 0;
            for (int i = 0; i < moves.Count; i++)
            {
                var target = board.GetPiece(moves[i].To);
                if (target != null && target.TrueType == PieceType.King && !identityKnown)
                    continue;
                moves[write++] = moves[i];
            }
            if (write < moves.Count) moves.RemoveRange(write, moves.Count - write);
        }

        private void OrderMoves(Board board, List<Move> moves, int ply, PieceColor side)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            OrderMovesCore(board, moves, ply, side);
            PerfProbe.Add(ref PerfProbe.OrderTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.OrderCalls);
#else
            OrderMovesCore(board, moves, ply, side);
#endif
        }

        private void OrderMovesCore(Board board, List<Move> moves, int ply, PieceColor side)
        {
            int count = moves.Count;
            if (count <= 1) return;

            // 每个走法只算一次排序分：插入排序里反复调用 MoveOrderScore 会重复扫盘，是热点。
            Span<int> scores = stackalloc int[count];
            for (int i = 0; i < count; i++)
                scores[i] = MoveOrderScore(board, moves[i], ply, side);

            for (int i = 1; i < count; i++)
            {
                Move current = moves[i];
                int currentScore = scores[i];
                int j = i - 1;

                while (j >= 0 && scores[j] < currentScore)
                {
                    moves[j + 1] = moves[j];
                    scores[j + 1] = scores[j];
                    j--;
                }

                moves[j + 1] = current;
                scores[j + 1] = currentScore;
            }
        }

        // 吃子/升变/易位优先；安静走法按“杀手走法 → 历史得分”排序，让 alpha-beta 更早剪枝。
        private int MoveOrderScore(Board board, Move move, int ply, PieceColor side)
        {
            int score = QuickMoveOrderScore(board, move);
            if (score > 0) return score;

            if (ply >= 0 && ply < MaxPly)
            {
                if (_hasKiller[ply, 0] && _killers[ply, 0].Equals(move)) return 90000;
                if (_hasKiller[ply, 1] && _killers[ply, 1].Equals(move)) return 85000;
            }

            return Math.Min(80000, _history[HistoryIndex(side, move)] / 8);
        }

        private static int HistoryIndex(PieceColor side, Move move)
        {
            int from = move.From.Y * 8 + move.From.X;
            int to = move.To.Y * 8 + move.To.X;
            return ((side == PieceColor.White ? 0 : 1) * 64 + from) * 64 + to;
        }

        private void RecordCutoff(Move move, bool wasCapture, int ply, PieceColor side, int depth)
        {
            if (wasCapture || move.PromotionType.HasValue || ply < 0 || ply >= MaxPly) return;

            if (!_hasKiller[ply, 0] || !_killers[ply, 0].Equals(move))
            {
                _killers[ply, 1] = _killers[ply, 0];
                _hasKiller[ply, 1] = _hasKiller[ply, 0];
                _killers[ply, 0] = move;
                _hasKiller[ply, 0] = true;
            }

            int index = HistoryIndex(side, move);
            if (_history[index] < 1 << 20)
                _history[index] += depth * depth;
        }

        // 深层节点只做便宜的吃子/升变排序，避免每个比较都扫全盘。
        private static int QuickMoveOrderScore(Board board, Move move)
        {
            int score = 0;
            var target = board.GetPiece(move.To);
            if (target != null)
            {
                int attacker = board.GetPiece(move.From)?.TrueType.GetValue() ?? 100;
                score += 100000 + target.TrueType.GetValue() * 10 - attacker;
            }
            if (move.PromotionType.HasValue)
                score += 80000 + move.PromotionType.Value.GetValue();
            if (move.IsCastling) score += 1500;
            return score;
        }

        // 根节点排序额外考虑：疑似王目标、受威胁后撤离、落入火力后反吃、走完后直接攻击王。
        internal void OrderRootMoves(Board board, List<Move> moves, Dictionary<Move, double> previousAverages)
        {
            var scored = new List<(Move move, double previous, int order)>(moves.Count);
            for (int i = 0; i < moves.Count; i++)
            {
                double previous;
                if (!previousAverages.TryGetValue(moves[i], out previous))
                    previous = double.NegativeInfinity;
                scored.Add((moves[i], previous, RootMoveOrderScore(board, moves[i])));
            }

            scored.Sort((a, b) =>
            {
                int byDepth = b.previous.CompareTo(a.previous);
                return byDepth != 0 ? byDepth : b.order.CompareTo(a.order);
            });

            for (int i = 0; i < scored.Count; i++)
                moves[i] = scored[i].move;
        }

        private int RootMoveOrderScore(Board board, Move move)
        {
            int score = QuickMoveOrderScore(board, move);
            var target = board.GetPiece(move.To);
            var piece = board.GetPiece(move.From);

            if (target != null && target.Color != _color)
            {
                float pKing = _beliefState.GetProbability(move.To, PieceType.King);
                if (pKing > 0.05f)
                    score += (int)(pKing * KingCertainWeight * 2f);
            }

            if (piece == null) return score;

            bool fromExposed = IsSquareAttackedByEnemyVisible(board, move.From);
            bool toExposed = IsSquareAttackedByEnemyVisible(board, move.To);
            if (fromExposed && !toExposed) score += 300;
            if (toExposed && target != null && target.Color != _color) score += 400;

            var enemyKing = board.FindKing(OpponentColor(_color));
            if (enemyKing.HasValue &&
                MoveGenerator.CanPieceAttackSquare(board, move.To, piece.TrueType, piece.Color, enemyKing.Value))
                score += 800;

            return score;
        }

        // 供 Hybrid 的 MCTS rollout 复用同一套静态评估，输出与 MCTS 叶子节点一致的胜率尺度。
        internal double EvaluateProbability(Board board, PieceColor forColor)
        {
            int centipawns = Evaluate(board, forColor);
            return 1.0 / (1.0 + Math.Exp(-centipawns / 900.0));
        }

        private int Evaluate(Board board, PieceColor forColor)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            int r = EvaluateCore(board, forColor);
            PerfProbe.Add(ref PerfProbe.EvalTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.EvalCalls);
            return r;
#else
            return EvaluateCore(board, forColor);
#endif
        }

        private int EvaluateCore(Board board, PieceColor forColor)
        {
            Position? ownKing = null;
            Position? enemyKing = null;
            int score = 0;
            int myMaterial = 0;
            int enemyMaterial = 0;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;

                    var pos = new Position(x, y);
                    if (piece.TrueType == PieceType.King)
                    {
                        if (piece.Color == forColor) ownKing = pos;
                        else enemyKing = pos;
                    }

                    // 王不是物质：它的真实价值只体现在“被吃即终局”，不能按 20000 卷入常规估值。
                    // 伪装也绝不改变这条铁律：王就是王，不按兵/其它子的价值参与交易。
                    int value = piece.TrueType == PieceType.King ? 0 : piece.TrueType.GetValue();
                    int pst = GetPieceSquareValue(piece.TrueType, pos, piece.Color);

                    // 残局专项：兵冲升变、王向中心靠拢。只在残局模式启用，避免中局鼓励孤王乱跑。
                    int endgameTerm = 0;
                    if (EndgameEvalActive)
                    {
                        if (piece.TrueType == PieceType.Pawn)
                        {
                            int promoDist = piece.Color == PieceColor.White ? 7 - y : y;
                            endgameTerm += (7 - promoDist) * 10;
                            if (promoDist == 1) endgameTerm += 150;
                            else if (promoDist == 2) endgameTerm += 60;
                        }
                        else if (piece.TrueType == PieceType.King)
                        {
                            double centerDist = Math.Abs(x - 3.5) + Math.Abs(y - 3.5);
                            endgameTerm += (int)Math.Round((7.0 - centerDist) * 10.0);
                        }
                    }

                    if (piece.Color == forColor)
                    {
                        score += value + pst + endgameTerm;
                        if (piece.TrueType != PieceType.King) myMaterial += value;
                        // 伪装收益只给普通子；给王发“伪装奖励”会鼓励王去冒险。
                        if (piece.TrueType != piece.VisibleType && piece.TrueType != PieceType.King) score += 12;
                        if (piece.TrueType == PieceType.Pawn)
                            score += (piece.Color == PieceColor.White ? y : 7 - y) * 6;
                    }
                    else
                    {
                        score -= value + pst + endgameTerm;
                        if (piece.TrueType != PieceType.King) enemyMaterial += value;
                    }

                    // 踩在“受威胁却不逃”的陷阱格上，随时可能被反吃。
                    if (piece.Color == _color)
                    {
#if PERF_PROBE
                        long trapTs = PerfProbe.Now;
                        float trapRisk = _beliefState.GetTrapRisk(pos);
                        PerfProbe.Add(ref PerfProbe.TrapTicks, PerfProbe.Now - trapTs);
                        PerfProbe.Hit(ref PerfProbe.TrapCalls);
#else
                        float trapRisk = _beliefState.GetTrapRisk(pos);
#endif
                        if (trapRisk > 0.01f)
                        {
                            int trapLoss = (int)(trapRisk * Math.Min(value, 900) * 0.45f);
                            score += forColor == _color ? -trapLoss : trapLoss;
                        }
                    }
                }
            }

            if (!ownKing.HasValue) return -MATE;
            if (!enemyKing.HasValue) return MATE;

            // 王区攻击只扫一次：残局数逃格与下面的王安全项共用同一份 3×3 掩码。
            ScanKingZones(board, ownKing.Value, enemyKing.Value, forColor,
                out bool kingAttacked, out int aroundKing,
                out bool enemyKingAttacked, out int aroundEnemyKing,
                out int ownZoneMask, out int enemyZoneMask);

            // 残局收网：从“物质占优方”的视角统一计算，再按 forColor 取符号。
            // v13/v15 实测 K+Q 对单王拖到 240 步：旧评估给“攻击敌王所在格”每步 +2400，
            // 皇后于是不停追着王将军，自己的王永远停在棋盘另一侧（靠近项只值几十）。
            // 这里改成“逃格”口径：敌王安全相邻格越少越接近绝杀，同时禁止贴脸送王。
            if (EndgameEvalActive)
            {
                int diff = myMaterial - enemyMaterial;
                if (Math.Abs(diff) >= 300)
                {
                    bool strongIsMe = diff > 0;
                    Position strongKing = strongIsMe ? ownKing.Value : enemyKing.Value;
                    Position weakKing = strongIsMe ? enemyKing.Value : ownKing.Value;
                    PieceColor strongColor = strongIsMe ? forColor : OpponentColor(forColor);

                    int edge = Math.Min(Math.Min(weakKing.X, 7 - weakKing.X),
                                        Math.Min(weakKing.Y, 7 - weakKing.Y));
                    int manhattan = Math.Abs(strongKing.X - weakKing.X) +
                                    Math.Abs(strongKing.Y - weakKing.Y);
                    int cheb = Math.Max(Math.Abs(strongKing.X - weakKing.X),
                                        Math.Abs(strongKing.Y - weakKing.Y));

                    int mop = (3 - edge) * 45;
                    mop += (14 - manhattan) * 12;
                    // 无将军规则：两王贴脸就是把自己的王送到对方嘴里。
                    if (cheb <= 1) mop -= 2500;
                    else if (cheb == 2) mop -= 150;

                    if (Math.Abs(diff) >= 500)
                    {
                        int exits = CountKingExits(board, weakKing, strongColor,
                            strongIsMe ? enemyZoneMask : ownZoneMask);
                        mop += (8 - exits) * 40;
                        if (exits == 0) mop += 2500;
                    }

                    score += strongIsMe ? mop : -mop;
                }
            }

            var ownKingPiece = board.GetPiece(ownKing.Value);
            bool disguisedKing = ownKingPiece != null && ownKingPiece.VisibleType != PieceType.King;

            // 悲观王安全：按“被攻击的概率期望”平滑扣分，而不是一刀切归零。
            // 真被咬住时仍然重罚（上限 1800，相当于两个后），但 4% 的幻影只扣小分，
            // 保证劣势/危险局面下不同走法仍可比较，迭代加深不会因为全部同分而“摆烂”。
            double exposureDanger = KingDangerScore(board);
            if (exposureDanger > 0.001)
            {
                int exposurePenalty = (int)Math.Round(KingExposurePenalty * Math.Min(1.0, exposureDanger));
                score += forColor == _color ? -exposurePenalty : exposurePenalty;
            }

            // 采样世界抽不到的本态也要计威胁：王线暴露按公开可能集悲观计。
            // 这一项不读概率、不读 TrueType，只回答“是否存在一种公开信息允许的本态能打到王”。
            // 必须按 forColor 的己方王 + 对方棋子来算：对手视角节点同样要为对手自己的王扣分。
            int maskExposure = MaskKingExposure(board, ownKing.Value, OpponentColor(forColor));
            if (maskExposure > 0)
                score -= maskExposure;

            // 真王暴露在火力下是重大损失；但同样不做成一刀切，保留与其他走法的可比性。
            if (kingAttacked) score -= 900;
            score -= aroundKing * (disguisedKing ? 30 : 100);
            int shield = EvaluateKingShield(board, ownKing.Value, forColor);
            score += EndgameEvalActive ? (int)Math.Round(shield * 0.5) : shield;

            if (disguisedKing && !kingAttacked)
            {
                // 伪装王的价值一半在“骗”，但只有王安全时才成立；
                // 被攻击时不再给予伪装补偿，避免鼓励王顶着火力装死。
                if (NearestEnemyHeavyDistance(board, ownKing.Value, forColor) <= 2) score += 60;
            }

            // 王的动态概率期望：几乎确定是王时放大围猎压力，模糊时只给温和收益。
            float enemyKingCertainty = enemyKing.HasValue
                ? _beliefState.GetProbability(enemyKing.Value, PieceType.King)
                : 0f;
            int kingPressure = enemyKingCertainty > KingCertainThreshold
                ? 2400
                : enemyKingCertainty >= KingMildThreshold ? 900 : 300;
            // 残局大幅领先时，“攻击敌王所在格”本身没有收益：无将军规则，单王每步都能躲，
            // 追将军只会把皇后留在远处巡逻。真正的收益是收紧逃格（收网项已计）。
            if (EndgameEvalActive && myMaterial - enemyMaterial >= 500)
                kingPressure = Math.Min(kingPressure, 300);
            if (enemyKingAttacked) score += kingPressure;
            score += aroundEnemyKing * (enemyKingCertainty > KingCertainThreshold ? 100 : 40);

            return score;
        }

        // 弱方王的“逃格”：相邻格中，既没有强方占位、也没有被强方子力攻击的格子数量。
        // 强方自己占住的格子若无人保护，弱方王可以吃子逃走，同样算逃格。
        // zoneMask 直接取自 ScanKingZones 的同一趟扫描（bit 布局与 MarkZone 一致）。
        private int CountKingExits(Board board, Position king, PieceColor strongColor, int zoneMask)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            int r = CountKingExitsCore(board, king, strongColor, zoneMask);
            PerfProbe.Add(ref PerfProbe.ExitTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.ExitCalls);
            return r;
#else
            return CountKingExitsCore(board, king, strongColor, zoneMask);
#endif
        }

        private int CountKingExitsCore(Board board, Position king, PieceColor strongColor, int zoneMask)
        {
            int exits = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var pos = new Position(king.X + dx, king.Y + dy);
                    if (!pos.IsValid) continue;

                    int occupantColor = 0;
                    var occupant = board.GetPiece(pos);
                    if (occupant != null) occupantColor = occupant.Color == strongColor ? 1 : -1;

                    if (occupantColor < 0) continue; // 弱方自己的子挡路
                    bool attacked = (zoneMask & (1 << ((dy + 1) * 3 + (dx + 1)))) != 0;
                    if (occupantColor > 0)
                    {
                        if (!attacked) exits++; // 无人保护，可以吃子逃走
                        continue;
                    }
                    if (!attacked) exits++;
                }
            }
            return exits;
        }

        private static void ScanKingZones(Board board, Position ownKing, Position enemyKing, PieceColor forColor,
            out bool ownAttacked, out int ownAround, out bool enemyAttacked, out int enemyAround,
            out int ownZoneMask, out int enemyZoneMask)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            ScanKingZonesCore(board, ownKing, enemyKing, forColor,
                out ownAttacked, out ownAround, out enemyAttacked, out enemyAround,
                out ownZoneMask, out enemyZoneMask);
            PerfProbe.Add(ref PerfProbe.ZoneTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.ZoneCalls);
#else
            ScanKingZonesCore(board, ownKing, enemyKing, forColor,
                out ownAttacked, out ownAround, out enemyAttacked, out enemyAround,
                out ownZoneMask, out enemyZoneMask);
#endif
        }

        private static void ScanKingZonesCore(Board board, Position ownKing, Position enemyKing, PieceColor forColor,
            out bool ownAttacked, out int ownAround, out bool enemyAttacked, out int enemyAround,
            out int ownZoneMask, out int enemyZoneMask)
        {
            bool ownHit = false;
            bool enemyHit = false;
            int ownMask = 0;
            int enemyMask = 0;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;

                    var from = new Position(x, y);
                    if (piece.Color != forColor)
                        ScanPieceAttacks(board, from, piece, ownKing, ref ownMask, ref ownHit);
                    else
                        ScanPieceAttacks(board, from, piece, enemyKing, ref enemyMask, ref enemyHit);
                }
            }

            ownAround = CountBits(ownMask);
            enemyAround = CountBits(enemyMask);
            ownAttacked = ownHit;
            enemyAttacked = enemyHit;
            ownZoneMask = ownMask;
            enemyZoneMask = enemyMask;
        }

        private static void ScanPieceAttacks(Board board, Position from, Piece piece, Position king,
            ref int mask, ref bool attacked)
        {
            switch (piece.TrueType)
            {
                case PieceType.Pawn:
                {
                    int dir = piece.Color == PieceColor.White ? 1 : -1;
                    MarkZone(king, from.X - 1, from.Y + dir, ref mask, ref attacked);
                    MarkZone(king, from.X + 1, from.Y + dir, ref mask, ref attacked);
                    break;
                }
                case PieceType.Knight:
                    foreach (var offset in KnightOffsets)
                        MarkZone(king, from.X + offset.X, from.Y + offset.Y, ref mask, ref attacked);
                    break;
                case PieceType.King:
                    foreach (var offset in KingOffsets)
                        MarkZone(king, from.X + offset.X, from.Y + offset.Y, ref mask, ref attacked);
                    break;
                case PieceType.Bishop:
                    if (!SliderMayReachZone(from, king, PieceType.Bishop)) break;
                    ScanRaysToZone(board, from, BishopDirs, king, ref mask, ref attacked);
                    break;
                case PieceType.Rook:
                    if (!SliderMayReachZone(from, king, PieceType.Rook)) break;
                    ScanRaysToZone(board, from, RookDirs, king, ref mask, ref attacked);
                    break;
                case PieceType.Queen:
                    if (!SliderMayReachZone(from, king, PieceType.Queen)) break;
                    ScanRaysToZone(board, from, AllDirs, king, ref mask, ref attacked);
                    break;
            }
        }

        // 王周围 3×3 区域内任一格若在 sliders 的射线上，则“同行/同列/同斜线”必成立；
        // 不满足几何条件的远子直接跳过，省掉整条射线扫描（结果完全一致）。
        private static bool SliderMayReachZone(Position from, Position king, PieceType type)
        {
            int dx = from.X - king.X;
            int dy = from.Y - king.Y;
            int adx = Math.Abs(dx);
            int ady = Math.Abs(dy);

            if ((type == PieceType.Rook || type == PieceType.Queen) && (adx <= 1 || ady <= 1))
                return true;

            if (type == PieceType.Bishop || type == PieceType.Queen)
            {
                int diff = Math.Abs(dx - dy);
                int sum = Math.Abs(dx + dy);
                if (diff <= 2 || sum <= 2) return true;
            }

            return false;
        }

        private static int CountBits(int value)
        {
            int count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }
            return count;
        }

        private static void MarkZone(Position king, int x, int y, ref int mask, ref bool attacked)
        {
            int dx = x - king.X;
            int dy = y - king.Y;
            if (dx < -1 || dx > 1 || dy < -1 || dy > 1) return;
            if (dx == 0 && dy == 0)
            {
                attacked = true;
                return;
            }
            mask |= 1 << ((dy + 1) * 3 + (dx + 1));
        }

        private static void ScanRaysToZone(Board board, Position from, Position[] dirs, Position king,
            ref int mask, ref bool attacked)
        {
            foreach (var dir in dirs)
            {
                int x = from.X + dir.X;
                int y = from.Y + dir.Y;
                while (x >= 0 && x < 8 && y >= 0 && y < 8)
                {
                    MarkZone(king, x, y, ref mask, ref attacked);
                    if (board.GetPiece(x, y) != null) break;
                    x += dir.X;
                    y += dir.Y;
                }
            }
        }

        private static int EvaluateKingShield(Board board, Position king, PieceColor color)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            int r = EvaluateKingShieldCore(board, king, color);
            PerfProbe.Add(ref PerfProbe.ShieldTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.ShieldCalls);
            return r;
#else
            return EvaluateKingShieldCore(board, king, color);
#endif
        }

        private static int EvaluateKingShieldCore(Board board, Position king, PieceColor color)
        {
            int shield = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var pos = new Position(king.X + dx, king.Y + dy);
                    if (!pos.IsValid) continue;
                    var piece = board.GetPiece(pos);
                    if (piece == null || piece.Color != color) continue;
                    // 伪装子贴身“护驾”更有欺骗性，不会一看就知道王在哪。
                    shield += piece.VisibleType != piece.TrueType ? 32 : 20;
                }
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

        private int GetDeceptionBonus(Move move, Board board)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null) return 0;

            int bonus = 0;
            bool validForVisible = MoveGenerator.IsValidMoveForPieceType(
                board, move.From, move.To, piece.VisibleType, piece.Color);
            if (validForVisible)
                bonus += 60;

            if (piece.TrueType != piece.VisibleType)
            {
                bonus += 80;

                // 王的价值（20000）不能参与欺诈换算，否则伪装成王的兵会拿到巨额奖励。
                int visibleValue = Math.Min(piece.VisibleType.GetValue(), 900);
                int trueValue = Math.Min(piece.TrueType.GetValue(), 900);
                if (visibleValue > trueValue)
                    bonus += Math.Min(150, (visibleValue - trueValue) / 5);

                // 伪装王走出“这个伪装根本不可能走”的步，会直接暴露身份。
                if (!validForVisible && piece.TrueType == PieceType.King)
                    bonus -= 90;
            }

            return bonus;
        }

        // 真王被咬住时不要只让王“机械躲闪”：让伪装成车/后的普通子主动走进火力，
        // 用一次看起来昂贵的牺牲把玩家的注意力从真王身上引开。
        private int GetDecoySacrificeBonus(Board board, Move move)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null || piece.Color != _color) return 0;
            if (piece.TrueType == PieceType.King || piece.VisibleType == PieceType.King) return 0;
            if (piece.TrueType == piece.VisibleType) return 0;
            if (piece.VisibleType != PieceType.Rook && piece.VisibleType != PieceType.Queen) return 0;

            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return 0;
            var kingPiece = board.GetPiece(kingPos.Value);
            if (kingPiece == null || kingPiece.VisibleType == PieceType.King) return 0;

            var fair = BuildFairCheckBoard(board);
            bool inCheck = IsOwnKingCapturablePessimistic(fair, out _);
            bool heavyNearby = NearestEnemyHeavyDistance(board, kingPos.Value, _color) <= 2;
            if (!inCheck && !heavyNearby) return 0;

            // 必须“看起来值钱、实际上便宜”，这样牺牲才有误导价值。
            int visibleValue = Math.Min(piece.VisibleType.GetValue(), 900);
            int trueValue = Math.Min(piece.TrueType.GetValue(), 900);
            if (visibleValue <= trueValue) return 0;

            if (!IsSquareAttackedByEnemyVisible(fair, move.To)) return 0;

            // 替死不能把真王一起搭进去。
            var after = fair.Clone();
            after.ExecuteMove(move);
            if (IsOwnKingCapturablePessimistic(after, out _)) return 0;

            return Math.Min(320, 140 + (visibleValue - trueValue) / 3);
        }

        private int GetProbeBonus(Move move, Board board)
        {
            var piece = board.GetPiece(move.From);
            var target = board.GetPiece(move.To);
            if (piece == null || target == null || target.Color == piece.Color) return 0;
            if (piece.TrueType != PieceType.Pawn) return 0;

            if (target.VisibleType == PieceType.Queen || target.VisibleType == PieceType.Rook)
            {
                float prob = _beliefState.GetProbability(move.To, target.VisibleType);
                if (prob < 0.35f) return 50;
            }

            return 0;
        }

        // 抽样世界里吃到的“王”在这一世界内就是终局，直接记 MATE 分。
        // 16 个世界取平均后自然得到 P_King × 胜利价值，并在其余世界里保留真实走子收益。
        // 但低概率的“可能王”不能按终局计价：实测 P_King=12% 时，一步 Rxf7 被 2 个
        // 恰好抽到王的样本把平均分拉到 1967/2649，压过了送车的风险，直接被真王吃掉。
        // 低于绝杀阈值的猜测按“普通换大子 + 小幅猎王奖励”结算，不鼓励拿大子赌猜王。
        private int SampleKingCaptureScore(Position at)
        {
            float p = _beliefState.GetProbability(at, PieceType.King);
            if (p >= AIPlayer.KingKillProbabilityThreshold) return MATE - 1;
            return 900 + (int)Math.Round(p * 3000f);
        }

        // 绝杀收益：仅用于日志展示的期望值（p × 胜利价值），不重复计入选择分——
        // 根节点平均分已经按抽样世界包含同一份收益。
        private int GetKingCaptureExpectation(Board board, Move move)
        {
            var target = board.GetPiece(move.To);
            if (target == null || target.Color == _color) return 0;

            float pKing = _beliefState.GetProbability(move.To, PieceType.King);
            if (pKing <= 0.02f) return 0;

            return (int)Math.Round(pKing * MATE);
        }

        // 本局前 10 步内逐步生效，之后按历史样本量饱和；实时画像不依赖 HasHistory 门槛。
        private float LiveProfileWeight()
        {
            var profile = PlayerProfile;
            if (profile == null || profile.Moves <= 0) return 0f;
            return Math.Min(1f, profile.Moves / 10f);
        }

        // 诱饵反杀损失：越不确定是王、越是被玩家主动送进火力的格子，越可能损失进攻子。
        // 玩家历史上越爱弃子，诱饵的可信度越低，惩罚按“疑心系数”放大。
        private int GetTrapExpectedLoss(Board board, Move move)
        {
            var target = board.GetPiece(move.To);
            if (target == null || target.Color == _color) return 0;

            // 吃王优先于防钓鱼：只有身份锁定的吃王才按绝杀处理，损失记 0。
            // 低置信度赌王仍要付诱饵代价，否则“可能是王”会变成免死金牌。
            if (_beliefState.GetProbability(move.To, PieceType.King) >= KingKillCertainThreshold)
                return 0;

            float pKing = _beliefState.GetProbability(move.To, PieceType.King);
            float trapRisk = _beliefState.GetTrapRisk(move.To);

            if (trapRisk <= 0.01f && pKing <= 0.02f) return 0;

            float decoyRisk = Math.Max(trapRisk, pKing < KingCertainThreshold ? (1f - pKing) * 0.5f : 0f);
            if (decoyRisk <= 0.01f) return 0;

            float sacrifice = PlayerProfile != null
                ? PlayerProfile.SacrificeRate * LiveProfileWeight()
                : 0f;
            decoyRisk = Math.Min(1f, decoyRisk * (1f + 0.9f * sacrifice));

            var attacker = board.GetPiece(move.From);
            int attackerValue = attacker != null ? Math.Min(attacker.TrueType.GetValue(), 900) : 400;
            bool recapturable = IsSquareAttackedByEnemyVisible(board, move.To);
            float exposure = recapturable ? 1f : 0.25f;

            return (int)Math.Round(decoyRisk * exposure * attackerValue);
        }

        // 根节点子力暴露：人类知道自己的本态，所以“这格可能被便宜子吃”在人类眼里是确定事件。
        // 采样搜索按信念概率平均，会低估这种不对称；这里按同一套并集算法直接结算，
        // 并在根选择里按风险厌恶系数放大“掉大子”的尾部代价。
        internal double EstimateOwnMaterialExposure(Board board, Move move)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return 0.0;

            var undo = fair.ExecuteMoveWithUndo(move);
            double risk = 0.0;
            PieceColor enemy = OpponentColor(_color);

            foreach (var (pos, piece) in fair.GetPiecesOfColor(_color))
            {
                if (piece.TrueType == PieceType.King) continue;

                // v104：王是唯一保护者时，王吃回本身可能就是诱饵。
                // G7：f7 真车被伪装“兵”的真象吃，根节点因为“王能保护 f7”只记 150 点损失，
                // 实际王吃回后 f 线敌真车立刻吃王——这不是保护，是请君入瓮。
                bool defended = IsSquareAttackedByAI(fair, pos) && !KingDefenseIsTrap(fair, pos);
                double ownValue = Math.Min(piece.TrueType.GetValue(), 900);
                double chance = 0.0;
                int cheapest = int.MaxValue;

                foreach (var (epos, epiece) in fair.GetPiecesOfColor(enemy))
                {
                    int mask = epiece.BeliefTypeMask;
                    if (mask == 0) continue;

                    // 王亲自扑子归王安全红线管，这里只算子力交换（King 不算可交易子）。
                    for (int t = 2; t < 7; t++)
                    {
                        if ((mask & (1 << t)) == 0) continue;
                        var type = (PieceType)t;
                        if (!MoveGenerator.CanPieceAttackSquare(fair, epos, type, enemy, pos)) continue;

                        float p = _beliefState.GetProbability(epos, type);
                        if (p <= 0.001f) continue;

                        int value = Math.Min(type.GetValue(), 900);
                        if (value < cheapest) cheapest = value;
                        // 有保护不等于对手就不吃：人类常用低子硬吃大子，兑掉也赚。
                        // 保护只能降低净损失（后续兑回最便宜攻击子），不能降低被抓的概率。
                        chance += p * (defended ? 1.0 : 0.9);
                    }

                    // 敌方王按“可能本态”走到贴身格白吃我子。它不能兑子：有保护时对方
                    // 绝不会拿王冒险（吃下去就会被反吃、直接输），所以只在无保护时计入。
                    if (!defended && (mask & (1 << (int)PieceType.King)) != 0 &&
                        MoveGenerator.CanPieceAttackSquare(fair, epos, PieceType.King, enemy, pos))
                    {
                        float pk = _beliefState.GetProbability(epos, PieceType.King);
                        if (pk > 0.05f)
                            chance += pk * 0.9;
                    }
                }

                if (chance <= 0.0) continue;
                if (cheapest == int.MaxValue) cheapest = 100;

                // 期望交换：被吃后若有保护，按“兑掉最便宜的攻击子”结算，不再额外把概率打对折。
                // 旧写法 chance×0.5 + swing−(0.7×最便宜子) 把“有保护”重复折价两次，
                // 实测导致马叉后+车时宁可送后：d8 后“被保护”只记 338，c7 后因未知子虚风险反而更贵。
                double swing = defended ? ownValue - cheapest : ownValue;
                if (swing > 0.0)
                    risk += Math.Min(1.0, chance) * swing;
            }

            fair.UndoMove(move, undo);
            return risk;
        }

        // 走完之后车/后是否暴露在“可能本态”的攻击下（并集概率 ≥25%）。
        // 期望搜索按信念折价，掉重子会被平均成噪声；根节点用它做硬约束：
        // 只要存在不挂大子的走法，就不允许主动把车/后送到多面攻击里。
        internal bool HasHangingHeavyPiece(Board board, Move move)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            bool hanging = false;

            foreach (var (pos, piece) in fair.GetPiecesOfColor(_color))
            {
                if (piece.TrueType != PieceType.Queen && piece.TrueType != PieceType.Rook) continue;

                double threshold = piece.TrueType == PieceType.Queen
                    ? QueenHangingThreshold
                    : RookHangingThreshold;
                if (HeavyAttackChance(fair, pos) >= threshold) { hanging = true; break; }
            }

            fair.UndoMove(move, undo);
            return hanging;
        }

        // 某格上的己方子被“敌方可能本态”攻击的概率和（并集近似）。
        // 敌方王不计入：王扑子由王安全红线单独处理，且计入会让几乎所有重子手都被判“挂子”，
        // 50 局对照（v53）平局从 2 涨到 4、胜率跌到 88%。
        private double HeavyAttackChance(Board fair, Position pos)
        {
            PieceColor enemy = OpponentColor(_color);
            double chance = 0.0;
            foreach (var (epos, epiece) in fair.GetPiecesOfColor(enemy))
            {
                int mask = epiece.BeliefTypeMask;
                if (mask == 0) continue;

                for (int t = 2; t < 7; t++)
                {
                    if ((mask & (1 << t)) == 0) continue;
                    var type = (PieceType)t;
                    if (!MoveGenerator.CanPieceAttackSquare(fair, epos, type, enemy, pos)) continue;

                    float p = _beliefState.GetProbability(epos, type);
                    if (p <= 0.001f) continue;
                    chance += p;
                }
            }
            return chance;
        }

        // 王是某格唯一保护者，且还有 ≥2 枚敌子能打到这格：王吃回会撞进第二枚敌子的火力。
        // 只用公开几何（任意敌子按任意类型），不读真实本态。
        private bool KingDefenseIsTrap(Board fair, Position pos)
        {
            var kingPos = fair.FindKing(_color);
            if (!kingPos.HasValue) return false;
            if (!MoveGenerator.CanPieceAttackSquare(fair, kingPos.Value, PieceType.King, _color, pos)) return false;

            foreach (var (p, pc) in fair.GetPiecesOfColor(_color))
            {
                if (p.Equals(kingPos.Value) || p.Equals(pos)) continue;
                if (MoveGenerator.CanPieceAttackSquare(fair, p, pc.TrueType, _color, pos)) return false;
            }

            PieceColor enemy = OpponentColor(_color);
            int attackers = 0;
            foreach (var (epos, _) in fair.GetPiecesOfColor(enemy))
            {
                if (epos.Equals(pos)) continue;
                for (int t = 1; t < 7; t++)
                {
                    if (MoveGenerator.CanPieceAttackSquare(fair, epos, (PieceType)t, enemy, pos))
                    {
                        attackers++;
                        break;
                    }
                }
                if (attackers >= 2) return true;
            }
            return false;
        }

        // 这一手走完后是否仍有真车处于“高置信被吃”状态（救后仲裁用）。
        internal bool CreatesRookLoss(Board board, Move move)
        {
            var fair = BuildFairCheckBoard(board);
            if (fair.GetPiece(move.From) == null) return false;

            var undo = fair.ExecuteMoveWithUndo(move);
            bool loss = false;
            foreach (var (pos, piece) in fair.GetPiecesOfColor(_color))
            {
                if (piece.TrueType != PieceType.Rook) continue;
                if (HeavyAttackChance(fair, pos) >= RookHangingThreshold) { loss = true; break; }
            }
            fair.UndoMove(move, undo);
            return loss;
        }

        // 当前物质差（只算可交易子力）：己方按真实本态，玩家按信念期望，不读取玩家真实本态。
        internal int GetMaterialLead(Board board)
        {
            if (board == null) return 0;

            int own = 0;
            int enemy = 0;
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null || piece.TrueType == PieceType.King) continue;

                    if (piece.Color == _color)
                    {
                        own += piece.TrueType.GetValue();
                        continue;
                    }

                    float expected = _beliefState.GetExpectedMaterialValue(new Position(x, y));
                    enemy += expected > 0.01f
                        ? (int)Math.Round(expected)
                        : (piece.VisibleType == PieceType.King ? 0 : Math.Min(piece.VisibleType.GetValue(), 900));
                }
            }

            return own - enemy;
        }

        // 多疑惩罚：人类不会白白送大子。无保护、非王、吃完也形不成直接杀王线路的送子，
        // 一律按诱饵处理；玩家历史越干净（少弃子、胜率高），这份“太干净”的礼物越可疑。
        private int GetLoneBaitPenalty(Board board, Move move, double searchScore, int rootDepth)
        {
            if (!AIPlayer.EnableBaitPenalty) return 0;

            var target = board.GetPiece(move.To);
            if (target == null || target.Color == _color) return 0;

            // 只对车、后这种“白送大子”起疑；小子被弃多半只是正常战术。
            int value = target.VisibleType.GetValue();
            if (value < 500) return 0;

            // 物质大优时不该再疑神疑鬼：已经吃掉玩家大量棋子，说明对方的“礼物”换不过我们，
            // 此时必须敢于吃子扩大优势，而不是走闲棋把主动权还回去。
            int lead = GetMaterialLead(board);
            if (lead >= 500) return 0;
            float leadScale = lead >= 250 ? 0.4f : 1f;

            // 只有玩家上一步主动走到这里的子才算“送到嘴边”；早已挂着的子更像普通漏着。
            if (!_lastOpponentTo.HasValue || _lastOpponentTo.Value != move.To) return 0;

            float pKing = _beliefState.GetProbability(move.To, PieceType.King);
            if (pKing >= KingMildThreshold) return 0;

            // 已经算到赢定（含连续将军/杀王线路）就不再多疑，避免把必胜走法让掉。
            if (searchScore >= DecisiveScore) return 0;

            // 有保护的换子属于正常兑子，不是“送到嘴边”。
            if (IsSquareAttackedByEnemyVisible(board, move.To)) return 0;

            // 这枚棋子正在攻击我方王：吃它是解围，不能按诱饵处理。
            var ourKing = board.FindKing(_color);
            if (ourKing.HasValue &&
                MoveGenerator.CanPieceAttackSquare(board, move.To, target.VisibleType, target.Color, ourKing.Value))
                return 0;

            // 吃完后这枚子若立刻威胁最可能的王位，属于连环杀王的第一步，允许吃。
            var after = board.Clone();
            after.ExecuteMove(move);
            if (CreatesDirectKingThreat(after, move.To)) return 0;

            float profileWeight = LiveProfileWeight();
            float discipline = PlayerProfile != null ? 1f - PlayerProfile.SacrificeRate : 0.5f;
            float expert = PlayerProfile != null ? Math.Max(0f, PlayerProfile.WinRate - 0.5f) * 2f : 0f;
            float suspicion = 0.45f + 0.35f * discipline + 0.2f * expert;
            suspicion += _beliefState.GetTrapRisk(move.To) * 0.5f;

            // 浅层先忍住不多疑；迭代加深到 4 层以上、后续变化算得更清时再逐步放宽。
            float depthFactor = rootDepth >= 5 ? 0.75f : rootDepth >= 4 ? 0.9f : 1f;
            suspicion *= depthFactor * (0.75f + 0.25f * profileWeight);

            return Math.Min(MaxBaitPenalty, (int)Math.Round(value * Math.Min(0.95f, suspicion) * leadScale));
        }

        // 吃子后由这枚子直接攻击“最可能的王位”，才视为连杀线路的一部分。
        private bool CreatesDirectKingThreat(Board after, Position from)
        {
            var kingPos = _beliefState.GetLikelyKingPosition();
            if (!kingPos.HasValue) return false;

            var piece = after.GetPiece(from);
            if (piece == null) return false;

            return MoveGenerator.CanPieceAttackSquare(after, from, piece.TrueType, piece.Color, kingPos.Value);
        }

        // 对“可能是王”的敌子持续施压：按每个候选的 P_King 连续给分，不再用 20% 阈值一刀切。
        // 暗棋里猜中敌王直接终局，猜错最多损失一个小子；廉价子探王的收益远大于损耗，
        // 而且每次逼迫对方走子都会让信念进一步收窄。权重刻意低于车（500），
        // 保证它只在与开发/防守大致同优时压过普通闲手，不会盖过真实战术。
        private int GetKingPressureBonus(Board board, Move move)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null) return 0;

            var after = board.Clone();
            after.ExecuteMove(move);

            double weight;
            switch (piece.TrueType)
            {
                case PieceType.Pawn: weight = 1800.0; break;
                case PieceType.Knight: weight = 1600.0; break;
                case PieceType.Bishop: weight = 1300.0; break;
                case PieceType.Rook: weight = 900.0; break;
                case PieceType.Queen: weight = 700.0; break;
                default: return 0; // 王亲自探王会自曝，交给伪装/逃王逻辑
            }

            double bonus = 0.0;
            int candidates = 0;
            foreach (var (pos, enemy) in after.GetPiecesOfColor(OpponentColor(_color)))
            {
                float p = _beliefState.GetProbability(pos, PieceType.King);
                if (p < 0.05f) continue;
                if (!MoveGenerator.CanPieceAttackSquare(after, move.To, piece.TrueType, _color, pos))
                    continue;

                bonus += p * weight;
                candidates++;
            }

            // 一子同时压住多个候选：对方只能撤一个，等于多线逼供。
            if (candidates >= 2) bonus += 60.0 * (candidates - 1);

            if (bonus > 700.0) bonus = 700.0;
            return (int)Math.Round(bonus);
        }

        private int GetBluffBonus(Move move, Board board)
        {
            var piece = board.GetPiece(move.From);
            if (piece == null) return 0;

            int bonus = 0;
            bool disguisedKing = piece.TrueType == PieceType.King && piece.VisibleType != PieceType.King;

            if (disguisedKing)
            {
                int heavyDistance = NearestEnemyHeavyDistance(board, move.To, _color);
                if (heavyDistance <= 2) bonus += 150;
                else if (heavyDistance <= 3) bonus += 75;

                if (IsSquareAttackedByEnemyVisible(board, move.To)) bonus += 120;
            }
            else if (piece.VisibleType != piece.TrueType)
            {
                // “假护驾”：伪装子主动贴到受威胁的王身边，比真的逃王更像没事。
                var kingPos = board.FindKing(_color);
                if (kingPos.HasValue && Chebyshev(move.To, kingPos.Value) == 1 && IsOwnKingAttackedByEnemy(board))
                    bonus += 60;
            }

            return bonus;
        }

        private int NearestEnemyHeavyDistance(Board board, Position pos, PieceColor forColor)
        {
            int best = 99;
            foreach (var (enemyPos, piece) in board.GetPiecesOfColor(OpponentColor(forColor)))
            {
                if (piece.VisibleType != PieceType.Queen && piece.VisibleType != PieceType.Rook) continue;
                best = Math.Min(best, Chebyshev(pos, enemyPos));
            }
            return best;
        }

        private int GetUrgencyBonus(Board board, Move move)
        {
            int bonus = 0;

            if (_movesSinceLastCapture > 10)
                bonus += Math.Min(60, _movesSinceLastCapture * 3);

            if (board.GetPiece(move.To) != null) bonus += 30;

            var piece = board.GetPiece(move.From);
            if (piece != null && piece.TrueType == PieceType.Pawn)
            {
                int dir = _color == PieceColor.White ? 1 : -1;
                int advance = (move.To.Y - move.From.Y) * dir;
                if (advance > 0) bonus += advance * 4;
            }

            return bonus;
        }

        private static ulong HashBoard(Board board, PieceColor side)
        {
#if PERF_PROBE
            long ts = PerfProbe.Now;
            ulong r = HashBoardCore(board, side);
            PerfProbe.Add(ref PerfProbe.HashTicks, PerfProbe.Now - ts);
            PerfProbe.Hit(ref PerfProbe.HashCalls);
            return r;
#else
            return HashBoardCore(board, side);
#endif
        }

        private static ulong HashBoardCore(Board board, PieceColor side)
        {
            ulong hash = 0UL;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;

                    int square = y * 8 + x;
                    hash ^= ZobristPiece[(int)piece.Color, (int)piece.TrueType, square];
                    if (piece.HasMoved) hash ^= ZobristMoved[square];
                }
            }

            if (side == PieceColor.Black) hash ^= ZobristSide;
            return hash;
        }
    }
}
