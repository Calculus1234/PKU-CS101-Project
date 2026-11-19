using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 对话桥接（模块三 3.4）：把「某个 NPC 发言」接到全局 Yarn DialogueRunner。
    /// 具体台词全部写在 Assets/Yarn/Meeting.yarn；本类只负责选择节点与转发调用。
    /// </summary>
    public class NPCDialogueBridge : MonoBehaviour
    {
        [Tooltip("留空则运行时自动查找场景中的 DialogueBridge")]
        public DialogueBridge dialogue;

        /// <summary>当前发言的号码（MeetingSystem 写入，供外部查询）。</summary>
        public int MeetingSpeakerNumber { get; private set; } = -1;

        /// <summary>本 NPC 是否正在说话（共享 DialogueRunner 的运行状态）。</summary>
        public bool IsSpeaking => dialogue != null && dialogue.IsRunning;

        public bool HasRunner => dialogue != null && dialogue.HasRunner;

        void Awake()
        {
            if (dialogue == null) dialogue = GetComponent<DialogueBridge>();
            if (dialogue == null) dialogue = FindMeetingBridge();
        }

        /// <summary>
        /// 运行时由 MeetingSystem 显式绑定会议对话桥。
        /// 场景里还有私聊 / 结局各自的 DialogueRunner，不能凭 FindObjectOfType 随便挑一个。
        /// </summary>
        public void Bind(DialogueBridge bridge)
        {
            if (bridge != null) dialogue = bridge;
        }

        static DialogueBridge FindMeetingBridge()
        {
            DialogueBridge[] bridges = FindObjectsOfType<DialogueBridge>(true);
            for (int i = 0; i < bridges.Length; i++)
            {
                if (bridges[i] == null) continue;
                if (bridges[i].GetComponent<MeetingDialoguePresenter>() != null)
                    return bridges[i];
            }
            return bridges.Length > 0 ? bridges[0] : null;
        }

        /// <summary>让该 NPC 说一段话（Yarn 节点名）。</summary>
        public void Speak(string yarnNodeName)
        {
            if (dialogue == null)
            {
                Debug.LogWarning("[NPCDialogueBridge] 场景中没有 DialogueBridge，无法播放 Yarn 发言。");
                return;
            }
            dialogue.StartDialogue(yarnNodeName);
        }

        /// <summary>
        /// 会议发言入口（3.4 任务书指定接口）：
        /// 记录发言号码并启动 Yarn 节点。变量注入由 MeetingSystem 在调用前完成。
        /// </summary>
        public bool TriggerMeetingSpeech(int speakerNumber, string yarnNode)
        {
            MeetingSpeakerNumber = speakerNumber;
            if (dialogue == null || !dialogue.HasRunner)
            {
                Debug.LogWarning($"[NPCDialogueBridge] {speakerNumber} 号发言失败：DialogueRunner 未装配。");
                return false;
            }
            if (dialogue.IsRunning)
            {
                Debug.LogWarning($"[NPCDialogueBridge] {speakerNumber} 号发言被忽略：已有对话在运行。");
                return false;
            }
            dialogue.StartDialogue(yarnNode);
            return true;
        }

        /// <summary>对话结束回调（预留：恢复站姿 / 更新黑板）。</summary>
        public void OnDialogueComplete()
        {
            MeetingSpeakerNumber = -1;
        }
    }
}
