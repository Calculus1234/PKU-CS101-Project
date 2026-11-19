using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 圆桌投票结算（模块三 3.5）：
    /// - 会议结束自动进入 Voting；玩家 30 秒内选择号码或弃权，超时弃权；
    /// - NPC 读取黑板 suspicion / suspectNumbers 投票：怀疑度不高时优先投玩家，
    ///   有极强怀疑对象（suspicion ≥ 80）时投该对象，并在倾向上加入小幅随机扰动；
    /// - 统计后唯一最高票者被抹杀（不流血、化作光点）；平票或全员弃权无人出局；
    /// - 结算展示完毕后切到 Reincarnation，由 CycleResetSystem 执行天亮轮回。
    /// </summary>
    public class VotingSystem : MonoBehaviour
    {
        public static VotingSystem Instance { get; private set; }

        [Header("引用（场景构建器装配）")]
        public PlayerController player;
        public VotingUI ui;
        public NPCFactory npcFactory;

        [Header("参数")]
        [Tooltip("玩家投票倒计时（秒）")]
        public float voteSeconds = 30f;
        [Tooltip("结算结果展示时长（秒）")]
        public float resultSeconds = 6f;
        [Tooltip("玩家已死亡时的旁观投票时长（秒）")]
        public float spectatorVoteSeconds = 8f;
        [Tooltip("NPC 投票随机扰动上限（避免所有 NPC 死板投同一人）")]
        public float voteNoise = 18f;

        bool _running;
        bool _playerVoted;
        bool _skipRequested;
        int _playerVote = -1;
        readonly Dictionary<int, int> _votes = new Dictionary<int, int>();
        readonly List<NPCController> _voters = new List<NPCController>();
        readonly Dictionary<int, float> _scores = new Dictionary<int, float>();
        Coroutine _routine;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            if (player == null) player = FindObjectOfType<PlayerController>();
            if (ui == null) ui = FindObjectOfType<VotingUI>();
            if (GameManager.Instance != null)
                GameManager.Instance.OnPhaseChanged += HandlePhaseChanged;
        }

        void OnDestroy()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPhaseChanged -= HandlePhaseChanged;
            if (Instance == this) Instance = null;
        }

        void HandlePhaseChanged(GamePhase phase)
        {
            if (phase == GamePhase.Voting)
            {
                if (_running) return;
                _routine = StartCoroutine(RunVoting());
            }
            else if (_running)
            {
                // 被外部（幽灵跳过 / 阶段强制推进）打断：停止流程，避免重复结算
                FinishVoting(false);
            }
        }

        // ------------------------------------------------------------------
        // 主流程
        // ------------------------------------------------------------------

        IEnumerator RunVoting()
        {
            _running = true;
            _playerVoted = false;
            _skipRequested = false;
            _playerVote = -1;
            _votes.Clear();
            _voters.Clear();
            _scores.Clear();

            CycleState.NotifyVotingStarted();
            CollectVoters();
            FreezeVoters(true);

            List<int> candidates = BuildCandidates();

            if (player != null)
            {
                player.movementLocked = true;
                player.inputLocked = true;
                player.SetCursorLocked(false);
            }

            if (CycleState.playerAlive && CycleState.playerNumber > 0)
            {
                if (player != null) player.ShowMessage("投票开始：选出你认为最可疑的人，或弃权。", 3f);
                if (ui != null)
                    ui.ShowBallot(candidates, CycleState.playerNumber, voteSeconds, OnPlayerSelected);
            }
            else
            {
                if (ui != null) ui.ShowSpectator(spectatorVoteSeconds);
            }

            // 等待玩家投票 / 超时 / 幽灵跳过
            float wait = CycleState.playerAlive ? voteSeconds + 0.5f : spectatorVoteSeconds;
            float elapsed = 0f;
            while (_running && elapsed < wait)
            {
                if (_playerVoted) break;
                if (!CycleState.playerAlive && !IsSpectatorBallotOpen()) break;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!_running) yield break;

            if (CycleState.playerAlive && CycleState.playerNumber > 0 && !_playerVoted)
                _playerVote = 0; // 超时兜底（UI 正常会先回调）

            if (ui != null) ui.Hide();
            if (!_skipRequested) yield return new WaitForSecondsRealtime(0.35f);

            CalculateNpcVotes(candidates);
            if (_playerVoted && _playerVote >= 0) AddVote(_playerVote);

            // 统计
            int topVotes = 0;
            int topNumber = 0;
            int topCount = 0;
            int abstain = GetVotes(0);

            for (int n = 1; n <= CycleState.MaxNumber; n++)
            {
                int count = GetVotes(n);
                if (count <= 0) continue;
                if (count > topVotes)
                {
                    topVotes = count;
                    topNumber = n;
                    topCount = 1;
                }
                else if (count == topVotes)
                {
                    topCount++;
                }
            }

            bool tie = topVotes == 0 || topCount != 1;
            int executed = tie ? 0 : topNumber;

            List<string> lines = BuildCountLines(abstain);
            string verdict;
            if (tie)
            {
                verdict = topVotes == 0
                    ? "全员弃权——本轮无人出局。"
                    : "平票——本轮无人出局。";
                Debug.Log($"[Voting] {verdict}");
            }
            else
            {
                verdict = $"{executed} 号获得最高票（{topVotes} 票）——立即抹杀。";
                Debug.Log($"[Voting] {verdict}");
            }

            int population = Mathf.Max(1, CycleState.currentPopulation - (executed > 0 ? 1 : 0));
            CycleState.NotifyVoteResult(executed, tie, population);

            // 投票结果：钟声。
            if (SFXManager.Instance != null) SFXManager.Instance.PlayBellToll(0.85f);

            if (ui != null && !_skipRequested) ui.ShowResults(lines, verdict, resultSeconds);

            yield return WaitResultPhase(0.8f);
            if (!_running) yield break;

            if (executed > 0)
            {
                Execute(executed);
            }

            // 结果展示剩余时长：幽灵按「跳过」时立即推进到天亮
            yield return WaitResultPhase(Mathf.Max(0.3f, resultSeconds - 0.8f));
            if (!_running) yield break;

            FreezeVoters(false);
            if (player != null && CycleState.playerAlive)
            {
                player.inputLocked = false;
                player.movementLocked = false;
                player.SetCursorLocked(true);
            }

            _running = false;
            _routine = null;

            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase == GamePhase.Voting)
                GameManager.Instance.SetPhase(GamePhase.Reincarnation);
        }

        /// <summary>结果展示计时（不受 timeScale 影响）；幽灵跳过时立即返回。</summary>
        IEnumerator WaitResultPhase(float seconds)
        {
            float elapsed = 0f;
            while (_running && !_skipRequested && elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        void OnPlayerSelected(int number)
        {
            if (!_running || _playerVoted) return;
            _playerVoted = true;
            _playerVote = number;
            Debug.Log($"[Voting] 玩家投票：{(number == 0 ? "弃权" : number + "号")}。");
        }

        bool IsSpectatorBallotOpen()
        {
            return ui != null && GameManager.Instance != null
                && GameManager.Instance.CurrentPhase == GamePhase.Voting;
        }

        /// <summary>幽灵跳过：立即结算当前投票（打开选票则按弃权，结果展示中则直接推进）。</summary>
        public void SkipVote()
        {
            if (!_running) return;

            _skipRequested = true;
            if (!_playerVoted)
            {
                _playerVoted = true;
                _playerVote = 0;
            }
            if (ui != null) ui.Hide();
        }

        // ------------------------------------------------------------------
        // 投票者 / 计票
        // ------------------------------------------------------------------

        void CollectVoters()
        {
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc == null || npc.isDead) continue;
                _voters.Add(npc);
            }
            _voters.Sort((a, b) => a.Number.CompareTo(b.Number));
            Debug.Log($"[Voting] 存活 NPC 投票者 {_voters.Count} 名，玩家 {(CycleState.playerAlive ? CycleState.playerNumber + "号" : "已出局")}。");
        }

        List<int> BuildCandidates()
        {
            var list = new List<int>();
            for (int i = 0; i < _voters.Count; i++) list.Add(_voters[i].Number);
            if (CycleState.playerAlive && CycleState.playerNumber > 0 && !list.Contains(CycleState.playerNumber))
                list.Add(CycleState.playerNumber);
            list.Sort();
            return list;
        }

        void CalculateNpcVotes(List<int> candidates)
        {
            for (int i = 0; i < _voters.Count; i++)
            {
                NPCController npc = _voters[i];
                if (npc == null || npc.isDead) continue;
                int target = ChooseVoteTarget(npc, candidates);
                AddVote(target);
                Debug.Log($"[Voting] {npc.Number}号 → {(target == 0 ? "弃权" : target + "号")}" +
                          $"（suspicion={npc.blackboard.suspicion:0}，" +
                          $"trust8={npc.blackboard.GetTrust(CycleState.playerNumber):0}，" +
                          $"intent={npc.blackboard.voteIntentNumber}）");
            }
        }

        int ChooseVoteTarget(NPCController npc, List<int> candidates)
        {
            if (candidates.Count == 0) return 0;

            NPCBlackboard board = npc.blackboard;
            _scores.Clear();
            for (int i = 0; i < candidates.Count; i++) _scores[candidates[i]] = 0f;

            int playerNumber = CycleState.playerAlive ? CycleState.playerNumber : 0;

            // 倾向一：玩家带票。会议里被说服的人会真的按这个方向投票，
            // 而不是嘴上答应、票箱里继续投 8号。
            if (board != null && board.voteIntentNumber > 0
                && board.voteIntentNumber != npc.Number
                && _scores.ContainsKey(board.voteIntentNumber))
            {
                _scores[board.voteIntentNumber] += 80f;
                Debug.Log($"[Voting] {npc.Number}号跟票 → {board.voteIntentNumber}号。");
            }

            // 倾向二：对玩家的结构性压力。任务书里所有人开局都怀疑 8号，
            // 但这份压力必须能被自辩、证据和带票削弱，而不是写死 90 分。
            if (playerNumber > 0 && _scores.ContainsKey(playerNumber))
            {
                float trust = board != null ? board.GetTrust(playerNumber) : 50f;
                float suspicion = board != null ? board.suspicion : 50f;
                bool inSuspects = board != null && board.suspectNumbers != null
                    && board.suspectNumbers.Contains(playerNumber);

                float playerWeight = 35f;
                playerWeight += Mathf.Max(0f, 50f - trust) * 1.5f;   // 信任越低越投玩家
                playerWeight += Mathf.Max(0f, suspicion - 50f) * 0.8f; // 场上怀疑越高越投玩家
                if (inSuspects) playerWeight += 25f;
                if (board != null && board.wantsPlayerDead) playerWeight += 25f;
                _scores[playerNumber] += Mathf.Max(0f, playerWeight);
            }

            // 倾向三：黑板里的嫌疑名单（越靠前越可疑）
            if (board != null && board.suspectNumbers != null)
            {
                for (int i = 0; i < board.suspectNumbers.Count; i++)
                {
                    int suspect = board.suspectNumbers[i];
                    if (suspect == npc.Number || !_scores.ContainsKey(suspect)) continue;
                    _scores[suspect] += Mathf.Max(18f, 62f - i * 14f);
                }
            }

            // 倾向三·补：LLM 通过 ChangeStat 写下的逐号码怀疑 / 敌意。
            // 这是会议上被说服、被激怒的直接结果，必须真的影响票。
            if (board != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    int n = candidates[i];
                    if (n == npc.Number) continue;
                    _scores[n] += board.GetSuspicionToward(n) * 0.8f
                                  + board.GetHostilityToward(n) * 1.2f;
                }
            }

            // 倾向四：自己的 stress / suspicion 越高，越倾向于投玩家之外的人（自保）
            if (board != null && board.suspicion >= 80f)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i] != playerNumber) _scores[candidates[i]] += 6f;
                }
            }

            // 随机扰动：避免所有 NPC 死板投同一人
            for (int i = 0; i < candidates.Count; i++)
            {
                int n = candidates[i];
                _scores[n] += Random.Range(0f, voteNoise);
            }

            int best = 0;
            float bestScore = -1f;
            for (int i = 0; i < candidates.Count; i++)
            {
                int n = candidates[i];
                if (_scores[n] > bestScore)
                {
                    bestScore = _scores[n];
                    best = n;
                }
            }

            // 不投自己：若最高分恰好是自己，改投第二名（没有别人则弃权）
            if (best == npc.Number)
            {
                int second = 0;
                float secondScore = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int n = candidates[i];
                    if (n == npc.Number) continue;
                    if (_scores[n] > secondScore)
                    {
                        secondScore = _scores[n];
                        second = n;
                    }
                }
                best = second;
            }

            if (best == 0) return 0;
            bestScore = _scores[best];

            // 弃权：以前只有“唯一候选是自己”才会弃权，等于永远看不到。
            // 现在按“没有把握 / 太害怕 / 谁都不想投”给一个真实概率。
            float runnerUp = float.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                int n = candidates[i];
                if (n == best) continue;
                if (_scores[n] > runnerUp) runnerUp = _scores[n];
            }

            float abstainChance = 0f;
            if (!float.IsNegativeInfinity(runnerUp))
            {
                float margin = bestScore - runnerUp;

                // 谁都不想投：对其他候选人都足够信任，且没有明确怀疑对象。
                bool allTrusted = board != null && candidates.Count > 1;
                if (allTrusted)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int n = candidates[i];
                        if (n == npc.Number) continue;
                        if (board.GetTrust(n) < 62f || board.GetSuspicionToward(n) >= 40f)
                        {
                            allTrusted = false;
                            break;
                        }
                    }
                }

                bool tooScared = board != null && board.stress >= 70f;
                bool lowConviction = bestScore < 50f;
                bool uncertain = margin < 12f;

                // 基础概率保证“偶尔有人谁都不想投”，其余条件把概率抬到能看见的程度。
                abstainChance = 0.08f;
                if (allTrusted) abstainChance = 0.55f;
                else if (lowConviction) abstainChance = 0.30f;
                else if (uncertain && tooScared) abstainChance = 0.35f;
                else if (uncertain) abstainChance = 0.18f;
                else if (tooScared) abstainChance = 0.12f;
            }

            if (abstainChance > 0f && Random.value < abstainChance)
            {
                Debug.Log($"[Voting] {npc.Number}号弃权（意图 {best} 号，最高分 {bestScore:0}，" +
                          $"概率 {abstainChance:0.00}）。");
                return 0;
            }

            return best;
        }

        void AddVote(int number)
        {
            if (number < 0) number = 0;
            if (_votes.TryGetValue(number, out int count)) _votes[number] = count + 1;
            else _votes[number] = 1;
        }

        int GetVotes(int number)
        {
            return _votes.TryGetValue(number, out int count) ? count : 0;
        }

        List<string> BuildCountLines(int abstain)
        {
            var entries = new List<KeyValuePair<int, int>>();
            for (int n = 1; n <= CycleState.MaxNumber; n++)
            {
                int count = GetVotes(n);
                if (count > 0) entries.Add(new KeyValuePair<int, int>(n, count));
            }
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));

            var lines = new List<string>();
            for (int i = 0; i < entries.Count; i++)
                lines.Add($"{entries[i].Key} 号　　{entries[i].Value} 票");
            if (abstain > 0) lines.Add($"弃权　　{abstain} 票");
            if (lines.Count == 0) lines.Add("无人投票");
            return lines;
        }

        // ------------------------------------------------------------------
        // 处决
        // ------------------------------------------------------------------

        void Execute(int number)
        {
            if (number == CycleState.playerNumber && CycleState.playerAlive)
            {
                PlayerDeathSystem death = PlayerDeathSystem.Instance;
                if (death != null) death.KillPlayer("圆桌投票抹杀", PlayerDeathType.VotedOut);
                else CycleState.NotifyPlayerDeath("圆桌投票抹杀", PlayerDeathType.VotedOut);
                return;
            }

            for (int i = 0; i < _voters.Count; i++)
            {
                NPCController npc = _voters[i];
                if (npc != null && npc.Number == number && !npc.isDead)
                {
                    npc.ExecuteByVote();
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // NPC 冻结 / 恢复
        // ------------------------------------------------------------------

        void FreezeVoters(bool freeze)
        {
            for (int i = 0; i < _voters.Count; i++)
            {
                NPCController npc = _voters[i];
                if (npc == null || npc.isDead) continue;
                if (freeze) npc.EnterMeetingMode();
                else npc.ExitMeetingMode();
            }
        }

        void FinishVoting(bool advance)
        {
            _running = false;
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            if (ui != null) ui.Hide();
            FreezeVoters(false);
            if (player != null && CycleState.playerAlive)
            {
                player.inputLocked = false;
                player.movementLocked = false;
            }
        }
    }
}
