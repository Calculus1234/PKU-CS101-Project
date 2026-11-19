using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 自定义鼠标光标：骨骼 + 红色菱形。只在系统光标可见（UI / 会议 / 私聊 /
    /// 设置面板 / 幽灵视角）时启用；第一人称调查阶段光标锁定、准星接管。
    /// 现有 UI 代码通过 Cursor.lockState / Cursor.visible 控制状态，本组件
    /// 每帧跟随，不需要逐个弹窗改代码。
    /// </summary>
    public class CustomCursor : MonoBehaviour
    {
        Texture2D _texture;
        bool _applied;

        void Awake()
        {
            _texture = BuildCursorTexture();
            Apply(true);
        }

        void LateUpdate()
        {
            bool want = Cursor.lockState != CursorLockMode.Locked && Cursor.visible;
            if (want != _applied) Apply(want);
        }

        void Apply(bool custom)
        {
            if (custom && _texture != null)
            {
                Cursor.SetCursor(_texture, new Vector2(16f, 16f), CursorMode.Auto);
                _applied = true;
            }
            else
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                _applied = false;
            }
        }

        void OnDisable()
        {
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        static Texture2D BuildCursorTexture()
        {
            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "CustomCursor_BoneDiamond";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(0f, 0f, 0f, 0f);

            Vector2 c = new Vector2(16f, 16f);

            // 深红外描边菱形
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Abs(x + 0.5f - c.x) + Mathf.Abs(y + 0.5f - c.y);
                    if (d >= 8.2f && d <= 11.4f)
                        pixels[y * size + x] = new Color(0.55f, 0.03f, 0.03f, 0.95f);
                    else if (d > 7.2f && d < 8.2f)
                        pixels[y * size + x] = new Color(0.12f, 0.02f, 0.02f, 0.8f);
                }
            }

            // 斜置的骨头：骨身 + 两端的骨节
            Vector2 a = new Vector2(9f, 23f);
            Vector2 b = new Vector2(23f, 9f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float line = DistanceToSegment(p, a, b);
                    bool bone = line <= 2.1f;
                    if (!bone)
                    {
                        bone = Vector2.Distance(p, new Vector2(8.3f, 22.4f)) <= 3.1f
                            || Vector2.Distance(p, new Vector2(11.4f, 25.1f)) <= 3.1f
                            || Vector2.Distance(p, new Vector2(23.7f, 9.6f)) <= 3.1f
                            || Vector2.Distance(p, new Vector2(20.6f, 6.9f)) <= 3.1f;
                    }

                    if (bone)
                    {
                        float edge = line <= 1.2f ? 0f : Mathf.Clamp01((line - 1.2f) / 1.2f);
                        pixels[y * size + x] = Color.Lerp(
                            new Color(0.97f, 0.94f, 0.86f, 1f),
                            new Color(0.30f, 0.26f, 0.22f, 1f),
                            edge * 0.8f);
                    }
                }
            }

            // 中心红点（提高瞄准时的落点辨识度）
            for (int y = 13; y <= 18; y++)
            {
                for (int x = 13; x <= 18; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), c);
                    if (d <= 1.6f) pixels[y * size + x] = new Color(0.9f, 0.08f, 0.06f, 1f);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude);
            t = Mathf.Clamp01(t);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
