using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using PetFramework;

namespace PetFramework
{
    /// <summary>
    /// 【终端用户的接口配置】—— 让完全不懂 Unity 的人也能换 API。
    ///
    /// 数据流：config.json（EXE 同级） ⟷ 本组件（编辑缓冲） ⟷ PetAPI（真正发请求的那位）
    /// 面板（PetConfigUI，Esc 呼出）改的就是这里的字段，点保存 → 写文件 + 立刻套用到 PetAPI。
    ///
    /// ⚠️ config.json 不存在时：用本组件的 default* 值（= 场景里配好的本地 Ollama），
    ///    并自动生成一份模板文件，用户照着改就行。
    /// </summary>
    public class PetApiConfig : MonoBehaviour
    {
        public static PetApiConfig Instance { get; private set; }

        [Header("配置文件（留空 = EXE 同级 config.json）")]
        public string settingsPathOverride = "";
        public string fileName = "config.json";
        public bool autoReload = true;

        [Header("默认值（配置文件不存在时用它，并写进自动生成的模板）")]
        public PetApiProvider defaultProvider = PetApiProvider.Ollama;
        public string defaultBaseUrl = "http://localhost:11434";
        public string defaultModel = "qwen3.5:4b";
        public string defaultApiKey = "";
        [Tooltip("默认是否让模型「思考」。⚠️ 本地 4B 开思考实测单次要 60 秒以上，默认关")]
        public bool defaultEnableThinking = false;

        // ---------------- 编辑缓冲（面板改这些）----------------
        public PetApiProvider provider = PetApiProvider.Ollama;
        public string baseUrl = "http://localhost:11434";
        public string model = "qwen3.5:4b";
        public string apiKey = "";
        [Tooltip("可选：聊天接口路径。留空 = 按协议自动补（OpenAI 是 /v1/chat/completions，Ollama 是 /api/chat）。" +
                 "本地推理服务路径不一样时填这里，例如 /v1/chat/completions")]
        public string chatPath = "";
        [Tooltip("模型思考开关：开 = 更聪明但更慢；关 = 快，适合 4B 小模型。落盘在 config.json")]
        public bool enableThinking = false;

        public string SettingsPath { get; private set; }
        public string Status { get; private set; }

        long lastWriteTicks = -1;
        float nextWatch;
        bool ready;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            SettingsPath = PetPathUtil.ResolveBesideExe(fileName, settingsPathOverride);
            Load();
        }

        IEnumerator Start()
        {
            yield return null;          // 等 PetAPI.Awake 先跑（它要设 Instance）
            ApplyToApi();
            ready = true;
        }

        void Update()
        {
            if (!autoReload || !ready) return;
            if (Time.time < nextWatch) return;
            nextWatch = Time.time + 2f;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                long t = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc();
                if (t != lastWriteTicks)
                {
                    if (lastWriteTicks >= 0) Debug.Log("[PetApiConfig] config.json 被外部改动 → 重新读取");
                    Load();
                    ApplyToApi();
                }
            }
            catch { }
        }

        // ---------------- 读 ----------------

        /// <summary>读 config.json；没有就落回默认值并生成模板。</summary>
        public void Load()
        {
            if (!File.Exists(SettingsPath))
            {
                provider = defaultProvider;
                baseUrl = defaultBaseUrl;
                model = defaultModel;
                apiKey = defaultApiKey;
                chatPath = "";
                enableThinking = defaultEnableThinking;
                Status = "没有配置文件，用场景默认值";
                Debug.Log("[PetApiConfig] " + Status + "（将生成模板 " + SettingsPath + "）");
                Save(false);            // 生成模板（不覆盖已有值）
                return;
            }

            try
            {
                var d = PetVisualRoot.ScanFlatJson(File.ReadAllText(SettingsPath));
                string s;

                provider = defaultProvider;
                if (d.TryGetValue("provider", out s)) provider = ParseProvider(s, provider);
                else if (d.TryGetValue("Provider", out s)) provider = ParseProvider(s, provider);

                baseUrl = Pick(d, "baseUrl", "BaseUrl", defaultBaseUrl);
                model = Pick(d, "model", "Model", defaultModel);
                apiKey = Pick(d, "apiKey", "ApiKey", defaultApiKey);
                // 本地推理服务（ONW / vLLM / LM Studio …）的聊天路径可能不是标准的 /v1/chat/completions
                chatPath = Pick(d, "chatPath", "endpoint", "");
                enableThinking = PickBool(d, "enableThinking", "EnableThinking", defaultEnableThinking);

                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                Status = "已读取 " + SettingsPath;
                Debug.Log("[PetApiConfig] ★ " + Status + " | 协议=" + provider + " 模型=" + model
                          + " 端点=" + baseUrl + (string.IsNullOrEmpty(chatPath) ? "" : chatPath)
                          + " Key=" + (string.IsNullOrEmpty(apiKey) ? "无" : "有")
                          + " 思考=" + (enableThinking ? "开" : "关"));
            }
            catch (System.Exception e)
            {
                Status = "读取失败：" + e.Message;
                Debug.LogWarning("[PetApiConfig] " + Status);
            }
        }

        static string Pick(Dictionary<string, string> d, string k1, string k2, string def)
        {
            string s;
            if (d.TryGetValue(k1, out s) && !string.IsNullOrEmpty(s)) return s;
            if (d.TryGetValue(k2, out s) && !string.IsNullOrEmpty(s)) return s;
            return def;
        }

        /// <summary>布尔键：接受 true/false/1/0/on/off/yes/no/开/关。</summary>
        static bool PickBool(Dictionary<string, string> d, string k1, string k2, bool def)
        {
            string s;
            if (!d.TryGetValue(k1, out s)) d.TryGetValue(k2, out s);
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().Trim('"').ToLowerInvariant();
            if (s == "1" || s == "true" || s == "on" || s == "yes" || s == "开" || s == "是") return true;
            if (s == "0" || s == "false" || s == "off" || s == "no" || s == "关" || s == "否") return false;
            return def;
        }

        public static PetApiProvider ParseProvider(string s, PetApiProvider def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().ToLowerInvariant();
            // OpenAI 兼容（云端 + 本地推理服务）：
            //   openai / 云端 / 硅基流动 …；onw（NPU 推理服务）/ vllm / lmstudio / llama.cpp / ollama-openai …
            // 这些服务说的都是 OpenAI 协议，只是地址不同 —— 不能因为不认识就掉回 Ollama，
            // 否则会去请求 /api/chat 直接 404（"改了配置没反应"就是这么来的）。
            if (s.StartsWith("openai") || s == "1" || s == "cloud" || s == "云端"
                || s == "onw" || s == "npu" || s == "vllm" || s == "lmstudio" || s == "lm-studio"
                || s == "llama.cpp" || s == "llamacpp" || s == "oai" || s == "openai-compatible"
                || s == "openai兼容" || s == "兼容" || s == "sglang" || s == "xinference")
                return PetApiProvider.OpenAI;
            if (s.StartsWith("ollama") || s == "0" || s == "local" || s == "本地") return PetApiProvider.Ollama;
            return def;
        }

        // ---------------- 写 ----------------

        /// <summary>面板点【保存】走这里：写文件 + 立刻套用。</summary>
        public void SaveAndApply()
        {
            Save(true);
            ApplyToApi();
            Status = "已保存并生效 → " + SettingsPath;
            Debug.Log("[PetApiConfig] ★ " + Status);
        }

        public void Save() { Save(true); }

        void Save(bool overwrite)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"_readme\": \"桌宠的接口配置。改完保存这个文件、重启即生效；也可以按 Esc 在游戏里改。\",\n");
                sb.Append("  \"_provider_说明\": \"ollama = 本地 Ollama；openai = OpenAI 兼容（云端硅基流动 / 本地 ONW、vLLM、LM Studio 等）\",\n");
                sb.Append("  \"provider\": \"").Append(provider == PetApiProvider.OpenAI ? "openai" : "ollama").Append("\",\n");
                sb.Append("  \"baseUrl\": \"").Append(JsonEsc(baseUrl)).Append("\",\n");
                sb.Append("  \"model\": \"").Append(JsonEsc(model)).Append("\",\n");
                sb.Append("  \"apiKey\": \"").Append(JsonEsc(apiKey)).Append("\",\n");
                sb.Append("  \"_chatPath_说明\": \"留空 = 自动（OpenAI 补 /v1/chat/completions，Ollama 补 /api/chat）；服务路径不同时手填\",\n");
                sb.Append("  \"chatPath\": \"").Append(JsonEsc(chatPath)).Append("\",\n");
                sb.Append("  \"_enableThinking_说明\": \"true = 让模型先思考再回答（更聪明但慢，本地 4B 会 60 秒+）；false = 直接答（快）\",\n");
                sb.Append("  \"enableThinking\": ").Append(enableThinking ? "true" : "false").Append("\n");
                sb.Append("}\n");

                File.WriteAllText(SettingsPath, sb.ToString());
                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                if (overwrite) Status = "已保存 → " + SettingsPath;
            }
            catch (System.Exception e)
            {
                Status = "保存失败：" + e.Message;
                Debug.LogWarning("[PetApiConfig] " + Status);
            }
        }

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ---------------- 套用到 PetAPI ----------------

        /// <summary>把当前配置写进 PetAPI —— 之后的聊天/截图/工具调用就都走新配置了。</summary>
        public void ApplyToApi()
        {
            var api = PetAPI.Instance;
            if (api == null)
            {
    #if UNITY_2022_2_OR_NEWER
                api = UnityEngine.Object.FindFirstObjectByType<PetAPI>();
    #else
                api = UnityEngine.Object.FindObjectOfType<PetAPI>();
    #endif
            }
            if (api == null) { Status = "找不到 PetAPI"; return; }

            api.provider = provider;
            api.baseUrl = baseUrl;
            api.model = model;
            api.apiKey = apiKey;
            api.chatPath = chatPath;
            api.disableThinking = !enableThinking;   // 面板上是「启用思考」，PetAPI 里是反向的开关

            Debug.Log("[PetApiConfig] 已套用到 PetAPI → " + api.provider + " " + api.Endpoint()
                      + " 模型=" + api.model + " Key=" + api.KeyHint()
                      + " 思考=" + (enableThinking ? "开" : "关"));
        }

        /// <summary>把 PetAPI 上当前实际生效的值拉回编辑缓冲（第一次打开面板时同步一下）。</summary>
        public void PullFromApi()
        {
            var api = PetAPI.Instance;
            if (api == null) return;
            provider = api.provider;
            baseUrl = api.baseUrl;
            model = api.model;
            apiKey = api.apiKey;
            chatPath = api.chatPath ?? "";
            enableThinking = !api.disableThinking;
        }

        /// <summary>给面板显示的一行摘要。</summary>
        public string Readable()
        {
            return (provider == PetApiProvider.OpenAI ? "云端(OpenAI兼容)" : "本地(Ollama)")
                 + " | " + baseUrl + " | " + model
                 + " | Key " + (string.IsNullOrEmpty(apiKey) ? "未填" : "已填")
                 + " | 思考 " + (enableThinking ? "开" : "关");
        }
    }
}
