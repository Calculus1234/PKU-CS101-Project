using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 监控系统：正交俯视摄像机 → RenderTexture → 圆桌上的悬浮显示屏。
    ///
    /// - 红点：UI Image 覆盖在画面层上，通过 WorldToViewportPoint 映射世界坐标；
    ///   玩家 / NPC 进入房间后红点隐藏（MonitorTracked.IsInRoom）。
    /// - 倒计时：显示屏中央显示当前阶段名与剩余时间（数据来自 GameManager）。
    /// - 摄像机层策略：监控摄像机看不到 "MonitorIgnore"（31）层
    ///   （天花板、灯具、玩家与 NPC 本体、显示屏自身），只看到建筑俯视图。
    /// </summary>
    public class MonitorSystem : MonoBehaviour
    {
        /// <summary>监控摄像机忽略的层（天花板 / 灯具 / 玩家 / NPC / 显示屏）。</summary>
        public const int MonitorIgnoreLayer = 31;

        public static MonitorSystem Instance { get; private set; }

        [Header("引用（由场景构建器装配）")]
        public Camera monitorCamera;
        public RenderTexture renderTexture;
        public RawImage monitorRawImage;
        [Tooltip("红点父容器（与显示画面同尺寸的 RectTransform）")]
        public RectTransform dotLayer;
        public TextMeshProUGUI countdownText;

        [Header("参数")]
        [Tooltip("重新扫描场景中 MonitorTracked 的间隔（秒）")]
        public float syncInterval = 0.5f;
        public int textureWidth = 1024;
        public int textureHeight = 576;
        public float dotSize = 14f;

        class TrackedDot
        {
            public MonitorTracked tracked;
            public Image dot;
            public RectTransform rect;
        }

        readonly List<TrackedDot> _dots = new List<TrackedDot>();
        float _syncTimer;
        Sprite _dotSprite;
        string _lastCountdownText;

        void Awake()
        {
            Instance = this;
            _dotSprite = CreateCircleSprite();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            ChineseFontUtility.Apply(countdownText);
            SetupRenderTexture();
            SyncTracked();
        }

        void Update()
        {
            _syncTimer -= Time.deltaTime;
            if (_syncTimer <= 0f)
            {
                _syncTimer = syncInterval;
                SyncTracked();
            }

            UpdateDots();
            UpdateCountdown();
        }

        void SetupRenderTexture()
        {
            if (renderTexture == null)
            {
                renderTexture = new RenderTexture(textureWidth, textureHeight, 16)
                {
                    name = "MonitorRT"
                };
                renderTexture.Create();
            }

            if (monitorCamera != null)
            {
                monitorCamera.targetTexture = renderTexture;
                monitorCamera.cullingMask = ~(1 << MonitorIgnoreLayer);
                monitorCamera.orthographic = true;
            }

            if (monitorRawImage != null)
            {
                monitorRawImage.texture = renderTexture;
            }
        }

        // ---------------------------------------------------------------
        // 红点同步
        // ---------------------------------------------------------------

        void SyncTracked()
        {
            if (dotLayer == null) return;

            MonitorTracked[] found = Object.FindObjectsOfType<MonitorTracked>();

            // 移除已销毁 / 不在场景中的
            for (int i = _dots.Count - 1; i >= 0; i--)
            {
                TrackedDot d = _dots[i];
                bool stillExists = d.tracked != null && System.Array.IndexOf(found, d.tracked) >= 0;
                if (!stillExists)
                {
                    if (d.dot != null) Destroy(d.dot.gameObject);
                    _dots.RemoveAt(i);
                }
            }

            // 为新出现的实体创建红点
            for (int i = 0; i < found.Length; i++)
            {
                if (!ContainsTracked(found[i])) _dots.Add(CreateDot(found[i]));
            }
        }

        bool ContainsTracked(MonitorTracked tracked)
        {
            for (int i = 0; i < _dots.Count; i++)
            {
                if (_dots[i].tracked == tracked) return true;
            }
            return false;
        }

        TrackedDot CreateDot(MonitorTracked tracked)
        {
            GameObject go = new GameObject("Dot_" + tracked.DisplayName, typeof(Image));
            go.layer = MonitorIgnoreLayer;
            go.transform.SetParent(dotLayer, false);

            Image image = go.GetComponent<Image>();
            image.sprite = _dotSprite;
            image.color = tracked.DotColor;
            image.raycastTarget = false;

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(dotSize, dotSize);

            return new TrackedDot { tracked = tracked, dot = image, rect = rect };
        }

        void UpdateDots()
        {
            if (monitorCamera == null || dotLayer == null) return;

            Vector2 layerSize = dotLayer.rect.size;

            for (int i = 0; i < _dots.Count; i++)
            {
                TrackedDot d = _dots[i];
                if (d.tracked == null || d.dot == null) continue;

                // 死亡 / 失活（例如 NPC 被击杀后禁用 MonitorTracked）→ 红点立即消失
                bool visible = d.tracked.isActiveAndEnabled
                    && !(d.tracked.hideDotInRooms && d.tracked.IsInRoom);
                if (visible)
                {
                    Vector3 vp = monitorCamera.WorldToViewportPoint(d.tracked.transform.position);
                    if (vp.z < 0f) visible = false;
                    else
                    {
                        d.rect.anchoredPosition = new Vector2(
                            (vp.x - 0.5f) * layerSize.x,
                            (vp.y - 0.5f) * layerSize.y);
                    }
                }

                if (d.dot.enabled != visible) d.dot.enabled = visible;
            }
        }

        // ---------------------------------------------------------------
        // 倒计时
        // ---------------------------------------------------------------

        void UpdateCountdown()
        {
            if (countdownText == null) return;

            string text;
            GameManager gm = GameManager.Instance;
            if (gm == null)
            {
                text = "--:--";
            }
            else if (gm.CurrentPhase == GamePhase.Investigation)
            {
                // 屏幕显示剧本里的游戏时钟（1:30 → 0:00），不是现实秒数。
                int gameMinutes = Mathf.CeilToInt(gm.PhaseTimeRemaining / LoopTimeline.SecondsPerGameMinute);
                text = GameManager.GetPhaseDisplayName(gm.CurrentPhase) + "\n" + LoopTimeline.FormatMinute(gameMinutes);
            }
            else
            {
                text = GameManager.GetPhaseDisplayName(gm.CurrentPhase) + "\n" + gm.GetFormattedTime();
            }

            if (text != _lastCountdownText)
            {
                _lastCountdownText = text;
                countdownText.text = text;
            }
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        /// <summary>生成一个白色圆形 Sprite（红点用，运行时自动着色）。</summary>
        static Sprite CreateCircleSprite(int size = 32)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float radius = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
