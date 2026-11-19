using System.Collections;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 小白桌宠大脑（第一步：本地反射逻辑）
    /// 状态机（11 状态，trigger 驱动，对应 PetAnimator.controller）：
    ///   Idle(默认) / Walk(追光标) / Run(双击生气·追击) / Drag(按住拖动) /
    ///   Drop(松开落地·播完回 Idle) / Pet(悬停触碰) / Jump / LookBack / Sit / Sleep / Talk
    /// 本阶段已接通本地交互：Idle, Walk, Run, Drag, Drop, Pet；
    /// 其余(Sit/Sleep/Talk/Jump/LookBack)由 AI 决策层经 PlayAction() 接入（下一步）。
    /// 保命红线：绝不动摄像机透明/背景Alpha/光照/阴影；一切网络请求仅可走 Coroutine 异步。
    /// </summary>
    /// <summary>
    /// 【运动 / 朝向策略】—— 一个开关决定这类生物**怎么动**。
    ///
    /// 原来框架只有一套（Yaw3D），那是给**人形 / 走兽**用的：
    ///   · 走路时把模型沿 Y 轴转到 ±90°，镜头看到的是它的侧面；
    ///   · 上下位移靠"跳"（X 上走、Y 上跳）。
    ///
    /// 但**龙、蛇、鱼、幽灵、浮游生物**两件事都不一样：
    ///   ① 走跑动画本身就是"正对镜头、身体起伏"，再叠 90° 转身就成了"一边走一边拧"；
    ///   ② 它们**不需要跳** —— 想去上面就直接游上去，所以应该做 X/Y 同时的 2D 直线运动。
    ///
    /// 所以这个开关**同时管**：转身方式、要不要跳、运动时头朝哪、停下怎么摆正。
    /// </summary>
    public enum PetTurnMode
    {
        Yaw3D,        // 转身 90° + 上下靠跳。人形 / 走兽（小白用的就是这个）
        Flip2D,       // 2D 直线游动 + 左右镜像 + 头朝运动方向 + 停下摆正。龙 / 鱼 / 蛇 / 精灵
        LockFacing    // 2D 直线游动 + 永不转身（头也不偏）+ 停下摆正。纯漂浮型
    }

    public class PetBrain : MonoBehaviour
    {
        const int I_IDLE = 0, I_WALK = 1, I_RUN = 2, I_DRAG = 3, I_DROP = 4, I_PET = 5,
                  I_JUMP = 6, I_LOOK = 7, I_SIT = 8, I_SLEEP = 9, I_TALK = 10;
        static readonly string[] STATE = { "Idle", "Walk", "Run", "Drag", "Drop", "Pet", "Jump", "LookBack", "Sit", "Sleep", "Talk" };
        static readonly string[] TRIG  = { "doIdle", "doWalk", "doRun", "doDrag", "doDrop", "doPet", "doJump", "doLookBack", "doSit", "doSleep", "doTalk" };

        [Header("移动参数")]
            public float walkSpeed = 0.85f;     // Walk 周期 1.0s，一步约0.85m → 0.85 m/s（防滑步）
        public float runSpeed = 1.49f;      // Run 周期 0.917s，步距更大 → 1.49 m/s（防滑步）
        public float speedTune = 1f;        // 全局微调：滑步就调它
        public float turnSpeed = 160f;      // 转身速度（度/秒）
                    public float baseYaw = 0f;              // 基准朝向补偿（模型正面朝 +Z 时保持 0）

        [Header("朝向 / 运动策略")]
        [Tooltip("Yaw3D=转身90°+上下靠跳（人形/走兽）；"
               + "Flip2D=2D直线游动+左右镜像+头朝运动方向（龙/鱼/蛇）；"
               + "LockFacing=2D直线游动但永不转身（漂浮型）")]
        public PetTurnMode turnMode = PetTurnMode.Yaw3D;

        [Header("非四足模式：头朝运动方向")]
        [Tooltip("运动时身体朝运动方向倾斜的最大角度。0=完全不倾斜；90=头完全指向运动方向")]
        public float steerTiltMax = 30f;
        [Tooltip("倾斜跟随的快慢（秒）。越小越灵敏")]
        public float steerSmoothTime = 0.22f;
        public float turnSmoothTime = 0.25f;    // 转身平滑时间（0.2~0.3s 惯性感）
        public float turnWalkSlow = 0.5f;       // 回正段 Walk 动画减速倍率
        public float jumpThreshold = 0.35f;     // 垂直差超过它才起跳（减少跳跃次数）
        float moveDirX = 0f;                    // 当前水平移动方向：-1 左 / 0 停 / +1 右
        UnityEngine.Vector2 moveVec = UnityEngine.Vector2.zero;  // 本帧真实运动方向（非四足模式用）
        float curTiltZ, steerVelZ;              // 绕 Z 的倾斜（头朝运动方向）
        float turnVel;                          // SmoothDampAngle 速度
        float angleGoal, queuedGoal;            // 当前 / 排队的旋转目标角
        bool hasQueued;                         // 是否处于“先转回正面”的阶段
        int facing = 0;                         // 当前朝向侧：-1 左 / 0 正 / +1 右
        public float chaseRange = 4f;       // 光标多近算附近→去走走
            public float arriveStop = 0.15f;    // 离目标这么近就停

        [Header("跳跃与2.5D边界")]
        public float jumpDur = 1f;          // Jump 动画时长（抛物线总时长）
        public float jumpArc = 0.6f;        // 跳跃最高抬升高度
            public float screenMargin = 0.3f;   // 距屏幕边缘留边
        [Tooltip("头顶高出脚底多少米。0 = 启动时自动量（推荐）。\n" +
                 "⚠️ 边界夹的是【脚底】，如果不减去这个高度，它走到屏幕最上面时头和上半身会出画。")]
        public float bodyHeightAboveFoot = 0f;

        // 自动测量出来的"头顶高出脚底"距离
        float headAboveFoot = 1.1f;

        [Header("光标对接（脚底原点 → 身体接光标）")]
        public float bodyOffsetY = 0.7f;    // 身体/头相对脚底的高度：光标对准这个高度，而非脚

        [Header("漫游(AI决策)")]
        public float wanderMin = 3f, wanderMax = 5f;  // 漫游持续区间
        public float wanderJumpCooldown = 2.5f;       // 漫游中跳跃冷却
        public float scriptedTimeout = 15f;           // move_to 走这么久还没到就放弃（防卡死兜底）

        [Header("鼠标交互")]
        public float doubleClickWindow = 0.4f;  // 双击判定窗口
        public float petHoverTime = 0.5f;       // 悬停触发 Pet
        public float angryTimeout = 8f;         // 生气追击最长持续

        [Header("形态（多形态预留）")]
        [Tooltip("当前形态名。⚠️ 现在只是记录状态，不会真的换模型/换动画")]
        public string currentForm = "default";

        /// <summary>
        /// 【骨架】切换到某个形态 —— 现在只改名字 + 打日志，外观和动画都【不会变】。
        /// 真正的换模型/换动画在 PetVisualRoot.SwitchForm() 里，那边也还是 TODO。
        /// 留这个入口是为了让"变身"这件事从一开始就有落脚点，而不是将来到处插代码。
        /// </summary>
        public void SwitchForm(string formName)
        {
            if (string.IsNullOrEmpty(formName)) return;
            currentForm = formName;

            PetFramework.PetVisualRoot root = null;
    #if UNITY_2022_2_OR_NEWER
            root = UnityEngine.Object.FindFirstObjectByType<PetFramework.PetVisualRoot>();
    #else
            root = UnityEngine.Object.FindObjectOfType<PetFramework.PetVisualRoot>();
    #endif
            bool ok = root != null && root.SwitchForm(formName);
            Log("形态切换（骨架）→ " + formName
                + (ok ? "　⚠ 外观尚未改变" : "　（没有多形态配置，仅记录名字）"));
        }

        /// <summary>当前能变成哪些形态（给 AI 工具描述用）。</summary>
        public string AvailableForms()
        {
            PetFramework.PetVisualRoot root = null;
    #if UNITY_2022_2_OR_NEWER
            root = UnityEngine.Object.FindFirstObjectByType<PetFramework.PetVisualRoot>();
    #else
            root = UnityEngine.Object.FindObjectOfType<PetFramework.PetVisualRoot>();
    #endif
            return root != null ? root.AvailableFormsReadable() : "（只有当前形态）";
        }

        Animator anim; Camera cam;
        UnityEngine.Vector3 baseScale = UnityEngine.Vector3.one;   // Flip2D 用：记住原始缩放
        float planeZ;    // 2D 横板桌宠：锁死屏幕深度(Z)，只在屏幕平面内上下左右移动
        int intent = -1;
        bool dragging, angry;
        float lastDown = -99f, angryStart = 0f, hoverEnter = -99f;
        bool armedHover;
        Coroutine singleClick;
        Vector3 target;
        bool jumping, wandering;
        float wanderUntil = -1f, lastWanderJump = -99f;
            float lastInteract = -999f;
        public System.Action<float> onIdleReport;            // 空闲阈值上报钩子（PetAPI 注入）
        public float[] idleBands = { 20f, 60f, 180f };   // 空闲上报阈值（秒）：20/60/180
        public System.Func<bool> isApiBusy;                  // API 请求锁（PetAPI 注入）：忙时忽略空闲触发
        int idleBandIdx;
        public bool freezeMove;   // 聊天输入时冻结移动（保持 Idle/Talk，不去追光标）

        // ---------- AI 工具：move_to 的目的地 ----------
        UnityEngine.Vector3 scriptedTarget;
        bool hasScripted;
        float scriptedSince = -1f;

        /// <summary>
        /// 【AI 工具 move_to】让小白走到屏幕上的某个位置（世界坐标，Z 锁 0）。
        /// 会自动把坐标钳进可见范围，防止 AI 给个屏幕外的点把小白弄丢。
        /// </summary>
        public void MoveTo(float x, float y)
        {
            UnityEngine.Vector3 saved = transform.position;
            transform.position = new UnityEngine.Vector3(x, y, planeZ);
            ClampToScreen();
            scriptedTarget = new UnityEngine.Vector3(transform.position.x, transform.position.y + bodyOffsetY, planeZ);
            transform.position = saved;
            hasScripted = true;
            scriptedSince = Time.time;
            Log("AI → 走到 (" + scriptedTarget.x.ToString("F2") + ", " + (scriptedTarget.y - bodyOffsetY).ToString("F2") + ")");
        }

        void Awake()
        {
            anim = GetComponent<Animator>();
            cam = Camera.main;
            baseScale = transform.localScale;
            planeZ = 0f;    // 锁定 Z=0（约束）
            lastInteract = Time.time;
            idleBandIdx = 0;
        }

        void Start() { StartCoroutine(MeasureBody()); }

        /// <summary>
        /// 量一下"头顶比脚底高多少"，供屏幕边界使用。
        /// 为什么必须量：ClampToScreen 夹的是 transform.position（= 脚底），
        /// 而身体是往上长的 —— 不减去这个高度，脚一到屏幕上沿，头和上半身就出画了。
        /// 用 renderer.bounds 而不是写死数字，换模型/换形态都不会错。
        /// </summary>
        System.Collections.IEnumerator MeasureBody()
        {
            yield return null;
            yield return null;      // 等动画器把初始姿势摆好再量

            if (bodyHeightAboveFoot > 0f)      // 手动指定优先
            {
                headAboveFoot = bodyHeightAboveFoot;
                Log("体型：用 Inspector 指定的 头顶高出脚底 " + headAboveFoot.ToString("F2") + " 米");
            }
            else
            {
                var r = GetComponentInChildren<Renderer>(true);
                if (r != null)
                {
                    float top = r.bounds.max.y - transform.position.y;
                    if (top > 0.05f && top < 20f)
                    {
                        headAboveFoot = top;
                        Log("体型：自动测得 头顶高出脚底 " + top.ToString("F2")
                            + " 米（身体底部 " + (transform.position.y - r.bounds.min.y).ToString("F2") + " 米）"
                            + "　→ 屏幕边界会按这个算，头不会再伸出屏幕上沿");
                        yield break;
                    }
                }
                Log("体型：量不出来，用默认 " + headAboveFoot.ToString("F2") + " 米");
            }
        }

        void Update()
        {
            if (anim == null) return;
            moveDirX = 0f;                 // 每帧重置，由移动逻辑写入
            moveVec = UnityEngine.Vector2.zero;
            UpdateTarget();
            if (hasScripted) target = scriptedTarget;   // AI 指定的目的地优先于鼠标
            ClampToScreen();
            CheckIdleReport();
            if (freezeMove && !dragging)
            {
                if (!jumping && !IsOneShotIntent()) Fire(I_IDLE);   // 输入中：静止保持 Idle（Talk 播放中不打断）
                return;
            }

            if (dragging) { MoveToward(runSpeed); return; }
            if (angry)
            {
                ChaseMove(runSpeed);
                if (Dist() <= arriveStop || Time.time - angryStart > angryTimeout)
                { angry = false; Fire(I_IDLE); Log("生气结束 → Idle"); }
                return;
            }
            if (jumping) return;   // 跳跃协程接管，落地后恢复
            if (hasScripted)       // AI 工具 move_to：走过去，到了就回 Idle
            {
                // ⚠️ 到达判据必须【两轴分别判】：ChaseMove 只在 X 上走路、Y 靠跳，
                //    而 Dist() 是含 Y 的直线距离 ⇒ 若 0.15 < |dy| ≤ jumpThreshold，
                //    它会永远差那么一点点、永远到不了 → hasScripted 卡死不放 → 小白彻底不动。
                float ddx = scriptedTarget.x - transform.position.x;
                float ddy = (scriptedTarget.y - bodyOffsetY) - transform.position.y;
                if (Mathf.Abs(ddx) <= arriveStop && Mathf.Abs(ddy) <= arriveStop)
                { hasScripted = false; Fire(I_IDLE); Log("抵达 AI 指定位置 → Idle"); }
                else if (Time.time - scriptedSince > scriptedTimeout)
                { hasScripted = false; Fire(I_IDLE); Log("⚠️ 走了 " + scriptedTimeout + "s 仍没到，放弃 → Idle（防卡死）"); }
                else ChaseMove(walkSpeed, arriveStop);
                return;
            }
            // ★★ 坐 / 睡是【保持姿势】—— 必须在【悬停判定之前】拦住：
            //    放在悬停后面的话，鼠标一靠近就 Fire(I_PET) 把坐姿打断，
            //    接着就走进下面的追光标逻辑 ⇒ 表现就是"坐下了又站起来追鼠标"。
            //    保持姿势期间：不追光标、不自动回 Idle、不被悬停打断。
            //    唤醒方式只剩三种明确的：鼠标点击 / 拖动 / AI 新指令 / move_to。
            //    （wandering 例外：那是显式要求漫游，得让它走。）
            if (IsHoldPose(intent) && !wandering) return;

            if (armedHover && Time.time - hoverEnter >= petHoverTime)
            { armedHover = false; Fire(I_PET); Log("悬停超" + petHoverTime + "s → Pet"); }

            if (wandering) { WanderStep(); if (Time.time >= wanderUntil) StopWander(); }
            else
            {
                float d = Dist();
                if (d > arriveStop && d <= chaseRange) ChaseMove(walkSpeed);   // 仅光标接近才追
                else if (!IsOneShotIntent()) Fire(I_IDLE);
            }
        }

        void LateUpdate()
        {
            transform.position = new UnityEngine.Vector3(transform.position.x, transform.position.y, planeZ);
            ClampToScreen();   // 每帧移动后强制把 X/Y 钳进屏幕边界（含边缘留白）

            int want = Mathf.Abs(moveDirX) > 0.1f ? (moveDirX > 0f ? 1 : -1) : 0;

            // ───────── 2D 翻转 / 锁定朝向：完全不碰 Y 轴旋转 ─────────
            //  龙 / 鱼 / 蛇 的行走动画本来就是「正对镜头、身体起伏」，
            //  再叠一个 90° 转身就成了「一边走一边拧」。这两档直接不走 Yaw 逻辑。
            if (turnMode != PetTurnMode.Yaw3D)
            {
                hasQueued = false; angleGoal = baseYaw; turnVel = 0f;

                bool moving = moveVec.sqrMagnitude > 1e-6f;

                // ───── ① 左右镜像（只按真正的水平分量判）─────
                if (turnMode == PetTurnMode.Flip2D)
                {
                    if (moving && Mathf.Abs(moveVec.x) > 0.05f) facing = moveVec.x > 0f ? 1 : -1;
                    // 停下时 sign=0 ⇒ 取消镜像 = "摆正"
                    int sign = moving ? facing : 0;
                    float sx = Mathf.Abs(baseScale.x) * (sign < 0 ? -1f : 1f);
                    var s = transform.localScale;
                    if (!Mathf.Approximately(s.x, sx)) { s.x = sx; transform.localScale = s; }
                }
                else if (moving) facing = moveVec.x >= 0f ? 1 : -1;   // LockFacing：不镜像

                // ───── ② 头朝运动方向：绕 Z 倾斜；停下 SmoothDamp 回 0 ─────
                //   ⚠️ 注意是 atan2(x, y) —— 0° = 头朝上，+90° = 头朝右。
                float targetZ = 0f;
                if (moving && steerTiltMax > 0.01f)
                {
                    float ang = Mathf.Atan2(moveVec.x, moveVec.y) * Mathf.Rad2Deg;
                    targetZ = Mathf.Clamp(-ang, -steerTiltMax, steerTiltMax);
                    // ⚠️ 负缩放会把这个 Z 旋转的【视觉方向】反过来 ⇒ 补偿一下
                    if (transform.localScale.x < 0f) targetZ = -targetZ;
                }
                curTiltZ = Mathf.SmoothDampAngle(curTiltZ, targetZ, ref steerVelZ,
                                                Mathf.Max(0.01f, steerSmoothTime));
                transform.rotation = UnityEngine.Quaternion.Euler(0f, baseYaw, curTiltZ);

                if (anim != null) anim.speed = 1f;
                return;
            }

            // 换向时走两段：先回正面(baseYaw)再转目标侧；否则一段直达
            if (want != facing)
            {
                bool cross = (facing == 1 && want == -1) || (facing == -1 && want == 1);
                facing = want;
                float finalYaw = want * 90f + baseYaw;
                if (cross) { angleGoal = baseYaw; queuedGoal = finalYaw; hasQueued = true; }
                else { angleGoal = finalYaw; hasQueued = false; }
                turnVel = 0f;   // 重置平滑速度，保证每段都从 0 开始（无抽搯）
            }

            float cur = transform.rotation.eulerAngles.y;
            float next = Mathf.SmoothDampAngle(cur, angleGoal, ref turnVel, turnSmoothTime);
            transform.rotation = UnityEngine.Quaternion.Euler(0f, next, 0f);

            // 到达中途点(正面) → 接上排队的目标侧，结束回正段
            if (hasQueued && Mathf.Abs(Mathf.DeltaAngle(next, angleGoal)) < 0.5f)
            { angleGoal = queuedGoal; hasQueued = false; turnVel = 0f; }

            // 回正段：Walk 动画放慢（减速转身感）；其余恢复常速
            if (anim != null) anim.speed = hasQueued ? turnWalkSlow : 1f;
        }

        // ---------- 鼠标事件（依赖 Collider + Camera）----------
        void OnMouseDown()
        {
            Touch();
            if (Time.time - lastDown <= doubleClickWindow)
            {
                if (singleClick != null) { StopCoroutine(singleClick); singleClick = null; }
                angry = true; angryStart = Time.time;
                Fire(I_RUN);
                Log("双击 → Run 生气追击");
                return;
            }
            lastDown = Time.time;
            if (singleClick != null) StopCoroutine(singleClick);
            singleClick = StartCoroutine(SingleClickConfirm());
        }

        void OnMouseDrag()
        {
            if (angry) return; // 生气中不拖
            Touch();
            if (!dragging) { dragging = true; Fire(I_DRAG); Log("按住 → Drag"); }
        }

        void OnMouseUp()
        {
            if (dragging) { dragging = false; Fire(I_DROP); Log("松开 → Drop（播完回 Idle）"); }
        }

        void OnMouseEnter() { Touch(); armedHover = true; hoverEnter = Time.time; TransparentWindow.mouseOverPet = true; }
        void OnMouseExit()
        {
            armedHover = false;
            // ⚠️ 主面板开着时别把「可交互」标志清掉：光标从桌宠移到面板上会触发这里，
            //    清成 false 会让整窗重新变回 WS_EX_TRANSPARENT，系统直接跳过窗口 ⇒ 面板点不动。
            var mgr = PetWindowManager.Instance;
            if (mgr == null || !mgr.IsDashboardOpen) TransparentWindow.mouseOverPet = false;
        }

        IEnumerator SingleClickConfirm()
        {
            float t0 = Time.time;
            while (Time.time - t0 < doubleClickWindow) yield return null;
            singleClick = null;
            if (!dragging) { Fire(I_PET); Log("单击 → Pet 触碰"); }
        }

        // ---------- 状态机驱动 ----------
        void Fire(int i)
        {
            if (intent == i) return;
            if (intent >= 0) anim.ResetTrigger(TRIG[intent]);
            intent = i;
            anim.SetTrigger(TRIG[i]);
            PetContext.LastPetAction = STATE[i];   // 报给环境感知层，供 AI 参考“我现在在干嘛”
        }

        bool IsOneShotIntent()
        {
            return intent == I_PET || intent == I_DROP || intent == I_JUMP ||
                   intent == I_LOOK || intent == I_TALK;
        }

        /// <summary>
        /// 坐 / 睡是【保持姿势】：播完要一直保持，不能自动回 Idle。
        /// ⚠️ 别把它们塞进 IsOneShotIntent —— 那个名单是"播完就结束"的一次性动作，
        ///    语义相反。（历史上就是因为漏了这一步，AI 说"坐下"它下一帧就站起来了。）
        /// </summary>
        static bool IsHoldPose(int i)
        {
            return i == I_SIT || i == I_SLEEP;
        }

        /// <summary>当前是不是"保持姿势"（坐着/睡着）。说话、TTS 这类动作不该把它顶掉。</summary>
        public bool IsHoldingPose { get { return IsHoldPose(intent); } }

        /// <summary>
        /// 说话时才切 Talk 动画 —— 但如果它正在坐/睡，就别切了。
        /// ⚠️ 这是"回复"和"动作"互不干扰的关键：模型给「好的，白虎大哥」+ Sleep 时，
        ///    念这句话不能把它从睡姿里拽起来。
        /// </summary>
        public void PlayTalkIfAllowed()
        {
            if (IsHoldPose(intent)) return;
            PlayAction("Talk");
        }

        /// <summary>AI 决策层统一入口：把 action 映射到状态并触发（未知回 Idle）。</summary>
        public void PlayAction(string action)
        {
            if (string.IsNullOrEmpty(action)) { Fire(I_IDLE); return; }
            string a = action.Trim();
            if (a.Equals("wander", System.StringComparison.OrdinalIgnoreCase)) { WanderStart(); return; }
            int i = -1;
            for (int k = 0; k < STATE.Length; k++)
                if (string.Equals(STATE[k], a, System.StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            if (i < 0) { Log("未知 action [" + action + "] → Idle"); i = I_IDLE; }
            Fire(i);
        }

        // ---------- 移动 / 目标 ----------
        void UpdateTarget() { target = MouseGroundPoint(); }

        UnityEngine.Vector3 MouseGroundPoint()
        {
            // 鼠标屏幕坐标 → 投射到屏幕平面（深度 planeZ 处），得 X/Y
            if (cam == null) return transform.position;
            UnityEngine.Ray r = cam.ScreenPointToRay(Input.mousePosition);
            if (Mathf.Abs(r.direction.z) < 1e-4f) return transform.position;
            float t = (planeZ - r.origin.z) / r.direction.z;
            if (t <= 0) return transform.position;
            UnityEngine.Vector3 p = r.origin + r.direction * t; p.z = planeZ; return p;
        }

        float Dist()
        {
            // 用【身体中心】而非脚底原点去量到光标的距离
            UnityEngine.Vector3 body = transform.position + new UnityEngine.Vector3(0f, bodyOffsetY, 0f);
            UnityEngine.Vector3 d = target - body; d.z = 0f; return d.magnitude;
        }

        void MoveToward(float speed)
        {
            FaceTarget();
            UnityEngine.Vector3 from = transform.position; from.z = planeZ;
            transform.position = UnityEngine.Vector3.MoveTowards(from, target, speed * speedTune * Time.deltaTime);
            transform.position = new UnityEngine.Vector3(transform.position.x, transform.position.y, planeZ);
        }
        void FaceTarget()
        {
            // 记录水平移动方向（右=+1 / 左=-1），供 LateUpdate 转身用
            float dx = target.x - transform.position.x;
            if (Mathf.Abs(dx) > 1e-3f) moveDirX = dx >= 0f ? 1f : -1f;
        }

            #region 2.5D平台跳跃移动（约束：X→Walk / Y→Jump / 禁Y用Walk / Z锁0 / 边界Clamp）
        void ChaseMove(float speed) { ChaseMove(speed, jumpThreshold); }

        /// <summary>追目标（只在 X 上走路，Y 靠跳）。
        /// yThresh = 多大的垂直差才值得跳一次。AI 指定目的地时传小值（arriveStop），
        /// 否则 Y 差落在 (arriveStop, jumpThreshold] 之间时会永远差一点、到不了、卡死不动。</summary>
        void ChaseMove(float speed, float yThresh)
        {
            // ───── 非四足（龙 / 蛇 / 鱼）：【不跳】，X/Y 同时朝目标直线游过去 ─────
            //  四足生物上台阶才需要"跳"；龙是浮游的，想去上面直接游上去就行。
            if (turnMode != PetTurnMode.Yaw3D)
            {
                float gx = target.x;
                float gy = target.y - bodyOffsetY;
                float mdx = gx - transform.position.x;
                float mdy = gy - transform.position.y;
                float mdist = Mathf.Sqrt(mdx * mdx + mdy * mdy);
                if (mdist <= 1e-4f) return;
                UnityEngine.Vector2 dir = new UnityEngine.Vector2(mdx, mdy) / mdist;
                moveDirX = dir.x >= 0f ? 1f : -1f;
                moveVec = dir;                       // 给 LateUpdate 做"头朝运动方向"
                Fire(I_WALK);
                transform.position += new UnityEngine.Vector3(dir.x, dir.y, 0f)
                                      * speed * speedTune * Time.deltaTime;
                return;
            }

            float goalY = target.y - bodyOffsetY;   // 脚应停的高度：让身体中心对齐光标
            float dx = target.x - transform.position.x;
            float dy = goalY - transform.position.y;
            if (Mathf.Abs(dy) > yThresh && !jumping) { StartJump(goalY); return; }  // 垂直差 → 只走 Jump
            if (Mathf.Abs(dx) > arriveStop)
            {
                FaceTarget();
                Fire(I_WALK);
                if (!hasQueued)   // 回正转身段：原地慢速刷 Walk，暂不平移
                    transform.position += new UnityEngine.Vector3(Mathf.Sign(dx) * speed * speedTune * Time.deltaTime, 0f, 0f);
            }
        }

        void StartJump(float destY)
        {
            if (jumping) return;
            StartCoroutine(JumpRoutine(destY));
        }
        IEnumerator JumpRoutine(float destY)
        {
            jumping = true; Fire(I_JUMP);
            float y0 = transform.position.y, t = 0f, T = jumpDur;
            while (t < T)
            {
                t += Time.deltaTime; float k = Mathf.Clamp01(t / T);
                float p = Mathf.Lerp(y0, destY, k) + jumpArc * 4f * k * (1f - k);  // 抛物线
                transform.position = new UnityEngine.Vector3(transform.position.x, p, planeZ);
                ClampToScreen();
                yield return null;
            }
            transform.position = new UnityEngine.Vector3(transform.position.x, destY, planeZ);
            jumping = false;   // 落地，交给 Update 决定回 Idle 或继续走
        }

        void ClampToScreen()
        {
            if (cam == null) return;
            UnityEngine.Vector3 bl = RayFor(cam.ViewportPointToRay(new UnityEngine.Vector3(0f, 0f, 0f)));
            UnityEngine.Vector3 tr = RayFor(cam.ViewportPointToRay(new UnityEngine.Vector3(1f, 1f, 1f)));
            float minX = Mathf.Min(bl.x, tr.x), maxX = Mathf.Max(bl.x, tr.x);
            float minY = Mathf.Min(bl.y, tr.y), maxY = Mathf.Max(bl.y, tr.y);

            // ★ 顶部边界必须把"身体高度"减掉：
            //   transform.position 是【脚底】，而身体往上长 headAboveFoot 米。
            //   以前只减 screenMargin ⇒ 脚刚碰到屏幕上沿，头和上半身就出画了。
            float bodyTop = bodyHeightAboveFoot > 0f ? bodyHeightAboveFoot : headAboveFoot;
            float loY = minY + screenMargin;
            float hiY = maxY - screenMargin - bodyTop;
            if (hiY < loY) hiY = loY;          // 屏幕矮得离谱时别把上下界搞反

            UnityEngine.Vector3 p = transform.position;
            p.x = Mathf.Clamp(p.x, minX + screenMargin, maxX - screenMargin);
            p.y = Mathf.Clamp(p.y, loY, hiY);
            transform.position = p;
        }
        UnityEngine.Vector3 RayFor(UnityEngine.Ray r)
        {
            if (Mathf.Abs(r.direction.z) < 1e-4f) return transform.position;
            float t = (planeZ - r.origin.z) / r.direction.z;
            return r.origin + r.direction * t;
        }

        /// <summary>
        /// 【本地兜底漫游】随机挑屏幕上一个点走过去 —— 不依赖鼠标位置。
        /// 原来的 WanderStart 是朝鼠标走的，鼠标要是正好贴在小白身上就会立刻中断，
        /// 起不到"自己溜达"的效果。
        /// </summary>
        public void WanderRandom()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) { WanderStart(); return; }

            UnityEngine.Vector3 bl = RayFor(cam.ViewportPointToRay(new UnityEngine.Vector3(0.08f, 0.12f, 0f)));
            UnityEngine.Vector3 tr = RayFor(cam.ViewportPointToRay(new UnityEngine.Vector3(0.92f, 0.88f, 0f)));
            float minX = Mathf.Min(bl.x, tr.x), maxX = Mathf.Max(bl.x, tr.x);
            float minY = Mathf.Min(bl.y, tr.y), maxY = Mathf.Max(bl.y, tr.y);

            float rx = UnityEngine.Random.Range(minX, maxX);
            float ry = UnityEngine.Random.Range(minY, maxY);
            Log("本地兜底 → 随机溜达到 (" + rx.ToString("F2") + ", " + ry.ToString("F2") + ")");
            MoveTo(rx, ry);
        }

        // ---------- 漫游（AI{"action":"wander"}）----------
        public void WanderStart()
        {
            if (wandering) return;
            wandering = true;
            wanderUntil = Time.time + UnityEngine.Random.Range(wanderMin, wanderMax);
            lastWanderJump = Time.time;
            PetContext.LastPetAction = "wander";
            Log("AI → 漫游 " + (wanderUntil - Time.time).ToString("0.0") + "s");
        }
        void WanderStep()
        {
            if (Dist() <= chaseRange) { StopWander(); Log("鼠标靠近 → 打断漫游"); return; }
            FaceTarget(); Fire(I_WALK);

            // 非四足：漫游也不跳，直接 2D 直线游过去
            if (turnMode != PetTurnMode.Yaw3D)
            {
                UnityEngine.Vector3 body = transform.position + new UnityEngine.Vector3(0f, bodyOffsetY, 0f);
                UnityEngine.Vector2 d = new UnityEngine.Vector2(target.x - body.x, target.y - body.y);
                if (d.sqrMagnitude > 1e-6f)
                {
                    d.Normalize();
                    moveDirX = d.x >= 0f ? 1f : -1f;
                    moveVec = d;
                    transform.position += new UnityEngine.Vector3(d.x, d.y, 0f) * walkSpeed * speedTune * Time.deltaTime;
                }
                return;
            }

            int dir = transform.position.x <= target.x ? 1 : -1;
            if (!hasQueued) transform.position += new UnityEngine.Vector3(dir * walkSpeed * speedTune * Time.deltaTime, 0f, 0f);
            if (!jumping && Time.time - lastWanderJump >= wanderJumpCooldown)
            { lastWanderJump = Time.time; StartJump(transform.position.y); }
        }
        void StopWander()
        {
            if (!wandering) return;
            wandering = false;
            Log("漫游结束 → Idle");
        }
        #endregion
            void CheckIdleReport()
        {
            // ★ 坐着/睡着的时候【不做自主决策】：
            //   提示词一直催它"别当木头桩子、主动走走"，空闲 20/60/180 秒会触发 AI，
            //   它多半就会调 wander_around 自己站起来溜达 —— 主人让它坐下/睡觉就白坐了。
            //   唤醒：主人点击/拖动/说话（都会重置 lastInteract 并改 intent）。
            if (IsHoldPose(intent)) return;

            if (isApiBusy != null && isApiBusy()) return;   // 正在请求中 → 忽略本次空闲触发（防并发）
            float idle = Time.time - lastInteract;
            while (idleBandIdx < idleBands.Length && idle >= idleBands[idleBandIdx])
            {
                float band = idleBands[idleBandIdx]; idleBandIdx++;
                if (onIdleReport != null) onIdleReport(band);
                else Log("空闲 " + band + "s（待接 AI 上报）");
            }
        }
        void Touch() { lastInteract = Time.time; idleBandIdx = 0; }

        // ---------- 预留：AI 层读取空闲时长 ----------
        public float GetIdleSeconds() { return Time.time - lastInteract; }

        void Log(string m) { Debug.Log("[PetBrain] " + m); }
    }
}
