using System;

namespace PokerSolver
{
    /// <summary>
    /// 手牌结构评估。核心是「最少几手能出完」（MinHands），
    /// 它是残局里最有用的启发式：手数越少越好，几乎所有残局公式本质都在比较这个。
    /// 用两种贪心顺序各算一遍取小（先连对 / 先顺子），比单一贪心稳。
    /// </summary>
    public static class HandEval
    {
        // 直接映射缓存：固定 2^17 项，靠手牌哈希取下标。
        // 比 Dictionary 快一个数量级（无哈希表探测、无扩容、无 GC 分配），
        // 冲突时直接覆盖 —— 只影响命中率，不影响正确性（未命中就重新算）。
        private const int CacheBits = 17;
        private const int CacheSize = 1 << CacheBits;

        [ThreadStatic] private static int[] _buf;
        [ThreadStatic] private static ulong[] _cacheKey;
        [ThreadStatic] private static int[] _cacheVal;

        // 诊断计数器（多线程下有竞争，只用于看数量级）
        public static long StatCalls;
        public static long StatHits;

        private static int[] Buf()
        {
            if (_buf == null) _buf = new int[CardUtil.RankCount];
            return _buf;
        }

        /// <summary>每次求解开始时清空评估缓存。</summary>
        public static void ResetCache()
        {
            if (_cacheKey == null)
            {
                _cacheKey = new ulong[CacheSize];
                _cacheVal = new int[CacheSize];
            }
            else
            {
                Array.Clear(_cacheKey, 0, CacheSize);
            }
        }

        /// <summary>手牌最少需要几手出完（贪心上界，越准确越好）。同一手牌会反复评估，所以带缓存。</summary>
        public static int MinHands(ulong hand)
        {
            if (hand == 0UL) return 0;

            if (_cacheKey == null)
            {
                _cacheKey = new ulong[CacheSize];
                _cacheVal = new int[CacheSize];
            }

            int idx = (int)((hand * 0x9E3779B97F4A7C15UL) >> (64 - CacheBits));
            if (_cacheKey[idx] == hand) return _cacheVal[idx];

            int pairsFirst = Greedy(hand, true);
            int straightsFirst = Greedy(hand, false);
            int result = pairsFirst < straightsFirst ? pairsFirst : straightsFirst;

            _cacheKey[idx] = hand;
            _cacheVal[idx] = result;
            return result;
        }

        private static int Greedy(ulong hand, bool pairsFirst)
        {
            int[] c = Buf();
            CardUtil.ToCounts(hand, c);
            int hands = 0;

            // 炸弹单独一手（拆不拆由搜索决定，评估先按整炸算）
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (c[r] == 4) { hands++; c[r] = 0; }
            }

            if (pairsFirst)
            {
                ExtractDoubleStraights(c, ref hands);
                ExtractStraights(c, ref hands);
            }
            else
            {
                ExtractStraights(c, ref hands);
                ExtractDoubleStraights(c, ref hands);
            }

            ExtractPlanes(c, ref hands);
            ExtractTriples(c, ref hands);
            ExtractPairs(c, ref hands);
            ExtractSingles(c, ref hands);

            return hands;
        }

        private static void ExtractDoubleStraights(int[] c, ref int hands)
        {
            int r = 0;
            while (r <= CardUtil.StraightMaxRank)
            {
                if (c[r] < 2) { r++; continue; }
                int s = r;
                while (r + 1 <= CardUtil.StraightMaxRank && c[r + 1] >= 2) r++;
                int e = r;

                if (e - s + 1 >= 3)
                {
                    hands++;
                    for (int k = s; k <= e; k++) c[k] -= 2;
                }
                r++;
            }
        }

        private static void ExtractStraights(int[] c, ref int hands)
        {
            int r = 0;
            while (r <= CardUtil.StraightMaxRank)
            {
                if (c[r] < 1) { r++; continue; }
                int s = r;
                while (r + 1 <= CardUtil.StraightMaxRank && c[r + 1] >= 1) r++;
                int e = r;

                if (e - s + 1 >= 5)
                {
                    hands++;
                    for (int k = s; k <= e; k++) c[k] -= 1;
                }
                r++;
            }
        }

        private static void ExtractPlanes(int[] c, ref int hands)
        {
            int r = 0;
            while (r <= CardUtil.StraightMaxRank)
            {
                if (c[r] < 3) { r++; continue; }
                int s = r;
                while (r + 1 <= CardUtil.StraightMaxRank && c[r + 1] >= 3) r++;
                int e = r;

                if (e - s + 1 >= 2)
                {
                    hands++;
                    for (int k = s; k <= e; k++) c[k] -= 3;
                }
                r++;
            }
        }

        private static void ExtractTriples(int[] c, ref int hands)
        {
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                while (c[r] >= 3)
                {
                    c[r] -= 3;
                    hands++;

                    // 三带一：顺手把小单张带掉，能省一手
                    for (int r2 = 0; r2 < CardUtil.RankCount; r2++)
                    {
                        if (r2 == r || c[r2] != 1) continue;
                        c[r2] = 0;
                        break;
                    }
                }
            }
        }

        private static void ExtractPairs(int[] c, ref int hands)
        {
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                while (c[r] >= 2)
                {
                    c[r] -= 2;
                    hands++;
                }
            }
        }

        private static void ExtractSingles(int[] c, ref int hands)
        {
            for (int r = 0; r < CardUtil.RankCount; r++)
            {
                if (c[r] > 0)
                {
                    hands++;
                    c[r] = 0;
                }
            }
        }
    }
}
