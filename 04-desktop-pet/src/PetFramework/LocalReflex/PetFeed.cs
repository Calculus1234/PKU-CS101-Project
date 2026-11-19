using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;

namespace PetFramework
{
    #endif

    /// <summary>
    /// 喂食：把拖入的<b>文件或文件夹</b>移入 Windows 回收站（可恢复），触发 Jump，再让小白通过 API 回应。
    /// 回收站删除走 Microsoft.VisualBasic.FileIO.FileSystem（反射调用，避免硬依赖程序集）；
    /// 若程序集缺失则【拒绝删除】而非直接删除，绝不误删。
    /// 另提供一条【不依赖任何拖放钩子】的可靠通道：Ctrl+C 复制文件后按 F10，或聊天框输入 /feed。
    /// </summary>
    public class PetFeed : MonoBehaviour
    {
        public PetBrain brain;

        [Header("剪贴板投喂（在资源管理器里 Ctrl+C 复制文件/文件夹后按这个键）")]
        public KeyCode pasteFeedKey = KeyCode.F10;
        public bool pasteFeedNeedsCtrl = false;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);
        const uint CF_HDROP = 15;

        /// <summary>读剪贴板里的文件列表（资源管理器 Ctrl+C 复制过文件后可用）。绝不改剪贴板内容。</summary>
        public static List<string> ClipboardFiles()
        {
            var list = new List<string>();
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return list;
                try
                {
                    if (!IsClipboardFormatAvailable(CF_HDROP)) return list;
                    IntPtr h = GetClipboardData(CF_HDROP);
                    if (h == IntPtr.Zero) return list;
                    uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < n; i++)
                    {
                        uint len = DragQueryFile(h, i, null, 0);
                        var sb = new StringBuilder((int)len + 1);
                        DragQueryFile(h, i, sb, (uint)sb.Capacity);
                        string p = sb.ToString();
                        if (!string.IsNullOrEmpty(p)) list.Add(p);
                    }
                }
                finally { CloseClipboard(); }
            }
            catch (Exception e) { Debug.LogWarning("[PetFeed] 读剪贴板失败：" + e.Message); }
            return list;
        }

        // ==================== 回收站（原生 shell API） ====================
        // ⚠️ 为什么不用 Microsoft.VisualBasic：
        //   同一段 SendToRecycleBin，从 PowerShell（.NET Framework）调会正常进回收站，
        //   但在 Unity（Mono）里「调用成功、文件消失、回收站却是空的」—— 说明 Mono 自带的那份
        //   Microsoft.VisualBasic 是残缺实现，把 SendToRecycleBin 吞掉退化成 File.Delete。
        //   下面直接调 shell 的 SHFileOperation（正是 .NET Framework 版 VB 内部真正调用的 API），
        //   而且按钮前用 SHQueryRecycleBin 记条目数、按钮后再查一次：没真进回收站就报错。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO p);

        const uint FO_DELETE = 0x0003;
        const ushort FOF_SILENT = 0x0004;
        const ushort FOF_NOCONFIRMATION = 0x0010;
        const ushort FOF_ALLOWUNDO = 0x0040;
        const ushort FOF_NOERRORUI = 0x0400;
        const ushort FOF_WANTNUKEWARNING = 0x4000;

        static long RecycleCount(string drive)
        {
            var q = new SHQUERYRBINFO();
            q.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            if (SHQueryRecycleBinW(drive, ref q) != 0) return -1;
            return q.i64NumItems;
        }

        /// <summary>拒绝一切“删了会出事”的路径。判断不了就拒绝（安全优先）。</summary>
        static bool IsProtectedPath(string full)
        {
            try
            {
                string s = full.TrimEnd('\\').ToLowerInvariant();
                if (s.Length <= 2 && s.EndsWith(":")) return true;          // 盘根 C:\
                if (s.Contains("$recycle.bin")) return true;                 // 回收站自己
                if (s.Contains("\\windows\\") || s.EndsWith("\\windows")) return true;

                string[] bad = new string[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                };
                for (int i = 0; i < bad.Length; i++)
                {
                    string b = bad[i];
                    if (string.IsNullOrEmpty(b)) continue;
                    string bb = b.TrimEnd('\\').ToLowerInvariant();
                    if (s == bb) return true;                                // 只拒绝“这个文件夹本身”，里面的东西照常可喂
                }
                return false;
            }
            catch { return true; }
        }
    #endif

        void Awake()
        {
            if (brain == null) brain = FindObjectOfType<PetBrain>();
        }

        void Update()
        {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (Input.GetKeyDown(pasteFeedKey) && (!pasteFeedNeedsCtrl || ctrl))
            {
                var files = ClipboardFiles();
                if (files.Count == 0) { Debug.Log("[PetFeed] 快捷键触发，但剪贴板里没有文件（先在资源管理器里 Ctrl+C 复制一个）"); return; }
                for (int i = 0; i < files.Count; i++)
                {
                    Debug.Log("[PetFeed] 剪贴板投喂：" + files[i]);
                    Feed(files[i]);
                }
            }
    #endif
        }

        public void Feed(string path)
        {
            Debug.Log("[PetFeed] 收到投喂：" + path);

            bool isFile = File.Exists(path);
            bool isDir = Directory.Exists(path);
            if (!isFile && !isDir)
            {
                Debug.LogWarning("[PetFeed] 路径不存在（既不 file 也不 dir）：" + path);
                return;
            }

            if (!MoveToRecycleBin(path, isDir))
            {
                Debug.LogWarning("[PetFeed] 移入回收站失败（缺组件或调用异常），已取消以免误删：" + path);
                return;
            }
            Debug.Log("[PetFeed] 已移入回收站：" + path);

            if (brain != null) brain.PlayAction("Jump");   // ① 喂食成功 → Jump

            // ② 发事件给 AI，让小白回应
            if (PetAPI.Instance != null)
            {
                PetAPI.Instance.FeedEvent(Path.GetFileName(path), r =>
                {
                    if (r == null) return;
                    if (brain != null) brain.PlayAction(r.action);
                    TTS.Speak(r.text);
                    Debug.Log("[PetFeed] " + PetProfileConfig.PetName + "：" + r.text);
                });
            }
        }

        /// <summary>
        /// 移入回收站。全程只用 shell 的 SHFileOperation + FOF_ALLOWUNDO，绝不碰 File.Delete。
        /// 三重保险：① 预检受保护路径 ② 该盘必须有回收站 ③ 事后校验回收站条目数确实 +1。
        /// 任一环节不满足 → 返回 false，上层会拒绝这次投喂。
        /// </summary>
        bool MoveToRecycleBin(string path, bool isDir)
        {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string full;
            try { full = Path.GetFullPath(path); }
            catch { Debug.LogError("[PetFeed] 路径无法规范化，拒绝删除：" + path); return false; }

            // ---- ① 受保护路径预检 ----
            if (IsProtectedPath(full))
            {
                Debug.LogError("[PetFeed] 拒绝：这是受保护的系统/用户路径，不允许喂：" + full);
                return false;
            }

            // ---- ② 该盘必须真的有回收站 ----
            string root = Path.GetPathRoot(full);
            long before = RecycleCount(root);
            if (before < 0)
            {
                Debug.LogError("[PetFeed] 拒绝：该盘查不到回收站（SHQueryRecycleBin 失败），宁可不动：" + root);
                return false;
            }

            // ---- ③ 调 shell 删（FOF_ALLOWUNDO = 进回收站）----
            var op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = full + "\0\0";   // 必须是双 null 结尾
            op.pTo = null;
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING);
            int hr = SHFileOperationW(ref op);
            if (hr != 0 || op.fAnyOperationsAborted)
            {
                Debug.LogError("[PetFeed] SHFileOperation 失败（hr=" + hr + " 已中止=" + op.fAnyOperationsAborted
                    + "），文件未被删除：" + full);
                return false;
            }

            // ---- ④ 事后校验：回收站条目必须 +1，否则就是永久删除，必须吼出来 ----
            long after = RecycleCount(root);
            if (after <= before)
            {
                Debug.LogError("[PetFeed] ★★★ 危险：文件已消失但回收站条目没有增加（" + before + " → " + after
                    + "）—— 可能发生了永久删除！路径=" + full);
                return false;
            }

            Debug.Log("[PetFeed] 已确认进入回收站：条目 " + before + " → " + after + "  " + full);
            return true;
    #else
            Debug.LogWarning("[PetFeed] 非 Windows 平台，拒绝删除");
            return false;
    #endif
        }
    }
}
