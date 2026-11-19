using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 场景里的「既定受害者」尸体（9号房床上）：用 NPC 正式模型摆出睡姿并冻结。
    /// 不参与 AI / 寻路 / 监控注册；摘面具由 CorpseInspectable 调用
    /// NPCVisualController.RevealFaceForCorpse 完成。
    /// </summary>
    [DisallowMultipleComponent]
    public class SleepingCorpseVisual : MonoBehaviour
    {
        public int number = 9;
        public Color maskColor = new Color(0.72f, 0.10f, 0.09f, 1f);

        void Awake()
        {
            // 旧场景可能残留 NPCController（RequireComponent 删除顺序问题），
            // 尸体会被算进 NPCController.All 当成一个睡着的活人。这里兜底清掉。
            NPCController stray = GetComponent<NPCController>();
            if (stray != null)
            {
                Debug.LogWarning("[SleepingCorpseVisual] 尸体上残留 NPCController，已在运行时移除。");
                Destroy(stray);
            }

            NPCVisualController visual = GetComponent<NPCVisualController>();
            if (visual == null) return;

            NPCData data = ScriptableObject.CreateInstance<NPCData>();
            data.number = number;
            data.displayName = number + "号";
            data.maskNumberColor = maskColor;
            visual.Configure(data);

            // 尸体的正式外观：斗篷 + 面具，不要裹尸布占位体，也不要人形骨架。
            // 旧场景里这几项状态可能残留，运行时强制纠正一次。
            if (visual.cloakRenderer != null) visual.cloakRenderer.enabled = true;
            if (visual.humanBody != null) visual.humanBody.SetActive(false);
            visual.sleepLocalScale = new Vector3(0.9f, 0.9f, 0.18f);
            visual.EnterSleepPose();
            Destroy(data);
        }
    }
}
