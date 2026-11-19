using System;
using System.Collections.Generic;

namespace PokerSolver
{
    public enum MoveType : byte
    {
        None = 0,
        Single = 1,
        Pair = 2,
        Triple = 3,
        TripleOne = 4,
        TripleTwo = 5,
        Straight = 6,
        DoubleStraight = 7,
        Plane = 8,
        PlaneOne = 9,
        PlaneTwo = 10,
        FourTwo = 11,
        FourTwoPair = 12,
        Bomb = 13,
        Rocket = 14
    }

    public struct Move
    {
        public MoveType Type;
        public int Rank;     // 比较用的主牌（顺子/连对/飞机取最小那张）
        public int Length;   // 连张节数；非连牌为 1
        public ulong Cards;  // 实际牌（与手牌同格式）
        public byte RestHands; // 排序用的临时字段：出完这手后剩余最少手数
        /// <summary>
        /// 这手牌的张数，生成时算好缓存。
        /// 排序比较会被调用上亿次，每次现算 SWAR 折叠是排序的主要开销之一。
        /// </summary>
        public byte CardCount;

        public static Move Pass
        {
            get { return default(Move); }
        }

        public bool IsPass
        {
            get { return Type == MoveType.None; }
        }
    }

    public static class MoveUtil
    {
        public static readonly string[] TypeNames =
        {
            "过", "单张", "对子", "三张", "三带一", "三带二",
            "顺子", "连对", "飞机", "飞机带单", "飞机带对",
            "四带二", "四带两对", "炸弹", "王炸"
        };

        public static bool CanBeat(in Move a, in Move b)
        {
            if (a.Type == MoveType.Rocket) return true;
            if (b.Type == MoveType.Rocket) return false;
            if (a.Type == MoveType.Bomb && b.Type != MoveType.Bomb) return true;
            if (b.Type == MoveType.Bomb && a.Type != MoveType.Bomb) return false;
            if (a.Type != b.Type) return false;
            if (a.Length != b.Length) return false;
            return a.Rank > b.Rank;
        }

        public static string Describe(in Move m)
        {
            if (m.IsPass) return "过";
            string cards = CardUtil.ToDisplayString(m.Cards);
            if (m.Type == MoveType.Single || m.Type == MoveType.Pair ||
                m.Type == MoveType.Triple || m.Type == MoveType.Bomb ||
                m.Type == MoveType.Rocket || m.Type == MoveType.Straight ||
                m.Type == MoveType.DoubleStraight || m.Type == MoveType.Plane)
                return cards;
            return TypeNames[(int)m.Type] + " " + cards;
        }
    }

    public static class MoveGen
    {
        [ThreadStatic] private static int[] _countsBuffer;
        [ThreadStatic] private static int[] _restBuffer;

        private static int[] CountsBuffer()
        {
            if (_countsBuffer == null) _countsBuffer = new int[CardUtil.RankCount];
            return _countsBuffer;
        }

        private static int[] RestBuffer()
        {
            if (_restBuffer == null) _restBuffer = new int[CardUtil.RankCount];
            return _restBuffer;
        }

        private static Move Mk(MoveType type, int rank, int length, ulong cards)
        {
            return new Move { Type = type, Rank = rank, Length = length, Cards = cards };
        }

        /// <summary>生成手牌能打出的所有牌型（自由出牌时用）。</summary>
        public static void GenerateAll(ulong hand, List<Move> output)
        {
            output.Clear();

            if (hand == 0UL) return;

            int[] counts = CountsBuffer();
            CardUtil.ToCounts(hand, counts);

            // 基础牌型：单张 / 对子 / 三张 / 炸弹
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                int c = counts[r];
                if (c >= 1) output.Add(Mk(MoveType.Single, r, 1, CardUtil.MakeCards(r, 1)));
                if (c >= 2) output.Add(Mk(MoveType.Pair, r, 1, CardUtil.MakeCards(r, 2)));
                if (c >= 3) output.Add(Mk(MoveType.Triple, r, 1, CardUtil.MakeCards(r, 3)));
                if (c >= 4) output.Add(Mk(MoveType.Bomb, r, 1, CardUtil.MakeCards(r, 4)));
            }

            // 王炸
            if (counts[CardUtil.SmallJoker] >= 1 && counts[CardUtil.BigJoker] >= 1)
                output.Add(Mk(MoveType.Rocket, CardUtil.BigJoker, 1,
                    CardUtil.MakeCards(CardUtil.SmallJoker, 1) + CardUtil.MakeCards(CardUtil.BigJoker, 1)));

            // 三带一 / 三带二
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (counts[r] < 3) continue;
                ulong triple = CardUtil.MakeCards(r, 3);

                for (int r2 = 0; r2 < CardUtil.RankCount; r2++)
                {
                    if (r2 == r || counts[r2] < 1) continue;
                    output.Add(Mk(MoveType.TripleOne, r, 1, triple + CardUtil.MakeCards(r2, 1)));
                }

                for (int r2 = 0; r2 < CardUtil.RankCount; r2++)
                {
                    if (r2 == r || counts[r2] < 2) continue;
                    output.Add(Mk(MoveType.TripleTwo, r, 1, triple + CardUtil.MakeCards(r2, 2)));
                }
            }

            // 顺子（至少 5 张，最高到 A）
            int r0 = 0;
            while (r0 <= CardUtil.StraightMaxRank)
            {
                if (counts[r0] == 0) { r0++; continue; }
                int s = r0;
                while (r0 + 1 <= CardUtil.StraightMaxRank && counts[r0 + 1] > 0) r0++;
                int e = r0;

                for (int start = s; start <= e; start++)
                {
                    int maxLen = e - start + 1;
                    for (int len = 5; len <= maxLen; len++)
                        output.Add(Mk(MoveType.Straight, start, len, CardUtil.RangeCards(start, len)));
                }
                r0++;
            }

            // 连对（至少 3 对，最高到 A）
            r0 = 0;
            while (r0 <= CardUtil.StraightMaxRank)
            {
                if (counts[r0] < 2) { r0++; continue; }
                int s = r0;
                while (r0 + 1 <= CardUtil.StraightMaxRank && counts[r0 + 1] >= 2) r0++;
                int e = r0;

                for (int start = s; start <= e; start++)
                {
                    int maxLen = e - start + 1;
                    for (int len = 3; len <= maxLen; len++)
                    {
                        ulong cards = 0UL;
                        for (int k = 0; k < len; k++) cards |= CardUtil.MakeCards(start + k, 2);
                        output.Add(Mk(MoveType.DoubleStraight, start, len, cards));
                    }
                }
                r0++;
            }

            // 飞机（至少 2 连三张，最高到 A），可带单、可带对
            r0 = 0;
            while (r0 <= CardUtil.StraightMaxRank)
            {
                if (counts[r0] < 3) { r0++; continue; }
                int s = r0;
                while (r0 + 1 <= CardUtil.StraightMaxRank && counts[r0 + 1] >= 3) r0++;
                int e = r0;

                for (int start = s; start <= e; start++)
                {
                    int maxLen = e - start + 1;
                    for (int len = 2; len <= maxLen; len++)
                    {
                        ulong body = 0UL;
                        for (int k = 0; k < len; k++) body |= CardUtil.MakeCards(start + k, 3);

                        output.Add(Mk(MoveType.Plane, start, len, body));

                        ulong rest = hand - body;
                        int[] restCounts = RestBuffer();
                        CardUtil.ToCounts(rest, restCounts);

                        EnumerateSingleWings(restCounts, 0, len, 0UL, body, start, len, MoveType.PlaneOne, output);
                        EnumeratePairWings(restCounts, 0, len, 0UL, body, start, len, MoveType.PlaneTwo, output);
                    }
                }
                r0++;
            }

            // 四带二 / 四带两对
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (counts[r] < 4) continue;
                ulong four = CardUtil.MakeCards(r, 4);
                ulong rest = hand - four;
                int[] restCounts = RestBuffer();
                CardUtil.ToCounts(rest, restCounts);

                EnumerateSingleWings(restCounts, 0, 2, 0UL, four, r, 1, MoveType.FourTwo, output);
                EnumeratePairWings(restCounts, 0, 2, 0UL, four, r, 1, MoveType.FourTwoPair, output);
            }
        }

        /// <summary>
        /// 只生成能压过 prev 的牌型。
        /// 按牌型直接生成，而不是「生成全部再过滤」——这是快慢的分水岭。
        /// 输出顺序：同型从小往大，炸弹/王炸放最后（尽量保留控制牌）。
        /// </summary>
        public static void GenerateBeats(ulong hand, in Move prev, List<Move> output)
        {
            output.Clear();
            if (hand == 0UL) return;
            if (prev.Type == MoveType.Rocket) return;

            int[] counts = CountsBuffer();
            CardUtil.ToCounts(hand, counts);

            switch (prev.Type)
            {
                case MoveType.Single:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                        if (counts[r] >= 1) output.Add(Mk(MoveType.Single, r, 1, CardUtil.MakeCards(r, 1)));
                    break;

                case MoveType.Pair:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                        if (counts[r] >= 2) output.Add(Mk(MoveType.Pair, r, 1, CardUtil.MakeCards(r, 2)));
                    break;

                case MoveType.Triple:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                        if (counts[r] >= 3) output.Add(Mk(MoveType.Triple, r, 1, CardUtil.MakeCards(r, 3)));
                    break;

                case MoveType.TripleOne:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                    {
                        if (counts[r] < 3) continue;
                        ulong triple = CardUtil.MakeCards(r, 3);
                        for (int r2 = 0; r2 < CardUtil.RankCount; r2++)
                        {
                            if (r2 == r || counts[r2] < 1) continue;
                            output.Add(Mk(MoveType.TripleOne, r, 1, triple + CardUtil.MakeCards(r2, 1)));
                        }
                    }
                    break;

                case MoveType.TripleTwo:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                    {
                        if (counts[r] < 3) continue;
                        ulong triple = CardUtil.MakeCards(r, 3);
                        for (int r2 = 0; r2 < CardUtil.RankCount; r2++)
                        {
                            if (r2 == r || counts[r2] < 2) continue;
                            output.Add(Mk(MoveType.TripleTwo, r, 1, triple + CardUtil.MakeCards(r2, 2)));
                        }
                    }
                    break;

                case MoveType.Straight:
                    AddStraightBeats(counts, prev, output);
                    break;

                case MoveType.DoubleStraight:
                    AddDoubleStraightBeats(counts, prev, output);
                    break;

                case MoveType.Plane:
                    AddPlaneBeats(hand, counts, prev, output);
                    break;

                case MoveType.PlaneOne:
                    AddPlaneWingBeats(hand, counts, prev, true, output);
                    break;

                case MoveType.PlaneTwo:
                    AddPlaneWingBeats(hand, counts, prev, false, output);
                    break;

                case MoveType.FourTwo:
                    AddFourWithBeats(counts, prev, true, output);
                    break;

                case MoveType.FourTwoPair:
                    AddFourWithBeats(counts, prev, false, output);
                    break;

                case MoveType.Bomb:
                    for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
                        if (counts[r] == 4) output.Add(Mk(MoveType.Bomb, r, 1, CardUtil.MakeCards(r, 4)));
                    if (counts[CardUtil.SmallJoker] >= 1 && counts[CardUtil.BigJoker] >= 1)
                        output.Add(Mk(MoveType.Rocket, CardUtil.BigJoker, 1,
                            CardUtil.MakeCards(CardUtil.SmallJoker, 1) + CardUtil.MakeCards(CardUtil.BigJoker, 1)));
                    return; // 炸弹分支不需要再附加炸弹
            }

            // 任何非炸弹牌型都能被炸弹/王炸压
            for (int r = 0; r < CardUtil.RankCount; r++)
                if (counts[r] == 4) output.Add(Mk(MoveType.Bomb, r, 1, CardUtil.MakeCards(r, 4)));

            if (counts[CardUtil.SmallJoker] >= 1 && counts[CardUtil.BigJoker] >= 1)
                output.Add(Mk(MoveType.Rocket, CardUtil.BigJoker, 1,
                    CardUtil.MakeCards(CardUtil.SmallJoker, 1) + CardUtil.MakeCards(CardUtil.BigJoker, 1)));
        }

        private static void AddStraightBeats(int[] counts, in Move prev, List<Move> output)
        {
            int len = prev.Length;
            for (int start = prev.Rank + 1; start + len - 1 <= CardUtil.StraightMaxRank; start++)
            {
                bool ok = true;
                for (int k = 0; k < len; k++)
                {
                    if (counts[start + k] < 1) { ok = false; break; }
                }
                if (ok) output.Add(Mk(MoveType.Straight, start, len, CardUtil.RangeCards(start, len)));
            }
        }

        private static void AddDoubleStraightBeats(int[] counts, in Move prev, List<Move> output)
        {
            int len = prev.Length;
            for (int start = prev.Rank + 1; start + len - 1 <= CardUtil.StraightMaxRank; start++)
            {
                bool ok = true;
                for (int k = 0; k < len; k++)
                {
                    if (counts[start + k] < 2) { ok = false; break; }
                }
                if (ok)
                {
                    ulong cards = 0UL;
                    for (int k = 0; k < len; k++) cards |= CardUtil.MakeCards(start + k, 2);
                    output.Add(Mk(MoveType.DoubleStraight, start, len, cards));
                }
            }
        }

        private static void AddPlaneBeats(ulong hand, int[] counts, in Move prev, List<Move> output)
        {
            int len = prev.Length;
            for (int start = prev.Rank + 1; start + len - 1 <= CardUtil.StraightMaxRank; start++)
            {
                bool ok = true;
                for (int k = 0; k < len; k++)
                {
                    if (counts[start + k] < 3) { ok = false; break; }
                }
                if (!ok) continue;

                ulong body = 0UL;
                for (int k = 0; k < len; k++) body |= CardUtil.MakeCards(start + k, 3);
                output.Add(Mk(MoveType.Plane, start, len, body));
            }
        }

        private static void AddPlaneWingBeats(ulong hand, int[] counts, in Move prev, bool singles, List<Move> output)
        {
            int len = prev.Length;
            for (int start = prev.Rank + 1; start + len - 1 <= CardUtil.StraightMaxRank; start++)
            {
                bool ok = true;
                for (int k = 0; k < len; k++)
                {
                    if (counts[start + k] < 3) { ok = false; break; }
                }
                if (!ok) continue;

                ulong body = 0UL;
                for (int k = 0; k < len; k++) body |= CardUtil.MakeCards(start + k, 3);

                ulong rest = hand - body;
                int[] restCounts = RestBuffer();
                CardUtil.ToCounts(rest, restCounts);

                if (singles)
                    EnumerateSingleWings(restCounts, 0, len, 0UL, body, start, len, MoveType.PlaneOne, output);
                else
                    EnumeratePairWings(restCounts, 0, len, 0UL, body, start, len, MoveType.PlaneTwo, output);
            }
        }

        private static void AddFourWithBeats(int[] counts, in Move prev, bool singles, List<Move> output)
        {
            for (int r = prev.Rank + 1; r < CardUtil.RankCount; r++)
            {
                if (counts[r] < 4) continue;
                ulong four = CardUtil.MakeCards(r, 4);

                if (singles)
                {
                    // 两张单：从其它点数里选两张（同一对也允许当两张单）
                    for (int a = 0; a < CardUtil.RankCount; a++)
                    {
                        if (a == r || counts[a] < 1) continue;
                        for (int b = a; b < CardUtil.RankCount; b++)
                        {
                            if (b == r || counts[b] < 1) continue;
                            if (a == b && counts[a] < 2) continue;
                            output.Add(Mk(MoveType.FourTwo, r, 1,
                                four + CardUtil.MakeCards(a, 1) + CardUtil.MakeCards(b, 1)));
                        }
                    }
                }
                else
                {
                    // 两对：从其它点数里选两个不同的对子
                    for (int a = 0; a < CardUtil.RankCount; a++)
                    {
                        if (a == r || counts[a] < 2) continue;
                        for (int b = a + 1; b < CardUtil.RankCount; b++)
                        {
                            if (b == r || counts[b] < 2) continue;
                            output.Add(Mk(MoveType.FourTwoPair, r, 1,
                                four + CardUtil.MakeCards(a, 2) + CardUtil.MakeCards(b, 2)));
                        }
                    }
                }
            }
        }

        private static readonly Comparison<Move> SearchOrder = CompareForSearch;

        /// <summary>
        /// 自由出牌的搜索顺序。
        /// 1) 一手走完直接最前；
        /// 2) 出完之后「剩余手数」越少越优先 —— 这条是整个求解器的灵魂，
        ///    残局里正确路线几乎总是「把手牌拆成尽量少的手」；
        /// 3) 同手数时张数多的优先，再按点数小的优先（保留大牌控场）。
        /// </summary>
        public static void SortForSearch(List<Move> moves, ulong hand)
        {
            for (int i = 0; i < moves.Count; i++)
            {
                if (moves[i].Cards == hand)
                {
                    if (i > 0)
                    {
                        Move t = moves[0];
                        moves[0] = moves[i];
                        moves[i] = t;
                    }
                    return;
                }
            }

            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                m.RestHands = (byte)HandEval.MinHands(hand - m.Cards);
                moves[i] = m;
            }

            // 候选通常只有几十个，插入排序比 List.Sort 快：
            // 没有递归/分区开销，比较直接内联，不走 Comparison 委托。
            for (int i = 1; i < moves.Count; i++)
            {
                Move cur = moves[i];
                int j = i - 1;
                while (j >= 0 && CompareForSearch(moves[j], cur) > 0)
                {
                    moves[j + 1] = moves[j];
                    j--;
                }
                moves[j + 1] = cur;
            }
        }

        /// <summary>
        /// 根节点专用排序：在「剩余手数」之上，优先挑「对手能压的走法最多」的一手。
        /// 对手可选分支越多，越容易踩进必败分支 —— 既符合「最顽强」的定义，
        /// 也常常能把真正的唯一解提前（20v14 实测从第 18 位提到很前面）。
        /// </summary>
        public static void SortRootForSearch(List<Move> moves, ulong hand, ulong oppHand)
        {
            for (int i = 0; i < moves.Count; i++)
            {
                if (moves[i].Cards == hand)
                {
                    if (i > 0) { Move t = moves[0]; moves[0] = moves[i]; moves[i] = t; }
                    return;
                }
            }

            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                m.RestHands = (byte)HandEval.MinHands(hand - m.Cards);
                moves[i] = m;
            }

            int n = moves.Count;
            int[] beats = new int[n];
            int[] minRank = new int[n];   // 对手「常规手段」压制这手牌所需的最小牌力；-1 = 只能炸弹/压不住
            var probe = new List<Move>(64);
            for (int i = 0; i < n; i++)
            {
                probe.Clear();
                Move mv = moves[i];
                GenerateBeats(oppHand, in mv, probe);
                beats[i] = probe.Count;

                int mr = int.MaxValue;
                for (int j = 0; j < probe.Count; j++)
                {
                    Move bm = probe[j];
                    if (bm.Type == MoveType.Bomb || bm.Type == MoveType.Rocket) continue;
                    if (bm.Rank < mr) mr = bm.Rank;
                }
                minRank[i] = mr == int.MaxValue ? -1 : mr;
            }

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, delegate(int a, int b)
            {
                // 第一优先：对手必须动用「常规牌」才能压住的走法。
                // 对手压不住/只能炸，说明我们打出去的是大牌，留着更值 → 降权。
                bool pa = minRank[a] >= 0;
                bool pb = minRank[b] >= 0;
                if (pa != pb) return pa ? -1 : 1;

                // 第二键：对手压制所需牌力越大越好（逼对手拆大牌/拆对子）
                if (pa && pb && minRank[a] != minRank[b]) return minRank[b] - minRank[a];

                if (moves[a].RestHands != moves[b].RestHands) return moves[a].RestHands - moves[b].RestHands;
                int ca = CardUtil.TotalCount(moves[a].Cards);
                int cb = CardUtil.TotalCount(moves[b].Cards);
                if (ca != cb) return ca - cb;
                return moves[a].Rank - moves[b].Rank;
            });

            var copy = moves.ToArray();
            for (int i = 0; i < n; i++) moves[i] = copy[order[i]];
        }

        private static int CompareForSearch(Move a, Move b)
        {
            if (a.RestHands != b.RestHands) return a.RestHands - b.RestHands;

            int ca = CardUtil.TotalCount(a.Cards);
            int cb = CardUtil.TotalCount(b.Cards);
            if (ca != cb) return cb - ca;
            return a.Rank - b.Rank;
        }

        private static void EnumerateSingleWings(
            int[] counts, int startRank, int need, ulong picked,
            ulong body, int moveRank, int length, MoveType type, List<Move> output)
        {
            if (need == 0)
            {
                output.Add(Mk(type, moveRank, length, body + picked));
                return;
            }

            for (int r = startRank; r < CardUtil.RankCount; r++)
            {
                if (counts[r] <= 0) continue;
                counts[r]--;
                EnumerateSingleWings(counts, r, need - 1, picked + CardUtil.MakeCards(r, 1),
                    body, moveRank, length, type, output);
                counts[r]++;
            }
        }

        private static void EnumeratePairWings(
            int[] counts, int startRank, int need, ulong picked,
            ulong body, int moveRank, int length, MoveType type, List<Move> output)
        {
            if (need == 0)
            {
                output.Add(Mk(type, moveRank, length, body + picked));
                return;
            }

            for (int r = startRank; r < CardUtil.RankCount; r++)
            {
                if (counts[r] < 2) continue;
                counts[r] -= 2;
                EnumeratePairWings(counts, r + 1, need - 1, picked + CardUtil.MakeCards(r, 2),
                    body, moveRank, length, type, output);
                counts[r] += 2;
            }
        }
    }
}
