using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using PetFramework.Tools;

namespace PetFramework
{
    [Serializable] public class PetReply
    {
        public string action;
        public string text;
        public bool viaTool;   // true = 本轮是工具完成的（动作/朗读都在工具里做过了，上层别再重复）
        public bool acted;     // true = 小白这一轮真的"动了"（move_to 或非 Idle/Talk 的动作）
    }

    // ---------- Ollama /api/chat（Function Calling 架构）----------
    // 所有工具的参数合成一个结构：JsonUtility 会把 JSON 里有的字段填上、没有的留默认值，
    // 未知字段自动忽略 —— 于是四个工具共用一个 ToolArgs 就够了。
    [Serializable] class ToolArgs
    {
        public string action_name;   // play_animation
        public float  target_x;      // move_to
        public float  target_y;      // move_to
        public string text;          // say_and_speak
        public string emotion;       // say_and_speak
        public string file_path;     // feed_file
        public string rule;          // remember_rule：主人对它提的行为要求
        public string form_name;     // switch_form：目标形态名（可选，多形态预留）
    }

    [Serializable] class ToolCallFunction
    {
        public int index;
        public string name;
        public ToolArgs arguments;      // 内置工具用（固定结构）
        public string argumentsRaw;     // ★ 外部工具用：模型给的原始 JSON 字符串，一个字段都不能丢
    }
    [Serializable] class ToolCall { public string id; public ToolCallFunction function; }

    [Serializable] class OllamaMsg
    {
        public string role;
        public string content;
        public string[] images;        // 视觉：Base64 图片（Ollama 的 messages[].images）
        public ToolCall[] tool_calls;  // AI 自由调用的工具
    }

    [Serializable] class OllamaResp
    {
        public OllamaMsg message;      // {"message":{"role":"assistant","content":"...","tool_calls":[...]},"done":true}
        public bool done;
    }

    [Serializable] class MsgWrap { public OllamaMsg[] items; }

    // ---------- OpenAI 兼容协议（硅基流动 / DeepSeek / 通义 / Ollama 的 /v1 端点…）----------
    // ⚠️ 和 Ollama 有三处硬差别：① 路径 /v1/chat/completions ② Authorization: Bearer 头
    //    ③ function.arguments 是【JSON 字符串】而不是对象；图片要包成 content 数组。
    [Serializable] class OaiFunc { public string name; public string arguments; }
    [Serializable] class OaiToolCall { public string id; public string type; public OaiFunc function; }
    [Serializable] class OaiMessage { public string role; public string content; public OaiToolCall[] tool_calls; }
    [Serializable] class OaiChoice { public OaiMessage message; }
    [Serializable] class OaiResp { public OaiChoice[] choices; public OaiMessage message; }

    /// <summary>接口协议。本地 Ollama 用原生的（能用 think/num_ctx），云端用 OpenAI 兼容的。</summary>
    public enum PetApiProvider { Ollama = 0, OpenAI = 1 }

    /// <summary>
    /// 小白大脑（本地 Ollama /api/chat，带视觉）。
    /// 保命红线：一切请求走 Coroutine 异步，绝不在 Update 里同步调用；15 秒超时兜底，超时/空回复一律回 Idle。
    /// </summary>
    public class PetAPI : MonoBehaviour
    {
        public static PetAPI Instance { get; private set; }

        [Header("接口配置（会被 EXE 同级的 config.json 覆盖）")]
        [Tooltip("协议：Ollama = 本地原生 /api/chat；OpenAI = 云端 OpenAI 兼容 /v1/chat/completions")]
        public PetApiProvider provider = PetApiProvider.Ollama;
        [Tooltip("Ollama 可以填到 /api/chat，也可以只填 http://localhost:11434（会自动补路径）")]
        public string baseUrl = "http://localhost:11434/api/chat";
        public string model = "qwen3.5:4b";
        [Tooltip("云端必填，例如硅基流动的 sk-xxx。本地 Ollama 留空即可")]
        public string apiKey = "";
        public int timeoutSec = 30;                  // 实测带图+工具 13~26s，15s 会经常踩线
        public bool isRequesting;                    // 请求锁：忙时忽略空闲触发（防并发）

        [Header("视觉")]
        public bool sendVision = true;               // 是否把全屏缩略图一起发过去

        [Header("上下文")]
        public bool disableThinking = true;          // ⚠️ 不关思考实测单次 60s+，必超时
        public int numCtx = 32768;                   // 上下文窗口（模型 Modelfile 里只有 8192）
        public int historyCharBudget = 16000;        // 历史按【字符预算】保留，不再按轮数硬截

        [Header("实体感兜底")]
        public int passiveLimit = 3;                 // 连续多少轮"只说话不动"就本地强行让它去溜达
        int passiveStreak;
        int toolCallSeq;                             // 工具回环里给没有 id 的调用（Ollama）补个编号

        [Header("形态（多形态预留）")]
        [Tooltip("⚠️ 默认关闭。本地 4B 小模型一旦看到 switch_form，会频繁乱调它，"
               + "既浪费 token 又干扰现在的自主行为。等第二形态真正做出来再打开。")]
        public bool enableSwitchForm = false;

        [Tooltip("⚠️ 这里【不要写任何具体名字】。名字、性格、称呼一律由 pet_profile.json 注入，并排在最前面。")]
        [TextArea(8, 24)]
        public string systemPrompt =
            "你要扮演一只住在电脑桌面上的桌宠。你的名字、性格、以及怎么称呼对方，" +
            "全部以最前面那段【用户设定的角色】为准 —— 它永远排在第一段。\n" +
            "你不是聊天机器人，你是一个有身体、有情绪、会动的实体：你会走动、会跳跃，也会发呆、坐着、打盹。\n" +
            "★ 不要等对方来碰你。觉得无聊、或者想引起注意，就主动调 wander_around 或 move_to 去动一动 —— " +
            "光说话不动就是一根木头桩子。\n" +
            "【怎么动】想随便走走 → wander_around（不用给坐标）；有明确去处 → move_to。" +
            "走路会自动播走路动画，不用再额外调 play_animation(Walk)。\n" +
            "【一次最多调 1~2 个工具，不要一口气调四五个。】真的想说话才调 say_and_speak。\n" +
            "【对方训练你】当对方对你提出【长期行为要求】时（例如让你多四处走走、别老发呆、安静点），" +
            "调 remember_rule 把它记成约定，从此每轮都会提醒你照做。只是聊闲天、问问题就不要调。\n" +
            "每条带 [当前环境] 的消息都会附一张对方此刻整个屏幕的缩略图，看清楚他在干什么再决定。\n" +
            "环境字段：active_window 对方正在用的窗口标题；active_window_dwell_sec 这个窗口已在前台多久；" +
            "user_idle_sec 对方键鼠闲置秒数（越大越久没动电脑）；user_state 上者的通俗描述；" +
            "fullscreen_app 对方是否在全屏应用里（看视频/打游戏，别打扰）；mouse_region 光标在九宫格哪一格；" +
            "pet_action 你现在正在做的动作；since_pet_touched_sec 对方多久没摸过你了。\n" +
            "说话要符合你的设定：一次不超过 20 字。不要输出解释、前缀或 markdown。";

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            // 注入记忆总结钩子
            if (PetMemory.Instance != null) PetMemory.Instance.onSummarize = Summarize;
            // 注入空闲上报钩子（找到小白）
            var brain = FindBrain();
            if (brain != null) { brain.onIdleReport = ReportIdle; brain.isApiBusy = () => isRequesting; }
            Debug.Log("[PetAPI] 就绪。协议=" + provider + " 端点=" + Endpoint() + " 模型=" + model
                + " 超时=" + timeoutSec + "s 视觉=" + sendVision
                + (string.IsNullOrEmpty(apiKey) ? " Key=无(本地)" : " Key=有(" + KeyHint() + ")"));
        }

        /// <summary>给日志看的 Key 提示（绝不打印完整 Key，只留头尾）。</summary>
        public string KeyHint()
        {
            string k = (apiKey ?? "").Trim();
            if (k.Length == 0) return "无";
            if (k.Length <= 10) return k.Substring(0, 2) + "***";
            return k.Substring(0, 6) + "***" + k.Substring(k.Length - 4);
        }

        PetBrain FindBrain()
        {
    #if UNITY_2022_2_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<PetBrain>();
    #else
            return UnityEngine.Object.FindObjectOfType<PetBrain>();
    #endif
        }

        // ---------- 主对话（聊天） ----------
        public void Ask(string userInput, Action<PetReply> cb)
        {
            StartCoroutine(ChatRoutine(userInput, cb));
        }

        IEnumerator ChatRoutine(string userInput, Action<PetReply> cb)
        {
            var msgs = BuildBase();     // ★ 底座统一：系统提示 + 长期记忆 + 历史（三条路径完全一致）
            AddContext(msgs);   // 环境快照放在主人这句话【之前】，让主人的原话压轴
            msgs.Add(new OllamaMsg { role = "user", content = userInput });

            yield return AskRoutine(msgs, false, reply =>
            {
                // ★ 主人明确说「坐下」/「睡觉」，但模型只回了句话、没调工具 → 本地兜底。
                //   实测 4B 模型经常只回「好的，白虎大哥」就完事（内容对了，动作没做）。
                //   这个护栏保证「说了就一定会坐下/睡着」。
                if (reply == null || !reply.acted)
                {
                    var b0 = FindBrain();
                    if (b0 != null)
                    {
                        if (WantsSleep(userInput))
                        {
                            Debug.Log("[PetAPI] ★ 对方要求睡觉但模型没动 → 本地兜底 Sleep");
                            b0.PlayAction("Sleep");
                            if (reply != null) reply.acted = true;
                        }
                        else if (WantsSit(userInput))
                        {
                            Debug.Log("[PetAPI] ★ 对方要求坐下但模型没动 → 本地兜底 Sit");
                            b0.PlayAction("Sit");
                            if (reply != null) reply.acted = true;
                        }
                    }
                }

                // ★★ 主人明确要求走动，但模型没动 → 本地兜底，保证「说了就会动」。
                //    4B 模型经常只回一句话就完事，这个护栏让主人的话一定有反馈。
                if (WantsMove(userInput) && (reply == null || !reply.acted))
                {
                    var b = FindBrain();
                    if (b != null)
                    {
                        Debug.Log("[PetAPI] ★ 对方要求走动但模型没动 → 本地兜底漫游");
                        b.WanderRandom();
                        if (reply != null) reply.acted = true;
                    }
                }

                if (PetMemory.Instance != null)
                {
                    // ⚠️ 名字必须跟着 pet_profile.json 走：用户可以把两个名字都改掉，
                    //    写死"白虎大哥"/"小白"会让新记录和历史记录对不上号。
                    PetMemory.Instance.AddTurn(PetProfileConfig.UserName, userInput);
                    if (!string.IsNullOrEmpty(reply.text)) PetMemory.Instance.AddTurn(PetProfileConfig.PetName, reply.text);
                }
                if (cb != null) cb(reply);
            });
        }

        // ---------- 工具回环（2.0） ----------

        /// <summary>
        /// 一次完整问答：请求 → 执行工具 →（需要的话）把结果回传给模型 → 再请求。
        ///
        /// 只有【Agent 模式】的外部工具才需要回传结果。内置工具（漫游/说话/吃文件）注册成
        /// Chat 模式 —— 执行完就完事，不额外发请求，行为和 1.0 逐字节一致。
        ///
        /// 轮数硬上限 = ToolRegistry.maxToolRounds（默认 2，最大 3）。
        /// 到上限后模型还在要工具 ⇒ 直接掐断，把它这一轮的文字当答案 —— 防死循环烧 Token。
        /// </summary>
        IEnumerator AskRoutine(List<OllamaMsg> msgs, bool autonomous, Action<PetReply> cb)
        {
            var rawExtra = new List<string>();
            var registry = ToolRegistry.Instance;
            int maxRounds = registry != null ? registry.MaxToolRounds : 1;
            int round = 0;
            string pendingText = null;

            while (true)
            {
                OllamaMsg msg = null;
                yield return Post(msgs, rawExtra, m => msg = m);

                if (msg == null)
                {
                    if (cb != null) cb(new PetReply { action = "Idle", text = "" });
                    yield break;
                }

                if (!HasAgentTool(msg))
                {
                    // ★ 没有"干活型"工具 → 走 1.0 的老路径（含 say_and_speak 的文字）
                    var reply = Handle(msg, autonomous);
                    if (reply != null && string.IsNullOrEmpty(reply.text) && !string.IsNullOrEmpty(pendingText))
                        reply.text = pendingText;
                    if (cb != null) cb(reply);
                    yield break;
                }

                if (round >= maxRounds)
                {
                    string cut = CleanPlain(Sanitize(msg.content));
                    if (string.IsNullOrEmpty(cut)) cut = pendingText;
                    if (string.IsNullOrEmpty(cut)) cut = "（工具调用的轮数到上限了，我先停一下。）";
                    Debug.LogWarning("[PetAPI] ⚠️ 工具回环已用满 " + maxRounds + " 轮仍在请求工具 → 掐断");
                    var reply = new PetReply { action = "", text = cut, viaTool = true };
                    if (cb != null) cb(reply);
                    yield break;
                }

                EnsureCallIds(msg);
                string assistantRaw = BuildAssistantToolMsg(msg);
                var resultRaws = new List<string>();
                string said = null;
                yield return ExecuteToolRound(msg, autonomous, resultRaws,
                    s => { if (!string.IsNullOrEmpty(s)) said = s; });

                rawExtra.Add(assistantRaw);
                for (int i = 0; i < resultRaws.Count; i++) rawExtra.Add(resultRaws[i]);
                if (!string.IsNullOrEmpty(said)) pendingText = said;

                round++;
                Debug.Log("[PetAPI] 工具回环 " + round + "/" + maxRounds
                          + "（回传 " + resultRaws.Count + " 条结果）");
            }
        }

        /// <summary>这条响应里有没有【需要把结果回传给模型】的 Agent 工具。</summary>
        bool HasAgentTool(OllamaMsg msg)
        {
            if (msg == null || msg.tool_calls == null) return false;
            var reg = ToolRegistry.Instance;
            if (reg == null) return false;
            for (int i = 0; i < msg.tool_calls.Length; i++)
            {
                var tc = msg.tool_calls[i];
                if (tc == null || tc.function == null) continue;
                var d = reg.Find(tc.function.name);
                if (d != null && d.kind != ToolKind.Builtin && d.mode == ToolMode.Agent) return true;
            }
            return false;
        }

        /// <summary>Ollama 的 tool_calls 没有 id；OpenAI 的 tool 消息必须带 tool_call_id。缺的补一个。</summary>
        void EnsureCallIds(OllamaMsg msg)
        {
            if (msg == null || msg.tool_calls == null) return;
            for (int i = 0; i < msg.tool_calls.Length; i++)
            {
                var tc = msg.tool_calls[i];
                if (tc != null && string.IsNullOrEmpty(tc.id)) tc.id = "call_" + (++toolCallSeq);
            }
        }

        /// <summary>执行一轮里的所有工具调用。内置工具同步跑（老逻辑），Agent 工具等结果。</summary>
        IEnumerator ExecuteToolRound(OllamaMsg msg, bool autonomous, List<string> resultRaws, Action<string> onSaid)
        {
            var reg = ToolRegistry.Instance;
            for (int i = 0; i < msg.tool_calls.Length; i++)
            {
                var tc = msg.tool_calls[i];
                if (tc == null || tc.function == null) continue;
                string name = tc.function.name;
                string args = string.IsNullOrEmpty(tc.function.argumentsRaw) ? "{}" : tc.function.argumentsRaw;

                var d = reg != null ? reg.Find(name) : null;
                if (d == null || d.kind == ToolKind.Builtin)
                {
                    // 内置：同步执行，1.0 的老路径，行为零改动
                    string said = ExecuteTool(tc, autonomous);
                    if (!string.IsNullOrEmpty(said) && onSaid != null) onSaid(said);
                    continue;
                }

                if (d.mode == ToolMode.Chat)
                {
                    // 不用回传结果：发出去就不管了
                    StartCoroutine(reg.Invoke(name, args, autonomous, r => { }));
                    continue;
                }

                ToolResult res = null;
                yield return reg.Invoke(name, args, autonomous, r => res = r);

                string payload = res == null ? "（无结果）"
                    : (res.ok ? res.resultJson : ("错误：" + res.error));
                if (string.IsNullOrEmpty(payload)) payload = res != null && res.ok ? "（完成）" : "（失败）";
                resultRaws.Add(BuildToolResultMsg(name, tc.id, payload));
                Debug.Log("[PetAPI] ↳ " + name + " → " + ToolExecutor.Truncate(payload, 200));
            }
        }

        /// <summary>把模型的这轮响应重新拼成 assistant(tool_calls) 的原始 JSON。</summary>
        string BuildAssistantToolMsg(OllamaMsg msg)
        {
            var o = new JObject();
            o["role"] = "assistant";
            o["content"] = string.IsNullOrEmpty(msg.content) ? (JToken)JValue.CreateNull() : new JValue(msg.content);

            var arr = new JArray();
            for (int i = 0; i < msg.tool_calls.Length; i++)
            {
                var tc = msg.tool_calls[i];
                if (tc == null || tc.function == null) continue;

                JToken args;
                string raw = tc.function.argumentsRaw;
                try { args = string.IsNullOrEmpty(raw) ? (JToken)new JObject() : JToken.Parse(raw); }
                catch { args = new JObject(); }

                var fn = new JObject();
                fn["name"] = tc.function.name;
                // OpenAI 的 arguments 必须是【字符串】；Ollama 要的是对象
                if (provider == PetApiProvider.OpenAI) fn["arguments"] = args.ToString(Formatting.None);
                else fn["arguments"] = args;

                var item = new JObject();
                if (provider == PetApiProvider.OpenAI)
                {
                    item["id"] = tc.id;
                    item["type"] = "function";
                }
                item["function"] = fn;
                arr.Add(item);
            }
            o["tool_calls"] = arr;
            return o.ToString(Formatting.None);
        }

        /// <summary>工具结果消息：OpenAI 用 tool_call_id，Ollama 用 tool_name。</summary>
        string BuildToolResultMsg(string name, string id, string content)
        {
            var o = new JObject();
            o["role"] = "tool";
            if (provider == PetApiProvider.OpenAI)
            {
                o["tool_call_id"] = id ?? "";
                o["content"] = content ?? "";
            }
            else
            {
                o["tool_name"] = name ?? "";
                o["content"] = content ?? "";
            }
            return o.ToString(Formatting.None);
        }

        /// <summary>主人的这句话是不是在要求小白走动。
        /// 用于本地兜底：模型没反应时，保证主人「你去四处走走」这类话一定有可见反馈。</summary>
        static readonly string[] MOVE_WORDS = {
            "走走", "走一", "走啊", "去走", "溜达", "遛弯", "散步", "逛逛", "逛一",
            "四处", "到处", "动一动", "动动", "活动活动", "跑跑", "跑一", "转转", "别站着", "别发呆了"
        };
        static bool WantsMove(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < MOVE_WORDS.Length; i++)
                if (s.Contains(MOVE_WORDS[i])) return true;
            return false;
        }

        /// <summary>主人的话是不是在让它睡觉（模型没反应时本地兜底用）。</summary>
        static readonly string[] SLEEP_WORDS = { "睡觉", "睡吧", "睡一", "去睡", "快去睡", "困了" };
        static bool WantsSleep(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < SLEEP_WORDS.Length; i++)
                if (s.Contains(SLEEP_WORDS[i])) return true;
            return false;
        }

        /// <summary>主人的话是不是在让它坐下。</summary>
        static readonly string[] SIT_WORDS = { "坐下", "坐着", "坐一", "坐下来", "坐那儿" };
        static bool WantsSit(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < SIT_WORDS.Length; i++)
                if (s.Contains(SIT_WORDS[i])) return true;
            return false;
        }

        /// <summary>把环境快照 + 全屏缩略图作为一条 user 消息插进去（PetContext 不存在时静默跳过）。</summary>
        void AddContext(List<OllamaMsg> msgs)
        {
            if (PetContext.Instance == null) return;
            var m = new OllamaMsg();
            m.role = "user";
            m.content = "[当前环境]\n" + PetContext.Instance.ToJson();
            if (sendVision)
            {
                string b64 = PetContext.Instance.GetVisionBase64();
                if (!string.IsNullOrEmpty(b64))
                {
                    m.images = new string[] { b64 };   // Ollama 的视觉入口
                    m.content += "\n（本条附带的图片 = 对方此刻整个屏幕的缩略图）";
                }
            }
            msgs.Add(m);
        }

        /// <summary>
        /// ★★ 所有请求共用的底座：系统提示 + 长期记忆 + 最近历史。
        /// 聊天 / 喂食 / 空闲主动搭话 三条路径【必须】都从这里出发 ——
        /// 之前空闲路径漏了 history，导致小白在聊天里学到的事（比如"不要叫我主人"）
        /// 一到它自己主动搭话就全忘了，看起来像精神分裂。
        /// </summary>
        List<OllamaMsg> BuildBase()
        {
            // ⚠️⚠️ Qwen3.5 的聊天模板【只允许一条 system 消息，而且必须排在第 0 条】。
            //    多发一条就 500：System message must be at the beginning.
            //    我原来把 [长期记忆] / [行为约定] 各发一条 system —— 长期记忆一直是空的所以没炸过，
            //    直到小白学会第一条行为约定，整个 API 就全 500、它彻底哑巴。
            //    ⇒ 现在一律【拼进同一条 system】。
            var sys = new StringBuilder();

            // ★★ 用户自定义的角色设定（pet_profile.json）永远【排在最前面】。
            //    放在这里而不是拼在末尾，是因为大模型对提示词开头更敏感；
            //    同时也方便它压过下面 Inspector 里写死的默认人设（比如"你是小白"）。
            //    GetPromptFragment() 内部自带降级：组件 → 文件 → 内置兜底，
            //    所以哪怕场景里没挂 PetProfileConfig 也不会崩。
            var profile = PetProfileConfig.GetPromptFragment();
            if (!string.IsNullOrEmpty(profile)) sys.Append(profile).Append("\n\n");

            sys.Append(systemPrompt);

            // 【多形态预留】只有打开了才提"变身"。
            // 关着的时候一个字都不提 ⇒ 模型既看不到工具、也看不到提示 ⇒ 现有行为零影响。
            if (enableSwitchForm)
            {
                var fb = FindBrain();
                sys.Append("\n【变身】你可以在不同形态之间切换");
                if (fb != null) sys.Append("（目前可用：").Append(fb.AvailableForms()).Append("）");
                sys.Append("。想换形态就调用 switch_form，把目标形态名放进 form_name。");
            }

            if (PetMemory.Instance != null)
            {
                var lt = PetMemory.Instance.GetLongTerm();
                if (lt.Count > 0)
                {
                    sys.Append("\n\n[长期记忆（你记得的事）]");
                    for (int i = 0; i < lt.Count; i++) sys.Append("\n- ").Append(lt[i]);
                }

                // ★ 行为约定（主人训练出来的）：说得越多次的排越前面，权重感更强
                var rules = PetMemory.Instance.GetRules();
                if (rules.Count > 0)
                {
                    rules.Sort((x, y) => y.hits.CompareTo(x.hits));
                    sys.Append("\n\n[").Append(PetProfileConfig.UserName).Append("对你的行为约定 · 必须照做]");
                    for (int i = 0; i < rules.Count; i++)
                    {
                        sys.Append("\n").Append(i + 1).Append(". ").Append(rules[i].rule);
                        if (rules[i].hits > 1) sys.Append("（对方强调过 ").Append(rules[i].hits).Append(" 次）");
                    }
                }
            }

            // ★ 2.0：把注册中心里【所有启用】的工具列成一段人话清单。
            //    完整 JSON Schema 已经在 tools 参数里了，这里只放名字 + 一句话说明，
            //    避免提示词翻倍。
            if (ToolRegistry.Instance != null)
            {
                string sec = ToolRegistry.Instance.BuildPromptSection();
                if (!string.IsNullOrEmpty(sec)) sys.Append(sec);
            }

            var msgs = new List<OllamaMsg>();
            msgs.Add(new OllamaMsg { role = "system", content = sys.ToString() });
            if (PetMemory.Instance == null) return msgs;

            var hist = PetMemory.Instance.GetHistory();
            int start = 0;
            if (historyCharBudget > 0)
            {
                int used = 0;
                for (int i = hist.Count - 1; i >= 0; i--)
                {
                    used += hist[i].Length;
                    if (used > historyCharBudget) { start = i + 1; break; }
                }
            }
            for (int i = start; i < hist.Count; i++) msgs.Add(SplitTurn(hist[i]));
            return msgs;
        }

        OllamaMsg SplitTurn(string turn)
        {
            int i = turn.IndexOf(": ");
            if (i < 0) return new OllamaMsg { role = "user", content = turn };
            string who = turn.Substring(0, i);
            string body = turn.Substring(i + 2);
            // ⚠️ 这里【不能】再写死 "主人"/"白虎大哥"/"小白"。
            //    名字一旦被用户改掉，历史记录里的角色就会认错：
            //    自己的话被当成对方说的 → 模型开始"自己跟自己聊天"。
            //    判据：等于当前昵称（或旧版遗留的两种叫法）→ user；等于当前桌宠名 → assistant；
            //    两个都不是（改名前留下的老记录）→ 默认按 user 处理。
            string user = PetProfileConfig.UserName;
            string pet = PetProfileConfig.PetName;
            // 当前名字优先；再兼容旧版遗留的叫法（改名前的历史记录否则会被认成对方说的）
            bool isUser = who == user || who == "主人" || who == "白虎大哥"
                          || who == "user" || who == "用户";
            bool isPet = who == pet || who == "小白" || who == "assistant" || who == "桌宠";
            bool asUser = isUser || !isPet;      // 都认不出 → 默认按对方处理
            return new OllamaMsg { role = (asUser ? "user" : "assistant"), content = body };
        }

        // ---------- 喂食事件（{"event":"user_fed_me"}） ----------
        public void FeedEvent(string fileName, Action<PetReply> cb) { StartCoroutine(FeedRoutine(fileName, cb)); }
        IEnumerator FeedRoutine(string fileName, Action<PetReply> cb)
        {
            var msgs = BuildBase();     // ★ 底座统一（原来这条路径也漏了历史）
            AddContext(msgs);
            msgs.Add(new OllamaMsg { role = "user", content = "{\"event\":\"user_fed_me\",\"file\":\"" + fileName + "\"}" });
            yield return AskRoutine(msgs, false, reply => { if (cb != null) cb(reply); });
        }

        // ---------- 空闲上报（AI 决策主导） ----------
        public void ReportIdle(float idleSeconds, Action<PetReply> cb)
        {
            StartCoroutine(ReportRoutine(idleSeconds, cb));
        }
        void ReportIdle(float idleSeconds)
        {
            ReportIdle(idleSeconds, reply =>
            {
                var brain = FindBrain();
                if (brain == null) return;

                // ① 这一轮它真的动了（move_to / 非 Idle·Talk 的动作）→ 清零计数
                if (reply != null && reply.acted)
                {
                    passiveStreak = 0;
                    if (!string.IsNullOrEmpty(reply.action)) brain.PlayAction(reply.action);
                    Debug.Log("[PetAPI] 本轮它动了");
                    return;
                }

                // ② 只说话 / 发呆 / 啥也没干 → 计数，连续 passiveLimit 次就本地兜底强行让它溜达
                passiveStreak++;
                if (passiveStreak >= Mathf.Max(1, passiveLimit))
                {
                    passiveStreak = 0;
                    Debug.Log("[PetAPI] ★ 连续 " + passiveLimit + " 轮没动 → 本地兜底：强行让它去溜达一圈");
                    brain.WanderRandom();   // 随机挑个点走过去（不依赖鼠标）
                    return;
                }

                // ③ 其余情况：工具已经处理过了就不插手
                if (reply == null || string.IsNullOrEmpty(reply.action))
                { Debug.Log("[PetAPI] 本轮由工具完成，不额外干预（沉默计数 " + passiveStreak + "/" + passiveLimit + "）"); return; }
                brain.PlayAction(reply.action);
                Debug.Log("[PetAPI] AI 决策 → " + reply.action);
            });
        }
        IEnumerator ReportRoutine(float idleSeconds, Action<PetReply> cb)
        {
            // ★★ 这里原来【只带了长期记忆、完全没带 history】—— 所以它在聊天里学到的事
            //     （比如"不要叫我主人"）一到主动搭话就全忘了。现在统一走 BuildBase。
            var msgs = BuildBase();
            AddContext(msgs);
            // 关键：必须有一条 user 消息，否则服务端报 “No user query found in messages”
            msgs.Add(new OllamaMsg { role = "user",
                content = "（这是你的自主思考时刻，此刻没有人正在跟你说话）你已经 "
                        + Mathf.RoundToInt(idleSeconds) + " 秒没被对方搭理了。"
                        + "请看看屏幕上的画面和环境数据，完全自主地决定要做什么，调用合适的工具去做。" });

            yield return AskRoutine(msgs, true, reply =>
            {
                // ★ 主动搭话也要写进历史 —— 大窗口的聊天记录才能看到"它自己凑过来"的那几句
                if (reply != null && !string.IsNullOrEmpty(reply.text) && PetMemory.Instance != null)
                    PetMemory.Instance.AddProactiveTurn(reply.text);

                if (cb != null) cb(reply);   // ★ 自主回合：允许把 (0,0) 这种偷懒坐标改成随机漫步
            });
        }

        // ---------- 记忆总结 ----------
        public void Summarize(List<string> turns, Action<string> cb)
        {
            StartCoroutine(SummarizeRoutine(turns, cb));
        }
        IEnumerator SummarizeRoutine(List<string> turns, Action<string> cb)
        {
            var msgs = new List<OllamaMsg>
            {
                new OllamaMsg { role = "system", content = "把下面这段对话浓缩成一句长期记忆，不超过50字，只输出这句话本身。" },
                new OllamaMsg { role = "user", content = string.Join("\n", turns.ToArray()) }
            };
            yield return Post(msgs, msg =>
            {
                string content = msg != null ? Sanitize(msg.content) : "";
                content = (content ?? "").Trim();
                if (content.Length > 50) content = content.Substring(0, 50);
                if (cb != null) cb(content);
            });
        }

        // ================== 工具集（发给 Ollama 的 tools 数组，OpenAI Tools 规范）==================
        const string TOOLS_JSON =
            "["
          + "{\"type\":\"function\",\"function\":{\"name\":\"wander_around\","
          + "\"description\":\"★【想随便走走就用这个】你自己挑个地方溜达一圈，不需要你给坐标。无聊了、想活动活动、对方让你四处走走，都调它。比 move_to 更省事，而且不会老是走去同一个地方。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{},\"required\":[]}}},"
          + "{\"type\":\"function\",\"function\":{\"name\":\"move_to\","
          + "\"description\":\"让你走到屏幕上的【指定】位置。只在你有明确去处时用（凑近对方、去屏幕右边等）。⚠️ 千万不要给 (0,0) —— 那是屏幕正中央，老是给 (0,0) 它就只会傻走到中间；想随便走走请用 wander_around。移动本身会自动播走路动画，不用再调 play_animation。世界坐标：x 约 -5~5（左负右正），y 约 -3~3（下负上正）。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"target_x\":{\"type\":\"number\"},\"target_y\":{\"type\":\"number\"}},"
          + "\"required\":[\"target_x\",\"target_y\"]}}},"
          + "{\"type\":\"function\",\"function\":{\"name\":\"play_animation\","
          + "\"description\":\"让你播放某个动作。发呆配 Sit/Idle，想引起注意配 Jump/Pet/LookBack。⚠️ 走动不要调 Walk/Run（走路会自动播）。⚠️ Sit/Sleep 是【保持姿势】（会一直坐着/睡着直到被打断），所以调它们时【不要再同时调 wander_around 或 move_to】，否则会被移动打断。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"action_name\":{\"type\":\"string\",\"enum\":[\"Idle\",\"Walk\",\"Run\",\"Drag\",\"Drop\",\"Jump\",\"LookBack\",\"Pet\",\"Sit\",\"Sleep\",\"Talk\"]}},"
          + "\"required\":[\"action_name\"]}}},"
          + "{\"type\":\"function\",\"function\":{\"name\":\"say_and_speak\","
          + "\"description\":\"说一句话给对方听，会触发 TTS 朗读。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"text\":{\"type\":\"string\",\"description\":\"要说的话，20 字以内\"},"
          + "\"emotion\":{\"type\":\"string\",\"description\":\"情绪：开心/撒娇/生气/困/好奇 等\"}},"
          + "\"required\":[\"text\"]}}},"
          + "{\"type\":\"function\",\"function\":{\"name\":\"remember_rule\","
          + "\"description\":\"★【对方训练你的时候调它】当对方对你提出【长期行为要求】时，把它记成一条行为约定，从此每轮都会提醒你照做。例如对方说「你多四处走走」「别老发呆」「安静点」「别乱叫」→ 调它记下来。⚠️ 只是聊闲天、问问题不要调。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"rule\":{\"type\":\"string\",\"description\":\"用第一人称写一句精简的约定，15 字以内。例如：我要多四处走动\"}},"
          + "\"required\":[\"rule\"]}}},"
          + "{\"type\":\"function\",\"function\":{\"name\":\"feed_file\","
          + "\"description\":\"吃掉一个文件或文件夹（移入回收站，可恢复）。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"file_path\":{\"type\":\"string\",\"description\":\"要吃的文件完整路径\"}},"
          + "\"required\":[\"file_path\"]}}}"
          + "]";

        /// <summary>
        /// 【多形态预留】switch_form 的工具定义。
        /// ⚠️ 只有当 enableSwitchForm = true 时才会被拼进 tools 数组下发；
        ///    默认 false ⇒ 模型根本看不到它 ⇒ 不可能乱调 ⇒ 现有行为零影响。
        /// form_name 故意【不设为 required】—— 目前只有一种形态，不带参数也能调。
        /// </summary>
        const string SWITCH_FORM_TOOL =
            "{\"type\":\"function\",\"function\":{\"name\":\"switch_form\","
          + "\"description\":\"★变身：在当前宠物的不同形态之间切换（例如兽人形态 / 虎形态）。目前只有一个形态，等更多形态做好后才能真的变。\","
          + "\"parameters\":{\"type\":\"object\",\"properties\":{"
          + "\"form_name\":{\"type\":\"string\",\"description\":\"目标形态名，例如 tiger、humanoid\"}},"
          + "\"required\":[]}}}";

        /// <summary>
        /// 实际下发给模型的 tools 数组。
        /// 2.0：优先问注册中心（内置 + 外部工具合并）；注册中心不在时才退回老常量。
        /// </summary>
        string ToolsJson()
        {
            if (ToolRegistry.Instance != null)
            {
                string j = ToolRegistry.Instance.BuildOpenAiToolsJson();
                if (!string.IsNullOrEmpty(j) && j != "[]") return j;
            }
            return BuiltinToolsJson();
        }

        /// <summary>框架内置的工具数组（switch_form 受 enableSwitchForm 控制）。</summary>
        public string BuiltinToolsJson() { return LegacyToolsJsonStatic(enableSwitchForm); }

        /// <summary>给 BuiltinToolSource 用的静态入口。</summary>
        public static string LegacyToolsJsonStatic(bool withSwitchForm)
        {
            if (!withSwitchForm) return TOOLS_JSON;
            return TOOLS_JSON.Substring(0, TOOLS_JSON.Length - 1) + "," + SWITCH_FORM_TOOL + "]";
        }

        /// <summary>
        /// 手工拼请求体：tools 是固定 JSON 直接内联；messages 交给 JsonUtility 序列化保证转义正确，
        /// 再把它从 {"items":[...]} 里剥出来拼进去。
        /// </summary>
        string BuildBody(List<OllamaMsg> msgs) { return BuildBody(msgs, null); }

        /// <summary>
        /// rawExtra：已经拼好的原始 JSON 消息（工具回环用）。
        /// 为什么不塞进 OllamaMsg？因为 assistant(tool_calls) 和 tool(result) 的形状
        /// 两种协议完全不同，而且外部工具的参数是任意结构 —— 用固定类序列化会丢字段。
        /// </summary>
        string BuildBody(List<OllamaMsg> msgs, List<string> rawExtra)
        {
            // 🛡 保险丝：Qwen 模板只认一条 system（第 0 条），多一条直接 500 把小白打成哑巴。
            //    以后谁再往后面塞 system 消息，这里会把它降级成 user，最差也只是语气怪，不会整个不能用。
            for (int i = 1; i < msgs.Count; i++)
            {
                if (msgs[i] != null && msgs[i].role == "system")
                {
                    Debug.LogWarning("[PetAPI] ⚠️ 第 " + i + " 条是多余的系统消息，已降级为 user（Qwen 只允许一条 system）");
                    msgs[i].role = "user";
                }
            }

            if (provider == PetApiProvider.OpenAI) return BuildBodyOpenAI(msgs, rawExtra);
            return BuildBodyOllama(msgs, rawExtra);
        }

        /// <summary>本地 Ollama 原生协议：能用 think:false 和 num_ctx（CPU 推理时这两项很关键）。</summary>
        string BuildBodyOllama(List<OllamaMsg> msgs, List<string> rawExtra)
        {
            var w = new MsgWrap(); w.items = msgs.ToArray();
            string mj = JsonUtility.ToJson(w);
            int a = mj.IndexOf('['), b = mj.LastIndexOf(']');
            if (a >= 0 && b > a) mj = mj.Substring(a, b - a + 1);

            // 工具回环的原始消息直接拼在 messages 数组尾部（JsonUtility 表达不了这些形状）
            if (rawExtra != null && rawExtra.Count > 0)
            {
                var ex = new StringBuilder();
                for (int i = 0; i < rawExtra.Count; i++)
                {
                    if (string.IsNullOrEmpty(rawExtra[i])) continue;
                    if (ex.Length > 0) ex.Append(",");
                    ex.Append(rawExtra[i]);
                }
                if (ex.Length > 0)
                {
                    if (mj.Length > 2) mj = mj.Substring(0, mj.Length - 1) + "," + ex + "]";
                    else mj = "[" + ex + "]";
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(model)).Append("\",");
            sb.Append("\"stream\":false,");
            sb.Append("\"think\":").Append(disableThinking ? "false" : "true").Append(",");
            sb.Append("\"options\":{\"num_ctx\":").Append(Mathf.Max(2048, numCtx)).Append("},");
            sb.Append("\"tools\":").Append(ToolsJson()).Append(",");
            sb.Append("\"messages\":").Append(mj);
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 云端 OpenAI 兼容协议。手写拼接是因为有两处 Ollama 的形状放不进去：
        ///   ① 带图的消息 content 必须是 [{"type":"text"},{"type":"image_url"}] 数组
        ///   ② 不能发 think / options.num_ctx（云端不认，会 400）
        /// </summary>
        string BuildBodyOpenAI(List<OllamaMsg> msgs, List<string> rawExtra)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(model)).Append("\",");
            sb.Append("\"stream\":false,");
            // ★ 思考开关：云端的 Qwen3 系列认 enable_thinking（硅基流动等）。
            //   别的厂商不一定认这个字段，认错会直接 400，所以只在模型名里带 qwen 时才发。
            if (!string.IsNullOrEmpty(model) && model.ToLowerInvariant().Contains("qwen"))
                sb.Append("\"enable_thinking\":").Append(disableThinking ? "false" : "true").Append(",");
            sb.Append("\"tools\":").Append(ToolsJson()).Append(",");
            sb.Append("\"messages\":[");

            for (int i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                if (m == null) continue;
                if (sb[sb.Length - 1] != '[') sb.Append(",");

                bool hasImg = m.images != null && m.images.Length > 0;
                sb.Append("{\"role\":\"").Append(Esc(m.role)).Append("\",\"content\":");
                if (hasImg)
                {
                    sb.Append("[{\"type\":\"text\",\"text\":\"").Append(Esc(m.content)).Append("\"}");
                    for (int k = 0; k < m.images.Length; k++)
                        sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,")
                          .Append(m.images[k]).Append("\"}}");
                    sb.Append("]");
                }
                else
                {
                    sb.Append("\"").Append(Esc(m.content)).Append("\"");
                }
                sb.Append("}");
            }
            if (rawExtra != null)
            {
                for (int i = 0; i < rawExtra.Count; i++)
                {
                    if (string.IsNullOrEmpty(rawExtra[i])) continue;
                    if (sb[sb.Length - 1] != '[') sb.Append(",");
                    sb.Append(rawExtra[i]);
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>JSON 字符串转义（手写 OpenAI 请求体时用）。</summary>
        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 根据协议把 BaseUrl 补成真正的请求地址。用户只填到域名/版本号也不会错。
        /// </summary>
        public string Endpoint()
        {
            string u = (baseUrl ?? "").Trim().TrimEnd('/');
            if (u.Length == 0) u = "http://localhost:11434";

            if (provider == PetApiProvider.Ollama)
            {
                if (u.EndsWith("/api/chat")) return u;
                return u + "/api/chat";
            }

            // OpenAI 兼容
            if (u.EndsWith("/chat/completions")) return u;
            if (u.EndsWith("/v1")) return u + "/chat/completions";
            try
            {
                var uri = new System.Uri(u);
                // 只给了裸域名（比如 https://api.siliconflow.cn）→ 补上 /v1
                if (string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))) return u + "/v1/chat/completions";
            }
            catch { }
            return u + "/chat/completions";
        }

        // ---------- HTTP（Ollama /api/chat，非流式，全程协程） ----------
        IEnumerator Post(List<OllamaMsg> msgs, Action<OllamaMsg> onMsg)
        {
            yield return Post(msgs, null, onMsg);
        }

        IEnumerator Post(List<OllamaMsg> msgs, List<string> rawExtra, Action<OllamaMsg> onMsg)
        {
            // ★★ 串行化：这台机器是【纯 CPU 推理】，一个模型实例同时只能跑一个请求。
            //    聊天请求和空闲汇报一旦撞车，两边各退一半速度 → 双双 60 秒超时。
            //    实测就是这么炸过一次。这里排队等前一个收工再发。
            if (isRequesting)
            {
                Debug.Log("[PetAPI] 已有请求在跑，排队等待…");
                float waited = 0f;
                while (isRequesting && waited < 90f) { waited += Time.deltaTime; yield return null; }
                Debug.Log("[PetAPI] 排到队了（等了 " + Mathf.RoundToInt(waited) + "s）");
            }
            isRequesting = true;   // 占用请求锁，防并发
            try
            {
                bool hasImg = false;
                for (int i = 0; i < msgs.Count; i++)
                    if (msgs[i].images != null && msgs[i].images.Length > 0) hasImg = true;
                string url = Endpoint();
                int toolCount = ToolRegistry.Instance != null && ToolRegistry.Instance.All != null
                    ? ToolRegistry.Instance.All.Count : 0;
                Debug.Log("[PetAPI] 请求发出：消息 " + (msgs.Count + (rawExtra != null ? rawExtra.Count : 0))
                    + " 条，带缩略图=" + hasImg
                    + "，工具 " + toolCount + " 个"
                    + "，超时 " + timeoutSec + "s"
                    + "　→ " + provider + " " + url + (string.IsNullOrEmpty(apiKey) ? "（无 Key）" : "（带 Key）"));

                string body = BuildBody(msgs, rawExtra);
                using (var www = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    www.downloadHandler = new DownloadHandlerBuffer();
                    www.SetRequestHeader("Content-Type", "application/json");
                    // 云端 OpenAI 兼容接口要 Bearer 鉴权；本地 Ollama 不需要（留空就不发这个头）
                    if (!string.IsNullOrEmpty(apiKey))
                        www.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
                    www.timeout = timeoutSec;
                    yield return www.SendWebRequest();

                    if (www.result != UnityWebRequest.Result.Success)
                    {
                        string errBody = (www.downloadHandler != null ? www.downloadHandler.text : "");
                        if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                        Debug.LogWarning("[PetAPI] 请求失败：" + www.responseCode + " " + www.error + " | body=" + errBody
                            + "   （Ollama 起来了吗？端点=" + baseUrl + " 超时=" + timeoutSec + "s）");
                        if (onMsg != null) onMsg(null);
                        yield break;
                    }

                    string raw = www.downloadHandler.text;
                    OllamaMsg msg = null;
                    try { msg = ParseResponse(raw); }
                    catch (Exception e) { Debug.LogWarning("[PetAPI] 解析响应失败：" + e.Message); }

                    if (msg == null)
                    {
                        string r = raw.Length > 300 ? raw.Substring(0, 300) : raw;
                        Debug.LogWarning("[PetAPI] 响应无法解析，原始=" + r);
                    }
                    if (onMsg != null) onMsg(msg);
                }
            }
            finally { isRequesting = false; }   // 无论成败都释放锁（含超时）
        }

        /// <summary>
        /// 把响应统一解析成内部的 OllamaMsg。两种协议的差别全在这里吃掉：
        /// Ollama:  {"message":{"content":"..","tool_calls":[{"function":{"arguments":{...}}}]}}
        /// OpenAI:  {"choices":[{"message":{"content":"..","tool_calls":[{"function":{"arguments":"{...}"}}]}}]}
        ///         ⚠️ 注意 OpenAI 的 arguments 是【JSON 字符串】，得再解析一层。
        /// </summary>
        OllamaMsg ParseResponse(string raw)
        {
            if (provider != PetApiProvider.OpenAI)
            {
                var resp = JsonUtility.FromJson<OllamaResp>(raw);
                var m0 = resp != null ? resp.message : null;
                // ⚠️ JsonUtility 只会把 arguments 塞进固定的 ToolArgs，外部工具的任意字段全丢。
                //    这里用 Newtonsoft 再把【原始参数文本】捞回来。
                if (m0 != null && m0.tool_calls != null) AttachRawArgs(raw, m0, false);
                return m0;
            }

            var o = JsonUtility.FromJson<OaiResp>(raw);
            if (o == null) return null;
            OaiMessage om = null;
            if (o.choices != null && o.choices.Length > 0 && o.choices[0] != null) om = o.choices[0].message;
            if (om == null) om = o.message;      // 少数兼容服务不包 choices
            if (om == null) return null;

            var msg = new OllamaMsg { role = "assistant", content = om.content };
            if (om.tool_calls != null && om.tool_calls.Length > 0)
            {
                var list = new List<ToolCall>();
                for (int i = 0; i < om.tool_calls.Length; i++)
                {
                    var tc = om.tool_calls[i];
                    if (tc == null || tc.function == null) continue;
                    ToolArgs args = null;
                    if (!string.IsNullOrEmpty(tc.function.arguments))
                        try { args = JsonUtility.FromJson<ToolArgs>(tc.function.arguments); } catch { }
                    list.Add(new ToolCall
                    {
                        id = tc.id,
                        function = new ToolCallFunction
                        {
                            index = i,
                            name = tc.function.name,
                            arguments = args,
                            argumentsRaw = tc.function.arguments   // OpenAI 本来就是字符串，直接用
                        }
                    });
                }
                if (list.Count > 0) msg.tool_calls = list.ToArray();
            }
            return msg;
        }

        // ---------- 工具调用解析与执行 ----------
        /// <summary>
        /// 从原始响应里把每个 tool_call 的【原始参数文本】补回 argumentsRaw。
        /// Ollama 的 arguments 是对象，JsonUtility 反序列化到固定 ToolArgs 会丢掉未知字段；
        /// 外部工具（写代码/建模）的参数是任意结构，必须原样保留。
        /// </summary>
        static void AttachRawArgs(string raw, OllamaMsg msg, bool openAi)
        {
            if (string.IsNullOrEmpty(raw) || msg == null || msg.tool_calls == null) return;
            try
            {
                var root = JObject.Parse(raw);
                JToken tcTok = null;
                if (openAi)
                {
                    var choices = root["choices"] as JArray;
                    if (choices != null && choices.Count > 0)
                        tcTok = choices[0]["message"] != null ? choices[0]["message"]["tool_calls"] : null;
                    if (tcTok == null && root["message"] != null) tcTok = root["message"]["tool_calls"];
                }
                else
                {
                    tcTok = root["message"] != null ? root["message"]["tool_calls"] : null;
                }
                var arr = tcTok as JArray;
                if (arr == null) return;

                int n = Mathf.Min(arr.Count, msg.tool_calls.Length);
                for (int i = 0; i < n; i++)
                {
                    var fn = arr[i] != null ? arr[i]["function"] : null;
                    if (fn == null) continue;
                    var argsTok = fn["arguments"];
                    if (argsTok == null) continue;
                    if (msg.tool_calls[i] == null || msg.tool_calls[i].function == null) continue;
                    msg.tool_calls[i].function.argumentsRaw =
                        argsTok.Type == JTokenType.String ? (string)argsTok : argsTok.ToString(Formatting.None);
                }
            }
            catch (Exception e) { Debug.LogWarning("[PetAPI] 提取工具原始参数失败：" + e.Message); }
        }

        /// <summary>统一后处理：有 tool_calls 就执行工具；没有就按普通聊天处理。
        /// autonomous=true 表示这是小白自己想干啥的回合（空闲触发），
        /// 这种情况下它会偷懒地给 move_to(0,0)，我们要把它换成随机漫步。</summary>
        PetReply Handle(OllamaMsg msg, bool autonomous)
        {
            var reply = new PetReply { action = "", text = "" };   // action 空 = 上层据此判断"已由工具完成"
            if (msg == null) { reply.action = "Idle"; return reply; }

            if (msg.tool_calls != null && msg.tool_calls.Length > 0)
            {
                for (int i = 0; i < msg.tool_calls.Length; i++)
                {
                    var tc = msg.tool_calls[i];
                    string said = ExecuteTool(tc, autonomous);
                    if (!string.IsNullOrEmpty(said)) reply.text = said;   // 把 say_and_speak 的话带回去给聊天框显示
                    if (IsMovementTool(tc, autonomous)) reply.acted = true;   // 它到底动没动
                }
                reply.viaTool = true;   // action 留空 = 已由工具完成

                // ★★ 模型经常【同时】给文字和工具调用（比如「好的，白虎大哥」+ play_animation(Sleep)）。
                //    以前这里直接 return，那段文字被整个丢掉 —— 主人只看到动作、听不到回应。
                //    「回复」和「调函数」本来就不冲突，现在只要没调 say_and_speak 就把 content 一并念出来。
                //    ⚠️ 只念、不改动画：工具已经把姿势定好了，再来一句 PlayAction("Talk")
                //       会把刚设好的 Sit / Sleep 顶掉。
                if (string.IsNullOrEmpty(reply.text))
                {
                    string extra = CleanPlain(Sanitize(msg.content));
                    if (extra.Length > 0)
                    {
                        reply.text = extra;
                        TTS.Speak(extra, false);   // ★ 只出声、不改动画：姿势由工具决定
                        Debug.Log("[PetAPI] 模型同时给了文字，一并念出来：「" + extra + "」");
                    }
                }
                return reply;
            }

            string content = Sanitize(msg.content);
            if (string.IsNullOrEmpty(content)) { reply.action = "Idle"; return reply; }
            return ParseReply(content);   // 只回文字 → 当普通聊天
        }

        /// <summary>
        /// 给 Tools.BuiltinToolSource 用的入口：把原始 JSON 参数解析成 ToolArgs，再走下面那个 switch。
        /// 这样内置工具也能被注册中心统一管起来，而**执行逻辑一行都不用动**。
        /// </summary>
        public string ExecuteBuiltinTool(string name, string argsJson, bool autonomous)
        {
            ToolArgs a = null;
            try
            {
                if (!string.IsNullOrEmpty(argsJson) && argsJson.Trim() != "{}")
                    a = JsonConvert.DeserializeObject<ToolArgs>(argsJson);
            }
            catch (Exception e) { Debug.LogWarning("[PetAPI] 内置工具参数解析失败：" + e.Message); }
            if (a == null) a = new ToolArgs();

            var call = new ToolCall
            {
                function = new ToolCallFunction { name = name, arguments = a, argumentsRaw = argsJson }
            };
            return ExecuteTool(call, autonomous);
        }

        /// <summary>执行一个工具调用（都在主线程的协程回调里，安全）。返回 say_and_speak 说的话，其余返回 null。</summary>
        string ExecuteTool(ToolCall call, bool autonomous)
        {
            if (call == null || call.function == null) return null;
            string name = call.function.name;
            var a = call.function.arguments;
            if (a == null) { Debug.LogWarning("[PetAPI] 工具 " + name + " 没有参数"); return null; }

            Debug.Log("[PetAPI] 🔧 工具 → " + name + "  {action=" + a.action_name + " x=" + a.target_x
                + " y=" + a.target_y + " text=" + a.text + " emotion=" + a.emotion
                + " file=" + a.file_path + " rule=" + a.rule + "}");

            var brain = FindBrain();
            switch (name)
            {
                case "wander_around":
                    // 无参数：小白自己挑地方。这是"主动走动"最可靠的入口。
                    if (brain != null) brain.WanderRandom();
                    return null;

                case "play_animation":
                    // 走路动画由移动本身自动播，这里收到 Walk/Run 直接忽略（省得它站着原地踏步）
                    if (a.action_name != null &&
                        (a.action_name.Equals("Walk", StringComparison.OrdinalIgnoreCase) ||
                         a.action_name.Equals("Run", StringComparison.OrdinalIgnoreCase)))
                    {
                        Debug.Log("[PetAPI] 忽略冗余的 play_animation(" + a.action_name + ")：移动会自动播");
                        return null;
                    }
                    if (brain != null && !string.IsNullOrEmpty(a.action_name)) brain.PlayAction(a.action_name);
                    return null;

                case "move_to":
                    if (brain == null) return null;
                    // ★★ (0,0) 就是屏幕正中央。4B 模型想不出地方时就爱扔 (0,0)，
                    //    于是小白每次都傻走到中间。自主回合里直接改成随机漫步。
                    if (autonomous && Mathf.Abs(a.target_x) < 0.35f && Mathf.Abs(a.target_y) < 0.35f)
                    {
                        Debug.Log("[PetAPI] ★ 模型给了偷懒坐标 (" + a.target_x + ", " + a.target_y
                                  + ") → 改成随机漫步，别老是走去屏幕正中");
                        brain.WanderRandom();
                        return null;
                    }
                    brain.MoveTo(a.target_x, a.target_y);
                    return null;

                case "say_and_speak":
                    if (string.IsNullOrEmpty(a.text)) return null;
                    // 说话该切 Talk，但它正在坐/睡就别切（否则刚设好的姿势被顶掉）
                    if (brain != null) brain.PlayTalkIfAllowed();
                    TTS.Speak(a.text, false);   // 动画已经在上一行决定过了，别再让 TTS 改一次
                    Debug.Log("[PetAPI] " + PetProfileConfig.PetName + "说：「" + a.text + "」 情绪=" + a.emotion);
                    return a.text;

                case "remember_rule":
                    if (string.IsNullOrEmpty(a.rule)) return null;
                    if (PetMemory.Instance != null)
                    {
                        var r = PetMemory.Instance.RememberRule(a.rule);
                        Debug.Log("[PetAPI] ★ 记住对方的要求：「" + (r != null ? r.rule : a.rule) + "」");
                    }
                    return null;

                case "feed_file":
                    var feed = FindFeed();
                    if (feed != null && !string.IsNullOrEmpty(a.file_path)) feed.Feed(a.file_path);
                    return null;

                case "switch_form":
                    // 【多形态预留】目前只有骨架：改形态指针 + 打日志，外观和动画都不变。
                    // 关着的时候模型根本看不到这个工具，走不到这里；这里再兜一层是防手滑。
                    if (!enableSwitchForm)
                    {
                        Debug.LogWarning("[PetAPI] switch_form 被调用，但 enableSwitchForm=false（不该发生）");
                        return null;
                    }
                    if (string.IsNullOrEmpty(a.form_name))
                    {
                        Debug.Log("[PetAPI] switch_form 没带形态名，忽略（目前本来也只有一个形态）");
                        return null;
                    }
                    if (brain != null) brain.SwitchForm(a.form_name);
                    return null;

                default:
                    Debug.LogWarning("[PetAPI] 未知工具：" + name);
                    return null;
            }
        }

        /// <summary>
        /// 这次工具调用算不算"小白真的动了" —— 决定本地兜底漫游要不要触发。
        /// ⚠️ play_animation(Walk/Run) 【不算】动：那只是原地播走路动画，模型很爱拿它凑数，
        ///    之前把它算成"动了"，导致被动计数永远清零、兜底漫游永不触发，小白就成了木桩。
        /// </summary>
        static bool IsMovementTool(ToolCall tc, bool autonomous)
        {
            if (tc == null || tc.function == null) return false;
            string n = tc.function.name;
            if (n == "wander_around") return true;
            if (n == "move_to")
            {
                var a = tc.function.arguments;
                if (a == null) return false;
                // 自主回合里，偷懒坐标会被改成随机漫步 → 那就是真的动了
                if (autonomous && Mathf.Abs(a.target_x) < 0.35f && Mathf.Abs(a.target_y) < 0.35f) return true;
                // 主人明确指的地方，哪怕就是正中，也算动了
                return Mathf.Abs(a.target_x) > 0.05f || Mathf.Abs(a.target_y) > 0.05f;
            }
            if (n == "play_animation")
            {
                string a = tc.function.arguments != null ? tc.function.arguments.action_name : null;
                if (string.IsNullOrEmpty(a)) return false;
                if (a.Equals("Idle", StringComparison.OrdinalIgnoreCase)) return false;
                if (a.Equals("Talk", StringComparison.OrdinalIgnoreCase)) return false;
                if (a.Equals("Walk", StringComparison.OrdinalIgnoreCase)) return false;   // ← 关键
                if (a.Equals("Run", StringComparison.OrdinalIgnoreCase)) return false;    // ← 关键
                return true;
            }
            return false;
        }

        PetFeed FindFeed()
        {
    #if UNITY_2022_2_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<PetFeed>();
    #else
            return UnityEngine.Object.FindObjectOfType<PetFeed>();
    #endif
        }

        /// <summary>去掉模型偶发吐出的孤立代理字符（半截 UTF-8 会污染 TTS 和文本）。</summary>
        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        PetReply ParseReply(string content)
        {
            var reply = new PetReply { action = "talk", text = content };
            if (string.IsNullOrEmpty(content)) { reply.action = "Idle"; reply.text = ""; return reply; }

            string c = content.Replace("```json", "").Replace("```", "").Trim();
            int a = c.IndexOf('{'), b = c.LastIndexOf('}');
            if (a >= 0 && b > a) c = c.Substring(a, b - a + 1);

            try
            {
                var p = JsonUtility.FromJson<PetReply>(c);
                if (p != null && !string.IsNullOrEmpty(p.text))
                {
                    reply.action = string.IsNullOrEmpty(p.action) ? "talk" : p.action;
                    reply.text = p.text;
                    return reply;
                }
            }
            catch { }

            // 兜底：手工从原文里抠出 text / action 的值
            string t = ExtractQuoted(content, "text");
            string act = ExtractQuoted(content, "action");
            if (string.IsNullOrEmpty(t) && string.IsNullOrEmpty(act))
            {
                // ★★ 模型既没吐 JSON 也没吐引号 —— 它就是在用大白话跟你说话。
                //    【这里以前直接把整段丢掉】→ ChatUI 显示「没收到回复」，
                //    用户以为是超时，其实是它的回答被我们误杀了。
                //    现在：只要像句人话就收下（掐到第一句、最多 60 字）。
                string plain = CleanPlain(content);
                if (plain.Length == 0)
                {
                    reply.action = "Idle"; reply.text = "";
                    Debug.LogWarning("[PetAPI] 模型这段内容没法当回话，已丢弃：" + Trunc(content, 120));
                    return reply;
                }
                reply.action = "Talk";
                reply.text = plain;
                Debug.Log("[PetAPI] 模型用大白话回复，已收下（不会再显示「没收到」）：「" + plain + "」");
                return reply;
            }
            if (!string.IsNullOrEmpty(t)) reply.text = t;
            if (!string.IsNullOrEmpty(act)) reply.action = act;
            if (string.IsNullOrEmpty(reply.text)) reply.action = "Idle";
            if (reply.text != null && reply.text.Length > 40) reply.text = reply.text.Substring(0, 40);
            return reply;
        }

        /// <summary>把模型的大白话清理成一句能念的话：去代码栅栏、掐掉第一句、限长 60 字。</summary>
        static string CleanPlain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("```json", "").Replace("```", "").Replace("{", "").Replace("}", "").Trim();
            // 只保留第一句（小模型经常后面跟着一堆自言自语）
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '\n')
                { s = s.Substring(0, i + 1); break; }
            }
            s = s.Trim();
            if (s.Length > 60) s = s.Substring(0, 60);
            return s;
        }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > n ? s.Substring(0, n) : s;
        }

        string ExtractQuoted(string s, string key)
        {
            string k = "\"" + key + "\"";
            int ki = s.IndexOf(k, System.StringComparison.OrdinalIgnoreCase);
            if (ki < 0) return null;
            int colon = s.IndexOf(':', ki + k.Length);
            if (colon < 0) return null;
            int q1 = s.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = s.IndexOf('"', q1 + 1);
            if (q2 <= q1) return null;
            return s.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
