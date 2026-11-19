using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 行为倾向阈值条件（模块三 3.2）：黑板上的 aggression / caution / curiosity / cooperation > 阈值。
    /// playbackOnly = true 时只为「8号 AI 复现」生效（黑板必须携带上一轮玩家行为数据）；
    /// noteTrustedAlso = true 时纸条信任（noteTrusted）也触发本条件（「纸条信任高 → 按纸条自保」）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Stat Threshold")]
    public class StatThresholdCondition : NPCCondition
    {
        public enum Stat { Aggression, Caution, Curiosity, Cooperation }

        [Tooltip("检查的行为数值")]
        public Stat stat = Stat.Curiosity;

        [Tooltip("阈值（严格大于才通过）")]
        public float threshold = 60f;

        [Tooltip("仅在 8号 AI 复现时生效（黑板必须带有上一轮玩家行为数据）")]
        public bool playbackOnly = false;

        [Tooltip("纸条信任（noteTrusted）也触发本条件")]
        public bool noteTrustedAlso = false;

        bool _hitLogged;

        public override bool Check()
        {
            NPCBlackboard b = Board;
            if (b == null) return false;
            if (playbackOnly && !b.replayFromPlayer) return false;

            bool hit = noteTrustedAlso && b.noteTrusted || Value(b) > threshold;

            if (hit && !_hitLogged)
            {
                _hitLogged = true; // 只记一次：验证「行为复现 / 状态切换」时可在 Console 直接看到分支命中
                int number = Npc != null ? Npc.Number : -1;
                Debug.Log(playbackOnly
                    ? $"[NPC {number}] 复现倾向命中：{stat} = {Value(b):0} > {threshold:0}（开始复现上一轮玩家的行为）。"
                    : $"[NPC {number}] 破坏后倾向命中：{stat} = {Value(b):0} > {threshold:0}（循环破坏后的应对行为）。");
            }
            return hit;
        }

        float Value(NPCBlackboard b)
        {
            switch (stat)
            {
                case Stat.Aggression: return b.aggression;
                case Stat.Caution: return b.caution;
                case Stat.Curiosity: return b.curiosity;
                case Stat.Cooperation: return b.cooperation;
                default: return 0f;
            }
        }
    }
}
