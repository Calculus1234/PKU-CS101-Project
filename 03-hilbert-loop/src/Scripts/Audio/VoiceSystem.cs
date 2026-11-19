using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace HilbertLoop
{
    /// <summary>
    /// 语音系统：Windows SAPI 离线合成 + AudioMixer 变声 + 预录人声。
    ///
    /// - LLM / 会议台词：调用 SpeakNpc，异步启动 powershell.exe 的 System.Speech，
    ///   把文本合成到临时 WAV，再用 UnityWebRequest 载入并播放；
    /// - 所有 NPC 语音都路由到 Resources/Audio/NpcVoice.mixer 的 NPC_Voice 组，
    ///   并叠加低通 / 短回声，形成“面具变声”；
    /// - Resources/Voice/ 下已有的预录片段通过 PlayPrerecorded 播放（颅内音也走这里）。
    ///
    /// 如果系统没有可用的 SAPI 中文语音，会自动退回 NpcVoiceRouter 的程序合成低语，
    /// 不会阻塞对话。
    /// </summary>
    public class VoiceSystem : MonoBehaviour
    {
        static VoiceSystem _instance;
        public static VoiceSystem Instance
        {
            get
            {
                if (_instance == null) EnsureInstance();
                return _instance;
            }
        }

        [Header("变声")]
        public float lowPassCutoff = 1550f;
        public float echoDelayMs = 34f;
        public float echoDecay = 0.13f;
        public float echoWet = 0.08f;
        public int maxConcurrentSynthesis = 2;

        AudioMixerGroup _npcGroup;
        AudioMixerGroup _prerecordedGroup;
        bool _mixerSearched;

        public static float VolumeScale
        {
            get { return PlayerPrefs.GetFloat("HL_VoiceVolume", 0.9f); }
            set { PlayerPrefs.SetFloat("HL_VoiceVolume", Mathf.Clamp01(value)); PlayerPrefs.Save(); }
        }
        int _activeSynthesis;
        int _tempIndex;

        static VoiceSystem EnsureInstance()
        {
            if (_instance != null) return _instance;
            GameObject go = new GameObject("VoiceSystem");
            _instance = go.AddComponent<VoiceSystem>();
            DontDestroyOnLoad(go);
            return _instance;
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            ResolveMixer();
            RegisterWithNpcRouter();
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        void ResolveMixer()
        {
            if (_mixerSearched) return;
            _mixerSearched = true;

            AudioMixer mixer = Resources.Load<AudioMixer>("Audio/NpcVoice");
            if (mixer == null)
            {
                UnityEngine.Debug.LogWarning("[VoiceSystem] 未找到 Resources/Audio/NpcVoice.mixer，" +
                                             "将使用 AudioSource 滤镜变声。");
                return;
            }

            AudioMixerGroup[] groups = mixer.FindMatchingGroups("NPC_Voice");
            if (groups != null && groups.Length > 0) _npcGroup = groups[0];
            AudioMixerGroup[] master = mixer.FindMatchingGroups("Master");
            _prerecordedGroup = _npcGroup != null ? _npcGroup :
                (master != null && master.Length > 0 ? master[0] : null);

            UnityEngine.Debug.Log($"[VoiceSystem] AudioMixer 已接入：NPC_Voice={_npcGroup != null}。");
        }

        void RegisterWithNpcRouter()
        {
            NpcVoiceRouter.MixerGroup = _npcGroup;
        }

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        /// <summary>
        /// 播放预录人声。clipName 相对 Resources/Voice，例如 "wake_3"。
        /// </summary>
        public AudioSource PlayPrerecorded(string clipName, float volume = 0.85f, float pitch = 1f,
            bool spatial = false, Transform follow = null)
        {
            AudioClip clip = Resources.Load<AudioClip>("Voice/" + clipName);
            if (clip == null) return null;

            AudioSource source = CreateSource(follow != null ? follow : transform,
                "PrerecordedVoice_" + clipName, spatial);
            source.pitch = pitch;
            source.outputAudioMixerGroup = _prerecordedGroup;
            ApplyMaskFilters(source, false);
            source.clip = clip;
            source.volume = volume * VolumeScale;
            source.Play();
            return source;
        }

        /// <summary>
        /// NPC 语音：优先使用 SAPI 异步合成，失败时返回 false 让调用方播程序低语。
        /// </summary>
        public bool SpeakNpc(NPCController npc, string text)
        {
            if (npc == null || npc.isDead || string.IsNullOrWhiteSpace(text)) return false;
            if (!Application.isPlaying) return false;

            AudioSource source = GetNpcSource(npc);
            source.Stop();
            source.pitch = Mathf.Lerp(0.88f, 1.12f, Mathf.Clamp01((npc.Number - 1) / 8f));
            source.outputAudioMixerGroup = _npcGroup;
            ApplyMaskFilters(source, true);

            StartCoroutine(SynthesisRoutine(text, npc.Number, source, 0.62f));
            return true;
        }

        AudioSource GetNpcSource(NPCController npc)
        {
            Transform existing = npc.transform.Find("NpcVoiceSource");
            if (existing != null)
            {
                AudioSource found = existing.GetComponent<AudioSource>();
                if (found != null) return found;
            }

            AudioSource source = CreateSource(npc.transform, "NpcVoiceSource", true);
            source.minDistance = 0.9f;
            source.maxDistance = 13f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.dopplerLevel = 0f;
            return source;
        }

        AudioSource CreateSource(Transform parent, string name, bool spatial)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = spatial ? 1f : 0f;
            source.ignoreListenerPause = true;
            return source;
        }

        void ApplyMaskFilters(AudioSource source, bool strong)
        {
            AudioLowPassFilter low = source.GetComponent<AudioLowPassFilter>();
            if (low == null) low = source.gameObject.AddComponent<AudioLowPassFilter>();
            low.cutoffFrequency = strong ? lowPassCutoff : 4200f;
            low.lowpassResonanceQ = 0.7f;

            AudioEchoFilter echo = source.GetComponent<AudioEchoFilter>();
            if (echo == null) echo = source.gameObject.AddComponent<AudioEchoFilter>();
            echo.delay = echoDelayMs;
            echo.decayRatio = echoDecay;
            echo.wetMix = strong ? echoWet : 0.03f;
            echo.dryMix = 1f;
        }

        // ------------------------------------------------------------------
        // Windows SAPI
        // ------------------------------------------------------------------

        IEnumerator SynthesisRoutine(string text, int voiceNumber, AudioSource source, float volume)
        {
            while (_activeSynthesis >= Mathf.Max(1, maxConcurrentSynthesis))
                yield return null;

            _activeSynthesis++;
            SapiJob job = null;

            try
            {
                job = StartSapiProcess(text, voiceNumber);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[VoiceSystem] SAPI 启动失败：{e.Message}");
            }

            Process process = job != null ? job.process : null;
            bool started = process != null;

            float deadline = Time.realtimeSinceStartup + 12f;
            while (started && process != null && !process.HasExited && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (started && process != null && !process.HasExited)
            {
                try { process.Kill(); } catch { }
                UnityEngine.Debug.LogWarning("[VoiceSystem] SAPI 合成超时。");
            }

            bool synthesisFailed = false;
            if (started && process != null)
            {
                try { synthesisFailed = process.ExitCode != 0; }
                catch { synthesisFailed = true; }
                try { process.Dispose(); } catch { }
            }

            string path = job != null ? job.wavPath : null;
            if (synthesisFailed || string.IsNullOrEmpty(path) ||
                !File.Exists(path) || new FileInfo(path).Length < 128)
            {
                // 系统没有中文语音 / 合成失败：不播英文腔读中文，直接退回程序化低语。
                _activeSynthesis = Mathf.Max(0, _activeSynthesis - 1);
                if (job != null) job.Cleanup();
                NpcVoiceRouter.SpeakFallback(source, text, voiceNumber, volume);
                yield break;
            }

            string uri = "file:///" + path.Replace("\\", "/");
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        source.clip = clip;
                        source.volume = volume * VolumeScale;
                        source.Play();
                    }
                    else
                    {
                        NpcVoiceRouter.SpeakFallback(source, text, voiceNumber, volume);
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[VoiceSystem] 载入 SAPI 音频失败：{request.error}");
                    NpcVoiceRouter.SpeakFallback(source, text, voiceNumber, volume);
                }
            }

            _activeSynthesis = Mathf.Max(0, _activeSynthesis - 1);
            if (job != null) job.Cleanup();
        }

        /// <summary>一次 SAPI 合成任务持有的临时文件，播完/失败后统一清理。</summary>
        class SapiJob
        {
            public Process process;
            public string wavPath;
            public string textPath;
            public string scriptPath;

            public void Cleanup()
            {
                VoiceSystem.Cleanup(wavPath);
                VoiceSystem.Cleanup(textPath);
                VoiceSystem.Cleanup(scriptPath);
            }
        }

        SapiJob StartSapiProcess(string text, int voiceNumber)
        {
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            string stamp = $"{voiceNumber}_{_tempIndex++}_{DateTime.Now.Ticks}";
            string temp = Application.temporaryCachePath;
            string wavPath = Path.Combine(temp, $"hl_tts_{stamp}.wav");
            string textPath = Path.Combine(temp, $"hl_tts_{stamp}.txt");
            string scriptPath = Path.Combine(temp, $"hl_tts_{stamp}.ps1");

            // 文本走 UTF-8 文件而不是 stdin：stdin 会按控制台代码页解码，
            // 中文会变成乱码，SAPI 于是读出完全听不懂的音节。
            File.WriteAllText(textPath, text ?? string.Empty, new UTF8Encoding(false));

            string lang = Localization.IsEnglish ? "en-US" : "zh-CN";
            string escWav = wavPath.Replace("'", "''");
            string escText = textPath.Replace("'", "''");
            string script =
                "Add-Type -AssemblyName System.Speech\r\n" +
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\r\n" +
                "$s.Rate = -1\r\n" +
                "$s.Volume = 100\r\n" +
                $"$voice = $s.GetInstalledVoices() | Where-Object {{ $_.Enabled -and $_.VoiceInfo.Culture.Name -like '{lang}*' }} | Select-Object -First 1\r\n" +
                "if ($voice -eq $null) { $s.Dispose(); exit 2 }\r\n" +
                "$s.SelectVoice($voice.VoiceInfo.Name)\r\n" +
                $"$text = [System.IO.File]::ReadAllText('{escText}', [System.Text.Encoding]::UTF8)\r\n" +
                $"$s.SetOutputToWaveFile('{escWav}')\r\n" +
                "$s.Speak($text)\r\n" +
                "$s.Dispose()\r\n" +
                "exit 0\r\n";
            // 带 BOM 写出：Windows PowerShell 5.1 才会按 UTF-8 解析脚本。
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Process process = Process.Start(info);
            if (process == null)
            {
                Cleanup(textPath);
                Cleanup(scriptPath);
                return null;
            }

            return new SapiJob
            {
                process = process,
                wavPath = wavPath,
                textPath = textPath,
                scriptPath = scriptPath,
            };
        }

        static void Cleanup(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
