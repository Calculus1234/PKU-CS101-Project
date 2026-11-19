using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 情绪的外显层（模块四补丁）：
    /// - 怀疑 &gt; 70：走廊相遇时主动退后保持距离，或盯着玩家直到离开；
    /// - 信任 &gt; 70：走廊相遇时主动靠近、点头示意；
    /// - 压力 &gt; 80：面具下呼吸变重、机械音夹电流声（运行时合成音频），
    ///   在房间里会抱头前倾 / 无目的地踱步。
    ///
    /// 本组件只做「短暂接管」：演出期间暂停行为树，结束后原样恢复，
    /// 不修改行为树资产，也不改变任何数值计算。
    /// </summary>
    [RequireComponent(typeof(NPCController))]
    public class NPCEmotionFeedback : MonoBehaviour
    {
        NPCController _npc;
        BehaviourTreeRunner _tree;
        NPCNavigator _nav;
        PlayerController _player;

        float _nextScanTime;
        float _nextBreakdownTime;
        float _nextStaticTime;
        bool _reacting;

        AudioSource _breathSource;
        AudioSource _staticSource;
        static AudioClip _sharedBreathClip;
        static AudioClip _sharedStaticClip;

        void Awake()
        {
            _npc = GetComponent<NPCController>();
            _tree = _npc.behaviourTree;
            _nav = GetComponent<NPCNavigator>();
            if (_tree == null && _npc != null) _tree = _npc.GetComponentInChildren<BehaviourTreeRunner>(true);

            _nextBreakdownTime = Time.time + Random.Range(12f, 22f);
            BuildAudio();
        }

        void Update()
        {
            if (_npc == null || _npc.isDead || !_npc.IsAwake)
            {
                StopBreath();
                return;
            }

            UpdateStressAudio();

            if (_npc.InMeeting)
            {
                _reacting = false; // 入座时协程会被 NPCController 停止，这里清除占位
                return;
            }
            if (_reacting) return;

            EnsureTree();
            // 大脑对象在沉睡期间是失活的；起床动画期间 _awake 已为 true 但大脑还没激活。
            if (_tree != null && !_tree.gameObject.activeSelf) return;
            if (_tree != null && _tree.IsPaused) return; // 推搡 / 其他演出正在接管
            if (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive) return;
            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase != GamePhase.Investigation) return;
            if (Time.time < _nextScanTime) return;
            _nextScanTime = Time.time + 0.4f;

            if (_player == null) _player = FindObjectOfType<PlayerController>();
            if (_player == null || !CycleState.playerAlive) return;

            float distance = HorizontalDistance(_player.transform.position);
            if (distance > 6f) return;

            NPCBlackboard board = _npc.blackboard;
            if (board == null) return;

            int playerNumber = CycleState.playerNumber;
            float trust = playerNumber > 0 ? board.GetTrust(playerNumber) : board.trust;

            // 1) 高怀疑：退后 + 盯着你（优先级最高）
            if (board.suspicion >= 70f && distance <= 4.5f && HasLineOfSight())
            {
                StartCoroutine(RetreatThenStare());
                return;
            }

            // 2) 高信任：主动靠近 + 点头
            if (trust >= 70f && board.suspicion < 60f && distance <= 3.5f && HasLineOfSight())
            {
                StartCoroutine(ApproachThenNod());
                return;
            }

            // 3) 极高压力：在房间里抱头前倾 / 原地踱步
            if (board.stress >= 80f && board.isInRoom && Time.time >= _nextBreakdownTime)
            {
                _nextBreakdownTime = Time.time + Random.Range(18f, 30f);
                StartCoroutine(CrouchBreakdown());
            }
        }

        // ------------------------------------------------------------------
        // 行为演出
        // ------------------------------------------------------------------

        IEnumerator RetreatThenStare()
        {
            _reacting = true;
            PauseBrain(true);
            if (_nav != null) _nav.Stop();

            Vector3 away = transform.position - _player.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = -transform.forward;
            away.Normalize();

            _npc.FaceWorldPoint(transform.position + away); // 先转向背离方向
            if (_nav != null) _nav.MoveTo(transform.position + away * 2.2f);

            float moveEnd = Time.time + 0.9f;
            while (Time.time < moveEnd)
            {
                if (!ReactionShouldContinue()) { FinishReaction(); yield break; }
                yield return null;
            }

            if (_nav != null) _nav.Stop();
            FacePlayer();

            // 玩家还在逼近：继续盯着，直到走远或超过 4 秒
            float stareEnd = Time.time + 4f;
            while (Time.time < stareEnd && _player != null
                   && HorizontalDistance(_player.transform.position) < 4.5f)
            {
                if (!ReactionShouldContinue()) { FinishReaction(); yield break; }
                FacePlayer();
                yield return null;
            }

            FinishReaction();
            Debug.Log($"[NPC {_npc.Number}] 高怀疑反应：退后并盯着玩家（数值不显示给玩家）。");
        }

        IEnumerator ApproachThenNod()
        {
            _reacting = true;
            PauseBrain(true);
            if (_nav != null) _nav.Stop();

            if (_player != null && HorizontalDistance(_player.transform.position) > 2f)
                if (_nav != null) _nav.MoveTo(_player.transform.position);

            float moveEnd = Time.time + 0.8f;
            while (Time.time < moveEnd)
            {
                if (!ReactionShouldContinue()) { FinishReaction(); yield break; }
                yield return null;
            }

            if (_nav != null) _nav.Stop();
            FacePlayer();

            Quaternion baseRot = transform.rotation;
            float t = 0f;
            while (t < 1f)
            {
                if (!ReactionShouldContinue())
                {
                    if (!_npc.InMeeting) transform.rotation = baseRot;
                    FinishReaction();
                    yield break;
                }

                t += Time.deltaTime / 1.1f;
                float nod = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI * 2f) * 12f;
                transform.rotation = baseRot * Quaternion.Euler(nod, 0f, 0f);
                yield return null;
            }
            transform.rotation = baseRot;

            FinishReaction();
            Debug.Log($"[NPC {_npc.Number}] 高信任反应：主动靠近并点头。");
        }

        IEnumerator CrouchBreakdown()
        {
            _reacting = true;
            PauseBrain(true);
            if (_nav != null) _nav.Stop();

            Vector3 basePos = transform.position;
            Quaternion baseRot = transform.rotation;

            float duration = 2.6f;
            float t = 0f;
            while (t < duration)
            {
                // 会议开始 / 私聊开始 / 被杀死时立刻退出：
                // 此时座位由会议系统摆放，绝不能再把旧的蹲姿写回去。
                if (!ReactionShouldContinue())
                {
                    if (!_npc.InMeeting)
                    {
                        transform.position = basePos;
                        transform.rotation = baseRot;
                    }
                    FinishReaction();
                    yield break;
                }

                t += Time.deltaTime;
                float k = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                transform.rotation = baseRot * Quaternion.Euler(34f * k, 0f, 0f);
                transform.position = basePos + Vector3.down * (0.14f * k);
                yield return null;
            }

            transform.position = basePos;
            transform.rotation = baseRot;

            FinishReaction();
            Debug.Log($"[NPC {_npc.Number}] 高压反应：抱头前倾 / 原地踱步（Stress={_npc.blackboard.stress:0}）。");
        }

        /// <summary>演出期间每帧校验：会议 / 私聊 / 死亡 / 阶段切换都应立刻交还控制权。</summary>
        bool ReactionShouldContinue()
        {
            if (_npc == null || _npc.isDead || !_npc.IsAwake) return false;
            if (_npc.InMeeting) return false;
            if (_tree != null && !_tree.gameObject.activeSelf) return false;
            if (PrivateChatSystem.Instance != null && PrivateChatSystem.Instance.IsActive) return false;
            if (GameManager.Instance != null && GameManager.Instance.CurrentPhase != GamePhase.Investigation) return false;
            return true;
        }

        void FinishReaction()
        {
            if (_nav != null) _nav.Stop();
            // 会议系统会自己接管 / 恢复行为树，这里不能抢着解除暂停。
            if (_npc != null && !_npc.InMeeting) PauseBrain(false);
            _reacting = false;
        }

        void OnDisable()
        {
            _reacting = false;
            StopBreath();
        }

        void EnsureTree()
        {
            if (_tree != null || _npc == null) return;
            _tree = _npc.behaviourTree;
            if (_tree == null) _tree = _npc.GetComponentInChildren<BehaviourTreeRunner>(true);
        }

        void PauseBrain(bool paused)
        {
            EnsureTree();
            if (_tree != null) _tree.SetPaused(paused);
        }

        void FacePlayer()
        {
            if (_player != null) _npc.FaceWorldPoint(_player.transform.position);
        }

        float HorizontalDistance(Vector3 worldPoint)
        {
            Vector3 delta = worldPoint - transform.position;
            delta.y = 0f;
            return delta.magnitude;
        }

        bool HasLineOfSight()
        {
            if (_player == null) return false;
            Vector3 eye = transform.position + Vector3.up * 0.45f;
            Vector3 target = _player.transform.position + Vector3.up * 1.2f;
            Vector3 to = target - eye;
            float distance = to.magnitude;
            if (distance < 0.01f) return true;

            int mask = ~(1 << MonitorSystem.MonitorIgnoreLayer);
            return !Physics.Raycast(eye, to / distance, distance, mask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------
        // 音频：面具下的呼吸 + 机械音电流（无外部音频资产，运行时合成）
        // ------------------------------------------------------------------

        void BuildAudio()
        {
            GameObject audioGo = new GameObject("EmotionAudio");
            audioGo.transform.SetParent(transform, false);

            _breathSource = audioGo.AddComponent<AudioSource>();
            _breathSource.loop = true;
            _breathSource.playOnAwake = false;
            _breathSource.spatialBlend = 1f;
            _breathSource.rolloffMode = AudioRolloffMode.Linear;
            _breathSource.minDistance = 0.6f;
            _breathSource.maxDistance = 7f;
            _breathSource.volume = 0f;
            _breathSource.clip = GetBreathClip();

            _staticSource = audioGo.AddComponent<AudioSource>();
            _staticSource.playOnAwake = false;
            _staticSource.spatialBlend = 1f;
            _staticSource.rolloffMode = AudioRolloffMode.Linear;
            _staticSource.minDistance = 0.6f;
            _staticSource.maxDistance = 7f;
            GetStaticClip(); // 预热共享电流声
        }

        void UpdateStressAudio()
        {
            if (_breathSource == null) return;

            bool highStress = _npc.blackboard != null && _npc.blackboard.stress >= 80f;
            if (highStress)
            {
                if (!_breathSource.isPlaying) _breathSource.Play();
                _breathSource.volume = Mathf.MoveTowards(_breathSource.volume, 0.10f, Time.deltaTime * 0.15f);

                if (Time.time >= _nextStaticTime)
                {
                    _nextStaticTime = Time.time + Random.Range(3f, 7f);
                    if (_staticSource != null && _sharedStaticClip != null)
                        _staticSource.PlayOneShot(_sharedStaticClip, Random.Range(0.05f, 0.10f));
                }
            }
            else
            {
                _breathSource.volume = Mathf.MoveTowards(_breathSource.volume, 0f, Time.deltaTime * 0.2f);
                if (_breathSource.volume <= 0f && _breathSource.isPlaying) _breathSource.Stop();
            }
        }

        void StopBreath()
        {
            if (_breathSource != null && _breathSource.isPlaying) _breathSource.Stop();
        }

        static AudioClip GetBreathClip()
        {
            if (_sharedBreathClip != null) return _sharedBreathClip;

            const int rate = 22050;
            const int seconds = 2;
            int length = rate * seconds;
            float[] data = new float[length];
            float smooth = 0f;

            for (int i = 0; i < length; i++)
            {
                float white = Random.Range(-1f, 1f);
                smooth = Mathf.Lerp(smooth, white, 0.035f);  // 低通：闷在面具里的呼吸
                float breathe = 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(i * Mathf.PI * 2f * 0.32f / rate));
                data[i] = smooth * breathe * 0.9f;
            }

            _sharedBreathClip = AudioClip.Create("MaskBreath", length, 1, rate, false);
            _sharedBreathClip.SetData(data, 0);
            return _sharedBreathClip;
        }

        static AudioClip GetStaticClip()
        {
            if (_sharedStaticClip != null) return _sharedStaticClip;

            const int rate = 22050;
            int length = Mathf.RoundToInt(rate * 0.35f);
            float[] data = new float[length];
            for (int i = 0; i < length; i++)
            {
                float decay = 1f - (float)i / length;
                bool click = Random.value < 0.03f;
                data[i] = click ? Random.Range(-1f, 1f) * decay : Random.Range(-0.08f, 0.08f) * decay;
            }

            _sharedStaticClip = AudioClip.Create("MaskStatic", length, 1, rate, false);
            _sharedStaticClip.SetData(data, 0);
            return _sharedStaticClip;
        }
    }
}
