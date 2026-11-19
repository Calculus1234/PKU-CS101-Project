using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using PetFramework;

namespace PetFramework
{
    /// <summary>
    /// 【F9 实时调参面板】—— 现在已经【完全通用】了，不认识任何具体形态。
    ///
    /// 它的工作方式：找到场景里的 PetVisualRoot → 读它挂的 PetVisualConfig →
    /// 照着 config.parameters 动态画出滑块。挂 FurConfig 就是毛发面板，
    /// 挂 MetalConfig 就是金属面板，加新形态【不用改这个文件的任何一行】。
    ///
    /// ⚠️ 类名保留叫 FurTuner 是因为【场景里的组件是按 GUID+类名认脚本的】，
    ///    改名会让场景里的引用丢失。新工程请直接用 PetVisualTuner 这个名字。
    /// </summary>
    public class FurTuner : MonoBehaviour
    {
        public bool visible = false;   // 默认隐藏，按 F9 才出现
        public KeyCode toggleKey = KeyCode.F9;

        [Header("（留空会自动找场景里的 PetVisualRoot）")]
        public PetVisualRoot root;

        [Header("面板")]
        public bool showTips = true;
        public float panelWidth = 460f;

        string status = "";
        bool dirty;
        Vector2 scroll;

        GraphicRaycaster[] raycasters;
        Collider[] petColliders;
        bool uiBlocked;

        // ★ 用 ActiveConfig 而不是 config：多形态配好后，面板要跟着【当前形态】走。
        //   formSet 为空时 ActiveConfig 恒等于 config，行为和以前完全一样。
        PetVisualConfig Config { get { return root != null ? root.ActiveConfig : null; } }

        void Start() { FindRoot(); SetUIBlock(visible); }

        void FindRoot()
        {
            if (root != null) return;
    #if UNITY_2022_2_OR_NEWER
            root = Object.FindFirstObjectByType<PetVisualRoot>();
    #else
            root = Object.FindObjectOfType<PetVisualRoot>();
    #endif
            if (root == null) status = "场景里没有 PetVisualRoot（形态配置没挂）";
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) { visible = !visible; SetUIBlock(visible); }
            if (visible) TransparentWindow.mouseOverPet = true;   // 面板可见时别让点击穿透掉
            else if (uiBlocked) SetUIBlock(false);

            // 面板上改完，这一帧结束时统一套一次（比每个控件各套一次干净）
            if (dirty)
            {
                dirty = false;
                if (root != null) root.Apply();
            }
        }

        // 面板打开时：屏蔽后层 uGUI 的点击接收 + 小白的碰撞体，避免"拖滑杆误点到后面"
        void SetUIBlock(bool blocked)
        {
            if (blocked && (raycasters == null || raycasters.Length == 0)) raycasters = FindObjectsOfType<GraphicRaycaster>();
            if (blocked && (petColliders == null || petColliders.Length == 0))
            {
                var pet = GameObject.Find("WhiteTiger_DesktopPet");
                if (pet != null) petColliders = pet.GetComponentsInChildren<Collider>(true);
            }
            if (raycasters != null) foreach (var r in raycasters) if (r != null) r.enabled = !blocked;
            if (petColliders != null) foreach (var c in petColliders) if (c != null) c.enabled = !blocked;
            uiBlocked = blocked;
        }

        void OnGUI()
        {
            if (!visible) return;
            if (root == null) FindRoot();

            GUI.depth = -1000;
            GUI.skin.label.fontSize = 14;
            GUI.skin.button.fontSize = 15;

            GUILayout.BeginArea(new Rect(10, 10, panelWidth, Screen.height - 20), GUI.skin.box);

            var cfg = Config;
            if (cfg == null)
            {
                GUILayout.Label("<b>调参面板</b>");
                GUILayout.Label(root == null
                    ? "场景里找不到 PetVisualRoot。\n给它挂一个，并拖入一个形态配置（FurConfig / ScaleConfig / MetalConfig）。"
                    : "PetVisualRoot 上的 config 是空的。\n请拖一个形态配置进去。");
                GUILayout.Label(status);
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label("<b>" + cfg.presetName + "　(F9 开关)</b>");
            if (!string.IsNullOrEmpty(cfg.description)) GUILayout.Label("　" + cfg.description);
            GUILayout.Label("拖到满意 → 点最下方「保存」；也可以直接改 JSON 文件，保存后 2 秒内自动生效");

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));

            var ctx = root.MakeContext();
            string lastGroup = null;
            for (int i = 0; i < cfg.parameters.Count; i++)
            {
                var p = cfg.parameters[i];
                if (p == null) continue;

                if (!string.IsNullOrEmpty(p.group) && p.group != lastGroup)
                {
                    lastGroup = p.group;
                    GUILayout.Space(6);
                    GUILayout.Label("─── " + p.group + " ───");
                }
                DrawParam(p, ctx);
            }

            // 子类专属 UI（比如毛发那条"锁定视线方向"），通用面板不关心它画什么
            GUILayout.Space(8);
            cfg.DrawExtraGUI(ctx);

            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("★ 保存到 JSON")) { root.SaveJson(); status = root.Status; }
            if (GUILayout.Button("↻ 重新读取")) { root.Reload(); status = root.Status; }
            if (GUILayout.Button("恢复默认值")) { cfg.ResetAllValues(); root.Apply(); status = "已恢复默认值"; }
            GUILayout.EndHorizontal();

            GUILayout.Label(root.SettingsPath);
            GUILayout.Label(status);
            GUILayout.EndArea();
        }

        /// <summary>照着参数的类型画对应的控件。加新控件类型只需要在这里加一个 case。</summary>
        void DrawParam(PetVisualParam p, PetVisualContext ctx)
        {
            string tip = showTips ? p.tip : null;
            bool hasTip = !string.IsNullOrEmpty(tip);

            switch (p.kind)
            {
                case PetParamKind.Float:
                case PetParamKind.Int:
                {
                    bool whole = p.kind == PetParamKind.Int;
                    string shown = whole
                        ? Mathf.RoundToInt(p.value).ToString()
                        : p.value.ToString("0.000");
                    GUILayout.Label(p.label + " = " + shown + (hasTip ? "\n    " + tip : ""));
                    float nv = GUILayout.HorizontalSlider(p.value, p.min, p.max);
                    if (whole) nv = Mathf.Round(nv);
                    if (!Mathf.Approximately(nv, p.value)) { p.value = nv; dirty = true; }
                    break;
                }

                case PetParamKind.Toggle:
                {
                    bool cur = p.value > 0.5f;
                    bool nv = GUILayout.Toggle(cur, " " + p.label + (hasTip ? "　— " + tip : ""));
                    if (nv != cur) { p.value = nv ? 1f : 0f; dirty = true; }
                    break;
                }

                case PetParamKind.GrayColor:
                {
                    GUILayout.Label(p.label + " = " + p.value.ToString("0.00") + (hasTip ? "\n    " + tip : ""));
                    float nv = GUILayout.HorizontalSlider(p.value, p.min, p.max);
                    if (!Mathf.Approximately(nv, p.value)) { p.value = nv; dirty = true; }
                    // 顺手显示一下颜色（灰块）
                    var r = GUILayoutUtility.GetRect(panelWidth - 40f, 10f);
                    var old = GUI.color;
                    GUI.color = new Color(p.value, p.value, p.value, 1f);
                    GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = old;
                    break;
                }

                case PetParamKind.Color:
                {
                    GUILayout.Label(p.label + (hasTip ? "\n    " + tip : ""));
                    float rr = GUILayout.HorizontalSlider(p.color.r, 0f, 1f);
                    float gg = GUILayout.HorizontalSlider(p.color.g, 0f, 1f);
                    float bb = GUILayout.HorizontalSlider(p.color.b, 0f, 1f);
                    var nc = new Color(rr, gg, bb, 1f);
                    if (nc != p.color) { p.color = nc; dirty = true; }
                    var rect = GUILayoutUtility.GetRect(panelWidth - 40f, 14f);
                    var old = GUI.color;
                    GUI.color = nc;
                    GUI.DrawTexture(rect, Texture2D.whiteTexture);
                    GUI.color = old;
                    break;
                }

                case PetParamKind.KeywordToggle:
                {
                    bool cur = p.value > 0.5f;
                    bool nv = GUILayout.Toggle(cur, " " + p.label + (hasTip ? "　— " + tip : ""));
                    if (nv != cur) { p.value = nv ? 1f : 0f; dirty = true; }
                    break;
                }

                case PetParamKind.Vector:
                    // 三维向量不自动画（两个角度滑杆那种专用 UI 交给子类 DrawExtraGUI）
                    GUILayout.Label(p.label + " = " + p.vector.ToString("F3") + (hasTip ? "\n    " + tip : ""));
                    break;
            }
        }
    }
}
