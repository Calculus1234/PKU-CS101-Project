using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 隐藏的开发者模式：
    /// - 直接预览 A / B / C / D 四种结局；
    /// - 实时查看轮回状态、关键旗标、每个 NPC 的黑板、证据账本、监控记录与本地模型状态。
    ///
    /// 上线版本已彻底停用：不再监听 Ctrl + Shift + D，面板每次启动强制隐藏，
    /// 即使有人恢复快捷键，Open 也会被 DeveloperModeEnabled 拒绝。
    /// </summary>
    public class DeveloperModeUI : MonoBehaviour
    {
        public static DeveloperModeUI Instance { get; private set; }

        // 上线版本必须保持 false。本地调试时改回 true 并自行恢复入口。
        static readonly bool DeveloperModeEnabled = false;

        const float PanelWidth = 1280f;
        const float PanelHeight = 820f;
        const float RefreshInterval = 0.4f;

        CanvasGroup _group;
        TextMeshProUGUI _details;
        TextMeshProUGUI _status;

        bool _built;
        bool _open;
        float _refreshTimer;

        public bool IsOpen => _open;

        bool _prevClockPaused;
        bool _prevInputLocked;
        bool _prevMovementLocked;
        PlayerController _player;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("DeveloperModeUI");
            DontDestroyOnLoad(go);
            go.AddComponent<DeveloperModeUI>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            BuildIfNeeded();
            _open = false;
            SetVisible(false);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 场景重载后旧状态已失效：只收起面板，不把上一场景的暂停状态带过来。
            _open = false;
            _player = null;
            SetVisible(false);
        }

        void Update()
        {
            // 上线版本已删除 Ctrl + Shift + D 监听；开发者面板没有任何游戏内入口。
            if (!DeveloperModeEnabled) return;
            if (!_open) return;

            // 结局已经开始播放：立刻收起面板，把画面让给演出。
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding)
            {
                Close();
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = RefreshInterval;
                RefreshDetails();
            }
        }

        void Toggle()
        {
            if (_open)
            {
                Close();
                return;
            }
            Open();
        }

        void Open()
        {
            if (!DeveloperModeEnabled) return;
            // 主菜单没有可预览的局内状态；结局演出中也不打断。
            if (GameMode.MenuActive) return;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return;
            if (NpcFreeTextInputUI.IsOpen) return;
            if (LlmSettingsUI.Instance != null && LlmSettingsUI.Instance.IsOpen) return;

            if (!_built) BuildIfNeeded();
            _open = true;

            _player = FindObjectOfType<PlayerController>();
            if (_player != null)
            {
                _prevInputLocked = _player.inputLocked;
                _prevMovementLocked = _player.movementLocked;
                _player.inputLocked = true;
                _player.movementLocked = true;
            }

            if (GameManager.Instance != null)
            {
                _prevClockPaused = GameManager.Instance.ClockPaused;
                GameManager.Instance.ClockPaused = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SetVisible(true);
            _refreshTimer = 0f;
            RefreshDetails();
        }

        void Close()
        {
            if (!_open)
            {
                SetVisible(false);
                return;
            }

            _open = false;
            SetVisible(false);

            if (_player != null && (PlayerDeathSystem.Instance == null || !PlayerDeathSystem.Instance.IsDead))
            {
                _player.inputLocked = _prevInputLocked;
                _player.movementLocked = _prevMovementLocked;
            }
            _player = null;

            if (GameManager.Instance != null)
                GameManager.Instance.ClockPaused = _prevClockPaused || NpcFreeTextInputUI.IsOpen;

            bool chatActive = PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive;
            if (!chatActive && !NpcFreeTextInputUI.IsOpen)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        void SetVisible(bool visible)
        {
            if (_group == null) return;
            // 关闭时连同面板对象一起失活，避免留下一个透明但可被脚本重新启用的全屏 Canvas。
            if (_group.gameObject.activeSelf != visible)
                _group.gameObject.SetActive(visible);
            _group.alpha = visible ? 1f : 0f;
            _group.interactable = visible;
            _group.blocksRaycasts = visible;
        }

        // ------------------------------------------------------------------
        // 状态详情
        // ------------------------------------------------------------------

        void RefreshDetails()
        {
            if (_details == null) return;

            var sb = new StringBuilder(1800);

            sb.Append("【轮回】\n");
            sb.Append("第 ").Append(CycleState.loopIndex).Append(" 轮　你是 ")
              .Append(CycleState.playerNumber).Append(" 号（")
              .Append(CycleState.playerAlive ? "存活" : "已死亡").Append("）\n");

            GameManager gm = GameManager.Instance;
            if (gm != null)
            {
                sb.Append("阶段：").Append(gm.CurrentPhase)
                  .Append("　剩余：").Append(FormatTime(gm.PhaseTimeRemaining)).Append("\n");
            }
            sb.Append("人口：").Append(CycleState.currentPopulation)
              .Append("　已知最大号码：").Append(CycleState.currentKnownMax)
              .Append("　本轮已执行：").Append(CycleState.executedNumber > 0
                  ? CycleState.executedNumber + " 号" : "无人").Append("\n");

            sb.Append("\n【关键旗标】\n");
            sb.Append("玩家摘面具：").Append(Flag(CycleState.playerDisguiseRemoved))
              .Append("　9号面具揭开：").Append(Flag(CycleState.ninthMaskRemoved)).Append("\n");
            sb.Append("两张脸对比：").Append(Flag(CycleState.faceComparisonDone))
              .Append("　9号尸体检查：").Append(Flag(CycleState.ninthBodyInspected)).Append("\n");
            sb.Append("监控回放：").Append(Flag(CycleState.videoLogWatched))
              .Append("　9号已死：").Append(Flag(CycleState.kill9Executed)).Append("\n");

            EndingDSystem d = EndingDSystem.Instance;
            if (d != null)
            {
                string state = d.IsResolved ? "已结算" : (d.IsArmed ? "已装配" : "未装配");
                d.Evaluate();
                sb.Append("顿悟判定：").Append(state)
                  .Append("　活人 ").Append(d.LivingNpcCount)
                  .Append(" / 知道脸 ").Append(d.InformedNpcCount)
                  .Append(" / 物证 ").Append(d.CorroboratedNpcCount).Append("\n");
                if (!string.IsNullOrEmpty(d.LastReason))
                    sb.Append("判定说明：").Append(Trim(d.LastReason, 140)).Append("\n");
            }

            sb.Append("\n【NPC 黑板】\n");
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null) continue;
                NPCBlackboard b = npc.blackboard;
                sb.Append(npc.Number).Append("号 ")
                  .Append(npc.isDead ? "死" : "活")
                  .Append("　压").Append(Mathf.RoundToInt(b != null ? b.stress : 0f))
                  .Append(" 疑").Append(Mathf.RoundToInt(b != null ? b.suspicion : 0f));
                if (b != null)
                {
                    if (b.wantsPlayerDead) sb.Append(" 杀意");
                    if (b.hasNote) sb.Append(" 纸条");
                    if (b.knowsFacesIdentical) sb.Append(" 知脸同");
                    sb.Append(" 线索").Append(b.knownClues != null ? b.knownClues.Count : 0);
                }
                sb.Append("\n");
            }

            sb.Append("\n【证据账本】").Append(EvidenceLedger.Count).Append(" 件\n");
            var entries = EvidenceLedger.Entries;
            int start = Mathf.Max(0, entries.Count - 6);
            for (int i = start; i < entries.Count; i++)
            {
                EvidenceEntry e = entries[i];
                if (e == null) continue;
                sb.Append("· ").Append(e.displayName);
                if (!string.IsNullOrEmpty(e.origin)) sb.Append("（").Append(e.origin).Append("）");
                sb.Append("\n");
            }

            sb.Append("\n【监控记录】\n");
            string timeline = SurveillanceLog.BuildPromptTimeline();
            sb.Append(Trim(string.IsNullOrEmpty(timeline) ? "（暂无）" : timeline.Replace("\n", " / "), 420)).Append("\n");

            LLMNpcBrain brain = LLMNpcBrain.Instance;
            sb.Append("\n【本地模型】");
            if (brain == null) sb.Append("未创建\n");
            else
            {
                sb.Append(brain.CurrentModelLabel)
                  .Append(brain.IsReady ? "　就绪" : "　未就绪");
                if (brain.IsLoadingModel) sb.Append("　加载中");
                if (brain.IsFailed) sb.Append("　失败");
                sb.Append("\n");
            }

            _details.text = sb.ToString();
        }

        static string Flag(bool value) => value ? "是" : "否";

        static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        // ------------------------------------------------------------------
        // 结局按钮
        // ------------------------------------------------------------------

        void PreviewEnding(EndingType type)
        {
            EndingSystem ending = EndingSystem.Instance;
            if (ending == null)
            {
                SetStatus("场景里找不到 EndingSystem。");
                return;
            }
            if (ending.IsEnding)
            {
                SetStatus("已经有一个结局在播放。");
                return;
            }

            if (type == EndingType.C && CycleState.GetLivingParticipantCount() < 2)
            {
                SetStatus($"结局 C 至少需要 2 个活人（当前 {CycleState.GetLivingParticipantCount()}）。");
                return;
            }

            SetStatus($"正在播放结局 {type}……");
            Close();
            ending.TriggerEnding(type, $"开发者模式预览：结局 {type}");
        }

        void ReturnToMenu()
        {
            Close();
            GameMode.ReturnToMenu();
        }

        void SetStatus(string text)
        {
            if (_status != null) _status.text = text;
        }

        // ------------------------------------------------------------------
        // 运行时构建
        // ------------------------------------------------------------------

        void BuildIfNeeded()
        {
            if (_built) return;
            _built = true;

            Canvas canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;

            if (GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            GameObject root = NewUIObject(transform, "Root");
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            Image dim = root.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.80f);
            dim.raycastTarget = true;

            _group = root.AddComponent<CanvasGroup>();

            GameObject panel = NewUIObject(root.transform, "Panel");
            RectTransform panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            Image panelBg = panel.AddComponent<Image>();
            panelBg.color = new Color(0.03f, 0.035f, 0.05f, 0.98f);
            panelBg.raycastTarget = true;

            CreateText(panel.transform, "开发者模式 · 隐藏", new Vector2(0.5f, 1f),
                new Vector2(0f, -32f), new Vector2(PanelWidth - 80f, 44f),
                34f, TextAlignmentOptions.Center, new Color(1f, 0.84f, 0.48f));

            CreateText(panel.transform, "开发者模式已停用　｜　左侧滚轮可下翻　｜　结局按钮直接播放，不校验前置条件（C 仍要求至少 2 个活人）",
                new Vector2(0.5f, 1f), new Vector2(0f, -76f),
                new Vector2(PanelWidth - 80f, 30f), 19f, TextAlignmentOptions.Center,
                new Color(0.62f, 0.67f, 0.76f));

            // 左：状态详情
            GameObject detailBg = NewUIObject(panel.transform, "DetailBox");
            RectTransform detailRect = (RectTransform)detailBg.transform;
            detailRect.anchorMin = detailRect.anchorMax = new Vector2(0f, 1f);
            detailRect.pivot = new Vector2(0f, 1f);
            detailRect.anchoredPosition = new Vector2(34f, -112f);
            detailRect.sizeDelta = new Vector2(760f, 650f);
            Image detailImage = detailBg.AddComponent<Image>();
            detailImage.color = new Color(0.055f, 0.065f, 0.09f, 1f);
            detailImage.raycastTarget = true;

            // 详情可能超过一屏（9 名 NPC + 证据 + 监控 + 模型状态），
            // 用 ScrollRect 兜住，滚轮可以往下翻。
            GameObject viewport = NewUIObject(detailBg.transform, "Viewport");
            RectTransform viewportRect = (RectTransform)viewport.transform;
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(16f, 12f);
            viewportRect.offsetMax = new Vector2(-16f, -12f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = NewUIObject(viewport.transform, "Content");
            RectTransform contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 620f);

            _details = content.AddComponent<TextMeshProUGUI>();
            _details.text = "";
            _details.fontSize = 17f;
            _details.alignment = TextAlignmentOptions.TopLeft;
            _details.color = new Color(0.86f, 0.90f, 0.96f);
            _details.lineSpacing = 3f;
            _details.enableWordWrapping = true;
            _details.raycastTarget = false;
            ChineseFontUtility.Apply(_details);

            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = detailBg.AddComponent<ScrollRect>();
            scroll.viewport = viewportRect;
            scroll.content = contentRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 32f;

            // 右：结局按钮
            float rx = 828f;
            CreateText(panel.transform, "结局预览", new Vector2(0f, 1f),
                new Vector2(rx, -132f), new Vector2(400f, 32f),
                24f, TextAlignmentOptions.Left, new Color(0.82f, 0.86f, 0.94f));

            CreateButton(panel.transform, "A · 无尽轮回", new Vector2(rx, -184f), new Vector2(196f, 62f),
                () => PreviewEnding(EndingType.A), new Color(0.22f, 0.26f, 0.36f));
            CreateButton(panel.transform, "B · 死亡 / 悲剧", new Vector2(rx + 208f, -184f), new Vector2(196f, 62f),
                () => PreviewEnding(EndingType.B), new Color(0.28f, 0.18f, 0.20f));
            CreateButton(panel.transform, "C · 集体破局", new Vector2(rx, -258f), new Vector2(196f, 62f),
                () => PreviewEnding(EndingType.C), new Color(0.18f, 0.28f, 0.22f));
            CreateButton(panel.transform, "D · 顿悟", new Vector2(rx + 208f, -258f), new Vector2(196f, 62f),
                () => PreviewEnding(EndingType.D), new Color(0.30f, 0.26f, 0.14f));

            CreateText(panel.transform,
                "A：活到天亮的顺移与补杀\n" +
                "B：死亡 / 顿悟失败\n" +
                "C：圆桌上号召全员摘面具（需活人 ≥ 2）\n" +
                "D：活人靠证据链想通“同一张脸”",
                new Vector2(0f, 1f), new Vector2(rx, -344f),
                new Vector2(404f, 150f), 18f, TextAlignmentOptions.TopLeft,
                new Color(0.68f, 0.73f, 0.82f));

            CreateButton(panel.transform, "返回主菜单", new Vector2(rx, -516f), new Vector2(196f, 58f),
                ReturnToMenu, new Color(0.16f, 0.18f, 0.24f));
            CreateButton(panel.transform, "关闭面板", new Vector2(rx + 208f, -516f), new Vector2(196f, 58f),
                Close, new Color(0.12f, 0.14f, 0.19f));

            _status = CreateText(panel.transform, "", new Vector2(0f, 1f),
                new Vector2(rx, -602f), new Vector2(404f, 110f),
                19f, TextAlignmentOptions.TopLeft, new Color(1f, 0.70f, 0.38f));
            _status.enableWordWrapping = true;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            GameObject go = new GameObject("EventSystem",
                typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }

        static GameObject NewUIObject(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateText(Transform parent, string content, Vector2 anchor,
            Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align, Color color)
        {
            GameObject go = NewUIObject(parent, "Text");
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = color;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            ChineseFontUtility.Apply(text);
            return text;
        }

        static void CreateButton(Transform parent, string label, Vector2 pos, Vector2 size,
            UnityEngine.Events.UnityAction onClick, Color color)
        {
            GameObject go = NewUIObject(parent, "Button");
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = color;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            if (onClick != null) button.onClick.AddListener(onClick);

            TextMeshProUGUI text = CreateText(go.transform, label, new Vector2(0.5f, 0.5f),
                Vector2.zero, size, 22f, TextAlignmentOptions.Center, Color.white);
            text.raycastTarget = false;
        }
    }
}
