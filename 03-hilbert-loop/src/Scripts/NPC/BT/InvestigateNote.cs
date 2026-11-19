using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 调查纸条节点。模块三 3.2 起：
    /// 8号 AI（读档复现）按上一轮玩家对纸条的态度输出决策日志——
    /// 读过（noteTrusted 高 → 按纸条自保；低 → 不当回事）；无视 → 像上一轮的自己一样无视；
    /// 其他号码 / 无记录 → 中性调查日志。
    /// 决策完成后置位 blackboard.noteProcessed：纸条只决策一次，条件失活后放行后续分支。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Investigate Note")]
    public class InvestigateNote : NPCNode
    {
        public override NodeResult Execute()
        {
            int number = Npc != null ? Npc.Number : -1;
            NPCBlackboard b = Board;

            if (b != null) b.noteProcessed = true;

            if (b != null && b.replayFromPlayer)
            {
                if (b.replayedIgnoredNote)
                {
                    Debug.Log($"[NPC {number}] 复现上一轮玩家的纸条决策：无视——像上一轮的自己一样，把纸条丢在了一边。");
                }
                else if (b.replayedOpenedNote)
                {
                    Debug.Log($"[NPC {number}] 复现上一轮玩家的纸条决策：读过纸条（noteTrusted={b.noteTrusted}）——" +
                              (b.noteTrusted ? "相信上面的警告，之后按纸条自保。" : "但没把上面的警告当回事。"));
                }
                else
                {
                    Debug.Log($"[NPC {number}] 开始调查手中的纸条（上一轮玩家对纸条没有明确决策记录）。");
                }
                return NodeResult.success;
            }

            Debug.Log($"[NPC {number}] 开始调查手中的纸条。");
            return NodeResult.success;
        }
    }
}
