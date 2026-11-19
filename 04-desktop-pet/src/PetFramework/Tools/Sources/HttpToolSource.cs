using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【HTTP 工具】—— 把一次 POST 请求包装成工具。
    /// 适合挂本地小服务（比如 http://127.0.0.1:8765/weather）。
    /// 请求体 = 模型给的工具参数；响应体按 {ok,result,display} 解析，解析不出来就整段当结果。
    /// </summary>
    public class HttpToolSource : IToolSource
    {
        public string SourceId { get { return "http:" + _name; } }
        public List<ToolDescriptor> Tools { get; private set; }

        readonly JObject cfg;
        string _name, url;
        float defTimeout;
        int defMaxChars;
        bool defConfirm;
        string[] defRoots;

        public HttpToolSource(JObject entry, string prefix, float defTimeout, int defMaxChars, bool defConfirm, string[] defRoots)
        {
            cfg = entry;
            this.prefix = prefix ?? "";
            this.defTimeout = defTimeout;
            this.defMaxChars = defMaxChars;
            this.defConfirm = defConfirm;
            this.defRoots = defRoots;
            Tools = new List<ToolDescriptor>();

            string rawName = (string)entry["name"];
            if (string.IsNullOrEmpty(rawName)) rawName = (string)entry["title"];
            if (string.IsNullOrEmpty(rawName)) rawName = "http_tool";
            _name = this.prefix + rawName;
            url = (string)entry["url"];
        }

        readonly string prefix;

        public IEnumerator Initialize(ToolRegistry registry)
        {
            Tools.Clear();
            JToken pars = cfg["parameters"];
            if (pars == null) pars = JToken.Parse("{\"type\":\"object\",\"properties\":{}}");

            var d = new ToolDescriptor();
            d.name = _name;
            d.title = (string)cfg["title"] ?? _name;
            d.description = (string)cfg["description"] ?? "";
            d.parametersJson = pars.ToString(Formatting.None);
            d.kind = ToolKind.Http;
            d.mode = ((string)cfg["mode"] ?? "agent").Trim().ToLowerInvariant() == "chat" ? ToolMode.Chat : ToolMode.Agent;
            d.sourceId = SourceId;
            d.timeoutSec = cfg.Value<float?>("timeoutSec") ?? defTimeout;
            d.maxOutputChars = cfg.Value<int?>("maxOutputChars") ?? defMaxChars;
            d.requireConfirm = cfg.Value<bool?>("requireConfirm") ?? defConfirm;
            d.enabled = cfg.Value<bool?>("enabled") ?? true;
            d.safety.allowedRoots = defRoots;
            Tools.Add(d);
            yield break;
        }

        public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
        {
            bool allow = false;
            yield return ToolExecutor.Confirm(tool, argsJson, a => allow = a);
            if (!allow) { done(ToolResult.Fail("用户拒绝了这次工具调用", tool.mode)); yield break; }

            if (string.IsNullOrEmpty(url)) { done(ToolResult.Fail("没有配置 url", tool.mode)); yield break; }

            var res = new ToolResult { mode = tool.mode };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using (var www = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson));
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                var headers = cfg["headers"] as JObject;
                if (headers != null)
                    foreach (var kv in headers) www.SetRequestHeader(kv.Key, kv.Value != null ? kv.Value.ToString() : "");
                www.timeout = Mathf.CeilToInt(tool.timeoutSec);
                yield return www.SendWebRequest();

                sw.Stop();
                res.elapsedSec = (float)sw.Elapsed.TotalSeconds;

                if (www.result != UnityWebRequest.Result.Success)
                {
                    res.ok = false;
                    res.error = "HTTP " + www.responseCode + " " + www.error;
                    res.display = res.error;
                    done(res);
                    yield break;
                }

                string raw = www.downloadHandler != null ? www.downloadHandler.text : "";
                try
                {
                    var jo = JObject.Parse(raw);
                    res.ok = jo.Value<bool?>("ok") ?? true;
                    res.resultJson = jo["result"] != null ? jo["result"].ToString(Formatting.None) : raw;
                    res.display = (string)jo["display"];
                    res.error = (string)jo["error"];
                    if (string.IsNullOrEmpty(res.display)) res.display = res.ok ? res.resultJson : res.error;
                }
                catch
                {
                    res.ok = true;
                    res.resultJson = ToolExecutor.Truncate(raw, tool.maxOutputChars);
                    res.display = res.resultJson;
                }
                if (string.IsNullOrEmpty(res.display)) res.display = "（完成）";
                done(res);
            }
        }
    }
}
