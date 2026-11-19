using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 小白的【环境感知层】：每隔 refreshSec（默认 6 秒）采集一次"主人现在在干嘛"，
    /// 打包成 JSON 交给 PetAPI 注入到每次云端请求里，让小白完全自主决策。
    ///
    /// 采集项（user32 / kernel32）：
    ///   active_window      前台窗口标题（用 GetForegroundWindow + GetWindowTextW，过滤掉 MyPet 自己）
    ///   active_window_dwell 这个窗口已经在前台待了多久（秒）
    ///   recent_windows     最近 20 分钟内出现过的前台窗口（去重，最多 6 条）
    ///   user_idle_sec      用户鼠标/键盘闲置秒数（GetLastInputInfo）
    ///   user_state         把闲置时长翻译成人话（正在用电脑 / 刚停下 / 离开了 / 大概睡了）
    ///   fullscreen_app     前台窗口是否占满整屏（判断在看视频/打游戏）
    ///   mouse_region       光标在屏幕九宫格的哪一格
    ///   system_time        当前系统时间
    ///   session_uptime_sec 小白这次开机陪了主人多久
    ///   pet_action         小白自己当前在做什么
    ///   since_pet_touched  主人多久没碰过小白了
    /// </summary>
    [Serializable]
    public class PetEnv
    {
        public string system_time;
        public float  session_uptime_sec;
        public string active_window;
        public float  active_window_dwell_sec;
        public string recent_windows;
        public float  user_idle_sec;
        public string user_state;
        public bool   fullscreen_app;
        public string mouse_region;
        public int    display_count;
        public string pet_action;
        public float  since_pet_touched_sec;
    }

    public class PetContext : MonoBehaviour
    {
        public static PetContext Instance { get; private set; }

        /// <summary>小白当前动作，由 PetBrain 在切换状态时写入。</summary>
        public static string LastPetAction = "Idle";

        [Header("采集")]
        public float refreshSec = 6f;      // 5~10 秒刷新一次
        public bool  verbose = false;      // 打开后每次刷新都打印一行

        public PetEnv env = new PetEnv();

        [Header("全局缩略图视觉")]
        public bool visionEnabled = true;
        public float visionEverySec = 45f;     // 每 45 秒抓一张全屏缩略图（作者指定：提高环境感知频率）
        public int visionMaxWidth = 640;       // 缩略图最大宽（2880x1800 → 576x360）
        public int jpgQuality = 50;            // JPEG 质量

        /// <summary>最近一次的缩略图（Base64 JPEG）。没有就返回 null。</summary>
        public string GetVisionBase64()
        {
            if (string.IsNullOrEmpty(visionB64)) return null;
            if (Time.realtimeSinceStartup - visionAt > 120f) return null;   // 超过 2 分钟当过期
            return visionB64;
        }
        public float VisionAge() { return Time.realtimeSinceStartup - visionAt; }

        string visionB64;
        float visionAt = -999f;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // ---------------- user32 / kernel32 ----------------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        // ---------- GDI 抓屏（抓的是真实桌面，不是我们的游戏画面）----------
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDst, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight;
            public ushort biPlanes; public ushort biBitCount;
            public uint biCompression; public uint biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter;
            public uint biClrUsed; public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] bmiColors;
        }

        const uint SRCCOPY = 0x00CC0020;
    #endif

        // 最近出现过的工作窗口（标题 -> 最后一次见到的时间）
        readonly List<string> recent = new List<string>();
        readonly List<float> recentAt = new List<float>();
        string curTitle = "";
        float curTitleSince = 0f;
        float startedAt = 0f;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            startedAt = Time.realtimeSinceStartup;
            Sample();
            StartCoroutine(Loop());
            StartCoroutine(VisionLoop());   // 循环自己会看 visionEnabled，不用在这里判（这样开关能实时生效）
        }

        IEnumerator VisionLoop()
        {
            yield return new WaitForSeconds(3f);   // 先让初始化跑完，别抢
            while (true)
            {
                if (visionEnabled) CaptureThumbnail();   // ★ 每轮现查：改配置文件能立刻开/关
                yield return new WaitForSeconds(Mathf.Max(5f, visionEverySec));
            }
        }

        /// <summary>
        /// 抓一张【真实桌面】的缩略图：GDI BitBlt 全屏 → 整数箱式降采样 → JPEG(50) → Base64。
        /// ⚠️ 不能用 ScreenCapture：那抓的是我们自己的游戏画面（一个透明的桌宠层），看不到主人在干嘛。
        /// </summary>
        public void CaptureThumbnail()
        {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            int sw = Screen.currentResolution.width;
            int sh = Screen.currentResolution.height;
            if (sw <= 0 || sh <= 0) return;

            IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hbm = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                hdcScreen = GetDC(IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) { Debug.LogWarning("[PetContext] GetDC 失败"); return; }
                hdcMem = CreateCompatibleDC(hdcScreen);
                hbm = CreateCompatibleBitmap(hdcScreen, sw, sh);
                oldBmp = SelectObject(hdcMem, hbm);
                if (!BitBlt(hdcMem, 0, 0, sw, sh, hdcScreen, 0, 0, SRCCOPY))
                { Debug.LogWarning("[PetContext] BitBlt 失败"); return; }

                var bmi = new BITMAPINFO();
                bmi.bmiColors = new uint[256];
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = sw;
                bmi.bmiHeader.biHeight = -sh;      // 负 = 自上而下，省一次翻转
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;   // BI_RGB

                byte[] bits = new byte[sw * sh * 4];
                if (GetDIBits(hdcMem, hbm, 0, (uint)sh, bits, ref bmi, 0) == 0)
                { Debug.LogWarning("[PetContext] GetDIBits 失败"); return; }

                // 整数箱式降采样，顺手把 BGRA 换成 RGB24
                int step = Mathf.Max(1, Mathf.CeilToInt((float)sw / Mathf.Max(64, visionMaxWidth)));
                int dw = Mathf.Max(1, sw / step), dh = Mathf.Max(1, sh / step);
                byte[] thumb = new byte[dw * dh * 3];
                for (int y = 0; y < dh; y++)
                {
                    int sy0 = y * step, sy1 = Mathf.Min(sh, sy0 + step);
                    for (int x = 0; x < dw; x++)
                    {
                        int sx0 = x * step, sx1 = Mathf.Min(sw, sx0 + step);
                        long b = 0, g = 0, r = 0; int n = 0;
                        for (int yy = sy0; yy < sy1; yy++)
                        {
                            int row = yy * sw;
                            for (int xx = sx0; xx < sx1; xx++)
                            {
                                int i = (row + xx) * 4;
                                b += bits[i]; g += bits[i + 1]; r += bits[i + 2]; n++;
                            }
                        }
                        if (n == 0) n = 1;
                        int o = (y * dw + x) * 3;
                        thumb[o] = (byte)(r / n); thumb[o + 1] = (byte)(g / n); thumb[o + 2] = (byte)(b / n);
                    }
                }

                var tex = new Texture2D(dw, dh, TextureFormat.RGB24, false);
                tex.LoadRawTextureData(thumb);
                tex.Apply();
                byte[] jpg = tex.EncodeToJPG(Mathf.Clamp(jpgQuality, 10, 95));
                if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                else UnityEngine.Object.DestroyImmediate(tex);   // 编辑模式下 Destroy 不生效

                visionB64 = Convert.ToBase64String(jpg);
                visionAt = Time.realtimeSinceStartup;
                if (verbose) Debug.Log("[PetContext] 缩略图 " + dw + "x" + dh + "  JPG " + (jpg.Length / 1024) + "KB  Base64 " + (visionB64.Length / 1024) + "KB");
            }
            catch (Exception e) { Debug.LogWarning("[PetContext] 抓屏异常：" + e.Message); }
            finally
            {
                if (oldBmp != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, oldBmp);
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
            }
    #endif
        }

        IEnumerator Loop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Mathf.Max(2f, refreshSec));
                Sample();
            }
        }

        /// <summary>采集一次（外部也可手动调用）。</summary>
        public void Sample()
        {
            env.system_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            env.session_uptime_sec = Time.realtimeSinceStartup - startedAt;
            try { env.display_count = Display.displays != null ? Display.displays.Length : 1; }
            catch { env.display_count = 1; }

            float idle = 0f;
            string title = "";
            bool fullscreen = false;

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            idle = GetIdleSeconds();
            IntPtr h = GetForegroundWindow();
            if (h != IntPtr.Zero)
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                string t = WindowTitle(h);
                // 名字跟着设定走：用户改了名字之后，旧的两个硬编码叫法就不够用了
                bool self = (pid == myPid) || t.Contains("MyPet") || t.Contains("小白")
                            || t.Contains(PetProfileConfig.PetName);
                if (!self) { title = t; fullscreen = IsFullscreen(h); }
                else { title = "(桌宠层)"; }
            }
            else title = "(没有前台窗口，可能锁屏了)";
    #else
            title = "(非 Windows)";
    #endif

            if (string.IsNullOrWhiteSpace(title)) title = "(无标题窗口)";

            // 同一窗口停留计时
            if (title != curTitle) { curTitle = title; curTitleSince = Time.realtimeSinceStartup; }
            env.active_window = title;
            env.active_window_dwell_sec = Time.realtimeSinceStartup - curTitleSince;

            // 最近窗口列表（20 分钟内、去重、最多 6 条）
            if (!title.StartsWith("("))
            {
                int idx = recent.IndexOf(title);
                if (idx >= 0) { recent.RemoveAt(idx); recentAt.RemoveAt(idx); }
                recent.Insert(0, title); recentAt.Insert(0, Time.realtimeSinceStartup);
                while (recent.Count > 6) { recent.RemoveAt(recent.Count - 1); recentAt.RemoveAt(recentAt.Count - 1); }
            }
            var sbList = new StringBuilder();
            for (int i = 0; i < recent.Count; i++)
            {
                if (Time.realtimeSinceStartup - recentAt[i] > 1200f) continue;
                if (sbList.Length > 0) sbList.Append(" | ");
                sbList.Append(recent[i]);
            }
            env.recent_windows = sbList.ToString();

            env.user_idle_sec = idle;
            env.user_state = DescribeIdle(idle);
            env.fullscreen_app = fullscreen;
            env.mouse_region = MouseRegion();
            env.pet_action = LastPetAction;

            var brain = FindBrain();
            env.since_pet_touched_sec = brain != null ? brain.GetIdleSeconds() : 0f;

            if (verbose) Debug.Log("[PetContext] " + Readable());
        }

        static string DescribeIdle(float s)
        {
            if (s < 60f) return "正在用电脑";
            if (s < 300f) return "刚停下来，可能在发呆或看屏幕";
            if (s < 1800f) return "离开了一会儿（半小时内）";
            if (s < 7200f) return "离开很久了（半小时到两小时）";
            return "大概出门或者睡觉去了（超过两小时）";
        }

    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        static float GetIdleSeconds()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(lii);
            if (!GetLastInputInfo(ref lii)) return 0f;
            uint now = (uint)Environment.TickCount;
            uint d = unchecked(now - lii.dwTime);
            return d / 1000f;
        }

        static string WindowTitle(IntPtr h)
        {
            int len = GetWindowTextLengthW(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 2);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static bool IsFullscreen(IntPtr h)
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            Resolution res = Screen.currentResolution;
            return w >= res.width - 8 && hh >= res.height - 8 && w > 0 && hh > 0;
        }
    #endif

        string MouseRegion()
        {
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            POINT p;
            if (!GetCursorPos(out p)) return "未知";
            int w = Screen.currentResolution.width, h = Screen.currentResolution.height;
            if (w <= 0 || h <= 0) return "未知";
            int cx = Mathf.Clamp((int)(p.x * 3f / w), 0, 2);
            int cy = Mathf.Clamp((int)(p.y * 3f / h), 0, 2);
            string[] cols = { "左", "中", "右" };
            string[] rows = { "上", "中", "下" };
            return rows[cy] + cols[cx];
    #else
            return "未知";
    #endif
        }

        PetBrain FindBrain()
        {
    #if UNITY_2022_2_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<PetBrain>();
    #else
            return UnityEngine.Object.FindObjectOfType<PetBrain>();
    #endif
        }

        /// <summary>给云端模型的 JSON 快照。</summary>
        public string ToJson()
        {
            env.system_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            env.pet_action = LastPetAction;
            return JsonUtility.ToJson(env);
        }

        /// <summary>给日志/人类看的一行摘要。</summary>
        public string Readable()
        {
            return "时间=" + env.system_time
                 + " | 前台窗口=「" + env.active_window + "」已停留 " + Mathf.RoundToInt(env.active_window_dwell_sec) + "s"
                 + " | 全屏应用=" + (env.fullscreen_app ? "是" : "否")
                 + " | 用户闲置=" + Mathf.RoundToInt(env.user_idle_sec) + "s（" + env.user_state + "）"
                 + " | 光标=" + env.mouse_region
                 + " | 它在做=" + env.pet_action
                 + " | 对方多久没摸它=" + Mathf.RoundToInt(env.since_pet_touched_sec) + "s";
        }

        void OnApplicationQuit() { Instance = null; }
    }
}
