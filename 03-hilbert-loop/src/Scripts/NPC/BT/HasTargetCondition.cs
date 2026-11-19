using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>存在移动目标：黑板持有一个目标对象（→ 移动过去）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Has Target")]
    public class HasTargetCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && b.currentTarget != null;
        }
    }
}
