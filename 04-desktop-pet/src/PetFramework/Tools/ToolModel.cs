using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>工具从哪来。</summary>
    public enum ToolKind
    {
        Builtin,   // 框架自带（走动/说话/记忆…）
        Process,   // 本地可执行文件 / 脚本
        Http,      // HTTP POST
        Mcp,       // MCP Server（stdio JSON-RPC）
        Action     // 角色自己的动作池（doDance / doSit …）：不是内置，但同样是 Chat 模式
    }

    /// <summary>
    /// 工具执行完之后要不要把结果回传给模型。
    ///
    /// ⚠️ 这个区分是整个 2.0 架构的命门：
    ///   · Chat  = 本地行为（走/跳/说话）。执行完就该结束，
    ///             要是也回传，模型会为每一步都再唠一句，token 直接翻倍。
    ///   · Agent = 干活型（写代码/建模/查资料）。**必须**把结果喂回去，
    ///             否则模型永远不知道活干完没有。
    /// </summary>
    public enum ToolMode
    {
        Chat,
        Agent
    }

    /// <summary>安全约束。</summary>
    public class ToolSafety
    {
        public bool allowNetwork;
        public bool allowWrite;
        /// <summary>路径白名单（前缀匹配）。为空 = 不限制。</summary>
        public string[] allowedRoots;
    }

    /// <summary>一个工具的完整描述。既是注册表项，也是发给模型的 tools 条目来源。</summary>
    public class ToolDescriptor
    {
        public string name;
        public string title;
        public string description;
        /// <summary>原始 JSON Schema 文本（不解析，直接拼进请求体的 tools）。</summary>
        public string parametersJson = "{\"type\":\"object\",\"properties\":{}}";
        public ToolKind kind = ToolKind.Builtin;
        public ToolMode mode = ToolMode.Chat;
        public string sourceId = "builtin";
        public float timeoutSec = 30f;
        public int maxOutputChars = 4000;
        /// <summary>配置层开关（pet_tools.json 里写的 enabled）。</summary>
        public bool enabled = true;
        /// <summary>
        /// 【用户权限开关】—— 玩家在 Esc 面板里勾的。
        /// 只有 isEnabled == true 的工具才会出现在 tools 数组和 System Prompt 里；
        /// 关掉之后模型完全不知道这个工具存在。默认：内置全开、外部全关。
        /// </summary>
        public bool isEnabled = true;
        public bool requireConfirm;
        /// <summary>
        /// 只读工具：看一眼、列一下、查一下，不改变任何东西。
        /// 这类工具即使 requireConfirm=true 也不弹窗（`ToolExecutor.Confirm` 直接放行），
        /// 否则桌宠想"看一眼屏幕/场景"都要主人点一次允许，根本没法用。
        /// 判定来源：MCP annotations.readOnlyHint / 配置 readOnlyTools / 工具名启发式。
        /// </summary>
        public bool readOnly;
        public ToolSafety safety = new ToolSafety();

        public string Readable()
        {
            return name + "（" + (string.IsNullOrEmpty(title) ? kind.ToString() : title) + "）";
        }
    }

    /// <summary>一次工具调用的结果。</summary>
    public class ToolResult
    {
        public bool ok;
        public string resultJson;   // 回传给模型的原始内容
        public string display;      // 给聊天框 / 日志看的一句话
        public string error;
        public float elapsedSec;
        public ToolMode mode = ToolMode.Chat;

        public static ToolResult Ok(string json, string display, ToolMode mode)
        {
            return new ToolResult { ok = true, resultJson = json, display = display, mode = mode };
        }

        public static ToolResult Fail(string err, ToolMode mode)
        {
            return new ToolResult { ok = false, error = err, display = err, mode = mode };
        }

        public override string ToString()
        {
            return ok ? ("ok: " + (display ?? resultJson)) : ("fail: " + error);
        }
    }

    /// <summary>执行时能拿到的现场信息（脚本会收到其中一部分）。</summary>
    public class ToolContext
    {
        public string petName = "";
        public string userName = "";
        public string persona = "";
        public string projectRoot = "";
        public string workingDir = "";
        public GameObject petObject;
        public PetBrain brain;
        public string screenJson = "";
        public bool autonomous;
    }

    /// <summary>
    /// 工具来源。加一种新来源 = 实现这个接口，注册中心不用改。
    /// </summary>
    public interface IToolSource
    {
        string SourceId { get; }
        List<ToolDescriptor> Tools { get; }

        /// <summary>加载配置 / 握手 / 拉 schema。注册中心会 yield 它。</summary>
        IEnumerator Initialize(ToolRegistry registry);

        /// <summary>执行。argsJson 是【原始 JSON 字符串】，不要用 JsonUtility 反序列化。</summary>
        IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done);
    }
}
