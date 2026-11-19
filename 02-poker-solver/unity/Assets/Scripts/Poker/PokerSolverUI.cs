using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace PokerSolver
{
    /// <summary>
    /// 斗地主单挑残局解牌器 UI。
    /// 全部界面运行时生成，不依赖 prefab；中文用系统动态字体。
    /// 输入支持两条通道：大文本框（可粘贴） + 点数按钮（左键 +1 / 右键 -1）。
    /// </summary>
    public class PokerSolverUI : MonoBehaviour
    {
        private static readonly Color BgColor = new Color(0.09f, 0.10f, 0.13f, 1f);
        private static readonly Color PanelColor = new Color(0.14f, 0.16f, 0.20f, 1f);
        private static readonly Color InputColor = new Color(0.18f, 0.20f, 0.25f, 1f);
        private static readonly Color ButtonColor = new Color(0.23f, 0.26f, 0.33f, 1f);
        private static readonly Color ButtonHiColor = new Color(0.72f, 0.55f, 0.24f, 1f);
        private static readonly Color TextColor = new Color(0.93f, 0.94f, 0.96f, 1f);
        private static readonly Color DimColor = new Color(0.62f, 0.66f, 0.73f, 1f);
        private static readonly Color GoldColor = new Color(0.95f, 0.76f, 0.36f, 1f);
        private static readonly Color GreenColor = new Color(0.45f, 0.85f, 0.55f, 1f);
        private static readonly Color RedColor = new Color(0.95f, 0.42f, 0.42f, 1f);

        private Font _font;
        private Canvas _canvas;

        private ulong _myHand;
        private ulong _oppHand;
        private bool _iLead = true;

        private InputField _myInput;
        private InputField _oppInput;
        private Text _myPreview;
        private Text _oppPreview;
        private Text _statusText;
        private Text _resultText;
        private Text _timerText;
        private Button _meLeadButton;
        private Button _oppLeadButton;
        private Button _solveButton;
        private RectTransform _buttonBar;
        private Text[,] _cardLabels = new Text[2, CardUtil.RankCount];

        private bool _solving;
        private float _solveStart;
        private int _timeLimitMs = 180000;

        private readonly object _lock = new object();
        private SolveResult _pendingResult;
        private SolveResult _lastResult;

        private int _sampleIndex;

        // ---------------------------------------------------------------- 启动

        void Start()
        {
            EnsureEventSystem();
            LoadFont();

            // 残局记忆库：解过的局面下次直接命中（纯文本，几十条只有几 KB）
            try
            {
                EndgameMemory.SetPath(Application.persistentDataPath + "/endgame_memory.txt");
            }
            catch { }

            BuildUI();
            RefreshAll();
            SetStatus("输入双方手牌，选择先手，然后点「开始推算」。已记忆 "
                + EndgameMemory.Count + " 个残局。", DimColor);
        }

        /// <summary>场景里没挂脚本时也能自动起来，省得改场景。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrap()
        {
            if (FindObjectOfType<PokerSolverUI>() == null)
            {
                var go = new GameObject("PokerSolverUI");
                go.AddComponent<PokerSolverUI>();
            }
        }

        private void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private void LoadFont()
        {
            _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 24);
            if (_font == null)
            {
                _font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun", "Arial" }, 24);
            }
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch { }
            }
        }

        // ---------------------------------------------------------------- 构建界面

        private void BuildUI()
        {
            var canvasGo = new GameObject("SolverCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform root = (RectTransform)canvasGo.transform;

            // 全屏底色
            var bg = NewPanel(root, "Background", new Vector2(0, 0), Vector2.zero, BgColor);
            Stretch(bg);

            NewText(root, "Title", "斗地主 · 单挑残局解牌器", 34,
                new Vector2(60, 18), new Vector2(1100, 52), TextAnchor.MiddleLeft, GoldColor);

            NewText(root, "SubTitle", "明牌残局 · 完全搜索 · 给出必胜线或「无解」", 18,
                new Vector2(60, 66), new Vector2(1100, 30), TextAnchor.MiddleLeft, DimColor);

            // 左右面板
            RectTransform left = NewPanel(root, "MyPanel", new Vector2(60, 108), new Vector2(860, 566), PanelColor);
            RectTransform right = NewPanel(root, "OppPanel", new Vector2(1000, 108), new Vector2(860, 566), PanelColor);

            BuildHandPanel(left, 0, "我方手牌");
            BuildHandPanel(right, 1, "对方手牌");

            // 底部
            RectTransform bottom = NewPanel(root, "BottomPanel", new Vector2(60, 694), new Vector2(1800, 366), PanelColor);

            // 先手选择
            NewText(bottom, "LeadLabel", "牌权：", 22, new Vector2(24, 20), new Vector2(90, 44), TextAnchor.MiddleLeft, TextColor);
            _meLeadButton = NewButton(bottom, "LeadMe", "我方先出", new Vector2(110, 16), new Vector2(150, 48),
                ButtonHiColor, new Color(0.1f, 0.09f, 0.06f), () => SetLead(true));
            _oppLeadButton = NewButton(bottom, "LeadOpp", "对方先出", new Vector2(272, 16), new Vector2(150, 48),
                ButtonColor, TextColor, () => SetLead(false));

            // 按钮栏（模拟时可整体重建）
            _buttonBar = NewPanel(bottom, "ButtonBar", new Vector2(430, 12), new Vector2(1300, 58), new Color(0, 0, 0, 0));
            BuildActionButtons();

            _statusText = NewText(bottom, "Status", "", 18, new Vector2(24, 80), new Vector2(1300, 34),
                TextAnchor.MiddleLeft, DimColor);

            _timerText = NewText(bottom, "Timer", "", 20, new Vector2(1340, 80), new Vector2(300, 34),
                TextAnchor.MiddleLeft, GoldColor);

            // 结果滚动区
            RectTransform scrollRt = NewPanel(bottom, "ResultScroll", new Vector2(24, 122), new Vector2(1740, 228), new Color(0.10f, 0.11f, 0.15f, 1f));
            var scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            RectTransform viewport = NewPanel(scrollRt, "Viewport", new Vector2(0, 0), Vector2.zero, new Color(0, 0, 0, 0.001f));
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(Text), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewport, false);
            RectTransform content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 100);

            _resultText = contentGo.GetComponent<Text>();
            _resultText.font = _font;
            _resultText.fontSize = 20;
            _resultText.color = TextColor;
            _resultText.alignment = TextAnchor.UpperLeft;
            _resultText.supportRichText = true;
            _resultText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _resultText.verticalOverflow = VerticalWrapMode.Overflow;

            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
        }

        private void BuildHandPanel(RectTransform panel, int side, string title)
        {
            NewText(panel, "Title", title, 26, new Vector2(20, 14), new Vector2(400, 40),
                TextAnchor.MiddleLeft, side == 0 ? GreenColor : new Color(0.95f, 0.62f, 0.45f));

            var input = NewInput(panel, "Input", new Vector2(20, 62), new Vector2(820, 118));
            input.onValueChanged.AddListener((v) => OnTextChanged(side, v));

            Text preview = NewText(panel, "Preview", "", 18, new Vector2(20, 190), new Vector2(820, 34),
                TextAnchor.MiddleLeft, DimColor);

            if (side == 0)
            {
                _myInput = input;
                _myPreview = preview;
            }
            else
            {
                _oppInput = input;
                _oppPreview = preview;
            }

            NewText(panel, "Hint", "点牌面按钮：点一下 +1，长按 -1（电脑右键也可减）。也能直接粘贴：333 44 5 6 7", 16,
                new Vector2(20, 222), new Vector2(820, 28), TextAnchor.MiddleLeft, DimColor);

            // 15 个点数按钮，两行
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                int col = r % 8;
                int row = r / 8;
                float x = 20f + col * 104f;
                float y = 252f + row * 84f;

                int rank = r;
                Button b = NewButton(panel, "Card_" + side + "_" + r, "", new Vector2(x, y),
                    new Vector2(98, 76), ButtonColor, TextColor, null);

                AddPressHandler(b.gameObject, side, rank);
                _cardLabels[side, r] = b.GetComponentInChildren<Text>();
            }
        }

        // ---------------------------------------------------------------- UI 原子组件

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
        }

        private RectTransform NewPanel(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;

            go.GetComponent<Image>().color = color;
            return rt;
        }

        private Text NewText(Transform parent, string name, string content, int fontSize,
            Vector2 pos, Vector2 size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;

            Text t = go.GetComponent<Text>();
            t.font = _font;
            t.text = content;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private Button NewButton(Transform parent, string name, string label, Vector2 pos, Vector2 size,
            Color bg, Color fg, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = bg;

            Button btn = go.GetComponent<Button>();
            btn.targetGraphic = img;

            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.colorMultiplier = 1f;
            btn.colors = cb;

            if (onClick != null) btn.onClick.AddListener(() => onClick());

            Text text = NewText(rt, "Label", label, 20, Vector2.zero, size, TextAnchor.MiddleCenter, fg);
            Stretch(text.rectTransform);

            return btn;
        }

        private InputField NewInput(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);

            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = InputColor;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(rt, false);
            RectTransform trt = (RectTransform)textGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(12, 8);
            trt.offsetMax = new Vector2(-12, -8);

            Text text = textGo.GetComponent<Text>();
            text.font = _font;
            text.fontSize = 24;
            text.color = TextColor;
            text.alignment = TextAnchor.UpperLeft;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            InputField input = go.GetComponent<InputField>();
            input.textComponent = text;
            input.targetGraphic = img;
            input.lineType = InputField.LineType.MultiLineNewline;
            input.characterLimit = 120;
            return input;
        }

        /// <summary>
        /// 牌面按钮的按压处理：点一下 +1，长按 0.45 秒 -1。
        /// 手机上右键不可用，长按是唯一的减牌方式；电脑上右键仍可直接减。
        /// </summary>
        private void AddPressHandler(GameObject go, int side, int rank)
        {
            EventTrigger trigger = go.GetComponent<EventTrigger>();
            if (trigger == null) trigger = go.AddComponent<EventTrigger>();

            float downTime = -1f;

            var down = new EventTrigger.Entry();
            down.eventID = EventTriggerType.PointerDown;
            down.callback.AddListener((data) => { downTime = Time.unscaledTime; });
            trigger.triggers.Add(down);

            var up = new EventTrigger.Entry();
            up.eventID = EventTriggerType.PointerUp;
            up.callback.AddListener((data) =>
            {
                var ped = data as PointerEventData;

                if (ped != null && ped.button == PointerEventData.InputButton.Right)
                {
                    downTime = -1f;
                    OnCardClick(side, rank, -1);
                    return;
                }

                if (downTime < 0f)
                {
                    OnCardClick(side, rank, 1);
                    return;
                }

                float held = Time.unscaledTime - downTime;
                downTime = -1f;
                OnCardClick(side, rank, held >= 0.45f ? -1 : 1);
            });
            trigger.triggers.Add(up);
        }

        // ---------------------------------------------------------------- 交互

        // ---------------------------------------------------------------- 按钮栏

        private void ClearButtonBar()
        {
            if (_buttonBar == null) return;
            for (int i = _buttonBar.childCount - 1; i >= 0; i--)
            {
                GameObject child = _buttonBar.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
        }

        private void BuildActionButtons()
        {
            ClearButtonBar();
            float x = 0f;

            _solveButton = NewButton(_buttonBar, "SolveBtn", "开始推算", new Vector2(x, 0), new Vector2(180, 56),
                ButtonHiColor, new Color(0.1f, 0.09f, 0.06f), StartSolve);
            x += 192f;

            NewButton(_buttonBar, "SimBtn", "模拟对抗", new Vector2(x, 0), new Vector2(150, 56),
                new Color(0.26f, 0.46f, 0.78f), TextColor, StartSimulation);
            x += 162f;

            NewButton(_buttonBar, "ClearBtn", "清空", new Vector2(x, 0), new Vector2(100, 50),
                ButtonColor, TextColor, ClearAll);
            x += 112f;

            NewButton(_buttonBar, "SwapBtn", "交换", new Vector2(x, 0), new Vector2(100, 50),
                ButtonColor, TextColor, SwapSides);
            x += 112f;

            NewButton(_buttonBar, "SampleBtn", "示例残局", new Vector2(x, 0), new Vector2(130, 50),
                ButtonColor, TextColor, LoadSample);
        }

        // ---------------------------------------------------------------- 模拟对抗

        private bool _simActive;
        private string _savedResultText;
        private DDSolver _simSolver;
        private SolveResult _simRootResult;
        private ulong _lastSolveMy;
        private ulong _lastSolveOpp;
        private bool _lastSolveLead;
        private ulong _simMe;
        private ulong _simOpp;
        private Move _simPrev;
        private bool _simMyTurn;
        private readonly List<Move> _simBuf = new List<Move>(64);

        // 两段式：后台求解，界面先显示「计算中」，算完再刷新，避免卡死十几秒
        private volatile bool _simBusy;
        private volatile SolveResult _simReady;
        private System.Diagnostics.Stopwatch _simTimer;
        private int _simGen;
        private float _simNextTick;
        private bool _simBusyUiBuilt;

        // 模拟对抗的候选按钮分页（手机屏幕放不下太多）
        private int _simPage;
        private const int SimPerPage = 4;

        private sealed class SimItem
        {
            public string Label;
            public Action Act;
            public Color Bg;
            public float Width;
        }

        private void StartSimulation()
        {
            if (_solving) return;

            if (_myHand == 0UL || _oppHand == 0UL)
            {
                SetStatus("⚠ 先输入双方手牌再模拟。", RedColor);
                return;
            }

            _simActive = true;
            _savedResultText = _resultText.text;   // 退出时原样恢复，不丢已显示的局面
            _simMe = _myHand;
            _simOpp = _oppHand;
            _simPrev = Move.Pass;
            _simMyTurn = _iLead;
            _simSolver = new DDSolver();
            _simBusy = false;
            _simReady = null;
            _simPage = 0;
            _simGen++;

            // 如果刚推算过同样的牌面，直接复用上次结果，避免进入模拟再等几十秒
            _simRootResult = (_lastResult != null
                && _lastResult.Completed
                && _lastSolveMy == _myHand
                && _lastSolveOpp == _oppHand
                && _lastSolveLead == _iLead)
                ? _lastResult
                : null;

            RefreshSimulation();
        }

        private void ExitSimulation()
        {
            _simActive = false;
            _simBusy = false;
            _simReady = null;
            _simGen++;          // 作废正在跑的后台线程
            _simSolver = null;
            BuildActionButtons();
            RefreshAll();
            SetStatus("已退出模拟。", DimColor);
            _resultText.text = _savedResultText ?? "";
        }

        private void RefreshSimulation()
        {
            if (!_simActive) return;

            ClearButtonBar();

            if (_simMe == 0UL)
            {
                ShowSimEnd("🎉 我方手牌出完 —— 我方赢！");
                return;
            }
            if (_simOpp == 0UL)
            {
                ShowSimEnd("对方手牌出完 —— 对方赢。");
                return;
            }

            ulong cur = _simMyTurn ? _simMe : _simOpp;
            ulong oth = _simMyTurn ? _simOpp : _simMe;

            SolveResult r = null;

            // 后台算完了：取结果
            if (_simReady != null)
            {
                r = _simReady;
                _simReady = null;
                _simBusy = false;
            }
            // 根局面且刚推算过：直接复用，省掉几十秒
            else if (_simRootResult != null && _simPrev.IsPass
                && _simMe == _myHand && _simOpp == _oppHand && _simMyTurn == _iLead)
            {
                r = _simRootResult;
                _simRootResult = null;
            }

            if (r == null)
            {
                // 没结果：后台开算，界面先显示进度，绝不冻结
                if (!_simBusy) StartSimSearch(cur, oth, _simPrev);
                ShowSimBusy();
                return;
            }

            if (!string.IsNullOrEmpty(r.Error))
            {
                ShowSimEnd("模拟求解出错：" + r.Error);
                return;
            }

            bool meCanWin = false;
            bool hasAnswer = r.Completed;
            if (hasAnswer) meCanWin = _simMyTurn ? r.CanWin : !r.CanWin;

            var sb = new System.Text.StringBuilder();
            sb.Append("台面：").Append(_simPrev.IsPass ? "（自由出牌）" : MoveUtil.Describe(_simPrev)).Append("\n");
            sb.Append("我方：").Append(CardUtil.ToDisplayString(_simMe)).Append("（").Append(CardUtil.TotalCount(_simMe)).Append(" 张）\n");
            sb.Append("对方：").Append(CardUtil.ToDisplayString(_simOpp)).Append("（").Append(CardUtil.TotalCount(_simOpp)).Append(" 张）\n\n");

            if (hasAnswer)
            {
                if (_simMyTurn)
                    sb.Append(meCanWin
                        ? "<color=#77D98A>当前局面：我方必胜</color>"
                        : "<color=#F26B6B>当前局面：我方必败 —— 对方有必胜策略</color>");
                else
                    sb.Append(meCanWin
                        ? "<color=#77D98A>当前局面：我方必胜（对方怎么走都翻不了盘）</color>"
                        : "<color=#F26B6B>当前局面：我方必败 —— 对方下一步就能赢</color>");
            }
            else
                sb.Append("<color=#F2B33D>当前局面：搜索超时，仅供参考</color>");

            // 无论胜负都要把「赢家会怎么走」摆出来，否则用户只知道输，不知道为什么输。
            if (hasAnswer && !meCanWin && !_simMyTurn)
            {
                if (r.FirstMove.IsPass && !_simPrev.IsPass)
                    sb.Append("\n<color=#E8C46A>对方的制胜走法：</color>过（让你继续出，然后反制）");
                else if (r.FirstMove.Type != MoveType.None)
                    sb.Append("\n<color=#E8C46A>对方的制胜首手：</color>").Append(MoveUtil.Describe(r.FirstMove));
            }
            else if (hasAnswer && meCanWin && _simMyTurn && r.FirstMove.Type != MoveType.None)
            {
                sb.Append("\n<color=#E8C46A>我方制胜首手：</color>").Append(MoveUtil.Describe(r.FirstMove));
            }
            else if (hasAnswer && !meCanWin && _simMyTurn)
            {
                sb.Append("\n<color=#F2B33D>我方这一步怎么走都会输 —— 随便走一手，下一步会显示对方的制胜回应。</color>");
            }

            sb.Append("\n").Append(_simMyTurn ? "轮到【我方】。" : "轮到【对方】，请选择对方的走法：");

            _resultText.text = sb.ToString();
            SetStatus(_simActive ? "模拟模式：按提示点按钮。" : "", GoldColor);

            // 先收集本回合所有可点的按钮，再分页渲染 —— 手机屏幕一排放不下太多
            var items = new List<SimItem>();

            if (_simMyTurn)
            {
                Move suggest = Move.Pass;
                bool hasSuggest = hasAnswer && r.FirstMove.Type != MoveType.None;
                if (hasSuggest)
                {
                    suggest = r.FirstMove;
                    items.Add(new SimItem
                    {
                        Label = "建议：" + ShortLabel(suggest),
                        Act = () => ApplySimMove(suggest),
                        Bg = new Color(0.62f, 0.48f, 0.18f),
                        Width = 168f
                    });
                }

                _simBuf.Clear();
                if (_simPrev.IsPass) MoveGen.GenerateAll(_simMe, _simBuf);
                else MoveGen.GenerateBeats(_simMe, _simPrev, _simBuf);
                MoveGen.SortRootForSearch(_simBuf, _simMe, _simOpp);

                // 有建议时也把其它走法全部列出来，用户要能自己试探
                for (int i = 0; i < _simBuf.Count; i++)
                {
                    Move m = _simBuf[i];
                    if (hasSuggest && m.Cards == suggest.Cards && m.Type == suggest.Type) continue;
                    items.Add(new SimItem
                    {
                        Label = ShortLabel(m),
                        Act = () => ApplySimMove(m),
                        Bg = ButtonColor,
                        Width = 116f
                    });
                }
            }
            else
            {
                // 对方回合：先把「对方怎么赢」摆在最前面，用户才知道为什么输
                bool oppHasBest = hasAnswer && !meCanWin && r.FirstMove.Type != MoveType.None;
                if (oppHasBest)
                {
                    Move best = r.FirstMove;
                    items.Add(new SimItem
                    {
                        Label = "对方制胜：" + ShortLabel(best),
                        Act = () => ApplySimMove(best),
                        Bg = new Color(0.78f, 0.35f, 0.25f),
                        Width = 176f
                    });
                }

                _simBuf.Clear();
                if (_simPrev.IsPass) MoveGen.GenerateAll(_simOpp, _simBuf);
                else MoveGen.GenerateBeats(_simOpp, _simPrev, _simBuf);
                MoveGen.SortRootForSearch(_simBuf, _simOpp, _simMe);

                for (int i = 0; i < _simBuf.Count; i++)
                {
                    Move m = _simBuf[i];
                    if (oppHasBest && m.Cards == r.FirstMove.Cards && m.Type == r.FirstMove.Type) continue;
                    items.Add(new SimItem
                    {
                        Label = ShortLabel(m),
                        Act = () => ApplySimMove(m),
                        Bg = ButtonColor,
                        Width = 116f
                    });
                }

                if (!_simPrev.IsPass)
                {
                    items.Add(new SimItem
                    {
                        Label = "过",
                        Act = () => ApplySimMove(Move.Pass),
                        Bg = new Color(0.42f, 0.32f, 0.32f),
                        Width = 108f
                    });
                }
            }

            // 分页
            int total = items.Count;
            int pages = Math.Max(1, (total + SimPerPage - 1) / SimPerPage);
            if (_simPage >= pages) _simPage = pages - 1;
            if (_simPage < 0) _simPage = 0;

            float x = 0f;
            if (pages > 1)
            {
                NewButton(_buttonBar, "SimPrev", "◀", new Vector2(x, 0), new Vector2(52, 54),
                    ButtonColor, TextColor, () => { if (_simPage > 0) { _simPage--; RefreshSimulation(); } });
                x += 56f;

                NewText(_buttonBar, "SimPageLbl", (_simPage + 1) + "/" + pages, 20,
                    new Vector2(x, 0), new Vector2(72, 54), TextAnchor.MiddleCenter, TextColor);
                x += 76f;

                NewButton(_buttonBar, "SimNext", "▶", new Vector2(x, 0), new Vector2(52, 54),
                    ButtonColor, TextColor, () => { if (_simPage < pages - 1) { _simPage++; RefreshSimulation(); } });
                x += 60f;
            }

            int startIdx = _simPage * SimPerPage;
            int endIdx = Math.Min(startIdx + SimPerPage, total);
            for (int i = startIdx; i < endIdx; i++)
            {
                SimItem it = items[i];
                AddSimButton(it.Label, x, it.Width, it.Act, it.Bg);
                x += it.Width + 8f;
            }

            // 自由出牌：直接输入任意牌面（可以拆炸弹），程序只检查合法性
            InputField freeInput = NewInput(_buttonBar, "FreeInput", new Vector2(760, 2), new Vector2(180, 50));
            freeInput.lineType = InputField.LineType.SingleLine;
            freeInput.text = "";
            freeInput.characterLimit = 40;

            AddSimButton("出", 946, () => ApplyFreeMove(freeInput.text), new Color(0.55f, 0.42f, 0.16f));
            AddSimButton("退出", 1046, ExitSimulation, new Color(0.45f, 0.18f, 0.18f));
        }

        /// <summary>后台线程跑这一步的求解，结果通过 _simReady 交回主线程。</summary>
        private void StartSimSearch(ulong cur, ulong oth, Move prev)
        {
            _simBusy = true;
            _simBusyUiBuilt = false;   // 下一帧重建「计算中」界面
            _simTimer = System.Diagnostics.Stopwatch.StartNew();
            _simNextTick = 0f;
            int gen = ++_simGen;

            bool simMobile = Application.isMobilePlatform;
            int simThreads; int simMaxLog; long simMem;
            PickDeviceProfile(simMobile, out simThreads, out simMaxLog, out simMem);
            int limit = simMobile ? 180000 : 120000;

            var th = new Thread(() =>
            {
                SolveResult result;
                try
                {
                    var pr = ParallelSolver.SolveWithPrev(cur, oth, prev,
                        new ParallelSolveOptions
                        {
                            TimeLimitMs = limit,
                            ThreadCount = simThreads,
                            MaxTableLog2 = simMaxLog,
                            TotalMemoryLimitBytes = simMem
                        });

                    result = new SolveResult
                    {
                        Completed = pr.Completed,
                        CanWin = pr.CurrentWins,
                        TimedOut = pr.TimedOut,
                        ElapsedMs = pr.ElapsedMs,
                        Nodes = pr.TotalNodes,
                        States = 0,
                        Hits = 0,
                        Line = new List<string>(),
                        FirstMove = pr.FirstMove
                    };
                }
                catch (Exception e)
                {
                    result = new SolveResult { Completed = false, Error = e.Message };
                }

                // 代数变了说明用户已经退出/换了局面，丢弃这批结果
                if (gen == _simGen) _simReady = result;
            });
            th.IsBackground = true;
            th.Start();
        }

        private void ShowSimBusy()
        {
            // 只在第一次建按钮：否则每 0.5 秒重建会把用户正在输入的自由出牌框清掉
            if (!_simBusyUiBuilt)
            {
                ClearButtonBar();
                AddSimButton("退出", 0f, ExitSimulation, new Color(0.45f, 0.18f, 0.18f));
                _simBusyUiBuilt = true;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("台面：").Append(_simPrev.IsPass ? "（自由出牌）" : MoveUtil.Describe(_simPrev)).Append("\n");
            sb.Append("我方：").Append(CardUtil.ToDisplayString(_simMe)).Append("（").Append(CardUtil.TotalCount(_simMe)).Append(" 张）\n");
            sb.Append("对方：").Append(CardUtil.ToDisplayString(_simOpp)).Append("（").Append(CardUtil.TotalCount(_simOpp)).Append(" 张）\n\n");

            double sec = _simTimer != null ? _simTimer.Elapsed.TotalSeconds : 0.0;
            sb.Append("<color=#F2B33D>正在计算这一手… 已用 ").Append(sec.ToString("F1")).Append(" 秒</color>\n");
            sb.Append("<color=#9FB0C8>要证明「对方能赢 / 不能赢」，必须穷举对手所有抵抗，大局面通常几秒到几十秒。</color>\n");
            sb.Append(_simMyTurn ? "轮到【我方】。" : "轮到【对方】。");

            _resultText.text = sb.ToString();
            SetStatus("模拟计算中…", GoldColor);
            AddSimButton("退出", 0f, ExitSimulation, new Color(0.45f, 0.18f, 0.18f));
        }

        private void ApplyFreeMove(string text)
        {
            if (!_simActive) return;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                SetStatus("在输入框里写要出的牌（如 3334），或点「过」。", RedColor);
                return;
            }

            string trimmed = text.Trim();
            if (trimmed == "过" || trimmed == "不要" || trimmed == "pass" || trimmed == "PASS")
            {
                if (_simPrev.IsPass)
                {
                    SetStatus("自由出牌时必须出牌，不能过。", RedColor);
                    return;
                }
                ApplySimMove(Move.Pass);
                return;
            }

            ulong cards;
            string error;
            if (!CardUtil.TryParse(trimmed, out cards, out error))
            {
                SetStatus("解析失败：" + error, RedColor);
                return;
            }

            ulong hand = _simMyTurn ? _simMe : _simOpp;

            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (CardUtil.GetCount(cards, r) > CardUtil.GetCount(hand, r))
                {
                    SetStatus("这些牌不在" + (_simMyTurn ? "我方" : "对方") + "手牌里。", RedColor);
                    return;
                }
            }

            Move move;
            if (!TryIdentifyMove(cards, out move))
            {
                SetStatus("不是合法的牌型组合。", RedColor);
                return;
            }

            if (!_simPrev.IsPass && !MoveUtil.CanBeat(move, _simPrev))
            {
                SetStatus("压不过台面的「" + MoveUtil.Describe(_simPrev) + "」。", RedColor);
                return;
            }

            ApplySimMove(move);
        }

        /// <summary>
        /// 把一组牌识别成牌型：把 cards 当手牌生成全部牌型，
        /// 若存在恰好用完这组牌的牌型，就是合法的。
        /// </summary>
        private static bool TryIdentifyMove(ulong cards, out Move move)
        {
            move = Move.Pass;
            if (cards == 0UL) return false;

            var buf = new List<Move>(32);
            MoveGen.GenerateAll(cards, buf);

            for (int i = 0; i < buf.Count; i++)
            {
                if (buf[i].Cards == cards)
                {
                    move = buf[i];
                    return true;
                }
            }
            return false;
        }

        private string ShortLabel(in Move m)
        {
            if (m.IsPass) return "过";
            return CardUtil.ToDisplayString(m.Cards);
        }

        private void AddSimButton(string label, float x, Action onClick, Color bg)
        {
            AddSimButton(label, x, 108f, onClick, bg);
        }

        private void AddSimButton(string label, float x, float width, Action onClick, Color bg)
        {
            // 模拟按钮统一加亮，确保一眼能看到
            Color shown = bg;
            if (bg == ButtonColor) shown = new Color(0.30f, 0.44f, 0.66f);
            NewButton(_buttonBar, "Sim_" + label, label, new Vector2(x, 0), new Vector2(width, 54),
                shown, TextColor, onClick);
        }

        private void ApplySimMove(Move m)
        {
            if (_simMyTurn)
            {
                if (!m.IsPass) _simMe -= m.Cards;
            }
            else
            {
                if (!m.IsPass) _simOpp -= m.Cards;
            }

            _simPrev = m;
            _simMyTurn = !_simMyTurn;
            _simPage = 0;      // 新局面：候选变了，回到第一页
            RefreshSimulation();
        }

        private void ShowSimEnd(string message)
        {
            ClearButtonBar();
            _resultText.text = message;
            SetStatus("模拟结束。", GoldColor);
            AddSimButton("退出模拟", 0f, ExitSimulation, new Color(0.45f, 0.18f, 0.18f));
        }

        private void SetLead(bool meLead)
        {
            _iLead = meLead;
            _meLeadButton.GetComponent<Image>().color = meLead ? ButtonHiColor : ButtonColor;
            _oppLeadButton.GetComponent<Image>().color = meLead ? ButtonColor : ButtonHiColor;

            Text mt = _meLeadButton.GetComponentInChildren<Text>();
            Text ot = _oppLeadButton.GetComponentInChildren<Text>();
            if (mt != null) mt.color = meLead ? new Color(0.1f, 0.09f, 0.06f) : TextColor;
            if (ot != null) ot.color = meLead ? TextColor : new Color(0.1f, 0.09f, 0.06f);
        }

        private void OnCardClick(int side, int rank, int delta)
        {
            if (_solving) return;

            ulong hand = side == 0 ? _myHand : _oppHand;
            ulong other = side == 0 ? _oppHand : _myHand;

            int cur = CardUtil.GetCount(hand, rank);
            int otherCount = CardUtil.GetCount(other, rank);
            int maxTotal = rank >= CardUtil.SmallJoker ? 1 : 4;
            int maxMine = maxTotal - otherCount;

            int next = cur + delta;
            if (next < 0) next = 0;
            if (next > maxMine) next = maxMine;
            if (next == cur) return;

            hand += ((ulong)(next - cur)) << (rank * 4);
            if (side == 0) _myHand = hand; else _oppHand = hand;

            RefreshSide(side);
            RefreshPreview();
            SetStatus("", DimColor);
        }

        private void OnTextChanged(int side, string value)
        {
            if (_solving) return;

            ulong parsed;
            string error;
            if (!CardUtil.TryParse(value, out parsed, out error))
            {
                if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
                {
                    // 清空输入 -> 手牌也清空
                    if (side == 0) _myHand = 0; else _oppHand = 0;
                    RefreshPreview();
                    RefreshSide(side);
                }
                else
                {
                    if (side == 0) _myPreview.text = "⚠ " + error;
                    else _oppPreview.text = "⚠ " + error;
                    _myPreview.color = RedColor;
                    _oppPreview.color = RedColor;
                }
                return;
            }

            // 双方同一点数的总数不能超过一副牌
            ulong other = side == 0 ? _oppHand : _myHand;
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                int total = CardUtil.GetCount(parsed, r) + CardUtil.GetCount(other, r);
                int max = r >= CardUtil.SmallJoker ? 1 : 4;
                if (total > max)
                {
                    if (side == 0) _myPreview.text = "⚠ " + CardUtil.RankNames[r] + " 和对方加起来超过 " + max + " 张";
                    else _oppPreview.text = "⚠ " + CardUtil.RankNames[r] + " 和我方加起来超过 " + max + " 张";
                    _myPreview.color = RedColor;
                    _oppPreview.color = RedColor;
                    return;
                }
            }

            if (side == 0) _myHand = parsed; else _oppHand = parsed;
            RefreshPreview();
            RefreshSide(side);
        }

        private void RefreshAll()
        {
            RefreshSide(0);
            RefreshSide(1);
            RefreshPreview();
            SetLead(_iLead);
        }

        private void RefreshSide(int side)
        {
            ulong hand = side == 0 ? _myHand : _oppHand;
            InputField input = side == 0 ? _myInput : _oppInput;

            if (input != null)
            {
                string text = CardUtil.ToDisplayString(hand);
                if (input.text != text) input.text = text;
            }

            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                Text label = _cardLabels[side, r];
                if (label == null) continue;

                int n = CardUtil.GetCount(hand, r);
                label.text = CardUtil.RankNames[r] + "\n" + (n > 0 ? "×" + n : "·");
                label.color = n > 0 ? GoldColor : new Color(0.55f, 0.58f, 0.63f, 1f);
            }
        }

        private void RefreshPreview()
        {
            int myCount = CardUtil.TotalCount(_myHand);
            int oppCount = CardUtil.TotalCount(_oppHand);

            _myPreview.color = DimColor;
            _oppPreview.color = DimColor;
            _myPreview.text = myCount + " 张";
            _oppPreview.text = oppCount + " 张";
        }

        private void ClearAll()
        {
            if (_solving) return;
            _myHand = 0;
            _oppHand = 0;
            _lastResult = null;
            _resultText.text = "";
            _timerText.text = "";
            RefreshAll();
            SetStatus("已清空。", DimColor);
        }

        private void SwapSides()
        {
            if (_solving) return;
            ulong t = _myHand;
            _myHand = _oppHand;
            _oppHand = t;
            RefreshAll();
            SetStatus("已交换双方手牌。", DimColor);
        }

        private void LoadSample()
        {
            if (_solving) return;

            string my = "", opp = "";
            bool lead = true;

            switch (_sampleIndex % 4)
            {
                case 0:
                    my = "333 44"; opp = "55 66"; lead = true;
                    SetStatus("示例 1：三带一后剩一对，正常能走完。", DimColor);
                    break;
                case 1:
                    my = "3 4"; opp = "2 小王"; lead = true;
                    SetStatus("示例 2：对方两张都是大牌，通常是死局。", DimColor);
                    break;
                case 2:
                    my = "334455"; opp = "6677 8"; lead = true;
                    SetStatus("示例 3：六张连对 vs 连对加单张，考验先手拆牌。", DimColor);
                    break;
                default:
                    my = "3 3 3 4 4 4 5 5 5"; opp = "6 6 6 7 7 7 8"; lead = false;
                    SetStatus("示例 4：双方都有飞机，对方先手。", DimColor);
                    break;
            }

            _sampleIndex++;

            ulong m, o;
            string err;
            CardUtil.TryParse(my, out m, out err);
            CardUtil.TryParse(opp, out o, out err);

            _myHand = m;
            _oppHand = o;
            _iLead = lead;

            _lastResult = null;
            _resultText.text = "";
            _timerText.text = "";
            RefreshAll();
        }

        private void SetStatus(string message, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.color = color;
        }

        // ---------------------------------------------------------------- 求解

        /// <summary>
        /// 按设备能力挑并行配置。
        /// 置换表大小是性能命门：表小一倍，节点数往往多好几倍（重复搜索），
        /// 所以内存够的机器尽量把表开大。手机按实际 RAM 分三档。
        /// </summary>
        private static void PickDeviceProfile(bool isMobile, out int threads, out int maxLog, out long totalMem)
        {
            if (!isMobile)
            {
                // PC：静态分片 4 线程实测最快（6/8/12 线程会被内存带宽拖慢，动态队列也更快退化）
                threads = Math.Min(4, Math.Max(1, SystemInfo.processorCount - 1));
                // 实测 log24/25/26 在典型残局上耗时几乎相同（节点数完全一致），
                // 因为搜索量远小于表容量。留 25 应对更大局面，不再无脑开 26 吃内存。
                maxLog = 25;              // 2^25 条 ≈ 335MB/线程
                totalMem = 2_000_000_000L;
                return;
            }

            // 手机：留 2 个核给系统/渲染，最多 4 个工作线程
            threads = Math.Min(4, Math.Max(2, SystemInfo.processorCount - 2));

            int ramMb = SystemInfo.systemMemorySize;
            if (ramMb >= 6000) { maxLog = 24; totalMem = 1_200_000_000L; }       // 2^24 ≈ 168MB/线程
            else if (ramMb >= 4000) { maxLog = 23; totalMem = 900_000_000L; }
            else { maxLog = 22; totalMem = 600_000_000L; }
        }

        private static SolveResult SolveParallelCore(ulong my, ulong opp, bool lead,
            int limit, int threads, int maxLog, long totalMem)
        {
            ulong first = lead ? my : opp;
            ulong second = lead ? opp : my;

            var pr = ParallelSolver.Solve(first, second, new ParallelSolveOptions
            {
                TimeLimitMs = limit,
                ThreadCount = threads,
                MaxTableLog2 = maxLog,
                TotalMemoryLimitBytes = totalMem
            });

            bool myCanWin = lead ? pr.CurrentWins : !pr.CurrentWins;

            var r = new SolveResult
            {
                Completed = pr.Completed,
                CanWin = myCanWin,
                TimedOut = pr.TimedOut,
                ElapsedMs = pr.ElapsedMs,
                Nodes = pr.TotalNodes,
                States = 0,
                Hits = 0,
                Line = new List<string>()
            };

            // 只有我方先手时，首手才是我方的走法
            if (lead && pr.Completed && pr.CurrentWins)
                r.FirstMove = pr.FirstMove;

            // 定式命中（搜索 0 节点）时补一句来由
            if (pr.Completed && pr.TotalNodes == 0)
            {
                EndgameBook.Verdict v = EndgameBook.Judge(first, second, Move.Pass);
                if (v.Hit) r.Line.Add("【定式】" + v.Name + "：" + v.Detail);
            }

            return r;
        }

        private void StartSolve()
        {
            if (_solving) return;

            if (_myHand == 0UL)
            {
                SetStatus("⚠ 我方还没有牌。", RedColor);
                return;
            }
            if (_oppHand == 0UL)
            {
                SetStatus("⚠ 对方还没有牌。", RedColor);
                return;
            }

            // 双方同点数总数校验
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                int total = CardUtil.GetCount(_myHand, r) + CardUtil.GetCount(_oppHand, r);
                int max = r >= CardUtil.SmallJoker ? 1 : 4;
                if (total > max)
                {
                    SetStatus("⚠ 双方的 " + CardUtil.RankNames[r] + " 加起来有 " + total + " 张，超过一副牌。", RedColor);
                    return;
                }
            }

            int myCount = CardUtil.TotalCount(_myHand);
            int oppCount = CardUtil.TotalCount(_oppHand);

            // 残局记忆库：完全相同局面直接命中。
            // 只做精确匹配 —— 差一张牌结论就可能相反，泛化等于编答案。
            EndgameMemory.Entry memo;
            if (EndgameMemory.TryGet(_myHand, _oppHand, _iLead, Move.Pass, out memo))
            {
                _lastSolveMy = _myHand;
                _lastSolveOpp = _oppHand;
                _lastSolveLead = _iLead;

                // 命中时把上次的完整推演线原样带出来，首手也一起显示
                var cachedLine = new List<string>();
                cachedLine.Add("【记忆】命中 " + memo.Hits + " 次，首次 " + memo.SolvedAt + "（直接复用上次结果）");
                if (memo.Line != null && memo.Line.Count > 0)
                    cachedLine.AddRange(memo.Line);

                var cached = new SolveResult
                {
                    Completed = true,
                    CanWin = memo.Win,
                    TimedOut = false,
                    ElapsedMs = 0,
                    Nodes = 0,
                    States = 0,
                    Hits = 0,
                    FirstMove = memo.First,
                    Line = cachedLine
                };
                _lastResult = cached;
                ShowResult(cached);
                SetStatus("命中记忆库，直接给出结论。", GoldColor);
                return;
            }

            _solving = true;
            _pendingResult = null;
            _solveStart = Time.realtimeSinceStartup;
            _solveButton.interactable = false;
            _resultText.text = "";
            _timerText.text = "";
            SetStatus("计算中… 我方 " + myCount + " 张，对方 " + oppCount + " 张，"
                + (_iLead ? "我方先手" : "对方先手") + "。", GoldColor);

            ulong my = _myHand;
            ulong opp = _oppHand;
            bool lead = _iLead;
            int limit = _timeLimitMs;

            _lastSolveMy = my;
            _lastSolveOpp = opp;
            _lastSolveLead = lead;

            // 大局面走并行：PC 4 线程 + 64M 表；手机按 RAM 自适应（表越大越快）
            bool useParallel = (myCount + oppCount) >= 22;
            bool isMobile = Application.isMobilePlatform;
            int parallelThreads; int parallelMaxLog; long parallelTotalMem;
            PickDeviceProfile(isMobile, out parallelThreads, out parallelMaxLog, out parallelTotalMem);

            Thread thread = new Thread(() =>
            {
                SolveResult r;
                try
                {
                    if (useParallel)
                    {
                        r = SolveParallelCore(my, opp, lead, limit, parallelThreads, parallelMaxLog, parallelTotalMem);
                    }
                    else
                    {
                        var solver = new DDSolver();
                        r = solver.Solve(my, opp, lead, new SolveOptions { TimeLimitMs = limit });
                    }
                }
                catch (Exception e)
                {
                    r = new SolveResult { Completed = false, Error = e.ToString() };
                }

                lock (_lock)
                {
                    _pendingResult = r;
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void Update()
        {
            // 模拟对抗的两段式：后台算完 → 立刻重绘；没算完 → 每 0.5 秒刷一次已用时间
            if (_simActive && _simBusy)
            {
                if (_simReady != null)
                {
                    RefreshSimulation();
                }
                else if (Time.unscaledTime >= _simNextTick)
                {
                    _simNextTick = Time.unscaledTime + 0.5f;
                    ShowSimBusy();
                }
            }

            if (!_solving) return;

            float elapsed = Time.realtimeSinceStartup - _solveStart;
            _timerText.text = "计算中 " + elapsed.ToString("F1") + " 秒";

            SolveResult result = null;
            lock (_lock)
            {
                if (_pendingResult != null)
                {
                    result = _pendingResult;
                    _pendingResult = null;
                }
            }

            if (result != null)
            {
                _solving = false;
                _solveButton.interactable = true;
                _lastResult = result;

                // 记忆库：只有真正得出结论才记录（超时/超内存不记，避免污染）
                if (result.Completed && !result.TimedOut && !result.OutOfMemory
                    && string.IsNullOrEmpty(result.Error))
                {
                    EndgameMemory.Put(_lastSolveMy, _lastSolveOpp, _lastSolveLead, Move.Pass,
                        result.CanWin, result.FirstMove, result.Nodes, result.ElapsedMs, result.Line);
                    EndgameMemory.Save();
                }

                ShowResult(result);
                return;
            }

            // 保险丝：算法自身有超时，这里再兜一层（并行收尾要额外几秒）
            if (elapsed > (_timeLimitMs / 1000f) + 20f)
            {
                _solving = false;
                _solveButton.interactable = true;
                _timerText.text = "";
                SetStatus("⚠ 计算线程无响应，已中断显示（请重试）。", RedColor);
            }
        }

        private void ShowResult(SolveResult r)
        {
            _timerText.text = "";

            if (!string.IsNullOrEmpty(r.Error))
            {
                _resultText.text = "<color=#F26B6B>✘ 计算出错：</color>\n" + r.Error;
                SetStatus("计算出错。", RedColor);
                return;
            }

            if (r.OutOfMemory)
            {
                _resultText.text =
                    "<color=#F26B6B>⚠ 内存不足，搜索已停止</color>\n\n" +
                    "已搜索：" + r.Nodes.ToString("N0") + " 个节点 / " + r.States.ToString("N0") + " 个局面\n" +
                    "耗时：" + (r.ElapsedMs / 1000.0).ToString("F2") + " 秒\n\n" +
                    "这局太大，超出了设备内存。可以尝试缩短手牌，或换内存更大的设备。";

                SetStatus("内存不足：未得出确定结论。", RedColor);
                return;
            }

            if (r.TimedOut)
            {
                _resultText.text =
                    "<color=#F2B33D>⏱ 未能在时限内证明</color>\n\n" +
                    "已搜索：" + r.Nodes.ToString("N0") + " 个节点 / " + r.States.ToString("N0") + " 个局面\n" +
                    "耗时：" + (r.ElapsedMs / 1000.0).ToString("F2") + " 秒\n\n" +
                    "这不代表无解 —— 只是还没穷举完。可以缩短手牌或放宽时限再试。";

                SetStatus("超时：未得出确定结论。", new Color(0.95f, 0.72f, 0.32f));
                return;
            }

            var sb = new System.Text.StringBuilder();

            if (r.CanWin)
            {
                sb.AppendLine("<color=#77D98A>✔ 我方必胜</color>");
            }
            else
            {
                sb.AppendLine("<color=#F26B6B>✘ 无解（对方必有必胜策略）</color>");
                sb.AppendLine("<color=#9FB0C8>想知道对方怎么赢？点「模拟对抗」，随便走一手，程序会标出对方的制胜回应。</color>");
            }

            sb.AppendLine();
            sb.AppendLine("耗时：" + (r.ElapsedMs / 1000.0).ToString(r.ElapsedMs < 1000 ? "F3" : "F2") + " 秒"
                + "　·　搜索 " + r.Nodes.ToString("N0") + " 节点"
                + "　·　局面 " + r.States.ToString("N0")
                + "　·　缓存命中 " + r.Hits.ToString("N0"));

            // 局面特征：帮用户理解「为什么赢/输」，不给结论
            {
                EndgameBook.Profile prof = EndgameBook.Analyze(_myHand, _oppHand, _iLead);
                sb.AppendLine();
                sb.AppendLine("<color=#9FB0C8>局面特征：</color>");
                sb.AppendLine(EndgameBook.Describe(prof));
            }

            // 首手独立显示：不管有没有推演线、是不是命中记忆，都必须给「怎么走」
            if (r.CanWin && r.FirstMove.Type != MoveType.None)
            {
                sb.AppendLine();
                sb.AppendLine("<color=#E8C46A>建议首手：</color>" + MoveUtil.Describe(r.FirstMove));
            }

            if (r.Line.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(r.CanWin
                    ? "<color=#E8C46A>推演主线：</color>"
                    : "<color=#E8C46A>对方的取胜线（供核对）：</color>");
                for (int i = 0; i < r.Line.Count; i++)
                {
                    sb.AppendLine("  " + (i + 1) + ". " + r.Line[i]);
                }
            }

            _resultText.text = sb.ToString();

            if (r.CanWin)
                SetStatus("完成：我方必胜，推演主线已在下方。", GreenColor);
            else
                SetStatus("完成：无解 —— 对方存在必胜策略。", RedColor);
        }
    }
}
