using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 危险声音检查（3.3）：最近听到破门 / 战斗 / 尖叫，且怀疑度较高 → 前往声源查看。
    /// 查看结束后由 InvestigateSound 写入冷却，避免同一声音反复触发。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Heard Danger")]
    public class HeardDangerCondition : NPCCondition
    {
        [Tooltip("前往声源查看所需的怀疑度阈值")]
        public float suspicionThreshold = 50f;

        [Tooltip("声音记忆时长（秒）")]
        public float memorySeconds = 12f;

        public override bool Check()
        {
            NPCBlackboard b = Board;
            if (b == null) return false;
            if (Time.time < b.investigateCooldownUntil) return false;
            if (Time.time - b.lastHeardTime > memorySeconds) return false;

            SoundEventType type = b.lastHeardSoundType;
            if (type != SoundEventType.Combat && type != SoundEventType.DoorBreak
                && type != SoundEventType.Scream) return false;

            return b.suspicion > suspicionThreshold;
        }
    }
}
