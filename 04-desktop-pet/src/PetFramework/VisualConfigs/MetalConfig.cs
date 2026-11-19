using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【金属形态】—— 对应 URP/Lit 的金属工作流。
    /// 适合机甲、机器人、装甲宠物。
    /// </summary>
    [CreateAssetMenu(fileName = "MetalConfig", menuName = "桌面宠物/形态配置/金属 (Metal)", order = 12)]
    public class MetalConfig : PetVisualConfig
    {
        protected override void BuildDefaults()
        {
            presetName = "金属形态 (URP Lit)";
            if (string.IsNullOrEmpty(description))
                description = "高金属度、高光滑度的硬表面。适合机甲、机器人。";

            parameters = new System.Collections.Generic.List<PetVisualParam>
            {
                new PetVisualParam("BaseColor", "金属基色", "_BaseColor",
                                   0f, 1f, 0.78f, "颜色", "金属的反光会乘上这个颜色").AsColor(),
                new PetVisualParam("Metallic", "金属度 Metallic", "_Metallic",
                                   0f, 1f, 0.95f, "反光", "1 = 纯金属，几乎没有漫反射"),
                new PetVisualParam("Smoothness", "光滑度 Smoothness", "_Smoothness",
                                   0f, 1f, 0.85f, "反光", "越高越像镜面；0.6 左右更像喷砂金属"),
                new PetVisualParam("SpecularOff", "关闭镜面高光", "_SPECULARHIGHLIGHTS_OFF",
                                   0f, 1f, 0f, "反光", "觉得太刺眼可以打开它").AsKeyword(0f),
                new PetVisualParam("BumpScale", "表面凹凸", "_BumpScale",
                                   0f, 4f, 0.8f, "细节", "划痕/铆钉质感"),

                new PetVisualParam("EmissionColor", "发光颜色", "_EmissionColor",
                                   0f, 1f, 0f, "发光", "机甲的指示灯、能量线").AsColor(),
                new PetVisualParam("EmissionOn", "开启发光", "_EMISSION",
                                   0f, 1f, 0f, "发光").AsKeyword(0f),

                new PetVisualParam("LightRotX", "主光俯仰", "",
                                   0f, 89f, 45f, "光照", "金属很吃光的角度，多试几个", PetParamTarget.LightRotX),
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
