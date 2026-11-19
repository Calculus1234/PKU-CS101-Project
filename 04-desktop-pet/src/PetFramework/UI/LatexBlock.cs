using UnityEngine;
using UnityEngine.UI;

namespace PetFramework
{
    /// <summary>
    /// 把一条「文字 + LaTeX」消息渲染成一张图片（RawImage），高度自动适配气泡。
    ///
    /// 为什么不在 AddBubble 里直接 Render：
    ///   构建气泡的那一刻，父级 VerticalLayoutGroup 还没算过宽度，
    ///   rect.width 经常是 0 —— 必须等布局跑完（Start / OnRectTransformDimensionsChange）再渲染。
    /// </summary>
    public class LatexBlock : MonoBehaviour
    {
        public string source;
        public float fontSize = 16f;
        public Color color = Color.white;

        RawImage raw;
        LayoutElement le;
        Texture2D tex;
        float builtWidth = -1f;

        void Awake()
        {
            raw = gameObject.GetComponent<RawImage>();
            if (raw == null) raw = gameObject.AddComponent<RawImage>();
            // 渲染完成前先透明，否则 RawImage 空纹理会闪一块白色
            raw.color = new Color(1f, 1f, 1f, 0f);
            le = gameObject.GetComponent<LayoutElement>();
            if (le == null) le = gameObject.AddComponent<LayoutElement>();
            le.minHeight = 24f;
            le.preferredHeight = 24f;
        }

        void Start() { TryBuild(); }
        void OnRectTransformDimensionsChange() { TryBuild(); }

        void TryBuild()
        {
            if (raw == null) return;
            var rt = transform as RectTransform;
            if (rt == null) return;

            float w = rt.rect.width;
            if (w < 40f) return;
            if (tex != null && Mathf.Abs(w - builtWidth) < 6f) return;

            float ow, oh;
            var t = LatexRenderer.Render(source, fontSize, w, color, out ow, out oh);
            if (t == null) return;

            if (tex != null) Destroy(tex);
            tex = t;
            builtWidth = w;

            raw.texture = tex;
            raw.color = Color.white;
            raw.raycastTarget = false;

            le.preferredHeight = oh;
            le.minHeight = oh;
        }

        void OnDestroy()
        {
            if (tex != null) Destroy(tex);
        }
    }
}
