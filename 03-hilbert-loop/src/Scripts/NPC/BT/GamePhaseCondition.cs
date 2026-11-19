using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>游戏阶段检查：GameManager 当前处于指定阶段（搜证 / 会议 / 投票 / 轮回）。</summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Game Phase")]
    public class GamePhaseCondition : NPCCondition
    {
        [Tooltip("要匹配的游戏阶段")]
        public GamePhase phase = GamePhase.Investigation;

        public override bool Check()
        {
            GameManager gm = GameManager.Instance;
            return gm != null && gm.CurrentPhase == phase;
        }
    }
}
