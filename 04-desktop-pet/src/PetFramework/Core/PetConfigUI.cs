using System.Collections;
using System.Text;
using PetFramework.Tools;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PetFramework
{
    /// <summary>
    /// 【面向终端用户的接口配置面板】—— 按 Esc 呼出。
    ///
    /// 给"完全不懂 Unity"的人用：填 BaseUrl / Model / ApiKey → 保存 → 立刻生效。
    /// 保存会写进 EXE 同级的 config.json（并带"向上一级回退"的查找，和别的配置一致）。
    ///
    /// 保命红线：这个面板【只读写配置 + 调 PetAPI 的公开字段】，不碰请求/解析/记忆任何逻辑。
    /// </summary>
    public class PetConfigUI : MonoBehaviour
    {
        /// <summary>配置面板是否开着。别的模块（比如 ChatUI）靠它避免抢按键。</summary>
        public static bool IsOpen { get; private set; }

        [Header("呼出键")]
        public KeyCode toggleKey = KeyCode.Escape;

        [Header("外观")]
        public float panelWidth = 620f;
        public float panelHeight = 430f;

        bool visible;
        bool showKey;                   // ApiKey 是否明文显示
        bool confirmReset;              // 正在弹"确定要清空记忆吗"的二次确认
        string status = "";
        string testResult = "";
        bool testing;
        Vector2 scroll;
        int tab;                        // 0=接口 1=角色 2=工具
        Vector2 toolScroll;

        GraphicRaycaster[] raycasters;
        Collider[] petColliders;
        bool uiBlocked;

        PetApiConfig Cfg
        {
            get
            {
                if (PetApiConfig.Instance != null) return PetApiConfig.Instance;
    #if UNITY_2022_2_OR_NEWER
                return Object.FindFirstObjectByType<PetApiConfig>();
    #else
                return Object.FindObjectOfType<PetApiConfig>();
    #endif
            }
        }

        /// <summary>角色设定组件（名字 / 人设）。没挂也不影响 PetAPI 从文件读。</summary>
        PetProfileConfig Profile
        {
            get
            {
                if (PetProfileConfig.Instance != null) return PetProfileConfig.Instance;
    #if UNITY_2022_2_OR_NEWER
                return Object.FindFirstObjectByType<PetProfileConfig>();
    #else
                return Object.FindObjectOfType<PetProfileConfig>();
    #endif
            }
        }

        void Start() { SetUIBlock(false); }

        void Update()
        {
            IsOpen = visible;

            if (!visible)
            {
                // 聊天框开着时 Esc 归它（先关聊天），不要抢
                var chat = FindChat();
                if (chat != null && chat.IsVisible) return;

                if (Input.GetKeyDown(toggleKey)) Open();
            }
            else
            {
                if (Input.GetKeyDown(toggleKey)) Close();
            }

            if (visible) TransparentWindow.mouseOverPet = true;   // 别让点击穿透掉
            else if (uiBlocked) SetUIBlock(false);
        }

        ChatUI FindChat()
        {
    #if UNITY_2022_2_OR_NEWER
            return Object.FindFirstObjectByType<ChatUI>();
    #else
            return Object.FindObjectOfType<ChatUI>();
    #endif
        }

        void Open()
        {
            visible = true;
            IsOpen = true;
            var c = Cfg;
            if (c != null) c.PullFromApi();       // 同步一下当前真实生效的值
            status = "改完点【保存并生效】；也可以直接改 EXE 同级的 config.json";
            SetUIBlock(true);
            Debug.Log("[PetConfigUI] 打开配置面板");
        }

        void Close()
        {
            visible = false;
            IsOpen = false;
            confirmReset = false;      // 关面板 = 顺手取消未确认的弹窗
            SetUIBlock(false);
            Debug.Log("[PetConfigUI] 关闭配置面板");
        }

        // 面板打开时：屏蔽后层 uGUI 的点击接收 + 小白的碰撞体，避免"点输入框误点到后面"
        void SetUIBlock(bool blocked)
        {
            if (blocked && (raycasters == null || raycasters.Length == 0)) raycasters = FindObjectsOfType<GraphicRaycaster>();
            if (blocked && (petColliders == null || petColliders.Length == 0))
            {
                var pet = GameObject.Find("WhiteTiger_DesktopPet");
                if (pet != null) petColliders = pet.GetComponentsInChildren<Collider>(true);
            }
            if (raycasters != null) foreach (var r in raycasters) if (r != null) r.enabled = !blocked;
            if (petColliders != null) foreach (var c in petColliders) if (c != null) c.enabled = !blocked;
            uiBlocked = blocked;
        }

        void OnGUI()
        {
            if (!visible) return;

            GUI.depth = -2000;
            GUI.skin.label.fontSize = 15;
            GUI.skin.button.fontSize = 15;
            GUI.skin.textField.fontSize = 15;
            GUI.skin.textArea.fontSize = 15;

            float w = Mathf.Min(panelWidth, Screen.width - 40f);
            float h = Mathf.Min(panelHeight, Screen.height - 40f);
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GUILayout.BeginArea(rect, GUI.skin.box);

            var c = Cfg;
            if (c == null)
            {
                GUILayout.Label("场景里没有 PetApiConfig 组件，配置面板无法工作。");
                if (GUILayout.Button("关闭 (Esc)")) Close();
                GUILayout.EndArea();
                return;
            }

            // 标题跟着角色设定走：改了名字，面板上立刻就是新名字
            var prof0 = Profile;
            string who = (prof0 != null && !string.IsNullOrEmpty(prof0.petName)) ? prof0.petName : "桌宠";
            GUILayout.Label("<b>" + who + " · 接口与角色配置　（按 Esc 关闭）</b>");
            GUILayout.Label("改完点下面的【保存并生效】，配置会写进 EXE 同级的 config.json");
            GUILayout.Space(6);

            // 二次确认弹着的时候，把主面板整个禁掉 —— 这才是真正防止误点的机制
            GUI.enabled = !confirmReset;

            // ---- Tab 栏 ----
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(tab == 0, "① 接口", GUI.skin.button, GUILayout.Height(28))) tab = 0;
            if (GUILayout.Toggle(tab == 1, "② 角色", GUI.skin.button, GUILayout.Height(28))) tab = 1;
            if (GUILayout.Toggle(tab == 2, "③ 工具与 MCP", GUI.skin.button, GUILayout.Height(28))) tab = 2;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            scroll = GUILayout.BeginScrollView(scroll);

            if (tab == 0)
            {

            // ---- 协议 ----
            GUILayout.Label("① 接口协议");
            GUILayout.BeginHorizontal();
            bool isLocal = c.provider == PetApiProvider.Ollama;
            if (GUILayout.Toggle(isLocal, " 本地 Ollama（不需要 Key）") != isLocal)
                c.provider = PetApiProvider.Ollama;
            bool isCloud = c.provider == PetApiProvider.OpenAI;
            if (GUILayout.Toggle(isCloud, " 云端（OpenAI 兼容：硅基流动 / DeepSeek / 通义…）") != isCloud)
                c.provider = PetApiProvider.OpenAI;
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // ---- BaseUrl ----
            GUILayout.Label("② BaseUrl");
            if (c.provider == PetApiProvider.Ollama)
                GUILayout.Label("　本地一般填：http://localhost:11434　（只填域名也行，会自动补 /api/chat）");
            else
                GUILayout.Label("　云端一般填到版本号为止，例如：https://api.siliconflow.cn/v1");
            c.baseUrl = GUILayout.TextField(c.baseUrl ?? "", GUILayout.Height(26));

            GUILayout.Space(8);

            // ---- Model ----
            GUILayout.Label("③ 模型名 Model");
            if (c.provider == PetApiProvider.Ollama) GUILayout.Label("　例如：qwen3.5:4b");
            else GUILayout.Label("　例如：Qwen/Qwen2.5-7B-Instruct（要填服务商那边的完整模型名）");
            c.model = GUILayout.TextField(c.model ?? "", GUILayout.Height(26));

            GUILayout.Space(8);

            // ---- ApiKey ----
            GUILayout.Label("④ API Key" + (c.provider == PetApiProvider.Ollama ? "（本地不需要，可留空）" : "（云端必填）"));
            GUILayout.BeginHorizontal();
            string k = c.apiKey ?? "";
            string nk = showKey ? GUILayout.TextField(k, GUILayout.Height(26))
                                : GUILayout.PasswordField(k, '*', GUILayout.Height(26));
            if (nk != k) c.apiKey = nk;
            if (GUILayout.Button(showKey ? "隐藏" : "显示", GUILayout.Width(60), GUILayout.Height(26))) showKey = !showKey;
            GUILayout.EndHorizontal();
            GUILayout.Label("　只存在你本机的 config.json 里，不会上传");

            GUILayout.Space(10);

            // ---- 实际会请求的地址（防止填错自己不知道）----
            var api = PetAPI.Instance;
            if (api != null)
            {
                string preview = PreviewEndpoint(c);
                GUILayout.Label("实际请求地址：" + preview);
            }

            GUILayout.Space(10);

            }   // ===== end tab 0 =====

            // ---- 角色设定（名字 / 人设）----
            //  数据落在 pet_profile.json，和 config.json 分开：
            //  换 API 服务商不该把你的角色设定一起冲掉。
            if (tab == 1)
            {
            GUILayout.Label("角色设定（名字 / 人设）");
            var prof = Profile;
            if (prof == null)
            {
                GUILayout.Label("　场景里没有 PetProfileConfig 组件；PetAPI 仍会直接读 pet_profile.json");
            }
            else
            {
                GUILayout.Label("　桌宠的名字（它会这样称呼自己，聊天框也显示这个名字）");
                prof.petName = GUILayout.TextField(prof.petName ?? "", GUILayout.Height(26));

                GUILayout.Label("　你的昵称（它会这样称呼你，聊天框也用这个名字显示你说的话）");
                prof.userName = GUILayout.TextField(prof.userName ?? "", GUILayout.Height(26));

                GUILayout.Label("　角色设定 / 性格（会拼进 System Prompt 最前面）");
                prof.persona = GUILayout.TextArea(prof.persona ?? "", GUILayout.Height(72));

                GUILayout.Label("　例：傲娇、守护神兽 / 活泼、黏人、怕寂寞 / 冷静、话少但可靠");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("保存角色设定", GUILayout.Height(30), GUILayout.Width(150)))
                {
                    prof.SaveAndApply();
                    status = prof.Status;
                }
                GUILayout.Label("　配置文件：" + prof.SettingsPath);
                GUILayout.EndHorizontal();
            }
            }   // ===== end tab 1 =====

            if (tab == 2) DrawToolsTab();

            GUILayout.EndScrollView();

            GUILayout.Space(6);

            // ---- 按钮 ----
            if (tab == 2)
            {
                if (GUILayout.Button("↻ 重新加载工具（pet_tools.json）", GUILayout.Height(34)))
                {
                    if (ToolRegistry.Instance != null) ToolRegistry.Instance.ReloadNow();
                    status = "已请求重新加载工具配置";
                }
            }
            else
            {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("★ 保存并生效", GUILayout.Height(34)))
            {
                c.SaveAndApply();
                status = c.Status;
                // ★ 顺手把角色设定一起存了 —— 用户按这个按钮时，
                //   心里的预期就是"我刚改的都生效"，不该还要再点一次别的按钮。
                var pf = Profile;
                if (pf != null) { pf.SaveAndApply(); status += "　|　" + pf.Status; }
            }
            if (GUILayout.Button("↻ 重新读取文件", GUILayout.Height(34)))
            {
                c.Load(); c.ApplyToApi(); status = c.Status;
                var pf = Profile;
                if (pf != null) { pf.Load(); status += "　|　" + pf.Status; }
            }
            if (GUILayout.Button(testing ? "测试中…" : "测试连接", GUILayout.Height(34), GUILayout.Width(110)))
            {
                if (!testing) { testing = true; testResult = ""; StartCoroutine(TestRoutine(c)); }
            }
            GUILayout.EndHorizontal();
            }   // end tab != 2

            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
            if (!string.IsNullOrEmpty(testResult)) GUILayout.Label(testResult);
            GUILayout.Label("配置文件：" + c.SettingsPath);

            if (tab == 0)
            {
            GUILayout.Space(8);

            // ---- 重置记忆（出厂设置）----
            GUILayout.Label("——— 出厂设置 ———");
            GUILayout.Label("清空它记得的所有事，回到刚认识的初始状态。");
            GUILayout.Label("（" + who + " 的对话历史 + 长期记忆 + 行为约定）");
            GUILayout.Label("⚠️ 只清记忆，接口配置和形态参数不受影响。");
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.86f, 0.26f, 0.26f);   // 红色 = 危险操作
            if (GUILayout.Button("重置记忆", GUILayout.Height(36)))
                confirmReset = true;
            GUI.backgroundColor = oldBg;
            }

            GUI.enabled = true;                 // 恢复（上面那一段可能是禁用态）
            GUILayout.EndArea();

            if (confirmReset) DrawConfirmReset();
        }

        // ==================== 工具与 MCP ====================

        void DrawToolsTab()
        {
            var reg = ToolRegistry.Instance;
            if (reg == null)
            {
                GUILayout.Label("场景里没有 ToolRegistry 组件，工具配置不可用。");
                return;
            }

            // ---- 总闸 ----
            GUILayout.Label("<b>① 外部工具总闸</b>");
            bool master = reg.ExternalEnabled;
            bool newMaster = GUILayout.Toggle(master, " 启用外部工具（写回 pet_tools.json 的 enableExternalTools）");
            if (newMaster != master) reg.SetExternalMaster(newMaster);
            GUILayout.Label("　" + reg.Status);

            GUILayout.Space(10);

            // ---- 内置工具 ----
            GUILayout.Label("<b>② 内置工具权限</b>");
            GUILayout.Label("　关掉 = 模型完全不知道有这个工具（不是「调用了被拒绝」）");
            var all = reg.All;
            int builtinShown = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if (d == null || d.kind != ToolKind.Builtin) continue;
                    builtinShown++;
                    bool v = GUILayout.Toggle(d.isEnabled, " " + d.name + "　" + Snippet(d.description, 32));
                    if (v != d.isEnabled) reg.SetToolEnabled(d.name, v);
                }
            }
            if (builtinShown == 0) GUILayout.Label("　（没有内置工具）");

            GUILayout.Space(10);

            // ---- 外部工具 / MCP ----
            GUILayout.Label("<b>③ 外部工具 / MCP</b>（默认全关，勾上才给模型用）");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("打开工具配置文件夹", GUILayout.Height(30)))
                OpenFolder(System.IO.Path.GetDirectoryName(reg.SettingsPath));
            if (GUILayout.Button("重新加载工具", GUILayout.Height(30)))
                reg.ReloadNow();
            GUILayout.EndHorizontal();

            int extShown = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if (d == null || d.kind == ToolKind.Builtin) continue;
                    extShown++;
                    bool v = GUILayout.Toggle(d.isEnabled, " [" + d.kind + "] " + d.name + "　" + Snippet(d.description, 26));
                    if (v != d.isEnabled) reg.SetToolEnabled(d.name, v);
                }
            }
            if (extShown == 0)
                GUILayout.Label("　（没有加载到外部工具：先把上面总闸打开，或检查 pet_tools.json）");

            GUILayout.Space(12);

            // ---- MCP 引导 ----
            GUILayout.Label("<b>④ 想加新的 MCP 能力？</b>（文件系统 / Blender 建模…）");
            GUILayout.Label("　在 pet_tools.json 的 tools 数组里加一条配置，");
            GUILayout.Label("　保存后点上面的【重新加载工具】即可生效。");
            GUILayout.Label("　字段说明见 EXE 同级的 TOOLS_README.md。");
        }

        static string Snippet(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        void OpenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    status = "找不到目录：" + dir;
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                status = "已打开：" + dir;
            }
            catch (System.Exception e) { status = "打开文件夹失败：" + e.Message; }
        }

        /// <summary>二次确认弹窗。半透明遮罩 + 居中对话框，确定/取消。</summary>
        void DrawConfirmReset()
        {
            // 遮罩：压暗背景。真正阻止误点的不是它，而是上面 GUI.enabled=false 把主面板控件全禁了。
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float w = Mathf.Min(480f, Screen.width - 40f);
            float h = 230f;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GUILayout.BeginArea(r, GUI.skin.box);
            string who = PetProfileConfig.PetName;
            GUILayout.Label("<b>确定要清空「" + who + "」的所有记忆吗？</b>");
            GUILayout.Space(6);

            var mem = PetMemory.Instance;
            GUILayout.Label("将要清空：" + (mem != null ? mem.ScaleReadable() : "（读不到 PetMemory）"));
            GUILayout.Space(6);
            GUILayout.Label("清空后它会忘记之前说过的所有话，回到刚认识的初始状态。");
            GUILayout.Label("此操作不可撤销。");
            GUILayout.Space(4);
            GUILayout.Label("（接口配置 config.json、形态参数 pet_visual.json 都不会被动）");
            GUILayout.Space(12);

            GUILayout.BeginHorizontal();
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.86f, 0.26f, 0.26f);
            if (GUILayout.Button("确定清空", GUILayout.Height(38)))
            {
                confirmReset = false;
                if (mem == null) { status = "找不到 PetMemory，未执行"; }
                else
                {
                    mem.ResetAll();
                    status = "记忆已清空，" + who + " 回到了初始状态";
                    testResult = "";
                }
            }
            GUI.backgroundColor = oldBg;
            if (GUILayout.Button("取消", GUILayout.Height(38)))
                confirmReset = false;
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        /// <summary>
        /// 按当前输入推算真正会请求的地址。
        /// ⚠️ 这里的规则必须和 PetAPI.Endpoint() 保持一致，否则用户会看到"预览是对的、请求是错的"。
        /// </summary>
        string PreviewEndpoint(PetApiConfig c)
        {
            string u = (c.baseUrl ?? "").Trim().TrimEnd('/');
            if (u.Length == 0) u = "http://localhost:11434";

            if (c.provider == PetApiProvider.Ollama)
                return u.EndsWith("/api/chat") ? u : u + "/api/chat";

            if (u.EndsWith("/chat/completions")) return u;
            if (u.EndsWith("/v1")) return u + "/chat/completions";

            // 只给了裸域名（协议头之后没有路径）→ 补 /v1
            int scheme = u.IndexOf("://");
            if (scheme >= 0 && u.IndexOf('/', scheme + 3) < 0) return u + "/v1/chat/completions";
            return u + "/chat/completions";
        }

        /// <summary>
        /// 测试连接：发一个最小请求，把 HTTP 状态码和响应片段直接显示出来。
        /// 用独立的一次请求（不写记忆、不动小白状态），这样出错时能看到原始报错。
        /// </summary>
        IEnumerator TestRoutine(PetApiConfig c)
        {
            var api = PetAPI.Instance;
            if (api == null) { testResult = "❌ PetAPI 未就绪"; testing = false; yield break; }

            // 先把当前输入套上去，测的就是用户刚填的东西
            c.ApplyToApi();

            string url = api.Endpoint();
            string model = (c.model ?? "").Trim();
            if (model.Length == 0) model = "test";

            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(model)).Append("\",\"stream\":false,");
            if (api.provider == PetApiProvider.Ollama)
                sb.Append("\"think\":false,");
            sb.Append("\"messages\":[{\"role\":\"user\",\"content\":\"回复两个字：在的\"}]}");

            using (var www = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(c.apiKey))
                    www.SetRequestHeader("Authorization", "Bearer " + c.apiKey.Trim());
                www.timeout = 30;
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    string b = www.downloadHandler != null ? www.downloadHandler.text : "";
                    if (b != null && b.Length > 260) b = b.Substring(0, 260);
                    testResult = "❌ 失败 " + www.responseCode + "　" + www.error + "\n" + b;
                }
                else
                {
                    string b = www.downloadHandler.text ?? "";
                    if (b.Length > 200) b = b.Substring(0, 200);
                    testResult = "✅ 通了！返回片段：" + b;
                }
            }
            testing = false;
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
