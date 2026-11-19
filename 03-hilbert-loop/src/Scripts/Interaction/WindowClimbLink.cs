using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 窗户攀爬视觉组件（3.2 修正补丁重写）：每个房间窗口一份，向
    /// RopeTraverseSystem 提供该房窗外的「绳」「钉」视觉引用。
    /// - ropeVisual：长绳（从房内晾衣架垂到窗外；绳子系在本房后显示）；
    /// - nailVisual：钉在窗外墙缝的支点钉（绳子绕过钉子延长；钉下后显示）。
    /// 状态由 RopeState（全局）持有，本组件只负责把状态应用到视觉。
    /// </summary>
    public class WindowClimbLink : MonoBehaviour
    {
        [Tooltip("所在房间号（场景构建器装配）")]
        public int roomNumber = -1;

        [Header("路线视觉（场景构建器装配；初始隐藏）")]
        [Tooltip("长绳视觉（系绳后显示）")]
        public GameObject ropeVisual;

        [Tooltip("支点钉视觉（钉下后显示）")]
        public GameObject nailVisual;

        /// <summary>应用绳 / 钉的显示状态。</summary>
        public void ApplyVisuals(bool ropeTied, bool nailPlaced)
        {
            if (ropeVisual != null) ropeVisual.SetActive(ropeTied);
            if (nailVisual != null) nailVisual.SetActive(nailPlaced);
        }

        /// <summary>
        /// 把钉头挪到绳子实际经过的一侧（side = -1 本地 -x / +1 本地 +x）。
        /// 场景里的钉头默认在 +x；绳向另一侧铺开时，钉头也要跟着挪过去，
        /// 否则玩家会看到绳子从钉子背后绕了个空。
        /// </summary>
        public void SetNailSide(int side)
        {
            if (nailVisual == null || side == 0) return;

            Vector3 local = nailVisual.transform.localPosition;
            local.x = Mathf.Abs(local.x) * (side > 0 ? 1f : -1f);
            nailVisual.transform.localPosition = local;
        }
    }
}
