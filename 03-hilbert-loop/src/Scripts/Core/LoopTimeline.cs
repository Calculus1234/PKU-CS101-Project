using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 全局循环时间线（真实错峰醒来）。
    ///
    /// 剧本监控时间轴（倒计时 1:30 → 0:00）：1号 T=0（1:30）；2号 T=30（1:00）；
    /// 3号 T=35（0:55）；4号 T=40（0:50）；5号 T=45（0:45）；6号 T=50（0:40）；
    /// 7号 T=55（0:35）；8号 T=60（0:30）。
    ///
    /// 9号是「循环被破坏」后的例外：
    ///   1号拒杀 → 9号 = 第 85 分钟（倒计时 0:05）从床上醒来；
    ///   8号AI 的虚假认知重新指向 9号，最迟在第 89 分钟完成刺杀；
    ///   会议在第 90 分钟（0:00）开始，9号不会活到会议。
    ///
    /// 10号是旧设计的「无房异常者」，已随「9号必须杀青」方案停用：
    /// 不再生成 10号，也不需要为它新做地图。
    ///
    /// 可玩性换算：1 游戏分钟 = 6 现实秒，常规搜证阶段 90 游戏分钟（540 秒）。
    /// 时间轴以「玩家醒来」为原点做相对换算：
    ///   相对延迟 = max(0, 该 NPC 的绝对醒来分钟 − 玩家绝对醒来分钟) × 6 秒。
    /// 因此无论玩家抽到几号，都能获得完整的 90 游戏分钟搜证；
    /// 玩家扮演 1 号时，2号恰好在现实 180 秒（第 30 游戏分钟）推门而出。
    /// 若 9号活着，搜证结束时间会顺延，保证它能在会议前完成这段剧情。
    /// </summary>
    public static class LoopTimeline
    {
        /// <summary>1 游戏分钟对应的现实秒数。</summary>
        public const float SecondsPerGameMinute = 6f;

        /// <summary>常规搜证总游戏分钟数（距第 9 次会议的 90 分钟）。</summary>
        public const int TotalGameMinutes = 90;

        /// <summary>1号视角的完整搜证时长（90 × 6 = 540 秒）；其他号码按醒来时刻缩短。</summary>
        public const float InvestigationSeconds = TotalGameMinutes * SecondsPerGameMinute;

        /// <summary>1号拒杀后，9号醒来所在的绝对游戏分钟（0:05，即第 85 分钟）。</summary>
        public const int SurvivingNinthWakeMinute = 85;

        /// <summary>1号拒杀后，会议开始的绝对游戏分钟（9号 85 分钟醒来，8号在会前完成刺杀）。</summary>
        public const int RefusalMeetingMinute = 90;

        /// <summary>10号醒来的绝对游戏分钟。旧设计的异常者时点，当前剧本不会生成 10号。</summary>
        public const int TenthWakeMinute = 100;

        /// <summary>
        /// 9号必须杀青的保底分钟：8号AI 若没能自然得手，
        /// 循环会在第 89 分钟让场上存活号码最高的 NPC 替它完成这次谋杀。
        /// </summary>
        public const int NinthSecretKillMinute = 89;

        /// <summary>绝对醒来分钟表（索引 = 号码；0 号未使用）。</summary>
        static readonly int[] WakeMinutes = { -1, 0, 30, 35, 40, 45, 50, 55, 60, 85, 100 };

        static float _cycleStartTime;
        static bool _started;

        /// <summary>本轮开始时刻（CycleController.Awake 调用）。</summary>
        public static void BeginCycle()
        {
            _cycleStartTime = Time.time;
            _started = true;

            Debug.Log("[LoopTimeline] 错峰醒来时间轴已启动（1 游戏分钟 = " +
                      $"{SecondsPerGameMinute:0} 现实秒，常规搜证总时长 {InvestigationSeconds:0} 秒）：");
            for (int number = 1; number <= 10; number++)
            {
                int minute = WakeMinuteOf(number);
                if (number == 9)
                {
                    Debug.Log($"  9 号：正常循环中不会醒来；1号拒杀后于第 {minute} 游戏分钟醒来");
                }
                else if (number == 10)
                {
                    Debug.Log("  10 号：旧设计的异常者，当前剧本已停用（9号必定在会议前杀青）。");
                }
                else
                {
                    Debug.Log($"  {number} 号：第 {minute} 游戏分钟醒来（绝对时间线）");
                }
            }

            int player = CycleState.playerNumber;
            Debug.Log(player >= 1 && player <= 8
                ? $"[LoopTimeline] 玩家扮演 {player} 号 → 以第 {WakeMinuteOf(player)} 游戏分钟为原点做相对调度。"
                : "[LoopTimeline] 玩家为观察者 → 采用 1 号原点的绝对调度。");
        }

        /// <summary>某号码的绝对醒来分钟；非法号码返回 -1。</summary>
        public static int WakeMinuteOf(int number)
        {
            if (number < 1 || number >= WakeMinutes.Length) return -1;
            return WakeMinutes[number];
        }

        /// <summary>玩家绝对醒来分钟；观察者按 1 号（T=0）处理。</summary>
        public static int PlayerWakeMinute
        {
            get
            {
                int player = CycleState.playerNumber;
                return player >= 1 && player <= 8 ? WakeMinuteOf(player) : WakeMinuteOf(1);
            }
        }

        /// <summary>
        /// NPC 相对玩家的延迟秒数：绝对时间差 × 6 秒。
        /// 玩家醒来前就已到点的 NPC 返回 0（开局即醒）。
        /// </summary>
        public static float DelaySecondsFor(int npcNumber)
        {
            int wake = WakeMinuteOf(npcNumber);
            if (wake < 0) return float.PositiveInfinity;

            int baseline = PlayerWakeMinute;
            return Mathf.Max(0, wake - baseline) * SecondsPerGameMinute;
        }

        /// <summary>
        /// 以 NPCData.wakeupDelay（绝对游戏分钟）为准的延迟换算。
        /// 负数 = 永不醒来；玩家原点仍按号码表取（剧本固定）。
        /// </summary>
        public static float DelaySecondsFor(int npcNumber, float absoluteWakeMinute)
        {
            if (absoluteWakeMinute < 0f) return float.PositiveInfinity;
            return Mathf.Max(0f, absoluteWakeMinute - PlayerWakeMinute) * SecondsPerGameMinute;
        }

        /// <summary>
        /// 从当前时刻到某个绝对游戏分钟还需要等待的现实秒数。
        /// 已经过点返回 0（立即执行），用于 1号拒杀后把 9号的醒来精确排在时间线上。
        /// </summary>
        public static float DelayFromNowUntil(int absoluteWakeMinute)
        {
            float targetSeconds = Mathf.Max(0, absoluteWakeMinute) * SecondsPerGameMinute;
            return Mathf.Max(0f, targetSeconds - CycleElapsedSeconds);
        }

        /// <summary>本轮已过去的现实秒数。</summary>
        public static float CycleElapsedSeconds => _started ? Time.time - _cycleStartTime : 0f;

        /// <summary>本轮已过去的游戏分钟（浮点）。</summary>
        public static float CycleElapsedGameMinutes => CycleElapsedSeconds / SecondsPerGameMinute;

        /// <summary>把游戏分钟格式化为 "1:05"。</summary>
        public static string FormatMinute(int minute)
        {
            if (minute < 0) return "--:--";
            return $"{minute / 60}:{minute % 60:00}";
        }

        /// <summary>把游戏分钟格式化为一句便于 Console 阅读的说明。</summary>
        public static string DescribeWake(int number)
        {
            int minute = WakeMinuteOf(number);
            return minute < 0 ? "不会醒来" : $"第 {minute} 游戏分钟（{FormatMinute(minute)}）";
        }
    }
}
