using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 威胁响应：前往怀疑名单首位号码的房间查看。
    /// 到达后移除该怀疑对象、怀疑度回落一档并返回 success。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Move To Suspect Room")]
    public class MoveToSuspectRoom : NPCNode
    {
        [Tooltip("到达怀疑房间的判定距离")]
        public float arriveDistance = 1f;

        [Tooltip("查看后怀疑度回落量")]
        public float calmDown = 15f;

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null) return NodeResult.failure;
            if (b.suspectNumbers == null || b.suspectNumbers.Count == 0) return NodeResult.success;

            int suspect = b.suspectNumbers[0];
            Transform room = NPCRoomUtil.FindRoom(suspect);
            if (room == null)
            {
                b.suspectNumbers.RemoveAt(0);
                Debug.LogWarning($"[NPC {npc.Number}] 找不到 {suspect} 号房，跳过该怀疑对象。");
                return NodeResult.success;
            }

            NodeResult move = MoveTowards(room.position, arriveDistance);
            if (move.status != Status.Success) return move;

            // 已到达：蹲守一次，怀疑度回落并移出名单
            b.suspectNumbers.RemoveAt(0);
            b.suspicion = Mathf.Max(0f, b.suspicion - calmDown);
            Debug.Log($"[NPC {npc.Number}] 前往查看 {suspect} 号房（Suspicion={b.suspicion:0}）。");
            return NodeResult.success;
        }
    }
}
