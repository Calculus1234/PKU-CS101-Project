using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>身处房间：黑板记录了当前所在房间（→ 在房间内搜索）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Has Room")]
    public class HasRoomCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            return b != null && !string.IsNullOrEmpty(b.currentRoomName);
        }
    }
}
