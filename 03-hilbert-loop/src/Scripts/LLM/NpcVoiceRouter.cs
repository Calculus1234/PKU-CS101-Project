using System;
using UnityEngine;
using UnityEngine.Audio;

namespace HilbertLoop
{
    /// <summary>
    /// LLM 台词的播放路由。
    ///
    /// 当前项目没有接入 TTS，因此默认行为是：
    ///  - 台词文本照常由 MeetingUI / PrivateChatUI 显示；
    ///  - 同时根据字数合成一段很轻的“面具下的低语”，按号码变调；
    ///  - 如果以后接入 TTS，只要给 TtsProvider 赋值，就会优先播放真实语音；
    ///  - 如果场景里存在 Resources/Audio/NpcVoice.mixer，会自动路由到 NPC_Voice 组。
    ///
    /// 这样既满足“台词走 AudioMixer 变声”的架构，又不会在没有语音资产时假装有 TTS。
    /// </summary>
    public static class NpcVoiceRouter
    {
        public static bool Enabled = true;

        /// <summary>外部 TTS 注入点：返回 null 时退回程序合成的低语。</summary>
        public static Func<string, int, AudioClip> TtsProvider;

        public static AudioMixerGroup MixerGroup;

        static bool _mixerSearched;

        public static void Speak(NPCController npc, string text)
        {
            if (!Enabled || npc == null || npc.isDead || string.IsNullOrWhiteSpace(text)) return;

            // Windows SAPI：异步合成后仍然走 AudioMixer 变声。启动成功就不再叠低语。
            if (VoiceSystem.Instance != null && VoiceSystem.Instance.SpeakNpc(npc, text))
                return;

            AudioClip clip = null;
            if (TtsProvider != null)
            {
                try { clip = TtsProvider(text, npc.Number); }
                catch (Exception ex) { Debug.LogWarning($"[Voice] TTS 失败，改用合成低语：{ex.Message}"); }
            }
            if (clip == null) clip = BuildMaskedMurmur(text, npc.Number);

            AudioSource source = GetSource(npc);
            source.pitch = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp01((npc.Number - 1) / 8f));
            source.outputAudioMixerGroup = ResolveMixer();
            source.PlayOneShot(clip, 0.22f);
        }

        /// <summary>VoiceSystem 合成失败时的程序化低语兜底。</summary>
        public static void SpeakFallback(AudioSource source, string text, int npcNumber, float volume = 0.22f)
        {
            if (source == null || string.IsNullOrWhiteSpace(text)) return;
            AudioClip clip = BuildMaskedMurmur(text, npcNumber);
            source.pitch = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp01((npcNumber - 1) / 8f));
            source.outputAudioMixerGroup = ResolveMixer();
            source.PlayOneShot(clip, Mathf.Clamp01(volume));
        }

        static AudioSource GetSource(NPCController npc)
        {
            Transform existing = npc.transform.Find("NpcVoiceSource");
            if (existing != null)
            {
                AudioSource found = existing.GetComponent<AudioSource>();
                if (found != null) return found;
            }

            GameObject go = new GameObject("NpcVoiceSource");
            go.transform.SetParent(npc.transform, false);

            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 0.8f;
            source.maxDistance = 12f;
            source.dopplerLevel = 0f;
            return source;
        }

        static AudioMixerGroup ResolveMixer()
        {
            if (MixerGroup != null) return MixerGroup;
            if (_mixerSearched) return null;
            _mixerSearched = true;

            AudioMixer mixer = Resources.Load<AudioMixer>("Audio/NpcVoice");
            if (mixer == null) return null;

            AudioMixerGroup[] groups = mixer.FindMatchingGroups("NPC_Voice");
            if (groups != null && groups.Length > 0) MixerGroup = groups[0];
            return MixerGroup;
        }

        static AudioClip BuildMaskedMurmur(string text, int npcNumber)
        {
            const int rate = 22050;
            float duration = Mathf.Clamp(text.Length * 0.10f, 0.35f, 2.2f);
            int length = Mathf.Max(1, Mathf.RoundToInt(rate * duration));
            float[] data = new float[length];

            float smooth = 0f;
            float tone = 58f + (npcNumber % 5) * 11f;
            for (int i = 0; i < length; i++)
            {
                float t = (float)i / rate;
                float white = UnityEngine.Random.Range(-1f, 1f);
                smooth = Mathf.Lerp(smooth, white, 0.055f);

                float syllable = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.2f));
                float envelope = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                float body = smooth * 0.55f + Mathf.Sin(t * Mathf.PI * 2f * tone) * 0.18f;
                data[i] = body * syllable * envelope * 0.8f;
            }

            AudioClip clip = AudioClip.Create($"NpcMurmur_{npcNumber}", length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
