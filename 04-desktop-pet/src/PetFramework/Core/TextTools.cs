using System;
using System.Text;

namespace PetFramework
{
    /// <summary>
    /// 文本清洗：把「AI 怎么操控模型」的技术痕迹从【用户能看到的地方】抹掉。
    ///
    /// 用户不该看到 play_animation(action="Walk")、&lt;tool_call&gt;…
    /// 也不该看到裸动作名（look_back / idle / walk）或者 { "thought": … } 这种 JSON。
    /// 这些内容该由代码执行掉，而不是当台词念出来。
    /// </summary>
    public static class TextTools
    {
        static readonly string[] ToolNames =
        {
            "play_animation", "move_to", "wander_around", "say_and_speak",
            "remember_rule", "feed_file", "switch_form", "read_file",
            "write_file", "list_dir", "workspace_recall"
        };

        static readonly string[] ActionWords =
        {
            "idle", "walk", "run", "look_back", "lookback", "sit", "sleep", "talk",
            "jump", "drag", "drop", "pet", "switch_form", "switchform", "dance", "wander"
        };

        /// <summary>文本里有没有汉字。</summary>
        public static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x4E00 && c <= 0x9FFF) return true;      // 基本汉字
                if (c >= 0x3400 && c <= 0x4DBF) return true;      // 扩展 A
                if (c >= 0xF900 && c <= 0xFAFF) return true;      // 兼容
            }
            return false;
        }

        /// <summary>是不是「纯英文」的一句话（没有汉字、至少有 3 个字母）。</summary>
        public static bool IsEnglishOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (HasChinese(s)) return false;
            int letters = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++;
            }
            return letters >= 3;
        }

        /// <summary>整行是不是「一个裸动作名」（可带前导 /）。</summary>
        public static bool IsBareActionWord(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string s = line.Trim();
            if (s.StartsWith("/")) s = s.Substring(1).Trim();
            s = s.Trim('`', '"', '\'', '.', ',', '。', '，');
            if (s.Length == 0 || s.Length > 24) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
                if (c > 127) return false;          // 只有 ASCII 才可能是动作名
            }
            string low = s.ToLowerInvariant();
            for (int i = 0; i < ActionWords.Length; i++)
                if (low == ActionWords[i]) return true;
            return false;
        }

        public static bool LooksLikeToolCall(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string s = line.Trim();
            if (s.Length == 0) return false;

            if (s.IndexOf("<tool_call>", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("<function=", StringComparison.Ordinal) >= 0) return true;
            if (IsBareActionWord(s)) return true;

            // JSON 片段
            if (s.StartsWith("{") || s.StartsWith("}") || s.StartsWith("[") || s.StartsWith("]")) return true;
            if ((s.StartsWith("\"") || s.StartsWith("'")) && s.IndexOf(':') > 0 && s.Length < 200) return true;
            if (s.StartsWith("`") && s.IndexOf(':') > 0) return true;

            // 工具名 + 括号
            for (int i = 0; i < ToolNames.Length; i++)
            {
                string n = ToolNames[i];
                if (s.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                {
                    int p = n.Length;
                    while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                    if (p < s.Length && (s[p] == '(' || s[p] == ':')) return true;
                }
            }
            if (s.IndexOf("play_animation(", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("move_to(", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("wander_around(", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("action=\"", StringComparison.Ordinal) >= 0 && s.Length < 120) return true;

            return false;
        }

        /// <summary>把整段文本里的工具调用/JSON/裸动作名删掉；剩下的就是“人话”。</summary>
        public static string StripToolSyntax(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1) 先去 <tool_call>…</tool_call>
            string s = text.Replace("\r", "").Replace("```json", "").Replace("```", "");
            while (true)
            {
                int a = s.IndexOf("<tool_call>", StringComparison.Ordinal);
                if (a < 0) break;
                int b = s.IndexOf("</tool_call>", a, StringComparison.Ordinal);
                if (b < 0) { s = s.Substring(0, a); break; }
                s = s.Substring(0, a) + s.Substring(b + 12);
            }

            // 2) 去掉跨行 JSON 块（{"thought": …, "action": …}）
            s = StripJsonBlocks(s);

            // 3) 逐行过滤
            var lines = s.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (LooksLikeToolCall(line)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.ToString().Trim();
        }

        /// <summary>从一个行首 '{' 开始按括号配对吃掉整段 JSON（可跨多行）。</summary>
        static string StripJsonBlocks(string s)
        {
            if (s.IndexOf('{') < 0) return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                // 行首（或全文开头）出现 { 才当 JSON 块处理
                bool atLineStart = i == 0 || s[i - 1] == '\n';
                if (s[i] == '{' && atLineStart)
                {
                    int depth = 0, j = i;
                    bool ok = false;
                    for (; j < s.Length; j++)
                    {
                        if (s[j] == '{') depth++;
                        else if (s[j] == '}') { depth--; if (depth == 0) { ok = true; break; } }
                    }
                    if (ok)
                    {
                        // 吃掉块后面的换行
                        int k = j + 1;
                        if (k < s.Length && s[k] == '\n') k++;
                        i = k;
                        continue;
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>给 UI 用：清洗 + 限制长度（null/空返回空串）。</summary>
        public static string ForDisplay(string text, int max = 0)
        {
            string s = StripToolSyntax(text);
            if (max > 0 && s.Length > max) s = s.Substring(0, max) + "…";
            return s;
        }
    }
}
