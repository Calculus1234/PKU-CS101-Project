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
        [Tooltip("（已废弃，保留兼容）拖动时不再持续发射 —— 见 dragBurstCount")]
        public float dragRate = 24f;
        [Tooltip("被抓住的那一下喷多少颗（之后拖动期间 0 发射，粒子留在原地）")]
        public int dragBurstCount = 24;
        [Tooltip("落地瞬间一次性喷发多少颗")]
        public int dropBurstCount = 30;
        [Tooltip("进入互动状态时额外喷一簇，让情绪反馈立刻到位")]
        public int interactionBurst = 30;

        [Header("粒子归属")]
        [Tooltip("true = 吐出来的粒子留在世界里，不跟着宠物走（作者要求：放出来就不归宠物管了，自然消失）")]
        public bool detachParticles = true;

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
        bool dragOn;
        float appliedRate = -1f;
        float emitAcc;                      // 手动发射的累积小数
        [Tooltip("手动发射时，每颗粒子在宠物身体周围随机散开的半径（米）")]
        public float bodyScatterRadius = 0.55f;
        PetBrain brain;

        void Awake()
        {
            brain = GetComponent<PetBrain>();
            if (petAnimator == null) petAnimator = GetComponent<Animator>();
            if (petAnimator == null) petAnimator = GetComponentInChildren<Animator>();
            if (petAnimator == null)
                Debug.LogWarning("[PetVFXController] 找不到 Animator，特效不会触发");

            // ★ 粒子必须是 World 模拟：吐出去之后就不归宠物管了，宠物走开时它们留在原处自然消失。
            //   这里【强制】一次，防止 Prefab / 场景里被谁改回 Local（那样就会整团跟着宠物走）。
            if (detachParticles)
            {
                ForceWorldSpace(mathSymbolsVFX);
                ForceWorldSpace(movementVFX);
                // ★ 全身符号：关掉一切自动发射 + 把发射器固定在原点、**永不移动**。
                //   之后所有粒子都用 EmitParams 在宠物当前位置手动吐 —— 发射器根本不动，
                //   所以"已经发射过的粒子跟着宠物走"在物理上不可能发生。
                ParkEmitter(mathSymbolsVFX);
            }
        }

        /// <summary>
        /// 全身符号专用：清 burst、关自动发射、发射器钉死在原点。
        /// 不这么做的话：拖动/走路时发射器一直在动，rate/burst 会在新位置不断吐新粒子，
        /// 看上去就像整团粒子被宠物拖着走（作者看到的那条竖向粒子带）。
        /// </summary>
        void ParkEmitter(ParticleSystem ps)
        {
            if (ps == null) return;
            var em = ps.emission;
            if (em.burstCount > 0)
            {
                Debug.Log("[PetVFX] " + ps.name + " 去掉自带循环 burst ×" + em.burstCount);
                em.SetBursts(new ParticleSystem.Burst[0]);
            }
            em.enabled = false;            // 自动发射全关，改手动 Emit
            em.rateOverTime = 0f;
            em.rateOverDistance = 0f;
            ps.transform.position = UnityEngine.Vector3.zero;   // 发射器固定，永不移动
        }

        void ForceWorldSpace(ParticleSystem ps)
        {
            if (ps == null) return;
            var main = ps.main;
            if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            {
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                Debug.Log("[PetVFX] " + ps.name + " 强制改为 World 模拟空间（粒子不跟宠物走）");
            }
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
            // ★ 拖动以 PetBrain 的鼠标按住状态为准：Drag 动画会播完自动回 Idle，
            //   只看动画状态的话，长按拖动到后半段就会重新按"待机"发射 —— 粒子一路铺在拖动路径上。
            bool dragging    = (brain != null && brain.IsDragging) || IsInStates(dragStates);
            bool dropping    = IsInStates(dropStates);

            // ---- 优先级：被拽 > 互动 > 待机；移动不参与（移动时全身符号必须是 0）----
            // ★ 被拽时【不持续发射】：宠物跟着鼠标跑，持续发射等于把新粒子一路铺在它身上，
            //   看起来就是"粒子整团跟着宠物走"。改成起手喷一簇（留在原地），拖动期间 0 发射。
            float rate = 0f;
            if (dragging) rate = 0f;
            else if (interacting) rate = interactionRate;
            else if (idling) rate = idleRate;

            if (movementVFX != null) rate = moving ? 0f : rate;   // 移动时彻底关掉全身符号

            // 全身符号的发射点 = 宠物当前世界位置（发射器本身钉在原点不动）
            Transform body = vfxAnchor != null ? vfxAnchor : transform;
            UnityEngine.Vector3 emitPos = body.position + bodyOffset;

            // ★ 手动发射（按 rate 累积）：粒子生成在宠物当前位置，之后完全由世界接管
            if (rate > 0f && mathSymbolsVFX != null)
            {
                emitAcc += rate * Time.deltaTime;
                int n = Mathf.FloorToInt(emitAcc);
                if (n > 0) { emitAcc -= n; EmitAt(emitPos, n, 0f); }
            }
            else emitAcc = 0f;

            // 进入互动：立刻补一簇；离开互动：清掉标记
            if (interacting && !interactionOn && mathSymbolsVFX != null)
            {
                interactionOn = true;
                EmitAt(emitPos, interactionBurst, 0f);
                Debug.Log("[PetVFX] 互动爆发 → 额外喷发 " + interactionBurst + " 颗");
            }
            if (!interacting) interactionOn = false;

            // 进入被拽：只在抓住的那一下喷一簇（带一点四散速度，看着活），拖到哪都不再吐新粒子
            if (dragging && !dragOn && mathSymbolsVFX != null)
            {
                dragOn = true;
                EmitAt(emitPos, dragBurstCount, 1.15f);
                Debug.Log("[PetVFX] 开始拖动 → 喷 " + dragBurstCount + " 颗四散，拖动期间不再发射");
            }
            if (!dragging) dragOn = false;

            // 进入移动：只停发射，【不 Clear】—— 已经飘出去的符号留在原地自然消散，
            // 不跟着小白一起平移（World 模拟空间 + 不清理，才是"星尘留在空气里"的感觉）
            if (moving && !movingOn)
            {
                movingOn = true;
                Debug.Log("[PetVFX] 进入移动 → 停止发射全身符号，余下的自然飘散");
            }
            if (!moving) movingOn = false;

            // ---- 落地：一次性爆发（只在进入 Drop 的那一帧触发）----
            if (dropping && !dropFired)
            {
                dropFired = true;
                if (mathSymbolsVFX != null)
                {
                    EmitAt(emitPos, dropBurstCount, 0f);
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

            // ---- 跟随（只有脚底星尘和拖尾跟着；全身符号的发射器是钉死的）----
            if (movementVFX != null) movementVFX.transform.position = body.position + feetOffset;
            if (moveTrail != null) moveTrail.transform.position = body.position + trailOffset;

            if (!Mathf.Approximately(appliedRate, rate))
            {
                appliedRate = rate;
                Debug.Log("[PetVFX] 全身符号发射率 → " + rate.ToString("0.0") + "/s（手动发射，发射器不动）");
            }
        }

        /// <summary>
        /// 在指定世界位置手动喷 count 颗符号。
        /// scatterSpeed > 0 时每颗给一个随机的向外速度（"撒出去一把"）；0 就原地生成。
        /// ⚠️ 发射器本身钉在原点、永不移动 —— 所以这些粒子生成之后只受世界模拟，
        ///    宠物怎么走、怎么拖都不会带着它们移动。
        /// </summary>
        void EmitAt(UnityEngine.Vector3 center, int count, float scatterSpeed)
        {
            if (mathSymbolsVFX == null || count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                UnityEngine.Vector3 off = UnityEngine.Random.insideUnitSphere * bodyScatterRadius;
                off.z = 0f;
                var ep = new ParticleSystem.EmitParams();
                ep.position = center + off;
                ep.applyShapeToPosition = false;
                if (scatterSpeed > 0f)
                {
                    UnityEngine.Vector2 d = UnityEngine.Random.insideUnitCircle;
                    if (d.sqrMagnitude < 0.001f) d = UnityEngine.Vector2.right;
                    d.Normalize();
                    float s = scatterSpeed * UnityEngine.Random.Range(0.45f, 1.25f);
                    ep.velocity = new UnityEngine.Vector3(d.x, d.y, 0f) * s;
                }
                mathSymbolsVFX.Emit(ep, 1);
            }
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
