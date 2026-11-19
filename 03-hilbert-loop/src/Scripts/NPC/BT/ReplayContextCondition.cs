using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 情境复现条件（3.2 修正一·情境A）：8号AI 存在「当前情境匹配且未消费」的上一轮决策记录。
    /// 置于行为树主选择器前部：命中即由子节点精确复现上一轮玩家的关键决策；
    /// 未命中（或非 8号AI）放行，由后续的数值倾向分支兜底。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/Replay Context")]
    public class ReplayContextCondition : NPCCondition
    {
        public override bool Check()
        {
            NPCBlackboard b = Board;
            if (b == null || !b.replayFromPlayer) return false;

            // 子节点正在复现（running）时不中断：避免移动中途被打断
            if (TryGetChild(out Node node) && node.status == Status.Running) return true;

            return ReplayContexts.FindMatch(Npc, b) != null;
        }
    }
}
