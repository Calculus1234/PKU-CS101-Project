using UnityEngine;
using UnityEngine.Events;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 占位体（胶囊）：可被武器袭击；满足「同处一室 + 对方探头望向窗外」
    /// 条件时可被推搡出窗（致死、不留尸体）。
    /// 复杂的 NPC 行为逻辑不写在这个脚本里 —— 由 BehaviorTreeBridge
    /// 接入 MonoBehaviourTree（预留接口），剧情由 Yarn Spinner 驱动。
    /// </summary>
    public class NPCPlaceholder : MonoBehaviour, IInteractable, IHostileInteractable, IKillable
    {
        [Header("身份")]
        public string npcName = "住客";

        [Header("窗边推搡")]
        [Tooltip("推搡致死的提示语（不留尸体）")]
        public string pushOutWindowMessage = "窗外只剩风雪声——没有人听见呼救。";

        public bool IsDead { get; private set; }

        /// <summary>
        /// 是否正探头看向窗外（由后续行为树 / 剧情驱动：调用 SetLeaningOut）。
        /// 此状态下，与玩家同处一个房间时会被推搡出窗外。
        /// </summary>
        public bool IsLeaningOut { get; private set; }

        [Header("事件（预留：行为树 / 剧情接入点）")]
        public UnityEvent OnKilled = new UnityEvent();
        public UnityEvent OnKillAttemptFailed = new UnityEvent();
        public UnityEvent OnPushedOutWindow = new UnityEvent();

        BehaviorTreeBridge _bridge;

        /// <summary>设置「探头看向窗外」状态（预留接口：由行为树 / 剧情调用）。</summary>
        public void SetLeaningOut(bool leaning)
        {
            if (IsDead) return;
            IsLeaningOut = leaning;
        }

        /// <summary>E 键没有友善 / 环境动作；攻击全部走左键。</summary>
        public string GetInteractionPrompt(PlayerController player)
        {
            return "";
        }

        public void Interact(PlayerController player)
        {
            // 兼容保留的空实现：E 不再对占位 NPC 执行任何敌意动作。
        }

        public string GetHostilePrompt(PlayerController player)
        {
            if (IsDead) return "";

            if (CanPushOutWindow(player))
                return $"[左键] 将{npcName}推出窗外";

            ItemDefinition weapon = player != null && player.inventory != null
                ? player.inventory.GetLastWeapon()
                : null;

            return weapon != null ? $"[左键] 用{weapon.displayName}袭击{npcName}" : "";
        }

        public bool TryHostileInteract(PlayerController player)
        {
            if (IsDead) return false;

            if (CanPushOutWindow(player))
            {
                PushOutWindow(player);
                return true;
            }

            if (player == null || player.inventory == null) return false;
            return player.inventory.UseWeaponOn(this);
        }

        /// <summary>
        /// 窗边推搡触发条件：NPC 探身窗外，且与玩家处于同一房间。
        /// 任一方无法判定所在房间（缺少 MonitorTracked）时，视为条件不成立。
        /// </summary>
        bool CanPushOutWindow(PlayerController player)
        {
            if (!IsLeaningOut || IsDead || player == null) return false;

            MonitorTracked self = GetComponent<MonitorTracked>();
            MonitorTracked other = player.GetComponentInParent<MonitorTracked>();
            if (self == null || other == null) return false;
            return self.SharesAnyRoom(other);
        }

        /// <summary>窗边推搡：致死，且不留尸体（整个占位体被移除出世界与监控）。</summary>
        void PushOutWindow(PlayerController player)
        {
            if (IsDead) return;
            IsDead = true;

            if (player != null)
                player.ShowMessage($"你趁{npcName}探头看窗外，一把将其推出了窗外。{pushOutWindowMessage}");

            OnPushedOutWindow.Invoke();
            OnKilled.Invoke();
            EmitToTree("npc_pushed");
            EmitToTree("npc_killed");
            EmitToTree("npc_pushed_out_window");

            // 不留尸体：移除全部表现与碰撞（红点也会随对象失活从监控中消失）
            SetWorldPresence(false);
        }

        /// <summary>开关该占位体的世界存在感（失活即同时失去渲染 / 碰撞 / 监控红点）。</summary>
        void SetWorldPresence(bool present)
        {
            gameObject.SetActive(present);
        }

        public void ApplyKill(PlayerController player, ItemDefinition weapon)
        {
            if (IsDead) return;
            IsDead = true;

            // 简单倒地表现：绕 X 轴躺倒并下沉（后续可替换为死亡动画 / 布娃娃）
            transform.rotation = Quaternion.Euler(-90f, transform.rotation.eulerAngles.y, 0f);
            transform.position += Vector3.down * 0.7f;

            if (player != null)
                player.ShowMessage($"{npcName}倒下了。");

            OnKilled.Invoke();
            EmitToTree("npc_killed");
        }

        public void ApplyKillAttemptFailed(PlayerController player, ItemDefinition weapon)
        {
            if (player != null)
                player.ShowMessage($"袭击没有命中要害！{npcName}的惊呼惊动了所有人。");

            // 预留：失败事件 —— 所有 NPC 应得知（行为树 / 全局状态后续接入）
            OnKillAttemptFailed.Invoke();
        }

        /// <summary>向自身的行为树桥接广播事件（未挂桥接时静默降级）。</summary>
        void EmitToTree(string eventKey)
        {
            if (_bridge == null) _bridge = GetComponent<BehaviorTreeBridge>();
            if (_bridge != null) _bridge.Emit(eventKey, this);
        }
    }
}
