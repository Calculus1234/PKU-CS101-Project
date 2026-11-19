using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>设置面板：中英一键切换、总音量、音效、语音、全屏。</summary>
    public class SettingsUI : MonoBehaviour
    {
        static SettingsUI _instance;
        Canvas _canvas;
        TextMeshProUGUI _title;
        TextMeshProUGUI _languageLabel;
        TextMeshProUGUI _languageValue;
        TextMeshProUGUI _masterLabel;
        TextMeshProUGUI _sfxLabel;
        TextMeshProUGUI _voiceLabel;
        TextMeshProUGUI _displayLabel;
        TextMeshProUGUI _modelLabel;
        TextMeshProUGUI _modelValue;
        TextMeshProUGUI _modelHint;
        TextMeshProUGUI _hint;
        TextMeshProUGUI _fullscreenLabel;
        Button _languageButton;
        Button _fullscreenButton;
        Button _modelButton;
        Button _closeButton;

        public static SettingsUI Show()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("SettingsUI");
                _instance = go.AddComponent<SettingsUI>();
                _instance.Build();
            }
            _instance.gameObject.SetActive(true);
            _instance.transform.SetAsLastSibling();
            _instance.RefreshTexts();
            return _instance;
        }

        public static void Hide()
        {
            if (_instance != null) _instance.gameObject.SetActive(false);
        }

        void Build()
        {
            _canvas = UIOverlayUtil.CreateCanvas("SettingsCanvas", 800, transform);
            UIOverlayUtil.CreateImage(_canvas.transform, "Dim", new Color(0f, 0f, 0f, 0.62f));

            RectTransform panel = UIOverlayUtil.CreatePanel(_canvas.transform, "Panel",
                new Vector2(860f, 720f), Vector2.zero, new Color(0.025f, 0.022f, 0.025f, 1f));
            UIOverlayUtil.CreateImage(panel, "TopLine", new Color(0.62f, 0.10f, 0.08f, 0.85f))
                .rectTransform.anchorMin = new Vector2(0f, 1f);
            // 上面这一行只用于配色占位，随后把描边压成细线。
            RectTransform line = panel.Find("TopLine").GetComponent<RectTransform>();
            line.anchorMin = new Vector2(0f, 1f);
            line.anchorMax = new Vector2(1f, 1f);
            line.pivot = new Vector2(0.5f, 1f);
            line.offsetMin = new Vector2(0f, -3f);
            line.offsetMax = Vector2.zero;

            _title = UIOverlayUtil.CreateText(panel, "Title", "设置面板",
                new Vector2(0.5f, 1f), new Vector2(0f, -58f), new Vector2(700f, 54f), 38f);
            _languageLabel = UIOverlayUtil.CreateText(panel, "LanguageLabel", "语言",
                new Vector2(0f, 1f), new Vector2(170f, -128f), new Vector2(200f, 40f), 26f,
                TextAlignmentOptions.Left);
            _languageButton = UIOverlayUtil.CreateButton(panel, "LanguageButton", "English",
                new Vector2(250f, 232f), new Vector2(260f, 50f), ToggleLanguage,
                new Color(0.16f, 0.045f, 0.05f, 0.95f), 25f);
            _languageValue = _languageButton.GetComponentInChildren<TextMeshProUGUI>();

            _masterLabel = CreateSliderRow(panel, "MasterLabel", 202f, "主音量", "HL_MasterVolume", 0.85f,
                value => { AudioListener.volume = value; });
            _sfxLabel = CreateSliderRow(panel, "SfxLabel", 274f, "音效音量", "HL_SfxVolume", 0.9f,
                value => { SFXManager.VolumeScale = value; });
            _voiceLabel = CreateSliderRow(panel, "VoiceLabel", 346f, "语音音量", "HL_VoiceVolume", 0.9f,
                value => { VoiceSystem.VolumeScale = value; });

            _displayLabel = UIOverlayUtil.CreateText(panel, "DisplayLabel", "显示",
                new Vector2(0f, 1f), new Vector2(190f, -418f), new Vector2(240f, 40f), 26f,
                TextAlignmentOptions.Left);
            _fullscreenButton = UIOverlayUtil.CreateButton(panel, "FullscreenButton", "全屏",
                new Vector2(250f, -58f), new Vector2(300f, 52f), ToggleFullscreen,
                new Color(0.16f, 0.045f, 0.05f, 0.95f), 25f);
            _fullscreenLabel = _fullscreenButton.GetComponentInChildren<TextMeshProUGUI>();

            _modelLabel = UIOverlayUtil.CreateText(panel, "ModelLabel", "AI 模型",
                new Vector2(0f, 1f), new Vector2(190f, -490f), new Vector2(240f, 40f), 26f,
                TextAlignmentOptions.Left);
            _modelButton = UIOverlayUtil.CreateButton(panel, "ModelButton", "模型设置（F9）",
                new Vector2(250f, -130f), new Vector2(300f, 52f), OpenModelSettings,
                new Color(0.16f, 0.045f, 0.05f, 0.95f), 25f);
            _modelValue = _modelButton.GetComponentInChildren<TextMeshProUGUI>();
            _modelHint = UIOverlayUtil.CreateText(panel, "ModelHint",
                "游戏中按 F9 也可以随时打开。",
                new Vector2(0.5f, 1f), new Vector2(0f, -540f), new Vector2(760f, 30f), 18f,
                TextAlignmentOptions.Center, new Color(0.60f, 0.58f, 0.55f, 0.95f));

            _hint = UIOverlayUtil.CreateText(panel, "Hint",
                "语言切换会立即应用到 UI、Yarn 台词与 LLM 提示词。",
                new Vector2(0.5f, 0.5f), new Vector2(0f, -240f), new Vector2(760f, 44f), 19f,
                TextAlignmentOptions.Center, new Color(0.62f, 0.58f, 0.55f, 0.95f));

            _closeButton = UIOverlayUtil.CreateButton(panel, "CloseButton", "关闭",
                new Vector2(0f, -304f), new Vector2(260f, 52f), Hide,
                new Color(0.32f, 0.06f, 0.06f, 0.96f), 25f);

            RefreshTexts();
        }

        TextMeshProUGUI CreateSliderRow(RectTransform panel, string name, float y, string label,
            string prefKey, float defaultValue, UnityEngine.Events.UnityAction<float> onChanged)
        {
            TextMeshProUGUI text = UIOverlayUtil.CreateText(panel, name, label,
                new Vector2(0f, 1f), new Vector2(190f, -y), new Vector2(260f, 38f), 24f,
                TextAlignmentOptions.Left);
            float value = PlayerPrefs.GetFloat(prefKey, defaultValue);
            UIOverlayUtil.CreateSlider(panel, name + "Slider", new Vector2(80f, 360f - y),
                value, onChanged);
            onChanged?.Invoke(value);
            return text;
        }

        void ToggleLanguage()
        {
            Localization.ToggleLanguage();
            RefreshTexts();
        }

        void ToggleFullscreen()
        {
            Screen.fullScreen = !Screen.fullScreen;
            RefreshTexts();
        }

        void OpenModelSettings()
        {
            // 主菜单里没有 GameManager 的时钟，打开 F9 面板会在关闭时锁住鼠标，
            // 所以这里只提示，不允许在主菜单里换模型。
            if (GameMode.MenuActive)
            {
                _modelHint.text = Localization.T("主菜单中无法切换模型，请先进入游戏。");
                _modelHint.color = new Color(1f, 0.64f, 0.40f, 1f);
                return;
            }

            Hide();
            if (!LlmSettingsUI.OpenFromSettings())
            {
                Show();
                _modelHint.text = Localization.T("模型面板尚未初始化，请按 F9 重试。");
                _modelHint.color = new Color(1f, 0.64f, 0.40f, 1f);
            }
        }

        void RefreshTexts()
        {
            if (_title == null) return;
            _title.text = Localization.T("设置面板");
            _languageLabel.text = Localization.T("语言");
            _languageValue.text = Localization.LanguageLabel;
            _masterLabel.text = Localization.T("主音量");
            _sfxLabel.text = Localization.T("音效音量");
            _voiceLabel.text = Localization.T("语音音量");
            _displayLabel.text = Localization.T("显示");
            _fullscreenLabel.text = Screen.fullScreen ? Localization.T("全屏") : Localization.T("窗口化");
            _modelLabel.text = Localization.T("AI 模型");
            _modelValue.text = Localization.T("模型设置（F9）");
            string modelName = LlmSettings.UseSmartModel
                ? (Localization.IsEnglish ? "Smart (4B)" : "高智版（4B）")
                : (Localization.IsEnglish ? "Fast (1.5B)" : "极速版（1.5B）");
            _modelHint.text = Localization.T("当前模型：") + modelName + " · "
                + Localization.T("游戏中按 F9 也可以随时打开。");
            _modelHint.color = new Color(0.60f, 0.58f, 0.55f, 0.95f);
            _hint.text = Localization.T("语言切换会立即应用到 UI、Yarn 台词与 LLM 提示词。");
            _closeButton.GetComponentInChildren<TextMeshProUGUI>().text = Localization.T("关闭");
        }
    }
}
