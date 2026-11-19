using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// LLM ChangeEmotion 的表演层：只做 1~1.5 秒的短演出，结束后恢复原姿势。
    /// 会议里 NPC 已经入座，因此不做位移，只做前倾 / 后仰 / 发抖，避免离开椅子。
    /// </summary>
    public class NpcEmotionPlayer : MonoBehaviour
    {
        NPCController _npc;
        Coroutine _routine;
        Vector3 _basePosition;
        Quaternion _baseRotation;
        bool _hasBase;

        public static void Play(NPCController npc, string emotion)
        {
            if (npc == null || npc.isDead) return;

            NpcEmotionPlayer player = npc.GetComponent<NpcEmotionPlayer>();
            if (player == null) player = npc.gameObject.AddComponent<NpcEmotionPlayer>();
            player.PlayInternal(emotion);
        }

        void Awake()
        {
            _npc = GetComponent<NPCController>();
        }

        void PlayInternal(string emotion)
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                Restore();
            }

            string key = (emotion ?? "").Trim().ToLowerInvariant();
            if (key == "" || key == "none" || key == "calm")
            {
                _routine = null;
                return;
            }

            _basePosition = transform.position;
            _baseRotation = transform.rotation;
            _hasBase = true;

            switch (key)
            {
                case "tremble":
                    _routine = StartCoroutine(TrembleRoutine());
                    break;
                case "retreat":
                    _routine = StartCoroutine(RetreatRoutine());
                    break;
                case "angry":
                    _routine = StartCoroutine(AngryRoutine());
                    break;
                default:
                    _routine = StartCoroutine(TrembleRoutine());
                    break;
            }
        }

        IEnumerator TrembleRoutine()
        {
            float duration = 1.25f;
            float t = 0f;
            while (t < duration)
            {
                if (_npc == null || _npc.isDead) break;
                t += Time.deltaTime;
                float k = Mathf.Sin(t * 55f) * 2.4f * (1f - t / duration);
                transform.rotation = _baseRotation * Quaternion.Euler(k, 0f, k * 0.6f);
                yield return null;
            }
            Restore();
            _routine = null;
        }

        IEnumerator RetreatRoutine()
        {
            float duration = 1.0f;
            float t = 0f;
            bool canMove = _npc != null && !_npc.InMeeting
                && (PrivateChatSystem.Instance == null || !PrivateChatSystem.Instance.IsActive);

            Vector3 back = -transform.forward * 0.28f;
            while (t < duration)
            {
                if (_npc == null || _npc.isDead) break;
                t += Time.deltaTime;
                float k = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                transform.rotation = _baseRotation * Quaternion.Euler(-12f * k, 0f, 0f);
                if (canMove) transform.position = _basePosition + back * k;
                yield return null;
            }
            Restore();
            _routine = null;
        }

        IEnumerator AngryRoutine()
        {
            float duration = 1.0f;
            float t = 0f;
            while (t < duration)
            {
                if (_npc == null || _npc.isDead) break;
                t += Time.deltaTime;
                float k = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                float shake = Mathf.Sin(t * 45f) * 1.2f;
                transform.rotation = _baseRotation * Quaternion.Euler(18f * k + shake, 0f, 0f);
                yield return null;
            }
            Restore();
            _routine = null;
        }

        void Restore()
        {
            if (!_hasBase) return;
            transform.position = _basePosition;
            transform.rotation = _baseRotation;
            _hasBase = false;
        }

        void OnDisable()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            Restore();
        }
    }
}
