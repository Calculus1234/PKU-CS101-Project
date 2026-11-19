using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 每轮开局的「名册认知」。
    ///
    /// 规则：
    /// - 开局时，所有人都知道本轮名册上最大的号码 max，并默认 1..max 号都还在；
    ///   max 可能已经是一具尸体（例如 9 号），但「他参与过本轮」这件事所有人都知道。
    /// - 实际人口（例如只剩两个人）不会直接告诉任何人，只有走进空房间才会逐步修正。
    /// - 纸条由「号码最大、且还活着的参与者」持有（9 号除外：9 号要么是尸体，
    ///   要么是正睡在床上的既定受害者，纸条对他没有意义）。
    ///
    /// 这张表只影响认知 / 台词 / 调试，不直接限制 NPC 的既定行为：
    /// 例如 1 号即便面对「只剩 1 和 9」的局面，仍会按既定流程去杀 9 号，
    /// 因为在他的认知里，这栋房子还住着 1..9 号。
    /// </summary>
    public static class RoundKnowledge
    {
        /// <summary>本轮名册声明的最大号码（含已死者，1~9）。</summary>
        public static int MaxLivingNumber { get; private set; }

        /// <summary>本轮开局时实际存活的参与者数量（调试用，不直接暴露给 NPC）。</summary>
        public static int RoundStartLivingCount { get; private set; }

        /// <summary>玩家开局认知里的最大号码；非最大号时玩家也知道这个数字。</summary>
        public static int PlayerKnownMaxNumber { get; private set; }

        /// <summary>纸条持有者：号码最大且还活着的参与者（9 号除外）。</summary>
        public static int NoteHolderNumber { get; private set; }

        static readonly HashSet<int> PlayerDiscoveredEmptyRooms = new HashSet<int>();
        static readonly HashSet<int> PlayerSeenResidentOutRooms = new HashSet<int>();

        /// <summary>取场上最大的存活号码（玩家 + 全部存活 NPC）。</summary>
        public static int GetHighestLivingNumber()
        {
            int max = CycleState.playerAlive && CycleState.playerNumber > 0
                ? CycleState.playerNumber
                : 0;

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;
                if (npc.Number > max) max = npc.Number;
            }

            return max;
        }

        /// <summary>取号码最大、且还活着的参与者，9 号除外（9 号是既定受害者，不持有纸条）。</summary>
        public static int GetNoteHolderNumber()
        {
            int max = CycleState.playerAlive && CycleState.playerNumber > 0
                      && CycleState.playerNumber != 9
                ? CycleState.playerNumber
                : 0;

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.Number == 9) continue;
                if (npc.Number > max) max = npc.Number;
            }

            return max;
        }

        /// <summary>
        /// 开局 / 轮回重置时调用：写入本轮声明的最大号码、每个 NPC 的认知，并分配纸条。
        /// 必须在 NPC 已生成、玩家已落地之后调用。
        /// </summary>
        public static void BeginRound(string reason)
        {
            BeginRound(CycleState.currentKnownMax > 0
                ? CycleState.currentKnownMax
                : GetHighestLivingNumber(), reason);
        }

        /// <summary>指定 max 的版本，供玩家落地时复用同一次计算结果。</summary>
        public static void BeginRound(int knownMax, string reason)
        {
            MaxLivingNumber = Mathf.Clamp(knownMax, 0, CycleState.MaxLoopNumber);
            PlayerKnownMaxNumber = MaxLivingNumber;
            RoundStartLivingCount = CycleState.GetLivingParticipantCount();
            NoteHolderNumber = GetNoteHolderNumber();
            PlayerDiscoveredEmptyRooms.Clear();
            PlayerSeenResidentOutRooms.Clear();

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;

                npc.blackboard.knownMaxLivingNumber = MaxLivingNumber;
                npc.blackboard.assumedPopulation = MaxLivingNumber;
                if (npc.blackboard.discoveredEmptyRooms == null)
                    npc.blackboard.discoveredEmptyRooms = new List<int>();
                else
                    npc.blackboard.discoveredEmptyRooms.Clear();

                // 纸条交给「号码最大、还活着」的那个人；9 号不持有纸条。
                if (npc.Number == NoteHolderNumber && npc.Number != 9 && !npc.blackboard.hasNote)
                {
                    npc.blackboard.hasNote = true;
                    npc.blackboard.noteTrusted = true;
                    npc.blackboard.noteProcessed = false;
                    Debug.Log($"[RoundKnowledge] 本轮号码最大的活人是 {npc.Number} 号：纸条交给它。");
                }
            }

            Debug.Log($"[RoundKnowledge] {reason}：本轮最大号码={MaxLivingNumber}，" +
                      $"实际存活={RoundStartLivingCount}，纸条持有者=" +
                      $"{(NoteHolderNumber > 0 ? NoteHolderNumber + "号" : "无")}。" +
                      "所有人只知道 max，并默认 1..max 都还在。");
        }

        /// <summary>NPC 进入房间：如果房间空着，就发现「这一轮没住满」。</summary>
        public static void NotifyNpcEnteredRoom(NPCController npc, RoomArea area)
        {
            if (npc == null || area == null || npc.blackboard == null) return;
            if (!npc.IsAwake || npc.isDead) return;

            NPCBlackboard bb = npc.blackboard;
            if (bb.knownMaxLivingNumber <= 0) return;

            int roomNumber = ParseRoomNumber(area.roomName);
            if (roomNumber <= 0) return;
            if (roomNumber > bb.knownMaxLivingNumber && roomNumber != 9) return;
            if (roomNumber == npc.Number) return;

            // 进了别人的房间，就会看见桌上那套和自房间一样的道具。
            // 9号房是例外：桌上只有匕首和磁铁，窗台上还有一块神秘磁铁。
            if (roomNumber >= 1 && roomNumber <= 8)
            {
                if (!bb.knownClues.Contains("room_items_identical"))
                {
                    bb.knownClues.Add("room_items_identical");
                    Debug.Log($"[NPC {npc.Number}] 在 {roomNumber} 号房看见了和自己房间一模一样的" +
                              "长绳、锤子、匕首、磁铁。");
                }
            }
            else if (roomNumber == 9)
            {
                if (!bb.knownClues.Contains("ninth_room_items"))
                {
                    bb.knownClues.Add("ninth_room_items");
                    Debug.Log($"[NPC {npc.Number}] 在 9 号房看见了桌上的匕首、磁铁和窗台上的神秘磁铁。");
                }
            }

            if (roomNumber > 8) return; // 9号房是案发现场，不参与“这一轮住没住满”的人口推算

            if (bb.discoveredEmptyRooms == null) bb.discoveredEmptyRooms = new List<int>();
            if (bb.discoveredEmptyRooms.Contains(roomNumber)) return;

            // 房间里还有别的活人（包括睡在床上的人）时，不算空房。
            if (IsRoomOccupiedByOtherNpc(area, npc)) return;
            if (IsPlayerInRoom(area)) return;

            // 住户还活着、只是出门了：房间不是「空房」，不修正人口认知。
            if (IsRoomResidentAlive(roomNumber)) return;

            bb.discoveredEmptyRooms.Add(roomNumber);
            bb.assumedPopulation = Mathf.Max(2,
                bb.knownMaxLivingNumber - bb.discoveredEmptyRooms.Count);

            Debug.Log($"[NPC {npc.Number}] 发现 {roomNumber} 号房是空的：" +
                      $"原以为有 {bb.knownMaxLivingNumber} 人，现在估计还有 {bb.assumedPopulation} 人。");
        }

        /// <summary>玩家进入房间：空房会给玩家一次提示，并修正玩家对人口的估计。</summary>
        public static void NotifyPlayerEnteredRoom(PlayerController player, RoomArea area)
        {
            if (player == null || area == null || PlayerKnownMaxNumber <= 0) return;

            int roomNumber = ParseRoomNumber(area.roomName);
            int playerNumber = CycleState.playerNumber;
            if (roomNumber <= 0 || roomNumber > PlayerKnownMaxNumber) return;
            if (roomNumber == playerNumber) return;
            if (PlayerDiscoveredEmptyRooms.Contains(roomNumber)) return;
            if (IsRoomOccupiedByNpc(area)) return;

            // 住户活着但不在房间：只提示「人出去了」，绝不能判定成没人住，
            // 否则 1 号明明活着、只是离开了一会儿，玩家就会看到「这一轮并没有住满」。
            if (IsRoomResidentAlive(roomNumber))
            {
                if (PlayerSeenResidentOutRooms.Add(roomNumber))
                {
                    player.ShowMessage($"{roomNumber}号房里暂时没有人，" +
                        $"{roomNumber}号的床铺还留着余温——只是出去了。", 3.5f);
                    Debug.Log($"[RoundKnowledge] 玩家进入 {roomNumber} 号房：住户仍存活，只是外出。");
                }
                return;
            }

            PlayerDiscoveredEmptyRooms.Add(roomNumber);
            int assumed = Mathf.Max(2, PlayerKnownMaxNumber - PlayerDiscoveredEmptyRooms.Count);

            string text = $"{roomNumber}号房里没有人，床铺是冷的。";
            if (assumed <= 2)
                text += "你数了数这些空房——这一轮恐怕只剩两个人。";
            else
                text += "这一轮并没有住满。";

            player.ShowMessage(text, 4.5f);
            Debug.Log($"[RoundKnowledge] 玩家发现 {roomNumber} 号房是空的：" +
                      $"开局以为有 {PlayerKnownMaxNumber} 人，现在估计还有 {assumed} 人。");
        }

        static bool IsRoomOccupiedByOtherNpc(RoomArea area, NPCController self)
        {
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController other = NPCController.All[i];
                if (other == null || other == self || other.isDead) continue;
                if (other.currentRoomName == area.roomName) return true;

                MonitorTracked tracked = other.GetComponent<MonitorTracked>();
                if (tracked != null && tracked.CurrentRoom == area) return true;
            }
            return false;
        }

        /// <summary>
        /// 房间的登记住户是否还活着（号码即房间号；玩家也占一个号码）。
        /// 用于区分「房间空着没人住」与「住户只是出门了」。
        /// </summary>
        static bool IsRoomResidentAlive(int roomNumber)
        {
            if (roomNumber <= 0) return false;
            if (CycleState.playerAlive && CycleState.playerNumber == roomNumber) return true;

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && npc.Number == roomNumber) return true;
            }
            return false;
        }

        static bool IsRoomOccupiedByNpc(RoomArea area)
        {
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;
                if (npc.currentRoomName == area.roomName) return true;

                MonitorTracked tracked = npc.GetComponent<MonitorTracked>();
                if (tracked != null && tracked.CurrentRoom == area) return true;
            }
            return false;
        }

        static bool IsPlayerInRoom(RoomArea area)
        {
            PlayerController player = Object.FindObjectOfType<PlayerController>();
            if (player == null) return false;

            MonitorTracked tracked = player.GetComponent<MonitorTracked>();
            return tracked != null && tracked.CurrentRoom == area;
        }

        static int ParseRoomNumber(string roomName)
        {
            if (string.IsNullOrEmpty(roomName)) return 0;
            string digits = roomName.Replace("号房", "").Trim();
            return int.TryParse(digits, out int number) ? number : 0;
        }
    }
}
