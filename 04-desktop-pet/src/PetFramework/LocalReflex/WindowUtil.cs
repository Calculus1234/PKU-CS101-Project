using System;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;

namespace PetFramework
{
    #endif

    /// <summary>
    /// 可靠地拿到本进程的 Unity 主窗口句柄（比 GetActiveWindow 稳）：
    /// EnumWindows 遍历顶层窗口，取属于本进程、可见、标题非空、面积最大的那个。
    /// 双击启动时窗口尚未"激活"，GetActiveWindow 会拿错/拿空，这是黑屏的根因。
    /// </summary>
    public static class WindowUtil
    {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        public static IntPtr FindMainWindow()
        {
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != myPid || !IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                long area = (long)(r.right - r.left) * (r.bottom - r.top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }
    #endif
    }
}
