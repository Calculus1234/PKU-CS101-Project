using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace HilbertLoop
{
    /// <summary>
    /// 窗户控制器（3.2 修正补丁重写，挂在窗扇铰链根节点上；本轮把磁铁反锁泛化到所有窗）：
    /// - 老式窗锁：一根手拨锁销。锁销推上去就机械卡死，不需要任何外力维持；
    ///   屋里的人按 R 拨上 / 拨开；
    /// - 磁铁只是「隔着玻璃拨锁销」的工具，物理上需要两块：一块在窗内锁销旁，
    ///   一块在窗外。1 号在 9 号窗外用外磁铁吸住窗内的内磁铁，带着锁销滑进锁孔，
    ///   然后抽走外磁铁。内磁铁失去吸力掉回窗台，锁销却依旧卡死；
    /// - **每扇窗都能用同样的办法反锁**：9 号窗的碎石堆里本来就藏着一块内磁铁；
    ///   其他窗里侧没有，但玩家可以在屋里按 Q 把自己携带的磁铁放到窗台锁销旁，
    ///   再绕到窗外用另一块磁铁完成反锁；
    /// - 玩家在室内不能靠「取下磁铁」自动解锁；掉在窗台上的内磁铁会作为世界物品
    ///   出现（9 号是 mystic_magnet，玩家自己放的是 magnet），可以捡走，捡走也不会开锁；
    /// - 按键：E = 开关窗；R = 屋内拨锁销 / 屋外持磁铁隔玻璃拨锁；
    ///   Q = 屋内把磁铁放到窗台 / 取回；F = 翻窗。
    /// - 所有窗户初始不反锁；9 号窗的内磁铁由 1 号流程末尾隔玻璃驱动后掉落。
    /// </summary>
    public class WindowController : MonoBehaviour, IInteractable
    {
        [Header("运动")]
        public float openAngle = -80f;
        public float openDuration = 0.4f;

        [Header("装配（场景构建器）")]
        [Tooltip("所在房间号（1~9）")]
        public int roomNumber = -1;

        [Tooltip("锁销视觉（拨上时旋转示意；挂在窗框上）")]
        public GameObject latchVisual;

        [Tooltip("磁铁对视觉（外侧 + 内侧两块；只在隔玻璃拨锁的瞬间显示）")]
        public GameObject magnetVisual;

        [Tooltip("磁铁对刚贴上玻璃时的位置（窗户本地坐标；靠近窗台石子堆）")]
        public Vector3 magnetOnGlassLocal = new Vector3(0.5f, -0.62f, 0f);

        [Tooltip("磁铁被推到锁销旁的位置（窗户本地坐标；锁销在 (0.52,-0.16,0.1)）")]
        public Vector3 magnetLockedLocal = new Vector3(0.52f, -0.16f, 0f);

        [Tooltip("内磁铁掉落后落在窗台的位置（窗户本地坐标；9 号房石子堆）")]
        public Vector3 innerMagnetSillLocal = new Vector3(0.35f, -0.53f, -0.10f);

        [Header("磁铁物理（隔空推锁闩）")]
        [Tooltip("这扇窗内是否已有可被外磁铁吸起的内磁铁（9 号房开局为 true；其他窗可由玩家按 Q 放置）")]
        public bool hasInnerMagnet;

        [Tooltip("外磁铁吸住内磁铁后的停顿")]
        public float magnetAttractDelay = 0.55f;

        [Tooltip("内磁铁带着锁销滑进锁孔所需时间")]
        public float magnetPushDelay = 0.75f;

        [Tooltip("锁销卡死后，抽走外磁铁前的停顿")]
        public float magnetReleaseDelay = 0.45f;

        [Header("物品 id")]
        public string magnetItemId = "magnet";
        [Tooltip("内磁铁掉到窗台后生成的世界物品 id（9 号房为谜面磁铁；玩家自己放的磁铁掉回 magnet）")]
        public string sillMagnetItemId = "mystic_magnet";
        public string ropeItemId = "rope";

        [Header("事件（剧情 / 行为树）")]
        public UnityEvent OnOpened = new UnityEvent();
        public UnityEvent OnClosed = new UnityEvent();
        public UnityEvent OnMagnetAttached = new UnityEvent();
        public UnityEvent OnMagnetRemoved = new UnityEvent();

        Quaternion _closedRotation;
        Quaternion _openRotation;
        Quaternion _latchBaseRotation;
        Coroutine _animRoutine;
        Coroutine _magnetRoutine;
        bool _magnetBusy;
        ItemPickup _sillMagnetPickup;
        bool _innerMagnetPlacedByPlayer;
        string _sillMagnetItemId;

        public bool IsOpen { get; private set; }

        /// <summary>锁销是否拨上（机械自锁；与磁铁是否还在玻璃上无关）。</summary>
        public bool IsLatchLocked { get; private set; }

        /// <summary>两块磁铁是否正隔着玻璃对吸（只在拨锁过程中为 true）。</summary>
        public bool MagnetPairOnGlass { get; private set; }

        /// <summary>内磁铁是否已经从玻璃上脱落、掉在窗台（可能已被玩家捡走）。</summary>
        public bool InnerMagnetOnSill { get; private set; }

        /// <summary>兼容旧字段：磁铁对此刻是否贴在玻璃上（不再等同于窗户锁死）。</summary>
        public bool MagnetDeviceActive => MagnetPairOnGlass;

        /// <summary>磁铁正在执行隔玻璃推锁闩的动画。</summary>
        public bool MagnetBusy => _magnetBusy;

        void Awake()
        {
            _closedRotation = transform.rotation;
            _openRotation = _closedRotation * Quaternion.Euler(0f, openAngle, 0f);
            if (latchVisual != null) _latchBaseRotation = latchVisual.transform.localRotation;
            _sillMagnetItemId = sillMagnetItemId;
            ApplyVisuals();
        }

        void Start()
        {
            // 轮回重置后若内磁铁已掉在窗台，补生成世界物品（初始场景不会走到这里）。
            if (InnerMagnetOnSill) EnsureSillMagnetPickup();
        }

        void Update()
        {
            // 窗台上的内磁铁被玩家用 E 捡走：世界物品已经不在，窗口状态要跟着回落。
            // 否则这扇窗会永远认为「内磁铁还在」，既放不了新的，也无法再次反锁。
            if (_magnetBusy || _sillMagnetPickup != null) return;

            if (InnerMagnetOnSill)
            {
                InnerMagnetOnSill = false;
                hasInnerMagnet = false;
                _innerMagnetPlacedByPlayer = false;
                _sillMagnetItemId = sillMagnetItemId;
                Debug.Log($"[Window {roomNumber}] 窗台内磁铁已被捡走，隔玻璃反锁需要重新放置。");
                return;
            }

            if (hasInnerMagnet && _innerMagnetPlacedByPlayer)
            {
                hasInnerMagnet = false;
                _innerMagnetPlacedByPlayer = false;
                _sillMagnetItemId = sillMagnetItemId;
                Debug.Log($"[Window {roomNumber}] 玩家放下的内磁铁已被取走。");
            }
        }

        // ------------------------------------------------------------------
        // 提示（多选项并存）
        // ------------------------------------------------------------------

        public string GetInteractionPrompt(PlayerController player)
        {
            bool outside = RopeState.PlayerOutsideRoom == roomNumber;

            // 长绳正系在本房：无论屋里屋外，都能在窗边按 T 解开收回。
            string ropeHint = RopeTraverseSystem.IsPlayerRopeAnchoredAt(roomNumber)
                ? "  [T] 收回长绳"
                : "";

            if (outside)
            {
                // —— 屋外（站在檐台上）——
                if (MagnetBusy) return "磁铁正隔着玻璃拨动锁销……";

                if (IsLatchLocked)
                    return "窗户从外面反锁了——锁销卡死，外磁铁也拨不动" + ropeHint;

                if (IsOpen)
                    return "[E] 关窗  [F] 翻进窗内" + ropeHint;

                string outdoor = "[E] 开窗";

                // 只有窗内确实有内磁铁、且还没掉到窗台时，磁铁才有可拨的东西
                if (hasInnerMagnet && !InnerMagnetOnSill)
                {
                    outdoor += HasItem(player, magnetItemId)
                        ? "  [R] 用外磁铁隔着玻璃拨锁"
                        : "  [R] 拨锁（需要外磁铁）";
                }
                else
                {
                    outdoor += "  [R] 拨锁（窗内没有内磁铁）";
                }

                outdoor += "  [F] 翻窗（需先开窗）" + ropeHint;
                return outdoor;
            }

            // —— 屋内 ——
            if (MagnetBusy) return "磁铁还贴在玻璃上……";

            // 内磁铁：屋里可以把自己带的磁铁放到窗台里侧；放好后绕到窗外，
            // 才能用另一块磁铁隔着玻璃拨锁销。
            string magnetHint = "";
            if (hasInnerMagnet && !InnerMagnetOnSill)
            {
                if (_innerMagnetPlacedByPlayer) magnetHint = "\n[Q] 取回窗台磁铁";
            }
            else if (!hasInnerMagnet && HasItem(player, magnetItemId))
            {
                magnetHint = "\n[Q] 把磁铁放到窗台";
            }

            if (IsOpen)
                return "[E] 关窗  [F] 翻窗" + magnetHint + ropeHint;

            if (IsLatchLocked)
                return "[R] 手动拨开窗锁  [F] 翻窗（需先开窗）" + magnetHint + ropeHint;

            return "[E] 开窗  [R] 拨锁  [F] 翻窗（需先开窗）" + magnetHint + ropeHint;
        }

        // ------------------------------------------------------------------
        // E 键：开窗 / 关窗（内外两侧都可以推拉）
        // ------------------------------------------------------------------

        public void Interact(PlayerController player)
        {
            if (MagnetBusy)
            {
                Say(player, "磁铁正隔着玻璃拨动锁销——现在别碰窗。");
                return;
            }

            if (IsLatchLocked)
            {
                Say(player, "窗户被锁住了——锁销卡在锁孔里，从外面拨不动。");
                return;
            }

            if (_animRoutine != null) return;
            _animRoutine = StartCoroutine(Swing(!IsOpen));
        }

        // ------------------------------------------------------------------
        // R 键：拨锁销（屋内）/ 磁铁对反锁（屋外持磁铁）
        // ------------------------------------------------------------------

        public void RKeyInteract(PlayerController player)
        {
            if (MagnetBusy)
            {
                Say(player, "磁铁还贴在玻璃上——等锁销卡死再说。");
                return;
            }

            bool outside = RopeState.PlayerOutsideRoom == roomNumber;

            if (outside)
            {
                // 屋外：磁铁只是隔玻璃拨锁销的工具，不负责维持锁死
                if (IsLatchLocked)
                {
                    Say(player, "窗户已经反锁了——锁销卡在锁孔里，磁铁也拨不动。");
                    return;
                }
                if (IsOpen)
                {
                    Say(player, "先把窗户合上，两块磁铁才能隔着玻璃对上。");
                    return;
                }
                if (!hasInnerMagnet || InnerMagnetOnSill)
                {
                    Say(player, "这扇窗里侧没有能被吸住的内磁铁——除非屋里先有人把一块磁铁放到窗台上。", 4f);
                    return;
                }
                if (!HasItem(player, magnetItemId))
                {
                    Say(player, "锁销在窗户内侧，隔着玻璃够不着——除非你手上还有另一块磁铁。");
                    return;
                }

                // 外磁铁不消耗：用完要抽回来；内磁铁失去吸力后掉在窗台
                if (!_magnetBusy)
                    _magnetRoutine = StartCoroutine(MagnetLockSequence(player));
                return;
            }

            // —— 屋内：只能手动拨锁，和磁铁是否还在窗上无关 ——
            if (IsOpen)
            {
                Say(player, "窗户开着，锁销对不上锁孔——先把窗关上。");
                return;
            }

            if (IsLatchLocked)
            {
                IsLatchLocked = false;
                ApplyVisuals();
                Say(player, "你伸手拨开窗锁销——锁舌缩回，窗户可以推开了。");
                Debug.Log($"[Window {roomNumber}] 玩家在室内手动拨开窗锁销。");
            }
            else
            {
                IsLatchLocked = true;
                ApplyVisuals();
                Say(player, "你拨上窗锁销，从里面反锁了窗户。");
                Debug.Log($"[Window {roomNumber}] 玩家在室内手动拨上窗锁销。");
            }
        }

        // ------------------------------------------------------------------
        // Q 键：屋内把磁铁放到窗台 / 取回（让任意窗户都能被磁铁反锁）
        // ------------------------------------------------------------------

        public void QKeyInteract(PlayerController player)
        {
            if (MagnetBusy)
            {
                Say(player, "磁铁正隔着玻璃拨动锁销——先别动窗台。");
                return;
            }

            if (RopeState.PlayerOutsideRoom == roomNumber)
            {
                Say(player, "磁铁得从屋里放到窗台里侧——站在窗外够不着。");
                return;
            }

            if (InnerMagnetOnSill)
            {
                Say(player, "内磁铁已经掉在窗台上了，直接捡起来就行。");
                return;
            }

            if (hasInnerMagnet)
            {
                if (!_innerMagnetPlacedByPlayer)
                {
                    Say(player, "窗台碎石堆里那块磁铁卡得很死，隔着窗拿不回来。");
                    return;
                }

                if (player == null || player.inventory == null) return;
                if (player.inventory.IsFull)
                {
                    Say(player, "背包已经满了，磁铁先留在窗台上。");
                    return;
                }
                if (!player.inventory.Add(magnetItemId))
                {
                    Say(player, "背包已经满了，磁铁先留在窗台上。");
                    return;
                }

                if (_sillMagnetPickup != null)
                {
                    Destroy(_sillMagnetPickup.gameObject);
                    _sillMagnetPickup = null;
                }

                hasInnerMagnet = false;
                _innerMagnetPlacedByPlayer = false;
                _sillMagnetItemId = sillMagnetItemId;
                Say(player, "你把窗台上的磁铁收回口袋。");
                Debug.Log($"[Window {roomNumber}] 玩家取回自己放在窗台的内磁铁。");
                return;
            }

            if (player == null || player.inventory == null || !player.inventory.Has(magnetItemId))
            {
                Say(player, "得先有一块磁铁，才能放到窗台里侧。");
                return;
            }
            if (!player.inventory.Remove(magnetItemId))
            {
                Say(player, "背包里没有可用的磁铁。");
                return;
            }

            hasInnerMagnet = true;
            _innerMagnetPlacedByPlayer = true;
            _sillMagnetItemId = magnetItemId;
            SpawnPlacedInnerMagnet();
            Say(player, "你把磁铁放到窗台里侧的锁销旁。现在绕到窗外，" +
                        "用另一块磁铁隔着玻璃就能把它和锁销一起推上去。", 5f);
            Debug.Log($"[Window {roomNumber}] 玩家把磁铁放到窗台，可作为内磁铁被隔玻璃牵引。");
        }

        // ------------------------------------------------------------------
        // F 键：翻窗（室内 ↔ 檐台，真实翻越动画由 RopeTraverseSystem 执行）
        // ------------------------------------------------------------------

        public void FKeyInteract(PlayerController player)
        {
            if (RopeTraverseSystem.Instance == null) return;

            if (MagnetBusy)
            {
                Say(player, "磁铁正隔着玻璃拨动锁销——现在推窗会前功尽弃。");
                return;
            }

            if (IsLatchLocked)
            {
                Say(player, "锁销卡在锁孔里——先拨开锁，才能推窗。");
                return;
            }

            if (!IsOpen)
            {
                Say(player, RopeState.PlayerOutsideRoom == roomNumber
                    ? "先把窗户推开一条缝。"
                    : "先把窗户打开，才能翻出去。");
                return;
            }

            if (RopeState.PlayerOutsideRoom == roomNumber)
                RopeTraverseSystem.Instance.ClimbIn(player, this);
            else
                RopeTraverseSystem.Instance.ClimbOut(player, this);
        }

        // ------------------------------------------------------------------
        // T 键：收回系在本房窗外的长绳（室内窗边 / 檐台窗边均可）
        // ------------------------------------------------------------------

        public void TKeyInteract(PlayerController player)
        {
            if (RopeTraverseSystem.Instance == null) return;
            RopeTraverseSystem.Instance.RecallPlayerRope(player, roomNumber);
        }

        // ------------------------------------------------------------------
        // 剧情接口（1号 AI 流程 / 场景脚本）
        // ------------------------------------------------------------------

        /// <summary>
        /// 1 号 AI 杀完 9 号后的脚本动作：外磁铁隔着玻璃吸起内磁铁，
        /// 把锁销推进锁孔，随后抽走外磁铁——内磁铁掉回窗台，锁闩保持卡死。
        /// 用协程返回，调用方 yield return，动画与剧本节奏一致。
        /// </summary>
        public IEnumerator ForceLockWithMagnetSequence()
        {
            // 剧情保证 9 号窗内存在这块内磁铁；若场景装配漏了，这里补上，避免 1 号流程中断。
            if (!hasInnerMagnet)
            {
                hasInnerMagnet = true;
                Debug.LogWarning($"[Window {roomNumber}] 剧情反锁缺少内磁铁配置，已按 1 号流程补上。");
            }

            if (IsLatchLocked && InnerMagnetOnSill) yield break; // 已经完成过，不重复生成

            if (InnerMagnetOnSill) InnerMagnetOnSill = false;
            yield return MagnetLockSequence(null);
        }

        /// <summary>
        /// 隔玻璃推锁闩的完整物理过程：
        /// 外磁铁吸起内磁铁 → 带着锁销滑进锁孔 → 锁销机械卡死 → 抽走外磁铁 →
        /// 内磁铁失去吸力掉回窗台 → 锁闩仍锁死。外磁铁不消耗。
        /// </summary>
        IEnumerator MagnetLockSequence(PlayerController player)
        {
            _magnetBusy = true;

            if (IsOpen)
            {
                Debug.LogWarning($"[Window {roomNumber}] 窗户开着，无法隔玻璃吸住内磁铁。");
                _magnetBusy = false;
                _magnetRoutine = null;
                yield break;
            }

            // 窗台上如果有一块实体内磁铁（玩家放的），先让它“被吸起来”，
            // 后面由磁铁对视觉接管；锁销卡死后会重新掉回窗台。
            if (_sillMagnetPickup != null)
            {
                Destroy(_sillMagnetPickup.gameObject);
                _sillMagnetPickup = null;
            }

            if (magnetVisual != null)
                magnetVisual.transform.localPosition = magnetOnGlassLocal;

            MagnetPairOnGlass = true;
            ApplyVisuals();
            Say(player, "你把外磁铁贴上玻璃。窗台碎石堆里那块内磁铁「啪」地被吸起来，" +
                        "隔着玻璃和它咬在一起。", 3.5f);
            Debug.Log($"[Window {roomNumber}] 外磁铁吸起窗台内磁铁（隔玻璃）。");
            yield return new WaitForSeconds(magnetAttractDelay);

            Say(player, "你缓缓移动外磁铁，内侧那块便贴着玻璃，把锁销一点点往上推……", 3f);

            // 让磁铁对从窗台一路滑到锁销旁，玩家能直接看到「隔空推锁闩」。
            if (magnetVisual != null)
            {
                Vector3 from = magnetOnGlassLocal;
                Vector3 to = magnetLockedLocal;
                float t = 0f;
                while (t < 1f)
                {
                    t += Time.deltaTime / Mathf.Max(0.05f, magnetPushDelay);
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                    magnetVisual.transform.localPosition = Vector3.Lerp(from, to, k);
                    yield return null;
                }
                magnetVisual.transform.localPosition = to;
            }
            else
            {
                yield return new WaitForSeconds(magnetPushDelay);
            }

            IsLatchLocked = true;
            ApplyVisuals();
            OnMagnetAttached.Invoke();
            Say(player, "咔哒。锁销滑进锁孔，机械卡死了。", 2.2f);
            Debug.Log($"[Window {roomNumber}] 锁销被隔玻璃推入锁孔，机械自锁成立。");
            yield return new WaitForSeconds(magnetReleaseDelay);

            MagnetPairOnGlass = false;
            InnerMagnetOnSill = true;
            ApplyVisuals();
            SpawnSillMagnet();
            OnMagnetRemoved.Invoke();
            Say(player, "你抽走外磁铁。内侧那块失去吸力，当啷一声掉回窗台的碎石堆里。" +
                        "锁销依旧卡死——窗户从外面反锁了。", 4.5f);
            Debug.Log($"[Window {roomNumber}] 外磁铁收回、内磁铁掉在窗台；锁闩保持锁死，不依赖磁铁。");

            _magnetBusy = false;
            _magnetRoutine = null;
        }

        /// <summary>屋内玩家把磁铁放到窗台里侧：生成一块可看见、可捡回的世界物品。</summary>
        void SpawnPlacedInnerMagnet()
        {
            if (_sillMagnetPickup != null) return;

            Vector3 world = transform.TransformPoint(innerMagnetSillLocal);
            _sillMagnetPickup = ItemPickup.SpawnWorldItem(magnetItemId, world, transform.parent, roomNumber);
            _sillMagnetPickup.transform.rotation = transform.rotation;

            // 放好的磁铁要稳稳待在窗台上，不能自己滚下去。
            Rigidbody body = _sillMagnetPickup.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
        }

        /// <summary>内磁铁掉到窗台后生成可拾取的世界物品（9 号房是 mystic_magnet，玩家放的是 magnet）。</summary>
        void SpawnSillMagnet()
        {
            if (string.IsNullOrEmpty(_sillMagnetItemId)) return;
            if (_sillMagnetPickup != null) return;

            Vector3 world = transform.TransformPoint(innerMagnetSillLocal);
            Transform parent = transform.parent;
            _sillMagnetPickup = ItemPickup.SpawnWorldItem(_sillMagnetItemId, world, parent, roomNumber);
            Debug.Log($"[Window {roomNumber}] 内磁铁掉在窗台：{world}（物品 {_sillMagnetItemId}）。");
        }

        void EnsureSillMagnetPickup()
        {
            if (!InnerMagnetOnSill) return;
            if (_sillMagnetPickup != null) return;
            SpawnSillMagnet();
        }

        /// <summary>3.5 轮回重置：恢复窗户的初始开合 / 锁销 / 磁铁状态。</summary>
        public void ResetWindowState(bool open, bool latchLocked, bool magnetPairOnGlass,
            bool innerMagnetOnSill, bool innerMagnetPresent)
        {
            if (_animRoutine != null)
            {
                StopCoroutine(_animRoutine);
                _animRoutine = null;
            }

            if (_magnetRoutine != null)
            {
                StopCoroutine(_magnetRoutine);
                _magnetRoutine = null;
            }
            _magnetBusy = false;

            // 世界道具重置会销毁本轮所有 ItemPickup；这里清掉可能残留的引用，
            // 再按目标状态决定是否重新生成窗台上的内磁铁。
            if (_sillMagnetPickup != null)
            {
                Destroy(_sillMagnetPickup.gameObject);
                _sillMagnetPickup = null;
            }

            IsOpen = open;
            IsLatchLocked = latchLocked;
            MagnetPairOnGlass = magnetPairOnGlass;
            InnerMagnetOnSill = innerMagnetOnSill;

            // 玩家自己放到窗台的内磁铁不跨轮回保留；9 号窗的初始内磁铁由场景装配决定。
            hasInnerMagnet = innerMagnetPresent;
            _innerMagnetPlacedByPlayer = false;
            _sillMagnetItemId = sillMagnetItemId;

            transform.rotation = open ? _openRotation : _closedRotation;
            ApplyVisuals();

            if (InnerMagnetOnSill) EnsureSillMagnetPickup();
        }

        /// <summary>立即开 / 合（打断动画；1号 AI 流程与场景脚本用）。</summary>
        public void SetOpenInstant(bool open)
        {
            if (_animRoutine != null)
            {
                StopCoroutine(_animRoutine);
                _animRoutine = null;
            }

            transform.rotation = open ? _openRotation : _closedRotation;
            if (IsOpen == open) return;

            IsOpen = open;
            if (open) OnOpened.Invoke();
            else OnClosed.Invoke();
        }

        // ------------------------------------------------------------------
        // 视觉
        // ------------------------------------------------------------------

        void ApplyVisuals()
        {
            // 磁铁对只在「正隔着玻璃拨锁」的瞬间出现；锁销卡死后磁铁就离开玻璃了。
            if (magnetVisual != null)
            {
                magnetVisual.SetActive(MagnetPairOnGlass);
                if (!MagnetPairOnGlass)
                    magnetVisual.transform.localPosition = magnetOnGlassLocal;
            }

            if (latchVisual != null)
            {
                // 锁销拨上时转 90°（横过来卡住窗扇）
                latchVisual.transform.localRotation = IsLatchLocked
                    ? _latchBaseRotation * Quaternion.Euler(0f, 0f, 90f)
                    : _latchBaseRotation;
            }
        }

        static bool HasItem(PlayerController player, string itemId)
        {
            return player != null
                && player.inventory != null
                && player.inventory.Has(itemId);
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

            if (open) OnOpened.Invoke();
            else OnClosed.Invoke();
        }

        static void Say(PlayerController player, string message, float duration = 2.5f)
        {
            if (player != null) player.ShowMessage(message, duration);
            else Debug.Log("[Window] " + message);
        }
    }
}
