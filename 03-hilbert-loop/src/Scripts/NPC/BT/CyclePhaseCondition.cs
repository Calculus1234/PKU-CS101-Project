using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>循环状态检查：当前循环处于指定状态（如 Disrupted = 被破坏）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Cycle Phase")]
    public class CyclePhaseCondition : NPCCondition
    {
        [Tooltip("要匹配的循环状态")]
        public CyclePhase phase = CyclePhase.Disrupted;

        public override bool Check()
        {
            return CycleState.currentPhase == phase;
        }
    }
}
