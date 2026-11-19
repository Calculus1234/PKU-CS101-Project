using System.Collections;
using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 正式模型（斗篷 + 面具 + 号码）的视觉控制：
    /// - 站立 / 睡眠两套姿态（睡眠用 Waking 动作首帧冻结，整个模型收进床面）；
    /// - 行走 / 奔跑 / 坐下 / 环顾等 Animator 状态切换；
    /// - 面具号码与面具染色（每个 NPC 独立材质，不影响共享 FBX 材质）；
    /// - 摘面具演出（斗篷隐藏、露出里面的人形）。
    /// 该组件挂在 NPC 根节点，模型层级为 root/Visual/CloakModel。
    /// </summary>
    public class NPCVisualController : MonoBehaviour
    {
        [Header("模型引用（NPCArtBuilder 装配）")]
        public Animator animator;
        public Transform visualRoot;
        public SkinnedMeshRenderer cloakRenderer;
        public Transform headBone;
        public Transform maskTransform;
        public Renderer maskRenderer;
        public TextMeshPro maskNumber;
        public GameObject humanBody;
        [Tooltip("尸体摘面具时露出的「同一张脸」占位体（挂在 Head 骨骼下）")]
        public GameObject faceProxy;

        [Header("姿态")]
        [Tooltip("站立时模型相对根节点（胶囊中心）的偏移：脚底 = 根节点 - 0.85m")]
        public Vector3 standLocalPosition = new Vector3(0f, -0.85f, 0f);
        [Tooltip("床上躺姿：模型放平（局部 Y 指向床头、正面朝上），脚端落在 root 本地 -Z")]
        public Vector3 sleepLocalPosition = new Vector3(0f, -0.51f, -3.43f);
        public Vector3 sleepLocalEuler = new Vector3(270f, 180f, 0f);
        [Tooltip("躺姿缩放：斗篷很宽，放平后如果不压扁会像一顶帐篷；在此压薄厚度")]
        public Vector3 sleepLocalScale = new Vector3(1f, 1f, 0.25f);

        NPCController _npc;
        NPCNavigator _navigator;
        bool _sleeping;
        bool _sitting;
        float _actionUntil;
        string _currentState;
        Coroutine _wakeRoutine;

        public bool IsSleeping => _sleeping;
        public Transform MaskTransform => maskTransform;
        public Vector3 MaskWorldPosition => maskTransform != null ? maskTransform.position : transform.position;
        public Quaternion MaskWorldRotation => maskTransform != null ? maskTransform.rotation : transform.rotation;

        void Awake()
        {
            _npc = GetComponent<NPCController>();
            _navigator = GetComponent<NPCNavigator>();
            if (visualRoot == null)
            {
                Transform found = transform.Find("Visual");
                if (found != null) visualRoot = found;
            }
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
        }

        /// <summary>NPCFactory 在 Initialize 后调用：写入号码、面具颜色与斗篷暗色。</summary>
        public void Configure(NPCData data)
        {
            if (data == null) return;

            if (maskNumber != null)
            {
                maskNumber.text = data.number.ToString();
                maskNumber.fontStyle = FontStyles.Bold;
                maskNumber.alignment = TextAlignmentOptions.Center;
                // 号码默认血红色；但红 / 橙 / 黄面具上红字会糊掉。比较两种颜色
                // 与面具底色的距离，选更远的那个，保证任何面具上都能一眼读出号码。
                Color redColor = new Color(1f, 0.12f, 0.10f, 1f);
                Color paleColor = new Color(1f, 0.95f, 0.88f, 1f);
                Vector3 maskColor = new Vector3(
                    data.maskNumberColor.r, data.maskNumberColor.g, data.maskNumberColor.b);
                float redGap = (maskColor - new Vector3(redColor.r, redColor.g, redColor.b)).sqrMagnitude;
                float paleGap = (maskColor - new Vector3(paleColor.r, paleColor.g, paleColor.b)).sqrMagnitude;
                Color numberColor = paleGap > redGap ? paleColor : redColor;
                maskNumber.color = numberColor;
                ChineseFontUtility.Apply(maskNumber);
                ApplyNumberGlow(maskNumber);
                FitMaskNumber();
                maskNumber.gameObject.SetActive(true);
            }

            if (maskRenderer != null && maskRenderer.sharedMaterial != null)
            {
                Material maskMaterial = new Material(maskRenderer.sharedMaterial);
                maskMaterial.name = "NPC_Mask_" + data.number;
                if (maskMaterial.HasProperty("_BaseColor")) maskMaterial.SetColor("_BaseColor", data.maskNumberColor);
                if (maskMaterial.HasProperty("_Color")) maskMaterial.SetColor("_Color", data.maskNumberColor);
                maskRenderer.sharedMaterial = maskMaterial;
            }

            if (cloakRenderer != null && cloakRenderer.sharedMaterial != null)
            {
                Color cloakColor = Color.Lerp(data.maskNumberColor, new Color(0.09f, 0.09f, 0.11f), 0.78f);
                Material cloakMaterial = new Material(cloakRenderer.sharedMaterial);
                cloakMaterial.name = "NPC_Cloak_" + data.number;
                if (cloakMaterial.HasProperty("_BaseColor")) cloakMaterial.SetColor("_BaseColor", cloakColor);
                if (cloakMaterial.HasProperty("_Color")) cloakMaterial.SetColor("_Color", cloakColor);
                cloakRenderer.sharedMaterial = cloakMaterial;
            }
        }

        /// <summary>
        /// 号码自发光：切换中文字体后材质会被替换，这里重新打开 TMP 的 Emission，
        /// 保证红色号码在黑暗里仍然可读。
        /// </summary>
        public static void ApplyNumberGlow(TMP_Text text)
        {
            if (text == null) return;
            Material material = text.fontMaterial;
            if (material == null) return;
            material.EnableKeyword("_EMISSION");
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", new Color(2.4f, 0.18f, 0.12f));
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        /// <summary>
        /// 号码是预制体烘焙时按默认字体定尺寸的；运行时换成中文字体后字面尺寸会变，
        /// 沿用旧缩放会让号码胀到面具的一半、飘到额头上沿（俯视时只剩一团红光）。
        /// 这里按真实文本边界重新定尺寸，并把中心压回面具的额头带。
        /// </summary>
        void FitMaskNumber()
        {
            if (maskNumber == null || maskRenderer == null) return;

            // 深色描边：号码落在浅色 / 红色面具上时都能读出轮廓。
            maskNumber.outlineWidth = 0.2f;
            maskNumber.outlineColor = new Color(0.10f, 0.03f, 0.03f, 1f);
            Material outlineMaterial = maskNumber.fontMaterial;
            if (outlineMaterial != null)
            {
                outlineMaterial.EnableKeyword("_OUTLINE_ON");
                if (outlineMaterial.HasProperty("_OutlineWidth"))
                    outlineMaterial.SetFloat("_OutlineWidth", 0.2f);
                if (outlineMaterial.HasProperty("_OutlineColor"))
                    outlineMaterial.SetColor("_OutlineColor", new Color(0.10f, 0.03f, 0.03f, 1f));
            }

            maskNumber.ForceMeshUpdate();

            float maskHeight = maskRenderer.bounds.size.y;
            if (maskHeight < 0.02f) maskHeight = 0.18f;
            float targetWorldHeight = Mathf.Clamp(maskHeight * 0.24f, 0.032f, 0.052f);

            float oldLocalY = Mathf.Abs(maskNumber.transform.localScale.y);
            if (oldLocalY < 1e-7f) oldLocalY = 1e-3f;
            float parentWorldY = Mathf.Max(0.0001f, maskNumber.transform.lossyScale.y / oldLocalY);
            float textLocalHeight = Mathf.Max(0.0001f, maskNumber.textBounds.size.y);

            float newLocal = targetWorldHeight / (textLocalHeight * parentWorldY);
            maskNumber.transform.localScale = Vector3.one * newLocal;
            maskNumber.ForceMeshUpdate();

            Vector3 up = maskNumber.transform.up;
            Vector3 outward = -maskNumber.transform.forward;
            Bounds bounds = maskRenderer.bounds;

            // 面具是凸面，不能把号码放在包围盒中心附近——会整块埋进面具里。
            // 用 localBounds 的 8 个角点在「额头向上 / 正面朝外」两个方向上的最大投影，
            // 把号码放到面具前表面之外 1.2cm；无论头部怎么转都不会穿进去。
            Bounds local = maskRenderer.localBounds;
            Matrix4x4 toWorld = maskRenderer.transform.localToWorldMatrix;
            float upExtent = 0f;
            float outExtent = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? local.min.x : local.max.x,
                    (i & 2) == 0 ? local.min.y : local.max.y,
                    (i & 4) == 0 ? local.min.z : local.max.z);
                Vector3 delta = toWorld.MultiplyPoint3x4(corner) - bounds.center;
                upExtent = Mathf.Max(upExtent, Vector3.Dot(delta, up));
                outExtent = Mathf.Max(outExtent, Vector3.Dot(delta, outward));
            }
            if (upExtent < 0.01f) upExtent = bounds.size.y * 0.5f;
            if (outExtent < 0.01f) outExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);

            Vector3 targetCenter = bounds.center
                + up * (upExtent * 0.34f)
                + outward * (outExtent + 0.012f);

            Vector3 newCenter = maskNumber.transform.position
                + maskNumber.transform.rotation * Vector3.Scale(maskNumber.textBounds.center, maskNumber.transform.lossyScale);
            maskNumber.transform.position += targetCenter - newCenter;
        }

        void Update()
        {
            if (animator == null || !animator.enabled || _sleeping) return;
            if (_npc != null && _npc.isDead) return;
            if (Time.time < _actionUntil) return;

            if (_sitting)
            {
                SetLocomotionBools(false, false, true);
                CrossFade("Sit", 0.25f);
                return;
            }

            bool moving = _navigator != null && _navigator.IsMoving;
            bool agitated = _npc != null && _npc.blackboard != null
                && (_npc.blackboard.panicUntil > Time.time || _npc.blackboard.wantsPlayerDead);

            SetLocomotionBools(moving, moving && agitated, false);
            if (moving)
                CrossFade(agitated ? "Run" : "Walk", 0.18f);
            else
                CrossFade("Idle", 0.25f);
        }

        /// <summary>
        /// 状态机由 bool 过渡驱动（Idle/Walk/Run/Sit），只调 CrossFade 会被
        /// 下一秒的 bool 过渡立刻覆盖，坐姿/行走必须同步写 bool。
        /// </summary>
        void SetLocomotionBools(bool walking, bool running, bool sitting)
        {
            if (animator == null) return;
            animator.SetBool("Walking", walking);
            animator.SetBool("Running", running);
            animator.SetBool("Sitting", sitting);
        }

        void CrossFade(string state, float duration)
        {
            if (animator == null || _currentState == state) return;
            _currentState = state;
            animator.CrossFadeInFixedTime(state, duration, 0);
        }

        /// <summary>播放一次性动作（Stab / HitKnife 等），duration 秒内不覆盖为移动状态。</summary>
        public void PlayAction(string trigger, float duration = 1.5f)
        {
            if (animator == null || !animator.enabled) return;
            animator.ResetTrigger(trigger);
            animator.SetTrigger(trigger);
            _actionUntil = Time.time + duration;
            _currentState = null;
        }

        /// <summary>
        /// 进入睡眠 / 尸体躺姿：用 Idle 首帧（双臂自然下垂）整体放平到床上。
        /// 不能播 Waking 首帧——那是一段蜷缩起身动作，放在床上会缩成一团。
        /// </summary>
        public void EnterSleepPose()
        {
            _sleeping = true;
            _sitting = false;
            if (_wakeRoutine != null)
            {
                StopCoroutine(_wakeRoutine);
                _wakeRoutine = null;
            }

            if (visualRoot != null)
            {
                visualRoot.localPosition = sleepLocalPosition;
                visualRoot.localRotation = Quaternion.Euler(sleepLocalEuler);
                visualRoot.localScale = sleepLocalScale;
            }

            if (animator != null)
            {
                animator.enabled = true;
                animator.speed = 0f;
                SetLocomotionBools(false, false, false);
                animator.Play("Idle", 0, 0f);
                animator.Update(0f);
            }
            _currentState = "Lying";
        }

        /// <summary>从睡眠姿态起身（NPCController.StandUpRoutine 调用），duration 与根节点位移同步。</summary>
        public void WakeUpVisual(float duration)
        {
            if (!_sleeping && _currentState != "Lying") return;
            _sleeping = false;
            if (_wakeRoutine != null) StopCoroutine(_wakeRoutine);
            _wakeRoutine = StartCoroutine(WakeRoutine(Mathf.Max(0.1f, duration)));
        }

        IEnumerator WakeRoutine(float duration)
        {
            if (animator != null)
            {
                animator.speed = 2.2f;
                animator.CrossFadeInFixedTime("StandUp", 0.15f, 0, 0f);
            }

            Vector3 fromPos = visualRoot != null ? visualRoot.localPosition : standLocalPosition;
            Quaternion fromRot = visualRoot != null ? visualRoot.localRotation : Quaternion.identity;
            Vector3 fromScale = visualRoot != null ? visualRoot.localScale : Vector3.one;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                if (visualRoot != null)
                {
                    visualRoot.localPosition = Vector3.Lerp(fromPos, standLocalPosition, k);
                    visualRoot.localRotation = Quaternion.Slerp(fromRot, Quaternion.identity, k);
                    visualRoot.localScale = Vector3.Lerp(fromScale, Vector3.one, k);
                }
                yield return null;
            }

            if (visualRoot != null)
            {
                visualRoot.localPosition = standLocalPosition;
                visualRoot.localRotation = Quaternion.identity;
                visualRoot.localScale = Vector3.one;
            }
            if (animator != null)
            {
                animator.speed = 1f;
                animator.CrossFadeInFixedTime("Idle", 0.25f);
            }
            _currentState = "Idle";
            _wakeRoutine = null;
        }

        /// <summary>立刻回到站立姿态（会议就座 / 重置时使用，不播起身过渡）。</summary>
        public void SnapAwake()
        {
            if (_wakeRoutine != null)
            {
                StopCoroutine(_wakeRoutine);
                _wakeRoutine = null;
            }
            _sleeping = false;
            _sitting = false;
            if (visualRoot != null)
            {
                visualRoot.localPosition = standLocalPosition;
                visualRoot.localRotation = Quaternion.identity;
                visualRoot.localScale = Vector3.one;
            }
            if (animator != null)
            {
                animator.speed = 1f;
                SetLocomotionBools(false, false, false);
                animator.Play("Idle", 0, 0f);
            }
            _currentState = "Idle";
        }

        /// <summary>会议就座 / 离席：切换到坐姿循环。</summary>
        public void SetSitting(bool value)
        {
            _sitting = value;
            if (animator == null || !animator.enabled || _sleeping) return;
            SetLocomotionBools(false, false, value);
            _currentState = null;
            CrossFade(value ? "Sit" : "Idle", 0.25f);
        }

        public void SetMaskVisible(bool visible)
        {
            if (maskTransform != null) maskTransform.gameObject.SetActive(visible);
            if (maskNumber != null) maskNumber.gameObject.SetActive(visible);
        }

        /// <summary>
        /// 摘面具：只摘掉面具和号码，斗篷留在身上，露出兜帽里的「同一张脸」。
        /// 不能启用 humanBody —— 那具 FBX 人形没有贴图（纯白材质），
        /// 一旦切过去看起来就像「人的贴图丢了」。
        /// </summary>
        public void Unmask()
        {
            SetMaskVisible(false);

            if (faceProxy != null)
            {
                faceProxy.SetActive(true);
                return;
            }

            // 兜底：没有脸代理的旧预制体才退回人形模型。
            if (cloakRenderer != null) cloakRenderer.enabled = false;
            if (humanBody != null)
            {
                humanBody.SetActive(true);
                Animator humanAnim = humanBody.GetComponentInChildren<Animator>(true);
                if (humanAnim != null)
                {
                    humanAnim.enabled = true;
                    if (humanAnim.runtimeAnimatorController == null && animator != null)
                        humanAnim.runtimeAnimatorController = animator.runtimeAnimatorController;
                    humanAnim.Play(_sitting ? "Sit" : "Idle", 0, 0f);
                }
            }
        }

        /// <summary>
        /// 尸体摘面具：只隐藏面具并露出「同一张脸」占位体。
        /// 尸体已经处于 Ragdoll 状态，不在此时启用第二套人形骨架（会与原姿势错位）。
        /// </summary>
        public void RevealFaceForCorpse()
        {
            SetMaskVisible(false);
            if (faceProxy != null) faceProxy.SetActive(true);
        }

        /// <summary>轮回重置：把面具 / 斗篷 / 人形恢复初始状态。</summary>
        public void ResetVisuals()
        {
            if (cloakRenderer != null) cloakRenderer.enabled = true;
            if (humanBody != null) humanBody.SetActive(false);
            if (faceProxy != null) faceProxy.SetActive(false);
            SetMaskVisible(true);
            SnapAwake();
        }
    }
}
