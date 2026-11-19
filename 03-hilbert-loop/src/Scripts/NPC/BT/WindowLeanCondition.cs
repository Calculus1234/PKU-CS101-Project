using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 探头机会检查（3.3）：到点后偶尔去窗边探头看雪（每 45~75 秒一次），
    /// 为玩家「趁其探头把人推出窗外」提供真实时机。恐慌 / 对峙期间不探头。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Window Lean")]
    public class WindowLeanCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null || npc.isDead || !npc.IsAwake) return false;
            if (Time.time < b.nextWindowLeanTime) return false;
            if (Time.time < b.panicUntil || Time.time < b.pushReactionUntil) return false;
            return !string.IsNullOrEmpty(b.currentRoomName);
        }
    }
}
