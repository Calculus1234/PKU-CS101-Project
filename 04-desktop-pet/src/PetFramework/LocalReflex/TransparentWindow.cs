using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;

namespace PetFramework
{
    #endif

    /// <summary>
    /// 全屏透明覆盖层（捣蛋鹅式桌宠）。
    /// 关键修复：不再用 GetActiveWindow（双击启动时会拿错/拿空 → 黑屏），改用 WindowUtil 按进程枚举窗口；
    /// 并在启动前若干帧反复重试应用，确保窗口就绪后一定透明。
    /// 鼠标穿透：每帧命中检测，不在小白身上 → WS_EX_TRANSPARENT 整窗穿透。
    /// </summary>
    public class TransparentWindow : MonoBehaviour
    {
        public static bool mouseOverPet;

        [Header("窗口行为")]
        public bool borderless = true;
        public bool topMost = true;

        [Tooltip("排查用：工作区打开时每秒打印一行穿透判定")]
        public bool wsDiag = false;

        /// <summary>把窗口拉到前台并激活 —— 工作区打开后必须这样，否则点击不会进到 Unity。</summary>
        public static void FocusWindow()
        {
            var tw = UnityEngine.Object.FindFirstObjectByType<TransparentWindow>();
            if (tw == null) return;
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                if (tw.hwnd == IntPtr.Zero) return;
                SetForegroundWindow(tw.hwnd);
                SetActiveWindow(tw.hwnd);
            }
            catch { }
    #endif
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr h);

        /// <summary>工作区打开时把窗口从“始终置顶”降下来，让别的程序能盖住它。</summary>
        public static void SetTopMost(bool on)
        {
            var tw = UnityEngine.Object.FindFirstObjectByType<TransparentWindow>();
            if (tw == null) return;
            tw.topMost = on;
            tw.ReapplyTopMost();
        }

        void ReapplyTopMost()
        {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (topMost)
                {
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
                else
                {
                    // ⚠️ 必须用 HWND_NOTOPMOST 显式“退出置顶层”，
                    //    只传 HWND_TOP 不会清掉 WS_EX_TOPMOST 位 —— 窗口依旧盖在所有程序上面。
                    SetExStyleBits(0, WS_EX_TOPMOST);
                    SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
            }
            catch { }
    #endif
        }
        public bool coverFullScreen = true;
        public int applyRetryFrames = 10;   // 前几帧重复应用，等窗口就绪

        [Tooltip("编辑器里跳过窗口改造。⚠️ 必须保持勾选！")]
        public bool skipInEditor = true;

        [Tooltip("挂 WM_NCHITTEST 钩子（第二道保险）。" +
                 "⚠️ 实测分层窗口上 HTTRANSPARENT 并不能让点击穿到桌面，真正起作用的" +
                 "是下面逐帧切 WS_EX_TRANSPARENT；而这个原生回调一旦抛异常会直接把进程带走，" +
                 "所以打包版默认关掉。")]
        public bool useHitTestHook = false;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int GWL_WNDPROC = -4;

        // ---- WM_NCHITTEST：按坐标决定"这块区域要不要接鼠标" ----
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        // ---- 锁死窗口位置/尺寸 ----
        // ⚠️⚠️ 真凶（2026-11-18 从 Player.log 实锤）：
        //    拖动小白时整个窗口也在跟着鼠标跑。日志里每拖一下就出现
        //    「窗口位置漂了 [179,336,3059,2136]」这种 2880x1800 的窗口被平移的记录。
        //    窗口是 DwmExtendFrameIntoClientArea(-1) 的「整窗玻璃边框」窗口，
        //    系统会把客户区当成标题栏（HTCAPTION）⇒ 左键一按一拖 = 拖整个窗口。
        //    窗口一漂，2 秒一次的 → (0,0) 校正就把画面猛拽回去 ⇒ 用户看到「闪现回原位」。
        //    ✅ 两道根治：① WM_NCHITTEST 永不返回 HTCAPTION；② WM_WINDOWPOSCHANGING 里
        //       把任何移动/缩放请求强行改回 (0,0,屏幕宽,屏幕高)，窗口从物理上不可移动。
        const uint WM_WINDOWPOSCHANGING = 0x0046;
        const uint WM_SYSCOMMAND = 0x0112;
        const uint SC_MOVE = 0xF010;

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        static bool s_lockPos;          // ApplyTransparency 成功后打开
        static int s_coverW, s_coverH;  // 锁定的目标尺寸
        static int s_posBlockCount;

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rc);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ⚠️ 必须静态保活：委托被 GC 掉而窗口还在回调它 ⇒ 直接崩
        static WndProcDelegate s_wndProc;
        static IntPtr s_oldWndProc;
        static IntPtr s_hookedHwnd;

        /// <summary>
        /// 挂 WndProc。
        /// 为什么不继续用 WS_EX_TRANSPARENT：那个开关是【整窗级】的，
        /// 面板一开要么整个桌面点不了，要么面板点不到。
        /// WM_NCHITTEST 可以按坐标返回 HTCLIENT / HTTRANSPARENT，才是正确解法。
        /// </summary>
        void InstallWndProc()
        {
            if (hwnd == IntPtr.Zero || s_hookedHwnd == hwnd) return;

            s_wndProc = WndProcHook;
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(s_wndProc);
            IntPtr old = IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, GWL_WNDPROC, ptr)
                : SetWindowLong32(hwnd, GWL_WNDPROC, ptr);

            if (old != IntPtr.Zero)
            {
                s_oldWndProc = old;
                s_hookedHwnd = hwnd;
                Debug.Log("[TransparentWindow] WndProc 已挂钩（WM_NCHITTEST 按坐标穿透）");
            }
            else Debug.LogWarning("[TransparentWindow] WndProc 挂钩失败");
        }

        static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // ⚠️ 原生回调里抛异常会直接把进程带走 —— 整段包 try，任何意外都退回 DefWindowProc。
            try
            {
                // ① 窗口不可移动/不可缩放：谁来改都按回 (0,0,screenW,screenH)
                if (msg == WM_WINDOWPOSCHANGING && s_lockPos && lParam != IntPtr.Zero)
                {
                    WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                    bool badX = wp.x != 0 || wp.y != 0;
                    bool badW = (wp.cx != 0 && wp.cx != s_coverW) || (wp.cy != 0 && wp.cy != s_coverH);
                    if (badX || badW)
                    {
                        int rx = wp.x, ry = wp.y, rw = wp.cx, rh = wp.cy;
                        wp.x = 0; wp.y = 0;
                        if (s_coverW > 0) wp.cx = s_coverW;
                        if (s_coverH > 0) wp.cy = s_coverH;
                        wp.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);   // 清掉“忽略位置/尺寸”，否则改了也白改
                        Marshal.StructureToPtr(wp, lParam, false);
                        if (s_posBlockCount < 5)
                        {
                            s_posBlockCount++;
                            Debug.Log("[TransparentWindow] 拦下窗口移动请求 [" + rx + "," + ry + "," + rw + "," + rh
                                      + "]（已按回 0,0," + s_coverW + "," + s_coverH + "）");
                        }
                    }
                }

                // ② 系统菜单里的“移动”直接吞掉（Alt+F7 / 系统菜单）
                if (msg == WM_SYSCOMMAND && ((uint)wParam.ToInt64() & 0xFFF0) == SC_MOVE)
                    return IntPtr.Zero;

                // ③ 命中测试永远不当标题栏。
                //    整窗穿透由逐帧切 WS_EX_TRANSPARENT 负责（那才是可靠通道）；
                //    这里只保证“可交互”的那些帧不会被系统理解成按住标题栏拖窗口。
                if (msg == WM_NCHITTEST)
                    return (IntPtr)HTCLIENT;
            }
            catch { }

            if (s_oldWndProc == IntPtr.Zero) return IntPtr.Zero;
            return CallWindowProc(s_oldWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 【DPI 坑】WM_NCHITTEST 给的是**物理屏幕像素**，而 Unity 的 Screen.width/height
        /// 可能是**逻辑像素**（高 DPI 缩放下两者差 1.5~2 倍）。
        /// 直接写 Screen.height - sy 会让命中区整体偏掉（实测 200% 缩放下命中区只有面板的一半大小，
        /// 点面板中间反而被判成穿透）。
        /// 正确做法：先把点转成【客户区坐标】，再按 客户区尺寸→Screen 尺寸 的比例缩放。
        /// </summary>
        /// <summary>光标当前是否压在主面板上（逐帧用于切换整窗穿透）。</summary>
        /// <summary>
        /// 把系统光标（物理像素）换算成 Unity 屏幕坐标。
        /// ⚠️ 这台机器 200% 缩放：窗口客户区是 1440×900（Screen.width/height），
        ///    但 GetCursorPos 返回物理 2880×1800 —— 不做换算的话，
        ///    「光标在不在面板上」永远算错 ⇒ 工作区全透明可点（点不动）。
        /// </summary>
        public static Vector2 CursorToUnity()
        {
            POINT p;
            if (!GetCursorPos(out p)) return Vector2.zero;
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            float rx = sw > 0 ? (float)Screen.width / sw : 1f;
            float ry = sh > 0 ? (float)Screen.height / sh : 1f;
            return new Vector2(p.x * rx, Screen.height - p.y * ry);
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        bool CursorOverDashboard(PetWindowManager mgr)
        {
            return mgr.IsPointInsideDashboard(CursorToUnity());
        }

        /// <summary>光标是否在工作区面板上（F8 那个独立面板）。</summary>
        bool CursorOverWorkspace(PetFramework.Workspace.WorkspaceUI ws)
        {
            return ws.ContainsScreenPoint(CursorToUnity());
        }

        /// <summary>主面板当前是否开着（给其它 WndProc 钩子用）。</summary>
        public static bool DashboardOpen
        {
            get { var m = PetWindowManager.Instance; return m != null && m.IsDashboardOpen; }
        }

        /// <summary>
        /// 这个屏幕点是否落在主面板上。
        /// ⚠️ 多个 WndProc 钩子都会处理 WM_NCHITTEST（本类 / PetDragDrop），
        ///    谁在最外层谁先答，所以必须做成静态公共入口，大家共用同一套判定。
        /// </summary>
        public static bool DashboardHit(IntPtr hWnd, int screenX, int screenY)
        {
            // 工作区面板（F8）和主面板是两套独立 UI，任意一个盖住这个点都算“可点”
            var ws = PetFramework.Workspace.WorkspaceUI.Instance;
            if (ws != null && ws.IsOpen && ws.ContainsScreenPoint(new Vector2(screenX, Screen.height - screenY)))
                return true;

            var mgr = PetWindowManager.Instance;
            if (mgr == null || !mgr.IsDashboardOpen) return false;

            float nx, ny;
            if (ToNormalizedClient(hWnd, screenX, screenY, out nx, out ny))
                return mgr.IsNormalizedPointInsideDashboard(nx, ny);

            return mgr.IsPointInsideDashboard(new Vector2(screenX, Screen.height - screenY));
        }

        /// <summary>
        /// 屏幕坐标 → 归一化客户区坐标（0..1，左上原点）。
        /// 不依赖任何 DPI 假设：分子分母都是同一个窗口的客户区尺寸。
        /// </summary>
        static bool ToNormalizedClient(IntPtr hWnd, int screenX, int screenY, out float nx, out float ny)
        {
            nx = 0f; ny = 0f;
            RECT rc;
            if (hWnd == IntPtr.Zero || !GetClientRect(hWnd, out rc) || rc.right <= 0 || rc.bottom <= 0) return false;

            POINT pt; pt.x = screenX; pt.y = screenY;
            if (!ScreenToClient(hWnd, ref pt)) return false;

            nx = (float)pt.x / rc.right;
            ny = 1f - (float)pt.y / rc.bottom;
            return true;
        }

        static Vector2 ToUnityScreen(IntPtr hWnd, int screenX, int screenY)
        {
            RECT rc;
            if (hWnd != IntPtr.Zero && GetClientRect(hWnd, out rc) && rc.right > 0 && rc.bottom > 0)
            {
                POINT pt; pt.x = screenX; pt.y = screenY;
                if (ScreenToClient(hWnd, ref pt))
                {
                    float ux = pt.x * (float)Screen.width / rc.right;
                    float uy = Screen.height - pt.y * (float)Screen.height / rc.bottom;
                    return new Vector2(ux, uy);
                }
            }
            // 兜底：老路径
            return new Vector2(screenX, Screen.height - screenY);
        }
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_VISIBLE = 0x10000000;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_ACCEPTFILES = 0x00000010;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        float nextTopmostCheck;
        float nextWsDiag;
        static IntPtr lastForeground;
        bool loggedTopmostRestore;

        IntPtr hwnd;
        int applied;
        bool appliedThrough, appliedInited;

        [Header("拖放判定")]
        public float petPadPixels = 30f;   // 光标离小白包围盒这么近就算“在身上”

        void SetLong(int index, int value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, new IntPtr(value));
            else SetWindowLong32(hwnd, index, new IntPtr(value));
        }

        int GetLong(int index)
        {
            return IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hwnd, index).ToInt64() : GetWindowLong32(hwnd, index);
        }

        /// <summary>
        /// 【关键】只增删指定的扩展样式位，其余原样保留。
        /// 原来用 SetWindowLong(GWL_EXSTYLE, 整值) 会把 Unity 自己的扩展样式一起冲掉，
        /// 而且每次都要重新登记 ACCEPTFILES —— 拖动期间样式逐帧翻转，反复登记会把
        /// 外壳程序的拖放状态搅乱，表现为「WM_DROPFILES 收得到但文件数是 0」。
        /// </summary>
        void SetExStyleBits(int set, int clear)
        {
            int cur = GetLong(GWL_EXSTYLE);
            int want = (cur | set) & ~clear;
            if (want != cur) SetLong(GWL_EXSTYLE, want);
        }

        float nextCoverCheck;

        /// <summary>窗口必须严格盖住屏幕 (0,0,w,h)。位置一漂，宠物的"屏幕边界"就全错了。</summary>
        void EnsureCoverFullScreen()
        {
            if (hwnd == IntPtr.Zero) return;

            var pet = FindPet();
            var brain = pet != null ? pet.GetComponent<PetBrain>() : null;
            // ⚠️ 拖动过程中绝不碰窗口：一旦窗口动了，Input.mousePosition 与世界的映射会瞬间错位，
            //    表现就是「拖一半闪一下」。窗口现在已经由 WM_WINDOWPOSCHANGING 锁死，正常不会漂。
            if (brain != null && brain.IsDragging) return;

            int w = Screen.currentResolution.width;
            int h = Screen.currentResolution.height;
            s_coverW = w; s_coverH = h;

            RECT r;
            if (!GetWindowRect(hwnd, out r)) return;
            if (r.left == 0 && r.top == 0 && (r.right - r.left) == w && (r.bottom - r.top) == h) return;
            Debug.Log("[TransparentWindow] 窗口位置漂了 [" + r.left + "," + r.top + "," + r.right + "," + r.bottom
                      + "] → 校正为 (0,0," + w + "," + h + ")");
            SetWindowPos(hwnd, topMost ? HWND_TOPMOST : IntPtr.Zero, 0, 0, w, h,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }

        void ApplyTransparency()
        {
            if (hwnd == IntPtr.Zero) hwnd = WindowUtil.FindMainWindow();
            if (hwnd == IntPtr.Zero) return;

            var m = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            int hr = DwmExtendFrameIntoClientArea(hwnd, ref m);

            if (borderless) SetLong(GWL_STYLE, WS_POPUP | WS_VISIBLE);
            SetExStyleBits(
                WS_EX_LAYERED | WS_EX_ACCEPTFILES | (topMost ? WS_EX_TOPMOST : 0) | (appliedThrough ? WS_EX_TRANSPARENT : 0),
                appliedThrough ? 0 : WS_EX_TRANSPARENT);

            if (coverFullScreen)
            {
                s_coverW = Screen.currentResolution.width;
                s_coverH = Screen.currentResolution.height;
                // SWP_NOACTIVATE：校正窗口时绝不能把焦点/激活状态改掉（拖动中途被激活最容易丢鼠标事件）
                SetWindowPos(hwnd, topMost ? HWND_TOPMOST : IntPtr.Zero, 0, 0, s_coverW, s_coverH,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE);
                s_lockPos = true;
            }
            else if (topMost)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            // ⚠️ 这个钩子现在【必须挂】——它负责阻止窗口被系统当成标题栏拖走。
            //    以前由 useHitTestHook 控制（默认关），结果拖动小白 = 拖动整个窗口。
            InstallWndProc();

            if (applied == 0)
                Debug.Log("[TransparentWindow] 已应用 hwnd=" + hwnd + " dwmHr=" + hr + " 分辨率=" + Screen.currentResolution.width + "x" + Screen.currentResolution.height);
            applied++;
        }

        void Start()
        {
    #if UNITY_EDITOR
            // ⚠️⚠️ 编辑器里【绝对不能】跑：WindowUtil.FindMainWindow() 在编辑器里拿到的是
            //      Unity 编辑器主窗口（实测 hwnd=3475008，就是 Process.MainWindowHandle），
            //      下面会把它改成 WS_POPUP 全屏、加 LAYERED、还开鼠标穿透 ——
            //      等于把编辑器窗口拆了。打包成 EXE 后才需要这段。
            if (skipInEditor)
            {
                Debug.Log("[TransparentWindow] 编辑器里跳过窗口改造（打包后才真正透明）");
                enabled = false;
                return;
            }
    #endif
            ApplyTransparency();
        }

        void Update()
        {
            if (applied < applyRetryFrames) { ApplyTransparency(); return; }
            if (hwnd == IntPtr.Zero) return;

            // ★ 定期把窗口钉回 (0,0,屏幕分辨率)。
            //   实测踩过：窗口被 Unity/系统恢复成 (1488,1274) 这种偏移位置，
            //   窗口本身 2880x1800 大部分在屏幕外 ⇒ 小白看起来能"走出屏幕"（其实是窗口不在原点了），
            //   鼠标坐标也跟着错位。每 2 秒校正一次，成本极低。
            if (coverFullScreen && Time.unscaledTime >= nextCoverCheck)
            {
                nextCoverCheck = Time.unscaledTime + 2f;
                EnsureCoverFullScreen();
            }

            bool near = CursorNearPet(petPadPixels);
            // ⚠️ 这里【故意不看“左键是否按住”】。以前一旦按住左键就把整块全屏窗口变成可命中，
            // 结果是拖动期间整个桌面的鼠标输入都被我们抢走（别的程序拖不动东西），
            // 而且窗口样式在拖动中反复翻转会让外壳的拖放定位不稳定。
            // 现在只按【光标是否在小白的包围盒附近】来定：小白身上可交互，其余一律穿透放行给桌面。
            // mouseOverPet 也被 FurTuner 用做“面板打开→强制可交互”
            // ⚠️ 工具确认弹窗必须能点：不把 IsAsking 算进来，玩家会被穿透挡在按钮外面，
            //    120 秒后自动拒绝 —— 那这个「安全闸门」就变成静默失效了。
            bool modal = PetFramework.Tools.ToolConfirmUI.IsAsking;

            // ⚠️⚠️ 主面板开着时，**整窗 WS_EX_TRANSPARENT 必须关掉**：
            //    带这个样式的窗口，系统在做 WindowFromPoint / 命中测试时会**整个跳过**，
            //    我们的 WM_NCHITTEST 根本没机会返回 HTCLIENT —— 面板看得见但点不动。
            //    关掉之后靠 WM_NCHITTEST 逐点判定：面板内 HTCLIENT、其余 HTTRANSPARENT，
            //    桌面照样点得到。
            //    也不能只信 mouseOverPet：PetBrain.OnMouseExit 会在光标离开小白时把它清成 false，
            //    所以这里以 PetWindowManager.IsDashboardOpen 为准。
            // ⚠️⚠️ 分层窗口（WS_EX_LAYERED）上，WM_NCHITTEST 返回 HTTRANSPARENT **并不能**
            //    保证点击真的穿到桌面 —— 实测点下去还是被我们的窗口吃掉。
            //    所以这里用最可靠的经典做法：**按光标位置逐帧切换整窗 WS_EX_TRANSPARENT**。
            //    光标在面板上 ⇒ 关穿透（面板可点）；其余任何地方 ⇒ 开穿透（桌面可点）。
            //    WM_NCHITTEST 钩子继续保留，作为面板内部再细分的第二道保险。
            var mgr = PetWindowManager.Instance;
            var ws = PetFramework.Workspace.WorkspaceUI.Instance;
            bool wsOpen = ws != null && ws.IsOpen;
            bool uiOpen = (mgr != null && mgr.IsDashboardOpen) || wsOpen;
            bool overPanel = (mgr != null && mgr.IsDashboardOpen && CursorOverDashboard(mgr))
                          || (wsOpen && CursorOverWorkspace(ws));

            // ⚠️ 拖动窗口时，光标很容易被“夹”到面板外（面板卡在边界上、光标继续走），
            //    这时如果按坐标判成“面板外 ⇒ 穿透”，鼠标抬起事件就送给桌面了，
            //    EventSystem 会一直以为还按着 —— 表现就是「只能拖动一次，之后按钮全点不动」。
            //    所以：只要鼠标键还按着，或者光标正压在任何 uGUI 控件上，就一律不穿透。
            bool holdingMouse = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            // ⚠️ 不能用 EventSystem.IsPointerOverGameObject()：场景里有一块【全屏透明】的
            //    DragMoveCanvas/WindowMoveHandle（用来拖整个窗口的），它让这个函数永远返回 true，
            //    结果整窗永久不穿透 —— 桌面上点菜单栏/别的程序全都点不动。
            //    只认我们自己的面板矩形 + “鼠标正按着”这两个条件。
            if (uiOpen && holdingMouse) overPanel = true;
            bool overUI = false;

            // 诊断用：工作区开着时每秒打一行，排查“整窗点不动”（默认关，需要时在 Inspector 勾上）
            if (wsDiag && wsOpen && Time.unscaledTime >= nextWsDiag)
            {
                nextWsDiag = Time.unscaledTime + 1f;
                Debug.Log("[TW] wsOpen overPanel=" + overPanel + " overUI=" + overUI + " holding=" + holdingMouse
                          + " near=" + near + " screen=" + Screen.width + "x" + Screen.height
                          + " mouse=" + Input.mousePosition.ToString("F0")
                          + " mouseInPanel=" + (ws != null && ws.ContainsScreenPoint(Input.mousePosition)));
            }
            // 面板开着时【只看光标】：在面板上或小白身上 ⇒ 不穿透；其余 ⇒ 穿透给桌面。
            // 注意不能再把 mouseOverPet 算进来 —— 面板打开时它是 true，会把整窗永久锁成可点。
            bool wantThrough = uiOpen
                ? !(overPanel || near)
                : (!near && !mouseOverPet && !modal);
            if (!appliedInited || wantThrough != appliedThrough)
            {
                appliedThrough = wantThrough; appliedInited = true;
                ApplyClickThrough(wantThrough);
                Debug.Log("[TransparentWindow] 穿透=" + (wantThrough ? "开" : "关")
                    + "  (光标靠近桌宠=" + near + " Unity悬停=" + mouseOverPet
                    + " 光标在面板=" + overPanel
                    + " 左键按住=" + ((GetAsyncKeyState(0x01) & 0x8000) != 0) + ")");
            }

            // ⚠️⚠️ 【只在"真的掉了"的时候才重新置顶】—— 绝对不要每秒无脑 SetWindowPos。
            //
            //    两种"在别人上面"要区分开：
            //      · 我们被【普通窗口】压住（微信/浏览器）：这是 bug（TOPMOST 位被抹掉了）⇒ 要修
            //      · 我们头上的本来就是【另一个 TOPMOST 窗口】（截图工具/录屏工具/游戏覆盖层）
            //        ⇒ 那是人家的正当位置，**不能抢**，否则用户一截图小白就跳上去挡镜头
            //
            //    判据：读自己的 WS_EX_TOPMOST 位。位还在 ⇒ 我们仍在最顶层梯队，什么也不做；
            //    位掉了 ⇒ 才补样式位 + 重新置顶。平时完全不碰 z 序。
            if (topMost)
            {
                // 前台窗口一换（用户点微信/浏览器/Alt+Tab）就检查一次，不用等定时器
                IntPtr fg = GetForegroundWindow();
                if (fg != lastForeground)
                {
                    lastForeground = fg;
                    EnsureTopMost();
                }

                if (Time.unscaledTime >= nextTopmostCheck)
                {
                    nextTopmostCheck = Time.unscaledTime + 1f;
                    EnsureTopMost();
                }
            }
        }

        /// <summary>
        /// 只有在 WS_EX_TOPMOST 位真的丢了的时候才重新置顶。
        /// 位还在 ⇒ 直接返回（不抢别的 TOPMOST 窗口的位置，截图/录屏时不会突然跳上来）。
        /// </summary>
        void EnsureTopMost()
        {
            if (hwnd == IntPtr.Zero) return;

            int ex = GetLong(GWL_EXSTYLE);
            if ((ex & WS_EX_TOPMOST) != 0) return;      // 还在最顶层梯队，别动

            SetExStyleBits(WS_EX_TOPMOST, 0);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            if (!loggedTopmostRestore)
            {
                loggedTopmostRestore = true;
                Debug.Log("[TransparentWindow] 检测到 TOPMOST 位掉了（被普通窗口压住），已恢复置顶");
            }
        }

        /// <summary>
        /// 桌宠物体的名字。留空 = 自动找场景里的 PetBrain（推荐，换模型/改名都不用动这里）。
        /// </summary>
        public static string petObjectName = "";

        static GameObject cachedPet;

        /// <summary>
        /// 找到桌宠本体。
        /// ⚠️ 原来这里写死 `GameObject.Find("WhiteTiger_DesktopPet")` ——
        ///    换成任何别的模型（比如青龙）之后这里永远返回 null，
        ///    于是"光标靠近宠物"永远是 false，整个窗口彻底穿透，点击/拖动全失效。
        /// </summary>
        static GameObject FindPet()
        {
            if (cachedPet != null) return cachedPet;
            if (!string.IsNullOrEmpty(petObjectName))
            {
                cachedPet = GameObject.Find(petObjectName);
                if (cachedPet != null) return cachedPet;
            }
            // ⚠️ 必须写全限定名：文件顶部有 `using System;`，裸写 Object 会和 object 撞名
            var brain = UnityEngine.Object.FindObjectOfType<PetBrain>();
            if (brain != null) { cachedPet = brain.gameObject; return cachedPet; }
            cachedPet = GameObject.Find("WhiteTiger_DesktopPet");   // 旧工程兜底
            return cachedPet;
        }

        /// <summary>
        /// 光标是否落在桌宠附近 —— 纯几何计算（GetCursorPos + 世界包围盒投影到屏幕），
        /// 【不依赖 Unity 的 OnMouseEnter】：系统拖放（OLE）进行中 Unity 收不到鼠标事件，
        /// 用 OnMouseEnter 判断会永远为 false，窗口就被判成“穿透”，拖放自然落不到小白身上。
        /// 做成 static 是为了给 PetDragDrop 的 WM_NCHITTEST 复用。
        /// </summary>
        public static bool CursorNearPet(float pad)
        {
            var c = Camera.main;
            if (c == null) return false;
            var pet = FindPet();
            if (pet == null) return false;
            // ⚠️ 不能只取「第一个 Renderer」：运行时生成的特效/水球平时缩放是 0，
            //    正好排在最前面，包围盒退化成一个点，命中区就没了。
            //    这里取【所有有效渲染器】的并集。
            Bounds b = new Bounds();
            bool has = false;
            var renderers = pet.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var rr = renderers[i];
                if (rr == null || !rr.enabled || !rr.gameObject.activeInHierarchy) continue;
                if (rr.bounds.size.sqrMagnitude < 0.0001f) continue;
                if (!has) { b = rr.bounds; has = true; }
                else b.Encapsulate(rr.bounds);
            }
            if (!has) return false;

            Vector2 cur = CursorToUnity();

            Vector3 cc = b.center, e = b.extents;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = cc + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 sp = c.WorldToScreenPoint(corner);
                if (sp.z < 0f) return false;
                min = Vector2.Min(min, sp); max = Vector2.Max(max, sp);
            }
            return cur.x >= min.x - pad && cur.x <= max.x + pad && cur.y >= min.y - pad && cur.y <= max.y + pad;
        }

        void ApplyClickThrough(bool through)
        {
            if (hwnd == IntPtr.Zero) return;
            // 只改 TRANSPARENT 这一位；LAYERED / ACCEPTFILES / TOPMOST 每次都【显式补回】——
            // 只靠"读当前样式再改一位"的话，一旦读到的值不完整（窗口重建 / 别的程序改过样式），
            // TOPMOST 就会被顺手抹掉，小白就钻到别的窗口后面去了。
            int keep = WS_EX_LAYERED | WS_EX_ACCEPTFILES | (topMost ? WS_EX_TOPMOST : 0);
            if (through) SetExStyleBits(keep | WS_EX_TRANSPARENT, 0);
            else SetExStyleBits(keep, WS_EX_TRANSPARENT);
        }
    #endif
    }
}
