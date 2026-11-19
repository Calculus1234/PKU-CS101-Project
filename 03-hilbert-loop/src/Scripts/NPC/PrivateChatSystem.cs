using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Yarn.Unity;

namespace HilbertLoop
{
    /// <summary>
    /// 私聊系统（模块四）：
    /// - 搜证阶段，玩家对 2 米内的存活 NPC 按 E 发起私聊（E 只做友善交互，持武器也一样）；
    /// - 私聊运行 Yarn 节点 Chat_NPC_1 ~ Chat_NPC_8（内容见 Template_PrivateChat.yarn）；
    /// - 玩家可寒暄 / 试探 / 出示证据：每个话题对同一 NPC 本轮只结算一次；
    /// - 提升该 NPC 对玩家的信任，并记录核心真相证据，为结局 C / D 做铺垫。
    /// </summary>
    public class PrivateChatSystem : MonoBehaviour
    {
        public static PrivateChatSystem Instance { get; private set; }

        [Header("引用（场景构建器装配；留空运行时自找 / 自建）")]
        public PlayerController player;
        public PrivateChatUI ui;
        public YarnProject yarnProject;

        [Header("交互距离")]
        public float chatDistance = 2f;

        [Header("信任增益")]
        public float reassureTrust = 4f;
        public float probeTrust = 6f;
        public float evidenceBaseTrust = 6f;
        public float evidenceTrustPerWeight = 0.35f;
        public float coreEvidenceBonusTrust = 3f;

        [Header("本地大模型")]
        [Tooltip("模型就绪时，私聊台词与工具调用交给 LLM；失败/超时自动回退 Yarn")]
        public bool useLlmDialogue = true;
        [Tooltip("等待模型首句的软上限：超过先播兜底台词，模型继续后台生成，完成后补一句")]
        public float llmSoftDeadline = 3f;

        readonly List<string> _evidenceIds = new List<string>();
        readonly List<Action> _mainActions = new List<Action>();
        readonly Dictionary<int, HashSet<string>> _usedTopics =
            new Dictionary<int, HashSet<string>>();
        readonly List<NPCController> _frozenNpcs = new List<NPCController>();

        DialogueBridge _dialogue;
        PrivateChatDialoguePresenter _presenter;
        NPCController _npc;
        bool _active;
        bool _dialogueComplete;
        int _usedLoopIndex = -1;
        int _turnId;

        class PendingLlm
        {
            public NpcLLMReply reply;
            public bool finished;
        }

        public bool IsActive => _active;
        public NPCController CurrentNpc => _npc;

        bool LlmReady => useLlmDialogue
                         && LLMNpcBrain.Instance != null
                         && LLMNpcBrain.Instance.IsReady;

        void Awake()
        {
            Instance = this;
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (ui == null) ui = FindObjectOfType<PrivateChatUI>(true);

            if (ui == null)
            {
                GameObject uiGo = new GameObject("PrivateChatHUD");
                uiGo.transform.SetParent(transform, false);
                ui = uiGo.AddComponent<PrivateChatUI>();
            }

            BuildDialogueRunner();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!_active) return;

            if (!CycleState.playerAlive || EndingSystem.Instance != null && EndingSystem.Instance.IsEnding)
            {
                ForceClose();
                return;
            }

            if (GameManager.Instance != null
                && GameManager.Instance.CurrentPhase != GamePhase.Investigation)
            {
                // 会议 / 投票开始：安全退出私聊，把控制权交回流程。
                EndChat();
            }
        }

        // ------------------------------------------------------------------
        // 发起 / 结束
        // ------------------------------------------------------------------

        /// <summary>玩家是否可以在当前时刻与指定 NPC 私聊（2 米内、搜证阶段、双方存活）。</summary>
        public static bool CanChat(PlayerController player, NPCController npc)
        {
            if (Instance == null || Instance._active) return false;
            if (player == null || npc == null) return false;
            if (!CycleState.playerAlive || CycleState.playerNumber <= 0) return false;
            if (npc.isDead || !npc.IsAwake || npc.InMeeting || npc.InPrivateChat) return false;
            if (GameManager.Instance == null
                || GameManager.Instance.CurrentPhase != GamePhase.Investigation) return false;

            // 1号翻出窗外执行流程时不在可交谈范围：隔着墙 / 玻璃喊话没有意义。
            NPCAssassin assassin = npc.GetComponent<NPCAssassin>();
            if (assassin != null && assassin.IsOnExteriorRoute) return false;

            float distance = Vector3.Distance(player.transform.position, npc.transform.position);
            return distance <= Instance.chatDistance;
        }

        public bool TryStartChat(NPCController npc)
        {
            if (npc != null)
            {
                NPCAssassin assassin = npc.GetComponent<NPCAssassin>();
                if (assassin != null && assassin.IsOnExteriorRoute)
                {
                    if (player != null)
                        player.ShowMessage($"{npc.Number}号正贴着外墙，隔着玻璃听不见你。", 3f);
                    return false;
                }
            }

            if (!CanChat(player, npc)) return false;
            if (_dialogue != null && _dialogue.IsRunning) return false;

            if (_usedLoopIndex != CycleState.loopIndex)
            {
                _usedLoopIndex = CycleState.loopIndex;
                _usedTopics.Clear();
            }

            _active = true;
            _npc = npc;
            _dialogueComplete = false;
            _turnId++;

            // 私聊不是“玩家单方面被锁住”：交谈双方和附近的人一起停下来。
            FreezeNearby(npc);

            if (player != null)
            {
                player.inputLocked = true;
                player.movementLocked = true;
                player.SetCursorLocked(false);
                // 私聊面板是半透明全屏，探索 HUD 的背包 / 准星 / 短消息会透过面板叠字。
                player.SetHudVisible(false);
                if (player.interaction != null)
                {
                    player.interaction.enabled = false;
                    if (player.interaction.promptText != null)
                        player.interaction.promptText.gameObject.SetActive(false);
                }
            }

            if (ui != null)
            {
                ui.SetEndAction(EndChat);
                ui.SetVisible(true);
                ui.SetSpeaker($"{npc.Number} 号");
                ui.SetEmotion(npc.Number, npc.blackboard.GetTrust(CycleState.playerNumber),
                    npc.blackboard.suspicion, npc.blackboard.stress, npc.blackboard.aggression, true);
                ui.SetLine("……");
                ui.SetHint("正在靠近对方……");
            }

            SetNpcVariables();
            StartCoroutine(ConversationFlow());
            Debug.Log($"[PrivateChat] 与 {npc.Number} 号开始私聊（距离 " +
                      $"{Vector3.Distance(player.transform.position, npc.transform.position):0.00} m）。");
            return true;
        }

        IEnumerator ConversationFlow()
        {
            yield return RunNpcDialogue(string.Empty, null,
                $"Chat_NPC_{_npc.Number}", BuildFallbackGreeting());

            if (!_active) yield break;
            ShowMainMenu();
        }

        /// <summary>正常结束：恢复玩家控制。</summary>
        public void EndChat()
        {
            if (!_active) return;
            CloseInternal(true);
        }

        /// <summary>被结局 / 死亡强制中断：不恢复控制（由结局系统冻结）。</summary>
        public void ForceClose()
        {
            if (!_active) return;
            CloseInternal(false);
        }

        void CloseInternal(bool restorePlayer)
        {
            _active = false;
            NpcFreeTextInputUI.Close(false);
            NpcThinkingDisguise.EndAll();
            LLMNpcBrain.Instance?.Abort();
            StopAllCoroutines();

            if (_dialogue != null && _dialogue.IsRunning) _dialogue.StopDialogue();
            if (ui != null) ui.CloseImmediate();

            UnfreezeNearby();

            if (restorePlayer && player != null && CycleState.playerAlive)
            {
                // 结束私聊的这次 Esc 只属于弹窗：同帧别再触发“释放光标”。
                player.ConsumeEscapeThisFrame();
                player.inputLocked = false;
                player.movementLocked = false;
                player.SetCursorLocked(true);
                if (player.interaction != null) player.interaction.enabled = true;
                // 只在正常结束时恢复探索 HUD；强制中断（会议 / 死亡 / 结局）由各自流程决定。
                player.SetHudVisible(true);
            }

            // 聊完给玩家留出喘息时间：否则冷却在“走过来之前”就排好，
            // 一次长聊结束时早已过期，同一个 NPC 会立刻又贴上来。
            NPCSocialAgent.NotifyChatEnded(_npc);

            Debug.Log($"[PrivateChat] 私聊结束（{(restorePlayer ? "正常" : "强制中断")}）：" +
                      $"{(player != null ? "玩家 " + CycleState.playerNumber + " 号" : "无玩家")}。");
            _npc = null;
        }

        void FreezeNearby(NPCController partner)
        {
            _frozenNpcs.Clear();

            if (partner != null)
            {
                partner.EnterPrivateChatMode();
                _frozenNpcs.Add(partner);
            }

            // 私聊期间全场 NPC 一起停下：玩家被锁在对话里时，
            // 任何人在视野里走动都会破坏“时间被冻结”的错觉。
            // 结束后 UnfreezeNearby 原样恢复。
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc == partner || npc.isDead) continue;
                if (npc.InMeeting || npc.InPrivateChat) continue;

                npc.EnterPrivateChatMode();
                _frozenNpcs.Add(npc);
            }
        }

        void UnfreezeNearby()
        {
            for (int i = 0; i < _frozenNpcs.Count; i++)
            {
                if (_frozenNpcs[i] != null) _frozenNpcs[i].ExitPrivateChatMode();
            }
            _frozenNpcs.Clear();
        }

        // ------------------------------------------------------------------
        // 私聊菜单
        // ------------------------------------------------------------------

        void ShowMainMenu()
        {
            if (!_active || ui == null || _npc == null) return;

            SetNpcVariables();

            NPCBlackboard b = _npc.blackboard;
            float trust = b.GetTrust(CycleState.playerNumber);
            float stress = b.stress;

            RefreshEvidenceItems();
            bool hasEvidence = _evidenceIds.Count > 0;
            string caseId;
            bool hasCase = TryGetCaseAgainstOne(out caseId);
            string topEvidence = hasEvidence ? _evidenceIds[0] : null;

            // 菜单不再是固定七项：先摆“对方想不想杀你”“你手上有什么证据”，
            // 再由证据生成具体问题。选项上限仍是 6 项 + 结束，适配 7 格 UI。
            List<string> labels = new List<string>();
            _mainActions.Clear();

            // 自由发言永远是第一项：不受固定话题限制，玩家可以自己组织
            // 指控、辩解、试探或谈条件。本地模型还没加载完时也能用，
            // FreeSpeechRoutine 里有确定性的兜底回答，不会让入口消失。
            labels.Add("（自由发言）我想自己说……");
            _mainActions.Add(OpenFreeSpeech);

            bool playerUnderThreat = b.wantsPlayerDead
                || b.suspicion >= 50f
                || (b.suspectNumbers != null && b.suspectNumbers.Contains(CycleState.playerNumber));
            if (playerUnderThreat)
            {
                labels.Add(b.wantsPlayerDead
                    ? "（求饶）我知道你想让我死，但先听我说完"
                    : "（自辩）你们都在怀疑我，先听我说完");
                _mainActions.Add(() => StartCoroutine(PleaRoutine()));
            }

            if (hasCase)
            {
                labels.Add(_npc.Number == 1
                    ? "（摊牌）我看见你翻出窗外，去了9号房"
                    : "（摊牌）我知道 1 号做了什么，你听我说");
                _mainActions.Add(() => StartCoroutine(ShowdownRoutine(caseId)));
            }

            if (hasEvidence)
            {
                ItemDefinition top = GetItem(topEvidence);
                labels.Add($"（追问）{ShortName(top)}——你怎么解释？");
                _mainActions.Add(() => StartCoroutine(EvidenceQuestionRoutine(topEvidence)));
            }

            labels.Add(hasEvidence ? "（出示证据）看看这个。" : "（出示证据）我手上没有东西。");
            if (hasEvidence) _mainActions.Add(OpenEvidencePanel);
            else _mainActions.Add(() => StartCoroutine(HintThenMenu("你身上没有可以出示的东西。")));

            string probe = trust >= 70f
                ? "（追问）把你知道的都告诉我。"
                : trust >= 40f
                    ? "（追问）我想知道几件事。"
                    : "（追问）我只问一句。";
            labels.Add(probe);
            _mainActions.Add(ShowProbeMenu);

            if (trust >= 65f)
            {
                labels.Add("（直接）你醒来后，杀过人吗？");
                _mainActions.Add(() => StartCoroutine(TopicRoutine("direct", "Chat_Private_Direct",
                    -2f, 9f, "你醒来后，杀过人吗？")));
            }
            else if (trust >= 40f)
            {
                labels.Add("（试探）你觉得谁最可疑？");
                _mainActions.Add(() => StartCoroutine(TopicRoutine("suspect", "Chat_Private_Suspect",
                    2f, 4f, "你觉得谁最可疑？")));
            }
            else
            {
                labels.Add("（质问）你出门的时候，走廊里还有谁？");
                _mainActions.Add(() => StartCoroutine(TopicRoutine("confront", "Chat_Private_Confront",
                    1f, 5f, "你出门的时候，走廊里还有谁？")));
            }

            // 寒暄是低优先级：有求饶 / 摊牌 / 证据问题时给它让位。
            if (labels.Count < 6)
            {
                string reassure = trust >= 60f
                    ? "（寒暄）你也在害怕吗？"
                    : stress >= 70f
                        ? "（寒暄）你的手在抖。"
                        : "（寒暄）这栋房子里，你还睡得着吗？";
                labels.Add(reassure);
                _mainActions.Add(() => StartCoroutine(TopicRoutine("reassure", "Chat_Private_Reassure",
                    reassureTrust, -3f, "你也在害怕吗？")));
            }

            if (labels.Count > 6)
            {
                labels.RemoveRange(6, labels.Count - 6);
                _mainActions.RemoveRange(6, _mainActions.Count - 6);
            }

            labels.Add("结束交谈");

            ui.SetEmotion(_npc.Number, trust, b.suspicion, stress, b.aggression, true);
            ui.ShowOptions(labels.ToArray(), OnMainOption);
            ui.SetHint("数字键选择　·　Esc 结束私聊");
        }

        void OnMainOption(int index)
        {
            if (!_active) return;

            if (index >= 0 && index < _mainActions.Count)
            {
                _turnId++;
                if (ui != null) ui.HideOptions();
                _mainActions[index]?.Invoke();
                return;
            }

            EndChat();
        }

        // ------------------------------------------------------------------
        // LLM：自由发言 / 动态台词
        // ------------------------------------------------------------------

        void OpenFreeSpeech()
        {
            if (!_active || _npc == null) return;
            // 模型还在加载时也允许玩家开口；没有模型时不编造回答，
            // 只让 NPC 保持沉默，模型就绪后自动接管。

            bool opened = NpcFreeTextInputUI.Open(
                $"{_npc.Number} 号就在你面前",
                "直接说你想说的话。Enter 发送　·　Esc 取消",
                text =>
                {
                    if (_active) StartCoroutine(FreeSpeechRoutine(text));
                    else ShowMainMenu();
                },
                ShowMainMenu);

            if (!opened) ShowMainMenu();
        }

        IEnumerator FreeSpeechRoutine(string text)
        {
            if (!_active || _npc == null) yield break;

            string line = (text ?? "").Trim();
            if (string.IsNullOrEmpty(line))
            {
                ShowMainMenu();
                yield break;
            }

            if (ui != null)
            {
                ui.SetHint("他似乎在斟酌词句……");
                ui.SetLine($"你：{line}");
            }
            NpcConversationMemory.AddPrivate(_npc.Number,
                $"玩家{CycleState.playerNumber}号：{line}");

            ItemDefinition mentioned = FindMentionedEvidence(line);

            // 1.5B 经常漏掉 ChangeStat：玩家把证据和时间线说得再清楚，数值也纹丝不动。
            // 这里按发言质量给一次确定性的态度变化，保证“说得有理”一定有回响。
            ApplyPersuasionFallback(PlayerSpeechEvaluator.Score(line), mentioned != null);

            int turn = _turnId;
            if (LlmReady)
            {
                SetNpcVariables();
                NpcContext ctx = NpcContextBuilder.BuildPrivate(_npc, line, mentioned);
                ctx.sceneNote = "玩家是自由发言，可能试探、求饶、指控或谈条件。只回应他实际说到的内容。";
                yield return RunFreeSpeechLlm(ctx, BuildFreeSpeechFallback(), turn);
            }
            else
            {
                // 模型未就绪：不生成任何预设问答，只留一个沉默的停顿。
                string fallback = BuildFreeSpeechFallback();
                if (ui != null)
                {
                    ui.SetLine(fallback);
                    ui.SetHint("他没有接话。");
                }
                NpcConversationMemory.AddPrivate(_npc.Number, fallback);
                yield return new WaitForSecondsRealtime(2.2f);
            }

            if (!_active) yield break;
            RefreshEmotion();
            ShowMainMenu();
        }

        /// <summary>
        /// 自由发言的确定性结算：模型给不给 ChangeStat，玩家说得具体都该有反应。
        /// 有证据、有号码、有时间线 → 信任上升、怀疑下降；空泛或骂人 → 反向。
        /// </summary>
        void ApplyPersuasionFallback(float quality, bool showingEvidence)
        {
            if (_npc == null || _npc.blackboard == null) return;

            NPCBlackboard b = _npc.blackboard;
            int player = CycleState.playerNumber;

            float trustGain;
            float suspicionDrop;
            if (quality >= 0.75f) { trustGain = 6f; suspicionDrop = 8f; }
            else if (quality >= 0.5f) { trustGain = 3f; suspicionDrop = 4f; }
            else if (quality >= 0.3f) { trustGain = 1f; suspicionDrop = 1f; }
            else { trustGain = -1f; suspicionDrop = -2f; }

            if (showingEvidence) { trustGain += 2f; suspicionDrop += 4f; }

            b.AddTrust(player, trustGain);
            b.suspicion = Mathf.Clamp(b.suspicion - suspicionDrop, 0f, 100f);

            if (b.wantsPlayerDead && quality >= 0.8f && b.GetTrust(player) >= 45f)
            {
                b.wantsPlayerDead = false;
                b.hostileSince = 0f;
                b.aggression = Mathf.Max(0f, b.aggression - 10f);
            }

            Debug.Log($"[PrivateChat] 玩家发言质量 {quality:0.00}（证据={showingEvidence}）：" +
                      $"信任{trustGain:+#;-#;0}，怀疑{-suspicionDrop:+#;-#;0}，当前信任 {b.GetTrust(player):0}。");
        }

        /// <summary>
        /// 自由发言专用：最多等 llmSoftDeadline 秒；超时先播兜底台词，
        /// 模型在后台继续生成，完成后如果玩家还没选下一句，再把真正的回答补上。
        /// </summary>
        IEnumerator RunFreeSpeechLlm(NpcContext ctx, string fallback, int turn)
        {
            PendingLlm pending = new PendingLlm();
            bool anyPartial = false;

            Action<string> onPartial = p =>
            {
                if (!_active || turn != _turnId || ui == null || _npc == null) return;
                string clean = NpcToolCallParser.SanitizeSpeech(p);
                clean = NpcConversationMemory.StripOutOfWorld(clean);
                clean = NpcConversationMemory.StripSelfEchoStreaming(
                    _npc.Number, NpcLLMScene.PrivateChat, clean);
                if (string.IsNullOrEmpty(clean)) return;

                anyPartial = true;
                ui.SetStreamingLine($"{_npc.Number}号：{clean}");
                ui.SetHint("他斟酌着开口。");
            };

            // 3 秒还没有任何字：只给“正在斟酌”的提示，不再塞预设台词占位。
            NpcThinkingDisguise.Begin(_npc, 3f, () =>
            {
                if (!_active || turn != _turnId || ui == null || _npc == null || anyPartial) return;
                ui.SetHint("他沉默着，似乎在斟酌词句……");
            });

            StartCoroutine(LLMNpcBrain.Instance.Generate(ctx,
                r => { pending.reply = r; pending.finished = true; }, onPartial));

            float start = Time.realtimeSinceStartup;
            while (!pending.finished
                   && Time.realtimeSinceStartup - start < Mathf.Max(0.5f, llmSoftDeadline))
                yield return null;

            if (!_active || turn != _turnId)
            {
                NpcThinkingDisguise.End(_npc);
                yield break;
            }

            if (pending.finished)
            {
                NpcThinkingDisguise.End(_npc);
                if (pending.reply != null && pending.reply.success)
                {
                    yield return PlayLlmReply(pending.reply, true);
                    yield break;
                }

                // 模型这一回合直接失败：不编造回答，只留一个沉默的停顿。
                if (ui != null)
                {
                    ui.SetLine(fallback);
                    ui.SetHint("他停顿了一下，没有接话。");
                }
                NpcConversationMemory.AddPrivate(_npc.Number, fallback);
                yield return new WaitForSecondsRealtime(2.2f);
                yield break;
            }

            // 后台还在生成：屏幕上已经有流式台词或结巴占位，先把菜单还给玩家。
            // 真正的结果出来后再补一句；玩家一旦选了别的话题就作废。
            StartCoroutine(LateLlmReply(pending, turn, true));
        }

        IEnumerator LateLlmReply(PendingLlm pending, int turn, bool showHint)
        {
            while (!pending.finished) yield return null;
            if (!_active || turn != _turnId || _npc == null)
            {
                NpcThinkingDisguise.End(_npc);
                yield break;
            }

            NpcLLMReply reply = pending.reply;
            if (reply == null || !reply.success)
            {
                NpcThinkingDisguise.End(_npc);
                yield break;
            }

            NpcThinkingDisguise.End(_npc);
            if (ui != null) ui.SetHint("他沉默了几秒，又补了一句。");
            // 提示已经写过，别再被 PlayLlmReply 的默认提示覆盖。
            yield return PlayLlmReply(reply, false);
            if (!_active || turn != _turnId) yield break;
            ShowMainMenu();
        }

        /// <summary>固定话题也先交给 LLM；3 秒内没答完就取消并回退 Yarn，避免卡住对话。</summary>
        IEnumerator RunNpcDialogue(string playerLine, ItemDefinition evidence,
            string node, string fallback, string sceneNote = null)
        {
            if (!_active) yield break;

            // 不管走模型还是走固定 Yarn，玩家这句话都要进记忆；
            // 否则模型只知道这次说了什么，后面再提同一件事就对不上。
            if (!string.IsNullOrEmpty(playerLine))
            {
                NpcConversationMemory.AddPrivate(_npc.Number,
                    $"玩家{CycleState.playerNumber}号：{playerLine}");
            }

            if (LlmReady)
            {
                SetNpcVariables();
                NpcContext ctx = NpcContextBuilder.BuildPrivate(_npc, playerLine, evidence);
                ctx.sceneNote = sceneNote ?? "";

                PendingLlm pending = new PendingLlm();
                StartCoroutine(LLMNpcBrain.Instance.Generate(ctx,
                    r => { pending.reply = r; pending.finished = true; }));

                float start = Time.realtimeSinceStartup;
                while (!pending.finished
                       && Time.realtimeSinceStartup - start < Mathf.Max(0.5f, llmSoftDeadline))
                    yield return null;

                if (!_active) yield break;
                if (pending.finished && pending.reply != null && pending.reply.success)
                {
                    yield return PlayLlmReply(pending.reply, false);
                    yield break;
                }

                // 固定话题的兜底走 Yarn（会结算信任/变量），因此不等后台结果。
                if (!pending.finished) LLMNpcBrain.Instance.Abort();
            }

            yield return RunNode(node, fallback);
        }

        IEnumerator PlayLlmReply(NpcLLMReply reply, bool showHint)
        {
            if (!_active || _npc == null) yield break;

            if (ui != null) ui.HideOptions();
            string clean = NpcToolCallParser.SanitizeSpeech(reply.speech);
            clean = NpcConversationMemory.StripOutOfWorld(clean);
            clean = NpcConversationMemory.StripSelfEcho(_npc.Number, NpcLLMScene.PrivateChat, clean);
            if (NpcConversationMemory.IsSilent(clean))
                clean = NpcConversationMemory.FallbackLine(_npc.Number, NpcLLMScene.PrivateChat);

            if (ui != null)
            {
                ui.SetLine($"{_npc.Number}号：{clean}");
                if (showHint) ui.SetHint("他斟酌着开口。");
            }
            NpcConversationMemory.AddPrivate(_npc.Number, $"{_npc.Number}号：{clean}");
            NpcVoiceRouter.Speak(_npc, clean);

            NpcToolExecutionResult result =
                NpcToolExecutor.Execute(_npc, reply, NpcLLMScene.PrivateChat);

            float wait = Mathf.Clamp(1.1f + clean.Length * 0.055f, 1.6f, 4.4f);
            yield return new WaitForSecondsRealtime(wait);
            if (!_active) yield break;

            if (!string.IsNullOrEmpty(result.extraLine))
            {
                if (ui != null) ui.SetLine($"{_npc.Number}号：{result.extraLine}");
                NpcConversationMemory.AddPrivate(_npc.Number,
                    $"{_npc.Number}号：{result.extraLine}");
                NpcVoiceRouter.Speak(_npc, result.extraLine);
                yield return new WaitForSecondsRealtime(2.4f);
            }

            if (!_active) yield break;
            if (result.presentedEvidence && ui != null)
                ui.SetHint("他把一件东西摆到了你们之间。");
            else if (result.voteTarget > 0 && ui != null)
                ui.SetHint("他第一次把票意说得很明白。");

            RefreshEmotion();
        }

        void RefreshEmotion()
        {
            if (ui == null || _npc == null) return;
            NPCBlackboard b = _npc.blackboard;
            ui.SetEmotion(_npc.Number, b.GetTrust(CycleState.playerNumber),
                b.suspicion, b.stress, b.aggression, true);
        }

        ItemDefinition FindMentionedEvidence(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            RefreshEvidenceItems();

            for (int i = 0; i < _evidenceIds.Count; i++)
            {
                ItemDefinition def = GetItem(_evidenceIds[i]);
                if (def == null) continue;
                if (text.Contains(def.displayName)) return def;
                if (!string.IsNullOrEmpty(def.id) && text.Contains(def.id)) return def;
            }

            bool pointsAtSomething = text.Contains("证据") || text.Contains("这个")
                                     || text.Contains("这件") || text.Contains("看看")
                                     || text.Contains("给你看") || text.Contains("你解释");
            if (pointsAtSomething && _evidenceIds.Count > 0)
                return GetItem(_evidenceIds[0]);

            return null;
        }

        string BuildPleaPlayerLine(bool hasCase, string caseId)
        {
            string line = "我知道你想让我死，但先听我说完。9号不是我杀的，我醒来之后只出过一次门。";
            ItemDefinition item = hasCase ? GetItem(caseId) : null;
            if (item != null)
            {
                string origin = EvidenceLedger.GetOrigin(item.id);
                line += $"我知道1号做了什么：{item.displayName}" +
                        (string.IsNullOrEmpty(origin) ? "" : $"（{origin}）") +
                        "。杀9号的人就在我们中间。";
            }
            return line;
        }

        string BuildShowdownPlayerLine(ItemDefinition evidence, bool againstSelf)
        {
            string origin = EvidenceLedger.GetOrigin(evidence.id);
            string where = string.IsNullOrEmpty(origin) ? "" : $"（{origin}）";
            if (againstSelf)
                return $"我看见你翻出窗外，去了9号房。{evidence.displayName}{where}，" +
                       "你要怎么解释？";

            return $"我知道1号做了什么。{evidence.displayName}{where}。" +
                   $"{evidence.evidenceReason} 你还打算替他说话吗？";
        }

        string BuildEvidenceQuestionPlayerLine(ItemDefinition evidence)
        {
            string origin = EvidenceLedger.GetOrigin(evidence.id);
            string where = string.IsNullOrEmpty(origin) ? "" : $"（{origin}）";
            return $"{evidence.displayName}{where}，你怎么解释？{evidence.evidenceReason}";
        }

        string BuildEvidencePresentPlayerLine(ItemDefinition evidence)
        {
            string origin = EvidenceLedger.GetOrigin(evidence.id);
            string where = string.IsNullOrEmpty(origin) ? "" : $"（{origin}）";
            return $"看看这个。{evidence.displayName}{where}。{evidence.evidenceReason}";
        }

        /// <summary>
        /// 模型不可用时的唯一兜底：不编造任何回答内容。
        /// 以前这里按关键词拼“应答”，玩家一听就知道是预设台词；
        /// 现在只留一个沉默的停顿，让 NPC 保持“没接话”的状态。
        /// </summary>
        string BuildFreeSpeechFallback()
        {
            if (_npc == null) return "……";
            return $"{_npc.Number}号：……";
        }

        static string ShortName(ItemDefinition def)
        {
            if (def == null) return "这件东西";
            string name = def.displayName;
            return string.IsNullOrEmpty(name) ? "这件东西" : name;
        }

        /// <summary>玩家手里有没有能指到 1号 身上的东西（翻窗、墙钉、监控、1号房的钉子）。</summary>
        bool TryGetCaseAgainstOne(out string evidenceId)
        {
            evidenceId = null;
            for (int i = 0; i < _evidenceIds.Count; i++)
            {
                if (IsCaseAgainstOne(_evidenceIds[i]))
                {
                    evidenceId = _evidenceIds[i];
                    return true;
                }
            }
            return false;
        }

        static bool IsCaseAgainstOne(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id == "video_log" || id == "video_clip_1" || id.StartsWith("video_clip_1_")) return true;
            if (id.StartsWith("wall_nails")) return true;
            if (id.StartsWith("saw_window_")) return true;
            if (id.StartsWith("saw_nail_1_")) return true;
            if (id == "nail") return ItemProvenance.GetRoom(id) == 1;
            if (id == "mystic_magnet") return true;
            return false;
        }

        /// <summary>
        /// 追问子菜单：不再是一个笼统的“试探”，而是让玩家自己选择问什么。
        /// 所有问题都只针对“这一轮醒来之后”，没有人拥有昨天的记忆。
        /// </summary>
        void ShowProbeMenu()
        {
            if (!_active || ui == null || _npc == null) return;

            ui.SetEmotion(_npc.Number,
                _npc.blackboard.GetTrust(CycleState.playerNumber),
                _npc.blackboard.suspicion, _npc.blackboard.stress,
                _npc.blackboard.aggression, true);
            ui.ShowOptions(new[]
            {
                "你醒来时，房间里有什么不对？",
                "醒来之后，你离开过房间吗？",
                "你听见走廊里有什么声音吗？",
                "你房间里的东西，少了什么吗？",
                "如果9号不是唯一会死的人，你会怎么做？",
                "先问到这里。",
            }, OnProbeOption);
            ui.SetHint("数字键选择　·　Esc 结束私聊");
        }

        void OnProbeOption(int index)
        {
            if (!_active) return;

            switch (index)
            {
                case 0:
                    StartCoroutine(TopicRoutine("probe_wake", "Chat_Probe_Wake",
                        3f, 3f, "你醒来时，房间里有什么不对？"));
                    break;
                case 1:
                    StartCoroutine(TopicRoutine("probe_move", "Chat_Probe_Move",
                        2f, 5f, "醒来之后，你离开过房间吗？"));
                    break;
                case 2:
                    StartCoroutine(TopicRoutine("probe_sound", "Chat_Probe_Sound",
                        2f, 4f, "你听见走廊里有什么声音吗？"));
                    break;
                case 3:
                    StartCoroutine(TopicRoutine("probe_item", "Chat_Probe_Item",
                        2f, 4f, "你房间里的东西，少了什么吗？"));
                    break;
                case 4:
                    StartCoroutine(TopicRoutine("probe_threat", "Chat_Probe_Threat",
                        1f, 6f, "如果9号不是唯一会死的人，你会怎么做？"));
                    break;
                default:
                    ShowMainMenu();
                    break;
            }
        }

        /// <summary>
        /// 求饶 / 消解杀意：这是“知道所有人都想杀 8号”时唯一能活下来的谈判手段。
        /// 成功与否取决于信任、当前怀疑值，以及玩家是否握着能反咬 1号 的东西。
        /// </summary>
        IEnumerator PleaRoutine()
        {
            if (!_active || _npc == null) yield break;

            NPCBlackboard b = _npc.blackboard;
            float trust = b.GetTrust(CycleState.playerNumber);
            string caseId;
            bool hasCase = TryGetCaseAgainstOne(out caseId);

            float threshold = _npc.Number == 1 ? 55f : 45f;
            if (b.suspicion >= 80f) threshold += 10f;
            if (hasCase) threshold -= 10f;

            bool success = trust >= threshold;
            ItemDefinition caseEvidence = hasCase ? GetItem(caseId) : null;
            SetNpcVariables();
            if (caseEvidence != null) SetChatEvidenceVariables(caseEvidence);
            if (_dialogue != null)
            {
                _dialogue.SetVariable("$chat_plea_success", success);
                _dialogue.SetVariable("$chat_plea_threshold", threshold);
            }

            if (ui != null) ui.SetHint(string.Empty);
            yield return RunNpcDialogue(BuildPleaPlayerLine(hasCase, caseId), caseEvidence,
                "Chat_Private_Plea", BuildPleaFallback(success),
                "玩家在向你求饶，试图让你放弃杀他的念头。是否被说服取决于你对他的信任和当前怀疑，" +
                "不要直接说出任何数值；可以松口，也可以继续逼问。");
            if (!_active) yield break;

            if (success)
            {
                b.wantsPlayerDead = false;
                b.hostileSince = 0f;
                b.aggression = Mathf.Max(0f, b.aggression - 30f);
                b.suspicion = Mathf.Max(0f, b.suspicion - 20f);
                b.RemoveSuspect(CycleState.playerNumber);
                if (b.voteIntentNumber == CycleState.playerNumber) b.voteIntentNumber = 0;
                b.AddTrust(CycleState.playerNumber, 8f);
                CycleState.privateChatCount++;
                if (ui != null)
                    ui.SetHint("对方握紧的手松开了。至少现在，他不想杀你。");
            }
            else
            {
                b.aggression = Mathf.Min(100f, b.aggression + 15f);
                b.stress = Mathf.Min(100f, b.stress + 10f);
                b.AddTrust(CycleState.playerNumber, -2f);
                if (ui != null)
                    ui.SetHint("对方没有回答。你听见他握紧了什么东西。");
            }

            if (ui != null)
                ui.SetEmotion(_npc.Number, b.GetTrust(CycleState.playerNumber),
                    b.suspicion, b.stress, b.aggression, true);

            Debug.Log($"[PrivateChat] 求饶{(success ? "成功" : "失败")}：{_npc.Number}号 " +
                      $"trust={trust:0}/{threshold:0}，wantsPlayerDead={b.wantsPlayerDead}。");

            if (caseEvidence != null) ClearChatEvidenceVariables();
            ShowMainMenu();
        }

        /// <summary>
        /// 摊牌：把 1号 的翻窗路线摆到对方面前。
        /// 对 1号 本人是压迫；对其他 NPC 是“我替你找到了凶手”。
        /// </summary>
        IEnumerator ShowdownRoutine(string caseId)
        {
            if (!_active || _npc == null) yield break;

            ItemDefinition evidence = GetItem(caseId);
            if (evidence == null) { ShowMainMenu(); yield break; }

            NPCBlackboard b = _npc.blackboard;
            bool againstSelf = _npc.Number == 1;
            float trust = b.GetTrust(CycleState.playerNumber);

            SetNpcVariables();
            SetChatEvidenceVariables(evidence);
            if (_dialogue != null)
                _dialogue.SetVariable("$chat_showdown_against_self", againstSelf);

            if (ui != null)
            {
                ui.SetHint(string.Empty);
                ui.SetLine(string.Empty);
            }

            yield return RunNpcDialogue(
                BuildShowdownPlayerLine(evidence, againstSelf), evidence,
                "Chat_Private_Accuse_1", BuildShowdownFallback(evidence, againstSelf),
                againstSelf
                    ? "玩家当面指出你翻出窗外去了9号房，并且手里有东西。绝不能承认；可以反咬、质疑证据来源，或者威胁他。"
                    : "玩家把矛头指向1号，想让你相信1号杀了9号。你可以被说动，也可以怀疑玩家在借刀杀人。");
            if (!_active) yield break;

            if (againstSelf)
            {
                b.stress = Mathf.Min(100f, b.stress + 22f);
                b.aggression = Mathf.Min(100f, b.aggression + 10f);
                b.AddTrust(CycleState.playerNumber, -8f);
                b.suspicion = Mathf.Min(100f, b.suspicion + 8f);
                b.AddSuspectFront(CycleState.playerNumber);
                if (ui != null)
                    ui.SetHint("他没有承认。但他记住了你知道这件事。");
            }
            else
            {
                bool believes = trust >= 45f || evidence.evidenceWeight >= 40;
                if (believes)
                {
                    b.AddSuspectFront(1);
                    b.AddTrust(1, -10f);
                    b.AddTrust(CycleState.playerNumber, 5f);
                    b.suspicion = Mathf.Max(0f, b.suspicion - 8f);
                    if (ui != null)
                        ui.SetHint("他把这件事记下了。第一次，他没有把目光落在你身上。");
                }
                else
                {
                    b.AddTrust(CycleState.playerNumber, -4f);
                    b.suspicion = Mathf.Min(100f, b.suspicion + 6f);
                    if (ui != null)
                        ui.SetHint("“这只能说明你盯着1号。”他后退了半步。");
                }
            }

            if (ui != null)
                ui.SetEmotion(_npc.Number, b.GetTrust(CycleState.playerNumber),
                    b.suspicion, b.stress, b.aggression, true);

            ClearChatEvidenceVariables();
            ShowMainMenu();
        }

        /// <summary>
        /// 由玩家手上的证据生成的问题：不是固定菜单里的“试探”，
        /// 而是直接问这件东西和对方有什么关系。
        /// </summary>
        IEnumerator EvidenceQuestionRoutine(string itemId)
        {
            if (!_active || _npc == null) yield break;

            ItemDefinition evidence = GetItem(itemId);
            if (evidence == null) { ShowMainMenu(); yield break; }

            NPCBlackboard b = _npc.blackboard;
            bool firstTime = MarkTopic("ask:" + itemId);

            SetNpcVariables();
            SetChatEvidenceVariables(evidence);

            if (ui != null)
            {
                ui.SetHint(string.Empty);
                ui.SetLine(string.Empty);
            }

            yield return RunNpcDialogue(
                BuildEvidenceQuestionPlayerLine(evidence), evidence,
                "Chat_Private_Evidence_Question", BuildEvidenceQuestionFallback(evidence),
                "玩家在追问这件东西和你有什么关系。只回答这件东西，不要扯到门闩或别的话题；" +
                "如果它确实指向你，就辩解或质疑来源。");
            if (!_active) yield break;

            if (firstTime)
            {
                if (IsCaseAgainstOne(itemId) && _npc.Number == 1)
                {
                    b.stress = Mathf.Min(100f, b.stress + 12f);
                    b.AddTrust(CycleState.playerNumber, -3f);
                    if (ui != null) ui.SetHint("他的回答太快了，像是早就准备好的。");
                }
                else if (IsCaseAgainstOne(itemId))
                {
                    b.AddSuspect(1);
                    b.AddTrust(CycleState.playerNumber, 2f);
                    b.suspicion = Mathf.Max(0f, b.suspicion - 5f);
                    if (ui != null) ui.SetHint("他顺着你的话，第一次认真想起了1号。");
                }
                else
                {
                    b.AddTrust(CycleState.playerNumber, 1f);
                    if (ui != null) ui.SetHint("他答得很含糊。至少他知道你手里有东西。");
                }
            }
            else if (ui != null)
            {
                ui.SetHint("这个问题你已经问过了。");
            }

            ClearChatEvidenceVariables();
            ShowMainMenu();
        }

        IEnumerator TopicRoutine(string topicKey, string node, float trustGain,
                                 float stressDelta, string fallbackTopic)
        {
            if (!_active) yield break;

            bool firstTime = MarkTopic(topicKey);
            SetNpcVariables();
            if (ui != null) ui.SetHint(string.Empty);

            yield return RunNpcDialogue(fallbackTopic, null, node, BuildFallbackTopic(fallbackTopic),
                "玩家在问一个具体问题。只回答这个问题，不要背固定台词，也不要主动扯到别的话题。");
            if (!_active) yield break;

            if (firstTime)
            {
                ApplyTrust(trustGain, stressDelta);
                if (ui != null)
                    ui.SetHint(stressDelta >= 0f ? "对方的呼吸有些乱。" : "对方似乎放松了一点。");
            }
            else if (ui != null)
            {
                ui.SetHint("这个话题已经聊过了，再说也不会改变什么。");
            }

            ShowMainMenu();
        }

        void OpenEvidencePanel()
        {
            if (!_active) return;
            RefreshEvidenceItems();

            if (_evidenceIds.Count == 0)
            {
                StartCoroutine(HintThenMenu("你身上没有可以出示的东西。"));
                return;
            }

            string[] labels = new string[_evidenceIds.Count];
            for (int i = 0; i < _evidenceIds.Count; i++)
                labels[i] = BuildEvidenceLabel(_evidenceIds[i]);

            ui.ShowEvidence(labels, index =>
            {
                if (index >= 0 && index < _evidenceIds.Count)
                    StartCoroutine(EvidenceRoutine(_evidenceIds[index]));
            }, ShowMainMenu);
        }

        IEnumerator HintThenMenu(string text)
        {
            if (ui != null) ui.SetHint(text);
            yield return new WaitForSecondsRealtime(1.2f);
            if (_active) ShowMainMenu();
        }

        IEnumerator EvidenceRoutine(string itemId)
        {
            if (!_active) yield break;

            ItemDefinition evidence = GetItem(itemId);
            if (evidence == null) { ShowMainMenu(); yield break; }

            bool firstTime = MarkTopic("evidence:" + itemId);
            bool core = evidence.evidenceWeight >= CycleState.CoreEvidenceWeightThreshold;
            CycleState.RecordCoreEvidence(evidence.id, evidence.evidenceWeight);

            SetNpcVariables();
            SetChatEvidenceVariables(evidence);

            if (ui != null)
            {
                ui.SetHint(string.Empty);
                ui.SetLine(string.Empty);
            }

            yield return RunNpcDialogue(
                BuildEvidencePresentPlayerLine(evidence), evidence,
                "Chat_Private_Evidence",
                $"{_npc.Number}号：……{evidence.displayName}。你从哪弄来的？\n" +
                $"{evidence.evidenceReason}",
                "玩家把一件证据摆到你面前。根据这件东西是否真的指向你，决定否认、解释还是反咬；" +
                "不要编造你没有见过的证据。");
            if (!_active) yield break;

            ApplySpecialEvidenceEffects(itemId);

            if (firstTime)
            {
                float gain = evidenceBaseTrust
                             + evidence.evidenceWeight * evidenceTrustPerWeight
                             + (core ? coreEvidenceBonusTrust : 0f);
                ApplyTrust(gain, 5f);
                _npc.blackboard.suspicion =
                    Mathf.Max(0f, _npc.blackboard.suspicion - evidence.evidenceWeight * 0.25f);
                if (ui != null)
                    ui.SetHint(core
                        ? "对方沉默了一会儿。他看你的眼神，第一次不像在看一个凶手。"
                        : "证据起作用了。对方看你的眼神松动了一点。");
            }
            else if (ui != null)
            {
                ui.SetHint("这件证据你已经给他看过了。");
            }

            ClearChatEvidenceVariables();
            ShowMainMenu();
        }

        /// <summary>
        /// 世界证据不只是一段对话：它会把 NPC 的认知真正往前推。
        /// 尤其是“两张一样的脸”——这是顿悟链条上不可跳过的一环。
        /// </summary>
        void ApplySpecialEvidenceEffects(string itemId)
        {
            if (_npc == null || _npc.blackboard == null || string.IsNullOrEmpty(itemId)) return;

            NPCBlackboard b = _npc.blackboard;
            if (itemId == "face_compare" || itemId == "mask_9" || itemId == "faces_identical")
            {
                b.knowsFacesIdentical = true;
                if (itemId == "mask_9") b.sawNinthMaskOff = true;
                if (CycleState.playerDisguiseRemoved) b.knowsPlayerFace = true;
                if (!b.knownClues.Contains("faces_identical")) b.knownClues.Add("faces_identical");
                b.stress = Mathf.Min(100f, b.stress + 25f);
            }
            else if (itemId == "video_log")
            {
                if (!b.knownClues.Contains("video_log")) b.knownClues.Add("video_log");
                b.AddSuspect(1);
                b.AddTrust(1, -8f);
            }
            else if (itemId.StartsWith("video_clip_"))
            {
                if (!b.knownClues.Contains(itemId)) b.knownClues.Add(itemId);
                if (itemId.StartsWith("video_clip_1_")) b.AddSuspect(1);
                b.stress = Mathf.Min(100f, b.stress + 8f);
            }
            else if (itemId.StartsWith("saw_window_") || itemId.StartsWith("saw_nail_"))
            {
                if (!b.knownClues.Contains("player_saw_window_route"))
                    b.knownClues.Add("player_saw_window_route");
                b.AddSuspect(1);
                b.stress = Mathf.Min(100f, b.stress + 18f);
            }
            else if (itemId.StartsWith("saw_kill_"))
            {
                if (!b.knownClues.Contains("player_saw_kill"))
                    b.knownClues.Add("player_saw_kill");
                b.stress = Mathf.Min(100f, b.stress + 30f);
                b.panicUntil = Mathf.Max(b.panicUntil, Time.time + 8f);
            }
            else if (itemId == "saw_ninth_face_off")
            {
                b.sawNinthMaskOff = true;
                if (!b.knownClues.Contains("ninth_face")) b.knownClues.Add("ninth_face");
                b.stress = Mathf.Min(100f, b.stress + 15f);
            }
            else if (itemId.StartsWith("saw_body_"))
            {
                if (itemId == "saw_body_9") b.sawNinthBody = true;
                if (!b.knownClues.Contains(itemId)) b.knownClues.Add(itemId);
                b.stress = Mathf.Min(100f, b.stress + 12f);
            }
            else if (itemId.StartsWith("wall_nails"))
            {
                if (!b.knownClues.Contains("wall_nails")) b.knownClues.Add("wall_nails");
            }
            else if (itemId == "ninth_body" || itemId == "body_9")
            {
                b.sawNinthBody = true;
                if (!b.knownClues.Contains("ninth_body")) b.knownClues.Add("ninth_body");
            }
            else if (itemId.StartsWith("body_"))
            {
                if (!b.knownClues.Contains(itemId)) b.knownClues.Add(itemId);
            }
        }

        // ------------------------------------------------------------------
        // 效果 / 记录
        // ------------------------------------------------------------------

        void ApplyTrust(float trustGain, float stressDelta)
        {
            if (_npc == null) return;

            _npc.blackboard.AddTrust(CycleState.playerNumber, trustGain);
            _npc.blackboard.stress = Mathf.Clamp(_npc.blackboard.stress + stressDelta, 0f, 100f);
            if (trustGain > 0f) CycleState.privateChatTrustGain += trustGain;
            CycleState.privateChatCount++;

            Debug.Log($"[PrivateChat] {_npc.Number} 号信任 +{trustGain:0}（当前 " +
                      $"{_npc.blackboard.GetTrust(CycleState.playerNumber):0}），" +
                      $"压力 {_npc.blackboard.stress:0}。本轮私聊 {CycleState.privateChatCount} 次。");
        }

        bool MarkTopic(string key)
        {
            if (_npc == null) return false;
            int number = _npc.Number;
            if (!_usedTopics.TryGetValue(number, out HashSet<string> used))
            {
                used = new HashSet<string>();
                _usedTopics[number] = used;
            }

            if (used.Contains(key)) return false;
            used.Add(key);
            return true;
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

            // 搜证中亲眼确认过的世界证据（墙钉 / 监控回放 / 尸体 / 两张一样的脸）
            // 也能在私聊里说出来，不需要真的塞进背包。
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

        string BuildEvidenceLabel(string id)
        {
            ItemDefinition def = GetItem(id);
            if (def == null) return id;

            // 名称 / 出处 / 理由必须在拼接前逐段翻译：整串「名字（出处）｜理由」
            // 既不在词典里，也拆不出模板，英文模式下会留下半中半英。
            bool en = Localization.IsEnglish;
            string origin = Localization.T(EvidenceLedger.GetOrigin(id));
            string display = Localization.T(def.displayName);
            string name = string.IsNullOrEmpty(origin) ? display
                : (en ? $"{display} ({origin})" : $"{display}（{origin}）");

            string reason = string.IsNullOrEmpty(def.evidenceReason)
                ? string.Empty
                : def.evidenceReason.Replace("\r", " ").Replace("\n", " ").Trim();

            if (string.IsNullOrEmpty(reason) || reason == def.displayName)
                return name;

            reason = Localization.T(reason);
            int limit = en ? 52 : 20;
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

        static ItemDefinition GetItem(string id)
        {
            // 背包物品走 ItemDatabase，世界证据走 EvidenceLedger；
            // Resolve 会先查数据库，再回退到本轮记录。
            return EvidenceLedger.Resolve(id);
        }

        // ------------------------------------------------------------------
        // Yarn
        // ------------------------------------------------------------------

        void BuildDialogueRunner()
        {
            if (yarnProject == null)
            {
                DialogueBridge bridge = FindObjectOfType<DialogueBridge>();
                if (bridge != null && bridge.dialogueRunner != null)
                    yarnProject = bridge.dialogueRunner.YarnProject;
            }

            if (yarnProject == null)
            {
                Debug.LogError("[PrivateChat] 缺少 YarnProject，私聊将退化为占位台词。");
                return;
            }

            GameObject dialogueGo = new GameObject("PrivateChatDialogue");
            dialogueGo.transform.SetParent(transform, false);

            InMemoryVariableStorage storage = dialogueGo.AddComponent<InMemoryVariableStorage>();
            DialogueRunner runner = dialogueGo.AddComponent<DialogueRunner>();
            runner.VariableStorage = storage;

            _presenter = dialogueGo.AddComponent<PrivateChatDialoguePresenter>();
            _presenter.chat = this;
            runner.DialoguePresenters = new DialoguePresenterBase[] { _presenter };

            _dialogue = dialogueGo.AddComponent<DialogueBridge>();
            _dialogue.dialogueRunner = runner;

            runner.SetProject(yarnProject);

            Debug.Log($"[PrivateChat] Yarn 私聊运行器已装配（project={yarnProject.name}）。");
        }

        IEnumerator RunNode(string node, string fallback)
        {
            if (!_active) yield break;

            _dialogueComplete = false;
            bool started = _dialogue != null && _dialogue.HasRunner;

            if (started)
            {
                _dialogue.StartDialogue(node);

                float guard = 0f;
                while (!_dialogueComplete && guard < 30f && _active)
                {
                    guard += Time.unscaledDeltaTime;
                    yield return null;
                }

                if (guard >= 30f)
                    Debug.LogWarning($"[PrivateChat] 节点 {node} 超时未结束。");
            }
            else
            {
                if (ui != null) ui.SetLine(fallback);
                RememberNpcLine(fallback);
                yield return new WaitForSecondsRealtime(3f);
            }

        }

        /// <summary>把 NPC 说出口的固定台词写进记忆，避免下一轮“像是不记得自己说过”。</summary>
        void RememberNpcLine(string text)
        {
            if (_npc == null || string.IsNullOrEmpty(text)) return;
            string entry = text.Contains("号：") ? text : $"{_npc.Number}号：{text}";
            NpcConversationMemory.AddPrivate(_npc.Number, entry);
        }

        public void ShowLine(string text)
        {
            if (ui != null) ui.SetLine(text);
            // Yarn 固定话题的台词同样要入记忆：模型后续才能接上自己刚说过的话。
            RememberNpcLine(text);
        }

        public void HandleDialogueComplete()
        {
            _dialogueComplete = true;
        }

        void SetNpcVariables()
        {
            if (_dialogue == null || _npc == null) return;

            int playerNumber = CycleState.playerNumber;
            float trust = _npc.blackboard.GetTrust(playerNumber);
            float stress = _npc.blackboard.stress;

            _dialogue.SetVariable("$chat_npc_number", _npc.Number);
            _dialogue.SetVariable("$chat_npc_name", Localization.T($"{_npc.Number}号"));
            _dialogue.SetVariable("$chat_npc_surveillance",
                SurveillanceTextFor(_npc.Number, "我"));
            _dialogue.SetVariable("$chat_npc_went_out",
                SurveillanceLog.OutingCount(_npc.Number) > 0);
            _dialogue.SetVariable("$chat_player_surveillance",
                SurveillanceTextFor(playerNumber, "我"));
            _dialogue.SetVariable("$chat_one_surveillance",
                SurveillanceTextFor(1, "他"));
            _dialogue.SetVariable("$chat_npc_trust", trust);
            _dialogue.SetVariable("$chat_npc_stress", stress);
            _dialogue.SetVariable("$chat_npc_suspicion", _npc.blackboard.suspicion);
            _dialogue.SetVariable("$chat_npc_wants_dead", _npc.blackboard.wantsPlayerDead);
            _dialogue.SetVariable("$chat_npc_aggression", _npc.blackboard.aggression);
            _dialogue.SetVariable("$chat_player_number", playerNumber);
            _dialogue.SetVariable("$chat_player_name", Localization.T($"{playerNumber}号"));

            // 玩家亲眼看见过的窗外路线：只在私聊里作为“你敢不敢问”的底气。
            bool sawWindow = EvidenceLedger.Has("saw_window_out_1")
                             || EvidenceLedger.Has("saw_window_in_1");
            bool sawNail = false;
            IReadOnlyList<EvidenceEntry> entries = EvidenceLedger.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && entries[i].id.StartsWith("saw_nail_1_"))
                {
                    sawNail = true;
                    break;
                }
            }
            _dialogue.SetVariable("$chat_player_saw_window", sawWindow);
            _dialogue.SetVariable("$chat_player_saw_nail", sawNail);

            RefreshEvidenceItems();
            string caseId;
            bool hasCase = TryGetCaseAgainstOne(out caseId);
            ItemDefinition caseDef = hasCase ? GetItem(caseId) : null;
            _dialogue.SetVariable("$chat_has_case_against_1", hasCase);
            _dialogue.SetVariable("$chat_case_name",
                caseDef != null ? Localization.T(caseDef.displayName) : "");
            _dialogue.SetVariable("$chat_case_origin",
                caseDef != null ? Localization.T(EvidenceLedger.GetOrigin(caseDef.id)) : "");

            // Meeting.yarn 中声明的公共变量，私聊侧复用同一组语义。
            _dialogue.SetVariable("$trust", trust);
            _dialogue.SetVariable("$stress", stress);
            _dialogue.SetVariable("$npc_number", _npc.Number);
            _dialogue.SetVariable("$player_number", playerNumber);
            _dialogue.SetVariable("$player_name", Localization.T($"{playerNumber}号"));
        }

        void SetChatEvidenceVariables(ItemDefinition evidence)
        {
            if (_dialogue == null || evidence == null) return;
            _dialogue.SetVariable("$chat_evidence_id", evidence.id);
            _dialogue.SetVariable("$chat_evidence_name", Localization.T(evidence.displayName));
            _dialogue.SetVariable("$chat_evidence_reason", Localization.T(
                string.IsNullOrEmpty(evidence.evidenceReason)
                    ? "这件东西和这层楼里发生的事脱不了干系。"
                    : evidence.evidenceReason));
            _dialogue.SetVariable("$chat_evidence_weight", (float)evidence.evidenceWeight);
            _dialogue.SetVariable("$chat_evidence_kind", EvidenceKindOf(evidence.id));

            string origin = Localization.T(EvidenceLedger.GetOrigin(evidence.id));
            _dialogue.SetVariable("$chat_evidence_origin", origin);
            _dialogue.SetVariable("$chat_evidence_has_origin", !string.IsNullOrEmpty(origin));
            _dialogue.SetVariable("$chat_evidence_points_at_1", IsCaseAgainstOne(evidence.id));
            _dialogue.SetVariable("$chat_evidence_is_nail",
                evidence.id == "nail" || evidence.id.StartsWith("saw_nail_"));
            _dialogue.SetVariable("$chat_evidence_is_magnet",
                evidence.id == "magnet" || evidence.id == "mystic_magnet");
            _dialogue.SetVariable("$chat_evidence_is_wall_marks",
                evidence.id.StartsWith("wall_nails"));
            _dialogue.SetVariable("$chat_evidence_is_generic_tool",
                evidence.id == "magnet" || evidence.id == "rope"
                || evidence.id == "hammer" || evidence.id == "dagger"
                || evidence.id == "bloody_dagger");
        }

        static string SurveillanceTextFor(int number, string pronoun)
        {
            string text = number > 0 ? SurveillanceLog.DescribeOutingsFor(number) : "";
            if (!string.IsNullOrEmpty(text)) return text;
            if (Localization.IsEnglish)
                return pronoun == "他"
                    ? "The cameras never caught him leaving his room."
                    : "The cameras never caught me leaving my room.";
            return $"监控没有拍到{pronoun}出房门";
        }

        static string EvidenceKindOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "physical";
            if (id == "face_compare" || id.StartsWith("face_compare_")
                || id == "faces_identical")
                return "identity";
            if (id == "ninth_body" || id.StartsWith("body_")
                || id.StartsWith("saw_body_")) return "body";
            if (id == "video_log" || id.StartsWith("video_clip_")) return "video";
            if (id.StartsWith("saw_") || id.StartsWith("wall_nails")) return "observation";
            return "physical";
        }

        void ClearChatEvidenceVariables()
        {
            if (_dialogue == null) return;
            _dialogue.SetVariable("$chat_evidence_id", "");
            _dialogue.SetVariable("$chat_evidence_name", "");
            _dialogue.SetVariable("$chat_evidence_reason", "");
            _dialogue.SetVariable("$chat_evidence_weight", 0f);
            _dialogue.SetVariable("$chat_evidence_kind", "physical");
            _dialogue.SetVariable("$chat_evidence_origin", "");
            _dialogue.SetVariable("$chat_evidence_has_origin", false);
            _dialogue.SetVariable("$chat_evidence_points_at_1", false);
            _dialogue.SetVariable("$chat_evidence_is_nail", false);
            _dialogue.SetVariable("$chat_evidence_is_magnet", false);
            _dialogue.SetVariable("$chat_evidence_is_wall_marks", false);
            _dialogue.SetVariable("$chat_evidence_is_generic_tool", false);
        }

        // ------------------------------------------------------------------
        // 兜底台词
        // ------------------------------------------------------------------

        string BuildFallbackGreeting()
        {
            if (_npc == null) return "……";
            float trust = _npc.blackboard.GetTrust(CycleState.playerNumber);

            if (_npc.blackboard.wantsPlayerDead)
                return $"{_npc.Number}号：……（他没有放下手里的东西）你来找我，是觉得自己还能说服谁？";
            if (trust >= 70f)
                return $"{_npc.Number}号：……（压低声音）是你。说吧，这栋房子里我只敢信你。";
            if (trust >= 55f)
                return $"{_npc.Number}号：……你找我？别站在走廊里，进来一点。";
            return $"{_npc.Number}号：……（警惕地看着你）如果是来套话的，就到此为止。";
        }

        string BuildPleaFallback(bool success)
        {
            if (_npc == null) return "……";
            if (success)
                return $"{_npc.Number}号：……你说的这些，我都听见了。\n" +
                       "（他没有点头，但把手里握着的东西放回了口袋。）";
            return $"{_npc.Number}号：……说完了？\n" +
                   "（他往后退了一步，手一直没有离开身侧。）";
        }

        string BuildShowdownFallback(ItemDefinition evidence, bool againstSelf)
        {
            if (_npc == null || evidence == null) return "……";
            string origin = EvidenceLedger.GetOrigin(evidence.id);
            string where = string.IsNullOrEmpty(origin) ? string.Empty : $"（{origin}）";

            if (againstSelf)
                return $"{_npc.Number}号：{evidence.displayName}{where}……你拿这个来问我？\n" +
                       "我出去过。可出去过的人不止我一个。";

            return $"{_npc.Number}号：{evidence.displayName}{where}……你是说，1号？\n" +
                   "（他没有立刻反驳，只是把视线移向1号常坐的那把椅子。）";
        }

        string BuildEvidenceQuestionFallback(ItemDefinition evidence)
        {
            if (_npc == null || evidence == null) return "……";
            string origin = EvidenceLedger.GetOrigin(evidence.id);
            string where = string.IsNullOrEmpty(origin) ? string.Empty : $"（{origin}）";
            return $"{_npc.Number}号：{evidence.displayName}{where}？\n" +
                   $"{evidence.evidenceReason}\n" +
                   "……你想让我说什么？";
        }

        string BuildFallbackTopic(string topic)
        {
            if (_npc == null) return "……";
            return $"{_npc.Number}号：{topic}\n……（他沉默了一会儿，没有立刻回答）";
        }
    }
}
