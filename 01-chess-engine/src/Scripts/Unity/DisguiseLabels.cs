using UnityEngine;
using UnityEngine.UI;
using ChessDark.Core;
using System.Collections.Generic;

namespace ChessDark.Unity
{
    public class DisguiseLabels : MonoBehaviour
    {
        public Canvas uiCanvas;
        public Camera worldCamera;
        public Toggle toggle;
        public Font font;
        public PieceColor playerColor = PieceColor.White;
        public int fontSize = 22;

        static readonly Color WhiteSideColor = new Color(0.98f, 0.94f, 0.82f, 1f);
        static readonly Color BlackSideColor = new Color(1f, 0.6f, 0.48f, 1f);

        class Label
        {
            public PieceData piece;
            public RectTransform rect;
            public Text text;
            public string cached;
        }

        readonly List<Label> labels = new List<Label>();
        RectTransform container;
        bool showOwnDisguise;

        void Start()
        {
            if (uiCanvas == null) uiCanvas = FindObjectOfType<Canvas>();
            if (worldCamera == null) worldCamera = Camera.main;
            if (uiCanvas == null || worldCamera == null) return;

            Build();

            if (toggle != null)
            {
                toggle.onValueChanged.RemoveListener(OnToggleChanged);
                toggle.onValueChanged.AddListener(OnToggleChanged);
                showOwnDisguise = toggle.isOn;
            }
            else
            {
                showOwnDisguise = false;
            }
        }

        void OnDestroy()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnToggleChanged);
        }

        void OnToggleChanged(bool on)
        {
            showOwnDisguise = on;
        }

        void Build()
        {
            var containerGo = new GameObject("DisguiseLabels", typeof(RectTransform));
            container = containerGo.GetComponent<RectTransform>();
            container.SetParent(uiCanvas.transform, false);
            container.anchorMin = Vector2.zero;
            container.anchorMax = Vector2.one;
            container.offsetMin = Vector2.zero;
            container.offsetMax = Vector2.zero;
            container.SetAsLastSibling();

            if (font == null) font = FindFont();
            if (font == null) return;

            var pieces = FindObjectsOfType<PieceData>();
            foreach (var piece in pieces)
            {
                var labelGo = new GameObject("Label_" + piece.gameObject.name, typeof(RectTransform));
                var rect = labelGo.GetComponent<RectTransform>();
                rect.SetParent(container, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.sizeDelta = new Vector2(140f, fontSize + 12f);

                var text = labelGo.AddComponent<Text>();
                text.font = font;
                text.fontSize = fontSize;
                text.alignment = TextAnchor.LowerCenter;
                text.raycastTarget = false;
                text.color = piece.pieceColor == PieceColor.White ? WhiteSideColor : BlackSideColor;

                var shadow = labelGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
                shadow.effectDistance = new Vector2(1.5f, -1.5f);

                labels.Add(new Label { piece = piece, rect = rect, text = text });
            }
        }

        Font FindFont()
        {
            return RuntimeUI.ResolveFont(uiCanvas);
        }

        void LateUpdate()
        {
            if (container == null) return;

            Canvas canvas = uiCanvas;
            var screenRect = new Rect(0f, 0f, Screen.width, Screen.height);

            foreach (var label in labels)
            {
                var piece = label.piece;
                if (piece == null)
                {
                    label.rect.gameObject.SetActive(false);
                    continue;
                }

                bool isOwn = piece.pieceColor == playerColor;
                bool shouldShow = showOwnDisguise && isOwn && piece.gameObject.activeInHierarchy;

                // 模型显示本态；标注只表达我方棋子在对手眼中的伪装态。敌方伪装态就是它的模型本身，不重复标注。
                string content = null;
                if (shouldShow)
                    content = "伪装 " + piece.visibleType.ToChinese();

                bool visible = !string.IsNullOrEmpty(content);
                if (visible && content != label.cached)
                {
                    label.text.text = content;
                    label.cached = content;
                }

                label.rect.gameObject.SetActive(visible);
                if (!visible) continue;

                Bounds b = WorldBounds(piece.gameObject);
                Vector3 world = new Vector3(b.center.x, b.max.y + 0.2f, b.center.z);
                Vector3 screen = worldCamera.WorldToScreenPoint(world);
                if (screen.z <= 0f || !screenRect.Contains(screen))
                {
                    label.rect.gameObject.SetActive(false);
                    continue;
                }

                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        canvas.GetComponent<RectTransform>(), screen, null, out local))
                    continue;

                label.rect.anchoredPosition = local;
            }
        }

        static Bounds WorldBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>();
            Bounds bounds = new Bounds(obj.transform.position, Vector3.zero);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }
            return bounds;
        }
    }
}
