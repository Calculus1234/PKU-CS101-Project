using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace HilbertLoop
{
    /// <summary>
    /// 云端大模型客户端：OpenAI 兼容的 /chat/completions + SSE 流式。
    /// 智谱 GLM-4-Flash、DeepSeek、通义、vLLM、Ollama 都可以填同一个地址格式。
    ///
    /// 与本地 LLMNpcBrain 保持同样的回调约定：
    ///  - onPartial 收到的是“累积的原始输出”，调用方用 NpcToolCallParser 自己抠台词；
    ///  - done 一定会被调用，失败时传 null。
    /// </summary>
    public static class CloudLlmClient
    {
        static UnityWebRequest _active;

        public static void Abort()
        {
            if (_active != null)
            {
                try { _active.Abort(); } catch { }
                _active = null;
            }
        }

        public static IEnumerator Generate(string systemPrompt, string userMessage,
            int maxTokens, float temperature,
            Action<string> onPartial, Action<NpcLLMReply> done)
        {
            string url = LlmSettings.BaseUrl;
            string model = LlmSettings.Model;
            string key = LlmSettings.ApiKey;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(key))
            {
                done?.Invoke(null);
                yield break;
            }

            string body = BuildBody(systemPrompt, userMessage, model, maxTokens, temperature);
            byte[] payload = Encoding.UTF8.GetBytes(body);

            var handler = new SseDownloadHandler(onPartial);
            UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = handler;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "text/event-stream");
            request.SetRequestHeader("Authorization", "Bearer " + key);
            request.timeout = 25;

            _active = request;
            float start = Time.realtimeSinceStartup;

            yield return request.SendWebRequest();

            if (_active == request) _active = null;

            string raw = handler.Raw ?? "";
            bool ok = request.result == UnityWebRequest.Result.Success;

            if (!ok)
            {
                string err = request.error;
                long code = request.responseCode;
                Debug.LogWarning($"[CloudLLM] 请求失败（HTTP {code}）：{err}");
                request.Dispose();
                done?.Invoke(null);
                yield break;
            }

            request.Dispose();

            NpcLLMReply reply = NpcToolCallParser.Parse(raw);
            if (reply != null)
                reply.elapsedSeconds = Time.realtimeSinceStartup - start;

            done?.Invoke(reply);
        }

        static string BuildBody(string systemPrompt, string userMessage, string model,
            int maxTokens, float temperature)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"model\":\"").Append(Escape(model)).Append("\",");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Escape(systemPrompt)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Escape(userMessage)).Append("\"}],");
            sb.Append("\"stream\":true,");
            sb.Append("\"temperature\":")
              .Append(temperature.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"max_tokens\":").Append(Mathf.Max(32, maxTokens)).Append(',');
            // Qwen 系云端服务认这个字段；GLM / DeepSeek 会直接忽略。
            sb.Append("\"enable_thinking\":false}");
            return sb.ToString();
        }

        static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>逐块接收 SSE，拼出累积原文；UTF-8 跨块用 Decoder 保住多字节字符。</summary>
        class SseDownloadHandler : DownloadHandlerScript
        {
            readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
            readonly StringBuilder _line = new StringBuilder(256);
            readonly StringBuilder _raw = new StringBuilder(256);
            readonly Action<string> _onPartial;
            char[] _chars = new char[8192];

            public string Raw { get { return _raw.ToString(); } }

            public SseDownloadHandler(Action<string> onPartial) : base(new byte[8192])
            {
                _onPartial = onPartial;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;

                int need = _decoder.GetCharCount(data, 0, dataLength, false);
                if (need > _chars.Length) _chars = new char[need + 256];
                int count = _decoder.GetChars(data, 0, dataLength, _chars, 0, false);

                for (int i = 0; i < count; i++)
                {
                    char c = _chars[i];
                    if (c == '\n')
                    {
                        ConsumeLine(_line.ToString());
                        _line.Length = 0;
                    }
                    else if (c != '\r')
                    {
                        _line.Append(c);
                    }
                }
                return true;
            }

            void ConsumeLine(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) return;

                string payload = line.Substring(5).Trim();
                if (payload.Length == 0) return;
                if (payload == "[DONE]") return;

                try
                {
                    JObject obj = JObject.Parse(payload);
                    JToken choices = obj["choices"];
                    if (choices == null || choices.Type != JTokenType.Array || choices.Count() == 0)
                        return;

                    JToken delta = choices[0]["delta"];
                    if (delta == null) return;

                    JToken content = delta["content"];
                    if (content == null || content.Type == JTokenType.Null) return;

                    string piece = content.ToString();
                    if (string.IsNullOrEmpty(piece)) return;

                    _raw.Append(piece);
                    if (_onPartial != null) _onPartial(_raw.ToString());
                }
                catch (Exception)
                {
                    // 单行 JSON 不完整时跳过，等下一块。
                }
            }
        }
    }
}
