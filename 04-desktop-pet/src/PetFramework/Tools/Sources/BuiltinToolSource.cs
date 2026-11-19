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
                    string toolName = (string)fn["name"];
                    var d = new ToolDescriptor
                    {
                        name = toolName,
                        title = toolName,
                        description = (string)fn["description"],
                        parametersJson = (fn["parameters"] != null) ? fn["parameters"].ToString() : "{\"type\":\"object\",\"properties\":{}}",
                        kind = ToolKind.Builtin,
                        // ⚠️ look_screen 必须是 Agent：它要把"已抓屏"的结果回传给模型，
                        //    并且触发下一轮（下一轮才带图片）。其余内置一律 Chat：执行完就结束。
                        mode = (toolName == "look_screen") ? ToolMode.Agent : ToolMode.Chat,
                        sourceId = SourceId,
                        requireConfirm = false,
                        timeoutSec = 5f
                    };
                    // ⚠️ 只有 look_screen 是"只读"语义；feed_file 会移文件、move_to 会改位置，
                    //    别为了省事把内置全标成只读（万一以后给它们打开 requireConfirm 会出事）。
                    d.readOnly = (toolName == "look_screen");
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

            var r = tool.name == "look_screen"
                ? ToolResult.Ok("{\"ok\":true,\"note\":\"最新的屏幕截图已附在下一条消息里，请直接看图片。\"}", said, ToolMode.Agent)
                : ToolResult.Ok("{\"ok\":true}", said, ToolMode.Chat);
            done(r);
            yield break;
        }
    }
}
