using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 把相机拉到合适距离，让小白约占屏幕高度的 petScreenHeightFraction（默认 13%），
    /// 于是它能在一个"满屏"的平面上四处游走。同时按距离比例放大移动速度，保持手感一致。
    /// </summary>
    public class PetCameraFraming : MonoBehaviour
    {
        [Header("取景")]
        public float petScreenHeightFraction = 0.13f;   // 小白占屏幕高度的比例（越小越小只）
        public float petHeightMeters = 1.15f;           // 小白实际高度（米）

        [Header("速度换算")]
        public float baseDistance = 1.57f;              // 原相机距离（用于等比放大速度）
        public float baseWalkSpeed = 0.85f;
        public float baseRunSpeed = 1.49f;

        [Header("追光标判定范围（按屏幕高度比例，分辨率无关）")]
        public float chaseScreenFraction = 0.35f;   // 光标进入屏高 35% 内就去追（越大越敏感）

        /// <summary>玩家在面板里调过的大小（%）。没有记录就用 Inspector 的默认值。</summary>
        public const string SizePrefKey = "Pet_ScreenHeightPercent";

        /// <summary>当前占屏高百分比（0~100）。面板滑杆直接绑这个。</summary>
        public float ScreenPercent
        {
            get { return petScreenHeightFraction * 100f; }
        }

        /// <summary>面板滑杆调大小：设置 → 立刻重算取景 → 记住（下次启动还用这个值）。</summary>
        public void SetScreenPercent(float percent, bool save = true)
        {
            petScreenHeightFraction = Mathf.Clamp(percent, 3f, 60f) / 100f;
            Apply();
            if (save)
            {
                PlayerPrefs.SetFloat(SizePrefKey, ScreenPercent);
                PlayerPrefs.Save();
            }
            Debug.Log("[PetCameraFraming] 大小 = " + ScreenPercent.ToString("0.0") + "% 屏高");
        }

        void Start()
        {
            // 玩家调过就以他调的为准；没调过用 Inspector 的默认值
            if (PlayerPrefs.HasKey(SizePrefKey))
                petScreenHeightFraction = Mathf.Clamp(PlayerPrefs.GetFloat(SizePrefKey, ScreenPercent), 3f, 60f) / 100f;
            Apply();
        }

        /// <summary>按当前参数摆好相机并把速度换算到 PetBrain。
        /// 抽成公开方法是因为【取景只在 Start 算一次】—— 从 fur_settings.txt 改了
        /// petScreenHeightFraction 之后必须再调一次，否则改了没用。</summary>
        public void Apply()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[PetCameraFraming] 没有 MainCamera"); return; }

            float fov = cam.fieldOfView * Mathf.Deg2Rad;
            float d = petHeightMeters / (2f * Mathf.Tan(fov * 0.5f) * Mathf.Max(0.01f, petScreenHeightFraction));

            var p = cam.transform.position;
            p.x = 0f; p.y = petHeightMeters * 0.5f; p.z = d;
            cam.transform.position = p;
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);   // 正对 -Z（看向小白）

            float scale = d / Mathf.Max(0.01f, baseDistance);
            var brain = FindObjectOfType<PetBrain>();
            if (brain != null)
            {
                brain.walkSpeed = baseWalkSpeed * scale;
                brain.runSpeed = baseRunSpeed * scale;
                float visibleHeight = 2f * d * Mathf.Tan(fov * 0.5f);
                brain.chaseRange = visibleHeight * chaseScreenFraction;   // 屏幕比例→世界距离，屏幕大了追得也远
            }
            Debug.Log("[PetCameraFraming] 相机距离=" + d.ToString("0.00") + " 速度倍率=" + scale.ToString("0.00"));
        }
    }
}
