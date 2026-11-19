using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 背包系统：按物品 ID 保存携带物（容量固定），HUD 左下角显示当前携带。
    /// 提供丢弃、藏匿检索、物品使用入口与武器袭击判定。
    /// </summary>
    public class InventorySystem : MonoBehaviour
    {
        [Header("配置")]
        public int capacity = 9;
        public List<string> initialItemIds = new List<string>();

        [Header("HUD（由场景构建器装配）")]
        public TextMeshProUGUI bagText;

        readonly List<string> _items = new List<string>();
        PlayerController _player;
        bool _initialized;

        public event Action OnInventoryChanged;

        public IReadOnlyList<string> Items => _items;
        public int Count => _items.Count;
        public bool IsFull => _items.Count >= capacity;

        void Awake()
        {
            _player = GetComponentInParent<PlayerController>();
        }

        void Start()
        {
            ChineseFontUtility.Apply(bagText);

            // CycleController 可能先于本组件 Start 按角色重置了背包；此时不要重复叠加初始物品。
            if (_initialized)
            {
                RefreshBagText();
                return;
            }

            ResetTo(initialItemIds);
        }

        /// <summary>3.5 轮回重置：清空背包并按新角色的初始装备重新填充。</summary>
        public void ResetTo(IEnumerable<string> itemIds)
        {
            _initialized = true;
            _items.Clear();
            if (itemIds != null)
            {
                foreach (string id in itemIds)
                {
                    if (!string.IsNullOrEmpty(id) && _items.Count < capacity) _items.Add(id);
                }
            }
            NotifyChanged();
        }

        public bool Add(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            if (IsFull) return false;

            _items.Add(itemId);
            NotifyChanged();
            return true;
        }

        public bool Remove(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;

            int index = _items.IndexOf(itemId);
            if (index < 0) return false;

            _items.RemoveAt(index);
            NotifyChanged();
            return true;
        }

        public bool Has(string itemId)
        {
            return !string.IsNullOrEmpty(itemId) && _items.Contains(itemId);
        }

        /// <summary>统计携带的同名物品数量（长钉等可消耗工具用于显示剩余数量）。</summary>
        public int CountOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int count = 0;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i] == itemId) count++;
            return count;
        }

        /// <summary>查找某个藏匿点可以收容的物品（从后往前找第一个符合的，不可摘除的物品除外）。</summary>
        public string FindHideableItem(HideLocation location)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ItemDefinition def = GetDefinition(_items[i]);
                if (def != null && def.canDiscard && (def.allowedHiding & location) != 0) return _items[i];
            }
            return null;
        }

        /// <summary>丢弃最后放入的可丢弃物品（对应 G 键；面具/斗篷等 canDiscard=false 的物品不会被丢下）。</summary>
        public bool DropLastItem(Vector3 worldPosition)
        {
            int index = FindLastDiscardableIndex();
            if (index < 0)
            {
                if (_player != null) _player.ShowMessage("你手上没有可以丢下的东西。");
                return false;
            }

            string itemId = _items[index];
            _items.RemoveAt(index);
            // 保留出处：丢下再捡起，证据标签仍然写着“取自几号房”。
            ItemPickup.SpawnWorldItem(itemId, worldPosition, null, ItemProvenance.GetRoom(itemId));
            NotifyChanged();

            if (_player != null) _player.ShowMessage($"你丢下了{ItemDatabase.Instance.GetDisplayName(itemId)}。");
            return true;
        }

        int FindLastDiscardableIndex()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ItemDefinition def = GetDefinition(_items[i]);
                if (def == null || def.canDiscard) return i;
            }
            return -1;
        }

        /// <summary>查找背包中的最后一件武器（找不到返回 null）。</summary>
        public ItemDefinition GetLastWeapon()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ItemDefinition def = GetDefinition(_items[i]);
                if (def != null && def.category == ItemCategory.Weapon) return def;
            }
            return null;
        }

        /// <summary>
        /// 用背包中的武器袭击目标：按武器 killChance 掷骰，
        /// 成功调用 ApplyKill，失败调用 ApplyKillAttemptFailed（失败会惊动其他人）。
        /// 返回值表示是否执行了袭击（背包里没有武器时返回 false）。
        /// </summary>
        public bool UseWeaponOn(IKillable target)
        {
            if (target == null) return false;

            ItemDefinition weapon = GetLastWeapon();
            if (weapon == null) return false;

            // 模块三 3.2：使用武器计入 8号行为记录（aggression+15 / caution-5）
            PlayerBehaviorRecorder.Instance?.RecordEvent(PlayerBehaviorRecorder.EventUsedWeapon);

            // 外观音效：锤击 / 匕首刺入。
            if (SFXManager.Instance != null) SFXManager.Instance.PlayWeapon(weapon.id);

            // 模块三 3.3：战斗声音 + 简易攻击反馈（屏幕震动）
            if (_player != null)
            {
                SoundEventManager.Emit(SoundEventType.Combat, _player.transform.position + Vector3.up,
                    1f, _player.gameObject);
                _player.ShakeCamera(0.22f, 0.05f);
            }

            bool success = UnityEngine.Random.value < weapon.killChance;
            if (success) target.ApplyKill(_player, weapon);
            else target.ApplyKillAttemptFailed(_player, weapon);
            return true;
        }

        /// <summary>
        /// 通用物品使用入口（预留）：当前版本中物品效果由交互对象
        /// （窗户 / 门 / NPC 等）自行判定，这里作为后续主动使用道具的统一入口。
        /// </summary>
        public bool UseItem(string itemId)
        {
            return Has(itemId);
        }

        static ItemDefinition GetDefinition(string itemId)
        {
            ItemDatabase db = ItemDatabase.Instance;
            if (db == null) return null;

            ItemDefinition def;
            return db.TryGet(itemId, out def) ? def : null;
        }

        void NotifyChanged()
        {
            RefreshBagText();
            OnInventoryChanged?.Invoke();
        }

        void RefreshBagText()
        {
            if (bagText == null) return;

            bool en = Localization.IsEnglish;

            if (_items.Count == 0)
            {
                bagText.text = Localization.T("携带：无");
                return;
            }

            var sb = new System.Text.StringBuilder(Localization.T("携带："));
            if (en) sb.Append(' ');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(en ? ", " : "、");
                string itemId = _items[i];
                string display = ItemDatabase.Instance != null
                    ? ItemDatabase.Instance.GetDisplayName(itemId)
                    : itemId;
                sb.Append(Localization.PolishEnglish(Localization.T(display)));

                // 每个房间都是同一套工具：携带栏也标出处，玩家才知道手里的绳子是谁房里的。
                string origin = ItemProvenance.Describe(itemId);
                if (!string.IsNullOrEmpty(origin))
                    sb.Append(en ? " (" : "（")
                      .Append(Localization.PolishEnglish(Localization.T(origin)))
                      .Append(en ? ")" : "）");
            }
            // 模块四：面具 / 斗篷不可丢弃，但可以主动摘下（连按两次 M 献祭）。
            if (_items.Contains("mask") || _items.Contains("cloak"))
            {
                if (en) sb.Append("   ");
                sb.Append(Localization.T("　[M] 摘下面具与斗篷（连按两次）"));
            }
            bagText.text = sb.ToString();
        }
    }
}
