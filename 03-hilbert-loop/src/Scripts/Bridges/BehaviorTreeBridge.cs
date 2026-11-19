using System;
using MBT;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// MonoBehaviourTree（Qriva）行为树桥接（预留接口）。
    ///
    /// 本桥接不含任何 NPC 行为逻辑 —— 只负责把"玩法消息 / 世界状态"和
    /// "行为树"之间搭好通道：
    /// 1. Emit(eventKey, payload)：玩法代码（推搡 / 死亡 / 阶段变化）广播事件；
    /// 2. GetVariable&lt;T&gt;：读取行为树黑板变量；
    /// 3. 常用上下文只读访问器（阶段、剩余时间、到目标距离）。
    ///
    /// 使用方式：在 NPC 上挂需要的行为树组件（MonoBehaviourTree + Root 节点 +
    /// MBTExecutor），本桥接会自动找到它们；没有行为树时所有接口安全降级。
    /// </summary>
    public class BehaviorTreeBridge : MonoBehaviour
    {
        [Tooltip("留空则自动从自身 / 子物体查找")]
        public MonoBehaviourTree tree;

        [Tooltip("行为树执行器（控制自动 Tick 的开关）")]
        public MBTExecutor executor;

        [Tooltip("行为树黑板（此 NPC 专属）")]
        public Blackboard blackboard;

        /// <summary>玩法事件通道（eventKey + 可选负载），供行为树层订阅。</summary>
        public event Action<string, object> OnGameplayEvent;

        public bool IsTreeAvailable => tree != null;

        void Awake()
        {
            if (tree == null) tree = GetComponentInChildren<MonoBehaviourTree>(true);
            if (executor == null) executor = GetComponentInChildren<MBTExecutor>(true);
            if (blackboard == null) blackboard = GetComponentInChildren<Blackboard>(true);
        }

        /// <summary>
        /// 向行为树层广播一个玩法事件（预留）。
        /// 例：Emit("npc_pushed", this)；Emit("phase_changed", GamePhase.Meeting)。
        /// 后续在 MBT 编辑器里搭建具体行为树后，把事件接到对应节点即可。
        /// </summary>
        public void Emit(string eventKey, object payload = null)
        {
            OnGameplayEvent?.Invoke(eventKey, payload);
        }

        /// <summary>启用 / 暂停行为树的自动 Tick。</summary>
        public void SetTreeActive(bool active)
        {
            if (executor != null) executor.enabled = active;
            if (tree != null) tree.enabled = active;
        }

        /// <summary>手动 Tick 一次（自动 Tick 关闭时的备用入口）。</summary>
        public void ManualTick()
        {
            if (tree != null) tree.Tick();
        }

        /// <summary>重置行为树执行状态。</summary>
        public void RestartTree()
        {
            if (tree != null) tree.Restart();
        }

        /// <summary>读取行为树黑板变量（无黑板时返回 null）。</summary>
        public T GetVariable<T>(string key) where T : BlackboardVariable
        {
            return blackboard != null ? blackboard.GetVariable<T>(key) : null;
        }

        // ---------- 行为树常用上下文（只读） ----------

        public GamePhase CurrentPhase =>
            GameManager.Instance != null ? GameManager.Instance.CurrentPhase : GamePhase.Investigation;

        public float PhaseTimeRemaining =>
            GameManager.Instance != null ? GameManager.Instance.PhaseTimeRemaining : 0f;

        public float DistanceTo(Transform target)
        {
            return target != null ? Vector3.Distance(transform.position, target.position) : float.PositiveInfinity;
        }
    }
}
