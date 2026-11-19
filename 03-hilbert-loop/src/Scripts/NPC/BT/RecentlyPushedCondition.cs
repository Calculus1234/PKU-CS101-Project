using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>推搡对峙检查：刚被推搡（pushReactionUntil 时间窗内）且记得推搡者。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Recently Pushed")]
    public class RecentlyPushedCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && Time.time < b.pushReactionUntil && b.lastPusher != null;
        }
    }
}
