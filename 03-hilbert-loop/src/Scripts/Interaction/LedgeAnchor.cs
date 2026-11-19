using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 檐台绳移锚点（3.2 修正补丁·反馈⑥；本轮改为分段锚点提示）：
    /// 挂在每个檐台两侧的不可见交互点。玩家站在檐台上、看向某一侧的墙面时，
    /// 由 RopeTraverseSystem.GetPlayerMoveInfo 判断这一步是：
    /// - 沿自己已有的绳路前进 / 后退；
    /// - 第一次迈出，确定绳子铺向哪一侧；
    /// - 绳端有支点钉（或带着钉锤可现场钉）时接出第二跨；
    /// - 没有支点还强行向前 —— 提示会明确写「会坠落」，按 E 真的会死。
    /// 玩家的绳与 1 号的绳完全隔离，这里只查询 PlayerOwner。
    /// </summary>
    public class LedgeAnchor : MonoBehaviour, IInteractable
    {
        [Tooltip("所在檐台的房间号")]
        public int fromRoom;

        [Tooltip("该侧相邻的房间号（移动目标）")]
        public int toRoom;

        public string GetInteractionPrompt(PlayerController player)
        {
            // 只有人站在室外檐台上时才给出攀爬提示（在屋里看向窗外也不会误触发）
            if (RopeState.PlayerOutsideRoom != fromRoom) return string.Empty;

            InventorySystem inventory = player != null ? player.inventory : null;
            RopeMoveInfo info = RopeTraverseSystem.GetPlayerMoveInfo(fromRoom, toRoom, inventory);

            switch (info.kind)
            {
                case RopeMoveKind.NoRope:
                    return $"{toRoom} 号房方向没有可用的绳子（带长绳从窗口翻出即可系绳）";

                case RopeMoveKind.NotOnRoute:
                    return $"你的长绳系在 {info.anchorRoom} 号房窗外——这里够不到它";

                case RopeMoveKind.OtherSide:
                    if (info.routeEndRoom == info.anchorRoom)
                        return $"绳子只从 {info.anchorRoom} 号房铺向另一侧——{toRoom} 号房这边没有绳";
                    return $"绳子没有铺向 {toRoom} 号房这一侧（当前绳路：{info.anchorRoom}→{info.routeEndRoom}）";

                case RopeMoveKind.TooFar:
                    return string.Empty;

                case RopeMoveKind.AlongRoute:
                    return $"[E] 抓住长绳，挪向 {toRoom} 号房窗外";

                case RopeMoveKind.FirstMove:
                    return $"[E] 抓住长绳，把绳头甩向 {toRoom} 号房一侧并挪过去";

                case RopeMoveKind.ExtendWithNail:
                    return $"[E] 绳子绕过支点钉，挪向 {toRoom} 号房窗外";

                case RopeMoveKind.ExtendWithTools:
                    return $"[E] 在墙缝钉下支点钉，把绳子接向 {toRoom} 号房窗外";

                case RopeMoveKind.UnsafeForward:
                    if (info.atRopeLimit)
                        return $"[E] 强行向前（绳长已到尽头，没有下一处支点——会坠落）";
                    if (info.hasTools)
                        return $"[E] 强行向前（前方没有支点钉——会坠落）";
                    return $"[E] 强行向前（前方没有支点钉，也缺长钉/锤子——会坠落）";

                default:
                    return string.Empty;
            }
        }

        public void Interact(PlayerController player)
        {
            if (RopeState.PlayerOutsideRoom != fromRoom) return;
            if (RopeTraverseSystem.Instance != null)
                RopeTraverseSystem.Instance.TryMoveAlongWall(player, fromRoom, toRoom);
        }
    }
}
