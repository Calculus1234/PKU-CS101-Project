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
            // 每个 MCP 服务可以用 "prefix" 单独覆盖默认前缀。
            // ★ 为什么必须加前缀：不加的话 Blender 的 look 在模型眼里就叫「look」，
            //   4B 模型想"看一眼屏幕"时就会误挑它（作者实测踩过）。
            //   加上之后是 blender_look / unity_manage_scene，语义一眼就清楚。
            this.prefix = (string)entry["prefix"] ?? (prefix ?? "");
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

            bool ok = false;
            for (int attempt = 1; attempt <= 2 && !ok; attempt++)
            {
                if (!StartProcess())
                {
                    Debug.LogWarning("[McpStdioSource] " + _name + " 启动失败（第 " + attempt + " 次）");
                    yield return new WaitForSecondsRealtime(1.5f);
                    continue;
                }
                yield return Handshake(r => ok = r);
                if (!ok)
                {
                    Debug.LogWarning("[McpStdioSource] " + _name + " 握手失败（第 " + attempt + " 次），重启重试…");
                    Shutdown();
                    yield return new WaitForSecondsRealtime(2f);
                }
            }

            if (ok)
            {
                SaveCache();
                Debug.Log("[McpStdioSource] ★ " + _name + " 注册了 " + Tools.Count + " 个工具");
                yield break;
            }

            if (LoadCache())
                Debug.LogWarning("[McpStdioSource] ⚠ " + _name + " 连不上，先用缓存的 " + Tools.Count + " 个工具（可能不是最新）");
            else
                Debug.LogWarning("[McpStdioSource] ✗ " + _name + " 加载失败，且没有缓存");
        }

        /// <summary>握手 + 拉工具清单。成功返回 true（工具已填进 Tools）。</summary>
        IEnumerator Handshake(Action<bool> done)
        {
            // ① initialize（uvx 冷启动可能要几十秒，给足 60s）
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
            yield return WaitResponse(id1, 60f, r => resp = r);
            if (resp == null) { done(false); yield break; }

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
            yield return WaitResponse(id2, 45f, r => listResp = r);
            if (listResp == null)
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " tools/list 超时");
                done(false); yield break;
            }

            var toolsArr = listResp["result"] != null ? listResp["result"]["tools"] as JArray : null;
            if (toolsArr == null)
            {
                Debug.LogWarning("[McpStdioSource] " + _name + " 没有返回工具清单：" + ToolExecutor.Truncate(listResp.ToString(Formatting.None), 300));
                done(false); yield break;
            }

            BuildTools(toolsArr);
            done(Tools.Count > 0);
        }

        void BuildTools(JArray toolsArr)
        {
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

                // ★ 描述前面标清楚来源：不加的话，Blender 的 look 写成
                //   "See the scene as one image"，4B 模型会当成"看一眼主人的屏幕"。
                string srcLabel = (string)cfg["title"];
                if (string.IsNullOrEmpty(srcLabel)) srcLabel = _name;
                d.description = "[" + srcLabel + " 工具·不是对方的电脑桌面] "
                                + ((string)t["description"] ?? "");

                d.parametersJson = t["inputSchema"] != null
                    ? t["inputSchema"].ToString(Formatting.None)
                    : "{\"type\":\"object\",\"properties\":{}}";
                d.kind = ToolKind.Mcp;
                d.mode = ToolMode.Agent;
                d.sourceId = SourceId;
                d.timeoutSec = cfg.Value<float?>("timeoutSec") ?? defTimeout;
                d.maxOutputChars = cfg.Value<int?>("maxOutputChars") ?? defMaxChars;

                // ---- 只读判定（三条来源，任一命中即可）----
                bool readOnly = false;
                // ① MCP 官方注解
                var ann = t["annotations"] as JObject;
                if (ann != null && (ann.Value<bool?>("readOnlyHint") ?? false)) readOnly = true;
                // ② 配置里显式列出的只读工具（原始名）
                var roList = cfg["readOnlyTools"] as JArray;
                if (!readOnly && roList != null)
                {
                    for (int k = 0; k < roList.Count; k++)
                        if ((string)roList[k] == orig) { readOnly = true; break; }
                }
                // ③ 名字启发式（look / get_* / list_* / read_* / find_* / search_* …）
                if (!readOnly && IsReadOnlyName(orig)) readOnly = true;
                d.readOnly = readOnly;

                // 只读工具不弹窗；修改类照旧按配置询问
                bool req = cfg.Value<bool?>("requireConfirm") ?? defConfirm;
                d.requireConfirm = req && !readOnly;

                d.enabled = true;
                d.safety.allowedRoots = defRoots;
                Tools.Add(d);
            }
        }

        /// <summary>
        /// 名字启发式：只读工具不弹窗。
        /// ⚠️ 只做前缀/精确匹配，别用 Contains —— 否则 "create_look" 这种也会被误判。
        /// </summary>
        static bool IsReadOnlyName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            string s = n.ToLowerInvariant();

            // 精确名
            if (s == "look" || s == "status" || s == "info" || s == "ping" ||
                s == "stats" || s == "screenshot" || s == "list") return true;

            // 前缀名
            string[] prefixes =
            {
                "get_", "list_", "read_", "find_", "search_", "query_",
                "describe_", "preview_", "count_", "inspect_"
            };
            for (int i = 0; i < prefixes.Length; i++)
                if (s.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string CachePath
        {
            get
            {
                string dir = System.IO.Path.Combine(PetPathUtil.ExeDir(), "Data");
                try { if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir); } catch { }
                string safe = _name;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                return System.IO.Path.Combine(dir, "mcp_cache_" + safe + ".json");
            }
        }

        void SaveCache()
        {
            try
            {
                var arr = new JArray();
                for (int i = 0; i < Tools.Count; i++)
                {
                    var d = Tools[i];
                    var o = new JObject();
                    o["name"] = originalName.ContainsKey(d.name) ? originalName[d.name] : d.name;
                    o["title"] = d.title;
                    o["description"] = d.description;
                    o["inputSchema"] = JObject.Parse(string.IsNullOrEmpty(d.parametersJson) ? "{}" : d.parametersJson);
                    arr.Add(o);
                }
                var root = new JObject();
                root["saved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                root["tools"] = arr;
                System.IO.File.WriteAllText(CachePath, root.ToString(Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[McpStdioSource] 写缓存失败：" + e.Message); }
        }

        bool LoadCache()
        {
            try
            {
                if (!System.IO.File.Exists(CachePath)) return false;
                var root = JObject.Parse(System.IO.File.ReadAllText(CachePath));
                var arr = root["tools"] as JArray;
                if (arr == null || arr.Count == 0) return false;
                BuildTools(arr);
                Debug.Log("[McpStdioSource] " + _name + " 使用缓存（" + (string)root["saved"] + "）");
                return Tools.Count > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[McpStdioSource] 读缓存失败：" + e.Message);
                return false;
            }
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

        /// <summary>外部（工作区重载）关掉这个服务。</summary>
        public void Stop() { Shutdown(); }

        void Shutdown()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { if (proc != null) proc.Dispose(); } catch { }
            proc = null; alive = false;
        }
    }
}
