using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 盯视目标（3.3）：停下脚步转向视野中的可疑对象（玩家优先），
    /// 持续注视数秒后写入冷却。目标走出视野时立即结束，不会原地空盯。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Observe Target")]
    public class ObserveTarget : NPCNode
    {
        [Tooltip("持续注视时长（秒）")]
        public float observeSeconds = 3f;

        [Tooltip("注视结束后的冷却（秒）")]
        public float cooldownSeconds = 12f;

        [Tooltip("转向速度")]
        public float turnSpeed = 10f;

        GameObject _target;
        float _endTime;

        public override void OnEnter()
        {
            _target = PickTarget();
            _endTime = Time.time + observeSeconds;
            SetWalking(false);
            if (Nav != null) Nav.Stop();

            if (_target != null && Npc != null)
            {
                Debug.Log($"[NPC {Npc.Number}] 怀疑地盯着{Describe(_target)}" +
                          $"（Suspicion={Board.suspicion:0}）。");
            }
        }

        public override NodeResult Execute()
        {
            NPCBlackboard b = Board;
            if (Npc == null || b == null) return NodeResult.failure;

            bool stillVisible = _target != null && _target.activeInHierarchy
                && b.visibleTargets != null && b.visibleTargets.Contains(_target);
            if (!stillVisible) _target = PickTarget();

            if (_target == null)
            {
                Finish(b);
                return NodeResult.success;
            }

            if (Nav != null) Nav.Stop();
            SetWalking(false);
            FaceTowards(_target.transform.position + Vector3.up * 0.5f, turnSpeed);

            if (Time.time >= _endTime)
            {
                Finish(b);
                return NodeResult.success;
            }

            return NodeResult.running;
        }

        GameObject PickTarget()
        {
            NPCBlackboard b = Board;
            if (b == null || b.visibleTargets == null) return null;

            // 玩家优先（NPC 对玩家的行为最敏感）
            for (int i = 0; i < b.visibleTargets.Count; i++)
            {
                GameObject candidate = b.visibleTargets[i];
                if (candidate != null && candidate.GetComponentInParent<PlayerController>() != null)
                    return candidate;
            }

            for (int i = 0; i < b.visibleTargets.Count; i++)
            {
                if (b.visibleTargets[i] != null) return b.visibleTargets[i];
            }
            return null;
        }

        void Finish(NPCBlackboard b)
        {
            b.observeCooldownUntil = Time.time + cooldownSeconds;
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }

        static string Describe(GameObject target)
        {
            NPCController npc = target.GetComponentInParent<NPCController>();
            if (npc != null) return npc.Number + "号";
            if (target.GetComponentInParent<PlayerController>() != null) return "玩家";
            return target.name;
        }

        public override void OnExit()
        {
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }
    }
}
