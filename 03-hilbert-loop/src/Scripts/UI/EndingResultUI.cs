using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 四大结局的统一悬浮结算层。
    ///
    /// - A/B：全屏监控画面推演当前存活名单的「残缺循环」，随后画面定帧；
    /// - C：由 EndingDirector 播放白光 + 卧室苏醒动画；
    /// - D：由 EndingDirector 播放全员摘面具，随后监控画面碎裂花屏；
    /// - 最后统一叠加悬浮结算面板：再来一局 / 返回主菜单 / 制作人员 / 设置。
    ///
    /// 全程不切换场景、不黑屏。
    /// </summary>
    public class EndingResultUI : MonoBehaviour
    {
        public static EndingResultUI Instance { get; private set; }

        Canvas _feedCanvas;
        Canvas _panelCanvas;
        RawImage _feed;
        RawImage _noise;
        RawImage _scanlines;
        RawImage _vignette;
        Image _redTint;
        TextMeshProUGUI _rec;
        TextMeshProUGUI _timecode;
        TextMeshProUGUI _rosterText;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _cycleText;
        GameObject _crackRoot;
        CanvasGroup _crackGroup;
        readonly List<RectTransform> _glitchBars = new List<RectTransform>();
        NoiseTexture _noiseTexture;
        float _noiseTimer;
        float _clock;
        bool _feedVisible;
        bool _feedFrozen;

        GameObject _panel;
        TextMeshProUGUI _panelKicker;
        TextMeshProUGUI _panelTitle;
        TextMeshProUGUI _panelSub;
        TextMeshProUGUI _panelCause;
        TextMeshProUGUI _panelHint;
        readonly List<TextMeshProUGUI> _buttonLabels = new List<TextMeshProUGUI>();
        EndingType _endingType = EndingType.B;

        // A/B 与主菜单共用同一台探头、同一套滤镜与左侧文字菜单。
        Canvas _stageCanvas;
        Camera _stageCamera;
        RawImage _stageNoise;
        SurveillanceStage.NoiseTexture _stageNoiseTexture;
        float _stageNoiseTimer;
        bool _stageVisible;
        bool _stageFrozen;
        TextMeshProUGUI _stageRec;
        TextMeshProUGUI _stageTimecode;
        TextMeshProUGUI _stageTitle;
        TextMeshProUGUI _stageSub;
        TextMeshProUGUI _stageRoster;
        TextMeshProUGUI _stageStatus;
        TextMeshProUGUI _stageCycle;
        readonly List<Button> _stageButtons = new List<Button>();
        static readonly string[] StageButtonKeys = { "再来一局", "返回主菜单", "制作人员", "设置" };

        static readonly string[] ButtonKeys = { "再来一局", "返回主菜单", "制作人员", "设置" };

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            Localization.OnLanguageChanged -= RefreshPanelTexts;
            if (Instance == this) Instance = null;
        }

        public void Initialize()
        {
            if (_feedCanvas != null) return;
            BuildFeedCanvas();
            BuildStageCanvas();
            BuildPanelCanvas();
            Localization.OnLanguageChanged += RefreshPanelTexts;
            HideAll();
        }

        void Update()
        {
            if (!_feedVisible && !_stageVisible) return;

            _clock += Time.unscaledDeltaTime;
            UpdateStage();

            if (!_feedVisible) return;
            if (_rec != null)
                _rec.color = new Color(0.92f, 0.08f, 0.06f, Mathf.Sin(_clock * 5.4f) > 0.1f ? 1f : 0.15f);

            if (_timecode != null && !_feedFrozen)
            {
                System.DateTime now = System.DateTime.Now;
                _timecode.text = now.ToString("yyyy-MM-dd  HH:mm:ss") + "  CAM-01";
            }

            if (!_feedFrozen && _noise != null && _noiseTexture != null)
            {
                _noiseTimer -= Time.unscaledDeltaTime;
                if (_noiseTimer <= 0f)
                {
                    _noiseTimer = 0.075f;
                    _noiseTexture.Refresh();
                }
            }

            // 花屏：横向撕裂条不断跳动。
            for (int i = 0; i < _glitchBars.Count; i++)
            {
                RectTransform bar = _glitchBars[i];
                if (bar == null) continue;
                bar.anchoredPosition = new Vector2(
                    Random.Range(-26f, 26f),
                    Random.Range(-500f, 500f));
                bar.sizeDelta = new Vector2(2200f, Random.Range(2f, 26f));
                Image image = bar.GetComponent<Image>();
                if (image != null) image.color = new Color(1f, 1f, 1f, Random.Range(0.02f, 0.16f));
            }

            if (_feed != null && _feedVisible)
            {
                float jitter = _feedFrozen
                    ? 0f
                    : (Mathf.PerlinNoise(_clock * 9f, 0.21f) > 0.7f ? Random.Range(-2f, 2f) : 0f);
                _feed.rectTransform.anchoredPosition = new Vector2(jitter, 0f);
            }
        }

        // ------------------------------------------------------------------
        // 对外：结局演出
        // ------------------------------------------------------------------

        /// <summary>
        /// A/B：直接沿用主菜单的监控画面，演完「9 号死 → 天亮顺移 → 新的 1 号补入」，
        /// 然后定帧。循环不会闭合，它只是继续。
        /// </summary>
        public IEnumerator PlaySurveillanceDeduction(EndingType type, string cause)
        {
            // 和主菜单同一套东西：同一台走廊探头、同一套扫描线/噪点/暗角、
            // 左侧同一列文字按钮；不使用 RenderTexture，也不弹居中面板。
            ShowStage(type);

            List<int> roster = CurrentRoster();
            if (roster.Count == 0)
            {
                for (int n = 1; n <= 9; n++) roster.Add(n);
            }

            RefreshStageRoster(roster);
            SetStageStatus(Localization.IsEnglish
                ? "Surveillance feed online."
                : "监控画面已接入。");

            yield return new WaitForSecondsRealtime(1.2f);

            // 天亮前的既定事实：1 号杀死 9 号。
            if (type == EndingType.A)
            {
                roster.Remove(9);
                RefreshStageRoster(roster);
                SetStageStatus(Localization.IsEnglish
                    ? "No. 9 was killed in bed before dawn."
                    : "天亮之前，9 号在床上被杀死。");
                if (SFXManager.Instance != null) SFXManager.Instance.PlayBellToll(0.55f);
                yield return new WaitForSecondsRealtime(1.4f);
            }

            // 天亮：所有活人号码 +1，超过 9 的离开，新的 1 号补入。
            SetStageStatus(Localization.IsEnglish
                ? "Lights out. Every number shifts by one."
                : "灯灭了。所有人的号码开始顺移。");
            yield return new WaitForSecondsRealtime(1.3f);

            var shifted = new List<int>();
            for (int i = 0; i < roster.Count; i++)
            {
                int next = roster[i] + 1;
                if (next <= 9) shifted.Add(next);
            }
            if (!shifted.Contains(1)) shifted.Insert(0, 1);
            shifted.Sort();

            if (type == EndingType.A && shifted.Contains(9))
            {
                // 玩家以 8 号活到天亮：顺移之后，他自己成了那个 9 号。
                shifted.Remove(9);
            }

            RefreshStageRoster(shifted);
            SetStageStatus(Localization.IsEnglish
                ? "A new No. 1 enters. The loop continues."
                : "新的 1 号补入。循环继续。");
            yield return new WaitForSecondsRealtime(1.6f);

            SetStageStatus(Localization.IsEnglish
                ? "The feed continues. No one is watching."
                : "画面仍在继续。只是没有人再看着它了。");
            yield return new WaitForSecondsRealtime(1.2f);

            FreezeStage();
            RefreshStageCameraLabel();
            yield return new WaitForSecondsRealtime(0.35f);
        }

        /// <summary>D：把监控画面切到台前，然后碎裂 + 花屏，最后定帧。</summary>
        public IEnumerator PlayMonitorCrack()
        {
            ShowFeed();

            if (_cycleText != null)
                _cycleText.text = Localization.IsEnglish ? "SIGNAL LOST" : "信号中断";
            if (_statusText != null)
                _statusText.text = Localization.IsEnglish
                    ? "All masks removed. The surveillance system is collapsing."
                    : "全员摘下面具。监控系统正在崩塌。";

            if (_rosterText != null) _rosterText.text = string.Empty;
            yield return new WaitForSecondsRealtime(0.8f);

            if (_crackRoot != null) _crackRoot.SetActive(true);
            float t = 0f;
            while (t < 2.6f)
            {
                t += Time.unscaledDeltaTime;
                if (_crackGroup != null)
                    _crackGroup.alpha = Mathf.Clamp01(0.25f + t * 0.5f);
                if (_feed != null)
                {
                    _feed.rectTransform.anchoredPosition = new Vector2(
                        Random.Range(-8f, 8f), Random.Range(-4f, 4f));
                    _feed.color = new Color(
                        Random.Range(0.55f, 0.9f), Random.Range(0.55f, 0.9f), Random.Range(0.6f, 0.95f), 1f);
                }
                if (_redTint != null)
                    _redTint.color = new Color(0.7f, 0f, 0f, Random.Range(0.08f, 0.35f));
                yield return null;
            }

            if (_crackGroup != null) _crackGroup.alpha = 1f;
            FreezeFeed();
            if (_statusText != null)
                _statusText.text = Localization.IsEnglish
                    ? "No signal. The loop is over."
                    : "无信号。循环结束了。";
            yield return new WaitForSecondsRealtime(1.2f);
        }

        /// <summary>统一悬浮结算面板。</summary>
        public void ShowResultPanel(EndingType type, string cause)
        {
            _endingType = type;

            // A/B：结算就是主菜单那块监控屏本身——左侧标题、说明和文字按钮，
            // 不另起居中面板。C/D 仍然使用原来的悬浮卡片。
            if (type == EndingType.A || type == EndingType.B)
            {
                ShowStageResult(type, cause);
                return;
            }

            if (_panelCanvas != null) _panelCanvas.gameObject.SetActive(true);
            if (_panel != null) _panel.SetActive(true);

            if (_panelTitle != null)
            {
                _panelTitle.text = Localization.T(TitleKey(type));
                _panelTitle.color = TitleColor(type);
            }
            if (_panelSub != null)
            {
                _panelSub.text = Localization.T(SubtitleKey(type));
                _panelSub.color = TitleColor(type);
            }
            if (_panelCause != null)
            {
                string line = Localization.Translate(cause);
                string epilogue = Localization.T(EpilogueKey(type));
                _panelCause.text = string.IsNullOrEmpty(epilogue) ? line : line + "\n" + epilogue;
            }
            RefreshPanelTexts();
        }

        public void HideAll()
        {
            _feedVisible = false;
            _feedFrozen = false;
            if (_feedCanvas != null) _feedCanvas.gameObject.SetActive(false);
            if (_panelCanvas != null) _panelCanvas.gameObject.SetActive(false);
            if (_crackRoot != null) _crackRoot.SetActive(false);
            HideStage();
        }

        // ------------------------------------------------------------------
        // 监控画面
        // ------------------------------------------------------------------

        void ShowFeed()
        {
            _feedVisible = true;
            _feedFrozen = false;
            if (_feedCanvas != null) _feedCanvas.gameObject.SetActive(true);
            if (_crackRoot != null) _crackRoot.SetActive(false);
            BindFeed();
        }

        void BindFeed()
        {
            if (_feed == null || _feed.texture != null) return;
            if (MonitorSystem.Instance == null || MonitorSystem.Instance.renderTexture == null)
            {
                Debug.LogWarning("[EndingResultUI] 找不到监控 RenderTexture，使用黑底代替。");
                return;
            }
            _feed.texture = MonitorSystem.Instance.renderTexture;
            _feed.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        void FreezeFeed()
        {
            _feedFrozen = true;
            _noiseTimer = float.MaxValue;
            if (_noise != null) _noise.enabled = false;
            if (_scanlines != null) _scanlines.enabled = false;

            // 关闭监控相机：RenderTexture 保留最后一帧，画面就此定帧。
            if (MonitorSystem.Instance != null && MonitorSystem.Instance.monitorCamera != null)
                MonitorSystem.Instance.monitorCamera.enabled = false;
        }

        // ------------------------------------------------------------------
        // 主菜单同款监控舞台（结局 A/B）
        // ------------------------------------------------------------------

        void ShowStage(EndingType type)
        {
            if (_stageCanvas == null) BuildStageCanvas();

            bool alreadyVisible = _stageVisible;
            PrepareSceneForStage();

            _stageVisible = true;
            if (!alreadyVisible)
            {
                _stageFrozen = false;
                _stageNoiseTimer = 0f;
                if (_stageNoise != null) _stageNoise.enabled = true;
            }
            if (_stageCanvas != null) _stageCanvas.gameObject.SetActive(true);
            if (_stageCamera != null) _stageCamera.gameObject.SetActive(true);

            if (_stageTitle != null)
            {
                _stageTitle.text = Localization.T(TitleKey(type));
                _stageTitle.color = TitleColor(type);
                _stageTitle.fontSize = Localization.IsEnglish ? 42f : 62f;
            }
            if (_stageSub != null)
            {
                _stageSub.text = Localization.T(SubtitleKey(type));
                _stageSub.color = new Color(0.72f, 0.08f, 0.06f, 0.95f);
            }

            // 已经在演推演时不要重置名单：推演结束的顺移结果要保留到结算。
            if (!alreadyVisible)
            {
                RefreshStageCameraLabel();
                RefreshStageRoster(CurrentRoster());
                SetStageStatus(Localization.IsEnglish ? "Surveillance feed online." : "监控画面已接入。");
            }

            // 推演阶段不显示结算按钮；只有 ShowStageResult 之后才出现菜单。
            for (int i = 0; i < _stageButtons.Count; i++)
            {
                if (_stageButtons[i] != null)
                    _stageButtons[i].gameObject.SetActive(alreadyVisible);
            }
        }

        void HideStage()
        {
            _stageVisible = false;
            _stageFrozen = false;
            if (_stageCanvas != null) _stageCanvas.gameObject.SetActive(false);
            if (_stageCamera != null) _stageCamera.gameObject.SetActive(false);
        }

        /// <summary>
        /// 把玩家、HUD 和 UI 层从探头画面里拿掉，NPC 换到 30 层（探头可见）。
        /// 和主菜单的 PrepareSceneForMenu 保持同一套规则。
        /// </summary>
        void PrepareSceneForStage()
        {
            SetNamedActive("HUD", false);
            SetNamedActive("MeetingHUD", false);
            SetNamedActive("VotingUI_Canvas", false);
            SetNamedActive("PrivateChatHUD", false);
            SetNamedActive("SoloTimerUI", false);

            GameObject player = GameObject.Find("Player");
            if (player != null)
            {
                Renderer[] renderers = player.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = false;
            }

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null) continue;
                Transform[] parts = npc.GetComponentsInChildren<Transform>(true);
                for (int j = 0; j < parts.Length; j++)
                    if (parts[j] != null) parts[j].gameObject.layer = 30;
            }
        }

        static void SetNamedActive(string name, bool active)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) go.SetActive(active);
        }

        void ShowStageResult(EndingType type, string cause)
        {
            ShowStage(type);

            string line = Localization.Translate(cause);
            string epilogue = Localization.T(EpilogueKey(type));
            SetStageStatus(string.IsNullOrEmpty(epilogue) ? line : line + "\n" + epilogue, false);

            RefreshStageCameraLabel();

            for (int i = 0; i < _stageButtons.Count; i++)
            {
                if (_stageButtons[i] != null) _stageButtons[i].gameObject.SetActive(true);
            }
        }

        void RefreshStageCameraLabel()
        {
            if (_stageCycle == null) return;
            string head = _stageFrozen
                ? (Localization.IsEnglish ? "FEED FROZEN" : "画面定帧")
                : "LOOP  " + Mathf.Max(0, CycleState.loopIndex).ToString("00");
            _stageCycle.text = head + "  /  " + SurveillanceStage.CurrentCameraLabel;
        }

        void SetStageStatus(string text, bool alert = true)
        {
            if (_stageStatus == null) return;
            _stageStatus.text = text;
            _stageStatus.color = alert
                ? new Color(0.86f, 0.30f, 0.24f, 0.96f)
                : new Color(0.78f, 0.72f, 0.68f, 0.95f);
        }

        void RefreshStageRoster(List<int> alive)
        {
            if (_stageRoster == null) return;
            var labels = new List<string>();
            for (int i = 0; i < alive.Count; i++) labels.Add(alive[i] + "号");
            _stageRoster.text = Localization.T("存活名单") + "\n" + string.Join("   ", labels);
        }

        void UpdateStage()
        {
            if (!_stageVisible) return;

            // 小彩蛋：结局 A/B 也能按 Tab 切换探头，和主菜单共用同一个选中项。
            // 定帧之后依然可以切，方便玩家看不同角度的「最后画面」。
            if (SurveillanceStage.HandleCameraHotkey())
            {
                SurveillanceStage.ApplyTo(_stageCamera);
                RefreshStageCameraLabel();
            }

            if (_stageRec != null)
                _stageRec.color = new Color(0.92f, 0.08f, 0.06f,
                    Mathf.Sin(_clock * 5.2f) > 0.1f ? 1f : 0.18f);

            if (_stageTimecode != null && !_stageFrozen)
            {
                System.DateTime now = System.DateTime.Now;
                _stageTimecode.text = now.ToString("yyyy-MM-dd  HH:mm:ss") + "  " +
                                      SurveillanceStage.CurrentCameraTag;
            }

            if (_stageFrozen || _stageNoise == null || _stageNoiseTexture == null) return;
            _stageNoiseTimer -= Time.unscaledDeltaTime;
            if (_stageNoiseTimer <= 0f)
            {
                _stageNoiseTimer = 0.075f;
                _stageNoiseTexture.Refresh();
            }
        }

        void FreezeStage()
        {
            _stageFrozen = true;
            _stageNoiseTimer = float.MaxValue;
            if (_stageNoise != null) _stageNoise.enabled = false;
        }

        List<int> CurrentRoster()
        {
            var roster = new List<int>();
            if (CycleState.playerAlive && CycleState.playerNumber > 0)
                roster.Add(CycleState.playerNumber);
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && !roster.Contains(npc.Number))
                    roster.Add(npc.Number);
            }
            roster.Sort();
            return roster;
        }

        void RefreshRoster(List<int> alive, List<int> eliminated)
        {
            if (_rosterText == null) return;
            var aliveLabels = new List<string>();
            for (int i = 0; i < alive.Count; i++) aliveLabels.Add(alive[i] + "号");

            string text = (Localization.IsEnglish ? "SURVIVORS  " : "存活  ")
                          + string.Join("   ", aliveLabels);
            if (eliminated.Count > 0)
            {
                var deadLabels = new List<string>();
                for (int i = 0; i < eliminated.Count; i++)
                    deadLabels.Add("<color=#8e2b22>" + eliminated[i] + "号</color>");
                text += "\n" + (Localization.IsEnglish ? "VACANT  " : "空缺  ")
                        + string.Join("   ", deadLabels);
            }
            _rosterText.text = text;
        }

        // ------------------------------------------------------------------
        // UI 构建
        // ------------------------------------------------------------------

        /// <summary>
        /// 主菜单同款：左侧标题 + 细红框文字菜单，右侧走廊探头画面，
        /// 扫描线 / 噪点 / 暗角 / 时间码全部与主菜单同一套参数。
        /// </summary>
        void BuildStageCanvas()
        {
            _stageCanvas = UIOverlayUtil.CreateCanvas("EndingSurveillanceStage_Canvas", 460, transform);
            _stageCamera = SurveillanceStage.CreateCamera(transform, "EndingSurveillanceCamera");

            Image tint = UIOverlayUtil.CreateImage(_stageCanvas.transform, "RedTint",
                new Color(0.55f, 0.02f, 0.02f, 0.05f));
            tint.transform.SetAsLastSibling();

            GameObject scanGo = new GameObject("Scanlines", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            scanGo.transform.SetParent(_stageCanvas.transform, false);
            SurveillanceStage.Stretch(scanGo.GetComponent<RectTransform>());
            RawImage scanlines = scanGo.GetComponent<RawImage>();
            scanlines.texture = SurveillanceStage.BuildScanlineTexture();
            scanlines.uvRect = new Rect(0f, 0f, 1f, 260f);
            scanlines.color = new Color(1f, 1f, 1f, 0.16f);
            scanlines.raycastTarget = false;

            GameObject noiseGo = new GameObject("Noise", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            noiseGo.transform.SetParent(_stageCanvas.transform, false);
            SurveillanceStage.Stretch(noiseGo.GetComponent<RectTransform>());
            _stageNoise = noiseGo.GetComponent<RawImage>();
            _stageNoiseTexture = new SurveillanceStage.NoiseTexture(96, 96);
            _stageNoise.texture = _stageNoiseTexture.Texture;
            _stageNoise.uvRect = new Rect(0f, 0f, 3f, 3f);
            _stageNoise.color = new Color(1f, 1f, 1f, 0.05f);
            _stageNoise.raycastTarget = false;

            GameObject vignetteGo = new GameObject("Vignette", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            vignetteGo.transform.SetParent(_stageCanvas.transform, false);
            SurveillanceStage.Stretch(vignetteGo.GetComponent<RectTransform>());
            RawImage vignette = vignetteGo.GetComponent<RawImage>();
            vignette.texture = SurveillanceStage.BuildVignetteTexture();
            vignette.color = new Color(1f, 1f, 1f, 0.46f);
            vignette.raycastTarget = false;

            GameObject gradGo = new GameObject("StageGradient", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            gradGo.transform.SetParent(_stageCanvas.transform, false);
            RectTransform gradRect = gradGo.GetComponent<RectTransform>();
            gradRect.anchorMin = new Vector2(0f, 0f);
            gradRect.anchorMax = new Vector2(0f, 1f);
            gradRect.pivot = new Vector2(0f, 0.5f);
            gradRect.sizeDelta = new Vector2(640f, 0f);
            gradRect.anchoredPosition = Vector2.zero;
            RawImage grad = gradGo.GetComponent<RawImage>();
            grad.texture = SurveillanceStage.BuildLeftGradientTexture();
            grad.color = Color.white;
            grad.raycastTarget = false;

            _stageTitle = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageTitle", "结局",
                new Vector2(0f, 1f), new Vector2(320f, -118f), new Vector2(520f, 82f), 62f,
                TextAlignmentOptions.Left, new Color(0.95f, 0.91f, 0.86f, 1f));
            _stageTitle.fontStyle = FontStyles.Bold;
            _stageTitle.characterSpacing = 8f;

            _stageSub = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageSubtitle", string.Empty,
                new Vector2(0f, 1f), new Vector2(320f, -180f), new Vector2(520f, 22f), 12f,
                TextAlignmentOptions.Left, new Color(0.72f, 0.08f, 0.06f, 0.95f));
            _stageSub.characterSpacing = 9f;

            for (int i = 0; i < StageButtonKeys.Length; i++)
            {
                int index = i;
                Button button = SurveillanceStage.CreateTextButton(_stageCanvas.transform,
                    "EndingButton_" + StageButtonKeys[i], Localization.T(StageButtonKeys[i]),
                    new Vector2(230f, -250f - i * 56f), new Vector2(340f, 46f),
                    () => OnResultButton(index));
                _stageButtons.Add(button);
            }

            _stageStatus = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageStatus", string.Empty,
                new Vector2(0f, 1f), new Vector2(320f, -520f), new Vector2(560f, 120f), 18f,
                TextAlignmentOptions.TopLeft, new Color(0.86f, 0.30f, 0.24f, 0.96f));
            _stageStatus.lineSpacing = 6f;

            _stageRoster = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageRoster", string.Empty,
                new Vector2(0f, 0f), new Vector2(320f, 90f), new Vector2(520f, 60f), 16f,
                TextAlignmentOptions.BottomLeft, new Color(0.80f, 0.75f, 0.70f, 0.85f));
            _stageRoster.characterSpacing = 3f;
            _stageRoster.lineSpacing = 10f;

            _stageRec = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageREC", "● REC",
                new Vector2(0f, 1f), new Vector2(150f, -38f), new Vector2(220f, 30f), 17f,
                TextAlignmentOptions.Left, new Color(0.92f, 0.08f, 0.06f, 1f));
            _stageTimecode = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageTimecode", string.Empty,
                new Vector2(1f, 1f), new Vector2(-190f, -46f), new Vector2(360f, 34f), 18f,
                TextAlignmentOptions.Right, new Color(0.82f, 0.84f, 0.82f, 0.9f));
            _stageCycle = UIOverlayUtil.CreateText(_stageCanvas.transform, "StageCycle", string.Empty,
                new Vector2(1f, 0f), new Vector2(-190f, 44f), new Vector2(360f, 36f), 18f,
                TextAlignmentOptions.Right, new Color(0.70f, 0.66f, 0.62f, 0.85f));

            _stageCanvas.gameObject.SetActive(false);
            _stageCamera.gameObject.SetActive(false);
        }

        void BuildFeedCanvas()
        {
            _feedCanvas = UIOverlayUtil.CreateCanvas("EndingSurveillance_Canvas", 460, transform);

            GameObject feedGo = new GameObject("SurveillanceFeed", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            feedGo.transform.SetParent(_feedCanvas.transform, false);
            Stretch(feedGo.GetComponent<RectTransform>());
            _feed = feedGo.GetComponent<RawImage>();
            _feed.color = new Color(0.80f, 0.82f, 0.84f, 1f);
            _feed.raycastTarget = false;
            BindFeed();

            _redTint = UIOverlayUtil.CreateImage(_feedCanvas.transform, "RedTint",
                new Color(0.55f, 0.02f, 0.02f, 0.10f));
            _redTint.transform.SetAsLastSibling();

            GameObject scanGo = new GameObject("Scanlines", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            scanGo.transform.SetParent(_feedCanvas.transform, false);
            Stretch(scanGo.GetComponent<RectTransform>());
            _scanlines = scanGo.GetComponent<RawImage>();
            _scanlines.texture = BuildScanlineTexture();
            _scanlines.uvRect = new Rect(0f, 0f, 1f, 260f);
            _scanlines.color = new Color(1f, 1f, 1f, 0.15f);
            _scanlines.raycastTarget = false;

            GameObject noiseGo = new GameObject("Noise", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            noiseGo.transform.SetParent(_feedCanvas.transform, false);
            Stretch(noiseGo.GetComponent<RectTransform>());
            _noise = noiseGo.GetComponent<RawImage>();
            _noiseTexture = new NoiseTexture(96, 96);
            _noise.texture = _noiseTexture.Texture;
            _noise.uvRect = new Rect(0f, 0f, 4f, 4f);
            _noise.color = new Color(1f, 1f, 1f, 0.075f);
            _noise.raycastTarget = false;

            GameObject vignetteGo = new GameObject("Vignette", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            vignetteGo.transform.SetParent(_feedCanvas.transform, false);
            Stretch(vignetteGo.GetComponent<RectTransform>());
            _vignette = vignetteGo.GetComponent<RawImage>();
            _vignette.texture = BuildVignetteTexture();
            _vignette.raycastTarget = false;

            _rec = UIOverlayUtil.CreateText(_feedCanvas.transform, "REC", "● REC",
                new Vector2(0f, 1f), new Vector2(72f, -46f), new Vector2(220f, 40f), 24f,
                TextAlignmentOptions.Left, new Color(0.92f, 0.08f, 0.06f, 1f));
            _timecode = UIOverlayUtil.CreateText(_feedCanvas.transform, "Timecode", string.Empty,
                new Vector2(1f, 1f), new Vector2(-190f, -46f), new Vector2(360f, 34f), 18f,
                TextAlignmentOptions.Right, new Color(0.82f, 0.84f, 0.82f, 0.9f));
            _cycleText = UIOverlayUtil.CreateText(_feedCanvas.transform, "CycleText", string.Empty,
                new Vector2(1f, 0f), new Vector2(-190f, 44f), new Vector2(520f, 36f), 18f,
                TextAlignmentOptions.Right, new Color(0.72f, 0.68f, 0.64f, 0.9f));
            _rosterText = UIOverlayUtil.CreateText(_feedCanvas.transform, "Roster", string.Empty,
                new Vector2(0f, 0.5f), new Vector2(360f, 40f), new Vector2(640f, 260f), 28f,
                TextAlignmentOptions.Left, new Color(0.92f, 0.88f, 0.82f, 0.96f));
            _rosterText.lineSpacing = 12f;
            _statusText = UIOverlayUtil.CreateText(_feedCanvas.transform, "Status", string.Empty,
                new Vector2(0.5f, 0f), new Vector2(0f, 58f), new Vector2(1500f, 56f), 22f,
                TextAlignmentOptions.Center, new Color(0.86f, 0.30f, 0.24f, 0.96f));

            BuildCrackOverlay();
        }

        void BuildCrackOverlay()
        {
            GameObject root = new GameObject("MonitorCracks", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            root.transform.SetParent(_feedCanvas.transform, false);
            Stretch(root.GetComponent<RectTransform>());
            Image bg = root.GetComponent<Image>();
            bg.color = new Color(0.01f, 0.01f, 0.015f, 0.18f);
            bg.raycastTarget = false;
            _crackRoot = root;
            _crackGroup = root.GetComponent<CanvasGroup>();
            _crackGroup.alpha = 0f;
            _crackGroup.blocksRaycasts = false;

            // 放射状裂纹。
            Random.State oldState = Random.state;
            Random.InitState(20261030);
            for (int i = 0; i < 22; i++)
            {
                GameObject lineGo = new GameObject("Crack_" + i, typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image));
                lineGo.transform.SetParent(root.transform, false);
                RectTransform rect = lineGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                float angle = Random.Range(0f, 360f);
                rect.anchoredPosition = new Vector2(Random.Range(-40f, 40f), Random.Range(-40f, 40f));
                rect.sizeDelta = new Vector2(Random.Range(180f, 760f), Random.Range(1f, 3f));
                rect.localRotation = Quaternion.Euler(0f, 0f, angle);
                Image line = lineGo.GetComponent<Image>();
                line.color = new Color(0.92f, 0.94f, 0.95f, Random.Range(0.45f, 0.9f));
                line.raycastTarget = false;
            }

            // 碎玻璃三角块（用旋转矩形近似）。
            for (int i = 0; i < 12; i++)
            {
                GameObject shardGo = new GameObject("Shard_" + i, typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image));
                shardGo.transform.SetParent(root.transform, false);
                RectTransform rect = shardGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(Random.Range(80f, 300f), Random.Range(50f, 200f));
                rect.anchoredPosition = new Vector2(Random.Range(-760f, 760f), Random.Range(-420f, 420f));
                rect.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                Image shard = shardGo.GetComponent<Image>();
                shard.color = new Color(Random.Range(0.55f, 0.8f), Random.Range(0.6f, 0.85f),
                    Random.Range(0.65f, 0.9f), Random.Range(0.05f, 0.16f));
                shard.raycastTarget = false;
            }
            Random.state = oldState;

            // 花屏横条。
            for (int i = 0; i < 14; i++)
            {
                GameObject barGo = new GameObject("Glitch_" + i, typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image));
                barGo.transform.SetParent(root.transform, false);
                RectTransform rect = barGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(2200f, 8f);
                Image image = barGo.GetComponent<Image>();
                image.color = new Color(1f, 1f, 1f, 0.05f);
                image.raycastTarget = false;
                _glitchBars.Add(rect);
            }

            _crackRoot.SetActive(false);
        }

        void BuildPanelCanvas()
        {
            _panelCanvas = UIOverlayUtil.CreateCanvas("EndingResult_Canvas", 620, transform);

            _panel = new GameObject("ResultPanel", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            _panel.transform.SetParent(_panelCanvas.transform, false);
            RectTransform rect = _panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(980f, 660f);
            rect.anchoredPosition = Vector2.zero;
            Image bg = _panel.GetComponent<Image>();
            bg.color = new Color(0.015f, 0.012f, 0.015f, 0.88f);
            bg.raycastTarget = true;

            UIOverlayUtil.CreateImage(_panel.transform, "TopLine", new Color(0.55f, 0.10f, 0.08f, 0.9f));
            RectTransform lineRect = _panel.transform.Find("TopLine").GetComponent<RectTransform>();
            lineRect.anchorMin = new Vector2(0f, 1f);
            lineRect.anchorMax = new Vector2(1f, 1f);
            lineRect.pivot = new Vector2(0.5f, 1f);
            lineRect.sizeDelta = new Vector2(0f, 3f);
            lineRect.anchoredPosition = Vector2.zero;

            _panelKicker = UIOverlayUtil.CreateText(_panel.transform, "Kicker", "GAME OVER  /  游戏结束",
                new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(880f, 34f), 18f,
                TextAlignmentOptions.Center, new Color(0.62f, 0.56f, 0.52f, 1f));

            _panelTitle = UIOverlayUtil.CreateText(_panel.transform, "Title", "结局",
                new Vector2(0.5f, 1f), new Vector2(0f, -112f), new Vector2(900f, 82f), 56f,
                TextAlignmentOptions.Center, new Color(0.88f, 0.22f, 0.18f, 1f));
            _panelTitle.fontStyle = FontStyles.Bold;
            _panelTitle.characterSpacing = 4f;

            _panelSub = UIOverlayUtil.CreateText(_panel.transform, "Subtitle", string.Empty,
                new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(880f, 36f), 20f,
                TextAlignmentOptions.Center, new Color(0.78f, 0.70f, 0.66f, 1f));

            _panelCause = UIOverlayUtil.CreateText(_panel.transform, "Cause", string.Empty,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 62f), new Vector2(840f, 140f), 24f,
                TextAlignmentOptions.Center, new Color(0.84f, 0.80f, 0.76f, 1f));
            _panelCause.lineSpacing = 8f;

            string[] labels = ButtonKeys;
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                float x = (i % 2 == 0) ? -215f : 215f;
                float y = (i < 2) ? -58f : -138f;
                Button button = UIOverlayUtil.CreateButton(_panel.transform, "ResultButton_" + labels[i],
                    Localization.T(labels[i]), new Vector2(x, y), new Vector2(390f, 62f),
                    () => OnResultButton(index),
                    new Color(0.11f, 0.028f, 0.032f, 0.96f), 25f);
                TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) _buttonLabels.Add(label);
            }

            _panelHint = UIOverlayUtil.CreateText(_panel.transform, "Hint",
                "存档会在天亮与退出游戏时自动保存",
                new Vector2(0.5f, 0f), new Vector2(0f, 38f), new Vector2(880f, 30f), 16f,
                TextAlignmentOptions.Center, new Color(0.50f, 0.46f, 0.44f, 0.9f));

            if (_panelCanvas != null) _panelCanvas.gameObject.SetActive(false);
        }

        void OnResultButton(int index)
        {
            switch (index)
            {
                case 0: GameMode.RestartNewGame(); break;
                case 1: GameMode.ReturnToMenu(); break;
                case 2: CreditsUI.Show(); break;
                case 3: SettingsUI.Show(); break;
            }
        }

        void RefreshPanelTexts()
        {
            if (_panelKicker != null)
                _panelKicker.text = Localization.IsEnglish ? "GAME OVER  /  THE END" : "GAME OVER  /  游戏结束";
            for (int i = 0; i < _buttonLabels.Count && i < ButtonKeys.Length; i++)
            {
                if (_buttonLabels[i] != null) _buttonLabels[i].text = Localization.T(ButtonKeys[i]);
            }
            if (_panelHint != null)
                _panelHint.text = Localization.T("存档会在天亮与退出游戏时自动保存");
            if (_panelTitle != null)
                _panelTitle.text = Localization.T(TitleKey(_endingType));
            if (_panelSub != null)
                _panelSub.text = Localization.T(SubtitleKey(_endingType));

            for (int i = 0; i < _stageButtons.Count && i < StageButtonKeys.Length; i++)
            {
                if (_stageButtons[i] == null) continue;
                TextMeshProUGUI label = _stageButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = Localization.T(StageButtonKeys[i]);
            }
            if (_stageTitle != null)
            {
                _stageTitle.text = Localization.T(TitleKey(_endingType));
                _stageTitle.color = TitleColor(_endingType);
                _stageTitle.fontSize = Localization.IsEnglish ? 42f : 62f;
            }
            if (_stageSub != null)
                _stageSub.text = Localization.T(SubtitleKey(_endingType));
        }

        // ------------------------------------------------------------------
        // 文案
        // ------------------------------------------------------------------

        static string TitleKey(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "无尽轮回";
                case EndingType.C: return "大梦初醒";
                case EndingType.D: return "献祭顿悟";
                default: return "死亡悲剧";
            }
        }

        static string SubtitleKey(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "ENDLESS LOOP  /  无尽轮回";
                case EndingType.C: return "AWAKENING  /  大梦初醒";
                case EndingType.D: return "SACRIFICE & EPIPHANY  /  献祭顿悟";
                default: return "DEATH TRAGEDY  /  死亡悲剧";
            }
        }

        static string EpilogueKey(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return "循环仍会继续，只是不再需要你。";
                case EndingType.C: return "你终于在自己的床上醒来——或者，那只是另一层梦。";
                case EndingType.D: return "他们摘下面具，第一次认出了彼此，也认出了自己。";
                default: return "循环不会为任何人停下。";
            }
        }

        static Color TitleColor(EndingType type)
        {
            switch (type)
            {
                case EndingType.A: return new Color(0.66f, 0.62f, 0.72f, 1f);
                case EndingType.C: return new Color(0.96f, 0.84f, 0.48f, 1f);
                case EndingType.D: return new Color(0.96f, 0.68f, 0.38f, 1f);
                default: return new Color(0.86f, 0.20f, 0.18f, 1f);
            }
        }

        // ------------------------------------------------------------------
        // 程序化纹理
        // ------------------------------------------------------------------

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        static Texture2D BuildScanlineTexture()
        {
            Texture2D tex = new Texture2D(2, 4, TextureFormat.RGBA32, false);
            tex.name = "EndingScanlines";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point;
            Color[] pixels = new Color[8];
            for (int y = 0; y < 4; y++)
            {
                float alpha = y == 0 ? 0.55f : 0f;
                pixels[y * 2] = new Color(0f, 0f, 0f, alpha);
                pixels[y * 2 + 1] = new Color(0f, 0f, 0f, alpha);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        static Texture2D BuildVignetteTexture()
        {
            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "EndingVignette";
            tex.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 0.55f) / 0.45f)) * 0.75f;
                    pixels[y * size + x] = new Color(0f, 0f, 0f, a);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        class NoiseTexture
        {
            public Texture2D Texture { get; private set; }
            readonly Color32[] _pixels;
            readonly System.Random _random = new System.Random(9013);

            public NoiseTexture(int width, int height)
            {
                _pixels = new Color32[width * height];
                Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Texture.name = "EndingNoise";
                Texture.wrapMode = TextureWrapMode.Repeat;
                Texture.filterMode = FilterMode.Point;
                Refresh();
            }

            public void Refresh()
            {
                for (int i = 0; i < _pixels.Length; i++)
                {
                    byte v = (byte)_random.Next(0, 256);
                    _pixels[i] = new Color32(v, v, v, (byte)_random.Next(20, 100));
                }
                Texture.SetPixels32(_pixels);
                Texture.Apply(false);
            }
        }
    }
}
