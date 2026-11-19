using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PetFramework
{
    #endif

    /// <summary>
    /// 系统级文件拖放：P/Invoke user32/shell32 钩窗口过程，捕获 WM_DROPFILES，
    /// 用 DragQueryFile 取出拖入的文件/文件夹路径，交给 PetFeed（进回收站 + Jump + 通知 AI）。
    /// 关键：挂钩【本进程的所有窗口（含子窗口）】并定期重挂，避免拖放落在子窗口上收不到。
    /// </summary>
    public class PetDragDrop : MonoBehaviour
    {
        public PetFeed feed;
        public bool hookInstalled;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        // UIPI：非管理员进程默认收不到高完整性级别发来的部分消息，放行拖放相关的三条
        [DllImport("user32.dll", SetLastError = true)] static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);
        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
        [DllImport("shell32.dll")] static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
        // 剪贴板兜底：OLE 拖动期间拖的数据同时挂在剪贴板的 CF_HDROP 上
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        const uint CF_HDROP = 15;

        // 自检用：手工造一个 HDROP，验证 DragQueryFile 在本进程到底能不能用
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr hMem);
        [StructLayout(LayoutKind.Sequential)] struct DROPFILES { public uint pFiles; public int x; public int y; public int fNC; public int fWide; }
        const uint GMEM_MOVEABLE = 0x0002;
        const uint GMEM_ZEROINIT = 0x0040;

        const int GWL_WNDPROC = -4;
        const int GWL_EXSTYLE = -20;
        const uint WM_DROPFILES = 0x0233;
        const uint WM_NCHITTEST = 0x0084;
        const uint WM_COPYDATA = 0x004A;
        const uint WM_COPYGLOBALDATA = 0x0049;
        const uint MSGFLT_ALLOW = 1;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        WndProcDelegate proc;
        Dictionary<IntPtr, IntPtr> oldProcs = new Dictionary<IntPtr, IntPtr>();
        int frames;

        void Awake()
        {
            if (feed == null) feed = FindObjectOfType<PetFeed>();
            proc = WndProc;
            HookAll();
            SelfTest();
        }

        /// <summary>
        /// 决定性自检：在本进程手工拼一个合法的 HDROP 再交给 DragQueryFile 读。
        /// 读得出来 ⇒ DragQueryFile 没毛病，外壳给我们的那个句柄是真坏了（跨进程/完整性级别问题）。
        /// 读不出来 ⇒ 是我们进程自己调用不了 DragQueryFile（那才轮得到换 API）。
        /// </summary>
        void SelfTest()
        {
            try
            {
                string probe = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
                if (!System.IO.File.Exists(probe)) probe = Application.dataPath;
                probe = probe.Replace('/', '\\');

                int header = Marshal.SizeOf(typeof(DROPFILES));          // 应为 20
                int bytes = (probe.Length + 1) * 2 + 2;                  // 宽串 + 双结尾
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(header + bytes));
                if (h == IntPtr.Zero) { Debug.LogWarning("[PetDragDrop] 自检 GlobalAlloc 失败 err=" + Marshal.GetLastWin32Error()); return; }
                IntPtr p = GlobalLock(h);
                var df = new DROPFILES(); df.pFiles = (uint)header; df.x = 0; df.y = 0; df.fNC = 0; df.fWide = 1;
                Marshal.StructureToPtr(df, p, false);
                Marshal.Copy(probe.ToCharArray(), 0, new IntPtr(p.ToInt64() + header), probe.Length);
                GlobalUnlock(h);

                uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                int err = Marshal.GetLastWin32Error();
                var back = new StringBuilder(1024);
                DragQueryFile(h, 0, back, (uint)back.Capacity);
                Debug.Log("[PetDragDrop] 自检 手造HDROP → 文件数=" + n + " (期望1) GetLastError=" + err + " 读回=" + back.ToString());
                GlobalFree(h);
            }
            catch (Exception e) { Debug.LogWarning("[PetDragDrop] 自检异常：" + e.Message); }
        }

        void HookAll()
        {
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == myPid && IsWindowVisible(h))
                {
                    Hook(h);
                    EnumChildWindows(h, (c, l2) => { if (IsWindowVisible(c)) Hook(c); return true; }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            hookInstalled = oldProcs.Count > 0;
            if (frames == 0)
                Debug.Log("[PetDragDrop] 已挂钩 " + oldProcs.Count + " 个窗口接收拖放" + (hookInstalled ? "" : "（失败）"));
        }

        void Hook(IntPtr h)
        {
            if (h == IntPtr.Zero || oldProcs.ContainsKey(h)) return;
            // 拖放登记改由 PetOleDrop 负责（OLE 通道，失败时它自己回退到 DragAcceptFiles）。
            // 这里只保留挂窗口过程 —— WM_NCHITTEST 的命中修正还是要用。
            // 放行 UIPI（否则高完整性级别的来源可能发不进来这几条消息）
            try
            {
                ChangeWindowMessageFilterEx(h, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(h, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(h, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch { }
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(proc);
            IntPtr old = IntPtr.Size == 8 ? SetWindowLongPtr64(h, GWL_WNDPROC, ptr) : SetWindowLong32(h, GWL_WNDPROC, ptr);
            if (old != IntPtr.Zero)
            {
                oldProcs[h] = old;
                long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64() : GetWindowLong32(h, GWL_EXSTYLE);
                Debug.Log("[PetDragDrop] 挂钩 hwnd=" + h + " 原WndProc=" + old
                    + "  当前ExStyle=0x" + ex.ToString("X8")
                    + "  WS_EX_ACCEPTFILES(0x10)=" + ((ex & 0x10) != 0)
                    + "  WS_EX_TRANSPARENT(0x20)=" + ((ex & 0x20) != 0));
            }
        }

        void Update()
        {
            frames++;
            if (frames % 180 == 0)
            {
                HookAll();                                        // 补挂新出现的窗口
                // 只在真的丢了 ACCEPTFILES 位时才重新登记 —— 无条件反复调 DragAcceptFiles
                // 会不停 SetWindowLongPtr，把外壳程序的拖放登记状态搅乱（症状：收到 WM_DROPFILES 但文件数=0）
                // 只在【没有走 OLE 通道】时才维持 legacy 的 ACCEPTFILES 位
                if (!PetOleDrop.Registered)
                {
                    foreach (var kv in oldProcs)
                    {
                        long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(kv.Key, GWL_EXSTYLE).ToInt64() : GetWindowLong32(kv.Key, GWL_EXSTYLE);
                        if ((ex & 0x10) == 0)
                        {
                            DragAcceptFiles(kv.Key, true);
                            Debug.Log("[PetDragDrop] ACCEPTFILES 位丢了，已重新登记 hwnd=" + kv.Key);
                        }
                    }
                }
            }
            // ★ 诊断心跳（每 ~20 秒一次）：累计消息=0 说明钩子根本没活着；消息很多但 WM_DROPFILES=0
            //   说明消息能到、只是拖放没落上来（那就是命中判定/穿透的问题）
            if (frames % 600 == 0)
            {
                foreach (var kv in oldProcs)
                {
                    long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(kv.Key, GWL_EXSTYLE).ToInt64() : GetWindowLong32(kv.Key, GWL_EXSTYLE);
                    Debug.Log("[PetDragDrop] 心跳 hwnd=" + kv.Key + " 累计窗口消息=" + msgCount
                        + " 其中 WM_DROPFILES=" + dropCount
                        + " | ExStyle=0x" + ex.ToString("X8")
                        + " ACCEPTFILES=" + ((ex & 0x10) != 0)
                        + " TRANSPARENT=" + ((ex & 0x20) != 0)
                        + " | 左键按住=" + ((GetAsyncKeyState(0x01) & 0x8000) != 0)
                        + " 光标靠近桌宠=" + TransparentWindow.CursorNearPet(30f));
                }
            }
        }

        long msgCount;
        int dropCount;

        /// <summary>从一个 HDROP 里取出所有路径并投喂，返回成功喂进去的个数。</summary>
        int Emit(IntPtr hDrop, uint count)
        {
            int n = 0;
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                var s = new StringBuilder((int)len + 1);
                DragQueryFile(hDrop, i, s, (uint)s.Capacity);
                string path = s.ToString();
                bool ok = System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
                Debug.Log("[PetDragDrop] 拖入：" + path + "  存在=" + ok);
                if (ok && feed != null) { feed.Feed(path); n++; }
            }
            return n;
        }

        IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            msgCount++;

            if (msg == WM_DROPFILES)
            {
                dropCount++;
                int got = 0;
                try
                {
                    uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                    int err = Marshal.GetLastWin32Error();
                    POINT pt; bool ptOk = DragQueryPoint(wParam, out pt);
                    // ★★ 诊断第一行：能看到这行说明 WM_DROPFILES 真的送到了
                    Debug.Log("[PetDragDrop] ★★ 收到 WM_DROPFILES hwnd=" + hWnd + " wParam=" + wParam
                        + " 主路径文件数=" + count + " GetLastError=" + err
                        + " DragQueryPoint=" + ptOk + "(" + pt.x + "," + pt.y + ")");

                    if (count > 0) got += Emit(wParam, count);

                    if (count == 0)
                    {
                        Debug.Log("[PetDragDrop] 主路径为空 → 试剪贴板 CF_HDROP 兜底 (格式可用=" + IsClipboardFormatAvailable(CF_HDROP) + ")");
                        if (OpenClipboard(IntPtr.Zero))
                        {
                            try
                            {
                                if (IsClipboardFormatAvailable(CF_HDROP))
                                {
                                    IntPtr h = GetClipboardData(CF_HDROP);
                                    if (h != IntPtr.Zero)
                                    {
                                        uint c2 = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                                        Debug.Log("[PetDragDrop] 剪贴板 CF_HDROP 文件数=" + c2);
                                        if (c2 > 0) got += Emit(h, c2);
                                    }
                                }
                            }
                            finally { CloseClipboard(); }
                        }
                        else Debug.LogWarning("[PetDragDrop] OpenClipboard 失败 GetLastError=" + Marshal.GetLastWin32Error());
                    }

                    if (got == 0) Debug.LogWarning("[PetDragDrop] 这次拖放没拿到任何有效路径（拖的可能不是文件？）");
                }
                catch (Exception e) { Debug.LogWarning("[PetDragDrop] 处理拖放异常：" + e.Message); }
                try { DragFinish(wParam); } catch { }
                return IntPtr.Zero;
            }

            if (msg == WM_NCHITTEST)
            {
                // ⚠️⚠️ 主面板开着时，判定权必须让给面板：
                //    否则下面那句 `mouseOverPet → HTCLIENT` 会让**整块全屏窗口**都吃掉鼠标，
                //    面板看着正常、桌面却全点不到。
                if (TransparentWindow.DashboardOpen)
                {
                    long lp = lParam.ToInt64();
                    int sx = (short)(lp & 0xFFFF);
                    int sy = (short)((lp >> 16) & 0xFFFF);
                    return (IntPtr)(TransparentWindow.DashboardHit(hWnd, sx, sy) ? HTCLIENT : HTTRANSPARENT);
                }

                // 身上 → 收
                if (TransparentWindow.mouseOverPet) return (IntPtr)HTCLIENT;
                // 【关键修复】系统拖放进行中时 Unity 收不到鼠标事件，mouseOverPet 恒为 false，
                // 于是窗口一直自称 HTTRANSPARENT，拖放就绕开我们落到桌面上了。
                // 这里用「左键按住 + 光标几何上靠近小白」兜底，让拖放能落到小白身上。
                bool mouseHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                if (mouseHeld && TransparentWindow.CursorNearPet(80f)) return (IntPtr)HTCLIENT;
                return (IntPtr)HTTRANSPARENT;
            }

            IntPtr op;
            if (oldProcs.TryGetValue(hWnd, out op) && op != IntPtr.Zero)
                return CallWindowProc(op, hWnd, msg, wParam, lParam);
            return IntPtr.Zero;
        }

        void OnDestroy()
        {
            try
            {
                foreach (var kv in oldProcs)
                {
                    if (!PetOleDrop.Registered) DragAcceptFiles(kv.Key, false);
                    if (IntPtr.Size == 8) SetWindowLongPtr64(kv.Key, GWL_WNDPROC, kv.Value);
                    else SetWindowLong32(kv.Key, GWL_WNDPROC, kv.Value);
                }
                oldProcs.Clear();
            }
            catch { }
        }
    #endif
    }
}
