using System;
using Yarn.Unity;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 结局 B 的 Yarn 台词展示器（模块三 3.5 紧急修正）。
    /// 台词全部来自 Assets/Yarn/Ending.yarn；本类只把每一行交给 EndingSystem 的全屏结局界面，
    /// 按字数停留后自动进入下一行，不参与任何会议 / 投票流程。
    /// </summary>
    public class EndingDialoguePresenter : DialoguePresenterBase
    {
        [Tooltip("结局界面（运行时由 EndingSystem 装配）")]
        public EndingSystem ending;

        public override YarnTask OnDialogueStartedAsync()
        {
            return YarnTask.Yield();
        }

        public override async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            if (ending == null) return;

            string text = Localization.TranslateDialogue(line.TextWithoutCharacterName.Text);
            ending.ShowYarnLine(text);

            float duration = ending.GetLineDuration(text);
            try
            {
                await YarnTask.Delay(Mathf.Max(1, Mathf.RoundToInt(duration * 1000f)), token.NextContentToken);
            }
            catch (OperationCanceledException)
            {
                // 结局被跳过 / 场景重载属于预期中断。
            }
        }

        public override YarnTask OnDialogueCompleteAsync()
        {
            if (ending != null)
            {
                ending.ClearYarnLine();
                ending.HandleDialogueComplete();
            }
            return YarnTask.Yield();
        }
    }
}
