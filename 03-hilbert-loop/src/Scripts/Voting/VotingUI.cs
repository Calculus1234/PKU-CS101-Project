using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 投票 UI（模块三 3.5）：
    /// - 30 秒倒计时，列出存活号码 + 弃权；支持 1~10 号（10号点击选择，键盘 1~9 / 0）；
    /// - 超时自动弃权；
    /// - 结算面板显示各号得票、弃权票与最终裁定（处决 / 平票）。
    /// 运行时构建，sortingOrder 120（高于会议 UI 100，低于幽灵 200）。
    /// </summary>
    public class VotingUI : MonoBehaviour
    {
        Canvas _canvas;
        CanvasGroup _group;
        GameObject _ballotPanel;
        GameObject _resultPanel;

        TextMeshProUGUI _ballotCountdownText;
        TextMeshProUGUI _resultCountdownText;
        TextMeshProUGUI _ballotHintText;

        readonly Button[] _candidateButtons = new Button[CycleState.MaxNumber + 1]; // index 0 = 弃权，1~10 = 号码
        readonly TextMeshProUGUI[] _candidateLabels = new TextMeshProUGUI[CycleState.MaxNumber + 1];

        TextMeshProUGUI _resultTitleText;
        TextMeshProUGUI _resultBodyText;
        TextMeshProUGUI _resultVerdictText;

        Action<int> _onSelected;
        float _remaining;
        bool _ballotOpen;
        bool _resultOpen;
        bool _built;

        void Awake()
        {
            BuildIfNeeded();
            Hide();
        }

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        /// <summary>显示选票。candidates = 存活号码（可含玩家自己），onSelected 参数 0 = 弃权。</summary>
        public void ShowBallot(IReadOnlyList<int> candidates, int playerNumber, float seconds, Action<int> onSelected)
        {
            BuildIfNeeded();
            _canvas.gameObject.SetActive(true);
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = true;

            _ballotPanel.SetActive(true);
            _resultPanel.SetActive(false);
            _ballotOpen = true;
            _resultOpen = false;
            _onSelected = onSelected;
            _remaining = Mathf.Max(1f, seconds);

            for (int number = 0; number <= CycleState.MaxNumber; number++)
            {
                bool isAbstain = number == 0;
                bool allowed = isAbstain || Contains(candidates, number);
                Button button = _candidateButtons[number];
                if (button == null) continue;

                button.gameObject.SetActive(allowed);
                button.interactable = allowed;
                if (isAbstain)
                {
                    _candidateLabels[number].text = Localization.T("弃权") + "\n[0]";
                }
                else
                {
                    string name = Localization.IsEnglish ? "No. " + number : number + " 号";
                    _candidateLabels[number].text = number == playerNumber
                        ? name + "\n" + Localization.T("（你）")
                        : name;
                }
                _candidateLabels[number].color = isAbstain
                    ? new Color(0.82f, 0.82f, 0.86f)
                    : new Color(0.95f, 0.88f, 0.82f);
            }

            _ballotHintText.text = Localization.T("点击号码，或按数字键 1~9 / 0 弃权（10号请点击）");
            UpdateCountdownText("请投票");
        }

        /// <summary>幽灵 / 观察者旁观投票（无投票权）。</summary>
        public void ShowSpectator(float seconds)
        {
            BuildIfNeeded();
            _canvas.gameObject.SetActive(true);
            _group.alpha = 1f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _ballotPanel.SetActive(true);
            _resultPanel.SetActive(false);
            _ballotOpen = true;
            _resultOpen = false;
            _onSelected = null;
            _remaining = Mathf.Max(1f, seconds);

            for (int i = 0; i <= CycleState.MaxNumber; i++)
            {
                if (_candidateButtons[i] != null) _candidateButtons[i].gameObject.SetActive(false);
            }

            _ballotHintText.text = Localization.T("你已出局 / 无投票权，正在旁观投票。");
            UpdateCountdownText("投票");
        }

        /// <summary>显示计票结果与裁定，持续 seconds 秒后自动隐藏。</summary>
        public void ShowResults(IReadOnlyList<string> countLines, string verdict, float seconds)
        {
            BuildIfNeeded();
            _canvas.gameObject.SetActive(true);
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = true;

            _ballotPanel.SetActive(false);
            _resultPanel.SetActive(true);
            _ballotOpen = false;
            _resultOpen = true;
            _onSelected = null;
            _remaining = Mathf.Max(0.5f, seconds);

            _resultTitleText.text = Localization.T("投票结果");
            _resultBodyText.text = countLines != null && countLines.Count > 0
                ? Localization.T(string.Join("\n", countLines))
                : Localization.T("无人投票。");
            _resultVerdictText.text = Localization.T(verdict);
            UpdateCountdownText("天亮");
        }

        public void Hide()
        {
            _ballotOpen = false;
            _resultOpen = false;
            _onSelected = null;
            _remaining = -1f;
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
                _group.interactable = false;
            }
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        void Update()
        {
            if (!_canvas.gameObject.activeSelf) return;

            // 玩家正在自由打字时，数字键属于输入框，不能顺手投出去。
            if (NpcFreeTextInputUI.IsOpen) return;

            if (!_ballotOpen && !_resultOpen) return;
            if (_remaining < 0f) return;

            if (_ballotOpen && _onSelected != null) HandleNumberKeys();

            _remaining -= Time.unscaledDeltaTime;
            UpdateCountdownText(_ballotOpen ? "请投票" : "天亮");

            if (_remaining > 0f) return;

            if (_ballotOpen)
            {
                CommitSelection(0);
            }
            else
            {
                Hide();
            }
        }

        /// <summary>数字键 1~9 投对应号码，0 弃权；10号只接受鼠标点击。</summary>
        void HandleNumberKeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
            {
                if (_candidateButtons[0] != null && _candidateButtons[0].gameObject.activeSelf)
                    CommitSelection(0);
                return;
            }

            for (int number = 1; number <= 9; number++)
            {
                KeyCode alpha = KeyCode.Alpha1 + (number - 1);
                if (!Input.GetKeyDown(alpha) && !Input.GetKeyDown(KeyCode.Keypad1 + (number - 1))) continue;
                if (_candidateButtons[number] != null && _candidateButtons[number].gameObject.activeSelf)
                    CommitSelection(number);
                return;
            }
        }

        void CommitSelection(int number)
        {
            if (!_ballotOpen) return;
            _ballotOpen = false;
            _remaining = -1f;

            Action<int> callback = _onSelected;
            _onSelected = null;
            Hide();

            if (callback != null) callback(number);
        }

        void UpdateCountdownText(string phaseLabel)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(_remaining));
            string text = Localization.IsEnglish
                ? Localization.T(phaseLabel) + "  " + seconds.ToString("00") + " s"
                : $"{phaseLabel}　{seconds:00} 秒";
            if (_ballotCountdownText != null && _ballotPanel != null && _ballotPanel.activeSelf)
                _ballotCountdownText.text = text;
            if (_resultCountdownText != null && _resultPanel != null && _resultPanel.activeSelf)
                _resultCountdownText.text = Localization.IsEnglish
                    ? "Dawn in " + seconds.ToString("00") + " s"
                    : $"距天亮　{seconds:00} 秒";
        }

        static bool Contains(IReadOnlyList<int> list, int value)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // UI 构建
        // ------------------------------------------------------------------

        void BuildIfNeeded()
        {
            if (_built) return;
            _built = true;

            var canvasGo = new GameObject("VotingUI_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 120;
            _group = canvasGo.AddComponent<CanvasGroup>();

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            EnsureEventSystem();

            BuildBallotPanel(canvasGo.transform);
            BuildResultPanel(canvasGo.transform);
        }

        void BuildBallotPanel(Transform parent)
        {
            _ballotPanel = new GameObject("BallotPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _ballotPanel.transform.SetParent(parent, false);
            var rect = _ballotPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(1120f, 720f);
            _ballotPanel.GetComponent<Image>().color = new Color(0.055f, 0.035f, 0.04f, 0.94f);

            CreateText(_ballotPanel.transform, "Title", Localization.T("圆桌投票"), new Vector2(0f, 300f), new Vector2(900f, 64f), 44f, TextAlignmentOptions.Center);
            CreateText(_ballotPanel.transform, "Subtitle", Localization.T("得票最高者将被抹杀"), new Vector2(0f, 255f), new Vector2(900f, 36f), 24f, TextAlignmentOptions.Center,
                new Color(0.85f, 0.72f, 0.66f));
            _ballotCountdownText = CreateText(_ballotPanel.transform, "Countdown",
                Localization.IsEnglish ? Localization.T("请投票") + "  30 s" : "请投票　30 秒",
                new Vector2(0f, 205f), new Vector2(900f, 44f), 30f,
                TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.55f));
            _ballotHintText = CreateText(_ballotPanel.transform, "Hint", Localization.T("点击号码，或按数字键 1~9 / 0 弃权（10号请点击）"),
                new Vector2(0f, -315f), new Vector2(1000f, 36f), 22f,
                TextAlignmentOptions.Center, new Color(0.78f, 0.76f, 0.76f));

            // 4 × 3 按键区：1~10 号 + 弃权
            const float startX = -397f;
            const float startY = 130f;
            const float stepX = 265f;
            const float stepY = 115f;

            for (int number = 0; number <= CycleState.MaxNumber; number++)
            {
                int slot = number == 0 ? CycleState.MaxNumber : number - 1;
                int row = slot / 4;
                int col = slot % 4;
                Vector2 pos = new Vector2(startX + col * stepX, startY - row * stepY);

                Button button = CreateButton(_ballotPanel.transform, $"Vote_{number}", pos, new Vector2(240f, 95f));
                int captured = number;
                button.onClick.AddListener(() =>
                {
                    if (_ballotOpen) CommitSelection(captured);
                });

                _candidateButtons[number] = button;
                _candidateLabels[number] = button.GetComponentInChildren<TextMeshProUGUI>();
            }
        }

        void BuildResultPanel(Transform parent)
        {
            _resultPanel = new GameObject("ResultPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _resultPanel.transform.SetParent(parent, false);
            var rect = _resultPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(1000f, 680f);
            _resultPanel.GetComponent<Image>().color = new Color(0.055f, 0.035f, 0.04f, 0.94f);

            _resultTitleText = CreateText(_resultPanel.transform, "Title", Localization.T("投票结果"), new Vector2(0f, 280f), new Vector2(900f, 64f), 44f,
                TextAlignmentOptions.Center);
            _resultBodyText = CreateText(_resultPanel.transform, "Body", "", new Vector2(0f, 40f), new Vector2(880f, 330f), 30f,
                TextAlignmentOptions.Center, new Color(0.92f, 0.88f, 0.84f));
            _resultVerdictText = CreateText(_resultPanel.transform, "Verdict", "", new Vector2(0f, -170f), new Vector2(900f, 90f), 32f,
                TextAlignmentOptions.Center, new Color(1f, 0.72f, 0.55f));
            _resultCountdownText = CreateText(_resultPanel.transform, "Countdown", "", new Vector2(0f, -285f), new Vector2(900f, 40f), 22f,
                TextAlignmentOptions.Center, new Color(0.72f, 0.70f, 0.70f));
        }

        Button CreateButton(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = new Color(0.22f, 0.10f, 0.10f, 0.96f);
            var button = go.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.86f, 0.78f);
            colors.pressedColor = new Color(0.85f, 0.65f, 0.55f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;

            var label = CreateText(go.transform, "Label", "", Vector2.zero, size, 28f, TextAlignmentOptions.Center,
                new Color(0.95f, 0.88f, 0.82f));
            label.raycastTarget = false;
            return button;
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, string text, Vector2 position,
                                          Vector2 size, float fontSize, TextAlignmentOptions alignment,
                                          Color? color = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color ?? Color.white;
            label.raycastTarget = false;
            label.enableWordWrapping = true;
            ChineseFontUtility.Apply(label);
            return label;
        }

        void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
