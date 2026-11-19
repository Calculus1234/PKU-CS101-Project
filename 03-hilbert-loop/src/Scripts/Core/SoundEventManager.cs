using System;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>声音事件类型（模块三 3.3 听觉感知）。</summary>
    public enum SoundEventType
    {
        None = 0,
        Footstep,    // 脚步
        DoorOpen,    // 开门 / 关门
        DoorBreak,   // 破门 / 砸门
        ItemPickup,  // 拾取物品
        Combat,      // 挥动武器 / 袭击
        Push,        // 推搡
        Scream,      // 尖叫（锤子袭击失败等）
    }

    /// <summary>
    /// 一次声音事件：类型、世界位置、响度（0~1）、发声者。
    /// sourceNpcNumber 仅用于让 NPC 忽略自己发出的声音（-1 = 玩家 / 世界）。
    /// </summary>
    [Serializable]
    public struct SoundEvent
    {
        public SoundEventType type;
        public Vector3 position;
        public float loudness;
        public GameObject source;
        public int sourceNpcNumber;
    }

    /// <summary>
    /// 声音事件管理器（单例）：所有发声点通过 Emit 广播，NPC 的 NPCHearing 自行判定是否听见。
    /// 基础听觉半径按声音类型区分；实际判定 = 半径 × 响度，再按墙体遮挡衰减（见 NPCHearing）。
    /// 场景中由 MainSceneBuilder 在 Systems 上挂载；缺失时首次 Emit 自动创建。
    /// </summary>
    public class SoundEventManager : MonoBehaviour
    {
        public static SoundEventManager Instance { get; private set; }

        /// <summary>全局声音广播（NPCHearing 订阅；静态事件，避免初始化顺序依赖）。</summary>
        public static event Action<SoundEvent> OnSoundEmitted;

        [Tooltip("在 Console 打印声音事件（脚步除外）")]
        public bool verbose = false;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>发出一次声音事件（运行时自动确保单例存在）。</summary>
        public static void Emit(SoundEventType type, Vector3 position, float loudness = 1f,
            GameObject source = null, int sourceNpcNumber = -1)
        {
            if (type == SoundEventType.None) return;

            SoundEvent sound = new SoundEvent
            {
                type = type,
                position = position,
                loudness = Mathf.Clamp01(loudness),
                source = source,
                sourceNpcNumber = sourceNpcNumber,
            };

            SoundEventManager manager = EnsureInstance();
            if (manager != null && manager.verbose && type != SoundEventType.Footstep)
                Debug.Log($"[Sound] {type} @ {position}（响度 {sound.loudness:0.0}，来源 {(source != null ? source.name : "世界")}）");

            OnSoundEmitted?.Invoke(sound);
        }

        /// <summary>各类型声音的基础听觉半径（米）。实际半径 = 基础半径 × 响度。</summary>
        public static float BaseRadius(SoundEventType type)
        {
            switch (type)
            {
                case SoundEventType.Footstep: return 5f;
                case SoundEventType.DoorOpen: return 7f;
                case SoundEventType.DoorBreak: return 16f;
                case SoundEventType.ItemPickup: return 3.5f;
                case SoundEventType.Combat: return 12f;
                case SoundEventType.Push: return 8f;
                case SoundEventType.Scream: return 30f;
                default: return 0f;
            }
        }

        static SoundEventManager EnsureInstance()
        {
            if (Instance != null) return Instance;
            if (!Application.isPlaying) return null;

            GameObject go = new GameObject("SoundEventManager");
            return go.AddComponent<SoundEventManager>();
        }
    }
}
