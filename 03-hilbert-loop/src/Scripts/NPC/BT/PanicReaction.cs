using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 恐慌反应（3.3）：看见同房间的同伴死亡后，先远离尸体方向躲到安全位置，
    /// 到达后站在原地发抖并盯着尸体方向，直到恐慌时间窗结束。
    /// 无法后退（被家具堵死）时原地保持恐慌，不会卡死行为树。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Panic Reaction")]
    public class PanicReaction : NPCNode
    {
        [Tooltip("远离尸体方向的距离")]
        public float fleeDistance = 3.5f;

        [Tooltip("后退落点到达判定距离")]
        public float arriveDistance = 0.6f;

        Vector3 _fleeSpot;
        bool _spotPicked;
        bool _arrived;
        bool _logged;

        public override void OnEnter()
        {
            _spotPicked = false;
            _arrived = false;
            _logged = false;
        }

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null) return NodeResult.failure;

            if (Time.time >= b.panicUntil)
            {
                SetWalking(false);
                return NodeResult.success;
            }

            if (!_spotPicked)
            {
                Vector3 away = npc.transform.position - b.panicSourcePosition;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = -npc.transform.forward;

                Vector3 center = npc.transform.position + away.normalized * fleeDistance;
                _fleeSpot = PickWalkablePointAround(center, 1.2f);
                _spotPicked = true;
                SetWalking(true);

                if (!_logged)
                {
                    _logged = true;
                    Debug.Log($"[NPC {npc.Number}] 目击同伴死亡，恐慌后退（Stress={b.stress:0}）。");
                }
            }

            if (!_arrived)
            {
                NodeResult move = MoveTowards(_fleeSpot, arriveDistance);
                if (move.status == Status.Running) return NodeResult.running;

                // 到达 / 被堵死：都进入原地恐慌姿态，等时间窗结束
                _arrived = true;
                SetWalking(false);
                if (Nav != null) Nav.Stop();
            }

            FaceTowards(b.panicSourcePosition);
            return NodeResult.running;
        }

        public override void OnExit()
        {
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }
    }
}
