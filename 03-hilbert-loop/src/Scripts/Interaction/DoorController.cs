using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace HilbertLoop
{
    /// <summary>
    /// 房门控制器：挂在门的铰链根节点上，绕本地 Y 轴开合。
    /// 9 号房间默认 lockedFromInside = true（从内部闩住），两条解锁路径：
    /// 1. 找到工具（匕首 / 锤子等）一击破门，或徒手多次撞击；
    /// 2. 圆桌会议上宣布强行破门 —— 外部（辩论 / 投票系统）调用
    ///    ForceUnlockByDeclaration()（预留接口）。
    /// 破门（工具 / 徒手）与宣布强行破门都会发出全局通知（静态事件 + 行为树广播），
    /// 让所有 NPC 得知此事。
    ///
    /// 预留接口：OnDoorOpened / OnDoorClosed / OnDoorBroken / OnForceUnlocked 事件，
    /// 后续可挂接到行为树或 Yarn Spinner。
    /// </summary>
    public class DoorController : MonoBehaviour, IInteractable
    {
        [Header("运动")]
        public float openAngle = 95f;
        public float openDuration = 0.35f;

        [Header("身份")]
        [Tooltip("房间号（0 = 未指定；用于破门通知与剧情查询）")]
        public int roomNumber;

        [Header("内部闩锁")]
        public bool lockedFromInside = false;
        public string hardLockedMessage = "门从内部被闩死了，你用肩膀撞了一下，门板发出闷响。";
        public string brokenMessage = "门闩被你撞断了，门开了！";

        [Header("破门")]
        public int breakHitsRequired = 3;
        [Tooltip("可用的破门工具 id 列表（找到任意一件即可一击破门）")]
        public List<string> breakToolIds = new List<string> { "hammer", "dagger" };

        [Header("事件（预留：行为树 / 剧情触发）")]
        public UnityEvent OnDoorOpened = new UnityEvent();
        public UnityEvent OnDoorClosed = new UnityEvent();
        public UnityEvent OnDoorBroken = new UnityEvent();
        public UnityEvent OnForceUnlocked = new UnityEvent();

        /// <summary>任意房门被破开 / 被宣布强行破门（参数：门）。全局事件，供玩法层与 NPC 监听。</summary>
        public static event System.Action<DoorController> OnAnyDoorBrokenGlobal;

        Quaternion _closedRotation;
        Quaternion _openRotation;
        Coroutine _animRoutine;
        int _breakHits;

        public bool IsOpen { get; private set; }
        public bool IsBroken { get; private set; }
        public bool IsLocked => lockedFromInside;

        void Awake()
        {
            _closedRotation = transform.rotation;
            _openRotation = _closedRotation * Quaternion.Euler(0f, openAngle, 0f);
        }

        public string GetInteractionPrompt(PlayerController player)
        {
            // 门已损坏时没有任何可执行动作，不再显示会被误读成 [E] 的提示。
            if (IsBroken) return string.Empty;

            if (lockedFromInside)
            {
                string tool = FindBreakTool(player);
                if (tool != null)
                {
                    string toolName = ItemDatabase.Instance != null
                        ? ItemDatabase.Instance.GetDisplayName(tool)
                        : tool;
                    return $"用{toolName}破门";
                }
                return "尝试强行破门";
            }

            return IsOpen ? "关门" : "开门";
        }

        public void Interact(PlayerController player)
        {
            if (IsBroken)
            {
                Say(player, "门锁已经损坏，这扇门无法正常开合了。");
                return;
            }

            if (lockedFromInside)
            {
                TryBreak(player);
                return;
            }

            if (_animRoutine != null) return; // 正在开合中，忽略
            _animRoutine = StartCoroutine(Swing(!IsOpen));
        }

        /// <summary>返回背包里第一件可用的破门工具 id（没有则返回 null）。</summary>
        string FindBreakTool(PlayerController player)
        {
            if (player == null || player.inventory == null || breakToolIds == null) return null;
            for (int i = 0; i < breakToolIds.Count; i++)
            {
                if (player.inventory.Has(breakToolIds[i])) return breakToolIds[i];
            }
            return null;
        }

        void TryBreak(PlayerController player)
        {
            // 3.3：破门动作会被附近 NPC 看见（视觉）并听见（听觉）
            PlayerActionSignals.NotifyDoorBreak();
            SoundEventManager.Emit(SoundEventType.DoorBreak, transform.position + Vector3.up,
                0.7f, player != null ? player.gameObject : null);

            string tool = FindBreakTool(player);
            if (tool != null)
            {
                Break(true, tool, player);
                return;
            }

            _breakHits++;
            if (_breakHits >= breakHitsRequired)
            {
                Break(false, null, player);
            }
            else
            {
                Say(player, $"{hardLockedMessage}（{_breakHits}/{breakHitsRequired}）");
            }
        }

        void Break(bool withTool, string toolId, PlayerController player)
        {
            IsBroken = true;
            lockedFromInside = false;

            PlayerActionSignals.NotifyDoorBreak();
            SoundEventManager.Emit(SoundEventType.DoorBreak, transform.position + Vector3.up,
                1f, player != null ? player.gameObject : null);

            string toolName = toolId != null && ItemDatabase.Instance != null
                ? ItemDatabase.Instance.GetDisplayName(toolId)
                : null;
            Say(player, withTool ? $"你用{toolName}撬开了门闩，{roomNumber} 号房的门开了！" : brokenMessage);

            OnDoorBroken.Invoke();
            NotifyAllNpcs("door_broken");

            if (_animRoutine == null)
                _animRoutine = StartCoroutine(Swing(true));
        }

        /// <summary>3.5 轮回重置：恢复门的开合 / 破损 / 内部闩锁初始状态。</summary>
        public void ResetDoorState(bool open, bool broken, bool locked)
        {
            if (_animRoutine != null)
            {
                StopCoroutine(_animRoutine);
                _animRoutine = null;
            }

            IsOpen = open;
            IsBroken = broken;
            lockedFromInside = locked;
            _breakHits = 0;
            transform.rotation = open ? _openRotation : _closedRotation;
        }

        /// <summary>
        /// 9号活着醒来：从里面自己拨开门闩并推门出来。
        /// 这不是玩家破门，不触发 PlayerActionSignals / 全场破门警报，只保留开门动作。
        /// </summary>
        public void OpenForSurvivor()
        {
            if (IsBroken) return;
            lockedFromInside = false;

            if (IsOpen || _animRoutine != null) return;
            _animRoutine = StartCoroutine(Swing(true));
            Debug.Log($"[Door] {roomNumber} 号房的门从里面被拨开——住客自己走了出来。");
        }

        /// <summary>
        /// 圆桌会议宣布强行破门（预留接口）：由后续的辩论 / 投票系统在
        /// 决议通过后调用，直接解除内部闩锁（门此后可正常开合），
        /// 并像破门一样通知所有 NPC。
        /// </summary>
        public void ForceUnlockByDeclaration()
        {
            if (IsBroken || !lockedFromInside) return;

            lockedFromInside = false;
            Debug.Log($"[Door] {roomNumber} 号房的门被宣布强行破门，闩锁被拆除。");
            PlayerActionSignals.NotifyDoorBreak();
            SoundEventManager.Emit(SoundEventType.DoorBreak, transform.position + Vector3.up, 1f);
            OnForceUnlocked.Invoke();
            NotifyAllNpcs("door_force_unlocked");
        }

        /// <summary>破门 / 强行解锁的全局通知：静态事件 + 向所有 NPC 的行为树桥接广播。</summary>
        void NotifyAllNpcs(string eventKey)
        {
            OnAnyDoorBrokenGlobal?.Invoke(this);

            BehaviorTreeBridge[] bridges = FindObjectsOfType<BehaviorTreeBridge>();
            for (int i = 0; i < bridges.Length; i++)
            {
                bridges[i].Emit(eventKey, this);
            }
            Debug.Log($"[Door] {roomNumber} 号房事件「{eventKey}」已通知 {bridges.Length} 个 NPC。");
        }

        IEnumerator Swing(bool open)
        {
            Quaternion from = transform.rotation;
            Quaternion to = open ? _openRotation : _closedRotation;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, openDuration);
                transform.rotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
                yield return null;
            }

            transform.rotation = to;
            IsOpen = open;
            _animRoutine = null;

            // 3.3：开关门声音（NPC 替自己开门也会有声音）
            SoundEventManager.Emit(SoundEventType.DoorOpen, transform.position + Vector3.up,
                open ? 0.7f : 0.5f);

            // 外观音效：门轴吱呀声（只在开门时播放）。
            if (open && SFXManager.Instance != null && SFXManager.Instance.HasClip("door_creak"))
                SFXManager.Instance.PlayDoorCreak(0.55f);

            if (open) OnDoorOpened.Invoke();
            else OnDoorClosed.Invoke();
        }

        static void Say(PlayerController player, string message)
        {
            if (player != null) player.ShowMessage(message);
            else Debug.Log("[Door] " + message);
        }
    }
}
