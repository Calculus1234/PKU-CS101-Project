using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 9 号房床上的「第九席」占位体。
    /// - 玩家扮演 1 号时，床上应该是活着的 9 号（由 NPCFactory 生成沉睡 NPC），
    ///   本对象隐藏，只有 9 号被杀后才重新显形为尸体；
    /// - 玩家扮演 2~8 号时，本对象就是 1 号在玩家醒来前留下的既定尸体；
    /// - 玩家扮演 1 号且手持武器时，对准沉睡的 9 号按鼠标左键执行（杀死 → 循环照常运转）；
    /// - E 键始终只做藏匿 / 环境交互，不会误触击杀；
    /// - 其余角色 / 无武器时，保留原有藏匿点功能（尸体身上）。
    /// </summary>
    public class NinthVictim : HidingSpot, IKillable, IHostileInteractable
    {
        public bool IsDead { get; private set; }

        /// <summary>床上的“尸体占位体”是否在场。9号活着醒来时必须隐藏，避免和活人重叠。</summary>
        public bool BodyPresent => gameObject.activeSelf;

        /// <summary>玩家当前能否动手：扮演 1 号 + 循环正常 + 尚未执行 + 手持武器 + 未死。</summary>
        public bool CanPlayerKill(PlayerController player)
        {
            return !IsDead
                && CycleState.playerNumber == 1
                && !CycleState.kill9Executed
                && CycleState.currentPhase == CyclePhase.Normal
                && player != null && player.inventory != null
                && player.inventory.GetLastWeapon() != null;
        }

        /// <summary>鼠标左键提示：只有玩家扮演 1 号、尚未执行、且手持武器时才显示击杀。</summary>
        public string GetHostilePrompt(PlayerController player)
        {
            if (!CanPlayerKill(player)) return "";

            ItemDefinition weapon = player.inventory.GetLastWeapon();
            return $"[左键] 用{weapon.displayName}杀死沉睡的9号";
        }

        /// <summary>
        /// 鼠标左键：执行对 9 号的袭击。
        /// E 键永远走基类藏匿点逻辑，不会因为手持武器就误杀。
        /// </summary>
        public bool TryHostileInteract(PlayerController player)
        {
            if (!CanPlayerKill(player)) return false;

            player.inventory.UseWeaponOn(this); // 按武器 killChance 掷骰（匕首必杀）
            return true;
        }

        // ------------------------------------------------------------------
        // IKillable（由 InventorySystem.UseWeaponOn 调用）
        // ------------------------------------------------------------------

        public void ApplyKill(PlayerController player, ItemDefinition weapon)
        {
            if (IsDead) return;
            IsDead = true;

            if (player != null)
                player.ShowMessage("9 号的呼吸停止了。这个世界照常运转——没有人知道发生过什么。");
            Die("玩家");
        }

        public void ApplyKillAttemptFailed(PlayerController player, ItemDefinition weapon)
        {
            if (player != null)
            {
                string weaponName = weapon != null ? weapon.displayName : "武器";
                player.ShowMessage($"{weaponName}没有击中要害！9 号惊喘了一声，翻了个身——他还活着。");
            }
        }

        /// <summary>1号 AI 流程（NPCAssassin）调用：执行既定事实中的致命一击。</summary>
        public void ExecuteByAssassin()
        {
            if (IsDead) return;
            IsDead = true;
            Die("1号 AI");
        }

        /// <summary>
        /// 错峰时间线：玩家扮演 2~8 号时，9 号在玩家醒来前就已被 1 号杀死。
        /// 静默结算：不广播 NPCDie（否则开局所有 NPC 会因距离判定陷入恐慌），
        /// 只写入循环状态并保留 Room_9 床上的尸体。
        /// </summary>
        public void ApplyBackstoryKill()
        {
            if (IsDead) return;
            IsDead = true;
            CycleState.NotifyNpcDeath(9);
            CycleState.NotifyKill9Executed();
            Debug.Log("[NinthVictim] 时间线既定：9号在玩家醒来前已被1号杀死（静默结算，不触发全场恐慌）。");
        }

        /// <summary>
        /// 8号AI 虚假认知驱动调用（3.2 修正一·情境B）：机器故障让它相信自己是"唯一杀手"，
        /// 它杀死 9号——循环随之恢复（人口回 9）。而它不知道，自己已悄然成为新的 9号。
        /// </summary>
        public void ExecuteByFalseCognition()
        {
            if (IsDead) return;
            IsDead = true;
            Die("8号AI（虚假认知）");
        }

        void Die(string executor)
        {
            CycleState.NotifyNpcDeath(9);
            NPCEventBus.RaiseNPCDie(9, transform.position, true);
            CycleState.NotifyKill9Executed();
            Debug.Log($"[NinthVictim] 9号死亡（执行者：{executor}）。尸体留在 Room_9 床上，房门保持反锁。");
        }

        /// <summary>
        /// 9号活着醒来：把床上的尸体占位体移出场景（不销毁，天亮还能恢复）。
        /// </summary>
        public void SetBodyPresent(bool present)
        {
            if (gameObject.activeSelf != present) gameObject.SetActive(present);
        }

        /// <summary>
        /// 沉睡的 9 号被杀死：让床上的尸体占位体重新显形。
        /// 死亡结算（死亡名册 / 杀9号标记）已经由 NPCController 完成，这里只负责视觉与交互。
        /// </summary>
        public void RevealAsCorpse()
        {
            IsDead = true;
            SetBodyPresent(true);
            Debug.Log("[NinthVictim] 9号在睡梦中被杀，床上的尸体重新显形。");
        }

        /// <summary>
        /// 3.5 天亮轮回：9 号尸体恢复初始状态。
        /// bodyPresent=false 用于“9号活着、10号在大厅”的破坏轮——这一轮床上没有尸体。
        /// </summary>
        public void ResetVictim(bool bodyPresent = true)
        {
            IsDead = false;
            SetBodyPresent(bodyPresent);

            // 尸体检查 / 摘面具的状态也要跟着轮回清空，否则上一轮检查过的尸体
            // 在下一轮会直接跳过“检查尸体”，面具也保持摘下状态。
            CorpseInspectable[] inspectables = GetComponentsInChildren<CorpseInspectable>(true);
            for (int i = 0; i < inspectables.Length; i++)
            {
                if (inspectables[i] != null) inspectables[i].ResetInspection();
            }

            Debug.Log(bodyPresent
                ? "[NinthVictim] 天亮重置：9 号尸体重新出现在 Room_9 床上（本轮既定受害者）。"
                : "[NinthVictim] 天亮重置：本轮 9 号活着，床上尸体占位体保持隐藏。");
        }
    }
}
