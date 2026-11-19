using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 视觉警觉检查（3.3）：
    /// - 怀疑度 &gt; 60 且当前视野里有目标 → 盯着对方看；
    /// - 或者玩家已经摸到很近的距离（默认 3 米内）→ 转头看过去（潜行接近也会被发现）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Visible Suspicion")]
    public class VisibleSuspicionCondition : NPCCondition
    {
        [Tooltip("触发盯视的怀疑度阈值")]
        public float suspicionThreshold = 60f;

        [Tooltip("玩家进入该距离（米）时，即使怀疑度不高也会转头查看")]
        public float closeRange = 3f;

        public override bool Check()
        {
            NPCBlackboard b = Board;
            if (b == null) return false;
            if (Time.time < b.observeCooldownUntil) return false;
            if (b.visibleTargets == null || b.visibleTargets.Count == 0) return false;

            if (b.suspicion > suspicionThreshold) return true;

            // 近距离发现玩家：转头盯住（满足「潜行靠近被转头看」）
            NPCController npc = Npc;
            if (npc == null) return false;
            for (int i = 0; i < b.visibleTargets.Count; i++)
            {
                GameObject target = b.visibleTargets[i];
                if (target == null) continue;
                if (target.GetComponentInParent<PlayerController>() == null) continue;
                if (Vector3.Distance(npc.transform.position, target.transform.position) <= closeRange)
                    return true;
            }
            return false;
        }
    }
}
