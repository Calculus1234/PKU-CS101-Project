using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 号码档案（ScriptableObject）。
    /// 号码们本质是同一复制体，本资产只描述"这个号码掌握的信息与醒来时序"，
    /// 不含任何专属行为逻辑 —— 行为全部来自共用的通用理性行为树，
    /// 差异只通过这里的"信息状态"（hasNote / hasNails）与工厂初始化体现。
    /// </summary>
    [CreateAssetMenu(menuName = "HilbertLoop/NPC Data", fileName = "NPC_1")]
    public class NPCData : ScriptableObject
    {
        [Header("身份")]
        [Tooltip("号码（1~9；10号使用运行时数据）")]
        public int number = 1;

        [Tooltip("显示名（监控 / 调试用）")]
        public string displayName = "住客";

        [Header("信息状态（开局掌握的底牌）")]
        [Tooltip("身上是否携带纸条（8号）")]
        public bool hasNote;

        [Tooltip("身上是否携带钉子（1号；杀9号时用过的工具）")]
        public bool hasNails;

        [Header("一周目时序")]
        [Tooltip("绝对醒来分钟（自循环开始计，1 游戏分钟 = 6 现实秒）。剧本：1号0、2号60、3号65、4号70、5号75、6号80、7号85、8号90、9号95（仅1号拒杀的破坏轮）。10号为旧设计残留，当前剧本不会生成。实际延迟由 LoopTimeline 按玩家号码做相对换算")]
        public float wakeupDelay;

        [Header("控制方式")]
        [Tooltip("是否由玩家控制（8号）；AI 工厂不为玩家角色生成行为树")]
        public bool isPlayerControlled;

        [Header("外观占位")]
        [Tooltip("面具数字 / 斗篷的颜色标识")]
        public Color maskNumberColor = Color.white;
    }
}
