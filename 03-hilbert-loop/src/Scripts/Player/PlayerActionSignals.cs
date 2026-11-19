using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家动作信号（模块三 3.3 视觉感知）：
    /// 藏匿 / 破门这类「没有声音但会被看见」的动作，由交互处打时间戳，
    /// NPCVision 在扫描时判断自己是否正好看见玩家并据此提升怀疑度。
    /// </summary>
    public static class PlayerActionSignals
    {
        /// <summary>玩家最近一次藏匿物品的时刻（Time.time）。</summary>
        public static float LastHidingTime { get; private set; } = -999f;

        /// <summary>玩家最近一次尝试破门 / 砸门的时刻（Time.time）。</summary>
        public static float LastDoorBreakTime { get; private set; } = -999f;

        public static void NotifyHiding()
        {
            LastHidingTime = Time.time;
        }

        public static void NotifyDoorBreak()
        {
            LastDoorBreakTime = Time.time;
        }

        /// <summary>新一轮开始时清除（由场景重载自然重置，这里供测试 / 重置使用）。</summary>
        public static void ResetAll()
        {
            LastHidingTime = -999f;
            LastDoorBreakTime = -999f;
        }
    }
}
