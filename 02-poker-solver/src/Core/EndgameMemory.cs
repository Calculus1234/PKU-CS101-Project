using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PokerSolver
{
    /// <summary>
    /// 残局记忆库：把解过的局面存下来，下次精确命中直接给结论。
    ///
    /// 边界：只做「完全相同局面」的复用。相似局面绝不复用 ——
    /// 斗地主里差一张牌结论就可能相反，泛化就等于编答案。
    ///
    /// 存储：一个纯文本文件，一行一条（不依赖任何 JSON 库）。
    ///   my|opp|lead|prev|win|firstType|firstRank|firstLen|firstCards|nodes|ms|hits|date
    /// 手牌和牌都用 ulong 的十六进制表示。几十条记录只有几 KB。
    /// </summary>
    public static class EndgameMemory
    {
        public struct Entry
        {
            public bool Win;
            public Move First;
            public long Nodes;
            public double Ms;
            public int Hits;        // 被命中次数
            public string SolvedAt; // yyyy-MM-dd
            /// <summary>完整推演主线（命中时直接展示，不用重新搜索）。</summary>
            public List<string> Line;
        }

        private static Dictionary<string, Entry> _mem;
        private static string _path;
        private static bool _dirty;

        public static int Count { get { EnsureLoaded(); return _mem.Count; } }

        private static void EnsureLoaded()
        {
            if (_mem != null) return;
            _mem = new Dictionary<string, Entry>(256);
            if (!string.IsNullOrEmpty(_path)) LoadFrom(_path);
        }

        /// <summary>指定持久化路径（Unity 里传 Application.persistentDataPath 下的文件）。</summary>
        public static void SetPath(string path)
        {
            if (_path == path) return;
            _path = path;
            _mem = null;
            _dirty = false;
            EnsureLoaded();
        }

        private static string Key(ulong my, ulong opp, bool lead, Move prev)
        {
            int prevKey = ((int)prev.Type) | (((int)prev.Rank) << 8) | (((int)prev.Length) << 16);
            return my.ToString("X") + "|" + opp.ToString("X") + "|" + (lead ? "1" : "0") + "|" + prevKey.ToString("X");
        }

        public static bool TryGet(ulong my, ulong opp, bool lead, Move prev, out Entry entry)
        {
            EnsureLoaded();
            string k = Key(my, opp, lead, prev);
            if (_mem.TryGetValue(k, out entry))
            {
                entry.Hits++;              // 命中计数
                _mem[k] = entry;
                _dirty = true;
                return true;
            }
            entry = default(Entry);
            return false;
        }

        public static void Put(ulong my, ulong opp, bool lead, Move prev, bool win, Move first,
            long nodes, double ms, List<string> line)
        {
            EnsureLoaded();
            string k = Key(my, opp, lead, prev);
            Entry e;
            if (!_mem.TryGetValue(k, out e)) e = default(Entry);
            e.Win = win;
            e.First = first;
            e.Nodes = nodes;
            e.Ms = ms;
            e.SolvedAt = DateTime.Now.ToString("yyyy-MM-dd");
            e.Line = line != null ? new List<string>(line) : new List<string>();
            _mem[k] = e;
            _dirty = true;
        }

        public static bool Dirty { get { return _dirty; } }

        public static void Save()
        {
            if (_mem == null || string.IsNullOrEmpty(_path)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# PokerSolver endgame memory v1");
                foreach (KeyValuePair<string, Entry> kv in _mem)
                {
                    Entry e = kv.Value;
                    sb.Append(kv.Key).Append('|')
                      .Append(e.Win ? '1' : '0').Append('|')
                      .Append((int)e.First.Type).Append('|')
                      .Append(e.First.Rank).Append('|')
                      .Append(e.First.Length).Append('|')
                      .Append(e.First.Cards.ToString("X")).Append('|')
                      .Append(e.Nodes).Append('|')
                      .Append(e.Ms.ToString("F0", CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.Hits).Append('|')
                      .Append(e.SolvedAt ?? "").Append('|')
                      // 推演线用 <0x1> 连接，避免和列分隔符冲突
                      .Append(e.Line != null ? string.Join("\u0001", e.Line.ToArray()) : "")
                      .AppendLine();
                }

                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
                _dirty = false;
            }
            catch
            {
                // 记忆库写失败不影响求解
            }
        }

        private static void LoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 12) continue;

                    string k = parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + parts[3];
                    Entry e = new Entry();
                    e.Win = parts[4] == "1";

                    Move m = new Move();
                    m.Type = (MoveType)int.Parse(parts[5], CultureInfo.InvariantCulture);
                    m.Rank = int.Parse(parts[6], CultureInfo.InvariantCulture);
                    m.Length = int.Parse(parts[7], CultureInfo.InvariantCulture);
                    m.Cards = ulong.Parse(parts[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    e.First = m;

                    long nodes; long.TryParse(parts[9], out nodes); e.Nodes = nodes;
                    double ms; double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out ms); e.Ms = ms;
                    int hits; int.TryParse(parts[11], out hits); e.Hits = hits;
                    e.SolvedAt = parts.Length > 12 ? parts[12] : "";
                    e.Line = new List<string>();
                    if (parts.Length > 13 && !string.IsNullOrEmpty(parts[13]))
                        e.Line.AddRange(parts[13].Split('\u0001'));
                    _mem[k] = e;
                }
            }
            catch
            {
                // 文件损坏就当空库
                _mem.Clear();
            }
        }
    }
}
