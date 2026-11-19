using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 压力恢复服务（Service）：搜证阶段随行为树定时触发，
    /// 让压力值缓慢回落到基线（被打断 / 目睹事件的瞬时抬升由事件总线直接写入）。 
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Services/Update Stress")]
    public class UpdateStress : Service
    {
        [Tooltip("压力回落的目标基线")]
        public float baseline = 50f;

        [Tooltip("每次触发的回落步长")]
        public float recoverPerTick = 2f;

        NPCController _npc;

        public override void Task()
        {
            if (_npc == null) _npc = GetComponentInParent<NPCController>();
            if (_npc == null || _npc.blackboard == null) return;

            _npc.blackboard.stress = Mathf.MoveTowards(_npc.blackboard.stress, baseline, recoverPerTick);
        }
    }
}
