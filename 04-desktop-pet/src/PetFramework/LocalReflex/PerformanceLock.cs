using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 核显保命底线：锁定帧率 30、关闭垂直同步，避免高刷屏把核显风扇拉满。
    /// 挂在主摄像机上，随场景启动即执行。
    /// </summary>
    public class PerformanceLock : MonoBehaviour
    {
        void Awake()
        {
            Application.targetFrameRate = 30;
            QualitySettings.vSyncCount = 0;
        }
    }
}
