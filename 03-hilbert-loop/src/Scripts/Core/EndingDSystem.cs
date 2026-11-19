using System.Text;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 结局 D 的「顿悟」判定（重构：确定性，不再掷骰）。
    ///
    /// 顿悟不是玩家一死就自动发生，也不是一个 5%~95% 的概率。
    /// 它必须同时满足：
    ///   1. 玩家自己在活着时摘下了面具与斗篷（CycleState.playerDisguiseRemoved）；
    ///   2. 9 号的面具被揭开，至少完成过一次「两张脸」的对比
    ///      （CycleState.faceComparisonDone —— 玩家做的，或 NPC 自己做的都算）；
    ///   3. 至少还有 2 个活人；
    ///   4. 至少 2 个活人真正知道「两张脸一模一样」；
    ///   5. 其中至少 1 个人还把另一条物证（监控回放 / 墙钉 / 房间道具 / 9号尸体）
    ///      拼进了同一条逻辑。
    ///
    /// 条件不满足时，天亮只会进入结局 B；幽灵 HUD 会把具体缺哪一环直接写出来，
    /// 让玩家知道自己为什么没能让他们顿悟。
    /// </summary>
    public class EndingDSystem : MonoBehaviour
    {
        public static EndingDSystem Instance { get; private set; }

        [Header("引用（可留空，运行时自找）")]
        public PlayerDeathSystem deathSystem;
        public PlayerController player;

        public bool IsArmed { get; private set; }
        public bool IsResolved { get; private set; }

        public PlayerDeathType DeathType { get; private set; }
        public string DeathCause { get; private set; } = string.Empty;

        /// <summary>最近一次判定的结果：true = 结局 D，false = 结局 B。</summary>
        public bool LastResolvedToD { get; private set; }

        /// <summary>判定说明（失败时写清楚缺哪一环）。</summary>
        public string LastReason { get; private set; } = string.Empty;

        /// <summary>判定明细（调试 / 幽灵 HUD）。</summary>
        public string LastBreakdown { get; private set; } = string.Empty;

        public int LivingNpcCount { get; private set; }
        public int InformedNpcCount { get; private set; }
        public int CorroboratedNpcCount { get; private set; }

        void Awake()
        {
            Instance = this;
            if (deathSystem == null) deathSystem = FindObjectOfType<PlayerDeathSystem>();
            if (player == null) player = FindObjectOfType<PlayerController>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 装配 / 判定
        // ------------------------------------------------------------------

        /// <summary>玩家死亡时调用：记录死亡方式，并立刻评估一次（不立即触发结局）。</summary>
        public void Arm(PlayerDeathType type, string cause)
        {
            if (IsResolved) return;

            IsArmed = true;
            DeathType = type;
            DeathCause = string.IsNullOrEmpty(cause) ? "你死在了这一轮。" : cause;

            bool can = Evaluate();
            Debug.Log($"[EndingD] 已装配顿悟判定：方式={type}，当前结论={(can ? "条件达成" : "条件不足")}。{LastBreakdown}");
        }

        /// <summary>幽灵快进：立刻进行确定性判定（K 键 / 快进按钮）。</summary>
        public void ResolveByFastForward()
        {
            Resolve("快进");
        }

        /// <summary>天亮仍未触发结局 D：最后判定一次；失败进入结局 B。</summary>
        public void ResolveAtDawn()
        {
            Resolve("天亮");
        }

        void Resolve(string moment)
        {
            if (!IsArmed || IsResolved) return;

            IsResolved = true;
            IsArmed = false;

            bool success = Evaluate();
            LastResolvedToD = success;

            Debug.Log($"[EndingD] {moment}判定：{(success ? "顿悟成立" : "顿悟不成立")}。{LastReason}\n{LastBreakdown}");

            if (EndingSystem.Instance == null)
            {
                Debug.LogError("[EndingD] 场景缺少 EndingSystem，无法播放结局。");
                return;
            }

            if (success)
            {
                EndingSystem.Instance.TriggerEndingD(
                    "他们不是因为你死了才顿悟的。\n" +
                    "他们先看见了你摘下面具的脸，又看见了 9 号面具下那张一模一样的脸；" +
                    "锁着的门、窗台的磁铁、墙外的钉子把这条逻辑合上了。\n" +
                    LastReason);
            }
            else
            {
                EndingSystem.Instance.TriggerEndingB(
                    $"你死在了循环结束之前（{DeathCause}）。\n{LastReason}");
            }
        }

        /// <summary>
        /// 确定性评估：不掷骰，只看世界里真实发生过什么。
        /// </summary>
        public bool Evaluate()
        {
            LivingNpcCount = 0;
            InformedNpcCount = 0;
            CorroboratedNpcCount = 0;

            StringBuilder breakdown = new StringBuilder();

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || !npc.gameObject.activeInHierarchy) continue;
                LivingNpcCount++;

                NPCBlackboard b = npc.blackboard;
                if (b == null) continue;

                bool informed = b.knowsFacesIdentical
                    || (b.knownClues != null && b.knownClues.Contains("faces_identical"));
                if (informed)
                {
                    InformedNpcCount++;
                    if (HasCorroboration(b)) CorroboratedNpcCount++;
                }

                breakdown.Append($"{npc.Number}号[" );
                breakdown.Append(informed ? "见过两张脸" : "还不知道脸相同");
                if (informed && HasCorroboration(b)) breakdown.Append("+物证");
                breakdown.Append("] ");
            }

            bool playerExposed = CycleState.playerDisguiseRemoved;
            bool faceCompared = CycleState.faceComparisonDone;

            if (!playerExposed)
            {
                LastReason = "你的脸一直藏在面具后面。没有人看见过你的脸，也就没有人能把 9 号的脸和你的脸放在一起。";
                LastBreakdown = $"玩家摘面具={playerExposed}，脸对比={faceCompared}，活人={LivingNpcCount}，" +
                                $"知道脸相同={InformedNpcCount}，拼进物证={CorroboratedNpcCount}";
                return false;
            }

            if (!faceCompared)
            {
                LastReason = "9 号的面具还扣在脸上。没有活人见过那张脸，也没有人完成过「两张脸」的对比。";
                LastBreakdown = $"玩家摘面具={playerExposed}，脸对比={faceCompared}，活人={LivingNpcCount}，" +
                                $"知道脸相同={InformedNpcCount}，拼进物证={CorroboratedNpcCount}";
                return false;
            }

            if (LivingNpcCount < 2)
            {
                LastReason = "圆桌旁已经凑不齐两个活人。就算有人想通了，也没有「所有人一起摘下面具」的崩塌。";
                LastBreakdown = $"活人={LivingNpcCount}（至少需要 2），知道脸相同={InformedNpcCount}";
                return false;
            }

            if (InformedNpcCount < 2)
            {
                LastReason = $"只有 {InformedNpcCount} 个活人真正知道「两张脸一模一样」。一个人想通不算顿悟。";
                LastBreakdown = $"活人={LivingNpcCount}，知道脸相同={InformedNpcCount}（至少需要 2）";
                return false;
            }

            if (CorroboratedNpcCount < 1)
            {
                LastReason = "有人看见了相同的脸，但锁着的门、窗台的磁铁、墙外的钉子还没有被拼进同一条逻辑。" +
                             "只有一张脸，没有证据链，他们只会以为那是错觉。";
                LastBreakdown = $"活人={LivingNpcCount}，知道脸相同={InformedNpcCount}，" +
                                $"把物证拼进来={CorroboratedNpcCount}（至少需要 1）";
                return false;
            }

            LastReason = $"{InformedNpcCount} 个活人知道你们共用同一张脸，" +
                         $"其中 {CorroboratedNpcCount} 个人把门锁 / 磁铁 / 钉子 / 监控 / 尸体里的至少一条拼了进来。";
            LastBreakdown = $"玩家摘面具={playerExposed}，脸对比={faceCompared}，活人={LivingNpcCount}，" +
                            $"知道脸相同={InformedNpcCount}，拼进物证={CorroboratedNpcCount}｜{breakdown}";
            return true;
        }

        static bool HasCorroboration(NPCBlackboard b)
        {
            if (b == null) return false;
            if (b.sawNinthBody || b.sawNinthMaskOff) return true;
            if (b.knownClues == null) return false;

            for (int i = 0; i < b.knownClues.Count; i++)
            {
                string clue = b.knownClues[i];
                if (string.IsNullOrEmpty(clue)) continue;
                if (clue == "video_log"
                    || clue == "room_items_identical"
                    || clue == "ninth_room_items"
                    || clue == "ninth_body"
                    || clue == "saw_ninth_body"
                    || clue == "wall_nails"
                    || clue == "dagger_blood"
                    || clue == "ninth_face")
                    return true;
            }
            return false;
        }
    }
}
