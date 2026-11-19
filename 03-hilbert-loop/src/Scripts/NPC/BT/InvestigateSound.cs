using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 循声查看（3.3）：前往最后听到的危险声源位置，到达后原地环顾片刻。
    /// 查看期间听到新的危险声音会改道；结束后写入查看冷却。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Investigate Sound")]
    public class InvestigateSound : NPCNode
    {
        [Tooltip("到达声源判定距离")]
        public float arriveDistance = 1.2f;

        [Tooltip("到达后原地环顾时长")]
        public float listenSeconds = 2f;

        [Tooltip("查看结束后的冷却（秒）")]
        public float cooldownSeconds = 15f;

        [Tooltip("声音记忆时长（秒）")]
        public float memorySeconds = 12f;

        Vector3 _target;
        bool _arrived;
        float _lookEndTime;

        public override void OnEnter()
        {
            _target = Board != null ? Board.lastHeardSoundPosition : transform.position;
            _arrived = false;
            _lookEndTime = 0f;
            SetWalking(true);
        }

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null) return NodeResult.failure;

            // 声音已经过去太久：结束查看
            if (Time.time - b.lastHeardTime > memorySeconds)
            {
                Finish(b);
                return NodeResult.success;
            }

            // 途中又听到新的危险声音：改道去最新声源
            if (Time.time - b.lastHeardTime < 1.5f
                && (b.lastHeardSoundPosition - _target).sqrMagnitude > 1f)
            {
                _target = b.lastHeardSoundPosition;
                _arrived = false;
                _lookEndTime = 0f;
                SetWalking(true);
                Debug.Log($"[NPC {npc.Number}] 听到新的动静，改道前往最新声源。");
            }

            if (!_arrived)
            {
                NodeResult move = MoveTowards(_target, arriveDistance);
                if (move.status == Status.Running) return NodeResult.running;

                _arrived = true;
                SetWalking(false);
                if (Nav != null) Nav.Stop();
                _lookEndTime = Time.time + listenSeconds;

                Debug.Log($"[NPC {npc.Number}] 到达声源附近（{b.lastHeardSoundType}），环顾四周。");
            }

            FaceTowards(_target);
            if (Time.time >= _lookEndTime)
            {
                Finish(b);
                return NodeResult.success;
            }

            return NodeResult.running;
        }

        void Finish(NPCBlackboard b)
        {
            b.investigateCooldownUntil = Time.time + cooldownSeconds;
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }

        public override void OnExit()
        {
            SetWalking(false);
            if (Nav != null) Nav.Stop();
        }
    }
}
