using System;
using Yarn.Unity;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// Yarn Spinner 3 的会议台词展示器（模块三 3.4）。
    /// 台词文本全部来自 Assets/Yarn/Meeting.yarn；本类只负责把每行写入 MeetingUI，
    /// 按字数做自动停留（无需点击），并支持测试用的时长倍率。
    /// 选项不经过 Yarn（玩家选项由 MeetingUI 直接驱动）。
    /// </summary>
    public class MeetingDialoguePresenter : DialoguePresenterBase
    {
        [Tooltip("会议 UI（留空则运行时自动查找）")]
        public MeetingUI ui;

        [Header("每行停留（秒）")]
        public float baseSeconds = 1.2f;
        public float secondsPerCharacter = 0.045f;
        public float minSeconds = 1.8f;
        public float maxSeconds = 5.5f;

        [Tooltip("全局时长倍率（测试时可调小，正式保持 1）")]
        public float durationMultiplier = 1f;

        void Awake()
        {
            if (ui == null) ui = FindObjectOfType<MeetingUI>(true);
        }

        public override YarnTask OnDialogueStartedAsync()
        {
            return YarnTask.Yield();
        }

        public override async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            string text = Localization.TranslateDialogue(line.TextWithoutCharacterName.Text);
            if (ui != null) ui.SetLine(text);

            float duration = Mathf.Clamp(baseSeconds + text.Length * secondsPerCharacter,
                minSeconds, maxSeconds) * Mathf.Max(0.05f, durationMultiplier);

            try
            {
                await YarnTask.Delay(Mathf.Max(1, Mathf.RoundToInt(duration * 1000f)), token.NextContentToken);
            }
            catch (OperationCanceledException)
            {
                // 证据打断 / 主动 Stop 是预期流程：静默结束本行，不让 Yarn 报警告。
            }
        }

        public override YarnTask OnDialogueCompleteAsync()
        {
            if (ui != null) ui.SetLine(string.Empty);
            return YarnTask.Yield();
        }
    }
}
