using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 敌意武器 AI（重构）：
    /// 当 NPC 真的把玩家视为必须杀死的人时，它不会只站在原地“充满杀意”——
    /// 它会去桌上 / 地上找匕首或锤子，捡起来，追向玩家并动手。
    ///
    /// 触发条件写在黑板 wantsPlayerDead 上：
    /// - 玩家袭击失败，被袭击者会立刻反击；
    /// - 玩家在同房间杀死别人，被同房间目击者锁定。
    /// 其余情况（只是怀疑）仍然走会议 / 投票，不会私刑。
    /// </summary>
    public class NPCHostileAgent : MonoBehaviour
    {
        [Header("行为")]
        public float searchRadius = 16f;
        public float attackRange = 2.1f;
        public float attackCooldown = 2.8f;
        public float repathInterval = 0.4f;

        [Header("武器外观")]
        [Tooltip("刺杀动作中武器可见的时长；其余时间收在袖子里")]
        public float strikeVisibleDuration = 0.55f;
        [Tooltip("武器模型相对手掌的额外前移量")]
        public float weaponForwardOffset = 0.02f;

        NPCController _npc;
        NPCNavigator _navigator;
        PlayerController _player;

        bool _controlling;
        string _weaponId = "";
        GameObject _weaponVisual;
        Renderer[] _weaponRenderers;
        Coroutine _strikeRoutine;
        bool _weaponVisible;
        float _nextScanTime;
        float _nextAttackTime;
        float _nextRepathTime;
        ItemPickup _targetPickup;
        Transform _rightHand;

        void Awake()
        {
            _npc = GetComponent<NPCController>();
            _navigator = GetComponent<NPCNavigator>();
        }

        void Update()
        {
            if (_npc == null) return;
            if (_npc.isDead)
            {
                HideWeaponImmediately();
                return;
            }
            if (!_npc.IsAwake) return;
            if (_npc.InMeeting || _npc.InPrivateChat) { ReleaseControl(); return; }
            // 玩家已经被这一刀杀死：先把刺杀动作播完，再收武器，
            // 否则刀光只闪一帧就消失，看起来像根本没有出手。
            if (!CycleState.playerAlive) { ReleaseControl(false); return; }
            if (GameManager.Instance != null
                && GameManager.Instance.CurrentPhase != GamePhase.Investigation)
            {
                ReleaseControl();
                return;
            }
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding)
            {
                ReleaseControl();
                return;
            }

            if (_npc.blackboard == null || !_npc.blackboard.wantsPlayerDead)
            {
                ReleaseControl();
                return;
            }

            if (_player == null) _player = FindObjectOfType<PlayerController>();
            if (_player == null) return;

            TakeControl();

            // 没武器就找；拿着锤子时继续留意匕首——匕首优先级更高，
            // 一旦出现就换掉锤子，而不是“先捡到什么就一直用什么”。
            if (_weaponId != "dagger" && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + 2.5f;
                ItemPickup candidate = FindBestWeapon(_weaponId);
                if (candidate != null) _targetPickup = candidate;
            }

            if (_targetPickup != null && _targetPickup.gameObject != null)
            {
                if (WalkToWeapon(_targetPickup)) return; // 还在走向武器
            }

            float distance = Vector3.Distance(transform.position, _player.transform.position);

            Face(_player.transform.position);

            if (distance <= attackRange)
            {
                if (_navigator != null) _navigator.Stop();
                TryAttack();
                return;
            }

            if (Time.time >= _nextRepathTime)
            {
                _nextRepathTime = Time.time + repathInterval;
                if (_navigator != null) _navigator.MoveTo(_player.transform.position);
            }
        }

        void LateUpdate()
        {
            if (!_weaponVisible || _weaponVisual == null) return;
            UpdateWeaponPose();
        }

        void OnDisable()
        {
            HideWeaponImmediately();
        }

        void TakeControl()
        {
            if (_controlling) return;
            _controlling = true;

            if (_navigator != null) _navigator.enabled = true;
            if (_npc.behaviourTree != null) _npc.behaviourTree.SetPaused(true);

            Debug.Log($"[Hostile {_npc.Number}] 进入追杀状态：开始寻找武器。");
        }

        void ReleaseControl(bool hideWeapon = true)
        {
            if (hideWeapon) HideWeaponImmediately();

            if (!_controlling) return;
            _controlling = false;
            _targetPickup = null;

            if (_navigator != null)
            {
                _navigator.Stop();
            }
            if (_npc != null && _npc.behaviourTree != null && !_npc.isDead)
            {
                _npc.behaviourTree.SetPaused(false);
                _npc.behaviourTree.Restart();
            }
        }

        /// <summary>走向已锁定的武器；返回 true 表示还没走到</summary>
        bool WalkToWeapon(ItemPickup pickup)
        {
            float d = Vector3.Distance(transform.position, pickup.transform.position);
            if (d <= 1.8f)
            {
                PickUp(pickup);
                return true; // 本帧不再做别的，下一帧开始追杀
            }

            if (_navigator != null && Time.time >= _nextRepathTime)
            {
                _nextRepathTime = Time.time + repathInterval;
                _navigator.MoveTo(pickup.transform.position);
            }
            return true;
        }

        /// <summary>
        /// 扫描附近武器。匕首绝对优先：只要搜索半径里有匕首，
        /// 哪怕锤子就在脚边，也先去拿匕首。已经拿着匕首时不再找。
        /// </summary>
        ItemPickup FindBestWeapon(string currentWeaponId)
        {
            if (currentWeaponId == "dagger") return null;

            ItemPickup[] pickups = FindObjectsOfType<ItemPickup>();
            ItemPickup nearestDagger = null;
            ItemPickup nearestHammer = null;
            float daggerDistance = float.MaxValue;
            float hammerDistance = float.MaxValue;

            for (int i = 0; i < pickups.Length; i++)
            {
                ItemPickup pickup = pickups[i];
                if (pickup == null) continue;
                if (pickup.itemId != "dagger" && pickup.itemId != "hammer") continue;
                if (!string.IsNullOrEmpty(pickup.requiredCarriedItemId)) continue;

                float distance = Vector3.Distance(transform.position, pickup.transform.position);
                if (distance > searchRadius) continue;

                if (pickup.itemId == "dagger")
                {
                    if (distance < daggerDistance)
                    {
                        daggerDistance = distance;
                        nearestDagger = pickup;
                    }
                }
                else if (currentWeaponId != "hammer")
                {
                    // 已经拿着锤子时不再为另一把锤子跑一趟。
                    if (distance < hammerDistance)
                    {
                        hammerDistance = distance;
                        nearestHammer = pickup;
                    }
                }
            }

            if (nearestDagger != null)
            {
                Debug.Log($"[Hostile {_npc.Number}] 发现匕首（{daggerDistance:0.0}m）；匕首优先于锤子。");
                return nearestDagger;
            }

            if (nearestHammer != null)
                Debug.Log($"[Hostile {_npc.Number}] 附近没有匕首，退而求其次拿锤子（{hammerDistance:0.0}m）。");
            else if (string.IsNullOrEmpty(currentWeaponId))
                Debug.Log($"[Hostile {_npc.Number}] 附近没有匕首 / 锤子，赤手空拳追向玩家。");

            return nearestHammer;
        }

        void PickUp(ItemPickup pickup)
        {
            string pickedId = pickup.itemId;
            _weaponId = pickedId;
            _targetPickup = null;

            string displayName = ItemDatabase.Instance != null
                ? ItemDatabase.Instance.GetDisplayName(_weaponId)
                : _weaponId;

            Destroy(pickup.gameObject);
            AttachWeaponVisual();

            if (_player != null)
                _player.ShowMessage($"{_npc.Number}号从桌上抄起了{displayName}。", 3f);
            SoundEventManager.Emit(SoundEventType.ItemPickup, transform.position + Vector3.up, 0.7f, gameObject);

            Debug.Log($"[Hostile {_npc.Number}] 捡起了{displayName}，开始追杀玩家。");
        }

        void TryAttack()
        {
            if (Time.time < _nextAttackTime) return;
            _nextAttackTime = Time.time + attackCooldown;

            string weaponName = string.IsNullOrEmpty(_weaponId)
                ? "空手"
                : ItemDatabase.Instance.GetDisplayName(_weaponId);

            float chance;
            switch (_weaponId)
            {
                case "dagger": chance = 0.85f; break;
                case "hammer": chance = 0.55f; break;
                default: chance = 0.25f; break;
            }

            // 武器只在出手这一瞬间亮出来；其余时间收在袖子里。
            PlayStrikeVisual(_weaponId == "dagger" ? 0.75f : strikeVisibleDuration);
            SoundEventManager.Emit(SoundEventType.Combat, transform.position + Vector3.up, 1f, gameObject);

            if (Random.value < chance)
            {
                if (PlayerDeathSystem.Instance != null)
                {
                    PlayerDeathSystem.Instance.KillPlayer(
                        $"{_npc.Number}号用{weaponName}杀死了你", PlayerDeathType.Killed);
                }
                return;
            }

            if (_player != null)
            {
                _player.ShowMessage($"{_npc.Number}号的{weaponName}擦着你的身体过去——你勉强躲开。", 2.5f);
                _player.ShakeCamera(0.3f, 0.08f);
            }
        }

        void Face(Vector3 worldPoint)
        {
            Vector3 dir = worldPoint - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(dir);
        }

        // ------------------------------------------------------------------
        // 武器外观：真实匕首 / 锤子模型，平时隐藏，只在刺杀瞬间出现
        // ------------------------------------------------------------------

        void AttachWeaponVisual()
        {
            if (_weaponVisual != null) Destroy(_weaponVisual);
            _weaponVisual = null;
            _weaponRenderers = null;
            _weaponVisible = false;

            ItemDefinition def = ItemDatabase.Instance != null
                ? ItemDatabase.Instance.Get(_weaponId)
                : null;
            GameObject prefab = def != null ? def.worldPrefab : null;
            if (prefab == null)
            {
                Debug.LogWarning($"[Hostile {_npc.Number}] 武器 {_weaponId} 没有世界预制体，无法显示模型。");
                return;
            }

            _weaponVisual = Instantiate(prefab, transform, false);
            _weaponVisual.name = "HeldWeapon_" + _weaponId;
            _weaponVisual.layer = gameObject.layer;
            _weaponVisual.transform.localPosition = Vector3.zero;
            _weaponVisual.transform.localRotation = Quaternion.identity;
            _weaponVisual.transform.localScale = Vector3.one;

            // 手持模型不参与物理，也不要挡住射线 / 交互。
            Collider[] colliders = _weaponVisual.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Destroy(colliders[i]);
            Rigidbody[] bodies = _weaponVisual.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++) Destroy(bodies[i]);

            _weaponRenderers = _weaponVisual.GetComponentsInChildren<Renderer>(true);
            SetWeaponVisible(false);
        }

        void PlayStrikeVisual(float duration)
        {
            if (_weaponVisual == null) return;
            if (_strikeRoutine != null) StopCoroutine(_strikeRoutine);
            _strikeRoutine = StartCoroutine(StrikeRoutine(Mathf.Max(0.1f, duration)));
        }

        IEnumerator StrikeRoutine(float duration)
        {
            SetWeaponVisible(true);
            UpdateWeaponPose();
            yield return new WaitForSeconds(duration);
            SetWeaponVisible(false);
            _strikeRoutine = null;
        }

        void HideWeaponImmediately()
        {
            if (_strikeRoutine != null)
            {
                StopCoroutine(_strikeRoutine);
                _strikeRoutine = null;
            }
            SetWeaponVisible(false);
        }

        void SetWeaponVisible(bool visible)
        {
            _weaponVisible = visible;
            if (_weaponRenderers == null) return;
            for (int i = 0; i < _weaponRenderers.Length; i++)
            {
                if (_weaponRenderers[i] != null) _weaponRenderers[i].enabled = visible;
            }
        }

        /// <summary>
        /// 武器原点贴在右掌，刀尖 / 锤头朝 NPC 正前方（也就是玩家方向）。
        /// 不沿“前臂 → 手掌”方向：那样刀尖会朝下扎进自己的斗篷里。
        /// </summary>
        void UpdateWeaponPose()
        {
            if (_weaponVisual == null) return;

            Transform hand = GetRightHand();
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = transform.forward;
            forward.Normalize();

            Vector3 origin = hand != null
                ? hand.position
                : transform.position + transform.up * 1.1f + transform.forward * 0.35f;

            _weaponVisual.transform.SetPositionAndRotation(
                origin + forward * weaponForwardOffset,
                Quaternion.LookRotation(forward, Vector3.up));
        }

        Transform GetRightHand()
        {
            if (_rightHand != null) return _rightHand;
            if (_npc != null && _npc.animator != null)
                _rightHand = _npc.animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (_rightHand == null) _rightHand = FindDeep(transform, "RightHand");
            return _rightHand;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
