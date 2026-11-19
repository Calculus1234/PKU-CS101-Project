using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 视觉检测（模块三 3.3 接入行为树）。
    /// 视线判定 = 距离 + 90° 视锥 + 物理射线遮挡（墙壁 / 家具 / 关闭的门）。
    /// 每 0.2 秒扫描一次，把看得见的目标写入 NPCBlackboard.visibleTargets；
    /// 并识别四类玩家行为：
    ///   看到藏匿道具     → suspicion +10
    ///   看到持武器靠近   → suspicion +20 / stress +10
    ///   看到正在破门     → suspicion +25
    ///   看到 9 号的尸体   → stress +30（只触发一次）
    /// </summary>
    public class NPCVision : MonoBehaviour
    {
        [Header("视锥")]
        [Tooltip("水平视野角（度）")]
        public float viewAngle = 90f;

        [Tooltip("视距（米）")]
        public float viewDistance = 8f;

        [Tooltip("扫描间隔（秒）")]
        public float scanInterval = 0.2f;

        [Tooltip("眼睛相对本体根的高度偏移（本体根约在胶囊中心）")]
        public float eyeHeight = 0.45f;

        [Header("玩家行为识别")]
        [Tooltip("看到玩家持武器时的警觉距离（米）")]
        public float weaponAlertDistance = 6f;

        [Tooltip("藏匿动作的可见时间窗（秒）")]
        public float hidingMemorySeconds = 0.8f;

        [Tooltip("破门动作的可见时间窗（秒）")]
        public float doorBreakMemorySeconds = 1f;

        [Tooltip("同类视觉刺激的最小间隔（秒）")]
        public float alertCooldown = 6f;

        [Tooltip("玩家进入该距离（米）时立即转头查看（潜行接近也会被注意到）")]
        public float closeObserveRange = 3f;

        /// <summary>遮挡检测层：忽略监控层（玩家 / NPC 自身），保留墙体与家具。</summary>
        int ObstacleMask => ~(1 << MonitorSystem.MonitorIgnoreLayer);

        NPCController _npc;
        PlayerController _player;

        float _nextScan;
        float _lastWeaponSeen = -999f;
        float _lastHidingSeen = -999f;
        float _lastDoorBreakSeen = -999f;
        bool _playerWasVisible;
        bool _playerWasClose;

        /// <summary>
        /// 能否看见目标（兼容 3.1 接口：目标点取本体上方 0.8 米）。
        /// </summary>
        public bool CanSeeTarget(GameObject target, float viewAngle = 90f, float viewDistance = 8f)
        {
            if (target == null) return false;
            return CanSeePoint(target, target.transform.position + Vector3.up * 0.8f, viewAngle, viewDistance);
        }

        /// <summary>能否从眼睛看见世界中的某个点（距离 + 视锥 + 遮挡三重检查）。</summary>
        public bool CanSeePoint(GameObject target, Vector3 worldPoint,
            float viewAngle = 90f, float viewDistance = 8f)
        {
            if (target == null) return false;

            Vector3 eye = transform.position + Vector3.up * eyeHeight;
            Vector3 to = worldPoint - eye;
            float distance = to.magnitude;

            if (distance > viewDistance) return false;
            if (distance < 0.01f) return true;

            if (Vector3.Angle(transform.forward, to) > viewAngle * 0.5f) return false;

            // 遮挡检测：中间有墙 / 家具 / 关闭的门则看不见。
            // 命中目标自身（例如 9 号尸体未在监控层）不算遮挡。
            if (Physics.Raycast(eye, to / distance, out RaycastHit hit, distance,
                    ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                return hit.collider.transform.IsChildOf(target.transform);
            }

            return true;
        }

        void Awake()
        {
            _npc = GetComponent<NPCController>();
        }

        void Update()
        {
            if (_npc == null || _npc.isDead || !_npc.IsAwake) return;
            if (Time.time < _nextScan) return;

            _nextScan = Time.time + Mathf.Max(0.05f, scanInterval);
            Scan();
        }

        void Scan()
        {
            NPCBlackboard board = _npc.blackboard;
            if (board == null) return;

            board.visibleTargets.Clear();

            float suspicionBefore = board.suspicion;
            bool seesPlayer = ScanPlayer(board);
            ScanOtherNpcs(board);
            ScanCorpses(board);

            if (seesPlayer)
            {
                CheckPlayerBehaviors(board);

                if (!_playerWasVisible)
                    NPCEventBus.RaiseNPCSeeTarget(_npc.Number, _player.gameObject);

                // 近距离发现：玩家刚进入 3 米内，立即让行为树重选「转头盯住」
                float playerDistance = Vector3.Distance(_npc.transform.position, _player.transform.position);
                bool closeNow = playerDistance <= closeObserveRange;
                bool becameClose = closeNow && !_playerWasClose;
                _playerWasClose = closeNow;

                // 怀疑度刚越过盯视阈值（或首次看见时已很高）→ 立即让行为树重选分支
                bool crossedObserveThreshold = suspicionBefore <= 60f && board.suspicion > 60f;
                bool needTurn = (!_playerWasVisible && board.suspicion > 60f)
                    || crossedObserveThreshold
                    || (!_playerWasVisible && closeNow)
                    || becameClose;
                if (needTurn && Time.time >= board.observeCooldownUntil)
                    _npc.RequestPerceptionResponse();
            }
            else
            {
                _playerWasClose = false;
            }

            _playerWasVisible = seesPlayer;
        }

        bool ScanPlayer(NPCBlackboard board)
        {
            // 3.5：玩家出局后只剩幽灵视角，身体不再被 NPC 当作活人目标，
            // 避免幽灵观战期间 AI 对尸体持续盯视 / 起疑而偏离原有行为。
            if (!CycleState.playerAlive) return false;

            if (_player == null)
                _player = FindObjectOfType<PlayerController>();
            if (_player == null) return false;

            if (!CanSeePoint(_player.gameObject, _player.EyePosition, viewAngle, viewDistance))
                return false;

            board.visibleTargets.Add(_player.gameObject);
            board.lastKnownPosition = _player.transform.position;

            // 玩家已经摘下面具：任何第一次看清这张脸的人，都会按“规则”动手。
            if (CycleState.playerDisguiseRemoved)
                PlayerDeathSystem.Instance?.NotifyExposedFaceSeen(_npc);

            return true;
        }

        void ScanOtherNpcs(NPCBlackboard board)
        {
            var all = NPCController.All;
            for (int i = 0; i < all.Count; i++)
            {
                NPCController other = all[i];
                if (other == null || other == _npc || other.isDead) continue;

                Vector3 point = other.transform.position + Vector3.up * 0.3f;
                if (CanSeePoint(other.gameObject, point, viewAngle, viewDistance))
                    board.visibleTargets.Add(other.gameObject);
            }
        }

        /// <summary>看见任何一具尸体：写入个人线索、触发恐慌，并让全局“尸体已被发现”。</summary>
        void ScanCorpses(NPCBlackboard board)
        {
            var corpses = CorpseInspectable.All;
            for (int i = 0; i < corpses.Count; i++)
            {
                CorpseInspectable corpse = corpses[i];
                if (corpse == null) continue;

                Vector3 point = corpse.transform.position + Vector3.up * 0.35f;
                if (!CanSeePoint(corpse.gameObject, point, viewAngle, viewDistance)) continue;

                // 面具已经被摘下、而自己又见过玩家摘下面具后的脸——
                // 光是看见这张脸，就足以让 NPC 自己完成「我们是同一张脸」的推理。
                corpse.ObserveRevealedFace(_npc);

                string clue = corpse.isNinthVictim ? "saw_ninth_body" : $"saw_body_{corpse.number}";
                if (board.knownClues.Contains(clue)) continue;

                board.knownClues.Add(clue);
                CycleState.NotifyBodyDiscovered(corpse.number);

                if (corpse.isNinthVictim)
                {
                    board.sawNinthBody = true;
                    board.stress = Mathf.Min(100f, board.stress + 30f);
                }
                else
                {
                    board.stress = Mathf.Min(100f, board.stress + 22f);
                    board.panicUntil = Mathf.Max(board.panicUntil, Time.time + 6f);
                    board.panicSourcePosition = corpse.transform.position;
                    _npc.RequestPerceptionResponse();
                }

                Debug.Log($"[NPC {_npc.Number}] 发现了 {corpse.number} 号的尸体" +
                          $"（{(corpse.isNinthVictim ? "9号" : "本轮死者")}），Stress → {board.stress:0}。");
            }
        }

        /// <summary>四类玩家行为的视觉判定（同一类刺激有冷却，避免每 0.2 秒重复加分）。</summary>
        void CheckPlayerBehaviors(NPCBlackboard board)
        {
            float now = Time.time;

            // 1) 看到玩家在藏匿道具
            if (now - PlayerActionSignals.LastHidingTime <= hidingMemorySeconds
                && now - _lastHidingSeen >= alertCooldown)
            {
                _lastHidingSeen = now;
                AddSuspicion(board, 10f, "玩家正在把东西藏起来");
            }

            // 2) 看到玩家正持有武器靠近
            ItemDefinition weapon = _player.inventory != null ? _player.inventory.GetLastWeapon() : null;
            if (weapon != null
                && Vector3.Distance(transform.position, _player.transform.position) <= weaponAlertDistance
                && now - _lastWeaponSeen >= alertCooldown)
            {
                _lastWeaponSeen = now;
                board.stress = Mathf.Min(100f, board.stress + 10f);
                AddSuspicion(board, 20f, $"玩家手持{weapon.displayName}靠近");
            }

            // 3) 看到玩家正在破门
            if (now - PlayerActionSignals.LastDoorBreakTime <= doorBreakMemorySeconds
                && now - _lastDoorBreakSeen >= alertCooldown)
            {
                _lastDoorBreakSeen = now;
                AddSuspicion(board, 25f, "玩家正在破坏房门");
            }
        }

        void AddSuspicion(NPCBlackboard board, float amount, string reason)
        {
            board.suspicion = Mathf.Clamp(board.suspicion + amount, 0f, 100f);

            int playerNumber = CycleState.playerNumber > 0 ? CycleState.playerNumber : 8;
            if (board.suspectNumbers != null && !board.suspectNumbers.Contains(playerNumber))
                board.suspectNumbers.Add(playerNumber);

            NPCEventBus.RaiseSuspicionChanged(_npc.Number, playerNumber, board.suspicion);
            Debug.Log($"[NPC {_npc.Number}] 看见{reason}：Suspicion +{amount:0} → {board.suspicion:0}");
        }
    }
}
