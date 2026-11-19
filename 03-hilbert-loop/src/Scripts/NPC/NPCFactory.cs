using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 工厂：运行时按数据与「本轮角色配置」（CycleState）生成 NPC。
    /// - 正常循环：9号已死（尸体在 Room_9），不生成；
    /// - 1号拒杀后：9号在第 85 分钟（倒计时 0:05）活着醒来（Room_9 的门会从里面拨开），
    ///   最迟第 89 分钟被 8号AI / 其他 NPC 杀青；
    /// - 10号：旧设计的“无房间异常者”，当前剧本已停用（不会生成，也不需要新地图）；
    /// - 玩家扮演的号码不生成；
    /// - 8号AI：非玩家扮演且存在上一轮行为记录时才生成；
    /// - 2~7号：恒生成。
    ///
    /// 每个 NPC = 胶囊占位体 + 斗篷 / 面具（有色材质占位）+ 号码（TextMeshPro）
    ///          + 通用行为树"大脑"（克隆自 NPC_Common_Brain 预制体，初始沉睡）。
    /// 所有 NPC 共用同一棵行为树蓝图；差异仅来自各自的 NPCData 与黑板信息状态。
    /// </summary>
    public class NPCFactory : MonoBehaviour
    {
        [Header("数据与资产（编辑器装配）")]
        [Tooltip("NPC 数据资产（1~9号；8号玩家 / 10号由运行时数据补足）")]
        public NPCData[] npcData;

        [Tooltip("通用行为树预制体（Assets/Resources/NPC/NPC_Common_Brain.prefab）")]
        public GameObject brainPrefab;

        [Tooltip("NPC 正式模型预制体（Assets/Resources/NPC/NPC_Entity.prefab；为空时退回胶囊占位体）")]
        public GameObject entityPrefab;

        [Tooltip("各号码的房间锚点（索引 0 = 1号房），用于出生与归位")]
        public Transform[] roomAnchors;

        readonly List<NPCController> _spawned = new List<NPCController>();

        /// <summary>已生成的 NPC（按生成顺序，即号码升序）。</summary>
        public IReadOnlyList<NPCController> Spawned => _spawned;

        // 占位外观配色
        static readonly Color BodyColor = new Color(0.20f, 0.20f, 0.24f); // 深色布衣

        // 床几何（与 MainSceneBuilder.BuildBed 一致）：床在房间本地 (-1.6, 0, 0.3)，床垫顶 0.48。
        // 躺姿：胶囊横躺、中心离床面一个半径 0.35 → y = 0.83；站立点：床边空地（防穿模）。
        static readonly Vector3 BedLieLocal = new Vector3(-1.6f, 0.83f, 0.35f);
        static readonly Vector3 BedStandLocal = new Vector3(-0.55f, 0.9f, 0.3f);

        // 正式模型根节点 = 胶囊中心；床面 0.48m，胶囊中心取 0.9m，再由 Visual 的躺姿偏移落回床面。
        static readonly Vector3 PrefabBedLieLocal = new Vector3(-1.6f, 0.9f, 0.35f);

        // 10号：没有房间，循环多出来的那一个人睡在大厅地板上（圆桌区南侧空地）。
        static readonly Vector3 LobbyLiePosition = new Vector3(1.65f, 0.35f, -2.15f);
        static readonly Vector3 LobbyStandPosition = new Vector3(1.65f, 0.9f, -2.15f);

        NPCData _runtimeTenthData;

        /// <summary>10号的运行时数据（没有资产、没有房间；名字与号码仍是剧本的一部分）。</summary>
        NPCData TenthData
        {
            get
            {
                if (_runtimeTenthData == null)
                {
                    _runtimeTenthData = ScriptableObject.CreateInstance<NPCData>();
                    _runtimeTenthData.name = "NPC_10_Runtime";
                    _runtimeTenthData.number = 10;
                    _runtimeTenthData.displayName = "10号（无房者）";
                    _runtimeTenthData.wakeupDelay = LoopTimeline.TenthWakeMinute;
                    _runtimeTenthData.hasNails = false;
                    _runtimeTenthData.hasNote = false;
                    _runtimeTenthData.isPlayerControlled = false;
                    _runtimeTenthData.maskNumberColor = new Color(0.12f, 0.12f, 0.14f); // 近乎全黑的面具
                }
                return _runtimeTenthData;
            }
        }

        void Start()
        {
            // 主菜单模式：名册完全由 MainMenuController 的残缺循环接管。
            if (GameMode.MenuActive) return;

            // 「继续游戏」：按存档里的真实存活名单重建，死者不再出现在这一轮。
            if (SaveManager.TryConsumePendingRoster(out List<int> roster))
            {
                ResetForNewLoop(roster);
                Debug.Log($"[NPCFactory] 已按存档恢复名册：{string.Join("、", roster)}。");
                return;
            }

            SpawnAll();
        }

        /// <summary>
        /// 3.5 天亮轮回：销毁全部旧 NPC 与尸体，按新的号码名册原地重建。
        /// 新名册由 CycleResetSystem 计算（存活者 +1 顺移、去重、去掉玩家号码与 9 号）。
        /// </summary>
        public void ResetForNewLoop(IReadOnlyCollection<int> numbers)
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                NPCController npc = _spawned[i];
                if (npc == null) continue;
                npc.gameObject.SetActive(false); // 立即从 NPCController.All / 监控注册表移除
                Destroy(npc.gameObject);
            }
            _spawned.Clear();

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.name.StartsWith("NPC_Corpse_"))
                {
                    child.gameObject.SetActive(false);
                    Destroy(child.gameObject);
                }
            }

            if (numbers == null || numbers.Count == 0)
            {
                if (CycleState.playerNumber == 1 && !CycleState.kill9Executed)
                    EnsureSleepingNinthForPlayerOne();
                return;
            }

            var sorted = new List<int>(numbers);
            sorted.Sort();

            for (int i = 0; i < sorted.Count; i++)
            {
                int number = sorted[i];
                NPCData data = FindData(number);
                if (data == null)
                {
                    Debug.LogWarning($"[NPCFactory] 名册中有 {number} 号，但没有对应的 NPCData，跳过。");
                    continue;
                }

                if (number == 9)
                {
                    // 9 号只有在两种情况下是活人：
                    // - 玩家扮演 1 号：9 号正睡在床上，等玩家动手；
                    // - 主菜单：循环演示需要完整的 1~9。
                    // 其他情况 9 号已经被 1 号杀死，由 Room_9 床上的尸体占位体代表。
                    if (GameMode.MenuActive || (CycleState.playerNumber == 1 && !CycleState.kill9Executed))
                        Spawn(data, 9, LoopTimeline.SurvivingNinthWakeMinute,
                            false, GameMode.MenuActive);
                    else
                        Debug.Log("[NPCFactory] 9号按既定流程死在玩家醒来前：不生成活人，使用床上的尸体占位体。");
                }
                else if (number == 10)
                {
                    // 10号已从当前剧本移除：名册里即使残留 10 也不生成。
                    Debug.LogWarning("[NPCFactory] 名册中的 10号已停用，跳过生成。");
                }
                else
                {
                    Spawn(data);
                }
            }

            // 玩家以 1 号进入新一轮：9号不是「既定受害者」，而是还活着睡在 Room_9 床上。
            // 名册里可能已有 9号（破坏轮延续），Ensure 会直接复用，不会重复生成。
            if (CycleState.playerNumber == 1 && !CycleState.kill9Executed)
                EnsureSleepingNinthForPlayerOne();

            Debug.Log($"[NPCFactory] 新一轮 NPC 名册已重建：{string.Join("、", sorted)} 号（共 {_spawned.Count} 名）。");
        }

        NPCData FindData(int number)
        {
            if (number == 10) return TenthData;
            if (npcData == null) return null;
            for (int i = 0; i < npcData.Length; i++)
            {
                if (npcData[i] != null && npcData[i].number == number) return npcData[i];
            }
            return null;
        }

        /// <summary>按号码查找当前存活的已生成 NPC（死亡 / 未生成返回 null）。</summary>
        public NPCController FindSpawned(int number)
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                NPCController npc = _spawned[i];
                if (npc != null && npc.Number == number && !npc.isDead) return npc;
            }
            return null;
        }

        /// <summary>
        /// 玩家扮演 1 号且循环尚未结算时，9号应当还活着睡在 Room_9 床上：
        /// 生成一个沉睡的 9号（第 85 游戏分钟醒来），并隐藏床上的尸体占位体。
        /// 重复调用安全；其他号码局不调用（那些局里 9号是开局既定尸体）。
        /// </summary>
        public NPCController EnsureSleepingNinthForPlayerOne()
        {
            if (CycleState.playerNumber != 1 || CycleState.kill9Executed) return null;

            // 这一轮名册最大号码还没到 9：没有第九席，床上也不该有活着的人。
            if (CycleState.currentKnownMax < 9) return null;

            NPCController existing = FindSpawned(9);
            if (existing != null)
            {
                HideNinthBedCorpse();
                return existing;
            }

            NPCData data = FindData(9);
            if (data == null)
            {
                Debug.LogError("[NPCFactory] 缺少 9号 NPCData，无法生成沉睡的 9号。");
                return null;
            }

            NPCController npc = Spawn(data, 9, LoopTimeline.SurvivingNinthWakeMinute);
            if (npc != null)
            {
                // 活人 9号与床上的既定尸体不能同时出现。
                HideNinthBedCorpse();
                Debug.Log("[NPCFactory] 玩家扮演 1 号：9号还活着，正在 Room_9 床上沉睡，" +
                          $"第 {LoopTimeline.SurvivingNinthWakeMinute} 游戏分钟醒来。");
            }
            return npc;
        }

        /// <summary>隐藏 Room_9 床上那个「既定受害者」尸体占位体（9号活着时）。</summary>
        void HideNinthBedCorpse()
        {
            NinthVictim[] victims = FindObjectsOfType<NinthVictim>(true);
            for (int i = 0; i < victims.Length; i++)
            {
                if (victims[i] != null) victims[i].ResetVictim(false);
            }
        }

        /// <summary>
        /// 1号拒杀后，让 9号在第 85 游戏分钟（倒计时 0:05）从床上醒来：
        /// 隐藏 Room_9 里的尸体占位体，原地生成一个活着的 9号，并让门从里面打开。
        /// 只应在循环处于 Disrupted 且 9号尚未生成时调用。
        /// </summary>
        public NPCController SpawnSurvivingNinthNow()
        {
            NPCController existing = FindSpawned(9);
            if (existing != null) return existing;

            if (CycleState.kill9Executed)
            {
                Debug.LogWarning("[NPCFactory] 循环已经恢复（9号已死），不再生成活着的 9号。");
                return null;
            }

            NPCData data = FindData(9);
            if (data == null)
            {
                Debug.LogError("[NPCFactory] 缺少 9号 NPCData，无法让 9号醒来。");
                return null;
            }

            // 床上的“既定受害者”尸体与活着的 9号不能同时存在。
            NinthVictim[] victims = FindObjectsOfType<NinthVictim>(true);
            for (int i = 0; i < victims.Length; i++)
            {
                if (victims[i] != null) victims[i].SetBodyPresent(false);
            }

            NPCController npc = Spawn(data, 9, LoopTimeline.SurvivingNinthWakeMinute, false, true);
            if (npc != null)
            {
                // 9号活着时，纸条从 8号转到 9号手里：9号没有比自己号码更大的目标，
                // 这张「活过这一轮就能逃出生天」的纸条才能真正稳住它；
                // 8号则因为目标（9号）仍在，继续保持错误认知。
                if (npc.blackboard != null)
                {
                    npc.blackboard.hasNote = true;
                    npc.blackboard.noteTrusted = true;
                    npc.blackboard.noteProcessed = false;
                }
                Debug.Log("[NPCFactory] 9号在第 85 游戏分钟醒来——床上的尸体消失了，" +
                          "Room_9 的门从里面被拨开，手里攥着8号的纸条。");
            }
            return npc;
        }

        /// <summary>
        /// 10号生成链路已停用：9号若活着，8号AI 会在会议前杀死它；
        /// 即使 8号没能自然得手，CycleController 也会在第 89 分钟完成保底刺杀。
        /// 9号必定杀青，所以 10号不会出现，也不需要为它准备房间或地图。
        /// </summary>
        public NPCController SpawnSurvivingTenthNow()
        {
            Debug.LogWarning("[NPCFactory] 10号已从当前剧本移除：9号会在第 90 分钟（0:00）会议前被杀青。");
            return null;
        }

        // ------------------------------------------------------------------
        // 生成
        // ------------------------------------------------------------------

        void SpawnAll()
        {
            if (npcData == null || npcData.Length == 0)
            {
                Debug.LogWarning("[NPCFactory] 未配置 NPC 数据，跳过生成。");
                return;
            }

            if (brainPrefab == null)
                Debug.LogError("[NPCFactory] 未配置行为树预制体（NPC_Common_Brain），NPC 将不会行动。");

            string roleText = CycleState.playerNumber >= 1 && CycleState.playerNumber <= 8
                ? CycleState.playerNumber + "号"
                : "其他号码（观察者）";

            for (int i = 0; i < npcData.Length; i++)
            {
                NPCData data = npcData[i];
                if (data == null) continue;
                if (!ShouldSpawn(data)) continue;

                Spawn(data);
            }

            // 玩家扮演 1 号：9号还活着，正在 Room_9 床上沉睡。
            // 其他号码局不生成 9号——那些局里 9号是玩家醒来前就已被 1号杀死的既定事实。
            if (CycleState.playerNumber == 1 && !CycleState.kill9Executed)
                EnsureSleepingNinthForPlayerOne();

            Debug.Log($"[NPCFactory] 角色配置：玩家扮演{roleText}；已生成 {_spawned.Count} 名 NPC，" +
                      $"全部共用通用行为树。（8号AI 需存在上一轮行为记录：{PlayerBehaviorRecorder.HasReplayData}）");
        }

        /// <summary>
        /// 模块三 3.2：按本轮角色配置决定哪些号码需要生成。
        /// 9号不参与开局自动生成——正常循环里它是尸体，只有拒杀后才由
        /// <see cref="SpawnSurvivingNinthNow"/> 或天亮名册按时间线补入。
        /// </summary>
        bool ShouldSpawn(NPCData data)
        {
            if (data.number < 1 || data.number > 8) return false;
            if (data.number == CycleState.playerNumber) return false;      // 玩家占据的号码：不生成

            if (data.number == 8)
            {
                return PlayerBehaviorRecorder.HasReplayData;               // 8号AI：无记录则不生成
            }

            return true; // 2~7号：恒生成
        }

        /// <summary>
        /// 生成一个 NPC。
        /// </summary>
        /// <param name="data">号码档案。</param>
        /// <param name="roomNumberOverride">出生房间覆盖（0 = 使用号码本身）。</param>
        /// <param name="wakeMinuteOverride">绝对醒来分钟覆盖（NaN = 使用 NPCData.wakeupDelay）。</param>
        /// <param name="spawnInLobby">睡在大厅地板上（10号没有房间）。</param>
        /// <param name="wakeImmediately">跳过时间线立即醒来（剧情到点补生成时使用）。</param>
        NPCController Spawn(NPCData data, int roomNumberOverride = 0, float wakeMinuteOverride = float.NaN,
            bool spawnInLobby = false, bool wakeImmediately = false)
        {
            int number = data.number;
            int roomNumber = roomNumberOverride > 0 ? roomNumberOverride : number;
            float wakeMinute = float.IsNaN(wakeMinuteOverride) ? data.wakeupDelay : wakeMinuteOverride;

            Transform anchor = spawnInLobby ? null : GetAnchor(roomNumber);
            if (!spawnInLobby && anchor == null)
            {
                Debug.LogWarning($"[NPCFactory] 缺少 {roomNumber} 号房锚点，跳过生成 {number} 号。");
                return null;
            }

            const int layer = MonitorSystem.MonitorIgnoreLayer;

            // 大厅躺姿 / 站姿：面向大厅中心（圆桌方向）。
            Vector3 lobbyFaceDir = Vector3.zero - LobbyStandPosition;
            lobbyFaceDir.y = 0f;
            if (lobbyFaceDir.sqrMagnitude < 0.0001f) lobbyFaceDir = Vector3.forward;
            lobbyFaceDir.Normalize();
            Quaternion lobbyStandRot = Quaternion.LookRotation(lobbyFaceDir);

            // ---- 本体根（位置 = 胶囊中心高度） ----
            // 正式模型：直接实例化 NPC_Entity（斗篷 + 面具 + 号码 + Ragdoll + 全部组件）。
            // 占位兜底：没有配置预制体时，维持旧的胶囊 + 斗篷占位。
            bool usePrefab = entityPrefab != null;
            GameObject root;

            if (usePrefab)
            {
                root = Instantiate(entityPrefab, transform);
                root.name = "NPC_" + number;
                root.layer = layer;
                if (spawnInLobby)
                {
                    root.transform.position = LobbyLiePosition;
                    root.transform.rotation = lobbyStandRot * Quaternion.Euler(0f, 180f, 0f);
                }
                else
                {
                    // 正式模型根 = 胶囊中心；Visual 内部已经带躺姿偏移，根节点只需抬到床面。
                    root.transform.position = anchor.TransformPoint(PrefabBedLieLocal);
                    root.transform.rotation = anchor.rotation * Quaternion.Euler(0f, 180f, 0f);
                }
            }
            else
            {
                root = new GameObject("NPC_" + number);
                root.layer = layer;
                root.transform.SetParent(transform, false);
                if (spawnInLobby)
                {
                    root.transform.position = LobbyLiePosition;
                    root.transform.rotation = lobbyStandRot * Quaternion.Euler(-90f, 180f, 0f);
                }
                else
                {
                    root.transform.position = anchor.TransformPoint(BedLieLocal);
                    root.transform.rotation = anchor.rotation * Quaternion.Euler(-90f, 180f, 0f); // 仰卧，头朝枕头
                }

                BuildVisuals(root.transform, data, layer);
            }

            // ---- 物理 / 监控 / 控制组件 ----
            // 3.4 移动修复：用 CharacterController 取代 kinematic Rigidbody。
            // AIPath 在检测到 Rigidbody 时会走 FixedUpdate + MovePosition，完全绕过碰撞；
            // CharacterController 则走 Move()，墙壁 / 关闭的门会真实阻挡，配合网格直径不再穿模。
            CharacterController cc = root.GetComponent<CharacterController>();
            if (cc == null)
            {
                cc = root.AddComponent<CharacterController>();
                cc.center = Vector3.zero;
                cc.height = 1.7f;
                cc.radius = 0.34f;
                cc.skinWidth = 0.04f;
                cc.stepOffset = 0.15f;
                cc.slopeLimit = 45f;
            }

            MonitorTracked tracked = root.GetComponent<MonitorTracked>();
            if (tracked == null) tracked = root.AddComponent<MonitorTracked>();
            tracked.displayName = data.displayName;
            tracked.dotColor = Color.red;
            tracked.hideDotInRooms = true;

            NPCController controller = root.GetComponent<NPCController>();
            if (controller == null) controller = root.AddComponent<NPCController>();

            NPCNavigator navigator = root.GetComponent<NPCNavigator>();
            if (navigator == null) navigator = root.AddComponent<NPCNavigator>();
            navigator.enabled = false; // 沉睡期间不参与寻路；NPCController.ActivateBrain 醒来时启用

            NPCVision vision = root.GetComponent<NPCVision>();
            if (vision == null) vision = root.AddComponent<NPCVision>();
            NPCHearing hearing = root.GetComponent<NPCHearing>();
            if (hearing == null) hearing = root.AddComponent<NPCHearing>();
            NPCDialogueBridge dialogue = root.GetComponent<NPCDialogueBridge>();
            if (dialogue == null) dialogue = root.AddComponent<NPCDialogueBridge>();

            controller.navigator = navigator;
            controller.dialogue = dialogue;
            controller.vision = vision;
            controller.hearing = hearing;

            if (root.GetComponent<NPCEmotionFeedback>() == null) root.AddComponent<NPCEmotionFeedback>();
            if (root.GetComponent<NPCHostileAgent>() == null) root.AddComponent<NPCHostileAgent>();
            if (root.GetComponent<NPCSocialAgent>() == null) root.AddComponent<NPCSocialAgent>();

            // 正式模型：号码 / 面具染色 / 睡眠姿态。
            NPCVisualController visual = root.GetComponent<NPCVisualController>();
            if (visual != null)
            {
                controller.visual = visual;
                visual.Configure(data);
                visual.EnterSleepPose();
            }

            controller.Initialize(data);
            if (spawnInLobby)
            {
                controller.ConfigureBedWakeup(LobbyStandPosition, lobbyStandRot);
            }
            else
            {
                controller.ConfigureBedWakeup(
                    anchor.TransformPoint(BedStandLocal),
                    anchor.rotation * Quaternion.Euler(0f, 180f, 0f)); // 站起后面向房门
            }

            // 9号房默认从里面闩死：活着的 9号醒来时自己拨开门闩。
            if (number == 9)
            {
                GameObject door = GameObject.Find("Door_9");
                if (door != null) controller.wakeupDoor = door.GetComponent<DoorController>();
            }

            // ---- 大脑（行为树预制体克隆，初始沉睡） ----
            if (brainPrefab != null)
            {
                GameObject brain = Instantiate(brainPrefab, root.transform);
                brain.name = "Brain";
                brain.transform.localPosition = Vector3.zero;
                brain.transform.localRotation = Quaternion.identity;
                brain.SetActive(false); // 沉睡：由 StartWakeupSequence / WakeupNow 激活

                controller.behaviourTree = brain.GetComponent<BehaviourTreeRunner>();
            }

            // ---- 1号：杀9号流程执行者（模块三 3.2；简化位移 + 事件触发） ----
            if (number == 1 && root.GetComponent<NPCAssassin>() == null) root.AddComponent<NPCAssassin>();

            _spawned.Add(controller);

            // ---- 醒来时序（错峰：以绝对游戏分钟为准） ----
            float delay = LoopTimeline.DelaySecondsFor(number, wakeMinute);
            if (wakeImmediately)
            {
                Debug.Log($"[NPCFactory] {number}号：剧情到点，立即醒来" +
                          $"（计划中的绝对时间：第 {wakeMinute:0} 游戏分钟）。");
                controller.WakeupNow();
                return controller;
            }

            if (float.IsPositiveInfinity(delay))
            {
                Debug.Log($"[NPCFactory] {number}号：数据标记为永不醒来，保持沉睡。");
            }
            else if (delay <= 0f)
            {
                Debug.Log($"[NPCFactory] {number}号：绝对醒来第 {wakeMinute:0} 游戏分钟，" +
                          "已在玩家醒来前到点 → 开局即醒。");
            }
            else
            {
                Debug.Log($"[NPCFactory] {number}号：绝对醒来第 {wakeMinute:0} 游戏分钟，" +
                          $"相对玩家（第 {LoopTimeline.PlayerWakeMinute} 分钟）延迟 {delay:0} 现实秒。");
            }

            controller.StartWakeupSequence(delay);

            return controller;
        }

        Transform GetAnchor(int number)
        {
            int index = number - 1;
            if (roomAnchors == null || index < 0 || index >= roomAnchors.Length) return null;
            return roomAnchors[index];
        }

        // ------------------------------------------------------------------
        // 视觉占位（胶囊 + 斗篷 + 面具数字）
        // ------------------------------------------------------------------

        static void BuildVisuals(Transform root, NPCData data, int layer)
        {
            // 身体（主胶囊，保留碰撞体）
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.layer = layer;
            body.transform.SetParent(root, false);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            body.GetComponent<MeshRenderer>().sharedMaterial = CreateRuntimeMaterial(BodyColor);

            // 3.4：移动碰撞由根节点的 CharacterController 负责，身体碰撞体只做交互射线目标。
            Collider bodyCollider = body.GetComponent<Collider>();
            if (bodyCollider != null) bodyCollider.isTrigger = true;

            // 斗篷（有色材质占位；去掉碰撞体）
            Color cloakColor = Color.Lerp(data.maskNumberColor, Color.black, 0.35f);
            GameObject cape = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cape.name = "Cape";
            cape.layer = layer;
            RemoveCollider(cape);
            cape.transform.SetParent(root, false);
            cape.transform.localPosition = new Vector3(0f, -0.12f, 0f);
            cape.transform.localScale = new Vector3(0.78f, 0.72f, 0.78f);
            cape.GetComponent<MeshRenderer>().sharedMaterial = CreateRuntimeMaterial(cloakColor);

            // 面具面板（有色材质占位；去掉碰撞体）
            GameObject mask = GameObject.CreatePrimitive(PrimitiveType.Cube);
            mask.name = "Mask";
            mask.layer = layer;
            RemoveCollider(mask);
            mask.transform.SetParent(root, false);
            mask.transform.localPosition = new Vector3(0f, 0.30f, 0.335f);
            mask.transform.localScale = new Vector3(0.30f, 0.34f, 0.05f);
            mask.GetComponent<MeshRenderer>().sharedMaterial = CreateRuntimeMaterial(data.maskNumberColor);

            // 号码（TextMeshPro，正面朝 NPC 前方）
            GameObject number = new GameObject("MaskNumber");
            number.layer = layer;
            number.transform.SetParent(root, false);
            number.transform.localPosition = new Vector3(0f, 0.30f, 0.375f);
            // TMP 3D 文本的可读面在本地 -Z，转 180° 才能让号码正面朝向 NPC 外侧，否则会镜像。
            number.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            number.transform.localScale = Vector3.one * 0.06f;

            TextMeshPro tmp = number.AddComponent<TextMeshPro>();
            tmp.text = data.number.ToString();
            tmp.fontSize = 36f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            ChineseFontUtility.Apply(tmp);
        }

        static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }

        /// <summary>运行时材质（占位配色）。</summary>
        static Material CreateRuntimeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.color = color;
            return material;
        }
    }
}
