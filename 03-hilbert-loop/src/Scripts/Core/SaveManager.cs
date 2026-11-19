using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 轻量存档：JSON 存在 Application.persistentDataPath。
    ///
    /// 记录当前周目、玩家角色、存活名单、死亡名单与机器故障状态。
    /// - 天亮轮回完成时保存；
    /// - 玩家退出游戏时保存；
    /// - 主菜单推演每杀一个人，更新「菜单推演死亡」名单；
    /// - 「开始 / 再来一局」会删除存档，「继续」从存档恢复。
    /// </summary>
    public static class SaveManager
    {
        [Serializable]
        public class SaveData
        {
            public int version = 1;
            public int loopIndex;
            public int playerNumber;
            public bool playerAlive = true;
            public bool playerMemoryRetained;
            public bool machineFaultUsed;
            public int population;
            public int knownMax = 9;
            public string phase = "Investigation";
            public float phaseRemaining;
            public List<int> aliveNumbers = new List<int>();
            public List<int> deadNumbers = new List<int>();
            public List<int> menuDeductionDead = new List<int>();
            public bool hasRun;
            public string reason = "auto";
            public long savedAtUnix;
        }

        const string FileName = "hilbert_loop_save.json";

        static SaveData _cached;
        static bool _pendingRestore;

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);
        public static bool HasSave => File.Exists(FilePath);
        public static bool PendingRestore => _pendingRestore;
        public static SaveData Cached => _cached;

        /// <summary>是否有一局真正开始过、且玩家仍存活的存档可继续。</summary>
        public static bool CanContinue
        {
            get
            {
                SaveData data = Load();
                return data != null && data.hasRun && data.playerAlive;
            }
        }

        public static bool RequestRestore()
        {
            _cached = Load();
            _pendingRestore = _cached != null && _cached.hasRun && _cached.playerAlive;
            if (!_pendingRestore)
            {
                Debug.LogWarning("[SaveManager] 存档不存在或玩家已死亡，无法继续。");
            }
            return _pendingRestore;
        }

        public static void CancelRestore()
        {
            _pendingRestore = false;
            _cached = null;
        }

        public static SaveData Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string json = File.ReadAllText(FilePath);
                SaveData data = JsonUtility.FromJson<SaveData>(json);
                if (data == null) return null;
                if (data.aliveNumbers == null) data.aliveNumbers = new List<int>();
                if (data.deadNumbers == null) data.deadNumbers = new List<int>();
                if (data.menuDeductionDead == null) data.menuDeductionDead = new List<int>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveManager] 读取存档失败：{e.Message}");
                return null;
            }
        }

        /// <summary>保存当前游戏状态（天亮 / 退出时调用）。</summary>
        public static void Save(string reason)
        {
            if (GameMode.MenuActive) return;

            SaveData data = Load() ?? new SaveData();
            data.loopIndex = CycleState.loopIndex;
            data.playerNumber = CycleState.playerNumber;
            data.playerAlive = CycleState.playerAlive;
            data.playerMemoryRetained = CycleState.playerMemoryRetained;
            data.machineFaultUsed = CycleState.machineFaultUsed;
            data.population = CycleState.currentPopulation;
            data.knownMax = CycleState.currentKnownMax;
            data.phase = GameManager.Instance != null
                ? GameManager.Instance.CurrentPhase.ToString()
                : CyclePhase.Normal.ToString();
            data.phaseRemaining = GameManager.Instance != null
                ? GameManager.Instance.PhaseTimeRemaining
                : 0f;
            data.aliveNumbers = CaptureAliveNumbers();
            data.deadNumbers = new List<int>(CycleState.DeadNumbers);
            data.menuDeductionDead.Clear();
            data.hasRun = true;
            data.reason = reason ?? "auto";
            data.savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Write(data);
            Debug.Log($"[SaveManager] 已保存（{data.reason}）：第 {data.loopIndex} 周目，" +
                      $"玩家 {data.playerNumber} 号，存活 {string.Join("、", data.aliveNumbers)}。");
        }

        static List<int> CaptureAliveNumbers()
        {
            var alive = new List<int>();
            if (CycleState.playerAlive && CycleState.playerNumber > 0)
                alive.Add(CycleState.playerNumber);

            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && !alive.Contains(npc.Number))
                    alive.Add(npc.Number);
            }

            // 9号的尸体是剧本道具，不是活人；不要混进存活名单。
            alive.RemoveAll(n => n < 1 || n > 9);
            alive.Sort();
            return alive;
        }

        /// <summary>
        /// 主菜单推演名册：永远展示完整的 1~9。
        ///
        /// 主菜单演示的是「循环本身」，不是某一局存档：9 号每晚都被 1 号杀死，
        /// 天亮后所有人号码 +1、新的 1 号补入，名册仍然是 1~9。
        /// 因此这里不读取存档、也不做“出局 / 缺席”的减法。
        /// </summary>
        public static List<int> GetMenuRoster()
        {
            var roster = new List<int>();
            for (int n = 1; n <= CycleState.MaxLoopNumber; n++) roster.Add(n);
            return roster;
        }

        /// <summary>菜单推演死一个人：记录到存档的菜单死亡名单。</summary>
        public static void RecordMenuVictim(int number)
        {
            if (number <= 0) return;
            SaveData data = Load() ?? new SaveData();
            if (!data.menuDeductionDead.Contains(number))
                data.menuDeductionDead.Add(number);
            data.reason = "menu-deduction";
            data.savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Write(data);
        }

        /// <summary>菜单推演循环闭合：清空菜单死亡名单，重新开始展示完整循环。</summary>
        public static void ResetMenuDeduction()
        {
            SaveData data = Load();
            if (data == null) return;
            data.menuDeductionDead.Clear();
            data.reason = "menu-cycle-reset";
            data.savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Write(data);
        }

        /// <summary>供 NPCFactory 在天亮恢复时使用：返回要生成的 NPC 号码（不含玩家）。</summary>
        public static bool TryConsumePendingRoster(out List<int> roster)
        {
            roster = null;
            if (!_pendingRestore || _cached == null) return false;

            roster = new List<int>();
            for (int i = 0; i < _cached.aliveNumbers.Count; i++)
            {
                int number = _cached.aliveNumbers[i];
                if (number < 1 || number > 9) continue;
                if (number == CycleState.playerNumber) continue;
                roster.Add(number);
            }
            roster.Sort();
            _pendingRestore = false;
            Debug.Log($"[SaveManager] 继续存档：NPC 名册 {string.Join("、", roster)}，" +
                      $"玩家 {CycleState.playerNumber} 号。");
            return true;
        }

        public static void ApplyCachedToCycleState()
        {
            if (_cached == null) return;
            CycleState.RestoreFromSave(
                _cached.loopIndex,
                _cached.playerNumber,
                _cached.playerAlive,
                _cached.playerMemoryRetained,
                _cached.machineFaultUsed,
                _cached.population,
                _cached.knownMax,
                _cached.aliveNumbers,
                _cached.deadNumbers);
        }

        public static void DeleteSave()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                _cached = null;
                _pendingRestore = false;
                Debug.Log("[SaveManager] 本地存档已清除。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveManager] 清除存档失败：{e.Message}");
            }
        }

        static void Write(SaveData data)
        {
            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(data, true));
                if (File.Exists(FilePath)) File.Copy(temp, FilePath, true);
                else File.Move(temp, FilePath);
                if (File.Exists(temp)) File.Delete(temp);
                _cached = data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveManager] 写入存档失败：{e.Message}");
            }
        }
    }

    /// <summary>退出游戏时保存一次。场景加载后自动创建，菜单模式不挂。</summary>
    public class SaveOnQuit : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (GameMode.MenuActive) return;
            if (FindObjectOfType<SaveOnQuit>() != null) return;
            GameObject go = new GameObject("SaveOnQuit");
            go.AddComponent<SaveOnQuit>();
        }

        void OnApplicationQuit()
        {
            if (!GameMode.MenuActive)
                SaveManager.Save("quit");
        }
    }
}
