using System.Collections;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 颅内音播报器。
    ///
    /// 语音由 Windows 中文语音（Microsoft Huihui）离线合成成 WAV，放在
    /// Assets/Resources/Voice 下；运行时用 2D AudioSource + 低通 + 短回声播放，
    /// 让规则播报听起来像从玩家脑子里响起，而不是从房间某个方向传来。
    ///
    /// 所有片段都按编号命名，新增台词时把同名 WAV 放进 Resources/Voice 即可。
    /// </summary>
    public class IntracranialVoice : MonoBehaviour
    {
        static IntracranialVoice _instance;

        AudioSource _source;
        Coroutine _sequence;

        public static IntracranialVoice Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("IntracranialVoice");
                    _instance = go.AddComponent<IntracranialVoice>();
                }
                return _instance;
            }
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

            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;       // 2D：贴着耳朵，不随位置衰减
            _source.volume = 0.82f;
            _source.ignoreListenerPause = true;

            // 低通削掉齿音和高频空气感，留下“隔着头骨”的闷响。
            AudioLowPassFilter lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 1450f;
            lowPass.lowpassResonanceQ = 0.7f;

            // 极短的湿声混响，制造颅腔内的近场反射。
            AudioEchoFilter echo = gameObject.AddComponent<AudioEchoFilter>();
            echo.delay = 28f;
            echo.decayRatio = 0.12f;
            echo.wetMix = 0.07f;
            echo.dryMix = 1f;
        }

        /// <summary>停止当前播报（进入会议 / 结局时可调用）。</summary>
        public void StopAll()
        {
            if (_sequence != null)
            {
                StopCoroutine(_sequence);
                _sequence = null;
            }
            if (_source != null) _source.Stop();
        }

        /// <summary>
        /// 开局播报：只播人物剧本里用引号框出的“脑海里的声音”
        /// （briefing_head_N，例如“你是1号，也是这层楼中唯一的杀手……”），
        /// 紧接着播“本轮最大号码是 Y 号”（max_Y）。
        /// 剧本正文、谁已经醒了、场上还剩几个人，一律不播——那是玩家不该知道的信息。
        /// </summary>
        public void PlayBriefing(int number, int maxNumber)
        {
            PlaySequence($"briefing_head_{number}", $"max_{maxNumber}");
        }

        /// <summary>开局颅内音（引号内那段话）的长度（秒）；片段缺失时返回 0。</summary>
        public static float IntroLength(int number)
        {
            AudioClip clip = Resources.Load<AudioClip>("Voice/" + LocalizedClip($"briefing_head_{number}"));
            return clip != null ? clip.length : 0f;
        }

        /// <summary>
        /// 中文版由 Huihui 预录，英文版由 Zira 预录（en_ 前缀）。
        /// 英文模式下绝不回放中文片段，否则选英语的玩家开局听到的还是中文。
        /// </summary>
        static string LocalizedClip(string clipName)
        {
            return Localization.IsEnglish ? "en_" + clipName : clipName;
        }

        /// <summary>玩家醒来时，已经醒着的号码（只播声音，不占屏幕文字）。</summary>
        public void PlayWake(int number)
        {
            Play($"wake_{number}");
        }

        /// <summary>独处期间某个 NPC 醒来。</summary>
        public void PlayNotice(int number)
        {
            Play($"notice_{number}");
        }

        public void PlaySoloEnd()
        {
            Play("solo_end");
        }

        public void PlayKill9()
        {
            Play("kill9");
        }

        public void Play(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return;

            string localized = LocalizedClip(clipName);
            AudioClip clip = Resources.Load<AudioClip>("Voice/" + localized);
            if (clip == null)
            {
                Debug.LogWarning($"[IntracranialVoice] 找不到语音片段：Voice/{localized}");
                return;
            }

            StopAll();
            _source.clip = clip;
            _source.Play();
        }

        public void PlaySequence(params string[] clipNames)
        {
            if (clipNames == null || clipNames.Length == 0) return;
            StopAll();
            _sequence = StartCoroutine(SequenceRoutine(clipNames));
        }

        IEnumerator SequenceRoutine(string[] clipNames)
        {
            for (int i = 0; i < clipNames.Length; i++)
            {
                string localized = LocalizedClip(clipNames[i]);
                AudioClip clip = Resources.Load<AudioClip>("Voice/" + localized);
                if (clip == null)
                {
                    Debug.LogWarning($"[IntracranialVoice] 找不到语音片段：Voice/{localized}");
                    continue;
                }

                _source.clip = clip;
                _source.Play();
                // 用真实时间等待：自由打字暂停游戏时间时，播报仍应正常结束。
                yield return new WaitForSecondsRealtime(clip.length + 0.15f);
            }

            _source.clip = null;
            _sequence = null;
        }
    }
}
