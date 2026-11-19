using System.Collections;
using System.Collections.Generic;
using HilbertLoop.BT;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 循环调度器（模块三 3.2 / 3.5，挂在 Systems 上）：
    /// - Awake：从 PlayerPrefs 读取本轮角色配置，并启动错峰醒来时间线；
    /// - Start：按号码放置玩家（1~8 → 对应房间床上；0 → 大厅观察位）；
    ///   玩家扮演 2~8 号时，1 号 AI 会在玩家醒来前完成对 9 号的谋杀；
    /// - Update：玩家扮演 1 号时开启“动手窗口”——30 游戏分钟内未杀 9 号 → 循环被破坏；
    /// - 3.5：PlacePlayerForLoop 供 CycleResetSystem 在每轮天亮后重新落地玩家（号码顺移）。
    /// </summary>
    public class CycleController : MonoBehaviour
    {
        [Tooltip("玩家扮演 1 号时的动手窗口（现实秒）：30 游戏分钟 × 6 秒 = 180 秒；耗尽未杀 9 号 → 循环被破坏")]
        public float kill9WindowSeconds = 180f;

        [Tooltip("玩家引用（场景构建器装配；缺省时运行时自找）")]
        public PlayerController player;

        [Tooltip("NPC 工厂（场景构建器装配；缺省时运行时自找）")]
        public NPCFactory npcFactory;

        float _windowRemaining = -1f;
        bool _warnedMinute;
        SoloTimerUI _soloTimer;

        void Awake()
        {
            if (GameMode.MenuActive) return;

            if (SaveManager.PendingRestore)
            {
                // 「继续」：用存档里的周目 / 玩家角色 / 机器故障状态覆盖角色配置。
                SaveManager.ApplyCachedToCycleState();
                Debug.Log("[CycleController] 已从存档恢复周目状态。");
            }
            else
            {
                CycleState.ConfigureFromPreferences();
                
            }
            LoopTimeline.BeginCycle();
        }

        void Start()
        {
            if (GameMode.MenuActive) return;
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();

            // 延后一帧：等 NPCFactory.SpawnAll 完成，才能按本轮真实名册计算最大号码；
            // 同时确保 CycleResetSystem 已完成初始快照（否则 9 号窗的锁定状态
            // 会被误记为"初始状态"，导致后续每一轮都从反锁窗口开局）。
            StartCoroutine(StartRoundAfterSpawn());
        }

        IEnumerator StartRoundAfterSpawn()
        {
            yield return null;

            // 开局认知：本轮名册的最大号码（可能是一具尸体）所有人都知道，
            // 但没有人知道实际还剩几个人。
            RoundKnowledge.BeginRound(CycleState.currentKnownMax, "开局");
            SurveillanceLog.BeginCycle();
            ApplyPlayerRole();

            // GameManager.Awake 可能早于角色读取：角色落地后按真实醒来时刻校正倒计时。
            if (GameManager.Instance != null) GameManager.Instance.RefreshInvestigationClock();

            int number = CycleState.playerNumber;

            // 1号：9号还活着，正睡在 Room_9 床上，生死由玩家决定。
            if (number == 1)
            {
                if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();
                if (npcFactory != null) npcFactory.EnsureSleepingNinthForPlayerOne();
                yield break;
            }

            // 其他号码：1号 AI 会按 12 步流程在天亮前杀死 9 号。
            // 流程会把绳子、钉子、反锁的窗这些物证留在现场；
            // 万一流程没能完成，再由保底协程静默补上“9号已死”的既定事实。
            StartCoroutine(EnsureNinthDeadBeforeMeeting());
            yield break;
        }

        /// <summary>
        /// 保底：第 20 游戏分钟前 9 号仍未死，就直接按既定事实结算。
        /// 正常情况下 1号 AI 的流程会在第 5 游戏分钟左右完成，这里只是兜底。
        /// </summary>
        IEnumerator EnsureNinthDeadBeforeMeeting()
        {
            float wait = LoopTimeline.DelayFromNowUntil(20);
            if (wait > 0f) yield return new WaitForSeconds(wait);

            if (CycleState.kill9Executed) yield break;
            if (CycleState.playerNumber == 1) yield break;
            if (CycleState.currentKnownMax < 9) yield break;

            if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();
            if (npcFactory != null && npcFactory.FindSpawned(9) != null) yield break;

            PreKillNinthForTimeline();
        }

        /// <summary>
        /// 9 号已被 1 号杀死（玩家 2~8 号的开局既定事实）：
        /// Room_9 门反锁、窗从外锁死，窗台留下神秘磁铁。静默结算，不触发全场恐慌。
        /// </summary>
        void PreKillNinthForTimeline()
        {
            if (CycleState.currentKnownMax < 9) return;

            // 9号若还是活人（玩家扮演 1号），不能当尸体结算。
            if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();
            if (npcFactory != null && npcFactory.FindSpawned(9) != null) return;

            NinthVictim victim = FindObjectOfType<NinthVictim>(true);
            if (victim != null && !victim.IsDead) victim.ApplyBackstoryKill();

            Transform room9 = NPCRoomUtil.FindRoom(9);
            WindowController window = room9 != null
                ? room9.GetComponentInChildren<WindowController>(true)
                : null;
            if (window != null)
                window.ResetWindowState(false, true, false, true, true);

            Debug.Log("[CycleController] 错峰时间线：玩家醒来前，1号已完成对9号的谋杀。" +
                      "Room_9 门反锁、窗从外锁死，窗台留有一块神秘磁铁。");
        }

        void Update()
        {
            // 监控记录不依赖玩家是否扮演 1 号，先于所有分支采样。
            SurveillanceLog.Tick();

            if (!CycleState.playerAlive) return;
            if (CycleState.playerNumber != 1) return;
            if (CycleState.currentKnownMax < 9) return;
            if (CycleState.kill9Executed || CycleState.currentPhase != CyclePhase.Normal) return;
            if (_windowRemaining < 0f) return;
            if (GameManager.Instance != null && GameManager.Instance.ClockPaused) return;

            _windowRemaining -= Time.deltaTime;

            if (!_warnedMinute && _windowRemaining <= 60f)
            {
                _warnedMinute = true;
                Say("还剩最后 10 分钟……你还没有动手。", 3f);
            }
            if (_windowRemaining > 0f) return;

            HandleKill9Refused();
        }

        /// <summary>
        /// 1 号没有在窗口内动手：9 号会继续睡到第 85 游戏分钟，然后自行醒来、参加会议。
        /// 循环不会因此“坏掉”——天亮后所有人照样 +1，9 号顺移到没有房间的 10 号，
        /// 新的 1 号补进来。玩家只需要承担“有人活下来了”这个后果。
        /// </summary>
        void HandleKill9Refused()
        {
            _windowRemaining = -1f;
            HideSoloTimer();
            CycleState.NotifyKill9Refused();

            Say("你没有动手。走廊里安静得能听见自己的呼吸——" +
                $"9 号还在睡，他会在第 {LoopTimeline.SurvivingNinthWakeMinute} 游戏分钟自己醒来。", 8f);

            Debug.Log("[CycleController] 1号没有动手：9号保持存活，将按自己的时间线醒来。");
        }

        // ------------------------------------------------------------------
        // 角色落地
        // ------------------------------------------------------------------

        void ApplyPlayerRole()
        {
            if (player == null) return;

            int number = CycleState.playerNumber;
            int knownMax = Mathf.Max(1, RoundKnowledge.PlayerKnownMaxNumber);
            if (number >= 1 && number <= 8)
            {
                PutPlayerToBed(number);
                ResetPlayerInventory();
                SpawnPlayerNoteOnTable(number);

                // 开局只放颅内音：人物剧本原文 + 本轮最大号码。
                // 不弹任何规则面板，也不再用字幕复述号码与场上局势。
                IntracranialVoice.Instance.PlayBriefing(number, knownMax);

                if (number == 1)
                {
                    if (knownMax >= 9)
                    {
                        _windowRemaining = kill9WindowSeconds;
                        _warnedMinute = false;
                        EnsureSoloTimer().Bind(kill9WindowSeconds);
                    }
                    else
                    {
                        HideSoloTimer();
                    }
                }
                else
                {
                    HideSoloTimer();
                    if (number == RoundKnowledge.NoteHolderNumber)
                        StartCoroutine(DelayedSay(
                            IntracranialVoice.IntroLength(number) + 0.6f,
                            "你的桌上有一张纸条——按 E 拾取，再按 F 阅读。", 6f));
                }
            }
            else
            {
                MovePlayerToLobbyVantage();
                ResetPlayerInventory();
                HideSoloTimer();
                Say($"你以旁观者的身份进入这一轮。本轮最大号码是{knownMax}号。", 6f);
            }

            Debug.Log($"[CycleController] 玩家角色落地完成（playerNumber={CycleState.playerNumber}，" +
                      $"alive={CycleState.playerAlive}，knownMax={knownMax}）。");
        }

        /// <summary>
        /// 纸条属于「号码最大、还活着的参与者」（9 号除外）：
        /// 放在该号码房间的桌面上，必须走近、按 E 拾取，之后才能按 F 阅读。
        /// </summary>
        void SpawnPlayerNoteOnTable(int number)
        {
            if (number <= 0 || number != RoundKnowledge.NoteHolderNumber) return;

            Transform room = NPCRoomUtil.FindRoom(number);
            if (room == null) return;

            // 轮回重置会重建全部道具；如果桌上已经有一张（例如玩家没捡），不要叠第二张。
            ItemPickup[] pickups = FindObjectsOfType<ItemPickup>(true);
            for (int i = 0; i < pickups.Length; i++)
            {
                if (pickups[i] != null && pickups[i].itemId == "note"
                    && pickups[i].transform.IsChildOf(room))
                {
                    return;
                }
            }

            // 桌面上四件道具排成一列（局部 x≈1.15~1.95、z≈0.51，顶面高 0.93），
            // 纸条平铺在它们上方、桌子正中央，既不会从桌沿滚落，也不会悬在半空。
            Vector3 position = room.TransformPoint(new Vector3(1.55f, 0.96f, 0.51f));
            ItemPickup.SpawnWorldItem("note", position, room, number);
            Debug.Log($"[CycleController] 纸条已放在 {number} 号房桌面，等待玩家自行拾取。");
        }

        void EnsureSoloTimerIfNeeded(int number)
        {
            // 只有「玩家是 1 号且这一轮确实有 9 号」才需要动手倒计时。
            if (number == 1 && CycleState.currentKnownMax >= 9)
            {
                _windowRemaining = kill9WindowSeconds;
                _warnedMinute = false;
                EnsureSoloTimer().Bind(kill9WindowSeconds);
            }
            else
            {
                _windowRemaining = -1f;
                HideSoloTimer();
            }
        }

        SoloTimerUI EnsureSoloTimer()
        {
            if (_soloTimer == null)
            {
                GameObject go = new GameObject("SoloTimerUI");
                go.transform.SetParent(transform, false);
                _soloTimer = go.AddComponent<SoloTimerUI>();
            }
            return _soloTimer;
        }

        void HideSoloTimer()
        {
            if (_soloTimer != null) _soloTimer.Hide();
        }

        /// <summary>
        /// 3.5 天亮轮回：按新号码把玩家放回床上（无起床动画，黑场结束后直接站立视角）。
        /// </summary>
        public void PlacePlayerForLoop(int number)
        {
            if (player == null) return;

            // 新一轮名册已由 NPCFactory.ResetForNewLoop 生成：
            // 用本轮声明的最大号码刷新所有 NPC 的认知，再决定纸条归谁。
            RoundKnowledge.BeginRound(CycleState.currentKnownMax, "轮回重置");
            SurveillanceLog.BeginCycle();

            if (number < 1 || number > 8)
            {
                MovePlayerToLobbyVantage();
                ResetPlayerInventory();
                HideSoloTimer();
                Say($"你以旁观者的身份进入这一轮。本轮最大号码是" +
                    $"{Mathf.Max(1, RoundKnowledge.PlayerKnownMaxNumber)}号。", 6f);
                return;
            }

            PutPlayerToBed(number);
            ResetPlayerInventory();
            SpawnPlayerNoteOnTable(number);

            int knownMax = Mathf.Max(1, RoundKnowledge.PlayerKnownMaxNumber);

            if (number == 1)
            {
                // 新一轮玩家是 1 号：9号必须活着睡在 Room_9，而不是床上的尸体。
                if (npcFactory == null) npcFactory = FindObjectOfType<NPCFactory>();
                if (npcFactory != null) npcFactory.EnsureSleepingNinthForPlayerOne();
            }

            // 与开局一致：只播人物剧本原文 + 本轮最大号码，不额外弹字幕。
            IntracranialVoice.Instance.PlayBriefing(number, knownMax);

            if (number != 1 && number == RoundKnowledge.NoteHolderNumber)
            {
                StartCoroutine(DelayedSay(
                    IntracranialVoice.IntroLength(number) + 0.6f,
                    "你的桌上有一张纸条——按 E 拾取，再按 F 阅读。", 6f));
            }

            EnsureSoloTimerIfNeeded(number);
        }

        /// <summary>
        /// 把玩家放到某号房的床上醒来：
        /// 玩家 transform 落在床边站立点（避开床体，防穿模），相机从床上躺姿平滑抬起站直。
        /// </summary>
        void PutPlayerToBed(int number)
        {
            Transform room = NPCRoomUtil.FindRoom(number);
            if (room == null) return;

            Quaternion standRot = room.rotation * Quaternion.Euler(0f, 180f, 0f); // 面向房门
            MovePlayer(room.TransformPoint(new Vector3(-0.55f, 0.05f, 0.3f)), standRot);

            // 相机起点：枕头上的躺姿（头在枕子上方，面向天花板）
            Vector3 bedCam = room.TransformPoint(new Vector3(-1.6f, 0.85f, 1.08f));
            Quaternion bedCamRot = standRot * Quaternion.Euler(-75f, 0f, 0f);
            player.BeginWakeup(bedCam, bedCamRot, 2.4f);
        }

        void MovePlayerToLobbyVantage()
        {
            MovePlayer(new Vector3(0f, 0.1f, -2.45f), Quaternion.identity); // 大厅南侧，面向圆桌
        }

        void MovePlayer(Vector3 position, Quaternion rotation)
        {
            // CharacterController 启停包裹直接设位置，避免控制器把玩家顶回原位
            CharacterController cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.transform.position = position;
            player.transform.rotation = rotation;
            if (cc != null) cc.enabled = true;
        }

        void ResetPlayerInventory()
        {
            if (player == null || player.inventory == null) return;

            // 纸条不再直接塞进背包：它属于本轮号码最大、还活着的参与者，
            // 由 SpawnPlayerNoteOnTable 放在该号码房间的桌面上，玩家必须自己走过去捡。
            player.inventory.ResetTo(new List<string> { "mask", "cloak" });

            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;
        }

        void Say(string message, float duration)
        {
            if (player != null) player.ShowMessage(message, duration);
        }

        /// <summary>等颅内音播完再提示，避免和一百多秒的人物介绍抢注意力。</summary>
        IEnumerator DelayedSay(float delay, string message, float duration)
        {
            yield return new WaitForSecondsRealtime(delay);
            Say(message, duration);
        }
    }
}
