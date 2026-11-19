using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 道具来源账本（本轮有效）：
    /// 每个人的房间都是同一套工具，光说“我有一根绳子”分不出是谁的。
    /// 玩家真正需要的是“这根绳子取自几号房”——拾取时按所在房间记录，
    /// 会议 / 私聊的证据标签会带上这个出处。
    /// </summary>
    public static class ItemProvenance
    {
        static readonly Dictionary<string, List<int>> RoomsByItem =
            new Dictionary<string, List<int>>();

        public static void Record(string itemId, int roomNumber)
        {
            if (string.IsNullOrEmpty(itemId) || roomNumber <= 0) return;
            if (!RoomsByItem.TryGetValue(itemId, out List<int> rooms))
            {
                rooms = new List<int>();
                RoomsByItem[itemId] = rooms;
            }
            if (!rooms.Contains(roomNumber)) rooms.Add(roomNumber);
        }

        /// <summary>该物品第一个已知出处（用于“1号房的钉子”这类单点判定）。</summary>
        public static int GetRoom(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            return RoomsByItem.TryGetValue(itemId, out List<int> rooms) && rooms.Count > 0
                ? rooms[0]
                : 0;
        }

        /// <summary>
        /// 用于证据标签 / 台词：“取自2号房”。
        /// 同一种工具从多个房间拿过时，把出处全部列出，避免把两根绳子混成一根。
        /// </summary>
        public static string Describe(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return string.Empty;
            if (!RoomsByItem.TryGetValue(itemId, out List<int> rooms) || rooms.Count == 0)
                return string.Empty;

            bool en = Localization.IsEnglish;
            var sb = new System.Text.StringBuilder(en ? "Taken from Room " : "取自");
            for (int i = 0; i < rooms.Count; i++)
            {
                if (i > 0) sb.Append(en ? ", Room " : "、");
                sb.Append(rooms[i]);
            }
            if (!en) sb.Append("号房");
            return sb.ToString();
        }

        public static void Clear()
        {
            RoomsByItem.Clear();
        }

        /// <summary>沿父物体查找 Room_N（场景构建器把道具直接挂在房间节点下）。</summary>
        public static int RoomNumberFromTransform(Transform transform)
        {
            Transform t = transform;
            while (t != null)
            {
                if (t.name.StartsWith("Room_")
                    && int.TryParse(t.name.Substring(5), out int number) && number > 0)
                {
                    return number;
                }
                t = t.parent;
            }
            return 0;
        }
    }
}
