using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>威胁检查：怀疑度超过阈值且名下有怀疑对象（→ 前往查看）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Threat")]
    public class ThreatCondition : NPCCondition
    {
        [Tooltip("触发怀疑的怀疑度阈值")]
        public float suspicionThreshold = 70f;

        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && b.suspicion > suspicionThreshold
                && b.suspectNumbers != null && b.suspectNumbers.Count > 0;
        }
    }
}
