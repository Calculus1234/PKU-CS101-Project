using System;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>道具分类。</summary>
    public enum ItemCategory
    {
        Weapon,         // 武器（匕首、锤子）
        Tool,           // 工具（长绳、磁铁、钉子）
        Wearable,       // 穿戴（面具/斗篷）
        Note,           // 纸条类
        Miscellaneous,
    }

    /// <summary>可藏匿位置（位标志）。</summary>
    [Flags]
    public enum HideLocation
    {
        None = 0,
        OnBody = 1 << 0,     // 随身藏匿（尸体 / 身上）
        UnderBed = 1 << 1,   // 床底
        Table = 1 << 2,      // 桌子
    }

    /// <summary>
    /// 单个道具的静态定义，由 ItemDatabase 持有。
    /// 后续接入玩法时，使用效果统一走 useEffectId（Yarn / 行为树 / 玩法层解析）。
    /// </summary>
    [Serializable]
    public class ItemDefinition
    {
        [Tooltip("唯一 id，脚本 / Yarn 中引用")]
        public string id;

        [Tooltip("显示名称")]
        public string displayName;

        public ItemCategory category;

        [Tooltip("允许的藏匿位置")]
        public HideLocation allowedHiding = HideLocation.OnBody | HideLocation.UnderBed;

        [Tooltip("使用效果 id（留空 = 纯道具）。例：kill / lock_window / climb_window / climb_wall / voice_mimic")]
        public string useEffectId = "";

        [Tooltip("击杀类道具的成功概率（0~1，1 = 必杀）")]
        [Range(0f, 1f)] public float killChance = 1f;

        [Tooltip("是否允许主动丢弃（面具/斗篷不可摘除）")]
        public bool canDiscard = true;

        [TextArea] public string description = "";

        [Header("会议证据（模块三 3.4）")]
        [Tooltip("是否可以在圆桌会议上作为证据出示")]
        public bool canBeMeetingEvidence;

        [Tooltip("证据权重（0~100）：越高越能洗清自己 / 抬高指控者嫌疑")]
        [Range(0, 100)] public int evidenceWeight;

        [Tooltip("出示证据时玩家说出的理由（Yarn 变量 $evidence_reason）")]
        [TextArea] public string evidenceReason = "";

        [Tooltip("占位几何体颜色（仅用于基本几何体占位，替换 FBX 后可忽略）")]
        public Color placeholderColor = Color.gray;

        [Tooltip("世界中的 FBX 视觉预制体（由 ScenePropBuilder 自动生成并装配）")]
        public GameObject worldPrefab;
    }
}
