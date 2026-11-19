using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 纸条待调查：持有纸条且尚未做出决策（8号的信息状态；条件对所有号码通用）。
    /// 3.2：8号 AI 一次性复现「读 / 无视」决策后置 noteProcessed，条件随之失活并放行后续分支。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Note Pending")]
    public class NotePendingCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && b.hasNote && !b.noteProcessed;
        }
    }
}
