using MBT;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 行为树运行器：NPC 行为树（MonoBehaviourTree + MBTExecutor）的薄封装。
    /// 负责暂停 / 恢复 Tick（推搡打断用）与重启；不含任何行为逻辑本身。
    /// </summary>
    public class BehaviourTreeRunner : MonoBehaviour
    {
        [Tooltip("留空则自动取本物体上的 MonoBehaviourTree")]
        public MonoBehaviourTree tree;

        [Tooltip("留空则自动取本物体上的 MBTExecutor")]
        public MBTExecutor executor;

        float _resumeTime = -1f;

        public bool IsPaused => executor != null && !executor.enabled;

        void Awake()
        {
            if (tree == null) tree = GetComponent<MonoBehaviourTree>();
            if (executor == null) executor = GetComponent<MBTExecutor>();
        }

        void Update()
        {
            if (_resumeTime >= 0f && Time.time >= _resumeTime)
            {
                _resumeTime = -1f;
                SetPaused(false);
            }
        }

        /// <summary>暂停 / 恢复行为树 Tick（暂停期间节点状态保持）。</summary>
        public void SetPaused(bool paused)
        {
            // 手动静止（私聊 / 会议）必须压过之前的 PauseFor 自动恢复，
            // 否则推搡刚结束时的定时器会在几秒后把行为树重新打开，
            // 出现“玩家被锁在对话里，NPC 却继续走动”的情况。
            if (paused) _resumeTime = -1f;
            if (executor != null) executor.enabled = !paused;
        }

        /// <summary>暂停指定秒数后自动恢复（推搡打断用）。</summary>
        public void PauseFor(float seconds)
        {
            SetPaused(true);
            _resumeTime = Time.time + Mathf.Max(0f, seconds);
        }

        /// <summary>把行为树重置回根节点（下一帧从头执行）。</summary>
        public void Restart()
        {
            if (tree != null) tree.Restart();
        }
    }
}
