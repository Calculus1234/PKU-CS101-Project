using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家扮演 1 号时的「先手独处时间」HUD。
    /// 只显示时间与状态，不显示任何情绪数值。
    /// 倒计时按游戏分钟显示（1 游戏分钟 = 6 现实秒）。
    /// </summary>
    public class SoloTimerUI : MonoBehaviour
    {
        CanvasGroup _group;
        TextMeshProUGUI _countdown;
        TextMeshProUGUI _state;

        float _remainingSeconds;
        bool _active;
        bool _killNoticed;
        bool _hiddenAtZero;
        float _noticeUntil;
        string _noticeText;

        public void Bind(float soloSeconds)
        {
            _remainingSeconds = Mathf.Max(0f, soloSeconds);
            _active = true;
            _killNoticed = false;
            _hiddenAtZero = false;
            _noticeUntil = 0f;
            _noticeText = null;

            if (_group != null)
            {
                _group.alpha = 1f;
                _group.blocksRaycasts = false;
            }
            Refresh();
        }

        /// <summary>玩家换号 / 进入会议时收起。</summary>
        public void Hide()
        {
            _active = false;
            if (_group != null) _group.alpha = 0f;
        }

        void Awake()
        {
            BuildIfNeeded();
            NPCEventBus.OnNPCWakeup += HandleNpcWakeup;
            if (_group != null) _group.alpha = 0f;
        }

        void OnDestroy()
        {
            NPCEventBus.OnNPCWakeup -= HandleNpcWakeup;
        }

        void Update()
        {
            if (!_active) return;
            // 自由打字时停表：玩家在输入框里花的时间不算独处时间。
            if (GameManager.Instance != null && GameManager.Instance.ClockPaused) return;

            if (CycleState.kill9Executed && !_killNoticed)
            {
                _killNoticed = true;
                _noticeText = Localization.T("9 号已死 · 循环照常运转");
                _noticeUntil = Time.time + 5f;
                IntracranialVoice.Instance.PlayKill9();
            }

            _remainingSeconds = Mathf.Max(0f, _remainingSeconds - Time.deltaTime);

            if (_remainingSeconds <= 0f && !_hiddenAtZero)
            {
                _hiddenAtZero = true;
                _noticeUntil = Time.time + 5f;
                // 「2号已经醒了」只播颅内音，不再挤占 HUD 文本。
                IntracranialVoice.Instance.PlaySoloEnd();
            }

            if (_hiddenAtZero && _noticeText == null && Time.time >= _noticeUntil)
            {
                _active = false;
                if (_group != null) _group.alpha = 0f;
                return;
            }

            if (Time.time >= _noticeUntil && _noticeText != null)
            {
                _noticeText = null;
                if (_remainingSeconds <= 0f || _killNoticed)
                {
                    _active = false;
                    if (_group != null) _group.alpha = 0f;
                    return;
                }
            }

            Refresh();
        }

        void HandleNpcWakeup(int number)
        {
            if (!_active || number == CycleState.playerNumber) return;
            // 谁醒了只让玩家「听见」，不在屏幕上报幕。
            IntracranialVoice.Instance.PlayNotice(number);
        }

        void Refresh()
        {
            if (_countdown != null)
            {
                float gameSeconds = _remainingSeconds / LoopTimeline.SecondsPerGameMinute;
                int total = Mathf.CeilToInt(gameSeconds);
                _countdown.text = $"{total / 60:00}:{total % 60:00}";
            }

            if (_state != null)
            {
                if (!string.IsNullOrEmpty(_noticeText)) _state.text = _noticeText;
                else if (_remainingSeconds > 0f) _state.text = Localization.T("整栋房子只有你醒着");
                else _state.text = Localization.T("独处结束");
            }
        }

        void BuildIfNeeded()
        {
            Canvas canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 160;

            if (GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject content = new GameObject("SoloTimerContent", typeof(RectTransform));
            content.layer = 5;
            content.transform.SetParent(transform, false);
            RectTransform contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            _group = content.AddComponent<CanvasGroup>();

            GameObject panel = new GameObject("Panel", typeof(RectTransform));
            panel.layer = 5;
            panel.transform.SetParent(content.transform, false);
            RectTransform panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -32f);
            panelRect.sizeDelta = new Vector2(460f, 132f);
            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0.025f, 0.03f, 0.045f, 0.72f);
            bg.raycastTarget = false;

            _countdown = CreateText(panel.transform, "Countdown", new Vector2(0.5f, 0.5f),
                new Vector2(0f, 14f), new Vector2(420f, 76f), 56f,
                new Color(1f, 0.82f, 0.46f));
            _state = CreateText(panel.transform, "State", new Vector2(0.5f, 0f),
                new Vector2(0f, 14f), new Vector2(420f, 34f), 22f,
                new Color(0.82f, 0.86f, 0.95f));

            TextMeshProUGUI title = CreateText(panel.transform, "Title", new Vector2(0.5f, 1f),
                new Vector2(0f, -8f), new Vector2(420f, 30f), 20f,
                new Color(0.72f, 0.76f, 0.86f));
            title.text = Localization.T("先手独处时间");
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, Vector2 anchor,
            Vector2 anchoredPos, Vector2 size, float fontSize, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = color;
            ChineseFontUtility.Apply(text);
            return text;
        }
    }
}
