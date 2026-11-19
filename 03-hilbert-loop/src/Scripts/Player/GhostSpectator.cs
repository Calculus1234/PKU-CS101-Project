using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 幽灵观战（模块三 3.5）：
    /// 玩家死亡后摄像机脱离身体，变成空中自由视角：
    /// - WASD 平移 / 空格、左 Ctrl 升降 / 按住右键拖动视角 / 按住 Shift 加速
    ///   （不再占用 E / Q，避免与探索阶段的交互 / 藏匿切换混淆）；
    /// - Tab 在存活 NPC 之间跳跃（再按一次切下一个，循环一圈回到自由视角）；
    /// - K 或界面「快进」按钮加速时间：若结局 D 判定已装配则立刻掷骰（顿悟成功 → 结局 D，
    ///   失败 → 结局 B）；否则快进到本轮轮回结算；
    /// - 顶部持续显示当前「顿悟」的模糊描述（不显示概率数值），让玩家知道自己的死留下了多少影响；
    /// - 屏幕四周黑色 / 血色暗角 + 提示「你已出局，正在观察循环」。
    /// 不改动任何 NPC 行为树 / 感知 / 寻路，只是把玩家相机从身体上摘下来。
    /// </summary>
    public class GhostSpectator : MonoBehaviour
    {
        public static GhostSpectator Instance { get; private set; }

        [Header("自由相机")]
        public float moveSpeed = 5f;
        public float sprintMultiplier = 2.4f;
        public float lookSensitivity = 2.4f;
        public float minY = 0.4f;
        public float maxY = 14f;

        Camera _camera;
        Transform _cameraTransform;
        Transform _originalParent;
        Vector3 _originalLocalPosition;
        Quaternion _originalLocalRotation;

        bool _active;
        float _yaw;
        float _pitch;

        NPCController[] _npcs = new NPCController[0];
        int _followIndex = -1;
        Vector3 _followVelocity;

        Canvas _canvas;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _hintText;
        TextMeshProUGUI _endingDText;
        Image _flash;
        float _endingDRefreshTimer;

        public bool IsActive => _active;

        /// <summary>把玩家具体的死因写在顶部状态条上（而不是继续显示通用文案）。</summary>
        public void SetDeathCause(string cause)
        {
            if (_statusText == null || string.IsNullOrEmpty(cause)) return;
            _statusText.text = Localization.T($"你已出局　·　{cause}");
        }

        void Awake()
        {
            Instance = this;
            BuildUI();
            SetUIVisible(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 进出
        // ------------------------------------------------------------------

        /// <summary>让摄像机脱离玩家身体并进入幽灵模式。</summary>
        public void Enter(Transform cameraTransform)
        {
            if (_active || cameraTransform == null) return;

            _cameraTransform = cameraTransform;
            _originalParent = cameraTransform.parent;
            _originalLocalPosition = cameraTransform.localPosition;
            _originalLocalRotation = cameraTransform.localRotation;

            cameraTransform.SetParent(null, true);

            // 死亡镜头不能只是往上抬 1.2 米：玩家如果死在圆桌旁 / 椅子上，
            // 镜头很容易卡在桌面以下，只能看见桌板和桌腿。
            // 这里统一给一个“看得见现场”的观战机位：
            // - 桌椅圈内死亡：抬到桌面上方，并沿死亡方向退到椅子圈外，俯视桌心；
            // - 其他位置：在尸体上方 1.5 米，向下看尸体所在处；
            // - 高度限制在 2.45~3.1 之间（层高 3.5，避免穿顶）。
            Vector3 focus = cameraTransform.position;
            Vector3 spawn = focus + Vector3.up * 1.5f;
            spawn.y = Mathf.Clamp(spawn.y, 2.45f, 3.1f);

            Vector2 flat = new Vector2(focus.x, focus.z);
            if (flat.magnitude < 2.9f)
            {
                Vector2 away = flat.sqrMagnitude < 0.04f
                    ? new Vector2(0f, -1f)
                    : flat.normalized;
                Vector2 seat = away * 3.0f;
                spawn = new Vector3(seat.x, 2.55f, seat.y);
                focus = new Vector3(0f, 0.95f, 0f);
            }

            cameraTransform.position = spawn;
            Vector3 look = focus - spawn;
            if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
            cameraTransform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);

            Vector3 euler = cameraTransform.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);

            _active = true;
            _followIndex = -1;
            RefreshNpcList();
            SetUIVisible(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (_flash != null) StartCoroutine(FlashRoutine());
            _endingDRefreshTimer = 0f;
            RefreshEndingDStatus();

            Debug.Log("[Ghost] 进入幽灵观战：WASD + 空格/Ctrl 漫游，右键拖动视角，Tab 切换 NPC，" +
                      "K 快进时间 / 触发顿悟判定。");
        }

        /// <summary>下一轮开始：相机回到玩家身体，退出幽灵模式。</summary>
        public void Exit()
        {
            if (!_active) return;

            _active = false;
            SetUIVisible(false);

            if (_cameraTransform != null && _originalParent != null)
            {
                _cameraTransform.SetParent(_originalParent, false);
                _cameraTransform.localPosition = _originalLocalPosition;
                _cameraTransform.localRotation = _originalLocalRotation;
            }

            _cameraTransform = null;
            _originalParent = null;
            _followIndex = -1;

            Debug.Log("[Ghost] 退出幽灵观战，摄像机已回到玩家身体。");
        }

        // ------------------------------------------------------------------
        // 输入
        // ------------------------------------------------------------------

        void Update()
        {
            if (!_active) return;

            // 自由打字期间不响应 Tab / K / Esc / 移动，避免输入文字时镜头乱飞。
            if (NpcFreeTextInputUI.IsOpen) return;

            if (Input.GetKeyDown(KeyCode.Tab)) CycleFollowTarget();
            if (Input.GetKeyDown(KeyCode.K)) FastForward();
            if (Input.GetKeyDown(KeyCode.Escape)) Cursor.lockState = CursorLockMode.None;

            _endingDRefreshTimer -= Time.unscaledDeltaTime;
            if (_endingDRefreshTimer <= 0f)
            {
                _endingDRefreshTimer = 0.3f;
                RefreshEndingDStatus();
            }

            if (_followIndex >= 0)
            {
                UpdateFollowCamera();
                return;
            }

            HandleLook();
            HandleMove();
        }

        void HandleLook()
        {
            if (!Input.GetMouseButton(1)) return;

            _yaw += Input.GetAxis("Mouse X") * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * lookSensitivity, -85f, 85f);
            _cameraTransform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        void HandleMove()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            float up = 0f;
            if (Input.GetKey(KeyCode.Space)) up += 1f;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) up -= 1f;

            Transform cam = _cameraTransform;
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
            Vector3 move = right * h + forward * v + Vector3.up * up;
            if (move.sqrMagnitude > 1f) move.Normalize();

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);
            Vector3 next = cam.position + move * speed * Time.unscaledDeltaTime;
            next.y = Mathf.Clamp(next.y, minY, maxY);
            cam.position = next;
        }

        void CycleFollowTarget()
        {
            RefreshNpcList();
            if (_npcs.Length == 0)
            {
                _followIndex = -1;
                UpdateStatus("没有可观察的幸存者。");
                return;
            }

            _followIndex++;
            if (_followIndex >= _npcs.Length) _followIndex = -1;

            if (_followIndex < 0)
            {
                UpdateStatus("自由视角");
                return;
            }

            NPCController npc = _npcs[_followIndex];
            _followVelocity = Vector3.zero;
            UpdateStatus($"正在观察 {npc.Number} 号");
            Debug.Log($"[Ghost] 跟随 NPC {npc.Number} 号。");
        }

        void RefreshNpcList()
        {
            NPCController[] all = FindObjectsOfType<NPCController>();
            var alive = new System.Collections.Generic.List<NPCController>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.activeInHierarchy && !all[i].isDead)
                    alive.Add(all[i]);
            }
            alive.Sort((a, b) => a.Number.CompareTo(b.Number));
            _npcs = alive.ToArray();
        }

        void UpdateFollowCamera()
        {
            if (_followIndex < 0 || _followIndex >= _npcs.Length)
            {
                _followIndex = -1;
                return;
            }

            NPCController npc = _npcs[_followIndex];
            if (npc == null || npc.isDead || !npc.gameObject.activeInHierarchy)
            {
                RefreshNpcList();
                _followIndex = Mathf.Clamp(_followIndex, 0, Mathf.Max(0, _npcs.Length - 1));
                if (_npcs.Length == 0) { _followIndex = -1; return; }
                npc = _npcs[_followIndex];
            }

            Vector3 target = npc.transform.position + Vector3.up * 1.45f;
            Vector3 desired = target - npc.transform.forward * 2.6f + Vector3.up * 0.9f;
            _cameraTransform.position = Vector3.SmoothDamp(_cameraTransform.position, desired, ref _followVelocity, 0.25f);
            _cameraTransform.rotation = Quaternion.Slerp(
                _cameraTransform.rotation,
                Quaternion.LookRotation(target - _cameraTransform.position, Vector3.up),
                Time.unscaledDeltaTime * 6f);
        }

        /// <summary>
        /// 快进：结局 D 已装配时立刻进行确定性判定（只看着证据链）；
        /// 否则退回旧的「跳过本轮」行为。
        /// </summary>
        public void FastForward()
        {
            if (!_active) return;

            if (EndingDSystem.Instance != null && EndingDSystem.Instance.IsArmed)
            {
                UpdateStatus("时间开始加速……");
                EndingDSystem.Instance.ResolveByFastForward();
                return;
            }

            SkipToReincarnation();
        }

        /// <summary>
        /// 刷新顶部「顿悟」状态（重构）：
        /// 不显示概率，只显示真相链走到哪一步、还缺哪一环。
        /// </summary>
        void RefreshEndingDStatus()
        {
            EndingDSystem d = EndingDSystem.Instance;
            if (_endingDText == null) return;

            if (d == null || (!d.IsArmed && !d.IsResolved))
            {
                _endingDText.text = string.Empty;
                return;
            }

            bool success = d.IsResolved ? d.LastResolvedToD : d.Evaluate();
            string state = d.IsResolved
                ? (success ? "顿悟已发生" : "顿悟没有发生")
                : "他们还在想";

            string chain;
            if (!CycleState.playerDisguiseRemoved)
                chain = "你从没在他们面前摘下过面具——没有人见过你的脸";
            else if (!CycleState.faceComparisonDone)
                chain = "9号的面具还扣在脸上——没有人看见过那张脸";
            else if (d.LivingNpcCount < 2)
                chain = "圆桌旁已经凑不齐两个活人";
            else if (d.InformedNpcCount < 2)
                chain = $"只有 {d.InformedNpcCount} 个活人知道两张脸一模一样";
            else if (d.CorroboratedNpcCount < 1)
                chain = "两张脸对上了，但门锁 / 磁铁 / 钉子还没被拼进来";
            else
                chain = "两张脸和物证已经对上——只差有人先动手";

            _endingDText.text =
                $"{state}　·　{GetDeathTypeName(d.DeathType)}\n" +
                $"{chain}\n" +
                $"活人 {d.LivingNpcCount} · 知道脸相同 {d.InformedNpcCount} · 拼进物证 {d.CorroboratedNpcCount}";
        }

        static string GetDeathTypeName(PlayerDeathType type)
        {
            switch (type)
            {
                case PlayerDeathType.Sacrifice: return "主动摘下面具";
                case PlayerDeathType.VotedOut: return "被投票抹杀";
                case PlayerDeathType.Killed: return "被杀";
                default: return "未知";
            }
        }

        /// <summary>跳过：直接进入轮回结算（若正在投票，先立即结算）。</summary>
        public void SkipToReincarnation()
        {
            if (!_active) return;

            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase == GamePhase.Voting
                && VotingSystem.Instance != null)
            {
                VotingSystem.Instance.SkipVote();
                return;
            }

            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase != GamePhase.Reincarnation)
                GameManager.Instance.SetPhase(GamePhase.Reincarnation);
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------

        void BuildUI()
        {
            var canvasGo = new GameObject("GhostOverlay_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 200;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            EnsureEventSystem();

            // 血色暗角
            var vignetteGo = new GameObject("Vignette", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            vignetteGo.transform.SetParent(canvasGo.transform, false);
            var vignetteRect = vignetteGo.GetComponent<RectTransform>();
            vignetteRect.anchorMin = Vector2.zero;
            vignetteRect.anchorMax = Vector2.one;
            vignetteRect.offsetMin = Vector2.zero;
            vignetteRect.offsetMax = Vector2.zero;
            var vignette = vignetteGo.GetComponent<Image>();
            vignette.sprite = BuildVignetteSprite();
            vignette.color = Color.white;
            vignette.raycastTarget = false;

            // 死亡红闪
            var flashGo = new GameObject("DeathFlash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            flashGo.transform.SetParent(canvasGo.transform, false);
            var flashRect = flashGo.GetComponent<RectTransform>();
            flashRect.anchorMin = Vector2.zero;
            flashRect.anchorMax = Vector2.one;
            flashRect.offsetMin = Vector2.zero;
            flashRect.offsetMax = Vector2.zero;
            _flash = flashGo.GetComponent<Image>();
            _flash.color = new Color(0.5f, 0f, 0f, 0f);
            _flash.raycastTarget = false;

            // 顶部只留一条状态条；顿悟状态（最多三行）单独占一块区域，
            // 不能与状态条 / 会议标题挤在同一高度。
            _statusText = CreateText(canvasGo.transform, "Status", Localization.T("你已出局，正在观察循环"),
                new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(1500f, 56f), 34f, TextAlignmentOptions.Center);
            _statusText.color = new Color(0.92f, 0.72f, 0.72f, 1f);

            _endingDText = CreateText(canvasGo.transform, "EndingDStatus", string.Empty,
                new Vector2(0.5f, 1f), new Vector2(0f, -168f), new Vector2(1600f, 170f), 21f, TextAlignmentOptions.Top);
            _endingDText.color = new Color(0.96f, 0.78f, 0.48f, 1f);
            _endingDText.lineSpacing = 6f;
            _endingDText.enableWordWrapping = true;

            _hintText = CreateText(canvasGo.transform, "Hint",
                Localization.T("WASD 漫游　空格 / Ctrl 升降　按住右键拖动视角　Shift 加速\n" +
                               "Tab 切换观察对象　K 推进到天亮（他们会不会顿悟，取决于证据）"),
                new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1400f, 80f), 24f, TextAlignmentOptions.Center);
            _hintText.color = new Color(0.85f, 0.82f, 0.82f, 0.9f);

            // 跳过按钮
            var buttonGo = new GameObject("SkipButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var buttonRect = buttonGo.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(1f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0f);
            buttonRect.pivot = new Vector2(1f, 0f);
            buttonRect.anchoredPosition = new Vector2(-60f, 55f);
            buttonRect.sizeDelta = new Vector2(220f, 58f);
            var buttonImage = buttonGo.GetComponent<Image>();
            buttonImage.color = new Color(0.35f, 0.06f, 0.06f, 0.9f);
            var button = buttonGo.GetComponent<Button>();
            button.onClick.AddListener(FastForward);

            var buttonText = CreateText(buttonGo.transform, "Label", Localization.T("推进到天亮 [K]"),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(210f, 50f), 24f, TextAlignmentOptions.Center);
            buttonText.raycastTarget = false;
        }

        void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, string text,
                                          Vector2 anchor, Vector2 anchoredPosition, Vector2 size,
                                          float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            label.raycastTarget = false;
            ChineseFontUtility.Apply(label);
            return label;
        }

        static Sprite BuildVignetteSprite()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float maxDistance = center.magnitude;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center) / maxDistance;
                    float a = Mathf.Clamp01((d - 0.42f) / 0.58f);
                    a = a * a * 0.92f;
                    tex.SetPixel(x, y, new Color(0.18f, 0.01f, 0.01f, a));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        void SetUIVisible(bool visible)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
        }

        void UpdateStatus(string text)
        {
            if (_statusText != null) _statusText.text = Localization.T($"你已出局，正在观察循环　·　{text}");
        }

        System.Collections.IEnumerator FlashRoutine()
        {
            float t = 0f;
            while (t < 0.6f)
            {
                t += Time.unscaledDeltaTime;
                float a = Mathf.Lerp(0.55f, 0f, t / 0.6f);
                if (_flash != null) _flash.color = new Color(0.5f, 0f, 0f, a);
                yield return null;
            }
            if (_flash != null) _flash.color = new Color(0.5f, 0f, 0f, 0f);
        }

        static float NormalizePitch(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            return Mathf.Clamp(angle, -85f, 85f);
        }
    }
}
