using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 桌宠的系统托盘图标（只在打包后的 EXE 里启用）。
    ///
    /// 作者的要求：
    ///   · 桌宠**不占任务栏**（把窗口的任务栏按钮删掉），图标放到托盘里
    ///   · 托盘左键 = 打开工作区（同一个实例；工作区由桌宠预热，再按 F8 只是显示）
    ///   · 托盘右键 = 「打开工作区 / 退出小白」
    ///
    /// ⚠️ 任务栏按钮用 `ITaskbarList.DeleteTab` 删除，不能用 WS_EX_TOOLWINDOW
    ///    （那个会和标题栏的最大化/最小化按钮互斥）。
    /// ⚠️ WndProc 钩子必须把不认识的消转给上一个 proc（PetDragDrop 也挂了钩子）。
    /// </summary>
    public class PetTrayIcon : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Application.isEditor) return;      // 编辑器里别动窗口
            // 只在【桌宠场景】启用：独立工作区程序里没有 PetBrain，不要给它加托盘图标
            if (UnityEngine.Object.FindObjectOfType<PetBrain>() == null) return;
            var go = new GameObject("PetTrayIcon");
            DontDestroyOnLoad(go);
            go.AddComponent<PetTrayIcon>();
        }

        // ---------------- Win32 ----------------
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

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        const uint TrayCallback = 0x8000 + 33;
        const uint TrayIconID = 20261118;

        const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04;
        const uint NIM_ADD = 0x00, NIM_DELETE = 0x02;

        IntPtr hwnd = IntPtr.Zero;
        IntPtr trayIcon = IntPtr.Zero;
        IntPtr oldWndProc = IntPtr.Zero;
        WndProcDelegate newWndProc;
        bool trayAdded;

        IEnumerator Start()
        {
            yield return new WaitForSeconds(2.5f);   // 等 Unity 把窗口彻底建好

            var proc = System.Diagnostics.Process.GetCurrentProcess();
            for (int i = 0; i < 20 && hwnd == IntPtr.Zero; i++)
            {
                proc.Refresh();
                hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero) yield return new WaitForSeconds(0.5f);
            }
            if (hwnd == IntPtr.Zero)
            {
                Debug.LogWarning("[PetTray] 找不到主窗口，托盘图标未创建");
                yield break;
            }

            RemoveTaskbarButton();

            try
            {
                string exe = proc.MainModule.FileName;
                IntPtr big, small;
                ExtractIconEx(exe, 0, out big, out small, 1);
                trayIcon = small != IntPtr.Zero ? small : big;
            }
            catch { }
            if (trayIcon == IntPtr.Zero) trayIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));

            AddTrayIcon();
            HookWndProc();
            Debug.Log("[PetTray] 托盘图标就绪（任务栏图标已移除）");
        }

        void RemoveTaskbarButton()
        {
            try
            {
                var tb = (ITaskbarList)new CTaskbarList();
                tb.HrInit();
                tb.DeleteTab(hwnd);
                Debug.Log("[PetTray] 已从任务栏移除桌宠窗口按钮");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PetTray] 任务栏删除失败：" + e.Message);
            }
        }

        void AddTrayIcon()
        {
            if (trayAdded) return;
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = TrayIconID;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = TrayCallback;
            d.hIcon = trayIcon;
            d.szTip = PetProfileConfig.PetName + "（桌宠）  左键打开工作区";
            trayAdded = Shell_NotifyIcon(NIM_ADD, ref d);
            if (!trayAdded) Debug.LogWarning("[PetTray] Shell_NotifyIcon 失败");
        }

        void RemoveTrayIcon()
        {
            if (!trayAdded) return;
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = TrayIconID;
            Shell_NotifyIcon(NIM_DELETE, ref d);
            trayAdded = false;
        }

        void HookWndProc()
        {
            newWndProc = WndProc;
            IntPtr fn = Marshal.GetFunctionPointerForDelegate(newWndProc);
            oldWndProc = SetWindowLongPtr(hwnd, -4, fn);
        }

        IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == TrayCallback)
            {
                int ev = lParam.ToInt32();
                if (ev == 0x0202 || ev == 0x0203)        // 左键 / 双击
                {
                    OpenWorkspace();
                    return IntPtr.Zero;
                }
                if (ev == 0x0205)                         // 右键
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            }
            return CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
        }

        static void OpenWorkspace()
        {
            try
            {
                PetFramework.Workspace.WorkspaceUI.EnsureStandaloneRunning(true);
            }
            catch (Exception e) { Debug.LogWarning("[PetTray] 打开工作区失败：" + e.Message); }
        }

        void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            AppendMenu(menu, 0, 1, "打开工作区");
            AppendMenu(menu, 0, 2, "退出" + PetProfileConfig.PetName);
            POINT p; GetCursorPos(out p);
            SetForegroundWindow(hwnd);      // TrackPopupMenu 要求先激活
            int cmd = TrackPopupMenu(menu, 0x0100 | 0x0002, p.X, p.Y, 0, hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            if (cmd == 1) OpenWorkspace();
            else if (cmd == 2)
            {
                RemoveTrayIcon();
                Application.Quit();      // 工作区靠 -ownerPid 看门狗跟着退出
            }
        }

        void OnApplicationQuit() { RemoveTrayIcon(); }
    }
}
