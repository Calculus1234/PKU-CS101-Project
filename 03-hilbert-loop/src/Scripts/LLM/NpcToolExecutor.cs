using System;
using UnityEngine;

namespace HilbertLoop
{
    public class NpcToolExecutionResult
    {
        public string extraLine = "";
        public bool presentedEvidence;
        public int voteTarget;
    }

    /// <summary>
    /// 把 LLM 的工具调用落到真实游戏状态：
    ///  - ProvideRebuttal：台词由调用方播放（本类不重复处理）
    ///  - PresentEvidence：从 NPC 确实知道的线索里取一条摆上桌
    ///  - CastVote：写 voteIntentNumber，投票系统会优先采用
    ///  - ChangeEmotion：播放发抖 / 后退 / 愤怒的短演出
    ///  - ChangeStat：写 NPCBlackboard 的逐号码怀疑/敌意，并影响后续投票
    /// </summary>
    public static class NpcToolExecutor
    {
        public static NpcToolExecutionResult Execute(NPCController npc, NpcLLMReply reply, NpcLLMScene scene)
        {
            var result = new NpcToolExecutionResult();
            if (npc == null || npc.blackboard == null || reply == null) return result;

            NPCBlackboard board = npc.blackboard;

            NpcToolCall evidenceCall = reply.GetCall("PresentEvidence");
            if (evidenceCall != null)
            {
                string line = BuildEvidenceLine(npc, evidenceCall.evidenceId);
                if (!string.IsNullOrEmpty(line))
                {
                    result.extraLine = line;
                    result.presentedEvidence = true;
                }
            }

            NpcToolCall voteCall = reply.GetCall("CastVote");
            if (voteCall != null && voteCall.targetNumber > 0
                && voteCall.targetNumber != npc.Number
                && IsAliveNumber(voteCall.targetNumber))
            {
                board.voteIntentNumber = voteCall.targetNumber;
                board.meetingAccusedNumber = voteCall.targetNumber;
                result.voteTarget = voteCall.targetNumber;
                Debug.Log($"[LLM工具] {npc.Number}号决定投 {voteCall.targetNumber}号。");
            }

            NpcToolCall emotionCall = reply.GetCall("ChangeEmotion");
            if (emotionCall != null && !string.IsNullOrEmpty(emotionCall.emotion))
            {
                NpcEmotionPlayer.Play(npc, emotionCall.emotion);
                ApplyEmotionStats(board, emotionCall.emotion);
            }

            NpcToolCall statCall = reply.GetCall("ChangeStat");
            if (statCall != null)
            {
                // 4B 经常只写 suspicion/hostility 而漏掉 target。
                // 私聊里只有一个对象，可以安全地按玩家结算；会议里对象不唯一，仍然忽略。
                int statTarget = statCall.targetNumber;
                if (statTarget <= 0 && scene == NpcLLMScene.PrivateChat)
                    statTarget = CycleState.playerNumber;

                if (statTarget > 0)
                {
                    ApplyStat(npc, board, statTarget,
                        statCall.suspicionDelta, statCall.hostilityDelta);
                }
            }

            return result;
        }

        static void ApplyStat(NPCController npc, NPCBlackboard board, int target,
            int suspicionDelta, int hostilityDelta)
        {
            board.AddSuspicionToward(target, suspicionDelta);
            board.AddHostilityToward(target, hostilityDelta);

            if (suspicionDelta > 0) board.AddSuspect(target);
            else if (suspicionDelta < 0) board.RemoveSuspect(target);

            if (target == CycleState.playerNumber)
            {
                // 对玩家的怀疑会直接抬场上压力；敌意足够高时，NPC 真的会动手。
                board.suspicion = Mathf.Clamp(board.suspicion + suspicionDelta, 0f, 100f);
                board.AddTrust(target, -suspicionDelta * 0.4f);

                if (hostilityDelta != 0)
                {
                    board.wantsPlayerDead = board.GetHostilityToward(target) >= 70f;
                    board.hostileSince = board.wantsPlayerDead ? Time.time : 0f;
                }
            }
            else if (hostilityDelta > 0)
            {
                // 对同类的敌意先转化为“会议里针对他”，不直接触发互相残杀。
                board.AddSuspectFront(target);
            }

            Debug.Log($"[LLM工具] {npc.Number}号 → {target}号：怀疑{suspicionDelta:+#;-#;0}，敌意{hostilityDelta:+#;-#;0}");
        }

        static void ApplyEmotionStats(NPCBlackboard board, string emotion)
        {
            switch ((emotion ?? "").Trim().ToLowerInvariant())
            {
                case "tremble": board.stress = Mathf.Min(100f, board.stress + 6f); break;
                case "retreat": board.stress = Mathf.Min(100f, board.stress + 4f);
                    board.suspicion = Mathf.Min(100f, board.suspicion + 2f); break;
                case "angry": board.aggression = Mathf.Min(100f, board.aggression + 8f);
                    board.stress = Mathf.Min(100f, board.stress + 4f); break;
                case "calm": board.stress = Mathf.Max(0f, board.stress - 4f); break;
            }
        }

        static string BuildEvidenceLine(NPCController npc, string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return "";

            ItemDefinition def = ResolveItem(idOrName);
            string id = def != null ? def.id : idOrName.Trim();
            string name = def != null ? def.displayName : EvidenceLedger.GetDisplayName(id);
            if (string.IsNullOrEmpty(name)) name = idOrName.Trim();

            if (!CanNpcCite(npc, id, name)) return "";

            string reason = def != null ? def.evidenceReason : "";
            if (string.IsNullOrEmpty(reason) && EvidenceLedger.Has(id))
            {
                var entries = EvidenceLedger.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null && entries[i].id == id)
                    {
                        reason = entries[i].reason;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(reason))
                return $"（他把「{name}」推到桌上）这件事我早就想说。";
            return $"（他把「{name}」推到桌上）{reason}";
        }

        static ItemDefinition ResolveItem(string idOrName)
        {
            ItemDatabase db = ItemDatabase.Instance;
            if (db == null) return null;

            ItemDefinition def = db.Get(idOrName);
            if (def != null) return def;

            if (db.items != null)
            {
                for (int i = 0; i < db.items.Count; i++)
                {
                    ItemDefinition item = db.items[i];
                    if (item == null) continue;
                    if (string.Equals(item.displayName, idOrName, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
            }
            return null;
        }

        static bool CanNpcCite(NPCController npc, string id, string name)
        {
            NPCBlackboard board = npc.blackboard;

            if (board.knownClues != null)
            {
                for (int i = 0; i < board.knownClues.Count; i++)
                {
                    string clue = board.knownClues[i];
                    if (string.IsNullOrEmpty(clue)) continue;
                    if (clue.Contains(id) || (!string.IsNullOrEmpty(name) && clue.Contains(name)))
                        return true;
                }
            }

            // 1号对自己做过的事有第一手认知。
            if (npc.Number == 1 && board.selfHasKilled)
            {
                string lower = (id ?? "").ToLowerInvariant();
                if (lower.Contains("nail") || lower.Contains("magnet")
                    || lower.Contains("window") || lower.Contains("9"))
                    return true;
            }

            // 已经在会议/私聊里公开过的证据，NPC 可以引用。
            return EvidenceLedger.Has(id);
        }

        static bool IsAliveNumber(int number)
        {
            if (number <= 0) return false;
            if (number == CycleState.playerNumber) return CycleState.playerAlive;
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && npc.Number == number && !npc.isDead) return true;
            }
            return false;
        }
    }
}
