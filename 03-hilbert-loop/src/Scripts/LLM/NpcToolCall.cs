using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 一次 LLM 回合的解析结果。
    /// 台词由 ProvideRebuttal 承载；其余工具调用按需附带。
    /// </summary>
    [Serializable]
    public class NpcLLMReply
    {
        public string speech = "";
        public string raw = "";
        public float elapsedSeconds;
        public bool success;
        public readonly List<NpcToolCall> calls = new List<NpcToolCall>();

        public bool HasCall(string name)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                if (string.Equals(calls[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public NpcToolCall GetCall(string name)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                if (string.Equals(calls[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return calls[i];
            }
            return null;
        }
    }

    /// <summary>五个可被 LLM 调用的工具。</summary>
    [Serializable]
    public class NpcToolCall
    {
        public string name = "";
        public string text = "";
        public string evidenceId = "";
        public int targetNumber;
        public string emotion = "";
        public int suspicionDelta;
        public int hostilityDelta;
    }

    /// <summary>
    /// 把 Qwen 的紧凑 JSON 输出解析成工具调用。
    /// 兼容两种格式：
    ///  1) 紧凑字段：{"say":"...","emotion":"tremble","evidence":"wall_nails_1_L","vote":1,...}
    ///  2) 显式函数调用：{"calls":[{"name":"ChangeEmotion","arguments":{"emotion":"angry"}}]}
    /// 模型跑偏输出纯文本时，把整段文本当作 ProvideRebuttal，保证不会因为格式问题卡死。
    /// </summary>
    public static class NpcToolCallParser
    {
        public static NpcLLMReply Parse(string raw)
        {
            var reply = new NpcLLMReply { raw = raw ?? "" };
            string text = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return null;

            text = StripCodeFences(text);
            string json = ExtractJsonObject(text);

            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    JObject obj = JObject.Parse(json);
                    ParseObject(obj, reply);
                }
                catch (Exception)
                {
                    // 模型偶尔把 JSON 的引号多转义一层（\"say\"），
                    // 或者被 GBNF 的旧版本诱导成这种写法。先尝试修复再解析。
                    bool repairedOk = false;
                    try
                    {
                        string repaired = json.Replace("\\\"", "\"");
                        if (!string.Equals(repaired, json, StringComparison.Ordinal))
                        {
                            JObject obj = JObject.Parse(repaired);
                            ParseObject(obj, reply);
                            repairedOk = true;
                        }
                    }
                    catch (Exception)
                    {
                        // 继续走半截字段提取。
                    }

                    if (!repairedOk)
                    {
                        string partial = ExtractStringField(text.Replace("\\\"", "\""), "say");
                        if (string.IsNullOrEmpty(partial)) partial = ExtractStringField(text.Replace("\\\"", "\""), "text");
                        if (!string.IsNullOrEmpty(partial)) AddSpeech(reply, partial);
                    }
                }
            }
            else
            {
                // 不是 JSON：当作纯台词，至少给出一个人话。
                AddSpeech(reply, text);
            }

            if (string.IsNullOrEmpty(reply.speech) && reply.calls.Count == 0)
                return null;

            reply.success = !string.IsNullOrEmpty(reply.speech) || reply.calls.Count > 0;
            return reply;
        }

        static void ParseObject(JObject obj, NpcLLMReply reply)
        {
            // 1) 显式 calls 数组
            JToken callsToken = obj["calls"] ?? obj["tool_calls"];
            if (callsToken is JArray callsArray)
            {
                for (int i = 0; i < callsArray.Count; i++)
                {
                    if (callsArray[i] is JObject callObj)
                        ParseNamedCall(callObj, reply);
                }
            }

            // 2) 单个 name / tool 对象
            string singleName = GetString(obj, "name") ?? GetString(obj, "tool") ?? GetString(obj, "function");
            if (!string.IsNullOrEmpty(singleName))
            {
                JObject args = obj["arguments"] as JObject ?? obj["args"] as JObject;
                NpcToolCall call = BuildNamedCall(singleName, args ?? obj);
                if (call != null) AddCall(reply, call);
            }

            // 3) 紧凑字段
            string speech = GetString(obj, "say")
                            ?? GetString(obj, "speech")
                            ?? GetString(obj, "rebuttal")
                            ?? GetString(obj, "line");
            if (!string.IsNullOrEmpty(speech)) AddSpeech(reply, speech);

            string emotion = GetString(obj, "emotion");
            if (!string.IsNullOrEmpty(emotion) && !IsNone(emotion))
            {
                AddCall(reply, new NpcToolCall { name = "ChangeEmotion", emotion = emotion });
            }

            string evidence = GetString(obj, "evidence")
                              ?? GetString(obj, "evidenceId")
                              ?? GetString(obj, "evidence_id");
            if (!string.IsNullOrEmpty(evidence) && !IsNone(evidence))
            {
                AddCall(reply, new NpcToolCall { name = "PresentEvidence", evidenceId = evidence });
            }

            int vote = GetInt(obj, "vote") ?? GetInt(obj, "voteTarget") ?? 0;
            if (vote > 0)
                AddCall(reply, new NpcToolCall { name = "CastVote", targetNumber = vote });

            int target = GetInt(obj, "target") ?? GetInt(obj, "targetNumber") ?? 0;
            int suspicion = GetInt(obj, "suspicion") ?? GetInt(obj, "suspicionDelta") ?? 0;
            int hostility = GetInt(obj, "hostility") ?? GetInt(obj, "hostilityDelta") ?? 0;
            // target 允许为 0：4B 经常漏写目标，私聊里执行器会按玩家结算，
            // 会议里对象不唯一则忽略。这里不能因为 target=0 就把数值丢掉。
            if (suspicion != 0 || hostility != 0)
            {
                AddCall(reply, new NpcToolCall
                {
                    name = "ChangeStat",
                    targetNumber = target,
                    suspicionDelta = suspicion,
                    hostilityDelta = hostility,
                });
            }
        }

        static void ParseNamedCall(JObject callObj, NpcLLMReply reply)
        {
            string name = GetString(callObj, "name") ?? GetString(callObj, "tool") ?? GetString(callObj, "function");
            if (string.IsNullOrEmpty(name)) return;

            JObject args = callObj["arguments"] as JObject ?? callObj["args"] as JObject ?? callObj;
            NpcToolCall call = BuildNamedCall(name, args);
            if (call != null) AddCall(reply, call);
        }

        static NpcToolCall BuildNamedCall(string name, JObject args)
        {
            string normalized = NormalizeName(name);
            var call = new NpcToolCall { name = normalized };

            switch (normalized)
            {
                case "ProvideRebuttal":
                    call.text = GetString(args, "text") ?? GetString(args, "say") ?? GetString(args, "line") ?? "";
                    return call;
                case "PresentEvidence":
                    call.evidenceId = GetString(args, "evidenceId") ?? GetString(args, "evidence")
                                      ?? GetString(args, "id") ?? "";
                    return call;
                case "CastVote":
                    call.targetNumber = GetInt(args, "targetNumber") ?? GetInt(args, "target")
                                        ?? GetInt(args, "vote") ?? 0;
                    return call;
                case "ChangeEmotion":
                    call.emotion = GetString(args, "emotion") ?? GetString(args, "mood") ?? "";
                    return call;
                case "ChangeStat":
                    call.targetNumber = GetInt(args, "targetNumber") ?? GetInt(args, "target") ?? 0;
                    call.suspicionDelta = GetInt(args, "suspicionDelta") ?? GetInt(args, "suspicion") ?? 0;
                    call.hostilityDelta = GetInt(args, "hostilityDelta") ?? GetInt(args, "hostility") ?? 0;
                    return call;
                default:
                    return null;
            }
        }

        static string NormalizeName(string name)
        {
            string n = (name ?? "").Trim().Trim('"', '\'');
            if (n.Equals("ProvideRebuttal", StringComparison.OrdinalIgnoreCase)) return "ProvideRebuttal";
            if (n.Equals("PresentEvidence", StringComparison.OrdinalIgnoreCase)) return "PresentEvidence";
            if (n.Equals("CastVote", StringComparison.OrdinalIgnoreCase)) return "CastVote";
            if (n.Equals("ChangeEmotion", StringComparison.OrdinalIgnoreCase)) return "ChangeEmotion";
            if (n.Equals("ChangeStat", StringComparison.OrdinalIgnoreCase)) return "ChangeStat";
            return n;
        }

        static void AddSpeech(NpcLLMReply reply, string text)
        {
            string clean = SanitizeSpeech(text);
            if (string.IsNullOrEmpty(clean)) return;

            // 模型有时同时给出紧凑字段 say 和显式 ProvideRebuttal，
            // 两者内容相同，不能把同一句台词拼两遍。
            if (!string.IsNullOrEmpty(reply.speech))
            {
                if (reply.speech == clean
                    || reply.speech.Contains(clean)
                    || clean.Contains(reply.speech))
                    return;
                reply.speech += " ";
            }
            reply.speech += clean;

            if (!reply.HasCall("ProvideRebuttal"))
            {
                reply.calls.Insert(0, new NpcToolCall
                {
                    name = "ProvideRebuttal",
                    text = clean,
                });
            }
        }

        static void AddCall(NpcLLMReply reply, NpcToolCall call)
        {
            if (call == null || string.IsNullOrEmpty(call.name)) return;

            // ProvideRebuttal 与 speech 是同一件事，避免重复。
            if (call.name == "ProvideRebuttal")
            {
                AddSpeech(reply, call.text);
                return;
            }

            for (int i = 0; i < reply.calls.Count; i++)
            {
                if (reply.calls[i].name == call.name)
                    return;
            }
            reply.calls.Add(call);
        }

        public static string SanitizeSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            s = s.Trim('"', '“', '”', '\'');

            // 有些模型会自作主张加“8号：”“你：”“我：”，UI 会再加一次，先剥掉。
            // 连续剥两层，兼容“3号：你：……”这种叠在一起的输出。
            for (int i = 0; i < 2; i++)
            {
                string stripped = Regex.Replace(s,
                    @"^\s*(?:\d+\s*号|玩家|你|我|NPC|旁白)\s*[：:]\s*", "",
                    RegexOptions.IgnoreCase);
                if (stripped == s) break;
                s = stripped;
            }
            s = s.Replace("\r", " ").Replace("\n", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            if (s.Length > 160) s = s.Substring(0, 160).TrimEnd('，', ',', '。', ' ') + "……";

            // 英文模式下模型偶尔仍会冒中文。词典能翻就翻；翻不了就用英文兜底，
            // 保证任何情况下都不会把中文台词送进 UI。
            if (Localization.IsEnglish && Regex.IsMatch(s, "[\\u4e00-\\u9fff]"))
            {
                string translated = Localization.Translate(s);
                if (!string.IsNullOrEmpty(translated) && !Regex.IsMatch(translated, "[\\u4e00-\\u9fff]"))
                {
                    s = translated;
                }
                else
                {
                    Debug.LogWarning("[LLM] 英文模式下模型输出了中文台词，已替换为英文兜底：" + s);
                    s = "…That's all I can say. If you don't believe me, check the footage yourself.";
                }
            }
            return s.Trim();
        }

        static bool IsNone(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string v = value.Trim().ToLowerInvariant();
            return v == "none" || v == "null" || v == "0" || v == "无" || v == "不要";
        }

        static string StripCodeFences(string text)
        {
            string s = text.Trim();
            if (s.StartsWith("```"))
            {
                int firstLine = s.IndexOf('\n');
                if (firstLine >= 0) s = s.Substring(firstLine + 1);
                int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) s = s.Substring(0, lastFence);
            }
            return s.Trim();
        }

        static string ExtractJsonObject(string text)
        {
            int start = text.IndexOf('{');
            if (start < 0) return "";

            // 只取第一个花括号配平的对象，避免把 JSON 后面的 markdown 说明一起吞进来。
            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }

            // 输出被截断时保留剩余部分，交给半截字段提取。
            return text.Substring(start).Trim();
        }

        static string ExtractStringField(string text, string field)
        {
            Match m = Regex.Match(text,
                "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.Singleline);
            if (!m.Success) return "";
            return Regex.Unescape(m.Groups[1].Value);
        }

        /// <summary>流式输出时从半截 JSON 里抠出台词，用于边生成边显示。</summary>
        public static bool TryExtractSpeechPartial(string raw, out string speech)
        {
            speech = "";
            if (string.IsNullOrEmpty(raw)) return false;

            int key = raw.IndexOf("\"say\"", StringComparison.Ordinal);
            if (key < 0) return false;
            int colon = raw.IndexOf(':', key);
            if (colon < 0) return false;
            int quote = raw.IndexOf('"', colon + 1);
            if (quote < 0) return false;

            var sb = new StringBuilder();
            bool escape = false;
            for (int i = quote + 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (escape)
                {
                    sb.Append(c == 'n' ? ' ' : c);
                    escape = false;
                    continue;
                }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            speech = SanitizeSpeech(sb.ToString());
            return !string.IsNullOrEmpty(speech);
        }

        static string GetString(JObject obj, string key)
        {
            if (obj == null) return null;
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            string value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static int? GetInt(JObject obj, string key)
        {
            if (obj == null) return null;
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return Mathf.RoundToInt(token.Value<float>());
            if (token.Type == JTokenType.String)
            {
                string s = token.Value<string>().Trim();
                if (int.TryParse(s, out int value)) return value;
            }
            return null;
        }
    }

    /// <summary>函数调用说明（拼进 System Prompt，供模型选择工具）。</summary>
    public static class NpcToolRegistry
    {
        public static string Describe()
        {
            return
                "你可以调用以下五个工具（不需要真的发函数调用，只要在 JSON 字段里填对应值）：\n" +
                "1. ProvideRebuttal(text)：你的台词，写入 say。\n" +
                "2. PresentEvidence(evidenceId)：甩出你确实知道的证据，写入 evidence。\n" +
                "3. CastVote(targetNumber)：投票给某号，写入 vote。\n" +
                "4. ChangeEmotion(emotion)：改变表情/动作，写入 emotion（none/tremble/retreat/angry/calm）。\n" +
                "5. ChangeStat(targetNumber, suspicionDelta, hostilityDelta)：改变对某号的怀疑/敌意，写入 target/suspicion/hostility。\n";
        }
    }

    /// <summary>
    /// GBNF 语法：约束模型只输出紧凑 JSON，避免 4B 模型写出一堆解释性文字。
    /// 字段顺序固定为「数值 / 工具在前，say 在最后」。
    ///
    /// 这样排序是被截断逼出来的：maxTokens 有限，4B 写中文台词很费 token。
    /// 以前 say 在最前，64 token 常常只够写完台词，JSON 在 "suspicion" 处断掉，
    /// 怀疑 / 敌意 / 投票全部丢失。现在工具字段先输出，即使 say 被截断，
    /// 解析器仍能拿到数值，台词再走半截提取。
    /// </summary>
    public static class NpcToolGrammar
    {
        static readonly string Q = "\"";

        static string Lit(string value)
        {
            return Q + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + Q;
        }

        public static readonly string Gbnf = string.Join("\n", new[]
        {
            "root ::= " + Lit("{") + " ws " + key("emotion") + " ws " + Lit(":") + " ws emo ws " + Lit(",") + " ws " +
                key("vote") + " ws " + Lit(":") + " ws int ws " + Lit(",") + " ws " +
                key("evidence") + " ws " + Lit(":") + " ws string ws " + Lit(",") + " ws " +
                key("target") + " ws " + Lit(":") + " ws int ws " + Lit(",") + " ws " +
                key("suspicion") + " ws " + Lit(":") + " ws int ws " + Lit(",") + " ws " +
                key("hostility") + " ws " + Lit(":") + " ws int ws " + Lit(",") + " ws " +
                key("say") + " ws " + Lit(":") + " ws string ws " + Lit("}"),
            "string ::= " + Lit("\"") + " char* " + Lit("\""),
            "char ::= [^\"\\\\\\n\\r] | " + Lit("\\") + " [\"\\\\/bfnrt]",
            "emo ::= " + Lit("\"none\"") + " | " + Lit("\"tremble\"") + " | " +
                Lit("\"retreat\"") + " | " + Lit("\"angry\"") + " | " + Lit("\"calm\""),
            "int ::= " + Lit("-") + "? [0-9]+",
            "ws ::= [ \\t\\n]*",
        });

        static string key(string name)
        {
            return Lit("\"" + name + "\"");
        }
    }
}
