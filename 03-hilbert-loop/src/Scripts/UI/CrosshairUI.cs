using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 第一人称准星：调查阶段（光标锁定、玩家存活、无 UI 弹窗）才显示；
    /// 瞄准门 / 道具 / NPC 等可交互物时变亮、放大并轻微呼吸。
    /// 组件挂在 HUD Canvas 上，由 MainSceneBuilder 装配两个 Image。
    /// </summary>
    public class CrosshairUI : MonoBehaviour
    {
        public PlayerController player;
        public InteractionSystem interaction;

        [Header("准星图层")]
        public Image ring;
        public Image dot;

        static Sprite _ringSprite;
        static Sprite _dotSprite;

        static readonly Color IdleRing = new Color(0.42f, 0.04f, 0.04f, 0.55f);
        static readonly Color ActiveRing = new Color(1f, 0.16f, 0.10f, 0.98f);
        static readonly Color IdleDot = new Color(0.48f, 0.05f, 0.05f, 0.85f);
        static readonly Color ActiveDot = new Color(1f, 0.24f, 0.14f, 1f);

        float _pulse;

        void Awake()
        {
            if (ring != null)
            {
                ring.sprite = GetRingSprite();
                ring.raycastTarget = false;
            }
            if (dot != null)
            {
                dot.sprite = GetDotSprite();
                dot.raycastTarget = false;
            }
        }

        void Update()
        {
            bool visible = player != null && player.CanUseGameplayInput
                && (PlayerDeathSystem.Instance == null || !PlayerDeathSystem.Instance.IsDead);

            if (ring != null) ring.enabled = visible;
            if (dot != null) dot.enabled = visible;
            if (!visible) return;

            // 准星位于屏幕正中；瞄准可交互物时放大并变亮。
            bool aimed = interaction != null && interaction.HasFocus;
            _pulse += Time.unscaledDeltaTime * (aimed ? 6.5f : 3.2f);
            float breath = 0.90f + Mathf.Sin(_pulse) * 0.10f;

            if (ring != null)
            {
                Color color = aimed ? ActiveRing : IdleRing;
                color.a *= breath;
                ring.color = color;
                float size = aimed ? 32f : 22f;
                ring.rectTransform.sizeDelta = new Vector2(size, size);
            }

            if (dot != null)
            {
                dot.color = aimed ? ActiveDot : IdleDot;
                float size = aimed ? 8f : 5f;
                dot.rectTransform.sizeDelta = new Vector2(size, size);
            }
        }

        // ------------------------------------------------------------------
        // 程序化贴图（不依赖外部资源）
        // ------------------------------------------------------------------

        static Sprite GetRingSprite()
        {
            if (_ringSprite != null) return _ringSprite;

            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "CrosshairRing";
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = 0f;
                    // 细环：半径 24，线宽 2.2 像素
                    float edge = Mathf.Abs(d - 24f);
                    if (edge < 1.1f) alpha = 1f;
                    else if (edge < 2.1f) alpha = 1f - (edge - 1.1f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _ringSprite.name = "CrosshairRing";
            return _ringSprite;
        }

        static Sprite GetDotSprite()
        {
            if (_dotSprite != null) return _dotSprite;

            const int size = 16;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "CrosshairDot";
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = d < 2.4f ? 1f : Mathf.Clamp01((3.2f - d) / 0.8f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _dotSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _dotSprite.name = "CrosshairDot";
            return _dotSprite;
        }
    }
}
