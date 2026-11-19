using Pathfinding;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 寻路封装（A* 主路径 + 直线兜底两级降级）：
    /// 1) A*（默认）：场景里存在已烘焙好的寻路网格（菜单 HilbertLoop/生成寻路网格）时，
    ///    用 Seeker + AIPath 沿 A* 路径移动 —— 绕开墙壁与家具、走门洞；
    ///    接近"关着且没锁"的房门时自动替 NPC 开门，等门开后再继续走；
    ///    路径失败（目标不可达等）→ 放弃当前目标（GiveUp），由行为树换点重试。
    /// 2) 直线兜底：网格不可用（未烘焙 / 组件缺失）时按直线走，前方做**球体扫掠**
    ///    障碍检测（按全身宽度，不会穿墙、不会把身体插进床 / 桌子等家具）。
    ///
    /// 卡死保护（两种模式共用）：连续 2.5s 几乎没动 → 放弃当前目标，
    /// 置 GiveUp 标志（ConsumeGiveUp 读取），交由行为树换点重试。
    ///
    /// 对外接口保持不变：MoveTo / Stop / IsArrived / IsMoving / ConsumeGiveUp。
    /// </summary>
    public class NPCNavigator : MonoBehaviour
    {
        [Header("移动")]
        public float speed = 2.2f;
        [Tooltip("到达判定距离")]
        public float arriveThreshold = 0.4f;

        [Header("A* 自动开门")]
        [Tooltip("前方多少米内的关闭房门会被自动打开（仅限未上锁的门）")]
        public float doorProbeDistance = 1.4f;
        [Tooltip("等门打开的最长时间（秒），超时不再等待")]
        public float doorWaitTimeout = 1.2f;

        [Header("直线兜底（A* 网格不可用时）")]
        [Tooltip("球体扫掠余量（米）：障碍前预留距离，同时也是单帧最大步长")]
        public float obstacleProbe = 0.25f;
        [Tooltip("障碍检测层：默认忽略监控层（自身 / 其他 NPC / 玩家）")]
        public LayerMask obstacleMask = ~(1 << 31);

        /// <summary>NPC 身体半径（胶囊 0.7 缩放 → 半径 0.35），球体扫掠按全身宽度检测，防止身体插进家具。</summary>
        const float BodyRadius = 0.35f;

        /// <summary>探测球在身体上的采样高度偏移（相对胶囊中心，NPC 根位于胶囊中心）。</summary>
        static readonly float[] ProbeOffsets = { -0.25f, 0.25f };

        /// <summary>自动开门的采样高度（相对胶囊中心；门扇覆盖 0~2.2m，三档足以命中）。</summary>
        static readonly float[] DoorProbeHeights = { -0.3f, 0.1f, 0.5f };
        const float DoorProbeRadius = 0.3f;

        /// <summary>卡死判定：每 0.5s 检查一次，位移不足 5cm 记一次；连续 5 次（2.5s）放弃当前目标。</summary>
        const int StuckWindowsToGiveUp = 5;
        const float StuckMoveEpsilon = 0.05f;

        enum MoveMode { None, Direct, AStar }

        MoveMode _mode = MoveMode.None;
        Vector3 _target;
        bool _hasTarget;
        bool _arrived = true;

        // 卡死保护：目标被家具 / 死角挡住时放弃当前目标，由行为树换点重试
        bool _gaveUp;
        float _watchTimer;
        Vector3 _watchPos;
        int _stuckWindows;

        // A* 组件（懒加载：第一次真正用 A* 时才挂载）
        AIPath _ai;

        // 私聊 / 会议期间即使有残留目标也不许再挪一步（外层还有行为树冻结，
        // 这里是无视协程 / 外部 MoveTo 的最后一道保险）。
        NPCController _npc;

        // 自动开门等待
        DoorController _pendingDoor;
        float _doorWaitTimer;

        public bool IsArrived => !_hasTarget || _arrived;
        public bool IsMoving => _hasTarget && !_arrived;

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        /// <summary>移动到世界坐标（幂等：目标基本未变时保持当前移动）。</summary>
        public void MoveTo(Vector3 worldPos)
        {
            worldPos.y = transform.position.y;

            if (_hasTarget && !_arrived && (_target - worldPos).sqrMagnitude <= 0.04f)
            {
                return; // 目标未变，继续当前移动
            }

            _target = worldPos;
            _hasTarget = true;
            _arrived = false;
            BeginMove();
        }

        /// <summary>停止当前移动（停在原地）。</summary>
        public void Stop()
        {
            _hasTarget = false;
            _arrived = true;
            _pendingDoor = null;
            ResetStuckWatch();

            StopAStar();
        }

        /// <summary>
        /// 是否已放弃当前目标（被卡死超过 2.5s / 寻路失败）；读取后自动清零。
        /// 行为树节点应消费该标志并换一个目标点重试。
        /// </summary>
        public bool ConsumeGiveUp()
        {
            bool gaveUp = _gaveUp;
            _gaveUp = false;
            return gaveUp;
        }

        // ------------------------------------------------------------------
        // 模式选择与启动
        // ------------------------------------------------------------------

        void BeginMove()
        {
            ResetStuckWatch();
            _pendingDoor = null;
            _mode = HasBakedGraph() ? MoveMode.AStar : MoveMode.Direct;

            if (_mode == MoveMode.AStar)
            {
                BeginAStarMove();
            }
            else
            {
                StopAStar(); // 直线模式：确保 AIPath 完全关闭，避免两套移动打架
            }
        }

        /// <summary>场景里是否存在已烘焙好、带节点数据的 A* 网格。</summary>
        static bool HasBakedGraph()
        {
            AstarPath astar = AstarPath.active;
            if (astar == null || astar.data == null) return false;
            GridGraph graph = astar.data.gridGraph;
            return graph != null && graph.nodes != null && graph.nodes.Length > 0;
        }

        void EnsureAStarComponents()
        {
            if (_ai != null) return;

            if (GetComponent<Seeker>() == null) gameObject.AddComponent<Seeker>();
            _ai = gameObject.AddComponent<AIPath>();
            _ai.gravity = Vector3.zero;        // 平地移动：关重力，防止被往下拽
            // NPC 目标都是静态点，不需要周期性自动重算路径：
            // 关掉可避免空闲时每 ~2s 一次的无效寻路请求（及由此产生的 "Canceled" 日志）。
            _ai.autoRepath.mode = AutoRepathPolicy.Mode.Never;
            _ai.endReachedDistance = Mathf.Clamp(arriveThreshold * 0.75f, 0.15f, 0.3f);
            _ai.maxSpeed = speed;
            _ai.radius = BodyRadius;
            // 3.4：即使被物理挤出网格，也钳制回可行走区域（防贴墙穿模）。
            _ai.constrainInsideGraph = true;
            _ai.isStopped = true;
            _ai.enabled = false;               // 只有 A* 模式才启用
        }

        void BeginAStarMove()
        {
            EnsureAStarComponents();

            // 行为树给的目标点常常是房间本地坐标，可能落在墙里、床里或桌子里。
            // 直接丢给 A* 会让终点吸附到墙另一侧的节点，NPC 于是一路顶着墙走。
            // 这里先把目标吸附到最近的可行走节点；偏得太远就判定不可达。
            Vector3 destination = _target;
            Vector3 snapped = SnapToWalkable(_target, out bool found);
            if (found)
            {
                float offset = (snapped - _target).magnitude;
                if (offset > 0.9f)
                {
                    Debug.LogWarning($"[NPCNavigator] 目标点距可行走区域 {offset:0.0}m，放弃：{_target}");
                    GiveUp();
                    return;
                }

                // 目标点可能在家具 / 墙里：最近节点虽然在 0.9m 内，
                // 却可能在墙的另一侧。这时走过去只会一路顶墙，直接判不可达。
                if (IsBlockedByGeometry(_target, snapped))
                {
                    Debug.LogWarning($"[NPCNavigator] 目标点与最近可行走点之间隔着障碍，放弃：{_target}");
                    GiveUp();
                    return;
                }

                destination = snapped;
            }

            _ai.enabled = true;
            _ai.isStopped = false;
            _ai.maxSpeed = speed;
            _ai.destination = destination;
            _ai.SearchPath();
        }

        static Vector3 SnapToWalkable(Vector3 worldPos, out bool found)
        {
            found = false;
            AstarPath astar = AstarPath.active;
            if (astar == null) return worldPos;

            NNConstraint constraint = NNConstraint.Default;
            constraint.constrainWalkability = true;
            constraint.walkable = true;

            NNInfo nearest = astar.GetNearest(worldPos, constraint);
            if (nearest.node == null) return worldPos;

            found = true;
            Vector3 point = nearest.position;
            point.y = worldPos.y;
            return point;
        }

        /// <summary>
        /// 目标点与吸附点之间是否隔着实体（墙 / 家具）。
        /// 只在目标与吸附点之间做一次身体高度的直线检测，不做复杂几何判断。
        /// </summary>
        bool IsBlockedByGeometry(Vector3 from, Vector3 to)
        {
            float y = transform.position.y;
            Vector3 a = new Vector3(from.x, y, from.z);
            Vector3 b = new Vector3(to.x, y, to.z);
            return Physics.Linecast(a, b, obstacleMask, QueryTriggerInteraction.Ignore);
        }

        void StopAStar()
        {
            if (_ai == null) return;
            _ai.isStopped = true;
            _ai.enabled = false;
        }

        // ------------------------------------------------------------------
        // 每帧移动
        // ------------------------------------------------------------------

        void Awake()
        {
            _npc = GetComponent<NPCController>();
        }

        void Update()
        {
            if (_npc != null && (_npc.InPrivateChat || _npc.InMeeting))
            {
                if (_hasTarget) Stop();
                return;
            }

            if (!_hasTarget || _arrived) return;

            if (_pendingDoor != null)
            {
                UpdateDoorWait(); // 等门打开期间不做卡死判定
                return;
            }

            WatchProgress();

            switch (_mode)
            {
                case MoveMode.AStar: UpdateAStar(); break;
                default: UpdateDirect(); break;
            }
        }

        // ------------------------------------------------------------------
        // A* 模式
        // ------------------------------------------------------------------

        void UpdateAStar()
        {
            if (_ai == null)
            {
                _mode = MoveMode.Direct; // 组件缺失：降级直线
                return;
            }

            if (_ai.pathPending) return; // 等寻路结果

            if (!_ai.hasPath)
            {
                // 无路径 = 寻路失败（目标不可达 / 网格异常）：放弃，交行为树换点
                GiveUp();
                return;
            }

            if (_ai.reachedDestination)
            {
                _arrived = true;
                _ai.isStopped = true;
                return;
            }

            TryOpenDoorAhead();
        }

        /// <summary>
        /// 前方探测房门：命中"关着且没锁"的门时替 NPC 开门并停下等门，
        /// 门开（或超时）后继续沿路径走。
        /// </summary>
        void TryOpenDoorAhead()
        {
            Vector3 dir = _ai.steeringTarget - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f && _ai.velocity.sqrMagnitude > 0.0001f)
                dir = _ai.velocity;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
            else dir.Normalize();

            DoorController door = FindDoorAhead(dir);
            if (door == null || door.IsBroken) return;

            // 锁着的门（9 号房）不硬闯：放弃当前目标，让行为树换点。
            if (door.IsLocked)
            {
                Debug.Log($"[NPCNavigator] 前方 {door.roomNumber} 号房门已锁，放弃当前目标。");
                GiveUp();
                return;
            }

            if (door.IsOpen) return;

            door.Interact(null); // 替 NPC 打开（此时门必为"关 + 未锁 + 未破"）
            _pendingDoor = door;
            _doorWaitTimer = 0f;
            _ai.isStopped = true;
            ResetStuckWatch();
            Debug.Log($"[NPCNavigator] 自动开门：{door.roomNumber} 号房。");
        }

        /// <summary>
        /// 在前进方向的多档高度做球体扫掠，并在近处做一次球形重叠兜底，
        /// 找到最近的房门。薄门扇 / 斜向接近 / 低帧率都不会漏检。
        /// </summary>
        DoorController FindDoorAhead(Vector3 dir)
        {
            DoorController best = null;
            float bestDistance = float.MaxValue;

            for (int h = 0; h < DoorProbeHeights.Length; h++)
            {
                Vector3 origin = transform.position + Vector3.up * DoorProbeHeights[h];
                RaycastHit[] hits = Physics.SphereCastAll(origin, DoorProbeRadius, dir,
                    doorProbeDistance, obstacleMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; i++)
                {
                    DoorController candidate = hits[i].collider.GetComponentInParent<DoorController>();
                    if (candidate == null || hits[i].distance >= bestDistance) continue;
                    // 排除身后的门（距离为 0 时可能由重叠产生）
                    Vector3 to = candidate.transform.position - transform.position;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.0001f && Vector3.Dot(to.normalized, dir) < 0.2f) continue;
                    best = candidate;
                    bestDistance = hits[i].distance;
                }
            }

            if (best != null) return best;

            // 兜底：近距离球形重叠（门扇很薄，球扫掠在门框边缘可能刚好错过）
            Vector3 ahead = transform.position + dir * 0.85f + Vector3.up * 0.1f;
            Collider[] overlaps = Physics.OverlapSphere(ahead, 0.7f, obstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlaps.Length; i++)
            {
                DoorController candidate = overlaps[i].GetComponentInParent<DoorController>();
                if (candidate == null) continue;

                Vector3 to = candidate.transform.position - transform.position;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance > doorProbeDistance + 0.35f) continue;
                if (distance > 0.0001f && Vector3.Dot(to / distance, dir) < 0.15f) continue;
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        void UpdateDoorWait()
        {
            _doorWaitTimer += Time.deltaTime;

            if (_pendingDoor == null || _pendingDoor.IsOpen || _pendingDoor.IsBroken)
            {
                ResumeAfterDoor();
                return;
            }

            // 门在等待期间被锁上（剧情事件）：不再硬等，放弃目标
            if (_pendingDoor.IsLocked)
            {
                _pendingDoor = null;
                GiveUp();
                return;
            }

            // 还关着：再轻推一次（覆盖开门动画竞态 / 中途又被关上的情况）
            _pendingDoor.Interact(null);

            if (_doorWaitTimer >= doorWaitTimeout)
                ResumeAfterDoor(); // 超时不再等：直接走（网格是通的，卡死保护兜底）
        }

        void ResumeAfterDoor()
        {
            _pendingDoor = null;
            if (_mode == MoveMode.AStar && _ai != null) _ai.isStopped = false;
            ResetStuckWatch();
        }

        /// <summary>放弃当前目标：行为树经由 ConsumeGiveUp 感知并换点重试。</summary>
        void GiveUp()
        {
            _hasTarget = false;
            _arrived = true;
            _gaveUp = true;
            _pendingDoor = null;
            StopAStar();
        }

        // ------------------------------------------------------------------
        // 直线兜底
        // ------------------------------------------------------------------

        void UpdateDirect()
        {
            Vector3 pos = transform.position;
            Vector3 to = _target - pos;
            to.y = 0f;
            float distance = to.magnitude;

            if (distance <= arriveThreshold)
            {
                _arrived = true;
                return;
            }

            Vector3 dir = to / Mathf.Max(distance, 0.0001f);

            // 步长 = min(期望步长, 扫掠余量)：单帧绝不会跨越超过余量的距离
            float step = Mathf.Min(speed * Time.deltaTime, Mathf.Max(obstacleProbe, 0.05f));
            if (step <= 0.0001f) return;

            // 球体扫掠：按全身宽度（双高度采样）检测前进路径；
            // 命中 = 本帧不移动（身体贴住障碍而非插入），由卡死保护在超时后换点
            if (SweepBlocked(pos, dir, step))
                return;

            transform.position = pos + dir * step;
            FaceDirection(dir);
        }

        /// <summary>沿 dir 方向移动 step 距离是否会被障碍挡住（双高度球体扫掠）。</summary>
        bool SweepBlocked(Vector3 pos, Vector3 dir, float step)
        {
            for (int i = 0; i < ProbeOffsets.Length; i++)
            {
                Vector3 origin = pos + Vector3.up * ProbeOffsets[i];
                if (Physics.SphereCast(origin, BodyRadius, dir, out _, step,
                        obstacleMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }

        void FaceDirection(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 12f * Time.deltaTime);
        }

        // ------------------------------------------------------------------
        // 卡死保护
        // ------------------------------------------------------------------

        /// <summary>每 0.5s 检查窗口内的实际位移；连续 2.5s 几乎没动 = 卡死，放弃当前目标。</summary>
        void WatchProgress()
        {
            _watchTimer += Time.deltaTime;
            if (_watchTimer < 0.5f) return;
            _watchTimer = 0f;

            if ((transform.position - _watchPos).sqrMagnitude < StuckMoveEpsilon * StuckMoveEpsilon)
            {
                _stuckWindows++;
                if (_stuckWindows >= StuckWindowsToGiveUp)
                {
                    // 放弃：行为树通过 ConsumeGiveUp 感知并换点
                    _hasTarget = false;
                    _arrived = true;
                    _gaveUp = true;
                    StopAStar();
                }
            }
            else
            {
                _stuckWindows = 0;
            }

            _watchPos = transform.position;
        }

        void ResetStuckWatch()
        {
            _watchTimer = 0f;
            _stuckWindows = 0;
            _watchPos = transform.position;
        }
    }
}
