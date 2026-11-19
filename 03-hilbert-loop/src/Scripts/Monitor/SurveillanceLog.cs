using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HilbertLoop
{
    public enum SurveillanceEventKind
    {
        LeftRoom,
        EnteredLobby,
        LeftLobby,
        ReturnedRoom,
    }

    /// <summary>
    /// 圆桌监控的真实记录：只记录走廊与圆桌大厅里被拍到的人。
    ///
    /// - 采样玩家与所有 NPC 的 MonitorTracked 状态，房间内 / 窗外檐台都算“拍不到”；
    /// - 记录出房、进入大厅、离开大厅、回房，时间取自墙上的倒计时读数；
    /// - 会议公开回放、监控片段证据、NPC 的发言上下文全部从这里生成，
    ///   不再使用“1号 1:30 / 1:05”这种写死的剧本时间。
    /// </summary>
    public static class SurveillanceLog
    {
        public struct Entry
        {
            public int number;
            public int minute;
            public SurveillanceEventKind kind;
            public bool inLobby;
        }

        public struct Outing
        {
            public int number;
            public int leaveMinute;
            public int returnMinute;   // -1 = 直到最后都没回房
            public bool visitedLobby;
        }

        public struct Clip
        {
            public string id;
            public string name;
            public string reason;
            public int weight;
        }

        const float SampleInterval = 0.35f;
        const float LobbyHalfSize = 3.2f;

        static readonly List<Entry> _entries = new List<Entry>();
        static readonly Dictionary<int, bool> _outside = new Dictionary<int, bool>();
        static readonly Dictionary<int, bool> _lobby = new Dictionary<int, bool>();
        static float _timer;
        static int _loopIndex = int.MinValue;
        static bool _begun;
        static PlayerController _player;

        public static IReadOnlyList<Entry> Entries => _entries;
        public static bool HasData => _entries.Count > 0;

        /// <summary>每轮开始调用：清空上一轮记录，并记下开局时所有人所在的位置。</summary>
        public static void BeginCycle()
        {
            _entries.Clear();
            _outside.Clear();
            _lobby.Clear();
            _timer = 0f;
            _loopIndex = CycleState.loopIndex;
            _begun = true;
            _player = Object.FindObjectOfType<PlayerController>();
            Sample(true);
            Debug.Log("[SurveillanceLog] 本轮监控记录开始：只记录走廊与圆桌大厅。");
        }

        /// <summary>由 CycleController.Update 每帧调用。</summary>
        public static void Tick()
        {
            if (!_begun) return;
            if (CycleState.loopIndex != _loopIndex)
            {
                BeginCycle();
                return;
            }
            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase != GamePhase.Investigation)
                return;

            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = SampleInterval;
            Sample(false);
        }

        static void Sample(bool initial)
        {
            if (_player == null) _player = Object.FindObjectOfType<PlayerController>();

            int playerNumber = CycleState.playerNumber;
            if (_player != null && playerNumber > 0 && CycleState.playerAlive)
                Track(playerNumber, _player.GetComponent<MonitorTracked>(), _player.transform.position, initial);

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;
                Track(npc.Number, npc.GetComponent<MonitorTracked>(), npc.transform.position, initial);
            }
        }

        /// <summary>
        /// 监控镜头的实际覆盖范围：中央大厅、左右连接通道、左右环形走廊。
        /// 用几何范围正向判定，避免把窗外檐台 / 房间内部误算成“被拍到”。
        /// </summary>
        public static bool IsMonitored(Vector3 position)
        {
            // 中央大厅（地面 6.4×6.4，墙在 ±3.1）
            if (Mathf.Abs(position.x) <= 3.3f && Mathf.Abs(position.z) <= 3.3f) return true;

            // 大厅 ↔ 左右环的连接通道（x 3.1~4.9，宽 3 米）
            float ax = Mathf.Abs(position.x);
            if (ax >= 3.1f && ax <= 5.0f && Mathf.Abs(position.z) <= 1.6f) return true;

            // 左右环形走廊（中心 ±12，环带 4.5~7.5 米）
            for (int sx = -1; sx <= 1; sx += 2)
            {
                float dx = position.x - sx * 12f;
                float dz = position.z;
                float r = Mathf.Sqrt(dx * dx + dz * dz);
                if (r >= 4.4f && r <= 7.6f) return true;
            }
            return false;
        }

        static void Track(int number, MonitorTracked tracked, Vector3 position, bool initial)
        {
            if (tracked == null) return;

            // 只有真正站在走廊 / 连接通道 / 圆桌大厅里的人才算被拍到。
            // 房间内部、门外檐台、窗外的绳索路线都不在镜头覆盖范围。
            bool onLedge = RoomArea.IsOutsideAnyRoom(position);
            bool visible = !tracked.IsInRoom && !onLedge && IsMonitored(position);
            bool inLobby = visible
                && Mathf.Abs(position.x) <= LobbyHalfSize
                && Mathf.Abs(position.z) <= LobbyHalfSize;

            bool wasVisible = _outside.TryGetValue(number, out bool prevVisible) && prevVisible;
            bool wasLobby = _lobby.TryGetValue(number, out bool prevLobby) && prevLobby;

            if (!initial)
            {
                if (!wasVisible && visible)
                    Record(number, SurveillanceEventKind.LeftRoom, inLobby);
                else if (wasVisible && !visible)
                    Record(number, SurveillanceEventKind.ReturnedRoom, false);
                else if (visible)
                {
                    if (!wasLobby && inLobby)
                        Record(number, SurveillanceEventKind.EnteredLobby, true);
                    else if (wasLobby && !inLobby)
                        Record(number, SurveillanceEventKind.LeftLobby, false);
                }
            }

            _outside[number] = visible;
            _lobby[number] = inLobby;
        }

        static void Record(int number, SurveillanceEventKind kind, bool inLobby)
        {
            int minute = CurrentMinute();
            if (minute < 0) return;

            Entry entry = new Entry
            {
                number = number,
                minute = minute,
                kind = kind,
                inLobby = inLobby,
            };
            _entries.Add(entry);

            Debug.Log($"[SurveillanceLog] {LoopTimeline.FormatMinute(minute)} {number}号 " +
                      DescribeKind(kind) + (inLobby ? "（圆桌大厅）" : ""));
        }

        static string DescribeKind(SurveillanceEventKind kind)
        {
            bool en = Localization.IsEnglish;
            switch (kind)
            {
                case SurveillanceEventKind.LeftRoom: return en ? "left the room" : "走出房间";
                case SurveillanceEventKind.EnteredLobby: return en ? "entered the lobby" : "进入圆桌大厅";
                case SurveillanceEventKind.LeftLobby: return en ? "left the lobby" : "离开圆桌大厅";
                case SurveillanceEventKind.ReturnedRoom: return en ? "returned to the room" : "回到房间";
                default: return en ? "appeared in the corridor" : "出现在走廊";
            }
        }

        static int CurrentMinute()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return -1;
            int minute = Mathf.CeilToInt(
                Mathf.Max(0f, gm.PhaseTimeRemaining) / LoopTimeline.SecondsPerGameMinute);
            return Mathf.Clamp(minute, 0, LoopTimeline.TotalGameMinutes);
        }

        /// <summary>把“出房 → 回房”合并成一次外出。</summary>
        public static List<Outing> BuildOutings()
        {
            var outings = new List<Outing>();
            var open = new Dictionary<int, int>();

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.kind == SurveillanceEventKind.LeftRoom)
                {
                    open[e.number] = outings.Count;
                    outings.Add(new Outing
                    {
                        number = e.number,
                        leaveMinute = e.minute,
                        returnMinute = -1,
                        visitedLobby = e.inLobby,
                    });
                }
                else if (e.kind == SurveillanceEventKind.ReturnedRoom)
                {
                    if (!open.TryGetValue(e.number, out int index)) continue;
                    Outing o = outings[index];
                    o.returnMinute = e.minute;
                    outings[index] = o;
                    open.Remove(e.number);
                }
                else if (e.kind == SurveillanceEventKind.EnteredLobby)
                {
                    if (!open.TryGetValue(e.number, out int index)) continue;
                    Outing o = outings[index];
                    o.visitedLobby = true;
                    outings[index] = o;
                }
            }

            // 同一分钟内的“回房又出房”多半是门口判定抖动，合并成一次连续外出。
            for (int i = outings.Count - 1; i > 0; i--)
            {
                Outing cur = outings[i];
                Outing prev = outings[i - 1];
                if (cur.number != prev.number) continue;
                if (prev.returnMinute < 0 || prev.returnMinute != cur.leaveMinute) continue;

                prev.returnMinute = cur.returnMinute;
                prev.visitedLobby = prev.visitedLobby || cur.visitedLobby;
                outings[i - 1] = prev;
                outings.RemoveAt(i);
            }

            return outings;
        }

        /// <summary>会议公开回放：按真实记录复述每个人被拍到的外出。</summary>
        public static string BuildReplayText()
        {
            bool en = Localization.IsEnglish;
            List<Outing> outings = BuildOutings();
            if (outings.Count == 0)
            {
                return en
                    ? "The corridor and lobby cameras did not record anyone leaving a room this loop; every door stayed shut. If someone did leave, they went through a window — the cameras cannot see outside."
                    : "这一轮监控里，走廊和圆桌大厅没有拍到任何人出房，所有房门都一直关着。" +
                      "如果有人离开过自己的房间，他走的是窗户——监控拍不到窗外。";
            }

            var sb = new StringBuilder();
            sb.Append(en
                ? "The cameras cover only the corridor and the lobby, not the rooms or the outside of the windows. In the replay: "
                : "监控只拍走廊和圆桌大厅，拍不到房间内部和窗外。回放里：");

            var numbers = new List<int>();
            for (int i = 0; i < outings.Count; i++)
            {
                if (!numbers.Contains(outings[i].number)) numbers.Add(outings[i].number);
            }
            numbers.Sort();

            bool firstNumber = true;
            for (int i = 0; i < numbers.Count; i++)
            {
                int number = numbers[i];
                if (!firstNumber) sb.Append(en ? "; " : "；");
                firstNumber = false;
                if (en) sb.Append("No. ").Append(number).Append(' ');
                else sb.Append(number).Append("号 ");

                bool first = true;
                for (int j = 0; j < outings.Count; j++)
                {
                    if (outings[j].number != number) continue;
                    if (!first) sb.Append(en ? ", " : "，");
                    first = false;
                    sb.Append(DescribeOuting(outings[j]));
                }
            }

            sb.Append(en
                ? ". Anyone the cameras missed may have stayed in their room — or left through a window."
                : "。没被拍到的人，可能一直待在房里，也可能从窗户出去了。");
            return sb.ToString();
        }

        /// <summary>给 NPC 模型看的监控时间线（比 BuildReplayText 更短）。</summary>
        public static string BuildPromptTimeline()
        {
            List<Outing> outings = BuildOutings();
            if (outings.Count == 0)
                return "公开监控（只拍走廊和大厅）：这一轮没有拍到任何人出房。";

            var sb = new StringBuilder();
            sb.Append("公开监控（只拍走廊和大厅，拍不到房间内部和窗外）：");

            var numbers = new List<int>();
            for (int i = 0; i < outings.Count; i++)
            {
                if (!numbers.Contains(outings[i].number)) numbers.Add(outings[i].number);
            }
            numbers.Sort();

            for (int i = 0; i < numbers.Count; i++)
            {
                int number = numbers[i];
                if (i > 0) sb.Append("；");
                sb.Append(number).Append("号 ");

                bool first = true;
                for (int j = 0; j < outings.Count; j++)
                {
                    if (outings[j].number != number) continue;
                    if (!first) sb.Append("，");
                    first = false;
                    sb.Append(DescribeOuting(outings[j]));
                }
            }

            sb.Append("。没被拍到的人，可能一直没出门，也可能从窗户离开。");
            return sb.ToString();
        }

        /// <summary>检查监控时拆出的可引用片段（每次外出一条）。</summary>
        public static List<Clip> BuildClips()
        {
            var clips = new List<Clip>();
            List<Outing> outings = BuildOutings();

            for (int i = 0; i < outings.Count; i++)
            {
                Outing o = outings[i];
                int duration = o.returnMinute >= 0 ? Mathf.Max(0, o.leaveMinute - o.returnMinute) : 0;

                var sb = new StringBuilder();
                sb.Append("黑白画面里，").Append(o.number).Append("号在")
                  .Append(LoopTimeline.FormatMinute(o.leaveMinute)).Append("推开房门");
                if (o.visitedLobby) sb.Append("，在圆桌大厅停留过");
                if (o.returnMinute >= 0)
                    sb.Append("，").Append(LoopTimeline.FormatMinute(o.returnMinute)).Append("回到房间");
                else
                    sb.Append("，之后一直没有被拍到回房");
                sb.Append("。");

                clips.Add(new Clip
                {
                    id = $"video_clip_{o.number}_{o.leaveMinute:000}",
                    name = $"监控：{o.number}号 {LoopTimeline.FormatMinute(o.leaveMinute)}出房",
                    reason = sb.ToString(),
                    weight = Mathf.Clamp(12 + (o.visitedLobby ? 6 : 0) +
                                         (o.returnMinute < 0 ? 10 : 0) + Mathf.Min(8, duration / 3),
                        10, 36),
                });
            }

            return clips;
        }

        /// <summary>监控里“出门次数多 / 在外面待得久”的人，供会议自动施加一点怀疑。</summary>
        public static List<int> BuildWatchList(int maxCount = 2)
        {
            var scores = new Dictionary<int, float>();
            List<Outing> outings = BuildOutings();
            for (int i = 0; i < outings.Count; i++)
            {
                Outing o = outings[i];
                int duration = o.returnMinute >= 0 ? Mathf.Max(0, o.leaveMinute - o.returnMinute) : 12;
                float score = 10f + (o.visitedLobby ? 2f : 0f) +
                              (o.returnMinute < 0 ? 6f : 0f) + Mathf.Min(6f, duration * 0.6f);
                scores.TryGetValue(o.number, out float old);
                scores[o.number] = old + score;
            }

            var ranked = new List<int>();
            foreach (KeyValuePair<int, float> pair in scores)
            {
                if (pair.Value < 18f) continue;
                ranked.Add(pair.Key);
            }
            ranked.Sort((a, b) => scores[b].CompareTo(scores[a]));
            if (ranked.Count > maxCount) ranked.RemoveRange(maxCount, ranked.Count - maxCount);
            return ranked;
        }

        public static string BuildEvidenceMomentText()
        {
            List<Outing> outings = BuildOutings();
            if (outings.Count == 0)
                return Localization.T("监控里这一轮没有任何人出房。走廊一直是空的，大厅的椅子也没人坐过。") +
                       (Localization.IsEnglish ? " " : "") +
                       Localization.T("可这不代表没人离开过房间——窗户外面是监控的盲区。");

            // 后半句是中文，整串直接拼进英文回放会导致英文模式漏出中文；
            // 这里按词条翻译，中文模式仍返回原文。
            return BuildReplayText() + Localization.T(" 从这些记录里，能对出谁在什么时候不在自己房里。");
        }

        public static int OutingCount(int number)
        {
            int count = 0;
            List<Outing> outings = BuildOutings();
            for (int i = 0; i < outings.Count; i++)
            {
                if (outings[i].number == number) count++;
            }
            return count;
        }

        /// <summary>某个号码被拍到的全部外出，供 NPC 当面追问时引用。</summary>
        public static string DescribeOutingsFor(int number)
        {
            List<Outing> outings = BuildOutings();
            var sb = new StringBuilder();
            for (int i = 0; i < outings.Count; i++)
            {
                if (outings[i].number != number) continue;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(DescribeOuting(outings[i]));
            }
            return sb.ToString();
        }

        /// <summary>某个号码最近一次被拍到的分钟读数，没有则返回空串。</summary>
        public static string LatestMinuteText(int number)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].number != number) continue;
                return LoopTimeline.FormatMinute(_entries[i].minute);
            }
            return "";
        }

        /// <summary>某个号码最近一次被拍到的行动，没有则返回空串。</summary>
        public static string DescribeLatest(int number)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                if (e.number != number) continue;
                return LoopTimeline.FormatMinute(e.minute) + " " + DescribeKind(e.kind) +
                       (e.inLobby ? (Localization.IsEnglish ? " (lobby)" : "（圆桌大厅）") : "");
            }
            return "";
        }

        static string DescribeOuting(Outing o)
        {
            bool en = Localization.IsEnglish;
            var sb = new StringBuilder();
            sb.Append(LoopTimeline.FormatMinute(o.leaveMinute));
            sb.Append(en ? " left the room" : "出房");
            if (o.visitedLobby) sb.Append(en ? ", visited the lobby" : "，去过圆桌大厅");
            if (o.returnMinute >= 0)
            {
                sb.Append(en ? ", returned at " : "，")
                  .Append(LoopTimeline.FormatMinute(o.returnMinute))
                  .Append(en ? "" : "回房");
            }
            else
            {
                sb.Append(en ? ", and was never filmed returning" : "，之后没再被拍到回房");
            }
            return sb.ToString();
        }
    }
}
