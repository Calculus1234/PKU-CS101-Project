using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 天亮轮回（模块三 3.5 / 重构）：
    /// 投票结算后进入 Reincarnation，本系统负责——
    /// 1. 灯灭 + 钟声，黑场后直接进入下一轮，不向玩家解释任何“记忆 / 复制体”设定；
    /// 2. 号码顺移：所有活到天亮的人号码 +1；超过 9 号的人离开这栋房子；
    ///    新的复制体补入 1 号。9 号被 1 号杀死后，人口仍保持满员；
    /// 3. 玩家活着就跟着顺移；玩家 8 号第一次活到天亮时，机器故障把他识别为
    ///    新 1 号并保留记忆（整局只触发一次），循环继续；机器故障用掉之后
    ///    再次顺移到 9 号，才会在天亮时成为那具被发现的尸体，进入结局 A；
    /// 4. 玩家成为 1 号时，本轮必定存在第九席：即使其他人都死了，
    ///    也会补入一个沉睡的 9 号，场上只剩 1 号和 9 号；
    /// 5. 原地重建 NPC、重置道具 / 门窗 / 绳 / 藏匿物 / 9 号尸体，
    ///    玩家按新号码回到床上醒来，重新进入搜证阶段。
    /// 不重载场景，因此静态状态、UI、相机与 A* 网格全部保留。
    /// </summary>
    public class CycleResetSystem : MonoBehaviour
    {
        public static CycleResetSystem Instance { get; private set; }

        [Header("引用（场景构建器装配；缺省时运行时自找）")]
        public PlayerController player;
        public NPCFactory npcFactory;
        public CycleController cycleController;

        [Header("黑场节奏（秒）")]
        public float fadeOutSeconds = 1.8f;
        public float blackHoldSeconds = 3.2f;
        public float fadeInSeconds = 2.5f;

        struct ItemSnapshot
        {
            public string itemId;
            public string requiredCarriedItemId;
            public Vector3 position;
            public Quaternion rotation;
            public Transform parent;
        }

        struct WindowSnapshot
        {
            public WindowController window;
            public bool open;
            public bool latchLocked;
            public bool magnetPairOnGlass;
            public bool innerMagnetOnSill;
            public bool hasInnerMagnet;
        }

        struct DoorSnapshot
        {
            public DoorController door;
            public bool open;
            public bool broken;
            public bool locked;
        }

        struct HidingSnapshot
        {
            public HidingSpot spot;
            public string[] items;
        }

        readonly List<ItemSnapshot> _itemSnapshots = new List<ItemSnapshot>();
        readonly List<WindowSnapshot> _windowSnapshots = new List<WindowSnapshot>();
        readonly List<DoorSnapshot> _doorSnapshots = new List<DoorSnapshot>();
        readonly List<HidingSnapshot> _hidingSnapshots = new List<HidingSnapshot>();

        Canvas _overlay;
        Image _black;
        TextMeshProUGUI _message;
        AudioSource _audio;
        bool _resetting;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            if (GameMode.MenuActive) return;
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();
            if (cycleController == null) cycleController = FindObjectOfType<CycleController>();

            BuildUI();
            CaptureWorldSnapshot();

            if (GameManager.Instance != null)
                GameManager.Instance.OnPhaseChanged += HandlePhaseChanged;
        }

        void OnDestroy()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPhaseChanged -= HandlePhaseChanged;
            if (Instance == this) Instance = null;
        }

        void HandlePhaseChanged(GamePhase phase)
        {
            if (phase != GamePhase.Reincarnation) return;
            if (_resetting) return;
            StartCoroutine(DawnRoutine());
        }

        // ------------------------------------------------------------------
        // 天亮主流程
        // ------------------------------------------------------------------

        IEnumerator DawnRoutine()
        {
            _resetting = true;
            CycleState.NotifyReincarnationStarted();

            if (player != null)
            {
                player.inputLocked = true;
                player.movementLocked = true;
                if (player.interaction != null) player.interaction.enabled = false;
                player.SetCursorLocked(false);
            }

            PlayBell();

            // 3.5 紧急修正：玩家死亡后幽灵观战只允许看完本轮。
            // 模块四（重构）：天亮前做最后一次确定性顿悟判定——
            // 证据链不成立就进入结局 B，成立才由 EndingDSystem 拉起结局 D。
            if (!CycleState.playerAlive)
            {
                // 结局走实景演出：这里不再先把画面压成黑屏，
                // 只让灯光与提示停留一瞬，随后交给 EndingSystem / EndingDirector。
                SetMessage(Localization.IsEnglish
                    ? "The corridor lights fade...\nBut this time, no one will wake again."
                    : "走廊的灯暗了下去……\n但这一次，没有人会再醒来。");
                yield return new WaitForSeconds(1.2f);

                if (EndingDSystem.Instance != null && EndingDSystem.Instance.IsArmed)
                {
                    EndingDSystem.Instance.ResolveAtDawn();
                }
                else if (EndingSystem.Instance != null)
                {
                    EndingSystem.Instance.TriggerEndingB(Localization.IsEnglish
                        ? "You died before the loop could end."
                        : "你死在了循环结束之前。");
                }
                else
                {
                    if (GameManager.Instance != null) GameManager.Instance.HaltLoop();
                    if (PlayerDeathSystem.Instance != null)
                        PlayerDeathSystem.Instance.CloseGhostForEnding();
                    SetMessage(Localization.IsEnglish
                        ? "The corridor lights fade...\nBut this time, no one will wake again.\n\nThe end."
                        : "走廊的灯暗了下去……\n但这一次，没有人会再醒来。\n\n游戏结束。");
                    Debug.LogError("[CycleReset] 场景缺少 EndingSystem，无法播放 Yarn 结局节点。");
                }

                _resetting = false;
                yield break;
            }

            // 机器故障（整局只触发一次）：玩家以 8 号活到天亮时，
            // 系统把他识别为新 1 号，并保留这一轮的记忆。循环不会在这里结束。
            bool machineFault = CycleState.playerAlive
                                && !CycleState.machineFaultUsed
                                && CycleState.playerNumber == 8;

            // 机器故障用掉之后，8→9 的顺移仍然成立：
            // 今夜，轮到你成为那具被发现的尸体。
            if (!machineFault && CycleState.playerNumber >= 8)
            {
                SetMessage(Localization.IsEnglish
                    ? "The lights go out.\nThe numbers shift one last time.\nYou lie back and close your eyes."
                    : "灯光熄灭……\n所有人的号码开始顺移。\n你回到床上，闭上眼睛。");
                yield return new WaitForSeconds(1.2f);

                if (EndingSystem.Instance != null)
                {
                    EndingSystem.Instance.TriggerEndingA(
                        Localization.IsEnglish
                            ? "You survived to the end of the loop. The shift turned 8 into 9 — " +
                              "tonight, you are the body that will be found."
                            : "你活到了循环的尽头。号码顺移把「8」变成了「9」——" +
                              "今夜，轮到你成为那具被发现的尸体。");
                }
                else
                {
                    if (GameManager.Instance != null) GameManager.Instance.HaltLoop();
                    SetMessage(Localization.IsEnglish
                        ? "The instant the lights die...\na shadow climbs through the window.\n\nThe end."
                        : "灯灭的瞬间……\n一道黑影翻进房间。\n\n游戏结束。");
                    Debug.LogError("[CycleReset] 场景缺少 EndingSystem，无法播放 Yarn 结局节点。");
                }

                _resetting = false;
                yield break;
            }

            if (machineFault)
            {
                // 8 号被识别成新 1 号：记忆保留，循环继续。
                // 下一轮场上至少会有 1 号和 9 号——1 号是玩家，9 号是既定的受害者。
                SetMessage(Localization.IsEnglish
                    ? "The lights go out...\nThe system reads you as the new No. 1.\nYou remember everything."
                    : "灯光熄灭……\n系统把你识别成了新的 1 号。\n这一轮的经过，你没有忘。");
                yield return new WaitForSeconds(1.6f);
            }

            SetMessage(Localization.IsEnglish
                ? "The lights dim...\nThe last footsteps in the corridor fall silent."
                : "灯暗了下去……\n走廊里最后一点脚步声也停了。");
            yield return Fade(0f, 1f, fadeOutSeconds);

            SetMessage(Localization.IsEnglish
                ? "Dawn.\nA new round begins."
                : "天亮了。\n新的一轮开始了。");
            yield return new WaitForSeconds(blackHoldSeconds);

            int newPlayerNumber;
            bool memoryRetained;
            int knownMax;
            List<int> roster = CalculateNextRoster(machineFault, out newPlayerNumber,
                out memoryRetained, out knownMax);

            // 幽灵相机先回到身体，随后统一重新放置
            if (PlayerDeathSystem.Instance != null) PlayerDeathSystem.Instance.ReviveForNewLoop();

            // 新一轮有没有 9 号尸体：本轮最大号码到 9、且玩家不是 1 号时，
            // 9 号会在玩家醒来前被 1 号杀死，床上的尸体占位体必须显形。
            bool ninthCorpsePresent = knownMax >= 9 && newPlayerNumber != 1;
            ResetWorldObjects(ninthCorpsePresent);

            int population = roster.Count + (CycleState.playerAlive && newPlayerNumber > 0 ? 1 : 0);
            // 玩家成为 1 号时，沉睡的 9 号也占一个人口（场上至少是 1 和 9）。
            if (newPlayerNumber == 1 && knownMax >= CycleState.MaxLoopNumber) population++;
            population = Mathf.Clamp(population, 1, CycleState.MaxLoopNumber);

            CycleState.PrepareNewLoop(newPlayerNumber, true, memoryRetained, machineFault, population, knownMax);

            // 新一轮时间线重新开始：先确定玩家号码，再让 NPCFactory 计算相对醒来延迟。
            LoopTimeline.BeginCycle();

            if (npcFactory != null) npcFactory.ResetForNewLoop(roster);
            if (cycleController != null)
                cycleController.PlacePlayerForLoop(newPlayerNumber);

            SetMessage(Localization.IsEnglish
                ? $"Loop {CycleState.loopIndex + 1}:\nYou are No. {newPlayerNumber}."
                : $"第 {CycleState.loopIndex + 1} 周目：\n你是 {newPlayerNumber} 号。");

            yield return new WaitForSeconds(0.6f);

            if (GameManager.Instance != null) GameManager.Instance.BeginNextLoop();

            yield return Fade(1f, 0f, fadeInSeconds);

            HideUI();

            if (player != null)
            {
                player.inputLocked = false;
                player.movementLocked = false;
                if (player.interaction != null) player.interaction.enabled = true;
                player.SetCursorLocked(true);
            }

            _resetting = false;
            Debug.Log($"[CycleReset] 天亮完成：第 {CycleState.loopIndex} 轮，玩家 {newPlayerNumber} 号，" +
                      $"本轮最大号码 {knownMax}，人口 {population}。");

            // 天亮重置完成后写档：周目、玩家号码、存活名单、机器故障状态。
            SaveManager.Save("dawn");
        }

        // ------------------------------------------------------------------
        // 号码顺移 / 名册
        // ------------------------------------------------------------------

        List<int> CalculateNextRoster(bool machineFault, out int newPlayerNumber,
            out bool memoryRetained, out int knownMax)
        {
            var survivors = new List<int>();
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;
                survivors.Add(npc.Number);
            }
            survivors.Sort();

            int oldPlayerNumber = CycleState.playerNumber;
            bool playerParticipates = CycleState.playerAlive && oldPlayerNumber > 0;

            // 常规顺移：所有人 +1。
            // 机器故障：8 号被识别为新 1 号，记忆保留，循环继续。
            newPlayerNumber = playerParticipates
                ? (machineFault ? 1 : Mathf.Min(oldPlayerNumber + 1, CycleState.MaxLoopNumber))
                : 0;
            memoryRetained = machineFault;

            var roster = new List<int>();
            for (int i = 0; i < survivors.Count; i++)
            {
                int shifted = survivors[i] + 1;

                // 这栋房子只有 9 个房间：9 号活到天亮就顺移到 10 号，
                // 10 号没有房间，也不会被生成——它就这样离开循环。
                if (shifted > CycleState.MaxLoopNumber) continue;

                AddRosterNumber(roster, shifted, newPlayerNumber);
            }

            // 新的复制体补入 1 号位（玩家不占 1 号时由 NPC 扮演）。
            // 玩家成为 1 号时由玩家本人占据，不再补入新 1 号。
            if (newPlayerNumber != 1) AddRosterNumber(roster, 1, newPlayerNumber);

            roster.Sort();
            knownMax = Mathf.Max(newPlayerNumber,
                roster.Count > 0 ? roster[roster.Count - 1] : 0);

            // 玩家成为 1 号时，本轮必定存在第九席：
            // 即使 1~8 号都死了，也会补入一个沉睡的 9 号作为既定受害者。
            if (newPlayerNumber == 1) knownMax = CycleState.MaxLoopNumber;

            knownMax = Mathf.Clamp(knownMax, 1, CycleState.MaxLoopNumber);

            Debug.Log($"[CycleReset] 旧存活：{(survivors.Count > 0 ? string.Join("、", survivors) : "无")}；" +
                      $"玩家 {oldPlayerNumber}号 存活={CycleState.playerAlive} → 新 {newPlayerNumber} 号；" +
                      $"机器故障={machineFault}；" +
                      $"新名册：{(roster.Count > 0 ? string.Join("、", roster) : "无")}；" +
                      $"本轮最大号码 {knownMax}。");
            return roster;
        }

        static void AddRosterNumber(List<int> roster, int number, int newPlayerNumber)
        {
            if (number == newPlayerNumber) return;
            if (!roster.Contains(number)) roster.Add(number);
        }

        // ------------------------------------------------------------------
        // 世界重置
        // ------------------------------------------------------------------

        void CaptureWorldSnapshot()
        {
            _itemSnapshots.Clear();
            ItemPickup[] items = FindObjectsOfType<ItemPickup>(true);
            for (int i = 0; i < items.Length; i++)
            {
                ItemPickup item = items[i];
                if (item == null) continue;
                _itemSnapshots.Add(new ItemSnapshot
                {
                    itemId = item.itemId,
                    requiredCarriedItemId = item.requiredCarriedItemId,
                    position = item.transform.position,
                    rotation = item.transform.rotation,
                    parent = item.transform.parent,
                });
            }

            _windowSnapshots.Clear();
            WindowController[] windows = FindObjectsOfType<WindowController>(true);
            for (int i = 0; i < windows.Length; i++)
            {
                WindowController window = windows[i];
                if (window == null) continue;
                _windowSnapshots.Add(new WindowSnapshot
                {
                    window = window,
                    open = window.IsOpen,
                    latchLocked = window.IsLatchLocked,
                    magnetPairOnGlass = window.MagnetPairOnGlass,
                    innerMagnetOnSill = window.InnerMagnetOnSill,
                    hasInnerMagnet = window.hasInnerMagnet,
                });
            }

            _doorSnapshots.Clear();
            DoorController[] doors = FindObjectsOfType<DoorController>(true);
            for (int i = 0; i < doors.Length; i++)
            {
                DoorController door = doors[i];
                if (door == null) continue;
                _doorSnapshots.Add(new DoorSnapshot
                {
                    door = door,
                    open = door.IsOpen,
                    broken = door.IsBroken,
                    locked = door.lockedFromInside,
                });
            }

            _hidingSnapshots.Clear();
            HidingSpot[] spots = FindObjectsOfType<HidingSpot>(true);
            for (int i = 0; i < spots.Length; i++)
            {
                HidingSpot spot = spots[i];
                if (spot == null) continue;
                IReadOnlyList<string> hidden = spot.HiddenItemIds;
                var copy = new string[hidden.Count];
                for (int j = 0; j < hidden.Count; j++) copy[j] = hidden[j];
                _hidingSnapshots.Add(new HidingSnapshot { spot = spot, items = copy });
            }

            Debug.Log($"[CycleReset] 世界快照：道具 {_itemSnapshots.Count} 件、窗 {_windowSnapshots.Count} 扇、" +
                      $"门 {_doorSnapshots.Count} 扇、藏匿点 {_hidingSnapshots.Count} 处。");
        }

        void ResetWorldObjects(bool ninthBodyPresent)
        {
            // 道具：销毁本轮所有（含玩家丢下的），按初始快照重新生成
            ItemPickup[] currentItems = FindObjectsOfType<ItemPickup>(true);
            for (int i = 0; i < currentItems.Length; i++)
            {
                if (currentItems[i] != null) Destroy(currentItems[i].gameObject);
            }

            for (int i = 0; i < _itemSnapshots.Count; i++)
            {
                ItemSnapshot snapshot = _itemSnapshots[i];
                if (string.IsNullOrEmpty(snapshot.itemId)) continue;

                ItemPickup pickup = ItemPickup.SpawnWorldItem(snapshot.itemId, snapshot.position, snapshot.parent);
                pickup.transform.rotation = snapshot.rotation;
                pickup.requiredCarriedItemId = snapshot.requiredCarriedItemId;
            }

            // 窗户 / 门 / 9 号尸体 / 绳 / 藏匿物
            for (int i = 0; i < _windowSnapshots.Count; i++)
            {
                WindowSnapshot snapshot = _windowSnapshots[i];
                if (snapshot.window != null)
                    snapshot.window.ResetWindowState(snapshot.open, snapshot.latchLocked,
                        snapshot.magnetPairOnGlass, snapshot.innerMagnetOnSill,
                        snapshot.hasInnerMagnet);
            }

            for (int i = 0; i < _doorSnapshots.Count; i++)
            {
                DoorSnapshot snapshot = _doorSnapshots[i];
                if (snapshot.door != null)
                    snapshot.door.ResetDoorState(snapshot.open, snapshot.broken, snapshot.locked);
            }

            NinthVictim[] victims = FindObjectsOfType<NinthVictim>(true);
            for (int i = 0; i < victims.Length; i++)
            {
                if (victims[i] != null) victims[i].ResetVictim(ninthBodyPresent);
            }

            if (RopeTraverseSystem.Instance != null) RopeTraverseSystem.Instance.ResetRopeState();

            for (int i = 0; i < _hidingSnapshots.Count; i++)
            {
                HidingSnapshot snapshot = _hidingSnapshots[i];
                if (snapshot.spot != null) snapshot.spot.ResetHiddenItems(snapshot.items);
            }

            Debug.Log($"[CycleReset] 道具 / 门窗 / 绳 / 9号尸体（本轮在场={ninthBodyPresent}）/ 藏匿物已恢复初始状态。");
        }

        // ------------------------------------------------------------------
        // 黑场 UI / 音效
        // ------------------------------------------------------------------

        void BuildUI()
        {
            var canvasGo = new GameObject("DawnOverlay_Canvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            _overlay = canvasGo.GetComponent<Canvas>();
            _overlay.renderMode = RenderMode.ScreenSpaceOverlay;
            _overlay.sortingOrder = 300;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var blackGo = new GameObject("Black", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            blackGo.transform.SetParent(canvasGo.transform, false);
            var blackRect = blackGo.GetComponent<RectTransform>();
            blackRect.anchorMin = Vector2.zero;
            blackRect.anchorMax = Vector2.one;
            blackRect.offsetMin = Vector2.zero;
            blackRect.offsetMax = Vector2.zero;
            _black = blackGo.GetComponent<Image>();
            _black.color = new Color(0f, 0f, 0f, 0f);
            _black.raycastTarget = false;

            var textGo = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(canvasGo.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.sizeDelta = new Vector2(1300f, 220f);
            _message = textGo.GetComponent<TextMeshProUGUI>();
            _message.fontSize = 34f;
            _message.alignment = TextAlignmentOptions.Center;
            _message.color = new Color(0.88f, 0.82f, 0.76f, 1f);
            _message.raycastTarget = false;
            _message.text = string.Empty;
            ChineseFontUtility.Apply(_message);

            var audioGo = new GameObject("DawnAudio");
            audioGo.transform.SetParent(transform, false);
            _audio = audioGo.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
            _audio.volume = 0.55f;

            _overlay.gameObject.SetActive(false);
        }

        void SetMessage(string text)
        {
            if (_message != null) _message.text = text;
        }

        void HideUI()
        {
            if (_overlay != null) _overlay.gameObject.SetActive(false);
            SetMessage(string.Empty);
        }

        IEnumerator Fade(float from, float to, float duration)
        {
            if (_overlay != null) _overlay.gameObject.SetActive(true);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / Mathf.Max(0.01f, duration));
                if (_black != null) _black.color = new Color(0f, 0f, 0f, Mathf.Lerp(from, to, k));
                yield return null;
            }
            if (_black != null) _black.color = new Color(0f, 0f, 0f, to);
        }

        void PlayBell()
        {
            // 优先使用 Resources/SFX/bell_toll；缺失时回退到程序化钟声。
            if (SFXManager.Instance != null && SFXManager.Instance.HasClip("bell_toll"))
            {
                SFXManager.Instance.PlayBellToll(0.9f);
                return;
            }
            if (_audio == null) return;
            AudioClip clip = BuildDawnClip();
            _audio.PlayOneShot(clip);
        }

        static AudioClip BuildDawnClip()
        {
            const int sampleRate = 22050;
            const float duration = 6f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            var data = new float[sampleCount];

            float[] strikes = { 0f, 1.15f, 2.3f, 3.45f };
            for (int s = 0; s < strikes.Length; s++)
            {
                int start = Mathf.RoundToInt(strikes[s] * sampleRate);
                for (int i = start; i < sampleCount; i++)
                {
                    float t = (i - start) / (float)sampleRate;
                    if (t > 2.2f) break;
                    float env = Mathf.Exp(-2.1f * t);
                    float wave = Mathf.Sin(2f * Mathf.PI * 196f * t)
                                 + 0.55f * Mathf.Sin(2f * Mathf.PI * 392f * t)
                                 + 0.28f * Mathf.Sin(2f * Mathf.PI * 588f * t);
                    data[i] += wave * env * 0.16f;
                }
            }

            // 低频压抑底噪
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float drone = Mathf.Sin(2f * Mathf.PI * 55f * t) * 0.035f * (1f - t / duration);
                data[i] = Mathf.Clamp(data[i] + drone, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("DawnBell", sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
