using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 开局「公共信息」面板：把本轮完整规则一次性摊在玩家面前。
    /// 与顶部短消息不同，这里能容纳多行长文本，并会在语音播报期间一直停留。
    /// </summary>
    public class OpeningBriefingUI : MonoBehaviour
    {
        CanvasGroup _group;
        TextMeshProUGUI _body;
        Coroutine _routine;

        public void Show(string text, float duration = 16f)
        {
            BuildIfNeeded();
            _body.text = text;

            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(ShowRoutine(duration));
        }

        public void Hide()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            if (_group != null) _group.alpha = 0f;
        }

        void Awake()
        {
            BuildIfNeeded();
            if (_group != null) _group.alpha = 0f;
        }

        IEnumerator ShowRoutine(float duration)
        {
            if (_group == null) yield break;

            float t = 0f;
            while (t < 0.35f)
            {
                t += Time.unscaledDeltaTime;
                _group.alpha = Mathf.Clamp01(t / 0.35f);
                yield return null;
            }
            _group.alpha = 1f;

            if (duration > 0f)
                yield return new WaitForSecondsRealtime(duration);

            t = 0f;
            while (t < 0.6f)
            {
                t += Time.unscaledDeltaTime;
                _group.alpha = 1f - Mathf.Clamp01(t / 0.6f);
                yield return null;
            }
            _group.alpha = 0f;
            _routine = null;
        }

        void BuildIfNeeded()
        {
            if (_group != null) return;

            Canvas canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 170;

            if (GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject content = new GameObject("OpeningBriefingContent", typeof(RectTransform));
            content.layer = 5;
            content.transform.SetParent(transform, false);
            RectTransform contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            _group = content.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;

            GameObject backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
            backdrop.layer = 5;
            backdrop.transform.SetParent(content.transform, false);
            RectTransform backdropRect = (RectTransform)backdrop.transform;
            backdropRect.anchorMin = Vector2.zero;
            backdropRect.anchorMax = Vector2.one;
            backdropRect.offsetMin = Vector2.zero;
            backdropRect.offsetMax = Vector2.zero;
            Image backdropImage = backdrop.GetComponent<Image>();
            backdropImage.color = new Color(0f, 0f, 0f, 0.42f);
            backdropImage.raycastTarget = false;

            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.layer = 5;
            panel.transform.SetParent(content.transform, false);
            RectTransform panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(0f, 30f);
            panelRect.sizeDelta = new Vector2(1420f, 430f);
            Image panelImage = panel.GetComponent<Image>();
            panelImage.color = new Color(0.035f, 0.04f, 0.055f, 0.94f);
            panelImage.raycastTarget = false;

            GameObject accent = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accent.layer = 5;
            accent.transform.SetParent(panel.transform, false);
            RectTransform accentRect = (RectTransform)accent.transform;
            accentRect.anchorMin = new Vector2(0f, 1f);
            accentRect.anchorMax = new Vector2(1f, 1f);
            accentRect.pivot = new Vector2(0.5f, 1f);
            accentRect.anchoredPosition = Vector2.zero;
            accentRect.sizeDelta = new Vector2(0f, 4f);
            Image accentImage = accent.GetComponent<Image>();
            accentImage.color = new Color(0.93f, 0.72f, 0.32f, 0.95f);
            accentImage.raycastTarget = false;

            TextMeshProUGUI title = CreateText(panel.transform, "Title", new Vector2(0f, 1f),
                new Vector2(48f, -24f), new Vector2(800f, 44f), 30f,
                new Color(0.95f, 0.78f, 0.42f), TextAlignmentOptions.TopLeft);
            title.text = Localization.T("本轮规则");
            title.fontStyle = FontStyles.Bold;

            _body = CreateText(panel.transform, "Body", new Vector2(0f, 0.5f),
                new Vector2(48f, -8f), new Vector2(1324f, 300f), 34f,
                new Color(0.94f, 0.94f, 0.92f), TextAlignmentOptions.MidlineLeft);
            _body.lineSpacing = 12f;
            _body.enableWordWrapping = true;

            TextMeshProUGUI footer = CreateText(panel.transform, "Footer", new Vector2(0f, 0f),
                new Vector2(48f, 22f), new Vector2(1324f, 28f), 20f,
                new Color(0.62f, 0.66f, 0.74f), TextAlignmentOptions.BottomLeft);
            footer.text = Localization.T("这段信息只在本轮开始时出现。");
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, Vector2 anchor,
            Vector2 anchoredPos, Vector2 size, float fontSize, Color color,
            TextAlignmentOptions alignment)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(anchor.x, anchor.y);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            ChineseFontUtility.Apply(text);
            return text;
        }
    }
}
