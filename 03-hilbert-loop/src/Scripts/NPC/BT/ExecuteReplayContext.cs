using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 复现一条情境决策（3.2 修正一·情境A）：
    /// OnEnter 取出「当前情境匹配、未消费、timestamp 最小」的记录并立即标记消费（保证按
    /// timestamp 顺序逐条复现、不重复执行），随后按 action 分派——
    /// 移动类（搜查房间 / 接近NPC / 查看监控 / 藏匿）寻路走过去，瞬时类（纸条读/无视等）输出复现日志。
    /// 复现失败的记录不重试（警告日志后放弃），确保复现链不断流。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Execute Replay Context")]
    public class ExecuteReplayContext : NPCNode
    {
        [Tooltip("瞬时类决策的复现停留时长（秒）")]
        public float holdSeconds = 1.5f;

        ReplayContext _ctx;
        float _holdEnd = -1f;

        public override void OnEnter()
        {
            _holdEnd = -1f;
            _ctx = ReplayContexts.FindMatch(Npc, Board);
            if (_ctx == null) return;

            _ctx.consumed = true; // 立即消费：多条记录按 timestamp 顺序逐条复现
            Debug.Log($"[NPC {Npc.Number}] 复现上一轮决策：{_ctx.trigger} → {_ctx.action}" +
                      $"（上一轮结果：{_ctx.result}）");
        }

        public override NodeResult Execute()
        {
            if (Npc == null) return NodeResult.success;
            if (_ctx == null) return NodeResult.success; // 防御：无记录直接放行

            string action = _ctx.action ?? string.Empty;

            // ---- 纸条决策（瞬时；与既有 notePending 分支衔接：置位后旧分支放行） ----
            if (action == "OpenedNote" || action == "IgnoredNote")
            {
                if (Board != null) Board.noteProcessed = true;
                Debug.Log(action == "OpenedNote"
                    ? $"[NPC {Npc.Number}] 复现：像上一轮的自己一样，展开了纸条阅读。"
                    : $"[NPC {Npc.Number}] 复现：像上一轮的自己一样，把纸条丢在一边（无视）。");
                return NodeResult.success;
            }

            // ---- 移动类：搜查房间 ----
            if (action.StartsWith("SearchedRoom_"))
            {
                int number = ParseTrailingInt(action);
                Transform room = NPCRoomUtil.FindRoom(number);
                if (room == null) return Skip($"想搜查 {number} 号房，但那里不存在。");
                return MoveToAndComplete(room.position, 1.2f, $"复现：去 {number} 号房搜查了一圈。");
            }

            // ---- 移动类：接近NPC ----
            if (action.StartsWith("ApproachedNPC_"))
            {
                int number = ParseTrailingInt(action);
                NPCController target = FindNpc(number);
                if (target == null || target.isDead)
                    return Skip($"想接近 {number} 号，但那人已经不在了。");
                return MoveToAndComplete(target.transform.position, 1.7f, $"复现：主动接近了 {number} 号。");
            }

            // ---- 移动类：查看监控 ----
            if (action == "ViewedMonitor")
            {
                if (!TryGetMonitorPosition(out Vector3 screenPos))
                    return Skip("想查看监控，但监控屏不可用。");
                return MoveToAndComplete(screenPos, 2.2f, "复现：走到监控屏前查看了一遍。");
            }

            // ---- 移动类：藏匿道具（去 8 号房床边） ----
            if (action == "HidItem")
            {
                Transform room8 = NPCRoomUtil.FindRoom(8);
                Transform bed = room8 != null ? room8.Find("Bed") : null;
                if (bed == null) return Skip("想藏东西，但没找到可以藏的地方。");
                return MoveToAndComplete(bed.position, 1.6f, "复现：走到床边，把手里的东西藏了起来。");
            }

            // ---- 瞬时类兜底（弃权 / 使用武器 / 久待等）：短暂停留表示复现完成 ----
            if (_holdEnd < 0f)
            {
                _holdEnd = Time.time + (action == "StayedInRoom" ? holdSeconds * 2.5f : holdSeconds);
                Debug.Log($"[NPC {Npc.Number}] 复现：重演了「{action}」（短暂停留后继续）。");
                return NodeResult.running;
            }
            return Time.time >= _holdEnd ? NodeResult.success : NodeResult.running;
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        NodeResult MoveToAndComplete(Vector3 target, float arriveDistance, string doneMessage)
        {
            NodeResult move = MoveTowards(target, arriveDistance);
            if (move.status == Status.Failure)
            {
                Debug.LogWarning($"[NPC {Npc.Number}] 复现「{_ctx.action}」时路径受阻，放弃这条记录。");
                return NodeResult.failure;
            }
            if (move.status == Status.Running) return NodeResult.running;

            Debug.Log($"[NPC {Npc.Number}] {doneMessage}");
            return NodeResult.success;
        }

        NodeResult Skip(string reason)
        {
            Debug.Log($"[NPC {Npc.Number}] 复现跳过：{reason}");
            return NodeResult.success;
        }

        static int ParseTrailingInt(string action)
        {
            int idx = action.LastIndexOf('_');
            if (idx < 0) return -1;
            return int.TryParse(action.Substring(idx + 1), out int n) ? n : -1;
        }

        static NPCController FindNpc(int number)
        {
            NPCController[] npcs = Object.FindObjectsOfType<NPCController>();
            for (int i = 0; i < npcs.Length; i++)
            {
                if (npcs[i] != null && npcs[i].Number == number) return npcs[i];
            }
            return null;
        }

        static bool TryGetMonitorPosition(out Vector3 position)
        {
            position = default;
            MonitorSystem monitor = MonitorSystem.Instance;
            if (monitor == null) return false;
            if (monitor.dotLayer != null)
            {
                position = monitor.dotLayer.position;
                return true;
            }
            if (monitor.monitorRawImage != null)
            {
                position = monitor.monitorRawImage.transform.position;
                return true;
            }
            return false;
        }
    }
}
