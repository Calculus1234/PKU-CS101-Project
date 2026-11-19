using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 移动到黑板当前目标（currentTarget）。
    /// 移动中 = running；到达 = success（并收走行走姿态）；目标消失 = success（视为完成）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Move To Target")]
    public class MoveToTarget : NPCNode
    {
        [Tooltip("到达判定距离")]
        public float arriveDistance = 0.5f;

        public override NodeResult Execute()
        {
            if (Npc == null || Board == null || Board.currentTarget == null)
            {
                SetWalking(false);
                return NodeResult.success; // 目标已消失：没有要追的东西了
            }

            NodeResult result = MoveTowards(Board.currentTarget.transform.position, arriveDistance);
            if (result.status != Status.Running) SetWalking(false);
            return result;
        }
    }
}
