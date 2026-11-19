using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【特效调参面板 · F10】—— 运行时实时拖，改完当场看效果。
    ///
    /// 为什么单独做一个面板而不是塞进 FurTuner：
    ///   FurTuner 是"材质参数"面板（毛皮/鳞片那一套 PetVisualParam），
    ///   特效关心的是"发射率 / 尺寸 / 边缘强度 / 拖尾宽度"这些粒子系统参数，
    ///   两套模型不一样，硬塞会互相打架。
    ///
    /// ⚠️ 这里的修改只在运行时生效，不会写回 Prefab。
    ///    调满意后按「打印当前数值」，把日志里的数字抄进 Prefab/材质即可。
    /// </summary>
    public class PetVFXPanel : MonoBehaviour
    {
        [Tooltip("留空会自动在本物体上找")]
        public PetVFXController vfx;

        public KeyCode toggleKey = KeyCode.F10;
        public bool visible;

        Vector2 scroll;

        void Awake()
        {
            if (vfx == null) vfx = GetComponent<PetVFXController>();
            if (vfx == null) vfx = GetComponentInChildren<PetVFXController>();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (visible) TransparentWindow.mouseOverPet = true;   // 面板开着别让点击穿透掉
        }

        void OnGUI()
        {
            if (!visible || vfx == null) return;

            GUI.depth = -2200;
            GUI.skin.label.fontSize = 14;
            GUI.skin.button.fontSize = 14;
            GUI.skin.horizontalSlider.fontSize = 14;

            float w = 360f, h = Mathf.Min(720f, Screen.height - 40f);
            var r = new Rect(Screen.width - w - 16f, 16f, w, h);
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label("<b>特效调参 · F10 关闭</b>");

            scroll = GUILayout.BeginScrollView(scroll);

            Section("① 全身符号", vfx.mathSymbolsVFX, true);
            Section("② 移动星尘", vfx.movementVFX, false);

            GUILayout.Space(10);
            GUILayout.Label("<b>状态发射率</b>");
            vfx.interactionRate = Slider("互动（爆发）", vfx.interactionRate, 0f, 120f);
            vfx.idleRate = Slider("待机（呼吸）", vfx.idleRate, 0f, 60f);
            vfx.dragRate = Slider("被拽", vfx.dragRate, 0f, 60f);

            GUILayout.Space(10);
            GUILayout.Label("<b>拖尾</b>");
            if (vfx.moveTrail != null)
            {
                vfx.moveTrail.widthMultiplier = Slider("拖尾宽度", vfx.moveTrail.widthMultiplier, 0.02f, 0.5f);
                vfx.moveTrail.time = Slider("拖尾时长", vfx.moveTrail.time, 0.05f, 1.2f);
                if (vfx.moveTrail.sharedMaterial != null)
                {
                    var m = vfx.moveTrail.sharedMaterial;
                    if (m.HasProperty("_Intensity"))
                    {
                        float v = m.GetFloat("_Intensity");
                        float nv = Slider("拖尾亮度", v, 0f, 4f);
                        if (!Mathf.Approximately(v, nv)) m.SetFloat("_Intensity", nv);
                    }
                    if (m.HasProperty("_LengthFade"))
                    {
                        float v = m.GetFloat("_LengthFade");
                        float nv = Slider("拖尾衰减", v, 0.2f, 5f);
                        if (!Mathf.Approximately(v, nv)) m.SetFloat("_LengthFade", nv);
                    }
                }
            }
            else GUILayout.Label("　（未绑定 TrailRenderer）");

            GUILayout.Space(10);
            if (GUILayout.Button("打印当前数值到 Console", GUILayout.Height(30))) Dump();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void Section(string title, ParticleSystem ps, bool withLum)
        {
            GUILayout.Space(8);
            GUILayout.Label("<b>" + title + "</b>");
            if (ps == null) { GUILayout.Label("　（未绑定）"); return; }

            var main = ps.main;
            var em = ps.emission;

            float rate = Slider("发射率 Rate", em.rateOverTime.constant, 0f, 120f);
            if (!Mathf.Approximately(rate, em.rateOverTime.constant)) em.rateOverTime = rate;

            float szMin = Slider("最小尺寸", main.startSize.constantMin, 0.005f, 0.4f);
            float szMax = Slider("最大尺寸", main.startSize.constantMax, 0.005f, 0.4f);
            if (szMax < szMin) szMax = szMin;
            if (!Mathf.Approximately(main.startSize.constantMin, szMin) ||
                !Mathf.Approximately(main.startSize.constantMax, szMax))
                main.startSize = new ParticleSystem.MinMaxCurve(szMin, szMax);

            float lifeMin = Slider("最短寿命", main.startLifetime.constantMin, 0.2f, 6f);
            float lifeMax = Slider("最长寿命", main.startLifetime.constantMax, 0.2f, 6f);
            if (lifeMax < lifeMin) lifeMax = lifeMin;
            if (!Mathf.Approximately(main.startLifetime.constantMin, lifeMin) ||
                !Mathf.Approximately(main.startLifetime.constantMax, lifeMax))
                main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);

            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r != null && r.sharedMaterial != null)
            {
                var m = r.sharedMaterial;
                MatSlider(m, withLum ? "_EmissionIntensity" : "_EmissionIntensity", "自发光强度", 0f, 4f);
                MatSlider(m, "_EdgeMask", "边缘遮罩", 0f, 1f);
                MatSlider(m, "_FresnelStrength", "边缘光强", 0f, 3f);
                MatSlider(m, "_FresnelPower", "边缘锐度", 0.5f, 8f);
                if (withLum) MatSlider(m, "_LumGain", "亮度增益", 0.5f, 4f);
                MatSlider(m, "_EdgeFeather", "边缘羽化", 0f, 0.5f);
            }
        }

        void MatSlider(Material m, string prop, string label, float min, float max)
        {
            if (m == null || !m.HasProperty(prop)) return;
            float v = m.GetFloat(prop);
            float nv = Slider(label, v, min, max);
            if (!Mathf.Approximately(v, nv)) m.SetFloat(prop, nv);
        }

        float Slider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(96));
            float v = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.Label(v.ToString("0.00"), GUILayout.Width(46));
            GUILayout.EndHorizontal();
            return v;
        }

        void Dump()
        {
            Debug.Log("[VFXPanel] ===== 当前特效参数 =====");
            DumpPs("全身符号", vfx.mathSymbolsVFX);
            DumpPs("移动", vfx.movementVFX);
            Debug.Log("[VFXPanel] 状态发射率 互动=" + vfx.interactionRate + " 待机=" + vfx.idleRate + " 被拽=" + vfx.dragRate);
            if (vfx.moveTrail != null)
                Debug.Log("[VFXPanel] 拖尾 width=" + vfx.moveTrail.widthMultiplier
                          + " time=" + vfx.moveTrail.time
                          + " intensity=" + (vfx.moveTrail.sharedMaterial != null ? vfx.moveTrail.sharedMaterial.GetFloat("_Intensity") : -1));
        }

        void DumpPs(string label, ParticleSystem ps)
        {
            if (ps == null) { Debug.Log("[VFXPanel] " + label + " = 未绑定"); return; }
            var main = ps.main; var em = ps.emission;
            Debug.Log("[VFXPanel] " + label
                + " rate=" + em.rateOverTime.constant
                + " size=" + main.startSize.constantMin.ToString("0.000") + "-" + main.startSize.constantMax.ToString("0.000")
                + " life=" + main.startLifetime.constantMin.ToString("0.00") + "-" + main.startLifetime.constantMax.ToString("0.00"));
        }
    }
}
