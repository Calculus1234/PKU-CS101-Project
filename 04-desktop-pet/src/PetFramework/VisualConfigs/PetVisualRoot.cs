using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【形态根组件】—— 把 PetVisualConfig 变成实际外观。
    ///
    /// 职责：
    ///   ① 自动找目标材质和主平行光
    ///   ② 启动时读取 JSON 参数文件并套用（所以改参数不用重新打包）
    ///   ③ 文件被外部改动时热重载（默认 2 秒内生效）
    ///   ④ 旧版 fur_settings.txt 自动迁移成 JSON
    ///
    /// F9 调参面板（PetVisualTuner / 旧名 FurTuner）只负责画界面，真正的读写都在这儿。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetVisualRoot : MonoBehaviour
    {
        [Header("形态配置（拖一个 FurConfig / ScaleConfig / MetalConfig 进来）")]
        public PetVisualConfig config;

        [Header("多形态（可选；留空 = 单形态老路子，行为和以前完全一样）")]
        [Tooltip("里面放一个 PetForm 就是「单形态打包成多形态列表第一个元素」；未来加虎形态再塞第二个")]
        public PetFormSet formSet;

        [Header("目标（留空会自动找）")]
        public Renderer targetRenderer;
        public string petObjectName = "WhiteTiger_DesktopPet";
        public Light directionalLight;
        [Tooltip("留空会自动找 petObjectName 上的 Animator（多形态换动作要用）")]
        public Animator petAnimator;

        [Header("参数文件")]
        [Tooltip("留空 = 放 EXE（编辑器里是工程根目录）同级")]
        public string settingsPathOverride = "";
        public string fileName = "pet_visual.json";
        [Tooltip("旧版 fur_settings.txt 的路径，填了会自动迁移成 JSON")]
        public string legacyPath = "";
        public bool autoReload = true;
        public float watchIntervalSec = 2f;

        // ---------------- 运行时状态 ----------------
        public Material Material { get; private set; }
        public string SettingsPath { get; private set; }
        public string Status { get; private set; }
        public bool Ready { get; private set; }

        /// <summary>当前形态（多形态没配时为 null，走 config 单配置老路）。</summary>
        public PetForm CurrentForm { get; private set; }

        public string CurrentFormName
        { get { return CurrentForm != null ? CurrentForm.formName : "(单形态)"; } }

        /// <summary>
        /// 真正生效的材质配置：优先用当前形态的，没有就退回 config。
        /// ⇒ formSet 为空时，这里恒等于 config，**行为和改造前一模一样**。
        /// </summary>
        public PetVisualConfig ActiveConfig
        {
            get
            {
                var f = CurrentForm;
                if (f != null && f.visualConfig != null) return f.visualConfig;
                return config;
            }
        }

        long lastWriteTicks = -1;
        float nextWatch;

        /// <summary>参数被改过之后想让外部知道（比如面板刷新显示）。</summary>
        public System.Action onApplied;

        void Awake()
        {
            SettingsPath = ResolveSettingsPath();
            Refresh();
        }

        // Start 用协程等一帧：PetCameraFraming 之类的组件会在自己的 Start 里改灯光/速度，
        // 必须等它们跑完再套用我们的参数，否则会被覆盖。
        IEnumerator Start()
        {
            yield return null;
            PickStartForm();          // ★ 必须先定形态，ActiveConfig 才知道用哪套参数
            MigrateLegacyIfNeeded();
            LoadJson();
            Ready = true;
        }

        void Update()
        {
            if (autoReload) WatchFile();
        }

        // ---------------- 找目标 ----------------

        public void Refresh()
        {
            if (targetRenderer == null)
            {
                var go = !string.IsNullOrEmpty(petObjectName) ? GameObject.Find(petObjectName) : null;
                if (go != null) targetRenderer = go.GetComponentInChildren<Renderer>(true);
            }
            if (targetRenderer == null) targetRenderer = GetComponentInChildren<Renderer>(true);

            if (targetRenderer != null && targetRenderer.sharedMaterials.Length > 0)
                Material = targetRenderer.sharedMaterials[0];

            if (directionalLight == null)
            {
                var lights = FindObjectsOfType<Light>();
                for (int i = 0; i < lights.Length; i++)
                    if (lights[i].type == LightType.Directional) { directionalLight = lights[i]; break; }
            }
        }

        public PetVisualContext MakeContext()
        {
            // 优先用名字找本体（Renderer 可能在子物体上，那样取到的位置是错的）
            GameObject petGo = null;
            if (!string.IsNullOrEmpty(petObjectName)) petGo = GameObject.Find(petObjectName);
            if (petGo == null && targetRenderer != null) petGo = targetRenderer.gameObject;

            return new PetVisualContext
            {
                material = Material,
                directionalLight = directionalLight,
                pet = petGo,
                log = m => Debug.Log("[PetVisualRoot] " + m)
            };
        }

        // ---------------- 套用 ----------------

        public void Apply()
        {
            var cfg = ActiveConfig;
            if (cfg == null) { Status = "没有挂形态配置"; return; }
            Refresh();
            var ctx = MakeContext();
            cfg.Apply(ctx);
            cfg.ApplyExtra(ctx);
            if (onApplied != null) onApplied();
        }

        // ---------------- 多形态（骨架）----------------

        /// <summary>启动时选定初始形态。只有配了 formSet 才生效。</summary>
        void PickStartForm()
        {
            if (formSet == null || formSet.Count == 0) { CurrentForm = null; return; }
            CurrentForm = formSet.GetStartForm();
            Debug.Log("[PetVisualRoot] 初始形态 → " + (CurrentForm != null ? CurrentForm.Readable() : "(空)")
                      + "　（清单里共 " + formSet.Count + " 个形态）");
            ApplyFormAnimation();
        }

        /// <summary>
        /// 【补漏】把当前形态的 PetAnimationConfig 真正套到模型的 Animator 上。
        ///
        /// ⚠️ 框架原来只在注释里写了这一句（SwitchForm 的 TODO），**从来没被调用过** ——
        ///    也就是说 PetAnimationConfig 的 11 个槽位一直是"摆设"，
        ///    实际播的永远只是基础 controller 里的原片段。这里补上落地。
        /// </summary>
        public void ApplyFormAnimation()
        {
            var form = CurrentForm;
            if (form == null || form.animationConfig == null) return;

            if (petAnimator == null)
            {
                GameObject petGo = !string.IsNullOrEmpty(petObjectName) ? GameObject.Find(petObjectName) : null;
                if (petGo != null) petAnimator = petGo.GetComponent<Animator>();
                if (petAnimator == null && targetRenderer != null)
                    petAnimator = targetRenderer.GetComponentInParent<Animator>();
            }
            if (petAnimator == null)
            {
                Debug.LogWarning("[PetVisualRoot] 形态「" + form.formName + "」配了动作，但场景里找不到 Animator");
                return;
            }

            var ov = form.animationConfig.BuildOverride();
            if (ov == null)
            {
                Debug.Log("[PetVisualRoot] 形态「" + form.formName + "」没有可覆盖的片段，沿用基础状态机 "
                          + (form.animationConfig.baseController != null ? form.animationConfig.baseController.name : "(空)"));
                return;
            }
            petAnimator.runtimeAnimatorController = ov;
            Debug.Log("[PetVisualRoot] ★ 已套用动作覆盖 → " + form.animationConfig.name + "（"
                      + form.animationConfig.Readable() + "）");
        }

        /// <summary>
        /// 【骨架】切到某个形态。**现在只切指针 + 打日志，外观/动画都不会变。**
        /// 真做变身时，要在这里补上三件事：
        ///   ① 换模型：Instantiate(form.modelPrefab) 替换 targetRenderer，然后 Refresh()
        ///   ② 换动画：animator.runtimeAnimatorController = form.animationConfig.BuildOverride()
        ///   ③ 过渡：按 form.animationConfig.transitionSeconds 播变身特效/动画
        /// 返回 true 只代表"形态存在、指针已切"，不代表外观已经变了。
        /// </summary>
        public bool SwitchForm(string formName)
        {
            if (formSet == null || formSet.Count == 0)
            {
                Debug.LogWarning("[PetVisualRoot] 还没有配置多形态（formSet 为空），switch_form 无效。");
                return false;
            }
            if (!formSet.SwitchTo(formName)) return false;

            CurrentForm = formSet.Current;

            // TODO(未来)：换模型 / 换动画 / 播放变身过渡
            Debug.Log("[PetVisualRoot] ★ 形态指针 → " + (CurrentForm != null ? CurrentForm.formName : "?")
                      + "　⚠ 换装逻辑尚未实现，外观保持不变（这是预期行为）");
            return true;
        }

        /// <summary>当前能变成哪些形态（给 AI 工具用）。形如 "humanoid（兽人形态）、tiger（虎形态）"。</summary>
        public string AvailableFormsReadable()
        {
            if (formSet == null || formSet.Count == 0) return "（只有当前形态）";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < formSet.forms.Count; i++)
            {
                var f = formSet.forms[i];
                if (f == null) continue;
                if (sb.Length > 0) sb.Append("、");
                sb.Append(f.formName).Append("（").Append(f.displayName).Append("）");
            }
            return sb.Length > 0 ? sb.ToString() : "（空）";
        }

        // ---------------- 路径 ----------------

        string ResolveSettingsPath()
        {
            return ResolveConfigPath(settingsPathOverride, fileName);
        }

        /// <summary>
        /// 找配置文件。顺序：① 显式指定的路径 ② EXE 同级 ③ EXE 的【上一级】。
        ///
        /// ⚠️ 为什么要找上一级：编辑器里 Application.dataPath 是 &lt;工程&gt;/Assets，
        /// 上一级正好是工程根目录；而【成品里是 &lt;Build&gt;/MyPet_Data，上一级是 &lt;Build&gt;】。
        /// 不往上一级找的话，编辑器里能读到的配置，打成 EXE 就"消失"了
        /// （因为文件在工程根目录，而 EXE 只在自己的 Build 目录里翻）。
        /// 都不存在 → 返回 EXE 同级，保存时会建在那儿（新装用户第一次保存就落位）。
        /// </summary>
        public static string ResolveConfigPath(string overridePath, string fileName)
        {
            // 实现在 PetPathUtil 里（所有"放 EXE 旁边"的配置共用一套规则，别再各写一份）
            return PetPathUtil.ResolveBesideExe(fileName, overridePath);
        }

        // ---------------- 读 / 写 ----------------

        /// <summary>旧版 fur_settings.txt → pet_visual.json（只做一次）。</summary>
        void MigrateLegacyIfNeeded()
        {
            if (string.IsNullOrEmpty(legacyPath)) return;
            if (File.Exists(SettingsPath)) return;
            if (!File.Exists(legacyPath)) return;
            var cfg = ActiveConfig;
            if (cfg == null) return;

            try
            {
                var kv = ParseKeyValue(File.ReadAllLines(legacyPath));
                int n = 0;
                for (int i = 0; i < cfg.parameters.Count; i++)
                {
                    var p = cfg.parameters[i];
                    string s;
                    if (kv.TryGetValue(p.key, out s)) { p.value = ParseF(s, p.value); n++; }
                }
                // 旧版把方向拆成三个键
                if (cfg.Find("LockedViewDir") != null)
                {
                    var lp = cfg.Find("LockedViewDir");
                    string x, y, z;
                    if (kv.TryGetValue("LockedViewX", out x) && kv.TryGetValue("LockedViewY", out y) && kv.TryGetValue("LockedViewZ", out z))
                    {
                        lp.vector = new Vector3(ParseF(x, 0f), ParseF(y, 0f), ParseF(z, 1f));
                        if (lp.vector.sqrMagnitude > 1e-6f) lp.vector = lp.vector.normalized;
                        n++;
                    }
                }
                SaveJson();
                Debug.Log("[PetVisualRoot] 已把旧版 " + legacyPath + " 迁移成 " + SettingsPath + "（" + n + " 项）");
            }
            catch (System.Exception e) { Debug.LogWarning("[PetVisualRoot] 迁移旧参数失败：" + e.Message); }
        }

        void LoadJson()
        {
            var cfg = ActiveConfig;
            if (cfg == null) { Status = "没有挂形态配置"; return; }
            if (!File.Exists(SettingsPath))
            {
                Status = "没有参数文件，用配置里的默认值";
                Apply();
                Debug.Log("[PetVisualRoot] " + Status + "（" + SettingsPath + "）");
                return;
            }
            try
            {
                string text = File.ReadAllText(SettingsPath);
                int n = ApplyJson(text, cfg);
                Apply();
                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                Status = "已读 " + n + " 项";
                Debug.Log("[PetVisualRoot] ★ " + Status + " ← " + SettingsPath);
            }
            catch (System.Exception e)
            {
                Status = "读取失败：" + e.Message;
                Debug.LogWarning("[PetVisualRoot] " + Status);
            }
        }

        /// <summary>面板上的「重新读取文件」按钮。</summary>
        public void Reload() { LoadJson(); }

        public void SaveJson()
        {
            var cfg = ActiveConfig;
            if (cfg == null) return;
            try
            {
                // ★ 合并保存：文件里可能还有【不属于本配置的键】（比如 AI 的 Model / TimeoutSec，
                //   由项目自己的 PetAppSettings 读写）。直接整份重写会把这些键冲掉，
                //   用户在 F9 面板点一下"保存"，AI 配置就没了。所以先把别人的键捞出来再拼回去。
                var extra = new Dictionary<string, string>();
                if (File.Exists(SettingsPath))
                {
                    var old = ScanFlatJson(File.ReadAllText(SettingsPath));
                    foreach (var kv in old)
                    {
                        if (kv.Key.StartsWith("_")) continue;            // 元数据/分组注释，我们会重新生成
                        if (cfg.Find(kv.Key) != null) continue;        // 本配置的参数，重新生成
                        extra[kv.Key] = kv.Value;                         // 别人的键，原样保留
                    }
                }

                File.WriteAllText(SettingsPath, ToJson(cfg, extra));
                try { lastWriteTicks = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc(); } catch { }
                Status = "已保存 → " + SettingsPath + (extra.Count > 0 ? "（保留了 " + extra.Count + " 项外部配置）" : "");
                Debug.Log("[PetVisualRoot] " + Status);
            }
            catch (System.Exception e)
            {
                Status = "保存失败：" + e.Message;
                Debug.LogWarning("[PetVisualRoot] " + Status);
            }
        }

        void WatchFile()
        {
            if (!Ready) return;
            if (Time.time < nextWatch) return;
            nextWatch = Time.time + Mathf.Max(0.5f, watchIntervalSec);
            try
            {
                if (!File.Exists(SettingsPath)) return;
                long t = File.GetLastWriteTimeUtc(SettingsPath).ToFileTimeUtc();
                if (t != lastWriteTicks)
                {
                    if (lastWriteTicks >= 0) Debug.Log("[PetVisualRoot] 参数文件被外部改动 → 自动重新套用");
                    LoadJson();
                }
            }
            catch { }
        }

        // ================= JSON 读写（扁平格式，方便非开发者手改）=================

        /// <summary>
        /// 输出成这样的扁平 JSON —— 一个参数一行，好找好改：
        /// {
        ///   "_readme": "改完保存，游戏里 2 秒内自动生效",
        ///   "TotalShellStep": 0.045,
        ///   "LockedViewDir": [0.49, 0.24, 0.84]
        /// }
        /// </summary>
        public static string ToJson(PetVisualConfig config) { return ToJson(config, null); }

        public static string ToJson(PetVisualConfig config, Dictionary<string, string> extra)
        {
            var lines = new List<string>();
            lines.Add("  \"_readme\": \"形态参数。改完保存，游戏里 2 秒内自动生效。删掉某行 = 恢复该参数的默认值。\"");
            lines.Add("  \"_preset\": \"" + Escape(config.presetName) + "\"");

            string lastGroup = null;
            for (int i = 0; i < config.parameters.Count; i++)
            {
                var p = config.parameters[i];
                if (p == null || string.IsNullOrEmpty(p.key)) continue;
                if (!string.IsNullOrEmpty(p.group) && p.group != lastGroup)
                {
                    lastGroup = p.group;
                    lines.Add("  \"_组_" + Escape(lastGroup) + "\": \"↓ 以下属于【" + Escape(lastGroup) + "】\"");
                }
                lines.Add("  \"" + Escape(p.key) + "\": " + ValueText(p));
            }

            // 原样保留不属于本配置的键（AI 配置等）
            if (extra != null && extra.Count > 0)
            {
                lines.Add("  \"_组_外部配置\": \"↓ 以下是别的模块写的，F9 面板不认识，但会原样保留\"");
                foreach (var kv in extra)
                    lines.Add("  \"" + Escape(kv.Key) + "\": " + RawOrQuoted(kv.Value));
            }

            return "{\n" + string.Join(",\n", lines.ToArray()) + "\n}\n";
        }

        /// <summary>数字/数组原样输出，其余当作字符串加引号（这样 Model=qwen3.5:4b 不会被解析坏）。</summary>
        static string RawOrQuoted(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "\"\"";
            string t = raw.Trim();
            if (t.StartsWith("[")) return t;                       // 数组原样
            float f;
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return t;
            if (t.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
                t.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return t.ToLowerInvariant();
            return "\"" + Escape(t) + "\"";
        }

        static string ValueText(PetVisualParam p)
        {
            if (p.kind == PetParamKind.Vector)
                return "[" + F(p.vector.x) + ", " + F(p.vector.y) + ", " + F(p.vector.z) + "]";
            if (p.kind == PetParamKind.Color)
                return "[" + F(p.color.r) + ", " + F(p.color.g) + ", " + F(p.color.b) + "]";
            if (p.kind == PetParamKind.Int) return Mathf.RoundToInt(p.value).ToString(CultureInfo.InvariantCulture);
            return F(p.value);
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        /// <summary>把扁平 JSON 套回配置，返回套用成功的项数。容错：认不出的键直接跳过。</summary>
        public static int ApplyJson(string json, PetVisualConfig config)
        {
            if (string.IsNullOrEmpty(json) || config == null) return 0;
            int n = 0, i = 0;
            while (i < json.Length)
            {
                int q1 = json.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = FindStringEnd(json, q1 + 1);
                if (q2 < 0) break;
                string key = Unescape(json.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;

                int colon = json.IndexOf(':', i);
                if (colon < 0) break;
                i = colon + 1;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;

                if (json[i] == '[')
                {
                    int end = json.IndexOf(']', i);
                    if (end < 0) break;
                    string[] parts = json.Substring(i + 1, end - i - 1).Split(',');
                    i = end + 1;
                    var v = new float[3];
                    for (int k = 0; k < 3; k++) v[k] = k < parts.Length ? ParseF(parts[k], 0f) : 0f;

                    var p = config.Find(key);
                    if (p != null)
                    {
                        if (p.kind == PetParamKind.Color) { p.color = new Color(v[0], v[1], v[2], 1f); n++; }
                        else if (p.kind == PetParamKind.Vector) { p.vector = new Vector3(v[0], v[1], v[2]); n++; }
                    }
                }
                else if (json[i] == '"')
                {
                    int e = FindStringEnd(json, i + 1);      // 字符串值 = 元数据，跳过
                    if (e < 0) break;
                    i = e + 1;
                }
                else
                {
                    int e = i;
                    while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\n' && json[e] != '\r') e++;
                    string raw = json.Substring(i, e - i).Trim();
                    i = e;
                    var p = config.Find(key);
                    if (p != null && p.kind != PetParamKind.Vector && p.kind != PetParamKind.Color)
                    {
                        p.value = ParseF(raw, p.value);
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>找字符串的收尾引号，跳过 \" 转义。</summary>
        static int FindStringEnd(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        public static float ParseF(string s, float def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().TrimEnd(',');
            float v;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v))
                return v;
            if (float.TryParse(s, out v) && !float.IsNaN(v) && !float.IsInfinity(v)) return v;
            if (s.Equals("true", System.StringComparison.OrdinalIgnoreCase)) return 1f;
            if (s.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return 0f;
            return def;
        }

        /// <summary>
        /// 把扁平 JSON 扫成 键→原文字符串。（给"框架之外的胶水层"复用，
        /// 比如项目自己的 PetAppSettings 要读同一个文件里的 AI 配置。）
        /// </summary>
        public static Dictionary<string, string> ScanFlatJson(string json)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return d;
            int i = 0;
            while (i < json.Length)
            {
                int q1 = json.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = FindStringEnd(json, q1 + 1);
                if (q2 < 0) break;
                string key = Unescape(json.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;

                int colon = json.IndexOf(':', i);
                if (colon < 0) break;
                i = colon + 1;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;

                if (json[i] == '[')
                {
                    int end = json.IndexOf(']', i);
                    if (end < 0) break;
                    d[key] = json.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else if (json[i] == '"')
                {
                    int e = FindStringEnd(json, i + 1);
                    if (e < 0) break;
                    d[key] = json.Substring(i + 1, e - i - 1);
                    i = e + 1;
                }
                else
                {
                    int e = i;
                    while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\n' && json[e] != '\r') e++;
                    d[key] = json.Substring(i, e - i).Trim();
                    i = e;
                }
            }
            return d;
        }

        static Dictionary<string, string> ParseKeyValue(string[] lines)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln == null) continue;
                ln = ln.Trim();
                if (ln.Length == 0 || ln[0] == '#' || ln[0] == '/' || ln[0] == ';') continue;
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                d[ln.Substring(0, eq).Trim()] = ln.Substring(eq + 1).Trim();
            }
            return d;
        }
    }
}
