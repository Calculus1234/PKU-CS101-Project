using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 推搡对峙（3.3）：停下手中的事，持续转向推搡者并盯着对方，
    /// 直到 pushReactionUntil 结束。敌意已由 OnPushed 写入怀疑度 / 怀疑名单。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/React To Push")]
    public class ReactToPush : NPCNode
    {
        public override NodeResult Execute()
        {
            NPCBlackboard b = Board;
            if (Npc == null || b == null) return NodeResult.failure;

            if (Time.time >= b.pushReactionUntil || b.lastPusher == null)
            {
                if (Nav != null) Nav.Stop();
                return NodeResult.success;
            }

            if (Nav != null) Nav.Stop();
            SetWalking(false);
            FaceTowards(b.lastPusher.transform.position, 10f);

            return NodeResult.running;
        }

        public override void OnExit()
        {
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }
    }
}
