using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Yarn.Unity;

namespace HilbertLoop
{
    public enum GameLanguage
    {
        Chinese = 0,
        English = 1,
    }

    /// <summary>
    /// 中英双语。
    ///
    /// - 所有新 UI 通过 Localization.T 取得文本；
    /// - 现有运行时构建的 TMP 文本由 LocalizationAutoTranslator 自动替换；
    /// - Yarn 台词由三个 DialoguePresenter 调用 TranslateDialogue；
    /// - LLM System Prompt 通过 LanguageDirective 强制模型使用当前语言；
    /// - 词典放在 Resources/Localization/en.json，可继续追加，不重新编译代码。
    ///
    /// 中文原文永远是 key；切换回中文时直接恢复原文。
    /// </summary>
    public static class Localization
    {
        const string LanguageKey = "HL_Language";

        static GameLanguage _language = GameLanguage.Chinese;
        static bool _loaded;
        static readonly Dictionary<string, string> _catalog = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _normalized = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        static readonly List<DialogueTemplate> _templates = new List<DialogueTemplate>();
        static readonly Regex _numberRegex = new Regex(@"\d+", RegexOptions.Compiled);
        static readonly Regex _placeholderRegex = new Regex(@"\{[^}]*\}", RegexOptions.Compiled);
        // 运行期拼出来的台词常带 "4号：/ 玩家：" 前缀；整串不在词典里，
        // 必须先把前缀和正文拆开分别翻译，否则英文模式下整句原样漏成中文。
        static readonly Regex _dialoguePrefixRegex = new Regex(
            @"^\s*(?:(\d+)\s*号|你|我|玩家|系统|没有人|有人)\s*[:：]\s*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static bool _templatesBuilt;

        class DialogueTemplate
        {
            public Regex regex;
            public string english;
            public int placeholderCount;
            /// <summary>中文模板里各占位符的名字，顺序与捕获组一致。</summary>
            public string[] keyPlaceholders;
        }

        public static event Action OnLanguageChanged;

        public static GameLanguage Language
        {
            get { EnsureLoaded(); return _language; }
            set
            {
                EnsureLoaded();
                if (_language == value) return;
                _language = value;
                PlayerPrefs.SetInt(LanguageKey, (int)_language);
                PlayerPrefs.Save();
                _cache.Clear();
                SyncDialogueRunners();
                OnLanguageChanged?.Invoke();
                Debug.Log($"[Localization] 语言切换为 {(_language == GameLanguage.English ? "English" : "中文")}。");
            }
        }

        public static bool IsEnglish => Language == GameLanguage.English;

        public static string LanguageLabel => IsEnglish ? "中文" : "English";

        public static string LanguageDirective => IsEnglish
            ? "\n\n[LANGUAGE REQUIREMENT] The player has selected English. All player-facing speech (`say`) must be natural English. Keep the JSON field names, tool ids and enum values exactly as specified. Do not output Chinese in `say`."
            : "\n\n[语言要求] 玩家使用中文。`say` 必须使用自然中文，不要输出英文。";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LoadPreference()
        {
            _language = PlayerPrefs.GetInt(LanguageKey, 0) == 1
                ? GameLanguage.English
                : GameLanguage.Chinese;
            EnsureLoaded();
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            AddDefaults();
            LoadJsonCatalog();
        }

        static void AddDefaults()
        {
            // 主菜单
            Add("开始", "Start");
            Add("继续", "Continue");
            Add("设置", "Settings");
            Add("鸣谢", "Credits");
            Add("退出", "Quit");
            Add("退出游戏", "Quit Game");
            Add("无存档", "No Save");
            Add("继续游戏", "Continue");
            Add("新的循环", "New Loop");
            Add("大限将至", "Hilbert Loop");
            Add("监控推演", "SURVEILLANCE SIMULATION");
            Add("主菜单", "Main Menu");
            Add("返回主菜单", "Main Menu");
            Add("制作人员", "Credits");
            Add("再来一局", "Play Again");
            Add("返回", "Back");
            Add("关闭", "Close");
            Add("确认", "Confirm");
            Add("取消", "Cancel");
            Add("按任意键继续", "Press any key");

            // 设置
            Add("语言", "Language");
            Add("中文", "Chinese");
            Add("英文", "English");
            Add("主音量", "Master Volume");
            Add("音效音量", "SFX Volume");
            Add("语音音量", "Voice Volume");
            Add("设置面板", "SETTINGS");
            Add("音频", "Audio");
            Add("显示", "Display");
            Add("全屏", "Fullscreen");
            Add("窗口化", "Windowed");
            Add("应用", "Apply");
            Add("AI 模型", "AI MODEL");
            Add("模型设置（F9）", "Model Settings (F9)");
            Add("当前模型：", "Current model: ");
            Add("游戏中按 F9 也可以随时打开。", "Press F9 in game to open it anytime.");
            Add("主菜单中无法切换模型，请先进入游戏。",
                "Models can only be changed in game. Start a game first.");
            Add("模型面板尚未初始化，请按 F9 重试。",
                "The model panel is not ready. Press F9 to retry.");
            Add("语言切换会立即应用到 UI、Yarn 台词与 LLM 提示词。",
                "Language changes apply immediately to UI, Yarn dialogue and LLM prompts.");

            // 鸣谢
            Add("特别感谢", "Special Thanks");
            Add("剧本 / 导演 / 程序", "Story / Direction / Programming");
            Add("林中夕客（微积分·甝虪）", "Linzhong Xike (Calculus · Hanchu)");
            Add("3D 角色与动作", "3D Characters & Animation");
            Add("混元AI3D", "Hunyuan AI 3D");
            Add("动作资源", "Animation Assets");
            Add("Mixamo", "Mixamo");
            Add("音效素材", "Sound Effects");
            Add("Freesound.org", "Freesound.org");
            Add("大语言模型", "Large Language Model");
            Add("Qwen 大模型", "Qwen Large Language Model");
            Add("感谢每一个在循环里醒来的人。", "For everyone who wakes up inside the loop.");

            // 共用 UI
            Add("游戏结束", "GAME OVER");
            Add("重新开始", "Restart");
            Add("搜证中", "Investigation");
            Add("会议中", "Meeting");
            Add("投票中", "Voting");
            Add("轮回结算", "Reincarnation");
            Add("剩余时间", "Time Remaining");
            Add("生命", "Life");
            Add("证据", "Evidence");
            Add("背包", "Inventory");
            Add("携带", "Carrying");
            Add("空", "Empty");
            Add("发送", "Send");
            Add("自由输入", "Free Input");
            Add("公开聊天", "Public Chat");
            Add("私聊", "Private Chat");
            Add("投票", "Vote");
            Add("弃权", "Abstain");
            Add("指认", "Accuse");
            Add("出示证据", "Present Evidence");
            Add("摘下自己的面具", "Remove Your Mask");
            Add("摘下9号的面具", "Remove No. 9's Mask");
            Add("号召所有人摘下面具", "Call Everyone to Unmask");
            Add("结束发言", "End Speech");
            Add("继续对话", "Continue");
            Add("离开", "Leave");
            Add("返回会议", "Back to Meeting");
            Add("是", "Yes");
            Add("否", "No");

            // 结局
            Add("结局 A · 无尽轮回", "ENDING A · ENDLESS LOOP");
            Add("结局 B · 死亡悲剧", "ENDING B · DEATH TRAGEDY");
            Add("结局 C · 大梦初醒", "ENDING C · AWAKE AT LAST");
            Add("结局 D · 献祭顿悟", "ENDING D · SACRIFICIAL REVELATION");
            Add("ENDLESS LOOP  /  无尽轮回", "ENDLESS LOOP");
            Add("AWAKENING  /  大梦初醒", "AWAKENING");
            Add("SACRIFICE & EPIPHANY  /  献祭顿悟", "SACRIFICE & EPIPHANY");
            Add("DEATH TRAGEDY  /  死亡悲剧", "DEATH TRAGEDY");
            Add("无尽轮回", "Endless Loop");
            Add("死亡悲剧", "Death Tragedy");
            Add("大梦初醒", "Awake at Last");
            Add("献祭顿悟", "Sacrificial Revelation");
            Add("循环仍会继续，只是不再需要你。", "The loop continues. It simply no longer needs you.");
            Add("你终于在自己的床上醒来——或者，那只是另一层梦。",
                "You finally wake in your own bed — or perhaps that is merely another dream.");
            Add("他们摘下面具，第一次认出了彼此，也认出了自己。",
                "They remove their masks and recognize one another — and themselves — for the first time.");
            Add("循环不会为任何人停下。", "The loop stops for no one.");
            Add("你活到了循环的尽头。今夜，轮到你成为那具尸体。",
                "You survived to the end of the loop. Tonight, you become the body.");
            Add("所有人摘下面具，梦境失去了继续运转的理由。",
                "Everyone removed their masks. The dream lost its reason to continue.");
            Add("活下来的人靠证据链认出了同一张脸。",
                "The survivors followed the evidence to the same face.");
            Add("你没能活过这一轮。", "You did not survive this round.");
            Add("存档会在天亮与退出游戏时自动保存",
                "The game saves automatically at dawn and when you quit.");

            // 监控 / 推演
            Add("存活名单", "SURVIVORS");
            Add("本轮残缺", "INCOMPLETE ROSTER");
            Add("循环推演中", "SIMULATING LOOP");
            Add("监控画面已定帧", "SURVEILLANCE FEED FROZEN");
            Add("所有人都会记得的，只有空缺。", "All that remains is the absence.");
            Add("推演结束", "SIMULATION COMPLETE");

            // 主菜单循环演示（醒来 → 集合 → 公聊 → 投票 → 轮回）
            Add("醒来", "WAKE");
            Add("圆桌集合", "GATHER");
            Add("公开讨论", "PUBLIC DEBATE");
            Add("投票阶段", "VOTING");
            Add("轮回", "REINCARNATION");
            Add("天亮，号码顺移", "DAWN · NUMBERS SHIFT");
            Add("本轮出局", "ELIMINATED");
            Add("票", "votes");
            Add("监控回放", "SURVEILLANCE REPLAY");
            Add("第{0}轮", "Round {0}");
            Add("第 {0} 轮", "Round {0}");
            Add("第{0}号", "No. {0}");
            Add("有人", "Someone");
            Add("没有人", "Nobody");
            Add("未知", "Unknown");
            Add("走廊", "Corridor");
            Add("大厅", "Lobby");
            Add("圆桌", "round table");
            Add("房间", "room");
            Add("床底", "Under the Bed");
            Add("衣柜", "Wardrobe");

            // 主菜单公聊字幕（与 Yarn 台词同义，单独收录以免依赖 Yarn 语言包）
            Add("我出去过一次，走到圆桌看了看，又试了试其他房门。",
                "I went out once, walked to the round table, and tried the other doors.");
            Add("我房间的桌上有一把匕首，刀柄上有干涸的血迹。",
                "There was a dagger on my desk, with dried blood on the handle.");
            Add("如果每个人的房间都一样，那这套东西不是给某一个人准备的。",
                "If every room is the same, this kit wasn't prepared for just one person.");
            Add("监控把每个人出门和回房都拍下来了。谁出去过、什么时候回来，你们自己看。",
                "The cameras recorded everyone leaving and returning. See for yourselves who went out and when.");
            Add("如果今天一定要投一个人，我建议先从出门次数最多、路线又说不清的人开始。",
                "If we must vote someone out today, start with the one who went out most and can't explain the route.");
            Add("9号没有来。他的椅子空着。", "No. 9 never came. His chair is empty.");

            // 静态游戏文本（覆盖常用路径）
            Add("开门", "Open Door");
            Add("关门", "Close Door");
            Add("尝试强行破门", "Try to Break the Door");
            Add("藏匿", "Hide");
            Add("取回", "Take Back");
            Add("拾取", "Pick Up");
            Add("阅读", "Read");
            Add("丢弃", "Drop");
            Add("翻窗", "Climb Through");
            Add("拨开锁销", "Lift the Latch");
            Add("收回长绳", "Retrieve Rope");
            Add("放置内磁铁", "Place Inner Magnet");
            Add("取回内磁铁", "Take Inner Magnet");
            Add("私下交谈", "Talk Privately");
            Add("点头", "Nod");
            Add("后退", "Step Back");
            Add("推搡", "Push");
            Add("袭击", "Attack");
            Add("警告", "Warn");
            Add("沉默", "Stay Silent");
            Add("观察", "Observe");
            Add("检查", "Inspect");

            // 动态模板（数字统一用 # 占位；查找时会把真实数字归一化成 #）
            Add("你是 # 号，也是这层楼中唯一的杀手，你的任务，就是在天亮之前，按从大到小的顺序杀死所有序号比你大的人，然后你可以找到逃离这里的方法；当然，如果你没有逃掉……你会被抹杀欧~\\n本轮最大号码是 # 号。",
                "You are No. #, the only killer on this floor. Before dawn, kill everyone with a higher number, from highest to lowest. Then you may find a way out. If you fail to escape... you will be erased.\\nThe highest number this round is No. #.");
            Add("你在 # 号房醒来。", "You wake up in Room #.");
            Add("你在 # 号房醒来。新的一轮开始了。", "You wake up in Room #. A new loop begins.");
            Add("第 # 周目：\\n你是 # 号。", "Loop #:\\nYou are No. #.");
            Add("你带着上一轮的记忆在 # 号的身体里醒来。", "You wake in No. #'s body with last loop's memories.");
            Add("你带着上一轮的记忆，以 1 号的身体在空无一人的房子里醒来。",
                "You wake in No. 1's body, carrying last loop's memories, alone in the house.");
            Add("本轮最大号码是 # 号。", "The highest number this round is No. #.");
            Add("9 号还活着。", "No. 9 is still alive.");
            Add("你死了。", "You are dead.");
            Add("与#号私下交谈", "Talk privately with No. #");
        }

        static void Add(string zh, string en)
        {
            if (string.IsNullOrEmpty(zh) || string.IsNullOrEmpty(en)) return;
            _catalog[zh] = en;
            // 词典里的键来自 C# 源码字面量，换行是 "\\n" 两个字符；运行期文本是真正的换行符。
            // 两种写法都登记一份，否则多行提示（开发者面板、幽灵提示）永远命不中。
            if (zh.IndexOf("\\n", System.StringComparison.Ordinal) >= 0)
                _catalog[zh.Replace("\\n", "\n")] = en;
            else if (zh.IndexOf('\n') >= 0)
                _catalog[zh.Replace("\n", "\\n")] = en;
            _templatesBuilt = false;
            _cache.Clear();
        }

        static void LoadJsonCatalog()
        {
            TextAsset asset = Resources.Load<TextAsset>("Localization/en");
            if (asset == null) return;
            try
            {
                JObject obj = JObject.Parse(asset.text);
                foreach (var pair in obj)
                {
                    if (pair.Value != null && pair.Value.Type == JTokenType.String)
                        Add(pair.Key, pair.Value.ToString());
                }
                Debug.Log($"[Localization] 已加载英文词典：{obj.Count} 条。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Localization] 英文词典解析失败：{e.Message}");
            }
        }

        public static string T(string zh)
        {
            if (string.IsNullOrEmpty(zh) || !IsEnglish) return zh;
            EnsureLoaded();

            string cached;
            if (_cache.TryGetValue(zh, out cached)) return cached;

            string result = TranslateCore(zh);
            _cache[zh] = result;
            return result;
        }

        static string TranslateCore(string zh)
        {
            // "9号" / "9号、3号"：纯号码列表，直接按项翻译，避免被通用模板拆坏。
            if (IsNumberList(zh))
            {
                string list = Regex.Replace(zh, @"(\d+)\s*号", "No. $1");
                return list.Replace("、", ", ").Replace("，", ", ");
            }

            string segment = TranslateSegment(zh);
            if (segment != zh) return DedupeSpeakerPrefix(segment);

            // 多行文本先按行翻，且保留每行自己的说话人前缀。
            // "8号：等一下。……\n正文" 若先拆前缀，正文首行会丢掉 "8号："
            // 就命中不了整句模板，英文模式下会漏出中文。
            if (zh.IndexOf('\n') >= 0)
            {
                string multiline = TranslateComposite(zh);
                if (multiline != null) return PolishEnglish(multiline);
            }

            // 带说话人前缀的运行期台词（"4号：正文" / "你：正文"）。
            // 整串不在词典里，先把前缀换成 "No. 4:"，正文再走完整翻译。
            string prefixed;
            if (TryTranslatePrefixed(zh, out prefixed)) return prefixed;

            // 运行期拼接出来的文本（"搜证中\n0:00"、"天亮，号码顺移。"）整串不在词典里，
            // 按行 / 去句号 / 中文标点再翻一次，否则界面上会整段漏掉中文。
            string composite = TranslateComposite(zh);
            if (composite != null) return PolishEnglish(composite);

            return zh;
        }

        /// <summary>
        /// 只做整串匹配：精确条目、{变量} 模板、数字归一化。
        /// 不递归、不拆分，供复合翻译逐段调用。
        /// </summary>
        static string TranslateSegment(string zh)
        {
            string direct;
            if (_catalog.TryGetValue(zh, out direct)) return PolishEnglish(direct);

            // 带 {变量} 的模板：把运行期已替换的片段按顺序捕获，再填回英文模板。
            // 短模板（"{Number}号" → "No. {Number}"）会贪婪命中整句中文，
            // 所以先记下候补，继续找能把整串翻干净的模板。
            if (_templates.Count == 0) BuildTemplateIndex();
            string fallback = null;
            for (int i = 0; i < _templates.Count; i++)
            {
                DialogueTemplate tpl = _templates[i];
                Match match = tpl.regex.Match(zh);
                if (!match.Success || match.Groups.Count - 1 != tpl.placeholderCount) continue;

                string filled = FillTemplate(tpl.english, match, tpl.keyPlaceholders);
                // 变量翻不出来（例如 "{Number}号" 这类短模板吞掉了整句中文）时，
                // 不能拿丢掉变量后的残句当结果，换下一个模板。
                if (filled == null) continue;
                string candidate = PolishEnglish(filled);
                if (!HasCjk(candidate)) return candidate;
                if (fallback == null) fallback = candidate;
            }
            if (fallback != null) return fallback;

            string normalizedKey = NormalizeNumbers(zh);
            if (_normalized.Count == 0) BuildNormalizedIndex();
            string numbered;
            if (_normalized.TryGetValue(normalizedKey, out numbered))
                return PolishEnglish(RestoreNumbers(numbered, zh));

            return zh;
        }

        static readonly Regex _englishSpeakerPrefixRegex = new Regex(
            @"^((?:No\.\s*\d+|You|Me|Player(?: No\. \d+)?|Someone|Nobody|System)\s*:)\s*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// "{Number}号：{clean}" 这类模板遇到正文里已带 "No. 2:" 的 LLM 台词，
        /// 会拼出 "No. 2: No. 2: …"。这里把重复的同一个前缀压掉一次。
        /// </summary>
        static string DedupeSpeakerPrefix(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            Match match = _englishSpeakerPrefixRegex.Match(text);
            if (!match.Success) return text;
            string head = match.Groups[1].Value;
            string rest = text.Substring(match.Length);
            if (!rest.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return text;
            return head + " " + rest.Substring(head.Length).TrimStart();
        }

        static bool HasCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u4e00' && c <= '\u9fff') return true;
            }
            return false;
        }

        static string TranslateComposite(string zh)
        {
            if (zh.IndexOf('\n') >= 0)
            {
                string[] lines = zh.Split('\n');
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string part = TranslateLine(lines[i]);
                    if (part != lines[i]) { lines[i] = part; changed = true; }
                }
                return changed ? string.Join("\n", lines) : null;
            }
            string single = TranslateLine(zh);
            if (single != zh) return single;

            // 运行期用 + 拼出来的一句话（纸条、面具掉落、绳影消失）整串不在词典里，
            // 但前后两段各自是词典条目。在中文标点处找最长可命中的前缀，再翻剩余部分，
            // 否则这类拼接文本会整句漏成中文。
            return TranslateByPrefix(zh);
        }

        static string TranslateLine(string line)
        {
            // 先让整句模板命中，再拆说话人前缀。
            // "8号：等一下。我有东西要给大家看——窗外的墙钉。" 这类句子整句有模板，
            // 如果先拆前缀，正文就只走碎片翻译，整句反而漏成中文。
            string part = TranslateFragment(line);
            if (part != line) return part;

            string prefixed;
            if (TryTranslatePrefixed(line, out prefixed)) return prefixed;

            string spaced = TranslateSpaced(line);
            return spaced ?? line;
        }

        /// <summary>
        /// 把 "4号：正文" / "你：正文" 拆成前缀与正文分别翻译。
        /// 前缀必须是已知说话人，否则保持原样，交给后续规则处理。
        /// </summary>
        static bool TryTranslatePrefixed(string line, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(line)) return false;

            Match speaker = _dialoguePrefixRegex.Match(line);
            if (!speaker.Success || speaker.Length >= line.Length) return false;

            string head = TranslateSpeaker(speaker.Value);
            if (HasCjk(head)) return false;

            string rest = TranslateCore(line.Substring(speaker.Length));
            // LLM 有时自己在正文里又加了一遍 "No. 2:"，再补前缀就会变成双前缀。
            if (rest.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                result = rest;
            else
                result = head + (rest.Length > 0 ? " " + rest : string.Empty);
            return true;
        }

        /// <summary>
        /// 说话人标签（"3 号　[警惕]　声音里夹着电流声"）、票数行（"3 号　　5 票"）
        /// 用全角空格拼接；整串不在词典里，按分隔符拆段翻译再原样拼回。
        /// </summary>
        static string TranslateSpaced(string line)
        {
            if (line.IndexOf('\u3000') < 0 && line.IndexOf("  ", StringComparison.Ordinal) < 0) return null;

            var sb = new StringBuilder(line.Length + 16);
            int start = 0;
            bool changed = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                bool doubleSpace = c == ' ' && i + 1 < line.Length && line[i + 1] == ' ';
                if (c != '\u3000' && !doubleSpace) continue;

                string part = TranslateFragment(line.Substring(start, i - start));
                if (part != line.Substring(start, i - start)) changed = true;
                sb.Append(part);
                sb.Append(doubleSpace ? "  " : "\u3000");
                if (doubleSpace) i++;
                start = i + 1;
            }
            string tail = TranslateFragment(line.Substring(start));
            if (tail != line.Substring(start)) changed = true;
            sb.Append(tail);
            return changed ? sb.ToString() : null;
        }

        static string TranslateByPrefix(string zh)
        {
            for (int i = zh.Length - 1; i > 0; i--)
            {
                char c = zh[i];
                if (c != '，' && c != '。' && c != '！' && c != '？' && c != '；' &&
                    c != '：' && c != '、' && c != '”' && c != '…' &&
                    c != '—' && c != '～') continue;

                string prefix = zh.Substring(0, i + 1);
                string translated = TranslateFragment(prefix);
                if (translated == prefix || HasCjk(translated)) continue;

                string rest = zh.Substring(i + 1);
                string restTranslated = rest.Length == 0 ? rest : TranslateComposite(rest);
                if (restTranslated == null) restTranslated = rest;

                // 中文标点后没有空格，英文拼接时必须补一个，否则会出现 "table,If you..."。
                char last = translated.Length > 0 ? translated[translated.Length - 1] : ' ';
                char first = restTranslated.Length > 0 ? restTranslated[0] : ' ';
                bool needSpace = translated.Length > 0 && restTranslated.Length > 0 &&
                                 !char.IsWhiteSpace(last) && !char.IsWhiteSpace(first) &&
                                 last != '—' && last != '-' && last != '/' && last != '(';
                return translated + (needSpace ? " " : "") + restTranslated;
            }
            return zh;
        }

        static string TranslateFragment(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            if (IsNumberList(line))
            {
                string list = Regex.Replace(line, @"(\d+)\s*号", "No. $1");
                return list.Replace("、", ", ").Replace("，", ", ");
            }
            if (!Regex.IsMatch(line, "[\\u4e00-\\u9fff]")) return line;

            string segment = TranslateSegment(line);
            if (segment != line) return segment;

            // 词典按不带句末标点的形式收录
            if (line.EndsWith("。"))
            {
                string key = line.Substring(0, line.Length - 1);
                string translated = TranslateSegment(key);
                if (translated != key && !HasCjk(translated)) return translated + ".";
            }
            return line;
        }

        public static string Translate(string zh) => T(zh);

        /// <summary>
        /// 交互提示：由各交互脚本在运行期拼装（“[E] 开门”“[E] 开窗  [F] 翻窗（需先开窗）”），
        /// 整串不在词典里。这里按按键标签 / 换行 / 全角空格 / 双空格拆段，逐段翻译。
        /// </summary>
        public static string TranslatePrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt) || !IsEnglish) return prompt;

            var sb = new StringBuilder(prompt.Length + 24);
            int start = 0;
            for (int i = 0; i < prompt.Length; i++)
            {
                char c = prompt[i];
                bool doubleSpace = c == ' ' && i + 1 < prompt.Length && prompt[i + 1] == ' ';
                if (c != '\n' && c != '　' && !doubleSpace) continue;

                sb.Append(TranslatePromptSegment(prompt.Substring(start, i - start)));
                if (doubleSpace)
                {
                    sb.Append("  ");
                    i++;
                }
                else
                {
                    sb.Append(c);
                }
                start = i + 1;
            }
            sb.Append(TranslatePromptSegment(prompt.Substring(start)));

            string result = sb.ToString();
            // 运行期拼进来的号码可能仍是“3号”写法，统一收尾成 “No. 3”。
            return result == prompt ? prompt : PolishEnglish(result);
        }

        static string TranslatePromptSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return segment;
            if (!Regex.IsMatch(segment, "[\\u4e00-\\u9fff]")) return segment;

            string tag = "";
            string gap = "";
            string body = segment;

            if (segment[0] == '[')
            {
                int end = segment.IndexOf(']');
                if (end > 0)
                {
                    tag = segment.Substring(0, end + 1);
                    if (tag == "[左键]") tag = "[LMB]";
                    int p = end + 1;
                    while (p < segment.Length && (segment[p] == ' ' || segment[p] == '　')) p++;
                    gap = segment.Substring(end + 1, p - end - 1);
                    body = segment.Substring(p);
                }
            }

            return tag + gap + TranslatePromptBody(body);
        }

        static string TranslatePromptBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            if (!Regex.IsMatch(body, "[\\u4e00-\\u9fff]")) return body;

            Match m;

            // —— 绳索 / 墙檐（带房间号，必须在模板匹配之前处理） ——
            m = Regex.Match(body, @"^(\d+) 号房方向没有可用的绳子（带长绳从窗口翻出即可系绳）$");
            if (m.Success) return $"No rope leads toward Room {m.Groups[1].Value} (carry the long rope out the window to tie it)";

            m = Regex.Match(body, @"^你的长绳系在 (\d+) 号房窗外——这里够不到它$");
            if (m.Success) return $"Your long rope is tied outside Room {m.Groups[1].Value}—you cannot reach it from here";

            m = Regex.Match(body, @"^绳子只从 (\d+) 号房铺向另一侧——(\d+) 号房这边没有绳$");
            if (m.Success) return $"The rope only runs from Room {m.Groups[1].Value} toward the other side—there is no rope on Room {m.Groups[2].Value}'s side";

            m = Regex.Match(body, @"^绳子没有铺向 (\d+) 号房这一侧（当前绳路：(\d+)→(\d+)）$");
            if (m.Success) return $"The rope does not run toward Room {m.Groups[1].Value}'s side (current route: {m.Groups[2].Value}→{m.Groups[3].Value})";

            m = Regex.Match(body, @"^抓住长绳，挪向 (\d+) 号房窗外$");
            if (m.Success) return $"Grab the long rope and move to outside Room {m.Groups[1].Value}";

            m = Regex.Match(body, @"^抓住长绳，把绳头甩向 (\d+) 号房一侧并挪过去$");
            if (m.Success) return $"Grab the long rope, swing its end toward Room {m.Groups[1].Value}'s side, and move across";

            m = Regex.Match(body, @"^绳子绕过支点钉，挪向 (\d+) 号房窗外$");
            if (m.Success) return $"The rope is around the pivot nail—move to outside Room {m.Groups[1].Value}";

            m = Regex.Match(body, @"^在墙缝钉下支点钉，把绳子接向 (\d+) 号房窗外$");
            if (m.Success) return $"Drive a pivot nail into the wall crack and extend the rope to outside Room {m.Groups[1].Value}";

            if (body.StartsWith("强行向前（", StringComparison.Ordinal))
            {
                if (body.IndexOf("绳长已到尽头", StringComparison.Ordinal) >= 0)
                    return "Force your way forward (the rope has run out and there is no next anchor—you will fall)";
                if (body.IndexOf("也缺长钉", StringComparison.Ordinal) >= 0)
                    return "Force your way forward (no pivot nail ahead, and you lack nails/hammer—you will fall)";
                return "Force your way forward (no pivot nail ahead—you will fall)";
            }

            // —— 拾取 / 藏匿 / 取出 ——
            m = Regex.Match(body, @"^拾取(.+?)（(\d+)号房）$");
            if (m.Success) return $"Pick up {T(m.Groups[1].Value)} (Room {m.Groups[2].Value})";

            m = Regex.Match(body, @"^拾取(.+)$");
            if (m.Success) return $"Pick up {T(m.Groups[1].Value)}";

            m = Regex.Match(body, @"^藏匿：(.+?)（(\d+)/(\d+)，Q 切换）$");
            if (m.Success)
                return $"Hide: {T(m.Groups[1].Value)} ({m.Groups[2].Value}/{m.Groups[3].Value}, Q to switch)";

            m = Regex.Match(body, @"^藏匿：(.+)$");
            if (m.Success) return $"Hide: {T(m.Groups[1].Value)}";

            m = Regex.Match(body, @"^取出：(.+)$");
            if (m.Success) return $"Take out: {T(m.Groups[1].Value)}";

            // —— 私聊 / NPC 敌意动作 ——
            m = Regex.Match(body, @"^与(\d+)号私下交谈$");
            if (m.Success) return $"Talk privately with No. {m.Groups[1].Value}";

            m = Regex.Match(body, @"^将(.+?)推出窗外$");
            if (m.Success) return $"Push {PromptTarget(m.Groups[1].Value)} out the window";

            m = Regex.Match(body, @"^用(.+?)袭击(.+?)（有人看着）$");
            if (m.Success)
                return $"Attack {PromptTarget(m.Groups[2].Value)} with {T(m.Groups[1].Value)} (someone is watching)";

            m = Regex.Match(body, @"^用(.+?)袭击(.+)$");
            if (m.Success) return $"Attack {PromptTarget(m.Groups[2].Value)} with {T(m.Groups[1].Value)}";

            m = Regex.Match(body, @"^用(.+?)杀死(.+)$");
            if (m.Success) return $"Kill {PromptTarget(m.Groups[2].Value)} with {T(m.Groups[1].Value)}";

            m = Regex.Match(body, @"^轻推(.+)$");
            if (m.Success) return $"Nudge {PromptTarget(m.Groups[1].Value)}";

            // —— 尸体 / 证据 ——
            m = Regex.Match(body, @"^摘下(.+?)的面具，与自己的脸对比$");
            if (m.Success)
                return $"Take off {T(m.Groups[1].Value)}'s mask and compare it with your own face";

            m = Regex.Match(body, @"^摘下(.+?)的面具$");
            if (m.Success) return $"Take off {T(m.Groups[1].Value)}'s mask";

            m = Regex.Match(body, @"^检查(.+)$");
            if (m.Success) return $"Inspect {T(m.Groups[1].Value)}";

            m = Regex.Match(body, @"^查看(.+)$");
            if (m.Success) return $"Examine {T(m.Groups[1].Value)}";

            // —— 破门 ——
            m = Regex.Match(body, @"^用(.+?)破门$");
            if (m.Success) return $"Break the door with {T(m.Groups[1].Value)}";

            // —— 固定提示 ——
            switch (body)
            {
                case "开门": return "Open the door";
                case "关门": return "Close the door";
                case "开窗": return "Open the window";
                case "关窗": return "Close the window";
                case "翻窗": return "Climb through the window";
                case "翻进窗内": return "Climb inside";
                case "翻窗（需先开窗）": return "Climb through (open the window first)";
                case "拨锁": return "Move the latch";
                case "拨锁（需要外磁铁）": return "Move the latch (requires the outer magnet)";
                case "拨锁（窗内没有内磁铁）": return "Move the latch (no inner magnet inside)";
                case "用外磁铁隔着玻璃拨锁": return "Use the outer magnet to move the latch through the glass";
                case "手动拨开窗锁": return "Move the window latch by hand";
                case "把磁铁放到窗台": return "Place the magnet on the windowsill";
                case "取回窗台磁铁": return "Take back the windowsill magnet";
                case "磁铁还贴在玻璃上……": return "The magnet is still stuck to the glass…";
                case "磁铁正隔着玻璃拨动锁销……": return "The magnet is moving the latch through the glass…";
                case "窗户从外面反锁了——锁销卡死，外磁铁也拨不动":
                    return "The window is latched from outside—the latch is jammed, and even the outer magnet cannot move it";
                case "尝试强行破门": return "Try to force the door open";
                case "私下交谈": return "Chat privately";
                case "收回长绳": return "Retract the long rope";
                case "全部取出": return "Take all out";
            }

            // 其余单词 / 短句交给词典（“藏匿”“开门”等）。
            return T(body);
        }

        static string PromptTarget(string zh)
        {
            if (zh.StartsWith("熟睡的", StringComparison.Ordinal))
                return "the sleeping " + T(zh.Substring(3));
            if (zh.StartsWith("沉睡的", StringComparison.Ordinal))
                return "the sleeping " + T(zh.Substring(3));
            return T(zh);
        }

        /// <summary>Yarn 台词：支持 "1号：" / "玩家：" 等前缀与正文分离翻译。</summary>
        public static string TranslateDialogue(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsEnglish) return text;

            string translated = T(text);
            // 英文台词里的 {0}/{1} 替换值仍是中文（如“3号”“监控没有拍到我出房门”），
            // 这里统一做一次号码 / 常见称谓的收尾转换。
            translated = PolishEnglish(translated);

            if (translated == text)
            {
                // T() 没有命中（英文 CSV 已经给出整句），仍然要处理前缀。
                translated = PolishEnglish(text);
            }

            return translated;
        }

        /// <summary>把英文模式下残留的“N号 / N号房 / 有人”等替换值统一成英文写法。</summary>
        public static string PolishEnglish(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsEnglish) return text;

            string s = text;
            s = Regex.Replace(s, @"(\d+)\s*号房", "Room $1");
            s = Regex.Replace(s, @"(\d+)\s*号", "No. $1");
            s = Regex.Replace(s, @"\b(?:[Nn]umber|[Nn]o\.?)\s*(\d+)", "No. $1");
            s = Regex.Replace(s, @"\b[Rr]oom\s+(\d+)", "Room $1");
            s = s.Replace("No.：", "No. ");
            // 这里不再做 "有人 → someone" 之类的词内替换：未命中的中文整句会被
            // 拆成 "总得someone先开口" 这种半中半英，比原样保留中文更难排查。
            // 说话人标签由 TranslateSpeaker 负责，"有人：" 会在那里整体替换。
            return s;
        }

        static string FillTemplate(string english, Match match, string[] keyPlaceholders)
        {
            var sb = new StringBuilder(english.Length + 16);
            int group = 1;
            int i = 0;
            while (i < english.Length)
            {
                char c = english[i];
                if (c == '{')
                {
                    int end = english.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        string name = english.Substring(i + 1, end - i - 1).Trim();
                        // 英文常会调整占位符语序（"No. {Number} with {weaponName}"），
                        // 这里按中文模板里的名字取对应的捕获组，而不是盲目按顺序取。
                        int mapped = MapPlaceholder(name, keyPlaceholders, match.Groups.Count - 1);
                        if (mapped <= 0 && group < match.Groups.Count) mapped = group;
                        // 占位符填不上时整条模板作废，绝不能把 "{" 漏到界面上。
                        if (mapped <= 0 || mapped >= match.Groups.Count) return null;
                        group = Math.Max(group, mapped + 1);
                        string value = LocalizeValue(match.Groups[mapped].Value);
                        if (value == null) return null;
                        sb.Append(value);
                        i = end + 1;
                        continue;
                    }
                }
                else if (c == '#')
                {
                    if (group >= match.Groups.Count) return null;
                    string value = LocalizeValue(match.Groups[group++].Value);
                    if (value == null) return null;
                    sb.Append(value);
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>把英文占位符名映射到中文模板的捕获组序号；无法映射返回 0。</summary>
        static int MapPlaceholder(string name, string[] keyPlaceholders, int groupCount)
        {
            if (string.IsNullOrEmpty(name) || keyPlaceholders == null) return 0;

            for (int i = 0; i < keyPlaceholders.Length && i < groupCount; i++)
            {
                if (keyPlaceholders[i] == name) return i + 1;
            }

            // 英文用 {0}/{1} 而中文用具名占位符时，按位置对应。
            int index;
            if (int.TryParse(name, out index) && index >= 0 && index < groupCount)
                return index + 1;
            return 0;
        }

        /// <summary>
        /// 翻译模板里捕获到的变量。翻不出来时返回 null，
        /// 让调用方放弃这条模板，而不是把中文或空串硬塞进英文句子。
        /// </summary>
        static string LocalizeValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string trimmed = value.Trim();
            string direct;
            if (_catalog.TryGetValue(trimmed, out direct)) return PolishEnglish(direct);

            // "9号、3号" 这类号码列表：逐项换成 "No. 9, No. 3"，
            // 否则缺席椅那类句子会把中文顿号带进英文。
            if (IsNumberList(trimmed))
            {
                string list = Regex.Replace(trimmed, @"(\d+)\s*号", "No. $1");
                return list.Replace("、", ", ").Replace("，", ", ");
            }

            // 模板捕获到的往往是中文道具名 / 号码 / 人名。不翻译就直接填进英文模板，
            // 界面上会出现 "No. 3 snatched 匕首 from the table." 这种半中半英。
            string translated = T(trimmed);
            if (translated != trimmed)
            {
                // 只翻中一半的变量同样会把中文带进英文句子；整条模板作废。
                if (!Regex.IsMatch(translated, "[\\u4e00-\\u9fff]")) return translated;
                return null;
            }

            // 英文模式下，未收录的中文变量绝不能原样填进英文句子。
            // 返回 null 让模板匹配失败，交给更精确的模板或复合翻译。
            if (IsEnglish && Regex.IsMatch(trimmed, "[\\u4e00-\\u9fff]"))
                return null;
            return trimmed;
        }

        static string TranslateSpeaker(string prefix)
        {
            string name = prefix.TrimEnd('：', ':').Trim();
            if (name == "你") return "You:";
            if (name == "我") return "Me:";
            if (name == "玩家") return "Player:";
            Match match = Regex.Match(name, @"^(\d+)\s*号$");
            if (match.Success) return "No. " + match.Groups[1].Value + ":";
            if (name == "系统") return "System:";
            if (name == "有人") return "Someone:";
            if (name == "没有人") return "Nobody:";
            return name + ":";
        }

        static string NormalizeNumbers(string text)
        {
            return _numberRegex.Replace(text, "#");
        }

        static void BuildNormalizedIndex()
        {
            _normalized.Clear();
            foreach (var pair in _catalog)
            {
                string key = NormalizeNumbers(pair.Key);
                if (!_normalized.ContainsKey(key)) _normalized[key] = pair.Value;
            }
        }

        /// <summary>
        /// 把带 {变量} 的中文模板编译为正则：运行期文本里的变量片段会被捕获，
        /// 再按顺序填进英文模板的 {…} 占位符。这样 "拾取了匕首。" 也能命中
        /// "拾取了{DisplayName}。" 这一条。
        /// </summary>
        static void BuildTemplateIndex()
        {
            if (_templatesBuilt) return;
            _templatesBuilt = true;
            _templates.Clear();

            foreach (var pair in _catalog)
            {
                string key = pair.Key;
                if (key.IndexOf('{') < 0) continue;

                int groups;
                string pattern = BuildCapturePattern(key, out groups);
                if (groups <= 0) continue;
                // 跳过 "{0}：{1}" 这类只有标点的通用格式。它会把任何带冒号的句子
                // 整条吞掉，再逐段拼回去，结果就是 "No. There are 2 empty chairs..."。
                if (LiteralCjkCount(key) == 0) continue;

                int placeholders = CountPlaceholders(pair.Value);
                if (placeholders != groups) continue;

                var entry = new DialogueTemplate
                {
                    regex = new Regex(pattern, RegexOptions.CultureInvariant),
                    english = pair.Value,
                    placeholderCount = placeholders,
                    keyPlaceholders = ExtractPlaceholderNames(key),
                };
                _templates.Add(entry);
            }

            // 字面量越长，匹配越具体，排前面。
            _templates.Sort((a, b) => LiteralScore(b.english).CompareTo(LiteralScore(a.english)));
            Debug.Log($"[Localization] 模板索引已建立：{_templates.Count} 条。");
        }

        static int LiteralScore(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int score = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '{' && c != '}') score++;
            }
            return score;
        }

        static int CountPlaceholders(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return _placeholderRegex.Matches(text).Count;
        }

        /// <summary>按出现顺序取出中文模板里的占位符名字。</summary>
        static string[] ExtractPlaceholderNames(string key)
        {
            var names = new List<string>();
            int i = 0;
            while (i < key.Length)
            {
                if (key[i] == '{')
                {
                    int end = key.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        names.Add(key.Substring(i + 1, end - i - 1).Trim());
                        i = end + 1;
                        continue;
                    }
                }
                i++;
            }
            return names.ToArray();
        }

        static string BuildCapturePattern(string key, out int groups)
        {
            var sb = new StringBuilder("^");
            groups = 0;
            int i = 0;
            while (i < key.Length)
            {
                char c = key[i];
                if (c == '{')
                {
                    int end = key.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        string name = key.Substring(i + 1, end - i - 1).Trim();
                        // 号码 / 数量类占位符只吃数字。用 (.*?) 会让
                        // "{number}号没有来" 把前面的整段话吞进去，
                        // 生成 "No. There are 1 empty chairs... 9 didn't come" 这种错句。
                        // 判断依据除了名字，还包括“后面紧跟 号”这种最可靠的写法。
                        int probe = end + 1;
                        while (probe < key.Length && key[probe] == ' ') probe++;
                        bool followedByHao = probe < key.Length && key[probe] == '号';
                        sb.Append(followedByHao || IsNumericPlaceholder(name) ? @"(\d+)" : "(.*?)");
                        groups++;
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            sb.Append('$');
            return sb.ToString();
        }

        /// <summary>模板 key 里占位符之外的汉字数量。用于排除纯标点模板。</summary>
        static int LiteralCjkCount(string key)
        {
            int count = 0;
            bool inPlaceholder = false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c == '{') { inPlaceholder = true; continue; }
                if (c == '}') { inPlaceholder = false; continue; }
                if (inPlaceholder) continue;
                if (c >= '\u4e00' && c <= '\u9fff') count++;
            }
            return count;
        }

        /// <summary>号码 / 数量 / 秒数这类占位符，运行期一定是数字。</summary>
        static bool IsNumericPlaceholder(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n == "number" || n.EndsWith("number")
                || n.EndsWith("count") || n == "count"
                || n == "max" || n.EndsWith("max")
                || n.EndsWith("population")
                || n == "seconds" || n.StartsWith("seconds");
        }

        static readonly Regex _numberListRegex =
            new Regex(@"^\d+\s*号(?:\s*[、,，]\s*\d+\s*号)*$", RegexOptions.CultureInvariant);

        /// <summary>"9号" / "9号、3号" 这类纯号码列表。</summary>
        static bool IsNumberList(string text) => _numberListRegex.IsMatch(text);

        /// <summary>
        /// 语言切换后，让所有 Yarn DialogueRunner 的台词提供器读取对应语言的
        /// Localization 资产（英文 CSV 由 Meeting.yarnproject 的 localisation 配置生成）。
        /// </summary>
        public static void SyncDialogueRunners()
        {
            string code = IsEnglish ? "en" : "zh-CHS";
            DialogueRunner[] runners = UnityEngine.Object.FindObjectsOfType<DialogueRunner>(true);
            for (int i = 0; i < runners.Length; i++)
            {
                DialogueRunner runner = runners[i];
                if (runner == null || runner.YarnProject == null) continue;
                LineProviderBehaviour provider = runner.LineProvider as LineProviderBehaviour;
                if (provider != null && provider.LocaleCode != code)
                    provider.LocaleCode = code;
            }
        }

        static string RestoreNumbers(string template, string source)
        {
            MatchCollection matches = _numberRegex.Matches(source);
            int index = 0;
            return Regex.Replace(template, "#", _ =>
            {
                if (index >= matches.Count) return "#";
                return matches[index++].Value;
            });
        }

        /// <summary>切换语言并广播，供自动翻译器立即刷新。</summary>
        public static void ToggleLanguage()
        {
            Language = IsEnglish ? GameLanguage.Chinese : GameLanguage.English;
        }
    }

    /// <summary>
    /// 现有 UI 的中文由运行时拼装，无法逐个改调用点。
    /// 本组件每 0.3 秒扫描一次 UGUI 的 TMP 文本，在英文模式下用词典替换；
    /// 切回中文时恢复首次捕获的原文。用户输入框与 NPC 世界内号码不会被改写。
    /// </summary>
    public class LocalizationAutoTranslator : MonoBehaviour
    {
        class Entry
        {
            public string original;
            public string lastApplied;
            public TMP_Text text;
        }

        readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        readonly HashSet<string> _untranslated = new HashSet<string>();
        float _timer;
        float _dialogueTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<LocalizationAutoTranslator>() != null) return;
            GameObject go = new GameObject("LocalizationAutoTranslator");
            go.AddComponent<LocalizationAutoTranslator>();
            DontDestroyOnLoad(go);
        }

        void OnEnable()
        {
            Localization.OnLanguageChanged += RefreshAll;
        }

        void OnDisable()
        {
            Localization.OnLanguageChanged -= RefreshAll;
        }

        void Update()
        {
            // 会议 / 私聊 / 结局的 DialogueRunner 都是运行时创建的，
            // 台词提供器默认跟随系统语言；这里持续把语言同步过去。
            _dialogueTimer -= Time.unscaledDeltaTime;
            if (_dialogueTimer <= 0f)
            {
                _dialogueTimer = 1f;
                Localization.SyncDialogueRunners();
            }

            // 新出现的文本（面板打开、气泡生成）每 0.3 秒补充登记一次。
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;
            _timer = 0.3f;
            Discover();
        }

        void LateUpdate()
        {
            // 这一步必须放在 LateUpdate：HUD 脚本每帧在 Update 里重写中文，
            // 若等 0.3 秒轮询才替换，计时器 / 交互提示会以 3Hz 频率中英闪烁。
            ApplyAll();
        }

        void RefreshAll()
        {
            // 切回中文：把词典替换前捕获的原文写回去，再按当前语言重新应用一次。
            foreach (var entry in _entries.Values)
            {
                if (entry.text == null) continue;
                if (entry.original != null) entry.text.text = entry.original;
                entry.lastApplied = entry.original;
            }
            ApplyAll();
        }

        void Discover()
        {
            TextMeshProUGUI[] ugui = FindObjectsOfType<TextMeshProUGUI>(true);
            for (int i = 0; i < ugui.Length; i++) Track(ugui[i]);

            // 世界空间文本：NPC 头顶的低语气泡、场景里的提示牌同样要走词典。
            TextMeshPro[] world = FindObjectsOfType<TextMeshPro>(true);
            for (int i = 0; i < world.Length; i++) Track(world[i]);
        }

        void Track(TMP_Text text)
        {
            if (text == null) return;
            // 输入框里的正文是玩家输入，不能改写；但同一输入框的 placeholder（如“未填写”）要翻译。
            TMP_InputField input = text.GetComponentInParent<TMP_InputField>();
            if (input != null && input.textComponent == text) return;

            int id = text.GetInstanceID();
            Entry entry;
            if (_entries.TryGetValue(id, out entry) && entry.text == text) return;
            _entries[id] = new Entry { text = text, original = text.text, lastApplied = text.text };
        }

        void ApplyAll()
        {
            bool english = Localization.IsEnglish;
            List<int> destroyed = null;

            foreach (var pair in _entries)
            {
                Entry entry = pair.Value;
                TMP_Text text = entry.text;
                if (text == null)
                {
                    if (destroyed == null) destroyed = new List<int>();
                    destroyed.Add(pair.Key);
                    continue;
                }

                string current = text.text ?? string.Empty;
                // 外部系统刚改过文本：把新文本视为原文。
                if (current != entry.lastApplied && current != entry.original)
                    entry.original = current;

                string desired = english ? Localization.Translate(entry.original) : entry.original;
                if (desired == null) desired = string.Empty;

                if (current != desired)
                    text.text = desired;
                entry.lastApplied = desired;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // 自查兜底：英文模式下界面仍残留汉字，说明词典 / 模板漏了一条。
                // 只报一次，方便在开发版里直接定位，而不是等玩家截图。
                if (english && !string.IsNullOrEmpty(desired) &&
                    Regex.IsMatch(desired, "[\\u4e00-\\u9fff]") &&
                    _untranslated.Add(entry.original ?? string.Empty))
                {
                    Debug.LogWarning("[Localization] 英文模式下仍有未翻译文本：" + entry.original);
                }
#endif
            }

            if (destroyed != null)
                for (int i = 0; i < destroyed.Count; i++) _entries.Remove(destroyed[i]);
        }
    }
}
