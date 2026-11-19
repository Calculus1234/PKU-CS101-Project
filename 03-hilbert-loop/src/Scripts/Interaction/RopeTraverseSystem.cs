using System.Collections;
using System.Collections.Generic;
using HilbertLoop.BT;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 一条真实的绳索路线（分段锚点模型）：
    /// - 绳子只能从系绳的窗口出发，沿环上的某一个方向铺开，不能分叉；
    /// - 第一次移动时确定方向，之后只能沿这条路线前进 / 后退；
    /// - 基础长度 1 跨（锚点房 → 相邻窗）；要跨第二段，必须在当前绳端所在房间
    ///   钉一枚支点钉，把绳子绕在钉子上；
    /// - 物理上限 2 跨（1 号 → 2 号 → 9 号已经到顶），再多钉子也接不上；
    /// - 每条绳有独立 owner（玩家 / 1 号），互不共用，8 号碰不到 1 号的绳。
    /// </summary>
    public class RopeRoute
    {
        public string owner = "";
        public int anchorRoom = -1;

        /// <summary>环索引方向：-1 = 本地 +x / 环索引递减；+1 = 本地 -x / 环索引递增；0 = 尚未选择方向。</summary>
        public int direction;

        /// <summary>绳子经过的房间，按从锚点到绳端的顺序排列。</summary>
        public readonly List<int> rooms = new List<int>();

        /// <summary>这条绳上钉过支点钉的房间（只对这条绳有效）。</summary>
        public readonly HashSet<int> nails = new HashSet<int>();

        public int EndRoom => rooms.Count > 0 ? rooms[rooms.Count - 1] : -1;
        public int Spans => Mathf.Max(0, rooms.Count - 1);
        public bool IsEmpty => rooms.Count == 0;
        public string RoomsText => string.Join("→", rooms);
    }

    /// <summary>
    /// 绳索世界状态（全局静态）：按 owner 分开保存每条绳的锚点、方向、经过房间与支点钉。
    /// 玩家与 1 号各有一条独立的绳，空间上互不连通。
    /// </summary>
    public static class RopeState
    {
        public const string PlayerOwner = "player";
        public const string Npc1Owner = "npc1";

        public static readonly Dictionary<string, RopeRoute> Routes =
            new Dictionary<string, RopeRoute>();

        /// <summary>玩家当前在哪号房的窗外檐台上（-1 = 在室内）。</summary>
        public static int PlayerOutsideRoom = -1;

        public static RopeRoute GetRoute(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return null;
            RopeRoute route;
            return Routes.TryGetValue(owner, out route) ? route : null;
        }

        public static RopeRoute GetPlayerRoute()
        {
            return GetRoute(PlayerOwner);
        }

        public static void Reset()
        {
            Routes.Clear();
            PlayerOutsideRoom = -1;
        }
    }

    /// <summary>玩家在檐台按 E 时，这一次移动属于哪种情况。</summary>
    public enum RopeMoveKind
    {
        /// <summary>没有自己的绳。</summary>
        NoRope,
        /// <summary>当前檐台不在自己的绳路线上（绳系在别处）。</summary>
        NotOnRoute,
        /// <summary>目标房间不在环上相邻位置。</summary>
        TooFar,
        /// <summary>在自己的绳路线上，但绳没有铺向目标那一侧。</summary>
        OtherSide,
        /// <summary>沿已有绳路前进 / 后退（安全）。</summary>
        AlongRoute,
        /// <summary>绳刚系好，第一次向任意一侧迈出（安全，确定方向）。</summary>
        FirstMove,
        /// <summary>绳端已有支点钉，可以接出第二跨（安全）。</summary>
        ExtendWithNail,
        /// <summary>绳端没有钉，但携带有长钉 + 锤子，可以现场钉钉再接一跨（安全）。</summary>
        ExtendWithTools,
        /// <summary>绳端没有支点钉，强行向前会坠落。</summary>
        UnsafeForward
    }

    /// <summary>一次移动的判定结果（供交互提示与执行共用）。</summary>
    public struct RopeMoveInfo
    {
        public RopeMoveKind kind;
        public int fromRoom;
        public int toRoom;
        public int anchorRoom;
        public int routeEndRoom;
        public int direction;
        public int spans;
        public bool atRopeLimit;
        public bool hasNailAtEnd;
        public bool hasTools;

        public bool IsDangerous => kind == RopeMoveKind.UnsafeForward;

        public bool CanExecute =>
            kind == RopeMoveKind.AlongRoute ||
            kind == RopeMoveKind.FirstMove ||
            kind == RopeMoveKind.ExtendWithNail ||
            kind == RopeMoveKind.ExtendWithTools;

        public bool IsInfoOnly =>
            kind == RopeMoveKind.NoRope ||
            kind == RopeMoveKind.NotOnRoute ||
            kind == RopeMoveKind.OtherSide ||
            kind == RopeMoveKind.TooFar;
    }

    /// <summary>
    /// 室外攀爬系统（3.2 修正补丁·反馈①③⑥⑦，挂在 Systems 上；本轮改为分段锚点模型）：
    /// - 檐台：每扇窗外三尺宽的墙檐（场景构建器建造），是真实的室外落脚空间；
    /// - 每条绳只能从系绳窗口沿一个方向铺开，第一次移动锁死方向，之后不能分叉；
    /// - 基础 1 跨；第二跨必须在当前绳端钉支点钉（长钉 + 锤子），绳绕过钉子；
    /// - 无支点钉还强行向前 = 坠落，交给 PlayerDeathSystem 结算死亡；
    /// - 玩家绳与 1 号绳各自独立，杜绝 8 号隔空使用 1 号绳；
    /// - 连续 LineRenderer 把整条绳路画在外墙上，配合每房钉头看清路线。
    /// </summary>
    public class RopeTraverseSystem : MonoBehaviour
    {
        public static RopeTraverseSystem Instance { get; private set; }

        [Header("装配（场景构建器）")]
        [Tooltip("每个房间窗口的攀爬视觉组件（顺序 = 构建顺序：左环 8,5,6,3,4 → 右环 9,7,1,2）")]
        public WindowClimbLink[] windowLinks;

        [Header("檐台几何（与场景构建器一致）")]
        [Tooltip("檐台面高度（相对房间地面）")]
        public float ledgeHeight = 0.72f;
        [Tooltip("檐台中心相对房间的本地前向距离（窗在 +z）")]
        public float ledgeForward = 2.90f;

        [Header("绳索视觉")]
        [Tooltip("绳索在外墙上的高度（与场景里的 RouteNail 钉头一致）")]
        public float ropeHeight = 1.0f;
        [Tooltip("绳索贴着外墙的本地 z 偏移（墙面约 2.50，留 6cm 防闪烁）")]
        public float ropeWallOffset = 2.56f;
        [Tooltip("绳索 LineRenderer 宽度")]
        public float ropeWidth = 0.035f;
        [Tooltip("绳索颜色")]
        public Color ropeColor = new Color(0.70f, 0.58f, 0.38f, 1f);

        // 环序（与 MainSceneBuilder 的 RightRoomNumbers / LeftRoomNumbers 一致）
        // 右环 4 间：9（正东）→ 7（西北）→ 1（西南）→ 2（东南）。
        // 1→2→9 只走右环南 / 东侧外墙，远离中央大厅。
        // 左环 5 间：8（东北）→ 5（西北）→ 6（正西）→ 3（西南）→ 4（东南）。
        static readonly int[] RightRing = { 9, 7, 1, 2 };
        static readonly int[] LeftRing = { 8, 5, 6, 3, 4 };

        /// <summary>绳子的物理长度上限（跨度数）：1 号 → 9 号的 2 步就是极限。</summary>
        public const int MaxRopeSegments = 2;

        bool _playerBusy;
        readonly Dictionary<string, LineRenderer> _routeLines =
            new Dictionary<string, LineRenderer>();

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            foreach (LineRenderer line in _routeLines.Values)
            {
                if (line != null) Destroy(line.gameObject);
            }
            _routeLines.Clear();
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 环几何 / 方向（静态，节点与交互共用）
        // ------------------------------------------------------------------

        /// <summary>环上相邻步数（同一环内取两个方向的较小值；不同环 = -1 不可达）。</summary>
        public static int StepsBetween(int from, int to)
        {
            if (from == to) return 0;
            int[] ring = RingOf(from);
            if (ring == null || RingOf(to) != ring) return -1;

            int ia = IndexIn(ring, from), ib = IndexIn(ring, to);
            if (ia < 0 || ib < 0) return -1;
            int d = Mathf.Abs(ia - ib);
            return Mathf.Min(d, ring.Length - d);
        }

        /// <summary>环上从 from 到 to 的单步方向：-1 / +1；不相邻或不同环 = 0。</summary>
        public static int DirectionBetween(int from, int to)
        {
            if (from == to) return 0;
            int[] ring = RingOf(from);
            if (ring == null || RingOf(to) != ring) return 0;

            int n = ring.Length;
            int ia = IndexIn(ring, from), ib = IndexIn(ring, to);
            if (ia < 0 || ib < 0) return 0;

            int d = (ib - ia + n) % n;
            if (d == 1) return 1;
            if (d == n - 1) return -1;
            return 0;
        }

        /// <summary>沿环方向取下一个房间；dir = 0 或无效时返回 -1。</summary>
        public static int NeighborInDirection(int room, int dir)
        {
            if (dir == 0) return -1;
            int[] ring = RingOf(room);
            if (ring == null) return -1;

            int index = IndexIn(ring, room);
            if (index < 0) return -1;

            int n = ring.Length;
            return ring[(index + dir + n) % n];
        }

        static int[] RingOf(int room)
        {
            for (int i = 0; i < RightRing.Length; i++) if (RightRing[i] == room) return RightRing;
            for (int i = 0; i < LeftRing.Length; i++) if (LeftRing[i] == room) return LeftRing;
            return null;
        }

        static int IndexIn(int[] ring, int room)
        {
            for (int i = 0; i < ring.Length; i++) if (ring[i] == room) return i;
            return -1;
        }

        static Vector3 RingCenterOf(int room)
        {
            return RingOf(room) == RightRing ? new Vector3(12f, 0f, 0f) : new Vector3(-12f, 0f, 0f);
        }

        /// <summary>檐台落脚点（世界坐标；yOffset = 0 为脚底，NPC 用 0.9 = 胶囊中心）。</summary>
        public Vector3 LedgePoint(int room, float yOffset)
        {
            Transform roomT = NPCRoomUtil.FindRoom(room);
            if (roomT == null) return Vector3.zero;
            return roomT.TransformPoint(new Vector3(0f, ledgeHeight + yOffset, ledgeForward));
        }

        // ------------------------------------------------------------------
        // 移动判定（玩家 / NPC 共用）
        // ------------------------------------------------------------------

        public static RopeMoveInfo GetPlayerMoveInfo(int fromRoom, int toRoom, InventorySystem inventory)
        {
            return GetMoveInfo(RopeState.PlayerOwner, fromRoom, toRoom, inventory);
        }

        /// <summary>
        /// 判定从 fromRoom 的檐台挪向 toRoom 是否可行：
        /// - 绳必须属于 owner 且经过 fromRoom；
        /// - 目标必须与 fromRoom 在环上相邻；
        /// - 沿已铺好的绳路前进 / 后退永远安全；
        /// - 从锚点第一次迈出时确定方向；
        /// - 到绳端后，只有「绳端有支点钉」或「手头有钉锤可现场钉」才能接出第二跨；
        /// - 否则就是无支点强行向前 = 坠落。
        /// </summary>
        public static RopeMoveInfo GetMoveInfo(string owner, int fromRoom, int toRoom,
            InventorySystem inventory)
        {
            var info = new RopeMoveInfo
            {
                fromRoom = fromRoom,
                toRoom = toRoom,
                anchorRoom = -1,
                routeEndRoom = -1,
                kind = RopeMoveKind.NoRope
            };

            RopeRoute route = RopeState.GetRoute(owner);
            if (route == null || route.rooms.Count == 0) return info;

            info.anchorRoom = route.anchorRoom;
            info.routeEndRoom = route.EndRoom;
            info.direction = route.direction;
            info.spans = route.Spans;
            info.hasTools = inventory != null && inventory.Has("nail") && inventory.Has("hammer");

            int index = route.rooms.IndexOf(fromRoom);
            if (index < 0)
            {
                info.kind = RopeMoveKind.NotOnRoute;
                return info;
            }

            if (fromRoom == toRoom || StepsBetween(fromRoom, toRoom) != 1)
            {
                info.kind = RopeMoveKind.TooFar;
                return info;
            }

            // 1) 目标已在绳路上：沿绳前进或后退
            if (index > 0 && route.rooms[index - 1] == toRoom)
            {
                info.kind = RopeMoveKind.AlongRoute;
                return info;
            }
            if (index < route.rooms.Count - 1 && route.rooms[index + 1] == toRoom)
            {
                info.kind = RopeMoveKind.AlongRoute;
                return info;
            }

            // 2) 绳还只有锚点房：第一次迈出，任意相邻一侧都可以，走哪边就锁哪边
            if (route.rooms.Count == 1)
            {
                info.kind = RopeMoveKind.FirstMove;
                return info;
            }

            // 3) 不在绳端：绳没有铺向目标那一侧，不能横跨
            if (index != route.rooms.Count - 1)
            {
                info.kind = RopeMoveKind.OtherSide;
                return info;
            }

            // 4) 在绳端：只有继续沿既定方向才算「向前」
            int expected = NeighborInDirection(fromRoom, route.direction);
            if (toRoom != expected)
            {
                info.kind = RopeMoveKind.OtherSide;
                return info;
            }

            // 5) 绳长已到物理上限：再多钉子也接不上
            if (route.Spans >= MaxRopeSegments)
            {
                info.kind = RopeMoveKind.UnsafeForward;
                info.atRopeLimit = true;
                info.hasNailAtEnd = route.nails.Contains(fromRoom);
                return info;
            }

            // 6) 绳端已有支点钉：绳绕过钉子，可以接出下一跨
            if (route.nails.Contains(fromRoom))
            {
                info.kind = RopeMoveKind.ExtendWithNail;
                info.hasNailAtEnd = true;
                return info;
            }

            // 7) 绳端没有钉，但带着长钉 + 锤子：可以现场钉一枚再走
            if (info.hasTools)
            {
                info.kind = RopeMoveKind.ExtendWithTools;
                return info;
            }

            // 8) 没有支点，也没有工具：强行向前 = 坠落
            info.kind = RopeMoveKind.UnsafeForward;
            return info;
        }

        // ------------------------------------------------------------------
        // 路线状态设置（连同视觉刷新）
        // ------------------------------------------------------------------

        /// <summary>把 owner 的长绳系在某号房（已有绳时不重复系）。</summary>
        public RopeRoute SetAnchorFor(string owner, int room)
        {
            RopeRoute route = RopeState.GetRoute(owner);
            if (route != null)
            {
                if (route.anchorRoom != room)
                {
                    Debug.LogWarning($"[RopeTraverse] {owner} 的绳子已经系在 {route.anchorRoom} 号房，" +
                                     $"不能在 {room} 号房重复系绳。");
                }
                return route;
            }

            route = new RopeRoute { owner = owner, anchorRoom = room };
            route.rooms.Add(room);
            RopeState.Routes[owner] = route;
            RefreshVisuals();
            Debug.Log($"[RopeTraverse] {owner} 的绳子系在 {room} 号房窗外（基础 1 跨）。");
            return route;
        }

        /// <summary>兼容旧调用：默认视为 1 号的绳。</summary>
        public void SetAnchor(int room)
        {
            SetAnchorFor(RopeState.Npc1Owner, room);
        }

        /// <summary>玩家自己的长绳是否正系在这号房。</summary>
        public static bool IsPlayerRopeAnchoredAt(int room)
        {
            RopeRoute route = RopeState.GetPlayerRoute();
            return route != null && route.anchorRoom == room;
        }

        /// <summary>
        /// 玩家在锚点房（室内窗边或该房檐台）解开长绳并收回。
        /// 只清除这条绳路与沿途视觉；已经钉进墙里的支点钉属于消耗品，不会退回背包。
        /// 长绳本身仍留在背包里，可以重新系到任意窗口。
        /// </summary>
        public bool RecallPlayerRope(PlayerController player, int room)
        {
            if (_playerBusy)
            {
                if (player != null) player.ShowMessage("你正抓着绳子——等站稳了再收。", 2f);
                return false;
            }

            RopeRoute route = RopeState.GetPlayerRoute();
            if (route == null || route.rooms.Count == 0)
            {
                if (player != null) player.ShowMessage("窗外没有你系好的长绳。", 2f);
                return false;
            }

            if (route.anchorRoom != room)
            {
                if (player != null)
                    player.ShowMessage($"你的长绳系在 {route.anchorRoom} 号房窗外，" +
                                       $"要回到那里才能解开。", 3f);
                return false;
            }

            string oldRoute = route.RoomsText;
            RopeState.Routes.Remove(RopeState.PlayerOwner);
            RefreshVisuals();

            if (player != null)
                player.ShowMessage($"你把长绳从 {room} 号房的晾衣架上解开，一圈圈收回手里。" +
                                   "窗外的绳影和支点钉一起消失了。", 4f);
            Debug.Log($"[RopeTraverse] 玩家收回 {room} 号房的长绳（原绳路：{oldRoute}）。" +
                      "支点钉已消耗，不退回背包。");
            return true;
        }

        /// <summary>
        /// 外部脚本（1 号 AI）收绳：直接清掉某条绳的路线与视觉，不做玩家那种“必须回到锚点房”的限制。
        /// 支点钉是消耗品，随绳一起隐藏，不退回背包。
        /// </summary>
        public bool RecallRoute(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return false;

            RopeRoute route = RopeState.GetRoute(owner);
            if (route == null) return false;

            string oldRoute = route.RoomsText;
            RopeState.Routes.Remove(owner);
            RefreshVisuals();
            Debug.Log($"[RopeTraverse] {owner} 收回长绳（原绳路：{oldRoute}），窗外绳影与支点钉一并消失。");
            return true;
        }

        /// <summary>在 owner 的绳路上钉一枚支点钉（钉子只对这条绳有效）。</summary>
        public void PlaceNail(string owner, int room)
        {
            RopeRoute route = RopeState.GetRoute(owner);
            if (route == null)
            {
                Debug.LogWarning($"[RopeTraverse] {owner} 还没有绳，无法在 {room} 号房钉支点钉。");
                return;
            }

            if (!route.rooms.Contains(room))
            {
                Debug.LogWarning($"[RopeTraverse] {room} 号房不在 {owner} 的绳路上，钉子无法支撑这条绳。");
                return;
            }

            route.nails.Add(room);
            RefreshVisuals();
            Debug.Log($"[RopeTraverse] {owner} 在 {room} 号房窗外钉下支点钉（绳路：{route.RoomsText}）。");
        }

        /// <summary>兼容旧调用：默认视为 1 号的绳。</summary>
        public void PlaceNail(int room)
        {
            PlaceNail(RopeState.Npc1Owner, room);
        }

        /// <summary>某个房间窗外支点钉的世界坐标（供玩家“亲眼看见钉钉”的判定）。</summary>
        public Vector3 GetNailPoint(int room)
        {
            if (windowLinks != null)
            {
                for (int i = 0; i < windowLinks.Length; i++)
                {
                    WindowClimbLink link = windowLinks[i];
                    if (link != null && link.roomNumber == room && link.nailVisual != null)
                        return link.nailVisual.transform.position;
                }
            }

            Transform t = NPCRoomUtil.FindRoom(room);
            return t != null
                ? t.TransformPoint(new Vector3(0.55f, ropeHeight, ropeWallOffset))
                : Vector3.zero;
        }

        /// <summary>
        /// 把绳路向 toRoom 延长一跨。返回 false 表示方向不符 / 已到极限 / 绳端没有支点钉。
        /// 第一跨从锚点直接出发；第二跨要求当前绳端已有支点钉。
        /// </summary>
        bool ExtendRoute(RopeRoute route, int toRoom)
        {
            if (route == null || route.rooms.Count == 0) return false;

            int last = route.EndRoom;
            int dir = DirectionBetween(last, toRoom);
            if (dir == 0) return false;

            if (route.direction == 0) route.direction = dir;
            else if (route.direction != dir) return false;

            if (route.rooms.Contains(toRoom)) return false;
            if (route.Spans >= MaxRopeSegments) return false;

            // 第一跨靠窗内的晾衣架固定；第二跨起，绳端必须有支点钉
            if (route.rooms.Count > 1 && !route.nails.Contains(last)) return false;

            route.rooms.Add(toRoom);
            RefreshVisuals();
            Debug.Log($"[RopeTraverse] {route.owner} 的绳路延长：{route.RoomsText}" +
                      $"（{route.Spans}/{MaxRopeSegments} 跨）。");
            return true;
        }

        /// <summary>3.5 轮回重置：清空所有绳路 / 支点钉 / 檐台位置，并同步视觉。</summary>
        public void ResetRopeState()
        {
            RopeState.Reset();
            RefreshVisuals();
            Debug.Log("[RopeTraverse] 绳路 / 支点钉 / 檐台状态已重置。");
        }

        /// <summary>把每条绳的状态同步到各房间窗口的视觉体，并重画连续的绳索路线。</summary>
        public void RefreshVisuals()
        {
            if (windowLinks != null)
            {
                for (int i = 0; i < windowLinks.Length; i++)
                {
                    WindowClimbLink link = windowLinks[i];
                    if (link == null) continue;

                    bool ropeTied = false;
                    bool nailPlaced = false;
                    int nailSide = 0;

                    foreach (RopeRoute route in RopeState.Routes.Values)
                    {
                        if (route.anchorRoom == link.roomNumber) ropeTied = true;
                        if (route.nails.Contains(link.roomNumber))
                        {
                            nailPlaced = true;
                            if (route.direction != 0) nailSide = route.direction;
                        }
                    }

                    link.ApplyVisuals(ropeTied, nailPlaced);
                    if (nailPlaced) link.SetNailSide(nailSide == 0 ? 1 : nailSide);
                }
            }

            RefreshRouteLines();
        }

        void RefreshRouteLines()
        {
            var activeOwners = new HashSet<string>();

            foreach (RopeRoute route in RopeState.Routes.Values)
            {
                if (route == null || route.rooms.Count < 2) continue;

                activeOwners.Add(route.owner);
                LineRenderer line = GetOrCreateLine(route.owner);
                List<Vector3> points = BuildRoutePoints(route);
                if (points.Count < 2)
                {
                    line.enabled = false;
                    continue;
                }

                line.enabled = true;
                line.positionCount = points.Count;
                line.SetPositions(points.ToArray());
            }

            foreach (KeyValuePair<string, LineRenderer> pair in _routeLines)
            {
                if (pair.Value != null && !activeOwners.Contains(pair.Key))
                    pair.Value.enabled = false;
            }
        }

        LineRenderer GetOrCreateLine(string owner)
        {
            LineRenderer line;
            if (_routeLines.TryGetValue(owner, out line) && line != null) return line;

            var go = new GameObject("RopeLine_" + owner);
            go.transform.SetParent(transform, false);

            line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.startWidth = ropeWidth;
            line.endWidth = ropeWidth;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = ropeColor;
            line.endColor = ropeColor;
            line.material = CreateRopeMaterial(owner);

            _routeLines[owner] = line;
            return line;
        }

        Material CreateRopeMaterial(string owner)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Standard");

            var material = new Material(shader) { name = "RopeLineMat_" + owner };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", ropeColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", ropeColor);
            return material;
        }

        List<Vector3> BuildRoutePoints(RopeRoute route)
        {
            var points = new List<Vector3>();
            if (route == null || route.rooms.Count == 0) return points;

            Transform first = NPCRoomUtil.FindRoom(route.rooms[0]);
            if (first != null) points.Add(WallPoint(first));

            for (int i = 0; i < route.rooms.Count - 1; i++)
            {
                AppendWallArc(points, route.rooms[i], route.rooms[i + 1]);
            }
            return points;
        }

        /// <summary>把 fromRoom → toRoom 之间的外墙弧线采样进点列（沿环的短弧，固定绳高）。</summary>
        void AppendWallArc(List<Vector3> points, int fromRoom, int toRoom)
        {
            Transform fromT = NPCRoomUtil.FindRoom(fromRoom);
            Transform toT = NPCRoomUtil.FindRoom(toRoom);
            if (fromT == null || toT == null) return;

            Vector3 p0 = WallPoint(fromT);
            Vector3 p1 = WallPoint(toT);
            Vector3 center = RingCenterOf(fromRoom);

            float r0 = new Vector2(p0.x - center.x, p0.z - center.z).magnitude;
            float r1 = new Vector2(p1.x - center.x, p1.z - center.z).magnitude;
            float a0 = Mathf.Atan2(p0.z - center.z, p0.x - center.x);
            float a1 = Mathf.Atan2(p1.z - center.z, p1.x - center.x);

            while (a1 - a0 > Mathf.PI) a1 -= 2f * Mathf.PI;
            while (a0 - a1 > Mathf.PI) a1 += 2f * Mathf.PI;

            const int samples = 16;
            for (int s = 1; s <= samples; s++)
            {
                float t = s / (float)samples;
                float k = Mathf.SmoothStep(0f, 1f, t);
                float a = Mathf.Lerp(a0, a1, k);
                float r = Mathf.Lerp(r0, r1, k);
                var p = new Vector3(
                    center.x + Mathf.Cos(a) * r,
                    ropeHeight,
                    center.z + Mathf.Sin(a) * r);

                if (points.Count == 0 ||
                    (p - points[points.Count - 1]).sqrMagnitude > 0.0001f)
                {
                    points.Add(p);
                }
            }
        }

        Vector3 WallPoint(Transform roomT)
        {
            return roomT.TransformPoint(new Vector3(0f, ropeHeight, ropeWallOffset));
        }

        // ------------------------------------------------------------------
        // 玩家：翻窗（F）与沿墙绳移（檐台锚点 E）
        // ------------------------------------------------------------------

        /// <summary>玩家翻出窗外（室内 → 檐台；持长绳且尚未系绳时自动系绳）。</summary>
        public void ClimbOut(PlayerController player, WindowController window)
        {
            if (_playerBusy || player == null || window == null || window.roomNumber <= 0) return;
            StartCoroutine(ClimbOutRoutine(player, window.roomNumber));
        }

        /// <summary>玩家翻进窗内（檐台 → 室内）。</summary>
        public void ClimbIn(PlayerController player, WindowController window)
        {
            if (_playerBusy || player == null || window == null || window.roomNumber <= 0) return;
            StartCoroutine(ClimbInRoutine(player, window.roomNumber));
        }

        IEnumerator ClimbOutRoutine(PlayerController player, int room)
        {
            _playerBusy = true;
            yield return WrapPlayerAnimation(player, MoveThroughWindow(player.transform, room, true, 0f, 1.0f));

            RopeState.PlayerOutsideRoom = room;

            RopeRoute route = RopeState.GetPlayerRoute();
            bool hasRope = player.inventory != null && player.inventory.Has("rope");

            if (route == null && hasRope)
            {
                SetAnchorFor(RopeState.PlayerOwner, room);
                player.ShowMessage("你把长绳系在窗里的晾衣架上，绳头垂出窗外，贴着墙缝里的钉子。", 3.5f);
            }
            else if (route != null && route.rooms.Contains(room))
            {
                player.ShowMessage($"你翻出窗外，抓住了自己从 {route.anchorRoom} 号房牵出来的长绳。", 3f);
            }
            else if (route != null)
            {
                player.ShowMessage($"你翻出窗外——你的长绳还系在 {route.anchorRoom} 号房窗外，这里够不到。", 3.5f);
            }
            else
            {
                player.ShowMessage("你翻出窗外——脚下是三尺宽的墙檐，再往外就是吞掉一切的风雪。" +
                                   "没有绳，你只能沿原路翻回去。", 3.5f);
            }

            Debug.Log($"[RopeTraverse] 玩家翻出 {room} 号房窗外（檐台）。" +
                      $"玩家绳路={DescribeRoute(RopeState.GetPlayerRoute())}。");
            _playerBusy = false;
        }

        IEnumerator ClimbInRoutine(PlayerController player, int room)
        {
            _playerBusy = true;
            yield return WrapPlayerAnimation(player, MoveThroughWindow(player.transform, room, false, 0f, 1.0f));

            RopeState.PlayerOutsideRoom = -1;
            player.ShowMessage("你翻身回到屋里，风声被关在了窗外。", 2.5f);
            Debug.Log($"[RopeTraverse] 玩家从 {room} 号房窗外翻回室内。");
            _playerBusy = false;
        }

        /// <summary>
        /// 玩家在檐台按 E：按分段锚点判定移动。
        /// 安全情况直接走；绳端有钉 / 有钉锤则接出第二跨；无支点强行向前则坠落。
        /// </summary>
        public void TryMoveAlongWall(PlayerController player, int fromRoom, int toRoom)
        {
            if (_playerBusy || player == null) return;

            RopeMoveInfo info = GetPlayerMoveInfo(fromRoom, toRoom, player.inventory);

            switch (info.kind)
            {
                case RopeMoveKind.NoRope:
                    player.ShowMessage("你没有一条系好的长绳——携带长绳从窗口翻出去，绳子就会自动系上。", 4f);
                    return;

                case RopeMoveKind.NotOnRoute:
                    player.ShowMessage($"这条绳系在 {info.anchorRoom} 号房窗外，你在这段墙檐上够不到它。", 3.5f);
                    return;

                case RopeMoveKind.OtherSide:
                    player.ShowMessage($"绳子没有铺向 {toRoom} 号房这一侧——硬跨过去，手里什么也抓不到。", 3.5f);
                    return;

                case RopeMoveKind.TooFar:
                    player.ShowMessage("那一边不是相邻的窗，绳跨不过去。", 3f);
                    return;

                case RopeMoveKind.UnsafeForward:
                    StartCoroutine(FallFromLedgeRoutine(player, fromRoom, info));
                    return;
            }

            RopeRoute route = RopeState.GetPlayerRoute();

            if (info.kind == RopeMoveKind.ExtendWithTools)
            {
                // 长钉是消耗品：钉进墙缝后从背包扣除，不会留在手里。
                bool consumed = player.inventory != null && player.inventory.Remove("nail");
                if (!consumed)
                {
                    player.ShowMessage("你摸到了锤子，却发现长钉已经用完了。", 3f);
                    return;
                }

                PlaceNail(RopeState.PlayerOwner, fromRoom);
                int left = player.inventory.CountOf("nail");
                player.ShowMessage($"你摸出长钉和锤子，把支点钉整个钉进墙缝——" +
                                   $"钉头没入石缝，绳子绕过它绷直了。（长钉 -1，剩余 {left} 枚）", 4f);
                Debug.Log($"[RopeTraverse] 玩家在 {fromRoom} 号房窗外钉下支点钉" +
                          $"（钉子 -1，剩余 {left}）。");
            }

            if (info.kind == RopeMoveKind.FirstMove ||
                info.kind == RopeMoveKind.ExtendWithNail ||
                info.kind == RopeMoveKind.ExtendWithTools)
            {
                if (!ExtendRoute(route, toRoom))
                {
                    player.ShowMessage("绳子接不过去。", 3f);
                    Debug.LogWarning($"[RopeTraverse] 玩家绳路延长失败：{fromRoom} → {toRoom}。");
                    return;
                }
            }

            StartCoroutine(MovePlayerAlongWallRoutine(player, fromRoom, toRoom));
        }

        IEnumerator MovePlayerAlongWallRoutine(PlayerController player, int fromRoom, int toRoom)
        {
            _playerBusy = true;
            yield return WrapPlayerAnimation(player, ArcAlongWall(player.transform, fromRoom, toRoom, 0f, 1.6f));

            RopeState.PlayerOutsideRoom = toRoom;
            player.ShowMessage($"你抓着长绳沿着外墙挪动，翻到了 {toRoom} 号房窗外。", 2.8f);
            Debug.Log($"[RopeTraverse] 玩家沿墙绳移：{fromRoom} 号房窗外 → {toRoom} 号房窗外。" +
                      $"玩家绳路={DescribeRoute(RopeState.GetPlayerRoute())}。");
            _playerBusy = false;
        }

        /// <summary>
        /// 无支点强行向前：手指抓空，人从檐台坠落。
        /// 先把玩家带离檐台做出坠落，再交给 PlayerDeathSystem 进入幽灵观战 / 结局判定。
        /// </summary>
        IEnumerator FallFromLedgeRoutine(PlayerController player, int fromRoom, RopeMoveInfo info)
        {
            _playerBusy = true;

            if (PlayerDeathSystem.Instance == null)
            {
                player.ShowMessage("你向前迈了一步，脚下只剩风雪——但死亡系统缺失，坠落未结算。", 4f);
                Debug.LogError("[RopeTraverse] 场景缺少 PlayerDeathSystem，无法结算无锚点坠落。");
                _playerBusy = false;
                yield break;
            }

            player.ShowMessage(info.atRopeLimit
                ? "绳长已经到尽头。你还是向前迈了一步——手里只剩风。"
                : "前方没有支点钉。你还是向前迈了一步——指尖抓到的只有风。", 2.5f);

            CharacterController cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.inputLocked = true;
            player.movementLocked = true;

            RopeState.PlayerOutsideRoom = -1;

            Vector3 start = player.transform.position;
            Vector3 center = RingCenterOf(fromRoom);
            Vector3 outward = start - center;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : Vector3.forward;

            const float duration = 0.95f;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float k = Mathf.Clamp01(t);
                player.transform.position = start
                    + Vector3.down * (7f * k * k)
                    + outward * (0.9f * k);
                player.transform.Rotate(Vector3.forward, 120f * Time.deltaTime, Space.Self);
                yield return null;
            }

            Debug.LogWarning($"[RopeTraverse] 玩家在 {fromRoom} 号房窗外无支点强行向前，判定坠落。");
            PlayerDeathSystem.Instance.KillPlayer("从檐台坠落");
            _playerBusy = false;
        }

        /// <summary>动画期间包裹玩家控制：禁用 CharacterController 与输入，结束后恢复。</summary>
        static IEnumerator WrapPlayerAnimation(PlayerController player, IEnumerator animation)
        {
            CharacterController cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.inputLocked = true;

            yield return animation;

            player.inputLocked = false;
            if (cc != null) cc.enabled = true;
        }

        // ------------------------------------------------------------------
        // NPC：沿同一套绳路移动（1 号杀手流程）
        // ------------------------------------------------------------------

        /// <summary>
        /// NPC 沿自己（owner）的绳路移动；第一次移动 / 有支点钉的延长会自动写入绳路。
        /// 1 号流程使用 owner = RopeState.Npc1Owner，绝不与玩家共用。
        /// </summary>
        public IEnumerator MoveNpcAlongRope(Transform mover, string owner, int fromRoom, int toRoom,
            float yOffset, float duration)
        {
            RopeRoute route = RopeState.GetRoute(owner);
            RopeMoveInfo info = GetMoveInfo(owner, fromRoom, toRoom, null);

            if (info.kind == RopeMoveKind.AlongRoute)
            {
                yield return ArcAlongWall(mover, fromRoom, toRoom, yOffset, duration);
                yield break;
            }

            if (info.kind == RopeMoveKind.FirstMove || info.kind == RopeMoveKind.ExtendWithNail)
            {
                if (!ExtendRoute(route, toRoom))
                {
                    Debug.LogError($"[RopeTraverse] NPC 绳路延长失败：{owner} {fromRoom} → {toRoom}，" +
                                   $"当前绳路={DescribeRoute(route)}。");
                }
                yield return ArcAlongWall(mover, fromRoom, toRoom, yOffset, duration);
                yield break;
            }

            // 脚本化流程如果遇到异常状态，仍然播放位移，避免 1 号卡死在墙外；
            // 但明确记录错误，方便定位是哪一步破坏了绳路。
            Debug.LogError($"[RopeTraverse] NPC 绳路判定异常（{info.kind}）：" +
                           $"{owner} {fromRoom} → {toRoom}，当前绳路={DescribeRoute(route)}。");
            yield return ArcAlongWall(mover, fromRoom, toRoom, yOffset, duration);
        }

        static string DescribeRoute(RopeRoute route)
        {
            if (route == null) return "无";
            string nails = route.nails.Count > 0 ? string.Join("、", route.nails) : "无";
            return $"{route.RoomsText}（方向 {route.direction}，{route.Spans}/{MaxRopeSegments} 跨，支点钉：{nails}）";
        }

        // ------------------------------------------------------------------
        // 移动几何（玩家与 NPC 共用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 穿过窗户的翻越动画（outward=true：房内 → 檐台；false：檐台 → 房内）。
        /// 窗洞几何：y 0.90~2.10（高 1.2），墙厚 z 2.30~2.50。
        /// - 玩家（yOffset=0，脚底基准）：压低脚位让「眼睛」（脚位 + 1.62）从洞内穿过——
        ///   跨墙瞬间眼睛 ≈1.9~2.0，低于洞顶 2.10（否则第一人称视线会从洞顶的墙里穿出去）；
        /// - NPC（yOffset≈0.9，胶囊中心基准）：站姿总高 1.8 过不去 1.2 高的窗洞，
        ///   翻越时整体前倾 76°（垂直包络 ≈0.97），中心贴洞心 1.50 穿过；收腿站起时
        ///   微微上提（脚从墙缝下沿之上掠过），落在檐台上顺滑站直。
        /// 期间由调用方包裹 CharacterController / 输入锁定。
        /// </summary>
        public IEnumerator MoveThroughWindow(Transform mover, int room, bool outward, float yOffset, float duration)
        {
            Transform roomT = NPCRoomUtil.FindRoom(room);
            if (roomT == null) yield break;

            if (yOffset < 0.4f)
                yield return PlayerThroughWindow(mover, roomT, outward, Mathf.Max(0.2f, duration));
            else
                yield return NpcThroughWindow(mover, roomT, outward, Mathf.Max(0.3f, duration));
        }

        /// <summary>玩家翻窗（脚位基准；眼睛必须从窗洞内穿过）。</summary>
        IEnumerator PlayerThroughWindow(Transform mover, Transform roomT, bool outward, float duration)
        {
            Vector3 start = mover.position;
            Vector3 p1 = roomT.TransformPoint(new Vector3(0f, 0.30f, 2.18f)); // 洞前（内）· 眼睛 1.92
            Vector3 p2 = roomT.TransformPoint(new Vector3(0f, 0.40f, 2.58f)); // 洞后（外）· 眼睛 2.02
            Vector3 p3 = LedgePointOf(roomT, 0f);                             // 檐台脚位（0.72，z 2.75）
            Vector3 inner = roomT.TransformPoint(new Vector3(0f, 0.02f, 1.35f));

            if (outward)
            {
                yield return GlideSegment(mover, start, p1, duration * 0.35f);
                yield return GlideSegment(mover, p1, p2, duration * 0.30f);
                yield return GlideSegment(mover, p2, p3, duration * 0.35f);
            }
            else
            {
                yield return GlideSegment(mover, start, p2, duration * 0.35f);
                yield return GlideSegment(mover, p2, p1, duration * 0.30f);
                yield return GlideSegment(mover, p1, inner, duration * 0.35f);
            }
        }

        // NPC 翻窗参数（胶囊：中心 = 脚位 + 0.9；总高 1.8，半径 0.35）
        const float NpcLeanMax = 76f; // 前倾角：垂直包络 = 1.1*cos76 + 0.7 ≈ 0.97 < 1.2（窗洞高）

        /// <summary>NPC 翻窗（胶囊中心基准；前倾 76° 从窗洞钻过）。</summary>
        IEnumerator NpcThroughWindow(Transform mover, Transform roomT, bool outward, float duration)
        {
            Vector3 start = mover.position;
            Vector3 p1 = roomT.TransformPoint(new Vector3(0f, 1.32f, 2.05f)); // 内·撑起前倾
            Vector3 p3 = roomT.TransformPoint(new Vector3(0f, 1.52f, 2.62f)); // 外·仍前倾（已过墙）
            Vector3 p4 = LedgePointOf(roomT, 0.9f);                           // 檐台站姿中心（1.62，z 2.75）
            Vector3 inner = roomT.TransformPoint(new Vector3(0f, 0.92f, 1.35f));

            // 朝向参照取「设计路径的上一帧位置」而非 mover.position：
            // 高帧率下每帧位移可能小于 SetLeaningPose 的方向阈值，若拿自身写入结果当参照，
            // 会走兜底分支导致前倾角逐帧累积（角色原地打转）。
            Vector3 prev = mover.position;
            float s = 0f;
            while (s < 1f)
            {
                s += Time.deltaTime / duration;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s));
                NpcCrossSample(k, outward, start, inner, p1, p3, p4, out Vector3 pos, out float lean);
                SetLeaningPose(mover, pos, lean, pos - prev);
                prev = pos;
                yield return null;
            }

            NpcCrossSample(1f, outward, start, inner, p1, p3, p4, out Vector3 endPos, out float endLean);
            SetLeaningPose(mover, endPos, endLean, endPos - prev);
        }

        /// <summary>NPC 翻窗路径采样：k∈[0,1] → 位置 + 前倾角（outward / inward 两套分段）。</summary>
        static void NpcCrossSample(float k, bool outward, Vector3 start, Vector3 inner,
            Vector3 p1, Vector3 p3, Vector3 p4, out Vector3 pos, out float lean)
        {
            if (outward)
            {
                if (k < 0.25f)      // 起身前倾，钻向洞口
                {
                    float t = k / 0.25f;
                    pos = Vector3.Lerp(start, p1, t);
                    lean = NpcLeanMax * t;
                }
                else if (k < 0.55f) // 贴着洞心穿过墙
                {
                    float t = (k - 0.25f) / 0.30f;
                    pos = Vector3.Lerp(p1, p3, t);
                    lean = NpcLeanMax;
                }
                else                // 攀上檐台，收腿站起（微微上提）
                {
                    float t = (k - 0.55f) / 0.45f;
                    UnleanProfile(t, p3, p4, out pos, out lean);
                }
            }
            else
            {
                if (k < 0.45f)      // 檐台上前倾起步，钻回洞口（上提段反向）
                {
                    float t = k / 0.45f;
                    UnleanProfile(1f - t, p3, p4, out pos, out lean);
                }
                else if (k < 0.75f) // 贴洞心穿回墙内
                {
                    float t = (k - 0.45f) / 0.30f;
                    pos = Vector3.Lerp(p3, p1, t);
                    lean = NpcLeanMax;
                }
                else                // 落地站直
                {
                    float t = (k - 0.75f) / 0.25f;
                    pos = Vector3.Lerp(p1, inner, t);
                    lean = NpcLeanMax * (1f - t);
                }
            }
        }

        /// <summary>
        /// 前倾 ↔ 站立的过渡曲线（t=0 前倾贴地 / t=1 檐台站直）：
        /// 站位从 p3 挪到 p4；收腿期间按前倾角做「上提」修正，保证脚从窗洞下沿之上掠过（不穿墙）。
        /// </summary>
        static void UnleanProfile(float t, Vector3 p3, Vector3 p4, out Vector3 pos, out float lean)
        {
            lean = NpcLeanMax * (1f - t);
            Vector3 flat = Vector3.Lerp(p3, p4, t);
            float clearance = 1.36f + 0.55f * Mathf.Cos(lean * Mathf.Deg2Rad); // 脚球底 ≥0.90 所需中心高
            float w = Mathf.Clamp01((lean - 20f) / 12f);                        // 前倾 >20° 才需要上提
            float y = Mathf.Lerp(flat.y, Mathf.Max(flat.y, clearance), w);
            pos = new Vector3(flat.x, y, flat.z);
        }

        /// <summary>按位置 + 前倾角设置姿态（朝向取运动方向，再叠加前倾俯角）。</summary>
        static void SetLeaningPose(Transform mover, Vector3 pos, float lean, Vector3 delta)
        {
            mover.position = pos;
            delta.y = 0f;
            // 位移过小时保留当前偏航、把俯角重置为 lean——不能沿用 mover.rotation 当偏航，
            // 否则下式会把 Euler(lean) 逐帧乘进姿态里，前倾角累积成持续旋转。
            Quaternion yaw = delta.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(delta.normalized)
                : Quaternion.Euler(0f, mover.rotation.eulerAngles.y, 0f);
            mover.rotation = yaw * Quaternion.Euler(lean, 0f, 0f);
        }

        /// <summary>檐台落脚点（给定房间根节点；yOffset = 0 脚底 / 0.9 胶囊中心）。</summary>
        Vector3 LedgePointOf(Transform roomT, float yOffset)
        {
            return roomT.TransformPoint(new Vector3(0f, ledgeHeight + yOffset, ledgeForward));
        }

        /// <summary>沿外墙弧线从 from 檐台爬到 to 檐台（贴墙飞行，朝向运动方向）。</summary>
        public IEnumerator ArcAlongWall(Transform mover, int fromRoom, int toRoom, float yOffset, float duration)
        {
            Vector3 center = RingCenterOf(fromRoom);
            Vector3 p0 = LedgePoint(fromRoom, yOffset);
            Vector3 p1 = LedgePoint(toRoom, yOffset);

            float radius = new Vector2(p0.x - center.x, p0.z - center.z).magnitude;
            float a0 = Mathf.Atan2(p0.z - center.z, p0.x - center.x);
            float a1 = Mathf.Atan2(p1.z - center.z, p1.x - center.x);
            while (a1 - a0 > Mathf.PI) a1 -= 2f * Mathf.PI;
            while (a0 - a1 > Mathf.PI) a1 += 2f * Mathf.PI;

            Vector3 prev = mover.position;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.05f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                float a = Mathf.Lerp(a0, a1, k);
                Vector3 pos = new Vector3(
                    center.x + Mathf.Cos(a) * radius,
                    p0.y,
                    center.z + Mathf.Sin(a) * radius);
                mover.position = pos;

                Vector3 dir = pos - prev;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    mover.rotation = Quaternion.LookRotation(dir.normalized);
                prev = pos;
                yield return null;
            }

            mover.position = p1;
            Vector3 tan = p1 - prev;
            tan.y = 0f;
            if (tan.sqrMagnitude > 0.0001f)
                mover.rotation = Quaternion.LookRotation(tan.normalized);
        }

        static IEnumerator GlideSegment(Transform mover, Vector3 from, Vector3 to, float duration)
        {
            Quaternion fromRot = mover.rotation;
            Vector3 flat = to - from;
            flat.y = 0f;
            Quaternion toRot = flat.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(flat.normalized)
                : fromRot;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.05f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                mover.position = Vector3.Lerp(from, to, k);
                mover.rotation = Quaternion.Slerp(fromRot, toRot, k);
                yield return null;
            }

            mover.position = to;
            mover.rotation = toRot;
        }

        /// <summary>外部（玩家翻窗演出）查询：当前是否有翻窗 / 绳移动画在进行。</summary>
        public bool PlayerBusy => _playerBusy;
    }
}
