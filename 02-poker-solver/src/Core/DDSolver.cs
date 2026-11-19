using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PokerSolver
{
    public sealed class SolveOptions
    {
        /// <summary>硬超时（毫秒）。超时后返回「未完成」，不会给出错误结论。</summary>
        public int TimeLimitMs = 180000;

        /// <summary>内存上限（字节）。搜索超限时优雅停下，避免 OutOfMemory 崩溃。</summary>
        public long MemoryLimitBytes = 1_200_000_000L;

        /// <summary>
        /// 保留置换表缓存（模拟对抗时用）。同一局面的子查询基本瞬间命中，
        /// 相当于在搜索树上做 O(1) 查询，而不是每次从头算。
        /// </summary>
        public bool KeepCache;
    }

    public sealed class SolveResult
    {
        /// <summary>是否在时限内得到确定结论。</summary>
        public bool Completed;
        /// <summary>我方是否必胜（Completed 为 true 时有效）。</summary>
        public bool CanWin;
        public bool TimedOut;
        /// <summary>是否因内存超限而提前停止。</summary>
        public bool OutOfMemory;
        public double ElapsedMs;
        public long Nodes;
        public int States;
        public long Hits;
        /// <summary>完整推演主线（"我方：..."/"对方：..."）。</summary>
        public List<string> Line = new List<string>();
        /// <summary>建议的第一手（模拟对抗时直接执行）。</summary>
        public Move FirstMove;
        public string Error;
    }

    /// <summary>
    /// 单挑明牌残局求解器。
    ///
    /// 核心：布尔 negamax —— Win(当前方手牌, 对方手牌, 台面牌) 表示「当前行动方能否先出完」。
    /// 规则里「过牌」是独立分支：面对别人的牌可以压也可以过；自由出牌时必须出。
    /// 因为每次递归要么手牌减少、要么 prev 被清空，天然无环，可以放心记忆化。
    ///
    /// 置换表用紧凑开放寻址：64bit 哈希 + 16bit tag + 1byte 值，比 Dictionary 省一半以上内存，
    /// 大残局（千万级状态）下 GC 压力明显更低。
    /// </summary>
    public sealed class DDSolver
    {
        private const int InitialTableLog2 = 20; // 1M 条
        private const int MaxTableLog2 = 25;     // 33M 条上限（约 370MB），防止手机 OOM
        private const long MemCheckInterval = 1_000_000L;

        // 三个平行数组（hash 8B / tag 2B / value 1B），每条 11 字节。
        // 试过合并成一个 16B 结构体数组（cache line 友好），实测反而慢 2.4 倍 ——
        // 内存从 176MB 涨到 256MB/线程，GC 压力和命中率损失盖过了 cache 收益。
        private ulong[] _tableH;
        private ushort[] _tableTag;
        private byte[] _tableVal;   // 0=空 1=false 2=true
        private int _bucketMask;    // 桶数 - 1（每桶 BucketSize 条）
        private int _stateCount;

        // 分桶：每个桶固定 4 条，同桶的条目在三个数组里都是连续的。
        // 相比原来的线性探测（最多找 64 个槽），一次探测最多看 4 条，
        // 而且这 4 条落在 1~2 条 cache line 内。
        private const int BucketSize = 4;

        private readonly List<Move>[] _buffers;
        private readonly List<Move> _probeBuf = new List<Move>(64);
        private readonly Stopwatch _sw = new Stopwatch();

        private int _depth;
        private long _nodes;
        private long _hits;

        // 诊断计数器（只增不减，不影响算法）
        public long StatTtProbe;
        public long StatTtHit;
        public long StatGenAll;
        public long StatGenBeats;
        public long StatSort;
        /// <summary>走法生成 + 排序累计占用的 Stopwatch ticks。</summary>
        public long StatGenTicks;
        /// <summary>其中排序单独占用的 ticks。</summary>
        public long StatSortTicks;
        /// <summary>置换表探测累计 ticks。</summary>
        public long StatTtTicks;
        /// <summary>递归 + TT 的剩余时间（用总时间减）。</summary>
        public long StatTotalTicks;
        private int _timeLimitMs;
        private bool _timedOut;
        private bool _outOfMemory;
        private long _memoryLimitBytes;
        private long _lastMemCheckNode;
        private long _rebuildDeadline;
        private int _maxTableLog2;

        public DDSolver() : this(MaxTableLog2)
        {
        }

        /// <summary>
        /// maxTableLog2 控制置换表上限（每条约 11 字节）。
        /// 手机端并行时每线程用较小值，避免 OOM。
        /// </summary>
        public DDSolver(int maxTableLog2)
        {
            _buffers = new List<Move>[256];
            _maxTableLog2 = Math.Max(16, Math.Min(26, maxTableLog2));
            InitTable(Math.Min(InitialTableLog2, _maxTableLog2));
        }

        /// <summary>
        /// 供并行求解用：只判断「当前方走 m 之后，对方能否赢」。
        /// 返回 true = 对方能赢（这手不是制胜走法）。
        /// 每个线程必须持有独立实例。
        /// </summary>
        public bool ProbeChild(ulong myHand, ulong oppHand, Move m, int timeLimitMs, long memLimit)
        {
            _timedOut = false;
            _outOfMemory = false;
            _depth = 0;
            _nodes = 0;
            _hits = 0;
            _timeLimitMs = Math.Max(100, timeLimitMs);
            _memoryLimitBytes = memLimit > 0 ? memLimit : 800_000_000L;
            _lastMemCheckNode = 0;

            _sw.Reset();
            _sw.Start();

            bool result = Win(oppHand, myHand - m.Cards, m);

            _sw.Stop();
            return result;
        }

        public long LastNodes { get { return _nodes; } }
        public bool LastTimedOut { get { return _timedOut; } }

        private void InitTable(int log2)
        {
            int size = 1 << log2;
            _tableH = new ulong[size];
            _tableTag = new ushort[size];
            _tableVal = new byte[size];
            _bucketMask = (size / BucketSize) - 1;
            _stateCount = 0;
        }

        private void ClearTable()
        {
            Array.Clear(_tableVal, 0, _tableVal.Length);
            _stateCount = 0;
        }

        private static ulong HashState(ulong me, ulong opp, int prevKey)
        {
            unchecked
            {
                ulong h = me * 0x9E3779B97F4A7C15UL;
                h ^= (opp + 0x517CC1B727220A95UL) * 0xC2B2AE3D27D4EB4FUL;
                h ^= ((ulong)prevKey + 0x165667B19E3779F9UL) * 0x27D4EB2F165667C5UL;
                h ^= h >> 33;
                h *= 0xff51afd7ed558ccdUL;
                h ^= h >> 33;
                h *= 0xc4ceb9fe1a85ec53UL;
                h ^= h >> 33;
                return h;
            }
        }

        private static int PackPrev(in Move prev)
        {
            return ((int)prev.Type) | (((int)prev.Rank) << 8) | (((int)prev.Length) << 16);
        }

        private bool Lookup(ulong h, ushort tag, out bool value)
        {
            int idx = (int)(h & (ulong)_bucketMask) * BucketSize;
            int end = idx + BucketSize;
            for (; idx < end; idx++)
            {
                byte v = _tableVal[idx];
                if (v == 0) { value = false; return false; }
                if (_tableH[idx] == h && _tableTag[idx] == tag)
                {
                    value = (v == 2);
                    return true;
                }
            }
            value = false;
            return false;
        }

        private void Store(ulong h, ushort tag, bool value)
        {
            int baseIdx = ((int)(h & (ulong)_bucketMask)) * BucketSize;
            int slot = 0;
            for (; slot < BucketSize; slot++)
            {
                int idx = baseIdx + slot;
                byte v = _tableVal[idx];
                if (v == 0) break;          // 找到空槽
                if (_tableH[idx] == h && _tableTag[idx] == tag)
                {
                    _tableVal[idx] = value ? (byte)2 : (byte)1;
                    return;
                }
            }

            bool wasEmpty = slot < BucketSize;
            int target = wasEmpty ? baseIdx + slot : baseIdx + (int)((h >> 40) & 3);
            _tableH[target] = h;
            _tableTag[target] = tag;
            _tableVal[target] = value ? (byte)2 : (byte)1;

            if (wasEmpty)
            {
                _stateCount++;
                int capacity = (_bucketMask + 1) * BucketSize;
                if (_stateCount * 2 > capacity) Grow();
            }
        }

        private void Grow()
        {
            int capacity = (_bucketMask + 1) * BucketSize;
            if (capacity >= (1 << _maxTableLog2)) return;   // 已到上限

            int newLog = 1;
            while ((1 << newLog) < capacity) newLog++;
            newLog++;
            if (newLog > _maxTableLog2) return;

            ulong[] oh = _tableH;
            ushort[] ot = _tableTag;
            byte[] ov = _tableVal;
            int oldCount = capacity;

            int newSize = 1 << newLog;
            _tableH = new ulong[newSize];
            _tableTag = new ushort[newSize];
            _tableVal = new byte[newSize];
            _bucketMask = (newSize / BucketSize) - 1;
            _stateCount = 0;

            for (int i = 0; i < oldCount; i++)
            {
                if (ov[i] == 0) continue;
                InsertNoGrow(oh[i], ot[i], ov[i] == 2);
            }
        }

        private void InsertNoGrow(ulong h, ushort tag, bool value)
        {
            int baseIdx = ((int)(h & (ulong)_bucketMask)) * BucketSize;
            for (int slot = 0; slot < BucketSize; slot++)
            {
                int idx = baseIdx + slot;
                if (_tableVal[idx] == 0)
                {
                    _tableH[idx] = h;
                    _tableTag[idx] = tag;
                    _tableVal[idx] = value ? (byte)2 : (byte)1;
                    _stateCount++;
                    return;
                }
            }
            // 桶满：扩容过程中丢弃多余条目
        }

        private List<Move> GetBuffer()
        {
            int d = _depth;
            if (d < 0) d = 0;
            if (d >= _buffers.Length) d = _buffers.Length - 1;

            List<Move> b = _buffers[d];
            if (b == null)
            {
                b = new List<Move>(128);
                _buffers[d] = b;
            }
            return b;
        }

        public SolveResult Solve(ulong myHand, ulong oppHand, bool iLead, SolveOptions options)
        {
            return SolveCore(myHand, oppHand, iLead, Move.Pass, options);
        }

        /// <summary>
        /// 从「台面上还有一手牌」的局面开始求解。
        /// 模拟对抗时用：当前行动方必须压过 prev 或选择过，而不是自由出牌。
        /// </summary>
        public SolveResult SolveWithPrev(ulong me, ulong opp, Move prev, SolveOptions options)
        {
            return SolveCore(me, opp, true, prev, options);
        }

        private SolveResult SolveCore(ulong myHand, ulong oppHand, bool iLead, Move startPrev, SolveOptions options)
        {
            if (options == null) options = new SolveOptions();

            // 定式先判：能严格证明的局面直接返回，省掉整棵搜索树
            {
                ulong curHand = iLead ? myHand : oppHand;
                ulong othHand = iLead ? oppHand : myHand;
                EndgameBook.Verdict verdict = EndgameBook.Judge(curHand, othHand, startPrev);
                if (verdict.Hit)
                {
                    bool curWins = verdict.Win;
                    bool myWins = iLead ? curWins : !curWins;
                    return new SolveResult
                    {
                        Completed = true,
                        CanWin = myWins,
                        TimedOut = false,
                        OutOfMemory = false,
                        ElapsedMs = 0,
                        Nodes = 0,
                        States = 0,
                        Hits = 0,
                        Line = new List<string> { "【定式】" + verdict.Name + "：" + verdict.Detail },
                        FirstMove = verdict.First
                    };
                }
            }

            if (!options.KeepCache) ClearTable();
            HandEval.ResetCache();
            _nodes = 0;
            _hits = 0;
            _timedOut = false;
            _outOfMemory = false;
            _depth = 0;
            _lastMemCheckNode = 0;
            _timeLimitMs = Math.Max(100, options.TimeLimitMs);
            _memoryLimitBytes = options.MemoryLimitBytes > 0 ? options.MemoryLimitBytes : 1_200_000_000L;

            _sw.Reset();
            _sw.Start();

            bool rootWin;
            if (iLead)
            {
                rootWin = Win(myHand, oppHand, startPrev);
            }
            else
            {
                // 对方先手：对方必败 == 我方必胜
                rootWin = !Win(oppHand, myHand, startPrev);
            }

            bool searchTimedOut = _timedOut;

            // 重建推演线：给了 3 秒预算，超时就用简化策略。
            // 计时表一直不停，所以最终耗时包含重建，不会出现「实际 90 秒、显示 39 秒」。
            Move firstMove = Move.Pass;
            List<string> line = new List<string>();

            if (!searchTimedOut)
            {
                _timedOut = false;
                _timeLimitMs = int.MaxValue;
                // 首手必须精确验证（命中搜索缓存通常瞬间），所以给足预算；
                // 后续步在大局面用启发式，保证线能走完。
                _rebuildDeadline = _sw.ElapsedMilliseconds + 10000;
                bool exactRebuild = CardUtil.TotalCount(myHand) + CardUtil.TotalCount(oppHand) <= 26;
                firstMove = RebuildLine(myHand, oppHand, iLead, startPrev, rootWin, exactRebuild, line);
                _timedOut = searchTimedOut;
            }

            _sw.Stop();
            StatTotalTicks = _sw.ElapsedTicks;

            var result = new SolveResult
            {
                Completed = !searchTimedOut,
                CanWin = rootWin,
                TimedOut = searchTimedOut,
                OutOfMemory = _outOfMemory,
                ElapsedMs = _sw.Elapsed.TotalMilliseconds,
                Nodes = _nodes,
                States = _stateCount,
                Hits = _hits,
                Line = line,
                FirstMove = firstMove
            };

            return result;
        }

        private bool Win(ulong me, ulong opp, in Move prev)
        {
            if (me == 0UL) return true;
            if (opp == 0UL) return false;
            if (_timedOut) return false;

            _nodes++;
            if ((_nodes & 0x3FF) == 0 && _sw.ElapsedMilliseconds > _timeLimitMs)
            {
                _timedOut = true;
                return false;
            }

            // 内存保险丝：每百万节点看一次托管堆，超限就优雅停下
            if (_nodes - _lastMemCheckNode > MemCheckInterval)
            {
                _lastMemCheckNode = _nodes;
                long mem = GC.GetTotalMemory(false);
                if (mem > _memoryLimitBytes)
                {
                    _outOfMemory = true;
                    _timedOut = true;
                    return false;
                }
            }

            int prevKey = PackPrev(prev);
            ulong h = HashState(me, opp, prevKey);
            ushort tag = (ushort)(h >> 48);

            bool cached;
            if (Lookup(h, tag, out cached))
            {
                _hits++;
                return cached;
            }

            _depth++;
            bool result;
            try
            {
                if (prev.IsPass)
                {
                    result = false;
                    List<Move> moves = GetBuffer();
                    MoveGen.GenerateAll(me, moves);
                    MoveGen.SortForSearch(moves, me);

                    for (int i = 0; i < moves.Count; i++)
                    {
                        Move m = moves[i];
                        if (m.Cards == me) { result = true; break; }   // 一手走完

                        // 出完这手后能一手清台（排序时已算好），且对方压不住这手 → 必胜
                        if (m.RestHands == 1)
                        {
                            MoveGen.GenerateBeats(opp, m, _probeBuf);
                            if (_probeBuf.Count == 0) { result = true; break; }
                        }

                        if (!Win(opp, me - m.Cards, m)) { result = true; break; }
                        if (_timedOut) break;
                    }
                }
                else
                {
                    result = false;

                    // 先试「压」，再试「过」。
                    List<Move> beats = GetBuffer();
                    MoveGen.GenerateBeats(me, prev, beats);
                    MoveGen.SortForSearch(beats, me);

                    // 能一手走完直接赢
                    for (int i = 0; i < beats.Count; i++)
                    {
                        if (beats[i].Cards == me) { result = true; break; }
                    }

                    if (!result)
                    {
                        for (int i = 0; i < beats.Count; i++)
                        {
                            Move m = beats[i];

                            // 压完这手后能一手清台（排序时已算好），且对方压不住 → 必胜
                            if (m.RestHands == 1)
                            {
                                MoveGen.GenerateBeats(opp, m, _probeBuf);
                                if (_probeBuf.Count == 0) { result = true; break; }
                            }

                            if (!Win(opp, me - m.Cards, m)) { result = true; break; }
                            if (_timedOut) break;
                        }
                    }

                    if (!result && !_timedOut)
                    {
                        // 过牌前先看一眼：对方要是一手就能走完，过牌必输，不用递归
                        if (HandEval.MinHands(opp) > 1)
                        {
                            if (!Win(opp, me, Move.Pass)) result = true;
                        }
                    }
                }
            }
            finally
            {
                _depth--;
            }

            if (!_timedOut) Store(h, tag, result);
            return result;
        }

        private readonly List<Move> _evalBuf = new List<Move>(128);

        /// <summary>
        /// 必败方走完这一手后，轮到对手（必胜方）行动。
        /// 返回「对手的合法走法中，会把自己走输的比例」—— 越高说明越容易让人类对手犯错。
        /// </summary>
        private float ErrorPotentialAfter(ulong meAfter, ulong opp, in Move prevAfter)
        {
            // 超出重建预算就不再评估，直接返回 0（退回普通策略）
            if (_sw.ElapsedMilliseconds > _rebuildDeadline) return 0f;

            // 大局面跳过失误率评估：一次 Win 就可能几秒，评估会把重建线拖垮。
            if (CardUtil.TotalCount(meAfter) + CardUtil.TotalCount(opp) > 26) return 0f;

            _evalBuf.Clear();

            if (prevAfter.IsPass)
                MoveGen.GenerateAll(opp, _evalBuf);
            else
                MoveGen.GenerateBeats(opp, prevAfter, _evalBuf);

            // 轻量版：只要发现「对手有一种走法会直接输」，就算有失误空间。
            // 不再遍历对手全部走法，避免大局面下重建线被拖垮。
            int limit = Math.Min(_evalBuf.Count, 8);
            for (int i = 0; i < limit; i++)
            {
                Move m = _evalBuf[i];
                if (!Win(meAfter, opp - m.Cards, m)) return 1f;
                if (_sw.ElapsedMilliseconds > _rebuildDeadline) return 0f;
            }

            if (prevAfter.IsPass) return 0f;

            // 对手选择过牌也可能走错
            if (!Win(meAfter, opp, Move.Pass)) return 1f;
            return 0f;
        }

        private Move RebuildLine(ulong myHand, ulong oppHand, bool iLead, Move startPrev,
                                 bool rootWin, bool exact, List<string> line)
        {
            line.Clear();
            var buf = new List<Move>(128);

            ulong cur = iLead ? myHand : oppHand;
            ulong oth = iLead ? oppHand : myHand;
            bool curIsMe = iLead;
            Move prev = startPrev;
            Move firstMove = Move.Pass;
            bool isFirst = true;
            bool meIsWinner = rootWin;

            int guard = 0;
            while (cur != 0UL && oth != 0UL && guard++ < 256)
            {
                // 重建线也有预算；超时就降级为启发式，保证线能走完
                if (exact && _sw.ElapsedMilliseconds > _rebuildDeadline) exact = false;

                bool curWins;
                if (exact)
                    curWins = Win(cur, oth, prev);
                else
                    curWins = curIsMe ? meIsWinner : !meIsWinner;
                Move pick = Move.Pass;
                bool picked = false;
                bool couldBeat = false;

                if (prev.IsPass)
                {
                    MoveGen.GenerateAll(cur, buf);
                    MoveGen.SortForSearch(buf, cur);

                    if (curWins)
                    {
                        bool needExact = exact || line.Count == 0;

                        if (needExact)
                        {
                            for (int i = 0; i < buf.Count; i++)
                            {
                                if (!exact && _sw.ElapsedMilliseconds > _rebuildDeadline) break;
                                Move m = buf[i];
                                if (!Win(oth, cur - m.Cards, m)) { pick = m; picked = true; break; }
                            }
                        }
                        if (!picked && buf.Count > 0) pick = buf[0];
                    }
                    else
                    {
                        // 必败方自由出牌：挑最小的一手，让主线看起来像真人抵抗
                        if (buf.Count > 0)
                        {
                            pick = buf[0];
                            for (int i = 1; i < buf.Count; i++)
                            {
                                int ci = CardUtil.TotalCount(buf[i].Cards);
                                int cb = CardUtil.TotalCount(pick.Cards);
                                if (buf[i].Rank < pick.Rank || (buf[i].Rank == pick.Rank && ci < cb))
                                    pick = buf[i];
                            }
                            picked = true;
                        }
                    }
                }
                else
                {
                    MoveGen.GenerateBeats(cur, prev, buf);
                    couldBeat = buf.Count > 0;

                    if (curWins)
                    {
                        // 必胜方：能压就压（更符合真人直觉），压不了/压了会输才考虑过。
                        // 首步必须精确验证，否则可能给出「跟着走会输」的错误建议。
                        bool needExact = exact || line.Count == 0;

                        if (needExact)
                        {
                            for (int i = 0; i < buf.Count; i++)
                            {
                                if (!exact && _sw.ElapsedMilliseconds > _rebuildDeadline) break;
                                Move m = buf[i];
                                if (!Win(oth, cur - m.Cards, m)) { pick = m; picked = true; break; }
                            }

                            if (!picked && exact)
                            {
                                bool passChild = Win(oth, cur, Move.Pass);
                                if (!passChild) { pick = Move.Pass; picked = true; }
                            }
                        }

                        if (!picked)
                        {
                            if (buf.Count > 0) { pick = buf[0]; picked = true; }
                            else { pick = Move.Pass; picked = true; }
                        }
                    }
                    else
                    {
                        if (line.Count == 0)
                        {
                            // 必败方首手：做失误率评估 —— 选「让对手最容易翻车」的走法。
                            // 只评估首手，避免大局面下重建线被拖垮。
                            Move best = Move.Pass;
                            float bestScore = -1f;

                            int limit = Math.Min(buf.Count, 5);
                            for (int i = 0; i < limit; i++)
                            {
                                Move m = buf[i];
                                float score = ErrorPotentialAfter(cur - m.Cards, oth, m);
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    best = m;
                                }
                            }

                            float passScore = ErrorPotentialAfter(cur, oth, Move.Pass);
                            if (passScore > bestScore)
                            {
                                best = Move.Pass;
                            }

                            pick = best;
                            picked = true;
                        }
                        else
                        {
                            // 后续走法：轻量策略，保证线完整。
                            bool mustBeat = HandEval.MinHands(oth) == 1;
                            if (mustBeat)
                            {
                                if (buf.Count > 0) { pick = buf[0]; picked = true; }
                                else { pick = Move.Pass; picked = true; }
                            }
                            else
                            {
                                Move best = Move.Pass;
                                bool found = false;
                                for (int i = 0; i < buf.Count; i++)
                                {
                                    if (buf[i].Type == MoveType.Bomb || buf[i].Type == MoveType.Rocket) continue;
                                    best = buf[i];
                                    found = true;
                                    break;
                                }
                                if (found) { pick = best; picked = true; }
                                else { pick = Move.Pass; picked = true; }
                            }
                        }
                    }
                }

                string label;
                if (pick.IsPass)
                    label = couldBeat ? "过" : "要不起";
                else
                    label = MoveUtil.Describe(pick);

                line.Add((curIsMe ? "我方" : "对方") + "：" + label);

                if (isFirst)
                {
                    firstMove = pick;
                    isFirst = false;
                }

                if (!pick.IsPass) cur -= pick.Cards;
                prev = pick;

                ulong tmp = cur;
                cur = oth;
                oth = tmp;
                curIsMe = !curIsMe;
            }

            return firstMove;
        }
    }
}
