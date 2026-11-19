using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 世界证据账本（本轮有效）：
    /// - 有些证据不是背包里的实物，而是“玩家看过 / 检查过 / 对比过”的认知：
    ///   圆桌监控回放、外墙钉痕、9号尸体、两张相同的脸；
    /// - 会议 / 私聊的证据面板同时读取背包与这本账本，避免“看见了却拿不出来”；
    /// - 每轮天亮由 CycleState.ResetLoopSocialState 清空。
    /// </summary>
    public sealed class EvidenceEntry
    {
        public string id;
        public string displayName;
        public string reason;
        public int weight;
        public bool core;
        /// <summary>证据从哪来：几号房窗外 / 圆桌监控 / 谁的尸体。空串表示未知。</summary>
        public string origin;

        public ItemDefinition ToDefinition()
        {
            return new ItemDefinition
            {
                id = id,
                displayName = displayName,
                category = ItemCategory.Note,
                description = reason,
                canBeMeetingEvidence = true,
                evidenceWeight = weight,
                evidenceReason = reason,
                canDiscard = false,
                allowedHiding = HideLocation.None,
                placeholderColor = new Color(0.85f, 0.82f, 0.68f),
            };
        }
    }

    public static class EvidenceLedger
    {
        static readonly List<EvidenceEntry> _entries = new List<EvidenceEntry>();
        static readonly Dictionary<string, EvidenceEntry> _map = new Dictionary<string, EvidenceEntry>();

        public static IReadOnlyList<EvidenceEntry> Entries => _entries;
        public static int Count => _entries.Count;

        public static void Reset()
        {
            _entries.Clear();
            _map.Clear();
        }

        public static bool Has(string id)
        {
            return !string.IsNullOrEmpty(id) && _map.ContainsKey(id);
        }

        public static bool Record(string id, string displayName, string reason, int weight,
            string origin = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (_map.ContainsKey(id)) return false;

            var entry = new EvidenceEntry
            {
                id = id,
                displayName = string.IsNullOrEmpty(displayName) ? id : displayName,
                reason = reason ?? string.Empty,
                weight = Mathf.Clamp(weight, 0, 100),
                core = weight >= CycleState.CoreEvidenceWeightThreshold,
                origin = origin ?? string.Empty,
            };

            _map[id] = entry;
            _entries.Add(entry);

            if (entry.core) CycleState.RecordCoreEvidence(entry.id, entry.weight);
            Debug.Log($"[Evidence] 记录证据：{entry.displayName}（{entry.id}，权重 {entry.weight}）。");
            return true;
        }

        /// <summary>把账本里的证据 id 追加到列表（去重，按权重从高到低，最多 max 条）。</summary>
        public static void AppendIds(List<string> ids, int max)
        {
            if (ids == null) return;

            var ordered = new List<EvidenceEntry>(_entries);
            ordered.Sort((a, b) => b.weight.CompareTo(a.weight));
            for (int i = 0; i < ordered.Count && ids.Count < max; i++)
            {
                if (!ids.Contains(ordered[i].id)) ids.Add(ordered[i].id);
            }
        }

        /// <summary>优先返回物品数据库定义；账本证据返回运行时定义。</summary>
        public static ItemDefinition Resolve(string id)
        {
            ItemDatabase db = ItemDatabase.Instance;
            ItemDefinition def = db != null ? db.Get(id) : null;
            if (def != null) return def;

            if (!string.IsNullOrEmpty(id) && _map.TryGetValue(id, out EvidenceEntry entry))
                return entry.ToDefinition();
            return null;
        }

        public static string GetDisplayName(string id)
        {
            ItemDefinition def = Resolve(id);
            return def != null ? def.displayName : id;
        }

        /// <summary>证据出处（世界证据写在账本里；实物回退到拾取来源）。</summary>
        public static string GetOrigin(string id)
        {
            if (!string.IsNullOrEmpty(id) && _map.TryGetValue(id, out EvidenceEntry entry)
                && !string.IsNullOrEmpty(entry.origin))
                return entry.origin;
            return ItemProvenance.Describe(id);
        }
    }
}
