using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 自定义行为树节点基类（动作叶子）。
    /// brain 挂在 NPC 子物体上，节点统一通过 GetComponentInParent 找到所属 NPC，
    /// 引用缓存一次，避免每帧查找。
    /// </summary>
    public abstract class NPCNode : Leaf
    {
        NPCController _npc;

        protected NPCController Npc
        {
            get
            {
                if (_npc == null) _npc = GetComponentInParent<NPCController>();
                return _npc;
            }
        }

        protected NPCBlackboard Board => Npc != null ? Npc.blackboard : null;
        protected NPCNavigator Nav => Npc != null ? Npc.navigator : null;

        /// <summary>当前房间对象（由黑板房间名解析）。</summary>
        protected Transform CurrentRoom => NPCRoomUtil.FindRoom(Board != null ? Board.currentRoomName : null);

        /// <summary>Animator 行走布尔参数名（正式动画接入前为占位约定）。</summary>
        const string WalkingParam = "Walking";

        /// <summary>写入行走姿态（无 Animator 时静默跳过）。</summary>
        protected void SetWalking(bool walking)
        {
            if (Npc == null || Npc.animator == null) return;
            Npc.animator.SetBool(WalkingParam, walking);
        }

        /// <summary>平滑转向某个世界坐标（感知反应节点共用：观察 / 对峙 / 环顾）。</summary>
        protected void FaceTowards(Vector3 worldPoint, float turnSpeed = 8f)
        {
            if (Npc == null) return;

            Vector3 dir = worldPoint - Npc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;

            Quaternion target = Quaternion.LookRotation(dir);
            Npc.transform.rotation = Quaternion.Slerp(
                Npc.transform.rotation, target, Mathf.Clamp01(turnSpeed * Time.deltaTime));
        }

        /// <summary>朝目标移动一步：到达 = success、移动中 = running、无导航能力 / 目标被卡死放弃 = failure。</summary>
        protected NodeResult MoveTowards(Vector3 target, float arriveDistance)
        {
            if (Npc == null || Nav == null) return NodeResult.failure;

            // 导航器卡死保护触发过（目标被挡住超时）：本次移动尝试作废（failure），由上层节点换点重试
            if (Nav.ConsumeGiveUp()) return NodeResult.failure;

            Vector3 to = target - Npc.transform.position;
            to.y = 0f;
            if (to.magnitude <= arriveDistance)
            {
                Nav.Stop();
                return NodeResult.success;
            }

            Nav.MoveTo(target);
            return NodeResult.running;
        }

        /// <summary>在指定房间内挑一个直线可达的随机点（失败时返回房间中心）。</summary>
        protected Vector3 PickWalkablePointInRoom(Transform room, float radius)
        {
            Vector3 origin = Npc != null ? Npc.transform.position : transform.position;
            if (room == null) return origin;

            for (int i = 0; i < 8; i++)
            {
                Vector3 local = new Vector3(Random.Range(-radius, radius), 0f, Random.Range(-radius, radius));
                Vector3 world = room.TransformPoint(local);
                world.y = origin.y;
                if (Vector3.Distance(origin, world) < 0.4f) continue;
                if (IsPathBlocked(origin, world)) continue;
                return world;
            }

            Vector3 fallback = room.position;
            fallback.y = origin.y;
            return fallback;
        }

        /// <summary>以某点为中心挑一个直线可达的随机点（失败时返回原点）。</summary>
        protected Vector3 PickWalkablePointAround(Vector3 center, float radius)
        {
            Vector3 origin = Npc != null ? Npc.transform.position : transform.position;

            for (int i = 0; i < 8; i++)
            {
                Vector2 offset = Random.insideUnitCircle * radius;
                Vector3 world = new Vector3(center.x + offset.x, origin.y, center.z + offset.y);
                if (Vector3.Distance(origin, world) < 0.4f) continue;
                if (IsPathBlocked(origin, world)) continue;
                return world;
            }
            return origin;
        }

        // 球体扫掠采样高度（相对 NPC 根，根位于胶囊中心）：脚踝 / 腰 / 头，按全身宽度检测
        static readonly float[] ProbeOffsets = { -0.5f, 0f, 0.6f };
        const float ProbeRadius = 0.35f; // = NPC 身体半径，保证选点与走廊宽度一致

        static bool IsPathBlocked(Vector3 from, Vector3 to)
        {
            int mask = ~(1 << MonitorSystem.MonitorIgnoreLayer);

            Vector3 delta = to - from;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance < 0.0001f) return false;
            Vector3 dir = delta / distance;

            for (int i = 0; i < ProbeOffsets.Length; i++)
            {
                Vector3 origin = from + Vector3.up * ProbeOffsets[i];
                if (Physics.SphereCast(origin, ProbeRadius, dir, out _, distance, mask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 自定义行为树条件基类（Condition 装饰节点；与 NPCNode 相同地定位所属 NPC）。
    /// </summary>
    public abstract class NPCCondition : Condition
    {
        NPCController _npc;

        protected NPCController Npc
        {
            get
            {
                if (_npc == null) _npc = GetComponentInParent<NPCController>();
                return _npc;
            }
        }

        protected NPCBlackboard Board => Npc != null ? Npc.blackboard : null;
    }

    /// <summary>房间查找工具：RoomArea.roomName（如 "9号房"） → 场景房间对象（Room_9）。</summary>
    public static class NPCRoomUtil
    {
        static readonly System.Collections.Generic.Dictionary<string, Transform> Cache =
            new System.Collections.Generic.Dictionary<string, Transform>();

        /// <summary>按房间名（"9号房"）找房间对象；找不到返回 null。</summary>
        public static Transform FindRoom(string roomName)
        {
            if (string.IsNullOrEmpty(roomName)) return null;
            if (Cache.TryGetValue(roomName, out Transform cached) && cached != null) return cached;

            Transform room = null;
            string digits = roomName.Replace("号房", "").Trim();
            if (int.TryParse(digits, out int number))
            {
                GameObject go = GameObject.Find("Room_" + number);
                if (go != null) room = go.transform;
            }

            Cache[roomName] = room;
            return room;
        }

        /// <summary>按号码找房间对象（1~9）。</summary>
        public static Transform FindRoom(int number)
        {
            return FindRoom(number + "号房");
        }
    }
}
