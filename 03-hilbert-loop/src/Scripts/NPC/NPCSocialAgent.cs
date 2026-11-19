using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 社交主动性（重构）：
    /// 1) NPC 会主动走到玩家面前发起私聊——不是只有玩家能按 E；
    /// 2) 两个 NPC 会在走廊 / 房间里停下来低声交谈，玩家能看见“他们在聊”，
    ///    靠近时还会听到一句提示。
    /// 只发生在搜证阶段，且不会打断敌对追杀 / 会议 / 已有私聊。
    /// </summary>
    public class NPCSocialAgent : MonoBehaviour
    {
        [Header("主动私聊")]
        [Tooltip("只有在这个距离内、且玩家真的看得见他时，才会提示“有人朝你走来”")]
        public float approachRange = 14f;
        public float talkDistance = 1.8f;
        public float minPersonalInterval = 32f;
        public float maxPersonalInterval = 60f;
        [Tooltip("任意私聊结束后，全场至少安静这么久")]
        public float postChatGlobalCooldown = 35f;
        [Tooltip("刚和玩家聊完的 NPC，至少这么久不会再来找玩家")]
        public float postChatPersonalCooldown = 90f;

        [Header("同伴低语")]
        public float ambientInterval = 14f;
        [Tooltip("走廊里两个人至少要靠这么近才会停下来说话")]
        public float ambientPairRadius = 4.5f;
        [Tooltip("玩家能看见他们时，提示“在低声交谈”的最远距离")]
        public float ambientHintRange = 8f;
        public float ambientDuration = 7f;
        [Tooltip("玩家走到这个距离内，低语会被打断")]
        public float ambientInterruptDistance = 3.2f;

        [Header("尸体查证")]
        [Tooltip("全局最短间隔：避免所有 NPC 同时扑向 9 号床")]
        public float corpseCheckCooldown = 14f;
        public float corpseWalkTimeout = 18f;
        public float corpseInspectDistance = 1.5f;

        static NPCController _approaching;
        static float _nextGlobalApproachTime;
        static float _nextAmbientTime;
        static bool _ambientPlaying;
        static bool _corpseCheckPlaying;
        static float _nextCorpseCheckTime;

        NPCController _npc;
        NPCNavigator _navigator;
        PlayerController _player;
        float _nextPersonalApproachTime;
        bool _running;

        void Awake()
        {
            _npc = GetComponent<NPCController>();
            _navigator = GetComponent<NPCNavigator>();
            SchedulePersonal();
        }

        void SchedulePersonal()
        {
            _nextPersonalApproachTime = Time.time
                + Random.Range(minPersonalInterval, maxPersonalInterval);
        }

        /// <summary>
        /// 一次私聊结束后调用：重新排冷却，而不是沿用“走过来之前”的旧时间。
        /// 聊得越久越需要这段安静期，否则同一个人转身又贴上来。
        /// </summary>
        public static void NotifyChatEnded(NPCController npc)
        {
            NPCSocialAgent agent = npc != null ? npc.GetComponent<NPCSocialAgent>() : null;

            float global = agent != null ? agent.postChatGlobalCooldown : 35f;
            _nextGlobalApproachTime = Mathf.Max(_nextGlobalApproachTime, Time.time + global);

            if (agent == null) return;
            agent._nextPersonalApproachTime = Mathf.Max(
                agent._nextPersonalApproachTime,
                Time.time + Mathf.Max(agent.minPersonalInterval, agent.postChatPersonalCooldown));
        }

        void Update()
        {
            if (_npc == null || _npc.isDead || !_npc.IsAwake) return;
            if (_running) return;
            if (_npc.InMeeting || _npc.InPrivateChat) return;
            if (_npc.blackboard != null && _npc.blackboard.wantsPlayerDead) return;
            if (GameManager.Instance == null
                || GameManager.Instance.CurrentPhase != GamePhase.Investigation) return;
            if (!CycleState.playerAlive) return;
            if (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive) return;
            if (EndingSystem.Instance != null && EndingSystem.Instance.IsEnding) return;

            if (_player == null) _player = FindObjectOfType<PlayerController>();
            if (_player == null) return;

            // 玩家还在起床 / 过场 / 面板里时不要凑上来，否则私聊会打断醒来演出。
            if (_player.inputLocked || _player.movementLocked) return;

            TickAmbient();

            if (TickCorpseInspection()) return;

            if (Time.time < _nextGlobalApproachTime) return;
            if (Time.time < _nextPersonalApproachTime) return;
            if (_approaching != null) return;
            if (!HasSomethingToSay()) return;

            float distance = Vector3.Distance(transform.position, _player.transform.position);
            if (distance > approachRange) return;

            StartCoroutine(ApproachRoutine());
        }

        bool HasSomethingToSay()
        {
            NPCBlackboard board = _npc.blackboard;
            if (board == null) return false;
            if (board.knownClues != null && board.knownClues.Count > 0) return true;
            if (board.hasNote) return true;
            if (board.selfHasKilled) return true;
            if (board.suspicion >= 70f) return true;
            if (board.GetTrust(CycleState.playerNumber) >= 62f) return true;

            // 就算还没搜到东西，也会有人来试探玩家：你什么时候醒的、看见了什么。
            // 否则“NPC 从不主动私聊”会在开局整整半分钟里成立。
            return true;
        }

        IEnumerator ApproachRoutine()
        {
            _running = true;
            _approaching = _npc;
            _nextGlobalApproachTime = Time.time + 22f;
            SchedulePersonal();

            if (_navigator != null) _navigator.enabled = true;
            if (_npc.behaviourTree != null) _npc.behaviourTree.SetPaused(true);

            if (_player != null && PlayerSees(_npc, approachRange))
                _player.ShowMessage($"{_npc.Number}号朝你走过来，似乎有话要说。", 2.5f);

            float deadline = Time.time + 15f;
            bool started = false;

            while (Time.time < deadline)
            {
                if (_npc == null || _npc.isDead || !CycleState.playerAlive
                    || _npc.InMeeting || _npc.InPrivateChat
                    || (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive)
                    || GameManager.Instance == null
                    || GameManager.Instance.CurrentPhase != GamePhase.Investigation)
                    break;

                if (_player == null) break;

                float distance = Vector3.Distance(transform.position, _player.transform.position);
                if (distance <= talkDistance)
                {
                    if (PrivateChatSystem.Instance != null
                        && PrivateChatSystem.Instance.TryStartChat(_npc))
                    {
                        started = true;
                    }
                    break;
                }

                if (_navigator != null)
                    _navigator.MoveTo(_player.transform.position);

                yield return new WaitForSeconds(0.4f);
            }

            if (!started)
            {
                if (_navigator != null) _navigator.Stop();
                if (_npc != null && !_npc.isDead && !_npc.InPrivateChat && _npc.behaviourTree != null)
                {
                    _npc.behaviourTree.SetPaused(false);
                    _npc.behaviourTree.Restart();
                }
            }

            _approaching = null;
            _running = false;
        }

        // ------------------------------------------------------------------
        // 尸体查证（全局节流）
        // 只有「见过玩家的脸 + 早就见过 9 号尸体」的 NPC 才会主动走过去，
        // 亲手摘下 9 号的面具，完成「两张脸一样」的推理。
        // ------------------------------------------------------------------

        bool TickCorpseInspection()
        {
            if (_corpseCheckPlaying) return false;
            if (Time.time < _nextCorpseCheckTime) return false;

            _nextCorpseCheckTime = Time.time + corpseCheckCooldown;

            NPCBlackboard board = _npc.blackboard;
            if (board == null || board.wantsPlayerDead) return false;
            if (!CycleState.playerDisguiseRemoved) return false;
            if (!board.knowsPlayerFace) return false;
            if (!board.sawNinthBody && !board.knownClues.Contains("ninth_body")) return false;
            if (board.knowsFacesIdentical) return false;

            CorpseInspectable corpse = FindNinthCorpse();
            if (corpse == null || corpse.MaskRemoved) return false;

            // 9 号房门整轮锁死，NPC 不会翻窗。走不到尸体时不要白跑一趟，
            // 让他们继续在走廊里搜证，把「脸相同」留到私聊 / 公聊出示证据时再拼出来。
            if (!IsReachable(corpse.transform.position)) return false;

            StartCoroutine(CorpseInspectionRoutine(corpse));
            return true;
        }

        bool IsReachable(Vector3 worldPoint)
        {
            AstarPath astar = AstarPath.active;
            if (astar == null) return true;

            Pathfinding.GraphNode start = astar.GetNearest(transform.position).node;
            Pathfinding.GraphNode goal = astar.GetNearest(worldPoint).node;
            if (start == null || goal == null) return false;
            if (start == goal) return true;

            return Pathfinding.PathUtilities.IsPathPossible(start, goal);
        }

        static CorpseInspectable FindNinthCorpse()
        {
            var corpses = CorpseInspectable.All;
            for (int i = 0; i < corpses.Count; i++)
            {
                CorpseInspectable corpse = corpses[i];
                if (corpse != null && corpse.isNinthVictim) return corpse;
            }
            return null;
        }

        IEnumerator CorpseInspectionRoutine(CorpseInspectable corpse)
        {
            _corpseCheckPlaying = true;
            _running = true;
            _nextCorpseCheckTime = Time.time + corpseCheckCooldown + 20f;

            if (_navigator != null) _navigator.enabled = true;
            if (_npc.behaviourTree != null) _npc.behaviourTree.SetPaused(true);

            float deadline = Time.time + corpseWalkTimeout;
            bool arrived = false;

            while (Time.time < deadline)
            {
                if (_npc == null || _npc.isDead || corpse == null
                    || !CycleState.playerAlive
                    || _npc.InMeeting || _npc.InPrivateChat
                    || (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive)
                    || GameManager.Instance == null
                    || GameManager.Instance.CurrentPhase != GamePhase.Investigation)
                    break;

                float distance = Vector3.Distance(transform.position, corpse.transform.position);
                if (distance <= corpseInspectDistance)
                {
                    arrived = true;
                    break;
                }

                if (_navigator != null) _navigator.MoveTo(corpse.transform.position);
                yield return new WaitForSeconds(0.4f);
            }

            if (arrived && corpse != null)
            {
                if (_navigator != null) _navigator.Stop();
                FacePoint(corpse.transform.position);

                bool maskWasOn = !corpse.MaskRemoved;
                corpse.NpcInspect(_npc);
                yield return new WaitForSeconds(1.6f);

                if (corpse != null)
                {
                    corpse.NpcInspect(_npc);

                    bool removedMask = maskWasOn && corpse.MaskRemoved;
                    NPCConversationBubble.Spawn(
                        _npc.transform, removedMask ? "这张脸……" : "……");

                    if (_player != null)
                    {
                        float distance = Vector3.Distance(
                            _player.transform.position, corpse.transform.position);
                        if (distance <= 14f)
                        {
                            _player.ShowMessage(
                                removedMask
                                    ? $"{_npc.Number}号蹲在9号床边，伸手摘下了那张面具。"
                                    : $"{_npc.Number}号蹲在9号床边，盯着那张没有五官的脸看了很久。",
                                4f);
                        }
                    }
                }

                yield return new WaitForSeconds(2.2f);
            }

            if (_navigator != null) _navigator.Stop();
            if (_npc != null && !_npc.isDead && !_npc.InPrivateChat && _npc.behaviourTree != null)
            {
                _npc.behaviourTree.SetPaused(false);
                _npc.behaviourTree.Restart();
            }

            _corpseCheckPlaying = false;
            _running = false;
        }

        void FacePoint(Vector3 worldPoint)
        {
            Vector3 dir = worldPoint - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir);
        }

        // ------------------------------------------------------------------
        // 同伴低语（全局节流，由任意一个实例触发）
        // ------------------------------------------------------------------

        void TickAmbient()
        {
            if (_ambientPlaying) return;
            if (Time.time < _nextAmbientTime) return;

            _nextAmbientTime = Time.time + ambientInterval + Random.Range(0f, 6f);

            NPCController a = null;
            NPCController b = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController first = NPCController.All[i];
                if (!CanAmbient(first)) continue;

                for (int j = i + 1; j < NPCController.All.Count; j++)
                {
                    NPCController second = NPCController.All[j];
                    if (!CanAmbient(second)) continue;

                    float distance = Vector3.Distance(first.transform.position, second.transform.position);

                    // 用户规则：不能在隔着两个房间的地方“窃窃私语”。
                    // 要么两个人在同一个房间里，要么都在走廊上且真的靠得很近。
                    bool sameRoom = !string.IsNullOrEmpty(first.currentRoomName)
                        && first.currentRoomName == second.currentRoomName;
                    bool bothInCorridor = string.IsNullOrEmpty(first.currentRoomName)
                        && string.IsNullOrEmpty(second.currentRoomName);
                    bool samePlace = sameRoom || (bothInCorridor && distance <= ambientPairRadius);
                    if (!samePlace) continue;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        a = first;
                        b = second;
                    }
                }
            }

            if (a == null || b == null) return;
            StartCoroutine(AmbientRoutine(a, b));
        }

        static bool CanAmbient(NPCController npc)
        {
            return npc != null && !npc.isDead && npc.IsAwake
                   && !npc.InMeeting && !npc.InPrivateChat
                   && (npc.blackboard == null || !npc.blackboard.wantsPlayerDead);
        }

        IEnumerator AmbientRoutine(NPCController a, NPCController b)
        {
            _ambientPlaying = true;

            if (a.behaviourTree != null) a.behaviourTree.SetPaused(true);
            if (b.behaviourTree != null) b.behaviourTree.SetPaused(true);
            if (a.navigator != null) a.navigator.Stop();
            if (b.navigator != null) b.navigator.Stop();

            FaceEachOther(a, b);

            NPCConversationBubble.Spawn(a.transform, "低声交谈……");
            NPCConversationBubble.Spawn(b.transform, "……");

            PlayerController player = PlayerObservationSystem.Instance != null
                ? PlayerObservationSystem.Instance.GetComponent<PlayerController>()
                : FindObjectOfType<PlayerController>();
            if (player == null) player = FindObjectOfType<PlayerController>();

            // 只有玩家真的看见他们，屏幕才提示“在低声交谈”。
            if (player != null && (PlayerSees(a, ambientHintRange) || PlayerSees(b, ambientHintRange)))
            {
                float distance = Mathf.Min(
                    Vector3.Distance(player.transform.position, a.transform.position),
                    Vector3.Distance(player.transform.position, b.transform.position));
                if (distance <= ambientHintRange)
                    player.ShowMessage($"{a.Number}号和{b.Number}号停在角落里，压低声音说着什么。", 3.5f);
            }

            bool interrupted = false;
            float elapsed = 0f;
            while (elapsed < ambientDuration)
            {
                if (a == null || b == null || a.isDead || b.isDead
                    || a.InMeeting || b.InMeeting || a.InPrivateChat || b.InPrivateChat
                    || (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive)
                    || GameManager.Instance == null
                    || GameManager.Instance.CurrentPhase != GamePhase.Investigation)
                    break;

                if (player != null)
                {
                    float near = Mathf.Min(
                        Vector3.Distance(player.transform.position, a.transform.position),
                        Vector3.Distance(player.transform.position, b.transform.position));
                    if (near <= ambientInterruptDistance)
                    {
                        interrupted = true;
                        break;
                    }
                }

                elapsed += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            if (interrupted && player != null && a != null && b != null)
            {
                // 玩家走近：低语戛然而止，两个人同时看向玩家。
                FacePoint(a, player.transform.position);
                FacePoint(b, player.transform.position);
                NPCConversationBubble.Spawn(a.transform, "……");
                NPCConversationBubble.Spawn(b.transform, "……");
                player.ShowMessage($"你走近时，{a.Number}号和{b.Number}号立刻停住了，" +
                                   "一起转头看着你。", 3f);
                yield return new WaitForSeconds(0.6f);
            }

            if (a != null && !a.isDead && !a.InMeeting && !a.InPrivateChat && a.behaviourTree != null)
            {
                a.behaviourTree.SetPaused(false);
                a.behaviourTree.Restart();
            }
            if (b != null && !b.isDead && !b.InMeeting && !b.InPrivateChat && b.behaviourTree != null)
            {
                b.behaviourTree.SetPaused(false);
                b.behaviourTree.Restart();
            }

            _ambientPlaying = false;
        }

        static void FacePoint(NPCController npc, Vector3 worldPoint)
        {
            if (npc == null) return;
            Vector3 dir = worldPoint - npc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                npc.transform.rotation = Quaternion.LookRotation(dir);
        }

        /// <summary>玩家是否真的看得见这个 NPC（距离 + 视锥 + 遮挡）。</summary>
        static bool PlayerSees(NPCController npc, float range)
        {
            if (npc == null) return false;
            PlayerObservationSystem observation = PlayerObservationSystem.Instance;
            if (observation == null) return true; // 缺少观察系统时退回旧的近似行为
            return observation.CanPlayerSee(npc.transform,
                npc.transform.position + Vector3.up * 1.0f, 90f, range);
        }

        static void FaceEachOther(NPCController a, NPCController b)
        {
            Vector3 dir = b.transform.position - a.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) a.transform.rotation = Quaternion.LookRotation(dir);

            dir = a.transform.position - b.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) b.transform.rotation = Quaternion.LookRotation(dir);
        }
    }
}
