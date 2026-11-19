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
    /// 【通用工具注册中心】—— 2.0 架构的核心。
    ///
    /// 职责：
    ///   ① 汇总所有 IToolSource 的工具（内置 + 外部脚本 + HTTP + MCP）
    ///   ② 给 PetAPI 提供两样东西：tools 数组、System Prompt 里那段工具清单
    ///   ③ 名字去重 / 前缀 / 禁用
    ///   ④ 把调用转给真正的执行器
    ///
    /// 设计原则：**PetAPI 不认识任何具体工具**。加新工具 = 改 pet_tools.json，一行 C# 都不用动。
    ///
    /// ⚠️ 外部工具默认关闭（`enableExternalTools=false`）。玩家没配 ⇒ 只有内置的漫游/说话/吃文件，
    ///    和 1.0 行为完全一致。
    /// </summary>
    public class ToolRegistry : MonoBehaviour
    {
        public static ToolRegistry Instance { get; private set; }

        [Header("配置文件（留空 = EXE 同级 pet_tools.json）")]
        public string fileName = "pet_tools.json";
        public bool autoReload = true;
        public float watchIntervalSec = 3f;

        [Header("工具回环（干活型工具把结果回传给模型的轮数）")]
        [Tooltip("默认 2 轮，强制夹在 1~3 之间。超了就掐断让模型直接总结")]
        public int maxToolRounds = 2;

        public string SettingsPath { get; private set; }
        /// <summary>用户权限文件（每个工具的开/关）。</summary>
        public string PermissionsPath { get; private set; }
        public string Status { get; private set; }
        public List<ToolDescriptor> All { get; private set; }

        readonly List<IToolSource> sources = new List<IToolSource>();
        readonly Dictionary<string, IToolSource> sourceByTool = new Dictionary<string, IToolSource>();
        public bool ExternalEnabled { get; private set; }

        // 工具名 → 用户勾选状态。没记录过的走默认（内置开、外部关）
        readonly Dictionary<string, bool> userPrefs = new Dictionary<string, bool>();

        long lastWriteTicks = -1;
        float nextWatch;
        bool ready;

        public int MaxToolRounds
        {
            get { return Mathf.Clamp(maxToolRounds, 1, 3); }
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            All = new List<ToolDescriptor>();
            SettingsPath = PetPathUtil.ResolveBesideExe(fileName);
            PermissionsPath = PetPathUtil.ResolveBesideExe("pet_tool_permissions.json");
        }

        IEnumerator Start()
        {
            yield return Reload();
            ready = true;
        }

        void Update()
        {
            if (!autoReload || !ready) return;
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + Mathf.Max(1f, watchIntervalSec);
            try
            {
                if (!System.IO.File.Exists(SettingsPath)) return;
                long t = System.IO.File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc();
                if (t != lastWriteTicks && lastWriteTicks >= 0)
                {
                    Debug.Log("[ToolRegistry] pet_tools.json 被外部改动 → 重新加载");
                    StartCoroutine(Reload());
                }
            }
            catch { }
        }

        // ---------------- 加载 ----------------

        public IEnumerator Reload()
        {
            All.Clear();
            sourceByTool.Clear();
            sources.Clear();
            ExternalEnabled = false;

            // 内置永远有
            var builtin = new BuiltinToolSource();
            yield return builtin.Initialize(this);
            sources.Add(builtin);

            // 外部（默认关）
            JObject root = null;
            try
            {
                if (!System.IO.File.Exists(SettingsPath))
                {
                    WriteTemplate();
                }
                if (System.IO.File.Exists(SettingsPath))
                {
                    root = JObject.Parse(System.IO.File.ReadAllText(SettingsPath));
                    try { lastWriteTicks = System.IO.File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                }
            }
            catch (Exception e)
            {
                Status = "pet_tools.json 解析失败：" + e.Message;
                Debug.LogWarning("[ToolRegistry] " + Status);
            }

            bool wantExternal = root != null && (root.Value<bool?>("enableExternalTools") ?? false);
            ExternalEnabled = wantExternal;

            if (wantExternal && root != null)
            {
                var defaults = root["defaults"] as JObject;
                string prefix = defaults != null ? (string)defaults["namePrefix"] : null;
                if (prefix == null) prefix = "";
                float defTimeout = defaults != null ? (defaults.Value<float?>("timeoutSec") ?? 30f) : 30f;
                int defMaxChars = defaults != null ? (defaults.Value<int?>("maxOutputChars") ?? 4000) : 4000;
                bool defConfirm = defaults != null && (defaults.Value<bool?>("requireConfirm") ?? false);
                string[] defRoots = ReadStringArray(defaults != null ? defaults["allowedRoots"] : null);

                var toolsArr = root["tools"] as JArray;
                if (toolsArr != null)
                {
                    for (int i = 0; i < toolsArr.Count; i++)
                    {
                        var t = toolsArr[i] as JObject;
                        if (t == null) continue;
                        string kind = ((string)t["kind"] ?? "").Trim().ToLowerInvariant();
                        IToolSource src = null;
                        if (kind == "process") src = new ProcessToolSource(t, prefix, defTimeout, defMaxChars, defConfirm, defRoots);
                        else if (kind == "http") src = new HttpToolSource(t, prefix, defTimeout, defMaxChars, defConfirm, defRoots);
                        else if (kind == "mcp") src = new McpStdioSource(t, prefix, defTimeout, defMaxChars, defConfirm, defRoots);
                        else { Debug.LogWarning("[ToolRegistry] 未知 kind=" + kind + "，跳过"); continue; }

                        yield return src.Initialize(this);
                        sources.Add(src);
                    }
                }
            }

            // 汇总 + 去重
            for (int s = 0; s < sources.Count; s++)
            {
                var src = sources[s];
                if (src == null || src.Tools == null) continue;
                for (int i = 0; i < src.Tools.Count; i++)
                {
                    var d = src.Tools[i];
                    if (d == null || string.IsNullOrEmpty(d.name)) continue;
                    if (!d.enabled) continue;
                    if (sourceByTool.ContainsKey(d.name))
                    {
                        Debug.LogWarning("[ToolRegistry] 工具重名已跳过：" + d.name + "（来自 " + d.sourceId + "）");
                        continue;
                    }
                    sourceByTool[d.name] = src;
                    All.Add(d);
                }
            }

            // 用户权限：只有 isEnabled 的工具才会进 tools 数组和 System Prompt
            LoadPrefs();
            ApplyPrefs();

            int ext = 0, on = 0;
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].kind != ToolKind.Builtin) ext++;
                if (All[i].isEnabled) on++;
            }
            Status = All.Count + " 个工具（内置 " + (All.Count - ext) + " + 外部 " + ext + "），已启用 " + on
                   + (ExternalEnabled ? "" : "　⚠ 外部工具总闸关闭");
            Debug.Log("[ToolRegistry] ★ " + Status + " ← " + SettingsPath);
        }

        // ---------------- 用户权限（Esc 面板） ----------------

        void LoadPrefs()
        {
            userPrefs.Clear();
            try
            {
                if (!System.IO.File.Exists(PermissionsPath)) return;
                var root = JObject.Parse(System.IO.File.ReadAllText(PermissionsPath));
                foreach (var kv in root)
                {
                    if (kv.Value != null && kv.Value.Type == JTokenType.Boolean)
                        userPrefs[kv.Key] = (bool)kv.Value;
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[ToolRegistry] 读取权限文件失败：" + e.Message); }
        }

        void SavePrefs()
        {
            try
            {
                var root = new JObject();
                foreach (var kv in userPrefs) root[kv.Key] = kv.Value;
                System.IO.File.WriteAllText(PermissionsPath, root.ToString(Formatting.Indented));
            }
            catch (System.Exception e) { Debug.LogWarning("[ToolRegistry] 保存权限文件失败：" + e.Message); }
        }

        /// <summary>把用户勾选状态套到每个工具上。没记录过 ⇒ 内置开、外部关。</summary>
        public void ApplyPrefs()
        {
            if (All == null) return;
            for (int i = 0; i < All.Count; i++)
            {
                var d = All[i];
                bool def = d.kind == ToolKind.Builtin;     // 外部默认全关
                bool v;
                d.isEnabled = userPrefs.TryGetValue(d.name, out v) ? v : def;
            }
        }

        /// <summary>面板里拨一个开关。</summary>
        public void SetToolEnabled(string name, bool on)
        {
            if (string.IsNullOrEmpty(name)) return;
            userPrefs[name] = on;
            SavePrefs();
            ApplyPrefs();
            Debug.Log("[ToolRegistry] 用户" + (on ? "启用" : "关闭") + "工具：" + name);
        }

        public bool IsToolEnabled(string name)
        {
            var d = Find(name);
            return d != null && d.isEnabled;
        }

        /// <summary>外部工具总闸（写回 pet_tools.json 并重新加载）。默认只给面板用。</summary>
        public void SetExternalMaster(bool on)
        {
            try
            {
                JObject root = null;
                if (System.IO.File.Exists(SettingsPath))
                    root = JObject.Parse(System.IO.File.ReadAllText(SettingsPath));
                if (root == null) root = new JObject();
                root["enableExternalTools"] = on;
                System.IO.File.WriteAllText(SettingsPath, root.ToString(Formatting.Indented));
                Debug.Log("[ToolRegistry] 外部工具总闸 → " + (on ? "开" : "关"));
            }
            catch (System.Exception e) { Debug.LogWarning("[ToolRegistry] 写总闸失败：" + e.Message); }
            StartCoroutine(Reload());
        }

        /// <summary>重新加载 pet_tools.json（面板按钮用）。</summary>
        public void ReloadNow() { StartCoroutine(Reload()); }

        static string[] ReadStringArray(JToken tok)
        {
            if (tok == null || tok.Type != JTokenType.Array) return null;
            var arr = (JArray)tok;
            var list = new List<string>();
            for (int i = 0; i < arr.Count; i++)
            {
                string s = (string)arr[i];
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        void WriteTemplate()
        {
            try
            {
                string json =
                    "{\n" +
                    "  \"_readme\": \"外部工具配置。enableExternalTools=false 时完全不加载。改完保存，3 秒内自动生效。\",\n" +
                    "  \"enableExternalTools\": false,\n" +
                    "  \"maxToolRounds\": 2,\n" +
                    "  \"defaults\": {\n" +
                    "    \"timeoutSec\": 30,\n" +
                    "    \"maxOutputChars\": 4000,\n" +
                    "    \"namePrefix\": \"ext_\",\n" +
                    "    \"requireConfirm\": true,\n" +
                    "    \"allowedRoots\": []\n" +
                    "  },\n" +
                    "  \"tools\": []\n" +
                    "}\n";
                System.IO.File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
                Debug.Log("[ToolRegistry] 已生成模板 " + SettingsPath);
            }
            catch (Exception e) { Debug.LogWarning("[ToolRegistry] 生成模板失败：" + e.Message); }
        }

        // ---------------- 给 PetAPI 用 ----------------

        public ToolDescriptor Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < All.Count; i++) if (All[i].name == name) return All[i];
            return null;
        }

        /// <summary>OpenAI / Ollama 通用的 tools 数组（注意：只返回数组本身，不含 "tools": 前缀）。</summary>
        public string BuildOpenAiToolsJson()
        {
            var arr = new JArray();
            for (int i = 0; i < All.Count; i++)
            {
                var d = All[i];
                if (d == null || !d.enabled || !d.isEnabled) continue;   // 用户没勾 ⇒ 模型根本看不到
                JToken pars;
                try { pars = JToken.Parse(string.IsNullOrEmpty(d.parametersJson) ? "{\"type\":\"object\",\"properties\":{}}" : d.parametersJson); }
                catch { pars = JToken.Parse("{\"type\":\"object\",\"properties\":{}}"); }

                var fn = new JObject();
                fn["name"] = d.name;
                fn["description"] = d.description ?? "";
                fn["parameters"] = pars;

                var item = new JObject();
                item["type"] = "function";
                item["function"] = fn;
                arr.Add(item);
            }
            return arr.ToString(Formatting.None);
        }

        /// <summary>System Prompt 里那段「你可以用这些工具」。只放人话摘要，完整 schema 在 tools 参数里。</summary>
        public string BuildPromptSection()
        {
            var list = new List<ToolDescriptor>();
            for (int i = 0; i < All.Count; i++) if (All[i] != null && All[i].enabled && All[i].isEnabled) list.Add(All[i]);
            if (list.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append("\n\n[你可以用的工具]\n");
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                string desc = d.description ?? "";
                if (desc.Length > 90) desc = desc.Substring(0, 90) + "…";
                desc = desc.Replace("\r", " ").Replace("\n", " ");
                sb.Append("- ").Append(d.name).Append("：").Append(desc).Append("\n");
            }
            sb.Append("需要干活就调用对应工具；不需要就正常聊天。");
            return sb.ToString();
        }

        /// <summary>执行一次工具调用。argsJson 是原始 JSON 字符串。</summary>
        public IEnumerator Invoke(string name, string argsJson, bool autonomous, Action<ToolResult> done)
        {
            var d = Find(name);
            if (d == null)
            {
                done(ToolResult.Fail("没有叫「" + name + "」的工具", ToolMode.Chat));
                yield break;
            }
            if (!d.isEnabled)
            {
                done(ToolResult.Fail("工具「" + name + "」已被用户在面板里关闭", d.mode));
                yield break;
            }
            IToolSource src;
            if (!sourceByTool.TryGetValue(name, out src) || src == null)
            {
                done(ToolResult.Fail("工具没有对应的执行源：" + name, d.mode));
                yield break;
            }

            var ctx = new ToolContext();
            PetProfileConfig.GetSnapshot(out ctx.petName, out ctx.userName, out ctx.persona);
            ctx.projectRoot = PetPathUtil.ExeDir();
            ctx.workingDir = ctx.projectRoot;
            ctx.autonomous = autonomous;
            ctx.petObject = GameObject.Find("青龙_动作库");
            if (ctx.petObject == null) ctx.petObject = GameObject.Find("WhiteTiger_DesktopPet");
            ctx.brain = UnityEngine.Object.FindObjectOfType<PetBrain>();

            Debug.Log("[ToolRegistry] 🔧 " + name + " ← " + src.SourceId + "  args=" + (argsJson ?? "{}"));
            yield return src.Invoke(d, argsJson ?? "{}", ctx, done);
        }
    }
}
