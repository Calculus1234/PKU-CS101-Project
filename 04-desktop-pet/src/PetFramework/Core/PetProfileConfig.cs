using System.IO;
using System.Text;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【终端用户的角色设定】—— 决定桌宠叫什么、是什么性格。
    ///
    /// 数据流：pet_profile.json（EXE 同级） ⟷ 本组件 ⟷ PetAPI 的 System Prompt
    ///
    /// 为什么单独一个文件而不是塞进 config.json：
    ///   · config.json 管的是"怎么连大模型"（技术参数），pet_profile.json 管的是
    ///     "它是个什么样的角色"（创作内容）。混在一起的话，用户改一个名字
    ///     就得在一堆 URL / Key 里翻。
    ///   · 而且这两份文件的生命周期不同：换服务商要重填 config.json，
    ///     但角色设定不该被牵连。
    ///
    /// ⚠️ 没有这个组件时也能工作：PetAPI 走 GetPromptFragment()，它会退回去
    ///    直接读磁盘上的 pet_profile.json。所以哪怕场景里忘了挂，设定照样生效。
    /// </summary>
    public class PetProfileConfig : MonoBehaviour
    {
        public static PetProfileConfig Instance { get; private set; }

        // 没有组件、也没有文件时的最终兜底
        // ⚠️ 这里的文字【照抄场景里 PetAPI.systemPrompt】的身份描述，不是我编的：
        //    「你是小白，一只活泼可爱的白虎兽人，在电脑桌面上真实地生活着……」
        public const string FallbackName = "小白";
        public const string FallbackPersona =
            "一只活泼可爱的白虎兽人，在电脑桌面上真实地生活着。" +
            "有身体、有情绪、会移动，会走路、会跳跃，可以自由地在桌面上漫步、跳跃、坐着发呆，甚至发脾气。" +
            "说话活泼、可爱、有点黏人。";
        public const string FallbackUserName = "白虎大哥";
        public const string DefaultFileName = "pet_profile.json";

        [Header("配置文件（留空 = EXE 同级 pet_profile.json）")]
        public string settingsPathOverride = "";
        public string fileName = DefaultFileName;
        public bool autoReload = true;

        [Header("默认值（文件不存在时用它，并自动生成一份模板）")]
        public string defaultPetName = FallbackName;
        public string defaultUserName = FallbackUserName;
        [TextArea(2, 4)]
        public string defaultPersona = FallbackPersona;

        [Header("当前设定（面板改的是这里）")]
        public string petName = FallbackName;
        [Tooltip("用户自己的昵称 —— 桌宠会这样称呼他，聊天框里也用这个名字显示他说的话")]
        public string userName = FallbackUserName;
        [TextArea(2, 6)]
        public string persona = FallbackPersona;

        public string SettingsPath { get; private set; }
        public string Status { get; private set; }

        long lastWriteTicks = -1;
        float nextWatch;
        bool ready;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            SettingsPath = PetPathUtil.ResolveBesideExe(
                string.IsNullOrEmpty(fileName) ? DefaultFileName : fileName,
                settingsPathOverride);
            Load();
        }

        void Start() { ready = true; }

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
                    if (lastWriteTicks >= 0)
                    {
                        Debug.Log("[PetProfileConfig] pet_profile.json 被外部改动 → 重新读取");
                        Load();
                    }
                }
            }
            catch { }
        }

        // ---------------- 读 ----------------

        public void Load()
        {
            if (!File.Exists(SettingsPath))
            {
                petName = defaultPetName;
                userName = defaultUserName;
                persona = defaultPersona;
                Status = "没有配置文件，用默认设定";
                Debug.Log("[PetProfileConfig] " + Status + "（将生成模板 " + SettingsPath + "）");
                Save(false);
                return;
            }

            try
            {
                var d = PetVisualRoot.ScanFlatJson(File.ReadAllText(SettingsPath));
                string s;

                petName = defaultPetName;
                if (d.TryGetValue("petName", out s) && !string.IsNullOrEmpty(s.Trim())) petName = s.Trim();
                else if (d.TryGetValue("PetName", out s) && !string.IsNullOrEmpty(s.Trim())) petName = s.Trim();

                userName = defaultUserName;
                if (d.TryGetValue("userName", out s) && !string.IsNullOrEmpty(s.Trim())) userName = s.Trim();
                else if (d.TryGetValue("UserName", out s) && !string.IsNullOrEmpty(s.Trim())) userName = s.Trim();

                persona = defaultPersona;
                if (d.TryGetValue("persona", out s) && !string.IsNullOrEmpty(s.Trim())) persona = Unescape(s.Trim());
                else if (d.TryGetValue("Persona", out s) && !string.IsNullOrEmpty(s.Trim())) persona = Unescape(s.Trim());

                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                Status = "已读取 " + SettingsPath;
                Debug.Log("[PetProfileConfig] ★ " + Status + " | 名字=" + petName + " 设定=" + persona);
            }
            catch (System.Exception e)
            {
                Status = "读取失败：" + e.Message;
                Debug.LogWarning("[PetProfileConfig] " + Status);
            }
        }

        // ---------------- 写 ----------------

        /// <summary>面板点【保存】走这里。</summary>
        public void SaveAndApply()
        {
            Save(true);
            Status = "已保存 → " + SettingsPath + "（下一句话就生效）";
            Debug.Log("[PetProfileConfig] ★ " + Status);
        }

        public void Save() { Save(true); }

        void Save(bool verbose)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"_readme\": \"桌宠的名字、你的昵称、角色设定。改完保存，游戏里 2 秒内自动生效；也可以按 Esc 在游戏里改。\",\n");
                sb.Append("  \"petName\": \"").Append(EscapeOneLine(petName)).Append("\",\n");
                sb.Append("  \"userName\": \"").Append(EscapeOneLine(userName)).Append("\",\n");
                sb.Append("  \"persona\": \"").Append(EscapeOneLine(persona)).Append("\"\n");
                sb.Append("}\n");

                File.WriteAllText(SettingsPath, sb.ToString());
                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                if (verbose) Status = "已保存 → " + SettingsPath;
            }
            catch (System.Exception e)
            {
                Status = "保存失败：" + e.Message;
                Debug.LogWarning("[PetProfileConfig] " + Status);
            }
        }

        static string EscapeOneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "")
                    .Replace("\n", "\\n");
        }

        static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            // ⚠️ 顺序不能反：先把 \\n 还原成换行，再处理 \" 和 \\
            return s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        // ---------------- 给 PetAPI 用 ----------------

        /// <summary>
        /// 组装注入到 System Prompt 的那段话。
        /// 优先级：场景里的组件 → 磁盘上的 pet_profile.json → 内置兜底。
        /// 返回空字符串表示"没有任何设定可注入"。
        /// </summary>
        public static string GetPromptFragment()
        {
            string name, user, persona;
            GetSnapshot(out name, out user, out persona);

            // 设定本身可能已经以句号结尾，别再补一个变成「。。」
            persona = persona.TrimEnd('。', '.', ' ', '\n', '\r');

            var sb = new StringBuilder();
            sb.Append("[用户设定的角色 · 以此为准]\n");
            sb.Append("你的名字叫「").Append(name).Append("」，你的设定是：").Append(persona).Append("。\n");
            sb.Append("正在和你互动的人叫「").Append(user).Append("」，你要这样称呼他（不要叫「主人」）。\n");
            sb.Append("如果下面的默认介绍与这一段冲突（名字、身份、称呼、说话方式），一律以这一段为准。");
            return sb.ToString();
        }

        /// <summary>取当前生效的三个设定。降级链：组件 → 磁盘 → 内置兜底。</summary>
        public static void GetSnapshot(out string petName, out string userName, out string persona)
        {
            var inst = Instance;
            if (inst != null)
            {
                petName = inst.petName;
                userName = inst.userName;
                persona = inst.persona;
            }
            else
            {
                ReadFromDisk(out petName, out userName, out persona);
            }

            if (string.IsNullOrEmpty(petName)) petName = FallbackName;
            if (string.IsNullOrEmpty(userName)) userName = FallbackUserName;
            if (string.IsNullOrEmpty(persona)) persona = FallbackPersona;

            petName = petName.Trim();
            userName = userName.Trim();
            persona = persona.Trim();
        }

        /// <summary>UI 里到处要用的两个名字（带兜底，永远不会返回空）。</summary>
        public static string PetName
        {
            get { string n, u, p; GetSnapshot(out n, out u, out p); return n; }
        }

        public static string UserName
        {
            get { string n, u, p; GetSnapshot(out n, out u, out p); return u; }
        }

        /// <summary>面板显示的一行摘要。</summary>
        public string Readable()
        {
            return petName + "（称呼你为 " + userName + "）· "
                 + (persona.Length > 20 ? persona.Substring(0, 20) + "…" : persona);
        }

        static void ReadFromDisk(out string name, out string userName, out string persona)
        {
            name = null; userName = null; persona = null;
            try
            {
                string p = PetPathUtil.ResolveBesideExe(DefaultFileName);
                if (!File.Exists(p)) return;
                var d = PetVisualRoot.ScanFlatJson(File.ReadAllText(p));
                string s;
                if (d.TryGetValue("petName", out s)) name = s;
                else if (d.TryGetValue("PetName", out s)) name = s;

                if (d.TryGetValue("userName", out s)) userName = s;
                else if (d.TryGetValue("UserName", out s)) userName = s;

                if (d.TryGetValue("persona", out s)) persona = Unescape(s);
                else if (d.TryGetValue("Persona", out s)) persona = Unescape(s);
            }
            catch { }
        }
    }
}
