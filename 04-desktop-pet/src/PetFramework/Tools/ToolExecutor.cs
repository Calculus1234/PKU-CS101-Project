using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PetFramework.Tools
{
    public class ProcessOut
    {
        public bool started;
        public int exitCode = -999;
        public string stdout = "";
        public string stderr = "";
        public bool timedOut;
        public string startError;
    }

    /// <summary>
    /// 【工具执行器】—— 所有外部工具共用的安全底座。
    ///
    /// 负责四件事（全部是踩过坑才加上的）：
    ///   ① 超时 + 杀进程树：脚本挂死不能把整只宠物拖住
    ///   ② 输出截断：避免把上下文撑爆
    ///   ③ 路径白名单：防止提示词注入写到系统目录
    ///   ④ 确认弹窗：模型不许悄无声息地跑本地进程
    ///
    /// ⚠️ 所有等待都用协程 `yield return null`，**绝不 WaitForExit / Thread.Sleep**，
    ///    否则会冻结整个游戏循环（这个坑在别处已经踩过）。
    /// </summary>
    public static class ToolExecutor
    {
        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (max <= 0) max = 4000;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n…（输出过长，已截断 " + (s.Length - max) + " 字）";
        }

        /// <summary>路径是否落在白名单里。roots 为空 = 不限制。</summary>
        public static bool PathAllowed(string path, string[] roots)
        {
            if (roots == null || roots.Length == 0) return true;
            if (string.IsNullOrEmpty(path)) return true;
            string full;
            try { full = System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }
            catch { return false; }

            for (int i = 0; i < roots.Length; i++)
            {
                string r = roots[i];
                if (string.IsNullOrEmpty(r)) continue;
                string root;
                try { root = System.IO.Path.GetFullPath(r).Replace('\\', '/').TrimEnd('/'); }
                catch { continue; }
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
                if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>需要确认的工具先问一句。返回 true = 允许执行。</summary>
        public static IEnumerator Confirm(ToolDescriptor tool, string argsJson, Action<bool> done)
        {
            if (tool == null || !tool.requireConfirm) { done(true); yield break; }

            bool answered = false, allow = false;
            ToolConfirmUI.Ask(tool, argsJson, a => { answered = true; allow = a; });

            float waited = 0f;
            while (!answered && waited < 120f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!answered)
            {
                Debug.LogWarning("[ToolExecutor] 确认超时（120s），按拒绝处理：" + tool.name);
                ToolConfirmUI.Cancel();
            }
            done(allow);
        }

        // ---------------- 便携版 Python ----------------

        static string cachedPython;
        static bool pythonLooked;

        /// <summary>
        /// 找 EXE 旁边 / 上一级的 `PythonEnv\python.exe`。
        /// 打包后整份 Python 跟着 EXE 走 ⇒ 目标机器【不需要装 Python】也能跑外部工具和 TTS。
        /// </summary>
        public static string FindPortablePython()
        {
            if (pythonLooked) return string.IsNullOrEmpty(cachedPython) ? null : cachedPython;
            pythonLooked = true;
            try
            {
                string exe = PetPathUtil.ExeDir();
                string up = null;
                try { up = System.IO.Path.GetDirectoryName(exe); } catch { }

                string[] dirs = { exe, up };
                for (int i = 0; i < dirs.Length; i++)
                {
                    if (string.IsNullOrEmpty(dirs[i])) continue;
                    string p = System.IO.Path.Combine(dirs[i], "PythonEnv", "python.exe");
                    if (System.IO.File.Exists(p)) { cachedPython = p; return p; }
                }
            }
            catch { }
            return null;
        }

        /// <summary>在系统 PATH 里找一个可执行文件。</summary>
        public static string FindOnPath(string exeName)
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
                        string p = System.IO.Path.Combine(dir, exeName);
                        if (System.IO.File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>把配置里的 "python" 解析成真能跑的 python.exe（便携版优先，其次 PATH）。</summary>
        public static string ResolveCommand(string file)
        {
            if (string.IsNullOrEmpty(file)) return file;
            if (file.IndexOf('\\') >= 0 || file.IndexOf('/') >= 0) return file;   // 已经写死路径

            string bare = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (bare != "python" && bare != "python3" && bare != "pythonw" && bare != "py") return file;

            string p = FindPortablePython();
            if (!string.IsNullOrEmpty(p)) return p;
            p = FindOnPath("python.exe");
            if (!string.IsNullOrEmpty(p)) return p;
            return file;   // 交给系统按 PATH 解析
        }

        // ---------------- 进程 ----------------

        static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOf(' ') < 0 && s.IndexOf('\t') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        public static string BuildArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Quote(args[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 跑一个进程：写 stdin（可选），收 stdout/stderr，超时就杀掉整棵进程树。
        /// </summary>
        public static IEnumerator RunProcess(string file, string[] args, string cwd, string stdin,
                                             float timeoutSec, int maxChars, Action<ProcessOut> done)
        {
            var result = new ProcessOut();
            if (string.IsNullOrEmpty(file)) { result.startError = "没有配置 command"; done(result); yield break; }
            file = ResolveCommand(file);
            if (!System.IO.File.Exists(file))
            {
                // 允许直接给命令名（比如 python、npx），交给系统 PATH 解析
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (file.IndexOf('\\') >= 0 || file.IndexOf('/') >= 0)
                {
                    result.startError = "找不到可执行文件：" + file;
                    done(result);
                    yield break;
                }
                file = name;
            }

            System.Diagnostics.Process p = null;
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            object gate = new object();

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = BuildArgs(args);
                if (!string.IsNullOrEmpty(cwd) && System.IO.Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
                psi.UseShellExecute = false;     // 绝不经过 cmd.exe，杜绝命令拼接注入
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                p = System.Diagnostics.Process.Start(psi);
                if (p == null) { result.startError = "Process.Start 返回 null"; done(result); yield break; }
                result.started = true;

                p.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (gate) sbOut.AppendLine(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (gate) sbErr.AppendLine(e.Data); } };
                try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }

                try
                {
                    if (!string.IsNullOrEmpty(stdin)) p.StandardInput.Write(stdin);
                    p.StandardInput.Close();
                }
                catch { }
            }
            catch (Exception e)
            {
                result.startError = e.Message;
                done(result);
                yield break;
            }

            float t = 0f;
            bool exited = false;
            while (t < Mathf.Max(1f, timeoutSec))
            {
                try { if (p.HasExited) { exited = true; break; } } catch { exited = true; break; }
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!exited)
            {
                result.timedOut = true;
                KillTree(p);
                yield return new WaitForSecondsRealtime(0.2f);
            }
            else
            {
                // 等异步读把尾巴收完（否则最后一行经常丢）
                yield return null;
                yield return null;
            }

            try { if (p.HasExited) result.exitCode = p.ExitCode; } catch { }
            lock (gate)
            {
                result.stdout = Truncate(sbOut.ToString(), maxChars);
                result.stderr = Truncate(sbErr.ToString(), maxChars);
            }
            try { p.Dispose(); } catch { }
            done(result);
        }

        static void KillTree(System.Diagnostics.Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            // Mono 上不一定有 Kill(entireProcessTree)，用系统命令兜底
            try
            {
                var tk = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "taskkill.exe");
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = tk;
                psi.Arguments = "/PID " + p.Id + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }
}
