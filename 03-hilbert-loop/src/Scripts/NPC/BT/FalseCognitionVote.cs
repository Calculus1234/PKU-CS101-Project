using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 虚假认知行为链·会议推票（3.2 修正一·情境B）：
    /// "没机会下手"的 8号AI 在会议阶段走向圆桌，推动投票投 9 号（场上最大的号码）。
    /// 只广播投票意向（NPCEventBus.RaiseVoteCast），不实现完整会议/投票结算（3.4、3.5）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/False Cognition Vote")]
    public class FalseCognitionVote : NPCNode
    {
        [Tooltip("就座判定距离")]
        public float arriveDistance = 1.6f;

        [Tooltip("到达后重复广播投票意向的最小间隔（秒）")]
        public float repeatInterval = 30f;

        // 圆桌目标一次性缓存（对照 MoveToMeetingTable 的缓存模式）：
        // 每帧 GameObject.Find 会造成目标读取不稳定，nav 卡死计时被反复重置，既到不了也放弃不了。
        bool _targetSearched;
        Vector3 _target;

        // 投票广播节流：不随节点重入重置——就座后根节点每帧重入本节点，重置会导致广播刷屏。
        bool _voted;
        float _nextVoteTime;

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            if (npc == null) return NodeResult.failure;

            if (!_targetSearched)
            {
                _targetSearched = true;
                _target = FindTablePosition();
            }

            NodeResult move = MoveTowards(_target, arriveDistance);
            if (move.status == Status.Failure) return NodeResult.failure;
            if (move.status == Status.Running) return NodeResult.running;

            // 已到圆桌：推动投票投 9 号（节流重复广播）
            if (!_voted || Time.time >= _nextVoteTime)
            {
                _voted = true;
                _nextVoteTime = Time.time + repeatInterval;
                NPCEventBus.RaiseVoteCast(npc.Number, 9);
                Debug.Log($"[NPC {npc.Number}] 虚假认知驱动：在会议中推动投票投 9 号（场上最大的号码）。");
            }
            return NodeResult.success;
        }

        /// <summary>定位会议桌位置：优先圆桌中心，其次 8 号会议椅，都找不到时退往大厅中心。</summary>
        static Vector3 FindTablePosition()
        {
            GameObject table = GameObject.Find("RoundTable");
            if (table != null) return table.transform.position;

            GameObject chair = GameObject.Find("Chair_" + 8);
            if (chair != null) return chair.transform.position;

            Debug.LogWarning("[NPC 8] 会议推票：未找到圆桌与椅子，改往大厅中心。");
            return Vector3.zero;
        }
    }
}
