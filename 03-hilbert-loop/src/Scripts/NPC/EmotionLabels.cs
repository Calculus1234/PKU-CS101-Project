using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 情绪数值 → 模糊文字的映射（玩家永远看不到具体数字）。
    /// 数值系统仍保留在 NPCBlackboard 中，只在这里被翻译成感知描述。
    /// </summary>
    public static class EmotionLabels
    {
        /// <summary>信任标签（0~100）。</summary>
        public static string Trust(float trust)
        {
            if (trust <= 20f) return "[极其防备/充满敌意]";
            if (trust <= 40f) return "[警惕]";
            if (trust <= 60f) return "[中立/摇摆]";
            if (trust <= 80f) return "[略显信任]";
            return "[把你当盟友]";
        }

        /// <summary>
        /// 恶意 / 杀意标签：怀疑与激进值合成，只在较高时显示。
        /// </summary>
        public static string Hostility(float suspicion, float aggression)
        {
            float hostility = suspicion * 0.75f + aggression * 0.25f;
            if (hostility >= 85f) return "[正在盘算着怎么杀你]";
            if (hostility >= 75f) return "[充满杀意]";
            if (hostility >= 60f) return "[把你当成凶手]";
            if (hostility >= 45f) return "[一直在观察你]";
            return string.Empty;
        }

        /// <summary>压力 / 恐惧的肢体与声音描述。</summary>
        public static string Stress(float stress)
        {
            if (stress >= 85f) return "[呼吸急促，机械音里夹着电流声]";
            if (stress >= 70f) return "[指尖在抖]";
            return string.Empty;
        }

        /// <summary>整场会议对玩家的敌意（平均怀疑度 0~100）。</summary>
        public static string MeetingMood(float averageSuspicion)
        {
            if (averageSuspicion <= 20f) return "圆桌上的敌意正在消退";
            if (averageSuspicion <= 40f) return "仍有几道目光停在你身上";
            if (averageSuspicion <= 60f) return "大多数人在观望";
            if (averageSuspicion <= 80f) return "大多数人已经认定你有问题";
            return "整个房间都在等你露出破绽";
        }

        /// <summary>把 NPC 当前状态拼成一行私聊 / 会议标签。</summary>
        public static string Compose(float trust, float suspicion, float stress, float aggression)
        {
            string trustLabel = Trust(trust);
            string hostility = Hostility(suspicion, aggression);
            string stressLabel = Stress(stress);

            string text = trustLabel;
            if (!string.IsNullOrEmpty(stressLabel)) text += "　" + stressLabel;
            if (!string.IsNullOrEmpty(hostility)) text += "　" + hostility;
            return text;
        }

        /// <summary>给 Console 调试用的真实数值（玩家不可见）。</summary>
        public static string DebugNumbers(NPCBlackboard board, int playerNumber)
        {
            if (board == null) return "无黑板";

            float trust = playerNumber > 0 ? board.GetTrust(playerNumber) : board.trust;
            float hostility = board.suspicion * 0.75f + board.aggression * 0.25f;
            return $"怀疑 {board.suspicion:0.0}　对玩家信任 {trust:0.0}　压力 {board.stress:0.0}　" +
                   $"恶意 {hostility:0.0}　激进 {board.aggression:0.0}";
        }
    }
}
