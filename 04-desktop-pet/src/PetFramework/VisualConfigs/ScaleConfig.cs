using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【鳞片形态】—— 对应 URP/Lit（或任何用标准属性名的自定义 shader）。
    /// 适合青龙、蛟、鱼、蜥蜴等带鳞片/发光纹路的形态。
    /// 换别人的 shader 时，只要属性名对得上（_BaseColor/_Metallic/_Smoothness…），这里不用改代码。
    /// </summary>
    [CreateAssetMenu(fileName = "ScaleConfig", menuName = "桌面宠物/形态配置/鳞片 (Scale)", order = 11)]
    public class ScaleConfig : PetVisualConfig
    {
        protected override void BuildDefaults()
        {
            presetName = "鳞片形态 (URP Lit)";
            if (string.IsNullOrEmpty(description))
                description = "带鳞纹与发光的形态。适合青龙、蛟龙等。";

            parameters = new System.Collections.Generic.List<PetVisualParam>
            {
                new PetVisualParam("BaseColor", "基础色", "_BaseColor",
                                   0f, 1f, 0.25f, "颜色", "鳞片主色").AsColor(),
                new PetVisualParam("Metallic", "金属度 Metallic", "_Metallic",
                                   0f, 1f, 0.65f, "反光", "鳞片一般半金属"),
                new PetVisualParam("Smoothness", "光滑度 Smoothness", "_Smoothness",
                                   0f, 1f, 0.72f, "反光", "越高越湿滑反光"),
                new PetVisualParam("BumpScale", "鳞纹凹凸强度", "_BumpScale",
                                   0f, 4f, 1.2f, "细节", "需要材质上有法线贴图才有效果"),
                new PetVisualParam("Occlusion", "角落阴影 Occlusion", "_OcclusionStrength",
                                   0f, 1f, 0.8f, "细节", "需要 AO 贴图"),

                new PetVisualParam("EmissionColor", "发光颜色", "_EmissionColor",
                                   0f, 1f, 0f, "发光", "龙鳞的呼吸光").AsColor(),
                new PetVisualParam("EmissionOn", "开启发光", "_EMISSION",
                                   0f, 1f, 0f, "发光").AsKeyword(0f),

                new PetVisualParam("LightRotX", "主光俯仰", "",
                                   0f, 89f, 45f, "光照", "", PetParamTarget.LightRotX),
                new PetVisualParam("LightRotY", "主光水平", "",
                                   0f, 360f, 63.86f, "光照", "", PetParamTarget.LightRotY),
                new PetVisualParam("LightIntensity", "主光强度", "",
                                   0f, 3f, 0.9f, "光照", "", PetParamTarget.LightIntensity),
                new PetVisualParam("AmbientIntensity", "环境光", "",
                                   0f, 2f, 0.35f, "光照", "", PetParamTarget.Ambient)
            };
        }
    }
}
