using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LLMUnity;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// NPC 的本地大模型大脑（Qwen3.5-4B / LLMUnity）。
    ///
    /// 生命周期：
    ///  - 搜证阶段：模型加载完成后保持空闲，不产生任何推理；NPC 仍由行为树 / A* 控制。
    ///  - 私聊 / 会议：调用方暂停行为树，通过 Generate() 取一句台词和工具调用。
    ///  - 超时 / 报错 / 模型未就绪：一律回退到 Yarn 或规则兜底，绝不阻塞游戏。
    ///
    /// 性能取舍（本机为 Intel 核显 + 12 线程 CPU）：
    ///  - 关闭视觉（不加载 mmproj）与 Thinking（reasoning=false）。
    ///  - 上下文 4096，单次最多 96 token（40 字台词 + 工具字段），temperature 0.6。
    ///  - 本机 4B Q4 无法在 1~2 秒内完成一次带完整上下文的推理：
    ///    1,700 字上下文实测总耗时 18~26 秒（提示词约 17 秒，生成约 7 秒）。
    ///    因此默认首 token 上限 40 秒、整段上限 60 秒；超时回退 Yarn，
    ///    不阻塞对话。想要更快只能换更小模型或缩短上下文。
    ///  - 超时 / 关闭对话都不调用 CancelRequests：llamalib 2.0.5 在提示词处理阶段
    ///    取消会把推理槽位卡死，之后所有请求都拿不到 token。只作废结果，让原生请求跑完。
    /// </summary>
    public class LLMNpcBrain : MonoBehaviour
    {
        static LLMNpcBrain _instance;

        /// <summary>
        /// 静态引用可能被编辑器 domain reload 清空（脚本在 Play 中被改动时会发生），
        /// 这里做一次兜底查找，避免私聊突然判定“模型没准备好”而退回 Yarn。
        /// </summary>
        public static LLMNpcBrain Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindObjectOfType<LLMNpcBrain>(true);
                return _instance;
            }
            private set { _instance = value; }
        }

        [Header("模型（StreamingAssets 下的文件名）")]
        [Tooltip("默认极速版 1.5B；启动时会用 F9 面板保存的选择覆盖")]
        public string modelFile = LlmSettings.ModelFast;
        [Tooltip("所选模型文件不存在时使用的备用模型")]
        public string fallbackModelFile = LlmSettings.ModelSmart;

        [Header("推理参数（CPU）")]
        [Tooltip("0 = 自动使用物理核心数 - 1")]
        public int numThreads = 0;
        [Tooltip("卸载到 GPU 的层数；0 = 纯 CPU。核显可试 99（走 Vulkan）")]
        public int gpuLayers = 99;
        [Tooltip("8192 足够装下 12 条线索 + 24 轮私聊记忆；提示词处理只算实际 token，不会因为上限变大而变慢")]
        public int contextSize = 8192;
        public int batchSize = 512;
        [Tooltip("单次输出上限。64 不够：中文台词会吃光 token，JSON 在数值字段处被截断；96 约等于 40 字台词 + 全套工具字段")]
        public int maxTokens = 96;
        [Range(0f, 1.5f)] public float temperature = 0.6f;
        [Range(0f, 1f)] public float topP = 0.9f;

        [Header("超时（秒）")]
        [Tooltip("首 token 超时：1.5B + 1500 字上下文实测 1.2~1.5 秒，慢机器留 4 倍余量")]
        public float firstTokenTimeout = 12f;
        [Tooltip("整段输出上限：1.5B 实测 4~5 秒出完整 JSON，慢机器留余量")]
        public float totalTimeout = 25f;

        [Header("Function Calling")]
        public bool useGrammar = true;

        LLM _llm;
        LLMAgent _agent;
        GameObject _root;
        bool _ready;
        bool _busy;
        bool _failed;
        bool _loading;
        string _loadStatus = "";
        string _lastError = "";
        int _requestId;
        float _busySince;
        Task _inflightTask;

        [Header("最近一次请求（只读调试）")]
        public string lastRaw = "";
        public string lastSpeech = "";
        public float lastElapsed = -1f;
        public float lastFirstTokenSeconds = -1f;
        public int lastCallCount;
        public bool lastTimedOut;
        public bool lastGotFirstToken;
        public int lastPromptChars;

        bool IsLocalReady => _ready && !_failed && !_loading && _llm != null && _llm.started;
        /// <summary>本地模型就绪，或云端 API 配置完整，都能接对话。</summary>
        public bool IsReady => IsLocalReady || LlmSettings.CloudReady;
        public bool IsBusy => _busy;
        public bool IsFailed => _failed;
        public bool IsLoadingModel => _loading;
        public string LoadStatus => _loadStatus;
        public string LastError => _lastError;
        public string CurrentModelLabel => ModelLabel(modelFile);
        public string Status => $"ready={IsReady} busy={_busy} failed={_failed} loading={_loading} " +
                                $"model={modelFile} started={(_llm != null && _llm.started)} error={_lastError}";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            if (FindObjectOfType<LLMNpcBrain>() != null) return;

            GameObject go = new GameObject("LLMNpcBrain");
            DontDestroyOnLoad(go);
            go.AddComponent<LLMNpcBrain>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            StartCoroutine(InitializeRoutine());
        }

        IEnumerator InitializeRoutine()
        {
            // 等场景加载完再开始吃内存；模型加载本身在 llama.cpp 后台线程。
            yield return null;

            try
            {
                // 启动时直接用上次在 F9 面板里选定的本地模型，不再每次手动选。
                string requested = LlmSettings.LocalModel;
                string resolved = ResolveModelFile(requested, out string notice);
                if (!string.IsNullOrEmpty(notice))
                {
                    Debug.LogWarning("[LLM] " + notice);
                    // 启动旁白会覆盖 HUD 提示，等旁白播完再把回退原因告诉玩家。
                    StartCoroutine(ShowFallbackNoticeRoutine(notice));
                }
                if (resolved != requested) LlmSettings.LocalModel = resolved;
                modelFile = resolved;
                CreateComponents();
            }
            catch (Exception ex)
            {
                _failed = true;
                _lastError = ex.Message;
                Debug.LogError($"[LLM] 创建 LLMUnity 组件失败：{ex}");
                yield break;
            }

            Debug.Log($"[LLM] 正在加载模型 {modelFile}（{(_llm.numGPULayers > 0 ? $"GPU {_llm.numGPULayers} 层 / Vulkan" : "纯 CPU")} / " +
                      $"{_llm.numThreads} 线程 / ctx={contextSize} / maxTokens={maxTokens}）……");

            float waited = 0f;
            while (!_llm.started && !_llm.failed && waited < 90f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_llm.failed || !_llm.started)
            {
                _failed = true;
                _lastError = "模型加载失败（检查 StreamingAssets 与 LlamaLib）";
                Debug.LogError($"[LLM] {_lastError}。游戏会继续使用 Yarn 兜底台词。");
                yield break;
            }

            Debug.Log($"[LLM] 模型加载完成：architecture={_llm.architecture}，开始预热。");

            // 服务已就绪，此时才能安全地关闭 Thinking（Qwen3 系列默认可能带思考段）。
            try { _llm.reasoning = false; }
            catch (Exception ex) { Debug.LogWarning($"[LLM] 关闭 Thinking 失败：{ex.Message}"); }

            // 预热：把一段很短的 System Prompt 先算一遍，缩短第一次私聊的首 token 延迟。
            Task warm = null;
            try
            {
                _agent.systemPrompt = "你是《大限将至》里的住客。";
                warm = _agent.Warmup();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LLM] 预热失败（不影响使用）：{ex.Message}");
            }

            if (warm != null)
            {
                float warmWaited = 0f;
                while (!warm.IsCompleted && warmWaited < 45f)
                {
                    warmWaited += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            _ready = true;
            Debug.Log("[LLM] 就绪：搜证阶段保持休眠；私聊/会议时由 LLM 接管。");
        }

        IEnumerator ShowFallbackNoticeRoutine(string notice)
        {
            // 启动时玩家由 CycleController 在场景加载后才生成，先等它出现。
            PlayerController player = null;
            float spawnWait = 0f;
            while (player == null && spawnWait < 10f)
            {
                player = FindObjectOfType<PlayerController>();
                spawnWait += Time.unscaledDeltaTime;
                yield return null;
            }
            if (player == null) yield break;
            InteractionSystem hud = player.GetComponentInChildren<InteractionSystem>(true);

            // 等开场旁白（含知识提示）播完：连续 0.5s 没有 HUD 消息再显示，最多等 30s。
            float waited = 0f;
            float quiet = 0f;
            while (waited < 30f)
            {
                bool busy = hud != null && hud.messageText != null
                            && hud.messageText.gameObject.activeInHierarchy
                            && !string.IsNullOrEmpty(hud.messageText.text);
                if (busy) quiet = 0f;
                else
                {
                    quiet += Time.unscaledDeltaTime;
                    if (waited >= 1f && quiet >= 0.5f) break;
                }
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            player.ShowMessage(notice, 6f);
        }

        string ResolveModelFile(string requested, out string notice)
        {
            notice = "";
            string root = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(requested) && File.Exists(Path.Combine(root, requested)))
                return requested;

            string fallback = requested == LlmSettings.ModelSmart
                ? LlmSettings.ModelFast
                : LlmSettings.ModelSmart;
            if (File.Exists(Path.Combine(root, fallback)))
            {
                notice = Localization.IsEnglish
                    ? $"The {ModelLabel(requested)} model file is missing; loaded the {ModelLabel(fallback)} model instead."
                    : $"{ModelLabel(requested)}模型文件缺失，已自动加载{ModelLabel(fallback)}模型。";
                return fallback;
            }

            notice = Localization.IsEnglish
                ? $"The {ModelLabel(requested)} model file is missing, and no fallback model is available."
                : $"{ModelLabel(requested)}模型文件缺失，且没有可用的备用模型。";
            return string.IsNullOrEmpty(requested) ? LlmSettings.ModelFast : requested;
        }

        public static string ModelLabel(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "未知";
            if (fileName == LlmSettings.ModelFast) return "极速版（1.5B）";
            if (fileName == LlmSettings.ModelSmart) return "高智版（4B）";
            return fileName;
        }

        /// <summary>
        /// 运行时切换本地模型：先卸下旧服务，再加载新 gguf。
        /// 只在没有进行中的请求时执行；等待原生线程收尾后才释放组件，避免崩溃。
        /// </summary>
        public IEnumerator ReloadLocalModelRoutine(string fileName, Action<bool, string> done)
        {
            if (_loading)
            {
                done?.Invoke(false, "模型正在加载中，请稍候。");
                yield break;
            }

            if (string.IsNullOrEmpty(fileName)) fileName = LlmSettings.ModelFast;

            string resolved = ResolveModelFile(fileName, out string notice);
            bool fellBack = resolved != fileName;
            if (fellBack && !File.Exists(Path.Combine(Application.streamingAssetsPath, resolved)))
            {
                done?.Invoke(false, notice);
                yield break;
            }
            if (!string.IsNullOrEmpty(notice)) Debug.LogWarning("[LLM] " + notice);

            _loading = true;
            _loadStatus = "正在卸载旧模型……";
            Abort();

            // Abort() 只作废结果，不打断原生线程。真正释放之前必须等这次推理跑完，
            // 否则在提示词处理阶段 Dispose 会让 llama.cpp 访问已释放的内存。
            float waited = 0f;
            while (_inflightTask != null && !_inflightTask.IsCompleted
                   && waited < totalTimeout + 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_inflightTask != null && !_inflightTask.IsCompleted)
            {
                _loading = false;
                _loadStatus = "";
                done?.Invoke(false, "还有一次回复正在生成，等它结束后再切换模型。");
                yield break;
            }
            _inflightTask = null;

            try { _agent?.CancelRequests(); }
            catch (Exception ex) { Debug.LogWarning($"[LLM] CancelRequests 失败（继续卸载）：{ex.Message}"); }

            _ready = false;
            _failed = false;
            _lastError = "";

            if (_llm != null)
            {
                try { _llm.Destroy(); }
                catch (Exception ex) { Debug.LogWarning($"[LLM] 释放旧模型失败：{ex.Message}"); }
            }
            if (_root != null) Destroy(_root);
            _root = null;
            _llm = null;
            _agent = null;

            // 等两帧，让 OnDestroy 与原生卸载真正跑完。
            yield return null;
            yield return null;

            modelFile = resolved;
            LlmSettings.LocalModel = resolved;
            _loadStatus = Localization.IsEnglish
                ? $"Loading {ModelLabel(resolved)}..."
                : $"正在加载{ModelLabel(resolved)}……";

            Debug.Log($"[LLM] 切换模型：{resolved}……");
            try
            {
                CreateComponents();
            }
            catch (Exception ex)
            {
                _failed = true;
                _lastError = ex.Message;
                _loading = false;
                done?.Invoke(false, Localization.IsEnglish
                    ? $"Failed to load {ModelLabel(resolved)}: {ex.Message}"
                    : $"加载{ModelLabel(resolved)}失败：{ex.Message}");
                yield break;
            }

            float loadWait = 0f;
            while (_llm != null && !_llm.started && !_llm.failed && loadWait < 120f)
            {
                loadWait += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_llm == null || _llm.failed || !_llm.started)
            {
                _failed = true;
                _lastError = "模型加载失败（检查 StreamingAssets 与 LlamaLib）";
                _loading = false;
                Debug.LogError($"[LLM] {ModelLabel(resolved)}加载失败。");
                done?.Invoke(false, Localization.IsEnglish
                    ? $"Failed to load the {ModelLabel(resolved)} model. Check StreamingAssets."
                    : $"{ModelLabel(resolved)}模型加载失败，请检查 StreamingAssets。");
                yield break;
            }

            try { _llm.reasoning = false; }
            catch (Exception ex) { Debug.LogWarning($"[LLM] 关闭 Thinking 失败：{ex.Message}"); }

            Task warm = null;
            try
            {
                _agent.systemPrompt = "你是《大限将至》里的住客。";
                warm = _agent.Warmup();
            }
            catch (Exception ex) { Debug.LogWarning($"[LLM] 预热失败（不影响使用）：{ex.Message}"); }

            if (warm != null)
            {
                float warmWait = 0f;
                while (!warm.IsCompleted && warmWait < 45f)
                {
                    warmWait += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            _ready = true;
            _loading = false;
            _loadStatus = "";
            Debug.Log($"[LLM] 已切换到{ModelLabel(resolved)}（{loadWait:0.0} 秒）。");
            done?.Invoke(true, string.IsNullOrEmpty(notice)
                ? (Localization.IsEnglish
                    ? $"Switched to the {ModelLabel(resolved)} model."
                    : $"已切换到{ModelLabel(resolved)}模型。")
                : notice);
        }

        void CreateComponents()
        {
            // 关键：先建在 inactive 根节点下，把字段全部写好再激活，
            // 避免 LLM / LLMAgent 的 Awake 在模型路径还是空的时候先跑。
            GameObject root = new GameObject("LLM_Runtime");
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            _root = root;

            _llm = root.AddComponent<LLM>();
            _llm.model = modelFile;
            _llm.contextSize = contextSize;
            _llm.numThreads = numThreads > 0
                ? numThreads
                : Mathf.Clamp(SystemInfo.processorCount - 1, 4, 16);
            _llm.numGPULayers = gpuLayers;   // 0 = 纯 CPU；核显可试 99（Vulkan）
            _llm.parallelPrompts = 1;       // 一次只处理一个 NPC，避免上下文切换
            _llm.batchSize = batchSize;
            _llm.flashAttention = false;    // CPU 后端不强行开启
            // 注意：LLMUnity 3.x 的 reasoning setter 会立刻访问尚未创建的 llmService，
            // 只能在服务启动后调用。_reasoning 默认就是 false，这里不提前赋值。
            _llm.dontDestroyOnLoad = false;

            GameObject agentGo = new GameObject("LLM_Agent");
            agentGo.transform.SetParent(root.transform, false);
            _agent = agentGo.AddComponent<LLMAgent>();
            _agent.llm = _llm;
            _agent.numPredict = maxTokens;
            _agent.temperature = temperature;
            _agent.topP = topP;
            _agent.topK = 40;
            _agent.repeatPenalty = 1.08f;
            _agent.cachePrompt = true;

            root.SetActive(true);
        }

        /// <summary>
        /// 调用方提前关闭私聊 / 会议时，取消正在进行的请求并释放占用。
        /// 被 StopCoroutine 打断的 Generate 不会执行收尾代码，所以必须外部主动调用。
        /// </summary>
        public void Abort()
        {
            _requestId++;
            // 云端请求可以直接中止；本地不能调用 CancelRequests（见下方注释）。
            if (LlmSettings.CloudReady) CloudLlmClient.Abort();
            // 不调用 CancelRequests：llamalib 2.0.5 在提示词处理阶段取消会把槽位卡死，
            // 之后所有请求都拿不到 token（实测最小提示词 60 秒无输出）。
            // 这里只作废本次结果；原生请求自己跑完，槽位自然释放。
            _busy = false;
            _busySince = 0f;
        }

        /// <summary>
        /// 生成一句 NPC 台词 + 工具调用。
        /// 调用方以协程方式 yield 本方法；done 一定会在有限时间内被调用。
        /// </summary>
        public IEnumerator Generate(NpcContext ctx, Action<NpcLLMReply> done, Action<string> onPartial = null)
        {
            if (done == null) yield break;

            // 模型切换期间不接新请求：此时 _llm / _agent 正在被销毁，调用会拿不到 token。
            if (_loading)
            {
                Debug.LogWarning("[LLM] 模型正在重载，本次直接回退。");
                done(null);
                yield break;
            }

            // 看门狗：上一回合协程被外部 StopCoroutine 时，_busy 可能来不及释放。
            if (_busy && Time.realtimeSinceStartup - _busySince > totalTimeout + 2f)
                Abort();

            if (_busy)
            {
                Debug.LogWarning("[LLM] 上一回合还没结束，本次直接回退。");
                done(null);
                yield break;
            }

            // 云端优先：配置完整时走 OpenAI 兼容接口，失败再退回本地模型。
            if (LlmSettings.CloudReady)
            {
                bool cloudFailed = false;
                yield return GenerateCloud(ctx,
                    r => { if (r == null) cloudFailed = true; else done(r); },
                    onPartial);

                if (!cloudFailed) yield break;
                if (!IsLocalReady)
                {
                    done(null);
                    yield break;
                }
                Debug.Log("[LLM] 云端不可用，回退本地模型。");
            }

            if (!IsLocalReady)
            {
                done(null);
                yield break;
            }

            if (ctx == null || ctx.npc == null)
            {
                done(null);
                yield break;
            }

            int requestId = ++_requestId;
            _busy = true;
            _busySince = Time.realtimeSinceStartup;
            string systemPrompt = NpcContextBuilder.BuildSystemPrompt(ctx);
            string userMessage = NpcContextBuilder.BuildUserMessage(ctx);
            lastRaw = "";
            lastSpeech = "";
            lastElapsed = -1f;
            lastCallCount = 0;
            lastTimedOut = false;
            lastGotFirstToken = false;
            lastFirstTokenSeconds = -1f;
            lastPromptChars = (systemPrompt ?? "").Length + (userMessage ?? "").Length;

            string partial = "";
            bool gotFirstToken = false;
            Task<string> task = null;
            float start = Time.realtimeSinceStartup;

            try
            {
                _agent.systemPrompt = systemPrompt;
                _agent.SetGrammar(useGrammar ? NpcToolGrammar.Gbnf : "");
                _agent.numPredict = maxTokens;

                // 不使用 LLMAgent 自带历史：上下文每次完全由 ContextBuilder 重建，
                // 避免旧的 System Prompt 和旧的发言在模型里叠成一团。
                _agent.chat = new List<LLMUnity.ChatMessage>();

                task = _agent.Chat(userMessage, p =>
                {
                    partial = p ?? "";
                    if (!string.IsNullOrEmpty(partial))
                    {
                        if (!gotFirstToken)
                        {
                            gotFirstToken = true;
                            lastFirstTokenSeconds = Time.realtimeSinceStartup - start;
                            Debug.Log($"[LLM] 首 token：{lastFirstTokenSeconds:0.00}s（prompt={lastPromptChars} 字）");
                        }

                        if (onPartial != null
                            && NpcToolCallParser.TryExtractSpeechPartial(partial, out string streamed)
                            && !string.IsNullOrEmpty(streamed))
                            onPartial(streamed);
                    }
                }, null, false);
                _inflightTask = task;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Debug.LogWarning($"[LLM] 请求启动失败，回退：{ex.Message}");
                if (requestId == _requestId) _busy = false;
                done(null);
                yield break;
            }

            bool timedOut = false;
            while (task != null && !task.IsCompleted)
            {
                if (requestId != _requestId)
                {
                    done(null);
                    if (task != null && !task.IsCompleted)
                        StartCoroutine(ReleaseBusyWhenDone(task, requestId));
                    else
                        _busy = false;
                    yield break;
                }

                float now = Time.realtimeSinceStartup;
                if (!gotFirstToken && now - start > firstTokenTimeout)
                {
                    timedOut = true;
                    break;
                }
                if (now - start > totalTimeout)
                {
                    timedOut = true;
                    break;
                }
                yield return null;
            }

            if (requestId != _requestId)
            {
                done(null);
                if (task != null && !task.IsCompleted)
                    StartCoroutine(ReleaseBusyWhenDone(task, requestId));
                else
                    _busy = false;
                yield break;
            }

            if (timedOut)
            {
                // 不取消原生请求：见 Abort() 的注释。让它在后台跑完，槽位自然释放。
                Debug.LogWarning($"[LLM] {(gotFirstToken ? "整段" : "首 token")}超时，放弃本次结果。");
            }

            if (timedOut && task != null && !task.IsCompleted)
            {
                lastTimedOut = true;
                lastGotFirstToken = gotFirstToken;
                lastElapsed = Time.realtimeSinceStartup - start;
                done(null);
                StartCoroutine(ReleaseBusyWhenDone(task, requestId));
                yield break;
            }

            string raw = "";
            if (task != null && task.IsCompleted && !task.IsFaulted && !task.IsCanceled)
            {
                try { raw = task.Result; }
                catch (Exception ex) { _lastError = ex.Message; }
            }
            if (string.IsNullOrEmpty(raw)) raw = partial;

            lastRaw = raw;
            lastTimedOut = timedOut;
            lastGotFirstToken = gotFirstToken;
            lastElapsed = Time.realtimeSinceStartup - start;

            NpcLLMReply reply = NpcToolCallParser.Parse(raw);
            if (reply != null)
            {
                reply.elapsedSeconds = lastElapsed;
                lastSpeech = reply.speech ?? "";
                lastCallCount = reply.calls.Count;

                // 超时截断时只接受已经说完整的短句；否则宁可回退 Yarn。
                if (timedOut && reply.speech != null && reply.speech.Length < 6 && reply.calls.Count <= 1)
                    reply = null;
            }

            if (reply == null)
                Debug.LogWarning($"[LLM] 输出无法解析，回退到 Yarn/规则台词。原始输出：{TrimRaw(raw)}");
            else
                Debug.Log($"[LLM] {ctx.npc.Number}号回复（{reply.elapsedSeconds:0.00}s）：{reply.speech}");

            if (_inflightTask == task) _inflightTask = null;
            _busy = false;
            done(reply);
        }

        /// <summary>云端一轮：SSE 流式，收完整 JSON 后与本地共用同一套解析与字段。</summary>
        IEnumerator GenerateCloud(NpcContext ctx, Action<NpcLLMReply> done, Action<string> onPartial)
        {
            if (ctx == null || ctx.npc == null)
            {
                done?.Invoke(null);
                yield break;
            }

            int requestId = ++_requestId;
            _busy = true;
            _busySince = Time.realtimeSinceStartup;

            string systemPrompt = NpcContextBuilder.BuildSystemPrompt(ctx);
            string userMessage = NpcContextBuilder.BuildUserMessage(ctx);

            lastRaw = "";
            lastSpeech = "";
            lastElapsed = -1f;
            lastCallCount = 0;
            lastTimedOut = false;
            lastGotFirstToken = false;
            lastFirstTokenSeconds = -1f;
            lastPromptChars = (systemPrompt ?? "").Length + (userMessage ?? "").Length;

            float start = Time.realtimeSinceStartup;
            bool gotFirst = false;

            Action<string> partialHandler = p =>
            {
                if (requestId != _requestId) return;

                lastRaw = p ?? "";
                if (!gotFirst && !string.IsNullOrEmpty(lastRaw))
                {
                    gotFirst = true;
                    lastGotFirstToken = true;
                    lastFirstTokenSeconds = Time.realtimeSinceStartup - start;
                    Debug.Log($"[CloudLLM] 首 token：{lastFirstTokenSeconds:0.00}s（prompt={lastPromptChars} 字）");
                }

                if (onPartial != null
                    && NpcToolCallParser.TryExtractSpeechPartial(lastRaw, out string streamed)
                    && !string.IsNullOrEmpty(streamed))
                    onPartial(streamed);
            };

            NpcLLMReply reply = null;
            yield return CloudLlmClient.Generate(systemPrompt, userMessage, maxTokens, temperature,
                partialHandler, r => reply = r);

            if (requestId != _requestId)
            {
                _busy = false;
                _busySince = 0f;
                yield break;
            }

            _busy = false;
            _busySince = 0f;
            lastElapsed = Time.realtimeSinceStartup - start;
            lastGotFirstToken = gotFirst;

            if (reply != null)
            {
                reply.elapsedSeconds = lastElapsed;
                lastSpeech = reply.speech ?? "";
                lastCallCount = reply.calls.Count;
                if (string.IsNullOrEmpty(lastRaw)) lastRaw = reply.raw;
                Debug.Log($"[CloudLLM] {ctx.npc.Number}号回复（{lastElapsed:0.00}s）：{reply.speech}");
            }
            else
            {
                Debug.LogWarning($"[CloudLLM] 请求失败或无输出。raw={TrimRaw(lastRaw)}");
            }

            done?.Invoke(reply);
        }

        /// <summary>
        /// 超时 / 作废后，等原生请求真正结束再释放占用。
        /// 期间新的请求会排队，而不是在同一个 LLMAgent 上并发改 systemPrompt。
        /// </summary>
        IEnumerator ReleaseBusyWhenDone(Task task, int requestId)
        {
            while (task != null && !task.IsCompleted) yield return null;

            if (_inflightTask == task) _inflightTask = null;

            // 只有没有更新一轮请求接管时才清 _busy。
            if (requestId == _requestId)
            {
                _busy = false;
                _busySince = 0f;
            }
        }

        static string TrimRaw(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        [ContextMenu("运行一次 LLM 冒烟测试")]
        public void RunSmokeTest()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[LLM] 冒烟测试需要在 Play 模式运行。");
                return;
            }
            StartCoroutine(SmokeTestRoutine());
        }

        IEnumerator SmokeTestRoutine()
        {
            // 冒烟测试只针对本地 GGUF：云端走的是另一条链路。
            if (!IsLocalReady)
            {
                Debug.LogWarning($"[LLM] 冒烟测试跳过：{Status}");
                yield break;
            }

            NPCController npc = FindObjectOfType<NPCController>();
            if (npc == null)
            {
                Debug.LogWarning("[LLM] 场景里没有 NPC，冒烟测试跳过。");
                yield break;
            }

            NpcContext ctx = NpcContextBuilder.BuildPrivate(npc, "你醒来之后，去过哪里？", null);
            NpcLLMReply reply = null;
            yield return Generate(ctx, r => reply = r);

            if (reply == null)
                Debug.LogWarning("[LLM] 冒烟测试失败：没有拿到可解析的回复。");
            else
                Debug.Log($"[LLM] 冒烟测试通过：{reply.speech}（calls={reply.calls.Count}）");
        }
    }
}
