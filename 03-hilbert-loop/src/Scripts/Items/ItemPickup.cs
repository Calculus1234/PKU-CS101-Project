using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 世界中的可拾取物品（占位几何体，后续替换为 FBX 模型）。
    /// SpawnWorldItem 同时供场景构建器和运行时丢弃使用。
    /// </summary>
    public class ItemPickup : MonoBehaviour, IInteractable
    {
        public string itemId = "";

        [Tooltip("留空 = 正常可见；填写物品 id 时，只有玩家携带该物品才能发现并拾取（9 号窗台神秘磁铁）")]
        public string requiredCarriedItemId = "";

        [Tooltip("道具出处房间号（0 = 运行时按父物体 Room_N 自动判断）")]
        public int originRoom = 0;

        bool _discovered;
        InventorySystem _playerInventory;

        string DisplayName
        {
            get
            {
                return ItemDatabase.Instance != null
                    ? ItemDatabase.Instance.GetDisplayName(itemId)
                    : itemId;
            }
        }

        int ResolveOriginRoom()
        {
            if (originRoom > 0) return originRoom;
            originRoom = ItemProvenance.RoomNumberFromTransform(transform);
            return originRoom;
        }

        public string GetInteractionPrompt(PlayerController player)
        {
            int room = ResolveOriginRoom();
            return room > 0 ? $"拾取{DisplayName}（{room}号房）" : "拾取" + DisplayName;
        }

        public void Interact(PlayerController player)
        {
            if (player == null || player.inventory == null) return;

            int room = ResolveOriginRoom();

            if (!player.inventory.Add(itemId))
            {
                player.ShowMessage("背包已经满了。");
                return;
            }

            // 只有真正进了背包才记出处；背包满时不能污染证据归属。
            ItemProvenance.Record(itemId, room);

            player.ShowMessage(itemId == "note"
                ? "拾取了纸条。按 F 阅读。"
                : (room > 0
                    ? $"拾取了{DisplayName}（取自{room}号房）。"
                    : $"拾取了{DisplayName}。"));

            // 3.3：拾取声音（附近 NPC 可能听见）
            SoundEventManager.Emit(SoundEventType.ItemPickup, transform.position + Vector3.up * 0.8f,
                0.6f, player.gameObject);

            // 预留：可在此挂接剧情事件（例如拾取纸条后触发 Yarn 对话）
            Destroy(gameObject);
        }

        void Start()
        {
            if (string.IsNullOrEmpty(requiredCarriedItemId)) return;
            SetDiscoverable(false); // 未发现前：不可见、不可交互、刚体冻结
        }

        void Update()
        {
            if (_discovered || string.IsNullOrEmpty(requiredCarriedItemId)) return;

            if (_playerInventory == null)
                _playerInventory = FindObjectOfType<InventorySystem>();
            if (_playerInventory == null) return;

            if (_playerInventory.Has(requiredCarriedItemId))
            {
                _discovered = true;
                SetDiscoverable(true);
                Debug.Log($"[ItemPickup] 发现隐藏物品：{DisplayName}");
            }
        }

        /// <summary>显隐 + 可交互切换（关渲染 / 碰撞后，交互射线不会命中）。</summary>
        void SetDiscoverable(bool value)
        {
            // 道具视觉现在是 FBX 子物体，必须递归开关所有渲染器 / 碰撞体。
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = value;

            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) colliders[i].enabled = value;

            Rigidbody body = GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = !value;
        }

        /// <summary>在世界中生成一件道具（FBX 视觉 + 简单盒碰撞 + 刚体）。</summary>
        public static ItemPickup SpawnWorldItem(string itemId, Vector3 position, Transform parent = null,
            int originRoom = 0)
        {
            GameObject go = new GameObject("Item_" + itemId);
            if (parent != null) go.transform.SetParent(parent, true);

            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(0.22f, 0.18f, 0.22f);

            Rigidbody body = go.AddComponent<Rigidbody>();
            body.mass = 0.4f;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;

            // 纸张是薄片：用 0.05 厚的碰撞体，才能平贴在桌面上（0.18 会让它悬空半截）。
            if (itemId == "note")
            {
                box.size = new Vector3(0.22f, 0.05f, 0.22f);
                body.mass = 0.08f;
            }

            ItemPickup pickup = go.AddComponent<ItemPickup>();
            pickup.itemId = itemId;
            // 丢下再捡起时保留原出处；新生成则回退到账本里的最近来源。
            pickup.originRoom = originRoom > 0 ? originRoom : ItemProvenance.GetRoom(itemId);

            AttachVisual(go, itemId);
            return pickup;
        }

        /// <summary>挂上 ItemDatabase 中登记的道具 FBX 预制体（缺失时退回原来的小方块）。</summary>
        public static void AttachVisual(GameObject root, string itemId)
        {
            ItemDatabase db = ItemDatabase.Instance;
            ItemDefinition def = db != null ? db.Get(itemId) : null;
            if (def != null && def.worldPrefab != null)
            {
                GameObject visual = Instantiate(def.worldPrefab, root.transform, false);
                visual.name = def.worldPrefab.name;
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                return;
            }

            // 兜底：数据库/预制体缺失时仍给一个可见占位块，避免拾取物变成隐形。
            GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fallback.name = "FallbackVisual";
            fallback.transform.SetParent(root.transform, false);
            fallback.transform.localScale = new Vector3(0.14f, 0.14f, 0.14f);
            Destroy(fallback.GetComponent<Collider>());
        }
    }
}
