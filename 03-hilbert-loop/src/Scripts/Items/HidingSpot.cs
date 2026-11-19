using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 藏匿点（例如桌子 / 床底 / 尸体身上）：可以藏匿允许隐藏的物品，也可以取回。
    /// 一个藏匿点支持存放多件物品，藏 / 取由玩家自选（HUD 多行提示）：
    ///   [E]        藏匿当前选中的可藏物品；
    ///   [Q]        背包里有多件可藏物品时，循环切换要藏哪一件；
    ///   [1]..[N]   取回指定编号的已藏物品（编号按藏入顺序，从 1 起）；
    ///   [T]        全部取回（背包满时取到装不下为止，剩余留在原处）。
    /// R 键留给窗户拨锁 / 磁铁反锁，藏匿点不再占用。
    /// 藏匿的物品不会出现在背包里，但会以「物品 ID + 藏匿点」的形式被记录：
    /// 全部藏匿点在 OnEnable / OnDisable 时登记到静态注册表，
    /// 搜查阶段可通过 HidingSpot.FindSpotWithItem(itemId) 找回。
    /// </summary>
    public class HidingSpot : MonoBehaviour, IInteractable
    {
        [Tooltip("显示名，例如 床底")]
        public string displayName = "床底";

        [Tooltip("该藏匿点接受的隐藏位置类型")]
        public HideLocation allowedLocation = HideLocation.UnderBed;

        // 序列化：场景构建时预置的藏匿物（如 1 号床底用剩的钉子）需要随场景保存
        [SerializeField] List<string> _hidden = new List<string>();

        // 待藏物品选择（Q 键循环切换）：
        // _candidates 按「最近放入在前」收集背包中所有可藏物品，序号 0 = 原有默认行为（最后放入的那件）
        readonly List<string> _candidates = new List<string>();
        int _selectedIndex;

        /// <summary>场景中全部藏匿点（静态注册表，按登记顺序）。</summary>
        static readonly List<HidingSpot> Registry = new List<HidingSpot>();

        public static IReadOnlyList<HidingSpot> AllSpots => Registry;

        /// <summary>查找藏有指定物品 id 的藏匿点（找不到返回 null）。</summary>
        public static HidingSpot FindSpotWithItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            for (int i = 0; i < Registry.Count; i++)
            {
                if (Registry[i] != null && Registry[i].Contains(itemId)) return Registry[i];
            }
            return null;
        }

        void OnEnable()
        {
            if (!Registry.Contains(this)) Registry.Add(this);
        }

        void OnDisable()
        {
            Registry.Remove(this);
        }

        /// <summary>本藏匿点当前藏着的物品 id 列表（只读）。</summary>
        public IReadOnlyList<string> HiddenItemIds => _hidden;

        public bool HasHiddenItems => _hidden.Count > 0;

        public bool Contains(string itemId)
        {
            return !string.IsNullOrEmpty(itemId) && _hidden.Contains(itemId);
        }

        /// <summary>预置一件藏匿物（场景构建时用：1 号房床底早已藏着用剩的钉子）。</summary>
        public void Prestore(string itemId)
        {
            if (!string.IsNullOrEmpty(itemId)) _hidden.Add(itemId);
        }

        /// <summary>3.5 轮回重置：把藏匿物恢复成场景初始快照。</summary>
        public void ResetHiddenItems(System.Collections.Generic.IEnumerable<string> itemIds)
        {
            _hidden.Clear();
            _selectedIndex = 0;
            if (itemIds == null) return;
            foreach (string id in itemIds)
            {
                if (!string.IsNullOrEmpty(id)) _hidden.Add(id);
            }
        }

        public virtual string GetInteractionPrompt(PlayerController player)
        {
            // 多行提示：以 "[" 开头（自带按键说明），InteractionSystem 不再补 "[E] " 前缀
            var lines = new List<string>();

            CollectCandidates(player);
            if (_candidates.Count > 0)
            {
                string name = ItemDatabase.Instance.GetDisplayName(_candidates[_selectedIndex]);
                string selectHint = _candidates.Count > 1
                    ? $"（{_selectedIndex + 1}/{_candidates.Count}，Q 切换）"
                    : "";
                lines.Add($"[E] 藏匿：{name}{selectHint}");
            }

            if (_hidden.Count == 1)
            {
                lines.Add($"[1] 取出：{ItemDatabase.Instance.GetDisplayName(_hidden[0])}");
            }
            else if (_hidden.Count > 1)
            {
                var parts = new List<string>();
                for (int i = 0; i < _hidden.Count; i++)
                    parts.Add($"[{i + 1}] 取出：{ItemDatabase.Instance.GetDisplayName(_hidden[i])}");
                parts.Add("[T] 全部取出");
                lines.Add(string.Join("　", parts));
            }

            return string.Join("\n", lines);
        }

        public virtual void Interact(PlayerController player)
        {
            if (player == null || player.inventory == null) return;

            // E 键 = 藏匿当前选中的可藏物品（Q 切换选择；取出请用数字键 / T）
            CollectCandidates(player);
            if (_candidates.Count == 0)
            {
                player.ShowMessage($"你手上没有能藏进{displayName}的物品。");
                return;
            }

            string toHide = _candidates[_selectedIndex];
            player.inventory.Remove(toHide);
            _hidden.Add(toHide);
            player.ShowMessage($"你把{ItemDatabase.Instance.GetDisplayName(toHide)}藏到了{displayName}。");

            // 模块三 3.3：藏匿动作可被附近 NPC 看见（视觉感知）
            PlayerActionSignals.NotifyHiding();

            // 模块三 3.2：藏匿行为计入 8号行为记录（caution+10 / aggression-5）
            PlayerBehaviorRecorder.Instance?.RecordEvent(PlayerBehaviorRecorder.EventHidItem);
            // 预留：可在此通知监控 / 搜证系统（藏匿物品不算被携带）
        }

        /// <summary>循环切换当前待藏物品（Q 键）：背包里有多件可藏物品时才有效。</summary>
        public void CycleSelection(PlayerController player)
        {
            CollectCandidates(player);
            if (_candidates.Count < 2) return;
            _selectedIndex = (_selectedIndex + 1) % _candidates.Count;
        }

        /// <summary>
        /// 收集背包中所有可藏进本处的物品到 _candidates（最近放入的在前；
        /// 序号 0 = 原有默认选择：最后放入的那件），并修正选中序号。
        /// </summary>
        void CollectCandidates(PlayerController player)
        {
            _candidates.Clear();
            if (player != null && player.inventory != null)
            {
                IReadOnlyList<string> items = player.inventory.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    ItemDefinition def = GetDefinition(items[i]);
                    if (def != null && def.canDiscard && (def.allowedHiding & allowedLocation) != 0)
                        _candidates.Add(items[i]);
                }
            }

            if (_candidates.Count == 0) _selectedIndex = 0;
            else _selectedIndex = Mathf.Clamp(_selectedIndex, 0, _candidates.Count - 1);
        }

        static ItemDefinition GetDefinition(string itemId)
        {
            ItemDatabase db = ItemDatabase.Instance;
            if (db == null) return null;

            ItemDefinition def;
            return db.TryGet(itemId, out def) ? def : null;
        }

        /// <summary>取回编号对应的已藏物品（编号与提示中的 [1]..[N] 一致，从 1 起）。</summary>
        public void TakeByNumber(int number, PlayerController player)
        {
            if (player == null || player.inventory == null) return;

            int index = number - 1;
            if (index < 0 || index >= _hidden.Count) return;

            string id = _hidden[index];
            if (!player.inventory.Add(id))
            {
                player.ShowMessage("背包已经满了。");
                return;
            }

            _hidden.RemoveAt(index);
            player.ShowMessage($"你从{displayName}取回了{ItemDatabase.Instance.GetDisplayName(id)}。");
        }

        /// <summary>全部取回（背包满时取到装不下为止，剩余留在原处）。</summary>
        public void TakeAll(PlayerController player)
        {
            if (player == null || player.inventory == null) return;
            if (_hidden.Count == 0) return;

            int taken = 0;
            while (_hidden.Count > 0 && player.inventory.Add(_hidden[0]))
            {
                _hidden.RemoveAt(0);
                taken++;
            }

            if (taken == 0)
            {
                player.ShowMessage("背包已经满了。");
                return;
            }

            string remain = _hidden.Count > 0 ? $"（背包已满，还剩 {_hidden.Count} 件）" : "";
            player.ShowMessage($"你从{displayName}取回了 {taken} 件物品{remain}。");
        }
    }
}
