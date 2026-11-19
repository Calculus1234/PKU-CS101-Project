using System.Text;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 隐藏开发者面板：把所有 NPC 的真实怀疑 / 信任 / 压力 / 恶意打印到 Console。
    /// 上线版本已彻底停用：不再监听 F12，也不会自动创建对象。
    /// </summary>
    public class DeveloperDebugOverlay : MonoBehaviour
    {
        // 上线版本必须保持 false。本地调试时改回 true 并自行恢复入口。
        static readonly bool DeveloperDebugEnabled = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!DeveloperDebugEnabled) return;
            if (FindObjectOfType<DeveloperDebugOverlay>() != null) return;

            GameObject go = new GameObject("[DeveloperDebugOverlay]");
            DontDestroyOnLoad(go);
            go.AddComponent<DeveloperDebugOverlay>();
        }

        void Update()
        {
            // 上线版本已删除 F12 监听。
            if (!DeveloperDebugEnabled) return;
            if (Input.GetKeyDown(KeyCode.F12)) DumpAllNpcNumbers();
        }

        void DumpAllNpcNumbers()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== [DEV] NPC 真实数值（仅 Console，玩家 UI 不可见） =====");
            sb.AppendLine($"循环第 {LoopTimeline.CycleElapsedGameMinutes:0.0} 游戏分钟 / 现实 {Time.time:0.0} 秒　" +
                          $"玩家：{(CycleState.playerNumber > 0 ? CycleState.playerNumber + "号" : "观察者")}　" +
                          $"阶段：{(GameManager.Instance != null ? GameManager.Instance.CurrentPhase.ToString() : "无")}　" +
                          $"循环：{CycleState.currentPhase}");

            int count = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;

                count++;
                string room = string.IsNullOrEmpty(npc.currentRoomName) ? "走廊" : npc.currentRoomName;
                string awake = npc.IsAwake ? "醒着" : "沉睡";
                sb.AppendLine($"{npc.Number,2} 号　{EmotionLabels.DebugNumbers(npc.blackboard, CycleState.playerNumber)}　" +
                              $"{awake}　{room}");
            }

            if (count == 0) sb.AppendLine("（当前没有存活的 NPC）");

            MeetingSystem meeting = FindObjectOfType<MeetingSystem>();
            if (meeting != null && meeting.IsMeetingActive)
                sb.AppendLine($"会议全场对玩家的怀疑（内部值）：{meeting.PlayerSuspicion:0.0}");

            Debug.Log(sb.ToString());
        }
    }
}
