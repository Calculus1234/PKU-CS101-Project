using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【内置文件工具】read_file / list_dir / write_file。
    ///
    /// 为什么放在框架里而不是 pet_tools.json 的 process 工具：
    ///   · 这是"基本操作"，打包/换机器都不该丢；
    ///   · process 工具要依赖 python 脚本和相对路径，配置一漏就整个失效；
    ///   · 写入需要确认弹窗，内置路径能直接复用 ToolExecutor.Confirm。
    ///
    /// 模式是 Agent（结果要回传给模型），所以 ExecuteToolRound 里对 Builtin 的
    /// "同步执行、不回传" 分支要排除掉 mode=Agent 的内置工具。
    /// </summary>
    public class FileToolSource : IToolSource
    {
        public string SourceId { get { return "files"; } }
        public List<ToolDescriptor> Tools { get; private set; }

        public FileToolSource() { Tools = new List<ToolDescriptor>(); }

        static string[] DefaultRoots()
        {
            return new string[]
            {
                PetPathUtil.ExeDir(),
                @"D:\myprogram",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
        }

        public IEnumerator Initialize(ToolRegistry registry)
        {
            Tools.Clear();
            string[] roots = DefaultRoots();

            Tools.Add(Make("read_file", "读取文件",
                "读取一个文本文件的内容（最多 20 万字符）。参数 path = 文件完整路径。",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"文件完整路径\"}},\"required\":[\"path\"]}",
                false, roots));

            Tools.Add(Make("list_dir", "列出目录",
                "列出目录下的文件和子目录（名称、是否目录、大小）。参数 path = 目录完整路径。",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"目录完整路径\"}},\"required\":[\"path\"]}",
                false, roots));

            Tools.Add(Make("write_file", "写入文件",
                "把文本写入文件（会弹窗确认；可覆盖已有文件、自动创建父目录）。参数 path = 文件路径，content = 文本。",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"文件完整路径\"},\"content\":{\"type\":\"string\",\"description\":\"要写入的文本\"}},\"required\":[\"path\",\"content\"]}",
                true, roots));

            yield break;
        }

        static ToolDescriptor Make(string name, string title, string desc, string schema, bool confirm, string[] roots)
        {
            var d = new ToolDescriptor();
            d.name = name;
            d.title = title;
            d.description = desc;
            d.parametersJson = schema;
            d.kind = ToolKind.Builtin;
            d.mode = ToolMode.Agent;          // 结果回传给模型
            d.sourceId = "files";
            d.timeoutSec = 20f;
            d.maxOutputChars = 20000;
            d.requireConfirm = confirm;
            d.enabled = true;
            d.isEnabled = true;
            d.safety.allowedRoots = roots;
            d.safety.allowWrite = confirm;
            return d;
        }

        public IEnumerator Invoke(ToolDescriptor tool, string argsJson, ToolContext ctx, Action<ToolResult> done)
        {
            JObject a = null;
            try { a = string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch (Exception e) { done(ToolResult.Fail("参数不是合法 JSON：" + e.Message, ToolMode.Agent)); yield break; }

            string path = (string)a["path"] ?? (string)a["file_path"] ?? "";
            if (string.IsNullOrEmpty(path)) { done(ToolResult.Fail("缺少 path", ToolMode.Agent)); yield break; }

            if (!ToolExecutor.PathAllowed(path, tool.safety.allowedRoots))
            {
                done(ToolResult.Fail("路径不在允许范围内：" + path, ToolMode.Agent));
                yield break;
            }

            if (tool.requireConfirm)
            {
                bool allow = false;
                yield return ToolExecutor.Confirm(tool, argsJson, ok => allow = ok);
                if (!allow) { done(ToolResult.Fail("用户拒绝了写入", ToolMode.Agent)); yield break; }
            }

            try
            {
                if (tool.name == "read_file")
                {
                    if (!File.Exists(path)) { done(ToolResult.Fail("文件不存在：" + path, ToolMode.Agent)); yield break; }
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    if (text.Length > 200000) text = text.Substring(0, 200000) + "\n…（已截断）";
                    done(ToolResult.Ok(text, "读取 " + new FileInfo(path).Length + " 字节", ToolMode.Agent));
                }
                else if (tool.name == "list_dir")
                {
                    if (!Directory.Exists(path)) { done(ToolResult.Fail("目录不存在：" + path, ToolMode.Agent)); yield break; }
                    var arr = new JArray();
                    int n = 0;
                    foreach (string p in Directory.GetFileSystemEntries(path))
                    {
                        if (n++ >= 200) break;
                        var o = new JObject();
                        o["name"] = Path.GetFileName(p);
                        o["dir"] = Directory.Exists(p);
                        try { o["size"] = File.Exists(p) ? new FileInfo(p).Length : 0; } catch { o["size"] = 0; }
                        arr.Add(o);
                    }
                    done(ToolResult.Ok(arr.ToString(Newtonsoft.Json.Formatting.None),
                        "列出 " + arr.Count + " 项", ToolMode.Agent));
                }
                else if (tool.name == "write_file")
                {
                    string content = (string)a["content"] ?? "";
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, content, new UTF8Encoding(false));
                    done(ToolResult.Ok("written " + content.Length + " chars",
                        "写入 " + content.Length + " 字符 → " + path, ToolMode.Agent));
                }
                else
                {
                    done(ToolResult.Fail("未知文件工具：" + tool.name, ToolMode.Agent));
                }
            }
            catch (Exception e)
            {
                done(ToolResult.Fail(e.Message, ToolMode.Agent));
            }
        }
    }
}
