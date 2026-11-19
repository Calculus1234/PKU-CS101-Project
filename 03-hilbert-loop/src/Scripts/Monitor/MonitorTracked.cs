using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 被监控追踪的实体（玩家 / NPC）：在监控屏上显示为红点（UI 覆盖层）。
    /// hideDotInRooms = true 时，进入任一 RoomArea 后红点消失
    /// （人在房间里，走廊监控看不到）。
    /// </summary>
    public class MonitorTracked : MonoBehaviour
    {
        [Tooltip("监控上显示的标记名（调试用）")]
        public string displayName = "住客";

        [Tooltip("监控红点颜色")]
        public Color dotColor = Color.red;

        [Tooltip("进入房间后隐藏红点")]
        public bool hideDotInRooms = true;

        readonly HashSet<RoomArea> _rooms = new HashSet<RoomArea>();

        public bool IsInRoom => _rooms.Count > 0;
        public Color DotColor => dotColor;
        public string DisplayName => displayName;

        /// <summary>当前所在房间（重叠多个时取其一；不在任何房间内返回 null）。</summary>
        public RoomArea CurrentRoom
        {
            get
            {
                foreach (RoomArea room in _rooms) return room;
                return null;
            }
        }

        void Start()
        {
            // 出生点可能已在房间触发区内（例如玩家在 8 号房醒来）：
            // 静止的 Collider（无刚体）不会产生 OnTriggerEnter，这里主动补一次初始重叠检测。
            Collider[] hits = Physics.OverlapSphere(
                transform.position, 0.05f, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                RoomArea area = hits[i].GetComponent<RoomArea>();
                if (area != null) EnterRoom(area);
            }
        }

        public void EnterRoom(RoomArea room)
        {
            if (room != null) _rooms.Add(room);
        }

        public void ExitRoom(RoomArea room)
        {
            if (room != null) _rooms.Remove(room);
        }

        /// <summary>两个实体是否至少同处于一个房间（用于同房间判定，例如窗边推搡）。</summary>
        public bool SharesAnyRoom(MonitorTracked other)
        {
            if (other == null || _rooms.Count == 0 || other._rooms.Count == 0) return false;
            foreach (RoomArea room in _rooms)
            {
                if (other._rooms.Contains(room)) return true;
            }
            return false;
        }
    }
}
