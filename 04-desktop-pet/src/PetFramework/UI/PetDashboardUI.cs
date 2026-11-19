using System;
using System.Collections;
using System.Collections.Generic;
using PetFramework.Tools;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PetFramework
{
    /// <summary>
    /// 【主面板 · uGUI 版（代码生成）】
    ///
    /// 三页：聊天历史 / 设置 / 工具与 MCP。
    /// 整个 Canvas + 布局都是运行时生成的 —— 你**不需要在 Unity 里摆任何控件**。
    /// 如果以后想改成手动拖 Prefab：把下面的 public 引用拖进来，BuildWindow() 会跳过生成。
    ///
    /// 布局：
    ///   Canvas(ScreenSpaceOverlay, 500)
    ///   └ WindowsRoot 960×620（拖标题栏移动）
    ///     ├ TitleBar   [标题]              [—] [×]
    ///     ├ Nav 190px  [聊天] [设置] [工具]
    ///     └ Content
    ///         ├ PageChat     ScrollRect + 输入框 + 发送
    ///         ├ PageSettings API / 角色 / 桌宠开关
    ///         └ PageTools    内置/外部工具 Toggle + 总闸 + 重载
    ///
    /// ⚠️ 桌宠和面板共用同一个全屏透明窗口：面板关闭 = 隐藏，桌宠继续跑。
    /// </summary>
    public class PetDashboardUI : MonoBehaviour
    {
        [Header("可选：手动摆好 UI 就拖进来；留空自动生成")]
        public Canvas canvas;
        public RectTransform windowsRoot;
        public RectTransform contentArea;
        public ScrollRect chatScroll;
        public RectTransform chatContent;
        public InputField chatInput;
        public Toggle keepPetToggle;

        public bool IsTyping
        {
            get
            {
                if (chatInput != null && chatInput.isFocused) return true;
                return EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                       && EventSystem.current.currentSelectedGameObject.GetComponent<InputField>() != null;
            }
        }

        public bool IsOpen { get { return windowsRoot != null && windowsRoot.gameObject.activeSelf; } }

        // ---------------- 配色 ----------------
        static readonly Color ColBg      = new Color(0.078f, 0.098f, 0.125f, 0.98f);
        static readonly Color ColPanel   = new Color(0.106f, 0.129f, 0.161f, 1f);
        static readonly Color ColNav     = new Color(0.086f, 0.106f, 0.133f, 1f);
        static readonly Color ColAccent  = new Color(0.353f, 0.690f, 1f, 1f);
        static readonly Color ColText    = new Color(0.843f, 0.878f, 0.918f, 1f);
        static readonly Color ColDim     = new Color(0.545f, 0.596f, 0.647f, 1f);
        static readonly Color ColProact  = new Color(0.62f, 0.64f, 0.68f, 1f);

        Font font;
        RectTransform navRoot;
        readonly List<RectTransform> pages = new List<RectTransform>();
        readonly List<Button> navButtons = new List<Button>();
        int pageIndex;
        bool built;

        // =====================================================================
        //  生命周期
        // =====================================================================

        void Awake()
        {
            if (windowsRoot == null) BuildIfNeeded();
        }

        public void BuildIfNeeded()
        {
            if (built) return;
            built = true;

            font = Font.CreateDynamicFontFromOSFont(
                new string[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial" }, 16);
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            EnsureEventSystem();
            BuildCanvas();
            BuildWindow();
            BuildNav();

            pages.Add(BuildChatPage());        // 0 聊天历史
            pages.Add(BuildSettingsPage());    // 1 基础与角色
            pages.Add(BuildVisualPage());      // 2 材质与渲染（数据驱动）
            pages.Add(BuildParticlePage());    // 3 粒子特效
            pages.Add(BuildToolsPage());       // 4 工具与 MCP

            ShowPage(0);
        }

        public void Show()
        {
            BuildIfNeeded();
            windowsRoot.gameObject.SetActive(true);
            RefreshChat();
            if (pageIndex == 2) RebuildToolsPage();
            if (pageIndex == 1) RefreshSettings();
            windowsRoot.SetAsLastSibling();
        }

        public void Hide()
        {
            if (windowsRoot != null) windowsRoot.gameObject.SetActive(false);
        }

        /// <summary>用屏幕坐标判断点是否落在面板上（给 WM_NCHITTEST 用）。</summary>
        public bool ContainsScreenPoint(Vector2 screenPoint)
        {
            if (windowsRoot == null || !windowsRoot.gameObject.activeInHierarchy) return false;
            var corners = new Vector3[4];
            windowsRoot.GetWorldCorners(corners);
            var r = new Rect(corners[0].x, corners[0].y,
                             corners[2].x - corners[0].x, corners[2].y - corners[0].y);
            return r.Contains(screenPoint);
        }

        /// <summary>
        /// 【DPI 无关】用归一化客户区坐标判断（0..1，左上原点）。
        /// 高 DPI 下 OS 给的物理像素、Unity 的 Screen 逻辑像素、画布世界坐标三者可能不一致；
        /// 但「面板占画布的百分比」和「光标占客户区的百分比」是尺度无关的，永远对得上。
        /// </summary>
        public bool ContainsNormalizedPoint(float nx, float ny)
        {
            if (windowsRoot == null || !windowsRoot.gameObject.activeInHierarchy) return false;
            if (canvas == null) return false;

            var cw = new Vector3[4];
            canvas.GetComponent<RectTransform>().GetWorldCorners(cw);
            float cwW = cw[2].x - cw[0].x;
            float cwH = cw[2].y - cw[0].y;
            if (cwW <= 0.0001f || cwH <= 0.0001f) return false;

            var wc = new Vector3[4];
            windowsRoot.GetWorldCorners(wc);

            float x0 = (wc[0].x - cw[0].x) / cwW;
            float x1 = (wc[2].x - cw[0].x) / cwW;
            float y0 = (wc[0].y - cw[0].y) / cwH;   // 下边界
            float y1 = (wc[2].y - cw[0].y) / cwH;   // 上边界

            return nx >= x0 && nx <= x1 && ny >= y0 && ny <= y1;
        }

        // =====================================================================
        //  构建
        // =====================================================================

        void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(go);
        }

        void BuildCanvas()
        {
            if (canvas != null) return;

            var go = new GameObject("PetDashboardCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(go);

            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        void BuildWindow()
        {
            if (windowsRoot != null) return;

            windowsRoot = MakeRect("WindowsRoot", canvas.transform);
            windowsRoot.anchorMin = windowsRoot.anchorMax = new Vector2(0.5f, 0.5f);
            windowsRoot.pivot = new Vector2(0.5f, 0.5f);
            windowsRoot.sizeDelta = new Vector2(960, 620);
            windowsRoot.anchoredPosition = Vector2.zero;
            var bg = windowsRoot.gameObject.AddComponent<Image>();
            bg.color = ColBg;

            // ---- 标题栏 ----
            var title = MakeRect("TitleBar", windowsRoot);
            title.anchorMin = new Vector2(0, 1); title.anchorMax = new Vector2(1, 1);
            title.pivot = new Vector2(0.5f, 1); title.sizeDelta = new Vector2(0, 42);
            title.anchoredPosition = Vector2.zero;
            title.gameObject.AddComponent<Image>().color = ColNav;

            var titleText = MakeText("Title", title, "", 16, ColText, TextAnchor.MiddleLeft);
            titleText.rectTransform.anchorMin = new Vector2(0, 0);
            titleText.rectTransform.anchorMax = new Vector2(1, 1);
            titleText.rectTransform.offsetMin = new Vector2(16, 0);
            titleText.rectTransform.offsetMax = new Vector2(-90, 0);
            titleText.text = "Pet Dashboard";

            var drag = title.gameObject.AddComponent<DragHandle>();
            drag.target = windowsRoot;

            MakeButton("BtnMin", title, "—", () => PetWindowManager.Instance.MinimizeDashboard(),
                       new Vector2(-62, 0), new Vector2(38, 28));
            MakeButton("BtnClose", title, "×", () => PetWindowManager.Instance.CloseDashboard(),
                       new Vector2(-18, 0), new Vector2(38, 28));

            // ---- 左侧导航 ----
            navRoot = MakeRect("Nav", windowsRoot);
            navRoot.anchorMin = new Vector2(0, 0); navRoot.anchorMax = new Vector2(0, 1);
            navRoot.pivot = new Vector2(0, 0.5f);
            navRoot.sizeDelta = new Vector2(190, -42);
            navRoot.anchoredPosition = new Vector2(0, -21);
            navRoot.gameObject.AddComponent<Image>().color = ColNav;

            // ---- 右侧内容区 ----
            contentArea = MakeRect("Content", windowsRoot);
            contentArea.anchorMin = new Vector2(0, 0); contentArea.anchorMax = new Vector2(1, 1);
            contentArea.offsetMin = new Vector2(190, 0);
            contentArea.offsetMax = new Vector2(0, -42);
        }

        void BuildNav()
        {
            string[] names = { "聊天历史", "基础与角色", "材质与渲染", "粒子特效", "工具与 MCP" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = MakeButton("Nav" + i, navRoot, names[i], () => ShowPage(idx),
                                   new Vector2(0, -16 - i * 48), new Vector2(160, 40));
                var rt = b.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(15, -16 - i * 48);
                navButtons.Add(b);
            }

            // 角落提示：不然很多人不知道 Tab 能呼出
            var hint = MakeText("Hint", navRoot, "Tab / Esc 呼出或隐藏", 12, ColDim, TextAnchor.LowerLeft);
            hint.rectTransform.anchorMin = new Vector2(0, 0);
            hint.rectTransform.anchorMax = new Vector2(1, 0);
            hint.rectTransform.pivot = new Vector2(0, 0);
            hint.rectTransform.offsetMin = new Vector2(15, 14);
            hint.rectTransform.offsetMax = new Vector2(-15, 32);
        }

        void ShowPage(int idx)
        {
            pageIndex = idx;
            for (int i = 0; i < pages.Count; i++)
                if (pages[i] != null) pages[i].gameObject.SetActive(i == idx);
            for (int i = 0; i < navButtons.Count; i++)
            {
                var img = navButtons[i].GetComponent<Image>();
                if (img != null) img.color = (i == idx) ? ColAccent * 0.55f : ColPanel;
            }
            if (idx == 1) RefreshSettings();
            if (idx == 2) RebuildVisualPage();
            if (idx == 3) RefreshParticles();
            if (idx == 4) RebuildToolsPage();
        }

        // =====================================================================
        //  页面 1：聊天历史
        // =====================================================================

        RectTransform BuildChatPage()
        {
            var page = MakePage("PageChat");

            // 滚动区
            var scrollGo = MakeRect("ChatScroll", page);
            scrollGo.anchorMin = new Vector2(0, 0);
            scrollGo.anchorMax = new Vector2(1, 1);
            scrollGo.offsetMin = new Vector2(16, 64);
            scrollGo.offsetMax = new Vector2(-16, -12);

            var sr = scrollGo.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;

            var viewport = MakeRect("Viewport", scrollGo);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = viewport.offsetMax = Vector2.zero;
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.18f);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;

            chatContent = MakeRect("Content", viewport);
            chatContent.anchorMin = new Vector2(0, 1);
            chatContent.anchorMax = new Vector2(1, 1);
            chatContent.pivot = new Vector2(0.5f, 1);
            chatContent.anchoredPosition = Vector2.zero;
            chatContent.sizeDelta = new Vector2(0, 0);

            var vlg = chatContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8;
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;

            var fitter = chatContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.viewport = viewport;
            sr.content = chatContent;

            // 输入行
            var inputRow = MakeRect("InputRow", page);
            inputRow.anchorMin = new Vector2(0, 0);
            inputRow.anchorMax = new Vector2(1, 0);
            inputRow.pivot = new Vector2(0.5f, 0);
            inputRow.offsetMin = new Vector2(16, 16);
            inputRow.offsetMax = new Vector2(-16, 56);

            chatInput = MakeInputField("ChatInput", inputRow, "和小白说点什么…");
            var irt = chatInput.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0); irt.anchorMax = new Vector2(1, 1);
            irt.offsetMin = new Vector2(0, 0); irt.offsetMax = new Vector2(-196, 0);

            var send = MakeButton("Send", inputRow, "发送", SendChat, Vector2.zero, new Vector2(88, 40));
            var srt = send.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(1, 0); srt.anchorMax = new Vector2(1, 1);
            srt.pivot = new Vector2(1, 0.5f);
            srt.offsetMin = new Vector2(-88, 0); srt.offsetMax = Vector2.zero;

            // 停止按钮：请求进行中才出现
            var stop = MakeButton("StopChat", inputRow, "■ 停止", StopChat, Vector2.zero, new Vector2(88, 40));
            var stRt = stop.GetComponent<RectTransform>();
            stRt.anchorMin = new Vector2(1, 0); stRt.anchorMax = new Vector2(1, 1);
            stRt.pivot = new Vector2(1, 0.5f);
            stRt.offsetMin = new Vector2(-184, 0); stRt.offsetMax = new Vector2(-96, 0);
            chatStopBtn = stop;
            stop.gameObject.SetActive(false);

            return page;
        }

        // 流式回复的临时行（聊天页里实时显示"小白：……"）
        GameObject liveRow;
        Text liveText;
        string livePrefix;
        Button chatStopBtn;
        Coroutine chatWatchdog;

        void StopChat()
        {
            if (PetAPI.Instance != null) PetAPI.Instance.CancelAsk();
            EndLiveRow();
            if (chatStopBtn != null) chatStopBtn.gameObject.SetActive(false);
            AddRow("系统", "（已停止这一轮回复。）", false);
        }

        void StartLiveRow()
        {
            if (chatContent == null) return;
            liveRow = MakeRect("MsgLive", chatContent).gameObject;
            liveText = liveRow.AddComponent<Text>();
            liveText.font = font;
            liveText.fontSize = 15;
            liveText.supportRichText = true;
            liveText.horizontalOverflow = HorizontalWrapMode.Wrap;
            liveText.verticalOverflow = VerticalWrapMode.Overflow;
            liveText.color = ColText;
            livePrefix = "<color=#9AD0FF>" + PetProfileConfig.PetName + "：</color> ";
            liveText.text = livePrefix + "<color=#8C939E>▍</color>";
            Canvas.ForceUpdateCanvases();
            if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
        }

        void EndLiveRow()
        {
            if (liveRow != null) Destroy(liveRow);
            liveRow = null; liveText = null;
        }

        /// <summary>320 秒还没收到任何回调 → 说明请求/回调链断了，给个明确交代并收尾。</summary>
        IEnumerator ChatWatchdog()
        {
            float t = 0f;
            while (t < 320f && liveRow != null)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (liveRow == null) yield break;      // 正常收到回复了
            EndLiveRow();
            if (chatStopBtn != null) chatStopBtn.gameObject.SetActive(false);
            if (PetAPI.Instance != null) PetAPI.Instance.CancelAsk();
            AddRow("系统", "（等太久了：这一轮没有收到任何回应（可能是本地模型卡住/端点异常）。"
                         + "已经自动收尾，可以再发一次，或按停止后重试。）", false);
            Debug.LogWarning("[PetDashboard] 聊天看门狗触发：320 秒无回调");
        }

        void SendChat()
        {
            if (chatInput == null) return;
            string text = chatInput.text;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            chatInput.text = "";
            chatInput.ActivateInputField();

            if (PetAPI.Instance == null) return;

            // ★ 先把主人这句显示出来（等回复期间也有反馈），再挂流式回调
            EndLiveRow();
            AddRow(PetProfileConfig.UserName, text.Trim(), false);
            StartLiveRow();
            if (chatStopBtn != null) chatStopBtn.gameObject.SetActive(true);

            PetAPI.Instance.onStreamDelta = s =>
            {
                if (liveText != null)
                    liveText.text = livePrefix + PetFramework.MarkdownText.ToRich(s)
                                    + "<color=#8C939E>▍</color>";
                if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
            };

            // ★ 看门狗：万一回调因为异常/服务端怪响应没回来，不能让界面永远停在「小白：▍」。
            //   liveRow 被 EndLiveRow 销毁 ⇒ 循环自然退出；只有"卡住"时才会触发兜底。
            if (chatWatchdog != null) StopCoroutine(chatWatchdog);
            chatWatchdog = StartCoroutine(ChatWatchdog());

            PetAPI.Instance.Ask(text.Trim(), reply =>
            {
                if (PetAPI.Instance != null) PetAPI.Instance.onStreamDelta = null;
                if (chatStopBtn != null) chatStopBtn.gameObject.SetActive(false);
                EndLiveRow();
                if (reply != null && !string.IsNullOrEmpty(reply.text) && TTS.Instance != null)
                    TTS.Speak(reply.text, false);
                RefreshChat();
                // ★ 空回复也要给个明确交代，不能"发出去没反应"
                if (reply == null || string.IsNullOrEmpty(reply.text))
                    AddRow("系统", "（这一轮没有文字回复 —— 可能只做了动作，或者请求超时了。可以再试一次。）", false);
            });
        }

        /// <summary>从 PetMemory 重建整页聊天记录。</summary>
        public void RefreshChat()
        {
            if (chatContent == null) return;

            for (int i = chatContent.childCount - 1; i >= 0; i--)
                Destroy(chatContent.GetChild(i).gameObject);

            var mem = PetMemory.Instance;
            if (mem == null) { AddRow("系统", "记忆组件未就绪", false); return; }

            var hist = mem.GetHistory();
            if (hist == null || hist.Count == 0) { AddRow("系统", "还没有对话记录。按 T 或在这里输入。", false); return; }

            string petName = PetProfileConfig.PetName;
            for (int i = 0; i < hist.Count; i++)
            {
                string line = hist[i];
                int cut = line.IndexOf(": ");
                string role = cut > 0 ? line.Substring(0, cut) : "";
                string text = cut > 0 ? line.Substring(cut + 2) : line;

                // 主动搭话：role 里带「主动」
                bool proactive = role.Contains("主动");
                AddRow(role, text, proactive);
            }

            Canvas.ForceUpdateCanvases();
            if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
        }

        void AddRow(string role, string text, bool proactive)
        {
            // 旧存档里可能还留着 play_animation(...) 这类技术行 —— 显示前再洗一遍，洗空了就不显示
            text = PetFramework.TextTools.ForDisplay(text);
            if (string.IsNullOrEmpty(text)) return;
            // 主动搭话如果整句是英文（老存档里那种 "I'm bored and need to move."），不显示
            if (proactive && PetFramework.TextTools.IsEnglishOnly(text)) return;

            var go = MakeRect("Msg", chatContent).gameObject;
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = 15;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = proactive ? ColProact : ColText;

            string who = string.IsNullOrEmpty(role) ? "" : role;
            string prefix;
            // 主动搭话也按「正常一句话」显示：只留名字，不再写「[小白 凑了过来]」这种标签
            if (proactive) prefix = "<color=#8C939E>" + PetProfileConfig.PetName + "：</color> ";
            else if (who == PetProfileConfig.UserName) prefix = "<color=#5AB0FF>" + who + "：</color> ";
            else if (who == PetProfileConfig.PetName) prefix = "<color=#9AD0FF>" + who + "：</color> ";
            else prefix = "<color=#8C939E>" + who + "：</color> ";

            // 小白的回复走 Markdown；主人自己的话和系统提示保持原样
            bool aiLine = proactive || who == PetProfileConfig.PetName;
            t.text = prefix + (aiLine ? PetFramework.MarkdownText.ToRich(text) : text);
        }

        // =====================================================================
        //  页面 2：设置（API / 角色 / 桌宠开关）
        // =====================================================================

        RectTransform settingsPage;
        InputField setBaseUrl, setModel, setApiKey, setPetName, setUserName, setPersona;
        InputField setVoice, setRate, setPitch, setVolume;
        Slider sizeSlider;
        Text setStatus;
        Toggle keepPetVisibleToggle;
        Toggle thinkingToggle;

        RectTransform BuildSettingsPage()
        {
            var page = MakePage("PageSettings");
            settingsPage = page;
            float y = -16;

            // ---- 整页可滚动：参数多了也不会溢出窗口 ----
            var scrollGo = MakeRect("SettingsScroll", page);
            scrollGo.anchorMin = Vector2.zero; scrollGo.anchorMax = Vector2.one;
            scrollGo.offsetMin = Vector2.zero; scrollGo.offsetMax = Vector2.zero;

            var sr = scrollGo.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 40f;

            var viewport = MakeRect("Viewport", scrollGo);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero; viewport.offsetMax = new Vector2(-6, 0);
            // 近乎透明的 Image 只是为了让 ScrollRect 能收到拖拽/滚轮事件
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = MakeRect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 820);   // 比可视区高 ⇒ 可滚

            sr.viewport = viewport;
            sr.content = content;

            // ---- 细滚动条（自动隐藏）----
            var sbGo = MakeRect("Scrollbar", scrollGo);
            sbGo.anchorMin = new Vector2(1, 0); sbGo.anchorMax = new Vector2(1, 1);
            sbGo.pivot = new Vector2(1, 0.5f);
            sbGo.sizeDelta = new Vector2(5, 0);
            sbGo.anchoredPosition = Vector2.zero;
            var sbBg = sbGo.gameObject.AddComponent<Image>();
            sbBg.color = new Color(1f, 1f, 1f, 0.06f);
            var sb = sbGo.gameObject.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;

            var slideArea = MakeRect("Sliding Area", sbGo);
            slideArea.anchorMin = Vector2.zero; slideArea.anchorMax = Vector2.one;
            slideArea.offsetMin = Vector2.zero; slideArea.offsetMax = Vector2.zero;

            var handle = MakeRect("Handle", slideArea);
            // ⚠️⚠️ 这两行必须写：Scrollbar 只改 handle 的锚点（0..size），
            //      默认 RectTransform 的 sizeDelta 是 100×100，会让滑块横向鼓出 100px
            //      （之前那个"粗得离谱的蓝色大条"就是这么来的）。
            handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one;
            handle.sizeDelta = Vector2.zero;
            handle.anchoredPosition = Vector2.zero;
            var hImg = handle.gameObject.AddComponent<Image>();
            hImg.color = new Color(0.353f, 0.690f, 1f, 0.45f);
            sb.handleRect = handle;
            sb.targetGraphic = hImg;
            sb.size = 0.18f;

            sr.verticalScrollbar = sb;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            // 所有控件挂到 content（可滚动区）
            y -= 8;
            y = AddLabel(content, "桌宠大小（占屏幕高度 %）", y, 17, ColAccent);
            var __framing = FindObjectOfType<PetCameraFraming>();
            float __pct = __framing != null ? __framing.ScreenPercent : 13f;
            sizeSlider = MakeSlider(content, "大小", 4f, 30f, __pct, v =>
            {
                var f = FindObjectOfType<PetCameraFraming>();
                if (f != null) f.SetScreenPercent(v);
            }, y);
            y -= 40;

            y = AddLabel(content, "接口配置", y, 17, ColAccent);
            y = AddLabel(content, "BaseUrl（本地 http://localhost:11434 / 云端填到 /v1）", y, 14, ColDim);
            setBaseUrl = AddInput(content, ref y, "");
            y = AddLabel(content, "模型名 Model", y, 14, ColDim);
            setModel = AddInput(content, ref y, "");
            y = AddLabel(content, "API Key（本地留空）", y, 14, ColDim);
            setApiKey = AddInput(content, ref y, "");

            // ---- 模型思考开关（落盘在 config.json 的 enableThinking）----
            y -= 6;
            thinkingToggle = MakeToggle(content, "启用模型思考", y);
            if (PetApiConfig.Instance != null) thinkingToggle.isOn = PetApiConfig.Instance.enableThinking;
            thinkingToggle.onValueChanged.AddListener(v =>
            {
                if (PetApiConfig.Instance != null) PetApiConfig.Instance.enableThinking = v;
                if (setStatus != null) setStatus.text = v
                    ? "思考已开启 —— 点「保存设置」后生效（会明显变慢）"
                    : "思考已关闭 —— 点「保存设置」后生效";
            });
            y -= 30;
            y = AddLabel(content, "开 = 先思考再答，更聪明但很慢（本地 4B 单次可能 60 秒以上）；关 = 直接答", y, 12, ColDim);
            y -= 6;

            y -= 8;
            y = AddLabel(content, "角色设定", y, 17, ColAccent);
            y = AddLabel(content, "桌宠名字", y, 14, ColDim);
            setPetName = AddInput(content, ref y, "");
            y = AddLabel(content, "你的昵称（它会这样称呼你）", y, 14, ColDim);
            setUserName = AddInput(content, ref y, "");
            y = AddLabel(content, "人设 / 性格（拼进 System Prompt 最前面）", y, 14, ColDim);
            setPersona = AddInput(content, ref y, "", 74);

            y -= 8;
            y = AddLabel(content, "语音合成（Edge-TTS）", y, 17, ColAccent);
            y = AddLabel(content, "音色 Voice（如 zh-CN-YunxiNeural / zh-CN-XiaoxiaoNeural）", y, 14, ColDim);
            setVoice = AddInput(content, ref y, "");
            y = AddLabel(content, "语速 rate（Edge 格式，如 +8% / -5%）", y, 14, ColDim);
            setRate = AddInput(content, ref y, "");
            y = AddLabel(content, "音调 pitch（Edge 格式，如 +12Hz / -10Hz）", y, 14, ColDim);
            setPitch = AddInput(content, ref y, "");
            y = AddLabel(content, "音量 volume（Edge 格式，如 +0% / -20%）", y, 14, ColDim);
            setVolume = AddInput(content, ref y, "");

            y -= 4;
            var row = MakeRect("Row", content);
            row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(0, 1);
            row.pivot = new Vector2(0, 1); row.anchoredPosition = new Vector2(16, y);
            row.sizeDelta = new Vector2(400, 28);

            var b1 = MakeButton("Save", row, "保存设置", SaveSettings, new Vector2(0, 0), new Vector2(120, 34));
            var b1rt = b1.GetComponent<RectTransform>();
            b1rt.anchorMin = b1rt.anchorMax = new Vector2(0, 1);
            b1rt.pivot = new Vector2(0, 1);
            b1rt.anchoredPosition = Vector2.zero;

            var b2 = MakeButton("Reload", row, "从文件重读", ReloadSettings, new Vector2(130, 0), new Vector2(120, 34));
            var b2rt = b2.GetComponent<RectTransform>();
            b2rt.anchorMin = b2rt.anchorMax = new Vector2(0, 1);
            b2rt.pivot = new Vector2(0, 1);
            b2rt.anchoredPosition = new Vector2(130, 0);

            y -= 44;
            keepPetVisibleToggle = MakeToggle(content, "面板关闭时保留桌面桌宠", y);
            keepPetVisibleToggle.isOn = PetWindowManager.Instance == null || PetWindowManager.Instance.keepPetVisibleWhenClosed;
            keepPetVisibleToggle.onValueChanged.AddListener(v =>
            {
                if (PetWindowManager.Instance != null) PetWindowManager.Instance.SetKeepPetVisible(v);
            });

            y -= 12;

            setStatus = MakeText("Status", content, "", 14, ColDim, TextAnchor.UpperLeft);
            setStatus.rectTransform.anchorMin = new Vector2(0, 1);
            setStatus.rectTransform.anchorMax = new Vector2(1, 1);
            setStatus.rectTransform.pivot = new Vector2(0, 1);
            setStatus.rectTransform.offsetMin = new Vector2(16, 0);
            setStatus.rectTransform.offsetMax = new Vector2(-16, 0);
            setStatus.rectTransform.anchoredPosition = new Vector2(0, y);
            setStatus.rectTransform.sizeDelta = new Vector2(-32, 26);

            // 按实际内容撑高（加了 TTS 四个输入框 + 大小滑杆后，原来固定 760/820 已经不够，
            // 后面的行会被 ScrollRect 的遮罩裁掉 —— 表现就是「滑杆看不见」）
            content.sizeDelta = new Vector2(0, Mathf.Abs(y) + 60);

            return page;
        }

        // =====================================================================
        //  页面 3：材质与渲染 —— 【完全数据驱动】
        //
        //  这里一行 if (isFur) 都没有。工作方式：
        //    PetVisualRoot.ActiveConfig → parameters 列表 → 按 kind 生成控件
        //    Float/Int → 滑杆、GrayColor → 亮度滑杆、Color → RGB 三条、
        //    Vector → XYZ 三条、Toggle/KeywordToggle → 勾选框
        //  以后加 MetalConfig，这个页面【不用改一行代码】。
        // =====================================================================

        PetVisualRoot visualRoot;
        RectTransform visualListRoot;
        Text visualStatus;
        bool visualDirty;

        void EnsureVisualRoot()
        {
            if (visualRoot == null) visualRoot = FindObjectOfType<PetVisualRoot>();
        }

        RectTransform BuildVisualPage()
        {
            var page = MakePage("PageVisual");

            var head = MakeText("Head", page, "<b>材质与渲染</b>", 17, ColAccent, TextAnchor.UpperLeft);
            head.rectTransform.anchorMin = new Vector2(0, 1); head.rectTransform.anchorMax = new Vector2(1, 1);
            head.rectTransform.pivot = new Vector2(0, 1);
            head.rectTransform.offsetMin = new Vector2(16, 0); head.rectTransform.offsetMax = new Vector2(-16, 0);
            head.rectTransform.anchoredPosition = new Vector2(0, -10);
            head.rectTransform.sizeDelta = new Vector2(-32, 24);

            visualStatus = MakeText("Status", page, "", 12, ColDim, TextAnchor.UpperLeft);
            var vst = visualStatus.rectTransform;
            vst.anchorMin = new Vector2(0, 1); vst.anchorMax = new Vector2(1, 1);
            vst.pivot = new Vector2(0, 1);
            vst.offsetMin = new Vector2(16, 0); vst.offsetMax = new Vector2(-16, 0);
            vst.anchoredPosition = new Vector2(0, -36);
            vst.sizeDelta = new Vector2(-32, 18);

            var btnRow = MakeRect("BtnRow", page);
            btnRow.anchorMin = new Vector2(0, 1); btnRow.anchorMax = new Vector2(1, 1);
            btnRow.pivot = new Vector2(0, 1);
            btnRow.offsetMin = new Vector2(16, 0); btnRow.offsetMax = new Vector2(-16, 0);
            btnRow.anchoredPosition = new Vector2(0, -58);
            btnRow.sizeDelta = new Vector2(-32, 30);

            MakeLeftButton(btnRow, "保存到 JSON", 0, () =>
            {
                EnsureVisualRoot();
                if (visualRoot != null) { visualRoot.SaveJson(); RebuildVisualPage(); }
            });
            MakeLeftButton(btnRow, "重新读取", 118, () =>
            {
                EnsureVisualRoot();
                if (visualRoot != null) { visualRoot.Reload(); RebuildVisualPage(); }
            });
            MakeLeftButton(btnRow, "恢复默认值", 236, () =>
            {
                EnsureVisualRoot();
                var cfg = visualRoot != null ? visualRoot.ActiveConfig : null;
                if (cfg != null) { cfg.ResetAllValues(); visualRoot.Apply(); RebuildVisualPage(); }
            });

            ScrollRect sr;
            visualListRoot = MakeScrollList(page, 100f, 10f, out sr);
            return page;
        }

        void RebuildVisualPage()
        {
            if (visualListRoot == null) return;
            ClearList(visualListRoot);

            EnsureVisualRoot();
            var cfg = visualRoot != null ? visualRoot.ActiveConfig : null;
            if (cfg == null)
            {
                if (visualStatus != null) visualStatus.text = "没有找到形态配置";
                AddTextRow(visualListRoot, "场景里没有 PetVisualRoot，或者它上面没挂 PetVisualConfig。", ColDim);
                return;
            }

            if (visualStatus != null)
                visualStatus.text = "<b>" + cfg.presetName + "</b>　·　" + cfg.parameters.Count + " 个参数　·　当前形态："
                                    + (visualRoot != null ? visualRoot.CurrentFormName : "—");

            var list = cfg.parameters;
            if (list == null || list.Count == 0) { AddTextRow(visualListRoot, "这个形态没有参数。", ColDim); return; }

            string lastGroup = null;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;

                if (!string.IsNullOrEmpty(p.group) && p.group != lastGroup)
                {
                    lastGroup = p.group;
                    AddGroupRow(visualListRoot, p.group);
                }

                switch (p.kind)
                {
                    case PetParamKind.Float:
                    case PetParamKind.Int:
                    {
                        bool whole = p.kind == PetParamKind.Int || p.wholeNumbers;
                        var cap = p;   // 闭包捕获当前这一条
                        AddSliderRow(visualListRoot, p.label, p.min, p.max, p.value, whole, p.tip,
                            v =>
                            {
                                cap.value = whole ? Mathf.Round(v) : v;
                                visualDirty = true;
                            });
                        break;
                    }

                    case PetParamKind.GrayColor:
                    {
                        var cap = p;
                        AddSliderRow(visualListRoot, p.label, p.min, p.max, p.value, false, p.tip,
                            v => { cap.value = v; visualDirty = true; });
                        break;
                    }

                    case PetParamKind.Toggle:
                    case PetParamKind.KeywordToggle:
                    {
                        var cap = p;
                        AddToggleRow(visualListRoot, p.label, p.value > 0.5f,
                            v => { cap.value = v ? 1f : 0f; visualDirty = true; });
                        break;
                    }

                    case PetParamKind.Color:
                    {
                        var cap = p;
                        AddColorRow(visualListRoot, p.label, p.color,
                            c => { cap.color = c; visualDirty = true; });
                        break;
                    }

                    case PetParamKind.Vector:
                    {
                        var cap = p;
                        AddVectorRow(visualListRoot, p.label, p.vector,
                            v => { cap.vector = v; visualDirty = true; });
                        break;
                    }

                    default:
                        AddTextRow(visualListRoot, p.label + "（该类型暂不支持）", ColDim);
                        break;
                }
            }
        }

        // =====================================================================
        //  页面 4：粒子特效
        // =====================================================================

        PetVFXController vfxCtl;
        ParticleSystem vfxSymbols;
        Material vfxMat;
        TrailRenderer vfxTrail;
        Material vfxTrailMat;
        RectTransform vfxListRoot;
        Text vfxStatus;
        float vfxSizeScale = 1f;

        RectTransform BuildParticlePage()
        {
            var page = MakePage("PageParticle");

            var head = MakeText("Head", page, "<b>粒子特效</b>", 17, ColAccent, TextAnchor.UpperLeft);
            head.rectTransform.anchorMin = new Vector2(0, 1); head.rectTransform.anchorMax = new Vector2(1, 1);
            head.rectTransform.pivot = new Vector2(0, 1);
            head.rectTransform.offsetMin = new Vector2(16, 0); head.rectTransform.offsetMax = new Vector2(-16, 0);
            head.rectTransform.anchoredPosition = new Vector2(0, -10);
            head.rectTransform.sizeDelta = new Vector2(-32, 24);

            vfxStatus = MakeText("Status", page, "", 12, ColDim, TextAnchor.UpperLeft);
            var vst = vfxStatus.rectTransform;
            vst.anchorMin = new Vector2(0, 1); vst.anchorMax = new Vector2(1, 1);
            vst.pivot = new Vector2(0, 1);
            vst.offsetMin = new Vector2(16, 0); vst.offsetMax = new Vector2(-16, 0);
            vst.anchoredPosition = new Vector2(0, -36);
            vst.sizeDelta = new Vector2(-32, 18);

            var btnRow = MakeRect("BtnRow", page);
            btnRow.anchorMin = new Vector2(0, 1); btnRow.anchorMax = new Vector2(1, 1);
            btnRow.pivot = new Vector2(0, 1);
            btnRow.offsetMin = new Vector2(16, 0); btnRow.offsetMax = new Vector2(-16, 0);
            btnRow.anchoredPosition = new Vector2(0, -58);
            btnRow.sizeDelta = new Vector2(-32, 30);

            MakeLeftButton(btnRow, "恢复推荐值", 0, () => { ResetParticles(); RebuildParticleRows(); });

            ScrollRect sr;
            vfxListRoot = MakeScrollList(page, 100f, 10f, out sr);
            return page;
        }

        void RefreshParticles()
        {
            FindVfxRefs();
            RebuildParticleRows();
        }

        void FindVfxRefs()
        {
            if (vfxCtl == null) vfxCtl = FindObjectOfType<PetVFXController>();
            if (vfxSymbols == null && vfxCtl != null) vfxSymbols = vfxCtl.mathSymbolsVFX;
            if (vfxSymbols == null)
            {
                var go = GameObject.Find("Particle_MathSymbols");
                if (go != null) vfxSymbols = go.GetComponent<ParticleSystem>();
            }
            if (vfxMat == null && vfxSymbols != null)
            {
                var r = vfxSymbols.GetComponent<ParticleSystemRenderer>();
                if (r != null) vfxMat = r.sharedMaterial;
            }
            if (vfxTrail == null && vfxCtl != null) vfxTrail = vfxCtl.moveTrail;
            if (vfxTrail == null)
            {
                var go = GameObject.Find("VFX_MoveTrail");
                if (go != null) vfxTrail = go.GetComponent<TrailRenderer>();
            }
            if (vfxTrailMat == null && vfxTrail != null) vfxTrailMat = vfxTrail.sharedMaterial;
        }

        void RebuildParticleRows()
        {
            if (vfxListRoot == null) return;
            ClearList(vfxListRoot);

            if (vfxCtl == null || vfxSymbols == null)
            {
                if (vfxStatus != null) vfxStatus.text = "场景里没有 PetVFXController / 特效预制体";
                AddTextRow(vfxListRoot, "找不到特效对象（Particle_MathSymbols / PetVFXController）。", ColDim);
                return;
            }

            if (vfxStatus != null)
                vfxStatus.text = "当前状态：" + CurrentAnimatorState()
                                 + "　·　符号发射率 " + (vfxCtl != null ? vfxCtl.interactionRate.ToString("0") : "?") + "/s";

            // ---------- 全身符号 · 密度 ----------
            AddGroupRow(vfxListRoot, "全身符号 · 密度");
            AddSliderRow(vfxListRoot, "互动发射率", 0f, 120f, vfxCtl.interactionRate, false,
                "doTalk / doPet / doLookBack 时的每秒符号数", v => vfxCtl.interactionRate = v);
            AddSliderRow(vfxListRoot, "待机发射率", 0f, 60f, vfxCtl.idleRate, false,
                "doIdle / doSit / doSleep 时的每秒符号数", v => vfxCtl.idleRate = v);
            AddSliderRow(vfxListRoot, "拖拽发射率", 0f, 80f, vfxCtl.dragRate, false,
                "被拎起来的时候", v => vfxCtl.dragRate = v);
            AddSliderRow(vfxListRoot, "互动爆发数量", 0f, 100f, vfxCtl.interactionBurst, true,
                "进入互动状态时额外喷一次的颗数", v => vfxCtl.interactionBurst = Mathf.RoundToInt(v));
            AddSliderRow(vfxListRoot, "落地喷发数量", 0f, 100f, vfxCtl.dropBurstCount, true,
                "松手落地时喷一次的颗数", v => vfxCtl.dropBurstCount = Mathf.RoundToInt(v));

            // ---------- 外观 ----------
            AddGroupRow(vfxListRoot, "符号外观");
            AddSliderRow(vfxListRoot, "符号大小", 0.4f, 3f, vfxSizeScale, false,
                "相对原始尺寸的倍率", v =>
                {
                    vfxSizeScale = v;
                    if (vfxSymbols == null) return;
                    var main = vfxSymbols.main;
                    main.startSize = new ParticleSystem.MinMaxCurve(0.03f * v, 0.08f * v);
                });

            if (vfxMat != null)
            {
                float emi = vfxMat.HasProperty("_EmissionIntensity") ? vfxMat.GetFloat("_EmissionIntensity") : 1.15f;
                AddSliderRow(vfxListRoot, "自发光强度", 0f, 4f, emi, false, null,
                    v => { if (vfxMat.HasProperty("_EmissionIntensity")) vfxMat.SetFloat("_EmissionIntensity", v); });

                if (vfxMat.HasProperty("_EmissionColor"))
                {
                    AddColorRow(vfxListRoot, "自发光颜色", vfxMat.GetColor("_EmissionColor"),
                        c => vfxMat.SetColor("_EmissionColor", c));
                }
            }

            // ---------- 脚底拖尾 ----------
            if (vfxTrail != null)
            {
                AddGroupRow(vfxListRoot, "脚底拖尾");
                AddSliderRow(vfxListRoot, "存活时间", 0.1f, 2f, vfxTrail.time, false,
                    "越长尾巴越拖", v => vfxTrail.time = v);
                AddSliderRow(vfxListRoot, "拖尾宽度", 0.02f, 1.5f, vfxTrail.widthMultiplier, false, null,
                    v => vfxTrail.widthMultiplier = v);

                if (vfxTrailMat != null && vfxTrailMat.HasProperty("_PeakAlpha"))
                {
                    AddSliderRow(vfxListRoot, "拖尾透明度", 0f, 1f, vfxTrailMat.GetFloat("_PeakAlpha"), false, null,
                        v => vfxTrailMat.SetFloat("_PeakAlpha", v));
                }
            }
        }

        void ResetParticles()
        {
            FindVfxRefs();
            if (vfxCtl != null)
            {
                vfxCtl.interactionRate = 45f;
                vfxCtl.idleRate = 16f;
                vfxCtl.dragRate = 24f;
                vfxCtl.interactionBurst = 30;
                vfxCtl.dropBurstCount = 30;
            }
            vfxSizeScale = 1f;
            if (vfxSymbols != null)
            {
                var main = vfxSymbols.main;
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
            }
            if (vfxMat != null && vfxMat.HasProperty("_EmissionIntensity")) vfxMat.SetFloat("_EmissionIntensity", 1.15f);
            if (vfxTrail != null) { vfxTrail.time = 0.55f; vfxTrail.widthMultiplier = 0.38f; }
            if (vfxTrailMat != null && vfxTrailMat.HasProperty("_PeakAlpha")) vfxTrailMat.SetFloat("_PeakAlpha", 0.35f);
        }

        string CurrentAnimatorState()
        {
            var an = vfxCtl != null ? vfxCtl.petAnimator : null;
            if (an == null) return "—";
            var infos = an.GetCurrentAnimatorClipInfo(0);
            if (infos == null || infos.Length == 0) return "—";
            return infos[0].clip != null ? infos[0].clip.name : "—";
        }

        // ---------------- TTS 配置（pet_profile.json → tts_config） ----------------
        static string ProfileJsonPath
        {
            get { return System.IO.Path.Combine(PetPathUtil.ExeDir(), "pet_profile.json"); }
        }

        void RefreshTtsFields()
        {
            string v = "zh-CN-YunxiNeural", r = "+8%", p = "+12Hz", vol = "+0%";
            try
            {
                if (System.IO.File.Exists(ProfileJsonPath))
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(ProfileJsonPath));
                    var c = root["tts_config"] as Newtonsoft.Json.Linq.JObject;
                    if (c != null)
                    {
                        v = (string)c["voice"] ?? v;
                        r = (string)c["rate"] ?? r;
                        p = (string)c["pitch"] ?? p;
                        vol = (string)c["volume"] ?? vol;
                    }
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[Dashboard] 读 tts_config 失败：" + e.Message); }

            if (setVoice != null) setVoice.text = v;
            if (setRate != null) setRate.text = r;
            if (setPitch != null) setPitch.text = p;
            if (setVolume != null) setVolume.text = vol;
        }

        void SaveTtsFields()
        {
            try
            {
                Newtonsoft.Json.Linq.JObject root = null;
                if (System.IO.File.Exists(ProfileJsonPath))
                    root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(ProfileJsonPath));
                if (root == null) root = new Newtonsoft.Json.Linq.JObject();

                var c = root["tts_config"] as Newtonsoft.Json.Linq.JObject;
                if (c == null) { c = new Newtonsoft.Json.Linq.JObject(); root["tts_config"] = c; }
                if (setVoice != null && !string.IsNullOrEmpty(setVoice.text)) c["voice"] = setVoice.text.Trim();
                if (setRate != null && !string.IsNullOrEmpty(setRate.text)) c["rate"] = setRate.text.Trim();
                if (setPitch != null && !string.IsNullOrEmpty(setPitch.text)) c["pitch"] = setPitch.text.Trim();
                if (setVolume != null && !string.IsNullOrEmpty(setVolume.text)) c["volume"] = setVolume.text.Trim();

                System.IO.File.WriteAllText(ProfileJsonPath, root.ToString(Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
                if (TTS.Instance != null) TTS.Instance.LoadConfigFromProfile();   // 立刻生效
            }
            catch (System.Exception e) { Debug.LogWarning("[Dashboard] 写 tts_config 失败：" + e.Message); }
        }

        void RefreshSettings()
        {
            var cfg = PetApiConfig.Instance;
            if (cfg != null)
            {
                if (setBaseUrl != null) setBaseUrl.text = cfg.baseUrl ?? "";
                if (setModel != null) setModel.text = cfg.model ?? "";
                if (setApiKey != null) setApiKey.text = cfg.apiKey ?? "";
                if (thinkingToggle != null) thinkingToggle.isOn = cfg.enableThinking;
            }
            var prof = PetProfileConfig.Instance;
            if (prof != null)
            {
                if (setPetName != null) setPetName.text = prof.petName ?? "";
                if (setUserName != null) setUserName.text = prof.userName ?? "";
                if (setPersona != null) setPersona.text = prof.persona ?? "";
            }
            if (keepPetVisibleToggle != null && PetWindowManager.Instance != null)
                keepPetVisibleToggle.isOn = PetWindowManager.Instance.keepPetVisibleWhenClosed;
            if (sizeSlider != null)
            {
                var fr = FindObjectOfType<PetCameraFraming>();
                if (fr != null) sizeSlider.SetValueWithoutNotify(fr.ScreenPercent);
            }
            RefreshTtsFields();
        }

        void SaveSettings()
        {
            var cfg = PetApiConfig.Instance;
            if (cfg != null)
            {
                cfg.baseUrl = setBaseUrl != null ? setBaseUrl.text : cfg.baseUrl;
                cfg.model = setModel != null ? setModel.text : cfg.model;
                cfg.apiKey = setApiKey != null ? setApiKey.text : cfg.apiKey;
                if (thinkingToggle != null) cfg.enableThinking = thinkingToggle.isOn;
                cfg.SaveAndApply();
            }
            var prof = PetProfileConfig.Instance;
            if (prof != null)
            {
                prof.petName = setPetName != null ? setPetName.text : prof.petName;
                prof.userName = setUserName != null ? setUserName.text : prof.userName;
                prof.persona = setPersona != null ? setPersona.text : prof.persona;
                prof.SaveAndApply();
            }
            SaveTtsFields();
            if (setStatus != null) setStatus.text = "已保存并生效";
            RefreshChat();
        }

        void ReloadSettings()
        {
            var cfg = PetApiConfig.Instance;
            if (cfg != null) { cfg.Load(); cfg.ApplyToApi(); }
            var prof = PetProfileConfig.Instance;
            if (prof != null) prof.Load();
            RefreshSettings();
            if (setStatus != null) setStatus.text = "已从文件重新读取";
        }

        // =====================================================================
        //  页面 3：工具与 MCP
        // =====================================================================

        RectTransform toolsPage;
        RectTransform toolsListRoot;
        Text toolsStatus;
        Toggle toolsMaster;

        RectTransform BuildToolsPage()
        {
            var page = MakePage("PageTools");
            toolsPage = page;

            var head = MakeText("Head", page, "<b>工具与 MCP</b>", 17, ColAccent, TextAnchor.UpperLeft);
            head.rectTransform.anchorMin = new Vector2(0, 1); head.rectTransform.anchorMax = new Vector2(1, 1);
            head.rectTransform.pivot = new Vector2(0, 1);
            head.rectTransform.offsetMin = new Vector2(16, 0); head.rectTransform.offsetMax = new Vector2(-16, 0);
            head.rectTransform.anchoredPosition = new Vector2(0, -14);
            head.rectTransform.sizeDelta = new Vector2(-32, 26);

            toolsMaster = MakeToggle(page, "启用外部工具（总闸）", -46);
            toolsMaster.onValueChanged.AddListener(v =>
            {
                if (ToolRegistry.Instance != null) ToolRegistry.Instance.SetExternalMaster(v);
                Invoke("RebuildToolsPage", 0.4f);
            });

            var btnRow = MakeRect("BtnRow", page);
            btnRow.anchorMin = new Vector2(0, 1); btnRow.anchorMax = new Vector2(0, 1);
            btnRow.pivot = new Vector2(0, 1); btnRow.anchoredPosition = new Vector2(16, -84);
            btnRow.sizeDelta = new Vector2(460, 34);

            var reload = MakeButton("Reload", btnRow, "重新加载工具", () =>
            {
                if (ToolRegistry.Instance != null) ToolRegistry.Instance.ReloadNow();
                Invoke("RebuildToolsPage", 0.4f);
            }, Vector2.zero, new Vector2(140, 34));
            var rrt = reload.GetComponent<RectTransform>();
            rrt.anchorMin = rrt.anchorMax = new Vector2(0, 1); rrt.pivot = new Vector2(0, 1);
            rrt.anchoredPosition = Vector2.zero;

            var open = MakeButton("OpenFolder", btnRow, "打开配置文件夹", OpenToolsFolder, new Vector2(150, 0), new Vector2(150, 34));
            var ort = open.GetComponent<RectTransform>();
            ort.anchorMin = ort.anchorMax = new Vector2(0, 1); ort.pivot = new Vector2(0, 1);
            ort.anchoredPosition = new Vector2(150, 0);

            toolsStatus = MakeText("Status", page, "", 13, ColDim, TextAnchor.UpperLeft);
            toolsStatus.rectTransform.anchorMin = new Vector2(0, 1); toolsStatus.rectTransform.anchorMax = new Vector2(1, 1);
            toolsStatus.rectTransform.pivot = new Vector2(0, 1);
            toolsStatus.rectTransform.offsetMin = new Vector2(16, 0); toolsStatus.rectTransform.offsetMax = new Vector2(-16, 0);
            toolsStatus.rectTransform.anchoredPosition = new Vector2(0, -126);
            toolsStatus.rectTransform.sizeDelta = new Vector2(-32, 22);

            // 工具列表（自己滚）
            var scrollGo = MakeRect("ToolsScroll", page);
            scrollGo.anchorMin = new Vector2(0, 0); scrollGo.anchorMax = new Vector2(1, 1);
            scrollGo.offsetMin = new Vector2(16, 16);
            scrollGo.offsetMax = new Vector2(-16, -156);

            var sr = scrollGo.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;

            var viewport = MakeRect("Viewport", scrollGo);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = viewport.offsetMax = Vector2.zero;
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.15f);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;

            toolsListRoot = MakeRect("Content", viewport);
            toolsListRoot.anchorMin = new Vector2(0, 1);
            toolsListRoot.anchorMax = new Vector2(1, 1);
            toolsListRoot.pivot = new Vector2(0.5f, 1);
            toolsListRoot.anchoredPosition = Vector2.zero;
            toolsListRoot.sizeDelta = new Vector2(0, 0);
            var vlg = toolsListRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6; vlg.padding = new RectOffset(12, 12, 10, 10);
            vlg.childForceExpandHeight = false; vlg.childControlHeight = true; vlg.childControlWidth = true;
            var fitter = toolsListRoot.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.viewport = viewport; sr.content = toolsListRoot;

            return page;
        }

        void RebuildToolsPage()
        {
            if (toolsListRoot == null) return;
            for (int i = toolsListRoot.childCount - 1; i >= 0; i--)
                Destroy(toolsListRoot.GetChild(i).gameObject);

            var reg = ToolRegistry.Instance;
            if (reg == null || reg.All == null)
            {
                AddInfoRow("场景里没有 ToolRegistry 组件（或还没初始化）");
                return;
            }

            if (toolsMaster != null) toolsMaster.isOn = reg.ExternalEnabled;
            if (toolsStatus != null) toolsStatus.text = reg.Status;

            AddInfoRow("<b>内置工具</b>");
            int builtin = 0;
            for (int i = 0; i < reg.All.Count; i++)
            {
                var d = reg.All[i];
                if (d.kind != ToolKind.Builtin) continue;
                builtin++;
                AddToolRow(d, reg);
            }
            if (builtin == 0) AddInfoRow("（没有内置工具）");

            AddInfoRow("<b>外部工具 / MCP</b>");
            int ext = 0;
            for (int i = 0; i < reg.All.Count; i++)
            {
                var d = reg.All[i];
                if (d.kind == ToolKind.Builtin) continue;
                ext++;
                AddToolRow(d, reg);
            }
            if (ext == 0)
                AddInfoRow("（没有加载到外部工具：先打开总闸，或在 pet_tools.json 里添加）");

            AddInfoRow("<color=#8C939E>想加 MCP（文件系统 / Blender）？在 pet_tools.json 的 tools 数组里加一条，保存后点【重新加载工具】。</color>");
        }

        void AddInfoRow(string text)
        {
            var row = MakeRect("InfoRow", toolsListRoot).gameObject;
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 22;
            var t = row.AddComponent<Text>();
            t.font = font; t.fontSize = 14; t.color = ColDim;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = text;
        }

        void AddToolRow(ToolDescriptor d, ToolRegistry reg)
        {
            var row = MakeRect("ToolRow", toolsListRoot).gameObject;
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 28;

            var toggle = MakeToggle(row.transform, "", 0f);
            var trt = toggle.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1);
            trt.offsetMin = new Vector2(4, 0); trt.offsetMax = Vector2.zero;
            toggle.isOn = d.isEnabled;
            string name = d.name;
            toggle.onValueChanged.AddListener(v => { if (reg != null) reg.SetToolEnabled(name, v); });

            var label = toggle.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = " [" + d.kind + "] " + d.name + "　" + Snippet(d.description, 40);
                label.fontSize = 14;
            }
        }

        static string Snippet(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        void OpenToolsFolder()
        {
            var reg = ToolRegistry.Instance;
            if (reg == null) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(reg.SettingsPath);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception e) { if (toolsStatus != null) toolsStatus.text = "打开失败：" + e.Message; }
        }

        // =====================================================================
        //  小工具
        // =====================================================================

        // =====================================================================
        //  动态列表控件（给材质页 / 粒子页共用）
        // =====================================================================

        int lastToolCount = -1;
        float nextToolsRefresh;

        void Update()
        {
            // ★ MCP 是【异步】加载的（uvx 冷启动要几十秒）：工具页开着时，
            //   只要工具数量变了就自动重建列表 —— 否则打开页面那一刻还是 0，用户看到的就是“没加载出来”。
            if (pageIndex == 4 && toolsListRoot != null && Time.unscaledTime >= nextToolsRefresh)
            {
                nextToolsRefresh = Time.unscaledTime + 1f;
                var reg0 = ToolRegistry.Instance;
                int sig = reg0 != null && reg0.All != null ? reg0.All.Count : -1;
                if (sig != lastToolCount) { lastToolCount = sig; RebuildToolsPage(); }
            }

            // 滑杆拖动 => 标记 dirty => 每帧只套用一次（比每个控件各套一次省得多）
            if (!visualDirty) return;
            visualDirty = false;
            EnsureVisualRoot();
            if (visualRoot != null) visualRoot.Apply();
        }

        static void ClearList(RectTransform list)
        {
            if (list == null) return;
            for (int i = list.childCount - 1; i >= 0; i--)
            {
                var c = list.GetChild(i).gameObject;
                c.SetActive(false);      // Destroy 是帧末生效的，先藏起来避免一帧重影
                Destroy(c);
            }
        }

        RectTransform AddListRow(RectTransform parent, float height)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            return go.GetComponent<RectTransform>();
        }

        void AddGroupRow(RectTransform parent, string title)
        {
            var row = AddListRow(parent, 26f);
            var t = MakeText("Group", row, "───  " + title + "  ───", 14, ColAccent, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(2, 0);
            t.rectTransform.offsetMax = Vector2.zero;
        }

        void AddTextRow(RectTransform parent, string text, Color color)
        {
            var row = AddListRow(parent, 22f);
            var t = MakeText("Info", row, text, 12, color, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(2, 0);
            t.rectTransform.offsetMax = Vector2.zero;
        }

        /// <summary>一行滑杆：标题 + 滑杆 + 数值；tip 非空时下面多一行小字。</summary>
        Slider AddSliderRow(RectTransform parent, string label, float min, float max, float value,
                            bool whole, string tip, Action<float> onChange)
        {
            bool hasTip = !string.IsNullOrEmpty(tip);
            var row = AddListRow(parent, hasTip ? 44f : 30f);

            var lbl = MakeText("Label", row, label, 13, ColText, TextAnchor.MiddleLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1);
            lrt.pivot = new Vector2(0, 1);
            lrt.sizeDelta = new Vector2(150, 20);
            lrt.anchoredPosition = Vector2.zero;

            var val = MakeText("Value", row, "", 12, ColDim, TextAnchor.MiddleRight);
            var vrt = val.rectTransform;
            vrt.anchorMin = new Vector2(1, 1); vrt.anchorMax = new Vector2(1, 1);
            vrt.pivot = new Vector2(1, 1);
            vrt.sizeDelta = new Vector2(58, 20);
            vrt.anchoredPosition = Vector2.zero;

            string fmt = whole ? "0" : ((max - min) >= 20f ? "0.0" : "0.000");

            var slider = MakeSliderRect(row, -5f, 16f, 154f, 64f, min, max, value, whole,
                v =>
                {
                    val.text = whole ? Mathf.RoundToInt(v).ToString() : v.ToString(fmt);
                    if (onChange != null) onChange(v);
                });
            val.text = whole ? Mathf.RoundToInt(slider.value).ToString() : slider.value.ToString(fmt);

            if (hasTip)
            {
                var t = MakeText("Tip", row, tip, 11, ColDim, TextAnchor.MiddleLeft);
                var trt = t.rectTransform;
                trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1);
                trt.offsetMin = new Vector2(2, -42);
                trt.offsetMax = new Vector2(0, -26);
            }
            return slider;
        }

        void AddToggleRow(RectTransform parent, string label, bool value, Action<bool> onChange)
        {
            var row = AddListRow(parent, 28f);
            var bg = row.gameObject.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.19f, 0.24f, 1f);

            var check = MakeRect("Check", row);
            check.anchorMin = check.anchorMax = new Vector2(0, 0.5f);
            check.sizeDelta = new Vector2(16, 16);
            check.anchoredPosition = new Vector2(12, 0);
            var ci = check.gameObject.AddComponent<Image>();
            ci.color = ColAccent;

            var t = MakeText("Label", row, label, 13, ColText, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(36, 0);
            t.rectTransform.offsetMax = new Vector2(-8, 0);

            var tg = row.gameObject.AddComponent<Toggle>();
            tg.targetGraphic = bg;
            tg.graphic = ci;
            tg.isOn = value;
            if (onChange != null) tg.onValueChanged.AddListener(v => onChange(v));
        }

        /// <summary>颜色参数：R/G/B 三条滑杆 + 右上角预览色块（不需要 ColorPicker）。</summary>
        void AddColorRow(RectTransform parent, string label, Color value, Action<Color> onChange)
        {
            var row = AddListRow(parent, 92f);

            var lbl = MakeText("Label", row, label, 13, ColText, TextAnchor.MiddleLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1);
            lrt.pivot = new Vector2(0, 1);
            lrt.sizeDelta = new Vector2(240, 20);
            lrt.anchoredPosition = Vector2.zero;

            var swGo = MakeRect("Swatch", row);
            swGo.anchorMin = swGo.anchorMax = new Vector2(1, 1);
            swGo.pivot = new Vector2(1, 1);
            swGo.sizeDelta = new Vector2(30, 16);
            swGo.anchoredPosition = new Vector2(0, -2);
            var sw = swGo.gameObject.AddComponent<Image>();
            sw.color = value;

            Color c = value;
            string[] names = { "R", "G", "B" };
            for (int i = 0; i < 3; i++)
            {
                int ch = i;
                float chVal = i == 0 ? c.r : (i == 1 ? c.g : c.b);
                float top = -24f - i * 22f;

                var cl = MakeText("C" + i, row, names[i], 11, ColDim, TextAnchor.MiddleLeft);
                var clr = cl.rectTransform;
                clr.anchorMin = new Vector2(0, 1); clr.anchorMax = new Vector2(0, 1);
                clr.pivot = new Vector2(0, 1);
                clr.sizeDelta = new Vector2(14, 16);
                clr.anchoredPosition = new Vector2(0, top);

                MakeSliderRect(row, top, 16f, 20f, 44f, 0f, 1f, chVal, false,
                    v =>
                    {
                        if (ch == 0) c.r = v; else if (ch == 1) c.g = v; else c.b = v;
                        sw.color = c;
                        if (onChange != null) onChange(c);
                    });
            }
        }

        /// <summary>向量参数：X/Y/Z 三条滑杆（范围 -1..1）。</summary>
        void AddVectorRow(RectTransform parent, string label, Vector3 value, Action<Vector3> onChange)
        {
            var row = AddListRow(parent, 92f);

            var lbl = MakeText("Label", row, label, 13, ColText, TextAnchor.MiddleLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1);
            lrt.pivot = new Vector2(0, 1);
            lrt.sizeDelta = new Vector2(240, 20);
            lrt.anchoredPosition = Vector2.zero;

            Vector3 v3 = value;
            string[] names = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                int ch = i;
                float chVal = i == 0 ? v3.x : (i == 1 ? v3.y : v3.z);
                float top = -24f - i * 22f;

                var cl = MakeText("C" + i, row, names[i], 11, ColDim, TextAnchor.MiddleLeft);
                var clr = cl.rectTransform;
                clr.anchorMin = new Vector2(0, 1); clr.anchorMax = new Vector2(0, 1);
                clr.pivot = new Vector2(0, 1);
                clr.sizeDelta = new Vector2(14, 16);
                clr.anchoredPosition = new Vector2(0, top);

                MakeSliderRect(row, top, 16f, 20f, 44f, -1f, 1f, chVal, false,
                    v =>
                    {
                        if (ch == 0) v3.x = v; else if (ch == 1) v3.y = v; else v3.z = v;
                        if (onChange != null) onChange(v3);
                    });
            }
        }

        /// <summary>在父物体里摆一条滑杆（top 是相对父物体顶边的负偏移）。</summary>
        Slider MakeSliderRect(RectTransform parent, float top, float height,
                              float leftPad, float rightPad,
                              float min, float max, float value, bool whole, Action<float> onChange)
        {
            var sgo = new GameObject("Slider", typeof(RectTransform));
            sgo.transform.SetParent(parent, false);
            var srt = sgo.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 1);
            srt.anchorMax = new Vector2(1, 1);
            srt.pivot = new Vector2(0.5f, 1);
            srt.offsetMin = new Vector2(leftPad, top - height);
            srt.offsetMax = new Vector2(-rightPad, top);

            var slider = sgo.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = whole;
            slider.minValue = min;
            slider.maxValue = max;

            var bgGo = new GameObject("Background", typeof(RectTransform));
            bgGo.transform.SetParent(sgo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.16f, 0.19f, 0.24f, 1f);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0, 0.32f); bgRt.anchorMax = new Vector2(1, 0.68f);
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sgo.transform, false);
            var faRt = fillArea.GetComponent<RectTransform>();
            faRt.anchorMin = new Vector2(0, 0.32f); faRt.anchorMax = new Vector2(1, 0.68f);
            faRt.offsetMin = Vector2.zero; faRt.offsetMax = new Vector2(-10, 0);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = ColAccent;
            var fRt = fillGo.GetComponent<RectTransform>();
            fRt.anchorMin = Vector2.zero; fRt.anchorMax = new Vector2(0, 1);
            fRt.sizeDelta = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sgo.transform, false);
            var haRt = handleArea.GetComponent<RectTransform>();
            haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one;
            haRt.offsetMin = Vector2.zero; haRt.offsetMax = new Vector2(-10, 0);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = ColText;
            var hRt = handleGo.GetComponent<RectTransform>();
            hRt.anchorMin = new Vector2(0, 0); hRt.anchorMax = new Vector2(0, 1);
            hRt.sizeDelta = new Vector2(12, 0);

            slider.fillRect = fRt;
            slider.handleRect = hRt;
            slider.targetGraphic = handleImg;

            slider.value = Mathf.Clamp(value, min, max);   // 先赋值再挂监听，避免构建时触发一次
            if (onChange != null) slider.onValueChanged.AddListener(v => onChange(v));
            return slider;
        }

        /// <summary>带纵向滚动 + 5px 细滚动条的列表区，返回 Content（往里面塞 Row）。</summary>
        RectTransform MakeScrollList(RectTransform page, float topInset, float bottomInset, out ScrollRect sr)
        {
            var scrollGo = MakeRect("Scroll", page);
            scrollGo.anchorMin = Vector2.zero; scrollGo.anchorMax = Vector2.one;
            scrollGo.offsetMin = new Vector2(8, bottomInset);
            scrollGo.offsetMax = new Vector2(-12, -topInset);

            sr = scrollGo.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 40f;

            var viewport = MakeRect("Viewport", scrollGo);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero; viewport.offsetMax = new Vector2(-8, 0);
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = MakeRect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(10, 10, 8, 14);
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.viewport = viewport;
            sr.content = content;

            // 5px 细滚动条
            var sbGo = MakeRect("Scrollbar", scrollGo);
            sbGo.anchorMin = new Vector2(1, 0); sbGo.anchorMax = new Vector2(1, 1);
            sbGo.pivot = new Vector2(1, 0.5f);
            sbGo.sizeDelta = new Vector2(5, 0);
            sbGo.anchoredPosition = Vector2.zero;
            var sbBg = sbGo.gameObject.AddComponent<Image>();
            sbBg.color = new Color(1f, 1f, 1f, 0.05f);
            var sb = sbGo.gameObject.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;

            var slideArea = MakeRect("Sliding Area", sbGo);
            slideArea.anchorMin = Vector2.zero; slideArea.anchorMax = Vector2.one;
            slideArea.offsetMin = Vector2.zero; slideArea.offsetMax = Vector2.zero;

            var handle = MakeRect("Handle", slideArea);
            // ⚠️ 必须清零 sizeDelta，否则默认的 100×100 会让滑块鼓成一大条
            handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one;
            handle.sizeDelta = Vector2.zero;
            handle.anchoredPosition = Vector2.zero;
            var hImg = handle.gameObject.AddComponent<Image>();
            hImg.color = new Color(0.353f, 0.690f, 1f, 0.45f);
            sb.handleRect = handle;
            sb.targetGraphic = hImg;
            sb.size = 0.18f;

            sr.verticalScrollbar = sb;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            return content;
        }

        Button MakeLeftButton(RectTransform parent, string label, float x, Action onClick)
        {
            var b = MakeButton("Btn_" + label, parent, label, onClick, Vector2.zero, new Vector2(110, 28));
            var rt = b.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, 0);
            return b;
        }

        RectTransform MakePage(string name)
        {
            var p = MakeRect(name, contentArea);
            p.anchorMin = Vector2.zero; p.anchorMax = Vector2.one;
            p.offsetMin = p.offsetMax = Vector2.zero;
            return p;
        }

        static RectTransform MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.localScale = Vector3.one;
            return rt;
        }

        Text MakeText(string name, Transform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = color; t.text = text;
            t.alignment = anchor; t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        Button MakeButton(string name, Transform parent, string label, Action onClick, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = ColPanel;
            var b = go.AddComponent<Button>();
            b.targetGraphic = img;
            if (onClick != null) b.onClick.AddListener(() => onClick());

            var t = MakeText("Label", go.transform, label, 14, ColText, TextAnchor.MiddleCenter);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return b;
        }

        InputField MakeInputField(string name, Transform parent, string placeholder)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.06f, 0.075f, 0.095f, 1f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = font; text.fontSize = 15; text.color = ColText;
            text.supportRichText = false;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(10, 4); text.rectTransform.offsetMax = new Vector2(-10, -4);

            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.AddComponent<Text>();
            ph.font = font; ph.fontSize = 15; ph.color = ColDim; ph.text = placeholder;
            ph.rectTransform.anchorMin = Vector2.zero; ph.rectTransform.anchorMax = Vector2.one;
            ph.rectTransform.offsetMin = new Vector2(10, 4); ph.rectTransform.offsetMax = new Vector2(-10, -4);

            var input = go.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = ph;
            input.lineType = InputField.LineType.SingleLine;
            return input;
        }

        Slider MakeSlider(Transform parent, string label, float min, float max, float value, Action<float> onChange, float y)
        {
            var row = new GameObject("SliderRow", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(16, 0); rt.offsetMax = new Vector2(-16, 0);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-32, 30);

            var lbl = MakeText("Label", row.transform, label, 14, ColText, TextAnchor.MiddleLeft);
            lbl.rectTransform.anchorMin = new Vector2(0, 0);
            lbl.rectTransform.anchorMax = new Vector2(0, 1);
            lbl.rectTransform.pivot = new Vector2(0, 0.5f);
            lbl.rectTransform.sizeDelta = new Vector2(100, 0);
            lbl.rectTransform.anchoredPosition = Vector2.zero;

            var sgo = new GameObject("Slider", typeof(RectTransform));
            sgo.transform.SetParent(row.transform, false);
            var srt = sgo.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0.5f); srt.anchorMax = new Vector2(1, 0.5f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.offsetMin = new Vector2(106, -8); srt.offsetMax = new Vector2(-56, 8);

            var slider = sgo.AddComponent<Slider>();

            var bgGo = new GameObject("Background", typeof(RectTransform));
            bgGo.transform.SetParent(sgo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.16f, 0.19f, 0.24f, 1f);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0, 0.35f); bgRt.anchorMax = new Vector2(1, 0.65f);
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sgo.transform, false);
            var faRt = fillArea.GetComponent<RectTransform>();
            faRt.anchorMin = new Vector2(0, 0.35f); faRt.anchorMax = new Vector2(1, 0.65f);
            faRt.offsetMin = Vector2.zero; faRt.offsetMax = new Vector2(-10, 0);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = ColAccent;
            var fRt = fillGo.GetComponent<RectTransform>();
            fRt.anchorMin = Vector2.zero; fRt.anchorMax = new Vector2(0, 1);
            fRt.sizeDelta = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sgo.transform, false);
            var haRt = handleArea.GetComponent<RectTransform>();
            haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one;
            haRt.offsetMin = Vector2.zero; haRt.offsetMax = new Vector2(-10, 0);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = ColText;
            var hRt = handleGo.GetComponent<RectTransform>();
            hRt.sizeDelta = new Vector2(14, 0);
            hRt.anchorMin = new Vector2(0, 0); hRt.anchorMax = new Vector2(0, 1);

            slider.fillRect = fRt;
            slider.handleRect = hRt;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;

            var valText = MakeText("Value", row.transform, value.ToString("0.00"), 13, ColDim, TextAnchor.MiddleRight);
            valText.rectTransform.anchorMin = new Vector2(1, 0);
            valText.rectTransform.anchorMax = new Vector2(1, 1);
            valText.rectTransform.pivot = new Vector2(1, 0.5f);
            valText.rectTransform.sizeDelta = new Vector2(50, 0);
            valText.rectTransform.anchoredPosition = Vector2.zero;

            slider.onValueChanged.AddListener(v =>
            {
                if (valText != null) valText.text = v.ToString("0.00");
                if (onChange != null) onChange(v);
            });

            return slider;
        }

        Toggle MakeToggle(Transform parent, string label, float y)
        {
            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.19f, 0.24f, 1f);

            var checkGo = new GameObject("Check", typeof(RectTransform));
            checkGo.transform.SetParent(go.transform, false);
            var checkImg = checkGo.AddComponent<Image>();
            checkImg.color = ColAccent;
            var crt = checkGo.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = new Vector2(0, 0.5f);
            crt.sizeDelta = new Vector2(16, 16);
            crt.anchoredPosition = new Vector2(12, 0);

            var t = MakeText("Label", go.transform, label, 14, ColText, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(38, 0); t.rectTransform.offsetMax = Vector2.zero;

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = bg;
            toggle.graphic = checkImg;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(16, 0);
            rt.offsetMax = new Vector2(-16, 0);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-32, 28);
            return toggle;
        }

        float AddLabel(Transform parent, string text, float y, int size, Color color)
        {
            var t = MakeText("Label", parent, text, size, color, TextAnchor.UpperLeft);
            t.rectTransform.anchorMin = new Vector2(0, 1);
            t.rectTransform.anchorMax = new Vector2(1, 1);
            t.rectTransform.pivot = new Vector2(0, 1);
            t.rectTransform.offsetMin = new Vector2(16, 0);
            t.rectTransform.offsetMax = new Vector2(-16, 0);
            float h = size <= 14 ? 22f : 26f;
            t.rectTransform.anchoredPosition = new Vector2(0, y);
            t.rectTransform.sizeDelta = new Vector2(-32, h);
            return y - h - 4;
        }

        InputField AddInput(Transform parent, ref float y, string value, float height = 32f)
        {
            var go = new GameObject("Input", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = new Color(0.06f, 0.075f, 0.095f, 1f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = font; text.fontSize = 15; text.color = ColText; text.supportRichText = false;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(10, 4); text.rectTransform.offsetMax = new Vector2(-10, -4);

            var input = go.AddComponent<InputField>();
            input.textComponent = text;
            input.text = value;
            input.lineType = height > 50 ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(16, 0); rt.offsetMax = new Vector2(-16, 0);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-32, height);

            y -= height + 8;
            return input;
        }
    }

    /// <summary>标题栏拖动（面板在全屏窗口里"假装"是一个普通窗口）。</summary>
    public class DragHandle : MonoBehaviour, IDragHandler, IBeginDragHandler
    {
        public RectTransform target;
        Canvas canvas;

        public void OnBeginDrag(PointerEventData e)
        {
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
        }

        public void OnDrag(PointerEventData e)
        {
            if (target == null) return;
            float scale = canvas != null ? canvas.scaleFactor : 1f;
            if (scale <= 0.0001f) scale = 1f;
            target.anchoredPosition += e.delta / scale;
        }
    }
}
