using UnityEngine;
using UnityEngine.UI;
using ChessDark.Core;
using ChessDark.Logic;
using ChessDark.AI;
using ChessDark.Multiplayer;
using System.Collections;
using System.Collections.Generic;

namespace ChessDark.Unity
{
    public class GameManager : MonoBehaviour
    {
        public enum GamePhase { MainMenu, PlayerArrangement, WaitingOpponent, Playing, GameOver }

        [Header("References")]
        public BoardView boardView;
        public BoardInteraction boardInteraction;
        public Canvas uiCanvas;
        public Text statusText;
        public GameObject gameOverPanel;
        public Text gameOverText;
        public Button continueButton;
        public Button restartButton;
        public DisguiseLabels disguiseLabels;
        public GiantBoardCameraFramer cameraFramer;

        [Header("Game Settings")]
        public PieceColor playerColor = PieceColor.White;
        public bool standardOpening = true;
        public int aiSampleCount = 32;
        public int aiSearchDepth = 3;
        public int aiTimeLimitMs = 2500;
        public int aiMctsDeterminizations = 64;
        public int aiMctsTimeLimitMs = 3500;
        public int aiHybridPonderCandidates = 5;
        public int aiFastEmmMs = 1400;
        // 16 采样是强度与深度的平衡点；12 线程并行下实测平均主深度约 4.1 层。
        public int aiFastEmmSamples = 16;
        public int aiFastEmmDepth = 6;

        const int GameSeed = 42;

        private AIEngineType aiEngine;

        private GamePhase currentPhase;
        private Board board;
        private GameEngine gameEngine;
        private AIPlayer aiPlayer;
        private Position? selectedPiece;
        private List<Move> legalMovesForSelected;
        private Coroutine aiCoroutine;
        private Coroutine playerMoveCoroutine;
        private Coroutine remoteMoveCoroutine;
        private Position? draggingFrom;
        private MainMenuUI mainMenu;
        private GameObject inGameMenuButton;
        private GameObject disguiseToggleRoot;
        private bool isPaused;
        private bool isOnline;
        private IOnlineMatchController onlineController;
        private List<(Position pos, PieceType visibleType)> remoteArrangement;
        private bool remoteArrangementReceived;
        private FormationPlanner formationPlanner;
        private Coroutine formationPlannerCoroutine;
        private PlayerProfileStore profileStore;
        private GameRecorder gameRecorder;
        private bool gameResultRecorded;
        private GameObject promotionPanel;
        private List<Move> promotionChoices;
        private PonderPlanner ponderPlanner;
        private float lastPlayerMoveAt;
        private long lastAiResponseMs;

        // 最近一次“玩家落子 → AI 出招”的实际等待，供剧情/调试代码读取，不显示在 UI 上。
        public long LastAiResponseMs => lastAiResponseMs;
        public bool LastPonderHit => aiPlayer is HybridAI hybrid && hybrid.LastPonderHit;

        public bool IsOnlineMatchActive =>
            isOnline && (currentPhase == GamePhase.PlayerArrangement
                         || currentPhase == GamePhase.WaitingOpponent
                         || currentPhase == GamePhase.Playing
                         || currentPhase == GamePhase.GameOver);

        void OnApplicationQuit()
        {
            if (ponderPlanner != null) ponderPlanner.Stop();
            if (!isOnline && profileStore != null) profileStore.Save();
        }

        void OnDestroy()
        {
            if (ponderPlanner != null) ponderPlanner.Stop();
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus && !isOnline && profileStore != null) profileStore.Save();
        }

        void Start()
        {
            AIPlayer.MobileProfile = Application.platform == RuntimePlatform.Android;
            if (AIPlayer.MobileProfile)
            {
                AIPlayer.EnableParallelSearch = false;
                AIPlayer.ParallelThreads = 1;
                // 手机长屏按高度缩放，保证 1080 的纵向布局完整；PC 仍用场景里的宽度匹配。
                if (uiCanvas != null)
                {
                    var scaler = uiCanvas.GetComponent<CanvasScaler>();
                    if (scaler != null)
                    {
                        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        scaler.referenceResolution = new Vector2(1920f, 1080f);
                        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                        scaler.matchWidthOrHeight = 1f;
                    }
                }
                Debug.Log("[GameManager] 检测到 Android：AI 降级为 6 采样 / 深度 4 / 900ms，单线程无 Ponder。");
            }

            legalMovesForSelected = new List<Move>();
            if (boardView == null) boardView = FindObjectOfType<BoardView>();
            if (boardInteraction == null) boardInteraction = FindObjectOfType<BoardInteraction>();
            if (disguiseLabels == null) disguiseLabels = FindObjectOfType<DisguiseLabels>();
            if (cameraFramer == null) cameraFramer = FindObjectOfType<GiantBoardCameraFramer>();

            if (boardInteraction != null)
            {
                boardInteraction.PieceSelected -= OnPieceSelected;
                boardInteraction.TileClicked -= OnTileClicked;
                boardInteraction.ArrangementDragStarted -= OnArrangementDragStarted;
                boardInteraction.ArrangementDragMoved -= OnArrangementDragMoved;
                boardInteraction.ArrangementDragEnded -= OnArrangementDragEnded;

                boardInteraction.PieceSelected += OnPieceSelected;
                boardInteraction.TileClicked += OnTileClicked;
                boardInteraction.ArrangementDragStarted += OnArrangementDragStarted;
                boardInteraction.ArrangementDragMoved += OnArrangementDragMoved;
                boardInteraction.ArrangementDragEnded += OnArrangementDragEnded;
            }
            if (continueButton != null)
            {
                continueButton.onClick.RemoveListener(OnContinueClicked);
                continueButton.onClick.AddListener(OnContinueClicked);
            }
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(OnRestartClicked);
                restartButton.onClick.AddListener(OnRestartClicked);
            }

            AIPlayer.Logger = Debug.Log;
#if UNITY_WEBGL && !UNITY_EDITOR
            aiEngine = AIEngineType.Expectiminimax;
#else
            aiEngine = AIEngineType.Predator;
#endif
            BuildMainMenu();
            RuntimeUI.ApplyFontToCanvas(uiCanvas);
            ShowMainMenu();
        }

        void BuildMainMenu()
        {
            if (uiCanvas == null)
            {
                Debug.LogError("[GameManager] 缺少 uiCanvas，无法创建主菜单");
                return;
            }

            mainMenu = new MainMenuUI(uiCanvas, aiEngine);
            mainMenu.SinglePlayerSelected = OnSinglePlayerSelected;
            mainMenu.EngineChanged = OnAIEngineChanged;
            mainMenu.ResumeRequested = ResumeGameFromPause;
            mainMenu.ExitToMenuRequested = ReturnToMainMenuFromPause;
#if !DISABLE_MULTIPLAYER
            mainMenu.CreateRoomRequested = OnCreateRoomRequested;
            mainMenu.JoinRoomRequested = OnJoinRoomRequested;
#endif

            var font = RuntimeUI.ResolveFont(uiCanvas);
            var menuButton = RuntimeUI.CreateButton(uiCanvas.transform, "InGameMenuButton", "菜单",
                Vector2.zero, new Vector2(120f, 52f), font, OnMenuButtonClicked);
            var rect = menuButton.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(24f, 24f);
            menuButton.transform.SetAsLastSibling();
            inGameMenuButton = menuButton.gameObject;

            if (statusText != null)
            {
                statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
                statusText.rectTransform.sizeDelta = new Vector2(900f, statusText.rectTransform.sizeDelta.y);
            }

            disguiseToggleRoot = GameObject.Find("ShowDisguiseToggle");
        }

        void ShowMainMenu()
        {
            isPaused = false;
            ClosePromotionChooser();
            StopPonder();
            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (playerMoveCoroutine != null) { StopCoroutine(playerMoveCoroutine); playerMoveCoroutine = null; }
            if (remoteMoveCoroutine != null) { StopCoroutine(remoteMoveCoroutine); remoteMoveCoroutine = null; }
            StopFormationPlanner();
            formationPlanner = null;

            ShutdownOnlineController();
            ResetOnlineState();

            currentPhase = GamePhase.MainMenu;
            draggingFrom = null;
            ClearSelection();
            SetBoardInput(false);
            if (boardInteraction != null) boardInteraction.arrangementMode = false;

            if (continueButton != null) continueButton.gameObject.SetActive(false);
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (statusText != null) statusText.gameObject.SetActive(false);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(false);
            if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(false);
            if (mainMenu != null) mainMenu.Show();
        }

        void OnMenuButtonClicked()
        {
            if (currentPhase == GamePhase.MainMenu || isPaused) return;
            ShowPauseMenu();
        }

        void ShowPauseMenu()
        {
            isPaused = true;
            StopPonder();
            draggingFrom = null;
            ClearSelection();
            SetBoardInput(false);

            if (continueButton != null) continueButton.gameObject.SetActive(false);
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (statusText != null) statusText.gameObject.SetActive(false);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(false);
            if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(false);
            if (mainMenu != null) mainMenu.ShowPause();
        }

        void ResumeGameFromPause()
        {
            if (!isPaused) return;
            isPaused = false;
            if (mainMenu != null) mainMenu.Hide();

            if (currentPhase == GamePhase.MainMenu) return;

            if (inGameMenuButton != null) inGameMenuButton.SetActive(true);

            switch (currentPhase)
            {
                case GamePhase.PlayerArrangement:
                    if (continueButton != null) continueButton.gameObject.SetActive(true);
                    if (statusText != null) statusText.gameObject.SetActive(true);
                    if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(true);
                    SetBoardInput(true);
                    break;

                case GamePhase.GameOver:
                    if (statusText != null) statusText.gameObject.SetActive(true);
                    if (gameOverPanel != null) gameOverPanel.SetActive(true);
                    SetBoardInput(false);
                    break;

                case GamePhase.WaitingOpponent:
                    if (statusText != null)
                    {
                        statusText.gameObject.SetActive(true);
                        statusText.text = remoteArrangementReceived
                            ? "正在同步双方布阵…"
                            : "布阵已提交 · 等待对手完成布阵…";
                    }
                    if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(true);
                    SetBoardInput(false);
                    break;

                case GamePhase.Playing:
                    if (statusText != null) statusText.gameObject.SetActive(true);
                    if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(true);
                    UpdateStatusText();
                    // 暂停时如果 AI/动画协程还在跑，让它自己走到 ContinueTurnFlow，避免重启搜索或打断动画。
                    if (playerMoveCoroutine == null && remoteMoveCoroutine == null && aiCoroutine == null)
                        ContinueTurnFlow();
                    else if (gameEngine != null && gameEngine.CurrentTurn == playerColor)
                        StartPonder();
                    break;
            }
        }

        void ReturnToMainMenuFromPause()
        {
            ShowMainMenu();
        }

        void OnAIEngineChanged(AIEngineType engine)
        {
            aiEngine = engine;
            Debug.Log($"[GameManager] AI 引擎切换为：{DescribeEngine(engine)}");
        }

        static string DescribeEngine(AIEngineType engine)
        {
            switch (engine)
            {
                case AIEngineType.MCTS: return "Determined MCTS（实验组）";
                case AIEngineType.Hybrid: return "Hybrid（MCTS + Expectiminimax 复核）";
                case AIEngineType.Predator: return "Predator（镜像信念 + 隐匿猎王）";
                default: return "Expectiminimax（基线）";
            }
        }

        void OnSinglePlayerSelected(PieceColor side)
        {
            playerColor = side;
            if (disguiseLabels != null) disguiseLabels.playerColor = side;
            if (cameraFramer != null) cameraFramer.SetViewFromBlackSide(side == PieceColor.Black);
            if (statusText != null) statusText.gameObject.SetActive(true);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(true);
            StartNewGame();
        }

#if !DISABLE_MULTIPLAYER
        void OnCreateRoomRequested()
        {
            StartOnlineSession(null);
        }

        void OnJoinRoomRequested(string roomCode)
        {
            StartOnlineSession(roomCode);
        }

        void StartOnlineSession(string joinCode)
        {
            isPaused = false;
            ShutdownOnlineController();
            ResetOnlineState();
            StopPonder();
            ponderPlanner = null;

            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (playerMoveCoroutine != null) { StopCoroutine(playerMoveCoroutine); playerMoveCoroutine = null; }
            if (remoteMoveCoroutine != null) { StopCoroutine(remoteMoveCoroutine); remoteMoveCoroutine = null; }
            StopFormationPlanner();
            formationPlanner = null;

            isOnline = true;
            currentPhase = GamePhase.MainMenu;
            ClearSelection();
            SetBoardInput(false);
            if (boardInteraction != null) boardInteraction.arrangementMode = false;

            if (mainMenu != null) mainMenu.Hide();
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (continueButton != null) continueButton.gameObject.SetActive(false);
            if (statusText != null) statusText.gameObject.SetActive(false);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(false);
            if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(false);

            var controllerObject = new GameObject("MultiplayerController");
            var controller = controllerObject.AddComponent<MultiplayerController>();
            onlineController = controller;
            controller.Initialize(this, joinCode);
        }
#endif

        void StartNewGame()
        {
            isPaused = false;
            ClosePromotionChooser();
            ShutdownOnlineController();
            ResetOnlineState();
            StopPonder();

            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (playerMoveCoroutine != null) { StopCoroutine(playerMoveCoroutine); playerMoveCoroutine = null; }
            if (remoteMoveCoroutine != null) { StopCoroutine(remoteMoveCoroutine); remoteMoveCoroutine = null; }
            StopFormationPlanner();

            if (mainMenu != null) mainMenu.Hide();
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (statusText != null) statusText.gameObject.SetActive(true);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(true);
            if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(true);
            if (disguiseLabels != null) disguiseLabels.playerColor = playerColor;
            if (cameraFramer != null) cameraFramer.SetViewFromBlackSide(playerColor == PieceColor.Black);

            PieceColor aiColor = playerColor == PieceColor.White ? PieceColor.Black : PieceColor.White;

            gameEngine = new GameEngine(GameSeed);
            gameEngine.InitializeGame(standardOpening);
            board = gameEngine.Board;
            gameRecorder = null;

            if (profileStore == null) profileStore = PlayerProfileStore.Load();
            gameResultRecorded = false;
            var preGameTendency = profileStore.CreateSnapshot();

            int effectiveWorlds = aiMctsDeterminizations;
            int effectiveMctsTimeLimit = aiMctsTimeLimitMs;
            MctsAI.ApplyPlatformPreset(ref effectiveWorlds, ref effectiveMctsTimeLimit);

            if (aiEngine == AIEngineType.MCTS)
            {
                var mcts = new MctsAI(aiColor, GameSeed, effectiveWorlds, effectiveMctsTimeLimit);
                mcts.SetPlayerTendency(preGameTendency);
                aiPlayer = mcts;
                Debug.Log($"[GameManager] AI 引擎：Determined MCTS · 虚构世界 {effectiveWorlds} · 每步 {effectiveMctsTimeLimit}ms");
            }
            else if (aiEngine == AIEngineType.Hybrid)
            {
                // 16 采样 × 12 线程：实测平均主深度约 4.1 层；12 采样会掉 20 个胜率点，不能为深度牺牲世界数。
                int fastSamples = Mathf.Clamp(aiFastEmmSamples, 4, 32);
                int fastDepth = Mathf.Clamp(aiFastEmmDepth, 3, 6);
                int fastMs = Mathf.Max(600, aiFastEmmMs);
                if (AIPlayer.MobileProfile)
                {
                    fastSamples = AIPlayer.MobileSampleCount;
                    fastDepth = AIPlayer.MobileSearchDepth;
                    fastMs = AIPlayer.MobileTimeBudgetMs;
                }

                var hybrid = new HybridAI(aiColor, GameSeed, fastSamples, fastDepth, fastMs);
                hybrid.SetPlayerTendency(preGameTendency);
                aiPlayer = hybrid;
                Debug.Log($"[GameManager] AI 引擎：Fast Expectiminimax · 采样 {fastSamples} · 深度 {fastDepth} · " +
                          $"每步 {fastMs}ms（Ponder 命中则 0ms）· 并行 {AIPlayer.EnableParallelSearch} / {AIPlayer.ParallelThreads} 线程");
            }
            else if (aiEngine == AIEngineType.Predator)
            {
                int fastSamples = Mathf.Clamp(aiFastEmmSamples, 4, 32);
                int fastDepth = Mathf.Clamp(aiFastEmmDepth, 3, 6);
                int fastMs = Mathf.Max(600, aiFastEmmMs);
                if (AIPlayer.MobileProfile)
                {
                    fastSamples = AIPlayer.MobileSampleCount;
                    fastDepth = AIPlayer.MobileSearchDepth;
                    fastMs = AIPlayer.MobileTimeBudgetMs;
                }

                var predator = new PredatorAI(aiColor, GameSeed, fastSamples, fastDepth, fastMs);
                predator.SetPlayerTendency(preGameTendency);
                aiPlayer = predator;
                Debug.Log($"[GameManager] AI 引擎：Predator · 采样 {fastSamples} · 深度 {fastDepth} · " +
                          $"每步 {fastMs}ms · 镜像信念隐匿/猎王评分 · 并行 {AIPlayer.EnableParallelSearch} / {AIPlayer.ParallelThreads} 线程");
            }
            else
            {
                int effectiveSampleCount = aiSampleCount;
                int effectiveSearchDepth = aiSearchDepth;
                AIPlayer.ApplyPlatformPreset(ref effectiveSampleCount, ref effectiveSearchDepth);
                int effectiveTimeLimit = AIPlayer.MobileProfile
                    ? Mathf.Min(aiTimeLimitMs, AIPlayer.MobileTimeBudgetMs)
                    : aiTimeLimitMs;
                var expectiminimax = new ExpectiminimaxAI(aiColor, GameSeed, effectiveSampleCount,
                    effectiveSearchDepth, effectiveTimeLimit);
                expectiminimax.SetPlayerTendency(preGameTendency);
                aiPlayer = expectiminimax;
                Debug.Log($"[GameManager] AI 引擎：Expectiminimax · 采样 {effectiveSampleCount} · 深度 {effectiveSearchDepth} · " +
                          $"每步 {effectiveTimeLimit}ms · 并行 {AIPlayer.EnableParallelSearch}");
            }

            // 玩家的本态藏在“标准布阵标签”后面，标签只是公开外壳，任何模式下都不能当先验。
            if (aiPlayer != null)
                aiPlayer.SetVisibleLabelTrust(0f);

            if (aiPlayer != null && gameEngine != null)
                aiPlayer.WouldCauseThreefold = m => gameEngine.WouldCauseThreefold(m);

            ponderPlanner = null;
            if (!AIPlayer.MobileProfile &&
                (aiEngine == AIEngineType.Hybrid || aiEngine == AIEngineType.Predator) &&
                aiPlayer is HybridAI fastAi)
            {
                int candidates = Mathf.Clamp(aiHybridPonderCandidates, 3, 5);
                int ponderDepth = Mathf.Max(8, fastAi.SearchDepth);
                int ponderMs = Mathf.Max(2000, fastAi.SearchTimeBudgetMs);
                ponderPlanner = new PonderPlanner(aiColor, fastAi.SampleCount, GameSeed + 101,
                    candidates, ponderMs, ponderDepth);
                ponderPlanner.SetPlayerTendency(preGameTendency);
                Debug.Log($"[GameManager] Ponder 直接缓存完整 EMM 答案：{candidates} 个预测落点 × " +
                          $"{ponderMs}ms（目标深度 {ponderDepth}），命中后 0ms 出招并继续后台深算。");
            }

            int duelGames = Application.platform == RuntimePlatform.WebGLPlayer ? 2 : 4;
            formationPlanner = new FormationPlanner(aiColor, preGameTendency, GameSeed + 17, duelGames);
            Debug.Log($"[GameManager] 开局选阵启动，玩家画像：{preGameTendency.Describe()}");

            if (boardView != null)
            {
                boardView.RestoreInitialPieces();
                boardView.SyncPieceDataFromBoard(board, playerColor);
            }

            StartPlayerArrangement();
        }

        void StartPlayerArrangement()
        {
            currentPhase = GamePhase.PlayerArrangement;
            draggingFrom = null;
            ClearSelection();

            if (boardInteraction != null)
            {
                boardInteraction.arrangementMode = true;
                boardInteraction.arrangementColor = playerColor;
            }
            SetBoardInput(true);

            if (continueButton != null) continueButton.gameObject.SetActive(true);
            if (statusText != null)
                statusText.text = AIPlayer.MobileProfile
                    ? "布阵阶段 · 手指拖动己方棋子交换位置，完成后点「开始游戏」"
                    : "布阵阶段 · 拖动己方棋子交换位置，完成后点「开始游戏」";

            if (formationPlanner != null)
            {
                StopFormationPlanner();
                formationPlannerCoroutine = StartCoroutine(RunFormationPlanner());
            }
        }

        IEnumerator RunFormationPlanner()
        {
            var planner = formationPlanner;
            if (planner == null) yield break;

            planner.Step(4);
            yield return null;

            while (planner == formationPlanner
                   && currentPhase == GamePhase.PlayerArrangement
                   && !planner.IsComplete)
            {
                planner.Step(4);
                yield return null;
            }

            formationPlannerCoroutine = null;
            if (planner.IsComplete)
                Debug.Log("[FormationPlanner] " + planner.LastSummary);
        }

        void StopFormationPlanner()
        {
            if (formationPlannerCoroutine != null)
            {
                StopCoroutine(formationPlannerCoroutine);
                formationPlannerCoroutine = null;
            }
        }

        void OnContinueClicked()
        {
            if (continueButton != null) continueButton.gameObject.SetActive(false);
            StartGame();
        }

        void StartGame()
        {
            if (currentPhase != GamePhase.PlayerArrangement) return;

            if (boardInteraction != null)
            {
                boardInteraction.arrangementMode = false;
                boardInteraction.ClearSelection();
            }

            if (isOnline)
            {
                var pieces = CollectPlayerPieces();
                if (pieces.Count > 0)
                    gameEngine.ApplyArrangement(playerColor, pieces);
                board = gameEngine.Board;

                if (continueButton != null) continueButton.gameObject.SetActive(false);

                currentPhase = GamePhase.WaitingOpponent;
                SetBoardInput(false);
                if (statusText != null)
                    statusText.text = remoteArrangementReceived
                        ? "正在同步双方布阵…"
                        : "布阵已提交 · 等待对手完成布阵…";

                if (onlineController != null)
                    onlineController.SubmitArrangement(pieces);

                TryStartOnlinePlay();
                return;
            }

            ApplyPlayerArrangement();

            PieceColor aiColor = playerColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            StopFormationPlanner();

            if (formationPlanner == null)
            {
                var tendency = profileStore != null ? profileStore.CreateSnapshot() : new PlayerTendency();
                int duelGames = Application.platform == RuntimePlatform.WebGLPlayer ? 2 : 4;
                formationPlanner = new FormationPlanner(aiColor, tendency, GameSeed + 17, duelGames);
            }

            var aiFormation = formationPlanner.GetBestAvailableAgainst(gameEngine.Board);
            if (aiFormation != null)
                gameEngine.ApplyFormation(aiFormation, aiColor);

            Debug.Log("[FormationPlanner] AI 开局阵型：" + (aiFormation != null ? aiFormation.Describe() : "标准阵")
                      + (formationPlanner.IsComplete
                          ? " · " + formationPlanner.LastSummary
                          : " · 布阵提前结束，使用当前最优候选"));

            aiPlayer.ObserveGameStart(gameEngine.Board);
            if (aiFormation != null)
            {
                aiPlayer.BeginOpeningPlan(aiFormation);
                Debug.Log("[FormationPlanner] 阵型已锁定：" + aiFormation.Describe() +
                          " · 本局不再更换，仅按玩家前 3 步切换开局战术");
            }

            currentPhase = GamePhase.Playing;
            gameRecorder = new GameRecorder();
            gameRecorder.Begin(playerColor, aiPlayer != null ? aiPlayer.GetType().Name : "AI",
                aiFormation != null ? aiFormation.Describe() : "标准阵",
                profileStore != null ? profileStore.CreateSnapshot().Describe() : "");
            UpdateStatusText();
            ContinueTurnFlow();
        }

        List<(Position pos, PieceType trueType, PieceType visibleType)> CollectPlayerPieces()
        {
            var pieces = new List<(Position pos, PieceType trueType, PieceType visibleType)>();
            if (boardView == null) return pieces;

            foreach (var data in boardView.GetAllPieceData())
            {
                if (data == null || data.pieceColor != playerColor) continue;
                pieces.Add((data.GetLogicalPosition(), data.trueType, data.visibleType));
            }

            return pieces;
        }

        void ApplyPlayerArrangement()
        {
            if (boardView == null) return;

            var pieces = CollectPlayerPieces();
            if (pieces.Count > 0)
                gameEngine.ApplyArrangement(playerColor, pieces, publicVisibleLabels: true);

            board = gameEngine.Board;
            // 玩家本态已藏到标准标签后，刷新缓存让显示与 AI 看到的公开外壳一致。
            if (boardView != null)
                boardView.SyncPieceDataFromBoard(board, playerColor);
        }

        void OnArrangementDragStarted(Position pos, PieceData pieceData)
        {
            if (currentPhase != GamePhase.PlayerArrangement) return;
            if (pieceData == null || pieceData.pieceColor != playerColor) return;

            draggingFrom = pos;
            if (boardView != null)
            {
                boardView.BeginArrangementDrag(pos);
                boardView.HighlightPositions(GetPlayerHomeSquares());
            }
        }

        void OnArrangementDragMoved(Vector3 boardPoint)
        {
            if (currentPhase != GamePhase.PlayerArrangement) return;
            if (boardView != null) boardView.UpdateArrangementDrag(boardPoint);
        }

        void OnArrangementDragEnded(Position? target)
        {
            if (!draggingFrom.HasValue) return;

            Position from = draggingFrom.Value;
            draggingFrom = null;

            if (target.HasValue && target.Value != from && IsPlayerHomeSquare(target.Value))
            {
                boardView.EndArrangementDrag(from, target.Value);
                if (statusText != null)
                    statusText.text = $"已交换 {Position.ToAlgebraic(from)} 与 {Position.ToAlgebraic(target.Value)} · 点「开始游戏」";
            }
            else
            {
                boardView.CancelArrangementDrag(from);
            }
        }

        void OnPieceSelected(Position pos, PieceData pieceData)
        {
            if (currentPhase != GamePhase.Playing) return;
            if (gameEngine.CurrentTurn != playerColor) return;
            if (pieceData == null) return;

            if (pieceData.pieceColor != playerColor)
            {
                if (selectedPiece.HasValue) TryMovePiece(selectedPiece.Value, pos);
                return;
            }

            selectedPiece = pos;
            legalMovesForSelected = gameEngine.GetLegalMoves().FindAll(m => m.From == pos);

            var targets = new List<Position>();
            foreach (var move in legalMovesForSelected) targets.Add(move.To);
            if (boardView != null)
            {
                boardView.ClearHighlights();
                boardView.HighlightPositions(targets);
            }

            if (statusText != null && pieceData.trueType != pieceData.visibleType)
                statusText.text = $"己方 {Position.ToAlgebraic(pos)} 本态：{pieceData.trueType.ToChinese()}（伪装：{pieceData.visibleType.ToChinese()}）";
        }

        void OnTileClicked(Position pos)
        {
            if (currentPhase != GamePhase.Playing) return;
            if (gameEngine.CurrentTurn != playerColor) return;
            if (selectedPiece.HasValue) TryMovePiece(selectedPiece.Value, pos);
        }

        void TryMovePiece(Position from, Position to)
        {
            var matches = legalMovesForSelected.FindAll(m => m.From == from && m.To == to);
            if (matches.Count == 0) { ClearSelection(); return; }
            // 升变格上同一 from/to 会有后/车/象/马四条走法，必须让玩家自己挑；
            // 旧代码 FindIndex 永远命中第一条（后），双车杀王之类的选择就没了。
            if (matches.Count > 1 && matches.Exists(m => m.PromotionType.HasValue))
            {
                ShowPromotionChooser(from, to, matches);
                return;
            }
            ExecuteMove(matches[0]);
        }

        void ShowPromotionChooser(Position from, Position to, List<Move> choices)
        {
            if (promotionPanel != null) Destroy(promotionPanel);
            SetBoardInput(false);
            if (boardView != null) boardView.ClearHighlights();

            promotionChoices = choices;

            var font = RuntimeUI.ResolveFont(uiCanvas);
            promotionPanel = new GameObject("PromotionChooser", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rootRect = promotionPanel.GetComponent<RectTransform>();
            rootRect.SetParent(uiCanvas.transform, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            var backdrop = promotionPanel.GetComponent<Image>();
            backdrop.color = new Color(0f, 0f, 0f, 0.55f);
            backdrop.raycastTarget = true;

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.SetParent(promotionPanel.transform, false);
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(560f, 260f);
            var panelImage = panelGo.GetComponent<Image>();
            panelImage.color = new Color(0.11f, 0.14f, 0.19f, 0.98f);
            panelImage.raycastTarget = true;

            var title = RuntimeUI.CreateText(panelGo.transform, "Title",
                $"{Position.ToAlgebraic(from)}→{Position.ToAlgebraic(to)} 升变为：", 30,
                new Color(0.97f, 0.93f, 0.84f, 1f), TextAnchor.MiddleCenter, font);
            var titleRect = title.GetComponent<RectTransform>();
            RuntimeUI.PlaceCentered(titleRect, new Vector2(0f, 82f), new Vector2(520f, 46f));

            var options = new[]
            {
                (PieceType.Queen, "后"), (PieceType.Rook, "车"),
                (PieceType.Bishop, "象"), (PieceType.Knight, "马")
            };
            for (int i = 0; i < options.Length; i++)
            {
                if (!choices.Exists(m => m.PromotionType == options[i].Item1)) continue;
                var chosen = options[i].Item1;
                RuntimeUI.CreateButton(panelGo.transform, "Promotion_" + options[i].Item2,
                    options[i].Item2, new Vector2(-153f + i * 102f, 10f), new Vector2(92f, 92f),
                    font, () => OnPromotionChosen(chosen));
            }

            RuntimeUI.CreateButton(panelGo.transform, "PromotionCancel", "取消",
                new Vector2(0f, -82f), new Vector2(120f, 44f), font, CancelPromotionChooser);

            promotionPanel.transform.SetAsLastSibling();
        }

        void OnPromotionChosen(PieceType type)
        {
            Move? chosen = null;
            if (promotionChoices != null)
            {
                int index = promotionChoices.FindIndex(m => m.PromotionType == type);
                if (index >= 0) chosen = promotionChoices[index];
            }
            ClosePromotionChooser();
            if (chosen.HasValue) ExecuteMove(chosen.Value);
            else ClearSelection();
        }

        void CancelPromotionChooser()
        {
            ClosePromotionChooser();
            ClearSelection();
            if (currentPhase == GamePhase.Playing && gameEngine != null
                && !gameEngine.IsGameOver && gameEngine.CurrentTurn == playerColor)
                SetBoardInput(true);
        }

        void ClosePromotionChooser()
        {
            if (promotionPanel != null) Destroy(promotionPanel);
            promotionPanel = null;
            promotionChoices = null;
        }

        void ExecuteMove(Move move)
        {
            if (playerMoveCoroutine != null) return;
            playerMoveCoroutine = StartCoroutine(PlayerMoveCoroutine(move));
        }

        IEnumerator PlayerMoveCoroutine(Move move)
        {
            SetBoardInput(false);
            lastPlayerMoveAt = Time.realtimeSinceStartup;
            StopPonder();
            try
            {
                MoveResult result;
                if (isOnline)
                {
                    result = gameEngine.ApplyOnlineMove(move, playerColor, playerColor);
                    board = gameEngine.Board;
                    if (onlineController != null) onlineController.SendMove(move);
                }
                else
                {
                    var preBoard = gameEngine.Board.Clone();
                    var movingVisible = preBoard.GetPiece(move.From)?.VisibleType ?? PieceType.Pawn;

                    result = gameEngine.MakeMove(move);
                    board = gameEngine.Board;

                    if (profileStore != null)
                        profileStore.RecordMove(move, result.MovingPiece, result.CapturedPiece != null);

                    aiPlayer.NotifyOpponentMove(move, movingVisible, result.LeakType, preBoard);
                    if (move.PromotionType.HasValue)
                        aiPlayer.NotifyPromotion(move.To, move.PromotionType);
                    RefreshLiveProfile();
                    if (gameRecorder != null)
                        gameRecorder.RecordMove(gameEngine.MoveCount, playerColor, move, preBoard, result, "HumanPlayer");
                }

                ClearSelection();

                if (boardView != null)
                    yield return boardView.AnimateMove(move, result);
                else
                    yield return null;

                if (result.GameOver)
                {
                    // 联机时 movers 侧只可能判定三次重复；吃王由被吃方广播 GameOver。
                    if (isOnline && result.Winner.HasValue && onlineController != null)
                        onlineController.SendGameOver(result.Winner.Value);
                    EndGame(result.Winner);
                    yield break;
                }

                UpdateStatusText();
                ContinueTurnFlow();
            }
            finally
            {
                playerMoveCoroutine = null;
                if (currentPhase == GamePhase.Playing && gameEngine != null
                    && !gameEngine.IsGameOver && gameEngine.CurrentTurn == playerColor)
                    SetBoardInput(true);
            }
        }

        void ContinueTurnFlow()
        {
            if (isPaused) return;
            if (gameEngine == null || gameEngine.IsGameOver) return;
            if (currentPhase != GamePhase.Playing) return;

            if (isOnline)
            {
                if (gameEngine.CurrentTurn == playerColor)
                {
                    if (gameEngine.GetLegalMoves().Count == 0)
                    {
                        gameEngine.SkipTurn();
                        if (onlineController != null) onlineController.SendPass();
                        UpdateStatusText();
                        SetBoardInput(false);
                        return;
                    }

                    SetBoardInput(true);
                    UpdateStatusText();
                    return;
                }

                SetBoardInput(false);
                if (statusText != null) statusText.text = "等待对手走子…";
                return;
            }

            if (gameEngine.CurrentTurn == playerColor)
            {
                if (gameEngine.GetLegalMoves().Count == 0)
                {
                    gameEngine.SkipTurn();
                    StartAITurn();
                    return;
                }
                SetBoardInput(true);
                StartPonder();
                return;
            }

            StartAITurn();
        }

        // 玩家回合开始后让后台线程预判；AI 回合开始前必须停掉，避免抢 CPU。
        void StartPonder()
        {
            if (isOnline || isPaused || aiPlayer == null || ponderPlanner == null) return;
            if (gameEngine == null || gameEngine.IsGameOver) return;
            if (gameEngine.CurrentTurn != playerColor) return;
            ponderPlanner.Start(board, aiPlayer.GetBeliefState());
        }

        void StopPonder()
        {
            if (ponderPlanner != null) ponderPlanner.Stop();
        }

        void RefreshLiveProfile()
        {
            if (profileStore == null || aiPlayer == null) return;

            var live = profileStore.CreateSnapshot();
            // 实时更新不再重复打印画像，避免前 10 步刷屏。
            aiPlayer.SetPlayerTendency(live, false);
            if (ponderPlanner != null) ponderPlanner.SetPlayerTendency(live);
        }

        void StartAITurn()
        {
            if (gameEngine.IsGameOver) return;
            StopPonder();
            SetBoardInput(false);
            if (aiCoroutine != null) StopCoroutine(aiCoroutine);
            aiCoroutine = StartCoroutine(AIMoveCoroutine());
        }

        IEnumerator AIMoveCoroutine()
        {
            StopPonder();
            var thinkWatch = System.Diagnostics.Stopwatch.StartNew();
            if (statusText != null) statusText.text = "AI 思考中…";
            yield return new WaitForSeconds(0.2f);

            PonderResult ponder = ponderPlanner != null ? ponderPlanner.Consume(board) : null;

            AISearchSession session = null;
            System.Exception searchError = null;
            try
            {
                session = aiPlayer.CreateSearchSession(board, ponder);
            }
            catch (System.Exception e)
            {
                searchError = e;
            }

            while (searchError == null && session != null && !session.IsFinished)
            {
                try
                {
                    session.Advance(10);
                }
                catch (System.Exception e)
                {
                    searchError = e;
                }

                if (searchError != null) break;
                yield return null;
            }

            if (searchError != null || session == null)
            {
                Debug.LogError("[GameManager] AI 搜索异常：" + searchError);
                if (statusText != null) statusText.text = "AI 搜索异常，跳过回合";
                gameEngine.SkipTurn();
                aiCoroutine = null;
                ContinueTurnFlow();
                yield break;
            }

            Move? aiMove = session.Result;

            if (!aiMove.HasValue)
            {
                if (statusText != null) statusText.text = "AI 无合法走法，跳过回合";
                gameEngine.SkipTurn();
                aiCoroutine = null;
                ContinueTurnFlow();
                yield break;
            }

            lastAiResponseMs = thinkWatch.ElapsedMilliseconds;
            long perceivedMs = lastPlayerMoveAt > 0f
                ? (long)((Time.realtimeSinceStartup - lastPlayerMoveAt) * 1000f)
                : lastAiResponseMs;
            Debug.Log($"[Ponder] 玩家落子后 AI 出招：{(ponder != null ? "命中预判缓存" : "未命中，完整战术搜索兜底")} · " +
                      $"计算 {lastAiResponseMs}ms · 玩家感知 {perceivedMs}ms。");

            if (aiPlayer is HybridAI hybridAi)
            {
                Debug.Log($"[Search] 深度 {hybridAi.LastSearchDepth}/{hybridAi.LastTargetDepth}" +
                          $" · {(hybridAi.LastEndgame ? "残局穷举(单世界)" : "采样期望")}" +
                          $" · 采样 {hybridAi.LastEffectiveSamples} · 节点率 {hybridAi.LastNodesPerSecond / 1000.0:F0}k/s" +
                          $" · Ponder 命中率 {hybridAi.PonderHitRate:P0}（{hybridAi.PonderHits}/{hybridAi.PonderRequests}）");
            }
            else if (aiPlayer is ExpectiminimaxAI emmAi)
            {
                Debug.Log($"[Search] 深度 {emmAi.LastSearchDepth}/{emmAi.LastTargetDepth}" +
                          $" · {(emmAi.LastSearchWasEndgame ? "残局穷举(单世界)" : "采样期望")}" +
                          $" · 采样 {emmAi.LastEffectiveSamples} · 节点率 {emmAi.LastNodesPerSecond / 1000.0:F0}k/s");
            }

            Move move = aiMove.Value;
            var ownMoveBoardBefore = board.Clone();
            var result = gameEngine.MakeMove(move);
            aiPlayer.NotifyOwnMove(move, result.CapturedPiece != null, ownMoveBoardBefore);
            if (gameRecorder != null)
                gameRecorder.RecordMove(gameEngine.MoveCount, aiPlayer.Color, move, ownMoveBoardBefore, result, aiPlayer.GetType().Name);
            if (result.CapturedPiece != null)
            {
                aiPlayer.NotifyCapture(move.To);
                if (profileStore != null)
                {
                    profileStore.RecordPieceLost();
                    RefreshLiveProfile();
                }
            }

            board = gameEngine.Board;
            if (boardView != null)
                yield return boardView.AnimateMove(move, result);
            aiCoroutine = null;

            if (result.GameOver) { EndGame(result.Winner); yield break; }

            UpdateStatusText();
            ContinueTurnFlow();
        }

        void UpdateStatusText()
        {
            if (statusText == null) return;
            if (isOnline)
            {
                statusText.text = gameEngine.CurrentTurn == playerColor ? "你的回合" : "对手回合";
                return;
            }
            statusText.text = gameEngine.CurrentTurn == playerColor ? "你的回合" : "AI 回合";
        }

        void EndGame(PieceColor? winner, string message = null)
        {
            if (currentPhase == GamePhase.GameOver) return;

            currentPhase = GamePhase.GameOver;
            ClosePromotionChooser();
            StopPonder();

            if (!isOnline && profileStore != null && !gameResultRecorded)
            {
                gameResultRecorded = true;
                bool? playerWon = winner.HasValue ? winner.Value == playerColor : (bool?)null;
                profileStore.RecordGame(playerWon);
                profileStore.Save();
                Debug.Log("[GameManager] 已记录对局结果，玩家画像：" + profileStore.Tendency.Describe());
            }

            if (gameRecorder != null)
            {
                gameRecorder.Finish(winner,
                    gameEngine != null ? gameEngine.EndReason.ToString() : "",
                    message ?? (winner.HasValue
                        ? (winner.Value == playerColor ? "你赢了！" : "AI 获胜")
                        : "平局"));
            }

            SetBoardInput(false);
            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (gameOverPanel != null) gameOverPanel.SetActive(true);

            if (gameOverText == null) return;

            if (!string.IsNullOrEmpty(message))
            {
                gameOverText.text = message;
                return;
            }

            if (gameEngine.EndReason == GameEndReason.ThreefoldRepetition)
            {
                gameOverText.text = winner.HasValue && winner.Value == playerColor
                    ? "三次重复局面 · 对手判负，你赢了"
                    : "三次重复局面 · 你判负";
                return;
            }

            string opponentName = isOnline ? "对手" : "AI";
            gameOverText.text = winner.HasValue
                ? (winner.Value == playerColor ? "你赢了！" : opponentName + "获胜")
                : "平局";
        }

        // 时光回溯底层接口：只暴露给代码（后续剧情系统调用），不绑定 UI 按钮或快捷键。
        public GameSnapshotBundle SaveStateSnapshot()
        {
            if (gameEngine == null) return null;

            return new GameSnapshotBundle
            {
                Engine = gameEngine.SaveStateSnapshot(),
                AiBelief = aiPlayer != null ? aiPlayer.CaptureBeliefState() : null
            };
        }

        public void RollbackToSnapshot(GameSnapshotBundle snapshot)
        {
            if (snapshot == null || snapshot.Engine == null || gameEngine == null) return;
            if (isOnline)
            {
                Debug.LogWarning("[TimeRewind] 联机对局不支持本地回滚，已忽略。");
                return;
            }

            StopPonder();
            if (ponderPlanner != null) ponderPlanner.ClearCache();
            ShutdownCoroutines();
            StopFormationPlanner();

            gameEngine.RollbackToSnapshot(snapshot.Engine);
            board = gameEngine.Board;

            if (aiPlayer != null)
                aiPlayer.ResetAfterRollback(snapshot.AiBelief);

            gameResultRecorded = false;
            draggingFrom = null;
            selectedPiece = null;
            legalMovesForSelected.Clear();
            if (boardInteraction != null) boardInteraction.ClearSelection();
            if (boardView != null) boardView.RenderBoardState(board, playerColor);

            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            currentPhase = gameEngine.IsGameOver ? GamePhase.GameOver : GamePhase.Playing;
            UpdateStatusText();
            SetBoardInput(currentPhase == GamePhase.Playing && gameEngine.CurrentTurn == playerColor);
            ContinueTurnFlow();

            Debug.Log($"[TimeRewind] 已回滚到第 {snapshot.TurnNumber} 回合（{(gameEngine.CurrentTurn == PieceColor.White ? "白" : "黑")}方行棋），AI 搜索树与 Ponder 缓存已清空。");
        }

        void OnRestartClicked()
        {
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (isOnline)
            {
                ShowMainMenu();
                return;
            }
            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (playerMoveCoroutine != null) { StopCoroutine(playerMoveCoroutine); playerMoveCoroutine = null; }
            StartNewGame();
        }

        public void BeginOnlineGame(PieceColor side, IOnlineMatchController controller)
        {
            ShutdownCoroutines();
            isPaused = false;

            isOnline = true;
            onlineController = controller;
            playerColor = side;
            remoteArrangement = null;
            remoteArrangementReceived = false;

            if (mainMenu != null) mainMenu.Hide();
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (statusText != null) statusText.gameObject.SetActive(true);
            if (inGameMenuButton != null) inGameMenuButton.SetActive(true);
            if (disguiseToggleRoot != null) disguiseToggleRoot.SetActive(true);
            if (disguiseLabels != null) disguiseLabels.playerColor = side;
            if (cameraFramer != null) cameraFramer.SetViewFromBlackSide(side == PieceColor.Black);

            StopFormationPlanner();
            formationPlanner = null;
            gameResultRecorded = false;

            gameEngine = new GameEngine(GameSeed);
            gameEngine.InitializeGame(standardOpening);
            gameEngine.EnableVisibleOnlyHashing(true);
            aiPlayer = null;
            board = gameEngine.Board;

            if (boardView != null)
            {
                boardView.RestoreInitialPieces();
                boardView.SyncPieceDataFromBoard(board, playerColor);
            }

            StartPlayerArrangement();
        }

        public void OnRemoteArrangementReceived(List<(Position pos, PieceType visibleType)> pieces)
        {
            if (!isOnline) return;
            remoteArrangement = pieces;
            remoteArrangementReceived = true;
            TryStartOnlinePlay();
        }

        void TryStartOnlinePlay()
        {
            if (!isOnline || currentPhase != GamePhase.WaitingOpponent) return;
            if (!remoteArrangementReceived || gameEngine == null) return;

            PieceColor enemyColor = OpponentColor();

            if (remoteArrangement != null && remoteArrangement.Count > 0)
            {
                // 对方只发坐标和伪装态，本态用伪装态占位，永远不落进本地棋盘。
                var enemyPieces = new List<(Position pos, PieceType trueType, PieceType visibleType)>();
                foreach (var item in remoteArrangement)
                    enemyPieces.Add((item.pos, item.visibleType, item.visibleType));
                gameEngine.ApplyArrangement(enemyColor, enemyPieces);
            }

            board = gameEngine.Board;
            gameEngine.ResetRepetitionTracking();

            if (boardView != null)
            {
                boardView.SyncEnemyFormationFromBoard(board, enemyColor);
                boardView.SyncPieceDataFromBoard(board, playerColor);
            }

            remoteArrangement = null;
            remoteArrangementReceived = false;

            currentPhase = GamePhase.Playing;
            UpdateStatusText();
            ContinueTurnFlow();
        }

        public void OnRemoteMoveReceived(Position from, Position to, PieceType? promotion, bool isCastling)
        {
            if (!isOnline || currentPhase != GamePhase.Playing) return;
            if (gameEngine == null || gameEngine.IsGameOver) return;
            if (remoteMoveCoroutine != null) return;
            if (gameEngine.CurrentTurn == playerColor) return;

            remoteMoveCoroutine = StartCoroutine(RemoteMoveCoroutine(new Move(from, to, promotion, isCastling)));
        }

        IEnumerator RemoteMoveCoroutine(Move move)
        {
            SetBoardInput(false);
            MoveResult result;
            try
            {
                result = gameEngine.ApplyOnlineMove(move, OpponentColor(), playerColor);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[GameManager] 应用对手走子失败：" + e);
                if (statusText != null) statusText.text = "联机同步异常";
                remoteMoveCoroutine = null;
                yield break;
            }

            board = gameEngine.Board;
            ClearSelection();

            if (boardView != null)
                yield return boardView.AnimateMove(move, result);
            else
                yield return null;

            if (result.GameOver)
            {
                // 只有被吃方知道自己的真王是否被吃，由被吃方广播结果。
                if (result.IsKingCapture && result.Winner.HasValue && onlineController != null)
                    onlineController.SendGameOver(result.Winner.Value);
                EndGame(result.Winner);
                yield break;
            }

            UpdateStatusText();
            ContinueTurnFlow();
            remoteMoveCoroutine = null;
        }

        public void OnRemotePassReceived()
        {
            if (!isOnline || currentPhase != GamePhase.Playing) return;
            if (gameEngine == null || gameEngine.IsGameOver) return;
            if (remoteMoveCoroutine != null) return;
            if (gameEngine.CurrentTurn == playerColor) return;

            gameEngine.SkipTurn();
            UpdateStatusText();
            ContinueTurnFlow();
        }

        public void OnRemoteGameOverReceived(PieceColor winner)
        {
            if (!isOnline) return;
            EndGame(winner);
        }

        public void OnOnlineOpponentLeft()
        {
            if (!isOnline || currentPhase == GamePhase.GameOver) return;

            if (currentPhase == GamePhase.Playing || currentPhase == GamePhase.WaitingOpponent)
                EndGame(playerColor, "对手已离开，你赢了");
            else
                ShowMainMenu();
        }

        public void ExitOnlineToMenu()
        {
            ShowMainMenu();
        }

        void ShutdownOnlineController()
        {
            var controller = onlineController;
            onlineController = null;
            if (controller != null) controller.Shutdown();
        }

        void ResetOnlineState()
        {
            isOnline = false;
            remoteArrangement = null;
            remoteArrangementReceived = false;
            if (remoteMoveCoroutine != null) { StopCoroutine(remoteMoveCoroutine); remoteMoveCoroutine = null; }
        }

        void ShutdownCoroutines()
        {
            StopPonder();
            if (aiCoroutine != null) { StopCoroutine(aiCoroutine); aiCoroutine = null; }
            if (playerMoveCoroutine != null) { StopCoroutine(playerMoveCoroutine); playerMoveCoroutine = null; }
            if (remoteMoveCoroutine != null) { StopCoroutine(remoteMoveCoroutine); remoteMoveCoroutine = null; }
        }

        PieceColor OpponentColor()
        {
            return playerColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
        }

        void ClearSelection()
        {
            selectedPiece = null;
            legalMovesForSelected.Clear();
            if (boardView != null) boardView.ClearHighlights();
            if (boardInteraction != null) boardInteraction.ClearSelection();
        }

        void SetBoardInput(bool enabled)
        {
            if (boardInteraction != null) boardInteraction.inputEnabled = enabled;
        }

        List<Position> GetPlayerHomeSquares()
        {
            var result = new List<Position>();
            int minRow = playerColor == PieceColor.White ? 0 : 6;
            int maxRow = playerColor == PieceColor.White ? 1 : 7;
            for (int y = minRow; y <= maxRow; y++)
                for (int x = 0; x < 8; x++)
                    result.Add(new Position(x, y));
            return result;
        }

        bool IsPlayerHomeSquare(Position pos)
        {
            if (!pos.IsValid) return false;
            if (playerColor == PieceColor.White) return pos.Y == 0 || pos.Y == 1;
            return pos.Y == 6 || pos.Y == 7;
        }
    }
}
