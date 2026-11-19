using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// v2: 无牌可压时检查过牌（修复模拟误判必败）
namespace PokerSolver
{
    public sealed class ParallelSolveOptions
    {
        public int TimeLimitMs = 180000;
        public int ThreadCount = 4;
        public int MaxTableLog2 = 26;
        /// <summary>兼容旧字段（不再单独使用）。</summary>
        public long MemoryPerThread = 0;
        /// <summary>
        /// 进程级总内存上限（字节）。GC.GetTotalMemory 返回的是整个进程的托管堆，
        /// 多线程共享同一个上限，不能按线程各算一份。
        /// </summary>
        public long TotalMemoryLimitBytes = 0;
    }

    public sealed class ParallelSolveResult
    {
        public bool Completed;
        /// <summary>根行动方能否赢。</summary>
        public bool CurrentWins;
        public bool TimedOut;
        public double ElapsedMs;
        public long TotalNodes;
        /// <summary>制胜走法（如果能赢）。</summary>
        public Move FirstMove;
        public bool AnyThreadTimedOut;
    }

    /// <summary>
    /// 多线程残局求解：把根节点的候选走法分片，每个线程独立置换表搜索，
    /// 谁先找到「能赢」的走法就通知全体停止。
    /// </summary>
    public static class ParallelSolver
    {
        /// <summary>调试用：最近一次 SolveWithPrev 的过牌判定结果。</summary>
        public static bool LastPassWins;
        public static bool LastEnteredPassCheck;
        public static int LastRootMoveCount;
        public static bool LastAnyTimeout;
        public static int LastIdx;
        public static bool LastPrevIsPass;
        public static long LastRemain;
        public static ParallelSolveResult Solve(ulong firstHand, ulong secondHand, ParallelSolveOptions opt)
        {
            return SolveWithPrev(firstHand, secondHand, Move.Pass, opt);
        }

        /// <summary>
        /// 带台面的并行求解：当前方必须压过 prev 或选择过。
        /// 模拟对抗用这个，避免子局面退回单线程导致超时。
        /// </summary>
        public static ParallelSolveResult SolveWithPrev(ulong firstHand, ulong secondHand, Move prev, ParallelSolveOptions opt)
        {
            if (opt == null) opt = new ParallelSolveOptions();

            // 定式先判：能严格证明的局面直接返回，省掉整棵搜索树
            {
                EndgameBook.Verdict verdict = EndgameBook.Judge(firstHand, secondHand, prev);
                if (verdict.Hit)
                {
                    return new ParallelSolveResult
                    {
                        Completed = true,
                        CurrentWins = verdict.Win,
                        TimedOut = false,
                        ElapsedMs = 0,
                        TotalNodes = 0,
                        FirstMove = verdict.First
                    };
                }
            }

            // 进程级总内存上限（不是每线程）
            long totalMemLimit = opt.TotalMemoryLimitBytes > 0
                ? opt.TotalMemoryLimitBytes
                : (opt.MemoryPerThread > 0 ? opt.MemoryPerThread * opt.ThreadCount : 3_000_000_000L);

            var rootMoves = new List<Move>(256);
            if (prev.IsPass)
                MoveGen.GenerateAll(firstHand, rootMoves);
            else
                MoveGen.GenerateBeats(firstHand, prev, rootMoves);

            // 根节点用「对手可压制数」辅助排序：把真正的解尽量提前，
            // 减少「先花几十秒证明一堆错误候选」的情况。
            MoveGen.SortRootForSearch(rootMoves, firstHand, secondHand);

            if (rootMoves.Count == 0)
            {
                // 没有能压的牌：只能过。过牌完全可能赢，绝不能直接判必败。
                if (!prev.IsPass)
                {
                    var passSolver = new DDSolver(opt.MaxTableLog2);
                    bool oppWins = passSolver.ProbeChild(firstHand, secondHand, Move.Pass,
                        opt.TimeLimitMs, totalMemLimit);

                    if (passSolver.LastTimedOut)
                    {
                        return new ParallelSolveResult
                        {
                            Completed = false,
                            TimedOut = true,
                            CurrentWins = false,
                            ElapsedMs = 0
                        };
                    }

                    return new ParallelSolveResult
                    {
                        Completed = true,
                        CurrentWins = !oppWins,
                        FirstMove = Move.Pass,
                        ElapsedMs = 0
                    };
                }

                return new ParallelSolveResult
                {
                    Completed = true,
                    CurrentWins = false,
                    ElapsedMs = 0
                };
            }

            int threadCount = Math.Max(1, Math.Min(opt.ThreadCount, rootMoves.Count));

            var stopwatch = Stopwatch.StartNew();
            long deadlineMs = opt.TimeLimitMs;   // 全局总时限，不是每个候选单独计时

            int stopFlag = 0;
            int foundIndex = -1;
            long[] threadNodes = new long[threadCount];
            bool[] threadTimedOut = new bool[threadCount];

            var latch = new CountdownEvent(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;

                var thread = new Thread(() =>
                {
                    long myNodes = 0;
                    bool myTimedOut = false;

                    try
                    {
                        var solver = new DDSolver(opt.MaxTableLog2);
                        long mem = totalMemLimit;

                        // 静态分片（i += threadCount）：实测比动态任务队列快一倍。
                        // 原因：候选已按「希望大小」排序，静态分片让每个线程一开始就同时展开
                        // 一个靠前的候选；动态队列则退化成从前往后串行抢任务，
                        // 且打散了每线程 TT 的时间局部性（节点数 41M → 68M）。
                        for (int i = tid; i < rootMoves.Count; i += threadCount)
                        {
                            if (Volatile.Read(ref stopFlag) != 0) break;

                            // 全局预算：每个候选只能用剩余时间
                            long remain = deadlineMs - stopwatch.ElapsedMilliseconds;
                            if (remain <= 0) { myTimedOut = true; break; }

                            Move m = rootMoves[i];

                            // 一手走完，直接制胜
                            if (m.Cards == firstHand)
                            {
                                Interlocked.Exchange(ref foundIndex, i);
                                Interlocked.Exchange(ref stopFlag, 1);
                                break;
                            }

                            int perMoveLimit = (int)Math.Max(100, Math.Min(remain, int.MaxValue));
                            bool oppWins = solver.ProbeChild(firstHand, secondHand, m, perMoveLimit, mem);
                            myNodes += solver.LastNodes;

                            // 关键：超时时 Win 也会返回 false，绝不能当成「对方输」
                            if (solver.LastTimedOut)
                            {
                                myTimedOut = true;
                                continue;
                            }

                            if (!oppWins)
                            {
                                Interlocked.Exchange(ref foundIndex, i);
                                Interlocked.Exchange(ref stopFlag, 1);
                                break;
                            }
                        }
                    }
                    finally
                    {
                        threadNodes[tid] = myNodes;
                        threadTimedOut[tid] = myTimedOut;
                        latch.Signal();
                    }
                });

                thread.IsBackground = true;
                // 手机上渲染/系统会抢 CPU，把求解线程提到 AboveNormal（失败就算了）
                try { thread.Priority = ThreadPriority.AboveNormal; } catch { }
                thread.Start();
            }

            // 主线程最多再等 5 秒兜底；后台线程是 IsBackground，不会拖住进程
            long waitBudget = Math.Max(1000, deadlineMs - stopwatch.ElapsedMilliseconds + 5000);
            bool allFinished = latch.Wait((int)Math.Min(waitBudget, int.MaxValue));
            stopwatch.Stop();

            var result = new ParallelSolveResult();
            result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            if (!allFinished) result.TimedOut = true;

            long total = 0;
            bool anyTimeout = result.TimedOut;
            for (int i = 0; i < threadCount; i++)
            {
                total += threadNodes[i];
                if (threadTimedOut[i]) anyTimeout = true;
            }

            result.TotalNodes = total;
            result.AnyThreadTimedOut = anyTimeout;

            int idx = Volatile.Read(ref foundIndex);

            LastPassWins = false;
            LastEnteredPassCheck = false;
            LastRootMoveCount = rootMoves.Count;
            LastAnyTimeout = anyTimeout;
            LastIdx = idx;
            LastPrevIsPass = prev.IsPass;
            LastRemain = deadlineMs - stopwatch.ElapsedMilliseconds;

            // 候选都压不住时，「过」也是一条合法走法
            bool passWins = false;
            if (idx < 0 && !prev.IsPass && !anyTimeout)
            {
                long remain = deadlineMs - stopwatch.ElapsedMilliseconds;
                if (remain > 50)
                {
                    LastEnteredPassCheck = true;

                    var passSolver = new DDSolver(opt.MaxTableLog2);
                    long mem = totalMemLimit;
                    bool oppWins = passSolver.ProbeChild(firstHand, secondHand, Move.Pass,
                        (int)Math.Max(100, Math.Min(remain, int.MaxValue)), mem);

                    if (passSolver.LastTimedOut)
                        anyTimeout = true;
                    else if (!oppWins)
                        passWins = true;
                }
            }

            LastPassWins = passWins;
            LastAnyTimeout = anyTimeout;

            if (idx >= 0)
            {
                result.Completed = true;
                result.CurrentWins = true;
                result.FirstMove = rootMoves[idx];
            }
            else if (passWins)
            {
                result.Completed = true;
                result.CurrentWins = true;
                result.FirstMove = Move.Pass;
            }
            else if (anyTimeout)
            {
                result.Completed = false;
                result.TimedOut = true;
                result.CurrentWins = false;
            }
            else
            {
                result.Completed = true;
                result.CurrentWins = false;
            }

            return result;
        }
    }
}
