using System;
using Yarn.Unity;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 私聊 Yarn 台词展示器（模块四）：把 Chat_* 节点的每一行写到 PrivateChatUI，
    /// 按字数停留后自动继续；整段对话结束时通知 PrivateChatSystem 恢复选项。
    /// 玩家选项不经过 Yarn，由 PrivateChatUI / PrivateChatSystem 驱动。
    /// </summary>
    public class PrivateChatDialoguePresenter : DialoguePresenterBase
    {
        public PrivateChatSystem chat;

        [Header("每行停留（秒）")]
        public float baseSeconds = 0.9f;
        public float secondsPerCharacter = 0.04f;
        public float minSeconds = 1.4f;
        public float maxSeconds = 4.5f;

        public override YarnTask OnDialogueStartedAsync()
        {
            return YarnTask.Yield();
        }

        public override async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            if (chat == null) return;

            string text = Localization.TranslateDialogue(line.TextWithoutCharacterName.Text);
            chat.ShowLine(text);

            float duration = Mathf.Clamp(baseSeconds + text.Length * secondsPerCharacter,
                minSeconds, maxSeconds);
            try
            {
                await YarnTask.Delay(Mathf.Max(1, Mathf.RoundToInt(duration * 1000f)),
                    token.NextContentToken);
            }
            catch (OperationCanceledException)
            {
                // 结束私聊 / 进入结局时的主动 Stop 属于预期中断。
            }
        }

        public override YarnTask OnDialogueCompleteAsync()
        {
            if (chat != null) chat.HandleDialogueComplete();
            return YarnTask.Yield();
        }
    }
}
