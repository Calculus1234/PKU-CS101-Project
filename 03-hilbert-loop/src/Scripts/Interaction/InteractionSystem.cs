using System.Collections;
using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 可交互对象统一接口（E 键：友善 / 环境交互）。
    /// 门的开关、物品拾取、窗户开合、藏匿点、NPC 私聊等都实现该接口。
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// 返回提示文本（不含按键前缀，系统会自动补 "[E] "）。返回空字符串表示不显示提示。
        /// 特例：多键交互对象（如藏匿点 / 窗户）可返回以 "[" 开头的自带按键说明（可为多行），
        /// 此时系统不再自动补前缀。
        /// </summary>
        string GetInteractionPrompt(PlayerController player);

        /// <summary>执行友善 / 环境交互（E 键）。绝不应在这里执行攻击或推搡。</summary>
        void Interact(PlayerController player);
    }

    /// <summary>
    /// 恶意 / 物理交互目标接口（鼠标左键）。
    /// 与 IInteractable 完全分离：E 永远不会调用这里的任何方法。
    /// </summary>
    public interface IHostileInteractable
    {
        /// <summary>返回左键提示（自带 "[左键] " 前缀；没有可执行动作时返回空字符串）。</summary>
        string GetHostilePrompt(PlayerController player);

        /// <summary>执行左键动作；返回 true 表示本次点击已被消费。</summary>
        bool TryHostileInteract(PlayerController player);
    }

    /// <summary>可被武器袭击的目标（NPC 等）。由 InventorySystem.UseWeaponOn 调用。</summary>
    public interface IKillable
    {
        /// <summary>袭击成功（按武器 killChance 判定后）。</summary>
        void ApplyKill(PlayerController player, ItemDefinition weapon);

        /// <summary>袭击失败（武器未命中要害）。</summary>
        void ApplyKillAttemptFailed(PlayerController player, ItemDefinition weapon);
    }

    /// <summary>
    /// 交互系统：从眼睛位置沿视线做短射线检测。
    /// 按键分层（绝不互相吞并）：
    /// - E：只触发 IInteractable —— 开门 / 开关窗 / 拾取 / 藏匿 / 私聊 / 檐台绳移；
    /// - 鼠标左键：只触发 IHostileInteractable —— 武器袭击 / 推出窗外 / 空手轻推；
    /// - R / F：窗户的拨锁与翻窗；Q：屋内把磁铁放到窗台 / 取回；F 在非窗户目标上由 PlayerController 处理为阅读纸条；
    /// - Q：藏匿点切换要藏的物品；1-9：取回指定编号；T：藏匿点全部取回；
    ///   注视窗户时 Q = 放置 / 取回内磁铁，T = 收回系在本房窗外的长绳（按当前目标区分，不会冲突）。
    /// 光标未锁定时只显示“点击画面重新锁定”，不会把同一次点击当成攻击。
    /// </summary>
    public class InteractionSystem : MonoBehaviour
    {
        [Header("参数")]
        public float interactDistance = 2.6f;
        public float chatInteractDistance = 2f;
        public LayerMask raycastMask = ~0;

        [Header("HUD（由场景构建器装配）")]
        public TextMeshProUGUI promptText;
        public TextMeshProUGUI messageText;

        readonly RaycastHit[] _hitBuffer = new RaycastHit[32];
        PlayerController _player;
        IInteractable _current;
        IHostileInteractable _hostileTarget;
        NPCController _chatTarget;
        Coroutine _messageRoutine;

        /// <summary>当前注视的友善 / 环境交互对象（供玩家控制器做按键互斥判断）。</summary>
        public IInteractable Current => _current;

        /// <summary>准星是否应高亮：射线命中可交互物，或附近有可私聊的 NPC。</summary>
        public bool HasFocus => _current != null || _chatTarget != null;

        /// <summary>当前注视的恶意 / 物理交互目标（没有则为 null）。</summary>
        public IHostileInteractable HostileTarget => _hostileTarget;

        void Awake()
        {
            _player = GetComponentInParent<PlayerController>();
        }

        void Start()
        {
            // TextMeshPro 默认字体不含中文字形，运行时切换到系统动态字体
            ChineseFontUtility.Apply(promptText);
            ChineseFontUtility.Apply(messageText);
        }

        void Update()
        {
            if (_player == null) return;

            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding)
            {
                ClearTargets();
                return;
            }

            IInteractable target = FindTarget();
            _hostileTarget = target as IHostileInteractable;
            _chatTarget = target == null ? FindChatTarget() : null;

            if (target != _current || (_current != null && (_current as Component) == null))
            {
                _current = target;
                RefreshPrompt();
            }
            else
            {
                // 提示内容可能随持有物品 / 私聊条件变化，每帧刷新
                RefreshPrompt();
            }

            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!locked || !_player.CanUseGameplayInput)
            {
                if (promptText != null) promptText.gameObject.SetActive(false);
                return;
            }

            // E：友善 / 环境交互。即使手里拿着武器、面前是 NPC，也只拉起私聊。
            if (Input.GetKeyDown(KeyCode.E))
            {
                if (_current != null)
                {
                    _current.Interact(_player);
                }
                else if (_chatTarget != null && PrivateChatSystem.Instance != null)
                {
                    PrivateChatSystem.Instance.TryStartChat(_chatTarget);
                }
            }

            // 鼠标左键：恶意 / 物理交互。CanAttackInput 会排除“点击重新锁定光标”的那一下。
            if (Input.GetMouseButtonDown(0) && _player.CanAttackInput)
            {
                _hostileTarget?.TryHostileInteract(_player);
            }

            // 藏匿点：Q 切换要藏哪件，数字键 1-9 取回指定编号的已藏物品，T 全部取回。
            // E 只负责藏匿，不参与取回，也不会被 R（窗锁）抢走。
            if (_current is HidingSpot spot)
            {
                if (Input.GetKeyDown(KeyCode.Q))
                {
                    spot.CycleSelection(_player);
                }

                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    {
                        spot.TakeByNumber(i + 1, _player);
                        break;
                    }
                }

                if (Input.GetKeyDown(KeyCode.T))
                {
                    spot.TakeAll(_player);
                }
            }

            // 窗户：R = 拨锁销 / 磁铁反锁，Q = 屋内放置 / 取回内磁铁，
            // F = 翻窗，T = 收回本房窗外的长绳（E 由 IInteractable.Interact 负责开合）。
            // Q / T 只在注视窗户时生效，不会与藏匿点的「切换 / 全部取出」冲突。
            if (_current is WindowController window)
            {
                if (Input.GetKeyDown(KeyCode.R)) window.RKeyInteract(_player);
                if (Input.GetKeyDown(KeyCode.Q)) window.QKeyInteract(_player);
                if (Input.GetKeyDown(KeyCode.F)) window.FKeyInteract(_player);
                if (Input.GetKeyDown(KeyCode.T)) window.TKeyInteract(_player);
            }
        }

        void ClearTargets()
        {
            _current = null;
            _hostileTarget = null;
            _chatTarget = null;
            if (promptText != null) promptText.gameObject.SetActive(false);
        }

        IInteractable FindTarget()
        {
            if (_player == null) return null;

            Vector3 origin = _player.EyePosition;
            Vector3 direction = _player.EyeRotation * Vector3.forward;

            int count = Physics.RaycastNonAlloc(
                origin, direction, _hitBuffer, interactDistance,
                raycastMask, QueryTriggerInteraction.Collide);

            IInteractable best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hitBuffer[i];
                if (hit.distance >= bestDistance) continue;
                if (hit.collider.GetComponentInParent<PlayerController>() != null) continue;

                IInteractable candidate = hit.collider.GetComponentInParent<IInteractable>();
                if (candidate != null)
                {
                    best = candidate;
                    bestDistance = hit.distance;
                }
            }

            return best;
        }

        /// <summary>
        /// 模块四：视线没有命中可交互物时，寻找 2 米内可私聊的 NPC。
        /// 只作为兜底，保证玩家不必精确瞄准也能发起私聊。
        /// </summary>
        NPCController FindChatTarget()
        {
            if (_player == null) return null;
            if (PrivateChatSystem.Instance == null) return null;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return null;

            NPCController best = null;
            float bestDistance = Mathf.Min(chatInteractDistance,
                PrivateChatSystem.Instance.chatDistance);

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null) continue;
                if (!PrivateChatSystem.CanChat(_player, npc)) continue;

                float distance = Vector3.Distance(_player.transform.position, npc.transform.position);
                if (distance <= bestDistance)
                {
                    best = npc;
                    bestDistance = distance;
                }
            }

            return best;
        }

        void RefreshPrompt()
        {
            if (promptText == null) return;

            if (Cursor.lockState != CursorLockMode.Locked
                || (_player != null && !_player.CanUseGameplayInput))
            {
                promptText.gameObject.SetActive(false);
                return;
            }

            string prompt = null;
            if (_current != null && (_current as Component) != null)
                prompt = _current.GetInteractionPrompt(_player);

            if (string.IsNullOrEmpty(prompt) && _chatTarget != null)
                prompt = $"[E] 与{_chatTarget.Number}号私下交谈";

            if (!string.IsNullOrEmpty(prompt) && prompt[0] != '[')
                prompt = "[E] " + prompt;

            // 恶意 / 物理交互提示独立追加，绝不混进 E 的动作里。
            if (_hostileTarget != null && (_hostileTarget as Component) != null)
            {
                string hostile = _hostileTarget.GetHostilePrompt(_player);
                if (!string.IsNullOrEmpty(hostile))
                {
                    prompt = string.IsNullOrEmpty(prompt)
                        ? hostile
                        : prompt + (prompt.Contains("\n") ? "\n" : "　") + hostile;
                }
            }

            prompt = Localization.TranslatePrompt(prompt);

            bool show = !string.IsNullOrEmpty(prompt);
            promptText.gameObject.SetActive(show);
            if (show) promptText.text = prompt;
        }

        /// <summary>在屏幕上显示一条短消息（会覆盖上一条）。</summary>
        public void ShowMessage(string text, float duration = 2.5f)
        {
            if (messageText == null)
            {
                Debug.Log("[Message] " + text);
                return;
            }

            if (_messageRoutine != null) StopCoroutine(_messageRoutine);
            _messageRoutine = StartCoroutine(MessageRoutine(text, duration));
        }

        IEnumerator MessageRoutine(string text, float duration)
        {
            messageText.text = Localization.T(text);
            messageText.gameObject.SetActive(true);
            yield return new WaitForSeconds(duration);
            messageText.text = string.Empty;
            messageText.gameObject.SetActive(false);
            _messageRoutine = null;
        }

        /// <summary>
        /// 立即清掉屏幕中上方的短消息（会议 / 死亡观战等会接管屏幕的场合）。
        /// 只清文本，不影响底部交互提示。
        /// </summary>
        public void HideMessage()
        {
            if (_messageRoutine != null)
            {
                StopCoroutine(_messageRoutine);
                _messageRoutine = null;
            }

            if (messageText != null)
            {
                messageText.text = string.Empty;
                messageText.gameObject.SetActive(false);
            }
        }
    }
}
