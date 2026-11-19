using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PetFramework.Workspace
{
    /// <summary>
    /// 工作区的数据层：模型路由 + 会话 + 每个会话自己的记忆。
    /// 落盘在 EXE 同级（编辑器里是工程根目录）的 Data/workspace.json。
    ///
    /// ⚠️ 记忆隔离是【按会话】的：每个 WorkspaceConversation 有一份自己的
    ///    messages + memory + summary，互不共享。桌宠那边的聊天只通过
    ///    workspace_recall 工具【只读】查一查，不会互相污染。
    /// </summary>
    [Serializable]
    public class WorkspaceRoute
    {
        public string name = "本地 NPU";
        public string provider = "openai";          // openai / ollama
        public string baseUrl = "http://localhost:8001/v1";
        public string model = "Qwen3.6_18B";
        public string apiKey = "";
        public string chatPath = "";
        public bool enableThinking = false;
        /// <summary>单次回复的最大 token 数（0 = 不传，由服务端决定）。回答被截断时调大它。</summary>
        public int maxTokens = 4096;
    }

    [Serializable]
    public class WorkspaceMessage
    {
        public string role;           // user / assistant
        public string content;
        public string time;
        public string[] attachments;
        /// <summary>这一轮的工作链（工具调用/结果摘要），只用于界面回看，不塞回模型上下文。</summary>
        public string trace;
        /// <summary>
        /// 附件正文（只发给模型，不在气泡里显示）。
        /// ⚠️ 不能把整篇文件塞进 content —— 几十万字的 Text 会超过 uGUI 单网格顶点上限，
        ///    气泡直接渲染成一个空的大色块（作者截图踩过）。
        /// </summary>
        public string attachmentText;
    }

    [Serializable]
    public class WorkspaceConversation
    {
        public string id = "";
        public string title = "新对话";
        public int routeIndex = 0;
        public string created = "";
        public string updated = "";
        /// <summary>对话模式（新建时选定，之后不可改）：chat / claude / pi</summary>
        public string agentMode = "chat";
        /// <summary>工具权限：open（完全放开）/ smart（只读自动、修改需同意）/ ask（逐条审批）</summary>
        public string permission = "smart";
        public List<WorkspaceMessage> messages = new List<WorkspaceMessage>();
        /// <summary>这个会话自己的长期记忆（事实/偏好），只在本会话内使用。</summary>
        public List<string> memory = new List<string>();
        /// <summary>滚动摘要（压缩前 6 条以外的旧对话），只在本会话内使用。</summary>
        public string summary = "";
        /// <summary>messages 里前多少条已经被压缩进 summary（UI 仍显示完整历史）。</summary>
        public int summarizedUpTo = 0;

        public string LastUserText()
        {
            for (int i = messages.Count - 1; i >= 0; i--)
                if (messages[i] != null && messages[i].role == "user") return messages[i].content;
            return "";
        }
    }

    public static class WorkspaceStore
    {
        public static string DirPath
        {
            get
            {
                string dir = Path.Combine(PetPathUtil.ExeDir(), "Data");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }
        public static string FilePath { get { return Path.Combine(DirPath, "workspace.json"); } }

        public static List<WorkspaceRoute> Routes = new List<WorkspaceRoute>();
        public static List<WorkspaceConversation> Conversations = new List<WorkspaceConversation>();
        public static int ActiveIndex = -1;

        static bool loaded;

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Load();
        }

        public static void Reload() { loaded = false; Load(); }

        static void Load()
        {
            Routes.Clear();
            Conversations.Clear();
            ActiveIndex = -1;

            try
            {
                if (File.Exists(FilePath))
                {
                    var root = JObject.Parse(File.ReadAllText(FilePath));
                    var r = root["routes"] as JArray;
                    if (r != null)
                        foreach (var it in r)
                            Routes.Add(it.ToObject<WorkspaceRoute>());
                    var c = root["conversations"] as JArray;
                    if (c != null)
                        foreach (var it in c)
                            Conversations.Add(it.ToObject<WorkspaceConversation>());
                    ActiveIndex = root.Value<int?>("activeIndex") ?? -1;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Workspace] 读取失败：" + e.Message); }

            if (Routes.Count == 0)
            {
                // 默认两条路由：本地 NPU（现在用的）+ 云端硅基流动（填 Key 才能用）
                Routes.Add(new WorkspaceRoute
                {
                    name = "本地 NPU",
                    provider = "openai",
                    baseUrl = "http://localhost:8001/v1",
                    model = "Qwen3.6_18B"
                });
                Routes.Add(new WorkspaceRoute
                {
                    name = "硅基流动",
                    provider = "openai",
                    baseUrl = "https://api.siliconflow.cn/v1",
                    model = "Qwen/Qwen3.5-4B"
                });
            }

            if (Conversations.Count == 0) NewConversation();
            if (ActiveIndex < 0 || ActiveIndex >= Conversations.Count) ActiveIndex = 0;
        }

        public static void Save()
        {
            try
            {
                var root = new JObject();
                root["_readme"] = "工作区数据：模型路由 + 会话 + 每个会话独立的记忆。删掉这个文件 = 清空工作区。";
                root["routes"] = JArray.FromObject(Routes);
                root["conversations"] = JArray.FromObject(Conversations);
                root["activeIndex"] = ActiveIndex;
                File.WriteAllText(FilePath, root.ToString(Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[Workspace] 保存失败：" + e.Message); }
        }

        public static WorkspaceConversation NewConversation(string agentMode = "chat", string permission = "smart")
        {
            var c = new WorkspaceConversation
            {
                id = "c" + DateTime.Now.ToString("yyyyMMddHHmmss") + UnityEngine.Random.Range(100, 999),
                title = "新对话",
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                agentMode = string.IsNullOrEmpty(agentMode) ? "chat" : agentMode,
                permission = string.IsNullOrEmpty(permission) ? "smart" : permission,
                routeIndex = ActiveIndex >= 0 && ActiveIndex < Conversations.Count
                    ? Conversations[ActiveIndex].routeIndex : 0
            };
            Conversations.Insert(0, c);
            ActiveIndex = 0;
            Save();
            return c;
        }

        public static void DeleteConversation(int index)
        {
            if (index < 0 || index >= Conversations.Count) return;
            Conversations.RemoveAt(index);
            if (Conversations.Count == 0) NewConversation();
            ActiveIndex = Mathf.Clamp(index, 0, Conversations.Count - 1);
            Save();
        }

        public static WorkspaceConversation Active
        {
            get
            {
                EnsureLoaded();
                if (ActiveIndex < 0 || ActiveIndex >= Conversations.Count) return null;
                return Conversations[ActiveIndex];
            }
        }

        public static WorkspaceRoute RouteOf(WorkspaceConversation c)
        {
            if (Routes.Count == 0) return new WorkspaceRoute();
            int i = Mathf.Clamp(c != null ? c.routeIndex : 0, 0, Routes.Count - 1);
            return Routes[i];
        }

        /// <summary>给桌宠聊天用的只读检索：在所有会话的记忆 / 摘要里找关键词。</summary>
        public static string Recall(string query, int maxChars = 600)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(query)) return "";
            var hits = new List<string>();
            for (int i = 0; i < Conversations.Count; i++)
            {
                var c = Conversations[i];
                if (c == null) continue;
                for (int m = 0; m < c.memory.Count; m++)
                {
                    string line = c.memory[m];
                    if (!string.IsNullOrEmpty(line) && line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        hits.Add("[" + c.title + "] " + line);
                }
                if (!string.IsNullOrEmpty(c.summary) &&
                    c.summary.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add("[" + c.title + " · 摘要] " + c.summary);
            }
            if (hits.Count == 0) return "";
            string s = string.Join("\n", hits.ToArray());
            return s.Length <= maxChars ? s : s.Substring(0, maxChars) + "…";
        }

        /// <summary>当前会话的简短上下文（给桌宠聊天注入用，避免它完全不知道工作区）。</summary>
        public static string ActiveDigest()
        {
            EnsureLoaded();
            var c = Active;
            if (c == null) return "";
            string s = "工作区当前会话：」" + c.title + "」，最近用户说："
                     + (c.LastUserText() ?? "");
            if (!string.IsNullOrEmpty(c.summary)) s += "\n摘要：" + c.summary;
            return s.Length > 400 ? s.Substring(0, 400) : s;
        }
    }
}
