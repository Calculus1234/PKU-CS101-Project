using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 虚假认知条件（3.2 修正一·情境B）：8号AI（复现体）在"循环被破坏"的世界里
    /// 被机器故障识别为新1号——注入虚假认知「我是唯一杀手，要按从大到小杀死比我号码大的人」。
    /// mode = Hunt：搜证阶段主动寻找 9 号并找机会下手；
    /// mode = Vote：会议阶段推动投票投 9 号（"没机会下手则会议推动投票"）。
    /// 触发依据：当轮 Disrupted，或上一轮破局的持久痕迹（LastCycleDisrupted / 记录中的破局烙印）。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Conditions/False Cognition")]
    public class FalseCognitionCondition : NPCCondition
    {
        public enum Mode { Hunt, Vote }

        [Tooltip("行为模式：Hunt = 寻找/下手；Vote = 会议推票")]
        public Mode mode = Mode.Hunt;

        public override bool Check()
        {
            NPCBlackboard b = Board;
            if (b == null || !b.replayFromPlayer) return false; // 只有 8号AI（复现体）被注入虚假认知
            if (CycleState.kill9Executed) return false;         // 9号已死：目标不存在（复现链结束）

            bool disrupted = CycleState.currentPhase == CyclePhase.Disrupted || b.replayedDisruption;
            if (!disrupted) return false;

            // 子节点执行中（running）不中断
            if (TryGetChild(out Node node) && node.status == Status.Running) return true;

            GamePhase phase = GameManager.Instance != null ? GameManager.Instance.CurrentPhase : GamePhase.Investigation;

            if (mode == Mode.Vote)
            {
                // 推票：当轮破坏 + 会议阶段才成立
                return CycleState.currentPhase == CyclePhase.Disrupted && phase == GamePhase.Meeting;
            }

            // Hunt：搜证阶段主动寻找；Normal 轮（只剩破局痕迹）下只做一次探针
            if (phase != GamePhase.Investigation) return false;
            if (CycleState.currentPhase != CyclePhase.Disrupted && b.falseCognitionProbed) return false;
            return true;
        }
    }
}
