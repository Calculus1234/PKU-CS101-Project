using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【主窗口 / 桌宠 的状态管理器】
    ///
    /// 核心事实：**桌宠和主面板是同一个 Windows 窗口**（全屏透明层）。
    /// 所以"最小化主窗口"不能真的去调 OS 最小化 —— 那会把桌宠一起最小化。
    /// 这里的"最小化/关闭" = 把面板隐藏，桌宠继续跑在透明层上。
    ///
    /// ⚠️ 关键约束：全屏透明窗口的穿透是【整窗级】的。
    ///    面板一开如果直接关掉穿透，整个桌面就点不了了。
    ///    正确做法是 WM_NCHITTEST 按坐标判定：
    ///      面板矩形内 → HTCLIENT（可点）
    ///      其余地方   → HTTRANSPARENT（穿透给桌面）
    ///    ShouldPassThrough() 就是留给 TransparentWindow 的判定钩子。
    /// </summary>
    public class PetWindowManager : MonoBehaviour
    {
        public static PetWindowManager Instance { get; private set; }

        [Header("引用（留空会自动找）")]
        public PetDashboardUI dashboard;
        [Tooltip("小白本体（只用来隐藏/显示渲染器，不关窗口）")]
        public GameObject petRoot;

        [Header("呼出键（Tab 和 Esc 都可以开关面板）")]
        public KeyCode[] toggleKeys = { KeyCode.Tab, KeyCode.Escape };

        [Header("行为")]
        [Tooltip("面板关掉后，桌面上的小白是否继续显示")]
        public bool keepPetVisibleWhenClosed = true;

        public bool IsDashboardOpen { get; private set; }

        /// <summary>面板开/关时广播（别的模块可以据此暂停快捷键等）。</summary>
        public event System.Action<bool> onVisibilityChanged;

        const string PrefKey = "Pet_KeepPetVisible";

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            keepPetVisibleWhenClosed = PlayerPrefs.GetInt(PrefKey, 1) == 1;

            if (dashboard == null)
            {
    #if UNITY_2022_2_OR_NEWER
                dashboard = Object.FindFirstObjectByType<PetDashboardUI>();
    #else
                dashboard = Object.FindObjectOfType<PetDashboardUI>();
    #endif
            }
            if (petRoot == null) petRoot = GameObject.Find("WhiteTiger_DesktopPet");

            if (dashboard != null)
            {
                dashboard.BuildIfNeeded();
                dashboard.Hide();
            }
        }

        void Start()
        {
            // 调试/验收用：MyPet.exe -dashboard 启动时直接打开面板
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (args[i] == "-dashboard") { OpenDashboard(); break; }
            }
            catch { }
        }

        void Update()
        {
            bool typing = dashboard != null && dashboard.IsTyping;

            for (int i = 0; i < toggleKeys.Length; i++)
            {
                KeyCode k = toggleKeys[i];
                if (k == KeyCode.None) continue;

                // 输入框里按 Tab 是在切换焦点，不能抢
                if (k == KeyCode.Tab && typing) continue;

                // 小气泡开着时，Esc 归它（先关聊天），不抢
                if (k == KeyCode.Escape)
                {
                    var chat = FindChat();
                    if (chat != null && chat.IsVisible) continue;
                }

                if (Input.GetKeyDown(k)) { Toggle(); return; }
            }
        }

        ChatUI FindChat()
        {
    #if UNITY_2022_2_OR_NEWER
            return Object.FindFirstObjectByType<ChatUI>();
    #else
            return Object.FindObjectOfType<ChatUI>();
    #endif
        }

        /// <summary>屏幕坐标（Unity 左下原点）是否落在面板上 —— 给 WM_NCHITTEST 用。</summary>
        public bool IsPointInsideDashboard(Vector2 unityScreenPoint)
        {
            return dashboard != null && dashboard.ContainsScreenPoint(unityScreenPoint);
        }

        /// <summary>归一化客户区坐标（0..1，左上原点）是否落在面板上 —— WM_NCHITTEST 用，DPI 无关。</summary>
        public bool IsNormalizedPointInsideDashboard(float nx, float ny)
        {
            return dashboard != null && dashboard.ContainsNormalizedPoint(nx, ny);
        }

        // ---------------- 开关 ----------------

        public void Toggle() { if (IsDashboardOpen) CloseDashboard(); else OpenDashboard(); }

        public void OpenDashboard()
        {
            if (dashboard == null) return;
            dashboard.Show();
            SetOpen(true);
        }

        public void CloseDashboard()
        {
            if (dashboard != null) dashboard.Hide();
            SetOpen(false);
        }

        /// <summary>面板上的「—」按钮：等同于关闭（桌宠继续跑）。</summary>
        public void MinimizeDashboard() { CloseDashboard(); }

        void SetOpen(bool open)
        {
            if (IsDashboardOpen == open) return;
            IsDashboardOpen = open;

            // 面板开着时必须能接鼠标；关掉后把穿透判据交回原来的"光标靠近小白"
            TransparentWindow.mouseOverPet = open;
            ApplyPetVisibility();

            if (onVisibilityChanged != null) onVisibilityChanged(open);
            Debug.Log("[PetWindow] 主面板 " + (open ? "打开" : "关闭") + "，桌宠继续运行");
        }

        // ---------------- 桌宠可见性 ----------------

        public void SetKeepPetVisible(bool on)
        {
            keepPetVisibleWhenClosed = on;
            PlayerPrefs.SetInt(PrefKey, on ? 1 : 0);
            PlayerPrefs.Save();
            ApplyPetVisibility();
        }

        void ApplyPetVisibility()
        {
            if (petRoot == null) return;
            bool show = IsDashboardOpen || keepPetVisibleWhenClosed;
            var rs = petRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++) if (rs[i] != null) rs[i].enabled = show;

            // 粒子/拖尾也跟着隐藏，不然只剩特效飘着
            var ps = petRoot.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < ps.Length; i++) if (ps[i] != null) ps[i].Clear();
        }

        // ---------------- 给 TransparentWindow 的穿透判据 ----------------

        /// <summary>
        /// 屏幕坐标是否应该【穿透给桌面】。
        /// ⚠️ 这一步还没接进 TransparentWindow —— 下一步会把它的
        ///    "光标靠近小白" 判定换成：CanHit ? HTCLIENT : HTTRANSPARENT。
        /// </summary>
        public bool ShouldPassThrough(Vector2 screenPoint)
        {
            if (!IsDashboardOpen) return true;                 // 面板没开 ⇒ 交给原来的近距离逻辑
            if (dashboard == null) return true;
            return !dashboard.ContainsScreenPoint(screenPoint);
        }
    }
}
