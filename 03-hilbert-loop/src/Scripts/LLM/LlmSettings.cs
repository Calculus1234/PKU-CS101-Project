using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 大模型运行配置。密钥只存在本机 PlayerPrefs，不写进代码和仓库。
    /// 云端走 OpenAI 兼容接口（智谱 GLM-4-Flash、DeepSeek、通义等都可以填）。
    /// </summary>
    public static class LlmSettings
    {
        const string KeyMode = "llm.mode";              // 0 = 本地 GGUF，1 = 云端 API
        const string KeyBaseUrl = "llm.baseUrl";
        const string KeyModel = "llm.model";
        const string KeyApiKey = "llm.apiKey";
        const string KeyLocalModel = "LLM_Local_Model_Choice";   // 本地模型文件名

        public const string DefaultBaseUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
        public const string DefaultModel = "glm-4-flash";

        public const string ModelFast = "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf";
        public const string ModelSmart = "Qwen3.5-4B-Q4_K_M.gguf";

        public static bool UseCloud
        {
            get { return PlayerPrefs.GetInt(KeyMode, 0) == 1; }
            set { PlayerPrefs.SetInt(KeyMode, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static string BaseUrl
        {
            get { return PlayerPrefs.GetString(KeyBaseUrl, DefaultBaseUrl); }
            set { PlayerPrefs.SetString(KeyBaseUrl, value ?? ""); PlayerPrefs.Save(); }
        }

        public static string Model
        {
            get { return PlayerPrefs.GetString(KeyModel, DefaultModel); }
            set { PlayerPrefs.SetString(KeyModel, value ?? ""); PlayerPrefs.Save(); }
        }

        public static string ApiKey
        {
            get { return PlayerPrefs.GetString(KeyApiKey, ""); }
            set { PlayerPrefs.SetString(KeyApiKey, value ?? ""); PlayerPrefs.Save(); }
        }

        /// <summary>本地模型选择：默认极速版，启动时直接加载上次的选择。</summary>
        public static string LocalModel
        {
            get
            {
                string value = PlayerPrefs.GetString(KeyLocalModel, ModelFast);
                return string.IsNullOrEmpty(value) ? ModelFast : value;
            }
            set
            {
                PlayerPrefs.SetString(KeyLocalModel, string.IsNullOrEmpty(value) ? ModelFast : value);
                PlayerPrefs.Save();
            }
        }

        public static bool UseSmartModel
        {
            get { return LocalModel == ModelSmart; }
        }

        /// <summary>云端模式可用：开了云端，并且地址、模型、密钥都不为空。</summary>
        public static bool CloudReady
        {
            get
            {
                return UseCloud
                       && !string.IsNullOrEmpty(BaseUrl)
                       && !string.IsNullOrEmpty(Model)
                       && !string.IsNullOrEmpty(ApiKey);
            }
        }
    }
}
