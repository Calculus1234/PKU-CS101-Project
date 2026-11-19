using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PetFramework
{
    /// <summary>
    /// 本地 Edge TTS 客户端（弃用 Windows SAPI，避免打包后缺微软语音库报错）。
    /// 协程 POST 到 http://127.0.0.1:8000/tts，请求体含 text/voice/rate/pitch，
    /// 收到音频流后在 Unity 播放，播放前触发 Talk 动画；带超时重试（默认 3 次，防刚开机服务未起）。
    /// </summary>
    public class TTS : MonoBehaviour
    {
        public static TTS Instance { get; private set; }

        [Header("本地 Edge TTS 服务")]
        public string url = "http://127.0.0.1:8000/tts";
        public string voice = "zh-CN-YunxiNeural";   // 少年感、活泼男声
        public string rate = "+8%";                 // 语速（Edge 字符串格式，如 +8% / -10%）
        public string pitch = "+12Hz";              // 音调（Edge 字符串格式，如 +12Hz）
        public AudioType audioType = AudioType.MPEG; // Edge TTS 默认输出 mp3；若服务返回 wav 改 AudioType.WAV

        [Header("重试 / 超时")]
        public int maxRetry = 3;
        public float retryDelay = 1.5f;
        public float timeoutSec = 15f;

        [Header("自动拉起本地服务（开机忘了开 Python 也能自己救活）")]
        public bool autoStartServer = true;
        [Tooltip("留空 = 自动找：① EXE 旁的 PythonEnv\\python.exe（便携版）② 系统 PATH 里的 python.exe ③ D:\\python\\python.exe。"
               + "必须是 python.exe，不要用 pythonw.exe —— 实测隐藏窗口启动时 pythonw 会立刻 exit 1")]
        public string pythonPath = "";
        [Tooltip("留空 = 自动找：EXE 旁的 PythonEnv\\tts_server.py，其次 D:\\EdgeTTS\\tts_server.py")]
        public string serverScript = "";
        public string serverWorkDir = "";
        [Tooltip("拉起后最多等多少秒让它监听端口")]
        public int startWaitSec = 10;

        AudioSource src;
        PetBrain brain;
        bool probeOk;
        bool serverNotified;      // 一轮只提醒一次，别反复弹
        bool sapiMode;            // 没有可用的 Python ⇒ 退回 Windows 自带语音，别每句都干等三次超时

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            src = GetComponent<AudioSource>();
            if (src == null) src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;   // 2D 音频
            if (string.IsNullOrEmpty(rate)) rate = "+8%";      // 场景序列化丢失时兜底
            if (string.IsNullOrEmpty(pitch)) pitch = "+12Hz";
            if (string.IsNullOrEmpty(voice)) voice = "zh-CN-YunxiNeural";
            if (brain == null) brain = FindObjectOfType<PetBrain>();
        }

        [System.Serializable]
        class TTSReq { public string text; public string voice; public string rate; public string pitch; }

        // ==================== 启动时确认服务活着，不在就自己拉起来 ====================

        void Start()
        {
            if (autoStartServer) StartCoroutine(EnsureServer());
        }

        /// <summary>
        /// ① 先探一下 127.0.0.1:8000 在不在
        /// ② 不在 → 用 pythonw.exe 静默拉起 tts_server.py（无控制台窗口）
        /// ③ 等最多 startWaitSec 秒再探
        /// ④ 还是不在 → 在聊天框里告诉主人
        /// </summary>
        IEnumerator EnsureServer()
        {
            yield return null;                      // 让别的组件先初始化

            yield return ProbeRoutine();
            if (probeOk) { Debug.Log("[TTS] 服务已在运行 ✓"); yield break; }

            // ★ 便携版优先：EXE 旁边的 PythonEnv\python.exe，其次系统 PATH，最后老路径 D:\python
            string py = ResolvePython();
            string script = ResolveServerScript();
            if (string.IsNullOrEmpty(py) || string.IsNullOrEmpty(script))
            {
                Debug.LogWarning("[TTS] 没找到可用的 Python / tts_server.py（Python=" + (py ?? "null")
                                 + " script=" + (script ?? "null") + "）→ 退回 Windows 自带语音");
                sapiMode = true;
                NotifyDead(true);
                yield break;
            }
            pythonPath = py;
            serverScript = script;

            Debug.LogWarning("[TTS] 连不上本地 TTS 服务，尝试自动拉起：" + pythonPath + " " + serverScript);
            string err = TryStartServer();
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogError("[TTS] 自动拉起失败：" + err + " → 退回 Windows 自带语音");
                sapiMode = true;
                NotifyDead(true);
                yield break;
            }

            // 等服务监听端口（刚启动的 Python 要几秒）
            float waited = 0f;
            while (waited < startWaitSec)
            {
                yield return new WaitForSeconds(1f);
                waited += 1f;
                yield return ProbeRoutine();
                if (probeOk)
                {
                    Debug.Log("[TTS] ✅ 已自动拉起 TTS 服务（" + waited.ToString("0") + " 秒后就绪）");
                    yield break;
                }
            }

            Debug.LogError("[TTS] 拉起了进程，但 " + startWaitSec + " 秒内一直连不上 → 退回 Windows 自带语音");
            sapiMode = true;
            NotifyDead(true);
        }

        // ==================== 路径解析（便携版优先） ====================

        /// <summary>EXE 旁边 / 上一级的 PythonEnv\python.exe —— 打包后随身带着走的那份。</summary>
        public static string ResolvePortablePython()
        {
            var files = new string[] { "python.exe", "pythonw.exe" };
            string[] dirs = new string[]
            {
                System.IO.Path.Combine(PetPathUtil.ExeDir(), "PythonEnv"),
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PetPathUtil.ExeDir()) ?? "", "PythonEnv")
            };
            for (int d = 0; d < dirs.Length; d++)
                for (int f = 0; f < files.Length; f++)
                {
                    try { string p = System.IO.Path.Combine(dirs[d], files[f]); if (System.IO.File.Exists(p)) return p; }
                    catch { }
                }
            return null;
        }

        /// <summary>在系统 PATH 里找 python.exe（用户自己装过 Python 的情况）。</summary>
        public static string ResolveSystemPython()
        {
            try
            {
                string path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] parts = path.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string dir = (parts[i] ?? "").Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    try
                    {
                        string p = System.IO.Path.Combine(dir, "python.exe");
                        if (System.IO.File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>最终用哪个 Python：显式配置 → 便携版 → 系统 PATH → 老路径。</summary>
        public string ResolvePython()
        {
            if (!string.IsNullOrEmpty(pythonPath) && System.IO.File.Exists(pythonPath)) return pythonPath;
            string p = ResolvePortablePython();
            if (!string.IsNullOrEmpty(p)) return p;
            p = ResolveSystemPython();
            if (!string.IsNullOrEmpty(p)) return p;
            if (System.IO.File.Exists("D:\\python\\python.exe")) return "D:\\python\\python.exe";
            return null;
        }

        public string ResolveServerScript()
        {
            if (!string.IsNullOrEmpty(serverScript) && System.IO.File.Exists(serverScript)) return serverScript;

            string[] cands = new string[]
            {
                System.IO.Path.Combine(PetPathUtil.ExeDir(), "PythonEnv", "tts_server.py"),
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PetPathUtil.ExeDir()) ?? "", "PythonEnv", "tts_server.py"),
                "D:\\EdgeTTS\\tts_server.py"
            };
            for (int i = 0; i < cands.Length; i++)
            {
                try { if (System.IO.File.Exists(cands[i])) return cands[i]; }
                catch { }
            }
            return null;
        }

        /// <summary>探活：能拿到 HTTP 响应（哪怕 404）就算服务活着；连接被拒才算死。</summary>
        IEnumerator ProbeRoutine()
        {
            probeOk = false;
            string probe = url;
            int slash = probe.IndexOf("/", "http://".Length);
            if (slash > 0) probe = probe.Substring(0, slash);      // 只探域名根，不挑端点

            using (var www = UnityWebRequest.Get(probe))
            {
                www.timeout = 3;
                yield return www.SendWebRequest();
                probeOk = www.responseCode != 0;                    // 0 = 根本没连上
            }
        }

        /// <summary>静默拉起服务。成功返回空字符串，失败返回原因。</summary>
        string TryStartServer()
        {
            try
            {
                // ★★ 必须用 python.exe，**绝不能用 pythonw.exe**。
                //    实测（2026-10-18）：WshShell.Run 隐藏窗口启动 pythonw → 立刻 exit 1、无进程；
                //    同样写法换 python.exe → 正常常驻。pythonw 是 GUI 子系统程序，
                //    在没有控制台/事件循环的环境里会静默死掉 —— 这正是"开机后 TTS 打不开"的根因。
                //    CreateNoWindow=true 同样不会弹黑框，所以 python.exe 没有任何副作用。
                if (!System.IO.File.Exists(pythonPath)) return "找不到 Python：" + pythonPath;
                if (!System.IO.File.Exists(serverScript)) return "找不到脚本：" + serverScript;

                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = pythonPath;
                psi.Arguments = "\"" + serverScript + "\"";
                psi.WorkingDirectory = System.IO.Directory.Exists(serverWorkDir)
                    ? serverWorkDir
                    : System.IO.Path.GetDirectoryName(serverScript);
                psi.UseShellExecute = false;        // 不弹 shell
                psi.CreateNoWindow = true;          // 无控制台窗口
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

                // 给它有效的 stdio 管道并持续抽干 —— 否则日志写不出去或管道写满会卡住子进程
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
                }
                Debug.Log("[TTS] 已拉起 Python：" + pythonPath + " " + serverScript);
                return "";
            }
            catch (System.Exception e) { return e.Message; }
        }

        /// <summary>实在救不活 → 在聊天框里说一声。
        /// degraded=true 表示已经退回 Windows 自带语音（还能说话，只是音色变普通）。</summary>
        void NotifyDead(bool degraded)
        {
            if (serverNotified) return;
            serverNotified = true;

            var chat = FindObjectOfType<ChatUI>();
            string who = PetProfileConfig.UserName;
            if (chat != null)
            {
                chat.ShowNotice(degraded
                    ? who + "，Edge 语音服务没起来，我先用系统自带的嗓音凑合一下。"
                    : who + "，我的嗓子（TTS服务）好像卡住了，请检查一下。");
            }
            else Debug.LogWarning("[TTS] 找不到 ChatUI，无法显示提示");
        }

        // ==================== Windows 自带语音（SAPI）兜底 ====================
        //
        // ⚠️ System.Speech 在 .NET Standard 2.1 里【不存在】，所以走 COM:
        //    Type.GetTypeFromProgID("SAPI.SpVoice") —— Windows 自带，无需装任何东西。
        // ⚠️ Speak() 是同步阻塞的（读完才返回），必须丢到线程池，
        //    否则整个游戏循环会跟着卡住（这项目已经踩过"忙等冻结"的坑）。

        static void SpeakSapi(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var t = System.Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (t == null)
                    {
                        Debug.LogWarning("[TTS] 这台机器没有 SAPI.SpVoice，彻底没法发声");
                        return;
                    }
                    object voice = System.Activator.CreateInstance(t);
                    t.InvokeMember("Speak",
                        System.Reflection.BindingFlags.InvokeMethod, null, voice, new object[] { text });
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[TTS] SAPI 朗读失败：" + e.Message);
                }
            });
        }

        /// <summary>对外入口：朗读一段文本（自动创建 TTS 单例、触发 Talk 动画）。</summary>
        public static void Speak(string text) { Speak(text, true); }

        /// <summary>
        /// 朗读一段文本。
        /// playTalkAnim=false：只出声、不动姿势 —— 当模型同时调了 play_animation(Sit/Sleep)
        /// 又要说话时用这个，否则 Talk 动画会把刚设好的坐/睡顶掉。
        /// </summary>
        public static void Speak(string text, bool playTalkAnim)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Instance == null)
            {
                var go = new GameObject("TTS");
                Instance = go.AddComponent<TTS>();
            }
            Instance.StartCoroutine(Instance.Routine(text, playTalkAnim));
        }

        IEnumerator Routine(string text, bool playTalkAnim)
        {
            if (brain == null) brain = FindObjectOfType<PetBrain>();
            // 说话切 Talk，但【保持姿势（坐/睡）期间不切】—— 回复和动作不该互相拆台
            if (playTalkAnim && brain != null && !brain.IsHoldingPose) brain.PlayAction("Talk");

            if (sapiMode) { SpeakSapi(text); yield break; }

            var req = new TTSReq { text = text, voice = voice, rate = rate, pitch = pitch };
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(req));

            for (int attempt = 1; attempt <= maxRetry; attempt++)
            {
                using (var www = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    www.uploadHandler = new UploadHandlerRaw(payload);
                    www.downloadHandler = new DownloadHandlerAudioClip(url, audioType);
                    www.SetRequestHeader("Content-Type", "application/json");
                    www.timeout = Mathf.CeilToInt(timeoutSec);
                    yield return www.SendWebRequest();

                    if (www.result == UnityWebRequest.Result.Success)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(www);
                        if (clip != null)
                        {
                            if (src.isPlaying) src.Stop();
                            src.clip = clip;
                            src.Play();
                            Debug.Log("[TTS] 播放语音 " + clip.length.ToString("0.0") + "s: " + text);
                            yield break;
                        }
                        Debug.LogWarning("[TTS] 第 " + attempt + " 次：返回音频为空");
                    }
                    else
                    {
                        Debug.LogWarning("[TTS] 第 " + attempt + "/" + maxRetry + " 次请求失败：" + www.responseCode + " " + www.error + "（本地服务可能还没启动）");
                    }
                }
                yield return new WaitForSeconds(retryDelay);
            }
            Debug.LogWarning("[TTS] 重试 " + maxRetry + " 次仍失败 → 这一句改用系统语音");
            SpeakSapi(text);
        }
    }
}
