using System.Collections.Generic;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【单个形态的动作映射】—— 把该形态自己的 AnimationClip 塞进基础状态机。
    ///
    /// 关键设计：**我们不碰状态机本身**。
    /// PetBrain 是靠触发名（doIdle / doWalk / doRun …）驱动 Animator 的，
    /// 如果这里让用户自由定义槽位，PetBrain 就不知道触发什么了。
    /// 所以做法是 —— 基础 controller 的【状态机和触发名当地基】，
    /// 这里只负责用 AnimatorOverrideController 把【片段换掉】。
    ///
    ///   好处：① PetBrain 一行都不用改
    ///         ② 青龙 / 机甲做一个同样 11 状态的状态机就能复用
    ///         ③ 换形态 = 换一套 clip，不是换一套逻辑
    /// </summary>
    [CreateAssetMenu(fileName = "PetAnim_新形态", menuName = "桌面宠物/动作配置 (PetAnimationConfig)", order = 20)]
    public class PetAnimationConfig : ScriptableObject
    {
        [Header("基础状态机（触发名必须与 PetBrain 一致：doIdle/doWalk/…）")]
        public RuntimeAnimatorController baseController;

        [Header("槽位（按需覆盖，没填的槽位就用基础 controller 里的原片段）")]
        public List<AnimSlot> slots = new List<AnimSlot>();

        [Header("过渡")]
        [Tooltip("换形态时的过渡时长（未来做变身动画时用）")]
        public float transitionSeconds = 0.4f;

        [System.Serializable]
        public class AnimSlot
        {
            public string slot;             // 槽位名，如 Idle / Walk / Run
            public AnimationClip clip;      // 该形态的片段；留空 = 用基础 controller 的原片段
        }

        /// <summary>框架约定的 11 个槽位 —— 和 PetBrain 的状态机一一对应。</summary>
        public static readonly string[] DefaultSlots =
        {
            "Idle", "Walk", "Run", "Drag", "Drop", "Pet", "Jump", "LookBack", "Sit", "Sleep", "Talk"
        };

        void OnEnable()
        {
            if (slots == null || slots.Count == 0) FillDefaultSlots();
        }

        /// <summary>补齐缺失的槽位（不会覆盖已经填过的）。</summary>
        public void FillDefaultSlots()
        {
            if (slots == null) slots = new List<AnimSlot>();
            for (int i = 0; i < DefaultSlots.Length; i++)
            {
                bool has = false;
                for (int k = 0; k < slots.Count; k++)
                    if (slots[k] != null && slots[k].slot == DefaultSlots[i]) { has = true; break; }
                if (!has) slots.Add(new AnimSlot { slot = DefaultSlots[i], clip = null });
            }
        }

        /// <summary>
        /// 生成这个形态的 AnimatorOverrideController。
        /// 匹配规则：基础 controller 里原片段的【名字包含槽位名】就算命中
        /// （例如原片段叫 Anim_Loop_Idle，槽位名 Idle ⇒ 命中）。
        /// baseController 为空或没有任何可覆盖的片段时返回 null。
        /// </summary>
        public AnimatorOverrideController BuildOverride()
        {
            if (baseController == null) return null;

            var ov = new AnimatorOverrideController(baseController);
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            ov.GetOverrides(pairs);
            if (pairs.Count == 0) return null;

            int replaced = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var orig = pairs[i].Key;
                if (orig == null) continue;
                var want = FindClipFor(orig.name);
                if (want != null)
                {
                    pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(orig, want);
                    replaced++;
                }
            }
            if (replaced == 0) return null;      // 没配任何片段 → 不需要覆盖
            ov.ApplyOverrides(pairs);
            return ov;
        }

        AnimationClip FindClipFor(string originalName)
        {
            if (slots == null || string.IsNullOrEmpty(originalName)) return null;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.clip == null || string.IsNullOrEmpty(s.slot)) continue;
                if (originalName.IndexOf(s.slot, System.StringComparison.OrdinalIgnoreCase) >= 0) return s.clip;
            }
            return null;
        }

        /// <summary>面板/日志用的一行摘要。</summary>
        public string Readable()
        {
            if (slots == null) return "（无槽位）";
            int filled = 0;
            for (int i = 0; i < slots.Count; i++) if (slots[i] != null && slots[i].clip != null) filled++;
            return filled + "/" + slots.Count + " 个槽位已填片段"
                 + (baseController != null ? "，基础状态机=" + baseController.name : "，⚠ 没有基础状态机");
        }
    }
}
