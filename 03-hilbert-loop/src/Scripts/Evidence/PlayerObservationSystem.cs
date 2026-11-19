using System;
using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 玩家观察系统（模块四补强）：
    /// 玩家不是全知全能——只有亲眼看进视锥、且中间没有墙的东西才算“证据”。
    /// - NPC 离开 / 返回房间、翻窗、钉钉、杀人；
    /// - 监控屏、外墙墙钉、9号面具等世界证据；
    /// - 尸体与面具被摘下的瞬间。
    /// 观察到的内容自动写入 EvidenceLedger，会议 / 私聊的证据面板可直接出示。
    /// </summary>
    public class PlayerObservationSystem : MonoBehaviour
    {
        public static PlayerObservationSystem Instance { get; private set; }

        [Header("观察范围")]
        public float scanInterval = 0.5f;
        public float worldEvidenceRange = 8f;
        public float corpseRange = 8f;
        public float npcRange = 10f;
        public float windowRange = 14f;
        [Range(30f, 160f)] public float viewAngle = 80f;

        PlayerController _player;
        float _nextScan;
        float _nextWorldCache;
        WorldEvidence[] _worldCache;
        readonly Dictionary<int, int> _exitCounts = new Dictionary<int, int>();

        void Awake()
        {
            Instance = this;
            _player = GetComponent<PlayerController>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void OnEnable()
        {
            NPCEventBus.OnNPCExitRoom += HandleNpcExitRoom;
            NPCEventBus.OnNPCEnterRoom += HandleNpcEnterRoom;
            NPCEventBus.OnCycleStateChanged += HandleCycleStateChanged;
        }

        void OnDisable()
        {
            NPCEventBus.OnNPCExitRoom -= HandleNpcExitRoom;
            NPCEventBus.OnNPCEnterRoom -= HandleNpcEnterRoom;
            NPCEventBus.OnCycleStateChanged -= HandleCycleStateChanged;
        }

        void HandleCycleStateChanged(CyclePhase phase)
        {
            if (phase != CyclePhase.Normal && phase != CyclePhase.Reset) return;
            _exitCounts.Clear();
            _worldCache = null;
            _nextWorldCache = 0f;
            ItemProvenance.Clear();
        }

        void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + Mathf.Max(0.2f, scanInterval);
            if (!CanObserveNow()) return;

            ScanWorldEvidence();
            ScanCorpses();
        }

        // ------------------------------------------------------------------
        // NPC 行为（由 NPCAssassin / 事件总线调用）
        // ------------------------------------------------------------------

        void HandleNpcExitRoom(int number, string roomName)
        {
            if (!CanObserveNow()) return;

            NPCController npc = FindNpc(number);
            if (npc == null) return;

            int count;
            _exitCounts.TryGetValue(number, out count);
            count++;
            _exitCounts[number] = count;

            if (!CanPlayerSee(npc.transform, npc.transform.position + Vector3.up * 1.0f,
                    viewAngle, npcRange)) return;

            string time = NowTimeText();
            AddEvidence($"saw_exit_{number}_{count}",
                $"看见{number}号在{time}离开房间",
                $"你亲眼看见{number}号推开房门走到走廊上。", 10, $"{number}号房门口");
        }

        void HandleNpcEnterRoom(int number, string roomName)
        {
            if (!CanObserveNow()) return;
            if (!_exitCounts.ContainsKey(number)) return;

            NPCController npc = FindNpc(number);
            if (npc == null) return;
            if (!CanPlayerSee(npc.transform, npc.transform.position + Vector3.up * 1.0f,
                    viewAngle, npcRange)) return;

            int count = _exitCounts[number];
            AddEvidence($"saw_return_{number}_{count}",
                $"看见{number}号回到房间",
                $"你亲眼看见{number}号从走廊回到自己的房间，关上了门。", 6, $"{number}号房门口");
        }

        /// <summary>1号翻窗：只有玩家真的能看见窗外的人时才会记录。</summary>
        public void ReportWindow(NPCController npc, bool exiting)
        {
            if (!CanObserveNow() || npc == null) return;
            if (!CanPlayerSee(npc.transform, npc.transform.position + Vector3.up * 0.9f,
                    viewAngle, windowRange)) return;

            string time = NowTimeText();
            if (exiting)
            {
                AddEvidence($"saw_window_out_{npc.Number}",
                    $"看见{npc.Number}号翻出窗外",
                    $"你亲眼看见{npc.Number}号在{time}推开窗，翻到窗外的檐台上。", 35,
                    $"{npc.Number}号房窗外");
            }
            else
            {
                AddEvidence($"saw_window_in_{npc.Number}",
                    $"看见{npc.Number}号从窗外翻回房间",
                    $"你亲眼看见{npc.Number}号从窗外的檐台翻回房间，随后把窗合上。", 30,
                    $"{npc.Number}号房窗外");
            }
        }

        /// <summary>看见某人在别人窗外钉支点钉。</summary>
        public void ReportNail(NPCController npc, int roomNumber, Vector3 nailWorldPoint)
        {
            if (!CanObserveNow() || npc == null) return;

            // 墙钉在窗外。玩家必须真的站到檐台上（或同在窗外），
            // 不能隔着后墙 / 从走廊里“隔空看见”。
            if (!CanSeeExteriorPoint(nailWorldPoint, 6f)) return;
            if (!CanPlayerSeePoint(nailWorldPoint, 6f)) return;

            AddEvidence($"saw_nail_{npc.Number}_{roomNumber}",
                $"看见{npc.Number}号在{roomNumber}号房窗外钉钉子",
                $"你亲眼看见{npc.Number}号把一枚长钉钉进{roomNumber}号房窗外的墙缝，" +
                "又把绳子绕在钉子上当支点。", 32, $"{roomNumber}号房窗外");
        }

        /// <summary>
        /// 外墙证据只在玩家本人也位于窗外时成立：
        /// 站在房间 / 走廊里，墙体挡着，看到的只是窗玻璃的反光。
        /// </summary>
        bool CanSeeExteriorPoint(Vector3 worldPoint, float range)
        {
            // 玩家必须真的站在檐台踏板上，而不是大厅 / 走道里某个被旧版宽判定误算的点。
            RoomArea ledgeRoom = RoomArea.FindLedgeRoom(_player.transform.position);
            if (ledgeRoom == null) return false;

            RoomArea wallRoom = RoomArea.FindExteriorRoom(worldPoint);
            if (wallRoom == null) return false;

            // 只认同一扇窗外的墙钉：隔着墙角、从天井或隔壁窗看都不算。
            if (wallRoom != ledgeRoom) return false;

            return Vector3.Distance(_player.transform.position, worldPoint) <= range;
        }

        /// <summary>看见某人动手杀人。</summary>
        public void ReportKill(NPCController killer, int victimNumber)
        {
            if (!CanObserveNow() || killer == null) return;
            if (!CanPlayerSee(killer.transform, killer.transform.position + Vector3.up * 0.9f,
                    viewAngle, windowRange)) return;

            CycleState.NotifyBodyDiscovered(victimNumber);
            AddEvidence($"saw_kill_{killer.Number}_{victimNumber}",
                $"看见{killer.Number}号杀死{victimNumber}号",
                $"你亲眼看见{killer.Number}号把匕首刺进{victimNumber}号的身体。" +
                "这不是推测，是你看见的。", 80, $"{victimNumber}号房");
        }

        // ------------------------------------------------------------------
        // 世界证据 / 尸体
        // ------------------------------------------------------------------

        void ScanWorldEvidence()
        {
            if (_worldCache == null || Time.time >= _nextWorldCache)
            {
                _worldCache = FindObjectsOfType<WorldEvidence>();
                _nextWorldCache = Time.time + 3f;
            }

            for (int i = 0; i < _worldCache.Length; i++)
            {
                WorldEvidence evidence = _worldCache[i];
                if (evidence == null) continue;
                if (EvidenceLedger.Has(evidence.evidenceId)) continue;

                bool isWallNail = evidence.evidenceId.StartsWith("wall_nails");
                float range = isWallNail ? 4.5f : worldEvidenceRange;

                if (Vector3.Distance(_player.transform.position, evidence.transform.position)
                    > range) continue;

                // 墙钉在窗外：玩家没上檐台就根本看不到，避免在走廊 / 房间里隔墙触发。
                if (isWallNail
                    && !CanSeeExteriorPoint(evidence.transform.position, range)) continue;

                if (!CanPlayerSeePoint(evidence.transform.position + Vector3.up * 0.15f,
                        range)) continue;

                // 看见监控 / 墙钉 / 窗台痕迹，等同于检查过：走它自己的记录逻辑。
                evidence.Interact(_player);
            }
        }

        void ScanCorpses()
        {
            var corpses = CorpseInspectable.All;
            for (int i = 0; i < corpses.Count; i++)
            {
                CorpseInspectable corpse = corpses[i];
                if (corpse == null) continue;
                if (Vector3.Distance(_player.transform.position, corpse.transform.position)
                    > corpseRange) continue;
                if (!CanPlayerSee(corpse.transform,
                        corpse.transform.position + Vector3.up * 0.35f,
                        viewAngle, corpseRange)) continue;

                int number = corpse.number;
                string bodyId = number == 9 ? "ninth_body" : $"body_{number}";
                string sawId = number == 9 ? "saw_body_9" : $"saw_body_{number}";
                if (!EvidenceLedger.Has(bodyId) && !EvidenceLedger.Has(sawId))
                {
                    AddEvidence(sawId,
                        number == 9 ? "看见9号的尸体" : $"看见{number}号的尸体",
                        number == 9
                            ? "9号躺在床上，面具和斗篷还穿在身上。门从里面闩着，窗户也锁着。"
                            : $"{number}号倒在房间里，面具还扣在脸上。",
                        25, $"{number}号房");
                    CycleState.NotifyBodyDiscovered(number);
                }

                // 9号的面具被摘下：如果玩家也摘过自己的面具，这一刻就能完成对比；
                // 否则只能算一条“看见了一张脸”的观察线索。
                if (corpse.MaskRemoved && number == 9)
                {
                    if (CycleState.playerDisguiseRemoved)
                    {
                        if (!CycleState.faceComparisonDone && !EvidenceLedger.Has("face_compare"))
                        {
                            AddEvidence("face_compare", "两张一模一样的脸",
                                "9号的面具被摘下。那张脸和你摘下面具后的脸一模一样——" +
                                "同样的眉骨、同样的下颌线。你们是同一张脸。", 65, "9号房");
                            CycleState.NotifyFaceComparison();
                        }
                    }
                    else if (!EvidenceLedger.Has("face_compare")
                             && !EvidenceLedger.Has("saw_ninth_face_off"))
                    {
                        AddEvidence("saw_ninth_face_off", "9号面具下的脸",
                            "9号的面具已经被摘下。那张脸被灯光照得很清楚——" +
                            "可你还没见过自己的脸，无法确认那是不是和你一样。", 18, "9号房");
                    }

                    CycleState.NotifyPlayerSawNinthFace();
                }
            }
        }

        // ------------------------------------------------------------------
        // 可见性
        // ------------------------------------------------------------------

        bool CanObserveNow()
        {
            if (_player == null || !CycleState.playerAlive) return false;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return false;
            if (GameManager.Instance != null
                && GameManager.Instance.CurrentPhase != GamePhase.Investigation) return false;
            return true;
        }

        public bool CanPlayerSee(Transform target, Vector3 worldPoint, float angle, float range)
        {
            if (!PrepareRay(worldPoint, angle, range, out Vector3 dir, out float distance))
                return false;

            RaycastHit[] hits = Physics.RaycastAll(_player.EyePosition, dir, distance,
                ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                Transform hit = hits[i].collider.transform;
                if (hit.IsChildOf(_player.transform)) continue;
                if (target != null && (hit == target || hit.IsChildOf(target))) return true;
                if (IsSeeThrough(hit)) continue;
                return false;
            }

            // 射线什么都没打中：中间没有墙。
            return true;
        }

        public bool CanPlayerSeePoint(Vector3 worldPoint, float range)
        {
            if (!PrepareRay(worldPoint, viewAngle, range, out Vector3 dir, out float distance))
                return false;

            RaycastHit[] hits = Physics.RaycastAll(_player.EyePosition, dir, distance,
                ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                Transform hit = hits[i].collider.transform;
                if (hit.IsChildOf(_player.transform)) continue;
                if (IsSeeThrough(hit)) continue;

                // 命中点已经接近目标点：挡住视线的是目标本身，不是墙。
                return hits[i].distance >= distance - 0.45f;
            }

            return true;
        }

        bool PrepareRay(Vector3 worldPoint, float angle, float range,
            out Vector3 direction, out float distance)
        {
            direction = Vector3.zero;
            distance = 0f;
            if (_player == null) return false;

            Vector3 eye = _player.EyePosition;
            Vector3 to = worldPoint - eye;
            distance = to.magnitude;
            if (distance > range || distance < 0.01f) return false;
            if (Vector3.Angle(_player.EyeRotation * Vector3.forward, to) > angle * 0.5f)
                return false;

            direction = to / distance;
            return true;
        }

        static bool IsSeeThrough(Transform hit)
        {
            if (hit == null) return false;
            if (hit.GetComponentInParent<WindowController>() != null) return true;

            string name = hit.name.ToLowerInvariant();
            return name.Contains("glass") || name.Contains("window")
                   || name.Contains("screen") || name.Contains("monitor")
                   || name.Contains("bubble");
        }

        // ------------------------------------------------------------------
        // 记录
        // ------------------------------------------------------------------

        bool AddEvidence(string id, string displayName, string reason, int weight,
            string origin = null)
        {
            if (!EvidenceLedger.Record(id, displayName, reason, weight, origin)) return false;
            if (_player != null)
                _player.ShowMessage($"你看见了：{displayName}。（已记入证据）", 3.2f);
            return true;
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

        /// <summary>玩家亲眼看见的那一刻，墙上倒计时显示的时间。</summary>
        static string NowTimeText()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return "";
            int minute = Mathf.Clamp(
                Mathf.CeilToInt(Mathf.Max(0f, gm.PhaseTimeRemaining) /
                                LoopTimeline.SecondsPerGameMinute),
                0, LoopTimeline.TotalGameMinutes);
            return LoopTimeline.FormatMinute(minute);
        }
    }
}
