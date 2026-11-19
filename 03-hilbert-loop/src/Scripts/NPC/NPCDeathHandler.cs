using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 死亡演出 + Ragdoll 物理。
    /// - 活着时：所有骨骼 Rigidbody 为 kinematic、Collider 关闭，模型完全由 Animator 驱动；
    /// - 死亡时：先播放对应的受击动作，等到角色真正倒下的那一帧关闭 Animator、
    ///   打开全部骨骼物理，让斗篷 / 身体自然瘫倒（而不是整体瞬移消失）；
    /// - 尸体：在髋骨挂藏匿点、在胸骨挂可检查组件（摘面具仍走 CorpseInspectable）。
    /// 本组件挂在 NPC 根节点，由 NPCController.Die 调用；死亡后根节点保持激活。
    /// </summary>
    public class NPCDeathHandler : MonoBehaviour
    {
        [Header("装配（NPCArtBuilder）")]
        public NPCController npc;
        public NPCVisualController visual;
        public Animator animator;
        public Transform hips;
        public Transform chest;
        public Rigidbody[] bones;
        public Collider[] boneColliders;
        public Collider bodyTrigger;
        public CharacterController characterController;

        [Header("参数")]
        [Tooltip("触发布娃娃时给髋骨的初速度（沿受击方向）")]
        public float impulseSpeed = 0.55f;
        [Tooltip("不留尸体时，倒地后多久隐藏整个对象")]
        public float vanishDelay = 2.2f;

        bool _ragdollActive;
        bool _corpseReady;
        bool _collisionsIgnored;

        public bool RagdollActive => _ragdollActive;

        void Awake()
        {
            if (npc == null) npc = GetComponent<NPCController>();
            if (visual == null) visual = GetComponent<NPCVisualController>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (hips == null) hips = FindBone("Hips");
            if (chest == null) chest = FindBone("Spine1");
            if (characterController == null) characterController = GetComponent<CharacterController>();

            if (bones == null || bones.Length == 0)
                bones = GetComponentsInChildren<Rigidbody>(true);
            if (boneColliders == null || boneColliders.Length == 0)
            {
                // 只收集「骨骼」上的 Collider：根节点的 CharacterController / 触发胶囊
                // 不属于布娃娃，死亡时另行关闭。
                Collider[] all = GetComponentsInChildren<Collider>(true);
                var list = new System.Collections.Generic.List<Collider>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].GetComponent<Rigidbody>() != null)
                        list.Add(all[i]);
                }
                boneColliders = list.ToArray();
            }

            SetPhysicsEnabled(false);
        }

        Transform FindBone(string boneName)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == boneName) return all[i];
            return null;
        }

        /// <summary>活着：物理关闭（kinematic + collider 关闭），只留 CharacterController 碰撞。</summary>
        public void SetPhysicsEnabled(bool value)
        {
            if (bones != null)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    Rigidbody body = bones[i];
                    if (body == null) continue;
                    body.isKinematic = !value;
                    body.useGravity = value;
                    body.detectCollisions = value;
                    if (value)
                    {
                        // 限制穿透修正速度，避免骨骼与家具重叠时被瞬间弹开。
                        body.maxDepenetrationVelocity = 0.6f;
                        body.maxAngularVelocity = 6f;
                    }
                }
            }

            if (boneColliders != null)
            {
                for (int i = 0; i < boneColliders.Length; i++)
                {
                    Collider collider = boneColliders[i];
                    if (collider == null || collider == bodyTrigger) continue;
                    collider.enabled = value;
                }
            }
        }

        /// <summary>
        /// NPCController.Die 调用：选择受击动作并在倒地帧切入 Ragdoll。
        /// leaveCorpse=false（投票抹杀 / 推出窗外）时仍会倒地，但稍后隐藏。
        /// </summary>
        public void PlayDeath(string cause, bool leaveCorpse, GameObject killer, bool wasAsleep)
        {
            if (_ragdollActive) return;

            Vector3 hitDir = transform.forward;
            if (killer != null)
            {
                Vector3 d = transform.position - killer.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude > 0.0001f) hitDir = d.normalized;
            }

            StartCoroutine(DeathRoutine(cause, leaveCorpse, hitDir, wasAsleep));
        }

        IEnumerator DeathRoutine(string cause, bool leaveCorpse, Vector3 hitDir, bool wasAsleep)
        {
            if (npc != null && npc.isDead == false) npc.isDead = true;

            string trigger = wasAsleep ? null : SelectDeathTrigger(cause);
            float fallDelay = wasAsleep ? 0f : SelectFallDelay(cause);

            if (!string.IsNullOrEmpty(trigger) && animator != null && animator.enabled)
            {
                animator.speed = 1f;
                if (visual != null)
                {
                    visual.PlayAction(trigger, 99f);
                }
                else
                {
                    animator.ResetTrigger(trigger);
                    animator.SetTrigger(trigger);
                }
            }

            if (fallDelay > 0f) yield return new WaitForSeconds(fallDelay);

            // 睡梦中被杀：人已经躺在床上，骨骼和床的碰撞体大面积重叠。
            // 这时切动态布娃娃会被床的穿透修正弹飞——改为冻结布娃娃，
            // 保持睡姿、打开骨骼碰撞体供玩家检查，但不再参与物理模拟。
            if (wasAsleep && leaveCorpse)
                EnableRagdollFrozen();
            else
                EnableRagdoll(hitDir);

            SetupCorpse(cause);

            if (!leaveCorpse)
            {
                yield return new WaitForSeconds(vanishDelay);
                if (visual != null) visual.SetMaskVisible(false);
                gameObject.SetActive(false);
            }
        }

        static string SelectDeathTrigger(string cause)
        {
            if (string.IsNullOrEmpty(cause)) return "HitKnife";
            if (cause.Contains("锤")) return "HitHammer";
            if (cause.Contains("刀") || cause.Contains("匕首") || cause.Contains("刺")) return "HitKnife";
            if (cause.Contains("窗") || cause.Contains("推")) return "PushOutWindow";
            if (cause.Contains("投票") || cause.Contains("抹杀")) return "Agony";
            return "HitKnife";
        }

        static float SelectFallDelay(string cause)
        {
            if (string.IsNullOrEmpty(cause)) return 2.2f;
            if (cause.Contains("锤")) return 1.15f;
            if (cause.Contains("刀") || cause.Contains("匕首") || cause.Contains("刺")) return 2.35f;
            if (cause.Contains("窗") || cause.Contains("推")) return 1.1f;
            if (cause.Contains("投票") || cause.Contains("抹杀")) return 0.9f;
            return 2.2f;
        }

        /// <summary>关闭 Animator，打开全部骨骼物理，并沿受击方向施加一个很小的冲量。</summary>
        public void EnableRagdoll(Vector3 hitDir)
        {
            if (_ragdollActive) return;
            _ragdollActive = true;

            if (animator != null) animator.enabled = false;
            if (characterController != null) characterController.enabled = false;
            if (bodyTrigger != null) bodyTrigger.enabled = false;

            IgnoreSelfCollisions();
            SetPhysicsEnabled(true);

            if (hips != null)
            {
                Rigidbody hipsBody = hips.GetComponent<Rigidbody>();
                if (hipsBody != null)
                {
                    Vector3 dir = hitDir;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
                    // 只给一个很小的水平初速；绝不向上抛，避免尸体“起飞”。
                    hipsBody.AddForce(dir.normalized * impulseSpeed, ForceMode.VelocityChange);
                    hipsBody.AddTorque(new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(-0.12f, 0.12f), Random.Range(-0.25f, 0.25f)), ForceMode.VelocityChange);
                }
            }

            Debug.Log($"[NPCDeath] {name} 切入 Ragdoll（hitDir={hitDir}）。");
        }

        /// <summary>
        /// 冻结布娃娃：保持当前姿势（睡姿）不动，只打开骨骼碰撞体用于尸体检查。
        /// 用于床上死亡——骨骼与床体本来就重叠，动态模拟会被穿透修正弹飞。
        /// </summary>
        public void EnableRagdollFrozen()
        {
            if (_ragdollActive) return;
            _ragdollActive = true;

            if (animator != null) animator.enabled = false;
            if (characterController != null) characterController.enabled = false;
            if (bodyTrigger != null) bodyTrigger.enabled = false;

            IgnoreSelfCollisions();

            // 骨骼保持 kinematic（不参与物理），但碰撞体必须打开，
            // 否则 Player 的射线检不到尸体，无法按 E 检查。
            if (bones != null)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    Rigidbody body = bones[i];
                    if (body == null) continue;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                    body.useGravity = false;
                    body.detectCollisions = true;
                }
            }

            if (boneColliders != null)
            {
                for (int i = 0; i < boneColliders.Length; i++)
                {
                    Collider collider = boneColliders[i];
                    if (collider == null || collider == bodyTrigger) continue;
                    collider.enabled = true;
                }
            }

            Debug.Log($"[NPCDeath] {name} 在床上死亡：布娃娃冻结，保持睡姿。");
        }

        void IgnoreSelfCollisions()
        {
            if (_collisionsIgnored || boneColliders == null) return;
            _collisionsIgnored = true;

            for (int i = 0; i < boneColliders.Length; i++)
            {
                if (boneColliders[i] == null) continue;
                for (int j = i + 1; j < boneColliders.Length; j++)
                {
                    if (boneColliders[j] == null) continue;
                    Physics.IgnoreCollision(boneColliders[i], boneColliders[j], true);
                }
            }
        }

        void SetupCorpse(string cause)
        {
            if (_corpseReady) return;
            _corpseReady = true;

            Transform spotHost = hips != null ? hips : transform;
            HidingSpot spot = spotHost.GetComponent<HidingSpot>();
            if (spot == null) spot = spotHost.gameObject.AddComponent<HidingSpot>();
            spot.displayName = (npc != null ? npc.Number : 0) + "号（尸体）";
            spot.allowedLocation = HideLocation.OnBody;

            Transform inspectHost = chest != null ? chest : spotHost;
            CorpseInspectable inspectable = inspectHost.GetComponent<CorpseInspectable>();
            if (inspectable == null) inspectable = inspectHost.gameObject.AddComponent<CorpseInspectable>();
            inspectable.number = npc != null ? npc.Number : 0;
            inspectable.isNinthVictim = npc != null && npc.Number == 9;
            inspectable.subjectName = (npc != null ? npc.Number : 0) + "号的尸体";
            inspectable.cause = cause;

            Debug.Log($"[NPCDeath] {name} 尸体已就绪：藏匿点=髋骨，检查点=胸骨。");
        }
    }
}
