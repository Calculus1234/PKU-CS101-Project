using System;
using System.Collections.Generic;
using Pathfinding;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// A* Pathfinding Project（免费版）桥接。
    ///
    /// 免费版限制：寻路图（Grid Graph / Navmesh）必须在编辑器中手动配置、手动 Bake，
    /// 本桥接不生成、不扫描任何图数据。使用前请在编辑器里搭好寻路图。
    ///
    /// 预留接口：RequestPath(from, to, callback) 发起寻路请求并回调路径点列表，
    /// 供后续 NPC 行为树（MonoBehaviourTree）的"移动到目标"节点调用。
    /// </summary>
    public class AStarBridge : MonoBehaviour
    {
        public static AStarBridge Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>系统中是否存在可用的寻路数据（AstarPath 已初始化）。</summary>
        public static bool IsAvailable
        {
            get { return AstarPath.active != null; }
        }

        /// <summary>
        /// 请求一条路径。callback 参数：（是否成功，路径点列表）。
        /// 回调在寻路计算完成时触发（通常同一帧末或下一帧），不保证立即返回。
        /// </summary>
        public void RequestPath(Vector3 from, Vector3 to, Action<bool, List<Vector3>> callback)
        {
            if (!IsAvailable)
            {
                // 未配置寻路图时静默失败，调用方自行兜底（如直线移动）
                if (callback != null) callback(false, null);
                return;
            }

            ABPath path = ABPath.Construct(from, to, delegate (Path p)
            {
                ABPath abPath = p as ABPath;
                bool success = abPath != null
                    && !abPath.error
                    && abPath.vectorPath != null
                    && abPath.vectorPath.Count > 0;

                if (callback != null)
                    callback(success, success ? abPath.vectorPath : null);
            });

            AstarPath.StartPath(path);
        }

        /// <summary>找到离某点最近的可通行位置（距离超过 maxDistance 视为失败）。</summary>
        public static bool TryGetNearestWalkable(Vector3 position, float maxDistance, out Vector3 result)
        {
            result = position;
            if (!IsAvailable) return false;

            NNInfo nearest = AstarPath.active.GetNearest(position);
            if (nearest.node == null) return false;
            if (Vector3.Distance(position, nearest.position) > maxDistance) return false;

            result = nearest.position;
            return true;
        }
    }
}
