using System;
using UnityEngine;

namespace PetFramework.Tools
{
    /// <summary>
    /// 【工具执行确认弹窗】—— 模型想跑本地进程 / 写文件之前，必须先过这一关。
    ///
    /// 设计取舍：
    ///   · 用 OnGUI 而不是拼 uGUI —— 弹窗极少出现，没必要为它建 Canvas；
    ///   · 阻塞式：`Ask()` 回调之前，工具调用一直挂着（由 ToolExecutor 协程等待）；
    ///   · 120 秒没人理 ⇒ 按【拒绝】处理（ToolExecutor 负责超时）；
    ///   · 弹窗期间强制窗口不穿透，否则按钮点不到。
    /// </summary>
    public class ToolConfirmUI : MonoBehaviour
    {
        public static bool IsAsking { get; private set; }

        static ToolConfirmUI instance;
        static string sToolName = "";
        static string sToolTitle = "";
        static string sArgs = "";
        static Action<bool> sPending;
        static float sOpenedAt;

        /// <summary>弹窗是否开着（TransparentWindow 会读它来关闭鼠标穿透）。</summary>
        public static bool Asking { get { return IsAsking; } }

        static void Ensure()
        {
            if (instance != null) return;
            var go = new GameObject("ToolConfirmUI");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ToolConfirmUI>();
        }

        /// <summary>问一句。cb 只会被调用一次。</summary>
        public static void Ask(ToolDescriptor tool, string argsJson, Action<bool> cb)
        {
            Ensure();
            IsAsking = true;
            sToolName = tool != null ? tool.name : "?";
            sToolTitle = tool != null && !string.IsNullOrEmpty(tool.title) ? tool.title : sToolName;
            sArgs = string.IsNullOrEmpty(argsJson) ? "(无参数)" : argsJson;
            if (sArgs.Length > 400) sArgs = sArgs.Substring(0, 400) + "…";
            sPending = cb;
            sOpenedAt = Time.unscaledTime;
            Debug.Log("[ToolConfirm] 等待确认：" + sToolName + " " + sArgs);
        }

        /// <summary>超时/被外力取消 → 按拒绝回一次。</summary>
        public static void Cancel()
        {
            Resolve(false);
        }

        static void Resolve(bool allow)
        {
            if (!IsAsking) return;
            IsAsking = false;
            var cb = sPending;
            sPending = null;
            Debug.Log("[ToolConfirm] " + (allow ? "允许" : "拒绝") + "：" + sToolName);
            if (cb != null) cb(allow);
        }

        void Update()
        {
            // 弹窗期间强制不穿透：TransparentWindow 每帧会看 IsAsking，
            // 这里再兜一层，防止别的组件把 mouseOverPet 改回 false。
            if (IsAsking) TransparentWindow.mouseOverPet = true;
        }

        void OnGUI()
        {
            if (!IsAsking) return;

            GUI.depth = -3000;          // 比配置面板（-2000）更靠前
            GUI.skin.label.fontSize = 15;
            GUI.skin.button.fontSize = 15;
            GUI.skin.box.fontSize = 15;

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float w = Mathf.Min(560f, Screen.width - 40f);
            float h = 250f;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(r, GUI.skin.box);

            string who = PetProfileConfig.PetName;
            GUILayout.Label("<b>" + who + " 想执行一个外部工具，是否允许？</b>");
            GUILayout.Space(6);
            GUILayout.Label("工具：" + sToolTitle + "（" + sToolName + "）");
            GUILayout.Label("参数：");
            GUILayout.TextArea(sArgs, GUILayout.Height(70));
            GUILayout.Space(4);
            GUILayout.Label("⚠️ 允许后会在你电脑上真的执行这段操作。不确定就点拒绝。");
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.30f, 0.72f, 0.36f);
            if (GUILayout.Button("允许执行", GUILayout.Height(38))) Resolve(true);
            GUI.backgroundColor = new Color(0.86f, 0.26f, 0.26f);
            if (GUILayout.Button("拒绝", GUILayout.Height(38))) Resolve(false);
            GUI.backgroundColor = oldBg;
            GUILayout.EndHorizontal();

            float left = Mathf.Max(0f, 120f - (Time.unscaledTime - sOpenedAt));
            GUILayout.Label("（" + Mathf.CeilToInt(left) + " 秒内不选 = 自动拒绝）");

            GUILayout.EndArea();
        }
    }
}
