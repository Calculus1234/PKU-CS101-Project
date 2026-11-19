using System;
using System.Collections.Generic;
using System.Collections;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 圆桌会议 UI（模块三 3.4 / 模块四）：
    /// - 顶部：当前发言人 / 会议提示 / 全场敌意的模糊描述（不显示任何数值）；
    /// - 中部：深色对话框，保留最近三句历史台词，当前台词高亮；
    /// - 底部：玩家发言最多 7 个选项（鼠标点击或数字键 1~7）；
    /// - 右侧：证据面板（R 键打开），双列 8 件一页，Q / E 翻页，不再截断证据。
    /// 所有控件在运行时构建，字体走 ChineseFontUtility，避免场景预制体依赖。
    /// </summary>
    public class MeetingUI : MonoBehaviour
    {
        [Header("淡入淡出")]
        public float fadeDuration = 0.35f;

        const int EvidencePageSize = 8;

        CanvasGroup _content;
        Image _fade;
        TextMeshProUGUI _speakerText;
        TextMeshProUGUI _bannerText;
        TextMeshProUGUI _suspicionText;
        TextMeshProUGUI _historyText;
        TextMeshProUGUI _lineText;
        TextMeshProUGUI _hintText;

        GameObject _optionsPanel;
        readonly Button[] _optionButtons = new Button[9];
        readonly TextMeshProUGUI[] _optionLabels = new TextMeshProUGUI[9];

        GameObject _evidencePanel;
        readonly Button[] _evidenceButtons = new Button[EvidencePageSize];
        readonly TextMeshProUGUI[] _evidenceLabels = new TextMeshProUGUI[EvidencePageSize];
        TextMeshProUGUI _evidenceTitle;
        TextMeshProUGUI _evidencePageText;

        Action<int> _optionCallback;
        Action<int> _evidenceCallback;
        Action _evidenceCancel;

        string[] _evidencePages = Array.Empty<string>();
        string _evidenceTitleText;
        int _evidencePage;
        int _evidencePageCount;

        readonly List<string> _history = new List<string>();
        string _currentSpeaker = string.Empty;
        string _historySpeaker = string.Empty;
        string _lastLine = string.Empty;

        int _optionCount;
        bool _optionsVisible;
        bool _optionsSuspended;
        bool _evidenceVisible;
        bool _evidenceSelected;
        bool _built;
        Coroutine _fadeRoutine;

        public bool OptionsVisible => _optionsVisible;
        public bool EvidenceVisible => _evidenceVisible;

        void Awake()
        {
            BuildIfNeeded();
            SetVisible(false);
            SetFadeImmediate(0f);
        }

        void Update()
        {
            if (NpcFreeTextInputUI.IsOpen) return;

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
                for (int i = 0; i < EvidencePageSize; i++)
                {
                    if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    {
                        SelectEvidence(i);
                        return;
                    }
                }
            }

            if (!_optionsVisible || _optionsSuspended) return;
            for (int i = 0; i < _optionCount; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                {
                    SelectOption(i);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        public void SetVisible(bool visible)
        {
            if (!_built) BuildIfNeeded();
            if (_content == null) return;
            _content.alpha = visible ? 1f : 0f;
            _content.interactable = visible;
            _content.blocksRaycasts = visible;
            if (!visible)
            {
                HideOptions();
                HideEvidence();
                SetSpeaker(string.Empty);
                SetLine(string.Empty);
                SetHint(string.Empty);
                SetBanner(string.Empty);
                _history.Clear();
                _lastLine = string.Empty;
                RefreshHistory();
            }
            else
            {
                _history.Clear();
                _lastLine = string.Empty;
                RefreshHistory();
            }
        }

        public void SetSpeaker(string speaker)
        {
            // 历史区会直接复用这个标签，所以在入口就翻译好，
            // 否则英文模式下历史记录会留着 "[中立/摇摆]" 这类中文。
            _currentSpeaker = Localization.Translate(speaker ?? string.Empty);
            // 历史区只写号码，不写 "[中立/摇摆]　声音里夹着电流声" 这种长状态，
            // 否则每条历史都被前缀吃掉大半行。
            _historySpeaker = ShortSpeaker(_currentSpeaker);
            if (_speakerText != null) _speakerText.text = _currentSpeaker;
        }

        /// <summary>把 "No. 4  [Neutral/Swing]  ..." 压成 "No. 4"。</summary>
        static string ShortSpeaker(string speaker)
        {
            if (string.IsNullOrEmpty(speaker)) return speaker;
            int cut = speaker.IndexOf('\u3000');
            int dbl = speaker.IndexOf("  ", StringComparison.Ordinal);
            if (cut < 0 || (dbl >= 0 && dbl < cut)) cut = dbl;
            return cut > 0 ? speaker.Substring(0, cut).TrimEnd() : speaker;
        }

        public void SetLine(string text)
        {
            bool selfLabeled = !string.IsNullOrEmpty(text) &&
                               (text.StartsWith("有人：") || text.StartsWith("（") ||
                                text.StartsWith("Someone: ") || text.StartsWith("Nobody: ") ||
                                text.StartsWith("You: ") || text.StartsWith("("));

            if (!string.IsNullOrEmpty(text)) text = Localization.Translate(text);

            // 说话人已经单独显示在对话框上方，台词里再带一次 "8号： / No. 8：" 很蠢。
            // Yarn 台词、LLM 兜底、打断台词都自带前缀，这里统一在显示层去掉。
            string display = StripSpeakerEcho(text);
            if (_lineText != null) _lineText.text = display ?? string.Empty;

            if (string.IsNullOrEmpty(text) || text == _lastLine) return;
            _lastLine = text;

            // 历史区同样用去掉前缀后的台词，否则会留下 "No. 1: No. 1: ..."。
            string entry = selfLabeled || string.IsNullOrEmpty(_historySpeaker)
                ? display
                : _historySpeaker + (Localization.IsEnglish ? ": " : "：") + display;

            _history.Add(entry);
            while (_history.Count > 4) _history.RemoveAt(0);
            RefreshHistory();
        }

        static readonly Regex _speakerNumberRegex = new Regex(@"(\d+)", RegexOptions.CultureInvariant);
        static readonly Regex _lineSpeakerPrefixRegex =
            new Regex(@"^(?:No\.\s*)?(\d+)\s*号?\s*[:：]\s*", RegexOptions.CultureInvariant);

        /// <summary>台词开头重复了当前说话人的号码时，去掉这段前缀。</summary>
        string StripSpeakerEcho(string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_currentSpeaker)) return text;
            Match speaker = _speakerNumberRegex.Match(_currentSpeaker);
            if (!speaker.Success) return text;

            Match prefix = _lineSpeakerPrefixRegex.Match(text);
            if (!prefix.Success || prefix.Groups[1].Value != speaker.Groups[1].Value) return text;
            return text.Substring(prefix.Length);
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

        public void SetBanner(string text)
        {
            if (_bannerText != null)
            {
                _bannerText.text = string.IsNullOrEmpty(text) ? text : Localization.Translate(text);
                _bannerText.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        /// <summary>玩家只看到模糊的会场气氛；具体数值留在内部计算。</summary>
        public void SetSuspicion(int value)
        {
            if (_suspicionText == null) return;
            _suspicionText.text = EmotionLabels.MeetingMood(Mathf.Clamp(value, 0, 100));
        }

        public void ShowOptions(string[] labels, Action<int> onSelected)
        {
            if (!_built) BuildIfNeeded();
            _optionCallback = onSelected;
            _optionCount = Mathf.Clamp(labels != null ? labels.Length : 0, 0, _optionButtons.Length);
            _optionsVisible = true;
            _optionsSuspended = false;

            for (int i = 0; i < _optionButtons.Length; i++)
            {
                if (_optionButtons[i] == null || _optionLabels[i] == null) continue;
                bool show = i < _optionCount;
                _optionButtons[i].gameObject.SetActive(show);
                if (show) _optionLabels[i].text = $"{i + 1}. {Localization.Translate(labels[i])}";
            }

            if (_optionsPanel != null) _optionsPanel.SetActive(true);
            if (_evidencePanel != null) _evidencePanel.SetActive(false);
            SetHint($"按 1~{Mathf.Max(1, _optionCount)} 选择，按 R 出示证据");
        }

        public void HideOptions()
        {
            _optionsVisible = false;
            _optionsSuspended = false;
            _optionCallback = null;
            if (_optionsPanel != null) _optionsPanel.SetActive(false);
        }

        /// <summary>证据台词播放期间恢复玩家选项输入（面板会重新出现）。</summary>
        public void ResumeOptionsInput()
        {
            _optionsSuspended = false;
            if (_optionsPanel != null) _optionsPanel.SetActive(_optionsVisible);
            if (_optionsVisible) SetHint($"按 1~{Mathf.Max(1, _optionCount)} 选择，按 R 出示证据");
        }

        public void ShowEvidence(string[] labels, Action<int> onSelected, Action onCancel,
            string title = null)
        {
            if (!_built) BuildIfNeeded();
            _evidenceCallback = onSelected;
            _evidenceCancel = onCancel;
            _evidenceVisible = true;
            _evidenceSelected = false;
            _evidencePages = labels ?? Array.Empty<string>();
            _evidencePage = 0;
            _evidencePageCount = Mathf.Max(1,
                Mathf.CeilToInt(_evidencePages.Length / (float)EvidencePageSize));
            _evidenceTitleText = title;

            // 证据面板覆盖在玩家选项之上：逻辑上保留选项，但先隐藏并锁住输入，
            // 避免证据台词播放期间数字键误选玩家选项。
            _optionsSuspended = true;
            if (_optionsPanel != null) _optionsPanel.SetActive(false);

            RenderEvidencePage();
            if (_evidencePanel != null) _evidencePanel.SetActive(true);
            SetHint("选择一件证据——它会改变其他人对指控者的看法");
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

            if (_evidenceTitle != null)
                _evidenceTitle.text = Localization.Translate(string.IsNullOrEmpty(_evidenceTitleText)
                    ? $"出示证据（共 {_evidencePages.Length} 件）"
                    : _evidenceTitleText);

            if (_evidencePageText != null)
            {
                _evidencePageText.text = Localization.Translate(_evidencePageCount > 1
                    ? $"第 {_evidencePage + 1} / {_evidencePageCount} 页　Q / E 翻页　Esc 取消"
                    : "数字键 / 鼠标选择　Esc 取消");
                _evidencePageText.gameObject.SetActive(true);
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
            _evidenceCallback = null;
            _evidenceCancel = null;
            _evidenceTitleText = null;
            if (_evidencePanel != null) _evidencePanel.SetActive(false);

            // 取消：恢复玩家选项；已选中证据：保持输入锁定，等证据台词播完由
            // MeetingSystem 调 ResumeOptionsInput() 再恢复，避免误选。
            if (!_evidenceSelected && _optionsVisible)
            {
                _optionsSuspended = false;
                if (_optionsPanel != null) _optionsPanel.SetActive(true);
                SetHint($"按 1~{Mathf.Max(1, _optionCount)} 选择，按 R 出示证据");
            }
            else if (!_optionsVisible)
            {
                // NPC 回合出示证据：没有待恢复的选项，直接解除锁，避免状态残留。
                _optionsSuspended = false;
            }
        }

        public void SetFadeImmediate(float alpha)
        {
            if (!_built) BuildIfNeeded();
            if (_fadeRoutine != null)
            {
                StopCoroutine(_fadeRoutine);
                _fadeRoutine = null;
            }
            if (_fade != null)
            {
                Color c = _fade.color;
                c.a = Mathf.Clamp01(alpha);
                _fade.color = c;
            }
        }

        public void FadeTo(float alpha, float duration)
        {
            if (!_built) BuildIfNeeded();
            if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
            _fadeRoutine = StartCoroutine(FadeRoutine(alpha, duration));
        }

        IEnumerator FadeRoutine(float target, float duration)
        {
            if (_fade == null) yield break;
            Color c = _fade.color;
            float start = c.a;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, duration);
                c.a = Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
                _fade.color = c;
                yield return null;
            }
            c.a = target;
            _fade.color = c;
            _fadeRoutine = null;
        }

        void SelectOption(int index)
        {
            if (!_optionsVisible || _optionsSuspended || index < 0 || index >= _optionCount) return;
            Action<int> callback = _optionCallback;
            HideOptions();
            callback?.Invoke(index);
        }

        void SelectEvidence(int pageIndex)
        {
            if (!_evidenceVisible) return;
            int index = _evidencePage * EvidencePageSize + pageIndex;
            if (index < 0 || index >= _evidencePages.Length) return;
            Action<int> callback = _evidenceCallback;
            _evidenceSelected = true;
            HideEvidence();
            callback?.Invoke(index);
        }

        void CancelEvidence()
        {
            if (!_evidenceVisible) return;
            Action cancel = _evidenceCancel;
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
            canvas.sortingOrder = 100;

            if (GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // 以高度为基准：超宽 / 矮窗口只横向扩张画布，纵向始终保留 1080
            // 参考单位。用 0.5 折中会把上下布局一起压扁，说话人、横幅、历史
            // 台词会挤到同一块区域。
            scaler.matchWidthOrHeight = 1f;


            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            // 黑场（不拦截鼠标）
            GameObject fadeGo = NewUIObject(transform, "Fade");
            RectTransform fadeRect = (RectTransform)fadeGo.transform;
            fadeRect.anchorMin = Vector2.zero;
            fadeRect.anchorMax = Vector2.one;
            fadeRect.offsetMin = Vector2.zero;
            fadeRect.offsetMax = Vector2.zero;
            _fade = fadeGo.AddComponent<Image>();
            _fade.color = new Color(0f, 0f, 0f, 0f);
            _fade.raycastTarget = false;

            // 内容根（CanvasGroup 统一显隐）
            GameObject contentGo = NewUIObject(transform, "Content");
            RectTransform contentRect = (RectTransform)contentGo.transform;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            _content = contentGo.AddComponent<CanvasGroup>();

            // 说话人居中、嫌疑度贴右：同高但必须分栏，否则长名字会和右侧文本压字。
            // 三行全部强制单行 + 省略号：英文的说话人标签比中文长得多，
            // 一旦换行就会向下压到横幅，再压进对话框。
            _speakerText = CreateText(contentGo.transform, "Speaker", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(1100f, 48f),
                30f, TextAlignmentOptions.Center, new Color(1f, 0.84f, 0.48f));
            _speakerText.enableWordWrapping = false;
            _speakerText.overflowMode = TextOverflowModes.Ellipsis;

            _bannerText = CreateText(contentGo.transform, "Banner", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -72f), new Vector2(1700f, 38f),
                22f, TextAlignmentOptions.Center, new Color(0.95f, 0.62f, 0.62f));
            _bannerText.enableWordWrapping = false;
            _bannerText.overflowMode = TextOverflowModes.Ellipsis;

            _suspicionText = CreateText(contentGo.transform, "Suspicion", new Vector2(1f, 1f),
                new Vector2(1f, 1f), new Vector2(-24f, -20f), new Vector2(340f, 50f),
                21f, TextAlignmentOptions.MidlineRight, new Color(0.9f, 0.9f, 1f));
            _suspicionText.enableWordWrapping = false;
            _suspicionText.overflowMode = TextOverflowModes.Ellipsis;

            BuildDialogueBox(contentGo.transform);
            _hintText = CreateText(contentGo.transform, "Hint", new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 512f), new Vector2(1600f, 46f),
                24f, TextAlignmentOptions.Center, new Color(1f, 0.86f, 0.56f));
            _hintText.enableWordWrapping = false;
            _hintText.overflowMode = TextOverflowModes.Ellipsis;

            BuildOptionsPanel(contentGo.transform);
            BuildEvidencePanel(contentGo.transform);

            HideOptions();
            HideEvidence();
            SetBanner(string.Empty);
            SetHint(string.Empty);
        }

        void BuildDialogueBox(Transform parent)
        {
            GameObject box = NewUIObject(parent, "DialogueBox");
            RectTransform rect = (RectTransform)box.transform;
            // 上下拉伸：顶部固定让开说话人 / 横幅，底部固定停在提示语上方。
            // 窗口变矮时只压缩对话框高度，提示文字不会压进框里。
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(-760f, 572f);
            rect.offsetMax = new Vector2(760f, -126f);

            Image bg = box.AddComponent<Image>();
            bg.color = new Color(0.02f, 0.02f, 0.03f, 0.74f);
            bg.raycastTarget = false;

            // 历史与当前台词按比例分区，超出用省略号，绝不越界压到对方身上。
            // 历史区一条台词只占一行：英文句子很长，允许换行会吃掉两三行，
            // 把当前台词顶出对话框。
            _historyText = CreateText(box.transform, "History", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(36f, -18f), new Vector2(1448f, 120f),
                21f, TextAlignmentOptions.TopLeft, new Color(0.72f, 0.75f, 0.80f, 0.9f));
            _historyText.enableWordWrapping = false;
            _historyText.overflowMode = TextOverflowModes.Ellipsis;
            _historyText.lineSpacing = 4f;
            RectTransform historyRect = _historyText.rectTransform;
            historyRect.anchorMin = new Vector2(0f, 0.55f);
            historyRect.anchorMax = new Vector2(1f, 1f);
            historyRect.offsetMin = new Vector2(36f, 6f);
            historyRect.offsetMax = new Vector2(-36f, -14f);

            _lineText = CreateText(box.transform, "Line", new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(36f, 18f), new Vector2(1448f, 150f),
                28f, TextAlignmentOptions.TopLeft, Color.white);
            _lineText.overflowMode = TextOverflowModes.Ellipsis;
            _lineText.lineSpacing = 2f;
            RectTransform lineRect = _lineText.rectTransform;
            lineRect.anchorMin = new Vector2(0f, 0f);
            lineRect.anchorMax = new Vector2(1f, 0.55f);
            lineRect.offsetMin = new Vector2(36f, 14f);
            lineRect.offsetMax = new Vector2(-36f, -6f);
        }

        void BuildOptionsPanel(Transform parent)
        {
            _optionsPanel = NewUIObject(parent, "OptionsPanel");
            RectTransform rect = (RectTransform)_optionsPanel.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 30f);
            rect.sizeDelta = new Vector2(900f, 470f);

            Image bg = _optionsPanel.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.03f, 0.05f, 0.78f);
            bg.raycastTarget = false;

            for (int i = 0; i < _optionButtons.Length; i++)
            {
                int index = i;
                // 英文选项比中文长，行高留到 50：允许两行但不会压到下一项。
                _optionButtons[i] = CreateButton(_optionsPanel.transform, $"Option_{i + 1}",
                    new Vector2(0f, 200f - i * 50f), new Vector2(860f, 48f), out _optionLabels[i],
                    TextAlignmentOptions.MidlineLeft, 20f);
                _optionButtons[i].onClick.AddListener(() => SelectOption(index));
            }
        }

        void BuildEvidencePanel(Transform parent)
        {
            _evidencePanel = NewUIObject(parent, "EvidencePanel");
            RectTransform rect = (RectTransform)_evidencePanel.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-30f, 0f);
            rect.sizeDelta = new Vector2(1040f, 560f);

            Image bg = _evidencePanel.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.04f, 0.08f, 0.94f);
            bg.raycastTarget = false;

            _evidenceTitle = CreateText(_evidencePanel.transform, "EvidenceTitle",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f),
                new Vector2(980f, 46f), 27f, TextAlignmentOptions.Center,
                new Color(1f, 0.84f, 0.48f));

            for (int i = 0; i < EvidencePageSize; i++)
            {
                int index = i;
                float x = i < 4 ? -245f : 245f;
                float y = 156f - (i % 4) * 86f;
                // 英文证据要「名称（出处）| 理由」三行才放得下，行高必须留够，
                // 否则第三行会压到下一条证据上。
                _evidenceButtons[i] = CreateButton(_evidencePanel.transform, $"Evidence_{i + 1}",
                    new Vector2(x, y), new Vector2(470f, 76f), out _evidenceLabels[i],
                    TextAlignmentOptions.MidlineLeft, 18f);
                _evidenceButtons[i].onClick.AddListener(() => SelectEvidence(index));
            }

            _evidencePageText = CreateText(_evidencePanel.transform, "EvidencePage",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f),
                new Vector2(980f, 40f), 21f, TextAlignmentOptions.Center,
                new Color(0.78f, 0.80f, 0.86f));
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
            TextAlignmentOptions align = TextAlignmentOptions.Center, float fontSize = 26f)
        {
            GameObject go = NewUIObject(parent, name);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.24f, 0.07f, 0.08f, 0.96f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.82f, 0.82f);
            colors.pressedColor = new Color(0.72f, 0.52f, 0.52f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.6f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            label = CreateText(go.transform, "Label", new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, size, fontSize,
                align, new Color(1f, 0.94f, 0.86f));
            label.margin = new Vector4(14f, 0f, 10f, 0f);
            return button;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;

            GameObject go = new GameObject("MeetingEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }
    }
}
