using System;
using UnityEngine;
using UnityEngine.UI;
using ChessDark.AI;
using ChessDark.Core;

namespace ChessDark.Unity
{
    public class MainMenuUI
    {
        public Action<PieceColor> SinglePlayerSelected;
        public Action ResumeRequested;
        public Action ExitToMenuRequested;
        public Action<AIEngineType> EngineChanged;

        public AIEngineType SelectedEngine { get; private set; }
#if !DISABLE_MULTIPLAYER
        public Action CreateRoomRequested;
        public Action<string> JoinRoomRequested;
#endif

        private readonly GameObject root;
        private readonly GameObject mainPanel;
        private readonly GameObject sidePanel;
        private readonly GameObject pausePanel;
        private readonly Text engineLabel;
#if !DISABLE_MULTIPLAYER
        private readonly GameObject joinPanel;
        private readonly InputField roomInput;
        private readonly Text joinHint;
#endif

        private static readonly Color TitleColor = new Color(0.98f, 0.9f, 0.7f, 1f);
        private static readonly Color SubtitleColor = new Color(0.55f, 0.6f, 0.68f, 1f);

        public MainMenuUI(Canvas canvas, AIEngineType initialEngine = AIEngineType.MCTS)
        {
            SelectedEngine = initialEngine;
            Font font = RuntimeUI.ResolveFont(canvas);

            root = RuntimeUI.CreatePanel(canvas.transform, "MainMenu", new Color(0.012f, 0.016f, 0.024f, 0.95f));
            root.transform.SetAsLastSibling();

            mainPanel = CreateContainer(root.transform, "MainPanel");
            sidePanel = CreateContainer(root.transform, "SidePanel");

            var title = RuntimeUI.CreateText(mainPanel.transform, "Title", "暗  棋", 88, TitleColor,
                TextAnchor.MiddleCenter, font);
            title.rectTransform.anchoredPosition = new Vector2(0f, 230f);

            var subtitle = RuntimeUI.CreateText(mainPanel.transform, "Subtitle", "无 间 对 弈  ·  DARK CHESS", 26,
                SubtitleColor, TextAnchor.MiddleCenter, font);
            subtitle.rectTransform.anchoredPosition = new Vector2(0f, 150f);

            RuntimeUI.CreateButton(mainPanel.transform, "SinglePlayerButton", "人机对战", new Vector2(0f, 30f),
                new Vector2(340f, 76f), font, ShowSideSelection);

#if !DISABLE_MULTIPLAYER
            RuntimeUI.CreateButton(mainPanel.transform, "CreateRoomButton", "创建房间", new Vector2(0f, -66f),
                new Vector2(340f, 76f), font, () =>
                {
                    Hide();
                    if (CreateRoomRequested != null) CreateRoomRequested();
                });

            RuntimeUI.CreateButton(mainPanel.transform, "JoinRoomButton", "加入房间", new Vector2(0f, -162f),
                new Vector2(340f, 76f), font, ShowJoinPanel);

            var tip = RuntimeUI.CreateText(mainPanel.transform, "OnlineTip", "联机对战 · 局域网 / 广域网", 20,
                SubtitleColor, TextAnchor.MiddleCenter, font);
            tip.rectTransform.anchoredPosition = new Vector2(0f, -245f);
#endif

            var sideTitle = RuntimeUI.CreateText(sidePanel.transform, "SideTitle", "选择执子", 56, TitleColor,
                TextAnchor.MiddleCenter, font);
            sideTitle.rectTransform.anchoredPosition = new Vector2(0f, 200f);

            RuntimeUI.CreateButton(sidePanel.transform, "WhiteButton", "执白先行", new Vector2(0f, 110f),
                new Vector2(340f, 76f), font, () => SelectSide(PieceColor.White));
            RuntimeUI.CreateButton(sidePanel.transform, "BlackButton", "执黑后行", new Vector2(0f, 20f),
                new Vector2(340f, 76f), font, () => SelectSide(PieceColor.Black));

            var engineButton = RuntimeUI.CreateButton(sidePanel.transform, "EngineButton",
                EngineLabelText(SelectedEngine), new Vector2(0f, -78f), new Vector2(340f, 54f), font, CycleEngine);
            engineLabel = engineButton.GetComponentInChildren<Text>();
            if (engineLabel != null)
            {
                engineLabel.fontSize = 24;
                engineLabel.text = EngineLabelText(SelectedEngine);
            }

            var engineHint = RuntimeUI.CreateText(sidePanel.transform, "EngineHint", "AI 引擎仅影响下一局", 18,
                SubtitleColor, TextAnchor.MiddleCenter, font);
            engineHint.rectTransform.anchoredPosition = new Vector2(0f, -128f);

            RuntimeUI.CreateButton(sidePanel.transform, "SideBackButton", "返回", new Vector2(0f, -195f),
                new Vector2(220f, 62f), font, ShowMainPanel);

#if !DISABLE_MULTIPLAYER
            joinPanel = CreateContainer(root.transform, "JoinPanel");

            var joinTitle = RuntimeUI.CreateText(joinPanel.transform, "JoinTitle", "加入房间", 56, TitleColor,
                TextAnchor.MiddleCenter, font);
            joinTitle.rectTransform.anchoredPosition = new Vector2(0f, 170f);

            roomInput = RuntimeUI.CreateInputField(joinPanel.transform, "RoomInput", "请输入房间号",
                new Vector2(0f, 50f), new Vector2(340f, 70f), font, 12);

            joinHint = RuntimeUI.CreateText(joinPanel.transform, "JoinHint", string.Empty, 22,
                new Color(1f, 0.55f, 0.45f, 1f), TextAnchor.MiddleCenter, font);
            joinHint.rectTransform.anchoredPosition = new Vector2(0f, -6f);

            RuntimeUI.CreateButton(joinPanel.transform, "JoinConfirmButton", "加入", new Vector2(0f, -80f),
                new Vector2(340f, 70f), font, ConfirmJoin);
            RuntimeUI.CreateButton(joinPanel.transform, "JoinBackButton", "返回", new Vector2(0f, -180f),
                new Vector2(220f, 62f), font, ShowMainPanel);
#endif

            pausePanel = CreateContainer(root.transform, "PausePanel");

            var pauseTitle = RuntimeUI.CreateText(pausePanel.transform, "PauseTitle", "暂  停", 64, TitleColor,
                TextAnchor.MiddleCenter, font);
            pauseTitle.rectTransform.anchoredPosition = new Vector2(0f, 150f);

            var pauseHint = RuntimeUI.CreateText(pausePanel.transform, "PauseHint", "对局进度已保留", 24,
                SubtitleColor, TextAnchor.MiddleCenter, font);
            pauseHint.rectTransform.anchoredPosition = new Vector2(0f, 80f);

            RuntimeUI.CreateButton(pausePanel.transform, "ResumeButton", "继续对局", new Vector2(0f, -10f),
                new Vector2(340f, 76f), font, ResumeFromPause);

            RuntimeUI.CreateButton(pausePanel.transform, "ExitToMenuButton", "返回主菜单", new Vector2(0f, -106f),
                new Vector2(340f, 76f), font, ExitPauseToMenu);

            var exitHint = RuntimeUI.CreateText(pausePanel.transform, "ExitHint", "返回主菜单将放弃当前对局", 18,
                SubtitleColor, TextAnchor.MiddleCenter, font);
            exitHint.rectTransform.anchoredPosition = new Vector2(0f, -196f);

            ShowMainPanel();
        }

        public bool IsVisible => root != null && root.activeSelf;

        public void Show()
        {
            if (root != null) root.SetActive(true);
            ShowMainPanel();
        }

        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        public void ShowPause()
        {
            if (root != null)
            {
                root.SetActive(true);
                root.transform.SetAsLastSibling();
            }
            mainPanel.SetActive(false);
            sidePanel.SetActive(false);
#if !DISABLE_MULTIPLAYER
            joinPanel.SetActive(false);
#endif
            if (pausePanel != null) pausePanel.SetActive(true);
        }

        private static GameObject CreateContainer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            RuntimeUI.Stretch(rect);
            return go;
        }

        private void ShowMainPanel()
        {
            mainPanel.SetActive(true);
            sidePanel.SetActive(false);
            if (pausePanel != null) pausePanel.SetActive(false);
#if !DISABLE_MULTIPLAYER
            joinPanel.SetActive(false);
            joinHint.text = string.Empty;
#endif
        }

        private void ShowSideSelection()
        {
            mainPanel.SetActive(false);
            sidePanel.SetActive(true);
        }

#if !DISABLE_MULTIPLAYER
        private void ShowJoinPanel()
        {
            mainPanel.SetActive(false);
            joinPanel.SetActive(true);
            joinHint.text = string.Empty;
            roomInput.text = string.Empty;
        }

        private void ConfirmJoin()
        {
            string code = roomInput.text != null ? roomInput.text.Trim() : string.Empty;
            if (code.Length == 0)
            {
                joinHint.text = "请先输入房间号";
                return;
            }

            Hide();
            if (JoinRoomRequested != null) JoinRoomRequested(code);
        }
#endif

        private static string EngineLabelText(AIEngineType engine)
        {
            switch (engine)
            {
                case AIEngineType.MCTS: return "AI 引擎：MCTS（实验组）";
                case AIEngineType.Hybrid: return "AI 引擎：Hybrid（混合）";
                case AIEngineType.Predator: return "AI 引擎：Predator（隐匿猎王）";
                default: return "AI 引擎：Expectiminimax";
            }
        }

        private void CycleEngine()
        {
            switch (SelectedEngine)
            {
                case AIEngineType.MCTS:
                    SelectedEngine = AIEngineType.Expectiminimax;
                    break;
                case AIEngineType.Expectiminimax:
                    SelectedEngine = AIEngineType.Hybrid;
                    break;
                case AIEngineType.Hybrid:
                    SelectedEngine = AIEngineType.Predator;
                    break;
                default:
                    SelectedEngine = AIEngineType.MCTS;
                    break;
            }
            if (engineLabel != null) engineLabel.text = EngineLabelText(SelectedEngine);
            if (EngineChanged != null) EngineChanged(SelectedEngine);
        }

        private void SelectSide(PieceColor side)
        {
            Hide();
            if (SinglePlayerSelected != null) SinglePlayerSelected(side);
        }

        private void ResumeFromPause()
        {
            Hide();
            if (ResumeRequested != null) ResumeRequested();
        }

        private void ExitPauseToMenu()
        {
            if (ExitToMenuRequested != null) ExitToMenuRequested();
        }
    }
}
