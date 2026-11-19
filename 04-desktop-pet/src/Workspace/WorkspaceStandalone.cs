using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PetFramework.Workspace
{
    /// <summary>
    /// 独立工作区程序（MyPetWorkspace.exe）的宿主。
    ///
    /// 为什么单独做一个程序：
    ///   桌宠那套窗口是「全屏 + 分层 + 逐帧切 WS_EX_TRANSPARENT」的透明宠物窗口。
    ///   工作区面板嵌在它里面时，点击要穿过 Win32 的穿透判定，任何一环出岔子
    ///   表现就是"按钮点不动、红叉关不掉"。独立程序用【普通窗口】——
    ///   系统直接把鼠标事件交给 Unity，不需要任何穿透技巧。
    ///
    /// 窗口行为（作者要求）：
    ///   · 打开就是最大化；标题栏保留系统标准的【最小化 / 最大化 / 关闭】三键，边框可拖
    ///     ⚠️ 不能用 WS_EX_TOOLWINDOW 去"藏任务栏图标"——它会把三个键变成工具窗口的小标题栏
    ///   · 打开时托盘不显示图标；点关闭 / Esc → 不退出，隐藏到【系统托盘】并在托盘出现图标
    ///     （启动画面 Unity Splash 因此只出现一次）
    ///   · 托盘左键 = 重新打开，右键 = 打开 / 退出
    ///
    /// 数据完全共用：同一个 EXE 目录下的 config.json / pet_tools.json /
    /// pet_profile.json / Data/workspace.json / PythonEnv。
    /// </summary>
    public class WorkspaceStandalone : MonoBehaviour
    {
        public static WorkspaceStandalone Instance { get; private set; }

        [Header("窗口")]
        public int width = 1600;
        public int height = 1000;
        public bool centerWindow = true;

        // ---------------- Win32 ----------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SetWindowText(IntPtr hWnd, string text);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        static extern IntPtr CallWindowProc(IntPtr proc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT p);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string fileName, int iconIndex,
            out IntPtr largeIcon, out IntPtr smallIcon, uint nIcons);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ---------------- 任务栏按钮（ITaskbarList::DeleteTab）----------------
        // ⚠️ 不能用 WS_EX_TOOLWINDOW 去"隐藏任务栏图标"：Win32 规定它和
        //    WS_MINIMIZEBOX / WS_MAXIMIZEBOX **互斥** ⇒ 三个键会消失（踩过）。
        //    ITaskbarList.DeleteTab 可以从任务栏移除按钮，标题栏保持标准三键。
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class CTaskbarList { }
        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }
        ITaskbarList taskbar;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool AppendMenu(IntPtr hMenu, uint flags, uint id, string text);
        [DllImport("user32.dll")]
        static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr hMenu);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int GWLP_WNDPROC = -4;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_SYSMENU = 0x00080000;
        // ⚠️ 不要用 WS_EX_TOOLWINDOW：它会把标题栏变成"工具窗口"的小栏
        //    （最小化/最大化/关闭三个键会消失）。作者要的是标准窗口三键。

        const uint WM_CLOSE = 0x0010;
        const uint WM_APP = 0x8000;
        const uint TrayCallback = WM_APP + 21;
        const uint TrayIconID = 20261117;

        const uint NIF_MESSAGE = 0x01;
        const uint NIF_ICON = 0x02;
        const uint NIF_TIP = 0x04;
        const uint NIM_ADD = 0x00;
        const uint NIM_DELETE = 0x02;

        IntPtr hwnd = IntPtr.Zero;
        IntPtr oldWndProc = IntPtr.Zero;
        WndProcDelegate newWndProc;          // ⚠️ 必须存字段，否则被 GC 回收后崩溃
        IntPtr trayIcon = IntPtr.Zero;
        bool trayAdded;
        bool quitting;
        bool ready;      // 启动完成前 Unity 自己会发 WM_CLOSE/最小化，别当成"用户关闭"
        int ownerPid;    // 桌宠 pid：桌宠退出 ⇒ 工作区跟着退出
        float ownerCheckAt;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        void Awake()
        {
            Instance = this;
            // ★ 必须在任何 WorkspaceUI 逻辑之前置位（WorkspaceUI 的 Bootstrap 是 AfterSceneLoad）。
            WorkspaceUI.StandaloneMode = true;
            Application.runInBackground = true;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
        }

        IEnumerator Start()
        {
            yield return null;      // 等窗口真正创建

            // ⚠️ 不能按标题 FindWindow：作者机器上 Cherry Studio 的窗口标题也叫「MyPet 工作区」。
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                yield return null;
                proc.Refresh();
                hwnd = proc.MainWindowHandle;
            }

            // ★ 单实例：已经有工作区在跑（哪怕收在托盘里）就把它叫出来，自己退出。
            //   这样 F8 / 双击 永远只有同一个工作区页面。
            var others = System.Diagnostics.Process.GetProcessesByName(WorkspaceUI.StandaloneProcess);
            for (int i = 0; i < others.Length; i++)
            {
                if (others[i].Id == proc.Id) continue;
                var oh = FindProcessWindow(others[i].Id);
                if (oh == IntPtr.Zero) continue;
                ShowWindow(oh, 3);
                SetForegroundWindow(oh);
                Debug.Log("[WS] 已有工作区在运行（pid=" + others[i].Id + "），唤醒它并退出本实例");
                quitting = true;
                Application.Quit();
                yield break;
            }

            ApplyWindow();

            var ws = WorkspaceUI.Instance;
            if (ws == null)
            {
                Debug.LogError("[WS] WorkspaceUI 没有初始化（Bootstrap 失败）");
                yield break;
            }
            var args = Environment.GetCommandLineArgs();
            bool startHidden = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-wsdebug", StringComparison.OrdinalIgnoreCase))
                    ws.debugClicks = true;
                if (string.Equals(args[i], "-hidden", StringComparison.OrdinalIgnoreCase))
                    startHidden = true;
                if (string.Equals(args[i], "-ownerPid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out ownerPid);
            }
            ws.Show();

            yield return null;
            if (hwnd != IntPtr.Zero) SetWindowText(hwnd, WorkspaceUI.StandaloneTitle);
            // ⚠️ Unity 会在启动后几帧内按 Screen.SetResolution 再摆一次窗口，
            //    把前面的 SW_MAXIMIZE 盖掉 ⇒ 这里**再最大化一次**，保证一打开就是全屏。
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 3);
            ready = true;
            Debug.Log("[WS] ownerPid=" + ownerPid + " startHidden=" + startHidden);

            // ★ 桌宠启动时预热：进程起来、场景加载完，但窗口隐藏着等 F8。
            //   这样之后按 F8 只是 ShowWindow，不会再走启动画面。
            if (startHidden && hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 0);     // SW_HIDE
                Debug.Log("[WS] 预热完成，隐藏等待 F8（无托盘、无任务栏图标）");
            }
            else
            {
                Debug.Log("[WS] 独立工作区就绪（关闭 = 隐藏，F8 重新打开）");
            }
        }

        void ApplyWindow()
        {
            if (hwnd == IntPtr.Zero) return;

            SetWindowText(hwnd, WorkspaceUI.StandaloneTitle);

            // ★ 标准窗口：边框可拖 + 系统菜单（右上角三个键：最小化 / 最大化 / 关闭）
            //   绝对不能再加 WS_EX_TOOLWINDOW —— 那会把三个键变成工具窗口的小标题栏。
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style |= WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;
            SetWindowLong(hwnd, GWL_STYLE, style);

            if (centerWindow)
            {
                RECT r;
                if (GetWindowRect(hwnd, out r))
                {
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w > 0 && h > 0)
                    {
                        int x = (GetSystemMetrics(0) - w) / 2;
                        int y = (GetSystemMetrics(1) - h) / 2;
                        // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
                        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
                    }
                }
            }

            ShowWindow(hwnd, 3);    // SW_MAXIMIZE
            LoadTrayIcon();         // 只加载图标句柄（本版本不放托盘，留着备用）

            // 拦 WM_CLOSE：点窗口的 ✕ = 只隐藏，不是退出
            newWndProc = WndProc;
            IntPtr fn = Marshal.GetFunctionPointerForDelegate(newWndProc);
            oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, fn);
        }

        /// <summary>
        /// 把窗口从任务栏移除（保留给需要"无任务栏图标"的场合）。
        /// 用 ITaskbarList.DeleteTab，而不是 WS_EX_TOOLWINDOW（那个会和最小化/最大化按钮互斥）。
        /// </summary>
        void RemoveTaskbarButton()
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (taskbar == null)
                {
                    taskbar = (ITaskbarList)new CTaskbarList();
                    taskbar.HrInit();
                }
                taskbar.DeleteTab(hwnd);
                Debug.Log("[WS] 已从任务栏移除按钮（托盘里仍有）");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WS] 任务栏删除失败（不影响使用）：" + e.Message);
            }
        }

        void LoadTrayIcon()
        {
            string exe = WorkspaceUI.StandaloneExePath();
            try
            {
                IntPtr big, small;
                uint n = ExtractIconEx(exe, 0, out big, out small, 1);
                trayIcon = small != IntPtr.Zero ? small : big;
                Debug.Log("[WS] 托盘图标 ← " + exe + " (count=" + n + " small=" + small + " big=" + big + ")");
            }
            catch (Exception e) { Debug.LogWarning("[WS] 取 EXE 图标失败：" + e.Message); }

            if (trayIcon == IntPtr.Zero) trayIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
        }

        /// <summary>按 pid 找该进程的窗口（隐藏的也算 —— 收在托盘里的窗口 MainWindowHandle 是 0）。</summary>
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
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // ---------------- 显示 / 隐藏（不占托盘）----------------

        /// <summary>
        /// 关闭 = 只隐藏窗口（进程和场景都留着）：
        ///   · 任务栏图标随窗口消失
        ///   · 托盘里**不出现**工作区图标（作者要求）
        ///   · 再按 F8 只是 ShowWindow，同一个页面、不会再播启动画面
        /// </summary>
        public void HideToTray()
        {
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 0);       // SW_HIDE
            Debug.Log("[WS] 已隐藏（按 F8 重新打开同一页面）");
        }

        public void RestoreFromWindow()
        {
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 3);       // SW_MAXIMIZE（保持全屏感）
            SetForegroundWindow(hwnd);
            Debug.Log("[WS] 已显示");
        }

        void AddTrayIcon()
        {
            if (trayAdded || hwnd == IntPtr.Zero) return;
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = TrayIconID;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = TrayCallback;
            d.hIcon = trayIcon;
            d.szTip = "MyPet 工作区（单击打开，右键菜单）";
            trayAdded = Shell_NotifyIcon(NIM_ADD, ref d);
        }

        void RemoveTrayIcon()
        {
            if (!trayAdded || hwnd == IntPtr.Zero) return;
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = TrayIconID;
            Shell_NotifyIcon(NIM_DELETE, ref d);
            trayAdded = false;
        }

        void ShowTrayMenu()
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr menu = CreatePopupMenu();
            AppendMenu(menu, 0, 1, "打开工作区");
            AppendMenu(menu, 0, 2, "退出");
            POINT p; GetCursorPos(out p);
            SetForegroundWindow(hwnd);      // TrackPopupMenu 要求先激活，否则点外面菜单不消失
            int cmd = TrackPopupMenu(menu, 0x0100 | 0x0002, p.X, p.Y, 0, hwnd, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
            DestroyMenu(menu);
            if (cmd == 1) RestoreFromWindow();
            else if (cmd == 2) QuitApp();
        }

        public void QuitApp()
        {
            quitting = true;
            RemoveTrayIcon();
            Debug.Log("[WS] 退出工作区程序");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLOSE && !quitting)
            {
                // 启动阶段 Unity 自己也会发 WM_CLOSE（切分辨率/重建窗口），
                // 这时不能当成用户点了关闭 —— 直接吞掉。
                if (ready) HideToTray();
                return IntPtr.Zero;
            }
            return CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
        }

        void Update()
        {
            // ★ 桌宠退出 ⇒ 工作区跟着退出（用户：工作区是随着桌宠开、随着桌宠关的）
            if (ownerPid <= 0 || Time.time < ownerCheckAt) return;
            ownerCheckAt = Time.time + 2f;
            // ⚠️ Mono 的 GetProcessById 对已退出的 pid 不一定抛异常 ⇒ 必须看 HasExited
            bool alive = false;
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(ownerPid);
                alive = p != null && !p.HasExited;
            }
            catch { alive = false; }
            if (!alive)
            {
                Debug.Log("[WS] 桌宠已退出 → 工作区跟着退出");
                QuitApp();
            }
        }

        void OnApplicationQuit()
        {
            quitting = true;
            RemoveTrayIcon();
        }
    }
}
