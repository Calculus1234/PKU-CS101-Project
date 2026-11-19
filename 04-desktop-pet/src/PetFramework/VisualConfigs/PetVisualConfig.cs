using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetFramework
{
    /// <summary>参数控件类型 —— 决定 F9 面板给它生成什么控件、以及怎么写进材质。</summary>
    public enum PetParamKind
    {
        Float,          // 普通滑杆
        Int,            // 取整滑杆
        Toggle,         // 勾选框（写 0/1 到 float 属性）
        GrayColor,      // 一个亮度滑杆 → 灰度 Color（毛根/毛尖常用）
        Color,          // 完整 RGB
        Vector,         // 三维向量（面板不自动画，交给子类 DrawExtraGUI）
        KeywordToggle   // 开关一个 shader 关键字（同时可联动一个 float 属性）
    }

    /// <summary>参数作用到哪儿。</summary>
    public enum PetParamTarget
    {
        Material,
        LightRotX,
        LightRotY,
        LightIntensity,
        Ambient
    }

    /// <summary>
    /// 一条可调参数的完整描述：既是【面板 UI 的定义】，也是【JSON 的存档单元】。
    /// 把它做成数据而不是硬编码，F9 面板才能"挂什么配置就画什么滑块"。
    /// </summary>
    [Serializable]
    public class PetVisualParam
    {
        public string key;                       // 唯一 id = JSON 里的键名
        public string label = "参数";             // 面板上显示的中文
        public string group = "";                // 分组标题（相同 group 会画在一段里）
        public string tip = "";                  // 面板上的一行小字说明
        public PetParamKind kind = PetParamKind.Float;
        public PetParamTarget target = PetParamTarget.Material;
        public string prop = "";                 // 材质属性名（或关键字名，见 KeywordToggle）
        public string prop2 = "";                // KeywordToggle 联动设置的 float 属性
        public float min = 0f;
        public float max = 1f;
        public bool wholeNumbers = false;

        public float def = 0f;                   // 设计默认值
        public float value = 0f;                 // 当前值（面板/JSON 改的就是它）

        public Color defColor = Color.white;
        public Color color = Color.white;

        public Vector3 defVector = Vector3.forward;
        public Vector3 vector = Vector3.forward;

        public PetVisualParam() { }

        public PetVisualParam(string key, string label, string prop, float min, float max, float def,
                              string group = "", string tip = "", PetParamTarget target = PetParamTarget.Material)
        {
            this.key = key; this.label = label; this.prop = prop;
            this.min = min; this.max = max; this.def = def; this.value = def;
            this.group = group; this.tip = tip; this.target = target;
        }

        /// <summary>把当前值恢复成设计默认值。</summary>
        public void ResetToDefault()
        {
            value = def; color = defColor; vector = defVector;
        }

        // ⚠️ 下面这些 As* 只在【传了非空值】时才覆盖 group/tip ——
        //    否则 .AsInt() 这种不带参数的调用会把构造函数里精心写好的分组和说明清成空字符串，
        //    面板上所有参数就会挤成一大坨、提示也没了。
        void SetGroupTip(string group, string tip)
        {
            if (!string.IsNullOrEmpty(group)) this.group = group;
            if (!string.IsNullOrEmpty(tip)) this.tip = tip;
        }

        public PetVisualParam AsInt(string group = "", string tip = "")
        {
            kind = PetParamKind.Int; wholeNumbers = true; min = Mathf.Round(min); max = Mathf.Round(max);
            def = Mathf.Round(def); value = def; SetGroupTip(group, tip); return this;
        }
        public PetVisualParam AsToggle(string group = "", string tip = "")
        {
            kind = PetParamKind.Toggle; min = 0f; max = 1f;
            def = def > 0.5f ? 1f : 0f; value = def; SetGroupTip(group, tip); return this;
        }
        public PetVisualParam AsGray(string group = "", string tip = "")
        {
            kind = PetParamKind.GrayColor; SetGroupTip(group, tip); return this;
        }
        public PetVisualParam AsKeyword(float isOn, string prop2 = "", string group = "", string tip = "")
        {
            kind = PetParamKind.KeywordToggle; min = 0f; max = 1f;
            def = isOn > 0.5f ? 1f : 0f; value = def; this.prop2 = prop2; SetGroupTip(group, tip); return this;
        }
        public PetVisualParam AsColor(string group = "", string tip = "")
        {
            kind = PetParamKind.Color; SetGroupTip(group, tip);
            color = defColor; return this;
        }
        public PetVisualParam AsVector(Vector3 v, string group = "", string tip = "")
        {
            kind = PetParamKind.Vector; defVector = v; vector = v; SetGroupTip(group, tip); return this;
        }
    }

    /// <summary>把参数套到目标上时需要的东西（材质、主光、小白本体）。</summary>
    public class PetVisualContext
    {
        public Material material;
        public Light directionalLight;
        public GameObject pet;
        public Action<string> log;
    }

    /// <summary>
    /// 【形态配置基类】—— 一种外形的全部可调参数。
    /// 白虎的毛发、青龙的鳞片、机甲的金属，各自派生一个子类即可，
    /// F9 面板不需要认识任何一种具体形态，照着 parameters 画就行。
    ///
    /// 用法：菜单 Assets → Create → 桌面宠物 → 形态配置 → 选一种。
    /// </summary>
    public abstract class PetVisualConfig : ScriptableObject
    {
        [Header("形态信息")]
        public string presetName = "未命名形态";
        [TextArea(2, 5)]
        public string description = "";

        [Header("参数表（由子类生成，一般不用手改）")]
        public List<PetVisualParam> parameters = new List<PetVisualParam>();

        /// <summary>子类在这里把参数加进 parameters。只在 params 为空时调用一次。</summary>
        protected abstract void BuildDefaults();

        /// <summary>强制重建参数表（Inspector 上的「重建参数表」按钮会调它）。</summary>
        public void Rebuild()
        {
            parameters = new List<PetVisualParam>();
            BuildDefaults();
        }

        void OnEnable()
        {
            // 老资源里 parameters 可能是空的（比如脚本更新后新加的项）——自动补上
            if (parameters == null || parameters.Count == 0) BuildDefaults();
        }

        public void ResetAllValues()
        {
            if (parameters == null) return;
            for (int i = 0; i < parameters.Count; i++) parameters[i].ResetToDefault();
        }

        public PetVisualParam Find(string key)
        {
            if (parameters == null) return null;
            for (int i = 0; i < parameters.Count; i++)
                if (parameters[i].key == key) return parameters[i];
            return null;
        }

        // ---------------- 套用 ----------------

        /// <summary>把当前参数值写进材质 / 灯光 / 环境光。</summary>
        public virtual void Apply(PetVisualContext ctx)
        {
            if (ctx == null || parameters == null) return;
            var m = ctx.material;
            var l = ctx.directionalLight;

            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (p == null) continue;
                // ⚠️ 只有写材质的参数才需要 prop 名；灯光/环境光那几条 prop 是空的，
                //    这里要是无脑跳过，主光和环境光的滑杆就全变成摆设了。
                if (p.target == PetParamTarget.Material && string.IsNullOrEmpty(p.prop)) continue;

                switch (p.target)
                {
                    case PetParamTarget.Material:
                        ApplyMaterial(m, p);
                        break;

                    case PetParamTarget.LightIntensity:
                        if (l != null) l.intensity = p.value;
                        break;

                    case PetParamTarget.LightRotX:
                    case PetParamTarget.LightRotY:
                        if (l != null)
                        {
                            var e = l.transform.eulerAngles;
                            if (p.target == PetParamTarget.LightRotX) e.x = p.value; else e.y = p.value;
                            l.transform.eulerAngles = e;
                        }
                        break;

                    case PetParamTarget.Ambient:
                        RenderSettings.ambientIntensity = p.value;
                        // ⚠️ 实测：Flat 模式下改 ambientIntensity，URP 给 SampleSH 的
                        //    环境探针不变（同一个材质渲染出来一个像素都没变）。
                        //    所以让材质自己拿一个 _AmbientLift 来吃这个值 ——
                        //    只有声明了它的 shader 会受影响，别的材质是空操作。
                        if (m != null && m.HasProperty("_AmbientLift"))
                            m.SetFloat("_AmbientLift", p.value);
                        break;
                }
            }
        }

        // protected：子类（比如 ScaleConfig 要同时驱动本体和特效材质）可以复用同一套写入逻辑
        protected static void ApplyMaterial(Material m, PetVisualParam p)
        {
            if (m == null || !m.HasProperty(p.prop)) return;
            switch (p.kind)
            {
                case PetParamKind.Float:
                case PetParamKind.Int:
                case PetParamKind.Toggle:
                    m.SetFloat(p.prop, p.value);
                    break;

                case PetParamKind.GrayColor:
                    m.SetColor(p.prop, new Color(p.value, p.value, p.value, 1f));
                    break;

                case PetParamKind.Color:
                    m.SetColor(p.prop, p.color);
                    break;

                case PetParamKind.Vector:
                    m.SetVector(p.prop, new Vector4(p.vector.x, p.vector.y, p.vector.z, 0f));
                    break;

                case PetParamKind.KeywordToggle:
                    // 关键字开关（prop = 关键字名）
                    if (p.value > 0.5f) m.EnableKeyword(p.prop); else m.DisableKeyword(p.prop);
                    // 有些 shader 还额外读一个 float 开关（比如 _AlphaToCoverageOn）
                    if (!string.IsNullOrEmpty(p.prop2) && m.HasProperty(p.prop2))
                        m.SetFloat(p.prop2, p.value > 0.5f ? 1f : 0f);
                    break;
            }
        }

        /// <summary>
        /// 子类专属的额外 UI（比如毛发那条"锁定视线方向"）。
        /// 通用面板画完参数表后会调它，普通形态不用重写。
        /// </summary>
        public virtual void DrawExtraGUI(PetVisualContext ctx) { }

        /// <summary>子类专属的额外 Apply（在通用 Apply 之后调）。</summary>
        public virtual void ApplyExtra(PetVisualContext ctx) { }
    }
}
