using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【内置工具来源】—— 走动、播动作、说话、记规矩、吃文件、变身。
    ///
    /// ⚠️ 这里**不重写任何逻辑**：真正干活的还是 `PetAPI.ExecuteTool` 那个 switch。
    ///    本类只负责把那些工具「注册」到注册中心里，让外部工具和内置工具走同一条路。
    ///
    /// 工具清单直接从 `PetAPI.BuiltinToolsJson()` 拆出来 —— 避免 schema 在两处各写一份、
    /// 改一边忘一边（历史上踩过）。
    /// </summary>
    public class BuiltinToolSource : IToolSource
    {
        public string SourceId { get { return "builtin"; } }
        public List<ToolDescriptor> Tools { get; private set; }

        public BuiltinToolSource() { Tools = new List<ToolDescriptor>(); }

        public IEnumerator Initialize(ToolRegistry registry)
        {
            Tools.Clear();

            string json = PetAPI.Instance != null
                ? PetAPI.Instance.BuiltinToolsJson()
                : PetAPI.LegacyToolsJsonStatic(false);

            if (string.IsNullOrEmpty(json)) yield break;

            JArray arr = null;
            try { arr = JArray.Parse(json); }
            catch (Exception e) { Debug.LogWarning("[BuiltinToolSource] 解析内置工具失败：" + e.Message); }

            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var fn = arr[i]["function"] as JObject;
                    if (fn == null) continue;
                    var d = new ToolDescriptor
                    {
                        name = (string)fn["name"],
                        title = (string)fn["name"],
                        description = (string)fn["description"],
                        parametersJson = (fn["parameters"] != null) ? fn["parameters"].ToString() : "{\"type\":\"object\",\"properties\":{}}",
                        kind = ToolKind.Builtin,
                        mode = ToolMode.Chat,          // 内置一律「执行完就结束」，不回传
                        sourceId = SourceId,
                        requireConfirm = false,
                        timeoutSec = 5f
                    };
                    Tools.Add(d);
                }
            }
            yield break;
        }

        public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
        {
            // 内置工具都是瞬间完成的，但仍然用协程签名，方便以后接异步动作。
            string said = null;
            try
            {
                if (PetAPI.Instance != null)
                    said = PetAPI.Instance.ExecuteBuiltinTool(tool.name, argsJson, ctx != null && ctx.autonomous);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BuiltinToolSource] " + tool.name + " 执行失败：" + e.Message);
            }

            var r = ToolResult.Ok("{\"ok\":true}", said, ToolMode.Chat);
            done(r);
            yield break;
        }
    }
}
