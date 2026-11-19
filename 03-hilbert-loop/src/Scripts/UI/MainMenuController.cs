using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 动态主菜单。
    ///
    /// 画面：一台真实的走廊监控探头（Perspective / FOV 55）斜向下俯拍
    /// 大厅通往右环的走廊。UI 直接叠在画面上，不再使用俯瞰全图的 RenderTexture。
    ///
    /// 循环：不加载玩家、不启动 LLM；NPC 只跑行为树。
    /// 每一轮都是完整的 1~9 号——1 号在夜里杀死 9 号，天亮后号码顺移、
    /// 新的 1 号补入，然后原地重建名册。没有 SceneManager.LoadScene，也没有黑屏。
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        public static MainMenuController Instance { get; private set; }

        // 主菜单演示按正常游戏节奏走：一段话要留够读的时间，
        // 一轮完整流程（醒来 → 集合 → 讨论 → 投票 → 轮回）约 75~85 秒。
        [Header("节奏")]
        public float firstRoundDelay = 3f;
        public float aftermathSeconds = 8f;

        Canvas _canvas;
        Camera _menuCamera;
        RawImage _noise;
        RawImage _scanlines;
        RawImage _vignette;
        TextMeshProUGUI _title;
        TextMeshProUGUI _subtitle;
        TextMeshProUGUI _rec;
        TextMeshProUGUI _timecode;
        TextMeshProUGUI _rosterText;
        TextMeshProUGUI _cycleText;
        TextMeshProUGUI _phaseText;
        TextMeshProUGUI _transcriptText;
        TextMeshProUGUI _continueLabel;
        Button _continueButton;
        SurveillanceStage.NoiseTexture _noiseTexture;
        float _noiseTimer;
        float _clock;
        int _roundIndex;

        readonly List<Button> _menuButtons = new List<Button>();
        readonly List<string> _transcript = new List<string>();
        readonly System.Random _rng = new System.Random();
        List<int> _roster = new List<int>();
        string _phaseKey = "醒来";
        string _phaseSuffix;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void HookSceneLoaded()
        {
            // AfterSceneLoad 只会在编辑器首次启动时触发一次；
            // 「返回主菜单 / 再来一局」会重载 MainScene，必须靠 sceneLoaded 重新接管。
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Bootstrap();
        }

        static void Bootstrap()
        {
            if (!GameMode.MenuActive) return;
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != GameMode.MainSceneName) return;
            if (FindObjectOfType<MainMenuController>() != null) return;

            PrepareSceneForMenu();

            GameObject go = new GameObject("MainMenuController");
            go.AddComponent<MainMenuController>();
        }

        static void PrepareSceneForMenu()
        {
            // 玩家与 HUD 完全退场：不加载第一人称、不显示任何游戏 HUD。
            GameObject player = GameObject.Find("Player");
            if (player != null) player.SetActive(false);
            GameObject hud = GameObject.Find("HUD");
            if (hud != null) hud.SetActive(false);
            DisableNamed("MeetingHUD");
            DisableNamed("VotingUI_Canvas");
            DisableNamed("PrivateChatHUD");

            // 只留下监控、灯光、建筑和行为树。所有会推进剧情 / 驱动玩法的组件停用；
            // LLM 模型宿主保留常驻，见下方说明。
            foreach (GameManager gm in FindObjectsOfType<GameManager>(true))
            {
                gm.HaltLoop();
                gm.enabled = false;
            }
            foreach (CycleController c in FindObjectsOfType<CycleController>(true)) c.enabled = false;
            foreach (NPCFactory f in FindObjectsOfType<NPCFactory>(true)) f.enabled = false;
            foreach (CycleResetSystem c in FindObjectsOfType<CycleResetSystem>(true)) c.enabled = false;
            foreach (EndingSystem c in FindObjectsOfType<EndingSystem>(true)) c.enabled = false;
            foreach (EndingDSystem c in FindObjectsOfType<EndingDSystem>(true)) c.enabled = false;
            foreach (MeetingSystem c in FindObjectsOfType<MeetingSystem>(true)) c.enabled = false;
            foreach (VotingSystem c in FindObjectsOfType<VotingSystem>(true)) c.enabled = false;
            foreach (PrivateChatSystem c in FindObjectsOfType<PrivateChatSystem>(true)) c.enabled = false;
            foreach (PlayerDeathSystem c in FindObjectsOfType<PlayerDeathSystem>(true)) c.enabled = false;
            // 注意：LLMNpcBrain 是 DontDestroyOnLoad 常驻对象，菜单里不能停用。
            // 一旦停用，开始游戏重载场景后它也永远不会重新启用，
            // 本地模型会一直不 Ready，私聊的自由发言与 LLM 托管会全部消失。
            // 它自身在 MenuActive 时不跑对话逻辑，保留 enabled 是安全的。
            // LlmSettingsUI 同理：它自带 MenuActive 守卫，停用 GameObject 后
            // 进游戏也不会恢复，F9 模型面板会永久消失，所以这里不碰它。

            // 菜单只展示循环本身，不保留玩家身份。
            CycleState.playerNumber = 0;
            CycleState.playerAlive = true;
            CycleState.kill9Executed = true;
            CycleState.currentPhase = CyclePhase.Normal;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
            AudioListener.volume = PlayerPrefs.GetFloat("HL_MasterVolume", 0.85f);
        }

        static void DisableNamed(string name)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) go.SetActive(false);
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            BuildUI();
            EnsureMenuCamera();
            RefreshCameraLabel();
            StartCoroutine(MenuLoopRoutine());
        }

        void Update()
        {
            _clock += Time.unscaledDeltaTime;
            if (_rec != null)
                _rec.color = new Color(0.92f, 0.08f, 0.06f, Mathf.Sin(_clock * 5.2f) > 0.1f ? 1f : 0.18f);

            // 小彩蛋：Tab 在所有监控机位间循环，主菜单与结局 A/B 共用同一个选中项。
            if (SurveillanceStage.HandleCameraHotkey())
            {
                SurveillanceStage.ApplyTo(_menuCamera);
                RefreshCameraLabel();
            }

            if (_timecode != null)
            {
                System.DateTime now = System.DateTime.Now;
                _timecode.text = now.ToString("yyyy-MM-dd  HH:mm:ss") + "  " +
                                 SurveillanceStage.CurrentCameraTag;
            }

            _noiseTimer -= Time.unscaledDeltaTime;
            if (_noiseTimer <= 0f && _noise != null && _noiseTexture != null)
            {
                _noiseTimer = 0.075f;
                _noiseTexture.Refresh();
            }
        }

        /// <summary>一台真实的走廊监控探头：透视、FOV 55、离地 2.8 米、约 30° 俯拍。</summary>
        void EnsureMenuCamera()
        {
            if (_menuCamera != null) return;
            _menuCamera = SurveillanceStage.CreateCamera(transform, "MenuSurveillanceCamera");
            Debug.Log("[MainMenu] 监控探头已启用（Perspective / FOV 55 / 多机位 Tab 切换）。");
        }

        void RefreshCameraLabel()
        {
            if (_cycleText == null) return;
            int alive = CountLivingNpcs();
            _cycleText.text = "LOOP  " + Mathf.Max(0, _roundIndex).ToString("00")
                              + "   " + Localization.T("存活名单") + " " + alive
                              + "   " + SurveillanceStage.CurrentCameraLabel;
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------

        void BuildUI()
        {
            _canvas = UIOverlayUtil.CreateCanvas("MainMenu_Canvas", 200, transform);
            _canvas.transform.SetParent(transform, false);

            // 红色监控滤镜 + 暗角。
            Image tint = UIOverlayUtil.CreateImage(_canvas.transform, "RedTint", new Color(0.55f, 0.02f, 0.02f, 0.05f));
            tint.transform.SetAsLastSibling();

            GameObject scanGo = new GameObject("Scanlines", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            scanGo.transform.SetParent(_canvas.transform, false);
            SurveillanceStage.Stretch(scanGo.GetComponent<RectTransform>());
            _scanlines = scanGo.GetComponent<RawImage>();
            _scanlines.texture = SurveillanceStage.BuildScanlineTexture();
            _scanlines.uvRect = new Rect(0f, 0f, 1f, 260f);
            _scanlines.color = new Color(1f, 1f, 1f, 0.16f);
            _scanlines.raycastTarget = false;

            GameObject noiseGo = new GameObject("Noise", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            noiseGo.transform.SetParent(_canvas.transform, false);
            SurveillanceStage.Stretch(noiseGo.GetComponent<RectTransform>());
            _noise = noiseGo.GetComponent<RawImage>();
            _noiseTexture = new SurveillanceStage.NoiseTexture(96, 96);
            _noise.texture = _noiseTexture.Texture;
            _noise.uvRect = new Rect(0f, 0f, 3f, 3f);
            _noise.color = new Color(1f, 1f, 1f, 0.05f);
            _noise.raycastTarget = false;

            GameObject vignetteGo = new GameObject("Vignette", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            vignetteGo.transform.SetParent(_canvas.transform, false);
            SurveillanceStage.Stretch(vignetteGo.GetComponent<RectTransform>());
            _vignette = vignetteGo.GetComponent<RawImage>();
            _vignette.texture = SurveillanceStage.BuildVignetteTexture();
            _vignette.color = new Color(1f, 1f, 1f, 0.46f);
            _vignette.raycastTarget = false;

            BuildLeftColumn();
            BuildCornerHUD();

            RefreshMenuTexts();
            Localization.OnLanguageChanged += RefreshMenuTexts;
        }

        void OnDisable()
        {
            Localization.OnLanguageChanged -= RefreshMenuTexts;
        }

        void BuildLeftColumn()
        {
            // 左侧极淡渐变：让文字可读，但不出现死板的黑底区块。
            GameObject gradGo = new GameObject("MenuGradient", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            gradGo.transform.SetParent(_canvas.transform, false);
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

            _title = UIOverlayUtil.CreateText(_canvas.transform, "Title", "大限将至",
                new Vector2(0f, 1f), new Vector2(320f, -118f), new Vector2(520f, 82f), 62f,
                TextAlignmentOptions.Left, new Color(0.95f, 0.91f, 0.86f, 1f));
            _title.fontStyle = FontStyles.Bold;
            _title.characterSpacing = 8f;

            _subtitle = UIOverlayUtil.CreateText(_canvas.transform, "Warning",
                "WARNING: SURVEILLANCE ACTIVE",
                new Vector2(0f, 1f), new Vector2(320f, -180f), new Vector2(520f, 22f), 12f,
                TextAlignmentOptions.Left, new Color(0.72f, 0.08f, 0.06f, 0.95f));
            _subtitle.characterSpacing = 9f;

            string[] labels = { "开始", "继续", "设置", "鸣谢", "退出" };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                Button button = SurveillanceStage.CreateTextButton(_canvas.transform, "MenuButton_" + labels[i],
                    Localization.T(labels[i]), new Vector2(230f, -250f - i * 56f),
                    new Vector2(340f, 46f), () => OnMenuButton(index));
                _menuButtons.Add(button);
                if (labels[i] == "继续")
                {
                    _continueButton = button;
                    _continueLabel = button.GetComponentInChildren<TextMeshProUGUI>();
                }
            }

            _rosterText = UIOverlayUtil.CreateText(_canvas.transform, "Roster", string.Empty,
                new Vector2(0f, 0f), new Vector2(320f, 90f), new Vector2(520f, 60f), 16f,
                TextAlignmentOptions.BottomLeft, new Color(0.80f, 0.75f, 0.70f, 0.85f));
            _rosterText.characterSpacing = 3f;
            _rosterText.lineSpacing = 10f;
        }

        void BuildCornerHUD()
        {
            _rec = UIOverlayUtil.CreateText(_canvas.transform, "REC", "● REC",
                new Vector2(0f, 1f), new Vector2(150f, -38f), new Vector2(220f, 30f), 17f,
                TextAlignmentOptions.Left, new Color(0.92f, 0.08f, 0.06f, 1f));
            _timecode = UIOverlayUtil.CreateText(_canvas.transform, "Timecode", string.Empty,
                new Vector2(1f, 1f), new Vector2(-190f, -46f), new Vector2(360f, 34f), 18f,
                TextAlignmentOptions.Right, new Color(0.82f, 0.84f, 0.82f, 0.9f));
            _cycleText = UIOverlayUtil.CreateText(_canvas.transform, "CycleText", string.Empty,
                new Vector2(1f, 0f), new Vector2(-210f, 44f), new Vector2(560f, 34f), 16f,
                TextAlignmentOptions.Right, new Color(0.70f, 0.66f, 0.62f, 0.85f));

            // 当前阶段：监控画面右下角，一眼能看出这一轮走到哪一步。
            _phaseText = UIOverlayUtil.CreateText(_canvas.transform, "PhaseText", string.Empty,
                new Vector2(1f, 0f), new Vector2(-210f, 78f), new Vector2(560f, 40f), 24f,
                TextAlignmentOptions.Right, new Color(0.95f, 0.78f, 0.55f, 0.95f));
            _phaseText.fontStyle = FontStyles.Bold;
            _phaseText.characterSpacing = 2f;

            // 公聊滚动字幕：右侧中部，最多保留 6 行。
            _transcriptText = UIOverlayUtil.CreateText(_canvas.transform, "Transcript", string.Empty,
                new Vector2(1f, 0.5f), new Vector2(-230f, 10f), new Vector2(640f, 300f), 18f,
                TextAlignmentOptions.BottomRight, new Color(0.90f, 0.88f, 0.84f, 0.92f));
            _transcriptText.lineSpacing = 8f;
            _transcriptText.enableWordWrapping = true;
        }

        void RefreshMenuTexts()
        {
            if (_title == null) return;
            _title.text = Localization.T("大限将至");
            // 英文标题 "HILBERT LOOP" 比中文长得多，缩字号避免折行压到警告行。
            _title.fontSize = Localization.IsEnglish ? 42f : 62f;
            _subtitle.text = "WARNING: SURVEILLANCE ACTIVE";
            string[] labels = { "开始", "继续", "设置", "鸣谢", "退出" };
            for (int i = 0; i < _menuButtons.Count && i < labels.Length; i++)
            {
                TextMeshProUGUI label = _menuButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = Localization.T(labels[i]);
            }
            bool canContinue = SaveManager.CanContinue;
            if (_continueButton != null) _continueButton.interactable = canContinue;
            if (_continueLabel != null) _continueLabel.color = canContinue
                ? new Color(0.86f, 0.82f, 0.78f, 0.95f)
                : new Color(0.40f, 0.36f, 0.34f, 0.85f);
            ApplyPhaseText();
            RefreshTranscript();
            RefreshRosterText();
            RefreshCameraLabel();
        }

        void OnMenuButton(int index)
        {
            switch (index)
            {
                case 0: GameMode.StartNewGame(); break;
                case 1:
                    if (SaveManager.CanContinue)
                        GameMode.ContinueGame();
                    break;
                case 2: SettingsUI.Show(); break;
                case 3: CreditsUI.Show(); break;
                case 4:
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 监控画面 / 无缝残缺循环
        // ------------------------------------------------------------------

        IEnumerator MenuLoopRoutine()
        {
            yield return new WaitForSecondsRealtime(firstRoundDelay);

            // 主菜单演示的是完整的一轮游戏流程：
            // 醒来 → 圆桌集合 → 公开讨论 → 投票 → 天亮轮回（号码顺移）。
            // 投票出局的人是随机的，不再固定演「1 号杀 9 号」。
            _roster = InitialRoster();

            while (GameMode.MenuActive)
            {
                _roundIndex++;
                yield return RunFullLoopRound();
                yield return new WaitForSecondsRealtime(aftermathSeconds);
            }
        }

        static List<int> InitialRoster()
        {
            var roster = new List<int>();
            for (int n = 1; n <= CycleState.MaxLoopNumber; n++) roster.Add(n);
            return roster;
        }

        IEnumerator RunFullLoopRound()
        {
            // ---- ① 醒来 ----
            SetPhase("醒来");
            SetCameraPreset(6); // 东环望厅：能看见房门打开、人从环廊走进大厅
            _transcript.Clear();
            RefreshTranscript();

            yield return SpawnCast(_roster);

            List<NPCController> cast = LivingNpcs();
            for (int i = 0; i < cast.Count; i++)
            {
                cast[i].WakeupNow();
                yield return new WaitForSecondsRealtime(0.9f);
            }
            // 最后一个 NPC 错峰 0.9s 后被唤醒，起身动画还要 0.9s；
            // 再等 3.6s 才集合，避免有人还在床上就被下达寻路目标。
            yield return new WaitForSecondsRealtime(3.6f);

            // ---- ② 圆桌集合 ----
            SetPhase("圆桌集合");
            SetCameraPreset(0); // 大厅全景：圆桌与椅子全部入镜
            yield return GatherAtTable(cast);

            // ---- ③ 公开讨论 ----
            SetPhase("公开讨论");
            yield return PublicDebate(cast);

            // ---- ④ 投票 ----
            SetPhase("投票阶段");
            NPCController victim = null;
            yield return VoteAndExecute(cast, v => victim = v);

            // ---- ⑤ 轮回 ----
            SetPhase("轮回", Localization.T("天亮，号码顺移"));
            yield return Reincarnate(victim);

            Debug.Log($"[MainMenu] 第 {_roundIndex} 轮完整流程结束，下一轮名册：{string.Join("、", _roster)}。");
        }

        IEnumerator SpawnCast(List<int> roster)
        {
            NPCFactory factory = FindObjectOfType<NPCFactory>();
            if (factory == null)
            {
                Debug.LogError("[MainMenu] 场景缺少 NPCFactory。");
                yield break;
            }

            CycleState.playerNumber = 0;
            CycleState.kill9Executed = false;
            CycleState.currentKnownMax = 9;
            factory.ResetForNewLoop(roster);
            ApplyMenuCast(factory);

            RefreshRosterText();
            RefreshCameraLabel();
            Debug.Log($"[MainMenu] 第 {_roundIndex} 轮推演，名册：{string.Join("、", roster)}。");
            yield return null;
            EnsureMenuNpcLayers(factory);
        }

        void ConfigureMenuNpc(NPCController npc)
        {
            // 菜单不对话、不主动私聊、不跑暗杀脚本、不调用 LLM。
            // 行为树整体停用：菜单里的移动由本控制器统一指挥（走到椅子、坐下），
            // 否则 NPC 会一边被叫去开会、一边自己满屋乱走。
            if (npc.dialogue != null) npc.dialogue.enabled = false;
            if (npc.behaviourTree != null) npc.behaviourTree.enabled = false;
            NPCSocialAgent social = npc.GetComponent<NPCSocialAgent>();
            if (social != null) social.enabled = false;
            NPCHostileAgent hostile = npc.GetComponent<NPCHostileAgent>();
            if (hostile != null) hostile.enabled = false;
            NPCAssassin assassin = npc.GetComponent<NPCAssassin>();
            if (assassin != null) assassin.enabled = false;
            NPCVision vision = npc.GetComponent<NPCVision>();
            if (vision != null) vision.enabled = false;
            NPCHearing hearing = npc.GetComponent<NPCHearing>();
            if (hearing != null) hearing.enabled = false;
            MonitorTracked tracked = npc.GetComponent<MonitorTracked>();
            if (tracked != null)
            {
                tracked.hideDotInRooms = false;
                tracked.dotColor = new Color(1f, 0.08f, 0.06f, 1f);
            }
        }

        void ApplyMenuCast(NPCFactory factory)
        {
            if (factory == null) return;
            IReadOnlyList<NPCController> spawned = factory.Spawned;
            for (int i = 0; i < spawned.Count; i++)
            {
                NPCController npc = spawned[i];
                if (npc == null) continue;
                ConfigureMenuNpc(npc);
                // 不在这里唤醒：RunFullLoopRound 会错峰唤醒，让「醒来」这一段看得见。
            }
            EnsureMenuNpcLayers(factory);
        }

        IEnumerator GatherAtTable(List<NPCController> cast)
        {
            // 先走到自己椅子后方，再坐下。最多等 9 秒；超时的人直接落座（不卡主菜单）。
            for (int i = 0; i < cast.Count; i++)
            {
                NPCController npc = cast[i];
                Transform chair = FindChair(npc.Number);
                if (chair == null || npc.navigator == null) continue;
                // 椅子现在是寻路障碍：站位要退到椅背外侧 0.9m，
                // 否则 0.3m 半径的碰撞胶囊会落在椅子的碰撞盒里，永远“到不了”。
                Vector3 stand = chair.position - chair.forward * 0.9f;
                stand.y = npc.transform.position.y;
                npc.navigator.enabled = true;
                npc.navigator.MoveTo(stand);
            }

            float elapsed = 0f;
            while (elapsed < 9f)
            {
                bool allArrived = true;
                for (int i = 0; i < cast.Count; i++)
                {
                    NPCController npc = cast[i];
                    if (npc == null || npc.isDead) continue;
                    if (npc.navigator == null || !npc.navigator.IsArrived) { allArrived = false; break; }
                }
                if (allArrived) break;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            for (int i = 0; i < cast.Count; i++)
            {
                NPCController npc = cast[i];
                if (npc == null || npc.isDead) continue;
                Transform chair = FindChair(npc.Number);
                npc.EnterMeetingMode();
                if (chair != null)
                {
                    Vector3 seat = chair.position + chair.forward * 0.05f;
                    seat.y = chair.position.y + 0.9f;
                    npc.PlaceAtMeetingSeat(seat, chair.rotation);
                }
            }

            yield return new WaitForSecondsRealtime(0.7f);
        }

        IEnumerator PublicDebate(List<NPCController> cast)
        {
            string[] bodies =
            {
                "我出去过一次，走到圆桌看了看，又试了试其他房门。",
                "我房间的桌上有一把匕首，刀柄上有干涸的血迹。",
                "如果每个人的房间都一样，那这套东西不是给某一个人准备的。",
                "监控把每个人出门和回房都拍下来了。谁出去过、什么时候回来，你们自己看。",
                "如果今天一定要投一个人，我建议先从出门次数最多、路线又说不清的人开始。"
            };

            if (cast.Count == 0) yield break;

            for (int i = 0; i < bodies.Length; i++)
            {
                NPCController speaker = cast[i % cast.Count];
                AddTranscript(speaker.Number + "号：" + bodies[i]);
                yield return new WaitForSecondsRealtime(5f);
            }

            // 本轮 9 号不在场：补一句「缺座」的线索，让观众知道被投掉的人不会回来。
            bool ninthAlive = false;
            for (int i = 0; i < cast.Count; i++)
                if (cast[i] != null && !cast[i].isDead && cast[i].Number == 9) ninthAlive = true;
            if (!ninthAlive)
            {
                AddTranscript("9号没有来。他的椅子空着。");
                yield return new WaitForSecondsRealtime(4.5f);
            }
        }

        IEnumerator VoteAndExecute(List<NPCController> cast, System.Action<NPCController> onVictim)
        {
            var candidates = new List<NPCController>();
            for (int i = 0; i < cast.Count; i++)
                if (cast[i] != null && !cast[i].isDead) candidates.Add(cast[i]);

            if (candidates.Count == 0) yield break;

            NPCController victim = candidates[_rng.Next(candidates.Count)];
            int votes = Mathf.Max(2, Mathf.CeilToInt(candidates.Count * 0.6f));

            string tally = Localization.IsEnglish
                ? "VOTES  ·  No. " + victim.Number + "  —  " + votes + " votes"
                : "投票结果  ·  " + victim.Number + "号  —  " + votes + "票";
            if (_phaseText != null) _phaseText.text = Localization.T("投票阶段") + "   " + tally;

            yield return new WaitForSecondsRealtime(4.5f);

            victim.ExecuteByVote();
            onVictim?.Invoke(victim);

            if (SFXManager.Instance != null) SFXManager.Instance.PlayBellToll(0.5f);
            RefreshRosterText();
            RefreshCameraLabel();
            yield return new WaitForSecondsRealtime(2.5f);
        }

        IEnumerator Reincarnate(NPCController victim)
        {
            // 存活者号码 +1；超过 9 号的人离开这栋房子；新的复制体补入 1 号。
            // 人数降到 5 人及以下时，这一轮循环闭合，重新从完整的 1~9 开始。
            var survivors = new List<int>();
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.Number <= 0) continue;
                survivors.Add(npc.Number);
            }
            survivors.Sort();

            var next = new List<int>();
            for (int i = 0; i < survivors.Count; i++)
            {
                int shifted = survivors[i] + 1;
                if (shifted <= CycleState.MaxLoopNumber && !next.Contains(shifted)) next.Add(shifted);
            }
            if (!next.Contains(1)) next.Add(1);
            next.Sort();

            _roster = next.Count <= 5 ? InitialRoster() : next;

            SetCameraPreset(1); // 大厅反向，给轮回一个换镜
            AddTranscript("天亮，号码顺移。");
            yield return new WaitForSecondsRealtime(4f);

            Debug.Log($"[MainMenu] 出局：{(victim != null ? victim.Number + "号" : "无")}；" +
                      $"天亮名册：{string.Join("、", _roster)}。");
        }

        void SetPhase(string key, string suffix = null)
        {
            _phaseKey = key;
            _phaseSuffix = suffix;
            ApplyPhaseText();
        }

        void ApplyPhaseText()
        {
            if (_phaseText == null) return;
            _phaseText.text = Localization.T(_phaseKey)
                              + (string.IsNullOrEmpty(_phaseSuffix) ? string.Empty : "   ·   " + _phaseSuffix);
        }

        void AddTranscript(string zh)
        {
            _transcript.Add(zh);
            while (_transcript.Count > 6) _transcript.RemoveAt(0);
            RefreshTranscript();
        }

        void RefreshTranscript()
        {
            if (_transcriptText == null) return;
            var lines = new List<string>();
            for (int i = 0; i < _transcript.Count; i++)
            {
                string line = _transcript[i];
                if (!Localization.IsEnglish)
                {
                    lines.Add(line);
                    continue;
                }

                // "3号：正文" 这种运行期拼出来的台词，整句不在词典里；
                // 拆成「号码前缀 + 正文」分别翻译，英文才不会是半句中文。
                int split = line.IndexOf('：');
                if (split > 0)
                {
                    string head = Localization.PolishEnglish(line.Substring(0, split));
                    string body = Localization.T(line.Substring(split + 1));
                    lines.Add(head + ": " + body);
                }
                else
                {
                    lines.Add(Localization.TranslateDialogue(line));
                }
            }
            _transcriptText.text = string.Join("\n", lines);
        }

        List<NPCController> LivingNpcs()
        {
            var list = new List<NPCController>();
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.Number <= 0) continue;
                list.Add(npc);
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }

        int CountLivingNpcs()
        {
            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && npc.Number > 0) count++;
            }
            return count;
        }

        static Transform FindChair(int number)
        {
            GameObject chair = GameObject.Find("Chair_" + number);
            return chair != null ? chair.transform : null;
        }

        void SetCameraPreset(int index)
        {
            if (SurveillanceStage.CameraCount == 0) return;
            SurveillanceStage.SelectPreset(index);
            if (_menuCamera != null) SurveillanceStage.ApplyTo(_menuCamera);
            RefreshCameraLabel();
        }

        static void EnsureMenuNpcLayers(NPCFactory factory)
        {
            if (factory == null) return;
            IReadOnlyList<NPCController> spawned = factory.Spawned;
            // 30 层：监控相机能看到；31 层才会被监控相机剔除。
            for (int i = 0; i < spawned.Count; i++)
            {
                NPCController npc = spawned[i];
                if (npc == null) continue;
                Transform[] parts = npc.GetComponentsInChildren<Transform>(true);
                for (int j = 0; j < parts.Length; j++)
                {
                    if (parts[j] != null) parts[j].gameObject.layer = 30;
                }
            }
        }

        void RefreshRosterText()
        {
            if (_rosterText == null) return;

            // 只给当前还活着的号码，不做任何“空缺 / 出局 / 席位”的解释。
            // 玩家自己数缺了谁，这是主菜单唯一的信息。
            List<NPCController> living = LivingNpcs();
            var labels = new List<string>();
            for (int i = 0; i < living.Count; i++)
                labels.Add(Localization.IsEnglish ? "No. " + living[i].Number : living[i].Number + "号");

            _rosterText.text = Localization.T("存活名单") + "\n" + string.Join("   ", labels);
        }


    }
}
