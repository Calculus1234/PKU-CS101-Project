using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【本地进程工具】—— 玩家挂一个 .exe / .py / .bat，就能给桌宠加一种能力。
    ///
    /// 协议（一行 JSON 进，一行 JSON 出）：
    ///   stdin : {"tool":"write_code","arguments":{...},"context":{...}}
    ///   stdout: {"ok":true,"result":"...","display":"..."}  或  {"ok":false,"error":"..."}
    ///
    /// 参数 schema 三种来源（expose 字段）：
    ///   inline  → 配置里直接写 parameters
    ///   sidecar → 读 schemaFile 指向的 .json
    ///   auto    → 启动时跑 `command args --describe`，让脚本自己报 schema
    /// </summary>
    public class ProcessToolSource : IToolSource
    {
        public string SourceId { get { return "process:" + _name; } }
        public List<ToolDescriptor> Tools { get; private set; }

        string _name;
        readonly JObject cfg;
        readonly string prefix;
        readonly float defTimeout;
        readonly int defMaxChars;
        readonly bool defConfirm;
        readonly string[] defRoots;

        string command, cwd;
        string[] args;

        public ProcessToolSource(JObject entry, string prefix, float defTimeout, int defMaxChars, bool defConfirm, string[] defRoots)
        {
            this.cfg = entry;
            this.prefix = prefix ?? "";
            this.defTimeout = defTimeout;
            this.defMaxChars = defMaxChars;
            this.defConfirm = defConfirm;
            this.defRoots = defRoots;
            Tools = new List<ToolDescriptor>();

            string rawName = (string)entry["name"];
            if (string.IsNullOrEmpty(rawName)) rawName = (string)entry["title"];
            if (string.IsNullOrEmpty(rawName)) rawName = "tool";
            _name = this.prefix + rawName;

            command = (string)entry["command"];
            cwd = (string)entry["cwd"];
            // 没写 cwd 就固定在 EXE 目录：这样 args 里的相对路径（python_tools/xxx.py）
            // 在编辑器和成品里都指同一个地方，不会因为快捷方式的工作目录不同而找不到脚本。
            if (string.IsNullOrEmpty(cwd)) cwd = PetPathUtil.ExeDir();
            args = null;
            var a = entry["args"] as JArray;
            if (a != null)
            {
                var list = new List<string>();
                for (int i = 0; i < a.Count; i++) list.Add((string)a[i]);
                args = list.ToArray();
            }
        }

        public IEnumerator Initialize(ToolRegistry registry)
        {
            Tools.Clear();
            string expose = ((string)cfg["expose"] ?? "inline").Trim().ToLowerInvariant();

            string schema = null;
            if (cfg["parameters"] != null) { schema = cfg["parameters"].ToString(Formatting.None); expose = "inline"; }
            else if (expose == "sidecar")
            {
                string sf = (string)cfg["schemaFile"];
                try { if (!string.IsNullOrEmpty(sf) && System.IO.File.Exists(sf)) schema = System.IO.File.ReadAllText(sf); }
                catch (Exception e) { Debug.LogWarning("[ProcessToolSource] 读 schemaFile 失败：" + e.Message); }
            }
            else if (expose == "auto")
            {
                yield return FetchDescribe(registry, s => schema = s);
            }

            JToken pars = null;
            string description = (string)cfg["description"] ?? "";
            string title = (string)cfg["title"] ?? _name;
            if (!string.IsNullOrEmpty(schema))
            {
                try
                {
                    var so = JObject.Parse(schema);
                    if (so["parameters"] != null) pars = so["parameters"];
                    else if (so["inputSchema"] != null) pars = so["inputSchema"];   // 兼容 MCP 风格
                    else if (so["type"] != null) pars = so;                          // 直接就是 schema
                    if (so["description"] != null && string.IsNullOrEmpty(description)) description = (string)so["description"];
                    if (so["name"] != null && string.IsNullOrEmpty((string)cfg["title"])) title = (string)so["name"];
                }
                catch (Exception e) { Debug.LogWarning("[ProcessToolSource] schema 解析失败：" + e.Message); }
            }
            if (pars == null) pars = JToken.Parse("{\"type\":\"object\",\"properties\":{}}");

            var d = new ToolDescriptor();
            d.name = _name;
            d.title = string.IsNullOrEmpty(title) ? _name : title;
            d.description = description;
            d.parametersJson = pars.ToString(Formatting.None);
            d.kind = ToolKind.Process;
            d.mode = ParseMode((string)cfg["mode"], ToolMode.Agent);
            d.sourceId = SourceId;
            d.timeoutSec = cfg.Value<float?>("timeoutSec") ?? defTimeout;
            d.maxOutputChars = cfg.Value<int?>("maxOutputChars") ?? defMaxChars;
            d.requireConfirm = cfg.Value<bool?>("requireConfirm") ?? defConfirm;
            d.enabled = cfg.Value<bool?>("enabled") ?? true;
            d.safety.allowedRoots = ReadRoots(cfg["allowedRoots"]) ?? defRoots;
            Tools.Add(d);
            yield break;
        }

        /// <summary>跑 `command args --describe`，让脚本自己报 schema。</summary>
        IEnumerator FetchDescribe(ToolRegistry registry, Action<string> done)
        {
            string[] descArgs;
            if (args == null) descArgs = new string[] { "--describe" };
            else
            {
                descArgs = new string[args.Length + 1];
                Array.Copy(args, descArgs, args.Length);
                descArgs[args.Length] = "--describe";
            }

            ProcessOut o = null;
            yield return ToolExecutor.RunProcess(command, descArgs, cwd, null, Mathf.Min(20f, defTimeout), 20000, r => o = r);
            if (o == null || !o.started || o.timedOut)
            {
                Debug.LogWarning("[ProcessToolSource] " + _name + " 的 --describe 失败：" + (o != null ? (o.startError ?? "超时") : "null"));
                done(null);
                yield break;
            }
            string line = LastJsonLine(o.stdout);
            if (string.IsNullOrEmpty(line))
            {
                Debug.LogWarning("[ProcessToolSource] " + _name + " 的 --describe 没有输出 JSON。stdout=" + ToolExecutor.Truncate(o.stdout, 300));
                done(null);
                yield break;
            }
            done(line);
        }

        static ToolMode ParseMode(string s, ToolMode def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().ToLowerInvariant();
            if (s == "chat") return ToolMode.Chat;
            if (s == "agent") return ToolMode.Agent;
            return def;
        }

        static string[] ReadRoots(JToken tok)
        {
            if (tok == null || tok.Type != JTokenType.Array) return null;
            var arr = (JArray)tok;
            var list = new List<string>();
            for (int i = 0; i < arr.Count; i++) { string s = (string)arr[i]; if (!string.IsNullOrEmpty(s)) list.Add(s); }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>从多行输出里找最后一行像 JSON 的。</summary>
        public static string LastJsonLine(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            string[] lines = stdout.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string s = lines[i].Trim();
                if (s.Length < 2 || s[0] != '{') continue;
                try { JToken.Parse(s); return s; } catch { }
            }
            return null;
        }

        public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
        {
            // ① 确认
            bool allow = false;
            yield return ToolExecutor.Confirm(tool, argsJson, a => allow = a);
            if (!allow) { done(ToolResult.Fail("用户拒绝了这次工具调用", tool.mode)); yield break; }

            // ② 路径白名单（只检查参数里常见的路径键）
            string badPath = FindDisallowedPath(argsJson, tool.safety.allowedRoots);
            if (!string.IsNullOrEmpty(badPath))
            {
                done(ToolResult.Fail("路径不在允许范围内：" + badPath, tool.mode));
                yield break;
            }

            // ③ 组装 stdin
            JObject stdin = new JObject();
            stdin["tool"] = tool.name;
            try { stdin["arguments"] = JToken.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson); }
            catch { stdin["arguments"] = new JObject(); }
            var c = new JObject();
            c["petName"] = ctx != null ? ctx.petName : "";
            c["userName"] = ctx != null ? ctx.userName : "";
            c["projectRoot"] = ctx != null ? ctx.projectRoot : "";
            c["workingDir"] = ctx != null ? ctx.workingDir : "";
            stdin["context"] = c;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProcessOut o = null;
            yield return ToolExecutor.RunProcess(command,
                args, cwd, stdin.ToString(Formatting.None),
                tool.timeoutSec, tool.maxOutputChars, r => o = r);
            sw.Stop();

            var res = new ToolResult();
            res.mode = tool.mode;
            res.elapsedSec = (float)sw.Elapsed.TotalSeconds;

            if (o == null || !o.started)
            {
                res.ok = false; res.error = "启动失败：" + (o != null ? o.startError : "未知");
                res.display = res.error;
                done(res);
                yield break;
            }
            if (o.timedOut)
            {
                res.ok = false; res.error = "执行超时（" + tool.timeoutSec + "s），已强制结束";
                res.display = res.error;
                done(res);
                yield break;
            }

            string json = LastJsonLine(o.stdout);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var jo = JObject.Parse(json);
                    res.ok = jo.Value<bool?>("ok") ?? (o.exitCode == 0);
                    res.resultJson = jo["result"] != null ? jo["result"].ToString(Formatting.None) : json;
                    res.display = (string)jo["display"];
                    res.error = (string)jo["error"];
                    if (!res.ok && string.IsNullOrEmpty(res.error)) res.error = o.stderr;
                    if (string.IsNullOrEmpty(res.display)) res.display = res.ok ? res.resultJson : res.error;
                    done(res);
                    yield break;
                }
                catch { }
            }

            // 没有按协议返回 → 把 stdout 当结果，exitCode 决定成败
            res.ok = o.exitCode == 0;
            res.resultJson = ToolExecutor.Truncate(string.IsNullOrEmpty(o.stdout) ? o.stderr : o.stdout, tool.maxOutputChars);
            res.display = res.ok ? res.resultJson : ("退出码 " + o.exitCode + "：" + res.resultJson);
            if (!res.ok) res.error = res.display;
            if (string.IsNullOrEmpty(res.display)) res.display = res.ok ? "（完成）" : "（失败，无输出）";
            done(res);
        }

        /// <summary>扫参数里的常见路径键，检查是否越界。（MCP 那边也复用）</summary>
        public static string FindDisallowedPath(string argsJson, string[] roots)
        {
            if (roots == null || roots.Length == 0 || string.IsNullOrEmpty(argsJson)) return null;
            JObject o;
            try { o = JObject.Parse(argsJson); } catch { return null; }
            string[] keys = { "path", "file", "file_path", "filepath", "output", "output_path", "target", "target_path", "directory", "folder", "dir" };
            for (int i = 0; i < keys.Length; i++)
            {
                string v = (string)o[keys[i]];
                if (string.IsNullOrEmpty(v)) continue;
                if (!ToolExecutor.PathAllowed(v, roots)) return v;
            }
            return null;
        }
    }
}
