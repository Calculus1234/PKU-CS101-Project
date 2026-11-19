using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>死亡检查：NPC 已死亡时进入死亡分支（幽灵处理待后续模块）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Is Dead")]
    public class IsDeadCondition : NPCCondition
    {
        public override bool Check()
        {
            return Npc != null && Npc.isDead;
        }
    }
}
