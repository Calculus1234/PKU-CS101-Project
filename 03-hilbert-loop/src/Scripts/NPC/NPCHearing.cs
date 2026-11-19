using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 听觉（模块三 3.3）：订阅 SoundEventManager，
    /// 按「声音类型基础半径 × 响度」判定听觉范围；
    /// 若声音与耳朵之间隔着墙 / 关闭的门，半径再按墙体衰减系数缩小。
    /// 听见后写入黑板的 lastHeardSoundPosition / lastHeardSoundType，
    /// 并广播 NPCEventBus.OnNPCHeardNoise 供行为树与调试使用。
    /// </summary>
    public class NPCHearing : MonoBehaviour
    {
        [Tooltip("耳朵相对本体根的高度偏移（本体根约在胶囊中心）")]
        public float earHeight = 0.45f;

        [Tooltip("隔墙后的听觉半径衰减系数（0.45 = 只剩 45%）")]
        [Range(0.1f, 1f)] public float wallAttenuation = 0.45f;

        [Tooltip("在 Console 打印威胁性声音（Combat / DoorBreak / Scream）")]
        public bool logDangerSounds = true;

        NPCController _npc;

        int ObstacleMask => ~(1 << MonitorSystem.MonitorIgnoreLayer);

        void Awake()
        {
            _npc = GetComponent<NPCController>();
        }

        void OnEnable()
        {
            SoundEventManager.OnSoundEmitted += HandleSound;
        }

        void OnDisable()
        {
            SoundEventManager.OnSoundEmitted -= HandleSound;
        }

        void HandleSound(SoundEvent sound)
        {
            if (_npc == null || _npc.isDead || !_npc.IsAwake) return;
            if (sound.sourceNpcNumber == _npc.Number) return; // 自己发出的声音

            float radius = SoundEventManager.BaseRadius(sound.type) * sound.loudness;
            if (radius <= 0f) return;

            Vector3 ear = _npc.transform.position + Vector3.up * earHeight;
            float distance = Vector3.Distance(ear, sound.position);
            if (distance > radius) return;

            // 隔墙衰减：命中墙 / 家具 / 关闭的门后，有效半径缩小
            if (HasObstacleBetween(ear, sound.position, distance))
            {
                radius *= wallAttenuation;
                if (distance > radius) return;
            }

            NPCBlackboard board = _npc.blackboard;
            if (board == null) return;

            board.lastHeardSoundPosition = sound.position;
            board.lastHeardSoundType = sound.type;
            board.lastHeardTime = Time.time;

            NPCEventBus.RaiseNPCHeardNoise(_npc.Number, sound.position, sound.loudness);

            bool danger = sound.type == SoundEventType.Combat
                || sound.type == SoundEventType.DoorBreak
                || sound.type == SoundEventType.Scream;

            if (logDangerSounds && danger)
            {
                Debug.Log($"[NPC {_npc.Number}] 听到 {sound.type}（距离 {distance:0.0} 米，有效半径 {radius:0.0} 米）。");
            }

            // 3.3：危险声 + 怀疑度较高 → 行为树立即从根重选，优先「前往声源查看」
            if (danger && board.suspicion > 50f)
                _npc.RequestPerceptionResponse();
        }

        /// <summary>耳朵与声源之间是否有遮挡（排除声源自身最后 0.15 米内的命中）。</summary>
        bool HasObstacleBetween(Vector3 ear, Vector3 source, float distance)
        {
            if (distance <= 0.15f) return false;

            Vector3 dir = (source - ear) / distance;
            if (!Physics.Raycast(ear, dir, out RaycastHit hit, distance,
                    ObstacleMask, QueryTriggerInteraction.Ignore))
                return false;

            return hit.distance < distance - 0.15f;
        }
    }
}
