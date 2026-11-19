using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 循环被破坏后的兜底分支（3.2）：数值未命中「调查真相 / 自保 / 合作」任一倾向时，
    /// 记录一次状态日志并返回 failure——让上级选择器继续回落，NPC 维持通用行为。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Switch Behavior By Cycle State")]
    public class SwitchBehaviorByCycleState : NPCNode
    {
        CyclePhase _lastLogged;
        bool _logged;

        public override NodeResult Execute()
        {
            CyclePhase phase = CycleState.currentPhase;
            if (!_logged || phase != _lastLogged)
            {
                _logged = true;
                _lastLogged = phase;
                int number = Npc != null ? Npc.Number : -1;
                Debug.Log($"[NPC {number}] 感知到循环状态：{phase}（数值未命中倾向阈值，维持通用行为）。");
            }
            return NodeResult.failure; // 放行：让主选择器落到通用行为分支
        }
    }
}
