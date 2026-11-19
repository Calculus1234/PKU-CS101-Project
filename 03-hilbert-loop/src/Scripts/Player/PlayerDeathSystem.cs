using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家死亡统一入口（模块三 3.5 / 3.5 紧急修正）：
    /// 投票抹杀 / 锤子反杀 / 坠楼都走 KillPlayer，死亡当轮先进入幽灵观战；
    /// 天亮时若玩家仍处于死亡状态，不再复活、不再顺移，直接触发结局 B。
    /// 摘下面具被目击：目击者转为敌意，并按“规则”在数秒后处决玩家（同样进入幽灵观战）。
    /// </summary>
    public class PlayerDeathSystem : MonoBehaviour
    {
        public static PlayerDeathSystem Instance { get; private set; }

        [Header("引用（场景构建器装配；缺省时运行时自找）")]
        public PlayerController player;
        public GhostSpectator ghost;
        public Camera playerCamera;

        [Header("摘面具后的规则处决")]
        [Tooltip("被目击摘面具后，到目击者动手之间的秒数（留给玩家反应 / 逃跑 / 先下手）")]
        public float exposedExecutionDelay = 8f;

        bool _dead;
        float _sacrificeConfirmUntil;

        GameObject _corpse;
        GameObject _hudRoot;

        float _exposedExecuteAt = -1f;
        int _lastCountdownSecond = -1;
        readonly HashSet<int> _exposedWitnesses = new HashSet<int>();

        public bool IsDead => _dead;

        /// <summary>主动摘除面具 / 斗篷的二次确认窗口（秒）。</summary>
        public float sacrificeConfirmWindow = 3f;

        void Awake()
        {
            Instance = this;
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (ghost == null) ghost = FindObjectOfType<GhostSpectator>();
            if (playerCamera == null)
            {
                if (player != null) playerCamera = player.GetComponentInChildren<Camera>(true);
                if (playerCamera == null) playerCamera = Camera.main;
            }

            GameObject hud = GameObject.Find("HUD");
            if (hud != null) _hudRoot = hud;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (_dead || _exposedExecuteAt < 0f) return;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding)
            {
                CancelExposureExecution();
                return;
            }

            PruneExposedWitnesses();
            if (_exposedWitnesses.Count == 0)
            {
                CancelExposureExecution();
                if (player != null)
                    player.ShowMessage("看见你脸的人已经不在了。没有人能再按“规则”处置你。", 4f);
                return;
            }

            if (Time.time >= _exposedExecuteAt)
            {
                int witness = FirstExposedWitness();
                string cause = witness > 0
                    ? $"你摘下面具后，被{witness}号按规则处决"
                    : "你摘下面具后，被按规则处决";
                _exposedExecuteAt = -1f;
                KillPlayer(cause, PlayerDeathType.Sacrifice);
                return;
            }

            int secondsLeft = Mathf.CeilToInt(_exposedExecuteAt - Time.time);
            if (secondsLeft != _lastCountdownSecond)
            {
                _lastCountdownSecond = secondsLeft;
                int witness = FirstExposedWitness();
                if (player != null)
                    player.ShowMessage(witness > 0
                        ? $"{witness}号看见了你的脸，正朝你走来。“规则”不允许露脸。（{secondsLeft}）"
                        : $"有人看见了你的脸。“规则”不允许露脸。（{secondsLeft}）", 1.3f);
            }
        }

        /// <summary>
        /// NPC 第一次看清玩家摘下面具后的脸：
        /// 记住这张脸、锁定玩家、拿起武器，并启动“规则处决”倒计时。
        /// 之后每次被新的 NPC 看见，都会把新的目击者加进处决名单。
        /// </summary>
        public void NotifyExposedFaceSeen(NPCController witness)
        {
            if (_dead || witness == null || witness.isDead || witness.blackboard == null) return;
            if (!CycleState.playerDisguiseRemoved) return;

            bool firstTime = _exposedWitnesses.Add(witness.Number);
            NPCBlackboard board = witness.blackboard;
            board.knowsPlayerFace = true;
            board.knownClues.Add("player_face");

            if (!firstTime && board.wantsPlayerDead) return;

            board.stress = Mathf.Min(100f, board.stress + 20f);
            board.suspicion = 100f;
            board.AddSuspectFront(CycleState.playerNumber);
            board.AddTrust(CycleState.playerNumber, -40f);
            board.wantsPlayerDead = true;
            board.hostileSince = Time.time;

            if (witness.behaviourTree != null)
            {
                witness.behaviourTree.SetPaused(false);
                witness.behaviourTree.Restart();
            }

            if (!firstTime) return;

            if (_exposedExecuteAt < 0f)
            {
                _exposedExecuteAt = Time.time + Mathf.Max(1f, exposedExecutionDelay);
                _lastCountdownSecond = -1;
            }

            if (player != null)
                player.ShowMessage($"{witness.Number}号看清了你的脸。整层楼的“规则”开始运转——" +
                                   $"你最多还有 {exposedExecutionDelay:0} 秒。", 5f);
            Debug.Log($"[PlayerDeath] {witness.Number}号目击玩家露脸，" +
                      $"{exposedExecutionDelay:0} 秒后按规则处决。");
        }

        void PruneExposedWitnesses()
        {
            if (_exposedWitnesses.Count == 0) return;

            var dead = new List<int>();
            foreach (int number in _exposedWitnesses)
            {
                NPCController npc = FindNpc(number);
                if (npc == null || npc.isDead) dead.Add(number);
            }
            for (int i = 0; i < dead.Count; i++) _exposedWitnesses.Remove(dead[i]);
        }

        int FirstExposedWitness()
        {
            foreach (int number in _exposedWitnesses)
            {
                NPCController npc = FindNpc(number);
                if (npc != null && !npc.isDead) return number;
            }
            return 0;
        }

        static NPCController FindNpc(int number)
        {
            var all = NPCController.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].Number == number) return all[i];
            }
            return null;
        }

        void CancelExposureExecution()
        {
            _exposedExecuteAt = -1f;
            _lastCountdownSecond = -1;
            _exposedWitnesses.Clear();
        }

        /// <summary>杀死玩家：记录死亡方式、制造全场情绪冲击，并进入幽灵观战。</summary>
        public void KillPlayer(string cause)
        {
            KillPlayer(cause, PlayerDeathType.Killed);
        }

        /// <summary>杀死玩家（模块四：结局 D 需要区分被票死 / 被杀 / 主动献祭）。</summary>
        public void KillPlayer(string cause, PlayerDeathType type)
        {
            if (_dead) return;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return;
            _dead = true;
            CancelExposureExecution();

            CycleState.NotifyPlayerDeath(cause, type);
            ApplyDeathShock(type);

            if (player != null)
            {
                player.ShowMessage($"你死了——{cause}。", 3.5f);
                player.SetCursorLocked(false);

                var cc = player.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;

                if (player.interaction != null) player.interaction.enabled = false;
                var tracked = player.GetComponent<MonitorTracked>();
                if (tracked != null) tracked.enabled = false;

                player.inputLocked = true;
                player.movementLocked = true;
                player.enabled = false;
            }

            // 玩家原本没有可见身体（第一人称），死亡时补一具同号码的布娃娃尸体，
            // 否则幽灵视角回头看，地上什么都没有。
            SpawnPlayerCorpse(cause);
            HidePlayerHudForDeath();

            // 先按死亡方式与当前恐慌值装配结局 D 判定，再进入幽灵视角
            // （幽灵 HUD 会列出每条判定条件，缺哪一环就写哪一环）。
            if (EndingDSystem.Instance != null)
                EndingDSystem.Instance.Arm(type, cause);

            if (ghost == null) ghost = FindObjectOfType<GhostSpectator>();
            if (ghost != null && playerCamera != null)
            {
                ghost.Enter(playerCamera.transform);
                ghost.SetDeathCause(cause);
            }
            else
                Debug.LogWarning("[PlayerDeath] 缺少 GhostSpectator / 玩家相机，无法进入幽灵观战。");

            Debug.LogWarning($"[PlayerDeath] 玩家死亡（{cause} / {type}），进入幽灵观战。");
        }

        /// <summary>
        /// 死亡时把玩家 HUD（准星 / 背包 / 交互提示）整体收起来，
        /// 并把会议界面一并隐藏：幽灵 HUD 自己占屏幕，二者叠在一起就是文字重叠。
        /// </summary>
        void HidePlayerHudForDeath()
        {
            if (_hudRoot != null) _hudRoot.SetActive(false);

            MeetingUI meetingUI = FindObjectOfType<MeetingUI>(true);
            if (meetingUI != null)
            {
                meetingUI.HideOptions();
                meetingUI.HideEvidence();
                meetingUI.SetVisible(false);
            }
        }

        /// <summary>
        /// 用 NPC_Entity 预制体在玩家倒下的位置生成一具布娃娃尸体。
        /// 只借用它的模型 / 骨骼 / 面具，NPC 行为组件全部拆掉，避免多出一个 8 号 NPC。
        /// </summary>
        void SpawnPlayerCorpse(string cause)
        {
            if (_corpse != null) return;

            GameObject prefab = Resources.Load<GameObject>("NPC/NPC_Entity");
            if (prefab == null)
            {
                Debug.LogWarning("[PlayerDeath] 找不到 NPC_Entity，无法生成玩家尸体。");
                return;
            }

            int number = CycleState.playerNumber > 0 ? CycleState.playerNumber : 8;
            Vector3 feet = player != null ? player.transform.position : transform.position;
            Quaternion rotation = player != null ? player.transform.rotation : Quaternion.identity;

            GameObject corpse = Instantiate(prefab);
            corpse.name = $"PlayerCorpse_{number}";
            corpse.transform.SetPositionAndRotation(feet + Vector3.up * 0.9f, rotation);

            // 拆掉 NPC 逻辑：控制器 / 寻路 / 视觉感知 / 听觉 / 监控红点。
            NPCController npc = corpse.GetComponent<NPCController>();
            if (npc != null)
            {
                npc.isDead = true;
                npc.enabled = false;
                Destroy(npc);
            }
            DisableComponent<NPCNavigator>(corpse);
            DisableComponent<NPCVision>(corpse);
            DisableComponent<NPCHearing>(corpse);
            DisableComponent<MonitorTracked>(corpse);

            CharacterController cc = corpse.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            NPCVisualController visual = corpse.GetComponent<NPCVisualController>();
            if (visual != null)
            {
                NPCData data = Resources.Load<NPCData>($"NPC/NPC_{number}");
                if (data != null) visual.Configure(data);
                visual.ResetVisuals();
            }

            NPCDeathHandler handler = corpse.GetComponent<NPCDeathHandler>();
            if (handler != null)
            {
                handler.EnableRagdoll(rotation * Vector3.forward);
                AttachCorpseInteractions(handler, number, cause);
            }

            _corpse = corpse;
            Debug.Log($"[PlayerDeath] 已在玩家倒下的位置生成 {number} 号尸体。");
        }

        static void DisableComponent<T>(GameObject target) where T : Behaviour
        {
            T component = target.GetComponent<T>();
            if (component != null) component.enabled = false;
        }

        static void AttachCorpseInteractions(NPCDeathHandler handler, int number, string cause)
        {
            Transform hips = handler.hips != null ? handler.hips : handler.transform;
            HidingSpot spot = hips.GetComponent<HidingSpot>();
            if (spot == null) spot = hips.gameObject.AddComponent<HidingSpot>();
            spot.displayName = $"{number}号（你）的衣袋";
            spot.allowedLocation = HideLocation.OnBody;

            Transform chest = handler.chest != null ? handler.chest : hips;
            CorpseInspectable inspect = chest.GetComponent<CorpseInspectable>();
            if (inspect == null) inspect = chest.gameObject.AddComponent<CorpseInspectable>();
            inspect.number = number;
            inspect.isNinthVictim = false;
            inspect.subjectName = $"{number}号的尸体";
            inspect.cause = cause;
        }

        /// <summary>
        /// 摘下面具与斗篷（连按两次 M）。
        /// 这不是死亡：玩家只是把脸暴露在所有人面前。
        /// 真正的破局需要「所有人都摘」，单个人摘下面具只会让自己成为被注视的异类；
        /// 但摘下面具之后，玩家才有资格去摘下 9号 的面具并对比两张脸。
        /// </summary>
        public bool TrySacrifice()
        {
            // 输入框打开时 M 是文字，不是摘面具指令。
            if (NpcFreeTextInputUI.IsOpen) return false;
            if (_dead || player == null || player.inventory == null) return false;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return false;

            bool hasMask = player.inventory.Has("mask");
            bool hasCloak = player.inventory.Has("cloak");
            if (!hasMask && !hasCloak)
            {
                if (CycleState.playerDisguiseRemoved)
                    player.ShowMessage("你的脸已经露在外面了。所有人都记住了它。");
                else
                    player.ShowMessage("你身上已经没有可摘下的面具与斗篷。");
                return false;
            }

            if (Time.unscaledTime > _sacrificeConfirmUntil)
            {
                _sacrificeConfirmUntil = Time.unscaledTime + sacrificeConfirmWindow;
                player.ShowMessage("你摸到了面具的边缘。再按一次 M，就会把它和斗篷一起摘下——" +
                                   "所有人都会看见你的脸。", 4.5f);
                return false;
            }

            _sacrificeConfirmUntil = 0f;
            if (hasMask) player.inventory.Remove("mask");
            if (hasCloak) player.inventory.Remove("cloak");

            CycleState.NotifyPlayerDisguiseRemoved();

            // 只有真正看见这张脸的人才会记住它——而“看见玩家露脸”本身，
            // 就足以让目击者按规则动手。距离近但背对着你的人不算目击者。
            int witnesses = 0;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;

                NPCVision vision = npc.GetComponent<NPCVision>();
                bool sees = vision != null
                    ? vision.CanSeePoint(player.gameObject, player.EyePosition,
                        vision.viewAngle, vision.viewDistance)
                    : Vector3.Distance(npc.transform.position, player.transform.position) <= 8f;
                if (!sees) continue;

                NotifyExposedFaceSeen(npc);
                witnesses++;
            }

            player.ShowMessage(witnesses > 0
                ? $"面具与斗篷滑落在地。{witnesses}个人看清了你的脸——“摘下面具”这件事，" +
                  "已经不再是你一个人的秘密。"
                : "面具与斗篷滑落在地。此刻没有人看见你的脸。但下一次有人看向你，就会知道。", 5f);
            Debug.Log($"[PlayerDeath] 玩家摘下面具与斗篷：{witnesses} 名目击者。" +
                      (witnesses > 0 ? $" {exposedExecutionDelay:0} 秒后按规则处决。" : " 暂未被目击。"));
            return true;
        }

        /// <summary>
        /// 死亡冲击：主动献祭 > 被票死 > 被杀。
        /// 恐惧通过压力 / 恐慌状态影响周围 NPC 的行为，不作为顿悟概率。
        /// </summary>
        void ApplyDeathShock(PlayerDeathType type)
        {
            float stress;
            float suspicion;
            float panicSeconds;

            switch (type)
            {
                case PlayerDeathType.Sacrifice:
                    stress = 45f;
                    suspicion = 25f;
                    panicSeconds = 20f;
                    break;
                case PlayerDeathType.VotedOut:
                    stress = 25f;
                    suspicion = 10f;
                    panicSeconds = 8f;
                    break;
                default:
                    stress = 15f;
                    suspicion = 5f;
                    panicSeconds = 6f;
                    break;
            }

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;

                npc.blackboard.stress = Mathf.Min(100f, npc.blackboard.stress + stress);
                npc.blackboard.suspicion = Mathf.Min(100f, npc.blackboard.suspicion + suspicion);
                npc.blackboard.panicUntil = Mathf.Max(npc.blackboard.panicUntil,
                    Time.time + panicSeconds);
                if (player != null)
                    npc.blackboard.panicSourcePosition = player.transform.position;
            }

            Debug.Log($"[PlayerDeath] 死亡冲击（{type}）：全员 stress +{stress}，" +
                      $"恐慌 {panicSeconds:0}s。");
        }

        /// <summary>天亮轮回：把身体、控制器、摄像机全部恢复，等待 CycleController 重新放置。</summary>
        public void ReviveForNewLoop()
        {
            if (!_dead) return;
            _dead = false;
            CancelExposureExecution();
            _sacrificeConfirmUntil = 0f;

            if (_corpse != null)
            {
                Destroy(_corpse);
                _corpse = null;
            }

            if (ghost != null) ghost.Exit();

            if (_hudRoot != null) _hudRoot.SetActive(true);

            if (player != null)
            {
                player.enabled = true;
                player.inputLocked = false;
                player.movementLocked = false;

                if (player.interaction != null) player.interaction.enabled = true;
                var tracked = player.GetComponent<MonitorTracked>();
                if (tracked != null) tracked.enabled = true;

                var cc = player.GetComponent<CharacterController>();
                if (cc != null)
                {
                    cc.enabled = false; // 位置由 CycleController 放置后再启用
                }
            }

            Debug.Log("[PlayerDeath] 新轮回：玩家身体与摄像机已恢复。");
        }

        /// <summary>
        /// 结局 B：关闭幽灵视角 / 输入，但保持死亡状态，不生成下一轮身体。
        /// 相机回到玩家原位后会被结局全屏黑幕覆盖。
        /// </summary>
        public void CloseGhostForEnding()
        {
            if (ghost != null) ghost.Exit();
            Debug.Log("[PlayerDeath] 结局 B：幽灵观战关闭，玩家保持死亡状态。");
        }

        /// <summary>CycleController 放好位置后调用，重新启用 CharacterController。</summary>
        public void EnableBodyAfterPlacement()
        {
            if (player == null) return;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;
        }
    }
}
