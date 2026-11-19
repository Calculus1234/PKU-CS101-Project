using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【MCP 客户端（stdio）】—— 拉起一个本地 MCP Server，用 JSON-RPC 2.0 通信。
    ///
    /// 流程：initialize → notifications/initialized → tools/list → （按需）tools/call
    /// 第一版只做 stdio（换行分帧），不碰 HTTP/SSE —— 对桌宠来说本地 Python / 文件系统 /
    /// Blender 这些 server 已经覆盖 99% 的场景。
    ///
    /// ⚠️ 读管道必须在【后台线程】做：`ReadLine()` 是阻塞的，放主线程会冻住整个游戏
    ///    （和"忙等冻结游戏循环"是同一类坑）。这里用线程 + 队列 + 协程轮询。
    /// </summary>
    public class McpStdioSource : IToolSource
    {
        public string SourceId { get { return "mcp:" + _name; } }
        public List<ToolDescriptor> Tools { get; private set; }

        readonly JObject cfg;
        readonly string prefix;
        readonly float defTimeout;
        readonly int defMaxChars;
        readonly bool defConfirm;
        readonly string[] defRoots;

        string _name, command, cwd;
        string[] args;
        string[] allowList;

        System.Diagnostics.Process proc;
        Thread reader;
        readonly Queue<string> incoming = new Queue<string>();
        readonly object gate = new object();
        int nextId = 10;
        bool alive;

        readonly Dictionary<string, string> originalName = new Dictionary<string, string>();

        public McpStdioSource(JObject entry, string prefix, float timeoutSec, int maxChars, bool confirm, string[] roots)
        {
            cfg = entry;
            this.prefix = prefix ?? "";
            this.defTimeout = timeoutSec;
            this.defMaxChars = maxChars;
            this.defConfirm = confirm;
            this.defRoots = roots;
            Tools = new List<ToolDescriptor>();

            string rawName = (string)entry["name"];
            if (string.IsNullOrEmpty(rawName)) rawName = "mcp";
            _name = rawName;

            command = (string)entry["command"];
            cwd = (string)entry["cwd"];
            if (string.IsNullOrEmpty(cwd)) cwd = PetPathUtil.ExeDir();
            var a = entry["args"] as JArray;
            if (a != null)
            {
                var list = new List<string>();
                for (int i = 0; i < a.Count; i++) list.Add((string)a[i]);
                args = list.ToArray();
            }
            var al = entry["allow"] as JArray;
            if (al != null)
            {
                var list = new List<string>();
                for (int i = 0; i < al.Count; i++) { string s = (string)al[i]; if (!string.IsNullOrEmpty(s)) list.Add(s); }
                if (list.Count > 0) allowList = list.ToArray();
            }
        }

        // ---------------- 生命周期 ----------------

        bool StartProcess()
        {
            if (proc != null && alive) return true;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = ToolExecutor.ResolveCommand(command);   // "python" → 便携版 python.exe
                psi.Arguments = ToolExecutor.BuildArgs(args);
                if (!string.IsNullOrEmpty(cwd) && System.IO.Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                alive = true;

                reader = new Thread(ReadLoop);
                reader.IsBackground = true;
                reader.Start();

                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Debug.Log("[MCP:" + _name + "] " + e.Data); };
                try { proc.BeginErrorReadLine(); } catch { }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[McpStdioSource] 启动失败：" + e.Message);
                return false;
            }
        }

        void ReadLoop()
        {
            try
            {
                while (true)
                {
                    string line = proc != null ? proc.StandardOutput.ReadLine() : null;
                    if (line == null) break;
                    if (line.Trim().Length == 0) continue;
                    lock (gate) incoming.Enqueue(line);
                }
            }
            catch { }
            alive = false;
        }

        void Send(JObject msg)
        {
            if (proc == null) return;
            try
            {
                proc.StandardInput.WriteLine(msg.ToString(Formatting.None));
                proc.StandardInput.Flush();
            }
            catch (Exception e) { Debug.LogWarning("[McpStdioSource] 写管道失败：" + e.Message); }
        }

        IEnumerator WaitResponse(int id, float timeout, Action<JObject> done)
        {
            float t = 0f;
            while (t < Mathf.Max(3f, timeout))
            {
                string line = null;
                lock (gate) { if (incoming.Count > 0) line = incoming.Dequeue(); }
                if (line != null)
                {
                    JObject jo = null;
                    try { jo = JObject.Parse(line); } catch { }
                    if (jo != null)
                    {
                        var idTok = jo["id"];
                        if (idTok != null && idTok.Type == JTokenType.Integer && (int)idTok == id)
                        {
                            done(jo);
                            yield break;
                        }
                        // 通知（没有 id）或别人的响应：忽略
                    }
                    continue;   // 不消耗等待时间
                }
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            done(null);
        }

        // ---------------- 初始化：握手 + 拉工具清单 ----------------

        public IEnumerator Initialize(ToolRegistry registry)
        {
            Tools.Clear();
            originalName.Clear();

            if (!StartProcess())
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " 没起来，跳过");
                yield break;
            }

            // ① initialize
            int id1 = nextId++;
            var init = new JObject();
            init["jsonrpc"] = "2.0";
            init["id"] = id1;
            init["method"] = "initialize";
            var ps = new JObject();
            ps["protocolVersion"] = "2024-11-05";
            ps["capabilities"] = new JObject();
            var ci = new JObject(); ci["name"] = "DesktopPetFramework"; ci["version"] = "2.0";
            ps["clientInfo"] = ci;
            init["params"] = ps;
            Send(init);

            JObject resp = null;
            yield return WaitResponse(id1, 15f, r => resp = r);
            if (resp == null)
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " initialize 超时");
                yield break;
            }

            // ② notifications/initialized（没有 id，不需要等）
            var ready = new JObject();
            ready["jsonrpc"] = "2.0";
            ready["method"] = "notifications/initialized";
            Send(ready);

            yield return new WaitForSecondsRealtime(0.1f);

            // ③ tools/list
            int id2 = nextId++;
            var list = new JObject();
            list["jsonrpc"] = "2.0";
            list["id"] = id2;
            list["method"] = "tools/list";
            list["params"] = new JObject();
            Send(list);

            JObject listResp = null;
            yield return WaitResponse(id2, 15f, r => listResp = r);
            if (listResp == null)
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " tools/list 超时");
                yield break;
            }

            var toolsArr = listResp["result"] != null ? listResp["result"]["tools"] as JArray : null;
            if (toolsArr == null)
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " 没有返回工具清单：" + ToolExecutor.Truncate(listResp.ToString(Formatting.None), 300));
                yield break;
            }

            for (int i = 0; i < toolsArr.Count; i++)
            {
                var t = toolsArr[i] as JObject;
                if (t == null) continue;
                string orig = (string)t["name"];
                if (string.IsNullOrEmpty(orig)) continue;
                if (allowList != null && Array.IndexOf(allowList, orig) < 0) continue;

                string exposed = prefix + orig;
                originalName[exposed] = orig;

                var d = new ToolDescriptor();
                d.name = exposed;
                d.title = (string)t["title"] ?? orig;
                d.description = (string)t["description"] ?? "";
                d.parametersJson = t["inputSchema"] != null
                    ? t["inputSchema"].ToString(Formatting.None)
                    : "{\"type\":\"object\",\"properties\":{}}";
                d.kind = ToolKind.Mcp;
                d.mode = ToolMode.Agent;
                d.sourceId = SourceId;
                d.timeoutSec = cfg.Value<float?>("timeoutSec") ?? defTimeout;
                d.maxOutputChars = cfg.Value<int?>("maxOutputChars") ?? defMaxChars;
                d.requireConfirm = cfg.Value<bool?>("requireConfirm") ?? defConfirm;
                d.enabled = true;
                d.safety.allowedRoots = defRoots;
                Tools.Add(d);
            }

            Debug.Log("[McpStdioSource] ★ " + _name + " 注册了 " + Tools.Count + " 个工具");
        }

        // ---------------- 调用 ----------------

        public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
        {
            bool allow = false;
            yield return ToolExecutor.Confirm(tool, argsJson, a => allow = a);
            if (!allow) { done(ToolResult.Fail("用户拒绝了这次工具调用", tool.mode)); yield break; }

            string badPath = ProcessToolSource.FindDisallowedPath(argsJson, tool.safety.allowedRoots);
            if (!string.IsNullOrEmpty(badPath))
            {
                done(ToolResult.Fail("路径不在允许范围内：" + badPath, tool.mode));
                yield break;
            }

            if (!alive && !StartProcess())
            {
                done(ToolResult.Fail("MCP Server 没在运行，且重启失败", tool.mode));
                yield break;
            }

            string orig;
            if (!originalName.TryGetValue(tool.name, out orig)) orig = tool.name;

            int id = nextId++;
            var call = new JObject();
            call["jsonrpc"] = "2.0";
            call["id"] = id;
            call["method"] = "tools/call";
            var ps = new JObject();
            ps["name"] = orig;
            try { ps["arguments"] = JToken.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson); }
            catch { ps["arguments"] = new JObject(); }
            call["params"] = ps;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Send(call);

            JObject resp = null;
            yield return WaitResponse(id, tool.timeoutSec, r => resp = r);
            sw.Stop();

            var res = new ToolResult { mode = tool.mode, elapsedSec = (float)sw.Elapsed.TotalSeconds };
            if (resp == null)
            {
                res.ok = false; res.error = "MCP 调用超时（" + tool.timeoutSec + "s）"; res.display = res.error;
                done(res);
                yield break;
            }

            if (resp["error"] != null)
            {
                res.ok = false;
                res.error = resp["error"]["message"] != null ? (string)resp["error"]["message"] : resp["error"].ToString(Formatting.None);
                res.display = res.error;
                done(res);
                yield break;
            }

            var result = resp["result"] as JObject;
            if (result != null && result["content"] is JArray)
            {
                var sb = new StringBuilder();
                var content = (JArray)result["content"];
                for (int i = 0; i < content.Count; i++)
                {
                    var c = content[i] as JObject;
                    if (c == null) continue;
                    string type = (string)c["type"];
                    if (type == "text") { if (sb.Length > 0) sb.Append('\n'); sb.Append((string)c["text"]); }
                    else sb.Append("\n[").Append(type).Append(" 内容已省略]");
                }
                res.ok = !(result.Value<bool?>("isError") ?? false);
                res.resultJson = ToolExecutor.Truncate(sb.ToString(), tool.maxOutputChars);
                res.display = res.resultJson;
                if (!res.ok) res.error = res.resultJson;
            }
            else
            {
                res.ok = true;
                res.resultJson = result != null ? result.ToString(Formatting.None) : "{}";
                res.display = res.resultJson;
            }
            if (string.IsNullOrEmpty(res.display)) res.display = "（完成）";
            done(res);
        }

        void Shutdown()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { if (proc != null) proc.Dispose(); } catch { }
            proc = null; alive = false;
        }
    }
}
