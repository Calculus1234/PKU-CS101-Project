using System.Collections.Generic;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 全局音效单例：从 Resources/SFX/ 加载并播放。
    ///
    /// 命名约定：
    ///   door_creak  开门
    ///   hammer_hit  锤击
    ///   knife_stab  匕首刺入
    ///   clock_tick  倒计时最后 10 秒循环
    ///   bell_toll   投票 / 结局 / 天亮
    /// </summary>
    public class SFXManager : MonoBehaviour
    {
        static SFXManager _instance;
        public static SFXManager Instance
        {
            get
            {
                if (_instance == null) EnsureInstance();
                return _instance;
            }
        }

        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        readonly List<AudioSource> _pool = new List<AudioSource>();
        AudioSource _clockSource;
        bool _clockPlaying;
        int _nextSource;
        bool _missingLogged;

        /// <summary>设置面板的音效音量（0~1）。</summary>
        public static float VolumeScale
        {
            get { return PlayerPrefs.GetFloat("HL_SfxVolume", 0.9f); }
            set { PlayerPrefs.SetFloat("HL_SfxVolume", Mathf.Clamp01(value)); PlayerPrefs.Save(); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            EnsureInstance();
        }

        static SFXManager EnsureInstance()
        {
            if (_instance != null) return _instance;
            GameObject go = new GameObject("SFXManager");
            _instance = go.AddComponent<SFXManager>();
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
            LoadAll();
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        void LoadAll()
        {
            AudioClip[] clips = Resources.LoadAll<AudioClip>("SFX");
            for (int i = 0; i < clips.Length; i++)
            {
                AudioClip clip = clips[i];
                if (clip != null) _clips[clip.name] = clip;
            }
            _clockSource = CreateSource("ClockTick", true);
        }

        AudioSource CreateSource(string name, bool loop)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = true;
            source.volume = 0.8f;
            _pool.Add(source);
            return source;
        }

        AudioSource NextSource()
        {
            if (_pool.Count < 8) CreateSource("SFX_" + _pool.Count, false);
            for (int attempt = 0; attempt < _pool.Count; attempt++)
            {
                AudioSource source = _pool[_nextSource];
                _nextSource = (_nextSource + 1) % _pool.Count;
                if (source != _clockSource && !source.isPlaying) return source;
            }
            return _pool[_nextSource];
        }

        public AudioClip GetClip(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            AudioClip clip;
            if (_clips.TryGetValue(clipName, out clip)) return clip;

            clip = Resources.Load<AudioClip>("SFX/" + clipName);
            if (clip != null) _clips[clipName] = clip;
            else if (!_missingLogged)
            {
                _missingLogged = true;
                Debug.LogWarning($"[SFXManager] 找不到音效 Resources/SFX/{clipName}");
            }
            return clip;
        }

        public bool HasClip(string clipName) => GetClip(clipName) != null;

        public void Play(string clipName, float volume = 1f, float pitch = 1f)
        {
            AudioClip clip = GetClip(clipName);
            if (clip == null) return;
            AudioSource source = NextSource();
            source.pitch = Mathf.Clamp(pitch, 0.5f, 2f);
            source.PlayOneShot(clip, Mathf.Clamp01(volume) * VolumeScale);
        }

        public void PlayDoorCreak(float volume = 0.65f) => Play("door_creak", volume, Random.Range(0.94f, 1.06f));
        public void PlayHammerHit(float volume = 0.95f) => Play("hammer_hit", volume, Random.Range(0.95f, 1.05f));
        public void PlayKnifeStab(float volume = 0.95f) => Play("knife_stab", volume, Random.Range(0.96f, 1.04f));

        public void PlayBellToll(float volume = 0.9f)
        {
            Play("bell_toll", volume, 1f);
        }

        /// <summary>倒计时最后 10 秒：开启 / 关闭循环滴答。</summary>
        public void SetClockTick(bool active)
        {
            if (_clockSource == null) return;
            if (active)
            {
                if (_clockPlaying) return;
                AudioClip clip = GetClip("clock_tick");
                if (clip == null) return;
                _clockSource.clip = clip;
                _clockSource.volume = 0.8f * VolumeScale;
                _clockSource.Play();
                _clockPlaying = true;
            }
            else if (_clockPlaying)
            {
                _clockSource.Stop();
                _clockPlaying = false;
            }
        }

        public bool ClockTickPlaying => _clockPlaying;

        /// <summary>按武器 id 播放攻击音效。</summary>
        public void PlayWeapon(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return;
            if (weaponId == "dagger" || weaponId == "knife") PlayKnifeStab();
            else if (weaponId == "hammer") PlayHammerHit();
        }
    }
}
