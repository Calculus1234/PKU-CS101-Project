using UnityEngine;
using UnityEngine.SceneManagement;

namespace HilbertLoop
{
    /// <summary>
    /// 启动模式：主菜单 / 游戏。
    ///
    /// 不再新建场景，也不复制 MainScene。主菜单与游戏共用同一个场景，
    /// 通过 PlayerPrefs 的启动标记决定进入后由谁接管：
    ///   - Menu：只保留建筑、监控相机与行为树 NPC，不加载玩家、HUD 与 LLM；
    ///   - Play：走原有的完整游戏流程。
    ///
    /// 开始 / 继续 / 再来一局 / 返回主菜单都会重载 MainScene，这是唯一允许的
    /// 场景切换；主菜单内部的推演循环绝不切场景、不黑屏。
    /// </summary>
    public static class GameMode
    {
        const string BootKey = "HL_Boot_Mode";           // "menu" / "play"
        const string ContinueKey = "HL_Continue_Request";

        public static readonly string MainSceneName = "MainScene";

        static bool _menuActive;
        static bool _continueRequested;

        /// <summary>当前是否处于主菜单模式。所有游戏系统在 Start 时必须先看这个标记。</summary>
        public static bool MenuActive => _menuActive;

        /// <summary>本次进入场景是否要恢复存档（主菜单「继续」）。</summary>
        public static bool ContinueRequested => _continueRequested;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            // 每次启动游戏都必须先看到主菜单。
            // PlayerPrefs 里的 "play" 只在同一次运行、场景内部重载时有效，
            // 不能让上一局留下的标记把下一次启动直接送进游戏。
            _menuActive = true;
            _continueRequested = false;
            PlayerPrefs.SetString(BootKey, "menu");
            PlayerPrefs.SetInt(ContinueKey, 0);
            PlayerPrefs.Save();
        }

        public static void StartNewGame()
        {
            SaveManager.DeleteSave();
            SetBoot(false, false);
            ReloadMainScene();
        }

        public static void ContinueGame()
        {
            if (!SaveManager.CanContinue)
            {
                Debug.LogWarning("[GameMode] 没有可继续的存档。");
                return;
            }
            if (!SaveManager.RequestRestore()) return;
            SetBoot(false, true);
            ReloadMainScene();
        }

        public static void ReturnToMenu()
        {
            SaveManager.CancelRestore();
            SetBoot(true, false);
            ReloadMainScene();
        }

        /// <summary>结局面板「再来一局」：清除存档，以默认角色重新开始。</summary>
        public static void RestartNewGame()
        {
            SaveManager.DeleteSave();
            SetBoot(false, false);
            ReloadMainScene();
        }

        /// <summary>开发 / 调试：直接以游戏模式重载场景。</summary>
        public static void EnterPlayMode()
        {
            SetBoot(false, false);
            ReloadMainScene();
        }

        static void SetBoot(bool menu, bool continueRequested)
        {
            _menuActive = menu;
            _continueRequested = continueRequested;
            PlayerPrefs.SetString(BootKey, menu ? "menu" : "play");
            PlayerPrefs.SetInt(ContinueKey, continueRequested ? 1 : 0);
            PlayerPrefs.Save();
        }

        static void ReloadMainScene()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (Application.CanStreamedLevelBeLoaded(MainSceneName))
                SceneManager.LoadScene(MainSceneName);
            else
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
