using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>鸣谢面板：中英双语同时展示，任何语言设置下都能读懂。</summary>
    public class CreditsUI : MonoBehaviour
    {
        static CreditsUI _instance;
        Canvas _canvas;
        TextMeshProUGUI _title;
        TextMeshProUGUI _bodyZh;
        TextMeshProUGUI _bodyEn;
        TextMeshProUGUI _closeLabel;

        public static CreditsUI Show()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("CreditsUI");
                _instance = go.AddComponent<CreditsUI>();
                _instance.Build();
            }
            _instance.gameObject.SetActive(true);
            _instance.transform.SetAsLastSibling();
            _instance.RefreshTexts();
            return _instance;
        }

        public static void Hide()
        {
            if (_instance != null) _instance.gameObject.SetActive(false);
        }

        void Build()
        {
            _canvas = UIOverlayUtil.CreateCanvas("CreditsCanvas", 810, transform);
            UIOverlayUtil.CreateImage(_canvas.transform, "Dim", new Color(0f, 0f, 0f, 0.72f));

            var panel = UIOverlayUtil.CreatePanel(_canvas.transform, "Panel",
                new Vector2(1080f, 760f), Vector2.zero, new Color(0.022f, 0.020f, 0.024f, 1f));

            _title = UIOverlayUtil.CreateText(panel, "Title", "鸣谢",
                new Vector2(0.5f, 1f), new Vector2(0f, -62f), new Vector2(900f, 58f), 40f);
            UIOverlayUtil.CreateText(panel, "Subtitle", "/ CREDITS",
                new Vector2(0.5f, 1f), new Vector2(0f, -108f), new Vector2(900f, 34f), 20f,
                TextAlignmentOptions.Center, new Color(0.62f, 0.58f, 0.55f, 0.9f));

            string zh =
                "剧本 / 导演 / 程序\n林中夕客（微积分·甝虪）\n\n" +
                "3D 角色与动作\n混元AI3D\n\n" +
                "动作资源\nMixamo\n\n" +
                "音效素材\nFreesound.org\n\n" +
                "大语言模型\nQwen 大模型\n\n" +
                "感谢每一个在循环里醒来的人。";
            string en =
                "STORY / DIRECTION / PROGRAMMING\nLinzhong Xike (Calculus · Hanchu)\n\n" +
                "3D CHARACTERS & ANIMATION\nHunyuan AI 3D\n\n" +
                "ANIMATION ASSETS\nMixamo\n\n" +
                "SOUND EFFECTS\nFreesound.org\n\n" +
                "LARGE LANGUAGE MODEL\nQwen\n\n" +
                "For everyone who wakes up inside the loop.";

            _bodyZh = UIOverlayUtil.CreateText(panel, "BodyZh", zh,
                new Vector2(0.22f, 0.5f), new Vector2(0f, -16f), new Vector2(440f, 500f), 24f,
                TextAlignmentOptions.TopLeft, new Color(0.94f, 0.91f, 0.86f, 1f));
            _bodyEn = UIOverlayUtil.CreateText(panel, "BodyEn", en,
                new Vector2(0.74f, 0.5f), new Vector2(0f, -16f), new Vector2(440f, 500f), 22f,
                TextAlignmentOptions.TopLeft, new Color(0.82f, 0.78f, 0.73f, 1f));

            var close = UIOverlayUtil.CreateButton(panel, "CloseButton", "关闭",
                new Vector2(0f, -320f), new Vector2(260f, 52f), Hide,
                new Color(0.32f, 0.06f, 0.06f, 0.96f), 25f);
            _closeLabel = close.GetComponentInChildren<TextMeshProUGUI>();
            RefreshTexts();
        }

        void RefreshTexts()
        {
            if (_title == null) return;
            _title.text = Localization.T("鸣谢") + " / CREDITS";
            _closeLabel.text = Localization.T("关闭");
        }
    }
}
