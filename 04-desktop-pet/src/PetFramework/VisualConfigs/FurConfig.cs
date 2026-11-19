using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【毛发形态】—— 对应 ShellFur URP（Universal Render Pipeline/Fur/Shell/Lit）。
    /// 默认值就是小白当前调好的那一套，所以挂上新资源不会改变外观。
    /// </summary>
    [CreateAssetMenu(fileName = "FurConfig", menuName = "桌面宠物/形态配置/毛发 (Fur)", order = 10)]
    public class FurConfig : PetVisualConfig
    {
        protected override void BuildDefaults()
        {
            presetName = "毛发形态 (ShellFur)";
            if (string.IsNullOrEmpty(description))
                description = "多层壳式毛发。适合白虎、雪豹、狐狸等毛茸茸的形态。";

            parameters = new System.Collections.Generic.List<PetVisualParam>
            {
                // ---------------- 毛的长度与密度 ----------------
                new PetVisualParam("TotalShellStep", "毛长 Total Shell Step", "_TotalShellStep",
                                   0f, 0.3f, 0.045f, "毛的长度与层间距", "越大毛越长，太大会变成刺猬"),
                new PetVisualParam("ShellAmount", "层数 Shell Amount", "_ShellAmount",
                                   4f, 48f, 7f, "毛的长度与层间距", "层数越多越细腻，但越费性能").AsInt(),

                // ---------------- 毛的形态 ----------------
                new PetVisualParam("AlphaCutout", "裁剪 Fur Cutout", "_AlphaCutout",
                                   0.05f, 0.5f, 0.1539286f, "毛的形态", "越小毛越碎越蓬松"),
                new PetVisualParam("FurScale", "随机度 Noise Scale", "_FurScale",
                                   0f, 10f, 2f, "毛的形态", "毛的随机分布密度"),
                new PetVisualParam("FurLengthIntensity", "长度倍率 Length Intensity", "_FurLengthIntensity",
                                   0.01f, 5f, 0.9881191f, "毛的形态"),

                // ---------------- 颜色 ----------------
                new PetVisualParam("RootColorGray", "毛根亮度 Root", "_RootColor",
                                   0.2f, 1.2f, 1f, "颜色", "压低毛根 = 有层次感；1 = 纯白").AsGray(),
                new PetVisualParam("TipColorGray", "毛尖亮度 Tip", "_TipColor",
                                   0.2f, 1.2f, 0.9976191f, "颜色", "毛尖一般比毛根亮").AsGray(),

                // ---------------- 反光 ----------------
                new PetVisualParam("Smoothness", "光滑度 Smoothness", "_Smoothness",
                                   0f, 1f, 0f, "反光", "⚠ 物理头发模式下这一项无效，用下面两个"),
                new PetVisualParam("RootSmoothness", "毛根/裤子 光滑 Root Smooth", "_RootSmoothness",
                                   0f, 1f, 0.6023809f, "反光", "物理头发模式的有效项"),
                new PetVisualParam("TipSmoothness", "毛尖/裤子 光滑 Tip Smooth", "_TipSmoothness",
                                   0f, 1f, 0.4952381f, "反光", "物理头发模式的有效项"),
                new PetVisualParam("NoFurMatte", "没毛处哑光（治裤子反光）", "_NoFurMatte",
                                   0f, 1f, 1f, "反光", "1 = 短裤等没毛的地方完全哑光；0 = 关掉"),

                // ---------------- 轮廓光 ----------------
                new PetVisualParam("RimLightIntensity", "边缘光强度 Rim", "_RimLightIntensity",
                                   0f, 3f, 1.8f, "轮廓光", "剪影的发光轮廓，1.5~2.0 比较自然"),
                new PetVisualParam("RimLightPower", "边缘光收窄 Rim Power", "_RimLightPower",
                                   1f, 10f, 2.492857f, "轮廓光", "越大越只贴着轮廓；10 几乎看不见"),
                new PetVisualParam("Backlit", "透光 Backlit", "_Backlit",
                                   0f, 1f, 0.35f, "轮廓光", "毛的透光感"),

                // ---------------- 软边 ----------------
                new PetVisualParam("AlphaToCoverage", "MSAA 软边 Alpha-To-Coverage", "_ALPHATEST_ON",
                                   0f, 1f, 1f, "软边", "开了毛边更柔和").AsKeyword(1f, "_AlphaToCoverageOn"),

                // ---------------- 光照 ----------------
                new PetVisualParam("LightRotX", "主光俯仰", "",
                                   0f, 89f, 45.00018f, "光照", "", PetParamTarget.LightRotX),
                new PetVisualParam("LightRotY", "主光水平", "",
                                   0f, 360f, 63.85713f, "光照", "", PetParamTarget.LightRotY),
                new PetVisualParam("LightIntensity", "主光强度", "",
                                   0f, 3f, 0.9f, "光照", "", PetParamTarget.LightIntensity),
                new PetVisualParam("AmbientIntensity", "环境光", "",
                                   0f, 2f, 0.35f, "光照", "", PetParamTarget.Ambient),

                // ---------------- 屏幕位置明暗补偿（桌面宠物专用）----------------
                new PetVisualParam("ViewLock", "锁定视线方向（走到屏幕哪都受光一致）", "_FurViewLock",
                                   0f, 1f, 1f, "屏幕位置补偿",
                                   "解决「小白走到屏幕不同位置时明暗不同」").AsToggle(),
                new PetVisualParam("LockedViewDir", "锁定方向向量", "_FurLockedViewDir",
                                   0f, 1f, 0f, "屏幕位置补偿", "用下面的角度滑杆调，不用手改")
                    .AsVector(new Vector3(0.4914971f, 0.2425291f, 0.8364271f))
            };
        }

        // 面板上"锁定方向"是两个角度滑杆，这里存一下上一帧的值用于显示
        float lastYaw = float.NaN, lastPitch = float.NaN;

        public override void DrawExtraGUI(PetVisualContext ctx)
        {
            var lockParam = Find("ViewLock");
            var dirParam = Find("LockedViewDir");
            if (lockParam == null || dirParam == null) return;
            if (ctx == null || ctx.material == null || !ctx.material.HasProperty("_FurLockedViewDir")) return;

            GUILayout.Label("--- 锁定方向微调 ---");

            Vector3 d = -dirParam.vector;
            if (d.sqrMagnitude < 1e-6f) d = Vector3.back;
            Vector3 eu = Quaternion.LookRotation(d, Vector3.up).eulerAngles;
            // 与写入式 Quaternion.Euler(pitch, 180+yaw, 0) * Vector3.back 取同号
            float pitch = -Mathf.DeltaAngle(0f, eu.x);
            float yaw = Mathf.DeltaAngle(180f, eu.y);
            if (!float.IsNaN(lastYaw)) { yaw = lastYaw; pitch = lastPitch; }   // 稳定显示，避免翻转跳动

            float ny = GUILayout.HorizontalSlider(yaw, -60f, 60f);
            GUILayout.Label("   水平偏移 = " + ny.ToString("0.0") + "°");
            float np = GUILayout.HorizontalSlider(pitch, -60f, 60f);
            GUILayout.Label("   俯仰偏移 = " + np.ToString("0.0") + "°");

            if (Mathf.Abs(ny - yaw) > 0.01f || Mathf.Abs(np - pitch) > 0.01f)
            {
                lastYaw = ny; lastPitch = np;
                dirParam.vector = (Quaternion.Euler(np, 180f + ny, 0f) * Vector3.back).normalized;
                dirParam.value = 1f;
                ApplyMaterialNow(ctx.material, dirParam);
            }

            GUILayout.Label("   当前向量 = " + dirParam.vector.ToString("F3"));

            if (GUILayout.Button("★ 以小白此刻的屏幕位置为基准锁定"))
            {
                var cam = Camera.main;
                if (ctx.pet != null && cam != null)
                {
                    Vector3 v = cam.transform.position - ctx.pet.transform.position;
                    if (v.sqrMagnitude > 0.0001f)
                    {
                        dirParam.vector = v.normalized;
                        lastYaw = float.NaN; lastPitch = float.NaN;   // 重新推导角度
                        lockParam.value = 1f;
                        Apply(ctx);                                   // 整体重套一遍（含 _FurViewLock）
                        if (ctx.log != null) ctx.log("已按小白当前位置重新锁定视线方向");
                    }
                }
            }
        }

        static void ApplyMaterialNow(Material m, PetVisualParam p)
        {
            if (m == null || p == null || !m.HasProperty(p.prop)) return;
            m.SetVector(p.prop, new Vector4(p.vector.x, p.vector.y, p.vector.z, 0f));
        }
    }
}
