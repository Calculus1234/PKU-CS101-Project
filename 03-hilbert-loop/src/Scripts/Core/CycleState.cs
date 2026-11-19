using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>循环状态：正常 / 被破坏 / 投票中 / 重置中。</summary>
    public enum CyclePhase
    {
        Normal,     // 循环正常运转（全员按流程搜证）
        Disrupted,  // 循环被破坏（出现异常事件）
        Voting,     // 循环裁决（投票进行中）
        Reset,      // 循环重置（轮回结算）
    }

    /// <summary>玩家死亡方式（模块四：结局 D 的冲击力权重）。</summary>
    public enum PlayerDeathType
    {
        None = 0,       // 未死亡
        Killed = 1,     // 被 AI 袭击 / 坠楼 / 意外
        VotedOut = 2,   // 被圆桌投票抹杀
        Sacrifice = 3,  // 主动摘下面具与斗篷（献祭）
    }

    /// <summary>
    /// 循环状态感知（全局静态）。
    /// 3.5 扩展为完整的轮回状态机：
    /// - 玩家号码不再固定为 1 / 8，而是随每轮顺移（playerNumber）；
    /// - 记录存活 / 记忆保留 / 机器故障是否用掉 / 本轮投票出局者；
    /// - 天亮重置时由 CycleResetSystem 调用 PrepareNewLoop 写入下一轮状态。
    /// </summary>
    public static class CycleState
    {
        /// <summary>投票 UI 等数组容量用的上限（10 号只作为历史兼容，不再生成）。</summary>
        public const int MaxNumber = 10;

        /// <summary>循环里真正存在的最大号码。房间只有 1~9，超出的号码不会被生成。</summary>
        public const int MaxLoopNumber = 9;

        /// <summary>角色配置 PlayerPrefs 键（编辑器菜单「HilbertLoop → 循环模拟」写入）。</summary>
        public const string RolePrefKey = "HL_Cycle_Role";

        /// <summary>破局痕迹 PlayerPrefs 键（3.2 修正一：跨轮保留，供 8号AI「复现破局」）。</summary>
        const string DisruptedPrefKey = "HL_Cycle_LastDisrupted";

        /// <summary>
        /// 上一次循环破坏的持久标记（跨轮保留）：
        /// 玩家扮演 1 号拒杀时置位；杀 9 号成功（循环恢复）时清除。
        /// 8号AI 借此判断"上轮破局"，在复现层再现破局行为。
        /// </summary>
        public static bool LastCycleDisrupted => PlayerPrefs.GetInt(DisruptedPrefKey, 0) == 1;

        static void SetLastDisrupted(bool value)
        {
            PlayerPrefs.SetInt(DisruptedPrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>当前循环阶段。</summary>
        public static CyclePhase currentPhase = CyclePhase.Normal;

        /// <summary>当前人口记账（含玩家，最多 9；每轮有人死亡就减一，天亮补入新 1 号后回升）。</summary>
        public static int currentPopulation = 9;

        /// <summary>
        /// 本轮名册声明的最大号码（1~9）。房间只有 1~9，所以它同时也是本轮的“第九席”。
        /// 最大号码的人可能已经死了（例如 9 号尸体），但只要参与过本轮，所有人都知道他存在。
        /// </summary>
        public static int currentKnownMax = 9;

        /// <summary>本循环编号（从 0 开始，天亮后 +1）。</summary>
        public static int loopIndex = 0;

        // ------------------------------------------------------------------
        // 3.5 玩家身份 / 生存状态
        // ------------------------------------------------------------------

        /// <summary>玩家当前号码（1~8；0 = 无座位的观察者）。</summary>
        public static int playerNumber = 0;

        /// <summary>玩家是否存活。死亡后进入幽灵观战，直到轮回重置。</summary>
        public static bool playerAlive = true;

        /// <summary>本轮玩家是否保留了上一轮记忆（机器故障分支）。</summary>
        public static bool playerMemoryRetained = false;

        /// <summary>机器故障是否已经发生过（整局只能一次）。</summary>
        public static bool machineFaultUsed = false;

        /// <summary>本轮投票被抹杀的号码（0 = 无人出局 / 平票）。</summary>
        public static int executedNumber = 0;

        /// <summary>本轮投票是否平票 / 全员弃权。</summary>
        public static bool lastVoteWasTie = false;

        /// <summary>本轮"杀 9 号"是否已执行（玩家或 1 号 AI；模块三 3.2）。</summary>
        public static bool kill9Executed = false;

        // ------------------------------------------------------------------
        // 本轮死亡登记 / 真相认知（重构：死者不会在公聊里“复活”）
        // ------------------------------------------------------------------

        /// <summary>本轮已经死亡的号码。会议 / 投票 / 私聊都必须以此为准，而不仅看场景对象。</summary>
        static readonly HashSet<int> _deadNumbers = new HashSet<int>();

        public static IReadOnlyCollection<int> DeadNumbers => _deadNumbers;
        public static bool IsNumberDead(int number) => _deadNumbers.Contains(number);

        /// <summary>已经被「发现尸体」的号码（没被发现也会在圆桌缺座时暴露）。</summary>
        static readonly HashSet<int> _discoveredBodies = new HashSet<int>();
        public static IReadOnlyCollection<int> DiscoveredBodies => _discoveredBodies;
        public static bool IsBodyDiscovered(int number) => _discoveredBodies.Contains(number);

        /// <summary>NPC 死亡时登记（NPCController.Die / NinthVictim 调用）。</summary>
        public static void NotifyNpcDeath(int number)
        {
            if (number <= 0) return;
            if (_deadNumbers.Add(number))
                Debug.Log($"[CycleState] 登记死者：{number}号（本轮死亡 {_deadNumbers.Count} 人）。");
        }

        /// <summary>尸体被谁看见 / 检查：从这一刻起，NPC 的对话里才会出现“他已经死了”。</summary>
        public static void NotifyBodyDiscovered(int number)
        {
            if (number <= 0) return;
            if (_discoveredBodies.Add(number))
                Debug.Log($"[CycleState] 发现尸体：{number}号（已发现 {_discoveredBodies.Count} 具）。");
        }

        /// <summary>玩家是否已经主动摘下面具与斗篷（真结局的硬前提之一）。</summary>
        public static bool playerDisguiseRemoved = false;

        /// <summary>玩家是否在圆桌监控上看过本轮公开回放（1:30 / 1:05 / 各人出门时间）。</summary>
        public static bool videoLogWatched = false;

        /// <summary>是否检查过 9 号的尸体（确认死因与床上状态）。</summary>
        public static bool ninthBodyInspected = false;

        /// <summary>是否摘下过 9 号的面具（与玩家自己摘面具的先后顺序不限）。</summary>
        public static bool ninthMaskRemoved = false;

        /// <summary>玩家是否亲眼看见过9号面具下的脸（先摘自己或先摘9号都可能）。</summary>
        public static bool playerSawNinthFace = false;

        /// <summary>玩家是否已经完成“自己的脸 ↔ 9号的脸”的对比。</summary>
        public static bool faceComparisonDone = false;

        public static void NotifyPlayerSawNinthFace()
        {
            if (playerSawNinthFace) return;
            playerSawNinthFace = true;
            Debug.Log("[CycleState] 玩家亲眼看见了9号面具下的脸。");
        }

        public static void NotifyPlayerDisguiseRemoved()
        {
            if (playerDisguiseRemoved) return;
            playerDisguiseRemoved = true;
            Debug.Log("[CycleState] 玩家摘下了面具与斗篷：脸已经暴露在所有人面前。");

            // 如果玩家先摘了9号的面具、并且真的看见过那张脸，
            // 直到这一刻才第一次看见自己的脸：两张脸在这时才对上。
            if (ninthMaskRemoved && playerSawNinthFace && NotifyFaceComparison())
            {
                EvidenceLedger.Record("face_compare", "两张一模一样的脸",
                    "你先摘下了9号的面具，又摘下了自己的。窗边的镜子里和床上是同一张脸——" +
                    "不是相似，是完全相同。", 65);
                Debug.Log("[CycleState] 先摘9号面具、后摘自己面具：脸对比在此刻补完。");
            }
        }

        public static void NotifyVideoLogWatched()
        {
            if (videoLogWatched) return;
            videoLogWatched = true;
            Debug.Log("[CycleState] 玩家看完了圆桌监控的公开回放。");
        }

        public static void NotifyNinthBodyInspected()
        {
            if (ninthBodyInspected) return;
            ninthBodyInspected = true;
            Debug.Log("[CycleState] 玩家检查了9号的尸体。");
        }

        public static void NotifyNinthMaskRemoved()
        {
            if (ninthMaskRemoved) return;
            ninthMaskRemoved = true;
            Debug.Log("[CycleState] 玩家摘下了9号的面具。");
        }

        /// <summary>玩家完成脸对比。只有玩家自己先摘下面具，这个认知才成立。</summary>
        public static bool NotifyFaceComparison()
        {
            if (!playerDisguiseRemoved || !ninthMaskRemoved) return false;
            if (faceComparisonDone) return true;
            faceComparisonDone = true;
            Debug.Log("[CycleState] 脸对比完成：玩家与9号长得一模一样。");
            return true;
        }

        // ------------------------------------------------------------------
        // 模块四：破局判定用的本轮轨迹
        // ------------------------------------------------------------------

        /// <summary>核心真相证据的权重门槛：会议 / 私聊中出示权重 ≥ 此值的证据才算"核心"。</summary>
        public const int CoreEvidenceWeightThreshold = 20;

        /// <summary>玩家死亡方式（结局 D 的冲击力权重）。</summary>
        public static PlayerDeathType playerDeathType = PlayerDeathType.None;

        /// <summary>
        /// 本轮玩家出示过的核心真相证据（按物品 id 去重）。
        /// 结局 C 要求 ≥2 件；结局 D 用数量计算概率。
        /// </summary>
        static readonly List<string> _revealedCoreEvidence = new List<string>();

        /// <summary>本轮核心真相证据数量（去重）。</summary>
        public static int revealedCoreEvidenceCount => _revealedCoreEvidence.Count;

        /// <summary>本轮玩家发起过的私聊轮数（含话题 / 出示证据）。</summary>
        public static int privateChatCount = 0;

        /// <summary>本轮私聊累计带来的信任提升（调试 / 结局 D 展示用）。</summary>
        public static float privateChatTrustGain = 0f;

        /// <summary>玩家本轮是否抽到 1 号身份（兼容旧接口）。</summary>
        public static bool isPlayerAs1 => playerNumber == 1;

        /// <summary>玩家本轮是否抽到 8 号身份（兼容旧接口）。</summary>
        public static bool isPlayerAs8 => playerNumber == 8;

        // ------------------------------------------------------------------
        // 角色配置（每局开始时由 CycleController.Awake 调用）
        // ------------------------------------------------------------------

        /// <summary>从 PlayerPrefs 读取本轮角色配置并复位循环记账。</summary>
        public static void ConfigureFromPreferences()
        {
            string role = PlayerPrefs.GetString(RolePrefKey, "8");
            if (int.TryParse(role, out int parsed) && parsed >= 1 && parsed <= MaxLoopNumber)
            {
                playerNumber = parsed;
            }
            else
            {
                // "other" 等：玩家以旁观者身份出现（演示 8号AI 复现 / 1号AI 流程用）
                playerNumber = 0;
            }

            currentPhase = CyclePhase.Normal;
            currentPopulation = 9;
            currentKnownMax = 9;
            loopIndex = 0;
            playerAlive = true;
            playerMemoryRetained = false;
            machineFaultUsed = false;
            executedNumber = 0;
            lastVoteWasTie = false;
            kill9Executed = false;
            ResetLoopSocialState();

            Debug.Log($"[CycleState] 本轮角色配置：玩家扮演 " +
                      $"{(playerNumber > 0 ? playerNumber + "号" : "其他号码（观察者）")}（key={role}）" +
                      $"——初始 Normal / 人口 {currentPopulation}。");
        }

        // ------------------------------------------------------------------
        // 状态切换（模块三 3.2）
        // ------------------------------------------------------------------

        /// <summary>
        /// 主菜单「继续」：把存档中的周目 / 角色 / 存活 / 死亡 / 机器故障写回运行时。
        /// 场景加载完成后由 SaveManager.ApplyCachedToCycleState 调用。
        /// </summary>
        public static void RestoreFromSave(int savedLoopIndex, int savedPlayerNumber, bool savedPlayerAlive,
            bool savedMemoryRetained, bool savedMachineFault, int savedPopulation, int savedKnownMax,
            IReadOnlyList<int> savedAliveNumbers, IReadOnlyList<int> savedDead)
        {
            loopIndex = Mathf.Max(0, savedLoopIndex);
            playerNumber = Mathf.Clamp(savedPlayerNumber, 0, MaxLoopNumber);
            playerAlive = savedPlayerAlive;
            playerMemoryRetained = savedMemoryRetained;
            machineFaultUsed = savedMachineFault;
            currentPopulation = Mathf.Max(1, savedPopulation);

            // 旧存档没有 knownMax：用活着的人 + 死者 + 玩家号码推断。
            // 9 号尸体也算“参与过本轮”，所以死亡名单同样参与取最大值。
            int knownMax = savedKnownMax;
            if (knownMax <= 0)
            {
                knownMax = playerAlive ? playerNumber : 0;
                if (savedAliveNumbers != null)
                    for (int i = 0; i < savedAliveNumbers.Count; i++)
                        knownMax = Mathf.Max(knownMax, savedAliveNumbers[i]);
                if (savedDead != null)
                    for (int i = 0; i < savedDead.Count; i++)
                        knownMax = Mathf.Max(knownMax, savedDead[i]);
            }
            currentKnownMax = Mathf.Clamp(knownMax > 0 ? knownMax : 9, 1, MaxLoopNumber);

            currentPhase = CyclePhase.Normal;
            executedNumber = 0;
            lastVoteWasTie = false;
            kill9Executed = false;

            ResetLoopSocialState();
            if (savedDead != null)
            {
                for (int i = 0; i < savedDead.Count; i++)
                {
                    int number = savedDead[i];
                    if (number > 0) _deadNumbers.Add(number);
                }
            }

            Debug.Log($"[CycleState] 从存档恢复：第 {loopIndex} 周目，玩家 {playerNumber} 号，" +
                      $"存活={playerAlive}，机器故障={machineFaultUsed}，" +
                      $"存活名单={(savedAliveNumbers != null ? string.Join("、", savedAliveNumbers) : "无")}。");
        }

        /// <summary>
        /// 结局后重新开始：清空本局全部循环状态。
        /// 角色配置不在这里读取，场景重载后由 CycleController.Awake 重新调用
        /// ConfigureFromPreferences。
        /// </summary>
        public static void ResetForNewGame()
        {
            currentPhase = CyclePhase.Normal;
            currentPopulation = 9;
            currentKnownMax = 9;
            loopIndex = 0;
            playerNumber = 0;
            playerAlive = true;
            playerMemoryRetained = false;
            machineFaultUsed = false;
            executedNumber = 0;
            lastVoteWasTie = false;
            kill9Executed = false;
            ResetLoopSocialState();
            SetLastDisrupted(false);
            PlayerPrefs.Save();

            Debug.Log("[CycleState] 新游戏：循环状态已清空。");
        }

        /// <summary>杀 9 号执行完成（玩家手动或 1 号 AI）：循环照常运转（Normal / 人口 9）。</summary>
        public static void NotifyKill9Executed()
        {
            if (kill9Executed) return;

            kill9Executed = true;
            currentPhase = CyclePhase.Normal; // 循环照常
            // 杀 9 号不改变人口：正常循环保持 9；3.5 轮次规模缩小后保持当前人数；
            // 仅「拒杀破坏」的 10 人状态会回落到 9（9 号重新成为尸体）。
            currentPopulation = Mathf.Min(currentPopulation, 9);
            SetLastDisrupted(false);          // 循环恢复：破局痕迹清除

            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log("[CycleState] 9号已被杀死——循环照常运转（Normal / 人口 9）。");
        }

        /// <summary>拒绝杀 9 号（玩家扮演 1 号且超过执行窗口）：循环被破坏（Disrupted / 人口 10）。</summary>
        public static void NotifyKill9Refused()
        {
            if (kill9Executed) return; // 已经杀过了，不存在"拒绝"
            if (currentPhase == CyclePhase.Disrupted) return; // 防重复触发（Update / 外部调用）

            currentPhase = CyclePhase.Disrupted;
            // 9 号没有死，但这一轮仍然只有 9 个人、9 个房间：
            // 天亮时活下来的 9 号会成为新的 1 号，其他人整体 +1。
            currentPopulation = Mathf.Min(currentPopulation, MaxLoopNumber);
            SetLastDisrupted(true);    // 破局痕迹：跨轮保留，供 NPC 的谨慎 / 怀疑倾向使用

            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log("[CycleState] 1号没有动手——这一轮多活了一个人（Disrupted）。" +
                      "9号会在第 85 游戏分钟醒来；天亮后号码继续顺移。");
        }

        /// <summary>
        /// 天亮顺移后仍处于「9号没有被杀」的破坏状态。
        /// 当前剧本里 9号必定在会议前被杀青，因此正常情况下不会走到这里；
        /// 保留该入口只为兼容旧存档 / 调试强制切换。异常由活着的 9号承载，不再生成 10号。
        /// </summary>
        public static void MarkDisruptedCarryover()
        {
            currentPhase = CyclePhase.Disrupted;
            currentPopulation = Mathf.Min(currentPopulation, MaxLoopNumber);
            SetLastDisrupted(true);
            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log("[CycleState] 新一轮仍处于破坏状态：异常由活着的 9号承载（当前剧本不生成 10号）。");
        }

        /// <summary>
        /// 人数变化接口：每轮会议投票后由 VotingSystem 调用，
        /// 人数变化时广播状态变化，让 NPC 行为树重新读取。
        /// </summary>
        public static void ApplyPopulationChange(int newPopulation, string reason = null)
        {
            if (newPopulation == currentPopulation) return;

            currentPopulation = newPopulation;
            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log($"[CycleState] 人口变化：{currentPopulation}（{reason ?? "未说明"}），已广播循环状态。");
        }

        // ------------------------------------------------------------------
        // 3.5 投票 / 死亡 / 轮回
        // ------------------------------------------------------------------

        /// <summary>投票开始（VotingSystem 调用）：切换循环阶段并清空上一轮结果。</summary>
        public static void NotifyVotingStarted()
        {
            currentPhase = CyclePhase.Voting;
            executedNumber = 0;
            lastVoteWasTie = false;
            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log("[CycleState] 投票阶段开始。");
        }

        /// <summary>
        /// 投票结算完成：记录出局者并更新人口。
        /// population 由 VotingSystem 按"存活人数 + 9号尸体"计算后传入。
        /// </summary>
        public static void NotifyVoteResult(int executed, bool tie, int population)
        {
            executedNumber = Mathf.Max(0, executed);
            lastVoteWasTie = tie || executed <= 0;

            if (currentPopulation != population)
            {
                currentPopulation = population;
                NPCEventBus.RaiseCycleStateChanged(currentPhase);
            }

            Debug.Log(lastVoteWasTie
                ? "[CycleState] 投票平票 / 全员弃权：本轮无人出局。"
                : $"[CycleState] 投票出局：{executedNumber}号（人口 → {currentPopulation}）。");
        }

        /// <summary>玩家死亡（投票抹杀 / 锤子反杀 / 坠落 / 主动献祭）：记录死亡方式，进入幽灵观战。</summary>
        public static void NotifyPlayerDeath(string cause, PlayerDeathType type = PlayerDeathType.Killed)
        {
            if (!playerAlive) return;
            playerAlive = false;
            playerDeathType = type;
            Debug.LogWarning($"[CycleState] 玩家（{playerNumber}号）死亡：{cause}。" +
                             $"方式={type}。进入幽灵观战。");
        }

        /// <summary>
        /// 记录玩家出示过的一件核心真相证据（按 id 去重）。
        /// 只有权重 ≥ CoreEvidenceWeightThreshold 的证据才会被记入。
        /// </summary>
        public static bool RecordCoreEvidence(string itemId, int evidenceWeight)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            if (evidenceWeight < CoreEvidenceWeightThreshold) return false;
            if (_revealedCoreEvidence.Contains(itemId)) return false;

            _revealedCoreEvidence.Add(itemId);
            Debug.Log($"[CycleState] 核心真相证据 +1：{itemId}（权重 {evidenceWeight}），" +
                      $"本轮累计 {_revealedCoreEvidence.Count} 件。");
            return true;
        }

        public static IReadOnlyList<string> RevealedCoreEvidence => _revealedCoreEvidence;

        /// <summary>存活 NPC 对玩家的平均信任（无存活 NPC 时返回 0）。</summary>
        public static float GetAverageTrustTowardPlayer()
        {
            if (playerNumber <= 0) return 0f;

            float sum = 0f;
            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;
                sum += npc.blackboard.GetTrust(playerNumber);
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        /// <summary>对玩家信任度 ≥ threshold 的存活 NPC 数量（结局 C/D 的"亲密关系"指标）。</summary>
        public static int GetHighTrustNpcCount(float threshold = 70f)
        {
            if (playerNumber <= 0) return 0;

            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;
                if (npc.blackboard.GetTrust(playerNumber) >= threshold) count++;
            }
            return count;
        }

        /// <summary>当前处于恐慌 / 高压状态的存活 NPC 数量（结局 D 的"全场恐慌"指标）。</summary>
        public static int GetPanickingNpcCount()
        {
            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;
                if (npc.blackboard.panicUntil > Time.time || npc.blackboard.stress >= 70f) count++;
            }
            return count;
        }

        /// <summary>当前实际存活的参与者数量（玩家 + 存活 NPC）。结局 C/D 的硬门槛。</summary>
        public static int GetLivingParticipantCount()
        {
            int count = playerAlive && playerNumber > 0 ? 1 : 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead) count++;
            }
            return count;
        }

        /// <summary>当前实际存活的 NPC 数量。</summary>
        public static int GetLivingNpcCount()
        {
            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead) count++;
            }
            return count;
        }

        /// <summary>存活 NPC 的平均精神压力（0~100）。</summary>
        public static float GetAverageNpcStress()
        {
            float sum = 0f;
            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;
                sum += npc.blackboard.stress;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        static void ResetLoopSocialState()
        {
            _revealedCoreEvidence.Clear();
            _deadNumbers.Clear();
            _discoveredBodies.Clear();
            privateChatCount = 0;
            privateChatTrustGain = 0f;
            playerDeathType = PlayerDeathType.None;
            playerDisguiseRemoved = false;
            videoLogWatched = false;
            ninthBodyInspected = false;
            ninthMaskRemoved = false;
            playerSawNinthFace = false;
            faceComparisonDone = false;
            EvidenceLedger.Reset();
        }

        /// <summary>投票进入轮回结算阶段。</summary>
        public static void NotifyReincarnationStarted()
        {
            currentPhase = CyclePhase.Reset;
            NPCEventBus.RaiseCycleStateChanged(currentPhase);
        }

        /// <summary>
        /// 天亮重置完成后，写入下一轮状态并复位本轮临时字段。
        /// </summary>
        public static void PrepareNewLoop(int newPlayerNumber, bool alive, bool memoryRetained,
                                          bool machineFaultTriggered, int population, int knownMax)
        {
            loopIndex++;
            playerNumber = newPlayerNumber;
            playerAlive = alive;
            playerMemoryRetained = memoryRetained;
            if (machineFaultTriggered) machineFaultUsed = true;

            currentPhase = CyclePhase.Normal;
            currentPopulation = population;
            currentKnownMax = Mathf.Clamp(knownMax, 1, MaxLoopNumber);
            kill9Executed = false;
            executedNumber = 0;
            lastVoteWasTie = false;
            ResetLoopSocialState();

            NPCEventBus.RaiseCycleStateChanged(currentPhase);
            Debug.Log($"[CycleState] 进入第 {loopIndex} 轮：玩家 {(playerNumber > 0 ? playerNumber + "号" : "观察者")}，" +
                      $"本轮最大号码 {currentKnownMax}，人口 {population}。");
        }

        /// <summary>清空玩家身份（幽灵观战期间投票 UI 使用）。</summary>
        public static bool PlayerHasVote => playerAlive && playerNumber > 0;
    }
}
