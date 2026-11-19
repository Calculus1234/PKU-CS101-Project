using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 虚假认知行为链·寻找与下手（3.2 修正一·情境B）：
    /// 8号AI（被机器故障识别为新1号）按注入认知「我是唯一杀手，要杀死比我号码大的人」行动——
    ///   ① 寻找 9 号：走到 9 号房门口；
    ///   ② 观察机会：等待"四下无人"的独处窗口；
    ///   ③ 机会成立且当轮循环被破坏 → 简化位移潜入 9 号房，执行刺杀
    ///      （9号死后循环恢复、人口回 9——它不知道自己已悄然成为新的 9 号）；
    /// 未等到机会 → 放弃本次（下轮树重走时继续寻找；会议阶段由 Vote 节点推动投票）。
    /// Normal 轮（只剩上轮破局痕迹）下只执行一次"寻找"探针——这是"上轮破局则复现破局"的可见印记。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/False Cognition Hunt")]
    public class FalseCognitionHunt : NPCNode
    {
        enum Stage { None, Find, Wait, Kill, Done }

        [Header("机会窗口（秒）：守在 9 号房门口等待独处的时长")]
        public float chanceWindowSeconds = 20f;

        [Header("独处判定半径（米）：此范围内无其他人才算机会")]
        public float aloneRadius = 5f;

        [Header("潜入 9 号房的简化位移时长（秒）")]
        public float intrudeDuration = 1.2f;

        Stage _stage;
        float _waitEnd;
        Vector3 _glideFrom;
        Vector3 _glideTo;
        float _glideT;

        public override void OnEnter()
        {
            _stage = Stage.None;
            _glideT = 0f;
        }

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null) return NodeResult.failure;

            if (_stage == Stage.None)
            {
                _stage = Stage.Find;
                Debug.Log(CycleState.currentPhase == CyclePhase.Disrupted
                    ? $"[NPC {npc.Number}] 虚假认知驱动：开始寻找 9 号（循环已被破坏，它相信自己是唯一杀手）。"
                    : $"[NPC {npc.Number}] 破局痕迹在它身上复现：它不知道自己为什么，只是想找到 9 号。");
            }

            switch (_stage)
            {
                case Stage.Find: return DoFind(b);
                case Stage.Wait: return DoWait();
                case Stage.Kill: return DoKill();
            }
            return NodeResult.success;
        }

        // ------------------------------------------------------------------
        // 三个阶段
        // ------------------------------------------------------------------

        NodeResult DoFind(NPCBlackboard b)
        {
            if (!TryGetRoom9DoorPosition(out Vector3 doorPos))
            {
                Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：找不到 9 号房，放弃寻找。");
                _stage = Stage.Done;
                return NodeResult.success;
            }

            NodeResult move = MoveTowards(doorPos, 2.0f);
            if (move.status == Status.Failure)
            {
                Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：去 9 号房的路被挡住了。");
                return NodeResult.failure;
            }
            if (move.status == Status.Running) return NodeResult.running;

            // 已到达 9 号房门口
            if (CycleState.currentPhase != CyclePhase.Disrupted)
            {
                // Normal 轮（只剩破局痕迹）：完成一次"寻找"探针即收手（一次性）
                b.falseCognitionProbed = true;
                _stage = Stage.Done;
                Debug.Log($"[NPC {Npc.Number}] 它站在 9 号房门前看了一会儿，" +
                          "似乎想起了什么——随后又若无其事地离开了（破局痕迹一闪而过）。");
                return NodeResult.success;
            }

            Debug.Log($"[NPC {Npc.Number}] 它守在 9 号房门口，等待 9号醒来后再找机会……");
            // 9号要到第 85 分钟（倒计时 0:05）才醒。8号必须在门口守到那之后，
            // 截止时间放在保底刺杀之前，给自然得手留出窗口。
            _waitEnd = Time.time + Mathf.Max(chanceWindowSeconds,
                LoopTimeline.DelayFromNowUntil(LoopTimeline.NinthSecretKillMinute));
            _stage = Stage.Wait;
            return NodeResult.running;
        }

        NodeResult DoWait()
        {
            if (CycleState.kill9Executed) return NodeResult.success; // 世界已变化：目标没了

            NPCController livingNinth = FindLivingNinth();
            if (livingNinth == null || !livingNinth.IsAwake)
            {
                // 第 85 分钟之前 9号还在床上沉睡（现在是一个真实的沉睡 NPC）：
                // 继续守在门口，不能提前结算。
                if (Time.time < _waitEnd) return NodeResult.running;

                _stage = Stage.Done;
                Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：一直没等到 9号醒来，保底流程会接手。");
                return NodeResult.success;
            }

            bool alone = !ReplayContexts.AnyOtherPersonNear(Npc, Npc.transform.position, aloneRadius, true);
            if (alone && CycleState.currentPhase == CyclePhase.Disrupted)
            {
                if (IsNearRoom9(livingNinth.transform.position)
                    && TryGetRoom9InsidePosition(out Vector3 inside))
                {
                    _glideFrom = Npc.transform.position;
                    _glideTo = inside;
                    _glideTo.y = _glideFrom.y; // 保持胶囊中心高度
                    _glideT = 0f;
                    if (Nav != null) Nav.Stop();
                    _stage = Stage.Kill;
                    Debug.Log($"[NPC {Npc.Number}] 四下无人——它悄悄潜进了 9 号房。");
                    return NodeResult.running;
                }

                // 9号已经离开房间：在走廊的阴影里追上它，直接结算。
                Debug.Log($"[NPC {Npc.Number}] 四下无人——它在走廊的阴影里追上了 9号。");
                livingNinth.ExecuteByStoryKill("8号AI（虚假认知）");
                _stage = Stage.Done;
                return NodeResult.success;
            }

            if (Time.time >= _waitEnd)
            {
                _stage = Stage.Done;
                Debug.Log($"[NPC {Npc.Number}] 一直有人经过，没找到下手的机会——" +
                          "它暂时收敛了杀意（保底流程会在会议前完成刺杀）。");
                return NodeResult.success;
            }
            return NodeResult.running;
        }

        NodeResult DoKill()
        {
            _glideT += Time.deltaTime / Mathf.Max(0.05f, intrudeDuration);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_glideT));
            Npc.transform.position = Vector3.Lerp(_glideFrom, _glideTo, k);

            if (_glideT < 1f) return NodeResult.running;

            Npc.transform.position = _glideTo;
            if (Nav != null) Nav.Stop();

            // 破坏轮里 9号 可能是活着的 NPC；正常轮里则是床上的尸体占位体。
            // 优先刺杀活着的 9号，只有找不到活人时才去检查尸体。
            NPCController livingNinth = null;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController other = NPCController.All[i];
                if (other != null && !other.isDead && other.Number == 9)
                {
                    livingNinth = other;
                    break;
                }
            }

            if (livingNinth != null)
            {
                // 9号现在是一个真实的沉睡 NPC；它还没睁眼时，8号不能提前结算，
                // 否则玩家扮演 1 号、选择不动手时，9号会在第 85 分钟前就被杀掉。
                if (!livingNinth.IsAwake)
                {
                    Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：9号还在沉睡，尚未到醒来时刻，这次刺杀落空了。");
                    _stage = Stage.Done;
                    return NodeResult.success;
                }

                livingNinth.ExecuteByStoryKill("8号AI（虚假认知）");
            }
            else if (CycleState.currentPhase == CyclePhase.Disrupted && !CycleState.kill9Executed)
            {
                // 1号拒杀后的破坏轮：9号还在床上沉睡，要到第 85 分钟（0:05）才会醒来。
                // 床上的 NinthVictim 此刻是“活着的沉睡者”，不是可供刺杀的尸体；
                // 8号AI 不能在这里提前把它结算掉，否则 9号永远等不到醒来。
                Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：9号还在沉睡，尚未到醒来时刻，这次刺杀落空了。");
                _stage = Stage.Done;
                return NodeResult.success;
            }
            else
            {
                NinthVictim victim = null;
                NinthVictim[] victims = Object.FindObjectsOfType<NinthVictim>(true);
                for (int i = 0; i < victims.Length; i++)
                {
                    if (victims[i] != null && victims[i].BodyPresent && !victims[i].IsDead)
                    {
                        victim = victims[i];
                        break;
                    }
                }

                if (victim != null)
                {
                    victim.ExecuteByFalseCognition();
                }
                else
                {
                    // 9号 已经不在场（被投出 / 被别的流程处理）：幻觉落空，不再伪造一次击杀。
                    Debug.LogWarning($"[NPC {Npc.Number}] 虚假认知：9号不在场，这次刺杀落空了。");
                    _stage = Stage.Done;
                    return NodeResult.success;
                }
            }

            Debug.Log($"[NPC {Npc.Number}] 9 号的呼吸停止了。循环重新开始运转——" +
                      "它不知道，从这一刻起，自己已经悄然成为了新的 9 号。");
            _stage = Stage.Done;
            return NodeResult.success;
        }

        // ------------------------------------------------------------------
        // 目标点
        // ------------------------------------------------------------------

        static NPCController FindLivingNinth()
        {
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && npc.Number == 9) return npc;
            }
            return null;
        }

        static bool IsNearRoom9(Vector3 position)
        {
            return TryGetRoom9InsidePosition(out Vector3 inside)
                && Vector3.Distance(position, inside) < 3.5f;
        }

        static bool TryGetRoom9DoorPosition(out Vector3 position)
        {
            position = default;
            GameObject door = GameObject.Find("Door_9");
            if (door != null)
            {
                position = door.transform.position;
                return true;
            }
            Transform room9 = NPCRoomUtil.FindRoom(9);
            if (room9 != null)
            {
                position = room9.position;
                return true;
            }
            return false;
        }

        static bool TryGetRoom9InsidePosition(out Vector3 position)
        {
            position = default;
            Transform room9 = NPCRoomUtil.FindRoom(9);
            if (room9 == null) return false;
            position = room9.TransformPoint(new Vector3(-0.6f, 0f, 0.9f)); // 床边（本地 +z 靠窗一侧）
            return true;
        }
    }
}
