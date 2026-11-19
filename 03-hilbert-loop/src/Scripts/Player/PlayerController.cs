using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 第一人称玩家控制器（使用旧版 Input Manager）。
    /// 操作分层：
    /// - WASD 移动 / 左 Shift 冲刺 / 鼠标视角；
    /// - E 只做友善 / 环境交互（开门、开关窗、拾取、藏匿、私聊、檐台绳移）；
    /// - 鼠标左键只做恶意 / 物理交互（武器袭击、推出窗外、空手轻推）；
    /// - F 注视窗户时翻窗，否则阅读纸条；G 丢弃最后一个物品；
    /// - M 主动摘下面具与斗篷（连按两次确认）；Esc 释放光标，点击画面重新锁定。
    /// 光标未锁定时，同一次点击只用于重新锁定，绝不会被判定为攻击。
    /// 3.2 修正补丁：新增 inputLocked（翻窗 / 绳移动画期间锁输入）与起床过渡（反馈④）。
    /// 3.3：脚步声音事件（行走 / 冲刺不同响度）与武器袭击屏幕震动。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("引用（由场景构建器自动装配）")]
        public Transform cameraPivot;
        public InteractionSystem interaction;
        public InventorySystem inventory;
        [Tooltip("探索界面的 HUD 根节点（准星 / 提示 / 背包）。会议和私聊期间整体隐藏。")]
        public GameObject hudRoot;

        [Header("移动")]
        public float moveSpeed = 3.2f;
        [Tooltip("按住左 Shift 的冲刺速度")]
        public float sprintSpeed = 5.2f;
        public float gravity = -18f;
        public float eyeHeight = 1.62f;

        [Header("脚步（3.3 听觉感知）")]
        [Tooltip("行走时每走多少米触发一次脚步声")]
        public float walkStride = 2.2f;
        [Tooltip("冲刺时每走多少米触发一次脚步声")]
        public float sprintStride = 1.6f;
        [Tooltip("行走脚步响度（0~1）")]
        [Range(0f, 1f)] public float walkLoudness = 0.55f;

        [Header("视角")]
        public float mouseSensitivity = 2.2f;
        public float pitchLimit = 85f;

        /// <summary>输入锁（翻窗 / 绳移 / 起床过渡动画期间由外部置位）。</summary>
        [HideInInspector] public bool inputLocked;

        /// <summary>移动锁（会议期间由 MeetingSystem 置位：禁用视角 / 移动 / 快捷键，UI 鼠标不受影响）。</summary>
        [HideInInspector] public bool movementLocked;

        CharacterController _controller;
        float _pitch;
        float _verticalVelocity;
        bool _wakeupPlaying;
        Vector3 _lastFootstepPosition;
        Coroutine _shakeRoutine;
        Coroutine _pushRoutine;
        float _attackReadyAt;
        int _escapeConsumedFrame = -1;

        /// <summary>
        /// 是否处于可接受探索操作的正常游戏状态：光标锁定、无输入 / 移动锁、无起床过渡。
        /// 会议 / 私聊 / 翻窗 / 绳移 / 结局期间全部为 false。
        /// </summary>
        public bool CanUseGameplayInput =>
            Cursor.lockState == CursorLockMode.Locked
            && !inputLocked && !movementLocked && !_wakeupPlaying
            && !NpcFreeTextInputUI.IsOpen;

        /// <summary>
        /// 是否可以把鼠标左键解释为攻击 / 推搡。
        /// 额外用 _attackReadyAt 吃掉“点击画面重新锁定光标”的那一次点击。
        /// </summary>
        public bool CanAttackInput =>
            CanUseGameplayInput && Time.unscaledTime >= _attackReadyAt;

        /// <summary>
        /// 弹窗自己消费掉本帧的 Esc（例如私聊结束时）：避免同一次按键又被
        /// PlayerController 解释成“释放光标”，导致刚结束对话光标就再次被解锁。
        /// </summary>
        public void ConsumeEscapeThisFrame()
        {
            _escapeConsumedFrame = Time.frameCount;
        }

        /// <summary>眼睛（摄像机）的世界坐标，交互射线起点。</summary>
        public Vector3 EyePosition
        {
            get
            {
                if (cameraPivot != null) return cameraPivot.position;
                return transform.position + Vector3.up * eyeHeight;
            }
        }

        /// <summary>眼睛（摄像机）的世界朝向，交互射线方向。</summary>
        public Quaternion EyeRotation
        {
            get
            {
                if (cameraPivot != null) return cameraPivot.rotation;
                return transform.rotation;
            }
        }

        /// <summary>进入房间触发区：交给名册认知系统判断是否发现空房。</summary>
        public void NotifyEnteredRoom(RoomArea room)
        {
            RoundKnowledge.NotifyPlayerEnteredRoom(this, room);
        }

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (interaction == null) interaction = GetComponentInChildren<InteractionSystem>(true);
            if (inventory == null) inventory = GetComponentInChildren<InventorySystem>(true);
            _lastFootstepPosition = transform.position;
            SetCursorLocked(true);

            // 兜底：结局演出会关掉场景内所有相机，如果这时把场景保存下来，
            // PlayerCamera 就会以 disabled 状态进包，按「开始」后只剩上一帧主菜单。
            // 进入游戏时强制点亮自己的相机，保证一定有画面。
            Camera playerCamera = cameraPivot != null
                ? cameraPivot.GetComponentInChildren<Camera>(true)
                : GetComponentInChildren<Camera>(true);
            if (playerCamera != null) playerCamera.enabled = true;

            // 眼见为实：玩家亲眼看见的出门、翻窗、墙钉、尸体都会自动记入证据。
            if (GetComponent<PlayerObservationSystem>() == null)
                gameObject.AddComponent<PlayerObservationSystem>();
        }

        void Update()
        {
            // 3.5：坠楼（翻窗失足 / 被推下檐台）不再 GameOver，转入幽灵观战。
            if (transform.position.y < -6f)
            {
                PlayerDeathSystem.Instance?.KillPlayer("坠入雪原");
                return;
            }

            // 自由打字期间，所有游戏按键（M 摘面具 / F 阅读 / G 丢弃 / Esc 光标）
            // 都让位给输入框；提交或取消后自动恢复。
            if (NpcFreeTextInputUI.IsOpen) return;

            if (inputLocked || _wakeupPlaying || movementLocked) return;

            HandleCursorToggle();

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                HandleLook();
                // F：注视窗户时交给 InteractionSystem 翻窗；否则才是阅读纸条（互斥，不吞并）
                if (Input.GetKeyDown(KeyCode.F) && !WindowHasFocus()) TryReadNote();
                if (Input.GetKeyDown(KeyCode.G)) DropHeldItem();
                // 模块四：M 主动摘下面具与斗篷（第一次警告，第二次执行献祭）
                if (Input.GetKeyDown(KeyCode.M)) PlayerDeathSystem.Instance?.TrySacrifice();
            }

            HandleMove();
        }

        bool WindowHasFocus()
        {
            return interaction != null && interaction.Current is WindowController;
        }

        void HandleCursorToggle()
        {
            if (!Input.GetKeyDown(KeyCode.Escape) || _escapeConsumedFrame == Time.frameCount) return;

            // Esc 在「锁定 / 释放」之间切换。释放后点击鼠标不再重新锁定：
            // 那一下点击往往是想点 UI 或看清自定义光标，不应让光标凭空消失。
            bool wasLocked = Cursor.lockState == CursorLockMode.Locked;
            SetCursorLocked(!wasLocked);

            ShowMessage(wasLocked
                ? "光标已释放——再次按 Esc 锁定。E 交互 / 私聊，左键攻击 / 推搡。"
                : "光标已锁定。", 4f);
        }

        public void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;

            // 重新锁定光标的这一下点击只用于锁定，不触发攻击 / 推搡。
            if (locked) _attackReadyAt = Time.unscaledTime + 0.15f;
        }

        /// <summary>
        /// 会议就座（模块三 3.4）：黑场期间把玩家放到对应椅子前，复位视角与竖直速度。
        /// CharacterController 启用时直接改 transform 会被它内部状态覆盖，因此先禁用再恢复。
        /// </summary>
        public void PlaceAtMeetingSeat(Vector3 worldPosition, Quaternion worldRotation)
        {
            CharacterController cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            transform.SetPositionAndRotation(worldPosition, worldRotation);

            _pitch = 0f;
            _verticalVelocity = 0f;
            if (cameraPivot != null)
            {
                cameraPivot.localPosition = new Vector3(0f, eyeHeight, 0f);
                cameraPivot.localRotation = Quaternion.identity;
            }

            if (cc != null) cc.enabled = true;
        }

        void HandleLook()
        {
            float yaw = Input.GetAxis("Mouse X") * mouseSensitivity;
            float pitchDelta = Input.GetAxis("Mouse Y") * mouseSensitivity;

            transform.Rotate(0f, yaw, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - pitchDelta, -pitchLimit, pitchLimit);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void HandleMove()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 move = transform.right * h + transform.forward * v;
            if (move.sqrMagnitude > 1f) move.Normalize();

            bool sprinting = Input.GetKey(KeyCode.LeftShift) && move.sqrMagnitude > 0.0001f;
            float speed = sprinting ? sprintSpeed : moveSpeed;

            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            _verticalVelocity += gravity * Time.deltaTime;

            Vector3 velocity = move * speed + Vector3.up * _verticalVelocity;
            _controller.Move(velocity * Time.deltaTime);

            EmitFootstepIfNeeded(sprinting);
        }

        /// <summary>每走一段距离发出一次脚步声（冲刺更密、更响）。</summary>
        void EmitFootstepIfNeeded(bool sprinting)
        {
            if (!_controller.isGrounded) return;

            Vector3 delta = transform.position - _lastFootstepPosition;
            delta.y = 0f;
            float stride = sprinting ? sprintStride : walkStride;
            if (delta.magnitude < stride) return;

            _lastFootstepPosition = transform.position;
            SoundEventManager.Emit(
                SoundEventType.Footstep,
                transform.position + Vector3.up * 1.0f,
                sprinting ? 1f : walkLoudness,
                gameObject);
        }

        void DropHeldItem()
        {
            if (inventory == null) return;
            inventory.DropLastItem(EyePosition + EyeRotation * Vector3.forward * 0.6f);
        }

        /// <summary>
        /// 阅读携带的纸条（F 键，或由外部代码调用以便验证）。
        /// 模块三 3.2：阅读纸条计入 8号行为记录（curiosity+10 / noteTrust+20），
        /// 与「拿到纸条一直不读」(IgnoredNote) 互斥。
        /// </summary>
        public void TryReadNote()
        {
            if (inventory == null || !inventory.Has("note"))
            {
                ShowMessage("你手上没有纸条。");
                return;
            }

            PlayerBehaviorRecorder.Instance?.RecordEvent(PlayerBehaviorRecorder.EventOpenedNote);
            ShowMessage("你展开纸条，上面写着：“你的任务已经完成，活过这一轮圆桌会议，" +
                        "不被投出，则你可以逃出生天。注意，不要让别人发现这张纸条。”", 8f);
        }

        /// <summary>
        /// 起床过渡（3.2 修正补丁·反馈④）：玩家 transform 已落在床边站立点，
        /// 相机从床上的躺姿（面向天花板）平滑抬起、站直，期间输入锁定。
        /// </summary>
        public void BeginWakeup(Vector3 bedCameraWorld, Quaternion bedCameraRotation, float duration = 2.4f)
        {
            StartCoroutine(WakeupRoutine(bedCameraWorld, bedCameraRotation, duration));
        }

        IEnumerator WakeupRoutine(Vector3 bedCamWorld, Quaternion bedCamRot, float duration)
        {
            if (cameraPivot == null) yield break;

            _wakeupPlaying = true;
            inputLocked = true;

            Vector3 startLocal = transform.InverseTransformPoint(bedCamWorld);
            Quaternion startRot = Quaternion.Inverse(transform.rotation) * bedCamRot;
            Vector3 endLocal = new Vector3(0f, eyeHeight, 0f);

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.1f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                cameraPivot.localPosition = Vector3.Lerp(startLocal, endLocal, k);
                cameraPivot.localRotation = Quaternion.Slerp(startRot, Quaternion.identity, k);
                yield return null;
            }

            cameraPivot.localPosition = endLocal;
            cameraPivot.localRotation = Quaternion.identity;
            _pitch = 0f;
            _wakeupPlaying = false;
            inputLocked = false;

            Debug.Log("[Player] 起床过渡完成（相机已从躺姿抬起站直）。");
        }

        /// <summary>
        /// 屏幕震动（3.3 武器袭击的简易攻击反馈）：相机在本地空间做短促衰减抖动。
        /// 起床过渡期间不叠加（避免与躺姿相机插值冲突）。
        /// </summary>
        public void ShakeCamera(float duration = 0.25f, float magnitude = 0.05f)
        {
            if (cameraPivot == null || _wakeupPlaying) return;
            if (_shakeRoutine != null) StopCoroutine(_shakeRoutine);
            _shakeRoutine = StartCoroutine(ShakeRoutine(duration, magnitude));
        }

        IEnumerator ShakeRoutine(float duration, float magnitude)
        {
            Vector3 baseLocal = new Vector3(0f, eyeHeight, 0f);
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float decay = Mathf.Clamp01(1f - t / Mathf.Max(0.01f, duration));
                cameraPivot.localPosition = baseLocal + Random.insideUnitSphere * (magnitude * decay);
                yield return null;
            }

            cameraPivot.localPosition = baseLocal;
            _shakeRoutine = null;
        }

        /// <summary>
        /// 第一人称“推了一把”：镜头先猛的前冲，再收回来。
        /// 玩家没有可见身体模型，把人推出窗外的动作只能靠这个来表现。
        /// </summary>
        public void PushLunge(float forward = 0.38f, float duration = 0.55f)
        {
            if (cameraPivot == null || _wakeupPlaying) return;
            if (_pushRoutine != null) StopCoroutine(_pushRoutine);
            if (_shakeRoutine != null)
            {
                StopCoroutine(_shakeRoutine);
                _shakeRoutine = null;
            }

            _pushRoutine = StartCoroutine(PushLungeRoutine(
                Mathf.Max(0.05f, forward), Mathf.Max(0.1f, duration)));
        }

        IEnumerator PushLungeRoutine(float forward, float duration)
        {
            Vector3 baseLocal = new Vector3(0f, eyeHeight, 0f);
            Camera cam = cameraPivot.GetComponentInChildren<Camera>(true);
            float baseFov = cam != null ? cam.fieldOfView : 0f;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                // 前 35% 冲出去，后 65% 收回来。
                float thrust = k < 0.35f
                    ? Mathf.SmoothStep(0f, 1f, k / 0.35f)
                    : 1f - Mathf.SmoothStep(0f, 1f, (k - 0.35f) / 0.65f);
                cameraPivot.localPosition = baseLocal + Vector3.forward * (forward * thrust);
                // 镜头视野同步前冲：第一人称没有手臂模型，靠这个让"推"这一下看得见。
                if (cam != null) cam.fieldOfView = baseFov + 6f * thrust;
                yield return null;
            }

            cameraPivot.localPosition = baseLocal;
            if (cam != null) cam.fieldOfView = baseFov;
            _pushRoutine = null;
        }

        /// <summary>所有交互物显示提示消息的统一入口（HUD 由 InteractionSystem 管理）。</summary>
        public void ShowMessage(string text, float duration = 2.5f)
        {
            // 会议 / 私聊有独立的全屏对话区，探索 HUD 的短消息会压在台词上。
            if (hudRoot != null && !hudRoot.activeSelf) return;
            if (interaction != null) interaction.ShowMessage(text, duration);
            else Debug.Log("[Message] " + text);
        }

        /// <summary>探索 HUD 显隐：会议 / 私聊期间收起准星、提示和背包，避免与对话 UI 叠字。</summary>
        public void SetHudVisible(bool visible)
        {
            if (hudRoot != null) hudRoot.SetActive(visible);
        }
    }
}
