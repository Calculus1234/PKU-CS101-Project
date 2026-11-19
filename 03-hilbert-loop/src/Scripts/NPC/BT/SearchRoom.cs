using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 搜索当前房间：在房内随机挑一个可达点走过去，停留观察一段时间后返回 success。
    /// 每进入一次节点（树的一轮）只搜一个点；不在房间内时直接成功（无房可搜）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Search Room")]
    public class SearchRoom : NPCNode
    {
        [Tooltip("走到房内随机点后停留观察的时长（秒）")]
        public float holdSeconds = 2.5f;

        [Tooltip("房内随机搜索半径")]
        public float searchRadius = 1.45f;

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
            Transform room = CurrentRoom;
            if (room == null) return NodeResult.success; // 不在房间内：交给兜底分支

            if (!_spotPicked)
            {
                _spot = PickWalkablePointInRoom(room, searchRadius);
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
