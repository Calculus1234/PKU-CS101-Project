using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 个体控制器：数据装配、事件总线订阅、醒来时序、推搡响应、房间进出感知。
    /// 具体行为全部在行为树（NPC_Common_Brain）中 —— 本类不含任何行为逻辑。
    /// 3.2 修正补丁·反馈④：NPC 出生为「躺在床上的睡姿」，醒来时播一段起身动画站到床边。
    /// 3.3：实现 IInteractable / IHostileInteractable / IKillable ——
    ///      E 只负责私聊，鼠标左键负责轻推、窗外推下、武器袭击与死亡；
    ///      死亡时停行为树 / 寻路、生成含藏匿点的尸体、监控红点立即消失。
    /// </summary>
    public class NPCController : MonoBehaviour, IInteractable, IHostileInteractable, IKillable
    {
        /// <summary>场上全部 NPC（含沉睡 / 死亡前的实例；死亡后从列表移除）。</summary>
        public static readonly List<NPCController> All = new List<NPCController>();

        [Header("数据（NPCFactory 装配）")]
        public NPCData data;

        [Header("装配（NPCFactory 装配）")]
        public BehaviourTreeRunner behaviourTree;
        public NPCNavigator navigator;
        public NPCDialogueBridge dialogue;
        public NavMeshAgent agent;
        public Animator animator;

        [Header("正式模型（NPC_Entity 预制体装配）")]
        public NPCVision vision;
        public NPCHearing hearing;
        public NPCVisualController visual;
        public NPCDeathHandler deathHandler;

        [Tooltip("9号醒来时从里面拨开的房门（NPCFactory 装配；10号没有自己的房间，留空）")]
        public DoorController wakeupDoor;

        [Header("运行时状态")]
        public NPCBlackboard blackboard = new NPCBlackboard();
        public bool isDead;
        public string currentRoomName;

        /// <summary>是否处于圆桌会议（由 MeetingSystem 置位：停寻路 / 行为树 / 感知）。</summary>
        public bool InMeeting { get; private set; }

        bool _awake;
        bool _hasBedWakeup;
        Vector3 _bedStandPos;
        Quaternion _bedStandRot;
        float _nextEmotionTick;

        public int Number => data != null ? data.number : -1;
        public bool IsAwake => _awake;

        /// <summary>是否正探头看向窗外（行为树 LeanOutWindow / 1号流程第⑦步驱动）。</summary>
        public bool IsLeaningOut { get; private set; }

        int PlayerNumber => CycleState.playerNumber > 0 ? CycleState.playerNumber : 8;

        void Awake()
        {
            if (navigator == null) navigator = GetComponent<NPCNavigator>();
            if (dialogue == null) dialogue = GetComponent<NPCDialogueBridge>();
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (vision == null) vision = GetComponent<NPCVision>();
            if (hearing == null) hearing = GetComponent<NPCHearing>();
            if (visual == null) visual = GetComponent<NPCVisualController>();
            if (deathHandler == null) deathHandler = GetComponent<NPCDeathHandler>();
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);

            NPCEventBus.OnNPCPushed += HandleOthersPushed;
            NPCEventBus.OnNPCDie += HandleOthersDie;
            NPCEventBus.OnCycleStateChanged += HandleCycleStateChanged;
        }

        void OnDisable()
        {
            All.Remove(this);

            NPCEventBus.OnNPCPushed -= HandleOthersPushed;
            NPCEventBus.OnNPCDie -= HandleOthersDie;
            NPCEventBus.OnCycleStateChanged -= HandleCycleStateChanged;
        }

        /// <summary>情绪缓慢回落：没有可见目标 / 近期声音 / 对峙 / 恐慌时，怀疑度回落到基线 50。</summary>
        void Update()
        {
            if (isDead || InMeeting || InPrivateChat || blackboard == null) return;
            if (Time.time < _nextEmotionTick) return;
            _nextEmotionTick = Time.time + 2f;

            bool calm = (blackboard.visibleTargets == null || blackboard.visibleTargets.Count == 0)
                && Time.time - blackboard.lastHeardTime > 8f
                && Time.time >= blackboard.pushReactionUntil
                && Time.time >= blackboard.panicUntil;

            if (calm)
                blackboard.suspicion = Mathf.MoveTowards(blackboard.suspicion, 50f, 2f);
        }

        // ------------------------------------------------------------------
        // 初始化
        // ------------------------------------------------------------------

        /// <summary>由工厂在生成时调用：把静态数据写入黑板（含"信息状态"初始化）。</summary>
        public void Initialize(NPCData npcData)
        {
            data = npcData;
            if (data == null) return;

            blackboard = new NPCBlackboard
            {
                suspicion = 50f,
                trust = 50f,
                stress = 50f,
                hasNote = data.hasNote,
                // 信息状态：1号已经杀过9号（剧情既定事实，属于"信息差异"而非专属行为）
                selfHasKilled = data.number == 1,
                cycleState = CycleState.currentPhase,
                // 3.3：各人「探头看窗外」的低频机会错开，避免同时涌向窗口
                nextWindowLeanTime = Time.time + Random.Range(25f, 60f) + data.number * 2f,
            };

            // 3.4：会议信任表初始 50（索引 = 号码；10号也需要占一格）
            blackboard.trustToward = new float[NPCBlackboard.MaxTrackedNumber + 1];
            for (int i = 0; i < blackboard.trustToward.Length; i++)
                blackboard.trustToward[i] = 50f;

            // LLM 工具写入的逐号码关系值：怀疑 / 敌意都从 0 开始。
            blackboard.suspicionToward = new float[NPCBlackboard.MaxTrackedNumber + 1];
            blackboard.hostilityToward = new float[NPCBlackboard.MaxTrackedNumber + 1];

            // ---- 8号复现（模块三 3.2）：非玩家扮演时，读取上一轮玩家（8号）的行为数据 ----
            if (data.number == 8 && !CycleState.isPlayerAs8)
            {
                PlayerBehaviorRecorder.ApplyReplayData(blackboard);
            }
        }

        // ------------------------------------------------------------------
        // 醒来时序
        // ------------------------------------------------------------------

        /// <summary>开始醒来倒计时（delay 秒后激活行为树）。delay ≤ 0 立即醒来；正无穷 = 永不醒来。</summary>
        public void StartWakeupSequence(float delay)
        {
            if (_awake) return;

            if (float.IsPositiveInfinity(delay))
            {
                Debug.Log($"[NPC {Number}] 保持沉睡：时间线标记为永不醒来。");
                return;
            }

            if (delay <= 0f)
            {
                WakeupNow();
                return;
            }

            Debug.Log($"[NPC {Number}] 进入沉睡，计划 {delay:0.0} 现实秒后醒来" +
                      $"（循环第 {LoopTimeline.CycleElapsedGameMinutes + delay / LoopTimeline.SecondsPerGameMinute:0.0} 游戏分钟）。");
            StartCoroutine(WakeupAfter(delay));
        }

        IEnumerator WakeupAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            WakeupNow();
        }

        /// <summary>
        /// 配置「床上醒来」（NPCFactory 在生成时调用）：
        /// NPC 先以躺姿出现在床上，WakeupNow 时用起身动画站到 standPos（床边）。
        /// </summary>
        public void ConfigureBedWakeup(Vector3 standPos, Quaternion standRot)
        {
            _hasBedWakeup = true;
            _bedStandPos = standPos;
            _bedStandRot = standRot;
        }

        /// <summary>立即醒来：床上起身动画（或直接激活）后广播事件。</summary>
        public void WakeupNow()
        {
            if (_awake) return;
            _awake = true;

            if (_hasBedWakeup)
                StartCoroutine(StandUpRoutine());
            else
                ActivateBrain();
        }

        /// <summary>从躺姿站起到床边（防穿模：直线路径始终在床体上方）。</summary>
        IEnumerator StandUpRoutine()
        {
            Vector3 fromPos = transform.position;
            Quaternion fromRot = transform.rotation;

            // 正式模型：Visual 从睡眠姿态平滑回到站立，同时播 StandUp 动作。
            if (visual != null) visual.WakeUpVisual(0.9f);

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / 0.9f;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                transform.position = Vector3.Lerp(fromPos, _bedStandPos, k);
                transform.rotation = Quaternion.Slerp(fromRot, _bedStandRot, k);
                yield return null;
            }

            transform.position = _bedStandPos;
            transform.rotation = _bedStandRot;
            ActivateBrain();
        }

        void ActivateBrain()
        {
            // 睡眠期间行为树对象处于失活状态，醒来时激活开始 Tick
            if (behaviourTree != null)
                behaviourTree.gameObject.SetActive(true);

            // 睡眠期间寻路组件关闭，醒来后恢复（否则行为树有目标也不会移动）
            if (navigator != null) navigator.enabled = true;

            // 正式模型：如果之前因会议 / 死亡禁用过 CharacterController，这里恢复。
            CharacterController cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;

            // 9号房默认从里面闩死；活着的 9号自己拨开门闩走出来（不是破门，不触发警报）。
            if (wakeupDoor != null) wakeupDoor.OpenForSurvivor();

            NPCEventBus.RaiseNPCWakeup(Number);

            float elapsed = LoopTimeline.CycleElapsedSeconds;
            Debug.Log($"[NPC {Number}] 实际醒来：现实 +{elapsed:0.0} 秒 / " +
                      $"循环第 {LoopTimeline.CycleElapsedGameMinutes:0.0} 游戏分钟（{gameObject.name}）。");
        }

        // ------------------------------------------------------------------
        // 圆桌会议（模块三 3.4）
        // ------------------------------------------------------------------

        /// <summary>进入会议：立即醒来、停寻路 / 行为树 / 感知，并关闭 CharacterController 以便就座。</summary>
        public void EnterMeetingMode()
        {
            if (isDead || InMeeting) return;

            InMeeting = true;
            StopAllCoroutines(); // 中止起床 / 1号杀人流程等脚本协程
            _awake = true;
            _hasBedWakeup = false;
            if (visual != null) visual.SnapAwake();
            SetLeaningOut(false);

            if (navigator != null) navigator.Stop();
            if (behaviourTree != null)
            {
                // 沉睡 NPC 的大脑对象是失活的：先激活再暂停，会议结束后才能恢复 Tick。
                behaviourTree.gameObject.SetActive(true);
                behaviourTree.SetPaused(true);
            }

            NPCVision vision = GetComponent<NPCVision>();
            if (vision != null) vision.enabled = false;
            NPCHearing hearing = GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = false;

            NPCAssassin assassin = GetComponent<NPCAssassin>();
            if (assassin != null) assassin.AbortFlow();

            CharacterController cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
        }

        /// <summary>把 NPC 放到会议椅上（CC 已禁用；root 是胶囊中心，y 由调用方给出）。</summary>
        public void PlaceAtMeetingSeat(Vector3 worldPosition, Quaternion worldRotation)
        {
            if (isDead) return;
            transform.SetPositionAndRotation(worldPosition, worldRotation);
            if (visual != null) visual.SetSitting(true);
            currentRoomName = null;
            if (blackboard != null) blackboard.isInRoom = false;
        }

        /// <summary>记录进入会议前的房间，作为发言时的「不在场证明」。</summary>
        public string CaptureMeetingAlibi()
        {
            string room = blackboard != null ? blackboard.currentRoomName : currentRoomName;
            if (string.IsNullOrEmpty(room)) room = "走廊";
            if (blackboard != null)
            {
                blackboard.meetingAlibiRoom = room;
                blackboard.meetingSpoke = false;
                blackboard.meetingAccusations = 0;
            }
            return room;
        }

        /// <summary>会议结束：恢复 CharacterController / 感知 / 行为树，回到普通搜证逻辑。</summary>
        public void ExitMeetingMode()
        {
            if (isDead || !InMeeting) return;

            InMeeting = false;
            if (visual != null) visual.SetSitting(false);
            CharacterController cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;

            NPCVision vision = GetComponent<NPCVision>();
            if (vision != null) vision.enabled = true;
            NPCHearing hearing = GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = true;

            if (behaviourTree != null)
            {
                behaviourTree.SetPaused(false);
                behaviourTree.Restart();
            }
            if (navigator != null)
            {
                navigator.enabled = true;
                navigator.Stop();
            }
        }

        // ------------------------------------------------------------------
        // 私聊（重构：交谈期间双方都停下）
        // ------------------------------------------------------------------

        public bool InPrivateChat { get; private set; }

        /// <summary>进入私聊：停下脚步 / 行为树 / 感知，但保留 CharacterController。</summary>
        public void EnterPrivateChatMode()
        {
            if (isDead || InMeeting || InPrivateChat) return;

            InPrivateChat = true;
            SetLeaningOut(false);
            if (navigator != null) navigator.Stop();
            if (behaviourTree != null) behaviourTree.SetPaused(true);

            // 1号可能正在执行杀9号流程：必须一起冻结，不能一边说话一边走动。
            NPCAssassin assassin = GetComponent<NPCAssassin>();
            if (assassin != null) assassin.PauseForPrivateChat();

            NPCVision vision = GetComponent<NPCVision>();
            if (vision != null) vision.enabled = false;
            NPCHearing hearing = GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = false;
        }

        /// <summary>结束私聊：恢复行为树 / 感知 / 寻路。</summary>
        public void ExitPrivateChatMode()
        {
            if (isDead || !InPrivateChat) return;

            InPrivateChat = false;

            NPCAssassin assassin = GetComponent<NPCAssassin>();
            if (assassin != null) assassin.ResumeAfterPrivateChat();

            NPCVision vision = GetComponent<NPCVision>();
            if (vision != null) vision.enabled = true;
            NPCHearing hearing = GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = true;

            if (behaviourTree != null)
            {
                behaviourTree.SetPaused(false);
                behaviourTree.Restart();
            }
            if (navigator != null)
            {
                navigator.enabled = true;
                navigator.Stop();
            }
        }

        // ------------------------------------------------------------------
        // 推搡 / 袭击 / 死亡（模块三 3.3）
        // ------------------------------------------------------------------

        /// <summary>设置「探头看向窗外」状态（行为树 LeanOutWindow / 1号流程第⑦步驱动）。</summary>
        public void SetLeaningOut(bool leaning)
        {
            if (isDead) return;
            IsLeaningOut = leaning;
        }

        /// <summary>
        /// 模块四：集体摘面具演出——隐藏面具 / 斗篷 / 号码，只留下人的轮廓。
        /// 不改变碰撞与死亡状态，纯视觉；由 EndingSystem 在结局 C / D 调用。
        /// </summary>
        public void UnmaskForEnding()
        {
            if (isDead) return;

            SetLeaningOut(false);

            // 正式模型：隐藏面具 / 号码 / 斗篷，露出里面的人形；
            // 占位模型（没有 NPCVisualController 时）沿用旧的子物体开关。
            if (visual != null)
            {
                visual.Unmask();
            }
            else
            {
                SetChildActive("Mask", false);
                SetChildActive("MaskNumber", false);
                SetChildActive("Cape", false);
            }

            if (blackboard != null)
            {
                blackboard.stress = 100f;
                blackboard.panicUntil = Mathf.Max(blackboard.panicUntil, Time.time + 30f);
            }
        }

        void SetChildActive(string childName, bool active)
        {
            Transform child = transform.Find(childName);
            if (child != null) child.gameObject.SetActive(active);
        }

        /// <summary>E 键提示：只描述友善 / 环境交互（私聊），绝不描述攻击。</summary>
        public string GetInteractionPrompt(PlayerController player)
        {
            if (isDead || InMeeting || !_awake || player == null) return "";

            // 搜证阶段的近距离私聊：无论玩家站在正面、背后还是侧面，E 都默认拉起私聊。
            if (PrivateChatSystem.CanChat(player, this))
                return "[E] 私下交谈";

            return "";
        }

        /// <summary>E 键：只做友善交互。手里拿着武器也不会在这里袭击。</summary>
        public void Interact(PlayerController player)
        {
            if (isDead || InMeeting || !_awake || player == null) return;

            if (PrivateChatSystem.CanChat(player, this) && PrivateChatSystem.Instance != null)
            {
                PrivateChatSystem.Instance.TryStartChat(this);
                return;
            }

            // 非搜证阶段 / 对话运行中：E 不产生任何敌意动作。
        }

        /// <summary>鼠标左键提示：袭击 / 推出窗外 / 空手轻推。熟睡者也可以被袭击。</summary>
        public string GetHostilePrompt(PlayerController player)
        {
            if (isDead || InMeeting || player == null) return "";

            // 1) 探头看窗外 + 同处一室 → 推出窗外（致死、不留尸体）
            if (CanPushOutWindow(player))
                return $"[左键] 将{Number}号推出窗外";

            // 2) 手持武器 → 袭击（匕首必杀 / 锤子 50%）。
            // 熟睡 / 背对时没有反抗，但仍然可以被目击；目击不再硬阻止玩家动手。
            ItemDefinition weapon = player.inventory != null ? player.inventory.GetLastWeapon() : null;
            if (weapon != null)
            {
                string target = _awake ? $"{Number}号" : $"熟睡的{Number}号";
                string witnessed = HasWitnesses(player) ? "（有人看着）" : "";
                return $"[左键] 用{weapon.displayName}袭击{target}{witnessed}";
            }

            // 3) 空手且位于背后 / 侧面 → 轻推（转身、打断 1 秒、怀疑 +15）
            if (_awake && IsBehindOrSide(player))
                return $"[左键] 轻推{Number}号";

            return "";
        }

        /// <summary>鼠标左键：恶意 / 物理交互。返回 true 表示点击已消费。</summary>
        public bool TryHostileInteract(PlayerController player)
        {
            if (isDead || InMeeting || player == null) return false;

            if (CanPushOutWindow(player))
            {
                PushOutWindow(player);
                return true;
            }

            ItemDefinition weapon = player.inventory != null ? player.inventory.GetLastWeapon() : null;
            if (weapon != null)
            {
                bool witnessed = HasWitnesses(player);
                if (witnessed && _awake)
                    player.ShowMessage($"{Number}号看见你抬手——但你已经没有退路。", 2.5f);
                player.inventory.UseWeaponOn(this);
                return true;
            }

            if (_awake && IsBehindOrSide(player))
            {
                player.ShowMessage($"你从背后推了{Number}号一把——{Number}号猛地转身，警惕地盯着你。");
                OnPushed(player.gameObject);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 窗外推下：对方探头看窗外、与玩家同处一室，且当前房间的窗户确实开着、没有反锁。
        /// （3.5 物理反馈：窗扇关着 / 锁着时不可能把人推出去。）
        /// </summary>
        bool CanPushOutWindow(PlayerController player)
        {
            if (!IsLeaningOut || isDead || player == null) return false;

            MonitorTracked self = GetComponent<MonitorTracked>();
            MonitorTracked other = player.GetComponentInParent<MonitorTracked>();
            if (self == null || other == null) return false;
            if (!self.SharesAnyRoom(other)) return false;

            WindowController window = GetCurrentRoomWindow();
            return window != null && window.IsOpen && !window.IsLatchLocked;
        }

        WindowController GetCurrentRoomWindow()
        {
            string roomName = currentRoomName;
            MonitorTracked tracked = GetComponent<MonitorTracked>();
            if (tracked != null && tracked.CurrentRoom != null)
                roomName = tracked.CurrentRoom.roomName;

            if (string.IsNullOrEmpty(roomName)) return null;

            string digits = roomName.Replace("号房", "").Trim();
            if (!int.TryParse(digits, out int number)) return null;

            GameObject room = GameObject.Find("Room_" + number);
            return room != null ? room.GetComponentInChildren<WindowController>(true) : null;
        }

        /// <summary>推出窗外：直接死亡，不留尸体（整个对象失活 → 红点消失）。</summary>
        void PushOutWindow(PlayerController player)
        {
            player.ShowMessage($"你趁{Number}号探头看窗外，一把将其推出了窗外。窗外只剩风雪声。");
            SoundEventManager.Emit(SoundEventType.Push, transform.position + Vector3.up, 0.8f, player.gameObject);

            // 玩家这一侧没有可见身体，推人只能靠第一人称的短促前冲来表现。
            player.PushLunge();

            // NPC 这一侧：在受击动作播放的同时真的被顶出窗洞，
            // 1.1 秒后由 NPCDeathHandler 在窗外切 Ragdoll 接上坠落。
            WindowController window = GetCurrentRoomWindow();
            if (window != null) StartCoroutine(PushThroughWindowRoutine(window));

            Die(false, "被推出窗外", player.gameObject);
        }

        /// <summary>
        /// 被推者侧的动作：从站立位置被顶到窗洞，再翻到窗外。
        /// 位移时间和 NPCDeathHandler 的 PushOutWindow 倒地延迟（1.1 秒）对齐，
        /// 保证切 Ragdoll 的瞬间人已经在窗外，而不是原地倒下。
        /// </summary>
        IEnumerator PushThroughWindowRoutine(WindowController window)
        {
            if (window == null) yield break;

            Vector3 outDir = window.transform.forward;
            outDir.y = 0f;
            if (outDir.sqrMagnitude < 0.0001f) outDir = transform.forward;
            outDir.Normalize();

            Vector3 windowCenter = window.transform.position;
            float sillY = windowCenter.y - 0.6f;   // 窗洞下沿（窗中心向下半个洞高）
            Vector3 start = transform.position;
            Quaternion startRot = transform.rotation;
            Quaternion faceOut = Quaternion.LookRotation(outDir, Vector3.up);
            Quaternion pressRot = faceOut * Quaternion.Euler(22f, 0f, 0f);
            Quaternion overRot = faceOut * Quaternion.Euler(72f, 0f, 0f);

            // 三段姿态：顶到窗前 → 整个人翻上窗台 → 头朝外栽出窗洞。
            // 根部是胶囊中心，站立时 y≈0.9；必须抬到窗洞中心附近身体才过得去，
            // 否则脚会从窗台下面的墙里穿过去（旧版就是这么穿模的）。
            Vector3 press = windowCenter - outDir * 0.30f;
            press.y = sillY + 0.42f;
            Vector3 overSill = windowCenter + outDir * 0.10f;
            overSill.y = sillY + 0.55f;
            Vector3 outside = windowCenter + outDir * 0.75f;
            outside.y = sillY + 0.38f;

            // 0.42s 顶到窗前（前倾 22°），0.26s 翻上窗台（前倾 72°），
            // 0.22s 头朝下栽出窗外（前倾 102°）。总时长 0.9s，
            // 与 NPCDeathHandler 的 1.1s 倒地延迟对齐，给 Ragdoll 留 0.2s 的下落。
            const float pressTime = 0.42f;
            const float overTime = 0.26f;
            const float fallTime = 0.22f;

            float elapsed = 0f;
            while (elapsed < pressTime)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / pressTime));
                transform.position = Vector3.Lerp(start, press, k);
                transform.rotation = Quaternion.Slerp(startRot, pressRot, k);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < overTime)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / overTime));
                transform.position = Vector3.Lerp(press, overSill, k);
                transform.rotation = Quaternion.Slerp(pressRot, overRot, k);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < fallTime)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / fallTime);
                Vector3 p = Vector3.Lerp(overSill, outside, k);
                p.y = Mathf.Lerp(overSill.y, outside.y, k * k); // 后段加速下坠
                transform.position = p;
                transform.rotation = Quaternion.Slerp(
                    overRot, faceOut * Quaternion.Euler(102f, 0f, 0f), k);
                yield return null;
            }

            transform.position = outside;
            transform.rotation = faceOut * Quaternion.Euler(102f, 0f, 0f);
        }

        /// <summary>玩家是否在 NPC 的背后或侧面（正面夹角 &lt; 70° 视为不满足）。</summary>
        bool IsBehindOrSide(PlayerController player)
        {
            Vector3 to = player.transform.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return true;
            return Vector3.Dot(transform.forward, to.normalized) < 0.34f;
        }

        /// <summary>是否有其他醒着的 NPC 看见玩家（独处判定：有目击者则不能下手）。</summary>
        bool HasWitnesses(PlayerController player)
        {
            if (player == null) return false;

            for (int i = 0; i < All.Count; i++)
            {
                NPCController other = All[i];
                if (other == null || other == this || other.isDead || !other.IsAwake) continue;
                if (Vector3.Distance(other.transform.position, player.transform.position) > 9f) continue;

                NPCVision vision = other.GetComponent<NPCVision>();
                if (vision == null || vision.CanSeeTarget(player.gameObject)) return true;
            }
            return false;
        }

        /// <summary>被推搡：转身面对推搡者、打断行为树 1 秒并重置到感知分支、情绪变化、广播事件。</summary>
        public void OnPushed(GameObject pusher)
        {
            if (isDead || InMeeting) return;

            // 1) 强制转身面对推搡者
            if (pusher != null)
            {
                Vector3 dir = pusher.transform.position - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            // 2) 被推后不再探头（转身面向推搡者）
            SetLeaningOut(false);

            // 3) 打断行为树：停止移动并暂停 Tick，1 秒后自动恢复；
            //    重置到根节点，恢复后优先进入「推搡对峙」感知分支。
            if (navigator != null) navigator.Stop();
            if (behaviourTree != null)
            {
                behaviourTree.PauseFor(1f);
                behaviourTree.Restart();
            }

            // 4) 情绪变化（3.3：suspicion +15 / stress +10）
            blackboard.suspicion = Mathf.Min(100f, blackboard.suspicion + 15f);
            blackboard.stress = Mathf.Min(100f, blackboard.stress + 10f);
            blackboard.lastPusher = pusher;
            blackboard.pushReactionUntil = Time.time + 5f;
            AddSuspect(PlayerNumber);

            // 5) 推搡声（附近 NPC 会听见）
            SoundEventManager.Emit(SoundEventType.Push, transform.position + Vector3.up, 0.9f, pusher);

            // 6) 广播
            NPCEventBus.RaiseNPCPushed(Number, pusher);

            Debug.Log($"[NPC {Number}] 被推搡（推搡者：{(pusher != null ? pusher.name : "?")}）。" +
                      $"转身对峙，Suspicion={blackboard.suspicion:0}，Stress={blackboard.stress:0}");
        }

        // ------------------------------------------------------------------
        // IKillable：玩家武器袭击
        // ------------------------------------------------------------------

        public void ApplyKill(PlayerController player, ItemDefinition weapon)
        {
            if (isDead) return;

            string weaponName = weapon != null ? weapon.displayName : "武器";
            if (player != null)
                player.ShowMessage($"你用{weaponName}击中了{Number}号——{Number}号倒下了。");
            Die(true, weaponName, player != null ? player.gameObject : null);
        }

        /// <summary>
        /// 剧情处决（1号 AI 的匕首 / 8号AI 的虚假认知）：留尸体，不算玩家袭击。
        /// 9号 / 10号死亡会通知 CycleState 让循环恢复计数。
        /// </summary>
        public void ExecuteByStoryKill(string cause)
        {
            if (isDead) return;
            Die(true, cause, null);
        }

        /// <summary>3.5 圆桌投票处决：不流血，身体化作光点消散，不留尸体。</summary>
        public void ExecuteByVote()
        {
            if (isDead) return;

            Color color = data != null ? data.maskNumberColor : new Color(0.85f, 0.82f, 0.72f);
            VoteExecutionEffect.Play(transform.position + Vector3.up * 0.9f, color);
            Die(false, "圆桌投票抹杀", null);
        }

        public void ApplyKillAttemptFailed(PlayerController player, ItemDefinition weapon)
        {
            if (isDead) return;

            string weaponName = weapon != null ? weapon.displayName : "武器";
            int playerNumber = PlayerNumber;

            blackboard.suspicion = 100f;
            blackboard.stress = 100f;
            AddSuspect(playerNumber);
            blackboard.wantsPlayerDead = true;
            blackboard.hostileSince = Time.time;
            if (player != null) FaceToward(player.transform.position);

            if (player != null)
                player.ShowMessage($"{weaponName}没有击中要害！{Number}号惊恐地尖叫起来——尖叫声传遍了整栋房子。");

            if (behaviourTree != null)
            {
                behaviourTree.PauseFor(1.5f);
                behaviourTree.Restart();
            }

            // 全图其他 NPC：怀疑度 +20（即使没听见尖叫也知道出事了）
            for (int i = 0; i < All.Count; i++)
            {
                NPCController other = All[i];
                if (other == null || other == this || other.isDead) continue;
                other.blackboard.suspicion = Mathf.Min(100f, other.blackboard.suspicion + 20f);
                other.blackboard.stress = Mathf.Min(100f, other.blackboard.stress + 10f);
                other.AddSuspect(playerNumber);
            }

            // 先抬高全场怀疑度，再发尖叫（听到的人会立即进入「前往声源」感知分支）
            SoundEventManager.Emit(SoundEventType.Scream, transform.position + Vector3.up, 1f,
                gameObject, Number);

            // 3.5：锤子失手 → 对方夺锤反击，玩家可能当场死亡（进入幽灵观战）。
            if (weapon != null && weapon.id == "hammer" && Random.value < 0.45f)
            {
                if (player != null)
                    player.ShowMessage($"{Number}号一把夺过锤子，反手砸中了你的头……", 3f);
                PlayerDeathSystem.Instance?.KillPlayer($"{Number}号夺锤反击");
            }

            Debug.Log($"[NPC {Number}] 被{weaponName}袭击但未致命：尖叫报警，全场 suspicion +20。");
        }

        /// <summary>玩家行凶被看见：目击者把玩家锁定为凶手，进入最高怀疑。</summary>
        void MarkWitnessesOfPlayerKill(Vector3 deathPosition)
        {
            int playerNumber = PlayerNumber;
            for (int i = 0; i < All.Count; i++)
            {
                NPCController other = All[i];
                if (other == null || other == this || other.isDead || !other.IsAwake) continue;

                bool sameRoom = !string.IsNullOrEmpty(currentRoomName)
                    && other.currentRoomName == currentRoomName;
                bool saw = sameRoom || other.HasLineOfSightTo(deathPosition);
                if (!saw) continue;

                other.blackboard.suspicion = 100f;
                other.blackboard.stress = Mathf.Min(100f, other.blackboard.stress + 20f);
                other.blackboard.AddSuspectFront(playerNumber);
                other.blackboard.AddTrust(playerNumber, -50f);

                // 同房间目击者不会再等会议：它会去拿武器，真的动手。
                // 隔着房间只看到尸体 / 血迹的人仍然走公聊与投票。
                if (sameRoom)
                {
                    other.blackboard.wantsPlayerDead = true;
                    other.blackboard.hostileSince = Time.time;
                }

                Debug.Log($"[NPC {other.Number}] 目击玩家（{playerNumber}号）杀死{Number}号：锁定凶手" +
                          $"{(sameRoom ? "，开始寻找武器" : "")}。");
            }
        }

        /// <summary>死亡：停止行为树 / 寻路 / 感知，按需留下尸体，广播 NPCDie，监控红点消失。</summary>
        void Die(bool leaveCorpse, string cause, GameObject killer)
        {
            if (isDead) return;

            bool wasAsleep = !_awake;
            SetLeaningOut(false); // 必须在 isDead 置位前执行（SetLeaningOut 会拒绝已死亡对象）
            isDead = true;

            // 1号 AI 可能死在“绳已铺出去、人还在窗外”的半途：收尸的同时把绳收掉，
            // 否则玩家进 1 号房会看到一截无主的绳挂在窗外。
            if (Number == 1 && RopeTraverseSystem.Instance != null)
                RopeTraverseSystem.Instance.RecallRoute(RopeState.Npc1Owner);

            // 全循环共享的死亡名册：公聊 / 投票 / 结局都必须知道这个人已经死了，
            // 不能因为 NPCController.All 移除就当作从未存在。
            CycleState.NotifyNpcDeath(Number);

            Vector3 deathPosition = transform.position;
            Quaternion deathRotation = transform.rotation;

            if (behaviourTree != null)
            {
                behaviourTree.SetPaused(true);
                behaviourTree.enabled = false;
            }
            if (navigator != null)
            {
                navigator.Stop();
                navigator.enabled = false;
            }
            NPCVision vision = GetComponent<NPCVision>();
            if (vision != null) vision.enabled = false;
            NPCHearing hearing = GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = false;
            MonitorTracked tracked = GetComponent<MonitorTracked>();
            if (tracked != null) tracked.enabled = false; // 红点立即消失（MonitorSystem 跳过失活组件）

            // 有正式模型时：受击动作 → 倒地帧切入 Ragdoll，尸体就是这具模型本身；
            // 只有在没有正式模型（旧占位体 / 特殊场景）时才生成临时尸体或显形占位体。
            bool useRagdoll = deathHandler != null;

            if (leaveCorpse && !useRagdoll)
            {
                // 9号在床上沉睡时被杀：直接让 Room_9 床上那个尸体占位体显形，
                // 比在床边重新生成一具通用尸体更贴合原本的案发现场。
                if (Number == 9 && wasAsleep && RevealNinthBedCorpse())
                {
                    Debug.Log("[NPC 9] 在睡梦中被杀：Room_9 床上的尸体占位体显形。");
                }
                else
                {
                    SpawnCorpse(deathPosition, deathRotation, cause);
                }
            }

            if (useRagdoll)
                deathHandler.PlayDeath(cause, leaveCorpse, killer, wasAsleep);

            // 玩家公开行凶：看见这一幕的醒着 NPC 会立刻把玩家锁定为凶手。
            // 这里不阻止玩家动手（高自由度），但目击必须留下后果。
            if (killer != null && killer.GetComponent<PlayerController>() != null)
                MarkWitnessesOfPlayerKill(deathPosition);

            NPCEventBus.RaiseNPCDie(Number, deathPosition, leaveCorpse);

            // 9号 / 10号是循环的“第九席”。9号 死亡即第九席结算；
            // 10号 只有在 9号 也已死亡时才代表第九席被补上——否则 9号 还活着，
            // 循环并没有恢复（杀掉 10号 不能掩盖 9号 逃过谋杀这一事实）。
            // 投票抹杀不算“1号杀9号”，交给投票 / 轮回系统处理。
            if ((Number == 9 || Number == 10)
                && CycleState.currentPhase != CyclePhase.Voting
                && CycleState.currentPhase != CyclePhase.Reset
                && !HasLivingNinth())
            {
                CycleState.NotifyKill9Executed();
            }

            Debug.Log($"[NPC {Number}] 死亡（{cause}）：{(leaveCorpse ? "尸体留在原地（含藏匿点）" : "没有留下尸体")}。");

            if (useRagdoll)
            {
                // 根节点必须保持激活：布娃娃要留在场景里继续模拟。
                // 只关闭本组件 —— OnDisable 会把 NPC 从 All 中移除，Update / 行为树不再运行。
                enabled = false;
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        /// <summary>场上是否还有活着的 9号（用于判断第九席是否真的结算）。</summary>
        static bool HasLivingNinth()
        {
            for (int i = 0; i < All.Count; i++)
            {
                NPCController npc = All[i];
                if (npc != null && !npc.isDead && npc.Number == 9) return true;
            }
            return false;
        }

        /// <summary>让 Room_9 床上隐藏的 9号尸体占位体显形；找不到时返回 false。</summary>
        static bool RevealNinthBedCorpse()
        {
            NinthVictim[] victims = FindObjectsOfType<NinthVictim>(true);
            for (int i = 0; i < victims.Length; i++)
            {
                if (victims[i] == null) continue;
                victims[i].RevealAsCorpse();
                return true;
            }
            return false;
        }

        /// <summary>在死亡位置生成尸体占位体（含 HidingSpot「身上」藏匿点 + 可检查 / 摘面具）。</summary>
        void SpawnCorpse(Vector3 deathPosition, Quaternion deathRotation, string cause)
        {
            GameObject corpse = new GameObject($"NPC_Corpse_{Number}");
            corpse.layer = MonitorSystem.MonitorIgnoreLayer;
            corpse.transform.position = new Vector3(deathPosition.x, deathPosition.y - 0.9f, deathPosition.z);
            corpse.transform.rotation = Quaternion.Euler(0f, deathRotation.eulerAngles.y, 0f);
            if (transform.parent != null) corpse.transform.SetParent(transform.parent, true);

            HidingSpot spot = corpse.AddComponent<HidingSpot>();
            spot.displayName = Number + "号（尸体）";
            spot.allowedLocation = HideLocation.OnBody;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.layer = MonitorSystem.MonitorIgnoreLayer;
            body.transform.SetParent(corpse.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.33f, 0f);
            body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            body.transform.localScale = new Vector3(0.65f, 0.9f, 0.65f);
            body.GetComponent<MeshRenderer>().sharedMaterial = CreateRuntimeMaterial(new Color(0.24f, 0.12f, 0.12f));

            Collider bodyCollider = body.GetComponent<Collider>();
            if (bodyCollider != null) bodyCollider.isTrigger = true; // 不阻挡通行，但仍可被交互射线命中

            // 尸体检查 / 摘面具放在躯干子物体上：
            // 躯干命中 → 检查尸体；头部命中 → 根节点的 HidingSpot（藏匿）。
            CorpseInspectable inspectable = body.AddComponent<CorpseInspectable>();
            inspectable.number = Number;
            inspectable.isNinthVictim = Number == 9;
            inspectable.subjectName = Number + "号的尸体";
            inspectable.cause = cause;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.layer = MonitorSystem.MonitorIgnoreLayer;
            head.transform.SetParent(corpse.transform, false);
            head.transform.localPosition = new Vector3(0f, 0.3f, 0.78f);
            head.transform.localScale = Vector3.one * 0.3f;
            head.GetComponent<MeshRenderer>().sharedMaterial = CreateRuntimeMaterial(new Color(0.45f, 0.36f, 0.30f));
            Collider headCollider = head.GetComponent<Collider>();
            if (headCollider != null) headCollider.isTrigger = true;
        }

        static Material CreateRuntimeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.color = color;
            return material;
        }

        void FaceToward(Vector3 worldPoint)
        {
            Vector3 dir = worldPoint - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir);
        }

        /// <summary>转身面向世界坐标（情绪反馈 / 会议凝视用）。</summary>
        public void FaceWorldPoint(Vector3 worldPoint)
        {
            if (isDead) return;
            FaceToward(worldPoint);
        }

        void AddSuspect(int suspectNumber)
        {
            if (blackboard.suspectNumbers == null) blackboard.suspectNumbers = new List<int>();
            if (!blackboard.suspectNumbers.Contains(suspectNumber))
                blackboard.suspectNumbers.Add(suspectNumber);
        }

        /// <summary>感知触发后让行为树立即从根重选分支（行为树暂停期间不打断）。</summary>
        public void RequestPerceptionResponse()
        {
            if (isDead || behaviourTree == null) return;
            if (behaviourTree.IsPaused) return;
            behaviourTree.Restart();
        }

        // ------------------------------------------------------------------
        // 房间进出（监控红点走 RoomArea → MonitorTracked 另一路；此处维护黑板状态）
        // ------------------------------------------------------------------

        void OnTriggerEnter(Collider other)
        {
            RoomArea area = other.GetComponent<RoomArea>();
            if (area == null) return;

            currentRoomName = area.roomName;
            blackboard.isInRoom = true;
            blackboard.currentRoomName = area.roomName;
            RoundKnowledge.NotifyNpcEnteredRoom(this, area);
            NPCEventBus.RaiseNPCEnterRoom(Number, area.roomName);
        }

        void OnTriggerExit(Collider other)
        {
            RoomArea area = other.GetComponent<RoomArea>();
            if (area == null) return;

            if (currentRoomName == area.roomName)
            {
                currentRoomName = null;
                blackboard.currentRoomName = null;
            }
            blackboard.isInRoom = false; // 简化：走廊里视为不在任何房间
            NPCEventBus.RaiseNPCExitRoom(Number, area.roomName);
        }

        // ------------------------------------------------------------------
        // 感知（订阅事件总线）
        // ------------------------------------------------------------------

        void HandleOthersPushed(int number, GameObject pusher)
        {
            if (number == Number || isDead) return; // 自己挨推由 OnPushed 直接处理

            // 附近 8 米内的推搡会让自己紧张（雏形感知）
            Vector3 source = pusher != null ? pusher.transform.position : transform.position;
            if (Vector3.Distance(transform.position, source) <= 8f)
                blackboard.stress = Mathf.Min(100f, blackboard.stress + 8f);
        }

        void HandleOthersDie(int number, Vector3 position, bool bodyLeft)
        {
            if (number == Number || isDead) return;
            if (Vector3.Distance(transform.position, position) > 10f) return;

            // 3.3：同房间或能直接看见同伴死亡 → 恐慌（行为树优先分支）
            string deathRoom = FindRoomNameAt(position);
            bool sameRoom = !string.IsNullOrEmpty(currentRoomName)
                && !string.IsNullOrEmpty(deathRoom) && deathRoom == currentRoomName;
            bool witnessed = sameRoom || HasLineOfSightTo(position);

            if (witnessed)
            {
                blackboard.panicUntil = Time.time + 7f;
                blackboard.panicSourcePosition = position;
                blackboard.stress = Mathf.Min(100f, blackboard.stress + 25f);
                blackboard.suspicion = Mathf.Min(100f, blackboard.suspicion + 10f);
                RequestPerceptionResponse();

                Debug.Log($"[NPC {Number}] 目击 {number} 号死亡，陷入恐慌（Stress={blackboard.stress:0}）。");
            }
            else
            {
                // 只是听到动静 / 知道附近出了事：压力上升但不恐慌
                blackboard.stress = Mathf.Min(100f, blackboard.stress + 10f);
            }
        }

        /// <summary>死亡位置所在房间名（通过房间触发区判定；找不到返回 null）。</summary>
        static string FindRoomNameAt(Vector3 position)
        {
            Collider[] hits = Physics.OverlapSphere(position, 0.3f, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                RoomArea area = hits[i].GetComponent<RoomArea>();
                if (area != null) return area.roomName;
            }
            return null;
        }

        bool HasLineOfSightTo(Vector3 worldPoint)
        {
            Vector3 eye = transform.position + Vector3.up * 0.45f;
            Vector3 to = worldPoint - eye;
            float distance = to.magnitude;
            if (distance < 0.01f) return true;

            int mask = ~(1 << MonitorSystem.MonitorIgnoreLayer);
            return !Physics.Raycast(eye, to / distance, distance, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>循环状态变化（模块三 3.2）：同步黑板，行为树将按新状态切换分支。</summary>
        void HandleCycleStateChanged(CyclePhase phase)
        {
            blackboard.cycleState = phase;
            Debug.Log($"[NPC {Number}] 感知到循环状态变化：{phase}（行为树将按新状态选择分支）。");
        }
    }
}
