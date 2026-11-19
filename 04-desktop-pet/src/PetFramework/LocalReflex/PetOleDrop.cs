using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;

namespace PetFramework
{
    // ===== 这些类型必须放在命名空间层（不能嵌在 PetOleDrop 里面，
    // ===== 否则 PetOleDrop 继承自己的嵌套接口 = 循环基类，编译不过）=====

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FORMATETC
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STGMEDIUM
    {
        public uint tymed;
        public IntPtr unionmember;
        public IntPtr pUnkForRelease;
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
    }

    [ComImport, Guid("0000010E-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObject
    {
        [PreserveSig] int GetData(ref FORMATETC pformatetcIn, out STGMEDIUM pmedium);
        [PreserveSig] int GetDataHere(ref FORMATETC pformatetc, ref STGMEDIUM pmedium);
        [PreserveSig] int QueryGetData(ref FORMATETC pformatetc);
        [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC pformatectIn, out FORMATETC pformatetcOut);
        [PreserveSig] int SetData(ref FORMATETC pformatetc, ref STGMEDIUM pmedium, bool fRelease);
        [PreserveSig] int EnumFormatEtc(uint dwDirection, out IntPtr ppenumFormatEtc);
        [PreserveSig] int DAdvise(ref FORMATETC pformatetc, uint advf, IntPtr pAdvSink, out uint pdwConnection);
        [PreserveSig] int DUnadvise(uint dwConnection);
        [PreserveSig] int EnumDAdvise(out IntPtr ppenumAdvise);
    }
    #endif

    /// <summary>
    /// OLE 拖放（IDropTarget + RegisterDragDrop）—— 资源管理器实际走的通道。
    ///
    /// 为什么换掉 WM_DROPFILES：实机日志证明
    ///   ① 我们能收到 WM_DROPFILES（命中判定修对了）
    ///   ② 自检证明本进程 DragQueryFile 完全正常（手造 HDROP → 读出 C:\Windows\notepad.exe）
    ///   ③ 但外壳给的 HDROP 读出 GetLastError=6（ERROR_INVALID_HANDLE），两次拖放还是同一个句柄
    /// ⇒ 老式 WM_DROPFILES 的跨进程共享内存句柄在这台机器上取不到。
    /// OLE 由 COM 负责把数据搬进我们进程，不靠那个句柄。
    ///
    /// 目标定位：OLE 走 WindowFromPoint，而它跳过 WS_EX_TRANSPARENT 的窗口；
    /// TransparentWindow 只在小白的包围盒附近才摘掉这个位 —— 于是「拖到小白身上才收、拖别处照常给桌面」。
    /// </summary>
    [ComVisible(true)]
    public class PetOleDrop : MonoBehaviour
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        , IDropTarget
    #endif
    {
        public static bool Registered;     // OLE 通道是否登记成功

        [Header("总开关")]
        public bool enable = true;              // 出问题可在 Inspector 里直接关掉，不用重编译
        public PetFeed feed;
        public bool acceptOnlyNearPet = true;   // 只在光标靠近小白时才接住（其余放行给桌面）
        public float nearPad = 120f;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr pvReserved);
        [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);
        [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
        [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        const uint CF_HDROP = 15;
        const uint DVASPECT_CONTENT = 1;
        const uint TYMED_HGLOBAL = 1;
        const uint DROPEFFECT_NONE = 0;
        const uint DROPEFFECT_COPY = 1;
        const uint DROPEFFECT_MOVE = 2;

        IntPtr hwnd;

        void Awake()
        {
            if (feed == null) feed = FindObjectOfType<PetFeed>();
        }

        // 放在 Start（不是 Awake）：让别的系统先初始化完，出问题也更好定位
        void Start()
        {
            if (!enable) { Debug.Log("[PetOleDrop] 总开关关闭，跳过 OLE 登记"); return; }
            TryRegister();
        }

        void TryRegister()
        {
            try
            {
                // 每一步都先打日志：万一崩了，日志最后一行就是崩在哪一句
                Debug.Log("[PetOleDrop] ① 准备 OleInitialize");
                int hr = OleInitialize(IntPtr.Zero);
                Debug.Log("[PetOleDrop] ② OleInitialize hr=0x" + hr.ToString("X8")
                    + (hr == 0 ? " (S_OK)" : hr == 1 ? " (S_FALSE 已初始化过)" : " ← 失败，OLE 拖放可能不可用"));

                hwnd = WindowUtil.FindMainWindow();
                if (hwnd == IntPtr.Zero) { Debug.LogWarning("[PetOleDrop] 找不到主窗口，OLE 通道放弃"); return; }
                Debug.Log("[PetOleDrop] ③ 找到主窗口 hwnd=" + hwnd + "，准备 RegisterDragDrop(this)");

                int rv = RevokeDragDrop(hwnd);   // 先清场（可能挂着 Unity 自己的 / legacy 的）
                Debug.Log("[PetOleDrop] ④ RevokeDragDrop hr=0x" + rv.ToString("X8"));
                int reg = RegisterDragDrop(hwnd, this);
                Debug.Log("[PetOleDrop] ⑤ RegisterDragDrop hr=0x" + reg.ToString("X8") + (reg == 0 ? " 【成功】" : " 【失败】"));
                Registered = (reg == 0);

                if (!Registered)
                {
                    DragAcceptFiles(hwnd, true);   // OLE 没成 → 退回老式 WM_DROPFILES
                    Debug.Log("[PetOleDrop] OLE 失败，已回退到 legacy DragAcceptFiles");
                }
            }
            catch (Exception e) { Debug.LogWarning("[PetOleDrop] 初始化异常：" + e); }
        }

        List<string> ExtractPaths(IDataObject data)
        {
            var list = new List<string>();
            var fe = new FORMATETC();
            fe.cfFormat = (ushort)CF_HDROP;
            fe.ptd = IntPtr.Zero;
            fe.dwAspect = DVASPECT_CONTENT;
            fe.lindex = -1;
            fe.tymed = TYMED_HGLOBAL;

            STGMEDIUM stgm;
            int hr = data.GetData(ref fe, out stgm);
            if (hr != 0) { Debug.LogWarning("[PetOleDrop] GetData(CF_HDROP) hr=0x" + hr.ToString("X8")); return list; }
            try
            {
                IntPtr hDrop = stgm.unionmember;
                uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                Debug.Log("[PetOleDrop] CF_HDROP 文件数=" + n + " GetLastError=" + Marshal.GetLastWin32Error());
                for (uint i = 0; i < n; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    list.Add(sb.ToString());
                }
            }
            finally { ReleaseStgMedium(ref stgm); }
            return list;
        }

        bool Near() { return !acceptOnlyNearPet || TransparentWindow.CursorNearPet(nearPad); }

        public int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            bool near = Near();
            pdwEffect = near ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            Debug.Log("[PetOleDrop] DragEnter pt=(" + pt.x + "," + pt.y + ") 靠近桌宠=" + near);
            return 0;
        }

        public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            pdwEffect = Near() ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            return 0;
        }

        public int DragLeave()
        {
            Debug.Log("[PetOleDrop] DragLeave");
            return 0;
        }

        public int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            pdwEffect = DROPEFFECT_NONE;
            try
            {
                Debug.Log("[PetOleDrop] ★★ Drop 收到！pt=(" + pt.x + "," + pt.y + ")");
                if (pDataObj == null) { Debug.LogWarning("[PetOleDrop] Drop 的 IDataObject 为空"); return 0; }
                var paths = ExtractPaths(pDataObj);
                Debug.Log("[PetOleDrop] 解析出 " + paths.Count + " 个路径");
                int ok = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    bool exists = System.IO.File.Exists(paths[i]) || System.IO.Directory.Exists(paths[i]);
                    Debug.Log("[PetOleDrop]   [" + i + "] " + paths[i] + " 存在=" + exists);
                    if (exists && feed != null) { feed.Feed(paths[i]); ok++; }
                }
                if (ok > 0) pdwEffect = DROPEFFECT_MOVE;   // 我们做的是"移到回收站"
            }
            catch (Exception e) { Debug.LogWarning("[PetOleDrop] Drop 异常：" + e); }
            return 0;
        }

        void OnDestroy()
        {
            try { if (Registered && hwnd != IntPtr.Zero) RevokeDragDrop(hwnd); } catch { }
        }
    #endif
    }
}
