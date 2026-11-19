using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PetFramework.Workspace
{
    /// <summary>
    /// 工作区（类似 Qoder / Cherry Studio 的独立面板）
    ///
    ///  · F8 开关；和桌宠的 Tab 面板是两个互不影响的模块
    ///  · 左侧：会话列表 / 新建 / 删除 / 模型路由
    ///  · 右侧：聊天记录 / 附件 / 输入框
    ///  · 每个会话有自己的历史、记忆、摘要（互不污染）
    ///  · 支持只读/读写本地文件（写入前弹确认）
    /// </summary>
    public class WorkspaceUI : MonoBehaviour
    {
        public static WorkspaceUI Instance { get; private set; }

        [Header("开关")]
        public KeyCode toggleKey = KeyCode.F8;

        [Header("文件权限")]
        [Tooltip("允许读取/写入的根目录。留空 = 默认（EXE 目录、D:\\myprogram、用户主目录）")]
        public string[] allowedRoots = new string[0];
        [Tooltip("写文件前是否弹确认")]
        public bool confirmWrites = true;

        [Header("模型")]
        public int maxToolRounds = 6;
        public int historyMessages = 14;
        public int timeoutSec = 180;
        [Tooltip("一次最多带多少个 MCP 工具给模型（工具太多会撑爆提示词）。0 = 不带")]
        public int maxMcpTools = 30;

        // ---------- 运行时 ----------
        Canvas canvas;
        RectTransform root, sidebar, main, listContent, chatContent, inputRow, confirmBar;
        InputField input;
        Text statusText, titleText, routeText, confirmText, mcpText;
        Text modeText;
        Dropdown permDropdown;
        RectTransform newConvPanel, newConvMask;

        // ---------- 对话模式 / 工具权限 ----------
        public const string ModeChat = "chat";
        public const string ModeClaude = "claude";
        public const string ModePi = "pi";
        /// <summary>数学模式：低温度 + 完整推导 + 不挂工具。作者用来做几何/微积分。</summary>
        public const string ModeMath = "math";
        public const string PermOpen = "open";
        public const string PermSmart = "smart";
        public const string PermAsk = "ask";

        static readonly string[] ModeLabels = { "普通对话", "Claude Agent", "Pi 模式（计划-执行）", "数学模式（严谨推导）" };
        static readonly string[] PermLabels = { "完整放开权限", "智能审批（只读自动）", "逐条审批" };
        static readonly string[] PermValues = { PermOpen, PermSmart, PermAsk };

        static string ModeOf(WorkspaceConversation c)
        {
            string m = c != null ? c.agentMode : null;
            if (m == ModeClaude || m == ModePi || m == ModeMath) return m;
            return ModeChat;
        }
        static int ModeIndex(string m)
        {
            if (m == ModeClaude) return 1;
            if (m == ModePi) return 2;
            if (m == ModeMath) return 3;
            return 0;
        }
        static string ModeLabel(string m)
        {
            int i = ModeIndex(m);
            return i >= 0 && i < ModeLabels.Length ? ModeLabels[i] : "普通对话";
        }
        static int PermIndex(string p)
        {
            for (int i = 0; i < PermValues.Length; i++) if (PermValues[i] == p) return i;
            return 1;
        }
        readonly WorkspaceMcp mcp = new WorkspaceMcp();
        bool mcpLoaded;
        [Tooltip("默认不带 MCP 工具（提示词小、回得快）。要做 Unity/Blender 的活时在标题栏点一下 MCP 打开")]
        public bool useMcpTools = false;
        ScrollRect chatScroll;
        Button confirmYes, confirmNo;
        readonly List<GameObject> listItems = new List<GameObject>();
        readonly List<GameObject> bubbles = new List<GameObject>();
        bool busy;
        string pendingPath, pendingContent;
        Action<bool> pendingConfirm;
        bool confirmOpen;
        Font font;

        // 正在跑的那一轮请求（用于「停止」/切会话/删会话时及时掐断）
        Coroutine askCo;
        WorkspaceConversation askConv;
        Button stopBtn;

        /// <summary>
        /// 独立工作区程序（MyPetWorkspace.exe）模式：
        /// 普通窗口，不置顶、不穿透、不依赖桌宠的任何组件。由 WorkspaceStandalone 置位。
        /// </summary>
        public static bool StandaloneMode = false;
        public const string StandaloneTitle = "MyPet 工作区";

        public bool IsOpen { get { return canvas != null && canvas.gameObject.activeSelf; } }
        /// <summary>面板打开且正在输入框里打字（给 TransparentWindow 用）。</summary>
        public bool IsTyping { get { return IsOpen && input != null && input.isFocused; } }

        /// <summary>屏幕点（Unity 左下原点）是否在工作区面板上 —— 给穿透判定用。</summary>
        public bool ContainsScreenPoint(Vector2 unityScreenPoint)
        {
            if (!IsOpen || root == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(root, unityScreenPoint, null);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("Workspace");
            DontDestroyOnLoad(go);
            go.AddComponent<WorkspaceUI>();
        }

        void Awake()
        {
            Instance = this;
            WorkspaceStore.EnsureLoaded();
            BuildIfNeeded();
            Hide();

            // 命令行 -openworkspace：启动桌宠时顺手把独立工作区拉起来（做快捷方式用）
            if (!StandaloneMode)
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], "-openworkspace", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Log("[WS] -openworkspace 命中，开始启动独立工作区");
                        if (!EnsureStandaloneRunning(true)) Show();
                        break;
                    }

                // ★ 桌宠启动 → 工作区跟着启动（隐藏着预热）。这样按 F8 只是"显示"，
                //   不会再走一遍启动画面（Made with Unity 只出现一次）。
                if (!StandaloneMode) StartCoroutine(PrestartWorkspace());
            }
        }

        IEnumerator PrestartWorkspace()
        {
            if (Application.isEditor) yield break;
            yield return new WaitForSeconds(3f);
            if (StandaloneMode) yield break;
            EnsureStandaloneRunning(false);
        }

        [Tooltip("排查用：打印点击命中情况")]
        public bool debugClicks = false;

        void Update()
        {
            // ★ 独立工作区：普通窗口，Esc = 隐藏到系统托盘（不是退出，启动画面只出现一次）
            if (StandaloneMode)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { CloseOrHide(); return; }
            }
            else if (Input.GetKeyDown(toggleKey))
            {
                // 打包版里工作区是独立的普通窗口程序 —— 那里点击绝对不会被穿透吃掉。
                // 编辑器里仍然用内嵌面板（方便调试）。
                if (!Application.isEditor && EnsureStandaloneRunning(true)) return;
                Toggle();
            }

            if (debugClicks && IsOpen && Input.GetMouseButtonDown(0))
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = Input.mousePosition;
                var res = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                if (es != null) es.RaycastAll(ped, res);
                string top = res.Count > 0 ? (res[0].gameObject.name + "@" + res[0].module.transform.root.name) : "-";
                var gr = canvas != null ? canvas.GetComponent<GraphicRaycaster>() : null;
                var res2 = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                if (gr != null) gr.Raycast(ped, res2);
                string corners = "-";
                if (root != null)
                {
                    var cs = new Vector3[4];
                    root.GetWorldCorners(cs);
                    corners = cs[0].ToString("F0") + ".." + cs[2].ToString("F0");
                }
                Debug.Log("[WSDBG] click mouse=" + Input.mousePosition.ToString("F0")
                    + " screen=" + Screen.width + "x" + Screen.height
                    + " overUI=" + (es != null && es.IsPointerOverGameObject())
                    + " top=" + top + " rays=" + res2.Count
                    + " esNull=" + (es == null) + " grNull=" + (gr == null)
                    + " canvasActive=" + (canvas != null && canvas.gameObject.activeInHierarchy)
                    + " nES=" + UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>().Length
                    + " sf=" + (canvas != null ? canvas.scaleFactor : -1f)
                    + " rootScreen=" + corners
                    + " active=" + Application.isFocused);
            }
        }

        public void Toggle() { if (IsOpen) Hide(); else Show(); }
        public void Show()
        {
            if (canvas == null) BuildIfNeeded();
            canvas.gameObject.SetActive(true);
            RefreshConversationList();
            RefreshChat();
            // ★ 独立程序：普通窗口，绝不置顶、绝不穿透。
            if (!StandaloneMode)
            {
                // ★ 作者拍板：保持置顶（工作区不可被别的窗口盖住），桌宠一起留在最前面。
                //   面板以外的区域仍然【鼠标穿透】—— 桌面、菜单栏、别的程序都点得到。
                PetFramework.TransparentWindow.SetTopMost(true);
                PetFramework.TransparentWindow.FocusWindow();     // 不激活的话点击进不了 Unity
            }
            if (!mcpLoaded) { mcpLoaded = true; StartCoroutine(LoadMcpRoutine()); }
            if (input != null) input.ActivateInputField();
            Debug.Log("[WS] Show  screen=" + Screen.width + "x" + Screen.height
                + " root=" + (root != null ? root.sizeDelta.ToString() : "null"));
        }
        public void Hide()
        {
            CloseAttachmentPreview();   // 别把预览遮罩留在下次打开时
            if (canvas != null) canvas.gameObject.SetActive(false);
            if (!StandaloneMode) PetFramework.TransparentWindow.SetTopMost(true);
            Debug.Log("[WS] Hide");
        }

        /// <summary>关闭（内嵌模式只藏起来；独立程序隐藏到系统托盘，不退出）。</summary>
        public void CloseOrHide()
        {
            if (StandaloneMode)
            {
                var host = WorkspaceStandalone.Instance;
                if (host != null) host.HideToTray();
                else QuitApp();
            }
            else Hide();
        }

        void QuitApp()
        {
            if (StandaloneMode && WorkspaceStandalone.Instance != null)
            {
                WorkspaceStandalone.Instance.QuitApp();
                return;
            }
            Debug.Log("[WS] 退出工作区程序");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------- 打包版：F8 唤起独立工作区 ----------------
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// 找某个进程的窗口。
        /// ⚠️ 不能用 Process.MainWindowHandle：工作区收进托盘后窗口是**隐藏**的，
        ///    而 MainWindowHandle 对隐藏窗口返回 0 ⇒ 会误判"没开"再启动第二个实例。
        /// </summary>
        static IntPtr FindProcessWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p != (uint)pid) return true;
                RECT r;
                if (!GetClientRect(h, out r)) return true;
                if (r.Right - r.Left < 400 || r.Bottom - r.Top < 300) return true;
                found = h;
                return false;      // 找到了，停
            }, IntPtr.Zero);
            return found;
        }

        public const string StandaloneProcess = "MyPetWorkspace";

        public static string StandaloneExePath()
        {
            return Path.Combine(PetPathUtil.ExeDir(), "MyPetWorkspace.exe");
        }

        /// <summary>
        /// 保证工作区进程存在：
        ///   show = true  ⇒ 没开就启动并显示；已经开着（哪怕被隐藏）就唤醒 **同一个窗口**。
        ///   show = false ⇒ 只预热（桌宠启动时调用，隐藏着等 F8），不打扰用户。
        /// 返回 false = 没有这个程序（编辑器里退回内嵌面板）。
        ///
        /// ⚠️ 不能按【窗口标题】找：作者机器上 Cherry Studio 的窗口标题恰好也叫「MyPet 工作区」，
        ///    FindWindow 会把 Cherry Studio 当成工作区拉到前台（踩过）。按进程名找才靠谱。
        /// </summary>
        public static bool EnsureStandaloneRunning(bool show)
        {
            try
            {
                var running = System.Diagnostics.Process.GetProcessesByName(StandaloneProcess);
                for (int i = 0; i < running.Length; i++)
                {
                    try
                    {
                        var h = FindProcessWindow(running[i].Id);
                        if (h == IntPtr.Zero) continue;
                        if (show)
                        {
                            ShowWindow(h, 3);          // SW_MAXIMIZE（隐藏/最小化都能叫回来）
                            SetForegroundWindow(h);
                            Debug.Log("[WS] 独立工作区已在前台（同一实例）");
                        }
                        return true;
                    }
                    catch { }
                }

                string exe = StandaloneExePath();
                if (!File.Exists(exe)) return false;
                var psi = new System.Diagnostics.ProcessStartInfo(exe);
                psi.WorkingDirectory = PetPathUtil.ExeDir();
                psi.UseShellExecute = true;
                // ⚠️ 两个 EXE 的 company/product 一样，默认会写同一个 Player.log。
                //    工作区一启动就把桌宠的日志覆盖掉 —— 给它单独的日志文件。
                psi.Arguments = "-logFile \"" +
                    Path.Combine(PetPathUtil.ExeDir(), "workspace_player.log") + "\"" +
                    " -ownerPid " + System.Diagnostics.Process.GetCurrentProcess().Id +
                    (show ? "" : " -hidden");
                System.Diagnostics.Process.Start(psi);
                Debug.Log("[WS] 启动独立工作区 " + (show ? "（显示）" : "（预热、隐藏）") + "：" + exe);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WS] 启动独立工作区失败，回退内嵌面板：" + e.Message);
                return false;
            }
        }

        IEnumerator LoadMcpRoutine()
        {
            if (mcpText != null) mcpText.text = "MCP 关";
            yield return mcp.Reload();
            if (mcpText != null) mcpText.text = "MCP " + mcp.Tools.Count;
            Status(mcp.Status);
        }

        void ReloadMcp()
        {
            mcpLoaded = true;
            StartCoroutine(LoadMcpRoutine());
        }

        /// <summary>MCP 工具默认不挂：让模型每次请求都背 20~50 个 schema 会很慢。
        /// 要做 Unity/Blender 的活时再点开。</summary>
        void ToggleMcp()
        {
            useMcpTools = !useMcpTools;
            if (mcpText != null)
                mcpText.text = useMcpTools ? ("MCP " + mcp.Tools.Count) : "MCP 关";
            Status(useMcpTools
                ? "已带上 MCP 工具（" + mcp.Tools.Count + " 个）—— 首次会慢一点"
                : "已关闭 MCP 工具（回复更快）");
        }

        // ============================================================
        //  UI 搭建
        // ============================================================

        static readonly Color ColBg    = new Color(0.07f, 0.08f, 0.10f, 0.97f);
        static readonly Color ColPanel = new Color(0.11f, 0.12f, 0.15f, 1f);
        static readonly Color ColItem  = new Color(0.16f, 0.18f, 0.22f, 1f);
        static readonly Color ColAccent= new Color(0.35f, 0.69f, 1f, 1f);
        static readonly Color ColText  = new Color(0.92f, 0.94f, 0.97f, 1f);
        static readonly Color ColDim   = new Color(0.62f, 0.66f, 0.72f, 1f);
        static readonly Color ColUser  = new Color(0.20f, 0.36f, 0.55f, 1f);
        static readonly Color ColAi    = new Color(0.14f, 0.16f, 0.20f, 1f);

        void BuildIfNeeded()
        {
            if (canvas != null) return;
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, 16);

            var go = new GameObject("WorkspaceCanvas");
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 2000;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            if (StandaloneMode)
            {
                // ★ 独立程序：界面铺满整个窗口（窗口自身可最大化/拖边框缩放）
                root = MakeRect("Root", canvas.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            }
            else
            {
                // 嵌在桌宠窗口里：屏幕上固定大小、可拖动、不铺满
                root = MakeRect("Root", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                root.sizeDelta = new Vector2(1180, 760);
                root.anchoredPosition = Vector2.zero;
            }
            root.gameObject.AddComponent<Image>().color = ColBg;

            // 标题栏（内嵌模式可拖动；独立程序里窗口自己就能拖/最大化，面板不该再飘）
            var header = MakeRect("Header", root, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -40), Vector2.zero);
            header.gameObject.AddComponent<Image>().color = new Color(0.13f, 0.15f, 0.19f, 1f);
            if (!StandaloneMode)
            {
                var drag = header.gameObject.AddComponent<DragPanel>();
                drag.target = root;
                drag.canvas = canvas;
            }
            var hTitle = MakeText(header, "工作区", 17, ColText, TextAnchor.MiddleLeft);
            hTitle.rectTransform.anchorMin = Vector2.zero; hTitle.rectTransform.anchorMax = Vector2.one;
            hTitle.rectTransform.offsetMin = new Vector2(14, 0); hTitle.rectTransform.offsetMax = new Vector2(-480, 0);

            var mcpBtn = MakeButton(header, "MCP 关", 0, 0, 0, 0, ColItem, ToggleMcp);
            var mrt = mcpBtn.GetComponent<RectTransform>();
            mrt.anchorMin = new Vector2(1, 0.5f); mrt.anchorMax = new Vector2(1, 0.5f); mrt.pivot = new Vector2(1, 0.5f);
            mrt.anchoredPosition = new Vector2(-8, 0); mrt.sizeDelta = new Vector2(80, 28);
            mcpText = mcpBtn.GetComponentInChildren<Text>();

            // 对话模式（新建时选定，之后只读展示）
            modeText = MakeText(header, "模式：普通对话", 13, ColDim, TextAnchor.MiddleRight);
            modeText.rectTransform.anchorMin = new Vector2(1, 0.5f); modeText.rectTransform.anchorMax = new Vector2(1, 0.5f);
            modeText.rectTransform.pivot = new Vector2(1, 0.5f);
            modeText.rectTransform.anchoredPosition = new Vector2(-292, 0);
            modeText.rectTransform.sizeDelta = new Vector2(170, 26);

            // ⚠️ 权限下拉框【不能】放header 里：header main 先创建，
            //    Dropdown 展开的列表会被后面创建的聊天区盖住（列表在父级同层渲染）
            //    它在下面（statusText 之后）作root 的最后一个子物体创建，保证盖在最上层
            // 不再UI 上的 ✕：独立程序用的是普通窗口，右上角本来就有系统关闭按钮；
            //   嵌在桌宠里时Esc / F8 收起。作者明确要求去掉这个叉
            // 左侧
            sidebar = MakeRect("Sidebar", root, new Vector2(0, 0), new Vector2(0, 1), new Vector2(14, 14), new Vector2(340, -54));
            sidebar.gameObject.AddComponent<Image>().color = ColPanel;

            var sideTitle = MakeText(sidebar, "工作区", 22, ColText, TextAnchor.UpperLeft);
            var st = sideTitle.rectTransform; st.anchorMin = new Vector2(0, 1); st.anchorMax = new Vector2(1, 1);
            st.pivot = new Vector2(0.5f, 1); st.anchoredPosition = new Vector2(0, -14); st.sizeDelta = new Vector2(-28, 34);

            var btnNew = MakeButton(sidebar, "＋ 新建对话", 0, 0, 0, 0, ColAccent, ShowNewConversationDialog);
            var nrt = btnNew.GetComponent<RectTransform>();
            nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0.5f, 1);
            nrt.anchoredPosition = new Vector2(0, -56); nrt.sizeDelta = new Vector2(-28, 38);

            // 会话列表
            var listRect = MakeRect("ConvList", sidebar, new Vector2(0, 0), new Vector2(1, 1), new Vector2(14, 106), new Vector2(-14, -104));
            var scroll = listRect.gameObject.AddComponent<ScrollRect>();
            var viewport = MakeRect("Viewport", listRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();
            var vpImg = viewport.gameObject.AddComponent<Image>(); vpImg.color = new Color(0, 0, 0, 0.001f);
            listContent = MakeRect("Content", viewport, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            listContent.pivot = new Vector2(0.5f, 1);
            var vlg = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandHeight = false; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childControlWidth = true;
            vlg.spacing = 6; vlg.padding = new RectOffset(0, 0, 4, 4);
            var fit = listContent.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = listContent;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.scrollSensitivity = 30;

            // 底部：删+ 路由
            var btnDel = MakeButton(sidebar, "删除当前对话", 0, 0, 0, 0, new Color(0.42f, 0.20f, 0.22f), () =>
            {
                // 先掐断这个会话正在跑的输出，否则它会继续在后台生成、甚至串到新会话的界面里
                CancelAsk("已删除对话，停止生成");
                int idx = WorkspaceStore.ActiveIndex;
                WorkspaceStore.DeleteConversation(idx);
                RefreshConversationList(); RefreshChat(); Status("已删除");
            });
            var drt = btnDel.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(0, 0); drt.anchorMax = new Vector2(1, 0); drt.pivot = new Vector2(0.5f, 0);
            drt.anchoredPosition = new Vector2(0, 58); drt.sizeDelta = new Vector2(-28, 38);

            var btnRoute = MakeButton(sidebar, "模型路由", 0, 0, 0, 0, ColItem, CycleRoute);
            var rrt = btnRoute.GetComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0, 0); rrt.anchorMax = new Vector2(1, 0); rrt.pivot = new Vector2(0.5f, 0);
            rrt.anchoredPosition = new Vector2(0, 14); rrt.sizeDelta = new Vector2(-28, 38);
            routeText = btnRoute.GetComponentInChildren<Text>();

            // 右侧主区
            main = MakeRect("Main", root, new Vector2(0, 0), new Vector2(1, 1), new Vector2(354, 14), new Vector2(-14, -54));
            main.gameObject.AddComponent<Image>().color = ColPanel;

            titleText = MakeText(main, "对话", 20, ColText, TextAnchor.UpperLeft);
            var trt = titleText.rectTransform; trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1);
            trt.pivot = new Vector2(0.5f, 1); trt.anchoredPosition = new Vector2(0, -12); trt.sizeDelta = new Vector2(-28, 30);

            // 聊天滚动
            var chatRect = MakeRect("Chat", main, new Vector2(0, 0), new Vector2(1, 1), new Vector2(16, 96), new Vector2(-16, -54));
            chatScroll = chatRect.gameObject.AddComponent<ScrollRect>();
            var cvp = MakeRect("Viewport", chatRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            cvp.gameObject.AddComponent<RectMask2D>();
            var cimg = cvp.gameObject.AddComponent<Image>(); cimg.color = new Color(0, 0, 0, 0.001f);
            chatContent = MakeRect("Content", cvp, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            chatContent.pivot = new Vector2(0.5f, 1);
            var cvlg = chatContent.gameObject.AddComponent<VerticalLayoutGroup>();
            cvlg.childForceExpandHeight = false; cvlg.childControlHeight = true;
            cvlg.childForceExpandWidth = true; cvlg.childControlWidth = true;
            cvlg.spacing = 8; cvlg.padding = new RectOffset(4, 4, 4, 8);
            var cfit = chatContent.gameObject.AddComponent<ContentSizeFitter>();
            cfit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            chatScroll.viewport = cvp; chatScroll.content = chatContent;
            chatScroll.horizontal = false; chatScroll.vertical = true;
            chatScroll.scrollSensitivity = 40;

            // 输入
            inputRow = MakeRect("InputRow", main, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 16), new Vector2(-16, 84));
            var irImg = inputRow.gameObject.AddComponent<Image>(); irImg.color = new Color(0.09f, 0.10f, 0.13f, 1f);

            var attachBtn = MakeButton(inputRow, "附件", 0, 0, 0, 0, ColItem, PickAttachment);
            var art = attachBtn.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0, 0); art.anchorMax = new Vector2(0, 1); art.pivot = new Vector2(0, 0.5f);
            art.anchoredPosition = new Vector2(8, 0); art.sizeDelta = new Vector2(56, -16);

            var screenBtn = MakeButton(inputRow, "截屏", 0, 0, 0, 0, ColItem, AttachScreen);
            var srtB = screenBtn.GetComponent<RectTransform>();
            srtB.anchorMin = new Vector2(0, 0); srtB.anchorMax = new Vector2(0, 1); srtB.pivot = new Vector2(0, 0.5f);
            srtB.anchoredPosition = new Vector2(70, 0); srtB.sizeDelta = new Vector2(56, -16);

            var sendBtn = MakeButton(inputRow, "发送", 0, 0, 0, 0, ColAccent, Send);
            var srt = sendBtn.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(1, 0); srt.anchorMax = new Vector2(1, 1); srt.pivot = new Vector2(1, 0.5f);
            srt.anchoredPosition = new Vector2(-8, 0); srt.sizeDelta = new Vector2(96, -16);

            // 停止按钮：正在生成时出现，点了立刻掐断这一轮（含工具回环）
            stopBtn = MakeButton(inputRow, "■ 停止", 0, 0, 0, 0, new Color(0.42f, 0.20f, 0.22f), () => CancelAsk("已停止生成"));
            var stpRt = stopBtn.GetComponent<RectTransform>();
            stpRt.anchorMin = new Vector2(1, 0); stpRt.anchorMax = new Vector2(1, 1); stpRt.pivot = new Vector2(1, 0.5f);
            stpRt.anchoredPosition = new Vector2(-112, 0); stpRt.sizeDelta = new Vector2(88, -16);
            stopBtn.gameObject.SetActive(false);

            var inputRect = MakeRect("Input", inputRow, new Vector2(0, 0), new Vector2(1, 1), new Vector2(134, 10), new Vector2(-210, -10));
            var inputImg = inputRect.gameObject.AddComponent<Image>();
            inputImg.color = new Color(0.06f, 0.07f, 0.09f, 1f);
            input = inputRect.gameObject.AddComponent<InputField>();
            var placeholder = MakeText(inputRect, "说点什么…（回车发送，Shift+回车换行）", 16, new Color(0.5f, 0.55f, 0.6f), TextAnchor.MiddleLeft);
            placeholder.rectTransform.anchorMin = Vector2.zero; placeholder.rectTransform.anchorMax = Vector2.one;
            placeholder.rectTransform.offsetMin = new Vector2(12, 0); placeholder.rectTransform.offsetMax = new Vector2(-12, 0);
            var itext = MakeText(inputRect, "", 16, ColText, TextAnchor.MiddleLeft);
            itext.rectTransform.anchorMin = Vector2.zero; itext.rectTransform.anchorMax = Vector2.one;
            itext.rectTransform.offsetMin = new Vector2(12, 0); itext.rectTransform.offsetMax = new Vector2(-12, 0);
            input.textComponent = itext;
            input.placeholder = placeholder;
            input.lineType = InputField.LineType.MultiLineNewline;
            input.onEndEdit.AddListener(v => { if (Input.GetKeyDown(KeyCode.Return) && !Input.GetKey(KeyCode.LeftShift)) Send(); });

            statusText = MakeText(root, "", 13, ColDim, TextAnchor.LowerLeft);
            var srt2 = statusText.rectTransform; srt2.anchorMin = new Vector2(0, 0); srt2.anchorMax = new Vector2(1, 0);
            srt2.pivot = new Vector2(0.5f, 0); srt2.anchoredPosition = new Vector2(0, 34); srt2.sizeDelta = new Vector2(-1500, 22);

            // 工具权限下拉框（随时可改）：作为 root 的最后一个子物体 = 面板最上层
            // 展开的列表不会被 header / 聊天区盖住
            permDropdown = MakeDropdown(root, PermLabels, 1, i =>
            {
                var cc = WorkspaceStore.Active;
                if (cc == null) return;
                cc.permission = PermValues[Mathf.Clamp(i, 0, PermValues.Length - 1)];
                WorkspaceStore.Save();
                Status("工具权限 → " + PermLabels[Mathf.Clamp(i, 0, PermLabels.Length - 1)]);
            });
            var prt = permDropdown.GetComponent<RectTransform>();
            prt.anchorMin = new Vector2(1, 1); prt.anchorMax = new Vector2(1, 1);
            prt.pivot = new Vector2(1, 1);
            prt.anchoredPosition = new Vector2(-96, -6);
            prt.sizeDelta = new Vector2(190, 28);

            BuildConfirmBar();
        }

        void BuildConfirmBar()
        {
            confirmBar = MakeRect("Confirm", root, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 70), new Vector2(0, 70));
            confirmBar.anchorMin = new Vector2(0.5f, 0); confirmBar.anchorMax = new Vector2(0.5f, 0);
            confirmBar.pivot = new Vector2(0.5f, 0);
            confirmBar.sizeDelta = new Vector2(860, 56);
            confirmBar.gameObject.AddComponent<Image>().color = new Color(0.22f, 0.16f, 0.10f, 0.98f);

            confirmText = MakeText(confirmBar, "", 15, ColText, TextAnchor.MiddleLeft);
            confirmText.rectTransform.anchorMin = Vector2.zero; confirmText.rectTransform.anchorMax = Vector2.one;
            confirmText.rectTransform.offsetMin = new Vector2(14, 0); confirmText.rectTransform.offsetMax = new Vector2(-220, 0);

            confirmYes = MakeButton(confirmBar, "允许", 0, 0, 0, 0, new Color(0.2f, 0.45f, 0.25f), () => ResolveConfirm(true));
            var yrt = confirmYes.GetComponent<RectTransform>(); yrt.anchorMin = new Vector2(1, 0.5f); yrt.anchorMax = new Vector2(1, 0.5f);
            yrt.pivot = new Vector2(1, 0.5f); yrt.anchoredPosition = new Vector2(-106, 0); yrt.sizeDelta = new Vector2(90, 36);

            confirmNo = MakeButton(confirmBar, "拒绝", 0, 0, 0, 0, new Color(0.4f, 0.2f, 0.2f), () => ResolveConfirm(false));
            var nrt2 = confirmNo.GetComponent<RectTransform>(); nrt2.anchorMin = new Vector2(1, 0.5f); nrt2.anchorMax = new Vector2(1, 0.5f);
            nrt2.pivot = new Vector2(1, 0.5f); nrt2.anchoredPosition = new Vector2(-10, 0); nrt2.sizeDelta = new Vector2(90, 36);

            confirmBar.gameObject.SetActive(false);
        }

        // ============================================================
        //  列表 / 聊天渲染
        // ============================================================

        void RefreshConversationList()
        {
            if (listContent == null) return;
            for (int i = 0; i < listItems.Count; i++) if (listItems[i] != null) Destroy(listItems[i]);
            listItems.Clear();

            var convs = WorkspaceStore.Conversations;
            for (int i = 0; i < convs.Count; i++)
            {
                int idx = i;
                var c = convs[i];
                bool running = busy && askConv == c;
                string label = (c.title ?? "新对话");
                if (label.Length > 12) label = label.Substring(0, 12) + "…";
                if (running) label = "⟳ " + label;      // 会话标签上的"正在输出"标记

                Color bg = i == WorkspaceStore.ActiveIndex ? ColUser
                         : (running ? new Color(0.18f, 0.28f, 0.24f) : ColItem);
                var btn = MakeButton(listContent, label, 0, 0, 0, 0, bg,
                    () =>
                    {
                        // 切换会话【不】停止生成：那一轮会继续写进它自己的会话
                        //   标签上的 会一直亮着；只有删除会话或按「停止」才终止
                        editIndex = -1;
                        WorkspaceStore.ActiveIndex = idx;
                        WorkspaceStore.Save();
                        RefreshConversationList();
                        RefreshChat();
                    });
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 40; le.minHeight = 40;
                listItems.Add(btn.gameObject);
            }
            Canvas.ForceUpdateCanvases();
        }

        void RefreshChat()
        {
            for (int i = 0; i < bubbles.Count; i++) if (bubbles[i] != null) Destroy(bubbles[i]);
            bubbles.Clear();

            var c = WorkspaceStore.Active;
            if (c == null) return;
            string mode = ModeOf(c);
            titleText.text = c.title + "　·　" + WorkspaceStore.RouteOf(c).name + " / " + WorkspaceStore.RouteOf(c).model
                           + "　·　" + ModeLabel(mode);
            routeText.text = "模型：" + WorkspaceStore.RouteOf(c).name;
            if (modeText != null) modeText.text = "模式：" + ModeLabel(mode);
            if (permDropdown != null)
            {
                permDropdown.SetValueWithoutNotify(PermIndex(c.permission));
                permDropdown.RefreshShownValue();
            }

            for (int i = 0; i < c.messages.Count; i++)
            {
                var m = c.messages[i];
                if (m == null) continue;
                if (!string.IsNullOrEmpty(m.trace)) AddTraceBubble(m.trace);
                AddBubble(m.role == "user" ? "我" : "小白", m.content, m.role == "user" ? ColUser : ColAi, m, i);
            }

            // 切回"正在输出"的会话：把工作链和已生成的内容接回去
            //   （RefreshChat 会把所有气泡销毁，包括后台那一轮正在用的）
            if (busy && askConv == c)
            {
                if (liveTraceBuf.Length > 0)
                {
                    liveTrace = BuildTraceView(true);
                    if (liveTrace.text != null) liveTrace.text.text = liveTraceBuf.ToString().TrimEnd('\n');
                    RefreshTraceLayout(liveTrace);
                    StartCoroutine(RefreshTraceNextFrame(liveTrace));
                }
                if (streamActive) BeginStreamBubble(streamBuf.ToString());
            }

            StartCoroutine(ScrollToBottom());
        }

        void AddBubble(string who, string text, Color bg, WorkspaceMessage msg = null, int index = -1)
        {
            // ⚠️ 不要手算高度（字数估行数会算错，中文/英文混排更离气泡互相压住）
            //    交给 uGUI：VerticalLayoutGroup 定内边距，ContentSizeFitter Text 
            //    preferredHeight 自动撑高
            var row = MakeRect("Bubble", chatContent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var img = row.gameObject.AddComponent<Image>(); img.color = bg;

            var vlg = row.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(14, 10, 6, 8);
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var fit = row.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 第一行：名字 + 「复制」按钮（横向布局，天然靠右，不跟纵向布局打架
            var head = MakeRect("Head", row, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            var hlg = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 6;
            var hle = head.gameObject.AddComponent<LayoutElement>();
            hle.preferredHeight = 20f; hle.minHeight = 20f;

            var nameT = MakeText(head, who, 12, ColDim, TextAnchor.MiddleLeft);
            nameT.horizontalOverflow = HorizontalWrapMode.Overflow;
            var nle = nameT.gameObject.AddComponent<LayoutElement>();
            nle.flexibleWidth = 1f; nle.preferredHeight = 20f; nle.minHeight = 20f;

            string copySrc = text ?? "";
            if (copySrc.Length > 0)
            {
                var copyBtn = MakeButton(head, "复制", 0, 0, 0, 0, new Color(0.22f, 0.25f, 0.31f, 0.95f),
                    () => CopyToClipboard(copySrc));
                var cle = copyBtn.gameObject.AddComponent<LayoutElement>();
                cle.preferredWidth = 46f; cle.preferredHeight = 18f; cle.minWidth = 46f;
                var ct = copyBtn.GetComponentInChildren<Text>();
                if (ct != null) { ct.fontSize = 12; ct.alignment = TextAnchor.MiddleCenter; }
            }

            // 消息操作：用户消息可编辑/删除；回答可重新生成/删除（和 DeepSeek 网页版一致）
            if (msg != null && index >= 0)
            {
                if (msg.role == "user")
                    AddHeadButton(head, "编辑", 46f, () => BeginEditMessage(index));
                else
                    AddHeadButton(head, "重新生成", 76f, () => RegenerateFrom(index));
                AddHeadButton(head, "删除", 46f, () => DeleteMessage(index));
            }

            // 附件占位卡片（图片缩略图 / 文件方块），点开预览
            if (msg != null && msg.attachments != null && msg.attachments.Length > 0)
                AddAttachmentChips(row, msg.attachments);

            // ⚠️ 单条 Text 太长（几万字）会超过 uGUI 单网65535 顶点上限，整条渲染不出来
            //    —表现就是一个空的大色块。界面只显示6000 字，完整内容照样发给模型
            const int displayCap = 6000;
            string disp = text ?? "";
            if (disp.Length > displayCap)
                disp = disp.Substring(0, displayCap)
                     + "\n\n…（还有 " + (disp.Length - displayCap) + " 字没显示；完整内容仍会发给模型。用右上角「复制」可拿全文）";

            // 含 LaTeX 的消息走 SkiaSharp + CSharpMath：文字和公式一起排成一张图
            // （uGUI Text 画不出分式/根号/矩阵，只能这样）。
            // 纯文字消息继续用 uGUI Text：可选中、清晰、便宜。
            if (LatexRenderer.Available && LatexRenderer.ContainsMath(disp))
            {
                var latexGo = MakeRect("Latex", row, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                var lb = latexGo.gameObject.AddComponent<LatexBlock>();
                lb.source = disp;
                lb.fontSize = 16f;
                lb.color = ColText;
            }
            else
            {
                // Markdown → uGUI 富文本（标题/粗体/代码块/列表…）。用户消息也渲染，
                // 这样贴进去的代码片段两边看起来一致。
                var t = MakeText(row, MarkdownText.ToRich(disp), 16, ColText, TextAnchor.UpperLeft);
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Overflow;
            }

            bubbles.Add(row.gameObject);
        }

        void CopyToClipboard(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            GUIUtility.systemCopyBuffer = s;
            Status("已复制 " + s.Length + " 字到剪贴板");
        }

        // ---------- 消息级操作（编辑 / 删除 / 重新生成----------

        void AddHeadButton(RectTransform head, string label, float w, Action onClick)
        {
            var b = MakeButton(head, label, 0, 0, 0, 0, new Color(0.22f, 0.25f, 0.31f, 0.95f), onClick);
            var le = b.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = w; le.preferredHeight = 18f; le.minWidth = w;
            var t = b.GetComponentInChildren<Text>();
            if (t != null) { t.fontSize = 12; t.alignment = TextAnchor.MiddleCenter; }
        }

        int editIndex = -1;

        /// <summary>把这条用户消息放回输入框；发送时会把从这里往后的内容截掉重写/summary>
        void BeginEditMessage(int index)
        {
            var c = WorkspaceStore.Active;
            if (c == null || index < 0 || index >= c.messages.Count) return;
            var m = c.messages[index];
            if (m == null || m.role != "user") return;
            if (busy) { Status("还在等上一轮…先停止再编辑"); return; }
            editIndex = index;
            if (input != null) { input.text = m.content ?? ""; input.ActivateInputField(); }
            Status("编辑中：发送后会替换这条消息，并从它之后重新生成");
        }

        void DeleteMessage(int index)
        {
            var c = WorkspaceStore.Active;
            if (c == null || index < 0 || index >= c.messages.Count) return;
            if (busy && askConv == c) { Status("正在生成中，先停止再删除消息"); return; }
            c.messages.RemoveAt(index);
            if (editIndex == index) editIndex = -1;
            else if (editIndex > index) editIndex--;
            c.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            WorkspaceStore.Save();
            RefreshConversationList();
            RefreshChat();
            Status("已删除这条消息");
        }

        /// <summary>从这条回答开始重新生成（把它和它之后的内容截掉，再问一次）/summary>
        void RegenerateFrom(int index)
        {
            if (busy) { Status("还在跑上一轮…先停止再重新生成"); return; }
            var c = WorkspaceStore.Active;
            if (c == null || index < 0 || index >= c.messages.Count) return;
            if (index > 0 && c.messages[index].role == "assistant" && c.messages[index - 1].role != "user")
            {
                // 理论上不会发生；保底从最近一条用户消息之后切
                while (index > 0 && c.messages[index - 1].role != "user") index--;
            }
            c.messages.RemoveRange(index, c.messages.Count - index);
            c.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            WorkspaceStore.Save();
            RefreshChat();
            RefreshConversationList();
            askCo = StartCoroutine(RunAsk(c));
            Status("重新生成中…");
        }

        // ============================================================
        //  附件占位卡片 + 预览
        // ============================================================

        static readonly Dictionary<string, Sprite> spriteCache = new Dictionary<string, Sprite>();

        Sprite LoadSpriteCached(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Sprite s;
            if (spriteCache.TryGetValue(path, out s)) return s;
            try
            {
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                if (fi.Length > 12 * 1024 * 1024) return null;
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return null; }
                if (tex.width * tex.height > 8 * 1024 * 1024) { UnityEngine.Object.Destroy(tex); return null; }
                tex.wrapMode = TextureWrapMode.Clamp;
                s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                spriteCache[path] = s;
                return s;
            }
            catch { return null; }
        }

        /// <summary>附件卡片：图缩略图方块，文件=「文件名」方块；点一下打开预览/summary>
        void AddAttachmentChips(RectTransform row, string[] paths)
        {
            bool anyImage = false;
            for (int i = 0; i < paths.Length; i++) if (IsImage(paths[i])) { anyImage = true; break; }

            var strip = MakeRect("Attachments", row, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            var hlg = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8; hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            var sle = strip.gameObject.AddComponent<LayoutElement>();
            sle.preferredHeight = anyImage ? 100f : 30f;
            sle.minHeight = sle.preferredHeight;

            int n = Mathf.Min(paths.Length, 6);
            for (int i = 0; i < n; i++)
            {
                string p = paths[i];
                string name = Path.GetFileName(p);
                if (IsImage(p))
                {
                    var chip = new GameObject("Att_" + name, typeof(RectTransform), typeof(Image), typeof(Button));
                    var rt = chip.GetComponent<RectTransform>();
                    rt.SetParent(strip, false);
                    var cle = chip.AddComponent<LayoutElement>();
                    cle.preferredWidth = 92f; cle.preferredHeight = 92f; cle.minWidth = 92f;
                    var bg = chip.GetComponent<Image>(); bg.color = new Color(0.16f, 0.18f, 0.22f, 1f);
                    var btn = chip.GetComponent<Button>(); btn.targetGraphic = bg;
                    string cap = p;
                    btn.onClick.AddListener(() => OpenAttachmentPreview(cap));

                    var sp = LoadSpriteCached(p);
                    if (sp != null)
                    {
                        var thumbGo = new GameObject("Thumb", typeof(RectTransform), typeof(Image));
                        var trt = thumbGo.GetComponent<RectTransform>();
                        trt.SetParent(rt, false);
                        trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1);
                        trt.offsetMin = new Vector2(6, 20); trt.offsetMax = new Vector2(-6, -6);
                        var ti = thumbGo.GetComponent<Image>();
                        ti.sprite = sp; ti.preserveAspect = true; ti.raycastTarget = false;
                    }
                    var lbl = MakeText(rt, name, 10, ColDim, TextAnchor.MiddleCenter);
                    var lrt = lbl.rectTransform;
                    lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0);
                    lrt.pivot = new Vector2(0.5f, 0);
                    lrt.sizeDelta = new Vector2(-6, 16); lrt.anchoredPosition = new Vector2(0, 2);
                    lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
                    lbl.raycastTarget = false;
                }
                else
                {
                    var chip = MakeButton(strip, "📄 " + name, 0, 0, 0, 0, new Color(0.16f, 0.18f, 0.22f, 1f),
                        () => OpenAttachmentPreview(p));
                    var cle = chip.gameObject.AddComponent<LayoutElement>();
                    int w = Mathf.Clamp(name.Length * 9 + 44, 96, 260);
                    cle.preferredWidth = w; cle.preferredHeight = 28f; cle.minHeight = 28f;
                    var ct = chip.GetComponentInChildren<Text>();
                    if (ct != null) { ct.fontSize = 12; ct.alignment = TextAnchor.MiddleLeft; }
                }
            }
            if (paths.Length > n)
            {
                var more = MakeText(strip, "+" + (paths.Length - n), 12, ColDim, TextAnchor.MiddleLeft);
                var mle = more.gameObject.AddComponent<LayoutElement>();
                mle.preferredWidth = 30f;
            }
        }

        // ---------- 附件预览 ----------
        RectTransform previewOverlay;

        void CloseAttachmentPreview()
        {
            if (previewOverlay != null) Destroy(previewOverlay.gameObject);
            previewOverlay = null;
        }

        void OpenAttachmentPreview(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            CloseAttachmentPreview();

            var mask = MakeRect("PreviewMask", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var mimg = mask.gameObject.AddComponent<Image>(); mimg.color = new Color(0f, 0f, 0f, 0.82f);
            var mbtn = mask.gameObject.AddComponent<Button>(); mbtn.targetGraphic = mimg;
            mbtn.onClick.AddListener(CloseAttachmentPreview);
            previewOverlay = mask;

            var panel = MakeRect("PreviewPanel", mask, new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.94f), Vector2.zero, Vector2.zero);
            var pimg = panel.gameObject.AddComponent<Image>(); pimg.color = new Color(0.08f, 0.09f, 0.12f, 0.995f);
            // 吃掉点击，别让点面板时把整个预览关掉
            var pbtn = panel.gameObject.AddComponent<Button>(); pbtn.targetGraphic = pimg;
            pbtn.onClick.AddListener(() => { });

            var close = MakeButton(panel, "关闭 ✕", 0, 0, 0, 0, new Color(0.42f, 0.20f, 0.22f), CloseAttachmentPreview);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(1, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-10, -10); crt.sizeDelta = new Vector2(96, 32);

            var nameT = MakeText(panel, Path.GetFileName(path), 14, ColDim, TextAnchor.MiddleLeft);
            var nrt = nameT.rectTransform;
            nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1);
            nrt.pivot = new Vector2(0.5f, 1);
            nrt.offsetMin = new Vector2(16, 0); nrt.offsetMax = new Vector2(-120, 0);
            nrt.anchoredPosition = new Vector2(0, -10);
            nrt.sizeDelta = new Vector2(-136, 26);

            if (IsImage(path))
            {
                var sp = LoadSpriteCached(path);
                if (sp != null)
                {
                    var holder = MakeRect("Img", panel, Vector2.zero, Vector2.one, new Vector2(16, 16), new Vector2(-16, -50));
                    var im = holder.gameObject.AddComponent<Image>();
                    im.sprite = sp; im.preserveAspect = true; im.color = Color.white;
                }
                else
                {
                    var err = MakeText(panel, "（这张图片太大或格式不支持，无法预览。文件本身还在原路径）", 14, ColDim, TextAnchor.MiddleCenter);
                    err.rectTransform.anchorMin = Vector2.zero; err.rectTransform.anchorMax = Vector2.one;
                    err.rectTransform.offsetMin = new Vector2(20, 20); err.rectTransform.offsetMax = new Vector2(-20, -60);
                }
            }
            else
            {
                // ⚠️ 预览也受单网格顶点上限限制：超过一万多字整Text 会渲染不出来（一片空白）
                string body = ReadTextSafe(path, 30000);
                if (body.Length > 12000)
                    body = body.Substring(0, 12000) + "\n\n…（预览只显示前 12000 字；完整文件还在原路径）";
                var rect = MakeRect("TextScroll", panel, Vector2.zero, Vector2.one, new Vector2(16, 16), new Vector2(-16, -50));
                var sr = rect.gameObject.AddComponent<ScrollRect>();
                var vp = MakeRect("Viewport", rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                vp.gameObject.AddComponent<RectMask2D>();
                var vi = vp.gameObject.AddComponent<Image>(); vi.color = new Color(0, 0, 0, 0.001f);
                var content = MakeRect("Content", vp, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
                content.pivot = new Vector2(0.5f, 1);
                content.sizeDelta = new Vector2(0, 28);   // 给个初始高度，否则首帧 VLG 会算成 0
                var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                vlg.childForceExpandWidth = true; vlg.childControlWidth = true;
                vlg.childForceExpandHeight = false; vlg.childControlHeight = true;
                var fit = content.gameObject.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var t = MakeText(content, body, 13, ColText, TextAnchor.UpperLeft);
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                sr.viewport = vp; sr.content = content;
                sr.horizontal = false; sr.vertical = true; sr.scrollSensitivity = 40;
                StartCoroutine(PreviewTopNextFrame(sr));
            }

            Canvas.ForceUpdateCanvases();
        }

        IEnumerator PreviewTopNextFrame(ScrollRect sr)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            if (sr != null) sr.verticalNormalizedPosition = 1f;   // 从文件开头看
        }

        // ---------- 工作链（可收+ 内部滚动---------
        class TraceView
        {
            public GameObject root;
            public RectTransform body;
            public ScrollRect scroll;
            public Text text;
            public Text header;
            public LayoutElement panelLe;
            public LayoutElement bodyLe;
            public bool expanded;
        }

        const float TraceCollapsedH = 35f;   // 24 标题 + 上下 padding
        const float TraceExpandedH = 269f;   // 24 标题 + 4 间距 + 230 正文 + 上下 padding

        TraceView liveTrace;
        readonly StringBuilder liveTraceBuf = new StringBuilder();

        /// <summary>
        /// 工作链面板：一行标题（点击收起/展开 固定高度、内部可滚的正文
        /// 展开时最多占 230px，不会再把整个聊天区顶满
        /// </summary>
        TraceView BuildTraceView(bool expanded)
        {
            var v = new TraceView();
            var panel = MakeRect("Trace", chatContent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var pimg = panel.gameObject.AddComponent<Image>();
            pimg.color = new Color(0.10f, 0.115f, 0.145f, 1f);
            pimg.raycastTarget = false;   // 背景不抢点击
            var vlg = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 5, 6);
            vlg.spacing = 4;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            // ⚠️ 这里【不要】用 ContentSizeFitter：外层的 chatContent 也是 VLG（childControlHeight=true），
            //    两个布局系统互相等待时会把面板高度算正文盖住下一条消息
            //    直接给面板一个固定的 LayoutElement 高度，收展开时改这个数值
            var panelLe = panel.gameObject.AddComponent<LayoutElement>();
            panelLe.preferredHeight = expanded ? TraceExpandedH : TraceCollapsedH;
            panelLe.minHeight = panelLe.preferredHeight;
            v.panelLe = panelLe;
            v.root = panel.gameObject;

            // 标题行：整行都是按钮
            var hgo = new GameObject("Header", typeof(RectTransform), typeof(Image), typeof(Button));
            var hrt = hgo.GetComponent<RectTransform>();
            hrt.SetParent(panel, false);
            var hle = hgo.AddComponent<LayoutElement>();
            hle.preferredHeight = 24f; hle.minHeight = 24f;
            var himg = hgo.GetComponent<Image>();
            himg.color = new Color(0.14f, 0.16f, 0.20f, 1f);
            var htxt = MakeText(hrt, "⚙ 工作链", 12, ColDim, TextAnchor.MiddleLeft);
            htxt.rectTransform.anchorMin = Vector2.zero; htxt.rectTransform.anchorMax = Vector2.one;
            htxt.rectTransform.offsetMin = new Vector2(8, 0); htxt.rectTransform.offsetMax = new Vector2(-8, 0);
            htxt.horizontalOverflow = HorizontalWrapMode.Overflow;
            htxt.raycastTarget = false;   // 让点击直接落到标题行的 Image/Button 上
            v.header = htxt;

            // 正文：固定高+ 内部滚动
            var bodyRt = MakeRect("Body", panel, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var ble = bodyRt.gameObject.AddComponent<LayoutElement>();
            ble.preferredHeight = 40f; ble.minHeight = 24f; ble.flexibleHeight = 0f;
            v.bodyLe = ble;
            var sr = bodyRt.gameObject.AddComponent<ScrollRect>();
            var vp = MakeRect("Viewport", bodyRt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            vp.gameObject.AddComponent<RectMask2D>();
            var vimg = vp.gameObject.AddComponent<Image>();
            vimg.color = new Color(0, 0, 0, 0.001f);
            vimg.raycastTarget = false;   // ⚠️ 透明 Viewport 绝不能吃点击（会盖住上面的标题按钮）
            var content = MakeRect("Content", vp, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            content.pivot = new Vector2(0.5f, 1);
            var cvlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            cvlg.childForceExpandHeight = false; cvlg.childControlHeight = true;
            cvlg.childForceExpandWidth = true; cvlg.childControlWidth = true;
            var cfit = content.gameObject.AddComponent<ContentSizeFitter>();
            cfit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var t = MakeText(content, "", 12, ColDim, TextAnchor.UpperLeft);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            sr.viewport = vp; sr.content = content;
            sr.horizontal = false; sr.vertical = true; sr.scrollSensitivity = 30;
            v.body = bodyRt; v.scroll = sr; v.text = t;

            v.expanded = expanded;
            bodyRt.gameObject.SetActive(expanded);

            var btn = hgo.GetComponent<Button>();
            btn.targetGraphic = himg;
            btn.onClick.AddListener(() =>
            {
                var tv = v;
                tv.expanded = !tv.expanded;
                if (tv.body != null) tv.body.gameObject.SetActive(tv.expanded);
                RefreshTraceLayout(tv);
                if (chatContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(chatContent);
                Canvas.ForceUpdateCanvases();
                StartCoroutine(ScrollToBottom());
            });

            bubbles.Add(v.root);
            RefreshTraceLayout(v);
            StartCoroutine(RefreshTraceNextFrame(v));
            return v;
        }

        /// <summary>
        /// 等一帧再算高度：Text.preferredHeight 依赖当前宽度
        /// Create 出来那一刻宽度还没被布局系统算出来（0 宽会preferredHeight 爆炸）
        /// </summary>
        IEnumerator RefreshTraceNextFrame(TraceView v)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            RefreshTraceLayout(v);
            if (chatContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(chatContent);
        }

        /// <summary>
        /// 更新标题文字 + 按内容算高度
        /// 正文高度 = clamp(文本 preferredHeight, 24, 230) 短工作链不占地方，长工作链内部滚动
        /// </summary>
        void RefreshTraceLayout(TraceView v)
        {
            if (v == null || v.header == null) return;
            int lines = v.text != null && !string.IsNullOrEmpty(v.text.text)
                ? v.text.text.Split('\n').Length : 1;
            v.header.text = "⚙ 工作链 · " + lines + " 行"
                          + (v.expanded ? "　▾ 点击收起" : "　▸ 点击展开");

            if (v.bodyLe == null) return;
            float pref = v.text != null ? v.text.preferredHeight : 24f;
            float bodyH = Mathf.Clamp(pref + 8f, 24f, 230f);
            v.bodyLe.preferredHeight = bodyH;
            v.bodyLe.minHeight = Mathf.Min(bodyH, 24f);

            if (v.panelLe != null)
            {
                v.panelLe.preferredHeight = v.expanded ? (24f + 4f + bodyH + 11f) : TraceCollapsedH;
                v.panelLe.minHeight = v.panelLe.preferredHeight;
            }
        }

        /// <summary>往「工作链」里追加一行（会立刻出现在聊天区，并滚到最新）/summary>
        void AppendTrace(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            liveTraceBuf.Append(line).Append('\n');

            // 用户切去别的会话只记账，不碰界面（切回来RefreshChat 会重建）
            if (askConv != null && WorkspaceStore.Active != askConv) return;

            if (liveTrace != null && liveTrace.root == null) liveTrace = null;   // 已被 RefreshChat 销毁
            if (liveTrace == null) liveTrace = BuildTraceView(true);
            if (liveTrace.text != null) liveTrace.text.text = liveTraceBuf.ToString().TrimEnd('\n');
            RefreshTraceLayout(liveTrace);
            StartCoroutine(RefreshTraceNextFrame(liveTrace));
            StartCoroutine(ScrollTraceToBottom());
        }

        IEnumerator ScrollTraceToBottom()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            if (liveTrace != null && liveTrace.scroll != null)
                liveTrace.scroll.verticalNormalizedPosition = 0f;
            if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
        }

        void EndLiveTrace()
        {
            // ⚠️ root 可能已经RefreshChat 销毁了，别对死对象Destroy
            if (liveTrace != null && liveTrace.root != null) Destroy(liveTrace.root);
            liveTrace = null;
        }

        /// <summary>
        /// 历史消息里的工作链（只回看，不发给模型）
        /// 默认【展开】—但面板高度被锁在 230px 且内部可滚，不会再把聊天区顶满；
        /// 觉得烦就点标题收起
        /// </summary>
        void AddTraceBubble(string trace)
        {
            var v = BuildTraceView(true);
            if (v.text != null) v.text.text = trace ?? "";
            RefreshTraceLayout(v);
            StartCoroutine(RefreshTraceNextFrame(v));
        }

        IEnumerator ScrollToBottom()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
        }

        void Status(string s) { if (statusText != null) statusText.text = s; Debug.Log("[Workspace] " + s); }

        void CycleRoute()
        {
            var c = WorkspaceStore.Active;
            if (c == null || WorkspaceStore.Routes.Count == 0) return;
            c.routeIndex = (c.routeIndex + 1) % WorkspaceStore.Routes.Count;
            WorkspaceStore.Save();
            RefreshChat();
            Status("模型路由 → " + WorkspaceStore.RouteOf(c).name);
        }

        // ============================================================
        //  附件
        // ============================================================

        readonly List<string> pendingAttachments = new List<string>();
        string pendingScreenB64;   // 「📷 看屏幕」：随下一条消息发出去，不写进历史

        void AttachScreen()
        {
            var ctx = PetContext.Instance;
            string b64 = ctx != null ? ctx.GetVisionBase64() : null;
            if (string.IsNullOrEmpty(b64))
            {
                Status(StandaloneMode
                    ? "独立工作区没有桌宠的截图缓存 —— 用左侧「附件」发图片给模型"
                    : "还没有可用的截图 —— 让桌宠跑一会儿（每 15 秒抓一张）再点");
                return;
            }
            pendingScreenB64 = b64;
            Status("已附上当前屏幕截图（" + (b64.Length / 1024) + "KB），发送时一起给模型看");
        }

        void PickAttachment()
        {
            string p = OpenFileDialog("选择附件", null);
            if (string.IsNullOrEmpty(p)) { Status("已取消"); return; }
            try
            {
                var fi = new FileInfo(p);
                if (fi.Length > 2 * 1024 * 1024) { Status("文件太大（>2MB）" + p); return; }
            }
            catch { }
            pendingAttachments.Add(p);
            Status("已添加附件：" + Path.GetFileName(p) + "（发送时一起带上）");
        }

        // ============================================================
        //  发/ 工具回环
        // ============================================================

        void Send()
        {
            if (busy) { Status("还在等上一轮…"); return; }
            var c = WorkspaceStore.Active;
            if (c == null) return;
            string text = input != null ? (input.text ?? "").Trim() : "";
            if (text.Length == 0 && pendingAttachments.Count == 0) return;

            // 气泡里【不放】附件正文，只放占位卡片（文件名/缩略图，点开预览）
            //   正文attachmentText，只发给模型
            var attachBody = new StringBuilder();
            var atts = new List<string>();
            for (int i = 0; i < pendingAttachments.Count; i++)
            {
                string p = pendingAttachments[i];
                atts.Add(p);
                string name = Path.GetFileName(p);
                if (IsImage(p))
                {
                    // 图片作为 image_url 单独发；这里只留一个名字给模型参
                    attachBody.Append("\n\n[图片：").Append(name).Append("]");
                }
                else
                {
                    string body = ReadTextSafe(p, 200000);
                    if (string.IsNullOrEmpty(body)) body = "(空文件)";
                    attachBody.Append("\n\n[附件：").Append(name).Append("]\n").Append(body);
                }
            }
            pendingAttachments.Clear();

            var um = new WorkspaceMessage
            {
                role = "user",
                content = text,
                attachmentText = attachBody.Length > 0 ? attachBody.ToString() : null,
                time = DateTime.Now.ToString("HH:mm"),
                attachments = atts.ToArray()
            };

            // 编辑模式：从这里往后整段截掉，再写入这条新内容
            if (editIndex >= 0 && editIndex < c.messages.Count)
                c.messages.RemoveRange(editIndex, c.messages.Count - editIndex);
            editIndex = -1;

            c.messages.Add(um);
            if (c.title == "新对话" && text.Length > 0)
                c.title = text.Length > 18 ? text.Substring(0, 18) + "…" : text;
            c.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (input != null) input.text = "";

            // 本地规则记忆（不额外花钱）：带「记我叫/我喜欢」就存一
            MaybeRemember(c, text);

            WorkspaceStore.Save();
            RefreshConversationList(); RefreshChat();

            askCo = StartCoroutine(RunAsk(c));
        }

        /// <summary>包一层：负责停止按钮的显隐藏和「这一轮属于哪个会话」的记账/summary>
        IEnumerator RunAsk(WorkspaceConversation c)
        {
            askConv = c;
            if (stopBtn != null) stopBtn.gameObject.SetActive(true);
            RefreshConversationList();      // 标签上出现 ⟳
            yield return AskRoutine(c);
            if (stopBtn != null) stopBtn.gameObject.SetActive(false);
            askCo = null; askConv = null;
            busy = false;
            RefreshConversationList();
        }

        /// <summary>
        /// 立刻掐断正在生成的那一轮：停协程、关流式气泡、关工作链、收掉还开着的确认条
        /// 删除会话 / 切换会话 / 点「■ 停止」都会走这里
        /// </summary>
        void CancelAsk(string why)
        {
            if (askCo == null && !busy) return;
            if (askCo != null) { StopCoroutine(askCo); askCo = null; }
            askConv = null;
            if (confirmOpen) ResolveConfirm(false);
            EndStreamBubble();
            EndLiveTrace();
            busy = false;
            if (stopBtn != null) stopBtn.gameObject.SetActive(false);
            RefreshConversationList();
            Status(why);
            Debug.Log("[Workspace] " + why);
        }

        static void MaybeRemember(WorkspaceConversation c, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] keys = { "记住", "我叫", "我的名字", "我喜欢", "我不喜欢", "以后都", "别叫我" };
            bool hit = false;
            for (int i = 0; i < keys.Length; i++) if (text.Contains(keys[i])) { hit = true; break; }
            if (!hit) return;
            string line = text.Trim();
            if (line.Length > 120) line = line.Substring(0, 120);
            if (!c.memory.Contains(line)) c.memory.Add(line);
            if (c.memory.Count > 40) c.memory.RemoveAt(0);
        }

        int ModeRounds(string mode)
        {
            int r = Mathf.Max(1, maxToolRounds);
            if (mode == ModeClaude) return Mathf.Max(r, 12);
            if (mode == ModePi) return Mathf.Max(r, 10);
            if (mode == ModeMath) return 1;    // 数学模式不挂工具，一轮说完
            return r;
        }

        /// <summary>
        /// 本地模型偶尔会把整段回答再写一遍（尤其被要给结时）
        /// 检测「开160 个字符在后文原样重复」⇒ 在第二次出现处截断，避免回答看起来有两份
        /// 而且第二份还因为 max_tokens 被截在中途
        /// </summary>
        /// <summary>去掉所有空白，用来做"同一段"的模糊比较（模型复读时空白/换行经常不一样）。</summary>
        static string NormBlock(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            var sb = new StringBuilder(t.Length);
            for (int i = 0; i < t.Length; i++)
                if (!char.IsWhiteSpace(t[i])) sb.Append(t[i]);
            return sb.ToString();
        }

        static string TrimDuplicateTail(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 600) return s;

            // ① 按空行分块，找「整块重复」——忽略空白差异。
            //    作者截图那种情况：先写了一遍方案，然后又从【结论】开始把同样的段落重写一遍，
            //    两次之间只差几个换行/空格，按字符精确比较抓不到。
            var parts = new List<string>();
            var starts = new List<int>();
            int pos = 0;
            while (pos <= s.Length)
            {
                int idx = s.IndexOf("\n\n", pos, StringComparison.Ordinal);
                if (idx < 0) { parts.Add(s.Substring(pos)); starts.Add(pos); break; }
                parts.Add(s.Substring(pos, idx - pos)); starts.Add(pos);
                pos = idx + 2;
            }
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < parts.Count; i++)
            {
                string key = NormBlock(parts[i]);
                if (key.Length < 30) continue;          // 太短的块（标题、空行）不参与
                int prev;
                if (seen.TryGetValue(key, out prev) && i - prev >= 2)
                {
                    int cut = starts[i];
                    if (cut > s.Length / 3)
                    {
                        Debug.Log("[Workspace] 回复去重（重复段落 @" + cut + "）：raw=" + s.Length);
                        return CutAt(s, cut);
                    }
                }
                else if (!seen.ContainsKey(key)) seen[key] = i;
            }

            // ② 精确匹配兜底：开头 160 字在后文原样出现
            const int headLen = 160;
            string head = s.Substring(0, headLen);
            int second = s.IndexOf(head, headLen, StringComparison.Ordinal);
            if (second > s.Length / 3)
            {
                Debug.Log("[Workspace] 回复去重（整段重写）：raw=" + s.Length + " cut=" + second);
                return CutAt(s, second);
            }

            // ③ 300 字内容块精确重复
            const int probeLen = 300;
            const int step = 150;
            int n = s.Length;
            for (int st = 0; st + probeLen * 2 <= n; st += step)
            {
                string probe = s.Substring(st, probeLen);
                int again = s.IndexOf(probe, st + probeLen, StringComparison.Ordinal);
                if (again > st + probeLen)
                {
                    Debug.Log("[Workspace] 回复去重（整段复读）：raw=" + n + " cut=" + again + " probe@" + st);
                    return CutAt(s, again);
                }
            }
            return s;
        }

        static string CutAt(string s, int at)
        {
            // 留一份原始回复，方便排查"到底是模型复读还是界面重复渲染"
            try
            {
                File.WriteAllText(Path.Combine(WorkspaceStore.DirPath, "last_dup_reply.txt"), s);
            }
            catch { }
            return s.Substring(0, at).TrimEnd() + "\n\n…（检测到重复输出，后半段已自动截断）";
        }

        /// <summary>工具结果是不没拿到东（空、报错 个结果）。用来发现原地打转/summary>
        static bool LooksLikeEmptyResult(string result)
        {
            if (string.IsNullOrEmpty(result) || result == "{}") return true;
            if (result.Contains("\"ok\":false")) return true;
            if (result.Contains("\"success\":false")) return true;
            if (result.Contains("\"totalCount\":0")) return true;
            if (result.Contains("\"instances\":[]")) return true;
            if (result.Contains("\"count\":0")) return true;
            if (result.Contains("文件不存在") || result.Contains("不存在")) return true;
            if (result.Contains("\"error\"")) return true;
            return false;
        }

        /// <summary>模型只在文字说要调用工具"、却没有真正发起 tool_calls 的典型句式/summary>
        static bool LooksLikeToolIntent(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 900) return false;
            bool named = text.Contains("unity_") || text.Contains("blender_")
                      || text.Contains("read_file") || text.Contains("write_file") || text.Contains("list_dir");
            // 不点名工具、但明显是在下一步去去读"的短句（只在300 字里判断，避免误伤长回答
            bool vague = text.Length <= 300 && (
                   text.Contains("看一下场景") || text.Contains("看看场景") || text.Contains("查看场景")
                || text.Contains("读取文件") || text.Contains("查看文件") || text.Contains("先看看")
                || text.Contains("先看一下") || text.Contains("让我看看") || text.Contains("我去看看"));
            if (!named && !vague) return false;
            string[] verbs = { "调用", "使用", "查看", "读取", "让我先", "我先", "我需要", "接下来", "先看", "看一下" };
            for (int i = 0; i < verbs.Length; i++) if (text.Contains(verbs[i])) return true;
            return false;
        }

        IEnumerator AskRoutine(WorkspaceConversation c)
        {
            busy = true;
            liveTraceBuf.Length = 0;
            EndLiveTrace();

            var route = WorkspaceStore.RouteOf(c);
            string mode = ModeOf(c);
            float askStart = Time.realtimeSinceStartup;
            Status("正在请求 " + route.name + " …（" + ModeLabel(mode) + "）");

            var msgs = new JArray();
            msgs.Add(Msg("system", BuildSystemPrompt(c, route)));

            // 上下文压缩：把很旧的对话折叠summary，用户界面仍然显示全部原始消
            CompactIfNeeded(c);

            int from = Mathf.Max(c.summarizedUpTo, c.messages.Count - 1 - historyMessages);
            for (int i = from; i < c.messages.Count; i++)
            {
                var m = c.messages[i];
                if (m == null) continue;
                // 只有【最后一条用户消息】带图，历史里的图不再重复上传（token
                bool withImages = (i == c.messages.Count - 1) && m.role == "user";
                msgs.Add(BuildMsgToken(m, withImages));
            }

            // 数学模式不挂任何工具：纯推理，工具 schema 只会挤占上下文、诱导它去查东西
            var tools = (mode == ModeMath) ? null : BuildToolsJson();

            // 上下文预算：本地模型只有 32k。工schema 很占地方（Unity 一个就有几十个参数），
            //   超了服务端直400。这里按字符数估一遍，超了就【先砍工具、再砍历史】
            TrimToBudget(msgs, tools);

            string finalText = null;
            int rounds = 0;
            int maxRounds = ModeRounds(mode);
            bool nudgedTool = false, nudgedEmpty = false;
            bool finalRoundAnnounced = false;
            int sameSig = 0, emptyStreak = 0, stuckCount = 0;
            int toolNameStreak = 0;
            string lastSig = "", lastToolName = "";

            while (rounds++ < maxRounds)
            {
                // 最后一轮：把工具全部摘掉，逼模型收尾
                //   本地模型很容再来一停不下来，光靠提示词管不—这是硬保险
                bool finalRound = rounds >= maxRounds;
                // 数学模式本来就没有工具 —— 不要塞「最后一轮、工具已关闭、基于工具结果回答」，
                // 模型会以为前面有工具结果，反而敷衍收尾。
                if (finalRound && !finalRoundAnnounced && tools != null && tools.Count > 0)
                {
                    finalRoundAnnounced = true;
                    AppendTrace("⏳ 到达最后一轮 → 关闭工具，强制模型给出最终答复");
                    msgs.Add(Msg("user", "[系统提醒] 这是最后一轮，工具已经关闭。请立刻基于上面的工具结果给出最终答复（中文，先说结论），不要再调用任何工具。"));
                }

                bool stream = !string.Equals(route.provider, "ollama", StringComparison.OrdinalIgnoreCase);
                string body = BuildBody(route, msgs, finalRound ? null : tools, stream, mode);

                JObject msg = null;
                string raw = null;
                bool streamFailed = false;
                string streamFailReason = null;

                if (stream)
                {
                    // 流式：边收边显示，首个字出现得早很多
                    var h = new SseHandler(OnStreamDelta, OnReasoningDelta);
                    BeginStreamBubble();
                    yield return PostStream(route, body, h);
                    if (h.Failed) { streamFailed = true; streamFailReason = h.FailReason; }
                    string text = h.Text.ToString();
                    JArray sCalls = h.BuildToolCalls();

                    // ⚠️ 有些本地服务（比ONW）在 stream=true 时【不返回结构tool_calls】，
                    //    而是Qwen 的文本格`<tool_call><function=...>` 塞进 content
                    //    这里再解析一次；解析不出来的话至少把 XML 从正文里剥掉
                    if (sCalls == null)
                    {
                        string clean;
                        sCalls = ParseTextToolCalls(text, out clean);
                        text = clean;
                    }
                    // 最后一轮即使模型硬写了文本工具调用也忽略：本轮只收正文
                    if (finalRound && sCalls != null)
                    {
                        AppendTrace("（最后一轮：忽略文本工具调用，只取正文）");
                        sCalls = null;
                    }

                    if (!string.IsNullOrEmpty(text) || sCalls != null)
                    {
                        msg = new JObject();
                        msg["role"] = "assistant";
                        msg["content"] = string.IsNullOrEmpty(text) ? null : text;
                        if (sCalls != null) msg["tool_calls"] = sCalls;
                    }
                    if (msg == null)
                    {
                        if (h.AnyData)
                        {
                            // 服务端确实回choices，只delta 全空 当成"空回交给下面的提醒逻辑
                            msg = new JObject();
                            msg["role"] = "assistant";
                            msg["content"] = "";
                        }
                        else if (!string.IsNullOrEmpty(h.RawHead))
                            Debug.LogWarning("[Workspace] 流式返回为空，原始片段：" + h.RawHead);
                    }
                    EndStreamBubble();
                }
                else
                {
                    yield return Post(route, body, r => raw = r);
                    if (string.IsNullOrEmpty(raw))
                    {
                        Status("请求失败（端点 " + EndpointOf(route) + "）");
                        AppendTrace("⚠ 请求失败：" + EndpointOf(route));
                        busy = false;
                        yield break;
                    }
                    JObject j = null;
                    try { j = JObject.Parse(raw); } catch { }
                    msg = j != null && j["choices"] != null && j["choices"].Count() > 0
                        ? j["choices"][0]["message"] as JObject : null;
                }

                if (msg == null)
                {
                    if (streamFailed)
                    {
                        // 超时/断连：不要再当"空回复"重试一次，那只会再等一个超时
                        string why = "请求超时或失败（" + (streamFailReason ?? "") + "，上限 "
                                   + Mathf.Max(300, timeoutSec) + " 秒）";
                        AppendTrace("⚠ " + why);
                        finalText = "（" + why + "。\n"
                                  + "本地模型在上下文很大时会很慢——附件很大就先拆小，或者换个模型路由再试。）";
                        break;
                    }
                    Status("返回格式不认识：" + Safe(raw ?? "", 160));
                    AppendTrace("⚠ 返回格式不认识：" + Safe(raw ?? "", 160));
                    busy = false;
                    yield break;
                }

                var calls = finalRound ? null : msg["tool_calls"] as JArray;
                if (calls != null && calls.Count > 0)
                {
                    // 模型在发起工具调用前先说一（比如「我先看一下场景」）也留在工作链里，
                    // 不然流式气泡一销毁，这段话就丢了 —CherryStudio 也是把过程留着的
                    string preText = TokStr(msg["content"]);
                    if (!string.IsNullOrEmpty(preText)) AppendTrace("💬 " + Safe(preText, 300));

                    AppendTrace("第 " + rounds + " 轮 · 模型发起 " + calls.Count + " 个工具调用");
                    msgs.Add(JObject.Parse("{\"role\":\"assistant\",\"content\":null,\"tool_calls\":" + calls.ToString(Formatting.None) + "}"));

                    for (int i = 0; i < calls.Count; i++)
                    {
                        string name = TokStr(calls[i]["function"]["name"]);
                        // ★ 必须用 TokStr：arguments 可能是 JSON 对象而不是字符串（实测会抛异常）
                        string args = TokStr(calls[i]["function"]["arguments"]) ?? "{}";
                        string callId = TokStr(calls[i]["id"]);
                        if (string.IsNullOrEmpty(callId)) callId = "call_" + rounds + "_" + i;
                        if (string.IsNullOrEmpty(args)) args = "{}";

                        AppendTrace("→ " + name + "(" + Safe(args, 120) + ")");
                        Status("执行工具 " + name + " …（" + (i + 1) + "/" + calls.Count + "）");

                        string result = null;
                        yield return RunTool(name, args, r => result = r);

                        // 原地打转检测：同样的调用重复、连续空结果、或同一个工具连续调用很多次
                        string sig = name + "|" + Safe(args, 200);
                        if (sig == lastSig) sameSig++; else { lastSig = sig; sameSig = 1; }
                        if (name == lastToolName) toolNameStreak++; else { lastToolName = name; toolNameStreak = 1; }
                        bool empty = LooksLikeEmptyResult(result);
                        if (empty) emptyStreak++; else emptyStreak = 0;

                        AppendTrace(empty ? "← ⚠ 结果为空/失败：" + Safe(result ?? "", 180)
                                          : "← " + Safe(result ?? "", 200));
                        msgs.Add(JObject.Parse("{\"role\":\"tool\",\"tool_call_id\":\"" + Esc(callId) + "\",\"content\":" + Quote(result ?? "") + "}"));
                    }

                    // 连续重复调用 / 连续空结模型已经卡住了，硬提醒一次；再卡就提前收尾
                    if (sameSig >= 2 || emptyStreak >= 3 || toolNameStreak >= 4)
                    {
                        stuckCount++;
                        string why = emptyStreak >= 3 ? "连续空结果"
                                   : (toolNameStreak >= 4 ? "同一个工具连续调用 " + toolNameStreak + " 次" : "重复相同调用");
                        AppendTrace("⚠ 检测到原地打转（" + why + "）→ 要求它停止重试、换策略或直接汇报");
                        msgs.Add(Msg("user",
                            "[系统提醒] 你正在重复同样的工具调用，或者连续多次只拿到空结果/错误。继续重复不会改变结果。" +
                            "立刻停止重试：要么换一个完全不同的方法，要么把「查了什么、拿到什么、结论是什么」直接讲清楚。"));
                        sameSig = 0; emptyStreak = 0; lastSig = ""; toolNameStreak = 0;

                        if (stuckCount >= 2 && rounds + 1 < maxRounds)
                        {
                            maxRounds = rounds + 1;   // 下一轮就是"关工具收尾"
                            AppendTrace("⏳ 已经卡住两轮 → 提前进入收尾轮");
                        }
                    }

                    AppendTrace("… 把工具结果回传给模型，等它继续");
                    Status("工具执行完成，等模型继续…");
                    continue;
                }

                finalText = TokStr(msg["content"]);

                // 模型这一轮什么都没输出：别直接给用户看一句干巴巴没有返回内容"
                //   先提醒它一次（不少本地模型只是这一轮抽样失败）；再空才算失败
                if (string.IsNullOrWhiteSpace(finalText))
                {
                    if (!nudgedEmpty)
                    {
                        nudgedEmpty = true;
                        AppendTrace("⚠ 模型这一轮没有输出 → 提醒它继续");
                        msgs.Add(Msg("user", "[系统提醒] 你上一轮没有任何输出。请直接给出最终答复；如果还需要工具，请现在真正发起工具调用。"));
                        continue;
                    }
                    AppendTrace("⚠ 模型连续两轮空输出");
                    finalText = "（模型这一轮没有输出。可以再发一次，或换个模型路由试试。）";
                    break;
                }

                // 模型我要去看场景/读文却只输出文字、没tool_calls
                //   提醒它一次，让它真的走工具通道。只提醒一次，避免死循环
                if (!nudgedTool && rounds < maxRounds && LooksLikeToolIntent(finalText))
                {
                    nudgedTool = true;
                    AppendTrace("⚠ 模型只在文字里说要调用工具 → 提醒它真正调用");
                    msgs.Add(Msg("assistant", finalText));
                    msgs.Add(Msg("user", "[系统提醒] 不要只在正文里描述要调用工具。请立刻通过工具调用通道发起对应的 tool_calls；拿到工具结果后再给最终答复。"));
                    finalText = null;
                    continue;
                }

                break;
            }

            if (string.IsNullOrEmpty(finalText))
                finalText = "（已经跑完 " + (rounds - 1) + " 轮工具，模型还是没有给出最终答复。\n"
                          + "上面「工作链」里能看到它每一步查了什么；如果它一直在重复同样的调用，说明这个方向走不通。\n"
                          + "可以：① 直接发「继续」让它再收尾一次；② 把问题拆小（比如先问「场景里有哪些物体」）；③ 换个模型路由。）";

            finalText = TrimDuplicateTail(finalText);

            c.messages.Add(new WorkspaceMessage
            {
                role = "assistant",
                content = finalText,
                time = DateTime.Now.ToString("HH:mm"),
                trace = liveTraceBuf.Length > 0 ? liveTraceBuf.ToString().TrimEnd('\n') : null
            });
            c.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            WorkspaceStore.Save();

            // ⚠️ 先清 busy / 关掉流式状态，RefreshChat —
            //    否则 RefreshChat 会把刚才实时工作流式气泡"再恢复一遍，出现两份
            busy = false;
            EndStreamBubble();
            EndLiveTrace();
            RefreshConversationList(); RefreshChat();
            Status("✔ 完成 · " + DateTime.Now.ToString("HH:mm:ss")
                   + " · 用时 " + (Time.realtimeSinceStartup - askStart).ToString("0.0") + "s"
                   + " · " + ModeLabel(mode));
        }

        /// <summary>
        /// 把「保留区之外」的旧消息折叠进 c.summary
        /// 纯本地抽取式压缩（不token），UI messages 一条不删
        /// </summary>
        void CompactIfNeeded(WorkspaceConversation c)
        {
            if (c == null) return;
            int keep = Mathf.Max(6, historyMessages);
            int from = Mathf.Clamp(c.summarizedUpTo, 0, c.messages.Count);
            int end = c.messages.Count - keep;
            if (end - from < 6) return;

            var sb = new StringBuilder(c.summary ?? "");
            for (int i = from; i < end; i++)
            {
                var m = c.messages[i];
                if (m == null) continue;
                string t = TextTools.StripToolSyntax(m.content);
                if (string.IsNullOrEmpty(t)) continue;
                if (t.Length > 60) t = t.Substring(0, 60) + "…";
                if (sb.Length > 0) sb.Append("。");
                sb.Append(m.role == "user" ? "用户：" : "小白：").Append(t);
            }
            string s = sb.ToString();
            if (s.Length > 1200) s = "…" + s.Substring(s.Length - 1200);
            c.summary = s;
            c.summarizedUpTo = end;
            WorkspaceStore.Save();
            Debug.Log("[Workspace] 上下文压缩：前 " + end + " 条折叠成 " + s.Length + " 字摘要");
        }

        /// <summary>
        /// 估算整包大小（字符数），超预算就先删最后的工具（保留前 3 个文件工具）、再删最老的历史消息
        /// 中文1 1 token，英JSON 3~4 字符 1 token，这里用 2.6 保守估计
        /// </summary>
        void TrimToBudget(JArray msgs, JArray tools)
        {
            const int promptBudgetChars = 50000;   // 本地 32k 模型的安全线（中文≈1 字 1 token）
            int Guard = 0;
            while (Guard++ < 200 && TotalChars(msgs, tools) > promptBudgetChars)
            {
                if (tools != null && tools.Count > 3) { tools.RemoveAt(tools.Count - 1); continue; }
                if (msgs != null && msgs.Count > 3) { msgs.RemoveAt(1); continue; }   // 0 是 system
                break;
            }
            if (tools != null && tools.Count < BuildToolsJson().Count)
                Debug.LogWarning("[Workspace] 上下文超预算，工具裁到 " + tools.Count + " 个；历史裁到 " + msgs.Count + " 条");
        }

        static int TotalChars(JArray msgs, JArray tools)
        {
            int n = 0;
            if (msgs != null) for (int i = 0; i < msgs.Count; i++) n += (msgs[i] != null ? msgs[i].ToString(Formatting.None).Length : 0);
            if (tools != null) n += tools.ToString(Formatting.None).Length;
            return n;
        }

        string BuildSystemPrompt(WorkspaceConversation c, WorkspaceRoute route)
        {
            var sb = new StringBuilder();
            string mode = ModeOf(c);
            bool mathMode = mode == ModeMath;

            sb.Append("你是「小白」，一只住在电脑桌面上的白虎兽人，现在开的是你的工作区模式。");
            sb.Append("性格活泼、直率、爱吐槽，但对正事很靠谱；称呼对方为「").Append(PetProfileConfig.UserName).Append("」。");
            if (!mathMode) sb.Append("你可以读写本地文件（有权限限制），也能调用 Blender / Unity 的 MCP 工具。");
            if (mathMode)
                sb.Append("回答用中文；数学题要完整、严谨，宁可写长也不许省略推导步骤；不要自称助手。\n");
            else
                sb.Append("回答用中文，先给结论再给步骤，简洁、直接、可执行；不要自称助手。\n");
            sb.Append("工作区里有多个独立会话；你当前只属于「").Append(c.title).Append("」这一个会话，");
            sb.Append("其他会话的历史与记忆你看不到，也不要假装知道。\n");
            if (c.memory.Count > 0)
            {
                sb.Append("\n【本会话记忆】\n");
                for (int i = 0; i < c.memory.Count; i++) sb.Append("- ").Append(c.memory[i]).Append("\n");
            }
            if (!string.IsNullOrEmpty(c.summary))
                sb.Append("\n【本会话摘要】").Append(c.summary).Append("\n");
            if (!mathMode)
            {
                sb.Append("\n文件工具：read_file / write_file / list_dir。");
                sb.Append("需要看文件先 read_file；要改文件用 write_file。");
                sb.Append("不要编造文件内容。\n");
                // ★ 路径规则必须写死：模型以前会发 read_file({}) 空参数（或者只写相对路径），
                //   然后被「路径不在允许范围内」挡住，接着就原地重试 —— 看起来就是"根本不会传路径"。
                string[] roots = Roots();
                sb.Append("【路径规则·必须遵守】调用 read_file / write_file / list_dir 时，参数必须是 JSON 对象且含 path 字段，");
                sb.Append("path 必须是从盘符开始的完整绝对路径（例如 D:\\myprogram\\MyPet\\Assets\\Workspace\\WorkspaceUI.cs）。");
                sb.Append("禁止传空对象 {}、禁止编造不存在的路径、禁止只传文件名。\n");
                sb.Append("允许访问的根目录：\n");
                for (int i = 0; i < roots.Length; i++)
                    if (!string.IsNullOrEmpty(roots[i])) sb.Append("  · ").Append(roots[i]).Append("\n");
                sb.Append("不确定文件在哪时，先用 list_dir 列出某个根目录，再逐级往下。\n");
            }

            // 对话模式（新建时定的，整个会话不变；mode 已在函数开头取过）
            sb.Append("\n【当前模式】").Append(ModeLabel(mode)).Append("\n");
            if (mode == ModeClaude)
            {
                sb.Append("你是一个 Agent（工作代理），不是问答机器人。必须按下面的循环工作：\n");
                sb.Append("① 先判断这一步需要什么信息；② 需要外部信息/操作就【立刻真正发起工具调用】；");
                sb.Append("③ 读到工具结果后先用一句话复述关键结论；④ 再决定下一步或收尾。\n");
                sb.Append("任何涉及真实文件 / Unity / Blender 状态的内容都不能凭记忆回答，必须有工具结果支撑。\n");
                sb.Append("不要因为「看起来差不多」就跳过工具。工具报错就把错误原文转述出来，不要掩盖。\n");
                sb.Append("【止损规则·必须遵守】同一个工具连续两次拿到空结果/报错，就不许再用相同或近似参数重试；");
                sb.Append("要么换参数格式、换工具，要么直接把「查了什么、结果是什么、结论是什么」讲清楚并收尾。\n");
                sb.Append("绝对不许用 0 / 1 / 0.5 这类占位坐标反复调用 look_at / move_to / set_position 之类会改变场景的工具；");
                sb.Append("参数必须来自真实数据（层级、查询结果或用户给的坐标）。\n");
                sb.Append("每轮工具结果回来后先判断「这些信息够回答了吗？」——够了就立刻收尾，不要为了多调工具而继续。\n");
                sb.Append("全部做完后用【结论】开头给出最终答复，并简要列出你实际做了什么。\n");
            }
            else if (mode == ModePi)
            {
                sb.Append("采用 Pi 模式：先计划（Plan），再逐步执行（Implement）。\n");
                sb.Append("第一段先用编号列出 3~7 步计划；然后从「第 1 步」开始逐步执行，");
                sb.Append("每步开头写「第 n 步：」，需要信息/操作就【真正发起工具调用】，拿到结果再进入下一步。\n");
                sb.Append("不要在计划里假装已经完成；也不要跳过步骤。全部结束后用【完成情况】逐条对照计划说明结果。\n");
            }
            else if (mode == ModeMath)
            {
                sb.Append("这是【数学模式】：你的任务是严谨地解题 / 证明，不是闲聊。不用调工具，直接推理。\n");
                sb.Append("宁可写得长，也不许省略关键步骤——完整性比简洁重要。\n");
            }
            else
            {
                sb.Append("直接回答，先给结论再给步骤。确实需要工具时再用，不要为了用工具而用工具。\n");
            }

            // ★★ 数学硬规则：所有模式都生效（普通对话里遇到数学题同样适用）。
            //    作者实测：模型只写「结论 / 步骤 / 详细证明」，里面全是复述题目 + 报定理名字，
            //    根因就是提示词只要求「简洁、先给结论」——它真的就只写了个提纲。
            sb.Append("\n【数学题·必须遵守】\n");
            sb.Append("1. 先把题目【完整转写】一遍：已知条件、要证的结论、点和线的对应关系（例如 A↔D、B↔E）一个都不能省。");
            sb.Append("题目是图片时更必须先逐字转写（看不清就写「看不清」，不要猜）。\n");
            sb.Append("2. 证明 / 计算必须写出【完整推导链】：每一步写清楚「用了什么定理或公式 + 为什么能在这里用」。");
            sb.Append("禁止只复述题目、只报定理名字、用「显然」「同理」「类似可证」跳过关键步骤。\n");
            sb.Append("3. 需要辅助线 / 辅助点时，先用文字定义它（例如「取 AE 与 BD 的交点为 M」），再使用。\n");
            sb.Append("4. 证明结束写 ∎；计算题要给出最终结果并验算。\n");
            sb.Append("5. 公式一律用 LaTeX：行内 $...$，独立成行 $$...$$。不要用 Unicode 近似符号代替公式。\n");
            sb.Append("6. 不确定的地方明确写「这一步我不确定」，绝对不许编造条件或点。\n");
            sb.Append("7. 题目指定了方法（例如「用中心投影法」）就必须用那个方法；不许换成极点极线 / 调和点列来回避真正的构造。\n");

            // ★★ 中心投影 / 德扎格的「标准套路 + 示范」。
            //    作者实测：模型会认真转写题目，但构造三角形时把「同一斜线上的两点」配成对应点
            //    （△ABD / △CEF，A↔C），完全无视 O 不在 l 上 —— 这不是缺知识，是缺检查步骤。
            sb.Append("\n【中心投影 / 德扎格·标准套路】\n");
            sb.Append("① 先找【过同一点】的对应点：题目给的「过 O 的三条直线与 l、l′ 的交点」就是现成的对应点对。\n");
            sb.Append("② 用这些对应点组两个三角形，逐对检查：每一对对应顶点的连线都必须过 O。");
            sb.Append("只要有一对不过 O，这个配对就是错的，必须换三角形。\n");
            sb.Append("③ 写出三对对应边，求它们的交点；由德扎格定理，这三个交点共线。\n");
            sb.Append("【示范·就是常考的那道题】\n");
            sb.Append("题目：l∩l′=Q，O∉l,l′；过 O 的三条线交 l 于 A,B,C、交 l′ 于 D,E,F；");
            sb.Append("M=AE∩BD，N=BF∩CE。求证 M,N,Q 共线。\n");
            sb.Append("正确构造：△AEC 与 △DBF。\n");
            sb.Append("对应顶点：A↔D（AD 过 O）、E↔B（EB 过 O）、C↔F（CF 过 O）——三对都过 O ✓\n");
            sb.Append("对应边：AE↔DB ⇒ M；EC↔BF ⇒ N；CA↔FD ⇒ l∩l′=Q。\n");
            sb.Append("结论：由德扎格定理，M,N,Q 三点共线。∎\n");
            sb.Append("⚠️ 常见错误：把 △ABD 与 △CEF 配对。A、C 都在 l 上，AC 就是 l，而 O∉l，");
            sb.Append("所以 A↔C 的连线不过 O，不能用德扎格。\n");

            sb.Append("⚠️ 绝对不要用正文里的 <tool_call> 文本冒充工具调用；要调用就必须走真正的 tool_calls 通道。\n");
            sb.Append("【不要重复】不要把已经输出过的内容再写一遍；收尾时只用 3~5 句做总结，");
            sb.Append("禁止重写全文、禁止输出两份相同的段落。\n");

            // 权限 / MCP 提示只对带工具的会话有意义；数学模式纯推理，不塞这些噪音
            if (!mathMode)
            {
            // 权限档位（影响它直接还是"先问"
            string perm = string.IsNullOrEmpty(c.permission) ? PermSmart : c.permission;
            if (perm == PermOpen)
            {
                sb.Append("【审批】用户已选择「完整放开权限」：工具会直接执行，不必在回答里反复征求同意。\n");
            }
            else if (perm == PermAsk)
            {
                sb.Append("【审批】用户选择「逐条审批」：每一次工具调用都会弹出确认框，被拒绝时不要重复尝试同一个调用，");
                sb.Append("而是告诉用户被拒绝了并询问怎么继续。\n");
            }
            else
            {
                sb.Append("【审批】「智能审批」：只读操作直接执行；涉及修改/写入/执行的操作会先请用户确认。");
                sb.Append("被拒绝时不要假装已经完成。\n");
            }
            if (useMcpTools)
            {
                sb.Append("【工具使用·必须遵守】凡是涉及 Unity / Blender 的操作 —— 读控制台、看场景、加物体/组件、");
                sb.Append("改材质、写脚本、构建、建模、导入模型 —— 都必须真的调用对应工具（名字以 unity_ 或 blender_ 开头），");
                sb.Append("**绝对禁止凭空描述、凭记忆编答案**。工具返回什么就转述什么；工具报错就把错误原文给出来。");
                sb.Append("不确定该用哪个工具时，先调用最贴近的那个试，而不是直接回答。");
            }
            else
            {
                sb.Append("\n【当前未启用 MCP 工具】涉及 Unity / Blender 的操作，请先让用户点标题栏的「MCP 关」按钮打开，");
                sb.Append("打开后你才能真正调用 unity_* / blender_* 工具。现在不要假装已经操作过。");
            }
            } // !mathMode
            return sb.ToString();
        }

        static JObject Msg(string role, string content)
        {
            var o = new JObject();
            o["role"] = role;
            o["content"] = content ?? "";
            return o;
        }

        static bool IsImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp" || ext == ".gif";
        }

        static string MimeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".gif": return "image/gif";
                default: return "image/jpeg";
            }
        }

        /// <summary>
        /// 解析「工具调用被写成文本」的情况
        ///   JSON 形式lt;tool_call&gt;{"name":"x","arguments":{...}}&lt;/tool_call&gt;
        ///   Qwen 文本形式lt;tool_call&gt;&lt;function=x&gt;&lt;parameter=k&gt;v&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;
        /// 返回 null 表示正文里没有工具调用；clean = 去掉这些块之后的正文
        /// </summary>
        static JArray ParseTextToolCalls(string text, out string clean)
        {
            clean = text ?? "";
            if (string.IsNullOrEmpty(text) || text.IndexOf("<tool_call>", StringComparison.Ordinal) < 0)
                return null;

            var arr = new JArray();
            var sb = new StringBuilder();
            int pos = 0;
            while (true)
            {
                int s = text.IndexOf("<tool_call>", pos, StringComparison.Ordinal);
                if (s < 0) { sb.Append(text.Substring(pos)); break; }
                sb.Append(text.Substring(pos, s - pos));

                int e = text.IndexOf("</tool_call>", s, StringComparison.Ordinal);
                if (e < 0) { sb.Append(text.Substring(s)); break; }
                string block = text.Substring(s + 11, e - s - 11).Trim();
                pos = e + 12;

                JObject call = null;

                // JSON
                try
                {
                    var jo = JObject.Parse(block);
                    if (jo["name"] != null)
                    {
                        var fn = new JObject();
                        fn["name"] = TokStr(jo["name"]);
                        var ag = jo["arguments"];
                        fn["arguments"] = ag == null ? "{}"
                            : (ag.Type == JTokenType.String ? (string)ag : ag.ToString(Formatting.None));
                        call = new JObject();
                        call["id"] = "call_" + arr.Count;
                        call["type"] = "function";
                        call["function"] = fn;
                    }
                }
                catch { }

                // Qwen 文本格式
                if (call == null)
                {
                    int fs = block.IndexOf("<function=", StringComparison.Ordinal);
                    if (fs >= 0)
                    {
                        int fe = block.IndexOf('>', fs);
                        string nm = fe > fs ? block.Substring(fs + 10, fe - fs - 10).Trim() : "";
                        var args = new JObject();
                        int p = fe > 0 ? fe : 0;
                        while (true)
                        {
                            int ps = block.IndexOf("<parameter=", p, StringComparison.Ordinal);
                            if (ps < 0) break;
                            int pe = block.IndexOf('>', ps);
                            if (pe < 0) break;
                            string key = block.Substring(ps + 11, pe - ps - 11).Trim();
                            int vs = pe + 1;
                            int ve = block.IndexOf("</parameter>", vs, StringComparison.Ordinal);
                            if (ve < 0) break;
                            args[key] = block.Substring(vs, ve - vs);
                            p = ve + 12;
                        }
                        if (!string.IsNullOrEmpty(nm))
                        {
                            var fn = new JObject();
                            fn["name"] = nm;
                            fn["arguments"] = args.ToString(Formatting.None);
                            call = new JObject();
                            call["id"] = "call_" + arr.Count;
                            call["type"] = "function";
                            call["function"] = fn;
                        }
                    }
                }

                if (call != null) arr.Add(call);
            }

            clean = sb.ToString().Trim();
            return arr.Count > 0 ? arr : null;
        }

        /// <summary>一条消OpenAI 消息对象。带图时 content 变成数组（多模态）/summary>
        JObject BuildMsgToken(WorkspaceMessage m, bool withImages)
        {
            var o = new JObject();
            o["role"] = m.role == "user" ? "user" : "assistant";

            // 附件正文：只随【最后一条用户消息】发一次（历史里发了会白烧 token）
            // ⚠️ 再截一刀：本地 18B 模型吃不下几十万字的附件，预填充会让首字等好几分钟（甚至超时）。
            //    超出部分不丢——告诉模型路径，它可以用 read_file 自己读。
            string body = m.content ?? "";
            if (withImages && !string.IsNullOrEmpty(m.attachmentText))
            {
                string att = m.attachmentText;
                if (att.Length > 12000)
                {
                    string paths = m.attachments != null ? string.Join("；", m.attachments) : "";
                    att = att.Substring(0, 12000)
                        + "\n\n…（附件内容过长，这里只带了前 12000 字。完整文件请用 read_file 读取：" + paths + "）";
                }
                body += att;
            }

            if (withImages)
            {
                var arr = new JArray();
                arr.Add(new JObject { ["type"] = "text", ["text"] = body });
                int imgs = 0;

                // 附件里的图片
                if (m.attachments != null)
                {
                    for (int i = 0; i < m.attachments.Length && imgs < 3; i++)
                    {
                        string p = m.attachments[i];
                        if (!IsImage(p) || !File.Exists(p)) continue;
                        try
                        {
                            var bytes = File.ReadAllBytes(p);
                            if (bytes.Length > 6 * 1024 * 1024) continue;
                            string uri = "data:" + MimeOf(p) + ";base64," + Convert.ToBase64String(bytes);
                            var part = new JObject();
                            part["type"] = "image_url";
                            part["image_url"] = new JObject { ["url"] = uri };
                            arr.Add(part);
                            imgs++;
                        }
                        catch (Exception e) { Debug.LogWarning("[Workspace] 读图片失败 " + p + "：" + e.Message); }
                    }
                }

                // 📷 当前屏幕截图（只随这一条发一次）
                if (!string.IsNullOrEmpty(pendingScreenB64))
                {
                    var shot = new JObject();
                    shot["type"] = "image_url";
                    shot["image_url"] = new JObject { ["url"] = "data:image/jpeg;base64," + pendingScreenB64 };
                    arr.Add(shot);
                    pendingScreenB64 = null;
                    imgs++;
                }

                if (imgs > 0) { o["content"] = arr; return o; }
            }

            o["content"] = body;
            return o;
        }

        string EndpointOf(WorkspaceRoute r)
        {
            string u = (r.baseUrl ?? "").Trim().TrimEnd('/');
            string cp = (r.chatPath ?? "").Trim();
            if (cp.Length > 0)
            {
                if (cp.StartsWith("http")) return cp;
                if (!cp.StartsWith("/")) cp = "/" + cp;
                return u + cp;
            }
            if (string.Equals(r.provider, "ollama", StringComparison.OrdinalIgnoreCase))
                return u.EndsWith("/api/chat") ? u : u + "/api/chat";
            if (u.EndsWith("/chat/completions")) return u;
            if (u.EndsWith("/v1")) return u + "/chat/completions";
            return u + "/v1/chat/completions";
        }

        string BuildBody(WorkspaceRoute r, JArray msgs, JArray tools, bool stream, string mode = null)
        {
            bool ollama = string.Equals(r.provider, "ollama", StringComparison.OrdinalIgnoreCase);
            var o = new JObject();
            o["model"] = r.model;
            o["stream"] = stream && !ollama;

            // Agent 模式：把温度压下来，工具选择和步骤执行会稳定很多（本地模型尤其明显）
            if (mode == ModeClaude || mode == ModePi)
                o["temperature"] = 0.25;
            // 数学：温度再低一点，减少「术语堆砌 / 编造条件」
            else if (mode == ModeMath)
                o["temperature"] = 0.15;

            // 单次回复上限：回答很长时容易被服务端默认值截断（用户截图里第二遍就断在半句）
            if (r.maxTokens > 0) o["max_tokens"] = r.maxTokens;
            if (ollama)
            {
                o["think"] = r.enableThinking;
                o["options"] = new JObject { ["num_ctx"] = 16384 };
            }
            else
            {
                if (!r.enableThinking && !string.IsNullOrEmpty(r.model) &&
                    r.model.ToLowerInvariant().Contains("qwen"))
                    o["enable_thinking"] = false;
            }
            if (tools != null && tools.Count > 0) o["tools"] = tools;
            o["messages"] = msgs;
            return o.ToString(Formatting.None);
        }

        JArray BuildToolsJson()
        {
            var arr = new JArray();
            arr.Add(Fn("read_file", "读取一个文本文件的内容",
                new JObject { ["path"] = Prop("string", "绝对路径") }, new[] { "path" }));
            arr.Add(Fn("write_file", "把文本写入文件（覆盖）。会先弹窗让用户确认",
                new JObject
                {
                    ["path"] = Prop("string", "绝对路径"),
                    ["content"] = Prop("string", "要写入的完整内容")
                }, new[] { "path", "content" }));
            arr.Add(Fn("list_dir", "列出目录下的文件和子目录",
                new JObject { ["path"] = Prop("string", "绝对路径") }, new[] { "path" }));

            // MCP（Blender / Unity …）：把它们的工具直接并进来
            //   工具多的时候提示词会变长，超过 maxMcpTools 就只带前面这些
            if (useMcpTools && mcp != null && mcp.Tools.Count > 0)
            {
                var mt = mcp.DescribeForModel();
                int n = Mathf.Min(mt.Count, Mathf.Max(0, maxMcpTools));
                for (int i = 0; i < n; i++) arr.Add(mt[i]);
            }
            return arr;
        }

        static JObject Prop(string type, string desc)
        {
            var p = new JObject(); p["type"] = type; p["description"] = desc; return p;
        }

        static JObject Fn(string name, string desc, JObject props, string[] required)
        {
            var req = new JArray(); foreach (var r in required) req.Add(r);
            var fn = new JObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = req
                }
            };
            var t = new JObject(); t["type"] = "function"; t["function"] = fn; return t;
        }

        // ---------- 工具执行 ----------

        /// <summary>
        /// 当前会话的权限档位决定这次工具调用要不要先问用户
        ///   open  = 完全放开
        ///   smart = 只读自动，涉及「改」的才问
        ///   ask   = 逐条审批（连 read_file / list_dir 也问
        /// </summary>
        bool NeedsConfirm(string name)
        {
            // 用【正在生成的那个会话】的权限，而不是界面上当前选中的会话：
            // 后台会话还在跑，用户切走了也不能突然换成另一档权限。
            var c = askConv != null ? askConv : WorkspaceStore.Active;
            string perm = c != null && !string.IsNullOrEmpty(c.permission) ? c.permission : PermSmart;
            if (perm == PermOpen) return false;
            if (perm == PermAsk) return true;

            if (name == "read_file" || name == "list_dir") return false;
            if (name == "write_file") return true;

            string n = (name ?? "").ToLowerInvariant();
            string[] mut =
            {
                "write", "create", "delete", "remove", "set_", "add_", "modify", "import", "build",
                "execute", "run_", "apply", "save", "rename", "move", "generate", "assign",
                "instantiate", "destroy", "open_scene", "new_", "insert", "duplicate", "export",
                "bake", "install", "update_", "edit", "replace", "play", "stop"
            };
            for (int i = 0; i < mut.Length; i++) if (n.Contains(mut[i])) return true;
            return false;   // get / list / read / find / info / ping / stats … 视为只读
        }

        /// <summary>
        /// 安全地把 JToken 变成字符串。
        /// ⚠️ (string)token 只能用在 JValue(string) 上：对 JObject / JArray 会抛
        ///    ArgumentException「Can not convert Object to String」。
        ///    本机实测：某些兼容服务端会把 tool_calls.function.arguments 直接发成【对象】而不是字符串 —
        ///    老代码一抛，整轮响应就废了，界面表现就是「发出去没有任何反应」。
        /// </summary>
        static string TokStr(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t.Type == JTokenType.Object || t.Type == JTokenType.Array) return t.ToString(Formatting.None);
            return t.ToString();
        }

        /// <summary>
        /// 从工具参数里取路径。模型不总是把键名写成 path（也有 file_path / filename / dir / directory），
        /// 而且经常只传 {} 空对象 —— 那种情况下必须给一条【能自我纠正】的报错，
        /// 而不是「路径不在允许范围内：」（末尾空着，模型看了也不知道该填什么）。
        /// </summary>
        static string PathArg(JObject a)
        {
            if (a == null) return null;
            string[] keys = { "path", "file_path", "filepath", "filename", "file", "dir", "directory", "folder", "target" };
            for (int i = 0; i < keys.Length; i++)
            {
                var tok = a[keys[i]];
                if (tok != null && tok.Type == JTokenType.String)
                {
                    string s = ((string)tok).Trim().Trim('"');
                    if (s.Length > 0) return s;
                }
            }
            // 参数整个就是个位置的（有些 MCP 直接传绝对路径字符串）
            return null;
        }

        string PathHelp()
        {
            var r = Roots();
            return "允许访问的根目录：\n  · " + string.Join("\n  · ", r)
                 + "\n请把完整的绝对路径放进参数 path 里再调用一次。";
        }

        IEnumerator RunTool(string name, string argsJson, Action<string> done)
        {
            JObject a = null;
            try { a = JObject.Parse(argsJson); } catch { }
            string path = PathArg(a);
            Debug.Log("[Workspace] 工具 " + name + " 参数=" + (argsJson ?? "") + " → path=" + (path ?? "(空)"));

            // 空路径：本地文件工具的参数里没有 path ⇒ 直接给出可纠正的报错（列出根目录），
            // 别让它一路走到 CheckRead 再报一句「路径不在允许范围内：」（冒号后面什么都没有）。
            bool localFs = (name == "read_file" || name == "list_dir" || name == "write_file");
            if (localFs && string.IsNullOrEmpty(path))
            {
                string msg = "这次调用没有带 path 参数（收到的是空的）。" + PathHelp();
                AppendTrace("⚠ 参数缺 path → 已返回允许目录清单");
                done("{\"ok\":false,\"error\":" + Quote(msg) + "}");
                yield break;
            }

            // 权限闸门：所有工具都从这里过（不只是写文件）
            if (NeedsConfirm(name))
            {
                bool allowed = false;
                string what = "允许调用工具「" + name + "」吗？" + (string.IsNullOrEmpty(path) ? "" : "　" + path);
                RequestConfirm(what, r => allowed = r);
                while (confirmOpen) yield return null;
                if (!allowed)
                {
                    AppendTrace("✖ 用户拒绝：" + name);
                    done("{\"ok\":false,\"error\":\"用户拒绝了这次工具调用\"}");
                    yield break;
                }
                AppendTrace("✔ 用户已允许：" + name);
            }

            // MCP（Blender / Unity …）交给对应的服
            // skipConfirm=true：上面的权限闸门已经按会话档位问过用户了，
            // 这里必须压掉 pet_tools.json 的 requireConfirm，否则「完整放开权限」照样弹框。
            if (mcp != null && mcp.Has(name))
            {
                string r = null;
                yield return mcp.Invoke(name, argsJson, x => r = x, true);
                done(r ?? "{}");
                yield break;
            }

            if (name == "list_dir")
            {
                done(ListDir(path));
                yield break;
            }
            if (name == "read_file")
            {
                string err;
                if (!CheckRead(path, out err)) { done("{\"ok\":false,\"error\":" + Quote(err) + "}"); yield break; }
                string txt = ReadTextSafe(path, 200000);
                done("{\"ok\":true,\"content\":" + Quote(txt) + "}");
                yield break;
            }
            if (name == "write_file")
            {
                string content = a != null ? (TokStr(a["content"]) ?? "") : "";
                string err;
                if (!CheckWrite(path, out err)) { done("{\"ok\":false,\"error\":" + Quote(err) + "}"); yield break; }
                // ⚠️ 写入确认已经上移RunTool 开头的权限闸门（NeedsConfirm write_file 恒为 true
                //    但「完全放开权限」档位下应当不再弹窗）。这里不能再问一遍，否则会连弹两次
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, content ?? "");
                    done("{\"ok\":true,\"bytes\":" + (content != null ? content.Length : 0) + "}");
                }
                catch (Exception e) { done("{\"ok\":false,\"error\":" + Quote(e.Message) + "}"); }
                yield break;
            }
            done("{\"ok\":false,\"error\":\"未知工具\"}");
        }

        string[] Roots()
        {
            if (allowedRoots != null && allowedRoots.Length > 0) return allowedRoots;
            return new string[]
            {
                PetPathUtil.ExeDir(),
                @"D:\myprogram",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        bool Inside(string path, out string root)
        {
            root = null;
            if (string.IsNullOrEmpty(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            var roots = Roots();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.IsNullOrEmpty(roots[i])) continue;
                string r;
                try { r = Path.GetFullPath(roots[i]).TrimEnd('\\', '/'); } catch { continue; }
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) { root = r; return true; }
            }
            return false;
        }

        bool CheckRead(string path, out string err)
        {
            err = null;
            string root;
            if (string.IsNullOrEmpty(path)) { err = "path 是空的。" + PathHelp(); return false; }
            if (!Inside(path, out root)) { err = "路径不在允许范围内：「" + path + "」。" + PathHelp(); return false; }
            if (!File.Exists(path)) { err = "文件不存在：「" + path + "」。先用 list_dir 确认文件名。"; return false; }
            return true;
        }

        bool CheckWrite(string path, out string err)
        {
            err = null;
            string root;
            if (string.IsNullOrEmpty(path)) { err = "path 是空的。" + PathHelp(); return false; }
            if (!Inside(path, out root)) { err = "路径不在允许范围内：「" + path + "」。" + PathHelp(); return false; }
            return true;
        }

        string ListDir(string path)
        {
            string err;
            string root;
            if (!Inside(path, out root)) return "{\"ok\":false,\"error\":" + Quote("路径不在允许范围内") + "}";
            try
            {
                var sb = new StringBuilder();
                var dirs = Directory.GetDirectories(path);
                var files = Directory.GetFiles(path);
                for (int i = 0; i < dirs.Length && i < 50; i++) sb.Append("[D] ").Append(Path.GetFileName(dirs[i])).Append("\n");
                for (int i = 0; i < files.Length && i < 50; i++)
                    sb.Append("[F] ").Append(Path.GetFileName(files[i])).Append("  ")
                      .Append(new FileInfo(files[i]).Length).Append("B\n");
                return "{\"ok\":true,\"content\":" + Quote(sb.ToString()) + "}";
            }
            catch (Exception e) { return "{\"ok\":false,\"error\":" + Quote(e.Message) + "}"; }
        }

        static string ReadTextSafe(string path, int max)
        {
            try
            {
                if (!File.Exists(path)) return "(文件不存在)";
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > max) Array.Resize(ref bytes, max);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) { return "(读取失败：" + e.Message + ")"; }
        }

        // ---------- HTTP ----------

        IEnumerator Post(WorkspaceRoute r, string json, Action<string> done)
        {
            string url = EndpointOf(r);
            using (var www = new UnityWebRequest(url, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(r.apiKey)) www.SetRequestHeader("Authorization", "Bearer " + r.apiKey);
                www.timeout = Mathf.Max(300, timeoutSec);
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[Workspace] " + www.responseCode + " " + www.error + " | " + Safe(www.downloadHandler != null ? www.downloadHandler.text : "", 200));
                    done(null);
                    yield break;
                }
                done(www.downloadHandler.text);
            }
        }

        // ---------- 流式（SSE----------

        GameObject streamBubble;
        Text streamText;
        readonly StringBuilder streamBuf = new StringBuilder();
        readonly StringBuilder reasonBuf = new StringBuilder();
        Text reasonText;

        bool streamActive;

        void BeginStreamBubble(string initial = null)
        {
            streamBuf.Length = 0;
            reasonBuf.Length = 0;
            streamDeltaCount = 0;
            streamActive = true;

            // 后台会话（用户已经切走）：只准备缓冲区，不往当前界面里画
            if (askConv != null && WorkspaceStore.Active != askConv) return;

            streamBubble = MakeRect("Streaming", chatContent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).gameObject;
            var img = streamBubble.AddComponent<Image>(); img.color = ColAi;
            var vlg = streamBubble.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(14, 14, 8, 8);
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.spacing = 6;
            var fit = streamBubble.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 思考过程（模型支持 reasoning_content 时才有内容）
            reasonText = MakeText(streamBubble.transform, "", 12, new Color(0.55f, 0.62f, 0.72f), TextAnchor.UpperLeft);
            reasonText.horizontalOverflow = HorizontalWrapMode.Wrap;
            reasonText.verticalOverflow = VerticalWrapMode.Overflow;

            // 一开始就显示「▍」光标：用户能明确看它已经在生成
            streamText = MakeText(streamBubble.transform, "小白：▍", 16, ColText, TextAnchor.UpperLeft);
            streamText.horizontalOverflow = HorizontalWrapMode.Wrap;
            streamText.verticalOverflow = VerticalWrapMode.Overflow;

            // 切回正在输出的会话时，把已经收到的内容接上（不要从头开始）
            if (!string.IsNullOrEmpty(initial))
            {
                streamBuf.Append(initial);
                streamText.text = "小白：" + MarkdownText.ToRich(initial) + "<color=#8a93a3>▍</color>";
            }

            bubbles.Add(streamBubble);
            StartCoroutine(ScrollToBottom());
        }

        void OnReasoningDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            reasonBuf.Append(delta);
            if (askConv != null && WorkspaceStore.Active != askConv) return;
            if (reasonText != null)
            {
                string s = reasonBuf.ToString();
                reasonText.text = "（思考）" + (s.Length > 1200 ? "…" + s.Substring(s.Length - 1200) : s);
            }
        }

        int streamDeltaCount;

        void OnStreamDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            streamBuf.Append(delta);

            // 用户已经切到别的会话：继续记账，但别往别人的界面里
            if (askConv != null && WorkspaceStore.Active != askConv) return;

            if (streamText != null)
            {
                // 流式也走 Markdown：边吐边渲染，别等收尾才从纯文本"跳"成排版
                streamText.text = "小白：" + MarkdownText.ToRich(streamBuf.ToString())
                                  + "<color=#8a93a3>▍</color>";
                // 不是每个 token 都起协程（会瞬间堆一堆）；每 8 token 滚一次就够跟
                if ((++streamDeltaCount & 7) == 0) StartCoroutine(ScrollToBottom());
            }
        }

        void EndStreamBubble()
        {
            if (streamBubble != null) Destroy(streamBubble);
            streamBubble = null; streamText = null; reasonText = null; streamActive = false;
        }

        IEnumerator PostStream(WorkspaceRoute r, string json, SseHandler handler)
        {
            string url = EndpointOf(r);
            using (var www = new UnityWebRequest(url, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                www.downloadHandler = handler;
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Accept", "text/event-stream");
                if (!string.IsNullOrEmpty(r.apiKey)) www.SetRequestHeader("Authorization", "Bearer " + r.apiKey);
                www.timeout = Mathf.Max(300, timeoutSec);
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    handler.Failed = true;
                    handler.FailReason = "HTTP " + www.responseCode + " " + www.error;
                    Debug.LogWarning("[Workspace] 流式失败 " + handler.FailReason);
                }
            }
        }

        /// <summary>解析 OpenAI SSE：data: {...} 一行一条，[DONE] 结束/summary>
        class SseHandler : DownloadHandlerScript
        {
            public readonly StringBuilder Text = new StringBuilder();
            public readonly StringBuilder Reasoning = new StringBuilder();
            readonly Dictionary<int, StringBuilder> args = new Dictionary<int, StringBuilder>();
            readonly Dictionary<int, string> names = new Dictionary<int, string>();
            readonly Dictionary<int, string> ids = new Dictionary<int, string>();
            readonly Action<string> onDelta;
            readonly Action<string> onReasoning;
            string pending = "";
            /// <summary>异常/空响应时留一段原文，便于排错。</summary>
            public string RawHead = "";
            /// <summary>服务端确实返回过 choices（哪怕 delta 是空的）。用来区分"空回复"和"根本没连上"。</summary>
            public bool AnyData;
            /// <summary>请求失败/超时（UnityWebRequest 没成功）。</summary>
            public bool Failed;
            public string FailReason = "";

            public SseHandler(Action<string> onDelta) : this(onDelta, null) { }
            public SseHandler(Action<string> onDelta, Action<string> onReasoning) : base(new byte[4096])
            {
                this.onDelta = onDelta; this.onReasoning = onReasoning;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return false;
                pending += Encoding.UTF8.GetString(data, 0, dataLength);
                int idx;
                while ((idx = pending.IndexOf('\n')) >= 0)
                {
                    string line = pending.Substring(0, idx).TrimEnd('\r');
                    pending = pending.Substring(idx + 1);
                    Handle(line);
                }
                return true;
            }

            void Handle(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                if (!line.StartsWith("data:")) return;
                string payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") return;

                JObject j;
                try { j = JObject.Parse(payload); } catch { return; }

                var choices = j["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    if (RawHead.Length < 400) RawHead += payload + " ";
                    return;
                }
                AnyData = true;
                var d = choices[0]["delta"] as JObject;
                if (d == null) return;

                // 思考过程：ONW / vLLM / Ollama 有的reasoning_content，有的用 reasoning
                string rc = TokStr(d["reasoning_content"]);
                if (string.IsNullOrEmpty(rc)) rc = TokStr(d["reasoning"]);
                if (!string.IsNullOrEmpty(rc))
                {
                    Reasoning.Append(rc);
                    if (onReasoning != null) onReasoning(rc);
                }

                string c = TokStr(d["content"]);
                if (!string.IsNullOrEmpty(c))
                {
                    // ⚠️ 有些本地服务（ONW 等）最后会再发一整段「完整文本」的 delta
                    //    或者把已经发过的内容重发一—直接 Append 就会看到回答重复两遍
                    bool dup = false;
                    if (c.Length >= 32)
                    {
                        string cur = Text.ToString();
                        if (c == cur || (c.Length >= 64 && cur.EndsWith(c, StringComparison.Ordinal)))
                            dup = true;
                    }
                    if (!dup)
                    {
                        Text.Append(c);
                        if (onDelta != null) onDelta(c);
                    }
                }

                var tc = d["tool_calls"] as JArray;
                if (tc != null)
                {
                    for (int i = 0; i < tc.Count; i++)
                    {
                        var t = tc[i] as JObject;
                        if (t == null) continue;
                        int index = t["index"] != null ? (int)t["index"] : 0;
                        string idv = TokStr(t["id"]);
                        if (!string.IsNullOrEmpty(idv)) ids[index] = idv;
                        var fn = t["function"] as JObject;
                        if (fn == null) continue;
                        string nm = TokStr(fn["name"]);
                        if (!string.IsNullOrEmpty(nm)) names[index] = nm;
                        // ★ arguments 可能是对象（不是字符串片段）—— 老代码在这里抛异常，
                        //   整个 SSE 回调中断 ⇒ 界面上「发出去没有任何反应」
                        string a = TokStr(fn["arguments"]);
                        if (a != null)
                        {
                            StringBuilder sb;
                            if (!args.TryGetValue(index, out sb)) { sb = new StringBuilder(); args[index] = sb; }
                            sb.Append(a);
                        }
                    }
                }
            }

            public JArray BuildToolCalls()
            {
                if (args.Count == 0 && names.Count == 0) return null;

                // args / names / ids 的并集：有的服务端不arguments 字段（或发空串）
                // 只遍args 会把这类工具调用整条丢掉 模型"说要调工具却没调"
                var keys = new SortedSet<int>();
                foreach (var k in args.Keys) keys.Add(k);
                foreach (var k in names.Keys) keys.Add(k);
                foreach (var k in ids.Keys) keys.Add(k);

                var arr = new JArray();
                foreach (int i in keys)
                {
                    string name = names.ContainsKey(i) ? names[i] : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    StringBuilder sbArgs;
                    string argsStr = args.TryGetValue(i, out sbArgs) ? sbArgs.ToString() : "{}";
                    if (string.IsNullOrEmpty(argsStr)) argsStr = "{}";
                    var fn = new JObject();
                    fn["name"] = name;
                    fn["arguments"] = argsStr;
                    var call = new JObject();
                    call["id"] = ids.ContainsKey(i) ? ids[i] : ("call_" + i);
                    call["type"] = "function";
                    call["function"] = fn;
                    arr.Add(call);
                }
                return arr.Count > 0 ? arr : null;
            }
        }

        // ---------- 确认----------

        void RequestConfirm(string text, Action<bool> cb)
        {
            pendingConfirm = cb;
            confirmOpen = true;
            if (confirmText != null) confirmText.text = text;
            if (confirmBar != null) confirmBar.gameObject.SetActive(true);
        }

        void ResolveConfirm(bool ok)
        {
            confirmOpen = false;
            if (confirmBar != null) confirmBar.gameObject.SetActive(false);
            var cb = pendingConfirm; pendingConfirm = null;
            if (cb != null) cb(ok);
        }

        // ============================================================
        //  小工
        // ============================================================

        static string Safe(string s, int n) { if (string.IsNullOrEmpty(s)) return ""; return s.Length <= n ? s : s.Substring(0, n); }
        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }
        static string Quote(string s) { return "\"" + Esc(s) + "\""; }

        RectTransform MakeRect(string name, Transform parent, Vector2 amin, Vector2 amax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = amin; rt.anchorMax = amax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return rt;
        }

        Text MakeText(Transform parent, string s, int size, Color col, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = col; t.text = s;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        Button MakeButton(Transform parent, string label, float x, float y, float w, float h, Color bg, Action onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            if (w > 0f || h > 0f) rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>(); img.color = bg;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = img;
            btn.onClick.AddListener(() => { if (onClick != null) onClick(); });
            var t = MakeText(rt, label, 15, ColText, TextAnchor.MiddleCenter);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(8, 2); t.rectTransform.offsetMax = new Vector2(-8, -2);
            // 按钮文字允许溢出一点，别把「删除当前对话」这种截成半个字
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return btn;
        }

        /// <summary>
        /// 代码生成一个完整的 uGUI Dropdown（Caption + Template/Viewport/Content/Item）
        /// ⚠️ 路径必须Template/Viewport/Content/Item —Dropdown 内部按这个名字找克隆源
        /// </summary>
        Dropdown MakeDropdown(Transform parent, string[] options, int value, Action<int> onChange)
        {
            var go = new GameObject("Dropdown", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            var bg = go.AddComponent<Image>(); bg.color = ColItem;
            var dd = go.AddComponent<Dropdown>();
            dd.targetGraphic = bg;

            var cap = MakeText(rt, "", 13, ColText, TextAnchor.MiddleLeft);
            cap.rectTransform.anchorMin = Vector2.zero; cap.rectTransform.anchorMax = Vector2.one;
            cap.rectTransform.offsetMin = new Vector2(9, 0); cap.rectTransform.offsetMax = new Vector2(-22, 0);
            cap.horizontalOverflow = HorizontalWrapMode.Overflow;

            var arrow = MakeText(rt, "▼", 10, ColDim, TextAnchor.MiddleCenter);
            arrow.rectTransform.anchorMin = new Vector2(1, 0); arrow.rectTransform.anchorMax = new Vector2(1, 1);
            arrow.rectTransform.pivot = new Vector2(1, 0.5f);
            arrow.rectTransform.sizeDelta = new Vector2(20, 0);
            arrow.rectTransform.anchoredPosition = new Vector2(-2, 0);

            var tpl = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var tplRt = tpl.GetComponent<RectTransform>();
            tplRt.SetParent(rt, false);
            tplRt.anchorMin = new Vector2(0, 0); tplRt.anchorMax = new Vector2(1, 0);
            tplRt.pivot = new Vector2(0.5f, 1);
            tplRt.anchoredPosition = new Vector2(0, 2);
            tplRt.sizeDelta = new Vector2(0, 160);
            tpl.GetComponent<Image>().color = new Color(0.13f, 0.15f, 0.19f, 1f);
            var tplScroll = tpl.GetComponent<ScrollRect>();
            tplScroll.horizontal = false; tplScroll.vertical = true;
            tplScroll.movementType = ScrollRect.MovementType.Clamped;
            tplScroll.scrollSensitivity = 20;

            var viewport = MakeRect("Viewport", tplRt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var vpImg = viewport.gameObject.AddComponent<Image>(); vpImg.color = new Color(0, 0, 0, 0.01f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = MakeRect("Content", viewport, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = new Vector2(0, 28);

            var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            var itemRt = item.GetComponent<RectTransform>();
            itemRt.SetParent(content, false);
            itemRt.anchorMin = new Vector2(0, 1); itemRt.anchorMax = new Vector2(1, 1);
            itemRt.pivot = new Vector2(0.5f, 1);
            itemRt.sizeDelta = new Vector2(0, 28);
            var itemImg = item.AddComponent<Image>(); itemImg.color = new Color(0.18f, 0.20f, 0.25f, 1f);
            var toggle = item.GetComponent<Toggle>();
            toggle.targetGraphic = itemImg;
            toggle.toggleTransition = UnityEngine.UI.Toggle.ToggleTransition.None;

            var check = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            var chRt = check.GetComponent<RectTransform>();
            chRt.SetParent(itemRt, false);
            chRt.anchorMin = new Vector2(0, 0); chRt.anchorMax = new Vector2(0, 1);
            chRt.pivot = new Vector2(0, 0.5f);
            chRt.sizeDelta = new Vector2(3, -8);
            chRt.anchoredPosition = new Vector2(2, 0);
            var chImg = check.GetComponent<Image>(); chImg.color = ColAccent;
            toggle.graphic = chImg;

            var itemLabel = MakeText(itemRt, "Option", 13, ColText, TextAnchor.MiddleLeft);
            itemLabel.rectTransform.anchorMin = Vector2.zero; itemLabel.rectTransform.anchorMax = Vector2.one;
            itemLabel.rectTransform.offsetMin = new Vector2(12, 0); itemLabel.rectTransform.offsetMax = new Vector2(-8, 0);
            itemLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

            tpl.SetActive(false);
            tplScroll.viewport = viewport; tplScroll.content = content;

            dd.template = tplRt;
            dd.captionText = cap;
            dd.itemText = itemLabel;
            dd.options.Clear();
            for (int i = 0; i < options.Length; i++) dd.options.Add(new Dropdown.OptionData(options[i]));
            dd.value = Mathf.Clamp(value, 0, Mathf.Max(0, options.Length - 1));
            dd.RefreshShownValue();
            dd.onValueChanged.AddListener(i => { if (onChange != null) onChange(i); });
            return dd;
        }

        // ============================================================
        //  新建对话：选模+ 权限（模式选定后不可改
        // ============================================================

        void CloseNewConvDialog()
        {
            if (newConvPanel != null) { Destroy(newConvPanel.gameObject); newConvPanel = null; }
            if (newConvMask != null) { Destroy(newConvMask.gameObject); newConvMask = null; }
        }

        void ShowNewConversationDialog()
        {
            CloseNewConvDialog();

            // 半透明遮罩（点空白处关闭）
            var mask = MakeRect("NewConvMask", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var maskImg = mask.gameObject.AddComponent<Image>();
            maskImg.color = new Color(0f, 0f, 0f, 0.55f);
            var maskBtn = mask.gameObject.AddComponent<Button>();
            maskBtn.targetGraphic = maskImg;
            maskBtn.onClick.AddListener(CloseNewConvDialog);
            newConvMask = mask;

            var p = MakeRect("NewConv", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            p.sizeDelta = new Vector2(560, 350);
            p.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.11f, 0.14f, 0.995f);
            newConvPanel = p;

            var title = MakeText(p, "新建对话", 20, ColText, TextAnchor.MiddleLeft);
            title.rectTransform.anchorMin = new Vector2(0, 1); title.rectTransform.anchorMax = new Vector2(1, 1);
            title.rectTransform.pivot = new Vector2(0.5f, 1);
            title.rectTransform.anchoredPosition = new Vector2(0, -16);
            title.rectTransform.sizeDelta = new Vector2(-40, 32);

            var hint = MakeText(p, "模式在对话开始后不能再改；权限可以随时在标题栏切换。", 12, ColDim, TextAnchor.MiddleLeft);
            hint.rectTransform.anchorMin = new Vector2(0, 1); hint.rectTransform.anchorMax = new Vector2(1, 1);
            hint.rectTransform.pivot = new Vector2(0.5f, 1);
            hint.rectTransform.anchoredPosition = new Vector2(0, -50);
            hint.rectTransform.sizeDelta = new Vector2(-40, 20);

            var l1 = MakeText(p, "对话模式", 13, ColDim, TextAnchor.MiddleLeft);
            l1.rectTransform.anchorMin = new Vector2(0, 1); l1.rectTransform.anchorMax = new Vector2(0, 1);
            l1.rectTransform.pivot = new Vector2(0, 1);
            l1.rectTransform.anchoredPosition = new Vector2(20, -82);
            l1.rectTransform.sizeDelta = new Vector2(120, 22);

            var modeDd = MakeDropdown(p, ModeLabels, 0, null);
            var mrt2 = modeDd.GetComponent<RectTransform>();
            mrt2.anchorMin = new Vector2(0, 1); mrt2.anchorMax = new Vector2(1, 1);
            mrt2.pivot = new Vector2(0.5f, 1);
            mrt2.anchoredPosition = new Vector2(0, -108);
            mrt2.sizeDelta = new Vector2(-40, 34);

            var modeDesc = MakeText(p, "普通对话 = 直接回答；Claude Agent = 工具优先的完整工作循环；Pi = 先计划再逐步执行；"
                                     + "数学模式 = 低温度、完整推导、不挂工具（做题 / 证明用）。",
                12, ColDim, TextAnchor.UpperLeft);
            modeDesc.rectTransform.anchorMin = new Vector2(0, 1); modeDesc.rectTransform.anchorMax = new Vector2(1, 1);
            modeDesc.rectTransform.pivot = new Vector2(0.5f, 1);
            modeDesc.rectTransform.anchoredPosition = new Vector2(0, -148);
            modeDesc.rectTransform.sizeDelta = new Vector2(-40, 52);
            modeDesc.horizontalOverflow = HorizontalWrapMode.Wrap;
            modeDesc.verticalOverflow = VerticalWrapMode.Overflow;

            var l2 = MakeText(p, "工具权限", 13, ColDim, TextAnchor.MiddleLeft);
            l2.rectTransform.anchorMin = new Vector2(0, 1); l2.rectTransform.anchorMax = new Vector2(0, 1);
            l2.rectTransform.pivot = new Vector2(0, 1);
            l2.rectTransform.anchoredPosition = new Vector2(20, -210);
            l2.rectTransform.sizeDelta = new Vector2(120, 22);

            var permDd = MakeDropdown(p, PermLabels, 1, null);
            var prt2 = permDd.GetComponent<RectTransform>();
            prt2.anchorMin = new Vector2(0, 1); prt2.anchorMax = new Vector2(1, 1);
            prt2.pivot = new Vector2(0.5f, 1);
            prt2.anchoredPosition = new Vector2(0, -236);
            prt2.sizeDelta = new Vector2(-40, 34);

            var cancel = MakeButton(p, "取消", 0, 0, 0, 0, ColItem, CloseNewConvDialog);
            var crt = cancel.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(0, 0);
            crt.pivot = new Vector2(0, 0);
            crt.anchoredPosition = new Vector2(20, 20); crt.sizeDelta = new Vector2(110, 38);

            var create = MakeButton(p, "创建", 0, 0, 0, 0, ColAccent, () =>
            {
                string[] modeValues = { ModeChat, ModeClaude, ModePi, ModeMath };
                string mode = modeValues[Mathf.Clamp(modeDd.value, 0, modeValues.Length - 1)];
                string perm = PermValues[Mathf.Clamp(permDd.value, 0, PermValues.Length - 1)];
                int permIdx = permDd.value;
                // ⚠️ 新建对话【不】停止上一轮（作者要求：只有删除会话或按「停止」才终止
                editIndex = -1;
                WorkspaceStore.NewConversation(mode, perm);
                CloseNewConvDialog();
                RefreshConversationList(); RefreshChat();
                Status("已新建对话 · " + ModeLabel(mode) + " · " + PermLabels[Mathf.Clamp(permIdx, 0, PermLabels.Length - 1)]);
            });
            var nrt3 = create.GetComponent<RectTransform>();
            nrt3.anchorMin = new Vector2(1, 0); nrt3.anchorMax = new Vector2(1, 0);
            nrt3.pivot = new Vector2(1, 0);
            nrt3.anchoredPosition = new Vector2(-20, 20); nrt3.sizeDelta = new Vector2(130, 38);
        }

        // ============================================================
        //  Win32 文件选择
        // ============================================================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileName(ref OpenFileName ofn);

        public static string OpenFileDialog(string title, string filter)
        {
            try
            {
                var ofn = new OpenFileName();
                ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
                ofn.lpstrFilter = string.IsNullOrEmpty(filter)
                    ? "所有文件\0*.*\0文本文件\0*.txt;*.md;*.json;*.cs;*.py\0"
                    : filter;
                ofn.lpstrFile = new string(new char[1024]);
                ofn.nMaxFile = 1024;
                ofn.lpstrTitle = title;
                ofn.Flags = 0x00080000 | 0x00001000 | 0x00000008; // 文件必须存在 / 路径必须存在 / 不改变目录
                if (!GetOpenFileName(ref ofn)) return null;
                return ofn.lpstrFile;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Workspace] 打开文件选择框失败：" + e.Message);
                return null;
            }
        }
    }

    /// <summary>拖标题栏移动整个面板（像普通窗口一样）/summary>
    public class DragPanel : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public RectTransform target;
        public Canvas canvas;

        public void OnPointerDown(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            if (target == null) return;
            float s = canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
            target.anchoredPosition += e.delta / s;
            Clamp();
        }

        void Clamp()
        {
            var parent = target.parent as RectTransform;
            if (parent == null) return;
            // 允许拖到屏幕边缘甚至露出一部分 —只保证还120×80 在屏幕里抓得回来
            // （夹得太死的话，光标一继续走就跑到面板外面，拖动手感会“卡住”）
            float maxX = Mathf.Max(0f, (parent.rect.width + target.rect.width) / 2f - 120f);
            float maxY = Mathf.Max(0f, (parent.rect.height + target.rect.height) / 2f - 80f);
            var p = target.anchoredPosition;
            p.x = Mathf.Clamp(p.x, -maxX, maxX);
            p.y = Mathf.Clamp(p.y, -maxY, maxY);
            target.anchoredPosition = p;
        }
    }
}

