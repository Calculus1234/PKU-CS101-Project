using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 原地待命占位节点（死亡 / 投票等阶段保持静止，不做任何动作）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Do Nothing")]
    public class DoNothing : NPCNode
    {
        public override NodeResult Execute()
        {
            return NodeResult.success;
        }
    }
}
