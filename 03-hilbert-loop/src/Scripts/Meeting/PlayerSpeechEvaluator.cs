using System.Text.RegularExpressions;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家发言质量评估（0~1）。会议自由发言与私聊自由发言共用。
    /// 评估只看“说得够不够具体”：有没有点名、有没有时间、有没有物证、有没有因果。
    /// 它不判断玩家说得对不对，只决定听众愿不愿意认真考虑。
    /// 中英双语都要能识别，否则英文模式下的发言永远被当成空话。
    /// </summary>
    public static class PlayerSpeechEvaluator
    {
        static readonly string[] EvidenceWords =
        {
            "窗", "钉", "绳", "磁", "锁", "匕首", "刀", "锤",
            "监控", "录像", "面具", "血", "脚印", "房间", "门"
        };

        static readonly string[] EvidenceWordsEn =
        {
            "window", "nail", "rope", "magnet", "lock", "dagger", "knife", "hammer",
            "surveillance", "footage", "camera", "video", "mask", "blood", "footprint",
            "room", "door"
        };

        static readonly string[] LogicWords =
        {
            "因为", "所以", "说明", "证明", "对不上", "矛盾",
            "时间线", "顺序", "不可能", "如果"
        };

        static readonly string[] LogicWordsEn =
        {
            "because", "since", "therefore", "so", "means", "prove", "proof",
            "contradiction", "contradicts", "timeline", "order", "impossible", "if"
        };

        static readonly string[] HostileWords =
        {
            "闭嘴", "蠢", "滚", "杀了你", "弄死你", "废物"
        };

        static readonly string[] HostileWordsEn =
        {
            "shut up", "idiot", "get lost", "kill you", "useless", "moron", "stupid"
        };

        static readonly Regex NumberTagRegex = new Regex(
            @"\b(?:no\.?|number|npc|player)\s*(\d{1,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex ArabicNumberTagRegex = new Regex(
            @"(\d{1,2})\s*[号號]",
            RegexOptions.Compiled);

        static readonly Regex ChineseNumberTagRegex = new Regex(
            @"([一二三四五六七八九十])\s*[号號]",
            RegexOptions.Compiled);

        public static float Score(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            string raw = text;
            string t = text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            float score = 0.18f;

            if (ContainsNumberReference(t)) score += 0.22f;
            if (ContainsTimeReference(t)) score += 0.20f;

            // 中文按无空格串匹配；英文必须用原串，否则 "at the window" 被压成
            // "atthewindow"，单词边界全部失效，物证永远得不到加分。
            int evidenceHits = CountAny(t, EvidenceWords) + CountEnglishWords(raw, EvidenceWordsEn);
            if (evidenceHits >= 1) score += 0.18f;
            if (evidenceHits >= 2) score += 0.08f;

            int logicHits = CountAny(t, LogicWords) + CountEnglishWords(raw, LogicWordsEn);
            if (logicHits >= 1) score += 0.16f;

            if (t.Length >= 15) score += 0.06f;
            if (t.Length >= 40) score += 0.06f;

            int hostileHits = CountAny(t, HostileWords) + CountEnglishWords(raw, HostileWordsEn);
            if (hostileHits >= 1) score -= 0.25f;

            return Mathf.Clamp01(score);
        }

        /// <summary>
        /// 找出玩家最先点到的号码。支持 "1号 / 一号 / No. 1 / No.1 / Number 1 / NPC 1"。
        /// 返回 0 表示没有点名；excludeNumber 用于排除玩家自己。
        /// </summary>
        public static int FindMentionedNumber(string text, int excludeNumber)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int best = 0;
            int bestIndex = int.MaxValue;

            ConsiderMatches(NumberTagRegex, text, excludeNumber, ref best, ref bestIndex);
            ConsiderMatches(ArabicNumberTagRegex, text, excludeNumber, ref best, ref bestIndex);
            foreach (Match m in ChineseNumberTagRegex.Matches(text))
            {
                int n = ChineseNumeralToInt(m.Groups[1].Value[0]);
                if (n <= 0 || n == excludeNumber) continue;
                if (m.Index < bestIndex)
                {
                    bestIndex = m.Index;
                    best = n;
                }
            }

            return best;
        }

        /// <summary>文本里是否点到了指定号码（不限第一次出现）。</summary>
        public static bool MentionsNumber(string text, int number)
        {
            if (string.IsNullOrEmpty(text) || number <= 0) return false;

            foreach (Match m in NumberTagRegex.Matches(text))
            {
                int n;
                if (int.TryParse(m.Groups[1].Value, out n) && n == number) return true;
            }
            foreach (Match m in ArabicNumberTagRegex.Matches(text))
            {
                int n;
                if (int.TryParse(m.Groups[1].Value, out n) && n == number) return true;
            }
            foreach (Match m in ChineseNumberTagRegex.Matches(text))
            {
                if (ChineseNumeralToInt(m.Groups[1].Value[0]) == number) return true;
            }
            return false;
        }

        static void ConsiderMatches(Regex regex, string text, int excludeNumber,
            ref int best, ref int bestIndex)
        {
            foreach (Match m in regex.Matches(text))
            {
                int n;
                if (!int.TryParse(m.Groups[1].Value, out n)) continue;
                if (n <= 0 || n > 10 || n == excludeNumber) continue;
                if (m.Index >= bestIndex) continue;
                bestIndex = m.Index;
                best = n;
            }
        }

        static int ChineseNumeralToInt(char c)
        {
            const string numerals = "一二三四五六七八九十";
            int index = numerals.IndexOf(c);
            return index >= 0 ? index + 1 : 0;
        }

        static bool ContainsNumberReference(string t)
        {
            const string chineseNumbers = "一二三四五六七八九十";
            for (int i = 0; i + 1 < t.Length; i++)
            {
                if (t[i + 1] != '号' && t[i + 1] != '號') continue;
                if (char.IsDigit(t[i]) || chineseNumbers.IndexOf(t[i]) >= 0) return true;
            }
            return NumberTagRegex.IsMatch(t);
        }

        static bool ContainsTimeReference(string t)
        {
            if (t.Contains("倒计时")) return true;
            for (int i = 0; i < t.Length; i++)
            {
                if (!char.IsDigit(t[i])) continue;
                int j = i;
                while (j < t.Length && char.IsDigit(t[j])) j++;
                if (j < t.Length && (t[j] == ':' || t[j] == '点' || t[j] == '分')) return true;
                i = j;
            }

            string lower = t.ToLowerInvariant();
            return lower.Contains("o'clock") || lower.Contains("am") || lower.Contains("pm")
                   || lower.Contains("minute") || lower.Contains("hour");
        }

        static int CountAny(string t, string[] words)
        {
            int hits = 0;
            for (int i = 0; i < words.Length; i++)
                if (t.Contains(words[i])) hits++;
            return hits;
        }

        static int CountEnglishWords(string t, string[] words)
        {
            string lower = t.ToLowerInvariant();
            int hits = 0;
            for (int i = 0; i < words.Length; i++)
            {
                if (Regex.IsMatch(lower, @"\b" + Regex.Escape(words[i]) + @"\b")) hits++;
            }
            return hits;
        }
    }
}
