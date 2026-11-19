using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>恐慌检查：目击同房间同伴死亡后，panicUntil 时间窗内优先进入恐慌分支。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Panic")]
    public class PanicCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && Time.time < b.panicUntil;
        }
    }
}
