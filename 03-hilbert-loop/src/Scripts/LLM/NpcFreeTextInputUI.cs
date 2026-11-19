using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家自由发言输入框（私聊 / 会议共用）。
    /// 不依赖预制体：运行时建 Canvas，sortingOrder=300，压在所有聊天 HUD 之上。
    /// Enter 提交，Esc 取消；提交后立刻关闭，避免与底层的数字键选项抢输入。
    /// </summary>
    public class NpcFreeTextInputUI : MonoBehaviour
    {
        public static NpcFreeTextInputUI Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        CanvasGroup _group;
        TMP_InputField _input;
        TextMeshProUGUI _title;
        TextMeshProUGUI _hint;
        TextMeshProUGUI _preview;
        Action<string> _onSubmit;
        Action _onCancel;
        bool _open;
        bool _submitting;
        bool _built;
        float _openedAt;

        /// <summary>打开输入框。返回 false 表示当前已有输入框在等待。</summary>
        public static bool Open(string title, string hint, Action<string> onSubmit, Action onCancel = null)
        {
            if (Instance != null && Instance._open) return false;
            EnsureInstance();
            Instance.OpenInternal(title, hint, onSubmit, onCancel);
            return true;
        }

        public static void Close(bool invokeCancel)
        {
            if (Instance == null) return;
            Instance.CloseInternal(invokeCancel);
        }

        static void EnsureInstance()
        {
            if (Instance != null) return;

            GameObject existing = GameObject.Find("NpcFreeTextInput");
            if (existing != null)
            {
                Instance = existing.GetComponent<NpcFreeTextInputUI>();
                if (Instance != null) return;
            }

            GameObject go = new GameObject("NpcFreeTextInput");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<NpcFreeTextInputUI>();
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
        }

        void Update()
        {
            if (!_open) return;
            if (Time.unscaledTime - _openedAt < 0.15f) return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CloseInternal(true);
            }
        }

        void OpenInternal(string title, string hint, Action<string> onSubmit, Action onCancel)
        {
            if (!_built) BuildIfNeeded();

            _open = true;
            _submitting = false;
            _openedAt = Time.unscaledTime;
            SetClockPaused(true);
            _onSubmit = onSubmit;
            _onCancel = onCancel;

            if (_title != null)
                _title.text = Localization.T(string.IsNullOrEmpty(title) ? "你想说什么？" : title);
            if (_hint != null)
                _hint.text = Localization.T(string.IsNullOrEmpty(hint)
                    ? "Enter 发送　·　Esc 取消"
                    : hint);
            if (_preview != null) _preview.text = string.Empty;
            if (_input != null)
            {
                _input.text = string.Empty;
                _input.interactable = true;
            }

            if (_group != null)
            {
                _group.alpha = 1f;
                _group.interactable = true;
                _group.blocksRaycasts = true;
            }

            StartCoroutine(FocusRoutine());
        }

        IEnumerator FocusRoutine()
        {
            yield return null;
            if (!_open || _input == null) yield break;

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_input.gameObject);
            _input.ActivateInputField();
            _input.caretPosition = 0;
            _input.ForceLabelUpdate();

            // TMP 的插入光标是运行时创建的兄弟节点；显式提到最上层，
            // 否则它会压在文字下面，看起来像「没有光标」。
            Transform[] children = _input.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].name != "Caret") continue;
                children[i].SetAsLastSibling();
                break;
            }
        }

        void CloseInternal(bool invokeCancel)
        {
            if (!_open) return;

            _open = false;
            _submitting = false;
            SetClockPaused(false);
            Action cancel = _onCancel;
            _onSubmit = null;
            _onCancel = null;

            if (_input != null)
            {
                _input.DeactivateInputField();
                _input.text = string.Empty;
            }
            if (EventSystem.current != null
                && _input != null
                && EventSystem.current.currentSelectedGameObject == _input.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            if (_group != null)
            {
                _group.alpha = 0f;
                _group.interactable = false;
                _group.blocksRaycasts = false;
            }

            if (invokeCancel) cancel?.Invoke();
        }

        void OnDestroy()
        {
            // 输入框被意外销毁时也要把表放开，否则倒计时会一直停着。
            if (_open) SetClockPaused(false);
        }

        static void SetClockPaused(bool paused)
        {
            if (GameManager.Instance != null) GameManager.Instance.ClockPaused = paused;
        }

        void OnInputSubmit(string _)
        {
            Submit();
        }

        void Submit()
        {
            if (!_open || _submitting || _input == null) return;

            string text = (_input.text ?? "").Trim();
            if (text.Length == 0)
            {
                if (_hint != null) _hint.text = Localization.T("先写一句话再发送。Enter 发送　·　Esc 取消");
                _input.ActivateInputField();
                return;
            }

            if (text.Length > 120) text = text.Substring(0, 120);

            _submitting = true;
            Action<string> callback = _onSubmit;
            _open = false;
            SetClockPaused(false);
            _onSubmit = null;
            _onCancel = null;

            if (_input != null)
            {
                _input.DeactivateInputField();
                _input.text = string.Empty;
            }
            if (EventSystem.current != null
                && _input != null
                && EventSystem.current.currentSelectedGameObject == _input.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.interactable = false;
                _group.blocksRaycasts = false;
            }

            callback?.Invoke(text);
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
            canvas.sortingOrder = 300;

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

            // 遮罩要足够不透明：半透明时左下角背包、底部交互提示会透上来，
            // 与输入框文字叠在一起，看起来像 UI 重叠。
            Image dim = root.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.88f);
            dim.raycastTarget = true;

            _group = root.AddComponent<CanvasGroup>();

            GameObject panel = NewUIObject(root.transform, "Panel");
            RectTransform panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0f);
            panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0f, 90f);
            panelRect.sizeDelta = new Vector2(1480f, 400f);

            Image panelBg = panel.AddComponent<Image>();
            panelBg.color = new Color(0.03f, 0.035f, 0.05f, 0.97f);
            panelBg.raycastTarget = true;

            _title = CreateText(panel.transform, "Title", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(1400f, 44f),
                28f, TextAlignmentOptions.Center, new Color(1f, 0.84f, 0.48f));

            _hint = CreateText(panel.transform, "Hint", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -66f), new Vector2(1400f, 30f),
                20f, TextAlignmentOptions.Center, new Color(0.78f, 0.82f, 0.9f));

            // 完整预览：输入框里只显示可见行，这里始终展示全部已输入文字，
            // 方便确认自己到底打了什么、有没有打错。
            _preview = CreateText(panel.transform, "Preview", new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(1400f, 82f),
                20f, TextAlignmentOptions.TopLeft, new Color(0.72f, 0.86f, 0.96f));

            GameObject inputBg = NewUIObject(panel.transform, "Input");
            RectTransform inputRect = (RectTransform)inputBg.transform;
            inputRect.anchorMin = inputRect.anchorMax = new Vector2(0.5f, 0f);
            inputRect.pivot = new Vector2(0.5f, 0f);
            inputRect.anchoredPosition = new Vector2(0f, 86f);
            inputRect.sizeDelta = new Vector2(1360f, 124f);

            Image inputImage = inputBg.AddComponent<Image>();
            inputImage.color = new Color(0.09f, 0.11f, 0.15f, 1f);
            inputImage.raycastTarget = true;

            GameObject textArea = NewUIObject(inputBg.transform, "Text Area");
            RectTransform areaRect = (RectTransform)textArea.transform;
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.offsetMin = new Vector2(18f, 8f);
            areaRect.offsetMax = new Vector2(-18f, -8f);
            textArea.AddComponent<RectMask2D>();

            GameObject textGo = NewUIObject(textArea.transform, "Text");
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            TextMeshProUGUI text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.color = Color.white;
            text.enableWordWrapping = true; // 自动换行：120 字全部留在框内，而不是被裁掉
            ChineseFontUtility.Apply(text);

            GameObject placeholderGo = NewUIObject(textArea.transform, "Placeholder");
            RectTransform placeholderRect = (RectTransform)placeholderGo.transform;
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;

            TextMeshProUGUI placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.fontSize = 24f;
            placeholder.alignment = TextAlignmentOptions.TopLeft;
            placeholder.color = new Color(0.55f, 0.58f, 0.64f, 0.8f);
            placeholder.enableWordWrapping = true;
            placeholder.text = Localization.T("直接说你想说的话……（最多 120 字，可换行）");
            ChineseFontUtility.Apply(placeholder);

            _input = inputBg.AddComponent<TMP_InputField>();
            _input.textViewport = areaRect;
            _input.textComponent = text;
            _input.placeholder = placeholder;
            // MultiLineSubmit：Enter 发送，Shift+Enter 换行；文本自动换行并可滚动。
            _input.lineType = TMP_InputField.LineType.MultiLineSubmit;
            _input.characterLimit = 120;
            _input.caretWidth = 3;
            _input.caretBlinkRate = 0.45f;
            _input.customCaretColor = true;
            _input.caretColor = new Color(1f, 0.86f, 0.35f, 1f);
            _input.selectionColor = new Color(0.55f, 0.72f, 1f, 0.35f);
            _input.onSubmit.AddListener(OnInputSubmit);
            _input.onValueChanged.AddListener(value =>
            {
                if (_preview != null) _preview.text = value ?? string.Empty;
            });

            // TMP_InputField 的插入光标是在 OnEnable 里创建的；运行时 AddComponent
            // 时 textComponent 尚未赋值，那一刻会直接跳过创建，之后再也不补。
            // 配置完成后重新启用一次，让 OnEnable 在 textComponent 就绪的状态下创建 Caret。
            _input.enabled = false;
            _input.enabled = true;

            Button send = CreateButton(panel.transform, "Send", Localization.T("发送（Enter）"),
                new Vector2(170f, 16f), new Vector2(240f, 52f), Submit);
            send.GetComponent<Image>().color = new Color(0.24f, 0.42f, 0.32f, 1f);

            CreateButton(panel.transform, "Cancel", Localization.T("取消（Esc）"),
                new Vector2(-170f, 16f), new Vector2(240f, 52f),
                () => CloseInternal(true));

            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
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
            text.raycastTarget = false;
            ChineseFontUtility.Apply(text);
            return text;
        }

        static Button CreateButton(Transform parent, string name, string label,
            Vector2 anchoredPos, Vector2 size, Action onClick)
        {
            GameObject go = NewUIObject(parent, name);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.16f, 0.20f, 0.27f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.85f, 0.92f, 1f);
            colors.pressedColor = new Color(0.6f, 0.72f, 0.85f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            if (onClick != null) button.onClick.AddListener(() => onClick());

            TextMeshProUGUI text = CreateText(go.transform, "Label", new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, size, 24f,
                TextAlignmentOptions.Center, new Color(0.94f, 0.96f, 1f));
            text.text = label;
            return button;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;

            GameObject go = new GameObject("NpcFreeTextEventSystem",
                typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }
    }
}
