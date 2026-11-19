using System;
using UnityEngine;
using UnityEngine.UI;

namespace ChessDark.Unity
{
    public static class RuntimeUI
    {
        private static Font cjkFont;

        public static Font ResolveFont(Canvas canvas)
        {
            // WebGL 读不到系统字体，必须用随包资源里的中文字体。
            var bundled = LoadBundledCjkFont();
            if (bundled != null) return bundled;

            if (canvas != null)
            {
                var texts = canvas.GetComponentsInChildren<Text>(true);
                for (int i = 0; i < texts.Length; i++)
                    if (texts[i] != null && texts[i].font != null) return texts[i].font;
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            var osFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei", "PingFang SC", "Noto Sans SC", "SimHei" }, 32);
            if (osFont != null) return osFont;
#endif

            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static Font LoadBundledCjkFont()
        {
            if (cjkFont != null) return cjkFont;

            cjkFont = Resources.Load<Font>("Fonts/NotoSansSC-Regular");
            if (cjkFont == null) cjkFont = Resources.Load<Font>("Fonts/NotoSansSC");
            return cjkFont;
        }

        // 场景里已序列化的 Text 也要换成中文字体，否则 WebGL 下仍会显示方框。
        public static void ApplyFontToCanvas(Canvas canvas)
        {
            if (canvas == null) return;

            var font = ResolveFont(canvas);
            if (font == null) return;

            var texts = canvas.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++)
                if (texts[i] != null) texts[i].font = font;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public static void PlaceCentered(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        public static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Stretch(rect);

            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return go;
        }

        public static Text CreateText(Transform parent, string name, string content, int size, Color color,
            TextAnchor anchor, Font font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            PlaceCentered(rect, Vector2.zero, new Vector2(720f, size + 24f));

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = content;
            return text;
        }

        public static Button CreateButton(Transform parent, string name, string label, Vector2 position,
            Vector2 size, Font font, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            PlaceCentered(rect, position, size);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.13f, 0.16f, 0.21f, 0.98f);

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            colors.pressedColor = new Color(0.75f, 0.72f, 0.68f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;
            if (onClick != null) button.onClick.AddListener(() => onClick());

            var text = CreateText(go.transform, "Label", label, 30, new Color(0.97f, 0.93f, 0.84f, 1f),
                TextAnchor.MiddleCenter, font);
            var textRect = text.GetComponent<RectTransform>();
            Stretch(textRect);
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);

            return button;
        }

        public static InputField CreateInputField(Transform parent, string name, string placeholder,
            Vector2 position, Vector2 size, Font font, int characterLimit)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            PlaceCentered(rect, position, size);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.08f, 0.1f, 0.13f, 0.98f);

            var input = go.GetComponent<InputField>();
            input.targetGraphic = image;
            input.characterLimit = characterLimit;

            var text = CreateText(go.transform, "Text", string.Empty, 28, new Color(0.97f, 0.93f, 0.84f, 1f),
                TextAnchor.MiddleCenter, font);
            var textRect = text.GetComponent<RectTransform>();
            Stretch(textRect);
            textRect.offsetMin = new Vector2(14f, 4f);
            textRect.offsetMax = new Vector2(-14f, -4f);
            text.supportRichText = false;
            input.textComponent = text;

            var placeholderText = CreateText(go.transform, "Placeholder", placeholder, 26,
                new Color(0.6f, 0.6f, 0.6f, 0.8f), TextAnchor.MiddleCenter, font);
            var placeholderRect = placeholderText.GetComponent<RectTransform>();
            Stretch(placeholderRect);
            placeholderRect.offsetMin = new Vector2(14f, 4f);
            placeholderRect.offsetMax = new Vector2(-14f, -4f);
            input.placeholder = placeholderText;

            return input;
        }
    }
}
