using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Yarn.Unity;

namespace HilbertLoop
{
    /// <summary>四种结局（模块四）。</summary>
    public enum EndingType
    {
        A, // 无尽轮回：活到尽头的 8 号被顺移成 9 号，在睡梦中被黑影补杀
        B, // 死亡 / 悲剧：局内死亡，或结局 D 判定失败
        C, // 集体破局：会议中号召全员摘面具成功
        D, // 顿悟：玩家死后，活人靠证据链想通「大家是同一张脸」
    }

    /// <summary>
    /// 通用结局播放器（重构）：
    /// - 停止循环与所有玩法输入，冻结全场；
    /// - 由 EndingDirector 接管一台结局相机，在真实场景里演完 A/B/C/D；
    /// - Yarn 结局台词只作为底部旁白字幕，不再使用黑屏 + 大段文字；
    /// - 演出结束后，在画面下方显示半透明的「游戏结束」卡片，场景仍可见。
    /// </summary>
    public class EndingSystem : MonoBehaviour
    {
        public static EndingSystem Instance { get; private set; }

        [Header("Yarn")]
        [Tooltip("留空时回退到场景中 MeetingDialogue 的 YarnProject")]
        public YarnProject yarnProject;
        public string endingNodeA = "Ending_A";
        public string endingNodeB = "Ending_B";
        public string endingNodeC = "Ending_C";
        public string endingNodeD = "Ending_D";

        [Header("台词停留（秒）")]
        public float lineMinSeconds = 2.4f;
        public float lineSecondsPerCharacter = 0.055f;
        public float lineMaxSeconds = 6f;

        [Header("引用")]
        public PlayerController player;

        Canvas _canvas;
        Image _black;
        Image _flash;
        TextMeshProUGUI _title;
        TextMeshProUGUI _causeText;
        TextMeshProUGUI _lineText;
        TextMeshProUGUI _hintText;
        EndingResultUI _resultUI;

        DialogueRunner _runner;
        EndingDialoguePresenter _presenter;

        bool _ending;
        bool _gameOverShown;
        bool _dialogueComplete;
        bool _suppressYarnLines;
        EndingType _endingType = EndingType.B;
        string _cause = "你没能活过这一轮。";

        public bool IsEnding => _ending;
        public EndingType CurrentEnding => _endingType;

        void Awake()
        {
            Instance = this;
            if (player == null) player = FindObjectOfType<PlayerController>();
            BuildUI();
            EnsureResultUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        void EnsureResultUI()
        {
            if (_resultUI != null) return;
            GameObject go = new GameObject("EndingResultUI");
            go.transform.SetParent(transform, false);
            _resultUI = go.AddComponent<EndingResultUI>();
            _resultUI.Initialize();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 触发
        // ------------------------------------------------------------------

        public void TriggerEndingA(string cause) => TriggerEnding(EndingType.A, cause);
        public void TriggerEndingB(string cause) => TriggerEnding(EndingType.B, cause);
        public void TriggerEndingC(string cause) => TriggerEnding(EndingType.C, cause);
        public void TriggerEndingD(string cause) => TriggerEnding(EndingType.D, cause);

        /// <summary>触发指定结局：终止循环、冻结全场、交给 EndingDirector 演实景。</summary>
        public void TriggerEnding(EndingType type, string cause)
        {
            if (_ending) return;

            // 真正的集体破局必须还有至少两个活人；把其他人杀光不会触发认知崩塌。
            if (type == EndingType.C && CycleState.GetLivingParticipantCount() < 2)
            {
                Debug.LogWarning($"[Ending] 结局 C 被拒绝：存活参与者只有 " +
                                 $"{CycleState.GetLivingParticipantCount()} 人（至少需要 2 人）。");
                return;
            }

            _ending = true;
            _endingType = type;
            _cause = string.IsNullOrEmpty(cause) ? DefaultCause(type) : cause;

            FreezeWorld(type);

            if (_canvas != null) _canvas.gameObject.SetActive(true);
            if (_black != null) _black.color = new Color(0f, 0f, 0f, 0f);
            if (_flash != null) _flash.color = new Color(1f, 1f, 1f, 0f);

            if (_title != null)
            {
                // A/B 完全交给监控画面，不再往镜头前面压一行大字。
                bool showTitle = type != EndingType.A && type != EndingType.B;
                _title.gameObject.SetActive(showTitle);
                _title.text = Localization.T(GetTitle(type));
                _title.color = GetTitleColor(type);
            }
            if (_causeText != null) _causeText.gameObject.SetActive(false);
            if (_lineText != null) _lineText.text = string.Empty;
            if (_hintText != null) _hintText.text = string.Empty;
            EnsureResultUI();
            if (_resultUI != null) _resultUI.HideAll();

            if (_runner == null) CreateDialogueRunner();

            _dialogueComplete = false;
            Debug.Log($"[Ending] 触发结局 {type}：{_cause}");
            StartCoroutine(RunEndingSequence(type));
        }

        void FreezeWorld(EndingType type)
        {
            if (player == null) player = FindObjectOfType<PlayerController>();

            MeetingSystem meeting = FindObjectOfType<MeetingSystem>();
            if (meeting != null) meeting.HaltForEnding();
            if (PrivateChatSystem.Instance != null) PrivateChatSystem.Instance.ForceClose();

            if (player != null)
            {
                player.inputLocked = true;
                player.movementLocked = true;
                if (player.interaction != null) player.interaction.enabled = false;
            }

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead) npc.EnterMeetingMode();
            }

            if (GameManager.Instance != null) GameManager.Instance.HaltLoop();

            if (PlayerDeathSystem.Instance != null)
                PlayerDeathSystem.Instance.CloseGhostForEnding();
            else if (GhostSpectator.Instance != null)
                GhostSpectator.Instance.Exit();

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            EnsureDirector();
            if (EndingDirector.Instance != null)
            {
                // A / B 走主菜单同款监控探头，不创建结局相机；C / D 才需要实景跟拍。
                bool cinematic = type == EndingType.C || type == EndingType.D;
                EndingDirector.Instance.Prepare(player, cinematic);
            }
        }

        void EnsureDirector()
        {
            if (EndingDirector.Instance != null) return;

            GameObject go = new GameObject("EndingDirector");
            go.transform.SetParent(transform, false);
            go.AddComponent<EndingDirector>();
        }

        IEnumerator RunEndingSequence(EndingType type)
        {
            // A/B 完全交给主菜单同款监控画面：不叠 Yarn 字幕、不压标题，
            // 画面里只留探头、时间码、存活名单和左侧文字菜单。
            _suppressYarnLines = type == EndingType.A || type == EndingType.B;

            string node = GetEndingNode(type);
            bool yarnStarted = StartYarnEnding(node);

            if (type == EndingType.A || type == EndingType.B)
            {
                // 死亡 / 无尽轮回：不切场景，直接在监控画面上推演残缺名单，然后定帧。
                // 双保险：如果同一个会话里先预览过 C/D，结局相机可能已经存在，
                // 这里必须把它关掉，否则 depth 50 的相机会再次盖住监控画面。
                if (EndingDirector.Instance != null && EndingDirector.Instance.EndingCamera != null)
                    EndingDirector.Instance.EndingCamera.gameObject.SetActive(false);

                if (_resultUI != null)
                    yield return _resultUI.PlaySurveillanceDeduction(type, _cause);
                else
                    yield return new WaitForSecondsRealtime(4f);
            }
            else
            {
                if (EndingDirector.Instance != null)
                    yield return EndingDirector.Instance.Play(type);
                else
                    yield return new WaitForSecondsRealtime(4f);

                if (type == EndingType.D && _resultUI != null)
                {
                    // 献祭顿悟：全员摘面具之后，监控屏幕碎裂花屏。
                    yield return _resultUI.PlayMonitorCrack();
                }
            }

            // 演出结束立刻叠加统一结算面板；Yarn 台词可以继续在面板下方播完。
            if (yarnStarted && !_dialogueComplete)
                Debug.Log($"[Ending] 结局 {type} 动画结束，结算面板已显示，Yarn 台词继续播放。");

            yield return new WaitForSecondsRealtime(0.5f);
            ShowGameOver();
        }

        bool StartYarnEnding(string node)
        {
            if (_runner == null || _runner.YarnProject == null || _runner.YarnProject.Program == null)
            {
                Debug.LogError($"[Ending] 无法加载 {node}：YarnProject 或结局文件未装配。");
                return false;
            }

            try
            {
                _runner.StartDialogue(node);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Ending] 启动 {node} 失败：{e.Message}");
                return false;
            }
        }

        void ShowGameOver()
        {
            if (_gameOverShown) return;
            _gameOverShown = true;

            // 标题与台词淡出，统一悬浮结算面板接管画面。
            _suppressYarnLines = true;
            if (_title != null) _title.gameObject.SetActive(false);
            if (_causeText != null) _causeText.gameObject.SetActive(false);
            if (_lineText != null) _lineText.text = string.Empty;
            if (_hintText != null) _hintText.text = string.Empty;

            EnsureResultUI();
            if (_resultUI != null)
                _resultUI.ShowResultPanel(_endingType, _cause);

            // 结局出现即保存，下次「继续」可从当前周目接手。
            SaveManager.Save("ending");

            Debug.Log($"[Ending] 结局 {_endingType} 演出完毕，统一结算面板已显示。");
        }

        // ------------------------------------------------------------------
        // 结局文案 / 节点映射
        // ------------------------------------------------------------------

        string GetEndingNode(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return endingNodeA;
                case EndingType.C: return endingNodeC;
                case EndingType.D: return endingNodeD;
                default: return endingNodeB;
            }
        }

        static string GetTitle(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "无尽轮回";
                case EndingType.C: return "大梦初醒";
                case EndingType.D: return "献祭顿悟";
                default: return "死亡悲剧";
            }
        }

        static string GetSubtitle(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "ENDLESS LOOP  /  无尽轮回";
                case EndingType.C: return "AWAKENING  /  大梦初醒";
                case EndingType.D: return "SACRIFICE & EPIPHANY  /  献祭顿悟";
                default: return "DEATH TRAGEDY  /  死亡悲剧";
            }
        }

        static Color GetTitleColor(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return new Color(0.66f, 0.62f, 0.72f, 1f);
                case EndingType.C: return new Color(0.96f, 0.82f, 0.42f, 1f);
                case EndingType.D: return new Color(0.96f, 0.68f, 0.38f, 1f);
                default: return new Color(0.82f, 0.16f, 0.16f, 1f);
            }
        }

        static Color GetSubtitleColor(EndingType type)
        {
            Color color = GetTitleColor(type);
            color.a = 0.9f;
            return color;
        }

        static string GetEpilogue(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "循环仍会继续，只是不再需要你。";
                case EndingType.C: return "你终于在自己的床上醒来——或者，那只是另一层梦。";
                case EndingType.D: return "他们摘下面具，第一次认出了彼此，也认出了自己。";
                default: return "循环不会为任何人停下。";
            }
        }

        static string DefaultCause(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "你活到了循环的尽头。今夜，轮到你成为那具尸体。";
                case EndingType.C: return "所有人摘下面具，梦境失去了继续运转的理由。";
                case EndingType.D: return "活下来的人靠证据链认出了同一张脸。";
                default: return "你没能活过这一轮。";
            }
        }

        // ------------------------------------------------------------------
        // Yarn
        // ------------------------------------------------------------------

        void CreateDialogueRunner()
        {
            if (yarnProject == null)
            {
                DialogueBridge bridge = FindObjectOfType<DialogueBridge>();
                if (bridge != null && bridge.dialogueRunner != null)
                    yarnProject = bridge.dialogueRunner.YarnProject;
            }

            if (yarnProject == null)
            {
                Debug.LogError("[Ending] 缺少 YarnProject，结局只会显示游戏结束卡片。");
                return;
            }

            GameObject dialogueGo = new GameObject("EndingDialogue");
            dialogueGo.transform.SetParent(transform, false);

            InMemoryVariableStorage storage = dialogueGo.AddComponent<InMemoryVariableStorage>();
            DialogueRunner runner = dialogueGo.AddComponent<DialogueRunner>();
            runner.VariableStorage = storage;

            _presenter = dialogueGo.AddComponent<EndingDialoguePresenter>();
            _presenter.ending = this;
            runner.DialoguePresenters = new DialoguePresenterBase[] { _presenter };

            runner.SetProject(yarnProject);
            _runner = runner;

            Debug.Log($"[Ending] Yarn 结局节点已装配：{GetEndingNode(_endingType)}（project={yarnProject.name}）。");
        }

        public void ShowYarnLine(string text)
        {
            if (_suppressYarnLines) return;
            if (_lineText != null) _lineText.text = text;
        }

        public void ClearYarnLine()
        {
            if (_lineText != null) _lineText.text = string.Empty;
        }

        /// <summary>由 EndingDialoguePresenter 在整段 Yarn 台词结束时调用。</summary>
        public void HandleDialogueComplete()
        {
            _dialogueComplete = true;
        }

        public float GetLineDuration(string text)
        {
            int length = string.IsNullOrEmpty(text) ? 0 : text.Length;
            return Mathf.Clamp(lineMinSeconds + length * lineSecondsPerCharacter,
                lineMinSeconds, lineMaxSeconds);
        }

        // ------------------------------------------------------------------
        // UI 构建
        // ------------------------------------------------------------------

        void BuildUI()
        {
            var canvasGo = new GameObject("EndingOverlay_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 500;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            EnsureEventSystem();

            // 不再使用全屏黑幕；保留透明图层作为兼容。
            _black = CreateImage(canvasGo.transform, "Black", new Color(0f, 0f, 0f, 0f));
            _flash = CreateImage(canvasGo.transform, "Flash", new Color(1f, 1f, 1f, 0f));
            _flash.transform.SetAsLastSibling();

            _title = CreateText(canvasGo.transform, "Title", GetTitle(EndingType.B),
                new Vector2(0.5f, 1f), new Vector2(0f, -58f), new Vector2(1400f, 72f),
                44f, TextAlignmentOptions.Center);
            _title.color = GetTitleColor(EndingType.B);

            _causeText = CreateText(canvasGo.transform, "Cause", string.Empty,
                new Vector2(0.5f, 1f), new Vector2(0f, -118f), new Vector2(1400f, 50f),
                22f, TextAlignmentOptions.Center);
            _causeText.color = new Color(0.78f, 0.72f, 0.70f, 1f);
            _causeText.gameObject.SetActive(false);

            _lineText = CreateText(canvasGo.transform, "Line", string.Empty,
                new Vector2(0.5f, 0f), new Vector2(0f, 118f), new Vector2(1560f, 260f),
                31f, TextAlignmentOptions.Center);
            _lineText.color = new Color(0.96f, 0.93f, 0.88f, 1f);
            _lineText.outlineWidth = 0.18f;
            _lineText.outlineColor = new Color(0f, 0f, 0f, 0.9f);

            _hintText = CreateText(canvasGo.transform, "Hint", string.Empty,
                new Vector2(0.5f, 0f), new Vector2(0f, 58f), new Vector2(1400f, 40f),
                20f, TextAlignmentOptions.Center);
            _hintText.color = new Color(0.62f, 0.58f, 0.58f, 0.9f);

            _title.transform.SetAsLastSibling();
            _causeText.transform.SetAsLastSibling();
            _lineText.transform.SetAsLastSibling();
            _hintText.transform.SetAsLastSibling();
        }

        static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, string text,
                                          Vector2 anchor, Vector2 anchoredPosition, Vector2 size,
                                          float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            label.raycastTarget = false;
            ChineseFontUtility.Apply(label);
            return label;
        }
    }
}
