using System;
using System.Text;

namespace PokerSolver
{
    /// <summary>
    /// 牌与手牌的位棋表示工具。
    /// 手牌用 ulong 表示：每个点数占 4 bit（取值 0~4），共 15 个点数：
    /// 0=3 1=4 2=5 3=6 4=7 5=8 6=9 7=10 8=J 9=Q 10=K 11=A 12=2 13=小王 14=大王
    /// 因为每段的数值不会溢出 4，所以「手牌 - 牌的位棋」天然等于「剩余手牌」，不需要借位处理。
    /// </summary>
    public static class CardUtil
    {
        public const int RankCount = 15;
        public const int StraightMaxRank = 11; // 顺子/连对/飞机最高到 A，不能含 2 和王
        public const int SmallJoker = 13;
        public const int BigJoker = 14;

        public static readonly string[] RankNames =
        {
            "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A", "2", "小王", "大王"
        };

        public const ulong Empty = 0UL;

        public static int GetCount(ulong hand, int rank)
        {
            return (int)((hand >> (rank * 4)) & 0xFUL);
        }

        public static ulong MakeCards(int rank, int count)
        {
            return (ulong)count << (rank * 4);
        }

        public static ulong RangeCards(int startRank, int length)
        {
            ulong result = 0UL;
            for (int i = 0; i < length; i++)
                result |= 1UL << ((startRank + i) * 4);
            return result;
        }

        public static ulong AddCard(ulong hand, int rank)
        {
            return hand + (1UL << (rank * 4));
        }

        public static ulong AddCards(ulong hand, int rank, int count)
        {
            return hand + ((ulong)count << (rank * 4));
        }

        public static ulong RemoveCards(ulong hand, ulong cards)
        {
            return hand - cards;
        }

        /// <summary>
        /// 手牌张数。用 SWAR 把 15 个 4bit 计数折叠求和，比循环移位快得多，
        /// 而排序比较里会调到几百万次。
        /// </summary>
        public static int TotalCount(ulong hand)
        {
            ulong x = hand;
            x = (x & 0x0F0F0F0F0F0F0F0FUL) + ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL);
            x = (x & 0x00FF00FF00FF00FFUL) + ((x >> 8) & 0x00FF00FF00FF00FFUL);
            x = (x & 0x0000FFFF0000FFFFUL) + ((x >> 16) & 0x0000FFFF0000FFFFUL);
            x = (x & 0x00000000FFFFFFFFUL) + ((x >> 32) & 0x00000000FFFFFFFFUL);
            return (int)x;
        }

        public static void ToCounts(ulong hand, int[] counts)
        {
            for (int i = 0; i < RankCount; i++)
                counts[i] = (int)((hand >> (i * 4)) & 0xFUL);
        }

        public static ulong FromCounts(int[] counts)
        {
            ulong hand = 0UL;
            for (int i = 0; i < RankCount; i++)
                hand |= (ulong)counts[i] << (i * 4);
            return hand;
        }

        /// <summary>
        /// 随手牌展示：按点数从小到大，同点数的牌连写，"33 44 5 6 7" 这种。
        /// </summary>
        public static string ToDisplayString(ulong hand)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < RankCount; r++)
            {
                int n = GetCount(hand, r);
                if (n == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                for (int k = 0; k < n; k++) sb.Append(RankNames[r]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析用户输入。支持：
        ///   "333 444 5 6 7" / "3 3 3 4 4 4" / "3334445678" 连写
        ///   "10" 或 "T"，"J/Q/K/A/2"
        ///   "小王/大王"，简写 "X/x"（小王）、"D/d"（大王），单个「王」按先小王后大王自动分配
        /// 空格、中英文逗号、顿号、斜杠、加号、花色符号都会被忽略。
        /// </summary>
        public static bool TryParse(string text, out ulong hand, out string error)
        {
            hand = Empty;
            error = null;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "输入为空";
                return false;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c) || c == ',' || c == '，' || c == '、' ||
                    c == '/' || c == '-' || c == '+' || c == '|' || c == ';' || c == '；')
                {
                    i++;
                    continue;
                }

                // 花色符号直接忽略
                if (c == '♠' || c == '♥' || c == '♣' || c == '♦')
                {
                    i++;
                    continue;
                }

                int rank = -1;

                if (c == '1')
                {
                    if (i + 1 < text.Length && text[i + 1] == '0')
                    {
                        rank = 7;
                        i += 2;
                    }
                    else
                    {
                        error = "无法识别字符 '1'（10 请写成 10 或 T）";
                        return false;
                    }
                }
                else if (c >= '3' && c <= '9')
                {
                    rank = c - '3';
                    i++;
                }
                else if (c == 'T' || c == 't')
                {
                    rank = 7;
                    i++;
                }
                else if (c == 'J' || c == 'j')
                {
                    rank = 8;
                    i++;
                }
                else if (c == 'Q' || c == 'q')
                {
                    rank = 9;
                    i++;
                }
                else if (c == 'K' || c == 'k')
                {
                    rank = 10;
                    i++;
                }
                else if (c == 'A' || c == 'a')
                {
                    rank = 11;
                    i++;
                }
                else if (c == '2')
                {
                    rank = 12;
                    i++;
                }
                else if (c == '小')
                {
                    rank = SmallJoker;
                    i += (i + 1 < text.Length && text[i + 1] == '王') ? 2 : 1;
                }
                else if (c == '大')
                {
                    rank = BigJoker;
                    i += (i + 1 < text.Length && text[i + 1] == '王') ? 2 : 1;
                }
                else if (c == '王')
                {
                    rank = GetCount(hand, SmallJoker) == 0 ? SmallJoker : BigJoker;
                    i++;
                }
                else if (c == 'X' || c == 'x')
                {
                    rank = SmallJoker;
                    i++;
                }
                else if (c == 'D' || c == 'd')
                {
                    rank = BigJoker;
                    i++;
                }
                else
                {
                    error = "无法识别字符 '" + c + "'";
                    return false;
                }

                int cur = GetCount(hand, rank);
                int max = rank >= SmallJoker ? 1 : 4;
                if (cur >= max)
                {
                    error = RankNames[rank] + " 最多只有 " + max + " 张";
                    return false;
                }

                hand = AddCard(hand, rank);
            }

            if (hand == Empty)
            {
                error = "没有识别到任何牌";
                return false;
            }

            return true;
        }
    }
}
