using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 把本次搜索的发现写入黑板已知线索（按当前房间名生成文案），并广播线索事件。
    /// 线索数量到达上限后不再新增，直接成功。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Add Known Clue")]
    public class AddKnownClue : NPCNode
    {
        [Tooltip("每名 NPC 的线索数量上限")]
        public int maxClues = 6;

        // 线索文案模板（{0} = 房间名）；数量与上限一致，保证同一 NPC 不出现重复线索。
        // 只描述场景里真实存在的床、桌面、道具、门闩、窗台与地板；
        // 不写剧本里没有的钟表、水房、厨房，也不写水渍、拖拽痕这类场景没有的痕迹。
        static readonly string[] Templates =
        {
            "{0}的床铺还留着有人刚躺过的褶皱。",
            "{0}的桌面上，几件东西被挪动过位置。",
            "{0}的桌边空着，原本摆在那里的东西被带走了。",
            "{0}的门闩是从里面拨开的，门板没有撬动痕迹。",
            "{0}的窗锁完好，窗框内侧没有被撬过的痕迹。",
            "{0}的桌上，匕首和锤子的柄都被擦过。",
        };

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null) return NodeResult.failure;

            string roomName = b.currentRoomName;
            if (string.IsNullOrEmpty(roomName)) return NodeResult.success;
            if (b.knownClues.Count >= maxClues) return NodeResult.success;

            int start = b.knownClues.Count;
            for (int i = 0; i < Templates.Length; i++)
            {
                string clue = string.Format(Templates[(start + i) % Templates.Length], roomName);
                if (b.knownClues.Contains(clue)) continue;

                b.knownClues.Add(clue);
                NPCEventBus.RaiseClueDiscovered(npc.Number, clue);
                Debug.Log($"[NPC {npc.Number}] 发现线索（{b.knownClues.Count}/{maxClues}）：{clue}");
                break;
            }

            return NodeResult.success;
        }
    }
}
