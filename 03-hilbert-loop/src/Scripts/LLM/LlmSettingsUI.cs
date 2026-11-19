using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// F9 打开的大模型设置面板：本地 GGUF / 云端 OpenAI 兼容接口二选一。
    /// 面板运行时自建，不依赖场景预制体；密钥只写本机 PlayerPrefs。
    /// </summary>
    public class LlmSettingsUI : MonoBehaviour
    {
        public static LlmSettingsUI Instance { get; private set; }

        CanvasGroup _group;
        TMP_InputField _urlInput;
        TMP_InputField _modelInput;
        TMP_InputField _keyInput;
        TextMeshProUGUI _modeHint;
        TextMeshProUGUI _localLabel;
        TextMeshProUGUI _cloudLabel;
        Image _localBg;
        Image _cloudBg;

        Image _fastBg;
        Image _smartBg;
        TextMeshProUGUI _fastLabel;
        TextMeshProUGUI _smartLabel;
        TextMeshProUGUI _smartWarning;

        GameObject _loadingOverlay;
        TextMeshProUGUI _loadingText;

        bool _built;
        bool _open;
        bool _cloudMode;
        bool _smartModel;
        bool _reloading;

        public bool IsOpen => _open;

        const float PanelWidth = 1000f;
        const float PanelHeight = 840f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("LlmSettingsUI");
            DontDestroyOnLoad(go);
            go.AddComponent<LlmSettingsUI>();
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
            // 面板默认关闭：必须同时关闭射线遮挡，否则这个透明的全屏 Canvas
            // 会吃掉主菜单 / 游戏里的所有鼠标点击。
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.interactable = false;
                _group.blocksRaycasts = false;
            }
        }

        void Update()
        {
            // 主菜单是纯监控推演，不开放开发者面板。
            if (GameMode.MenuActive) return;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                // 换模型期间不接受新输入，避免重复卸载。
                if (_reloading) return;
                // 正在自由打字时把 F9 让给输入框，避免误开设置。
                if (NpcFreeTextInputUI.IsOpen) return;
                if (_open) Close();
                else OpenPanel();
            }
        }

        /// <summary>
        /// 设置界面里的「模型设置」按钮调用：不依赖 F9，直接打开面板。
        /// 返回 false 表示面板还没初始化或正在换模型，调用方可以给出提示。
        /// </summary>
        public static bool OpenFromSettings()
        {
            if (Instance == null || Instance._reloading) return false;
            if (!Instance._open) Instance.OpenPanel();
            return true;
        }

        void OpenPanel()
        {
            if (!_built) BuildIfNeeded();
            _open = true;
            _cloudMode = LlmSettings.UseCloud;
            _smartModel = LlmSettings.UseSmartModel;

            if (_urlInput != null) _urlInput.text = LlmSettings.BaseUrl;
            if (_modelInput != null) _modelInput.text = LlmSettings.Model;
            if (_keyInput != null) _keyInput.text = LlmSettings.ApiKey;
            RefreshModeButtons();
            RefreshLocalModelButtons();

            if (_group != null)
            {
                _group.alpha = 1f;
                _group.interactable = true;
                _group.blocksRaycasts = true;
            }

            if (GameManager.Instance != null) GameManager.Instance.ClockPaused = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            RebuildTexts();
        }

        /// <summary>
        /// 面板是在 CanvasGroup.alpha=0 时构建的，动态中文字体在图集扩容后不会自动
        /// 回填这些隐藏文本的网格，首次打开会出现"有文字对象但看不见字"。
        /// 打开时统一强制重建一次。
        /// </summary>
        void RebuildTexts()
        {
            Canvas.ForceUpdateCanvases();
            TextMeshProUGUI[] texts = GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i] == null) continue;
                texts[i].ForceMeshUpdate(true, true);
            }
        }

        void Close()
        {
            _open = false;
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.interactable = false;
                _group.blocksRaycasts = false;
            }

            // 自由输入框还开着的时候要继续停表，不能在这里一刀切恢复。
            if (GameManager.Instance != null)
                GameManager.Instance.ClockPaused = NpcFreeTextInputUI.IsOpen;

            bool chatActive = PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive;
            if (!chatActive && !ShouldKeepCursorFree())
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        /// <summary>
        /// 主菜单、结局结算面板、自由输入框打开时，关闭模型面板后必须保留鼠标，
        /// 否则玩家会失去光标，无法继续点主菜单 / 结算按钮。
        /// </summary>
        static bool ShouldKeepCursorFree()
        {
            if (GameMode.MenuActive) return true;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return true;
            if (NpcFreeTextInputUI.IsOpen) return true;
            return false;
        }

        void SaveAndClose()
        {
            LlmSettings.UseCloud = _cloudMode;
            LlmSettings.BaseUrl = _urlInput != null ? _urlInput.text.Trim() : "";
            LlmSettings.Model = _modelInput != null ? _modelInput.text.Trim() : "";
            LlmSettings.ApiKey = _keyInput != null ? _keyInput.text.Trim() : "";

            string selected = _smartModel ? LlmSettings.ModelSmart : LlmSettings.ModelFast;
            bool needReload = selected != LlmSettings.LocalModel;
            LlmSettings.LocalModel = selected;

            bool ready = LlmSettings.CloudReady;
            Debug.Log(ready
                ? $"[LLM] 已切换到云端：{LlmSettings.Model} @ {LlmSettings.BaseUrl}"
                : (_cloudMode ? "[LLM] 云端配置不完整（地址/模型/密钥），会继续用本地模型。" : "[LLM] 已切换到本地模型。"));
            Debug.Log($"[LLM] 本地模型选择：{LLMNpcBrain.ModelLabel(selected)}" +
                      (needReload ? "（需要重载）" : "（未变化）"));

            Close();

            if (needReload) StartModelReload(selected);
        }

        void SelectLocal()
        {
            _cloudMode = false;
            RefreshModeButtons();
        }

        void SelectCloud()
        {
            _cloudMode = true;
            RefreshModeButtons();
        }

        void RefreshModeButtons()
        {
            if (_localBg != null)
                _localBg.color = _cloudMode ? new Color(0.10f, 0.12f, 0.17f) : new Color(0.55f, 0.42f, 0.16f);
            if (_cloudBg != null)
                _cloudBg.color = _cloudMode ? new Color(0.55f, 0.42f, 0.16f) : new Color(0.10f, 0.12f, 0.17f);
            if (_localLabel != null)
                _localLabel.color = _cloudMode ? new Color(0.7f, 0.74f, 0.82f) : Color.white;
            if (_cloudLabel != null)
                _cloudLabel.color = _cloudMode ? Color.white : new Color(0.7f, 0.74f, 0.82f);
            if (_modeHint != null)
                _modeHint.text = _cloudMode
                    ? Localization.T("云端模式：需要联网，通常 1 秒内出首字；失败会自动回退本地模型。")
                    : Localization.T($"本地模式：{LLMNpcBrain.ModelLabel(SelectedModelFile())}，完全离线。");
        }

        string SelectedModelFile()
        {
            return _smartModel ? LlmSettings.ModelSmart : LlmSettings.ModelFast;
        }

        void SelectFast()
        {
            _smartModel = false;
            RefreshLocalModelButtons();
            RefreshModeButtons();
        }

        void SelectSmart()
        {
            _smartModel = true;
            RefreshLocalModelButtons();
            RefreshModeButtons();
        }

        void RefreshLocalModelButtons()
        {
            Color on = new Color(0.55f, 0.42f, 0.16f);
            Color off = new Color(0.10f, 0.12f, 0.17f);
            Color dim = new Color(0.70f, 0.74f, 0.82f);

            if (_fastBg != null) _fastBg.color = _smartModel ? off : on;
            if (_smartBg != null) _smartBg.color = _smartModel ? on : off;
            if (_fastLabel != null) _fastLabel.color = _smartModel ? dim : Color.white;
            if (_smartLabel != null) _smartLabel.color = _smartModel ? Color.white : dim;
            if (_smartWarning != null) _smartWarning.gameObject.SetActive(_smartModel);
        }

        // ------------------------------------------------------------------
        // 模型重载
        // ------------------------------------------------------------------

        void StartModelReload(string target)
        {
            if (_reloading) return;
            _reloading = true;

            ShowLoading(Localization.T("模型加载中……\n") + LLMNpcBrain.ModelLabel(target));

            // 换模型期间游戏必须停表，否则遮罩下面是活的世界。
            if (GameManager.Instance != null) GameManager.Instance.ClockPaused = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            StartCoroutine(ReloadRoutine(target));
        }

        IEnumerator ReloadRoutine(string target)
        {
            LLMNpcBrain brain = LLMNpcBrain.Instance;
            if (brain == null)
            {
                yield return FinishLoading(false, Localization.T("找不到 LLMNpcBrain，模型未切换。"), 2.5f);
                yield break;
            }

            bool finished = false;
            bool ok = false;
            string message = "";

            StartCoroutine(brain.ReloadLocalModelRoutine(target, (success, msg) =>
            {
                finished = true;
                ok = success;
                message = msg ?? "";
            }));

            while (!finished)
            {
                if (_loadingText != null && brain.IsLoadingModel
                    && !string.IsNullOrEmpty(brain.LoadStatus))
                {
                    _loadingText.text = Localization.T("模型加载中……\n") + brain.LoadStatus +
                                        Localization.T("\n\n首次加载约 10~40 秒，请勿关闭游戏。");
                }
                yield return null;
            }

            yield return FinishLoading(ok, message, ok ? 2.2f : 5f);
        }

        IEnumerator FinishLoading(bool ok, string message, float hold)
        {
            if (_loadingText != null)
                _loadingText.text = ok ? message : Localization.T("模型切换失败\n") + message;

            if (hold > 0f) yield return new WaitForSecondsRealtime(hold);

            if (_loadingOverlay != null) _loadingOverlay.SetActive(false);
            _reloading = false;

            // 面板已经关了，这里把停表与鼠标状态恢复到正常游玩。
            if (GameManager.Instance != null)
                GameManager.Instance.ClockPaused = NpcFreeTextInputUI.IsOpen;
            bool chatActive = PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive;
            if (!chatActive && !ShouldKeepCursorFree())
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        void ShowLoading(string text)
        {
            if (!_built) BuildIfNeeded();
            if (_loadingOverlay == null) return;
            _loadingOverlay.SetActive(true);
            if (_loadingText != null) _loadingText.text = text;
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
            canvas.sortingOrder = 320;

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
            dim.color = new Color(0f, 0f, 0f, 0.72f);
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

            CreateText(panel.transform, "大模型设置", new Vector2(0.5f, 1f),
                new Vector2(0f, -34f), new Vector2(PanelWidth - 80f, 44f),
                32f, TextAlignmentOptions.Center, new Color(1f, 0.84f, 0.48f));

            CreateText(panel.transform, "F9 关闭", new Vector2(1f, 1f),
                new Vector2(-46f, -34f), new Vector2(180f, 30f),
                20f, TextAlignmentOptions.Right, new Color(0.62f, 0.67f, 0.76f));

            // 模式
            CreateText(panel.transform, "运行模式", new Vector2(0f, 1f),
                new Vector2(62f, -96f), new Vector2(220f, 30f),
                22f, TextAlignmentOptions.Left, new Color(0.82f, 0.86f, 0.94f));

            _localBg = CreateButton(panel.transform, "本地模型（离线）", new Vector2(0f, 1f),
                new Vector2(300f, -96f), new Vector2(280f, 56f), SelectLocal,
                new Color(0.10f, 0.12f, 0.17f));
            _localLabel = _localBg.GetComponentInChildren<TextMeshProUGUI>();

            _cloudBg = CreateButton(panel.transform, "云端 API（更快）", new Vector2(0f, 1f),
                new Vector2(600f, -96f), new Vector2(280f, 56f), SelectCloud,
                new Color(0.10f, 0.12f, 0.17f));
            _cloudLabel = _cloudBg.GetComponentInChildren<TextMeshProUGUI>();

            _modeHint = CreateText(panel.transform, "", new Vector2(0f, 1f),
                new Vector2(62f, -164f), new Vector2(PanelWidth - 124f, 28f),
                19f, TextAlignmentOptions.Left, new Color(0.66f, 0.72f, 0.82f));

            // 本地模型：两排单选按钮
            CreateText(panel.transform, "本地模型", new Vector2(0f, 1f),
                new Vector2(62f, -212f), new Vector2(220f, 30f),
                22f, TextAlignmentOptions.Left, new Color(0.82f, 0.86f, 0.94f));

            _fastBg = CreateButton(panel.transform, "极速版（1.5B）· 推荐", new Vector2(0f, 1f),
                new Vector2(300f, -212f), new Vector2(280f, 56f), SelectFast,
                new Color(0.10f, 0.12f, 0.17f));
            _fastLabel = _fastBg.GetComponentInChildren<TextMeshProUGUI>();

            _smartBg = CreateButton(panel.transform, "高智版（4B）· 高配", new Vector2(0f, 1f),
                new Vector2(600f, -212f), new Vector2(280f, 56f), SelectSmart,
                new Color(0.10f, 0.12f, 0.17f));
            _smartLabel = _smartBg.GetComponentInChildren<TextMeshProUGUI>();

            _smartWarning = CreateText(panel.transform,
                "※ 4B 模型逻辑更强，但在核显/低配电脑上回复可能极其缓慢（>20秒）。",
                new Vector2(0f, 1f), new Vector2(62f, -276f),
                new Vector2(PanelWidth - 124f, 26f),
                18f, TextAlignmentOptions.Left, new Color(1f, 0.70f, 0.38f));

            // 云端
            CreateText(panel.transform, "云端 API（可选，失败自动回退本地）", new Vector2(0f, 1f),
                new Vector2(62f, -306f), new Vector2(PanelWidth - 124f, 30f),
                22f, TextAlignmentOptions.Left, new Color(0.82f, 0.86f, 0.94f));

            _urlInput = BuildField(panel.transform, "接口地址（OpenAI 兼容 /chat/completions）",
                LlmSettings.DefaultBaseUrl, -348f, false, 200);
            _modelInput = BuildField(panel.transform, "模型名",
                LlmSettings.DefaultModel, -452f, false, 80);
            _keyInput = BuildField(panel.transform, "API Key（只保存在本机）",
                "", -556f, true, 200);

            CreateText(panel.transform,
                "智谱：https://open.bigmodel.cn/api/paas/v4/chat/completions　模型 glm-4-flash\n" +
                "其他服务同理，只要兼容 OpenAI 的 messages + stream 格式。",
                new Vector2(0f, 1f), new Vector2(62f, -654f),
                new Vector2(PanelWidth - 124f, 54f),
                18f, TextAlignmentOptions.TopLeft, new Color(0.60f, 0.66f, 0.76f));

            CreateButton(panel.transform, "保存并关闭", new Vector2(0.5f, 0f),
                new Vector2(-140f, 54f), new Vector2(240f, 62f), SaveAndClose,
                new Color(0.50f, 0.38f, 0.14f));

            CreateButton(panel.transform, "取消", new Vector2(0.5f, 0f),
                new Vector2(140f, 54f), new Vector2(180f, 62f), Close,
                new Color(0.12f, 0.14f, 0.19f));

            // 模型切换遮罩：挂在 CanvasGroup 之外，面板关闭后仍然可见。
            _loadingOverlay = NewUIObject(transform, "ModelLoadingOverlay");
            RectTransform overlayRect = (RectTransform)_loadingOverlay.transform;
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            Image overlayBg = _loadingOverlay.AddComponent<Image>();
            overlayBg.color = new Color(0f, 0f, 0f, 0.82f);
            overlayBg.raycastTarget = true;

            _loadingText = CreateText(_loadingOverlay.transform, "模型加载中……",
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 320f),
                30f, TextAlignmentOptions.Center, new Color(1f, 0.86f, 0.55f));

            _loadingOverlay.SetActive(false);
        }

        TMP_InputField BuildField(Transform parent, string label, string value,
            float y, bool password, int limit)
        {
            CreateText(parent, label, new Vector2(0f, 1f),
                new Vector2(62f, y), new Vector2(PanelWidth - 124f, 28f),
                20f, TextAlignmentOptions.Left, new Color(0.80f, 0.84f, 0.92f));

            GameObject bg = NewUIObject(parent, "Field");
            RectTransform bgRect = (RectTransform)bg.transform;
            bgRect.anchorMin = bgRect.anchorMax = new Vector2(0f, 1f);
            bgRect.pivot = new Vector2(0f, 1f);
            bgRect.anchoredPosition = new Vector2(62f, y - 32f);
            bgRect.sizeDelta = new Vector2(PanelWidth - 124f, 58f);

            Image bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.09f, 0.11f, 0.15f, 1f);
            bgImage.raycastTarget = true;

            GameObject area = NewUIObject(bg.transform, "Text Area");
            RectTransform areaRect = (RectTransform)area.transform;
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.offsetMin = new Vector2(16f, 6f);
            areaRect.offsetMax = new Vector2(-16f, -6f);
            area.AddComponent<RectMask2D>();

            GameObject textGo = NewUIObject(area.transform, "Text");
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            TextMeshProUGUI text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 22f;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;
            text.enableWordWrapping = false;
            ChineseFontUtility.Apply(text);

            GameObject placeholderGo = NewUIObject(area.transform, "Placeholder");
            RectTransform placeholderRect = (RectTransform)placeholderGo.transform;
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;

            TextMeshProUGUI placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.fontSize = 21f;
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
            placeholder.color = new Color(0.52f, 0.56f, 0.63f, 0.85f);
            placeholder.text = Localization.T("未填写");
            ChineseFontUtility.Apply(placeholder);

            TMP_InputField input = bg.AddComponent<TMP_InputField>();
            input.textViewport = areaRect;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = limit;
            input.caretWidth = 2;
            input.customCaretColor = true;
            input.caretColor = new Color(1f, 0.85f, 0.5f);
            input.selectionColor = new Color(0.55f, 0.72f, 1f, 0.35f);
            input.contentType = password
                ? TMP_InputField.ContentType.Password
                : TMP_InputField.ContentType.Standard;
            input.text = value ?? "";
            return input;
        }

        Image CreateButton(Transform parent, string label, Vector2 anchor,
            Vector2 pos, Vector2 size, Action onClick, Color color)
        {
            GameObject go = NewUIObject(parent, "Button");
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0f, 1f);
            if (anchor.x > 0.5f) rect.pivot = new Vector2(1f, 1f);
            if (Mathf.Approximately(anchor.x, 0.5f)) rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = color;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            if (onClick != null) button.onClick.AddListener(() => onClick());

            CreateText(go.transform, label, new Vector2(0.5f, 0.5f),
                Vector2.zero, size, 22f, TextAlignmentOptions.Center, Color.white);

            return image;
        }

        static TextMeshProUGUI CreateText(Transform parent, string content, Vector2 anchor,
            Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align, Color color)
        {
            GameObject go = NewUIObject(parent, "Text");
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(anchor.x, 1f);
            if (Mathf.Approximately(anchor.y, 0.5f)) rect.pivot = new Vector2(anchor.x, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = Localization.T(content);
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = color;
            text.enableWordWrapping = true;
            ChineseFontUtility.Apply(text);
            return text;
        }

        static GameObject NewUIObject(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            GameObject go = new GameObject("EventSystem",
                typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }
    }
}
