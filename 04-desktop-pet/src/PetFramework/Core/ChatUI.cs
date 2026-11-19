using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace PetFramework
{
    /// <summary>
    /// 小白聊天 UI（呼出式）：
    /// 平时完全隐藏，按【回车】或【T】在小白头顶弹出；输入时冻结小白移动（保持 Idle/Talk）；
    /// 回车发送 → PetAPI.Ask → 小白回话播 Talk 动画 + TTS；Esc 收起。不遮挡日常办公。
    /// </summary>
    public class ChatUI : MonoBehaviour
    {
        public PetBrain brain;
        public KeyCode toggleKey = KeyCode.Return;
        public Vector3 headOffset = new Vector3(0f, 1.25f, 0f);   // 小白头顶
        public float panelWidth = 420f;

        Font cnFont;
        Canvas canvas;
        RectTransform root;
        InputField input;
        Text replyText;
        Text placeholderText;
        bool visible;

        // ⚠️ 名字【绝不写死】：用户可以在 Esc 面板里给桌宠和自己起任何名字。
        //    所有 UI 文案每次显示时现取（RefreshNames），改完名字立刻生效。
        static string PetName_ { get { return PetProfileConfig.PetName; } }
        static string UserName_ { get { return PetProfileConfig.UserName; } }

        void Awake()
        {
            EnsureEventSystem();
            cnFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
            if (cnFont == null) cnFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
            if (brain == null) brain = FindObjectOfType<PetBrain>();
            RefreshNames();
            SetVisible(false);
        }

        /// <summary>
        /// 把「名字」相关的文案全部重刷一遍。
        /// ⚠️ 必须在每次 Show() 时再刷一次 —— 用户可能刚在 Esc 面板里改完名字。
        /// </summary>
        void RefreshNames()
        {
            if (placeholderText != null)
                placeholderText.text = "和" + PetName_ + "说点什么…";
        }

        /// <summary>聊天框是不是开着（别的面板要判断，避免抢 Esc）。</summary>
        public bool IsVisible { get { return visible; } }

        void Update()
        {
            // 配置面板开着时别抢按键 —— 用户正在里面打字，按到 T 就弹出聊天框会很烦
            if (PetConfigUI.IsOpen) { if (visible) Hide(); return; }

            // ★ 工作区（F8）开着时也不抢 T：那是工作区的输入框，弹小白的聊天框只会添乱
            var ws = PetFramework.Workspace.WorkspaceUI.Instance;
            if (ws != null && ws.IsOpen) { if (visible) Hide(); return; }

            if (!visible && (Input.GetKeyDown(toggleKey) || Input.GetKeyDown(KeyCode.T))) Show();
            else if (visible && Input.GetKeyDown(KeyCode.Escape)) Hide();
            if (visible) FollowPet();
        }

        void FollowPet()
        {
            var cam = Camera.main;
            if (brain == null || cam == null || root == null) return;
            Vector3 sp = cam.WorldToScreenPoint(brain.transform.position + headOffset);
            sp.x = Mathf.Clamp(sp.x, panelWidth * 0.5f + 10f, Screen.width - panelWidth * 0.5f - 10f);
            sp.y = Mathf.Clamp(sp.y, 90f, Screen.height - 60f);
            root.position = sp;
        }

        void Show() { RefreshNames(); SetVisible(true); StartCoroutine(FocusSoon()); }
        void Hide() { SetVisible(false); }

        // ---- 主动通知（不是回复，是应用层要跟用户说的话，比如 TTS 服务挂了）----
        Coroutine noticeTimer;

        /// <summary>
        /// 让小白的聊天气泡主动显示一句话。
        /// ⚠️ 刻意【不设 freezeMove】—— 这是通知，不该把宠物冻在原地。
        /// </summary>
        public void ShowNotice(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            visible = true;
            if (root != null) root.gameObject.SetActive(true);
            if (replyText != null) replyText.text = text;

            if (noticeTimer != null) StopCoroutine(noticeTimer);
            noticeTimer = StartCoroutine(HideNoticeLater(10f));
            Debug.Log("[ChatUI] 通知：" + text);
        }

        /// <summary>10 秒后自动收起（如果主人没在打字）。</summary>
        IEnumerator HideNoticeLater(float sec)
        {
            yield return new WaitForSeconds(sec);
            noticeTimer = null;
            if (visible && input != null && string.IsNullOrEmpty(input.text)) Hide();
        }

        void SetVisible(bool v)
        {
            visible = v;
            if (root != null) root.gameObject.SetActive(v);
            if (brain != null) brain.freezeMove = v;   // 聊天时冻结移动：保持 Idle / 播放 Talk
            // 不显示任何默认提示文字（气泡平时留空）
        }

        IEnumerator FocusSoon()
        {
            yield return null;
            if (input != null) { input.ActivateInputField(); input.Select(); }
        }

        void Send()
        {
            if (input == null) return;
            string txt = input.text;
            if (string.IsNullOrWhiteSpace(txt)) return;
            input.text = "";
            input.ActivateInputField();

            // 兜底喂食：/feed <文件或文件夹路径>  —— 不依赖任何 Win32 拖放钩子，绝对可用
            if (txt.StartsWith("/feed"))
            {
                string p = (txt.Length > 5 ? txt.Substring(5) : "").Trim();
                p = p.Trim('"').Trim('“', '”').Trim();
                if (p.Length == 0)
                {
                    // 不带参数 → 读剪贴板（在资源管理器里 Ctrl+C 复制文件/文件夹后直接用）
    #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                    var files = PetFeed.ClipboardFiles();
                    if (files.Count == 0)
                    {
                        if (replyText != null) replyText.text = "剪贴板里没有文件。用法：/feed <路径>，或先在资源管理器 Ctrl+C 复制文件再打 /feed";
                        return;
                    }
                    var f0 = FindObjectOfType<PetFeed>();
                    if (replyText != null) replyText.text = "喂食(剪贴板)：" + string.Join("、", files.ToArray());
                    for (int i = 0; i < files.Count; i++)
                    {
                        Debug.Log("[ChatUI] /feed 剪贴板 → " + files[i]);
                        if (f0 != null) f0.Feed(files[i]);
                    }
                    return;
    #else
                    if (replyText != null) replyText.text = "用法：/feed <路径>";
                    return;
    #endif
                }
                bool ok = System.IO.File.Exists(p) || System.IO.Directory.Exists(p);
                Debug.Log("[ChatUI] /feed → 「" + p + "」 存在=" + ok);
                if (replyText != null) replyText.text = (ok ? "喂食：" : "路径不存在：") + p;
                var feed = FindObjectOfType<PetFeed>();
                if (feed != null) feed.Feed(p);
                else
                {
                    Debug.LogWarning("[ChatUI] 场景没有 PetFeed");
                    if (replyText != null) replyText.text = "（场景里找不到 PetFeed）";
                }
                return;
            }

            // 查看 / 清空「行为约定」（小白被主人训练出来的长期规矩）
            if (txt.StartsWith("/rules"))
            {
                if (replyText != null)
                    replyText.text = PetMemory.Instance != null
                        ? "【" + PetName_ + "的行为约定】" + PetMemory.Instance.RulesReadable()
                        : "（PetMemory 未就绪）";
                return;
            }
            if (txt.StartsWith("/forget"))
            {
                if (PetMemory.Instance != null) PetMemory.Instance.ClearRules();
                if (replyText != null) replyText.text = "已清空所有行为约定。";
                return;
            }

            if (replyText != null) replyText.text = UserName_ + "：" + txt;
            if (PetAPI.Instance == null) { Debug.LogWarning("[ChatUI] PetAPI 未就绪"); return; }

            // ★ 发出去之后就把小白放开：不然它被 freezeMove 冻着，
            //   就算调了 move_to / 兜底漫游也只能等面板关掉才动 —— 你会以为它没反应。
            if (brain != null) brain.freezeMove = false;

            // ★ 流式：边收边显示（模型吐字就开始写），第一个字出现得早很多
            PetAPI.Instance.onStreamDelta = s =>
            {
                // 桌面气泡也跟着渲染 Markdown（**粗体**、`代码`、标题…），不然模型一排版就满屏星号
                if (replyText != null) replyText.text = PetName_ + "：" + PetFramework.MarkdownText.ToRich(s);
            };
            PetAPI.Instance.Ask(txt, OnReply);
        }

        void OnReply(PetReply r)
        {
            if (PetAPI.Instance != null) PetAPI.Instance.onStreamDelta = null;   // 收尾，别影响下一轮
            if (r != null && !string.IsNullOrEmpty(r.text))
                r.text = PetFramework.TextTools.StripToolSyntax(r.text);   // 不留工具调用痕迹
            // ① 空回复才算失败（这是本地 Ollama，跟余额无关）
            if (r == null)
            {
                if (replyText != null) replyText.text = "（" + PetName_ + "没收到回复…Ollama 在跑吗？）";
                if (brain != null) brain.PlayAction("Idle");
                Debug.LogWarning("[ChatUI] 回复为空（请求失败或超时）");
                return;
            }

            if (!string.IsNullOrEmpty(r.text) && replyText != null)
                replyText.text = PetName_ + "：" + PetFramework.MarkdownText.ToRich(r.text);

            // ② 本轮是【工具】完成的：动作和 TTS 在工具里都做过一遍了，
            //    这里只负责把话显示出来，绝不能重复朗读、也不能报"没收到回复"。
            if (r.viaTool)
            {
                Debug.Log("[ChatUI] 本轮由工具回应（已朗读）：" + r.text);
                return;
            }

            // ②.5 只做了动作、没说话（模型只吐了 look_back / walk 这种）——
            //     正常执行动作，别给用户显示「没收到回复」。
            if (string.IsNullOrEmpty(r.text) && !string.IsNullOrEmpty(r.action) &&
                !string.Equals(r.action, "Idle", System.StringComparison.OrdinalIgnoreCase))
            {
                if (brain != null) brain.PlayAction(r.action);
                Debug.Log("[ChatUI] 只做动作（" + r.action + "），不说话");
                return;
            }

            // ③ 普通文字回复
            if (string.IsNullOrEmpty(r.text))
            {
                if (replyText != null) replyText.text = "（" + PetName_ + "没收到回复…Ollama 在跑吗？）";
                if (brain != null) brain.PlayAction("Idle");
                Debug.LogWarning("[ChatUI] 未收到有效回复");
                return;
            }
            // ★ 只是"说话"的话，别把坐/睡的姿势顶掉；TTS 那边也不许再改动画
            if (brain != null)
            {
                if (string.Equals(r.action, "talk", System.StringComparison.OrdinalIgnoreCase))
                    brain.PlayTalkIfAllowed();
                else
                    brain.PlayAction(r.action);
            }
            TTS.Speak(r.text, false);
            Debug.Log("[ChatUI] " + PetName_ + "(" + r.action + ")" + r.text);
        }

        void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }
        }

        void BuildUI()
        {
            var canvasGO = new GameObject("PetChatCanvas", typeof(Canvas), typeof(GraphicRaycaster));
            DontDestroyOnLoad(canvasGO);
            canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;                 // 高层，盖在其他之上

            var rootGO = new GameObject("ChatRoot", typeof(RectTransform));
            rootGO.transform.SetParent(canvasGO.transform, false);
            root = rootGO.GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(panelWidth, 100f);
            root.pivot = new Vector2(0.5f, 0f);        // 底边中心贴小白头顶

            // 回话气泡（无边框深色圆角感）
            var bubble = new GameObject("Bubble", typeof(Image));
            bubble.transform.SetParent(root, false);
            var br = bubble.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0f, 0f); br.anchorMax = new Vector2(1f, 0f);
            br.pivot = new Vector2(0.5f, 0f);
            br.sizeDelta = new Vector2(0f, 44f);
            br.anchoredPosition = new Vector2(0f, 52f);
            bubble.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.16f, 0.88f);

            var rt = new GameObject("ReplyText", typeof(Text));
            rt.transform.SetParent(bubble.transform, false);
            var rtr = rt.GetComponent<RectTransform>();
            rtr.anchorMin = Vector2.zero; rtr.anchorMax = Vector2.one;
            rtr.offsetMin = new Vector2(14f, 4f); rtr.offsetMax = new Vector2(-14f, -4f);
            replyText = rt.GetComponent<Text>();
            replyText.font = cnFont; replyText.color = new Color(0.95f, 0.97f, 1f);
            replyText.fontSize = 18; replyText.alignment = TextAnchor.MiddleCenter;
            replyText.horizontalOverflow = HorizontalWrapMode.Wrap; replyText.verticalOverflow = VerticalWrapMode.Overflow;

            // 输入条
            var bar = new GameObject("InputBar", typeof(Image), typeof(InputField));
            bar.transform.SetParent(root, false);
            var barR = bar.GetComponent<RectTransform>();
            barR.anchorMin = new Vector2(0f, 0f); barR.anchorMax = new Vector2(1f, 0f);
            barR.pivot = new Vector2(0.5f, 0f);
            barR.sizeDelta = new Vector2(0f, 46f);
            barR.anchoredPosition = Vector2.zero;
            var barImg = bar.GetComponent<Image>();
            barImg.color = new Color(1f, 1f, 1f, 0.97f);
            input = bar.GetComponent<InputField>();
            input.targetGraphic = barImg;

            var t = new GameObject("Text", typeof(Text));
            t.transform.SetParent(bar.transform, false);
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(14f, 4f); tr.offsetMax = new Vector2(-14f, -4f);
            var tt = t.GetComponent<Text>();
            tt.font = cnFont; tt.color = new Color(0.1f, 0.1f, 0.12f); tt.fontSize = 18;
            tt.supportRichText = false; tt.alignment = TextAnchor.MiddleLeft;

            var ph = new GameObject("Placeholder", typeof(Text));
            ph.transform.SetParent(bar.transform, false);
            var phr = ph.GetComponent<RectTransform>();
            phr.anchorMin = Vector2.zero; phr.anchorMax = Vector2.one;
            phr.offsetMin = new Vector2(14f, 4f); phr.offsetMax = new Vector2(-14f, -4f);
            var pt = ph.GetComponent<Text>();
            pt.font = cnFont; pt.color = new Color(0f, 0f, 0f, 0.38f); pt.fontSize = 18;
            pt.alignment = TextAnchor.MiddleLeft;
            pt.text = "和" + PetName_ + "说点什么…";
            placeholderText = pt;      // ★ 存下来，改名字时 RefreshNames() 会重刷

            input.textComponent = tt;
            input.placeholder = pt;
            input.lineType = InputField.LineType.SingleLine;
            input.onSubmit.AddListener(_ => Send());
        }
    }
}
