using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PetFramework;
using PetFramework.Tools;
using UnityEngine;

namespace PetFramework.Workspace
{
    /// <summary>
    /// 工作区专用的 MCP 客户端集合。
    ///
    /// 直接读 `pet_tools.json` 里 kind = "mcp" 且 enabled 的条目，
    /// 复用框架的 McpStdioSource（JSON-RPC over stdio）。
    ///
    /// 为什么不用 ToolRegistry：那套是给桌宠聊天用的单例，带用户开关 / 权限文件，
    /// 工作区要的是「独立的一套会话」，混在一起两边互相影响。
    /// </summary>
    public class WorkspaceMcp
    {
        public readonly List<ToolDescriptor> Tools = new List<ToolDescriptor>();
        readonly List<McpStdioSource> sources = new List<McpStdioSource>();
        readonly Dictionary<string, McpStdioSource> owner = new Dictionary<string, McpStdioSource>();

        public bool Loading { get; private set; }
        public string Status { get; private set; }

        public IEnumerator Reload()
        {
            Loading = true;
            Status = "正在加载 MCP …";

            for (int i = 0; i < sources.Count; i++) { try { sources[i].Stop(); } catch { } }
            sources.Clear(); Tools.Clear(); owner.Clear();

            string path = Path.Combine(PetPathUtil.ExeDir(), "pet_tools.json");
            JObject root = null;
            try
            {
                if (File.Exists(path)) root = JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceMcp] 读 pet_tools.json 失败：" + e.Message); }

            var arr = root != null ? root["tools"] as JArray : null;
            if (arr == null) { Status = "pet_tools.json 里没有工具配置"; Loading = false; yield break; }

            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr[i] as JObject;
                if (e == null) continue;
                if ((string)e["kind"] != "mcp") continue;
                bool enabled = e["enabled"] == null || (bool)e["enabled"];
                if (!enabled) continue;

                string name = (string)e["name"] ?? "mcp";
                var src = new McpStdioSource(e, name + "_", 90f, 4000, false, null);
                Status = "正在连接 " + name + " …";
                yield return src.Initialize(null);

                sources.Add(src);
                int before = Tools.Count;
                for (int k = 0; k < src.Tools.Count; k++)
                {
                    Tools.Add(src.Tools[k]);
                    owner[src.Tools[k].name] = src;
                }
                Debug.Log("[WorkspaceMcp] " + name + " → " + (Tools.Count - before) + " 个工具");
            }

            Status = "MCP：" + sources.Count + " 个服务 / " + Tools.Count + " 个工具";
            Debug.Log("[WorkspaceMcp] " + Status);
            Loading = false;
        }

        public bool Has(string toolName) { return owner.ContainsKey(toolName); }

        /// <summary>
        /// 调用一个 MCP 工具。
        /// ⚠️ skipConfirm=true 时【临时】把 ToolDescriptor.requireConfirm 关掉再调用，
        ///    调用完还原。这是「工作区权限闸门」和「桌宠 ToolConfirmUI」的唯一衔接点：
        ///    pet_tools.json 里 requireConfirm:true 会在 McpStdioSource 构造时覆盖默认值，
        ///    不在这里压下去的话，工作区选了「完整放开权限」也照样弹桌宠那个确认框
        ///    （作者实测：「开放全部权限一点用都没有」）。
        /// </summary>
        public IEnumerator Invoke(string toolName, string argsJson, Action<string> done, bool skipConfirm = false)
        {
            McpStdioSource src;
            if (!owner.TryGetValue(toolName, out src)) { done("{\"ok\":false,\"error\":\"没有这个 MCP 工具\"}"); yield break; }

            ToolDescriptor d = src.Tools.FirstOrDefault(t => t.name == toolName);
            bool oldConfirm = d != null && d.requireConfirm;
            if (skipConfirm && d != null) d.requireConfirm = false;
            string result = null;
            yield return src.Invoke(d, argsJson, new ToolContext(), r =>
            {
                result = r != null ? (r.resultJson ?? r.display ?? (r.ok ? "{}" : "{\"ok\":false}")) : "{}";
            });
            if (skipConfirm && d != null) d.requireConfirm = oldConfirm;
            done(result ?? "{}");
        }

        public JArray DescribeForModel()
        {
            var arr = new JArray();
            for (int i = 0; i < Tools.Count; i++)
            {
                var d = Tools[i];
                JObject schema;
                try { schema = JObject.Parse(string.IsNullOrEmpty(d.parametersJson) ? "{\"type\":\"object\",\"properties\":{}}" : d.parametersJson); }
                catch { schema = JObject.Parse("{\"type\":\"object\",\"properties\":{}}"); }
                var fn = new JObject();
                fn["name"] = d.name;
                // 描述截断：Unity 有些工具的描述上千字，全发过去纯浪费上下文
                string desc = d.description ?? "";
                if (desc.Length > 160) desc = desc.Substring(0, 160) + "…";
                fn["description"] = desc + (string.IsNullOrEmpty(d.title) ? "" : "（" + d.title + "）");
                fn["parameters"] = schema;
                var t = new JObject();
                t["type"] = "function";
                t["function"] = fn;
                arr.Add(t);
            }
            return arr;
        }
    }
}
