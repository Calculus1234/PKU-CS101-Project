using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 会议阶段：走向自己的会议椅（Chair_N）。到达后返回 success，
    /// 后续每轮重复检查时直接成功（已就座不再移动）。找不到椅子时退往会议区中心。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Move To Meeting Table")]
    public class MoveToMeetingTable : NPCNode
    {
        [Tooltip("就座判定距离")]
        public float arriveDistance = 1.4f;

        Transform _chair;
        bool _chairSearched;
        bool _reported;

        public override NodeResult Execute()
        {
            if (Npc == null) return NodeResult.failure;

            if (!_chairSearched)
            {
                _chairSearched = true;
                GameObject chair = GameObject.Find("Chair_" + Npc.Number);
                if (chair != null)
                {
                    _chair = chair.transform;
                }
                else
                {
                    Debug.LogWarning($"[NPC {Npc.Number}] 未找到会议椅 Chair_{Npc.Number}，改往会议区中心。");
                }
            }

            Vector3 target = _chair != null ? _chair.position : Vector3.zero;
            NodeResult move = MoveTowards(target, arriveDistance);
            if (move.status == Status.Failure) return NodeResult.failure;

            if (move.status == Status.Success && !_reported)
            {
                _reported = true;
                Debug.Log($"[NPC {Npc.Number}] 已就座（{(_chair != null ? _chair.name : "会议区中心")}）。");
            }
            return move;
        }
    }
}
