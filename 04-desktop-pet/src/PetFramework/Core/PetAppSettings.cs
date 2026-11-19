using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using PetFramework;

namespace PetFramework
{
    /// <summary>
    /// 【项目胶水层】把 pet_visual.json 里的"应用级配置"套到小白自己的组件上。
    ///
    /// 为什么单独一个文件、而不是放进 PetFramework：
    ///   框架是给别人复用的，不该认识 PetAPI / PetContext / PetCameraFraming 这些本项目的类。
    ///   所以框架只管【材质/灯光】，AI 与取景这些属于"接入层"的东西留在项目里。
    ///
    /// 它是【重构二的铺垫】—— 等模块二做了 EXE 内的配置面板，这里的键就搬到那个面板去改。
    /// 现阶段的作用：保住上一版"改模型/改超时不用重新打包"的能力，别因为框架化而回退。
    /// </summary>
    public class PetAppSettings : MonoBehaviour
    {
        [Header("配置文件（和 F9 面板用的是同一个）")]
        public string settingsPath = "";                 // 留空 = EXE/工程根目录
        public string fileName = "pet_visual.json";
        public bool autoReload = true;
        public float watchIntervalSec = 2f;

        long lastWriteTicks = -1;
        float nextWatch;
        bool ready;

        public string Status { get; private set; }

        // 和 PetVisualRoot 用【同一套查找规则】，否则会出现"F9 面板读得到、AI 配置读不到"的怪事
        string Path_ { get { return PetVisualRoot.ResolveConfigPath(settingsPath, fileName); } }

        IEnumerator Start()
        {
            yield return null;      // 等所有组件 Start 完（PetCameraFraming 会重算 walkSpeed）
            Apply();
            ready = true;
        }

        void Update()
        {
            if (!autoReload || !ready) return;
            if (Time.time < nextWatch) return;
            nextWatch = Time.time + Mathf.Max(0.5f, watchIntervalSec);
            try
            {
                string p = Path_;
                if (!File.Exists(p)) return;
                long t = File.GetLastWriteTimeUtc(p).ToFileTimeUtc();
                if (t != lastWriteTicks)
                {
                    if (lastWriteTicks >= 0) Debug.Log("[PetAppSettings] 配置文件被改动 → 重新套用应用级设置");
                    Apply();
                }
            }
            catch { }
        }

        public void Apply()
        {
            string p = Path_;
            if (!File.Exists(p)) { Status = "没有配置文件，用场景里的原值"; return; }

            Dictionary<string, string> d;
            try { d = PetVisualRoot.ScanFlatJson(File.ReadAllText(p)); }
            catch (System.Exception e) { Status = "读取失败：" + e.Message; return; }

            var api = FindObjectOfType<PetAPI>();
            var ctx = FindObjectOfType<PetContext>();
            var frame = FindObjectOfType<PetCameraFraming>();
            int n = 0;

            string s;
            if (api != null)
            {
                // ⚠️ Model / BaseUrl 不在这里处理了 —— 它们归 PetApiConfig + config.json 管。
                //    两个地方都能设同一个字段的话，加载顺序一变行为就飘，终端用户会彻底懵。
                if (d.TryGetValue("TimeoutSec", out s)) { api.timeoutSec = Mathf.Clamp(Mathf.RoundToInt(PetVisualRoot.ParseF(s, api.timeoutSec)), 5, 600); n++; }
                if (d.TryGetValue("NumCtx", out s)) { api.numCtx = Mathf.Clamp(Mathf.RoundToInt(PetVisualRoot.ParseF(s, api.numCtx)), 2048, 262144); n++; }
                if (d.TryGetValue("SendVision", out s)) { api.sendVision = PetVisualRoot.ParseF(s, 1f) > 0.5f; n++; }
                if (d.TryGetValue("PassiveLimit", out s)) { api.passiveLimit = Mathf.Max(1, Mathf.RoundToInt(PetVisualRoot.ParseF(s, api.passiveLimit))); n++; }
            }
            if (ctx != null)
            {
                if (d.TryGetValue("VisionEnabled", out s)) { ctx.visionEnabled = PetVisualRoot.ParseF(s, 1f) > 0.5f; n++; }
                if (d.TryGetValue("VisionEverySec", out s)) { ctx.visionEverySec = Mathf.Max(5f, PetVisualRoot.ParseF(s, ctx.visionEverySec)); n++; }
            }
            if (frame != null && d.TryGetValue("PetScreenHeightFraction", out s))
            {
                frame.petScreenHeightFraction = Mathf.Clamp(PetVisualRoot.ParseF(s, frame.petScreenHeightFraction), 0.02f, 1f);
                frame.Apply();      // 取景只在 Start 算一次，改完必须重算
                n++;
            }

            try { lastWriteTicks = File.GetLastWriteTimeUtc(p).ToFileTimeUtc(); } catch { }
            Status = "已套用 " + n + " 项应用级设置";
            Debug.Log("[PetAppSettings] ★ " + Status + " ← " + p);
        }
    }
}
