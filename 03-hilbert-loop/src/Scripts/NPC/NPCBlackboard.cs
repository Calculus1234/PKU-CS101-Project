using System;
using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 个体黑板：每个 NPC 一份独立实例。
    /// 行为树的全部决策输入 / 输出都落在这里；号码差异只体现为
    /// "信息差异"（selfHasKilled / hasNote / knownClues ...），不存在专属行为分支。
    /// </summary>
    [Serializable]
    public class NPCBlackboard
    {
        /// <summary>信任表覆盖的最大号码（10号也需要被其他 NPC 记住信任度）。</summary>
        public const int MaxTrackedNumber = 10;

        [Header("情绪 / 关系（0~100）")]
        [Tooltip("对『有人在破坏循环』的怀疑度")]
        public float suspicion = 50f;

        [Tooltip("对同类的信任度")]
        public float trust = 50f;

        [Tooltip("精神压力")]
        public float stress = 50f;

        [Header("行为上下文")]
        [Tooltip("当前移动 / 交互目标")]
        public GameObject currentTarget;

        [Tooltip("目标最后被看到的位置（3.3 视觉接入）")]
        public Vector3 lastKnownPosition;

        [Header("感知（模块三 3.3 视觉）")]
        [Tooltip("本次扫描看得见的目标（玩家 / 其他 NPC），每 0.2 秒刷新")]
        public List<GameObject> visibleTargets = new List<GameObject>();

        [Header("感知（模块三 3.3 听觉）")]
        [Tooltip("最后听到的声音位置")]
        public Vector3 lastHeardSoundPosition;

        [Tooltip("最后听到的声音类型")]
        public SoundEventType lastHeardSoundType = SoundEventType.None;

        [Tooltip("最后听到声音的时刻（Time.time）")]
        public float lastHeardTime = -999f;

        [Header("感知反应（模块三 3.3）")]
        [Tooltip("盯着目标看的冷却结束时刻（避免持续死盯）")]
        public float observeCooldownUntil;

        [Tooltip("前往声源查看的冷却结束时刻")]
        public float investigateCooldownUntil;

        [Tooltip("被推搡后的对峙状态持续到该时刻")]
        public float pushReactionUntil;

        [Tooltip("最近一次推搡者")]
        public GameObject lastPusher;

        [Tooltip("恐慌状态持续到该时刻（同房间 / 目击同伴死亡）")]
        public float panicUntil;

        [Tooltip("恐慌来源（同伴死亡位置）")]
        public Vector3 panicSourcePosition;

        [Tooltip("已经看见过 9 号的尸体（压力 +30 只触发一次）")]
        public bool sawNinthBody;

        [Tooltip("下一次探头看窗外的时刻（行为树低频机会分支）")]
        public float nextWindowLeanTime;

        [Header("信息")]
        [Tooltip("已知线索")]
        public List<string> knownClues = new List<string>();

        [Tooltip("怀疑对象号码")]
        public List<int> suspectNumbers = new List<int>();

        [Tooltip("已排除嫌疑的号码")]
        public List<int> clearedNumbers = new List<int>();

        [Header("所在位置")]
        public bool isInRoom;
        public string currentRoomName;

        [Header("本轮名册认知")]
        [Tooltip("开局认知里的最大存活号码；默认 1..max 都还住着")]
        public int knownMaxLivingNumber;

        [Tooltip("自己估计的当前人数（开局 = knownMaxLivingNumber，发现空房后递减）")]
        public int assumedPopulation;

        [Tooltip("搜证中已经确认空着的房间号码")]
        public List<int> discoveredEmptyRooms = new List<int>();

        [Header("循环感知")]
        [Tooltip("本 NPC 感知到的循环状态（与 CycleState 同步）")]
        public CyclePhase cycleState = CyclePhase.Normal;

        [Header("行为倾向（0~100；模块三 3.2）")]
        [Tooltip("激进值：主动接近他人 / 使用武器（8号复现读档时写入，其余号码恒为 50）")]
        public float aggression = 50f;

        [Tooltip("谨慎值：藏匿 / 回避 / 待在房间（同上）")]
        public float caution = 50f;

        [Tooltip("好奇值：搜查房间 / 调查线索（同上）")]
        public float curiosity = 50f;

        [Tooltip("合作值：主动对话 / 前往公共区域（同上）")]
        public float cooperation = 50f;

        [Header("8号复现（模块三 3.2）")]
        [Tooltip("本轮 8号AI 是否在读档复现上一轮玩家行为")]
        public bool replayFromPlayer;

        [Tooltip("上一轮玩家读过纸条（OpenedNote）")]
        public bool replayedOpenedNote;

        [Tooltip("上一轮玩家无视纸条（IgnoredNote）")]
        public bool replayedIgnoredNote;

        [Tooltip("上一轮玩家的关键事件原始列表（复现与调试参考）")]
        public List<string> replayEvents = new List<string>();

        [Tooltip("上一轮玩家的「情境 trigger → 决策 action → 结果 result」序列（3.2 修正一：复现层按情境匹配逐条执行）")]
        public List<ReplayContext> replayContexts = new List<ReplayContext>();

        [Tooltip("上一轮循环曾被破坏（破局痕迹：LastCycleDisrupted 或记录中含破局情境）——虚假认知的触发依据之一")]
        public bool replayedDisruption;

        [Tooltip("Normal 轮下已执行过一次「寻找 9号」探针（破局复现的一次性印记）")]
        public bool falseCognitionProbed;

        [Header("会议 / 关系（模块三 3.4）")]
        [Tooltip("对每个号码的信任度（索引 = 号码，0 未使用；初始 50）")]
        public float[] trustToward = new float[MaxTrackedNumber + 1];

        [Tooltip("对每个号码的怀疑度（LLM ChangeStat 写入；0~100）")]
        public float[] suspicionToward = new float[MaxTrackedNumber + 1];

        [Tooltip("对每个号码的敌意（LLM ChangeStat 写入；0~100）")]
        public float[] hostilityToward = new float[MaxTrackedNumber + 1];

        [Tooltip("本轮会议中最后一次指控的号码（0 = 未指控）")]
        public int meetingAccusedNumber;

        [Tooltip("玩家带票后该 NPC 打算投的号码（0 = 无；投票系统优先采用）")]
        public int voteIntentNumber = 0;

        [Tooltip("本轮会议中的不在场证明（进入会议前所在房间）")]
        public string meetingAlibiRoom = "";

        [Tooltip("本轮会议是否已发过言")]
        public bool meetingSpoke;

        [Tooltip("本轮会议中被他人指控的次数")]
        public int meetingAccusations;

        [Header("底牌（信息状态）")]
        [Tooltip("自己是否杀过人（1号开局 true）")]
        public bool selfHasKilled;

        [Tooltip("是否持有纸条（8号）")]
        public bool hasNote;

        [Tooltip("是否已相信纸条内容（3.2 由 8号 AI 流程写入）")]
        public bool noteTrusted;

        [Tooltip("纸条是否已完成决策（3.2：8号 AI 复现「读 / 无视」后置位，保证只决策一次）")]
        public bool noteProcessed;

        [Header("真相认知（重构：顿悟必须有依据）")]
        [Tooltip("是否已经亲眼确认「摘下面具后，大家的臉一模一样」")]
        public bool knowsFacesIdentical;

        [Tooltip("是否见过玩家摘下面具后的脸")]
        public bool knowsPlayerFace;

        [Tooltip("是否亲手摘下过 9号的面具（或亲眼看见别人摘下）")]
        public bool sawNinthMaskOff;

        [Header("敌意武器（重构：有杀意就会真的动手）")]
        [Tooltip("是否已把玩家视为必须杀死的目标")]
        public bool wantsPlayerDead;

        [Tooltip("当前握在手里的武器 id（空 = 徒手）")]
        public string armedWeaponId = "";

        [Tooltip("敌意开始时刻（Time.time），用于给其他 NPC 反应时间")]
        public float hostileSince;

        [Tooltip("下一次愿意接受私聊的时刻（避免被反复骚扰）")]
        public float privateChatCooldownUntil;

        // ------------------------------------------------------------------
        // 会议 / 关系辅助（3.4）
        // ------------------------------------------------------------------

        /// <summary>对指定号码的信任度（越界返回 50）。</summary>
        public float GetTrust(int number)
        {
            if (trustToward == null || number < 0 || number >= trustToward.Length) return 50f;
            return trustToward[number];
        }

        /// <summary>增减对指定号码的信任度（自动夹在 0~100）。</summary>
        public void AddTrust(int number, float delta)
        {
            if (number < 0) return;
            if (trustToward == null || trustToward.Length != MaxTrackedNumber + 1)
            {
                float[] rebuilt = new float[MaxTrackedNumber + 1];
                for (int i = 0; i < rebuilt.Length; i++) rebuilt[i] = 50f;
                if (trustToward != null)
                {
                    for (int i = 0; i < trustToward.Length && i < rebuilt.Length; i++)
                        rebuilt[i] = trustToward[i];
                }
                trustToward = rebuilt;
            }
            if (number >= trustToward.Length) return;
            trustToward[number] = Mathf.Clamp(trustToward[number] + delta, 0f, 100f);
        }

        /// <summary>对指定号码的怀疑度（越界返回 0）。</summary>
        public float GetSuspicionToward(int number)
        {
            if (suspicionToward == null || number < 0 || number >= suspicionToward.Length) return 0f;
            return suspicionToward[number];
        }

        /// <summary>增减对指定号码的怀疑度（自动夹在 0~100）。</summary>
        public void AddSuspicionToward(int number, float delta)
        {
            if (number < 0) return;
            EnsureRelationArray(ref suspicionToward);
            if (number >= suspicionToward.Length) return;
            suspicionToward[number] = Mathf.Clamp(suspicionToward[number] + delta, 0f, 100f);
        }

        /// <summary>对指定号码的敌意（越界返回 0）。</summary>
        public float GetHostilityToward(int number)
        {
            if (hostilityToward == null || number < 0 || number >= hostilityToward.Length) return 0f;
            return hostilityToward[number];
        }

        /// <summary>增减对指定号码的敌意（自动夹在 0~100）。</summary>
        public void AddHostilityToward(int number, float delta)
        {
            if (number < 0) return;
            EnsureRelationArray(ref hostilityToward);
            if (number >= hostilityToward.Length) return;
            hostilityToward[number] = Mathf.Clamp(hostilityToward[number] + delta, 0f, 100f);
        }

        void EnsureRelationArray(ref float[] array)
        {
            if (array != null && array.Length == MaxTrackedNumber + 1) return;

            float[] rebuilt = new float[MaxTrackedNumber + 1];
            if (array != null)
            {
                for (int i = 0; i < array.Length && i < rebuilt.Length; i++)
                    rebuilt[i] = array[i];
            }
            array = rebuilt;
        }

        /// <summary>加入怀疑列表（保留已有顺序，追加到末尾）。</summary>
        public void AddSuspect(int number)
        {
            if (number <= 0) return;
            if (suspectNumbers == null) suspectNumbers = new List<int>();
            if (!suspectNumbers.Contains(number)) suspectNumbers.Add(number);
        }

        /// <summary>把号码提到怀疑列表最前（结构化敌意 / 强指控用）。</summary>
        public void AddSuspectFront(int number)
        {
            if (number <= 0) return;
            if (suspectNumbers == null) suspectNumbers = new List<int>();
            suspectNumbers.Remove(number);
            suspectNumbers.Insert(0, number);
        }

        public void RemoveSuspect(int number)
        {
            if (suspectNumbers != null) suspectNumbers.Remove(number);
        }
    }
}
