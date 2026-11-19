using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【行为约定】= 小白的"另类训练"成果。
    /// 主人每次对它提出行为要求（多走动 / 别老发呆 / 安静点 / 别叫主人…），
    /// AI 调 remember_rule 记一条；重复说同一条 → hits++（说得越多越牢）。
    /// 这些约定每轮都会注入系统提示，所以主人的话真的会改变它的长期行为。
    /// </summary>
    [Serializable]
    public class PetRule
    {
        public string rule;      // 约定内容（精简，<= ruleMaxLen 字）
        public int hits;         // 被主人强调过几次
        public long lastTicks;   // 最后一次提及的 UTC ticks
    }

    [Serializable]
    public class PetMemoryData
    {
        public List<string> history = new List<string>();   // 最近对话（最多 maxHistory 轮）
        public List<string> longTerm = new List<string>();  // 长期记忆（每条 <= 50 字）
        public List<PetRule> rules = new List<PetRule>();   // ★ 行为约定（主人训练出来的）
        public long lastForgetTicks = 0;                    // 上次遗忘的 UTC ticks
    }

    /// <summary>
    /// 小白记忆系统：滑动窗口(默认100轮) + 自动总结 + 本地 JSON 持久化 + 7天遗忘。
    /// 总结由外部（PetAPI 层）注入 onSummarize；未注入时用本地截断兜底，保证不崩。
    /// 单例，跨场景存活（DontDestroyOnLoad）。
    /// </summary>
    public class PetMemory : MonoBehaviour
    {
        public static PetMemory Instance { get; private set; }

        [Header("记忆参数")]
        public int maxHistory = 300;         // 滑动窗口上限（轮）——256K 上下文，可记更长
        public int summaryKeepRecent = 20;   // 总结后保留最近 N 轮原文
        public int longTermLimit = 50;       // 单条长期记忆字数上限
        public int longTermMaxCount = 30;    // 长期记忆条数上限
        public int forgetDays = 7;           // 遗忘周期（天）
        public int ruleMaxLen = 30;          // 单条行为约定字数上限
        public int ruleMaxCount = 20;        // 行为约定条数上限

        [Header("存储")]
        [Tooltip("留空 = 放在 EXE 同级（推荐，整个文件夹可以直接拷走）；填了就用指定目录")]
        public string memoryDir = "";

        PetMemoryData data = new PetMemoryData();
        public bool loaded { get; private set; }

        string filePath;
        string FilePath { get { return filePath; } }

        /// <summary>
        /// 解析存储路径：memoryDir 留空 ⇒ 放在 EXE 同级（和其他配置文件一致，整个文件夹可以直接拷走）；
        /// 建不出目录就回退到 persistentDataPath。
        /// </summary>
        void ResolvePath()
        {
            try
            {
                string dir = string.IsNullOrEmpty(memoryDir) ? PetPathUtil.ExeDir() : memoryDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, "PetMemory.json");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PetMemory] 指定目录不可用（" + e.Message + "），回退到 persistentDataPath");
                filePath = Path.Combine(Application.persistentDataPath, "PetMemory.json");
            }
            Debug.Log("[PetMemory] 存储路径 = " + filePath);
        }

        /// <summary>总结钩子：输入待总结的历史文本，回调返回 <=50 字摘要。由 PetAPI 注入。</summary>
        public Action<List<string>, Action<string>> onSummarize;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            ResolvePath();
            Load();
            CheckForget();
        }

        // ---------- 对话读写 ----------
        public void AddTurn(string role, string text)
        {
            data.history.Add(role + ": " + text);
            if (data.history.Count > maxHistory) SummarizeIfNeeded();
            Save();
        }

        /// <summary>
        /// 【主动搭话】—— 记进历史，角色名带「主动」标记。
        /// 大窗口聊天记录会灰显成「[小白凑了过来]」。
        /// </summary>
        public void AddProactiveTurn(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            AddTurn(PetProfileConfig.PetName + "（主动）", text);
        }
        public List<string> GetHistory() { return new List<string>(data.history); }
        public List<string> GetLongTerm() { return new List<string>(data.longTerm); }
        public int HistoryCount { get { return data.history.Count; } }
        public int LongTermCount { get { return data.longTerm.Count; } }

        // ---------- 行为约定（主人的"训练"）----------
        public List<PetRule> GetRules() { return new List<PetRule>(data.rules); }
        public int RuleCount { get { return data.rules.Count; } }

        /// <summary>
        /// 记住（或加强）一条行为约定。反复说同一件事 → hits++，注入时写得越靠前越"重"。
        /// 去重是宽松的：只要一条是另一条的子串（去掉标点后）就算同一条，
        /// 否则 4B 模型每次换个说法就会堆一堆重复条目。
        /// </summary>
        public PetRule RememberRule(string rule)
        {
            if (string.IsNullOrEmpty(rule)) return null;
            rule = rule.Trim().Trim('。', '！', '!', '.', '，', ',', ' ');
            if (rule.Length == 0) return null;
            if (rule.Length > ruleMaxLen) rule = rule.Substring(0, ruleMaxLen);

            for (int i = 0; i < data.rules.Count; i++)
            {
                if (SameRule(data.rules[i].rule, rule))
                {
                    data.rules[i].hits++;
                    data.rules[i].lastTicks = DateTime.UtcNow.Ticks;
                    if (rule.Length > data.rules[i].rule.Length) data.rules[i].rule = rule;  // 用更完整的措辞
                    Save();
                    Debug.Log("[PetMemory] ★ 行为约定加强（第 " + data.rules[i].hits + " 次）：「"
                              + data.rules[i].rule + "」");
                    return data.rules[i];
                }
            }

            var r = new PetRule { rule = rule, hits = 1, lastTicks = DateTime.UtcNow.Ticks };
            data.rules.Add(r);
            while (data.rules.Count > ruleMaxCount) data.rules.RemoveAt(0);
            Save();
            Debug.Log("[PetMemory] ★ 新行为约定：「" + rule + "」");
            return r;
        }

        /// <summary>宽松同义判断：去掉非文字字符后，一条是另一条的子串就算同一条。</summary>
        static bool SameRule(string a, string b)
        {
            string x = OnlyWord(a), y = OnlyWord(b);
            if (x.Length < 3 || y.Length < 3) return x == y;
            return x.Contains(y) || y.Contains(x);
        }
        static string OnlyWord(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (char.IsLetterOrDigit(s[i])) sb.Append(char.ToLowerInvariant(s[i]));
            return sb.ToString();
        }

        /// <summary>清掉所有行为约定（聊天框打 /forget 触发）。</summary>
        public void ClearRules()
        {
            int n = data.rules.Count;
            data.rules.Clear();
            Save();
            Debug.Log("[PetMemory] 已清空 " + n + " 条行为约定");
        }

        /// <summary>给日志/聊天框看的一行摘要。</summary>
        public string RulesReadable()
        {
            if (data.rules.Count == 0) return "（还没有任何行为约定。跟我说「你多四处走走」试试）";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < data.rules.Count; i++)
                sb.Append(i + 1).Append(". ").Append(data.rules[i].rule)
                  .Append("  (x").Append(data.rules[i].hits).Append(")").Append("   ");
            return sb.ToString();
        }

        void SummarizeIfNeeded()
        {
            if (data.history.Count <= maxHistory) return;
            int cut = data.history.Count - summaryKeepRecent;
            if (cut <= 0) return;
            var toSum = data.history.GetRange(0, cut);
            data.history.RemoveRange(0, cut);
            if (onSummarize != null) onSummarize(toSum, ReceiveSummary);
            else ReceiveSummary(LocalFallbackSummary(toSum));
        }
        void ReceiveSummary(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return;
            if (summary.Length > longTermLimit) summary = summary.Substring(0, longTermLimit);
            data.longTerm.Add(summary);
            while (data.longTerm.Count > longTermMaxCount) data.longTerm.RemoveAt(0);
            Save();
            Debug.Log("[PetMemory] 已总结并存入长期记忆：" + summary);
        }
        string LocalFallbackSummary(List<string> turns)
        {
            string s = string.Join(" / ", turns.ToArray());
            if (s.Length > longTermLimit) s = s.Substring(0, longTermLimit);
            return s;
        }

        // ---------- 遗忘 ----------
        void CheckForget()
        {
            long now = DateTime.UtcNow.Ticks;
            if (data.lastForgetTicks == 0) { data.lastForgetTicks = now; Save(); return; }
            double days = TimeSpan.FromTicks(now - data.lastForgetTicks).TotalDays;
            if (days >= forgetDays)
            {
                ForgetOldest((int)(days / forgetDays));
                data.lastForgetTicks = now;
                Save();
                Debug.Log("[PetMemory] 超过 " + forgetDays + " 天，已淡化最旧记忆");
            }
        }
        /// <summary>手动遗忘按钮：丢弃最旧一条长期记忆。</summary>
        public void ManualForget()
        {
            ForgetOldest(1);
            data.lastForgetTicks = DateTime.UtcNow.Ticks;
            Save();
            Debug.Log("[PetMemory] 手动遗忘 → 剩余长期记忆 " + data.longTerm.Count + " 条");
        }
        void ForgetOldest(int count)
        {
            for (int i = 0; i < count && data.longTerm.Count > 0; i++) data.longTerm.RemoveAt(0);
        }

        // ---------- 持久化 ----------
        public void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(data, true)); }
            catch (Exception e) { Debug.LogWarning("[PetMemory] 保存失败: " + e.Message); }
        }
        public void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var txt = File.ReadAllText(FilePath);
                    data = JsonUtility.FromJson<PetMemoryData>(txt) ?? new PetMemoryData();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PetMemory] 加载失败: " + e.Message);
                data = new PetMemoryData();
            }
            loaded = true;
            Debug.Log("[PetMemory] 已加载 → 对话 " + data.history.Count + " 轮 / 长期记忆 " + data.longTerm.Count + " 条");
        }
        /// <summary>
        /// 【出厂设置】清空小白的全部记忆：对话历史 + 长期记忆 + 行为约定。
        ///
        /// ⚠️ 只动 **PetMemory.json 这一个文件**。
        ///    接口配置（config.json）和形态参数（pet_visual.json）由别的组件管，
        ///    这里碰都不碰 —— 用户点了"重置记忆"绝不该把 API Key 也弄丢。
        /// </summary>
        public void ResetAll()
        {
            int h = data.history != null ? data.history.Count : 0;
            int l = data.longTerm != null ? data.longTerm.Count : 0;
            int r = data.rules != null ? data.rules.Count : 0;

            data = new PetMemoryData();                 // history / longTerm / rules 全部归零
            data.lastForgetTicks = DateTime.UtcNow.Ticks;
            Save();                                     // 覆盖写回同一个文件（不是删除）

            Debug.Log("[PetMemory] 已清空 对话 " + h + " 轮 / 长期记忆 " + l + " 条 / 行为约定 " + r + " 条"
                      + "（只动了 " + FilePath + "，接口配置与形态参数未受影响）");
            Debug.Log("记忆已清空，" + PetProfileConfig.PetName + " 回到了初始状态");
        }

        /// <summary>当前记忆规模，给确认弹窗用。</summary>
        public string ScaleReadable()
        {
            int h = data.history != null ? data.history.Count : 0;
            int l = data.longTerm != null ? data.longTerm.Count : 0;
            int r = data.rules != null ? data.rules.Count : 0;
            return "对话 " + h + " 轮 / 长期记忆 " + l + " 条 / 行为约定 " + r + " 条";
        }
    }
}
