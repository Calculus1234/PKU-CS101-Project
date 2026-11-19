using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 写入 Animator 的行走布尔参数（无 Animator 时静默跳过，作为正式动画接入前的占位）。
    /// 移动分支中与 MoveToTarget 配合：进入分支时置 true，MoveToTarget 到达后自行置 false。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Set Animator Walking")]
    public class SetAnimatorWalking : NPCNode
    {
        [Tooltip("要写入的 Walking 布尔值")]
        public bool walking = true;

        public override NodeResult Execute()
        {
            SetWalking(walking);
            return NodeResult.success;
        }
    }
}
