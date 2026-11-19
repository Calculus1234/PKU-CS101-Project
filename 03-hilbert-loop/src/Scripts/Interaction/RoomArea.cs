using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 房间触发区域：玩家 / NPC 进入或离开时通知其身上的 MonitorTracked，
    /// 用于监控红点的显示与隐藏（进入房间 = 不在走廊监控视野内，红点消失）。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class RoomArea : MonoBehaviour
    {
        [Tooltip("调试用房间名，例如 9号房")]
        public string roomName = "";

        static readonly List<RoomArea> All = new List<RoomArea>();
        int _cachedNumber = -1;

        /// <summary>房间号码（从 roomName / 父物体 Room_N 解析，不依赖场景重建）。</summary>
        public int RoomNumber
        {
            get
            {
                if (_cachedNumber > 0) return _cachedNumber;

                string n = roomName;
                int idx = n.IndexOf('号');
                if (idx > 0 && int.TryParse(n.Substring(0, idx), out int parsed) && parsed > 0)
                {
                    _cachedNumber = parsed;
                    return _cachedNumber;
                }

                Transform parent = transform.parent;
                if (parent != null && parent.name.StartsWith("Room_")
                    && int.TryParse(parent.name.Substring(5), out parsed) && parsed > 0)
                {
                    _cachedNumber = parsed;
                }
                return _cachedNumber;
            }
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
        }

        /// <summary>
        /// 世界坐标是否位于“房间外侧的墙面 / 窗台”这一窄带。
        /// 后墙在本地 z≈2.4，檐台只到 z≈3.45；横向不出墙宽（±2.4）。
        /// 旧版写成 z 2.3~7.5、|x|&lt;4，会把大厅北侧和走道误判成窗外，导致隔墙看到墙钉。
        /// </summary>
        public bool IsExteriorPoint(Vector3 worldPos)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            return local.z > 2.3f && local.z < 3.6f && Mathf.Abs(local.x) < 2.4f;
        }

        /// <summary>
        /// 玩家是否真的站在檐台踏板上（平台 2.0×0.95，可用宽度约 ±1.0）。
        /// 只认踏板范围，不认墙外更远的空中区域。
        /// </summary>
        public bool IsOnExteriorLedge(Vector3 worldPos)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            return local.z > 2.35f && local.z < 3.6f && Mathf.Abs(local.x) < 1.15f;
        }

        /// <summary>玩家此刻是否真的站在某个房间的窗外檐台上。</summary>
        public static bool IsOutsideAnyRoom(Vector3 worldPos)
        {
            for (int i = 0; i < All.Count; i++)
            {
                RoomArea room = All[i];
                if (room != null && room.IsOnExteriorLedge(worldPos)) return true;
            }
            return false;
        }

        /// <summary>玩家此刻所在的檐台属于哪个房间（不在檐台上返回 null）。</summary>
        public static RoomArea FindLedgeRoom(Vector3 worldPos)
        {
            for (int i = 0; i < All.Count; i++)
            {
                RoomArea room = All[i];
                if (room != null && room.IsOnExteriorLedge(worldPos)) return room;
            }
            return null;
        }

        /// <summary>给定外墙上的点属于哪个房间的外侧（找不到返回 null）。</summary>
        public static RoomArea FindExteriorRoom(Vector3 worldPos)
        {
            for (int i = 0; i < All.Count; i++)
            {
                RoomArea room = All[i];
                if (room != null && room.IsExteriorPoint(worldPos)) return room;
            }
            return null;
        }

        void OnTriggerEnter(Collider other)
        {
            MonitorTracked tracked = other.GetComponentInParent<MonitorTracked>();
            if (tracked != null) tracked.EnterRoom(this);

            PlayerController player = other.GetComponentInParent<PlayerController>();
            if (player != null) player.NotifyEnteredRoom(this);
        }

        void OnTriggerExit(Collider other)
        {
            MonitorTracked tracked = other.GetComponentInParent<MonitorTracked>();
            if (tracked != null) tracked.ExitRoom(this);
        }
    }
}
