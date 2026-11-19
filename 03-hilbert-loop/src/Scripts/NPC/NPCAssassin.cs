using System.Collections;
using HilbertLoop.BT;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 1号杀9号流程（3.2 修正补丁重写，挂在 1号 NPC 上）。
    /// 按机制说明书的「循环既定事实」，用真实路径重演那场谋杀——12 步：
    ///   ① 走到桌边，取走长绳 / 钉子 / 锤子 / 磁铁 / 匕首（桌面物件消失）
    ///   ② 在窗边把长绳系在晾衣架上（绳子系好状态）
    ///   ③ 推开窗、翻出窗外（落到檐台，跨过窗台的真实翻越动画）
    ///   ④ 沿外墙挪到 2 号房窗外（贴墙弧线攀爬）
    ///   ⑤ 在 2 号房窗外墙缝钉下支点钉（钉子 -1，绳子绕过钉子）
    ///   ⑥ 继续挪到 9 号房窗外
    ///   ⑦ 探头查看——确认 9 号睡在床上
    ///   ⑧ 推开 9 号窗、翻进 9 号房
    ///   ⑨ 用匕首杀死 9 号（触发 NPCDie(9)）
    ///   ⑩ 翻出窗外、合上窗，用外磁铁隔着玻璃把锁销推上去；抽走外磁铁，内磁铁掉回窗台，
    ///      锁销机械卡死（磁铁不留在玻璃上）
    ///   ⑪ 原路返回 1 号房（9 → 2 → 1，翻进 1 号窗内）
    ///   ⑫ 关上窗，回房等待（流程完成）
    /// 每步都有状态标记（CurrentStepIndex / CurrentStepName / FlowCompleted）与日志。
    /// 只在 1号 不是玩家扮演时执行；玩家扮演 1 号时由玩家手动操作。
    /// </summary>
    public class NPCAssassin : MonoBehaviour
    {
        [Header("时序（秒）")]
        [Tooltip("醒来后开始流程前的停顿")]
        public float startDelay = 2.5f;

        [Tooltip("每个简化步骤之间的停留")]
        public float stepWait = 0.8f;

        [Tooltip("去桌边 / 窗边寻路阶段的超时（超时保留在位，后续动画接管）")]
        public float walkTimeout = 8f;

        // ------------------------------------------------------------------
        // 状态标记（只读：供验证与玩家观测）
        // ------------------------------------------------------------------

        /// <summary>当前步骤序号（0 = 未开始，1~12 = 进行中）。</summary>
        public int CurrentStepIndex { get; private set; }

        /// <summary>当前步骤名（"未开始" / "完成"）。</summary>
        public string CurrentStepName { get; private set; } = "未开始";

        /// <summary>12 步是否全部完成。</summary>
        public bool FlowCompleted { get; private set; }

        /// <summary>流程是否正在执行（含暂停）。</summary>
        public bool FlowRunning { get; private set; }

        /// <summary>私聊等原因造成的暂停。</summary>
        public bool FlowPaused { get; private set; }

        /// <summary>已经翻出窗外、还在檐台 / 别人房间里的阶段：此时不能对话。</summary>
        public bool IsOnExteriorRoute
        {
            get { return FlowRunning && CurrentStepIndex >= 3 && CurrentStepIndex <= 11; }
        }

        /// <summary>私聊开始：冻结流程，等人说完再继续。</summary>
        public void PauseForPrivateChat()
        {
            if (!FlowRunning) return;
            FlowPaused = true;
            if (_nav != null) _nav.Stop();
            Debug.Log($"[NPC 1] 私聊开始，杀手流程暂停在第 {CurrentStepIndex}/12 步。");
        }

        /// <summary>私聊结束：恢复流程。</summary>
        public void ResumeAfterPrivateChat()
        {
            if (!FlowPaused) return;
            FlowPaused = false;
            Debug.Log($"[NPC 1] 私聊结束，杀手流程从第 {CurrentStepIndex}/12 步继续。");
        }

        IEnumerator WaitWhilePaused()
        {
            if (!FlowPaused) yield break;
            if (_nav != null) _nav.Stop();
            if (_npc != null) _npc.SetLeaningOut(false);
            while (FlowPaused) yield return null;
        }

        NPCController _npc;
        NPCNavigator _nav;
        BehaviourTreeRunner _bt;

        Transform _room1, _room2, _room9;
        WindowController _window1, _window9;
        RopeTraverseSystem _rope;

        /// <summary>会议开始等外部原因中止流程：停止全部协程并关闭组件（3.4）。</summary>
        public void AbortFlow()
        {
            StopAllCoroutines();
            FlowRunning = false;
            FlowPaused = false;
            enabled = false;

            // 流程可能停在「人还挂在窗外探头」的姿态：必须把倾斜/探头姿势还原，
            // 否则会议结束后 1 号会保持歪在窗边的姿势站在大厅里。
            if (_npc != null) _npc.SetLeaningOut(false);

            // 流程可能停在“绳已经铺出去、人还没走完”的半途：
            // 中止时必须把 1 号的绳与支点钉一起收回，否则窗外会留下无主的绳子。
            if (_rope == null) _rope = RopeTraverseSystem.Instance;
            if (_rope != null) _rope.RecallRoute(RopeState.Npc1Owner);

            Debug.Log($"[NPC 1] 杀手流程已中止（当前步骤：{CurrentStepName}）。");
        }

        IEnumerator Start()
        {
            _npc = GetComponent<NPCController>();
            if (_npc == null || _npc.Number != 1)
            {
                enabled = false;
                yield break;
            }

            _nav = _npc.navigator;
            _bt = _npc.behaviourTree;

            // 玩家扮演 1 号 / 这一轮没有第九席：不需要自动流程。
            if (CycleState.isPlayerAs1) yield break;
            if (CycleState.currentKnownMax < 9) yield break;

            yield return new WaitForSeconds(startDelay);

            if (CycleState.isPlayerAs1) yield break;   // 玩家扮演 1号：不自动执行
            if (CycleState.kill9Executed) yield break; // 9号已被结算（防御）

            // 1号 AI 按 12 步流程重演那场谋杀：
            // 9 号是活人（玩家 1 号局）时直接刺杀，否则杀死床上那具既定占位尸体。
            yield return RunFlow();
        }

        // ------------------------------------------------------------------
        // 12 步流程
        // ------------------------------------------------------------------

        IEnumerator RunFlow()
        {
            FlowRunning = true;
            Debug.Log("[NPC 1] 杀9号流程开始（12 步，真实路径重演）。");

            if (_bt != null) _bt.SetPaused(true); // 流程期间行为树让位
            if (_nav != null) _nav.Stop();

            _room1 = NPCRoomUtil.FindRoom(1);
            _room2 = NPCRoomUtil.FindRoom(2);
            _room9 = NPCRoomUtil.FindRoom(9);
            _window1 = _room1 != null ? _room1.GetComponentInChildren<WindowController>(true) : null;
            _window9 = _room9 != null ? _room9.GetComponentInChildren<WindowController>(true) : null;
            _rope = RopeTraverseSystem.Instance;

            // ---- ① 桌边取走 5 件道具 ----
            SetStep(1, "走到桌边，取走长绳、钉子、锤子、磁铁、匕首");
            if (_room1 != null)
                yield return WalkTo(_room1.TransformPoint(new Vector3(0.85f, 0f, 0.5f)));
            yield return WaitWhilePaused();
            if (_room1 != null) TakeItems(_room1);
            yield return new WaitForSeconds(stepWait);

            // ---- ② 系绳 ----
            SetStep(2, "走到窗边，把长绳系在晾衣架上（绳子系好）");
            if (_room1 != null)
                yield return WalkTo(_room1.TransformPoint(new Vector3(0f, 0f, 1.55f)));
            yield return WaitWhilePaused();
            if (_rope != null) _rope.SetAnchorFor(RopeState.Npc1Owner, 1);
            yield return new WaitForSeconds(stepWait);

            // ---- ③ 翻出窗外 ----
            SetStep(3, "推开窗户，翻出窗外");
            yield return WaitWhilePaused();
            if (_window1 != null) _window1.SetOpenInstant(true);
            if (_rope != null)
                yield return _rope.MoveThroughWindow(transform, 1, true, 0.9f, 1.0f);
            PlayerObservationSystem.Instance?.ReportWindow(_npc, true);
            yield return new WaitForSeconds(stepWait);

            // ---- ④ 挪到 2 号房窗外 ----
            SetStep(4, "沿外墙挪到 2 号房窗外");
            if (_rope != null)
                yield return _rope.MoveNpcAlongRope(transform, RopeState.Npc1Owner, 1, 2, 0.9f, 1.8f);
            yield return new WaitForSeconds(stepWait);

            // ---- ⑤ 钉下支点钉 ----
            SetStep(5, "在 2 号房窗外钉下支点钉（钉子 -1，绳子绕过钉子）");
            if (_rope != null) _rope.PlaceNail(RopeState.Npc1Owner, 2);
            if (_rope != null)
                PlayerObservationSystem.Instance?.ReportNail(_npc, 2, _rope.GetNailPoint(2));
            yield return new WaitForSeconds(stepWait);

            // ---- ⑥ 挪到 9 号房窗外 ----
            SetStep(6, "继续挪到 9 号房窗外");
            if (_rope != null)
                yield return _rope.MoveNpcAlongRope(transform, RopeState.Npc1Owner, 2, 9, 0.9f, 2.2f);
            yield return new WaitForSeconds(stepWait);

            // ---- ⑦ 探头查看 9 号是否在床上 ----
            SetStep(7, "探头查看——9 号睡在床上");
            if (_npc != null) _npc.SetLeaningOut(true);
            if (_room9 != null)
                yield return FaceToward(_room9.TransformPoint(new Vector3(0f, 0.75f, 1.8f)), 0.4f);
            yield return new WaitForSeconds(1.0f);
            if (_npc != null) _npc.SetLeaningOut(false);

            // ---- ⑧ 翻进 9 号房 ----
            SetStep(8, "推开 9 号窗，翻进 9 号房");
            if (_window9 != null) _window9.SetOpenInstant(true);
            if (_rope != null)
                yield return _rope.MoveThroughWindow(transform, 9, false, 0.9f, 1.0f);
            PlayerObservationSystem.Instance?.ReportWindow(_npc, false);
            yield return new WaitForSeconds(stepWait);

            // ---- ⑨ 用匕首杀死 9 号 ----
            SetStep(9, "用匕首杀死 9 号");
            NPCFactory factory = FindObjectOfType<NPCFactory>();
            NPCController livingNinth = factory != null ? factory.FindSpawned(9) : null;
            if (livingNinth != null)
            {
                // 破坏轮：9号 是活着的 NPC（可能还在睡），直接刺杀。
                livingNinth.ExecuteByStoryKill("1号（匕首）");
                Debug.Log("[NPC 1] 活着的 9号 已被匕首杀死。");
            }
            else
            {
                NinthVictim victim = FindObjectOfType<NinthVictim>();
                if (victim != null && victim.BodyPresent && !victim.IsDead)
                {
                    victim.ExecuteByAssassin();
                    Debug.Log("[NPC 1] 9号已被匕首杀死（NPCDie(9) 已触发）。");
                }
                else if (!CycleState.kill9Executed)
                {
                    // 兜底：尸体与活人都不在时，仍按事件把第九席记为已死（避免循环状态悬空）
                    CycleState.NotifyKill9Executed();
                    if (_room9 != null) NPCEventBus.RaiseNPCDie(9, _room9.position, true);
                    Debug.LogWarning("[NPC 1] 未找到 NinthVictim 或活着的 9号，直接按事件结算杀9号。");
                }
            }
            PlayerObservationSystem.Instance?.ReportKill(_npc, 9);
            yield return new WaitForSeconds(stepWait);

            // ---- ⑩ 翻出窗外、合上窗，隔玻璃把锁销推上去 ----
            SetStep(10, "翻出窗外、合上窗，用外磁铁隔着玻璃把锁销推上去");
            if (_rope != null)
                yield return _rope.MoveThroughWindow(transform, 9, true, 0.9f, 1.0f);
            PlayerObservationSystem.Instance?.ReportWindow(_npc, true);
            if (_window9 != null)
            {
                _window9.SetOpenInstant(false);            // 从外面把窗扇推合
                yield return _window9.ForceLockWithMagnetSequence(); // 推锁闩→抽外磁铁→内磁铁掉落
            }
            yield return new WaitForSeconds(stepWait);

            // ---- ⑪ 原路返回 1 号房 ----
            SetStep(11, "原路返回 1 号房（9 → 2 → 1，翻进 1 号窗内）");
            if (_rope != null)
            {
                // 沿自己铺好的绳路原路返回（后退不需要支点钉）
                yield return _rope.MoveNpcAlongRope(transform, RopeState.Npc1Owner, 9, 2, 0.9f, 2.2f);
                yield return _rope.MoveNpcAlongRope(transform, RopeState.Npc1Owner, 2, 1, 0.9f, 1.8f);
                yield return _rope.MoveThroughWindow(transform, 1, false, 0.9f, 1.0f);
            }
            PlayerObservationSystem.Instance?.ReportWindow(_npc, false);
            yield return new WaitForSeconds(stepWait);

            // ---- ⑫ 关上窗，回房等待（流程完成） ----
            SetStep(12, "关上窗，收回长绳，回房等待");
            if (_window1 != null) _window1.SetOpenInstant(false);
            // 绳必须当场收回：玩家随后进入 1 号房时，窗外不能还挂着一截不属于他的绳。
            if (_rope != null) _rope.RecallRoute(RopeState.Npc1Owner);
            if (_room1 != null) transform.rotation = _room1.rotation * Quaternion.Euler(0f, 180f, 0f);
            FlowCompleted = true;
            FlowRunning = false;
            FlowPaused = false;
            CurrentStepName = "完成";
            Debug.Log("[NPC 1] 杀9号流程完成（12/12）：9号 isDead=true，Room_9 窗户的锁销被" +
                      "外磁铁隔玻璃推入锁孔并机械卡死；外磁铁已收回，内磁铁掉在窗台碎石堆里。" +
                      "道具已全部带走。");

            if (_nav != null) _nav.Stop();
            if (_bt != null) _bt.SetPaused(false); // 恢复通用行为树（回房等待）
        }

        void SetStep(int index, string name)
        {
            CurrentStepIndex = index;
            CurrentStepName = name;
            Debug.Log($"[NPC 1] 步骤{index}/12：{name}");
        }

        // ------------------------------------------------------------------
        // 道具（从桌面取走：世界物件消失）
        // ------------------------------------------------------------------

        void TakeItems(Transform room)
        {
            string[] ids = { "rope", "nail", "hammer", "magnet", "dagger" };
            int taken = 0;
            for (int j = 0; j < ids.Length; j++)
            {
                if (TakeItem(room, ids[j])) taken++;
            }
            Debug.Log($"[NPC 1] 从 1 号房桌面取走 {taken}/5 件道具（长绳/钉子/锤子/磁铁/匕首）。");
        }

        /// <summary>取走房间里指定道具（世界物件消失）；返回是否真的取到。</summary>
        bool TakeItem(Transform room, string itemId)
        {
            if (room == null || string.IsNullOrEmpty(itemId)) return false;
            ItemPickup[] pickups = room.GetComponentsInChildren<ItemPickup>(true);
            for (int i = 0; i < pickups.Length; i++)
            {
                ItemPickup p = pickups[i];
                if (p == null || p.itemId != itemId) continue;
                p.gameObject.SetActive(false); // 从桌面上消失（被 1号 带走）
                Debug.Log($"[NPC 1] 取走道具：{itemId}。");
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 移动
        // ------------------------------------------------------------------

        /// <summary>走到目标点（寻路；超时则保留在位，后续动画接管）。</summary>
        IEnumerator WalkTo(Vector3 floorTarget)
        {
            if (_nav == null)
            {
                transform.position = floorTarget + Vector3.up * 0.9f;
                yield break;
            }

            _nav.MoveTo(floorTarget + Vector3.up * 0.9f);

            float timer = 0f;
            while (timer < walkTimeout)
            {
                // 私聊期间原地不动：导航停下，等人说完再重新下达目标。
                if (FlowPaused)
                {
                    _nav.Stop();
                    while (FlowPaused) yield return null;
                    _nav.MoveTo(floorTarget + Vector3.up * 0.9f);
                }

                Vector3 to = floorTarget - transform.position;
                to.y = 0f;
                if (to.magnitude <= 0.9f) break;
                timer += Time.deltaTime;
                yield return null;
            }
        }

        /// <summary>原地转身朝向某个点（探头查看用）。</summary>
        IEnumerator FaceToward(Vector3 worldPoint, float duration)
        {
            Vector3 flat = worldPoint - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) yield break;

            Quaternion from = transform.rotation;
            Quaternion to = Quaternion.LookRotation(flat.normalized);

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.05f, duration);
                transform.rotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
                yield return null;
            }

            transform.rotation = to;
        }
    }
}
