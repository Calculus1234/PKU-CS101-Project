using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 接近最近的住客（模块三 3.2「复现：激进行为」）。
    /// 用于 8号 AI 复现上一轮玩家的社交倾向：aggression 高 → 主动接近他人。
    /// 到达后记录一次日志并返回 success；目标移动导致再次拉开距离时会重新接近。
    /// 目标被卡死放弃 / 场景中没有其他活着的住客时结束本分支。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Approach Nearest NPC")]
    public class ApproachNearestNPC : NPCNode
    {
        [Tooltip("接近到位判定距离")]
        public float arriveDistance = 1.6f;

        [Tooltip("重新挑选最近目标的间隔（秒）")]
        public float retargetInterval = 2f;

        NPCController _target;
        float _retargetTime;
        bool _arrivalLogged;
        bool _approachingFromFar; // 本次目标是否需要「从远处走到到场」的到达日志

        public override void OnEnter()
        {
            _target = null;
            _retargetTime = 0f;
            _arrivalLogged = false;
            _approachingFromFar = false;
        }

        public override NodeResult Execute()
        {
            if (Npc == null) return NodeResult.failure;

            if (_target == null || _target.isDead || !_target.gameObject.activeInHierarchy
                || Time.time >= _retargetTime)
            {
                _target = FindNearest();
                _retargetTime = Time.time + retargetInterval;
                _arrivalLogged = false;
                // 到达帧与 MoveTowards 的 Success 同帧互斥（内部 <= arriveDistance 即成功），
                // 无法在到达帧判断「是否从远处走来」——改为挑选目标时记录
                _approachingFromFar = _target != null
                    && Vector3.Distance(Npc.transform.position, _target.transform.position) > arriveDistance;
                if (_target == null) return NodeResult.success; // 没有别的住客：无社交对象
            }

            NodeResult move = MoveTowards(_target.transform.position, arriveDistance);

            if (move.status == Status.Failure)
            {
                _target = null; // 被卡死放弃：清掉目标，让选择器落到后续分支
                return NodeResult.failure;
            }

            if (move.status == Status.Success && _approachingFromFar && !_arrivalLogged)
            {
                _arrivalLogged = true;
                NPCBlackboard b = Board;
                Debug.Log($"[NPC {Npc.Number}] 复现上一轮玩家的社交倾向：主动接近 {_target.Number} 号" +
                          $"（aggression={(b != null ? b.aggression : 0f):0}）。");
            }

            return move;
        }

        /// <summary>最近的、活着的其他住客（没有则返回 null）。</summary>
        NPCController FindNearest()
        {
            NPCController[] npcs = FindObjectsOfType<NPCController>();
            NPCController best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < npcs.Length; i++)
            {
                NPCController npc = npcs[i];
                if (npc == null || npc == Npc || npc.isDead || !npc.gameObject.activeInHierarchy) continue;

                float d = Vector3.Distance(Npc.transform.position, npc.transform.position);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = npc;
                }
            }

            return best;
        }
    }
}
