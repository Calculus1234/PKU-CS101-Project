using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 闲置徘徊（兜底分支）：在房间内 / 当前位置附近随机漫步一小段，停留片刻后返回 success。
    /// 搜索分支不可用时由选择器落到这里，保证 NPC 永远不会原地发呆。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Wander In Room")]
    public class WanderInRoom : NPCNode
    {
        [Tooltip("房内徘徊半径")]
        public float roomRadius = 1.6f;

        [Tooltip("不在房间时的徘徊半径（以当前位置为中心）")]
        public float openRadius = 2f;

        [Tooltip("到达后发呆的时长（秒）")]
        public float holdSeconds = 3f;

        [Tooltip("到达判定距离")]
        public float arriveDistance = 0.45f;

        Vector3 _spot;
        bool _spotPicked;
        float _holdEndTime;
        bool _holdStarted;

        public override void OnEnter()
        {
            _spotPicked = false;
            _holdStarted = false;
            _holdEndTime = 0f;
        }

        public override NodeResult Execute()
        {
            if (!_spotPicked)
            {
                Transform room = CurrentRoom;
                if (room != null)
                {
                    _spot = PickWalkablePointInRoom(room, roomRadius);
                }
                else
                {
                    Vector3 origin = Npc != null ? Npc.transform.position : transform.position;
                    _spot = PickWalkablePointAround(origin, openRadius);
                }
                _spotPicked = true;
            }

            if (!_holdStarted)
            {
                NodeResult move = MoveTowards(_spot, arriveDistance);
                if (move.status == Status.Failure)
                {
                    _spotPicked = false; // 目标被卡死放弃：下次进入重新选点
                    return NodeResult.failure;
                }
                if (move.status == Status.Running) return NodeResult.running;

                _holdStarted = true;
                _holdEndTime = Time.time + holdSeconds;
                return NodeResult.running;
            }

            return Time.time >= _holdEndTime ? NodeResult.success : NodeResult.running;
        }
    }
}
