using System;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>游戏阶段：搜证 / 会议 / 投票 / 轮回。</summary>
    public enum GamePhase
    {
        Investigation,  // 搜证
        Meeting,        // 会议
        Voting,         // 投票
        Reincarnation,  // 轮回结算
    }

    /// <summary>
    /// 游戏状态管理：阶段切换与时间推进。
    /// 监控屏倒计时、NPC 行为树、Yarn 对话均可订阅事件接入。
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("阶段时长（秒）")]
        [Tooltip("搜证阶段上限（现实秒）。实际时长按玩家醒来时刻动态计算：会议固定在 0:00（第 90 游戏分钟），" +
                 "玩家醒得越晚，本轮可用的搜证时间越短。")]
        [SerializeField] private float investigationSeconds = 540f; // 1号：90 游戏分钟 × 6 秒
        [SerializeField] private float meetingSeconds = 180f;
        [SerializeField] private float votingSeconds = 120f;
        [SerializeField] private float reincarnationSeconds = 20f;

        [Header("流程")]
        [Tooltip("时间耗尽后自动推进下一阶段（演示用；正式流程可关闭，改由 Yarn/行为树驱动）")]
        [SerializeField] private bool autoAdvance = true;

        public GamePhase CurrentPhase { get; private set; } = GamePhase.Investigation;
        public float PhaseTimeRemaining { get; private set; }
        /// <summary>轮回次数（开局为 0，每次轮回结算后 +1）。</summary>
        public int LoopCount { get; private set; }

        /// <summary>结局触发后循环是否已停止（停止后不再计时、不再自动进入下一轮）。</summary>
        public bool LoopHalted { get; private set; }

        /// <summary>
        /// 暂停阶段倒计时（自由打字 / 查看长文本时使用）。
        /// 只停表，不冻结世界：NPC 行为、玩家移动、对话协程照常。
        /// </summary>
        public bool ClockPaused { get; set; }

        public event Action<GamePhase> OnPhaseChanged;
        /// <summary>某阶段时间耗尽（参数：已结束的阶段）。</summary>
        public event Action<GamePhase> OnPhaseTimeExpired;
        /// <summary>进入新一轮（参数：新的轮回数）。</summary>
        public event Action<int> OnLoopAdvanced;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            PhaseTimeRemaining = GetPhaseDuration(CurrentPhase);
        }

        private void Update()
        {
            if (GameMode.MenuActive) return;
            if (LoopHalted || ClockPaused)
            {
                UpdateClockTick();
                return;
            }
            if (PhaseTimeRemaining <= 0f) return;

            PhaseTimeRemaining -= Time.deltaTime;
            UpdateClockTick();
            if (PhaseTimeRemaining > 0f) return;

            PhaseTimeRemaining = 0f;
            UpdateClockTick();
            OnPhaseTimeExpired?.Invoke(CurrentPhase);
            if (autoAdvance) AdvancePhase();
        }

        /// <summary>倒计时最后 10 秒：clock_tick 循环；离开窗口立即停。</summary>
        void UpdateClockTick()
        {
            bool shouldTick = !LoopHalted
                              && CurrentPhase != GamePhase.Reincarnation
                              && PhaseTimeRemaining > 0f
                              && PhaseTimeRemaining <= 10f;
            if (shouldTick == _clockTickActive) return;
            _clockTickActive = shouldTick;
            if (SFXManager.Instance != null)
                SFXManager.Instance.SetClockTick(shouldTick);
        }

        bool _clockTickActive;

        void OnDisable()
        {
            if (_clockTickActive && SFXManager.Instance != null)
                SFXManager.Instance.SetClockTick(false);
            _clockTickActive = false;
        }

        /// <summary>推进到下一阶段：搜证 → 会议 → 投票 → 轮回 → （新轮回）搜证。</summary>
        public void AdvancePhase()
        {
            if (LoopHalted) return;

            switch (CurrentPhase)
            {
                case GamePhase.Investigation:
                    SetPhase(GamePhase.Meeting);
                    break;
                case GamePhase.Meeting:
                    SetPhase(GamePhase.Voting);
                    break;
                case GamePhase.Voting:
                    SetPhase(GamePhase.Reincarnation);
                    break;
                case GamePhase.Reincarnation:
                    BeginNextLoop();
                    break;
            }
        }

        /// <summary>切换阶段并重置该阶段计时。</summary>
        public void SetPhase(GamePhase phase)
        {
            CurrentPhase = phase;
            PhaseTimeRemaining = GetPhaseDuration(phase);
            OnPhaseChanged?.Invoke(phase);
            Debug.Log($"[HilbertLoop] 进入阶段：{GetPhaseDisplayName(phase)}（{PhaseTimeRemaining:0}s）");
        }

        /// <summary>
        /// 延长当前阶段（秒）。用于 9号 / 10号活着时把搜证阶段顺延，
        /// 让他们在会议开始前至少有 5 游戏分钟的参与窗口。
        /// </summary>
        public void ExtendCurrentPhase(float extraSeconds)
        {
            if (LoopHalted || extraSeconds <= 0f) return;

            PhaseTimeRemaining += extraSeconds;
            Debug.Log($"[HilbertLoop] 当前阶段延长 {extraSeconds:0} 秒" +
                      $"（{extraSeconds / 6f:0.#} 游戏分钟），剩余 {PhaseTimeRemaining:0} 秒。");
        }

        /// <summary>开始新一轮轮回：轮回数 +1 并回到搜证阶段。</summary>
        public void BeginNextLoop()
        {
            if (LoopHalted)
            {
                Debug.LogWarning("[HilbertLoop] 循环已停止，忽略 BeginNextLoop。");
                return;
            }

            LoopCount++;
            OnLoopAdvanced?.Invoke(LoopCount);
            SetPhase(GamePhase.Investigation);
            Debug.Log($"[HilbertLoop] 第 {LoopCount} 次轮回开始。");
        }

        /// <summary>
        /// 结局触发：彻底停止阶段计时与自动推进。
        /// 玩家死亡进入结局 B 时必须调用，避免 20 秒后自动回到搜证阶段。
        /// </summary>
        public void HaltLoop()
        {
            if (LoopHalted) return;
            LoopHalted = true;
            PhaseTimeRemaining = 0f;
            Debug.Log("[HilbertLoop] 循环已停止（结局）。");
        }

        public float GetPhaseDuration(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Investigation:
                    // 会议固定在 0:00（第 90 游戏分钟）。玩家醒得越晚，本轮剩下的
                    // 搜证时间越短：1号 540 秒（1:30），2号 360 秒（1:00），8号 180 秒（0:30）。
                    float remainingGameMinutes = Mathf.Max(0.1f,
                        LoopTimeline.TotalGameMinutes - LoopTimeline.PlayerWakeMinute);
                    return Mathf.Min(investigationSeconds,
                        remainingGameMinutes * LoopTimeline.SecondsPerGameMinute);
                case GamePhase.Meeting: return meetingSeconds;
                case GamePhase.Voting: return votingSeconds;
                case GamePhase.Reincarnation: return reincarnationSeconds;
                default: return 0f;
            }
        }

        /// <summary>
        /// 玩家角色确定后，按本轮真实醒来时刻校正搜证倒计时。
        /// 开局 Awake 时 CycleState 可能尚未读取角色，必须在角色落地后补一次。
        /// </summary>
        public void RefreshInvestigationClock()
        {
            if (CurrentPhase != GamePhase.Investigation) return;
            PhaseTimeRemaining = GetPhaseDuration(GamePhase.Investigation);
        }

        /// <summary>当前阶段剩余时间，格式 "MM:SS"。</summary>
        public string GetFormattedTime()
        {
            int total = Mathf.CeilToInt(PhaseTimeRemaining);
            return $"{total / 60:00}:{total % 60:00}";
        }

        public static string GetPhaseDisplayName(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Investigation: return "搜证中";
                case GamePhase.Meeting: return "会议中";
                case GamePhase.Voting: return "投票中";
                case GamePhase.Reincarnation: return "轮回结算";
                default: return phase.ToString();
            }
        }
    }
}
