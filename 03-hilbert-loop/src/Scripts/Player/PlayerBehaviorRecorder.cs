using System;
using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 8号（玩家）行为记录器（模块三 3.2，单例）。
    /// 玩家扮演 8 号时，把关键行为事件与行为数值记录下来（C 方案：数值驱动 + 事件加权）；
    /// 下一轮由 AI 扮演 8 号时读取复现（见 NPCController.Initialize / ApplyReplayData）。
    ///
    /// 数值（0~100）：aggression 激进 / caution 谨慎 / curiosity 好奇 / cooperation 合作（初始 50）、
    ///               noteTrust 纸条信任（初始 0）。
    /// 事件：OpenedNote / IgnoredNote / InvestigatedRoom / ContactedNPC / HidItem /
    ///       UsedWeapon / ViewedMonitor / RefusedVote / AccusedNPC / StayedInRoom。
    ///
    /// 数据用 PlayerPrefs（JSON）持久化，每次记录立即落盘，跨周目 / 跨 Play 会话保留。
    /// </summary>
    public class PlayerBehaviorRecorder : MonoBehaviour
    {
        public static PlayerBehaviorRecorder Instance { get; private set; }

        // ------------------------------------------------------------------
        // 数值键 / 事件 ID（供其他系统引用，避免拼写漂移）
        // ------------------------------------------------------------------

        public const string StatAggression = "aggression";
        public const string StatCaution = "caution";
        public const string StatCuriosity = "curiosity";
        public const string StatCooperation = "cooperation";
        public const string StatNoteTrust = "noteTrust";

        public const string EventOpenedNote = "OpenedNote";
        public const string EventIgnoredNote = "IgnoredNote";
        public const string EventInvestigatedRoom = "InvestigatedRoom";
        public const string EventContactedNPC = "ContactedNPC";
        public const string EventHidItem = "HidItem";
        public const string EventUsedWeapon = "UsedWeapon";
        public const string EventViewedMonitor = "ViewedMonitor";
        public const string EventRefusedVote = "RefusedVote";
        public const string EventAccusedNPC = "AccusedNPC";
        public const string EventStayedInRoom = "StayedInRoom";

        // ------------------------------------------------------------------
        // PlayerPrefs 键与存储结构
        // ------------------------------------------------------------------

        const string PrefHasData = "HL_PBR_HasData";
        const string PrefStats = "HL_PBR_Stats";
        const string PrefEvents = "HL_PBR_Events";
        const string PrefContexts = "HL_PBR_Contexts"; // 3.2 修正一：情境决策序列

        [Serializable]
        struct StatsData
        {
            public int aggression;
            public int caution;
            public int curiosity;
            public int cooperation;
            public int noteTrust;
        }

        [Serializable]
        class EventListData
        {
            public List<string> events = new List<string>();
        }

        [Serializable]
        class ContextListData
        {
            public List<ReplayContext> contexts = new List<ReplayContext>();
        }

        // ------------------------------------------------------------------
        // 行为数值（0~100）
        // ------------------------------------------------------------------

        int _aggression = 50;
        int _caution = 50;
        int _curiosity = 50;
        int _cooperation = 50;
        int _noteTrust = 0;

        readonly List<string> _events = new List<string>();

        /// <summary>情境决策序列（3.2 修正一）：{trigger, action, result, timestamp}。</summary>
        readonly List<ReplayContext> _contexts = new List<ReplayContext>();

        /// <summary>本轮内"循环被破坏"是否已记入情境序列（破局烙印，一次性）。</summary>
        bool _disruptionLogged;

        // ------------------------------------------------------------------
        // 轮询参数（演示可调）
        // ------------------------------------------------------------------

        [Header("轮询识别参数")]
        [Tooltip("进入一个房间并停留多少秒后记为「搜查了该房间」")]
        public float investigateSeconds = 3f;

        [Tooltip("在同一房间连续停留多少秒记为「长时间待在房间」")]
        public float stayInRoomSeconds = 30f;

        [Tooltip("与 NPC 距离多近算「主动接触」")]
        public float npcContactDistance = 1.6f;

        [Tooltip("两次「接触 NPC」记录的最小间隔（秒）")]
        public float npcContactCooldown = 30f;

        [Tooltip("距监控屏多近算「查看监控」")]
        public float monitorDistance = 3.5f;

        [Tooltip("视线朝向监控屏的最小夹角余弦（1 = 正对）")]
        public float monitorViewDot = 0.35f;

        [Tooltip("两次「查看监控」记录的最小间隔（秒）")]
        public float monitorCooldown = 60f;

        [Tooltip("拿到纸条后多少秒未阅读记为「无视纸条」")]
        public float ignoredNoteSeconds = 30f;

        [Tooltip("轮询间隔（秒）")]
        public float pollInterval = 1f;

        // ------------------------------------------------------------------
        // 运行时轮询状态
        // ------------------------------------------------------------------

        PlayerController _player;
        MonitorTracked _playerTracked;
        float _pollTimer;

        string _currentRoom;
        float _roomEnterTime;
        bool _stayedReported;
        readonly HashSet<string> _investigatedRooms = new HashSet<string>();

        float _npcContactCooldownEnd;
        float _monitorCooldownEnd;

        float _notePickupTime = -1f;
        bool _noteDecisionMade; // 本张纸条已做出「读」或「无视」决定（取走 / 丢弃后复位）

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        void Awake()
        {
            Instance = this;
            Load();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!CycleState.isPlayerAs8) return; // 只有玩家扮演 8 号时才记录

            _pollTimer -= Time.deltaTime;
            if (_pollTimer > 0f) return;
            _pollTimer = pollInterval;
            Poll();
        }

        // ------------------------------------------------------------------
        // 对外接口（任务书要求的最小接口集）
        // ------------------------------------------------------------------

        /// <summary>记录一个关键行为事件：写入事件列表、按规则调整数值、立即持久化。</summary>
        public void RecordEvent(string eventID)
        {
            RecordEvent(eventID, null);
        }

        /// <summary>
        /// 记录一个关键行为事件（3.2 修正一扩展）：额外附带对象说明（如房间名 / NPC号码），
        /// 用于情境决策序列的动作标注（SearchedRoom_3 / ApproachedNPC_7）。
        /// </summary>
        public void RecordEvent(string eventID, string subject)
        {
            if (string.IsNullOrEmpty(eventID)) return;
            if (!CycleState.isPlayerAs8) return; // 档案只来自"玩家扮演 8 号"的周目

            // 纸条二选一：同一张纸条上「打开」与「无视」只记先发生的一个
            if (eventID == EventOpenedNote)
            {
                if (_noteDecisionMade) return;
                _noteDecisionMade = true;
            }

            _events.Add(eventID);
            ApplyStatRules(eventID);
            RecordContext(eventID, subject); // 3.2 修正一：带情境的决策序列
            Save();

            Debug.Log($"[PBR] 记录事件 {eventID} → [aggression={_aggression} caution={_caution} " +
                      $"curiosity={_curiosity} cooperation={_cooperation} noteTrust={_noteTrust}]");
        }

        /// <summary>读取行为数值（aggression / caution / curiosity / cooperation / noteTrust）。</summary>
        public int GetStat(string statName)
        {
            switch (statName)
            {
                case StatAggression: return _aggression;
                case StatCaution: return _caution;
                case StatCuriosity: return _curiosity;
                case StatCooperation: return _cooperation;
                case StatNoteTrust: return _noteTrust;
                default: return 0;
            }
        }

        /// <summary>关键事件列表（副本）。</summary>
        public List<string> GetEvents()
        {
            return new List<string>(_events);
        }

        /// <summary>周目重置时调用：只复位"本轮的轮询判定状态"，数值与事件跨周目保留。</summary>
        public void ResetForNewCycle()
        {
            _investigatedRooms.Clear();
            _currentRoom = null;
            _stayedReported = false;
            _npcContactCooldownEnd = 0f;
            _monitorCooldownEnd = 0f;
            _notePickupTime = -1f;
            _noteDecisionMade = false;
            _disruptionLogged = false;
            Debug.Log($"[PBR] 周目重置：跨周目记录保留（{_events.Count} 条事件 / {_contexts.Count} 条情境决策，数值将继续累计）。");
        }

        // ------------------------------------------------------------------
        // 数值更新规则（任务书给出的 4 条为准；其余事件为合理的补充规则）
        // ------------------------------------------------------------------

        void ApplyStatRules(string eventID)
        {
            switch (eventID)
            {
                // —— 任务书明确规则 ——
                case EventUsedWeapon:      _aggression += 15; _caution -= 5; break;
                case EventHidItem:         _caution += 10; _aggression -= 5; break;
                case EventOpenedNote:      _curiosity += 10; _noteTrust += 20; break;
                case EventStayedInRoom:    _caution += 5; _curiosity -= 5; break;

                // —— 补充规则（任务书未逐条给出的其余事件） ——
                case EventIgnoredNote:     _noteTrust -= 20; _curiosity -= 5; break;
                case EventInvestigatedRoom:_curiosity += 8; _caution += 2; break;
                case EventContactedNPC:    _cooperation += 8; _aggression -= 2; break;
                case EventViewedMonitor:   _caution += 6; _curiosity += 3; break;
                case EventAccusedNPC:      _aggression += 8; _cooperation -= 5; break;
                case EventRefusedVote:     _cooperation -= 5; _caution += 5; break;
            }

            _aggression = Mathf.Clamp(_aggression, 0, 100);
            _caution = Mathf.Clamp(_caution, 0, 100);
            _curiosity = Mathf.Clamp(_curiosity, 0, 100);
            _cooperation = Mathf.Clamp(_cooperation, 0, 100);
            _noteTrust = Mathf.Clamp(_noteTrust, 0, 100);
        }

        // ------------------------------------------------------------------
        // 情境决策序列（3.2 修正一：记录层）
        // ------------------------------------------------------------------

        /// <summary>把一次关键事件登记为「情境 → 决策 → 结果」记录（复现层的输入）。</summary>
        void RecordContext(string eventID, string subject)
        {
            string trigger = IdentifyPlayerContext();
            string action = BuildAction(eventID, subject);
            string result = BuildResult(eventID);

            _contexts.Add(new ReplayContext(trigger, action, result, Time.time));
            Debug.Log($"[PBR] 记录情境决策（第 {_contexts.Count} 条）：{trigger} → {action}（{result}）");
        }

        /// <summary>记录时（玩家视角）的当前情境：房间 / 附近人员 / 循环与阶段状态。</summary>
        string IdentifyPlayerContext()
        {
            string roomName = _playerTracked != null && _playerTracked.CurrentRoom != null
                ? _playerTracked.CurrentRoom.roomName
                : null;

            bool personNear = _player != null &&
                ReplayContexts.AnyOtherPersonNear(null, _player.transform.position, npcContactDistance, false);

            return ReplayContexts.Identify(roomName, personNear);
        }

        /// <summary>事件 → 动作标注（尽量带上对象：SearchedRoom_3 / ApproachedNPC_7）。</summary>
        static string BuildAction(string eventID, string subject)
        {
            switch (eventID)
            {
                case EventOpenedNote: return "OpenedNote";
                case EventIgnoredNote: return "IgnoredNote";
                case EventInvestigatedRoom:
                    return string.IsNullOrEmpty(subject) ? "SearchedRoom" : "SearchedRoom_" + RoomDigits(subject);
                case EventContactedNPC:
                    return string.IsNullOrEmpty(subject) ? "ApproachedNPC" : "ApproachedNPC_" + subject;
                case EventHidItem: return "HidItem";
                case EventUsedWeapon: return "UsedWeapon";
                case EventViewedMonitor: return "ViewedMonitor";
                case EventRefusedVote: return "RefusedVote";
                case EventAccusedNPC: return "AccusedNPC";
                case EventStayedInRoom: return "StayedInRoom";
                default: return eventID;
            }
        }

        /// <summary>事件 → 结果标注。</summary>
        static string BuildResult(string eventID)
        {
            switch (eventID)
            {
                case EventOpenedNote: return "NoteRead";
                case EventIgnoredNote: return "NoteIgnored";
                case EventInvestigatedRoom: return "ClueFound";
                case EventContactedNPC: return "Contacted";
                case EventHidItem: return "ItemHidden";
                case EventUsedWeapon: return "WeaponUsed";
                case EventViewedMonitor: return "MonitorChecked";
                case EventStayedInRoom: return "Stayed";
                default: return "Recorded";
            }
        }

        static string RoomDigits(string roomName)
        {
            return roomName != null ? roomName.Replace("号房", "").Trim() : "?";
        }

        // ------------------------------------------------------------------
        // 轮询判定（无法用单次交互直接挂钩的行为）
        // ------------------------------------------------------------------

        void Poll()
        {
            if (_player == null)
            {
                _player = FindObjectOfType<PlayerController>();
                if (_player == null) return;
                _playerTracked = _player.GetComponent<MonitorTracked>();
            }

            PollRoom();
            PollNpcContact();
            PollMonitor();
            PollNote();
            PollDisruption();
        }

        /// <summary>破局烙印（3.2 修正一）：本轮经历循环破坏时，记一条情境决策（跨轮复现依据）。</summary>
        void PollDisruption()
        {
            if (_disruptionLogged) return;
            if (CycleState.currentPhase != CyclePhase.Disrupted) return;

            _disruptionLogged = true;
            _contexts.Add(new ReplayContext(ReplayContexts.CycleDisrupted,
                "WitnessedDisruption", "CycleBroken", Time.time));
            Save();
            Debug.Log("[PBR] 记录破局烙印：循环被破坏（Disrupted）——" +
                      "该痕迹将跨轮保留，成为下一轮 8号AI「复现破局」的依据。");
        }

        /// <summary>InvestigatedRoom（进入房间停留片刻）/ StayedInRoom（长时间不出门）。</summary>
        void PollRoom()
        {
            string roomName = _playerTracked != null && _playerTracked.CurrentRoom != null
                ? _playerTracked.CurrentRoom.roomName
                : null;

            if (roomName != _currentRoom)
            {
                _currentRoom = roomName;
                _roomEnterTime = Time.time;
                _stayedReported = false;
                return;
            }
            if (string.IsNullOrEmpty(roomName)) return;

            float stayed = Time.time - _roomEnterTime;

            if (stayed >= investigateSeconds && _investigatedRooms.Add(roomName))
                RecordEvent(EventInvestigatedRoom, roomName);

            if (stayed >= stayInRoomSeconds && !_stayedReported)
            {
                _stayedReported = true;
                RecordEvent(EventStayedInRoom);
            }
        }

        /// <summary>ContactedNPC：与最近的非死亡 NPC 贴近（冷却内不重复）。</summary>
        void PollNpcContact()
        {
            if (Time.time < _npcContactCooldownEnd) return;

            NPCController[] npcs = FindObjectsOfType<NPCController>();
            NPCController nearest = null;
            float best = float.MaxValue;
            for (int i = 0; i < npcs.Length; i++)
            {
                NPCController npc = npcs[i];
                if (npc == null || npc.isDead || !npc.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(_player.transform.position, npc.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = npc;
                }
            }

            if (best <= npcContactDistance && nearest != null)
            {
                _npcContactCooldownEnd = Time.time + npcContactCooldown;
                RecordEvent(EventContactedNPC, nearest.Number.ToString());
            }
        }

        /// <summary>ViewedMonitor：站在监控屏前并朝它看（冷却内不重复）。</summary>
        void PollMonitor()
        {
            if (Time.time < _monitorCooldownEnd) return;

            MonitorSystem monitor = MonitorSystem.Instance;
            if (monitor == null) return;

            Vector3 screenPos;
            if (monitor.dotLayer != null) screenPos = monitor.dotLayer.position;
            else if (monitor.monitorRawImage != null) screenPos = monitor.monitorRawImage.transform.position;
            else return;

            Vector3 to = screenPos - _player.EyePosition;
            if (to.magnitude > monitorDistance) return;
            if (Vector3.Dot(_player.EyeRotation * Vector3.forward, to.normalized) < monitorViewDot) return;

            _monitorCooldownEnd = Time.time + monitorCooldown;
            RecordEvent(EventViewedMonitor);
        }

        /// <summary>IgnoredNote：拿到纸条后迟迟不读（与 OpenedNote 互斥，先发生者为准）。</summary>
        void PollNote()
        {
            bool hasNote = _player.inventory != null && _player.inventory.Has("note");

            if (!hasNote)
            {
                // 纸条不在身上（未拾取 / 已丢弃 / 已藏匿）：本张纸条的生命周期结束
                _notePickupTime = -1f;
                _noteDecisionMade = false;
                return;
            }

            if (_notePickupTime < 0f)
            {
                _notePickupTime = Time.time; // 首次发现持有纸条：开始计时
                return;
            }

            if (!_noteDecisionMade && Time.time - _notePickupTime >= ignoredNoteSeconds)
            {
                _noteDecisionMade = true;
                RecordEvent(EventIgnoredNote);
            }
        }

        // ------------------------------------------------------------------
        // 持久化（PlayerPrefs + JSON）
        // ------------------------------------------------------------------

        void Load()
        {
            if (!HasReplayData) return;

            StatsData stats = JsonUtility.FromJson<StatsData>(PlayerPrefs.GetString(PrefStats, ""));
            _aggression = stats.aggression;
            _caution = stats.caution;
            _curiosity = stats.curiosity;
            _cooperation = stats.cooperation;
            _noteTrust = stats.noteTrust;

            EventListData list = JsonUtility.FromJson<EventListData>(PlayerPrefs.GetString(PrefEvents, ""));
            if (list != null && list.events != null) _events.AddRange(list.events);

            ContextListData contexts = JsonUtility.FromJson<ContextListData>(PlayerPrefs.GetString(PrefContexts, ""));
            if (contexts != null && contexts.contexts != null) _contexts.AddRange(contexts.contexts);

            Debug.Log($"[PBR] 已载入跨周目行为记录：{_events.Count} 条事件 / {_contexts.Count} 条情境决策，数值 " +
                      $"[aggression={_aggression} caution={_caution} curiosity={_curiosity} " +
                      $"cooperation={_cooperation} noteTrust={_noteTrust}]");
        }

        void Save()
        {
            var stats = new StatsData
            {
                aggression = _aggression,
                caution = _caution,
                curiosity = _curiosity,
                cooperation = _cooperation,
                noteTrust = _noteTrust,
            };

            PlayerPrefs.SetInt(PrefHasData, 1);
            PlayerPrefs.SetString(PrefStats, JsonUtility.ToJson(stats));
            PlayerPrefs.SetString(PrefEvents, JsonUtility.ToJson(new EventListData { events = _events }));
            PlayerPrefs.SetString(PrefContexts, JsonUtility.ToJson(new ContextListData { contexts = SnapshotContexts() }));
            PlayerPrefs.Save();
        }

        /// <summary>保存快照：统一复位 consumed——跨轮复现时每条记录都可再次执行。</summary>
        List<ReplayContext> SnapshotContexts()
        {
            var copy = new List<ReplayContext>(_contexts.Count);
            for (int i = 0; i < _contexts.Count; i++)
            {
                ReplayContext c = _contexts[i];
                if (c == null) continue;
                copy.Add(new ReplayContext(c.trigger, c.action, c.result, c.timestamp));
            }
            return copy;
        }

        /// <summary>是否存在可用的上一轮 8 号行为记录（8号AI 是否生成的开关）。</summary>
        public static bool HasReplayData => PlayerPrefs.GetInt(PrefHasData, 0) == 1;

        /// <summary>清除全部行为记录（编辑器菜单「循环模拟 → 清除 8号行为记录」）。</summary>
        public static void DeleteSavedData()
        {
            PlayerPrefs.DeleteKey(PrefHasData);
            PlayerPrefs.DeleteKey(PrefStats);
            PlayerPrefs.DeleteKey(PrefEvents);
            PlayerPrefs.DeleteKey(PrefContexts);
            PlayerPrefs.Save();
            Debug.Log("[PBR] 8号行为记录已清除（下一轮不会生成 8号AI）。");
        }

        /// <summary>已保存数据的摘要文本（编辑器菜单 / 调试用；无记录时返回 null）。</summary>
        public static string DescribeSavedData()
        {
            if (!HasReplayData) return null;

            StatsData stats = JsonUtility.FromJson<StatsData>(PlayerPrefs.GetString(PrefStats, ""));
            EventListData list = JsonUtility.FromJson<EventListData>(PlayerPrefs.GetString(PrefEvents, ""));
            ContextListData contexts = JsonUtility.FromJson<ContextListData>(PlayerPrefs.GetString(PrefContexts, ""));
            int count = list != null && list.events != null ? list.events.Count : 0;
            int contextCount = contexts != null && contexts.contexts != null ? contexts.contexts.Count : 0;
            string events = count > 0 ? string.Join(",", list.events) : "无";

            return $"aggression={stats.aggression} caution={stats.caution} curiosity={stats.curiosity} " +
                   $"cooperation={stats.cooperation} noteTrust={stats.noteTrust}；事件 {count} 条：{events}；" +
                   $"情境决策 {contextCount} 条。";
        }

        /// <summary>
        /// 直接写入一组行为数据（编辑器菜单「循环模拟 → 写入演示数据」的调试入口）：
        /// 无需完整游玩记录即可快速验证「8号AI 复现」链路。
        /// </summary>
        public static void WriteDemoData(int aggression, int caution, int curiosity, int cooperation,
            int noteTrust, params string[] events)
        {
            var stats = new StatsData
            {
                aggression = aggression,
                caution = caution,
                curiosity = curiosity,
                cooperation = cooperation,
                noteTrust = noteTrust,
            };

            var list = new EventListData();
            if (events != null) list.events.AddRange(events);

            PlayerPrefs.SetInt(PrefHasData, 1);
            PlayerPrefs.SetString(PrefStats, JsonUtility.ToJson(stats));
            PlayerPrefs.SetString(PrefEvents, JsonUtility.ToJson(list));
            PlayerPrefs.Save();
            Debug.Log($"[PBR] 已写入演示行为数据：{DescribeSavedData()}");
        }

        /// <summary>
        /// 直接写入一组情境决策（调试入口，3.2 修正一）：
        /// 与 WriteDemoData 组合使用，快速验证「8号AI 情境匹配复现」链路。
        /// </summary>
        public static void WriteDemoContexts(params ReplayContext[] contexts)
        {
            var data = new ContextListData();
            if (contexts != null)
            {
                for (int i = 0; i < contexts.Length; i++)
                    if (contexts[i] != null) data.contexts.Add(contexts[i]);
            }

            PlayerPrefs.SetString(PrefContexts, JsonUtility.ToJson(data));
            PlayerPrefs.Save();
            Debug.Log($"[PBR] 已写入 {data.contexts.Count} 条演示情境决策。");
        }

        /// <summary>
        /// 8号 AI 复现：把上一轮玩家（8号）的行为数据注入黑板。
        /// 由 NPCController.Initialize 在"非玩家扮演的 8号"生成时调用。
        /// </summary>
        public static bool ApplyReplayData(NPCBlackboard board)
        {
            if (board == null || !HasReplayData) return false;

            StatsData stats = JsonUtility.FromJson<StatsData>(PlayerPrefs.GetString(PrefStats, ""));
            EventListData list = JsonUtility.FromJson<EventListData>(PlayerPrefs.GetString(PrefEvents, ""));
            List<string> events = list != null && list.events != null ? list.events : new List<string>();

            board.aggression = stats.aggression;
            board.caution = stats.caution;
            board.curiosity = stats.curiosity;
            board.cooperation = stats.cooperation;
            board.noteTrusted = stats.noteTrust > 50;

            board.replayFromPlayer = true;
            board.replayedOpenedNote = events.Contains(EventOpenedNote);
            board.replayedIgnoredNote = events.Contains(EventIgnoredNote);
            board.replayEvents = new List<string>(events);

            // ---- 3.2 修正一：注入情境决策序列 + 破局痕迹 ----
            ContextListData contextData = JsonUtility.FromJson<ContextListData>(PlayerPrefs.GetString(PrefContexts, ""));
            List<ReplayContext> contexts = contextData != null && contextData.contexts != null
                ? contextData.contexts
                : new List<ReplayContext>();
            for (int i = 0; i < contexts.Count; i++)
                if (contexts[i] != null) contexts[i].consumed = false; // 每次载入都从零开始复现

            board.replayContexts = contexts;
            board.replayedDisruption =
                CycleState.LastCycleDisrupted ||
                contexts.Exists(c => c != null && c.trigger == ReplayContexts.CycleDisrupted);

            Debug.Log($"[PBR] 8号AI 已读取上一轮玩家行为：aggression={stats.aggression}、caution={stats.caution}、" +
                      $"curiosity={stats.curiosity}、cooperation={stats.cooperation}、noteTrust={stats.noteTrust}" +
                      $"（noteTrusted={board.noteTrusted}，事件 {events.Count} 条）；" +
                      $"情境决策 {contexts.Count} 条；破局痕迹={board.replayedDisruption}。" +
                      "行为树将按「情境匹配精确复现 → 数值倾向兜底」执行。");
            return true;
        }
    }
}
