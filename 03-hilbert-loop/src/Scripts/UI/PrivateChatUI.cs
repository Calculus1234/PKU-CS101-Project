using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 私聊 HUD（模块四）：搜证阶段与 NPC 一对一交谈。
    /// - 顶部：NPC 号码 / 情绪标签（信任、压力、杀意均为模糊描述，不显示数字）；
    /// - 中部：深色对话框，保留最近三句历史台词；
    /// - 底部：6 个话题选项，第三个会打开证据列表；
    /// - 证据双列 8 件一页，Q / E 翻页；数字键 1~8 选择，Esc 取消证据 / 结束私聊；
    /// - E 在私聊中不再承担任何功能，避免与“开始私聊”的 E 键互相吞并。
    /// 控件全部运行时构建，不依赖场景预制体。
    /// </summary>
    public class PrivateChatUI : MonoBehaviour
    {
        [Header("输入保护（防止开始私聊的同帧按键误选选项）")]
        public float inputGuardSeconds = 0.3f;

        const int EvidencePageSize = 8;

        CanvasGroup _content;
        Image _fade;
        TextMeshProUGUI _speakerText;
        TextMeshProUGUI _trustText;
        TextMeshProUGUI _historyText;
        TextMeshProUGUI _lineText;
        TextMeshProUGUI _hintText;
        TextMeshProUGUI _panelTitle;
        TextMeshProUGUI _evidencePageText;

        GameObject _panel;
        readonly Button[] _optionButtons = new Button[7];
        readonly TextMeshProUGUI[] _optionLabels = new TextMeshProUGUI[7];
        readonly Button[] _evidenceButtons = new Button[EvidencePageSize];
        readonly TextMeshProUGUI[] _evidenceLabels = new TextMeshProUGUI[EvidencePageSize];

        Action<int> _selectCallback;
        Action _cancelEvidence;
        Action _endChat;

        string[] _evidencePages = Array.Empty<string>();
        int _evidencePage;
        int _evidencePageCount;

        readonly List<string> _history = new List<string>();
        string _currentSpeaker = string.Empty;
        string _lastLine = string.Empty;

        int _optionCount;
        bool _optionsVisible;
        bool _evidenceVisible;
        bool _built;
        float _inputReadyAt;

        public bool OptionsVisible => _optionsVisible;
        public bool EvidenceVisible => _evidenceVisible;

        void Awake()
        {
            BuildIfNeeded();
            SetVisible(false);
        }

        void Update()
        {
            if (NpcFreeTextInputUI.IsOpen) return;
            if (Time.unscaledTime < _inputReadyAt) return;

            if (_evidenceVisible)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CancelEvidence();
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Q))
                {
                    ChangeEvidencePage(-1);
                    return;
                }
                if (Input.GetKeyDown(KeyCode.E))
                {
                    ChangeEvidencePage(1);
                    return;
                }
                HandleNumberKeys(EvidencePageSize, SelectEvidence);
                return;
            }

            if (_optionsVisible)
            {
                // E 在私聊面板中不再结束对话，避免和“对 NPC 按 E 发起私聊”冲突。
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _endChat?.Invoke();
                    return;
                }
                HandleNumberKeys(_optionCount, SelectOption);
            }
        }

        void HandleNumberKeys(int count, Action<int> select)
        {
            for (int i = 0; i < count; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                {
                    select(i);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        public void SetEndAction(Action endChat)
        {
            _endChat = endChat;
        }

        public void SetVisible(bool visible)
        {
            if (!_built) BuildIfNeeded();
            if (_content == null) return;

            _content.alpha = visible ? 1f : 0f;
            _content.interactable = visible;
            _content.blocksRaycasts = visible;
            if (_fade != null)
            {
                Color color = _fade.color;
                color.a = visible ? 0.58f : 0f;
                _fade.color = color;
            }

            if (visible)
            {
                _inputReadyAt = Time.unscaledTime + inputGuardSeconds;
                _history.Clear();
                _lastLine = string.Empty;
                RefreshHistory();
            }
            else
            {
                HidePanel();
                SetLine(string.Empty);
                SetHint(string.Empty);
                SetSpeaker(string.Empty);
                SetEmotion(0, 0f, 0f, 0f, 0f, false);
                _history.Clear();
                _lastLine = string.Empty;
                RefreshHistory();
                _endChat = null;
            }
        }

        public void SetSpeaker(string speaker)
        {
            // 先在入口翻译再保存：历史台词会复用这个说话人标签，
            // 保存中文原文会让英文模式的历史区冒出 "4 号：..."。
            _currentSpeaker = Localization.Translate(speaker ?? string.Empty);
            if (_speakerText != null) _speakerText.text = _currentSpeaker;
        }

        /// <summary>
        /// 情绪标签：信任 / 压力 / 杀意全部转成文字，玩家看不到任何数值。
        /// </summary>
        public void SetEmotion(int npcNumber, float trust, float suspicion, float stress,
            float aggression, bool visible)
        {
            if (_trustText == null) return;
            _trustText.gameObject.SetActive(visible);
            if (!visible) return;

            // 逐段翻译再拼接：整串「4 号　[警惕]　[指尖在抖]」不在词典里，
            // 整体翻译会原样返回，英文模式下留下一行中文。
            bool en = Localization.IsEnglish;
            string sep = en ? "  " : "　";
            string text = Localization.Translate(EmotionLabels.Trust(trust));
            string stressLabel = Localization.Translate(EmotionLabels.Stress(stress));
            string hostility = Localization.Translate(EmotionLabels.Hostility(suspicion, aggression));
            if (!string.IsNullOrEmpty(stressLabel)) text += sep + stressLabel;
            if (!string.IsNullOrEmpty(hostility)) text += sep + hostility;

            _trustText.text = Localization.Translate($"{npcNumber} 号") + sep + text;
        }

        /// <summary>
        /// 流式上屏专用：只改当前台词，不写历史。
        /// 模型边生成边回调，若每次都进 _history，同一句话会被记成十几条。
        /// 最终台词仍由 SetLine 统一入账。
        /// </summary>
        public void SetStreamingLine(string text)
        {
            // 流式回调带 "4号：" 前缀，英文模式下会在正文里露出一截中文，
            // 这里统一翻译前缀（正文已由 SanitizeSpeech 保证是英文）。
            if (!string.IsNullOrEmpty(text)) text = Localization.Translate(text);
            if (_lineText != null) _lineText.text = text ?? string.Empty;
        }

        static readonly Regex _speakerEchoRegex =
            new Regex(@"^(?:No\.\s*)?(\d+)\s*号?\s*[:：]\s*", RegexOptions.CultureInvariant);

        /// <summary>台词开头重复了当前说话人的号码时，去掉这段前缀。</summary>
        string StripSpeakerEcho(string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_currentSpeaker)) return text;
            Match speaker = Regex.Match(_currentSpeaker, @"(\d+)");
            if (!speaker.Success) return text;

            Match prefix = _speakerEchoRegex.Match(text);
            if (!prefix.Success || prefix.Groups[1].Value != speaker.Groups[1].Value) return text;
            return text.Substring(prefix.Length);
        }

        public void SetLine(string text)
        {
            if (!string.IsNullOrEmpty(text)) text = Localization.Translate(text);

            // 说话人已经单独显示在对话框上方，台词里再带一次 "4号： / No. 4：" 很蠢。
            string display = StripSpeakerEcho(text);
            if (_lineText != null) _lineText.text = display ?? string.Empty;

            if (string.IsNullOrEmpty(text) || text == _lastLine) return;
            _lastLine = text;

            // 旁白、以及玩家 / 系统自己的台词已经自带说话人，不能再补 NPC 前缀；
            // 其余台词的 NPC 前缀刚刚被 StripSpeakerEcho 去掉，这里统一补回短前缀。
            bool selfLabeled = text.StartsWith("（") || text.StartsWith("(") ||
                               text.StartsWith("你：") || text.StartsWith("我：") ||
                               text.StartsWith("You: ") || text.StartsWith("Me: ") ||
                               text.StartsWith("Player: ") || text.StartsWith("Someone: ") ||
                               text.StartsWith("Nobody: ");
            string entry = selfLabeled || string.IsNullOrEmpty(_currentSpeaker)
                ? display
                : _currentSpeaker + (Localization.IsEnglish ? ": " : "：") + display;

            _history.Add(entry);
            while (_history.Count > 4) _history.RemoveAt(0);
            RefreshHistory();
        }

        void RefreshHistory()
        {
            if (_historyText == null) return;
            // 最后一条就是正在正文区显示的当前台词，历史区不再重复它。
            int end = _history.Count - 1;
            if (end <= 0)
            {
                _historyText.text = string.Empty;
                return;
            }
            int start = Mathf.Max(0, end - 3);
            var lines = new List<string>(end - start);
            for (int i = start; i < end; i++) lines.Add(ClipHistoryEntry(_history[i]));
            _historyText.text = string.Join("\n", lines);
        }

        /// <summary>历史区每条只留一行：英文按单词边界截，中文按字符截。</summary>
        static string ClipHistoryEntry(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            bool en = Localization.IsEnglish;
            int limit = en ? 104 : 52;
            if (text.Length <= limit) return text;

            int cut = limit;
            if (en)
            {
                int space = text.LastIndexOf(' ', cut);
                if (space > limit / 2) cut = space;
            }
            return text.Substring(0, cut).TrimEnd() + (en ? "..." : "…");
        }

        public void SetHint(string text)
        {
            if (_hintText != null)
            {
                _hintText.text = string.IsNullOrEmpty(text) ? text : Localization.Translate(text);
                _hintText.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        public void ShowOptions(string[] labels, Action<int> onSelected)
        {
            if (!_built) BuildIfNeeded();
            _selectCallback = onSelected;
            _optionsVisible = true;
            _evidenceVisible = false;
            _optionCount = Mathf.Clamp(labels != null ? labels.Length : 0, 0, _optionButtons.Length);

            if (_panelTitle != null) _panelTitle.text = Localization.Translate("你想说什么？");
            if (_evidencePageText != null) _evidencePageText.gameObject.SetActive(false);

            for (int i = 0; i < _optionButtons.Length; i++)
            {
                if (_optionButtons[i] == null || _optionLabels[i] == null) continue;
                bool show = i < _optionCount;
                _optionButtons[i].gameObject.SetActive(show);
                if (show) _optionLabels[i].text = $"{i + 1}. {Localization.Translate(labels[i])}";
            }
            for (int i = 0; i < _evidenceButtons.Length; i++)
                _evidenceButtons[i].gameObject.SetActive(false);

            if (_panel != null) _panel.SetActive(true);
            SetHint("数字键选择　·　Esc 结束私聊");
        }

        public void HideOptions()
        {
            _optionsVisible = false;
            _selectCallback = null;
            HidePanel();
        }

        public void ShowEvidence(string[] labels, Action<int> onSelected, Action onCancel)
        {
            if (!_built) BuildIfNeeded();
            _selectCallback = onSelected;
            _cancelEvidence = onCancel;
            _optionsVisible = false;
            _evidenceVisible = true;
            _evidencePages = labels ?? Array.Empty<string>();
            _evidencePage = 0;
            _evidencePageCount = Mathf.Max(1,
                Mathf.CeilToInt(_evidencePages.Length / (float)EvidencePageSize));

            if (_panelTitle != null) _panelTitle.text = Localization.Translate($"出示证据（共 {_evidencePages.Length} 件）");

            for (int i = 0; i < _optionButtons.Length; i++)
                _optionButtons[i].gameObject.SetActive(false);

            RenderEvidencePage();
            if (_panel != null) _panel.SetActive(true);
            SetHint("选择一件证据——它会改变这个人的判断");
        }

        void RenderEvidencePage()
        {
            for (int i = 0; i < EvidencePageSize; i++)
            {
                int index = _evidencePage * EvidencePageSize + i;
                bool show = index >= 0 && index < _evidencePages.Length;
                _evidenceButtons[i].gameObject.SetActive(show);
                if (show) _evidenceLabels[i].text = $"{i + 1}. {Localization.Translate(_evidencePages[index])}";
            }

            if (_evidencePageText != null)
            {
                _evidencePageText.gameObject.SetActive(true);
                _evidencePageText.text = Localization.Translate(_evidencePageCount > 1
                    ? $"第 {_evidencePage + 1} / {_evidencePageCount} 页　Q / E 翻页　Esc 取消"
                    : "数字键 / 鼠标选择　Esc 取消");
            }
        }

        void ChangeEvidencePage(int delta)
        {
            if (!_evidenceVisible || _evidencePageCount <= 1) return;
            _evidencePage = (_evidencePage + delta + _evidencePageCount) % _evidencePageCount;
            RenderEvidencePage();
        }

        public void HideEvidence()
        {
            _evidenceVisible = false;
            _selectCallback = null;
            _cancelEvidence = null;
            HidePanel();
        }

        /// <summary>私聊结束 / 被迫中断：隐藏全部控件。</summary>
        public void CloseImmediate()
        {
            _optionsVisible = false;
            _evidenceVisible = false;
            _selectCallback = null;
            _cancelEvidence = null;
            _endChat = null;
            _optionCount = 0;
            HidePanel();
            SetVisible(false);
        }

        void OnOptionClicked(int index)
        {
            if (!_optionsVisible || index < 0 || index >= _optionCount) return;
            Action<int> callback = _selectCallback;
            HideOptions();
            callback?.Invoke(index);
        }

        void SelectOption(int index)
        {
            OnOptionClicked(index);
        }

        void SelectEvidence(int pageIndex)
        {
            if (!_evidenceVisible) return;
            int index = _evidencePage * EvidencePageSize + pageIndex;
            if (index < 0 || index >= _evidencePages.Length) return;
            Action<int> callback = _selectCallback;
            HideEvidence();
            callback?.Invoke(index);
        }

        void CancelEvidence()
        {
            if (!_evidenceVisible) return;
            Action cancel = _cancelEvidence;
            HideEvidence();
            cancel?.Invoke();
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
            canvas.sortingOrder = 150;

            if (GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // 同公聊：锁高度，超宽窗口只横向扩张，纵向布局不压缩。
            scaler.matchWidthOrHeight = 1f;

            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            GameObject fadeGo = NewUIObject(transform, "Fade");
            RectTransform fadeRect = (RectTransform)fadeGo.transform;
            fadeRect.anchorMin = Vector2.zero;
            fadeRect.anchorMax = Vector2.one;
            fadeRect.offsetMin = Vector2.zero;
            fadeRect.offsetMax = Vector2.zero;
            _fade = fadeGo.AddComponent<Image>();
            _fade.color = new Color(0f, 0f, 0f, 0f);
            _fade.raycastTarget = false;

            GameObject contentGo = NewUIObject(transform, "Content");
            RectTransform contentRect = (RectTransform)contentGo.transform;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            _content = contentGo.AddComponent<CanvasGroup>();

            // 说话人居中、情绪贴右：同高但必须分栏，否则长昵称会和右侧状态压在一起。
            // 全部单行 + 省略号，英文长标签不会再折行压到对话框。
            _speakerText = CreateText(contentGo.transform, "Speaker", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(760f, 48f),
                30f, TextAlignmentOptions.Center, new Color(1f, 0.84f, 0.48f));
            _speakerText.enableWordWrapping = false;
            _speakerText.overflowMode = TextOverflowModes.Ellipsis;

            _trustText = CreateText(contentGo.transform, "Emotion", new Vector2(1f, 1f),
                new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(520f, 50f),
                19f, TextAlignmentOptions.MidlineRight, new Color(0.88f, 0.90f, 1f));
            _trustText.enableWordWrapping = false;
            _trustText.overflowMode = TextOverflowModes.Ellipsis;

            BuildDialogueBox(contentGo.transform);
            _hintText = CreateText(contentGo.transform, "Hint", new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 590f), new Vector2(1400f, 40f),
                23f, TextAlignmentOptions.Center, new Color(1f, 0.86f, 0.56f));
            _hintText.enableWordWrapping = false;
            _hintText.overflowMode = TextOverflowModes.Ellipsis;

            BuildPanel(contentGo.transform);

            HidePanel();
            SetHint(string.Empty);
        }

        void BuildDialogueBox(Transform parent)
        {
            GameObject box = NewUIObject(parent, "DialogueBox");
            RectTransform rect = (RectTransform)box.transform;
            // 上下拉伸：顶部固定留出说话人标题，底部固定停在提示语上方。
            // 窗口变矮（超宽屏 / 小窗口）时只压缩对话框高度，不会让提示文字压进框里。
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(-760f, 640f);
            rect.offsetMax = new Vector2(760f, -112f);

            Image bg = box.AddComponent<Image>();
            bg.color = new Color(0.02f, 0.025f, 0.035f, 0.78f);
            bg.raycastTarget = false;

            // 历史区与当前台词区按比例分区，各自固定高度，长文本用省略号收尾。
            // 历史区一条台词只占一行，避免英文长句换行后把当前台词顶出框外。
            _historyText = CreateText(box.transform, "History", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(36f, -16f), new Vector2(1448f, 116f),
                21f, TextAlignmentOptions.TopLeft, new Color(0.72f, 0.76f, 0.82f, 0.9f));
            _historyText.enableWordWrapping = false;
            _historyText.overflowMode = TextOverflowModes.Ellipsis;
            _historyText.lineSpacing = 4f;
            RectTransform historyRect = _historyText.rectTransform;
            historyRect.anchorMin = new Vector2(0f, 0.55f);
            historyRect.anchorMax = new Vector2(1f, 1f);
            historyRect.offsetMin = new Vector2(36f, 6f);
            historyRect.offsetMax = new Vector2(-36f, -12f);

            _lineText = CreateText(box.transform, "Line", new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(36f, 16f), new Vector2(1448f, 140f),
                28f, TextAlignmentOptions.TopLeft, Color.white);
            _lineText.overflowMode = TextOverflowModes.Ellipsis;
            _lineText.lineSpacing = 2f;
            RectTransform lineRect = _lineText.rectTransform;
            lineRect.anchorMin = new Vector2(0f, 0f);
            lineRect.anchorMax = new Vector2(1f, 0.55f);
            lineRect.offsetMin = new Vector2(36f, 14f);
            lineRect.offsetMax = new Vector2(-36f, -6f);
        }

        void BuildPanel(Transform parent)
        {
            _panel = NewUIObject(parent, "ChatPanel");
            RectTransform rect = (RectTransform)_panel.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 16f);
            rect.sizeDelta = new Vector2(920f, 560f);

            Image bg = _panel.AddComponent<Image>();
            bg.color = new Color(0.035f, 0.04f, 0.055f, 0.88f);
            bg.raycastTarget = false;

            _panelTitle = CreateText(_panel.transform, "PanelTitle",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -20f),
                new Vector2(840f, 40f), 24f, TextAlignmentOptions.Center,
                new Color(1f, 0.82f, 0.46f));

            for (int i = 0; i < _optionButtons.Length; i++)
            {
                int index = i;
                _optionButtons[i] = CreateButton(_panel.transform, $"ChatOption_{i + 1}",
                    new Vector2(0f, 190f - i * 58f), new Vector2(840f, 54f), out _optionLabels[i],
                    TextAlignmentOptions.Center, 22f);
                _optionButtons[i].onClick.AddListener(() => OnOptionClicked(index));
            }

            for (int i = 0; i < EvidencePageSize; i++)
            {
                int index = i;
                float x = i < 4 ? -215f : 215f;
                float y = 156f - (i % 4) * 84f;
                // 英文证据是「名称（出处）| 理由」三段，至少三行，行高必须留够。
                _evidenceButtons[i] = CreateButton(_panel.transform, $"ChatEvidence_{i + 1}",
                    new Vector2(x, y), new Vector2(410f, 74f), out _evidenceLabels[i],
                    TextAlignmentOptions.MidlineLeft, 18f);
                _evidenceButtons[i].onClick.AddListener(() => SelectEvidence(index));
            }

            _evidencePageText = CreateText(_panel.transform, "EvidencePage",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f),
                new Vector2(840f, 36f), 20f, TextAlignmentOptions.Center,
                new Color(0.78f, 0.80f, 0.86f));
        }

        void HidePanel()
        {
            if (_panel != null) _panel.SetActive(false);
        }

        static GameObject NewUIObject(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, Vector2 anchor,
            Vector2 pivot, Vector2 anchoredPos, Vector2 size, float fontSize,
            TextAlignmentOptions align, Color color)
        {
            GameObject go = NewUIObject(parent, name);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = color;
            text.enableWordWrapping = true;
            ChineseFontUtility.Apply(text);
            return text;
        }

        static Button CreateButton(Transform parent, string name, Vector2 anchoredPos,
            Vector2 size, out TextMeshProUGUI label,
            TextAlignmentOptions align = TextAlignmentOptions.Center, float fontSize = 25f)
        {
            GameObject go = NewUIObject(parent, name);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.10f, 0.16f, 0.22f, 0.96f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.82f, 0.90f, 1f);
            colors.pressedColor = new Color(0.55f, 0.68f, 0.82f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.6f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            label = CreateText(go.transform, "Label", new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, size, fontSize,
                align, new Color(0.92f, 0.95f, 1f));
            label.margin = new Vector4(12f, 0f, 8f, 0f);
            return button;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;

            GameObject go = new GameObject("PrivateChatEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }
    }
}
