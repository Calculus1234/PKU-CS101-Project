using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 监控画面舞台：主菜单与结局 A/B 共用同一套探头、滤镜和菜单式文字按钮。
    ///
    /// 这里只负责「看起来像什么」——相机位置 / 角度 / FOV、扫描线、噪点、暗角、
    /// 左侧渐变、细红框文字按钮。谁在用它、为什么用，交给调用方。
    /// </summary>
    public static class SurveillanceStage
    {
        public const float CameraFov = 55f;

        /// <summary>一台监控探头的机位。</summary>
        public readonly struct CameraPreset
        {
            public readonly string NameCn;
            public readonly string NameEn;
            public readonly Vector3 Position;
            public readonly Vector3 Euler;
            public readonly float Fov;

            public CameraPreset(string nameCn, string nameEn, Vector3 position, Vector3 euler,
                float fov = CameraFov)
            {
                NameCn = nameCn;
                NameEn = nameEn;
                Position = position;
                Euler = euler;
                Fov = fov;
            }
        }

        /// <summary>
        /// 走廊 / 大厅的监控机位表。主菜单与结局 A/B 共用同一份表，
        /// Tab 在两者之间循环切换（小彩蛋，不额外提示）。
        /// </summary>
        public static readonly CameraPreset[] Presets =
        {
            // 01 大厅全景：南墙高处俯拍，圆桌、9 把椅子、两侧开口全部入镜。
            new CameraPreset("大厅全景", "LOBBY OVERVIEW",
                new Vector3(0f, 3.18f, -2.86f), new Vector3(38f, 0f, 0f)),
            // 02 大厅反向：北墙高处俯拍，补足另一侧视野。
            new CameraPreset("大厅反向", "LOBBY REVERSE",
                new Vector3(0f, 3.18f, 2.86f), new Vector3(38f, 180f, 0f)),
            // 03 / 04 连接走廊：正对大厅开口，能看清谁从环廊进出大厅。
            new CameraPreset("东连廊", "EAST LINK",
                new Vector3(5.4f, 2.75f, 0f), new Vector3(22f, 270f, 0f)),
            new CameraPreset("西连廊", "WEST LINK",
                new Vector3(-5.4f, 2.75f, 0f), new Vector3(22f, 90f, 0f)),
            // 05 / 06 东环走廊：贴着环廊中线，朝两个相反方向看，覆盖右环南北两弧与房门。
            new CameraPreset("东环北弧", "EAST RING NORTH",
                new Vector3(6.563f, 2.86f, 2.536f), new Vector3(14f, 25f, 0f)),
            new CameraPreset("东环南弧", "EAST RING SOUTH",
                new Vector3(6.563f, 2.86f, -2.536f), new Vector3(14f, 155f, 0f)),
            // 07 东环望厅：从环廊穿过连接口直视大厅圆桌，一镜覆盖走廊与大厅全貌。
            new CameraPreset("东环望厅", "EAST RING TO LOBBY",
                new Vector3(6.35f, 2.90f, 0f), new Vector3(10f, 270f, 0f)),
            // 08 / 09 西环走廊（东环镜像）。
            new CameraPreset("西环北弧", "WEST RING NORTH",
                new Vector3(-6.563f, 2.86f, 2.536f), new Vector3(14f, 335f, 0f)),
            new CameraPreset("西环南弧", "WEST RING SOUTH",
                new Vector3(-6.563f, 2.86f, -2.536f), new Vector3(14f, 205f, 0f)),
            // 10 西环望厅。
            new CameraPreset("西环望厅", "WEST RING TO LOBBY",
                new Vector3(-6.35f, 2.90f, 0f), new Vector3(10f, 90f, 0f))
        };

        static int _selectedIndex;

        public static int SelectedCameraIndex => _selectedIndex;
        public static int CameraCount => Presets.Length;
        public static CameraPreset CurrentPreset => Presets[Mathf.Clamp(_selectedIndex, 0, Presets.Length - 1)];
        public static string CurrentCameraTag => "CAM-" + (_selectedIndex + 1).ToString("00");
        public static string CurrentCameraLabel =>
            CurrentCameraTag + "  " + (Localization.IsEnglish ? CurrentPreset.NameEn : CurrentPreset.NameCn);

        /// <summary>Tab：循环切换机位。返回 true 表示本帧切换过，调用方应重新 ApplyTo。</summary>
        public static bool HandleCameraHotkey()
        {
            return Input.GetKeyDown(KeyCode.Tab) ? CycleCamera() : false;
        }

        public static bool CycleCamera()
        {
            _selectedIndex = (_selectedIndex + 1) % Presets.Length;
            return true;
        }

        /// <summary>直接切到指定机位（主菜单演示按阶段换镜用）。</summary>
        public static void SelectPreset(int index)
        {
            if (Presets.Length == 0) return;
            _selectedIndex = Mathf.Clamp(index, 0, Presets.Length - 1);
        }

        /// <summary>把当前机位写入相机。主菜单与结局 A/B 每帧切换后都调用这里。</summary>
        public static void ApplyTo(Camera camera)
        {
            if (camera == null) return;
            CameraPreset preset = CurrentPreset;
            camera.fieldOfView = preset.Fov;
            camera.transform.position = preset.Position;
            camera.transform.rotation = Quaternion.Euler(preset.Euler);
        }

        /// <summary>
        /// 一台真实的监控探头：透视、FOV 55、离地约 2.8 米、约 30° 俯拍。
        /// 相机的补光挂在相机子节点上，相机被 SetActive(false) 时灯光一起熄灭，
        /// 不会在正常游玩时偷偷照亮场景。
        /// </summary>
        public static Camera CreateCamera(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            Camera camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.012f, 0.006f, 0.008f, 1f);
            camera.orthographic = false;
            camera.fieldOfView = CameraFov;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 80f;
            camera.depth = 20f;
            // 只剔除 UI 层与监控剔除层，建筑、走廊、30 层 NPC 全部可见。
            camera.cullingMask = ~((1 << 5) | (1 << 31));

            if (Object.FindObjectOfType<AudioListener>() == null)
                go.AddComponent<AudioListener>();

            // 主菜单里玩家相机已经退场，需要这台探头兼任 MainCamera；
            // 结局里玩家相机还在，不能抢 MainCamera 标签。
            if (Camera.main == null) go.tag = "MainCamera";

            ApplyTo(camera);
            AddCameraLights(go.transform);
            return camera;
        }

        /// <summary>探头自带的近场补光：跟着机位走，保证任何机位都有可见的纵深。</summary>
        static void AddCameraLights(Transform parent)
        {
            AddFillLight(parent, "SurveillanceFill_A", new Vector3(-1.6f, 0.35f, 4.0f), 2.6f, 11f);
            AddFillLight(parent, "SurveillanceFill_B", new Vector3(1.6f, 0.35f, 4.0f), 2.6f, 11f);
            AddFillLight(parent, "SurveillanceFill_C", new Vector3(0f, -0.1f, 7.0f), 1.8f, 14f);
        }

        static void AddFillLight(Transform parent, string name, Vector3 localPosition,
            float intensity, float range)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.95f, 0.90f, 1f);
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>主菜单式文字按钮：透明命中区 + 四周 1.2px 细红线 + 悬停发红。</summary>
        public static Button CreateTextButton(Transform parent, string name, string label,
            Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image hit = go.GetComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;

            Color lineColor = new Color(0.48f, 0.07f, 0.055f, 0.55f);
            Image top = CreateLine(rect, "LineTop", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(size.x, 1.2f), lineColor);
            Image bottom = CreateLine(rect, "LineBottom", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(size.x, 1.2f), lineColor);
            Image left = CreateLine(rect, "LineLeft", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                Vector2.zero, new Vector2(1.2f, size.y), lineColor);
            Image right = CreateLine(rect, "LineRight", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                Vector2.zero, new Vector2(1.2f, size.y), lineColor);

            Button button = go.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(1f, 0.75f, 0.72f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.55f);
            button.colors = colors;
            button.onClick.AddListener(onClick);

            TextMeshProUGUI text = UIOverlayUtil.CreateText(go.transform, "Label", label,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size.x - 40f, size.y), 21f,
                TextAlignmentOptions.Left, new Color(0.86f, 0.82f, 0.78f, 0.95f));
            text.characterSpacing = 5f;

            SurveillanceMenuButton hover = go.AddComponent<SurveillanceMenuButton>();
            hover.Bind(button, text, new Graphic[] { top, bottom, left, right }, lineColor);
            return button;
        }

        public static Image CreateLine(Transform parent, string name, Vector2 anchor, Vector2 pivot,
            Vector2 position, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static Texture2D BuildLeftGradientTexture()
        {
            const int width = 256;
            const int height = 4;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.name = "SurveillanceLeftGradient";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = x / (float)(width - 1);
                    float alpha = Mathf.Lerp(0.55f, 0f, Mathf.SmoothStep(0f, 1f, t));
                    pixels[y * width + x] = new Color(0.010f, 0.004f, 0.006f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        public static Texture2D BuildScanlineTexture()
        {
            Texture2D tex = new Texture2D(2, 4, TextureFormat.RGBA32, false);
            tex.name = "SurveillanceScanlines";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point;
            Color[] pixels = new Color[8];
            for (int y = 0; y < 4; y++)
            {
                float alpha = y == 0 ? 0.55f : 0f;
                pixels[y * 2] = new Color(0f, 0f, 0f, alpha);
                pixels[y * 2 + 1] = new Color(0f, 0f, 0f, alpha);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        public static Texture2D BuildVignetteTexture()
        {
            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "SurveillanceVignette";
            tex.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 0.55f) / 0.45f)) * 0.72f;
                    pixels[y * size + x] = new Color(0f, 0f, 0f, a);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        public class NoiseTexture
        {
            public Texture2D Texture { get; private set; }
            readonly Color32[] _pixels;
            readonly System.Random _random = new System.Random(7391);

            public NoiseTexture(int width, int height)
            {
                _pixels = new Color32[width * height];
                Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Texture.name = "SurveillanceNoise";
                Texture.wrapMode = TextureWrapMode.Repeat;
                Texture.filterMode = FilterMode.Point;
                Refresh();
            }

            public void Refresh()
            {
                for (int i = 0; i < _pixels.Length; i++)
                {
                    byte v = (byte)_random.Next(0, 256);
                    _pixels[i] = new Color32(v, v, v, (byte)_random.Next(20, 100));
                }
                Texture.SetPixels32(_pixels);
                Texture.Apply(false);
            }
        }
    }
}
