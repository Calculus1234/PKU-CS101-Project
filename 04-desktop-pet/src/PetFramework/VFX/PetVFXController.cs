using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【形态特效控制器 · 状态减法版】
    ///
    /// 原则：**不是所有状态都满屏符号**。用一个全身符号系统 + 按状态调发射率，
    ///       让"情绪爆发 / 待机呼吸 / 移动拖尾"在视觉上一眼就能区分开。
    ///
    ///   互动（Talk / Pet / LookBack） → 全身符号，高密度（情绪反馈）
    ///   待机（Idle / Sit / Sleep）    → 同一个系统，发射率降到 1/4 左右（呼吸感）
    ///   移动（Walk / Run / Jump）     → 全身符号彻底关掉，只剩脚底星尘拖尾
    ///   被拽（Drag）                  → 散乱的符号/微光
    ///   掉落（Drop）                  → 落地瞬间一次性 Emit 一簇
    ///
    /// 为什么用"改发射率"而不是"多个系统开关"：
    ///   · 同一个系统切换时，场上已有粒子会自然飘完，不会出现"啪一下换了一套"；
    ///   · 也不用维护三四个几乎一样的粒子对象。
    ///
    /// ⚠️ 粒子不挂在宠物子物体上：PetBrain 靠 localScale.x 翻转，
    ///    子物体跟着翻会让 billboard 的 UV 镜像（π 会变成反的）。
    /// </summary>
    public class PetVFXController : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("留空会自动在本物体 / 子物体上找 Animator")]
        public Animator petAnimator;
        [Tooltip("身体锚点（全身符号 / 脚底拖尾 / TrailRenderer 都用它）。留空 = 本物体")]
        public Transform vfxAnchor;

        [Header("全身符号（MathSymbols）")]
        public ParticleSystem mathSymbolsVFX;
        [Tooltip("相对身体锚点的偏移：放在腰腹高度，形状本身覆盖全身")]
        public Vector3 bodyOffset = new Vector3(0f, 0.60f, -0.05f);

        [Header("脚底星尘拖尾（移动时）")]
        public ParticleSystem movementVFX;
        public Vector3 feetOffset = new Vector3(0f, 0.12f, 0.10f);
        [Tooltip("流光带（TrailRenderer），移动时 emitting=true")]
        public TrailRenderer moveTrail;
        public Vector3 trailOffset = new Vector3(0f, 0.10f, 0.02f);

        [Header("状态 → 发射率")]
        [Tooltip("互动/爆发：保持高密度")]
        public float interactionRate = 45f;
        [Tooltip("待机：降低到约 1/3，让符号零散出现")]
        public float idleRate = 16f;
        [Tooltip("被拽：介于两者之间，且更散")]
        public float dragRate = 24f;
        [Tooltip("落地瞬间一次性喷发多少颗")]
        public int dropBurstCount = 30;
        [Tooltip("进入互动状态时额外喷一簇，让情绪反馈立刻到位")]
        public int interactionBurst = 30;

        [Header("状态列表（两套命名都认）")]
        public string[] interactionStates = { "Talk", "doTalk", "Pet", "doPet", "LookBack", "doLookBack" };
        public string[] idleStates = { "Idle", "doIdle", "Sit", "doSit", "Sleep", "doSleep" };
        public string[] movementStates = { "Walk", "doWalk", "Run", "doRun", "Jump", "doJump" };
        public string[] dragStates = { "Drag", "doDrag" };
        public string[] dropStates = { "Drop", "doDrop" };

        bool dropFired;
        bool movementOn;
        bool interactionOn;
        bool movingOn;
        float appliedRate = -1f;

        void Awake()
        {
            if (petAnimator == null) petAnimator = GetComponent<Animator>();
            if (petAnimator == null) petAnimator = GetComponentInChildren<Animator>();
            if (petAnimator == null)
                Debug.LogWarning("[PetVFXController] 找不到 Animator，特效不会触发");
        }

        void Start()
        {
            // 全身系统一开局就 Play，靠发射率控制"有没有东西出来"
            if (mathSymbolsVFX != null)
            {
                var main = mathSymbolsVFX.main;
                main.playOnAwake = false;
                mathSymbolsVFX.Play();
            }
            if (movementVFX != null) movementVFX.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void LateUpdate()
        {
            if (petAnimator == null) return;

            bool interacting = IsInStates(interactionStates);
            bool idling      = IsInStates(idleStates);
            bool moving      = IsInStates(movementStates);
            bool dragging    = IsInStates(dragStates);
            bool dropping    = IsInStates(dropStates);

            // ---- 优先级：被拽 > 互动 > 待机；移动不参与（移动时全身符号必须是 0）----
            float rate = 0f;
            if (dragging) rate = dragRate;
            else if (interacting) rate = interactionRate;
            else if (idling) rate = idleRate;

            if (movementVFX != null) rate = moving ? 0f : rate;   // 移动时彻底关掉全身符号

            // 进入互动：立刻补一簇；离开互动：清掉标记
            if (interacting && !interactionOn && mathSymbolsVFX != null)
            {
                interactionOn = true;
                mathSymbolsVFX.Emit(interactionBurst);
                Debug.Log("[PetVFX] 互动爆发 → 额外喷发 " + interactionBurst + " 颗");
            }
            if (!interacting) interactionOn = false;

            // 进入移动：只停发射，【不 Clear】—— 已经飘出去的符号留在原地自然消散，
            // 不跟着小白一起平移（World 模拟空间 + 不清理，才是"星尘留在空气里"的感觉）
            if (moving && !movingOn)
            {
                movingOn = true;
                Debug.Log("[PetVFX] 进入移动 → 停止发射全身符号，余下的自然飘散");
            }
            if (!moving) movingOn = false;

            ApplyRate(mathSymbolsVFX, rate);

            // ---- 落地：一次性爆发（只在进入 Drop 的那一帧触发）----
            if (dropping && !dropFired)
            {
                dropFired = true;
                if (mathSymbolsVFX != null)
                {
                    mathSymbolsVFX.Emit(dropBurstCount);
                    Debug.Log("[PetVFX] 落地 → 一次性喷发 " + dropBurstCount + " 颗符号");
                }
            }
            if (!dropping) dropFired = false;

            // ---- 移动：脚底星尘 + 拖尾 ----
            bool wantMoveVFX = moving;
            if (movementVFX != null)
            {
                if (wantMoveVFX && !movementOn) { movementVFX.Play(); movementOn = true; }
                else if (!wantMoveVFX && movementOn)
                {
                    movementVFX.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    movementOn = false;
                }
            }
            if (moveTrail != null && moveTrail.emitting != wantMoveVFX) moveTrail.emitting = wantMoveVFX;

            // ---- 跟随 ----
            Transform body = vfxAnchor != null ? vfxAnchor : transform;
            if (mathSymbolsVFX != null) mathSymbolsVFX.transform.position = body.position + bodyOffset;
            if (movementVFX != null) movementVFX.transform.position = body.position + feetOffset;
            if (moveTrail != null) moveTrail.transform.position = body.position + trailOffset;
        }

        void ApplyRate(ParticleSystem ps, float rate)
        {
            if (ps == null) return;
            if (Mathf.Approximately(appliedRate, rate)) return;
            appliedRate = rate;
            var em = ps.emission;
            em.rateOverTime = rate;
            Debug.Log("[PetVFX] 全身符号发射率 → " + rate.ToString("0.0") + "/s");
        }

        /// <summary>当前是否处在给定的任一状态里（含过渡中的目标状态）。</summary>
        bool IsInStates(string[] names)
        {
            if (names == null || names.Length == 0) return false;

            AnimatorStateInfo cur = petAnimator.GetCurrentAnimatorStateInfo(0);
            for (int i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                if (cur.IsName(names[i])) return true;
            }
            if (petAnimator.IsInTransition(0))
            {
                AnimatorStateInfo next = petAnimator.GetNextAnimatorStateInfo(0);
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.IsNullOrEmpty(names[i])) continue;
                    if (next.IsName(names[i])) return true;
                }
            }
            return false;
        }
    }
}
