using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PetFramework.Tools;

/// <summary>
/// 把 FurinaActionRegistry 的动作池接进 ToolRegistry：
/// 每个 do* 动作 = 一个 Chat 类工具（执行完不回传给模型，避免多唠一句）。
/// </summary>
public class FurinaActionToolSource : MonoBehaviour, IToolSource
{
    public FurinaActionRegistry registry;

    readonly List<ToolDescriptor> tools = new List<ToolDescriptor>();

    public string SourceId { get { return "furina-actions"; } }
    public List<ToolDescriptor> Tools { get { return tools; } }

    public IEnumerator Initialize(ToolRegistry reg)
    {
        if (registry == null) registry = GetComponent<FurinaActionRegistry>();
        if (registry == null) registry = FindObjectOfType<FurinaActionRegistry>();
        tools.Clear();
        if (registry == null) yield break;
        registry.Load();

        for (int i = 0; i < registry.actions.Count; i++)
        {
            var a = registry.actions[i];
            if (a == null || string.IsNullOrEmpty(a.name)) continue;
            tools.Add(new ToolDescriptor
            {
                name = a.name,
                title = a.state,
                description = a.description,
                parametersJson = "{\"type\":\"object\",\"properties\":{}}",
                kind = ToolKind.Action,        // ⚠️ 不能用 Builtin：那个值会被路由回 PetAPI 的老 switch（只认桌宠那几个工具）
                mode = ToolMode.Chat,          // 动作类：执行完就结束，不要把结果再喂回模型
                sourceId = SourceId,
                timeoutSec = 10f,
                enabled = true,
                isEnabled = true,
                requireConfirm = false
            });
        }
        Debug.Log("[FurinaActions] 注册 " + tools.Count + " 个动作工具");
        yield return null;
    }

    public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
    {
        string msg = "";
        bool ok = registry != null && registry.Run(tool.name, out msg);
        if (!ok && string.IsNullOrEmpty(msg)) msg = "动作池未就绪";
        if (done != null)
            done(ok ? ToolResult.Ok("{\"ok\":true,\"action\":\"" + tool.name + "\"}", msg, ToolMode.Chat)
                    : ToolResult.Fail(msg, ToolMode.Chat));
        yield return null;
    }
}
