using System;
using System.Collections.Generic;
using System.Text;

namespace PokerSolver
{
    /// <summary>
    /// 残局定式库 + 局面特征分析。
    ///
    /// 背景：网上没有公开的「斗地主残局定式表」——牌型组合空间太大，人类总结的技巧
    /// 全是启发式，无法保证正确。所以这里只做两件事：
    ///
    /// 1. **严格定式**：只收录可以证明的判定（一手清台、残局锁、必死锁），
    ///    命中后可以跳过搜索直接给结论。
    /// 2. **特征分析**：不给结论，只给出人类看局面时的关键指标
    ///    （张数差、炸弹/王炸、大牌数、最少手数），帮助理解「为什么赢/输」。
    /// </summary>
    public static class EndgameBook
    {
        /// <summary>定式命中结果。</summary>
        public struct Verdict
        {
            public bool Hit;          // 是否命中严格定式（可以直接返回结论）
            public bool Win;          // 命中时的结论：我方能否赢
            public Move First;        // 命中时的首手
            public string Name;       // 定式名称
            public string Detail;     // 一句话说明
        }

        /// <summary>局面特征（给人类看的，不是结论）。</summary>
        public struct Profile
        {
            public int MyCards;
            public int OppCards;
            public int MyBombs;        // 含王炸
            public int OppBombs;
            public bool MyRocket;      // 王炸
            public bool OppRocket;
            public int MyBigCards;     // 2 + 大小王
            public int OppBigCards;
            public int MyHands;        // 最少几手出完
            public int OppHands;
            public bool ILead;
        }

        // ---------------------------------------------------------------- 特征

        public static Profile Analyze(ulong my, ulong opp, bool iLead)
        {
            var p = new Profile();
            p.MyCards = CardUtil.TotalCount(my);
            p.OppCards = CardUtil.TotalCount(opp);
            p.ILead = iLead;

            int[] cm = new int[CardUtil.RankCount];
            int[] co = new int[CardUtil.RankCount];
            CardUtil.ToCounts(my, cm);
            CardUtil.ToCounts(opp, co);

            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (cm[r] == 4) p.MyBombs++;
                if (co[r] == 4) p.OppBombs++;
            }
            p.MyRocket = cm[CardUtil.SmallJoker] >= 1 && cm[CardUtil.BigJoker] >= 1;
            p.OppRocket = co[CardUtil.SmallJoker] >= 1 && co[CardUtil.BigJoker] >= 1;

            // 大牌：2 和大小王（2 的点数索引是 12）
            p.MyBigCards = cm[12] + cm[CardUtil.SmallJoker] + cm[CardUtil.BigJoker];
            p.OppBigCards = co[12] + co[CardUtil.SmallJoker] + co[CardUtil.BigJoker];

            p.MyHands = HandEval.MinHands(my);
            p.OppHands = HandEval.MinHands(opp);
            return p;
        }

        /// <summary>把特征格式化成几行文字。</summary>
        public static string Describe(Profile p)
        {
            var sb = new StringBuilder();
            sb.Append("张数 ").Append(p.MyCards).Append(" vs ").Append(p.OppCards).Append("　");
            sb.Append("最少手数 ").Append(p.MyHands).Append(" vs ").Append(p.OppHands).Append("\n");

            sb.Append("炸弹 ").Append(p.MyBombs).Append(" vs ").Append(p.OppBombs);
            if (p.MyRocket) sb.Append("（我方王炸）");
            if (p.OppRocket) sb.Append("（对方王炸）");
            sb.Append("　大牌(2/王) ").Append(p.MyBigCards).Append(" vs ").Append(p.OppBigCards).Append("\n");

            // 一句倾向性描述（不是结论）
            int diff = p.MyHands - p.OppHands;
            if (diff <= -2) sb.Append("手数上我方明显占优");
            else if (diff >= 2) sb.Append("手数上对方明显占优");
            else sb.Append("手数接近");

            if (p.MyBombs > p.OppBombs) sb.Append("，炸弹占优");
            else if (p.MyBombs < p.OppBombs) sb.Append("，炸弹劣势");

            if (p.MyBigCards > p.OppBigCards) sb.Append("，大牌更多");
            else if (p.MyBigCards < p.OppBigCards) sb.Append("，大牌更少");

            return sb.ToString();
        }

        // ---------------------------------------------------------------- 严格定式

        /// <summary>
        /// 跑一遍严格定式。命中就返回结论；没命中返回 Hit=false，交给搜索。
        /// 注意：这些判定都是可证明的，不含任何启发式猜测。
        /// </summary>
        public static Verdict Judge(ulong my, ulong opp, Move prev)
        {
            var v = new Verdict();

            // 空手牌不应该走到这里，兜底
            if (my == 0UL) { v.Hit = true; v.Win = true; v.Name = "D0 已出完"; return v; }
            if (opp == 0UL) { v.Hit = true; v.Win = false; v.Name = "D0 对方已出完"; return v; }

            // 只处理「自由出牌」或「我方必须压」的根局面；台面上有牌时交给搜索更稳
            // （因为「过」的分支会让定式变复杂）

            // D1：我方手牌本身就是一个合法牌型 → 一手出完
            if (prev.IsPass && IsSingleHand(my, out Move whole))
            {
                v.Hit = true;
                v.Win = true;
                v.First = whole;
                v.Name = "D1 一手清台";
                v.Detail = "我方全部手牌构成一个合法牌型，直接出完获胜。";
                return v;
            }

            // D2：我方剩 1 手，且对手压不住这一手 → 我方连续走完
            //     成立条件：MinHands(my)==1（已由 D1 覆盖）——所以这里处理的是
            //     「我先出一手，剩下的能一手清台，且对手压不住我这一手」。
            if (prev.IsPass)
            {
                var probe = new List<Move>(64);
                var all = new List<Move>(128);
                MoveGen.GenerateAll(my, all);
                for (int i = 0; i < all.Count; i++)
                {
                    Move m = all[i];
                    if (m.Cards == my) continue;                 // D1 已覆盖
                    if (HandEval.MinHands(my - m.Cards) != 1) continue;  // 出完剩 1 手

                    probe.Clear();
                    Move mm = m;
                    MoveGen.GenerateBeats(opp, in mm, probe);
                    if (probe.Count == 0)
                    {
                        v.Hit = true;
                        v.Win = true;
                        v.First = m;
                        v.Name = "D2 残局锁";
                        v.Detail = "出这一手后对手压不住，我方再一手清台。";
                        return v;
                    }
                }
            }

            return v;
        }

        private static bool IsSingleHand(ulong hand, out Move move)
        {
            move = Move.Pass;
            if (hand == 0UL) return false;

            // 用走法生成器验证：如果生成列表里有 Cards == hand，说明整手是一个合法牌型
            var list = new List<Move>(64);
            MoveGen.GenerateAll(hand, list);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Cards == hand) { move = list[i]; return true; }
            }
            return false;
        }
    }
}
