using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 圆桌会议基础框架（模块三 3.4）。
    ///
    /// 流程：
    /// 1. GameManager 进入 Meeting → 停掉所有搜证行为（寻路 / 行为树 / 视觉 / 听觉 / 1号杀手流程）；
    /// 2. 黑场平滑位移：存活 NPC + 玩家按号码坐到 Chair_N，锁定玩家输入 3 秒；
    /// 3. 按 1→8 升序发言（跳过死亡 / 不存在的号码），镜头切到发言者；
    /// 4. NPC 台词由 Yarn 节点 Meeting_Speech 生成（黑板变量在每次发言前注入）；
    /// 5. 玩家回合显示 5 个选项（鼠标 / 数字键 1~5），选择后同样走 Yarn 节点；
    ///    第 5 项"号召所有人摘下面具"是结局 C 的破局入口；
    /// 6. 1~7 号的结构化敌意：除非已有极强怀疑对象，否则强制把玩家（8号）放到怀疑列表最前；
    /// 7. 玩家按 R → 打断：可以自由发言或出示证据；NPC 会把当前这段说完再让出话头；
    /// 8. 全部发言结束 → 进入 Voting（最终投票结算留给 3.5）。
    ///
    /// 本类只做流程编排；台词在 Assets/Yarn/Meeting.yarn，界面在 MeetingUI。
    /// </summary>
    public class MeetingSystem : MonoBehaviour
    {
        [Header("引用（场景构建器装配；留空自动查找）")]
        public PlayerController player;
        public MeetingUI ui;
        public DialogueBridge dialogue;
        public Camera meetingCamera;
        public Transform meetingArea;

        [Header("时序")]
        [Tooltip("黑场时长（就座前）")]
        public float fadeOutSeconds = 0.35f;
        [Tooltip("玩家输入锁定 / 入座等待（秒）")]
        public float seatingLockSeconds = 3f;
        [Tooltip("每位发言者之间的停顿")]
        public float betweenSpeechesSeconds = 0.65f;
        [Tooltip("玩家发言选择超时（秒），超时自动沉默")]
        public float playerOptionTimeout = 75f;

        [Header("本地大模型")]
        [Tooltip("模型就绪时，NPC 发言与玩家自由发言由 LLM 接管；否则走 Yarn")]
        public bool useLlmDialogue = true;
        [Tooltip("每场会议最多让几位 NPC 走 LLM，其余用 Yarn。本机一次 4B 推理约 17 秒，默认只给 1 位")]
        public int llmNpcTurnsPerMeeting = 1;
        [Tooltip("玩家自由发言等待模型的上限：超过直接回退，避免整张圆桌被卡住")]
        public float llmPlayerReplyTimeout = 3f;
        [Tooltip("证据面板最长停留秒数；超时自动关闭，避免回调丢失时整场会议卡死")]
        public float evidencePanelTimeout = 120f;

        public bool IsMeetingActive => _meetingActive;
        public int PlayerNumber => _playerNumber;
        public float PlayerSuspicion => _playerSuspicion;
        public IReadOnlyList<NPCController> Attendees => _attendees;

        bool LlmReady => useLlmDialogue
                         && LLMNpcBrain.Instance != null
                         && LLMNpcBrain.Instance.IsReady;

        readonly List<NPCController> _attendees = new List<NPCController>();
        readonly List<string> _evidenceIds = new List<string>();

        bool _meetingActive;
        int _playerNumber = 8;
        int _maxMeetingNumber = 8;
        float _playerSuspicion = 50f;
        bool _revealedTruth;
        Coroutine _meetingRoutine;

        NPCController _currentSpeaker;
        bool _currentSpeakerIsPlayer;
        bool _npcTurnActive;
        bool _playerOptionsVisible;
        bool _playerOptionsNeedRefresh;
        bool _witnessOptionAvailable;
        int _witnessTarget;
        string _witnessWhat = "";
        bool _skipCurrentSpeech;
        bool _evidenceResolving;
        bool _playerSeated;
        int _lastAccuserId;
        int _npcInterruptsThisMeeting;
        int _llmNpcTurnsUsed;
        bool _llmPresentedEvidenceThisTurn;
        int _lastLlmVoteTarget;
        readonly HashSet<int> _npcEvidencePresented = new HashSet<int>();
        readonly HashSet<string> _presentedEvidenceIds = new HashSet<string>();

        /// <summary>玩家发言意图：用枚举而不是选项下标，避免新增选项后编号错位。</summary>
        enum PlayerIntent
        {
            Alibi,
            Accuse,
            VoteLead,
            Rebuttal,
            Timeline,
            Demand,
            Silence,
            FreeSpeech,
            Witness,
            Unmask,
        }

        struct PlayerChoice
        {
            public PlayerIntent intent;
            public string label;
        }

        readonly List<PlayerChoice> _playerChoices = new List<PlayerChoice>();
        PlayerIntent _selectedIntent = PlayerIntent.Silence;

        /// <summary>打断 NPC 时玩家选的动作：等对方把话说完再执行。</summary>
        enum InterruptAction
        {
            None,
            FreeSpeech,
            Evidence,
        }

        InterruptAction _pendingInterrupt = InterruptAction.None;
        string _pendingEvidenceId;
        NPCController _pendingEvidenceAccuser;
        bool _interruptMenuOpen;

        Coroutine _gazeRoutine;
        NPCController _gazeNpc;
        Quaternion _gazeBaseRot;

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        void Awake()
        {
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (ui == null) ui = FindObjectOfType<MeetingUI>(true);
            if (dialogue == null) dialogue = FindObjectOfType<DialogueBridge>();
            if (meetingArea == null)
            {
                GameObject area = GameObject.Find("MeetingArea");
                if (area != null) meetingArea = area.transform;
            }
            EnsureMeetingCamera();
        }

        void Start()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnPhaseChanged += HandlePhaseChanged;
                if (GameManager.Instance.CurrentPhase == GamePhase.Meeting)
                    HandlePhaseChanged(GamePhase.Meeting);
            }
            else
            {
                Debug.LogWarning("[MeetingSystem] 场景中没有 GameManager，会议系统不会自动触发。");
            }
        }

        void OnDestroy()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPhaseChanged -= HandlePhaseChanged;
        }

        void Update()
        {
            if (!_meetingActive) return;

            // 自由打字期间 R 属于输入框，不能顺手打开证据面板 / 打断。
            if (NpcFreeTextInputUI.IsOpen) return;

            // 死人不能抢话：出局后 R 不再打开证据面板，只能看别人吵。
            if (!CycleState.playerAlive)
            {
                if (Input.GetKeyDown(KeyCode.R) && ui != null)
                    ui.SetHint("你已经出局——只能旁观他们投票。");
                return;
            }

            if (!Input.GetKeyDown(KeyCode.R)) return;
            if (_evidenceResolving) return;

            bool npcSpeaking = _npcTurnActive && !_currentSpeakerIsPlayer;
            // 以 UI 的实际可见状态为准：选项被点击的同一帧立即关闭，避免这一帧内
            // 又打开证据面板、与本回合后续 Yarn 台词争抢 DialogueRunner。
            bool playerChoosing = _currentSpeakerIsPlayer && _playerOptionsVisible
                && ui != null && ui.OptionsVisible;
            if (!npcSpeaking && !playerChoosing) return;

            if (npcSpeaking)
            {
                // 已经选好打断方式：等对方说完再执行，别反复弹面板。
                if (_interruptMenuOpen) return;
                if (_pendingInterrupt != InterruptAction.None)
                {
                    if (ui != null) ui.SetHint("他已经听见了——等他把话说完。");
                    return;
                }
                StartCoroutine(OpenInterruptMenu());
                return;
            }

            StartCoroutine(OpenEvidencePanel());
        }

        void HandlePhaseChanged(GamePhase phase)
        {
            if (phase == GamePhase.Meeting)
            {
                if (_meetingActive) return;
                _meetingRoutine = StartCoroutine(RunMeeting());
            }
            else
            {
                StopMeeting();
            }
        }

        // ------------------------------------------------------------------
        // 会议主流程
        // ------------------------------------------------------------------

        IEnumerator RunMeeting()
        {
            _meetingActive = true;
            _playerNumber = CycleState.playerNumber > 0 ? CycleState.playerNumber : 8;
            _playerSuspicion = 50f;
            _revealedTruth = false;
            _lastAccuserId = 0;
            _skipCurrentSpeech = false;
            _evidenceResolving = false;
            _pendingInterrupt = InterruptAction.None;
            _pendingEvidenceId = null;
            _pendingEvidenceAccuser = null;
            _interruptMenuOpen = false;
            _currentSpeaker = null;
            _currentSpeakerIsPlayer = false;
            _npcTurnActive = false;
            _playerOptionsVisible = false;
            _playerOptionsNeedRefresh = false;
            _npcInterruptsThisMeeting = 0;
            _llmNpcTurnsUsed = 0;
            _npcEvidencePresented.Clear();
            _presentedEvidenceIds.Clear();

            PrepareAttendees();
            BeginPlayerLock();

            // 玩家 HUD 的短消息在屏幕顶端居中，背包在左下角，都会压在会议 UI 上；
            // 会议期间整体收起探索 HUD，结束（StopMeeting）时恢复。
            if (player != null)
            {
                if (player.interaction != null) player.interaction.HideMessage();
                player.SetHudVisible(false);
            }

            if (ui != null)
            {
                ui.SetVisible(true);
                ui.SetFadeImmediate(1f);
                ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
                ui.SetSpeaker(string.Empty);
                ui.SetLine(string.Empty);
                ui.SetBanner("圆桌会议——所有人按号码入座");
            }

            yield return new WaitForSeconds(fadeOutSeconds);

            SeatEveryone();

            if (ui != null)
            {
                ui.FadeTo(0f, 0.5f);
                ui.SetBanner($"玩家 {_playerNumber} 号 · 发言顺序 1 → {_maxMeetingNumber}（跳过已死亡号码）");
                ui.SetLine(MissingSeatText() +
                           "\n\n墙上那排数字不是钟，是这一轮剩下的倒计时：" +
                           "1:30 是1号醒来时的读数，之后每过五分钟少五分钟，" +
                           "0:00 就是现在——会议开始。");
            }

            // 任务书要求：入座后锁定玩家输入 3 秒；空椅子先摆在他们面前。
            yield return new WaitForSeconds(seatingLockSeconds);

            if (ui != null) ui.SetLine(string.Empty);

            for (int number = 1; number <= _maxMeetingNumber; number++)
            {
                if (!_meetingActive || GameManager.Instance == null
                    || GameManager.Instance.CurrentPhase != GamePhase.Meeting)
                    break;

                while (_evidenceResolving) yield return null;

                if (number == _playerNumber && CycleState.playerAlive)
                {
                    // 最后一层保险：轮到玩家时至少留出 100 秒，避免刚开口就被切到投票。
                    EnsurePlayerTurnTime();
                    yield return PlayerTurn();
                }
                else
                {
                    NPCController npc = FindAttendee(number);
                    if (npc != null)
                        yield return NpcTurn(npc);
                }

                while (_evidenceResolving) yield return null;
                yield return new WaitForSeconds(betweenSpeechesSeconds);
            }

            if (!_meetingActive) yield break;

            if (ui != null)
            {
                ui.SetSpeaker(string.Empty);
                ui.SetLine(string.Empty);
                ui.SetHint(string.Empty);
                ui.SetBanner("发言结束——即将进入投票阶段");
            }

            yield return new WaitForSeconds(1.5f);

            if (_meetingActive && GameManager.Instance != null
                && GameManager.Instance.CurrentPhase == GamePhase.Meeting)
            {
                GameManager.Instance.SetPhase(GamePhase.Voting);
            }
        }

        /// <summary>轮到玩家发言时保证还有足够时间：玩家是会议的一部分，不是计时器的牺牲品。</summary>
        void EnsurePlayerTurnTime()
        {
            if (GameManager.Instance == null) return;
            const float minimum = 100f;
            if (GameManager.Instance.PhaseTimeRemaining < minimum)
                GameManager.Instance.ExtendCurrentPhase(
                    minimum - GameManager.Instance.PhaseTimeRemaining);
        }

        void PrepareAttendees()
        {
            _attendees.Clear();

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;

                npc.CaptureMeetingAlibi();
                npc.EnterMeetingMode();
                if (npc.blackboard != null) npc.blackboard.voteIntentNumber = 0;

                // 场景里同时存在私聊 / 结局的独立 DialogueRunner。NPC 在 Awake 里
                // 自动查找对话桥时可能抓到私聊桥，导致会议台词被私聊 Presenter 吞掉。
                // 会议开始时统一改绑到 MeetingDialogue，确保 9号 / 10号 的证词也走会议 UI。
                if (npc.dialogue != null) npc.dialogue.Bind(dialogue);

                _attendees.Add(npc);
            }

            _attendees.Sort((a, b) => a.Number.CompareTo(b.Number));

            PublishPublicVideo();

            // 发言顺序的最大号码：至少 1→8，9号 / 10号活着时扩展到 9 / 10。
            _maxMeetingNumber = 8;
            for (int i = 0; i < _attendees.Count; i++)
            {
                if (_attendees[i] != null)
                    _maxMeetingNumber = Mathf.Max(_maxMeetingNumber, _attendees[i].Number);
            }
            if (CycleState.playerAlive)
                _maxMeetingNumber = Mathf.Max(_maxMeetingNumber, _playerNumber);

            // 会议原本只有 180 秒，而玩家 8号 排在最后一个发言：
            // 时间一到 GameManager 直接切到投票，玩家根本没机会开口。
            // 按“每位发言者 35 秒 + 玩家回合 90 秒 + 入座 / 缓冲”预留，保证轮得到玩家。
            if (GameManager.Instance != null)
            {
                int speakerCount = _attendees.Count;
                if (CycleState.playerAlive && _playerNumber > 0) speakerCount++;
                float required = 30f + speakerCount * 35f + 90f;
                float deficit = required - GameManager.Instance.PhaseTimeRemaining;
                if (deficit > 0f)
                {
                    GameManager.Instance.ExtendCurrentPhase(deficit);
                    Debug.Log($"[MeetingSystem] 为保证 {speakerCount} 位发言者（含玩家）全部说完，" +
                              $"会议延长 {deficit:0} 秒。");
                }
            }

            Debug.Log($"[MeetingSystem] 会议开始：存活 NPC {_attendees.Count} 名，玩家 {_playerNumber} 号，" +
                      $"发言顺序 1 → {_maxMeetingNumber}。");
        }

        /// <summary>
        /// 原作规则：圆桌监控里那段回放是“必须公开”的线索。
        /// 不论玩家有没有去屏幕前按过 E，会议开始时都自动摆到桌上。
        /// 回放内容完全取自 SurveillanceLog：这一轮谁在几点出房、去没去大厅、几点回房，
        /// 不会再出现“1号 1:30 / 1:05”这种写死的剧本时间。
        /// </summary>
        void PublishPublicVideo()
        {
            string reason = SurveillanceLog.BuildReplayText();
            EvidenceLedger.Record("video_log", "圆桌监控回放", reason, 50);
            CycleState.NotifyVideoLogWatched();

            List<int> watchList = SurveillanceLog.BuildWatchList(2);
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.blackboard == null) continue;
                if (!npc.blackboard.knownClues.Contains("video_log"))
                    npc.blackboard.knownClues.Add("video_log");

                // 监控里出门次数多、在外面待得久的人，才会被多看一眼；
                // 没被拍到的人不会因为剧本预设而背锅。
                for (int j = 0; j < watchList.Count; j++)
                {
                    int number = watchList[j];
                    if (number == npc.Number) continue;
                    if (CycleState.IsNumberDead(number)) continue;
                    npc.blackboard.AddSuspect(number);
                    npc.blackboard.AddTrust(number, -2f);
                }
            }

            Debug.Log("[MeetingSystem] 监控回放已作为公开线索摆到圆桌上：" + reason);
        }

        void SeatEveryone()
        {
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                Transform chair = FindChair(npc.Number);
                if (chair == null)
                {
                    Debug.LogWarning($"[MeetingSystem] 找不到 Chair_{npc.Number}，{npc.Number} 号留在原地。");
                    continue;
                }

                Vector3 seat = chair.position + chair.forward * 0.05f;
                seat.y = chair.position.y + 0.9f;
                npc.PlaceAtMeetingSeat(seat, chair.rotation);
            }

            _playerSeated = false;
            if (player != null && CycleState.playerAlive)
            {
                Transform chair = FindChair(_playerNumber);
                if (chair != null)
                {
                    Vector3 seat = chair.position + chair.forward * 0.05f;
                    seat.y = chair.position.y + 0.05f;
                    player.PlaceAtMeetingSeat(seat, chair.rotation);
                    _playerSeated = true;

                    // 会议期间禁用 CharacterController：椅子坐面 / 靠背是实体碰撞，
                    // 保持启用会把玩家顶开。会议结束时再放回椅子后方并恢复。
                    CharacterController cc = player.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                }
            }

            MoveCameraImmediate(OverviewPosition(), OverviewLookAt());
        }

        void BeginPlayerLock()
        {
            if (player == null || !CycleState.playerAlive) return;
            player.inputLocked = true;
            player.movementLocked = true;
            player.SetCursorLocked(false);

            if (player.interaction != null)
            {
                player.interaction.enabled = false;
                if (player.interaction.promptText != null)
                    player.interaction.promptText.gameObject.SetActive(false);
            }
        }

        void EndPlayerLock()
        {
            if (player == null || !CycleState.playerAlive) return;

            // 从椅子里退到椅子后方，避免 CharacterController 恢复后卡在家具里
            if (_playerSeated)
            {
                Transform chair = FindChair(_playerNumber);
                if (chair != null)
                {
                    Vector3 stand = chair.position - chair.forward * 0.55f;
                    stand.y = chair.position.y + 0.05f;
                    player.PlaceAtMeetingSeat(stand, chair.rotation);
                }
                _playerSeated = false;
            }

            player.inputLocked = false;
            player.movementLocked = false;
            player.SetCursorLocked(true);

            if (player.interaction != null)
                player.interaction.enabled = true;
        }

        /// <summary>
        /// 结局中断会议：只停止流程 / 隐藏 HUD / 关闭会议相机，
        /// 不把 NPC 移出椅子、不解除玩家锁定——后续由 EndingSystem 统一冻结。
        /// </summary>
        public void HaltForEnding()
        {
            if (!_meetingActive) return;

            _meetingActive = false;
            if (_meetingRoutine != null)
            {
                StopCoroutine(_meetingRoutine);
                _meetingRoutine = null;
            }
            if (dialogue != null && dialogue.IsRunning) dialogue.StopDialogue();
            StopGaze();
            StopAllCoroutines();

            _currentSpeaker = null;
            _currentSpeakerIsPlayer = false;
            _npcTurnActive = false;
            _playerOptionsVisible = false;
            _evidenceResolving = false;

            if (meetingCamera != null) meetingCamera.enabled = false;
            if (ui != null)
            {
                ui.HideOptions();
                ui.HideEvidence();
                ui.SetVisible(false);
                ui.SetFadeImmediate(0f);
            }

            Debug.Log("[MeetingSystem] 会议因结局中断（NPC 保持在座位上，由结局系统接管）。");
        }

        void StopMeeting()
        {
            if (!_meetingActive) return;

            _meetingActive = false;
            if (_meetingRoutine != null)
            {
                StopCoroutine(_meetingRoutine);
                _meetingRoutine = null;
            }

            NpcFreeTextInputUI.Close(false);
            LLMNpcBrain.Instance?.Abort();
            if (dialogue != null && dialogue.IsRunning) dialogue.StopDialogue();
            StopGaze();
            StopAllCoroutines();

            _currentSpeaker = null;
            _currentSpeakerIsPlayer = false;
            _npcTurnActive = false;
            _playerOptionsVisible = false;
            _evidenceResolving = false;
            _pendingInterrupt = InterruptAction.None;
            _pendingEvidenceId = null;
            _pendingEvidenceAccuser = null;
            _interruptMenuOpen = false;

            if (player != null) player.SetHudVisible(true);

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.isDead) continue;

                // 先把 NPC 从椅子里挪到椅子后方，再恢复 CharacterController；
                // 否则启用碰撞的瞬间会与椅面 / 靠背重叠。
                Transform chair = FindChair(npc.Number);
                if (chair != null)
                {
                    Vector3 stand = chair.position - chair.forward * 0.55f;
                    stand.y = chair.position.y + 0.9f; // NPC 根节点 = 胶囊中心
                    npc.transform.SetPositionAndRotation(stand, chair.rotation);
                }

                npc.ExitMeetingMode();
            }
            _attendees.Clear();

            EndPlayerLock();

            if (meetingCamera != null) meetingCamera.enabled = false;
            if (ui != null)
            {
                ui.HideOptions();
                ui.HideEvidence();
                ui.SetVisible(false);
                ui.SetFadeImmediate(0f);
            }

            Debug.Log("[MeetingSystem] 会议结束，NPC 与玩家已恢复控制。");
        }

        // ------------------------------------------------------------------
        // NPC 回合
        // ------------------------------------------------------------------

        IEnumerator NpcTurn(NPCController npc)
        {
            _currentSpeaker = npc;
            _currentSpeakerIsPlayer = false;
            _npcTurnActive = true;
            _skipCurrentSpeech = false;
            npc.blackboard.meetingSpoke = true;

            int target = DetermineNpcTarget(npc);
            InjectNpcVariables(npc, target);

            if (ui != null)
            {
                float trust = npc.blackboard.GetTrust(_playerNumber);
                string tag = EmotionLabels.Trust(trust);
                if (npc.blackboard.stress >= 80f) tag += "　声音里夹着电流声";
                ui.SetSpeaker($"{npc.Number} 号　{tag}");
                ui.SetLine(string.Empty);
                ShowNpcSpeakingHint();
            }

            yield return MoveCameraToSpeaker(npc);

            // 高怀疑：发言时频繁转头盯着玩家，身体前倾、手指紧扣桌面。
            if (npc.blackboard.suspicion >= 70f && CycleState.playerAlive)
                StartGaze(npc);

            // 玩家可能在镜头移动期间按 R 打开证据面板：等它处理完再开始发言，
            // 避免证据对话与本 NPC 的 Yarn 节点争抢同一个 DialogueRunner。
            while (_evidenceResolving) yield return null;
            if (!_meetingActive)
            {
                StopGaze();
                yield break;
            }
            if (_skipCurrentSpeech)
            {
                // 证据在开口前就打断了他：本回合直接跳过
                _skipCurrentSpeech = false;
                _npcTurnActive = false;
                _currentSpeaker = null;
                StopGaze();
                if (ui != null)
                {
                    ui.SetSpeaker(string.Empty);
                    ui.SetLine(string.Empty);
                    ui.SetHint(string.Empty);
                }
                yield break;
            }

            bool spokeByLlm = false;
            _llmPresentedEvidenceThisTurn = false;
            _lastLlmVoteTarget = 0;

            if (LlmReady && _llmNpcTurnsUsed < Mathf.Max(0, llmNpcTurnsPerMeeting))
            {
                NpcLLMReply llmReply = null;
                NpcContext llmContext = NpcContextBuilder.BuildMeeting(npc, target, "", _playerSuspicion);
                if (ui != null) ui.SetHint("他正在组织语言……");

                bool llmFinished = false;
                StartCoroutine(LLMNpcBrain.Instance.Generate(llmContext,
                    r => { llmReply = r; llmFinished = true; }));

                // 不能直接 yield Generate：云端请求没有自己的超时，一旦挂住整场会议就停在这里。
                // 本地推理本身有 totalTimeout（默认 25 秒），这里统一再加一层看门狗。
                float llmWaitStart = Time.realtimeSinceStartup;
                float llmBudget = Mathf.Max(5f, LLMNpcBrain.Instance.totalTimeout + 5f);
                while (!llmFinished && _meetingActive
                       && Time.realtimeSinceStartup - llmWaitStart < llmBudget)
                    yield return null;

                if (!llmFinished)
                {
                    LLMNpcBrain.Instance.Abort();
                    llmReply = null;
                    Debug.LogWarning($"[MeetingSystem] {npc.Number} 号发言生成超过 {llmBudget:0} 秒，" +
                                     "作废本次结果，改用 Yarn 兜底。");
                }

                if (!_meetingActive)
                {
                    _npcTurnActive = false;
                    _currentSpeaker = null;
                    StopGaze();
                    yield break;
                }

                if (_skipCurrentSpeech)
                {
                    _skipCurrentSpeech = false;
                    _npcTurnActive = false;
                    _currentSpeaker = null;
                    StopGaze();
                    if (ui != null)
                    {
                        ui.SetSpeaker(string.Empty);
                        ui.SetLine(string.Empty);
                        ui.SetHint(string.Empty);
                    }
                    yield break;
                }

                if (llmReply != null && llmReply.success)
                {
                    _llmNpcTurnsUsed++;
                    spokeByLlm = true;
                    yield return PlayLlmMeetingSpeech(npc, llmReply);
                }
            }

            if (!spokeByLlm)
            {
                string node = "Meeting_Speech";
                bool started = npc.dialogue != null && npc.dialogue.TriggerMeetingSpeech(npc.Number, node);
                if (!started)
                {
                    if (ui != null) ui.SetLine(BuildFallbackSpeech(npc, target));
                    yield return new WaitForSeconds(3.4f);
                }
                else
                {
                    yield return WaitForDialogue();
                }
            }

            // 玩家可能在发言途中按 R 选好了打断方式：等他把这段说完再执行。
            while (_evidenceResolving) yield return null;

            int effectTarget = _lastLlmVoteTarget > 0 ? _lastLlmVoteTarget : target;
            if (_meetingActive && !_skipCurrentSpeech)
            {
                // 他说出口的话仍然算数，只是不再追加举证 / 抢话，把下一句留给玩家。
                ApplyNpcSpeechEffects(npc, effectTarget);
                if (_pendingInterrupt == InterruptAction.None && !_llmPresentedEvidenceThisTurn)
                    yield return MaybeNpcPresentEvidence(npc);
                if (_pendingInterrupt == InterruptAction.None)
                    yield return MaybeNpcInterrupt(npc, effectTarget);
            }

            // 如果玩家在追加环节才按下 R，也要等他把最后一句说完。
            while (_evidenceResolving) yield return null;
            if (_meetingActive && _pendingInterrupt != InterruptAction.None)
                yield return ExecutePendingInterrupt(npc);

            _skipCurrentSpeech = false;
            _pendingInterrupt = InterruptAction.None;
            _pendingEvidenceId = null;
            _pendingEvidenceAccuser = null;
            _npcTurnActive = false;
            StopGaze();
            if (ui != null) ui.SetHint(string.Empty);

            Debug.Log($"[MeetingSystem] {npc.Number} 号发言结束：指控 {target} 号；" +
                      $"（信任表已更新，玩家怀疑值 {_playerSuspicion:0}）");
        }

        /// <summary>播放 LLM 生成的会议发言，并把工具调用落到黑板 / 投票 / 动作。</summary>
        IEnumerator PlayLlmMeetingSpeech(NPCController npc, NpcLLMReply reply)
        {
            string clean = NpcToolCallParser.SanitizeSpeech(reply.speech);
            clean = NpcConversationMemory.StripOutOfWorld(clean);
            clean = NpcConversationMemory.StripSelfEcho(npc.Number, NpcLLMScene.Meeting, clean);
            if (NpcConversationMemory.IsSilent(clean))
                clean = NpcConversationMemory.FallbackLine(npc.Number, NpcLLMScene.Meeting);

            if (ui != null)
            {
                ui.SetLine($"{npc.Number}号：{clean}");
                ShowNpcSpeakingHint();
            }
            NpcConversationMemory.AddMeeting($"{npc.Number}号：{clean}");
            NpcVoiceRouter.Speak(npc, clean);

            NpcToolExecutionResult result =
                NpcToolExecutor.Execute(npc, reply, NpcLLMScene.Meeting);
            if (result.presentedEvidence) _llmPresentedEvidenceThisTurn = true;
            if (result.voteTarget > 0) _lastLlmVoteTarget = result.voteTarget;

            float wait = Mathf.Clamp(1.3f + clean.Length * 0.05f, 2.0f, 4.8f);
            yield return new WaitForSecondsRealtime(wait);
            if (!_meetingActive) yield break;

            if (!string.IsNullOrEmpty(result.extraLine))
            {
                if (ui != null) ui.SetLine($"{npc.Number}号：{result.extraLine}");
                NpcConversationMemory.AddMeeting($"{npc.Number}号：{result.extraLine}");
                NpcVoiceRouter.Speak(npc, result.extraLine);
                yield return new WaitForSecondsRealtime(2.4f);
            }

            if (!_meetingActive) yield break;
            if (result.voteTarget > 0 && ui != null)
                ui.SetHint($"他把票意指向了 {result.voteTarget} 号。");
        }

        /// <summary>高怀疑 NPC 的会议凝视：反复转头看玩家，前倾并轻微颤动。</summary>
        void StartGaze(NPCController npc)
        {
            StopGaze();
            _gazeNpc = npc;
            _gazeBaseRot = npc.transform.rotation;
            _gazeRoutine = StartCoroutine(MeetingGazeAtPlayer(npc));
        }

        void StopGaze()
        {
            if (_gazeRoutine != null) StopCoroutine(_gazeRoutine);
            _gazeRoutine = null;
            if (_gazeNpc != null) _gazeNpc.transform.rotation = _gazeBaseRot;
            _gazeNpc = null;
        }

        IEnumerator MeetingGazeAtPlayer(NPCController npc)
        {
            float endTime = Time.time + 25f;
            while (_meetingActive && _npcTurnActive && _currentSpeaker == npc && Time.time < endTime)
            {
                yield return new WaitForSeconds(Random.Range(0.8f, 1.5f));
                if (!_meetingActive || !_npcTurnActive || _currentSpeaker != npc || player == null)
                    yield break;

                Vector3 dir = player.transform.position - npc.transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) continue;
                Quaternion facing = Quaternion.LookRotation(dir);

                float t = 0f;
                while (t < 0.5f)
                {
                    t += Time.deltaTime;
                    float lean = 7f * Mathf.Clamp01(t / 0.3f);
                    float tremor = Mathf.Sin(Time.time * 28f) * 0.35f;
                    npc.transform.rotation = facing * Quaternion.Euler(lean + tremor, 0f, 0f);
                    yield return null;
                }

                yield return new WaitForSeconds(Random.Range(0.5f, 0.9f));
                npc.transform.rotation = _gazeBaseRot;
            }
        }

        /// <summary>
        /// 结构化敌意：1~7 号的虚假认知中 8 号（玩家）是必杀目标。
        /// 除非该 NPC 已有极强的其他怀疑对象（suspicion ≥ 85 且首选不是玩家），
        /// 否则强制把玩家放到 suspectNumbers 最前，并作为本轮指控对象。
        /// </summary>
        int DetermineNpcTarget(NPCController npc)
        {
            NPCBlackboard b = npc.blackboard;
            int first = FirstLivingSuspect(b);

            // 9号 / 10号是被循环多出来的人：他们记得自己本该是尸体，
            // 指控对象天然指向“负责杀 9号”的 1号（若玩家就是 1号，则直接指控玩家）。
            if (npc.Number == 9 || npc.Number == 10)
            {
                int target = _playerNumber == 1 && CycleState.playerAlive ? _playerNumber : 0;
                if (target == 0 && FindAttendee(1) != null) target = 1;

                b.meetingAccusedNumber = target;
                if (target > 0)
                {
                    b.AddSuspectFront(target);
                    if (target == _playerNumber) _lastAccuserId = npc.Number;
                }
                return target;
            }

            // 1号：自己知道做了什么，会议上的首要目标是转移视线。
            // 他会把矛头指向玩家或任何一个已经被别人怀疑的人，而不是主动提 9号房。
            if (npc.Number == 1)
            {
                int target = 0;
                if (CycleState.playerAlive && b.GetTrust(_playerNumber) <= 55f)
                    target = _playerNumber;
                else if (first > 0)
                    target = first;
                else if (CycleState.playerAlive)
                    target = _playerNumber;

                b.meetingAccusedNumber = target;
                if (target > 0)
                {
                    b.AddSuspectFront(target);
                    if (target == _playerNumber) _lastAccuserId = npc.Number;
                }
                return target;
            }

            // 2~8号：先看谁真正可疑；玩家只有在自己的行为引起怀疑时才会被指认，
            // 不再无条件把玩家推到怀疑列表最前。
            if (npc.Number >= 2 && npc.Number <= 8)
            {
                // 玩家带票成功后，愿意跟票的人会真的把矛头转向那个目标。
                if (b.voteIntentNumber > 0 && b.voteIntentNumber != npc.Number
                    && b.voteIntentNumber != _playerNumber
                    && IsAliveParticipant(b.voteIntentNumber)
                    && b.GetTrust(_playerNumber) >= 50f)
                {
                    b.meetingAccusedNumber = b.voteIntentNumber;
                    return b.voteIntentNumber;
                }

                bool playerSuspicious = CycleState.playerAlive
                    && _playerNumber != npc.Number
                    && (b.suspicion >= 65f
                        || b.GetTrust(_playerNumber) <= 35f
                        || (b.suspectNumbers != null && b.suspectNumbers.Contains(_playerNumber)
                            && b.GetTrust(_playerNumber) <= 48f));

                if (playerSuspicious)
                {
                    b.AddSuspectFront(_playerNumber);
                    b.meetingAccusedNumber = _playerNumber;
                    _lastAccuserId = npc.Number;
                    return _playerNumber;
                }

                if (first > 0)
                {
                    b.meetingAccusedNumber = first;
                    if (first == _playerNumber) _lastAccuserId = npc.Number;
                    return first;
                }
            }

            if (first > 0)
            {
                b.meetingAccusedNumber = first;
                if (first == _playerNumber) _lastAccuserId = npc.Number;
                return first;
            }

            b.meetingAccusedNumber = 0;
            return 0;
        }

        /// <summary>指控对象只能落在还坐在桌旁的人身上，死者的号码不能再被拉起来背锅。</summary>
        bool IsAliveParticipant(int number)
        {
            if (number <= 0) return false;
            if (number == _playerNumber) return CycleState.playerAlive;
            return FindAttendee(number) != null;
        }

        int FirstLivingSuspect(NPCBlackboard b)
        {
            if (b == null || b.suspectNumbers == null) return 0;
            for (int i = 0; i < b.suspectNumbers.Count; i++)
            {
                int number = b.suspectNumbers[i];
                if (IsAliveParticipant(number)) return number;
            }
            return 0;
        }

        void ApplyNpcSpeechEffects(NPCController speaker, int target)
        {
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener == speaker) continue;

                float trustInSpeaker = listener.blackboard.GetTrust(speaker.Number);
                if (trustInSpeaker >= 55f)
                {
                    // 信任的发言者：信任上升，附和指控
                    listener.blackboard.AddTrust(speaker.Number, 2f);
                    listener.blackboard.suspicion = Mathf.Min(100f, listener.blackboard.suspicion + 2f);
                    listener.blackboard.RemoveSuspect(speaker.Number);
                    if (target > 0) listener.blackboard.AddSuspect(target);
                }
                else if (trustInSpeaker <= 45f)
                {
                    // 被怀疑的发言者：起哄，降低信任
                    listener.blackboard.AddTrust(speaker.Number, -3f);
                    listener.blackboard.suspicion = Mathf.Min(100f, listener.blackboard.suspicion + 3f);
                    listener.blackboard.AddSuspect(speaker.Number);
                }
                else
                {
                    listener.blackboard.suspicion = Mathf.Min(100f, listener.blackboard.suspicion + 1f);
                    if (target > 0) listener.blackboard.AddSuspect(target);
                }

                if (target == listener.Number)
                {
                    listener.blackboard.suspicion = Mathf.Min(100f, listener.blackboard.suspicion + 12f);
                    listener.blackboard.AddTrust(speaker.Number, -8f);
                    listener.blackboard.AddSuspectFront(speaker.Number);
                    listener.blackboard.meetingAccusations++;
                }
            }

            // 被指控的是玩家：全场对玩家的怀疑值只加一次（不随在场人数叠加）。
            // 7 位 NPC 依次指认时每次 +5：玩家回合前约 85，仍有空间靠证据 / 发言挽回。
            if (target == _playerNumber)
                _playerSuspicion = Mathf.Min(100f, _playerSuspicion + 5f);

            if (ui != null) ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
        }

        // ------------------------------------------------------------------
        // NPC 主动出示证据 / 打断
        // ------------------------------------------------------------------

        struct NpcEvidenceMoment
        {
            public string id;
            public string name;
            public string text;
            public int weight;
        }

        /// <summary>
        /// NPC 在公聊里主动把已知线索摆上桌。最多三位 NPC 各出示一次，
        /// 只有真正见过 / 搜到过的东西才会说出口，不凭空编造证词。
        /// </summary>
        IEnumerator MaybeNpcPresentEvidence(NPCController npc)
        {
            if (!_meetingActive || npc == null || npc.blackboard == null) yield break;
            if (_npcEvidencePresented.Count >= 3) yield break;
            if (_npcEvidencePresented.Contains(npc.Number)) yield break;

            NpcEvidenceMoment moment;
            if (!TryBuildNpcEvidence(npc, out moment)) yield break;

            _npcEvidencePresented.Add(npc.Number);

            if (ui != null)
            {
                ui.SetSpeaker($"{npc.Number} 号");
                ui.SetLine($"{npc.Number}号：等一下。有件事必须现在就摆到桌上——{moment.name}。");
                ShowNpcSpeakingHint();
            }
            yield return new WaitForSeconds(2.2f);

            if (!_meetingActive) yield break;

            if (ui != null) ui.SetLine($"{npc.Number}号：{moment.text}");
            yield return new WaitForSeconds(3.4f);

            ApplyNpcEvidenceEffects(npc, moment);
        }

        bool TryBuildNpcEvidence(NPCController npc, out NpcEvidenceMoment moment)
        {
            NPCBlackboard b = npc.blackboard;
            moment = default;

            if (b.knowsFacesIdentical && !_presentedEvidenceIds.Contains("faces_identical"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "faces_identical",
                    name = "两张一模一样的脸",
                    weight = 65,
                    text = "我看见了9号面具底下的脸。那张脸和另一个摘下面具的人，一模一样——同样的眉骨、同样的下颌线。" +
                           "我没见过自己的脸，可我现在开始怀疑，我们面具底下根本不是不同的人。",
                };
                return true;
            }

            if ((b.sawNinthMaskOff || b.knownClues.Contains("ninth_face"))
                && !_presentedEvidenceIds.Contains("ninth_face"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "ninth_face",
                    name = "9号面具下的脸",
                    weight = 50,
                    text = "9号的面具被摘下来过。那张脸没有编号，也没有名字。我没见过自己的脸，" +
                           "可那张脸让我觉得熟悉——熟悉得不对。",
                };
                return true;
            }

            if (b.knownClues.Contains("room_items_identical")
                && !_presentedEvidenceIds.Contains("room_items_identical"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "room_items_identical",
                    name = "每个房间都是同一套工具",
                    weight = 45,
                    text = "我搜过别的房间。每张桌子上都是同一套东西：长绳、锤子、匕首、磁铁。" +
                           "连匕首上干涸的血迹都一模一样。这不是巧合，是有人把同一套东西摆进了每一间房。",
                };
                return true;
            }

            if (b.knownClues.Contains("ninth_room_items")
                && !_presentedEvidenceIds.Contains("ninth_room_items"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "ninth_room_items",
                    name = "9号房间的磁铁",
                    weight = 40,
                    text = "9号的桌上只有一把匕首和一块磁铁，窗台上还有另一块磁铁，混在石子里。" +
                           "两块磁铁隔着玻璃能互相吸住——如果有人站在窗外，他不需要打开窗就能把锁销推死。",
                };
                return true;
            }

            if (b.knownClues.Contains("video_log")
                && !_presentedEvidenceIds.Contains("video_log"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "video_log",
                    name = "圆桌监控回放",
                    weight = 50,
                    text = SurveillanceLog.BuildEvidenceMomentText(),
                };
                return true;
            }

            if ((b.sawNinthBody || b.knownClues.Contains("ninth_body"))
                && !_presentedEvidenceIds.Contains("ninth_body"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "ninth_body",
                    name = "9号的尸体",
                    weight = 30,
                    text = "9号躺在床上，门从里面闩着。我隔着窗户看见他的时候，房间里没有别人。" +
                           "可一个从里面闩死的房间，不会自己变成尸体。",
                };
                return true;
            }

            for (int i = 0; i < b.knownClues.Count; i++)
            {
                string clue = b.knownClues[i];
                if (!clue.StartsWith("body_")) continue;
                if (_presentedEvidenceIds.Contains(clue)) continue;
                int number;
                if (!int.TryParse(clue.Substring(5), out number)) continue;

                moment = new NpcEvidenceMoment
                {
                    id = clue,
                    name = $"{number}号的尸体",
                    weight = 25,
                    text = $"{number}号死在自己的房间里。面具和斗篷都还在，看不见脸。" +
                           "如果连尸体都不让看脸，那这些面具到底是在遮谁？",
                };
                return true;
            }

            // 每个人醒来时都看见自己桌上的匕首有干涸血迹——这条不需要额外搜证。
            if (!_presentedEvidenceIds.Contains("dagger_blood"))
            {
                moment = new NpcEvidenceMoment
                {
                    id = "dagger_blood",
                    name = "刀柄上的干涸血迹",
                    weight = 20,
                    text = "我醒来的时候，桌上的匕首上就有干涸的血迹。不是我的血。" +
                           "如果每个人的匕首都是这样——那这场游戏在我们睁眼之前就已经开始了。",
                };
                return true;
            }

            return false;
        }

        void ApplyNpcEvidenceEffects(NPCController presenter, NpcEvidenceMoment moment)
        {
            if (presenter == null || presenter.blackboard == null) return;

            _presentedEvidenceIds.Add(moment.id);

            // 标记“这条公开信息已经说出口”，避免第二个 NPC 再把它当成新发现。
            if (moment.id == "dagger_blood") _npcEvidencePresented.Add(-1);

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener == presenter || listener.blackboard == null) continue;

                if (moment.id == "faces_identical")
                {
                    listener.blackboard.knowsFacesIdentical = true;
                    if (!listener.blackboard.knownClues.Contains("faces_identical"))
                        listener.blackboard.knownClues.Add("faces_identical");
                    listener.blackboard.stress = Mathf.Min(100f, listener.blackboard.stress + 18f);
                }
                else if (moment.id == "ninth_face")
                {
                    listener.blackboard.sawNinthMaskOff = true;
                    if (!listener.blackboard.knownClues.Contains("ninth_face"))
                        listener.blackboard.knownClues.Add("ninth_face");
                    listener.blackboard.stress = Mathf.Min(100f, listener.blackboard.stress + 10f);
                }
                else if (moment.id == "room_items_identical")
                {
                    if (!listener.blackboard.knownClues.Contains("room_items_identical"))
                        listener.blackboard.knownClues.Add("room_items_identical");
                    listener.blackboard.stress = Mathf.Min(100f, listener.blackboard.stress + 10f);
                }
                else if (moment.id == "video_log" && !CycleState.IsNumberDead(1))
                {
                    listener.blackboard.AddSuspect(1);
                    listener.blackboard.AddTrust(1, -6f);
                }
                else if (moment.id == "ninth_body" || moment.id.StartsWith("body_"))
                {
                    listener.blackboard.stress = Mathf.Min(100f, listener.blackboard.stress + 6f);
                }
            }

            if (moment.id == "faces_identical")
            {
                // 这张脸一旦被摆到桌上，所有人都听见了；这是顿悟链条的关键一步。
                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController listener = _attendees[i];
                    if (listener != null && listener.blackboard != null)
                        listener.blackboard.knowsFacesIdentical = true;
                }
                presenter.blackboard.stress = Mathf.Min(100f, presenter.blackboard.stress + 10f);
            }

            Debug.Log($"[MeetingSystem] {presenter.Number} 号在公聊中出示了：{moment.name}。");
        }

        /// <summary>
        /// 公聊打断：不是随机起哄，而是有人抓住了发言者的具体矛盾。
        /// 最多两次，避免会议变成吵架。
        /// </summary>
        IEnumerator MaybeNpcInterrupt(NPCController speaker, int target)
        {
            if (!_meetingActive || speaker == null) yield break;
            if (_npcInterruptsThisMeeting >= 2) yield break;

            NPCController interrupter = null;
            string reason = null;

            // 1) 发言者在监控里被拍到出过门：知道视频的人会当面核对他的时间线。
            // 不再固定只针对 1号，也不会在监控没拍到时凭空质问。
            if (SurveillanceLog.OutingCount(speaker.Number) > 0 && !CycleState.IsNumberDead(speaker.Number))
            {
                float bestSuspicion = 45f;
                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController other = _attendees[i];
                    if (other == null || other == speaker || other.Number == _playerNumber) continue;
                    if (other.blackboard == null) continue;
                    if (!other.blackboard.knownClues.Contains("video_log")) continue;
                    if (other.blackboard.suspicion <= bestSuspicion) continue;
                    bestSuspicion = other.blackboard.suspicion;
                    interrupter = other;
                }
                if (interrupter != null) reason = "video";
            }

            // 2) 有人把矛头指向玩家，而另一位 NPC 对玩家足够信任：会替玩家挡一句。
            if (interrupter == null && target == _playerNumber && CycleState.playerAlive)
            {
                float bestTrust = 52f;
                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController other = _attendees[i];
                    if (other == null || other == speaker || other.Number == _playerNumber) continue;
                    if (other.blackboard == null) continue;
                    float trust = other.blackboard.GetTrust(_playerNumber);
                    if (trust <= bestTrust) continue;
                    bestTrust = trust;
                    interrupter = other;
                }
                if (interrupter != null) reason = "defend";
            }

            // 3) 发言者本身不被信任：有人直接质疑他的证词。
            if (interrupter == null && Random.value < 0.5f)
            {
                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController other = _attendees[i];
                    if (other == null || other == speaker || other.Number == _playerNumber) continue;
                    if (other.blackboard == null) continue;
                    if (other.blackboard.GetTrust(speaker.Number) > 48f) continue;
                    if (other.blackboard.suspicion < 48f) continue;
                    interrupter = other;
                    reason = "question";
                    break;
                }
            }

            if (interrupter == null || string.IsNullOrEmpty(reason)) yield break;

            _npcInterruptsThisMeeting++;

            string line;
            switch (reason)
            {
                case "video":
                    line = $"{interrupter.Number}号：等一下，{speaker.Number}号。监控里，" +
                           $"{SurveillanceLog.DescribeOutingsFor(speaker.Number)}。" +
                           "你说的和画面里对不上——把这段路重新说一遍，从门开到门关。";
                    break;
                case "defend":
                    line = $"{interrupter.Number}号：你从坐下开始就在把话题往{_playerNumber}号身上推。" +
                           "可监控拍到的出门记录里，不止他一个。你凭什么觉得只有他的出门算数？";
                    break;
                default:
                    // 不能只说“你的发言有问题”——必须把矛盾点具体指出来：
                    // 几点醒、去了哪、谁能作证。
                    string speakerOut = MeetingOutTime(speaker.Number);
                    string speakerAlibi = RoomPhrase(speaker.blackboard != null
                        && !string.IsNullOrEmpty(speaker.blackboard.meetingAlibiRoom)
                        ? speaker.blackboard.meetingAlibiRoom
                        : "走廊");
                    line = $"{interrupter.Number}号：{speaker.Number}号，你说你{speakerOut}醒的，" +
                           $"出门只去了{speakerAlibi}。可监控只拍走廊，{speakerAlibi}里没有人能替你作证。" +
                           "你先把这段路重新说一遍——从门开到门关，用了多久？";
                    break;
            }

            if (ui != null)
            {
                ui.SetSpeaker($"{interrupter.Number} 号　（打断）");
                ui.SetLine(line);
                ShowNpcSpeakingHint();
            }
            yield return new WaitForSeconds(3.6f);

            if (!_meetingActive) yield break;

            if (reason == "video")
            {
                interrupter.blackboard.AddSuspectFront(1);
                interrupter.blackboard.AddTrust(1, -10f);
                speaker.blackboard.stress = Mathf.Min(100f, speaker.blackboard.stress + 12f);
            }
            else if (reason == "defend")
            {
                interrupter.blackboard.AddTrust(_playerNumber, 3f);
            }
            else
            {
                interrupter.blackboard.AddTrust(speaker.Number, -4f);
                speaker.blackboard.stress = Mathf.Min(100f, speaker.blackboard.stress + 6f);
            }

            Debug.Log($"[MeetingSystem] {interrupter.Number} 号打断了 {speaker.Number} 号（{reason}）。");
        }

        // ------------------------------------------------------------------
        // 玩家回合
        // ------------------------------------------------------------------

        IEnumerator PlayerTurn()
        {
            _currentSpeaker = null;
            _currentSpeakerIsPlayer = true;
            _npcTurnActive = false;
            _playerOptionsVisible = false;
            _playerOptionsNeedRefresh = false;
            _selectedIntent = PlayerIntent.Silence;

            BuildPlayerChoices();

            if (ui != null)
            {
                ui.SetSpeaker($"{_playerNumber} 号（你）");
                ui.SetLine(string.Empty);
                ui.SetHint(BuildPlayerTurnHint());
            }

            yield return MoveCameraToOverview();

            int selectedIndex = -1;
            int accuseTarget = 0;
            bool accuseWithEvidence = false;

            while (selectedIndex < 0 && _meetingActive)
            {
                selectedIndex = -1;
                _playerOptionsVisible = true;
                _playerOptionsNeedRefresh = false;
                ShowPlayerOptions(selected => selectedIndex = selected);

                float timer = 0f;
                while (selectedIndex < 0 && timer < playerOptionTimeout && _meetingActive)
                {
                    // 玩家回合中出示证据可能揭穿真相：重建选项，追加“号召摘面具”。
                    if (_playerOptionsNeedRefresh && !_evidenceResolving
                        && ui != null && _playerOptionsVisible)
                    {
                        _playerOptionsNeedRefresh = false;
                        ui.HideOptions();
                        _playerOptionsVisible = true;
                        ShowPlayerOptions(selected => selectedIndex = selected);
                    }

                    if (!_evidenceResolving) timer += Time.deltaTime;
                    yield return null;
                }

                if (ui != null) ui.HideOptions();
                _playerOptionsVisible = false;
                if (!_meetingActive) yield break;

                if (selectedIndex < 0)
                    selectedIndex = Mathf.Max(0, FindChoiceIndex(PlayerIntent.Silence));
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, _playerChoices.Count - 1));
                _selectedIntent = _playerChoices[selectedIndex].intent;

                // 自由发言：打开输入框，玩家自己组织台词；取消则回到选项。
                if (_selectedIntent == PlayerIntent.FreeSpeech)
                {
                    string freeText = null;
                    bool canceled = false;
                    bool opened = NpcFreeTextInputUI.Open(
                        "你要在圆桌上说什么？",
                        "直接说出你的指控、辩解或带票。Enter 发送　·　Esc 取消",
                        t => freeText = t,
                        () => canceled = true);

                    if (!opened)
                    {
                        selectedIndex = -1;
                        continue;
                    }

                    while (!canceled && freeText == null && _meetingActive)
                        yield return null;

                    if (!_meetingActive) yield break;
                    if (canceled || string.IsNullOrEmpty(freeText))
                    {
                        selectedIndex = -1;
                        continue;
                    }

                    yield return PlayerFreeSpeech(freeText);
                    _currentSpeakerIsPlayer = false;
                    if (ui != null) ui.SetHint(string.Empty);
                    yield break;
                }

                // 指认 / 带票都要先点名字；指认再让玩家决定“只凭时间线”还是“出示证据”。
                if (_selectedIntent == PlayerIntent.Accuse || _selectedIntent == PlayerIntent.VoteLead)
                {
                    int picked = -1;
                    yield return PickAccusationTarget(value => picked = value);
                    if (picked <= 0)
                    {
                        selectedIndex = -1;
                        continue;
                    }
                    accuseTarget = picked;

                    if (_selectedIntent == PlayerIntent.Accuse)
                    {
                        int mode = -1;
                        yield return PickAccusationMode(accuseTarget, value => mode = value);
                        if (mode < 0)
                        {
                            selectedIndex = -1;
                            continue;
                        }
                        accuseWithEvidence = mode == 1;
                    }
                }
            }

            if (!_meetingActive) yield break;

            // 目击陈述：玩家亲眼看见的翻窗 / 钉钉 / 杀人，可以直接摆到桌上。
            if (_selectedIntent == PlayerIntent.Witness && _witnessOptionAvailable)
            {
                yield return PlayerEyewitnessStatement();
                _currentSpeakerIsPlayer = false;
                if (ui != null) ui.SetHint(string.Empty);
                yield break;
            }

            // 模块四：最后一页——号召所有人摘下面具（结局 C 入口）。
            if (_selectedIntent == PlayerIntent.Unmask
                && CanTriggerCollectiveUnmask(out _, out _))
            {
                yield return PlayerUnmaskDemand();
                _currentSpeakerIsPlayer = false;
                if (ui != null) ui.SetHint(string.Empty);
                yield break;
            }

            // 指认＋证据：只把证据摆到某一个人面前，而不是泛泛地“给全场看”。
            if (_selectedIntent == PlayerIntent.Accuse && accuseWithEvidence && accuseTarget > 0)
            {
                yield return PlayerAccuseWithEvidence(accuseTarget);
                _currentSpeakerIsPlayer = false;
                if (ui != null) ui.SetHint(string.Empty);
                yield break;
            }

            // 口头反驳的怀疑度由 Yarn 的 $player_suspicion 决定，不再走固定 -2。
            if (_selectedIntent != PlayerIntent.Rebuttal)
                ApplyPlayerStatement(_selectedIntent, accuseTarget);
            InjectPlayerVariables(accuseTarget);

            string node;
            string fallback;
            switch (_selectedIntent)
            {
                case PlayerIntent.Alibi:
                    node = "Meeting_Player_Alibi";
                    fallback = BuildAlibiSpeech();
                    break;
                case PlayerIntent.Accuse:
                    node = "Meeting_Player_Accuse";
                    fallback = $"{_playerNumber}号：我怀疑{accuseTarget}号。{accuseTarget}号，你醒来之后的行踪说不清楚。";
                    break;
                case PlayerIntent.VoteLead:
                    node = "Meeting_Player_DirectVote";
                    fallback = BuildVoteLeadSpeech(accuseTarget);
                    break;
                case PlayerIntent.Rebuttal:
                    node = "Meeting_Player_Ninth";
                    fallback = $"{_playerNumber}号：门从里面闩着，不代表没人从窗户进去。" +
                               "你们每个人都出过门，别把一次出门当成杀人。";
                    break;
                case PlayerIntent.Timeline:
                    node = "Meeting_Player_Timeline";
                    fallback = BuildTimelineSpeech();
                    break;
                case PlayerIntent.Demand:
                    node = "Meeting_Player_Demand";
                    fallback = $"{_playerNumber}号：既然谁都说自己只出去过一次，那就从1号开始，" +
                               "每个人把自己醒来后走过的每一步说清楚。谁先说不下去，谁就先解释。";
                    break;
                default:
                    node = "Meeting_Player_Silence";
                    fallback = $"{_playerNumber}号：……（你沉默着，没有为自己辩解）";
                    break;
            }

            if (ui != null)
            {
                ui.SetHint(accuseTarget > 0 ? $"你把话头指向了 {accuseTarget} 号。" : string.Empty);
                ui.SetLine(string.Empty);
            }

            bool started = TryStartYarnNode(node);
            if (started)
            {
                yield return WaitForDialogue();
                SyncDialogueState();
            }
            else
            {
                if (ui != null) ui.SetLine(fallback);
                yield return new WaitForSeconds(3.2f);

                if (_selectedIntent == PlayerIntent.Rebuttal)
                {
                    float delta = HasTruthEvidenceInInventory() ? -10f : 15f;
                    _playerSuspicion = Mathf.Clamp(_playerSuspicion + delta, 0f, 100f);
                    if (ui != null) ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
                }
            }

            _currentSpeakerIsPlayer = false;
            if (ui != null) ui.SetHint(string.Empty);
        }

        // ------------------------------------------------------------------
        // 玩家自由发言 + NPC 现场回应
        // ------------------------------------------------------------------

        IEnumerator PlayerFreeSpeech(string text)
        {
            string line = (text ?? "").Trim();
            if (string.IsNullOrEmpty(line)) yield break;

            if (ui != null)
            {
                ui.SetLine($"{_playerNumber}号：{line}");
                ui.SetHint("圆桌安静了一瞬。");
            }
            NpcConversationMemory.AddMeeting($"玩家{_playerNumber}号：{line}");

            // 自由发言以前只上屏、不结算，说得再好也改变不了任何人。
            // 这里按“说了谁、说了什么时间、有没有物证、有没有因果”打分，
            // 分数直接降低他人的信任门槛，并抬升对所指目标的怀疑。
            PlayerIntent intent = InferIntent(line);
            float quality = ScoreSpeechQuality(line);
            int target = (intent == PlayerIntent.Accuse || intent == PlayerIntent.VoteLead)
                ? FindMentionedNumber(line, _playerNumber)
                : 0;
            if ((intent == PlayerIntent.Accuse || intent == PlayerIntent.VoteLead) && target <= 0)
                intent = PlayerIntent.Demand;

            int moved = ApplyPlayerStatement(intent, target, quality);

            // 被点名的人必须第一个接话。以前英文 "No. 1" 解析不出来，
            // 系统会按怀疑度随机挑人，看起来就像 No.1 从不回应。
            NPCController responder = PickMeetingResponder(line, target);
            bool targetSpoke = responder != null && target > 0 && responder.Number == target;

            if (ui != null)
            {
                if (targetSpoke)
                {
                    if (Localization.IsEnglish)
                    {
                        ui.SetHint(moved > 0
                            ? $"No. {target} jerks their head up; {moved} others mutter in agreement."
                            : $"No. {target} jerks their head up and stares straight at you.");
                    }
                    else
                    {
                        ui.SetHint(moved > 0
                            ? $"被点名的 {target} 号猛地抬起头，旁边也有 {moved} 个人低声附和。"
                            : $"被点名的 {target} 号猛地抬起头，盯着你。");
                    }
                }
                else
                {
                    ui.SetHint(moved > 0
                        ? $"你听见 {moved} 个人低声附和。"
                        : quality >= 0.55f
                            ? "有人皱眉，但没有当场反驳。"
                            : "没人接话，目光反而落到你身上。");
                }
            }

            // 点名到人时不要拖：被点名者会当场跳出来，而不是让圆桌安静两秒。
            yield return new WaitForSecondsRealtime(targetSpoke ? 0.7f : 1.8f);
            if (!_meetingActive) yield break;

            if (responder == null)
            {
                if (ui != null) ui.SetLine("没有人接话。");
                yield return new WaitForSecondsRealtime(2.2f);
                yield break;
            }

            _currentSpeaker = responder;
            _npcTurnActive = true;
            _skipCurrentSpeech = false;
            _llmPresentedEvidenceThisTurn = false;
            _lastLlmVoteTarget = 0;

            if (ui != null)
            {
                ui.SetSpeaker($"{responder.Number} 号");
                ShowNpcSpeakingHint();
            }

            yield return MoveCameraToSpeaker(responder);
            if (!_meetingActive) { ClearSpeakerState(); yield break; }

            NpcLLMReply reply = null;
            if (LlmReady)
            {
                NpcContext ctx = NpcContextBuilder.BuildMeeting(
                    responder, target, line, _playerSuspicion);
                bool responderIsTarget = target > 0 && responder.Number == target;
                ctx.sceneNote = responderIsTarget
                    ? $"玩家刚刚在圆桌上点了你的名（{target}号），把矛头直接指向你。" +
                      "你必须当场回应：先反驳或反问，再解释自己的时间线；不能装作没听见。"
                    : "玩家刚刚在圆桌上自由发言，可能指控某人、为自己辩解或带票。" +
                      "你只回应他实际说的话；不要背固定台词。";

                bool finished = false;
                StartCoroutine(LLMNpcBrain.Instance.Generate(ctx,
                    r => { reply = r; finished = true; }));

                float waitStart = Time.realtimeSinceStartup;
                while (!finished && Time.realtimeSinceStartup - waitStart
                       < Mathf.Max(0.5f, llmPlayerReplyTimeout))
                    yield return null;

                // 圆桌不能被一次推理拖住：超时直接取消，走 Yarn 兜底。
                if (!finished) LLMNpcBrain.Instance.Abort();
            }

            if (!_meetingActive || _skipCurrentSpeech)
            {
                _skipCurrentSpeech = false;
                ClearSpeakerState();
                yield break;
            }

            if (reply != null && reply.success)
            {
                yield return PlayLlmMeetingSpeech(responder, reply);
                if (_lastLlmVoteTarget > 0)
                    ApplyNpcSpeechEffects(responder, _lastLlmVoteTarget);
            }
            else
            {
                string fallback = BuildPlayerSpeechResponseFallback(responder, line);
                if (ui != null)
                {
                    ui.SetLine(fallback);
                    ui.SetHint("他没有接话。");
                }
                NpcConversationMemory.AddMeeting(fallback);
                yield return new WaitForSecondsRealtime(2.4f);
            }

            ClearSpeakerState();
        }

        void ClearSpeakerState()
        {
            _npcTurnActive = false;
            _currentSpeaker = null;
            if (ui != null) ui.SetHint(string.Empty);
        }

        /// <summary>NPC 发言时的底部提示。出局玩家不再被提示“按 R 打断”。</summary>
        void ShowNpcSpeakingHint()
        {
            if (ui == null) return;

            // 玩家已经选好打断方式：台词开始播时不要又把提示改回“按 R 打断”，
            // 否则玩家会以为还能再选一次。
            if (_pendingInterrupt != InterruptAction.None)
            {
                ui.SetHint("他已经听见了——等他把话说完。");
                return;
            }

            ui.SetHint(CycleState.playerAlive
                ? "按 R 打断（自由发言 / 出示证据）"
                : "你已经出局——只能旁观");
        }

        NPCController PickMeetingResponder(string text, int preferredTarget)
        {
            if (_attendees.Count == 0) return null;

            // 点名优先：中英双语都识别（1号 / No. 1 / No.1 / Number 1）。
            // 玩家明确提到谁，就让谁第一个接话（死者不能接）。
            if (preferredTarget > 0)
            {
                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController npc = _attendees[i];
                    if (npc == null || npc.isDead) continue;
                    if (npc.Number == preferredTarget) return npc;
                }
            }

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.isDead) continue;
                if (PlayerSpeechEvaluator.MentionsNumber(text, npc.Number)) return npc;
            }

            NPCController best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.isDead) continue;

                NPCBlackboard b = npc.blackboard;
                float score = b.GetSuspicionToward(_playerNumber) * 1.2f
                              + b.GetHostilityToward(_playerNumber) * 1.5f
                              + b.stress * 0.2f
                              + (b.wantsPlayerDead ? 20f : 0f)
                              - b.GetTrust(_playerNumber) * 0.3f
                              + Random.Range(0f, 12f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = npc;
                }
            }
            return best;
        }

        /// <summary>
        /// 模型不可用时不编造应答。以前这里按关键词拼一段“听起来像回答”的台词，
        /// 一旦重复就非常出戏；现在只保留一个沉默的停顿。
        /// </summary>
        string BuildPlayerSpeechResponseFallback(NPCController responder, string playerText)
        {
            return $"{responder.Number}号：……";
        }

        /// <summary>把自由发言归到最接近的会议意图。中英双语都识别。</summary>
        static PlayerIntent InferIntent(string text)
        {
            string raw = text ?? "";
            string t = raw.Replace(" ", "");
            string lower = raw.ToLowerInvariant();

            if (t.Contains("投") || t.Contains("票")
                || lower.Contains("vote") || lower.Contains("voting"))
                return PlayerIntent.VoteLead;
            if ((t.Contains("摘") && t.Contains("面具"))
                || lower.Contains("unmask")
                || (lower.Contains("take off") && lower.Contains("mask")))
                return PlayerIntent.Unmask;
            if ((t.Contains("我") && (t.Contains("没") || t.Contains("不是") || t.Contains("醒")))
                || (lower.Contains("i ") && (lower.Contains("didn't") || lower.Contains("did not")
                    || lower.Contains("wasn't") || lower.Contains("was not")
                    || lower.Contains("woke") || lower.Contains("asleep"))))
                return PlayerIntent.Alibi;
            if (t.Contains("怀疑") || t.Contains("凶手") || t.Contains("杀的")
                || (t.Contains("是") && t.Contains("号"))
                || lower.Contains("suspect") || lower.Contains("killer")
                || lower.Contains("murder") || lower.Contains("killed"))
                return PlayerIntent.Accuse;
            if (t.Contains("时间") || t.Contains("顺序") || t.Contains("几点")
                || lower.Contains("timeline") || lower.Contains("what time")
                || lower.Contains("in what order"))
                return PlayerIntent.Timeline;
            if (t.Contains("说清楚") || t.Contains("交代")
                || lower.Contains("explain") || lower.Contains("tell me"))
                return PlayerIntent.Demand;
            return PlayerIntent.Accuse;
        }

        /// <summary>
        /// 发言质量 0~1。玩家把“谁、什么时候、凭什么、因此如何”说清楚时，
        /// 听众才真正被说服；只是喊口号或骂人会被扣分。
        /// </summary>
        static float ScoreSpeechQuality(string text)
        {
            return PlayerSpeechEvaluator.Score(text);
        }

        int FindMentionedNumber(string text, int excludeNumber)
        {
            return PlayerSpeechEvaluator.FindMentionedNumber(text, excludeNumber);
        }

        /// <summary>按当前证据 / 真相 / 目击状态重建玩家的发言意图。</summary>
        void BuildPlayerChoices()
        {
            _playerChoices.Clear();
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.Alibi,
                label = "（自辩）说清我醒来后去过哪，让监控替我作证",
            });
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.Accuse,
                label = "（指认）点名一个人，摆出我手上的证据",
            });
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.VoteLead,
                label = "（带票）我建议先投某个人",
            });
            if (LlmReady)
            {
                _playerChoices.Add(new PlayerChoice
                {
                    intent = PlayerIntent.FreeSpeech,
                    label = "（自由发言）我想自己说……",
                });
            }
            else
            {
                _playerChoices.Add(new PlayerChoice
                {
                    intent = PlayerIntent.Rebuttal,
                    label = "（反驳）门闩着，不代表没人从窗进去",
                });
            }
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.Timeline,
                label = "（时间线）把监控里的出门顺序摆上桌",
            });
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.Demand,
                label = "（要求发言）让每个人交代醒来后的行动",
            });
            _playerChoices.Add(new PlayerChoice
            {
                intent = PlayerIntent.Silence,
                label = "沉默 / 过",
            });

            // 亲眼看见的越界行动：把它变成公聊里可以点名的一句话，而不是只躺在证据面板里。
            _witnessOptionAvailable = TryGetEyewitness(out _witnessTarget, out _witnessWhat);
            if (_witnessOptionAvailable)
            {
                _playerChoices.Add(new PlayerChoice
                {
                    intent = PlayerIntent.Witness,
                    label = $"（目击）我看见{_witnessTarget}号{_witnessWhat}",
                });
            }

            // 用户规则：真相已揭穿且平均信任 >= 60，才给出“集体摘面具”选项。
            if (CanTriggerCollectiveUnmask(out _, out _))
            {
                _playerChoices.Add(new PlayerChoice
                {
                    intent = PlayerIntent.Unmask,
                    label = "（真相）号召所有人摘下面具",
                });
            }
        }

        void ShowPlayerOptions(System.Action<int> onSelected)
        {
            if (ui == null) return;

            BuildPlayerChoices();
            string[] labels = new string[_playerChoices.Count];
            for (int i = 0; i < _playerChoices.Count; i++)
                labels[i] = _playerChoices[i].label;

            ui.ShowOptions(labels, onSelected);
            ui.SetHint(BuildPlayerTurnHint());
        }

        int FindChoiceIndex(PlayerIntent intent)
        {
            for (int i = 0; i < _playerChoices.Count; i++)
            {
                if (_playerChoices[i].intent == intent) return i;
            }
            return -1;
        }

        /// <summary>指认后的第二步：只凭时间线，还是把手上的证据摆到对方面前。</summary>
        IEnumerator PickAccusationMode(int target, System.Action<int> onPicked)
        {
            RefreshEvidenceItems();
            bool hasEvidence = _evidenceIds.Count > 0;

            List<string> labels = new List<string>
            {
                $"（只凭时间线）我怀疑 {target} 号",
            };
            if (hasEvidence)
                labels.Add($"（出示证据）我有东西能说明 {target} 号做了什么");
            labels.Add("返回");

            int picked = -2;
            _playerOptionsVisible = true;
            if (ui != null)
            {
                ui.ShowOptions(labels.ToArray(), index =>
                {
                    if (index < 0 || index >= labels.Count - 1) picked = -1;
                    else picked = index;
                });
            }
            else
            {
                picked = 0;
            }

            while (picked == -2 && _meetingActive) yield return null;

            if (ui != null) ui.HideOptions();
            _playerOptionsVisible = false;
            onPicked(picked == -2 ? -1 : picked);
        }

        /// <summary>指认＋证据：把某件证据直接摆到某个人的面前，并让全场听见。</summary>
        IEnumerator PlayerAccuseWithEvidence(int target)
        {
            RefreshEvidenceItems();
            if (_evidenceIds.Count == 0)
            {
                ApplyPlayerStatement(PlayerIntent.Accuse, target);
                InjectPlayerVariables(target);
                yield return PlayPlayerNode("Meeting_Player_Accuse",
                    $"{_playerNumber}号：我怀疑{target}号。{target}号，你醒来之后的行踪说不清楚。");
                yield break;
            }

            string[] labels = new string[_evidenceIds.Count];
            for (int i = 0; i < _evidenceIds.Count; i++)
                labels[i] = BuildEvidenceLabel(_evidenceIds[i]);

            int selected = -1;
            bool canceled = false;
            if (ui != null)
            {
                ui.ShowEvidence(labels, index => selected = index, () => canceled = true);
                float waitStart = Time.realtimeSinceStartup;
                while (!canceled && selected < 0 && _meetingActive
                       && Time.realtimeSinceStartup - waitStart < evidencePanelTimeout)
                    yield return null;
                if (!canceled && selected < 0 && _meetingActive)
                {
                    Debug.LogWarning("[MeetingSystem] 指认证据面板超过时限，自动关闭。");
                    canceled = true;
                }
                ui.HideEvidence();
            }
            else
            {
                canceled = true;
            }

            if (canceled || selected < 0 || !_meetingActive)
            {
                // 取消证据选择：退回“只凭时间线”的指认，而不是白费整个回合。
                ApplyPlayerStatement(PlayerIntent.Accuse, target);
                InjectPlayerVariables(target);
                yield return PlayPlayerNode("Meeting_Player_Accuse",
                    $"{_playerNumber}号：我怀疑{target}号。{target}号，你醒来之后的行踪说不清楚。");
                yield break;
            }

            ItemDefinition evidence = GetItem(_evidenceIds[selected]);
            if (evidence == null)
            {
                InjectPlayerVariables(target);
                yield return PlayPlayerNode("Meeting_Player_Accuse",
                    $"{_playerNumber}号：我怀疑{target}号。");
                yield break;
            }

            bool pointsAtTarget = EvidencePointsAt(evidence.id, target);
            bool correct = IsTruthEvidence(evidence.id);

            InjectPlayerVariables(target);
            InjectEvidenceVariables(evidence, FindAttendee(target));
            SetString("$evidence_origin", EvidenceLedger.GetOrigin(evidence.id));
            SetBool("$evidence_points_at_accused", pointsAtTarget);
            SetFloat("$accuse_number", target);
            SetString("$accuse_name", NameText(target));

            if (ui != null)
            {
                ui.SetSpeaker($"{_playerNumber} 号（你）");
                ui.SetLine(string.Empty);
                ui.SetHint($"你把{evidence.displayName}摆到了{target}号面前。");
            }

            string fallback = $"{_playerNumber}号：{target}号，先别急着反驳。" +
                              $"看看这个——{evidence.displayName}。{evidence.evidenceReason}";
            yield return PlayPlayerNode("Meeting_Player_Accuse_Evidence", fallback);
            SyncDialogueState();

            ApplyAccuseEvidenceEffects(evidence, target, pointsAtTarget);

            if (correct && target == 1)
            {
                _revealedTruth = true;
                SetBool("$revealed_truth", true);
            }

            if (ui != null)
            {
                ui.SetBanner($"你当众把{evidence.displayName}指向了{target}号。");
                ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
            }
        }

        IEnumerator PlayPlayerNode(string node, string fallback)
        {
            if (TryStartYarnNode(node))
            {
                yield return WaitForDialogue();
            }
            else
            {
                if (ui != null) ui.SetLine(fallback);
                yield return new WaitForSeconds(3.2f);
            }
        }

        void ApplyAccuseEvidenceEffects(ItemDefinition evidence, int target, bool pointsAtTarget)
        {
            float weight = Mathf.Max(1f, evidence.evidenceWeight);

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener.blackboard == null) continue;

                string clue = $"player_showed_evidence_{target}";
                if (!listener.blackboard.knownClues.Contains(clue))
                    listener.blackboard.knownClues.Add(clue);

                if (listener.Number == target)
                {
                    listener.blackboard.suspicion =
                        Mathf.Min(100f, listener.blackboard.suspicion + weight * 0.35f);
                    listener.blackboard.AddTrust(_playerNumber, -weight * 0.15f);
                    listener.blackboard.stress =
                        Mathf.Min(100f, listener.blackboard.stress + 8f);
                    listener.blackboard.AddSuspectFront(_playerNumber);
                    listener.blackboard.meetingAccusations++;
                    continue;
                }

                if (pointsAtTarget)
                {
                    listener.blackboard.AddSuspectFront(target);
                    // 证据不是“记一笔”就完了：它必须真的抬高对目标的怀疑，才会改变投票。
                    listener.blackboard.AddSuspicionToward(target, weight * 0.35f);
                    listener.blackboard.AddTrust(_playerNumber, weight * 0.10f);
                    listener.blackboard.suspicion =
                        Mathf.Max(0f, listener.blackboard.suspicion - 3f);

                    if (weight >= 40f && listener.blackboard.GetTrust(_playerNumber) >= 42f)
                    {
                        listener.blackboard.voteIntentNumber = target;
                        listener.blackboard.AddTrust(_playerNumber, 2f);
                    }

                    if (listener.blackboard.wantsPlayerDead
                        && listener.blackboard.GetTrust(_playerNumber) >= 50f)
                    {
                        listener.blackboard.wantsPlayerDead = false;
                        listener.blackboard.aggression =
                            Mathf.Max(0f, listener.blackboard.aggression - 12f);
                    }
                }
                else
                {
                    listener.blackboard.AddTrust(_playerNumber, 1f);
                }
            }

            if (pointsAtTarget)
            {
                _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 8f);
                CycleState.RecordCoreEvidence(evidence.id, evidence.evidenceWeight);
            }
            else
            {
                _playerSuspicion = Mathf.Min(100f, _playerSuspicion + 4f);
            }

            Debug.Log($"[MeetingSystem] 玩家向 {target} 号出示证据「{evidence.displayName}」" +
                      $"（指向={(pointsAtTarget ? "是" : "否")}，权重 {weight:0}）。");
        }

        /// <summary>这件证据是否真的指向某个人（来源、目击、监控、尸体）。</summary>
        bool EvidencePointsAt(string evidenceId, int target)
        {
            if (string.IsNullOrEmpty(evidenceId) || target <= 0) return false;

            if (evidenceId.StartsWith("saw_kill_"))
            {
                string[] parts = evidenceId.Split('_');
                return parts.Length >= 3 && int.TryParse(parts[2], out int killer) && killer == target;
            }

            if (evidenceId.StartsWith("saw_window_") || evidenceId.StartsWith("saw_nail_1_"))
            {
                return target == 1;
            }

            if (evidenceId.StartsWith("saw_nail_"))
            {
                string[] parts = evidenceId.Split('_');
                return parts.Length >= 3 && int.TryParse(parts[2], out int nailer) && nailer == target;
            }

            if (evidenceId.StartsWith("saw_exit_") || evidenceId.StartsWith("saw_return_"))
            {
                string[] parts = evidenceId.Split('_');
                return parts.Length >= 3 && int.TryParse(parts[2], out int mover) && mover == target;
            }

            if (evidenceId == "video_log" || evidenceId.StartsWith("video_clip_1_")
                || evidenceId.StartsWith("wall_nails") || evidenceId == "mystic_magnet")
            {
                return target == 1;
            }

            if (evidenceId.StartsWith("video_clip_"))
            {
                string[] parts = evidenceId.Split('_');
                return parts.Length >= 3 && int.TryParse(parts[2], out int clip) && clip == target;
            }

            if (evidenceId.StartsWith("saw_body_") || evidenceId.StartsWith("body_")
                || evidenceId == "ninth_body")
            {
                int victim = 0;
                if (evidenceId == "ninth_body") victim = 9;
                else
                {
                    string[] parts = evidenceId.Split('_');
                    int.TryParse(parts[parts.Length - 1], out victim);
                }
                return victim == target;
            }

            if (evidenceId == "face_compare" || evidenceId == "faces_identical")
                return true;

            // 实物：只按“取自几号房”算，不把每个房间都有的工具当成某人的罪证。
            return ItemProvenance.GetRoom(evidenceId) == target;
        }

        bool HasEvidenceAgainst(int target)
        {
            if (target == 1 && _revealedTruth) return true;

            IReadOnlyList<EvidenceEntry> entries = EvidenceLedger.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && EvidencePointsAt(entries[i].id, target)) return true;
            }

            if (player != null && player.inventory != null)
            {
                IReadOnlyList<string> items = player.inventory.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (EvidencePointsAt(items[i], target)) return true;
                }
            }
            return false;
        }

        string BuildAlibiSpeech()
        {
            return $"{_playerNumber}号：我醒来时，屏幕上的倒计时是{MeetingOutTime(_playerNumber)}。" +
                   "9号在2号醒来之前就已经死了——那时候我还没睁眼。" +
                   "你们可以把我的出门算进嫌疑，但不能把一个还在睡的人算成凶手。";
        }

        string BuildVoteLeadSpeech(int target)
        {
            return $"{_playerNumber}号：我不想再听一遍所有人说自己只出去过一次。" +
                   $"如果今天一定要投一个人，我建议先投{target}号。" +
                   "不是因为我讨厌他，是因为时间线只在他身上断过。";
        }

        /// <summary>
        /// 从证据账本里找出“玩家亲眼看见的越界行动”。
        /// 只认玩家真的看见并记录下来的条目，不做任何全知推断。
        /// </summary>
        bool TryGetEyewitness(out int target, out string what)
        {
            target = 0;
            what = "";

            IReadOnlyList<EvidenceEntry> entries = EvidenceLedger.Entries;
            EvidenceEntry best = null;
            for (int i = 0; i < entries.Count; i++)
            {
                EvidenceEntry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.id)) continue;
                if (!entry.id.StartsWith("saw_kill_")
                    && !entry.id.StartsWith("saw_window_")
                    && !entry.id.StartsWith("saw_nail_"))
                    continue;
                if (best == null || entry.weight > best.weight) best = entry;
            }

            if (best == null) return false;

            string[] parts = best.id.Split('_');
            if (parts.Length < 3) return false;

            // saw_window_out_1 / saw_window_in_1 把编号放在最后；其余把编号放在第三段。
            if (best.id.StartsWith("saw_window_"))
            {
                if (!int.TryParse(parts[parts.Length - 1], out target)) return false;
            }
            else if (!int.TryParse(parts[2], out target))
            {
                return false;
            }

            if (best.id.StartsWith("saw_kill_"))
            {
                int victim = 0;
                if (parts.Length >= 4) int.TryParse(parts[3], out victim);
                what = victim > 0
                    ? $"翻进{victim}号房，用匕首杀了{victim}号"
                    : "动手杀了人";
            }
            else if (best.id.StartsWith("saw_nail_"))
            {
                int room = 0;
                if (parts.Length >= 4) int.TryParse(parts[3], out room);
                what = room > 0
                    ? $"在{room}号房窗外钉下支点钉，把绳子绕了过去"
                    : "在窗外的墙上钉了支点钉";
            }
            else
            {
                what = "翻出窗外，沿着墙檐往9号房去了";
            }

            return target > 0;
        }

        /// <summary>玩家把自己的目击摆上桌：不是“我觉得”，而是“我看见”。</summary>
        IEnumerator PlayerEyewitnessStatement()
        {
            // 公开点名不是没有代价的：被点名的人会更防备你，其他人会重新掂量你说的话。
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.blackboard == null) continue;
                if (npc.Number == _witnessTarget)
                {
                    npc.blackboard.suspicion = Mathf.Min(100f, npc.blackboard.suspicion + 15f);
                    npc.blackboard.AddTrust(_playerNumber, -8f);
                }
                else
                {
                    npc.blackboard.AddTrust(_playerNumber, 2f);
                }
            }

            InjectPlayerVariables(0);
            SetFloat("$witness_number", _witnessTarget);
            SetString("$witness_name", NameText(_witnessTarget));
            SetString("$witness_what", Localization.T(_witnessWhat));

            if (ui != null)
            {
                ui.SetSpeaker($"{_playerNumber} 号（你）");
                ui.SetLine(string.Empty);
                ui.SetHint($"你点名了 {_witnessTarget} 号。");
            }

            if (TryStartYarnNode("Meeting_Player_Eyewitness"))
            {
                yield return WaitForDialogue();
                SyncDialogueState();
            }
            else
            {
                if (ui != null)
                    ui.SetLine($"{_playerNumber}号：我看见{_witnessTarget}号{_witnessWhat}。" +
                               "这不是推测。我亲眼看见的。");
                yield return new WaitForSeconds(3.2f);
            }

            if (ui != null) ui.SetHint(string.Empty);
        }

        /// <summary>
        /// 把公开监控里的真实出门记录写成玩家可以说出口的一句话。
        /// 内容全部来自 SurveillanceLog：玩家自己这轮怎么走，监控就怎么记，
        /// 不会再出现“1号1:30、1:05”这种和玩家行为对不上的剧本时间。
        /// </summary>
        string BuildTimelineSpeech()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"{_playerNumber}号：").Append(SurveillanceLog.BuildReplayText());
            sb.Append("如果只按“谁出去过”来定罪，那第一个该解释的人不是我。");
            return sb.ToString();
        }

        /// <summary>
        /// 模块四：玩家号召全员摘面具。
        /// 条件：本轮已在会议上揭穿真相，且存活 NPC 对玩家的平均信任 &gt;= 60。
        /// 成功 → 结局 C；失败 → 全员拒绝，玩家怀疑度大幅上升。
        /// </summary>
        IEnumerator PlayerUnmaskDemand()
        {
            bool ready = CanTriggerCollectiveUnmask(out int coreCount, out float avgTrust);
            InjectPlayerVariables(0);
            InjectCollectiveUnmaskVariables(coreCount, avgTrust, ready);

            if (ui != null)
            {
                ui.SetSpeaker($"{_playerNumber} 号（你）");
                ui.SetLine(string.Empty);
                ui.SetHint(string.Empty);
            }

            if (ready)
            {
                Debug.Log($"[MeetingSystem] 集体摘面具条件达成：真相已揭穿，核心证据 {coreCount} 件，" +
                          $"平均信任 {avgTrust:0.0} >= 60，存活参与者 {CycleState.GetLivingParticipantCount()} 人 → 结局 C。");

                for (int i = 0; i < _attendees.Count; i++)
                {
                    NPCController npc = _attendees[i];
                    if (npc == null) continue;
                    npc.blackboard.AddTrust(_playerNumber, 10f);
                    npc.blackboard.stress = Mathf.Min(100f, npc.blackboard.stress + 15f);
                    npc.blackboard.panicUntil = Mathf.Max(npc.blackboard.panicUntil, Time.time + 20f);
                    npc.blackboard.RemoveSuspect(_playerNumber);
                }
                _playerSuspicion = 0f;
                if (ui != null) ui.SetSuspicion(0);

                string successNode = "Meeting_Player_Unmask_Success";
                string fallback = $"{_playerNumber}号：如果你们还相信我说的话——把面具摘下来。" +
                                  "看看彼此的脸，看看我们到底在重复什么。";
                if (TryStartYarnNode(successNode))
                {
                    yield return WaitForDialogue();
                }
                else
                {
                    if (ui != null) ui.SetLine(fallback);
                    yield return new WaitForSeconds(4f);
                }

                if (ui != null)
                {
                    ui.SetBanner("所有人沉默着，抬手摘下了自己的面具。");
                    ui.SetHint(string.Empty);
                }
                yield return new WaitForSeconds(1f);

                if (EndingSystem.Instance != null)
                {
                    EndingSystem.Instance.TriggerEndingC(
                        "你在圆桌会议上说出了真相。所有人同时摘下了面具，循环失去了继续运转的理由。");
                }
                else
                {
                    if (GameManager.Instance != null) GameManager.Instance.HaltLoop();
                    if (ui != null)
                        ui.SetLine("所有人摘下了面具。\n灯光熄灭，梦境失去了继续运转的理由。");
                    Debug.LogError("[MeetingSystem] 场景缺少 EndingSystem，无法播放结局 C。");
                }
                yield break;
            }

            Debug.Log($"[MeetingSystem] 集体摘面具失败：真相={( _revealedTruth ? "已揭穿" : "未揭穿")}，" +
                      $"核心证据 {coreCount}/2，平均信任 {avgTrust:0.0}/60，" +
                      $"存活参与者 {CycleState.GetLivingParticipantCount()}/2。" +
                      "全员拒绝，玩家怀疑度大幅上升。");
            ApplyPlayerStatement(PlayerIntent.Unmask, 0);

            string refuseNode = "Meeting_Player_Unmask_Refused";
            string refuseFallback = $"{_playerNumber}号：把面具摘下来——你们难道不想看看彼此的脸吗？\n" +
                                    "（没有人动。有人低声说：你疯了。）";
            if (TryStartYarnNode(refuseNode))
            {
                yield return WaitForDialogue();
            }
            else
            {
                if (ui != null) ui.SetLine(refuseFallback);
                yield return new WaitForSeconds(4f);
            }

            if (ui != null)
            {
                ui.SetBanner("没有人响应你。怀疑像冷风一样灌满整个房间。");
                ui.SetHint(string.Empty);
            }
            yield return new WaitForSeconds(1.2f);
        }

        bool CanTriggerCollectiveUnmask(out int coreCount, out float avgTrust)
        {
            coreCount = CycleState.revealedCoreEvidenceCount;
            avgTrust = CycleState.GetAverageTrustTowardPlayer();
            // 真正的破局规则：所有活着的人一起摘下面具，且活着的人必须 ≥2。
            // 玩家可以杀很多人，但把其他人杀光就不会再有“集体”可言。
            bool enoughLiving = CycleState.GetLivingParticipantCount() >= 2;
            return _revealedTruth && avgTrust >= 60f && enoughLiving;
        }

        string BuildPlayerTurnHint()
        {
            bool ready = CanTriggerCollectiveUnmask(out _, out _);
            int max = _playerChoices.Count > 0 ? _playerChoices.Count : 7;

            if (CycleState.GetLivingParticipantCount() < 2)
                return $"按 1~{max} 选择，按 R 出示证据　|　还活着的人不足两个，没人能和你一起摘下面具";

            if (ready)
                return $"按 1~{max} 选择，按 R 出示证据　|　空气变了：有人开始认真听你说话";

            return _revealedTruth
                ? $"按 1~{max} 选择，按 R 出示证据　|　真相已经撕开一角，但还缺最后一块"
                : $"按 1~{max} 选择，按 R 出示证据";
        }

        IEnumerator PickAccusationTarget(System.Action<int> onPicked)
        {
            // 指认对象不再只给 3 个“系统觉得可疑的人”：桌上每一个活着的人都能被点名。
            List<int> candidates = new List<int>();
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null || npc.Number == _playerNumber) continue;
                if (!IsAliveParticipant(npc.Number)) continue;
                if (!candidates.Contains(npc.Number)) candidates.Add(npc.Number);
            }

            candidates.Sort((a, b) =>
            {
                NPCController na = FindAttendee(a);
                NPCController nb = FindAttendee(b);
                float sa = na != null && na.blackboard != null ? na.blackboard.suspicion : 0f;
                float sb = nb != null && nb.blackboard != null ? nb.blackboard.suspicion : 0f;
                return sb.CompareTo(sa);
            });
            if (candidates.Count > 8) candidates.RemoveRange(8, candidates.Count - 8);

            if (candidates.Count == 0)
            {
                onPicked(-1);
                yield break;
            }

            string[] labels = new string[candidates.Count + 1];
            for (int i = 0; i < candidates.Count; i++)
                labels[i] = $"怀疑 {candidates[i]} 号";
            labels[labels.Length - 1] = "返回";

            int picked = -2;
            _playerOptionsVisible = true;
            if (ui != null)
            {
                ui.ShowOptions(labels, index =>
                {
                    picked = index >= 0 && index < candidates.Count ? candidates[index] : -1;
                });
            }
            else
            {
                picked = candidates[0];
            }

            while (picked == -2 && _meetingActive) yield return null;

            if (ui != null) ui.HideOptions();
            _playerOptionsVisible = false;
            onPicked(picked == -2 ? -1 : picked);
        }

        /// <summary>
        /// 玩家发言的社交结算。每条发言都必须能改变别人的判断，
        /// 否则玩家就只是“看戏”——尤其是 Alibi / VoteLead 必须能真正动摇杀意和票向。
        /// </summary>
        int ApplyPlayerStatement(PlayerIntent intent, int accuseTarget, float quality = 0.5f)
        {
            int defused = 0;
            int swayed = 0;

            // 发言质量决定“别人愿不愿意信”。空泛的指控仍会被当成泼脏水，
            // 但把时间线、证据、动机说清楚时，原本中立的听众也会被带走。
            float q = Mathf.Clamp01(quality);
            float accuseTrustLine = Mathf.Lerp(58f, 40f, q);
            float accuseDistrustLine = Mathf.Lerp(42f, 28f, q);
            float alibiTrustLine = Mathf.Lerp(48f, 34f, q);
            float alibiDistrustLine = Mathf.Lerp(30f, 20f, q);
            float voteTrustLine = Mathf.Lerp(54f, 38f, q);
            float voteDistrustLine = Mathf.Lerp(35f, 24f, q);

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener.Number == _playerNumber || listener.blackboard == null) continue;

                NPCBlackboard b = listener.blackboard;
                float trust = b.GetTrust(_playerNumber);

                switch (intent)
                {
                    case PlayerIntent.Alibi:
                        if (listener.Number == 1)
                        {
                            // 自辩直接点到“我醒得最晚”，1号会本能地把它当成对自己的指控。
                            b.suspicion = Mathf.Min(100f, b.suspicion + 10f);
                            b.AddTrust(_playerNumber, -4f);
                            b.stress = Mathf.Min(100f, b.stress + 8f);
                            break;
                        }

                        // 8号 0:30 才醒、9号在2号1:00醒来前就死了——这是硬时间线。
                        // 其他人未必喜欢你，但没法否认这条事实。
                        b.AddSuspect(1);
                        b.AddSuspicionToward(1, Mathf.Lerp(2f, 10f, q));
                        b.stress = Mathf.Min(100f, b.stress + 4f);
                        if (trust >= alibiTrustLine)
                        {
                            b.suspicion = Mathf.Max(0f, b.suspicion - 8f);
                            b.AddTrust(_playerNumber, 4f);
                            b.RemoveSuspect(_playerNumber);
                            if (b.wantsPlayerDead && trust >= Mathf.Lerp(50f, 40f, q))
                            {
                                b.wantsPlayerDead = false;
                                b.hostileSince = 0f;
                                b.aggression = Mathf.Max(0f, b.aggression - 18f);
                                defused++;
                            }
                        }
                        else if (trust <= alibiDistrustLine)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 3f);
                            b.AddSuspect(_playerNumber);
                        }
                        else
                        {
                            b.suspicion = Mathf.Max(0f, b.suspicion - 3f * q);
                        }
                        break;

                    case PlayerIntent.Accuse:
                        if (accuseTarget <= 0) break;
                        if (listener.Number == accuseTarget)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 18f);
                            b.AddTrust(_playerNumber, -10f);
                            b.AddSuspectFront(_playerNumber);
                            b.meetingAccusations++;
                        }
                        else if (trust >= accuseTrustLine)
                        {
                            b.AddSuspect(accuseTarget);
                            b.AddSuspicionToward(accuseTarget, Mathf.Lerp(6f, 20f, q));
                            b.suspicion = Mathf.Min(100f, b.suspicion + 3f);
                            b.AddTrust(_playerNumber, 1f);
                            b.voteIntentNumber = accuseTarget;
                            swayed++;
                        }
                        else if (trust <= accuseDistrustLine)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 4f);
                            b.AddSuspect(_playerNumber);
                        }
                        else
                        {
                            // 半信半疑：不会立刻改票，但话已经进了他的判断。
                            b.AddSuspicionToward(accuseTarget, Mathf.Lerp(2f, 10f, q));
                            if (q >= 0.6f) b.AddSuspect(accuseTarget);
                        }
                        break;

                    case PlayerIntent.VoteLead:
                        if (accuseTarget <= 0) break;
                        if (listener.Number == accuseTarget)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 12f);
                            b.AddTrust(_playerNumber, -8f);
                            b.AddSuspectFront(_playerNumber);
                            b.meetingAccusations++;
                            break;
                        }

                        // 带票是公开的政治动作：所有人都会记住票要往哪走。
                        if (trust >= voteTrustLine)
                        {
                            b.voteIntentNumber = accuseTarget;
                            b.AddSuspect(accuseTarget);
                            b.AddSuspicionToward(accuseTarget, Mathf.Lerp(8f, 18f, q));
                            b.AddTrust(_playerNumber, 3f);
                            b.suspicion = Mathf.Max(0f, b.suspicion - 4f);
                            b.RemoveSuspect(_playerNumber);
                            swayed++;
                            if (b.wantsPlayerDead && trust >= Mathf.Lerp(55f, 42f, q))
                            {
                                b.wantsPlayerDead = false;
                                b.hostileSince = 0f;
                                b.aggression = Mathf.Max(0f, b.aggression - 10f);
                                defused++;
                            }
                        }
                        else if (trust <= voteDistrustLine)
                        {
                            // 不信任你的人不会跟你走，反而把票对准你。
                            b.voteIntentNumber = _playerNumber;
                            b.suspicion = Mathf.Min(100f, b.suspicion + 2f);
                            b.AddSuspect(_playerNumber);
                        }
                        else
                        {
                            b.voteIntentNumber = 0;
                            b.AddSuspicionToward(accuseTarget, Mathf.Lerp(2f, 8f, q));
                        }
                        break;

                    case PlayerIntent.Rebuttal:
                        b.suspicion = Mathf.Min(100f, b.suspicion + 2f);
                        if (b.knownClues != null && !b.knownClues.Contains("player_ninth_windows"))
                            b.knownClues.Add("player_ninth_windows");

                        if (trust >= Mathf.Lerp(52f, 36f, q))
                        {
                            b.AddTrust(_playerNumber, 3f);
                            b.RemoveSuspect(_playerNumber);
                        }
                        break;

                    case PlayerIntent.Timeline:
                        b.stress = Mathf.Min(100f, b.stress + 4f);
                        b.suspicion = Mathf.Min(100f, b.suspicion + 1f);
                        if (b.knownClues != null && !b.knownClues.Contains("public_timeline"))
                            b.knownClues.Add("public_timeline");

                        if (listener.Number == 1)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 10f);
                            b.AddTrust(_playerNumber, -2f);
                        }
                        else if (trust >= Mathf.Lerp(52f, 36f, q))
                        {
                            b.AddSuspect(1);
                            b.AddSuspicionToward(1, Mathf.Lerp(4f, 12f, q));
                            b.AddTrust(_playerNumber, 1.5f);
                        }
                        else if (trust <= Mathf.Lerp(45f, 30f, q))
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 3f);
                            b.AddSuspect(_playerNumber);
                        }
                        break;

                    case PlayerIntent.Demand:
                        b.stress = Mathf.Min(100f, b.stress + 6f);
                        b.suspicion = Mathf.Min(100f, b.suspicion + 2f);

                        if (listener.Number == 1)
                        {
                            b.suspicion = Mathf.Min(100f, b.suspicion + 5f);
                            b.AddTrust(_playerNumber, -3f);
                        }
                        else if (trust >= Mathf.Lerp(55f, 38f, q))
                        {
                            b.AddTrust(_playerNumber, 2f);
                            b.AddSuspect(1);
                            b.AddSuspicionToward(1, Mathf.Lerp(2f, 8f, q));
                        }
                        else if (trust <= Mathf.Lerp(45f, 32f, q))
                        {
                            b.AddSuspect(_playerNumber);
                        }
                        break;

                    case PlayerIntent.Unmask:
                        // 号召摘面具失败：被视为疯子 / 破坏者。
                        b.suspicion = Mathf.Min(100f, b.suspicion + 18f);
                        b.AddTrust(_playerNumber, -12f);
                        b.AddSuspectFront(_playerNumber);
                        b.stress = Mathf.Min(100f, b.stress + 10f);
                        break;

                    default: // 沉默
                        b.suspicion = Mathf.Min(100f, b.suspicion + 2f);
                        if (trust <= 45f) b.AddSuspect(_playerNumber);
                        break;
                }
            }

            switch (intent)
            {
                case PlayerIntent.Alibi: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 12f); break;
                case PlayerIntent.Accuse: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 3f); break;
                case PlayerIntent.VoteLead: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 6f); break;
                case PlayerIntent.Rebuttal: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 2f); break;
                case PlayerIntent.Timeline: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 5f); break;
                case PlayerIntent.Demand: _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 3f); break;
                case PlayerIntent.Unmask: _playerSuspicion = Mathf.Min(100f, _playerSuspicion + 28f); break;
                default: _playerSuspicion = Mathf.Min(100f, _playerSuspicion + 6f); break;
            }

            if (ui != null) ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
            Debug.Log($"[MeetingSystem] 玩家发言（{intent}，指控 {accuseTarget}，质量 {q:0.00}）：" +
                      $"全场怀疑值 {_playerSuspicion:0}，票向被带走 {swayed} 人，杀意被消解 {defused} 人。");
            return swayed;
        }

        // ------------------------------------------------------------------
        // 证据打断
        // ------------------------------------------------------------------

        /// <summary>
        /// NPC 发言时按 R：先选“自由发言”或“出示证据”。选完不立刻掐断他，
        /// 由 NpcTurn 等这一段说完后再执行，避免半句话被吞掉。
        /// </summary>
        IEnumerator OpenInterruptMenu()
        {
            if (!CycleState.playerAlive || !_meetingActive) yield break;

            _interruptMenuOpen = true;
            _evidenceResolving = true;
            try
            {
                NPCController speaker = _currentSpeaker;
                RefreshEvidenceItems();

                var labels = new List<string>(_evidenceIds.Count + 1)
                {
                    "直接说两句（自由发言）"
                };
                for (int i = 0; i < _evidenceIds.Count; i++)
                    labels.Add(BuildEvidenceLabel(_evidenceIds[i]));

                int selected = -1;
                bool canceled = false;
                if (ui != null)
                {
                    ui.ShowEvidence(labels.ToArray(), index => selected = index, () => canceled = true,
                        "打断他：自己说两句，或者把证据摆上桌");
                    // 复用的是证据面板，默认提示只提“选择证据”，这里改成打断语义。
                    ui.SetHint("按数字选择：直接说两句，或者出示证据；Esc 取消");
                    float waitStart = Time.realtimeSinceStartup;
                    while (!canceled && selected < 0 && _meetingActive
                           && Time.realtimeSinceStartup - waitStart < evidencePanelTimeout)
                        yield return null;

                    if (!canceled && selected < 0 && _meetingActive)
                    {
                        Debug.LogWarning("[MeetingSystem] 打断面板超过时限，自动关闭。");
                        canceled = true;
                    }
                    ui.HideEvidence();
                }
                else
                {
                    canceled = true;
                }

                if (canceled || selected < 0 || !_meetingActive)
                {
                    if (ui != null && _npcTurnActive) ShowNpcSpeakingHint();
                    yield break;
                }

                if (selected == 0)
                {
                    _pendingInterrupt = InterruptAction.FreeSpeech;
                }
                else if (selected - 1 < _evidenceIds.Count)
                {
                    _pendingInterrupt = InterruptAction.Evidence;
                    _pendingEvidenceId = _evidenceIds[selected - 1];
                    _pendingEvidenceAccuser = speaker;
                }

                if (_pendingInterrupt == InterruptAction.None)
                {
                    if (ui != null && _npcTurnActive) ShowNpcSpeakingHint();
                    yield break;
                }

                if (ui != null) ui.SetHint("他已经听见了——等他把话说完。");
            }
            finally
            {
                _interruptMenuOpen = false;
                _evidenceResolving = false;
            }
        }

        /// <summary>等 NPC 把当前这段说完后，再执行玩家选好的打断动作。</summary>
        IEnumerator ExecutePendingInterrupt(NPCController speaker)
        {
            InterruptAction action = _pendingInterrupt;
            _pendingInterrupt = InterruptAction.None;
            _currentSpeakerIsPlayer = true;

            try
            {
                if (action == InterruptAction.FreeSpeech)
                {
                    string freeText = null;
                    bool canceled = false;
                    bool opened = NpcFreeTextInputUI.Open(
                        "你要打断他说什么？",
                        "直接说出你的反驳、指控或带票。Enter 发送　·　Esc 放弃",
                        t => freeText = t,
                        () => canceled = true);
                    if (!opened) yield break;

                    while (!canceled && freeText == null && _meetingActive)
                        yield return null;

                    if (!_meetingActive) yield break;
                    if (canceled || string.IsNullOrEmpty(freeText)) yield break;

                    if (ui != null)
                    {
                        ui.SetSpeaker($"{_playerNumber} 号（你）");
                        ui.SetLine(string.Empty);
                        ui.SetHint(string.Empty);
                    }

                    yield return PlayerFreeSpeech(freeText);
                    yield break;
                }

                if (action == InterruptAction.Evidence)
                {
                    string id = _pendingEvidenceId;
                    NPCController accuser = _pendingEvidenceAccuser != null
                        ? _pendingEvidenceAccuser
                        : speaker;
                    _pendingEvidenceId = null;
                    _pendingEvidenceAccuser = null;

                    ItemDefinition evidence = GetItem(id);
                    if (evidence != null)
                        yield return PresentEvidence(evidence, accuser);
                }
            }
            finally
            {
                _currentSpeakerIsPlayer = false;
            }
        }

        IEnumerator OpenEvidencePanel()
        {
            if (!CycleState.playerAlive)
            {
                _evidenceResolving = false;
                yield break;
            }

            _evidenceResolving = true;
            try
            {
                RefreshEvidenceItems();

                if (_evidenceIds.Count == 0)
                {
                    if (ui != null) ui.SetHint("你身上没有能作为证据的东西。");
                    yield return new WaitForSeconds(1.4f);
                    if (ui != null && _npcTurnActive) ShowNpcSpeakingHint();
                    yield break;
                }

                NPCController accuser = FindLikelyAccuser();

                string[] labels = new string[_evidenceIds.Count];
                for (int i = 0; i < _evidenceIds.Count; i++)
                    labels[i] = BuildEvidenceLabel(_evidenceIds[i]);

                int selected = -1;
                bool canceled = false;
                if (ui != null)
                {
                    ui.ShowEvidence(labels, index => selected = index, () => canceled = true);
                    // 用真实时间等待：面板回调一旦丢失（UI 被关闭 / 对象被禁用），
                    // 原来无超时的循环会永远等下去，整场会议就死在 while(_evidenceResolving) 上。
                    float waitStart = Time.realtimeSinceStartup;
                    while (!canceled && selected < 0 && _meetingActive
                           && Time.realtimeSinceStartup - waitStart < evidencePanelTimeout)
                        yield return null;

                    if (!canceled && selected < 0 && _meetingActive)
                    {
                        Debug.LogWarning("[MeetingSystem] 证据面板超过时限，自动关闭。");
                        canceled = true;
                    }
                    ui.HideEvidence();
                }
                else
                {
                    canceled = true;
                }

                if (canceled || selected < 0 || !_meetingActive)
                {
                    // 取消：如果是打断 NPC，本回合已停下的发言不会恢复，
                    // NpcTurn 会看到 _skipCurrentSpeech 并结束该回合。
                    yield break;
                }

                ItemDefinition evidence = GetItem(_evidenceIds[selected]);
                if (evidence == null) yield break;

                yield return PresentEvidence(evidence, accuser);
            }
            finally
            {
                // 无论证据流程正常结束、被取消，还是中途抛异常，
                // 都必须复位：会议主循环 / NpcTurn 都靠这个标志判断是否继续。
                _evidenceResolving = false;
            }
        }

        /// <summary>把一件证据摆到桌上：切发言人、播 Yarn/兜底台词、结算 NPC 态度。</summary>
        IEnumerator PresentEvidence(ItemDefinition evidence, NPCController accuser)
        {
            bool correct = IsTruthEvidence(evidence.id);
            InjectEvidenceVariables(evidence, accuser);

            if (ui != null)
            {
                // 证据由玩家出示：先把发言人切到玩家，并清掉被打断 NPC 的残留台词。
                ui.SetSpeaker($"{_playerNumber} 号（你）");
                ui.SetLine(string.Empty);
                ui.SetHint(string.Empty);
            }

            // 节点不存在 / 已有对话在跑 / Yarn 抛异常时都返回 false，
            // 走下面的纯文本兜底——绝不能让异常从协程里冒出去把整场会议终结掉。
            if (TryStartYarnNode("Meeting_Evidence_Reveal"))
            {
                yield return WaitForDialogue();
                SyncDialogueState();
            }
            else
            {
                if (ui != null)
                {
                    ui.SetLine($"{_playerNumber}号：等一下。我有东西要给大家看——{evidence.displayName}。\n" +
                               evidence.evidenceReason);
                }
                yield return new WaitForSeconds(3.6f);

                if (correct)
                {
                    _revealedTruth = true;
                    _playerSuspicion = Mathf.Max(0f, _playerSuspicion - 18f);
                }
                else
                {
                    _playerSuspicion = Mathf.Min(100f, _playerSuspicion + 15f);
                }
                if (ui != null) ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
            }

            if (correct)
            {
                // Yarn 已写入 $revealed_truth / $player_suspicion；这里只做 NPC 态度与核心证据结算。
                _revealedTruth = true;
                ApplyEvidenceEffects(evidence, accuser);
            }
            else
            {
                ApplyWrongEvidenceEffects(evidence, accuser);
            }

            // 玩家回合中揭穿真相后，PlayerTurn 会重建选项，追加“号召摘面具”。
            if (_currentSpeakerIsPlayer && _playerOptionsVisible)
                _playerOptionsNeedRefresh = true;

            if (ui != null)
            {
                ui.SetBanner($"你出示了{evidence.displayName}：{evidence.evidenceReason}");
                if (_playerOptionsNeedRefresh)
                {
                    // 等 PlayerTurn 重建选项，避免恢复成旧的 6 项列表。
                }
                else if (_playerOptionsVisible) ui.ResumeOptionsInput();
                else if (_npcTurnActive) ShowNpcSpeakingHint();
            }
        }

        void RefreshEvidenceItems()
        {
            _evidenceIds.Clear();
            if (player == null || player.inventory == null) return;

            IReadOnlyList<string> items = player.inventory.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDefinition def = GetItem(items[i]);
                if (def == null || !def.canBeMeetingEvidence || def.evidenceWeight <= 0) continue;
                if (!_evidenceIds.Contains(def.id)) _evidenceIds.Add(def.id);
            }

            // 搜证中亲眼确认过的世界证据（墙钉、监控回放、尸体、两张一样的脸）。
            IReadOnlyList<EvidenceEntry> clues = EvidenceLedger.Entries;
            for (int i = 0; i < clues.Count; i++)
            {
                EvidenceEntry clue = clues[i];
                if (clue == null || clue.weight <= 0) continue;
                if (!_evidenceIds.Contains(clue.id)) _evidenceIds.Add(clue.id);
            }

            _evidenceIds.Sort((a, b) =>
            {
                ItemDefinition da = GetItem(a);
                ItemDefinition db = GetItem(b);
                int wa = da != null ? da.evidenceWeight : 0;
                int wb = db != null ? db.evidenceWeight : 0;
                return wb.CompareTo(wa);
            });
        }

        /// <summary>
        /// 证据列表不再只写名字：带一句短理由，玩家能分清“匕首”和“匕首上的血”。
        /// 面板一行放不下太长的说明，超出部分截断。
        /// </summary>
        string BuildEvidenceLabel(string id)
        {
            ItemDefinition def = GetItem(id);
            if (def == null) return id;

            // 名称 / 出处 / 理由必须在拼接前逐段翻译：整串「名字（出处）｜理由」
            // 既不在词典里，也拆不出模板，英文模式下会留下半中半英。
            bool en = Localization.IsEnglish;
            string origin = Localization.T(EvidenceLedger.GetOrigin(id));
            string display = Localization.T(def.displayName);

            // 每间房都有绳子 / 锤子 / 匕首 / 磁铁：不标出处，玩家根本分不清哪件是哪件。
            string name = string.IsNullOrEmpty(origin) ? display
                : (en ? $"{display} ({origin})" : $"{display}（{origin}）");

            string reason = string.IsNullOrEmpty(def.evidenceReason)
                ? string.Empty
                : def.evidenceReason.Replace("\r", " ").Replace("\n", " ").Trim();

            if (string.IsNullOrEmpty(reason) || reason == def.displayName)
                return name;

            reason = Localization.T(reason);
            int limit = en ? 52 : 22;
            if (reason.Length > limit)
            {
                int cut = limit;
                if (en)
                {
                    int space = reason.LastIndexOf(' ', cut);
                    if (space > 20) cut = space;
                }
                reason = reason.Substring(0, cut).TrimEnd() + (en ? "..." : "…");
            }
            return en ? $"{name} | {reason}" : $"{name}｜{reason}";
        }

        NPCController FindLikelyAccuser()
        {
            NPCController last = FindAttendee(_lastAccuserId);
            if (last != null) return last;

            NPCController best = null;
            float bestScore = -1f;
            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController npc = _attendees[i];
                if (npc == null) continue;
                bool suspectsPlayer = npc.blackboard.suspectNumbers != null
                    && npc.blackboard.suspectNumbers.Contains(_playerNumber);
                float score = npc.blackboard.suspicion + (suspectsPlayer ? 40f : 0f);
                if (score > bestScore)
                {
                    best = npc;
                    bestScore = score;
                }
            }
            return best;
        }

        void ApplyEvidenceEffects(ItemDefinition evidence, NPCController accuser)
        {
            float weight = evidence.evidenceWeight;

            if (accuser != null)
            {
                accuser.blackboard.suspicion = Mathf.Max(0f, accuser.blackboard.suspicion - weight * 0.6f);
                accuser.blackboard.AddTrust(_playerNumber, weight * 0.3f);
                accuser.blackboard.stress = Mathf.Min(100f, accuser.blackboard.stress + 5f);
                if (weight >= 20f) accuser.blackboard.RemoveSuspect(_playerNumber);
                _lastAccuserId = accuser.Number;
            }

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener == accuser) continue;

                listener.blackboard.suspicion = Mathf.Min(100f, listener.blackboard.suspicion + weight * 0.12f);
                listener.blackboard.AddTrust(_playerNumber, weight * 0.1f);
                if (weight >= 15f && accuser != null)
                    listener.blackboard.AddSuspect(accuser.Number);
            }

            // 模块四：只有能解释密室的正确证据才计入核心真相。
            CycleState.RecordCoreEvidence(evidence.id, Mathf.RoundToInt(weight));

            Debug.Log($"[MeetingSystem] 正确证据「{evidence.displayName}」（权重 {weight}）：" +
                      $"指控者 {(accuser != null ? accuser.Number + "号" : "未知")} 被反噬，" +
                      $"真相已揭穿，玩家怀疑值 {_playerSuspicion:0}。");
        }

        void ApplyWrongEvidenceEffects(ItemDefinition evidence, NPCController accuser)
        {
            float weight = evidence.evidenceWeight;

            if (accuser != null)
            {
                accuser.blackboard.AddTrust(_playerNumber, -4f);
                accuser.blackboard.suspicion = Mathf.Min(100f,
                    accuser.blackboard.suspicion + weight * 0.15f);
                accuser.blackboard.AddSuspectFront(_playerNumber);
                _lastAccuserId = accuser.Number;
            }

            for (int i = 0; i < _attendees.Count; i++)
            {
                NPCController listener = _attendees[i];
                if (listener == null || listener == accuser) continue;

                listener.blackboard.AddTrust(_playerNumber, -2f);
                listener.blackboard.suspicion = Mathf.Min(100f,
                    listener.blackboard.suspicion + 3f);
            }

            Debug.Log($"[MeetingSystem] 错误证据「{evidence.displayName}」（权重 {weight}）：" +
                      $"无法解释密室，玩家怀疑值升至 {_playerSuspicion:0}。");
        }

        static bool IsTruthEvidence(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;

            // 只有“这一根钉子来自1号房”才算线索：
            // 绳子 / 锤子 / 匕首 / 磁铁每个房间都有，单凭是这些东西证明不了任何事。
            if (itemId == "nail")
                return ItemProvenance.GetRoom(itemId) == 1;

            // 9号房窗台上那枚磁铁是特殊的（和鹅卵石混在一起），普通磁铁不算。
            if (itemId == "mystic_magnet") return true;

            // 公开回放、任意监控子片段与墙钉：都是可以摆到桌面上的实据。
            if (itemId == "video_log" || itemId.StartsWith("video_clip_")
                || itemId.StartsWith("wall_nails"))
                return true;

            // 玩家亲眼看见的越界行动：翻窗、钉钉、杀人。
            if (itemId.StartsWith("saw_window_") || itemId.StartsWith("saw_nail_")
                || itemId.StartsWith("saw_kill_"))
                return true;

            // 亲眼看见某人出房 / 回房：能把他放回时间线。
            if (itemId.StartsWith("saw_exit_") || itemId.StartsWith("saw_return_"))
                return true;

            // 尸体：指向“9号真的死了”，但面具本身不算真相。
            if (itemId == "ninth_body" || itemId.StartsWith("saw_body_9")) return true;
            if (itemId == "face_compare" || itemId.StartsWith("face_compare_")) return true;

            return false;
        }

        bool HasTruthEvidenceInInventory()
        {
            if (player == null || player.inventory == null) return false;

            IReadOnlyList<string> items = player.inventory.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (IsTruthEvidence(items[i])) return true;
            }
            return false;
        }

        void SyncDialogueState()
        {
            if (dialogue == null) return;

            if (dialogue.TryGetVariable("$player_suspicion", out float suspicion))
                _playerSuspicion = Mathf.Clamp(suspicion, 0f, 100f);

            if (dialogue.TryGetVariable("$revealed_truth", out bool revealed))
                _revealedTruth = revealed;

            if (ui != null) ui.SetSuspicion(Mathf.RoundToInt(_playerSuspicion));
        }

        /// <summary>
        /// 会议开场摆在大厅中央的那句话：这一轮死了谁，谁的椅子空着。
        /// 不能再出现“死人在公聊里复活”的错觉。
        /// </summary>
        static string MissingSeatText()
        {
            List<int> dead = new List<int>(CycleState.DeadNumbers);
            dead.Sort();

            if (dead.Count == 0)
                return "九把椅子都有人。没有人缺席——至少现在还没有。";

            StringBuilder sb = new StringBuilder();
            sb.Append("圆桌旁空着 ").Append(dead.Count).Append(" 把椅子：");
            for (int i = 0; i < dead.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(dead[i]).Append("号");
            }
            sb.Append("没有来。椅背上的编号还亮着，人已经不在了。");
            return sb.ToString();
        }

        /// <summary>房间的口语说法："走廊" → "the corridor"，"圆桌大厅" → "the round-table hall"。</summary>
        static string RoomPhrase(string room)
        {
            if (!Localization.IsEnglish) return room;
            if (string.IsNullOrEmpty(room) || room == "走廊") return "the corridor";
            if (room == "圆桌大厅") return "the round-table hall";
            return Localization.T(room);
        }

        /// <summary>号码的显示名：中文 "8号"，英文 "No. 8"；0 表示"没有人"。</summary>
        static string NameText(int number)
        {
            return number > 0
                ? Localization.T($"{number}号")
                : Localization.T("没有人");
        }

        static string DeadNumbersText()
        {
            List<int> dead = new List<int>(CycleState.DeadNumbers);
            dead.Sort();
            if (dead.Count == 0) return Localization.T("没有人");

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < dead.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(dead[i]).Append("号");
            }
            return Localization.T(sb.ToString());
        }

        static string DiscoveredBodyText()
        {
            List<int> found = new List<int>(CycleState.DiscoveredBodies);
            found.Sort();
            if (found.Count == 0) return Localization.T("没有人");

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < found.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(found[i]).Append("号");
            }
            return Localization.T(sb.ToString());
        }

        /// <summary>该号码的醒来读数（错峰醒来的固定时间表）。</summary>
        static string MeetingOutTime(int number)
        {
            int wake = LoopTimeline.WakeMinuteOf(number);
            return wake >= 0 ? LoopTimeline.FormatMinute(90 - wake) : Localization.T("未知");
        }

        /// <summary>该号码最近一次被监控拍到的时间；没被拍到就退回醒来读数。</summary>
        static string MeetingOutingTime(int number)
        {
            string latest = SurveillanceLog.LatestMinuteText(number);
            if (!string.IsNullOrEmpty(latest)) return latest;
            return MeetingOutTime(number);
        }

        /// <summary>给 Yarn 用的监控描述：本号码真实被拍到的外出，没有就直说。</summary>
        static string SurveillanceText(int number, string pronoun)
        {
            string text = number > 0 ? SurveillanceLog.DescribeOutingsFor(number) : "";
            if (!string.IsNullOrEmpty(text)) return text;
            if (Localization.IsEnglish)
                return pronoun == "他"
                    ? "The cameras never caught him leaving his room."
                    : "The cameras never caught me leaving my room.";
            return $"监控没有拍到{pronoun}出房门";
        }

        static ItemDefinition GetItem(string id)
        {
            // 背包物品 + 本轮亲眼确认的世界证据，统一从账本解析。
            return EvidenceLedger.Resolve(id);
        }

        // ------------------------------------------------------------------
        // Yarn 变量注入
        // ------------------------------------------------------------------

        void InjectNpcVariables(NPCController npc, int target)
        {
            if (dialogue == null) return;

            NPCBlackboard b = npc.blackboard;
            SetFloat("$speaker_number", npc.Number);
            SetString("$speaker_name", NameText(npc.Number));
            SetString("$speaker_out_time", MeetingOutTime(npc.Number));
            SetString("$speaker_surveillance", SurveillanceText(npc.Number, "我"));
            SetBool("$speaker_went_out", SurveillanceLog.OutingCount(npc.Number) > 0);
            SetBool("$player_went_out", SurveillanceLog.OutingCount(_playerNumber) > 0);
            SetString("$player_surveillance", SurveillanceText(_playerNumber, "我"));
            SetString("$accuse_surveillance", SurveillanceText(target, "他"));
            SetString("$player_timeline", SurveillanceLog.BuildReplayText());
            SetFloat("$npc_number", npc.Number);
            SetFloat("$suspect_number", target);
            SetString("$suspect_name", NameText(target));
            SetBool("$suspect_went_out", target > 0 && SurveillanceLog.OutingCount(target) > 0);
            SetBool("$is_player_suspect", target == _playerNumber);
            SetBool("$self_has_killed", b.selfHasKilled);
            SetBool("$has_note", b.hasNote);
            SetBool("$saw_ninth_body", b.sawNinthBody);
            SetFloat("$stress", b.stress);
            SetFloat("$suspicion", b.suspicion);
            SetFloat("$trust", b.trust);
            SetString("$alibi_room",
                Localization.T(string.IsNullOrEmpty(b.meetingAlibiRoom) ? "走廊" : b.meetingAlibiRoom));
            SetFloat("$player_number", _playerNumber);
            SetString("$player_name", NameText(_playerNumber));
            SetString("$player_out_time", MeetingOutTime(_playerNumber));
            SetFloat("$avg_trust", CycleState.GetAverageTrustTowardPlayer());
            SetFloat("$player_suspicion", _playerSuspicion);
            SetBool("$revealed_truth", _revealedTruth);
            SetBool("$rebuttal_ready", false);
            SetFloat("$visible_count", b.visibleTargets != null ? b.visibleTargets.Count : 0);
            SetString("$dead_numbers", DeadNumbersText());
            SetString("$found_body_numbers", DiscoveredBodyText());
            SetFloat("$dead_count", CycleState.DeadNumbers.Count);
            SetBool("$ninth_is_dead", CycleState.IsNumberDead(9));

            // 玩家回合变量复位，避免 NPC 回合的残留
            SetFloat("$accuse_number", 0);
            SetString("$accuse_name", "");
            SetFloat("$accuse_suspicion", 50f);
            ClearEvidenceVariables();
        }

        void InjectPlayerVariables(int accuseTarget)
        {
            if (dialogue == null) return;

            SetFloat("$player_number", _playerNumber);
            SetString("$player_name", NameText(_playerNumber));
            SetString("$player_out_time", MeetingOutTime(_playerNumber));
            SetFloat("$accuse_number", accuseTarget);
            SetString("$accuse_name", NameText(accuseTarget));
            SetFloat("$accuse_suspicion", accuseTarget > 0 ? _playerSuspicion : 50f);
            SetFloat("$vote_target_number", accuseTarget);
            SetString("$vote_target_name", NameText(accuseTarget));
            SetFloat("$speaker_number", _playerNumber);
            SetString("$speaker_name", NameText(_playerNumber));
            SetString("$speaker_out_time", MeetingOutTime(_playerNumber));
            SetString("$speaker_surveillance", SurveillanceText(_playerNumber, "我"));
            SetString("$player_surveillance", SurveillanceText(_playerNumber, "我"));
            SetBool("$player_went_out", SurveillanceLog.OutingCount(_playerNumber) > 0);
            SetString("$accuse_surveillance", SurveillanceText(accuseTarget, "他"));
            SetString("$player_timeline", SurveillanceLog.BuildReplayText());
            SetFloat("$npc_number", _playerNumber);
            SetFloat("$suspect_number", accuseTarget);
            SetString("$suspect_name", NameText(accuseTarget));
            SetBool("$suspect_went_out", accuseTarget > 0 && SurveillanceLog.OutingCount(accuseTarget) > 0);
            SetBool("$is_player_suspect", false);
            SetFloat("$avg_trust", CycleState.GetAverageTrustTowardPlayer());
            SetFloat("$player_suspicion", _playerSuspicion);
            SetBool("$revealed_truth", _revealedTruth);
            SetBool("$rebuttal_ready", HasTruthEvidenceInInventory() || _revealedTruth);
            SetFloat("$visible_count", _attendees.Count);
            SetString("$dead_numbers", DeadNumbersText());
            SetString("$found_body_numbers", DiscoveredBodyText());
            SetFloat("$dead_count", CycleState.DeadNumbers.Count);
            SetBool("$ninth_is_dead", CycleState.IsNumberDead(9));
            ClearEvidenceVariables();
        }

        void InjectCollectiveUnmaskVariables(int coreCount, float avgTrust, bool ready)
        {
            if (dialogue == null) return;
            SetFloat("$unmask_evidence_count", coreCount);
            SetFloat("$unmask_avg_trust", avgTrust);
            SetBool("$unmask_ready", ready);
            SetFloat("$avg_trust", avgTrust);
            SetBool("$revealed_truth", _revealedTruth);
        }

        void InjectEvidenceVariables(ItemDefinition evidence, NPCController accuser)
        {
            if (dialogue == null || evidence == null) return;

            string kind = EvidenceKindOf(evidence.id);
            string accuserName = accuser != null ? NameText(accuser.Number) : Localization.T("所有人");
            string accuserOut = accuser != null ? MeetingOutingTime(accuser.Number) : Localization.T("未知");
            string accuserAlibi = RoomPhrase(accuser != null && accuser.blackboard != null
                && !string.IsNullOrEmpty(accuser.blackboard.meetingAlibiRoom)
                ? accuser.blackboard.meetingAlibiRoom
                : "走廊");

            string origin = Localization.T(EvidenceLedger.GetOrigin(evidence.id));
            bool isNail = evidence.id == "nail" || evidence.id.StartsWith("saw_nail_");
            bool isWallMarks = evidence.id.StartsWith("wall_nails");
            bool isMagnet = evidence.id == "magnet" || evidence.id == "mystic_magnet";
            bool isRope = evidence.id == "rope";
            bool isGenericTool = (isMagnet && evidence.id == "magnet") || isRope
                || evidence.id == "hammer" || evidence.id == "dagger" || evidence.id == "bloody_dagger";

            SetString("$evidence_name", Localization.T(evidence.displayName));
            SetString("$evidence_id", evidence.id);
            SetString("$evidence_reason", Localization.T(
                string.IsNullOrEmpty(evidence.evidenceReason)
                    ? "这件东西和这层楼里发生的事脱不了干系。"
                    : evidence.evidenceReason));
            SetString("$evidence_origin", origin);
            SetBool("$evidence_has_origin", !string.IsNullOrEmpty(origin));
            SetFloat("$evidence_weight", evidence.evidenceWeight);
            SetBool("$evidence_correct", IsTruthEvidence(evidence.id));
            SetString("$evidence_kind", kind);
            SetBool("$evidence_is_observation", kind == "observation");
            SetBool("$evidence_is_identity", kind == "identity");
            SetBool("$evidence_is_video", kind == "video");
            SetBool("$evidence_is_body", kind == "body");
            SetBool("$evidence_is_nail", isNail);
            SetBool("$evidence_is_wall_marks", isWallMarks);
            SetBool("$evidence_is_magnet", isMagnet);
            SetBool("$evidence_is_rope", isRope);
            SetBool("$evidence_is_generic_tool", isGenericTool);
            SetBool("$evidence_points_at_accused", false);
            SetString("$accuser_name", accuserName);
            SetString("$accuser_out_time", accuserOut);
            SetString("$accuser_alibi", accuserAlibi);
            SetString("$accuser_surveillance",
                accuser != null ? SurveillanceText(accuser.Number, "我") : "");
            string accuserOutings = accuser != null
                ? SurveillanceLog.DescribeOutingsFor(accuser.Number) : "";
            string accuserClaim;
            if (accuser == null)
                accuserClaim = Localization.IsEnglish
                    ? "No one admits to going anywhere near Room 9."
                    : "没有人承认自己靠近过9号房。";
            else if (string.IsNullOrEmpty(accuserOutings))
                accuserClaim = Localization.IsEnglish
                    ? $"The surveillance never caught me leaving my room. I only went to {accuserAlibi}, and I never went near Room 9."
                    : $"监控没拍到我出房门，我只去了{accuserAlibi}，没有靠近9号房。";
            else
                accuserClaim = Localization.IsEnglish
                    ? $"The surveillance caught me {accuserOutings}. I only went to {accuserAlibi}, and I never went near Room 9."
                    : $"监控拍到我{accuserOutings}，我只去了{accuserAlibi}，没有靠近9号房。";
            SetString("$accuser_claim", accuserClaim);
            SetFloat("$accuser_number", accuser != null ? accuser.Number : 0);
            SetFloat("$accuser_stress", accuser != null && accuser.blackboard != null
                ? accuser.blackboard.stress : 50f);
            SetFloat("$accuser_trust", accuser != null && accuser.blackboard != null
                ? accuser.blackboard.GetTrust(_playerNumber) : 50f);
            SetFloat("$accuser_suspicion", accuser != null && accuser.blackboard != null
                ? accuser.blackboard.suspicion : 50f);
            SetString("$speaker_name", accuserName);
            SetString("$speaker_surveillance",
                accuser != null ? SurveillanceText(accuser.Number, "我") : "");
            SetString("$player_surveillance", SurveillanceText(_playerNumber, "我"));
            SetString("$player_timeline", SurveillanceLog.BuildReplayText());
            SetFloat("$npc_number", accuser != null ? accuser.Number : 0);
            SetFloat("$player_number", _playerNumber);
            SetString("$player_name", NameText(_playerNumber));
            SetString("$player_out_time", MeetingOutTime(_playerNumber));
            SetFloat("$player_suspicion", _playerSuspicion);
            SetFloat("$avg_trust", CycleState.GetAverageTrustTowardPlayer());
            SetBool("$revealed_truth", _revealedTruth);
        }

        /// <summary>证据类型决定 Yarn 的回应方式，避免“门被拴上”答非所问。</summary>
        static string EvidenceKindOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "physical";
            if (id == "face_compare" || id.StartsWith("face_compare_")
                || id == "faces_identical")
                return "identity";
            if (id == "ninth_body" || id.StartsWith("body_")
                || id.StartsWith("saw_body_")) return "body";
            if (id == "video_log" || id.StartsWith("video_clip_")) return "video";
            if (id.StartsWith("saw_") || id.StartsWith("timeline_")) return "observation";
            return "physical";
        }

        void ClearEvidenceVariables()
        {
            SetString("$evidence_name", "");
            SetString("$evidence_id", "");
            SetString("$evidence_reason", "");
            SetString("$evidence_origin", "");
            SetBool("$evidence_has_origin", false);
            SetFloat("$evidence_weight", 0f);
            SetBool("$evidence_correct", false);
            SetString("$evidence_kind", "physical");
            SetBool("$evidence_is_observation", false);
            SetBool("$evidence_is_identity", false);
            SetBool("$evidence_is_video", false);
            SetBool("$evidence_is_body", false);
            SetBool("$evidence_is_nail", false);
            SetBool("$evidence_is_wall_marks", false);
            SetBool("$evidence_is_magnet", false);
            SetBool("$evidence_is_rope", false);
            SetBool("$evidence_is_generic_tool", false);
            SetBool("$evidence_points_at_accused", false);
            SetString("$accuser_name", "");
            SetString("$accuser_out_time", "");
            SetString("$accuser_alibi", "");
            SetString("$accuser_claim", "");
            SetFloat("$accuser_number", 0f);
            SetFloat("$accuser_stress", 50f);
            SetFloat("$accuser_trust", 50f);
            SetFloat("$accuser_suspicion", 50f);
        }

        void SetFloat(string name, float value) => dialogue.SetVariable(name, value);
        void SetString(string name, string value) => dialogue.SetVariable(name, value);
        void SetBool(string name, bool value) => dialogue.SetVariable(name, value);

        // ------------------------------------------------------------------
        // 台词兜底（Yarn 未装配 / 编译失败时仍能走完流程）
        // ------------------------------------------------------------------

        string BuildFallbackSpeech(NPCController npc, int target)
        {
            if (npc.Number == 9) return BuildNinthFallbackSpeech(target);
            if (npc.Number == 10) return BuildTenthFallbackSpeech(target);

            NPCBlackboard b = npc.blackboard;
            bool knowsNinth = NinthDeathKnown(b);
            bool en = Localization.IsEnglish;
            StringBuilder sb = new StringBuilder();

            // 逐段本地化后再拼接。监控描述在英文模式下本身就是英文
            // （"0:20 left the room, visited the lobby..."），把整段中文 + 英文混在一起
            // 再交给 Localization，既命中不了词典、也没法按标点拆开，会留下半句中文。
            Say(sb, Localization.T(
                $"{npc.Number}号：我醒来时，屏幕上的倒计时停在{MeetingOutTime(npc.Number)}。" +
                "那不是钟，是这一轮剩下的时间。"));

            string ownOutings = SurveillanceLog.DescribeOutingsFor(npc.Number);
            if (!string.IsNullOrEmpty(ownOutings))
                Say(sb, Localization.T("监控拍到了我：") + ownOutings + (en ? "." : "。"));
            else
                Say(sb, Localization.T("监控没拍到我出房门——这不代表我一直待在房里，只是我走的路不在镜头里。"));

            if (knowsNinth)
                Say(sb, Localization.T(b.selfHasKilled
                    ? "9号已经死了。他断气的时候，我就在隔壁，听见窗外有动静。"
                    : "9号已经死了。他出事的时候，我只听见风雪。"));
            else
                Say(sb, Localization.T("9号一直没露面。他可能只是没醒，也可能出了别的事——我不知道。"));

            if (b.sawNinthBody)
                Say(sb, Localization.T("我亲眼见过他的尸体，那不是意外。"));

            if (target == _playerNumber)
            {
                string playerOutings = SurveillanceLog.DescribeOutingsFor(_playerNumber);
                if (string.IsNullOrEmpty(playerOutings))
                {
                    Say(sb, Localization.T(
                        $"{_playerNumber}号，监控没拍到你出房门，那你醒着的时候都在哪？"));
                }
                else
                {
                    Say(sb, Localization.T(
                        $"{_playerNumber}号，监控拍到你{playerOutings}。那段时间，谁替你作证？"));
                }
            }
            else if (target > 0)
            {
                string targetOutings = SurveillanceLog.DescribeOutingsFor(target);
                if (string.IsNullOrEmpty(targetOutings))
                {
                    Say(sb, Localization.T(
                        $"我怀疑{target}号。监控没拍到他出房门，但这不代表他一直待在房里。"));
                }
                else
                {
                    Say(sb, Localization.T(
                        $"我怀疑{target}号：监控拍到他{targetOutings}，他说不清那段时间去了哪。"));
                }
            }
            else
            {
                Say(sb, Localization.T("我暂时没有证据，但我不相信任何人。"));
            }

            return sb.ToString();
        }

        /// <summary>拼接已本地化的句子；英文句号后补空格，中文句号后不补。</summary>
        static void Say(StringBuilder sb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (sb.Length > 0)
            {
                char last = sb[sb.Length - 1];
                char first = text[0];
                if ((last == '.' || last == '!' || last == '?' || last == '"')
                    && !char.IsWhiteSpace(first))
                {
                    sb.Append(' ');
                }
            }
            sb.Append(text);
        }

        /// <summary>NPC 是否已经确认 9号死亡（亲眼见过尸体或拿到过尸体线索）。</summary>
        static bool NinthDeathKnown(NPCBlackboard b)
        {
            if (b == null) return false;
            if (b.sawNinthBody) return true;
            return b.knownClues != null &&
                   (b.knownClues.Contains("ninth_body") || b.knownClues.Contains("saw_ninth_body"));
        }

        string BuildNinthFallbackSpeech(int target)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("9号：我本该在你们醒来之前就死了——可我在 0:05 睁开了眼。");
            sb.Append("有人站在我床边，面具上的数字是「1」。他没来得及下手，或者他改了主意。");

            if (target == _playerNumber)
                sb.Append($"{_playerNumber}号，就是你。你站在我的床边，手里拿着刀。");
            else if (target == 1)
                sb.Append("我怀疑 1号。只有他有那半个小时，也只有他知道我什么时候不会醒。");
            else
                sb.Append("我不知道该信谁，但我知道——这个循环已经多出了一个人。");

            return sb.ToString();
        }

        string BuildTenthFallbackSpeech(int target)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("10号：我没有房间，也没有自己的床。醒来时我躺在圆桌旁的地板上。");
            sb.Append("我记得……我本来应该是 9号。有人没有杀死 9号，于是这个循环把我挤了出来。");

            if (target == _playerNumber)
                sb.Append($"{_playerNumber}号，如果 9号没有死，那么该负责的人就是你。");
            else if (target == 1)
                sb.Append("我怀疑 1号。9号没死，循环就多出了一个人——而 1号是唯一被允许动手的人。");
            else
                sb.Append("我连自己为什么在这里都不知道。但我不想再当那个被多出来的人。");

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 镜头
        // ------------------------------------------------------------------

        void EnsureMeetingCamera()
        {
            if (meetingCamera != null)
            {
                meetingCamera.enabled = false;
                return;
            }

            GameObject go = new GameObject("MeetingCamera");
            go.transform.SetParent(transform, false);
            meetingCamera = go.AddComponent<Camera>();
            meetingCamera.fieldOfView = 55f;
            meetingCamera.nearClipPlane = 0.05f;
            meetingCamera.farClipPlane = 80f;
            meetingCamera.depth = 20f;
            meetingCamera.clearFlags = CameraClearFlags.SolidColor;
            meetingCamera.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
            meetingCamera.enabled = false;
        }

        Vector3 TableCenter => meetingArea != null ? meetingArea.position : Vector3.zero;

        Vector3 OverviewPosition()
        {
            return TableCenter + new Vector3(0f, 2.9f, -2.9f);
        }

        Vector3 OverviewLookAt()
        {
            return TableCenter + Vector3.up * 0.65f;
        }

        void MoveCameraImmediate(Vector3 position, Vector3 lookAt)
        {
            if (meetingCamera == null) return;
            meetingCamera.enabled = true;
            meetingCamera.transform.position = position;
            Vector3 dir = lookAt - position;
            if (dir.sqrMagnitude > 0.0001f)
                meetingCamera.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        IEnumerator MoveCameraToSpeaker(NPCController npc)
        {
            if (meetingCamera == null || npc == null) yield break;

            Vector3 speaker = npc.transform.position;
            Vector3 dir = speaker - TableCenter;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            dir.Normalize();

            Vector3 camPos = TableCenter + Vector3.up * 1.9f + dir * 0.2f;
            Vector3 lookAt = speaker + Vector3.up * 0.25f;
            yield return MoveCamera(camPos, lookAt, 0.4f);
        }

        IEnumerator MoveCameraToOverview()
        {
            yield return MoveCamera(OverviewPosition(), OverviewLookAt(), 0.45f);
        }

        IEnumerator MoveCamera(Vector3 position, Vector3 lookAt, float duration)
        {
            if (meetingCamera == null) yield break;

            meetingCamera.enabled = true;
            Vector3 fromPos = meetingCamera.transform.position;
            Quaternion fromRot = meetingCamera.transform.rotation;
            Vector3 dir = lookAt - position;
            Quaternion toRot = dir.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                : fromRot;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                meetingCamera.transform.position = Vector3.Lerp(fromPos, position, k);
                meetingCamera.transform.rotation = Quaternion.Slerp(fromRot, toRot, k);
                yield return null;
            }

            meetingCamera.transform.position = position;
            meetingCamera.transform.rotation = toRot;
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        /// <summary>
        /// 启动 Yarn 节点，并把「节点不存在 / 已有对话在跑 / Yarn 内部异常」
        /// 统一收成 false，让调用方走纯文本兜底，而不是让异常终止整个会议协程。
        /// </summary>
        bool TryStartYarnNode(string node)
        {
            if (dialogue == null || !dialogue.HasRunner || string.IsNullOrEmpty(node)) return false;

            try
            {
                if (dialogue.IsRunning)
                {
                    // 残留对话会静默吞掉新的 StartDialogue，之后 WaitForDialogue 就会干等。
                    Debug.LogWarning($"[MeetingSystem] 启动 {node} 前发现残留对话，先停止。");
                    dialogue.StopDialogue();
                }

                dialogue.StartDialogue(node);
                return dialogue.IsRunning;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MeetingSystem] 启动 Yarn 节点 {node} 失败：{e.Message}");
                return false;
            }
        }

        IEnumerator WaitForDialogue()
        {
            if (dialogue == null) yield break;

            // 用真实时间等待：调试时 Time.timeScale 会被调高（10~20 倍），
            // 若这里累加缩放时间，45 秒的看门狗会缩成 4.5 秒，长台词还没播完
            // 就被判定结束，下一位发言者会撞上「已有对话在运行」。
            float startWait = 0f;
            while (!dialogue.IsRunning && startWait < 1f)
            {
                startWait += Time.unscaledDeltaTime;
                yield return null;
            }

            float guard = 0f;
            while (dialogue.IsRunning && guard < 120f)
            {
                guard += Time.unscaledDeltaTime;
                yield return null;
            }

            if (dialogue.IsRunning)
            {
                Debug.LogWarning("[MeetingSystem] 单段发言超过 120 秒仍未结束，强制停止对话。");
                dialogue.StopDialogue();

                float stopWait = 0f;
                while (dialogue.IsRunning && stopWait < 2f)
                {
                    stopWait += Time.unscaledDeltaTime;
                    yield return null;
                }
            }
        }

        NPCController FindAttendee(int number)
        {
            for (int i = 0; i < _attendees.Count; i++)
            {
                if (_attendees[i] != null && _attendees[i].Number == number) return _attendees[i];
            }
            return null;
        }

        Transform FindChair(int number)
        {
            if (number == 10) EnsureChair10();

            GameObject chair = GameObject.Find("Chair_" + number);
            return chair != null ? chair.transform : null;
        }

        /// <summary>
        /// 10号没有自己的场景资产：第一次需要时就地克隆 Chair_9，
        /// 插在 9号（50°）和 1号（90°）之间，并把椅背数字改成 10。
        /// </summary>
        void EnsureChair10()
        {
            if (GameObject.Find("Chair_10") != null) return;

            GameObject template = GameObject.Find("Chair_9");
            if (template == null)
            {
                Debug.LogWarning("[MeetingSystem] 找不到 Chair_9，无法为 10号生成临时座椅。");
                return;
            }

            GameObject chair = Instantiate(template, template.transform.parent);
            chair.name = "Chair_10";

            float angle = 70f * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            chair.transform.localPosition = dir * 1.75f;
            chair.transform.localRotation = Quaternion.LookRotation(-dir);

            Transform label = chair.transform.Find("ChairNumber_9");
            if (label != null)
            {
                label.name = "ChairNumber_10";
                TextMeshPro tmp = label.GetComponent<TextMeshPro>();
                if (tmp != null)
                {
                    tmp.text = "10";
                    ChineseFontUtility.Apply(tmp);
                }
            }

            Debug.Log("[MeetingSystem] 已为 10号在 9号与 1号之间临时生成 Chair_10。");
        }
    }
}
