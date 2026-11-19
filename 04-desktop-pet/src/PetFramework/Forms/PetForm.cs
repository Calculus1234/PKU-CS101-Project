using System;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【一个形态】—— 小白的"这一副样子"。
    ///
    /// 名字 + 模型 + 材质配置 + 动作配置，四件套。
    /// 未来加"虎形态"，就往 PetFormSet 里再塞一个 PetForm，不用改任何代码。
    ///
    /// ⚠️ 现在【没有任何切换实现】—— 仅为数据结构留位。见 PetVisualRoot.SwitchForm()。
    /// </summary>
    [Serializable]
    public class PetForm
    {
        [Tooltip("形态 id（给 AI / 代码用），如 humanoid、tiger。用英文、不带空格")]
        public string formName = "default";

        [Tooltip("给人看的名字，如「兽人形态」「虎形态」")]
        public string displayName = "默认形态";

        [TextArea(1, 3)]
        public string description = "";

        [Header("该形态的模型")]
        [Tooltip("留空 = 用场景里现有的那个模型（当前桌宠就是这种）")]
        public GameObject modelPrefab;

        [Header("该形态的材质参数")]
        [Tooltip("FurConfig / ScaleConfig / MetalConfig 都行")]
        public PetVisualConfig visualConfig;

        [Header("该形态的动作映射")]
        public PetAnimationConfig animationConfig;

        [Header("该形态的体型（可选，留空用 1）")]
        [Tooltip("有些形态是四足，站高和取景比例都不一样")]
        public float heightMeters = 0f;          // 0 = 不覆盖，沿用场景里的
        public float screenHeightFraction = 0f;  // 0 = 不覆盖

        public bool IsValid { get { return !string.IsNullOrEmpty(formName); } }

        /// <summary>给 AI / 日志看的一行摘要。</summary>
        public string Readable()
        {
            return formName + "（" + displayName + "）"
                 + " 模型=" + (modelPrefab != null ? modelPrefab.name : "场景现有")
                 + " 材质=" + (visualConfig != null ? visualConfig.presetName : "无")
                 + " 动作=" + (animationConfig != null ? animationConfig.name : "无");
        }
    }
}
