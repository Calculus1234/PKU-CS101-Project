using System;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 8号（玩家）在上一轮做出的「带情境的关键决策」一条记录（模块三 3.2 修正一）。
    /// 记录层不再只存 5 个性格数值，而是存「触发情境 → 决策 → 结果」序列（附本轮内相对时间）；
    /// 复现层按当前情境匹配 trigger → 精确执行 action；无匹配时才用数值倾向兜底。
    /// </summary>
    [Serializable]
    public class ReplayContext
    {
        [Tooltip("触发情境标识（ReplayContexts 词表）")]
        public string trigger;

        [Tooltip("做出的决策（如 OpenedNote / SearchedRoom_3 / ApproachedNPC_7 / HidItem）")]
        public string action;

        [Tooltip("决策导致的结果（如 NoteRead / ClueFound / Contacted / ItemHidden）")]
        public string result;

        [Tooltip("本轮内的相对时间（秒）：多条命中时按升序逐条复现")]
        public float timestamp;

        /// <summary>是否已被复现层消费（防止重复执行；不入持久化语义）。</summary>
        public bool consumed;

        public ReplayContext() { }

        public ReplayContext(string trigger, string action, string result, float timestamp)
        {
            this.trigger = trigger;
            this.action = action;
            this.result = result;
            this.timestamp = timestamp;
            this.consumed = false;
        }
    }

    /// <summary>
    /// 情境识别与匹配工具（模块三 3.2 修正一）：
    /// 记录层（玩家视角）与复现层（8号AI视角）共用同一套 trigger 词表与识别规则，
    /// 保证「上一轮记录下来的情境」与「本轮复现时身处的情境」可以直接对齐。
    /// 判断依据：循环状态 / 9号存活 / 当前人数 / 玩家1号抉择 / 当前阶段 / 周围人员。
    /// </summary>
    public static class ReplayContexts
    {
        // ------------------------------------------------------------------
        // 情境（trigger）词表
        // ------------------------------------------------------------------

        /// <summary>8号房独处（周遭无人）。</summary>
        public const string Room8Alone = "Room_8_Alone";

        /// <summary>搜证阶段·在某个房间内（可搜查）。</summary>
        public const string SearchStageRoomFree = "SearchStage_RoomFree";

        /// <summary>搜证阶段·走廊上遇到人。</summary>
        public const string CorridorEncounter = "CorridorEncounter";

        /// <summary>搜证阶段·持有道具且无人观察。</summary>
        public const string HasItemNoObserver = "HasItem_NoObserver";

        /// <summary>会议 / 投票阶段轮到自己表态。</summary>
        public const string MeetingTurnToSpeak = "Meeting_TurnToSpeak";

        /// <summary>循环被破坏（Disrupted）的世界。</summary>
        public const string CycleDisrupted = "Cycle_Disrupted";

        /// <summary>是否处于"循环被破坏"的世界（当轮破坏 或 上轮破局痕迹）。</summary>
        public static bool IsDisruptedWorld =>
            CycleState.currentPhase == CyclePhase.Disrupted || CycleState.LastCycleDisrupted;

        /// <summary>
        /// 按世界状态识别当前情境（记录 / 复现共用）。
        /// roomName = 自己所在房间名（"N号房"，不在房间为 null）；otherPersonNear = 附近是否有其他人。
        /// </summary>
        public static string Identify(string roomName, bool otherPersonNear)
        {
            if (CycleState.currentPhase == CyclePhase.Disrupted) return CycleDisrupted;

            GamePhase phase = GameManager.Instance != null ? GameManager.Instance.CurrentPhase : GamePhase.Investigation;
            if (phase == GamePhase.Meeting || phase == GamePhase.Voting) return MeetingTurnToSpeak;

            if (roomName == "8号房" && !otherPersonNear) return Room8Alone;
            if (!string.IsNullOrEmpty(roomName)) return SearchStageRoomFree;
            if (otherPersonNear) return CorridorEncounter;
            return HasItemNoObserver;
        }

        // ------------------------------------------------------------------
        // 复现层匹配
        // ------------------------------------------------------------------

        /// <summary>
        /// 8号AI复现：找出「当前情境匹配、尚未消费、timestamp 最小」的一条记录；
        /// 无匹配返回 null（由行为树的数值倾向分支兜底）。
        /// </summary>
        public static ReplayContext FindMatch(NPCController npc, NPCBlackboard board)
        {
            if (npc == null || board == null || board.replayContexts == null) return null;

            // 当轮循环被破坏 → 由虚假认知驱动接管（3.2 修正一·情境B），复现层让位不匹配
            if (CycleState.currentPhase == CyclePhase.Disrupted) return null;

            bool personNear = AnyOtherPersonNear(npc, npc.transform.position, 3.5f, true);
            string context = Identify(board.currentRoomName, personNear);
            if (string.IsNullOrEmpty(context)) return null;

            ReplayContext best = null;
            for (int i = 0; i < board.replayContexts.Count; i++)
            {
                ReplayContext c = board.replayContexts[i];
                if (c == null || c.consumed || c.trigger != context) continue;
                if (best == null || c.timestamp < best.timestamp) best = c;
            }
            return best;
        }

        /// <summary>
        /// 附近（radius 米内）是否有别人：其他 NPC（可含玩家）。
        /// self = 发起检查的 NPC（排除自己）；includePlayer = 是否把玩家也算作人。
        /// </summary>
        public static bool AnyOtherPersonNear(NPCController self, Vector3 position, float radius, bool includePlayer)
        {
            NPCController[] npcs = UnityEngine.Object.FindObjectsOfType<NPCController>();
            for (int i = 0; i < npcs.Length; i++)
            {
                NPCController npc = npcs[i];
                if (npc == null || npc == self || npc.isDead || !npc.gameObject.activeInHierarchy) continue;
                if (Vector3.Distance(position, npc.transform.position) <= radius) return true;
            }

            if (includePlayer)
            {
                PlayerController player = UnityEngine.Object.FindObjectOfType<PlayerController>();
                if (player != null && Vector3.Distance(position, player.transform.position) <= radius) return true;
            }

            return false;
        }
    }
}
