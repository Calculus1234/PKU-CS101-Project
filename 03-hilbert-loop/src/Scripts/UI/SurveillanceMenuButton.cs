using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>主菜单的极简监控风按钮：默认细红框，悬停时文字变亮红并发光。</summary>
    public class SurveillanceMenuButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        Button _button;
        TextMeshProUGUI _label;
        Graphic[] _lines;
        Color _lineNormal;
        Color _lineHover = new Color(0.95f, 0.18f, 0.12f, 0.95f);
        Color _textNormal;
        Color _textHover = new Color(1f, 0.30f, 0.22f, 1f);

        public void Bind(Button button, TextMeshProUGUI label, Graphic[] lines, Color lineNormal)
        {
            _button = button;
            _label = label;
            _lines = lines;
            _lineNormal = lineNormal;
            if (_label != null) _textNormal = _label.color;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_button != null && !_button.interactable) return;
            Apply(_textHover, _lineHover, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Color text = _textNormal;
            if (_button != null && !_button.interactable)
                text = new Color(0.40f, 0.36f, 0.34f, 0.85f);
            Apply(text, _lineNormal, false);
        }

        void Apply(Color textColor, Color lineColor, bool glow)
        {
            if (_label != null)
            {
                _label.color = textColor;
                _label.fontStyle = glow ? FontStyles.Bold : FontStyles.Normal;
                _label.outlineWidth = glow ? 0.22f : 0f;
                _label.outlineColor = new Color(0.75f, 0.02f, 0.02f, 0.9f);
            }
            if (_lines != null)
            {
                for (int i = 0; i < _lines.Length; i++)
                    if (_lines[i] != null) _lines[i].color = lineColor;
            }
        }
    }
}
