using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 尸体检查（E 键）：
    /// 1) 第一次检查：记录死因 / 尸体状态，9号会点亮「已检查9号尸体」；
    /// 2) 检查后可摘下死者的面具：先摘9号、后摘自己也能对比；
    ///    两边都摘下后才会得到「两张一模一样的脸」——这是顿悟链条上最关键的一环。
    ///
    /// 该组件挂在尸体拥有独立碰撞体的子物体上，
    /// 与根节点的 HidingSpot 分开，避免 E 键语义互相覆盖。
    /// </summary>
    public class CorpseInspectable : MonoBehaviour, IInteractable
    {
        [Header("尸体信息")]
        public int number = 9;
        public bool isNinthVictim;
        [TextArea] public string cause = "";

        [Header("交互")]
        public string subjectName = "尸体";
        public float witnessRadius = 7f;

        bool _bodyInspected;
        bool _maskRemoved;

        public bool BodyInspected => _bodyInspected;
        public bool MaskRemoved => _maskRemoved;

        /// <summary>场景中的全部尸体（NPC 视觉扫描与会议清点共用）。</summary>
        public static readonly List<CorpseInspectable> All = new List<CorpseInspectable>();

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
        }

        public string GetInteractionPrompt(PlayerController player)
        {
            // 床上那个「9号」在玩家扮演 1 号时是活着的沉睡者，
            // 在真正被杀之前不能当成尸体检查。
            NinthVictim livingNinth = GetComponentInParent<NinthVictim>();
            if (livingNinth != null && !livingNinth.IsDead) return string.Empty;

            if (!_bodyInspected)
                return $"检查{subjectName}";

            if (!_maskRemoved)
            {
                return CycleState.playerDisguiseRemoved
                    ? $"摘下{subjectName}的面具，与自己的脸对比"
                    : $"摘下{subjectName}的面具";
            }

            return string.Empty;
        }

        public void Interact(PlayerController player)
        {
            if (player == null) return;

            NinthVictim livingNinth = GetComponentInParent<NinthVictim>();
            if (livingNinth != null && !livingNinth.IsDead) return;

            if (!_bodyInspected)
            {
                InspectBody(player);
                return;
            }

            if (!_maskRemoved)
            {
                RemoveMask(player);
            }
        }

        void InspectBody(PlayerController player)
        {
            _bodyInspected = true;

            string reason = isNinthVictim
                ? "9号的尸体已经僵硬。斗篷下的身体和你一样瘦；面具严丝合缝地扣在脸上，看不见五官。床上没有挣扎的痕迹。" +
                  "门从里面闩着，窗户也锁着——可他死了。"
                : $"{number}号的尸体还有余温。面具和斗篷都还穿在身上，看不见脸。";

            EvidenceLedger.Record(
                isNinthVictim ? "ninth_body" : $"body_{number}",
                isNinthVictim ? "9号的尸体" : $"{number}号的尸体",
                reason,
                isNinthVictim ? 30 : 25);

            if (isNinthVictim) CycleState.NotifyNinthBodyInspected();
            CycleState.NotifyBodyDiscovered(number);

            player.ShowMessage(reason, 5f);
            Debug.Log($"[Corpse] 玩家检查了{subjectName}（死因：{cause}）。");
        }

        void RemoveMask(PlayerController player)
        {
            _maskRemoved = true;
            RevealMaskVisual();

            // 先摘9号面具、后摘自己的面具也成立：反过来时由
            // CycleState.NotifyPlayerDisguiseRemoved 自动补完脸对比。
            bool canCompare = CycleState.playerDisguiseRemoved;

            string reason;
            if (isNinthVictim)
            {
                EvidenceLedger.Record("mask_9", "9号的面具",
                    "面具内侧有一层薄薄的汗渍；边缘的编号被反复摩挲过。", 35);
                CycleState.NotifyNinthMaskRemoved();
                CycleState.NotifyPlayerSawNinthFace();
            }

            if (canCompare)
            {
                reason = isNinthVictim
                    ? "你摘下9号的面具。那张脸和你摘下面具后的脸一模一样——同样的眉骨、同样的下颌线。" +
                      "你们不是长得像，你们是同一张脸。"
                    : $"你摘下{number}号的面具。那张脸和你摘下面具后的脸一模一样。";

                EvidenceLedger.Record(
                    isNinthVictim ? "face_compare" : $"face_compare_{number}",
                    isNinthVictim ? "两张一模一样的脸" : $"{number}号与你的脸完全相同",
                    reason,
                    isNinthVictim ? 65 : 45);

                if (isNinthVictim) CycleState.NotifyFaceComparison();
            }
            else
            {
                reason = isNinthVictim
                    ? "你摘下9号的面具。面具下是一张普通的脸，灯光照得很清楚——" +
                      "可你从没见过自己的脸，无法判断这张脸意味着什么。"
                    : $"你摘下{number}号的面具。面具下是一张普通的脸。你从没见过自己的脸，无法对比。";

                EvidenceLedger.Record(
                    isNinthVictim ? "saw_ninth_face_off" : $"saw_face_{number}",
                    isNinthVictim ? "9号面具下的脸" : $"{number}号面具下的脸",
                    reason,
                    18);
            }

            // 在场的人只要看见这一幕，就会开始怀疑「大家共用同一张脸」；
            // 但如果连玩家自己都没见过自己的脸，他们也缺少对照物。
            MarkWitnesses(canCompare);

            player.ShowMessage(reason + "\n在场的人如果看清了，也会开始怀疑。", 6f);
            Debug.Log($"[Corpse] 玩家摘下了{subjectName}的面具" +
                      (canCompare ? "，完成脸对比。" : "（玩家尚未见过自己的脸，未完成对比）。"));
        }

        void MarkWitnesses(bool canCompare)
        {
            if (NPCController.All == null) return;

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead || npc.blackboard == null) continue;
                if (Vector3.Distance(npc.transform.position, transform.position) > witnessRadius) continue;

                // 隔着墙、隔着门、或者根本没朝这边看的人，不该凭空知道两张脸相同。
                NPCVision vision = npc.GetComponent<NPCVision>();
                if (vision != null && !vision.CanSeePoint(gameObject,
                        transform.position + Vector3.up * 0.6f, 140f, witnessRadius))
                    continue;

                if (isNinthVictim) npc.blackboard.sawNinthMaskOff = true;

                if (canCompare && npc.blackboard.knowsPlayerFace)
                {
                    npc.blackboard.knowsFacesIdentical = true;
                    npc.blackboard.knownClues.Add(isNinthVictim ? "faces_identical" : $"faces_identical_{number}");
                }

                npc.blackboard.stress = Mathf.Min(100f, npc.blackboard.stress + 18f);

                Debug.Log($"[Corpse] {npc.Number}号看见了摘下面具后的脸" +
                          (canCompare && npc.blackboard.knowsPlayerFace
                              ? "：开始怀疑所有人是同一张脸。"
                              : "，但还缺少可对比的另一张脸。"));
            }
        }

        /// <summary>
        /// 供 NPC 搜证 AI 调用：检查尸体 / 摘面具。
        /// 返回 true 表示本次调用产生了新的认知。
        /// </summary>
        public bool NpcInspect(NPCController npc)
        {
            if (npc == null || npc.blackboard == null) return false;

            NinthVictim livingNinth = GetComponentInParent<NinthVictim>();
            if (livingNinth != null && !livingNinth.IsDead) return false;

            if (!_bodyInspected)
            {
                _bodyInspected = true;
                npc.blackboard.knownClues.Add(isNinthVictim ? "ninth_body" : $"body_{number}");
                if (isNinthVictim) CycleState.NotifyNinthBodyInspected();
                CycleState.NotifyBodyDiscovered(number);
                npc.blackboard.stress = Mathf.Min(100f, npc.blackboard.stress + 10f);
                return true;
            }

            if (!_maskRemoved)
            {
                _maskRemoved = true;
                RevealMaskVisual();
                npc.blackboard.sawNinthMaskOff = isNinthVictim;
                npc.blackboard.knownClues.Add(isNinthVictim ? "ninth_face" : $"face_{number}");

                if (CycleState.playerDisguiseRemoved && npc.blackboard.knowsPlayerFace)
                {
                    npc.blackboard.knowsFacesIdentical = true;
                    npc.blackboard.knownClues.Add("faces_identical");
                    npc.blackboard.stress = Mathf.Min(100f, npc.blackboard.stress + 25f);
                }

                if (isNinthVictim)
                {
                    CycleState.NotifyNinthMaskRemoved();
                    if (CycleState.playerDisguiseRemoved) CycleState.NotifyFaceComparison();
                }

                return true;
            }

            return ObserveRevealedFace(npc);
        }

        /// <summary>
        /// 面具已经被别人摘下时，后来的 NPC 只要看见那张脸、并且见过玩家的脸，
        /// 也能自己完成「两张脸一模一样」的推理。
        /// </summary>
        public bool ObserveRevealedFace(NPCController npc)
        {
            if (npc == null || npc.blackboard == null || !isNinthVictim) return false;
            if (!_maskRemoved) return false;
            if (!CycleState.playerDisguiseRemoved) return false;
            if (!npc.blackboard.knowsPlayerFace) return false;

            NPCBlackboard b = npc.blackboard;
            if (b.knownClues.Contains("faces_identical")) return false;

            b.knowsFacesIdentical = true;
            b.sawNinthMaskOff = true;
            b.knownClues.Add("faces_identical");
            b.stress = Mathf.Min(100f, b.stress + 18f);
            CycleState.NotifyFaceComparison();

            Debug.Log($"[Corpse] {npc.Number}号看见了9号面具下的脸，" +
                      "又想起玩家摘下面具后的脸——两张脸一模一样。");
            return true;
        }

        void RevealMaskVisual()
        {
            // 正式模型：面具挂在 Head 骨骼下，由视觉控制器统一隐藏。
            NPCVisualController visual = GetComponentInParent<NPCVisualController>();
            if (visual != null)
            {
                visual.RevealFaceForCorpse();
                return;
            }

            Transform corpseRoot = transform.parent;
            Transform mask = corpseRoot != null ? corpseRoot.Find("CorpseMask") : null;
            if (mask == null) mask = transform.Find("CorpseMask");
            if (mask != null) mask.gameObject.SetActive(false);
        }

        /// <summary>轮回重置：清空本轮检查 / 摘面具状态，并让面具重新扣回脸上。</summary>
        public void ResetInspection()
        {
            _bodyInspected = false;
            _maskRemoved = false;

            NPCVisualController visual = GetComponentInParent<NPCVisualController>();
            if (visual != null) visual.SetMaskVisible(true);

            Transform corpseRoot = transform.parent;
            Transform mask = corpseRoot != null ? corpseRoot.Find("CorpseMask") : null;
            if (mask == null) mask = transform.Find("CorpseMask");
            if (mask != null) mask.gameObject.SetActive(true);
        }
    }
}
