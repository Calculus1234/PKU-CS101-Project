using System;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 事件总线（全局广播）。
    /// 系统之间只通过这里通信，避免互相硬引用：
    /// "发生了什么"由 Raise 方广播，具体反应由各自的黑板与行为树决定。
    /// </summary>
    public static class NPCEventBus
    {
        // ------------------------------------------------------------------
        // 个体事件
        // ------------------------------------------------------------------

        /// <summary>NPC 醒来（号码）。</summary>
        public static event Action<int> OnNPCWakeup;

        /// <summary>NPC 看到目标（号码，目标物体）。</summary>
        public static event Action<int, GameObject> OnNPCSeeTarget;

        /// <summary>NPC 听到噪音（号码，位置，响度）。</summary>
        public static event Action<int, Vector3, float> OnNPCHeardNoise;

        /// <summary>NPC 与他人互动（号码，互动描述）。</summary>
        public static event Action<int, string> OnNPCInteract;

        /// <summary>NPC 被推搡（号码，推搡者）。</summary>
        public static event Action<int, GameObject> OnNPCPushed;

        /// <summary>NPC 死亡（号码，位置，是否留下尸体）。</summary>
        public static event Action<int, Vector3, bool> OnNPCDie;

        /// <summary>NPC 进入房间（号码，房间名）。</summary>
        public static event Action<int, string> OnNPCEnterRoom;

        /// <summary>NPC 离开房间（号码，房间名）。</summary>
        public static event Action<int, string> OnNPCExitRoom;

        // ------------------------------------------------------------------
        // 全局事件
        // ------------------------------------------------------------------

        /// <summary>会议开始。</summary>
        public static event Action OnMeetingStart;

        /// <summary>会议结束。</summary>
        public static event Action OnMeetingEnd;

        /// <summary>投票（投票者号码，被投号码）。</summary>
        public static event Action<int, int> OnVoteCast;

        /// <summary>循环状态变化（新的循环阶段）。</summary>
        public static event Action<CyclePhase> OnCycleStateChanged;

        /// <summary>发现线索（发现者号码，线索）。</summary>
        public static event Action<int, string> OnClueDiscovered;

        /// <summary>怀疑度变化（号码，被怀疑号码，新的怀疑值）。</summary>
        public static event Action<int, int, float> OnSuspicionChanged;

        // ------------------------------------------------------------------
        // Raise（只有本类能触发事件）
        // ------------------------------------------------------------------

        public static void RaiseNPCWakeup(int number) => OnNPCWakeup?.Invoke(number);
        public static void RaiseNPCSeeTarget(int number, GameObject target) => OnNPCSeeTarget?.Invoke(number, target);
        public static void RaiseNPCHeardNoise(int number, Vector3 position, float loudness) => OnNPCHeardNoise?.Invoke(number, position, loudness);
        public static void RaiseNPCInteract(int number, string info) => OnNPCInteract?.Invoke(number, info);
        public static void RaiseNPCPushed(int number, GameObject pusher) => OnNPCPushed?.Invoke(number, pusher);
        public static void RaiseNPCDie(int number, Vector3 position, bool bodyLeft) => OnNPCDie?.Invoke(number, position, bodyLeft);
        public static void RaiseNPCEnterRoom(int number, string roomName) => OnNPCEnterRoom?.Invoke(number, roomName);
        public static void RaiseNPCExitRoom(int number, string roomName) => OnNPCExitRoom?.Invoke(number, roomName);
        public static void RaiseMeetingStart() => OnMeetingStart?.Invoke();
        public static void RaiseMeetingEnd() => OnMeetingEnd?.Invoke();
        public static void RaiseVoteCast(int voter, int target) => OnVoteCast?.Invoke(voter, target);
        public static void RaiseCycleStateChanged(CyclePhase phase) => OnCycleStateChanged?.Invoke(phase);
        public static void RaiseClueDiscovered(int number, string clue) => OnClueDiscovered?.Invoke(number, clue);
        public static void RaiseSuspicionChanged(int number, int suspectNumber, float value) => OnSuspicionChanged?.Invoke(number, suspectNumber, value);
    }
}
