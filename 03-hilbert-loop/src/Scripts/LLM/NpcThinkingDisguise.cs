using System;
using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// LLM 等待期间的“思考伪装”：
    ///  - 立刻转向玩家并停住（私聊本身已冻结行为）；
    ///  - 极轻微的发抖，避免像一尊静止模型；
    ///  - 面具里的呼吸声 + 间歇电流声；
    ///  - 超过占位时间仍没有首 token 时，回调一句结巴台词。
    ///
    /// 只做表演，不碰黑板与行为树。
    /// </summary>
    public class NpcThinkingDisguise : MonoBehaviour
    {
        static NpcThinkingDisguise _active;

        NPCController _npc;
        Coroutine _routine;
        Vector3 _basePosition;
        Quaternion _baseRotation;
        bool _hasBase;

        AudioSource _breathSource;
        AudioSource _staticSource;
        Action _placeholder;
        float _placeholderAt;
        bool _placeholderFired;
        float _nextStaticAt;

        static AudioClip _sharedBreathClip;
        static AudioClip _sharedStaticClip;

        public static void Begin(NPCController npc, float placeholderDelay, Action onPlaceholder)
        {
            if (npc == null || npc.isDead) return;
            if (_active != null && _active._npc != npc) _active.StopInternal();

            NpcThinkingDisguise d = npc.GetComponent<NpcThinkingDisguise>();
            if (d == null) d = npc.gameObject.AddComponent<NpcThinkingDisguise>();
            d.StartInternal(placeholderDelay, onPlaceholder);
        }

        public static void End(NPCController npc)
        {
            if (npc == null) return;
            NpcThinkingDisguise d = npc.GetComponent<NpcThinkingDisguise>();
            if (d != null) d.StopInternal();
        }

        public static void EndAll()
        {
            if (_active != null) _active.StopInternal();
        }

        void Awake()
        {
            _npc = GetComponent<NPCController>();
        }

        void StartInternal(float placeholderDelay, Action onPlaceholder)
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            RestorePose();

            _basePosition = transform.position;
            _baseRotation = transform.rotation;
            _hasBase = true;
            _placeholder = onPlaceholder;
            _placeholderFired = false;
            _placeholderAt = placeholderDelay > 0f
                ? Time.unscaledTime + placeholderDelay
                : -1f;
            _nextStaticAt = Time.unscaledTime + UnityEngine.Random.Range(1.4f, 2.8f);

            FacePlayer();
            EnsureAudio();
            if (_breathSource != null)
            {
                if (!_breathSource.isPlaying) _breathSource.Play();
                _breathSource.volume = 0f;
            }

            _active = this;
            _routine = StartCoroutine(Tick());
        }

        void StopInternal()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            RestorePose();
            _placeholder = null;
            _placeholderFired = false;
            _placeholderAt = -1f;

            if (_breathSource != null)
            {
                _breathSource.Stop();
                _breathSource.volume = 0f;
            }
            if (_staticSource != null) _staticSource.Stop();

            if (_active == this) _active = null;
        }

        IEnumerator Tick()
        {
            // 呼吸拉满需要一点时间，避免一开口就“嘶”一声。
            float fade = 0f;
            while (true)
            {
                if (_npc == null || _npc.isDead)
                {
                    StopInternal();
                    yield break;
                }

                float now = Time.unscaledTime;
                fade = Mathf.MoveTowards(fade, 1f, Time.unscaledDeltaTime * 1.6f);

                // 会议里 NPC 已经入座，只做极小幅度的前倾/发抖，不转整个身体。
                if (!_npc.InMeeting)
                {
                    float t = now * 9f;
                    float k = Mathf.Sin(t) * 1.1f + Mathf.Sin(t * 0.37f) * 0.6f;
                    transform.rotation = _baseRotation * Quaternion.Euler(k * 0.7f, 0f, k);
                }

                if (_breathSource != null)
                    _breathSource.volume = Mathf.MoveTowards(_breathSource.volume, 0.075f * fade,
                        Time.unscaledDeltaTime * 0.12f);

                if (now >= _nextStaticAt)
                {
                    _nextStaticAt = now + UnityEngine.Random.Range(2.2f, 4.6f);
                    if (_staticSource != null && _sharedStaticClip != null)
                        _staticSource.PlayOneShot(_sharedStaticClip, UnityEngine.Random.Range(0.05f, 0.10f));
                }

                if (!_placeholderFired && _placeholderAt > 0f && now >= _placeholderAt)
                {
                    _placeholderFired = true;
                    Action cb = _placeholder;
                    _placeholder = null;
                    cb?.Invoke();
                }

                yield return null;
            }
        }

        void FacePlayer()
        {
            PlayerController player = FindObjectOfType<PlayerController>();
            if (player == null) return;
            Vector3 to = player.transform.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return;

            // 只转水平方向；视线高度由面具模型自己决定。
            _baseRotation = transform.rotation;
            transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
            _baseRotation = transform.rotation;
        }

        void RestorePose()
        {
            if (!_hasBase) return;
            transform.position = _basePosition;
            transform.rotation = _baseRotation;
            _hasBase = false;
        }

        void EnsureAudio()
        {
            if (_breathSource != null) return;

            GameObject audioGo = new GameObject("ThinkingAudio");
            audioGo.transform.SetParent(transform, false);

            _breathSource = audioGo.AddComponent<AudioSource>();
            _breathSource.loop = true;
            _breathSource.playOnAwake = false;
            _breathSource.spatialBlend = 1f;
            _breathSource.rolloffMode = AudioRolloffMode.Linear;
            _breathSource.minDistance = 0.5f;
            _breathSource.maxDistance = 6f;
            _breathSource.volume = 0f;
            _breathSource.clip = GetBreathClip();

            _staticSource = audioGo.AddComponent<AudioSource>();
            _staticSource.playOnAwake = false;
            _staticSource.spatialBlend = 1f;
            _staticSource.rolloffMode = AudioRolloffMode.Linear;
            _staticSource.minDistance = 0.5f;
            _staticSource.maxDistance = 6f;
            GetStaticClip();
        }

        void OnDisable()
        {
            StopInternal();
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
                float white = UnityEngine.Random.Range(-1f, 1f);
                smooth = Mathf.Lerp(smooth, white, 0.035f);
                float breathe = 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(i * Mathf.PI * 2f * 0.32f / rate));
                data[i] = smooth * breathe * 0.9f;
            }

            _sharedBreathClip = AudioClip.Create("ThinkingMaskBreath", length, 1, rate, false);
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
                bool click = UnityEngine.Random.value < 0.03f;
                data[i] = click
                    ? UnityEngine.Random.Range(-1f, 1f) * decay
                    : UnityEngine.Random.Range(-0.08f, 0.08f) * decay;
            }

            _sharedStaticClip = AudioClip.Create("ThinkingMaskStatic", length, 1, rate, false);
            _sharedStaticClip.SetData(data, 0);
            return _sharedStaticClip;
        }
    }
}
