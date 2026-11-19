using System.Collections.Generic;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【形态清单】—— 一只桌宠能变的【所有】样子。
    ///
    /// 现在的用法：里面只放一个 PetForm（小白当前的样子），
    /// 于是"单形态"天然就是"多形态列表的第一个元素"，未来加形态零改动。
    ///
    /// ⚠️ 切换【故意没实现】：SwitchTo() 只改指针 + 打日志，
    ///    不换模型、不换材质、不换动画。真做变身时要补的是 PetVisualRoot 里的 TODO。
    /// </summary>
    [CreateAssetMenu(fileName = "PetFormSet_宠物名", menuName = "桌面宠物/形态清单 (PetFormSet)", order = 21)]
    public class PetFormSet : ScriptableObject
    {
        [Header("形态清单（第一个 = 默认形态）")]
        public List<PetForm> forms = new List<PetForm>();

        [Header("启动时用哪个（留空 = 用第一个）")]
        public string startFormName = "";

        [SerializeField, HideInInspector] int currentIndex = 0;

        public int Count { get { return forms != null ? forms.Count : 0; } }

        public PetForm Current
        {
            get
            {
                if (forms == null || forms.Count == 0) return null;
                if (currentIndex < 0 || currentIndex >= forms.Count) currentIndex = 0;
                return forms[currentIndex];
            }
        }

        public int CurrentIndex { get { return currentIndex; } }

        /// <summary>按 formName 找形态（不区分大小写）。</summary>
        public PetForm Find(string formName)
        {
            if (forms == null || string.IsNullOrEmpty(formName)) return null;
            for (int i = 0; i < forms.Count; i++)
            {
                var f = forms[i];
                if (f == null) continue;
                if (string.Equals(f.formName, formName, System.StringComparison.OrdinalIgnoreCase)) return f;
                if (string.Equals(f.displayName, formName, System.StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>启动时该用哪个形态。</summary>
        public PetForm GetStartForm()
        {
            var f = Find(startFormName);
            return f != null ? f : Current;
        }

        /// <summary>
        /// 【骨架】切到某个形态。目前【只改指针 + 打日志】，外观/动画都不会变。
        /// 返回 true 表示"形态存在、指针已切"，不表示外观已经变了。
        /// </summary>
        public bool SwitchTo(string formName)
        {
            var f = Find(formName);
            if (f == null)
            {
                Debug.LogWarning("[PetFormSet] 没有叫「" + formName + "」的形态。现有："
                                 + AllNames());
                return false;
            }
            int idx = forms.IndexOf(f);
            if (idx == currentIndex) return true;
            currentIndex = idx;
            Debug.Log("[PetFormSet] 形态指针 → " + f.Readable()
                      + "　⚠ 外观尚未切换（变身逻辑未实现）");
            return true;
        }

        public string AllNames()
        {
            if (forms == null || forms.Count == 0) return "（空）";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < forms.Count; i++)
            {
                if (forms[i] == null) continue;
                if (sb.Length > 0) sb.Append("、");
                sb.Append(forms[i].formName);
            }
            return sb.ToString();
        }
    }
}
