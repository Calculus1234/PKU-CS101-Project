using System;
using Yarn.Unity;
using UnityEngine;
using UnityEngine.Events;

namespace HilbertLoop
{
    /// <summary>
    /// Yarn Spinner 桥接（预留接口）。
    /// 本脚本不含任何对话内容 —— 对话文本全部写在 .yarn 脚本中，由 DialogueRunner 运行。
    ///
    /// 提供：
    /// - StartDialogue(nodeName) / StopDialogue() / IsRunning
    /// - SetVariable / TryGetVariable：供玩法层写入或读取线索、身份等标记
    ///   （Yarn 变量名带 $ 前缀，例如 $has_dagger）
    /// - 节点事件（开始 / 完成 / 整段对话结束）转成 C# 事件，便于衔接游戏流程
    /// </summary>
    public class DialogueBridge : MonoBehaviour
    {
        [Tooltip("留空则自动从自身 / 子物体查找")]
        public DialogueRunner dialogueRunner;

        public event Action<string> OnNodeStarted;
        public event Action<string> OnNodeCompleted;
        public event Action OnDialogueCompleted;

        public bool IsRunning => dialogueRunner != null && dialogueRunner.IsDialogueRunning;

        /// <summary>场景里是否已装配 Yarn DialogueRunner（会议系统据此决定是否走 Yarn 或占位文本）。</summary>
        public bool HasRunner => dialogueRunner != null;

        void Awake()
        {
            if (dialogueRunner == null) dialogueRunner = GetComponentInChildren<DialogueRunner>(true);
            if (dialogueRunner == null) return; // 尚未接入 DialogueRunner 时静默降级（接口保留）

            // Yarn 的事件字段在 Inspector 也可用；这里做空值保护并转发到 C# 事件
            if (dialogueRunner.onNodeStart == null) dialogueRunner.onNodeStart = new UnityEventString();
            dialogueRunner.onNodeStart.AddListener(node => OnNodeStarted?.Invoke(node));

            if (dialogueRunner.onNodeComplete == null) dialogueRunner.onNodeComplete = new UnityEventString();
            dialogueRunner.onNodeComplete.AddListener(node => OnNodeCompleted?.Invoke(node));

            if (dialogueRunner.onDialogueComplete == null) dialogueRunner.onDialogueComplete = new UnityEvent();
            dialogueRunner.onDialogueComplete.AddListener(() => OnDialogueCompleted?.Invoke());
        }

        /// <summary>开始运行指定名称的 Yarn 节点（对话内容在 .yarn 脚本里编写）。</summary>
        public void StartDialogue(string nodeName)
        {
            if (dialogueRunner == null)
            {
                Debug.LogWarning("[DialogueBridge] 场景中还没有接入 DialogueRunner，无法开始对话。");
                return;
            }

            if (dialogueRunner.IsDialogueRunning)
            {
                Debug.LogWarning("[DialogueBridge] 已有对话在运行，忽略新的开始请求。");
                return;
            }

            StartDialogueAsync(nodeName);
        }

        async void StartDialogueAsync(string nodeName)
        {
            await dialogueRunner.StartDialogue(nodeName);
        }

        /// <summary>停止当前对话。</summary>
        public void StopDialogue()
        {
            if (dialogueRunner == null) return;
            StopDialogueAsync();
        }

        async void StopDialogueAsync()
        {
            await dialogueRunner.Stop();
        }

        // ---------- 变量读写 ----------

        public void SetVariable(string variableName, string value)
        {
            VariableStorageBehaviour storage = GetStorage();
            if (storage != null) storage.SetValue(variableName, value);
        }

        public void SetVariable(string variableName, bool value)
        {
            VariableStorageBehaviour storage = GetStorage();
            if (storage != null) storage.SetValue(variableName, value);
        }

        public void SetVariable(string variableName, float value)
        {
            VariableStorageBehaviour storage = GetStorage();
            if (storage != null) storage.SetValue(variableName, value);
        }

        public bool TryGetVariable(string variableName, out string value)
        {
            value = null;
            VariableStorageBehaviour storage = GetStorage();
            if (storage == null) return false;

            return storage.TryGetValue<string>(variableName, out value);
        }

        public bool TryGetVariable(string variableName, out float value)
        {
            value = 0f;
            VariableStorageBehaviour storage = GetStorage();
            if (storage == null) return false;

            return storage.TryGetValue<float>(variableName, out value);
        }

        public bool TryGetVariable(string variableName, out bool value)
        {
            value = false;
            VariableStorageBehaviour storage = GetStorage();
            if (storage == null) return false;

            return storage.TryGetValue<bool>(variableName, out value);
        }

        VariableStorageBehaviour GetStorage()
        {
            if (dialogueRunner == null) return null;
            return dialogueRunner.VariableStorage;
        }
    }
}
