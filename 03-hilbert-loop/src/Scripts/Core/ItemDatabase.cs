using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 道具数据库（ScriptableObject）。
    /// 约定资产路径：Assets/Resources/ItemDatabase.asset，
    /// 运行时通过 ItemDatabase.Instance 全局访问（由 MainSceneBuilder 自动生成与填充）。
    /// </summary>
    [CreateAssetMenu(menuName = "HilbertLoop/Item Database", fileName = "ItemDatabase")]
    public class ItemDatabase : ScriptableObject
    {
        public const string ResourcesPath = "ItemDatabase";

        public List<ItemDefinition> items = new List<ItemDefinition>();

        private Dictionary<string, ItemDefinition> _map;
        private static ItemDatabase _instance;

        /// <summary>全局实例（从 Resources 加载并缓存）。</summary>
        public static ItemDatabase Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<ItemDatabase>(ResourcesPath);
                }
                return _instance;
            }
        }

        private void OnEnable()
        {
            RebuildMap();
        }

        /// <summary>重建 id → 定义 的索引。</summary>
        public void RebuildMap()
        {
            _map = new Dictionary<string, ItemDefinition>();
            for (int i = 0; i < items.Count; i++)
            {
                var def = items[i];
                if (def == null || string.IsNullOrEmpty(def.id)) continue;
                _map[def.id] = def;
            }
        }

        public ItemDefinition Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_map == null) RebuildMap();
            return _map.TryGetValue(id, out var def) ? def : null;
        }

        public bool TryGet(string id, out ItemDefinition def)
        {
            def = Get(id);
            return def != null;
        }

        public string GetDisplayName(string id)
        {
            var def = Get(id);
            return def != null ? def.displayName : id;
        }
    }
}
