using System.Text;
using System.Text.RegularExpressions;

namespace PetFramework
{
    /// <summary>
    /// 把模型输出的 Markdown 转成 uGUI Text 能吃的富文本。
    ///
    /// 为什么不是「上个 Markdown 库」：
    ///   · uGUI 的 Text 只认 <b> <i> <size> <color>，**没有** 背景色 / 代码块 / 表格；
    ///   · 桌面宠物是内置管线打包，不想为几个标签引第三方 dll。
    /// 所以这里做「够用」的子集：标题、粗体、斜体、行内代码、围栏代码块、列表、
    /// 引用、分隔线、链接、删除线、复选框。转不了的（表格）原样保留，至少不难看。
    ///
    /// ⚠️ 普通文字里的 `<` 会被包进 <noparse>，否则模型吐个 HTML/XML 就把整段吃掉了；
    ///    代码块和行内代码一律 <noparse>，所以 `a &lt; b` 这种能原样显示。
    /// </summary>
    public static class MarkdownText
    {
        static readonly Regex ReHeading = new Regex(@"^(#{1,6})\s+(.*)$");
        static readonly Regex ReBullet = new Regex(@"^(\s*)[-*+]\s+(.*)$");
        static readonly Regex ReTask = new Regex(@"^\[([ xX])\]\s+(.*)$");
        static readonly Regex ReHr = new Regex(@"^\s*([-*_])\1{2,}\s*$");
        static readonly Regex ReImage = new Regex(@"!\[([^\]]*)\]\(([^)\s]+)\)");
        static readonly Regex ReLink = new Regex(@"\[([^\]]+)\]\(([^)\s]+)\)");
        static readonly Regex ReBold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Singleline);
        static readonly Regex ReStrike = new Regex(@"~~(.+?)~~", RegexOptions.Singleline);
        static readonly Regex ReItalic = new Regex(@"(?<!\*)\*(?!\*)([^*\n]+?)\*(?!\*)", RegexOptions.Singleline);

        const string CodeColor = "#9fdc8f";
        const string CodeFenceColor = "#65707f";
        const string QuoteColor = "#8a93a3";
        const string LinkColor = "#6cb6ff";

        public static string ToRich(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";

            string[] lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(md.Length + 256);
            bool inCode = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                // ---- 围栏代码块 ----
                if (trimmed.StartsWith("```"))
                {
                    if (!inCode)
                    {
                        inCode = true;
                        string lang = trimmed.Substring(3).Trim();
                        sb.Append("<color=").Append(CodeFenceColor).Append(">```")
                          .Append(lang).Append("</color>\n");
                    }
                    else
                    {
                        inCode = false;
                        sb.Append("<color=").Append(CodeFenceColor).Append(">```</color>\n");
                    }
                    continue;
                }
                if (inCode)
                {
                    sb.Append("<color=").Append(CodeColor).Append("><noparse>")
                      .Append(line).Append("</noparse></color>\n");
                    continue;
                }

                // ---- 分隔线 ----
                if (ReHr.IsMatch(line))
                {
                    sb.Append("<color=").Append(CodeFenceColor).Append(">──────────────</color>\n");
                    continue;
                }

                // ---- 标题 ----
                var h = ReHeading.Match(line);
                if (h.Success)
                {
                    int level = h.Groups[1].Value.Length;
                    int size = level <= 1 ? 24 : level == 2 ? 21 : level == 3 ? 19 : 17;
                    sb.Append("<size=").Append(size).Append("><b>")
                      .Append(Inline(h.Groups[2].Value)).Append("</b></size>\n");
                    continue;
                }

                // ---- 引用 ----
                if (trimmed.StartsWith(">"))
                {
                    string q = trimmed.TrimStart('>').TrimStart();
                    sb.Append("<color=").Append(QuoteColor).Append(">▎ ")
                      .Append(Inline(q)).Append("</color>\n");
                    continue;
                }

                // ---- 列表（含复选框）----
                var b = ReBullet.Match(line);
                if (b.Success)
                {
                    string indent = b.Groups[1].Value;
                    string body = b.Groups[2].Value;
                    var task = ReTask.Match(body);
                    string mark = "• ";
                    if (task.Success)
                    {
                        mark = task.Groups[1].Value == " " ? "☐ " : "☑ ";
                        body = task.Groups[2].Value;
                    }
                    sb.Append(indent).Append("<color=").Append(QuoteColor).Append(">")
                      .Append(mark).Append("</color>").Append(Inline(body)).Append('\n');
                    continue;
                }

                // ---- 普通行 ----
                sb.Append(Inline(line)).Append('\n');
            }

            if (inCode) sb.Append("<color=").Append(CodeFenceColor).Append(">```</color>\n");
            return sb.ToString();
        }

        /// <summary>一行内的行内语法。行内代码切片后其余部分走 Plain。</summary>
        static string Inline(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 32);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '`')
                {
                    int j = s.IndexOf('`', i + 1);
                    if (j > i)
                    {
                        string code = s.Substring(i + 1, j - i - 1);
                        sb.Append("<color=").Append(CodeColor).Append("><noparse>")
                          .Append(code).Append("</noparse></color>");
                        i = j + 1;
                        continue;
                    }
                }
                int start = i;
                while (i < s.Length && s[i] != '`') i++;
                sb.Append(Plain(s.Substring(start, i - start)));
            }
            return sb.ToString();
        }

        /// <summary>非代码正文：先保护 &lt;，再上 Markdown 的强调 / 链接。</summary>
        static string Plain(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";

            // 模型偶尔吐 <br> —— 先变成真换行
            t = t.Replace("<br />", "\n").Replace("<br/>", "\n").Replace("<br>", "\n");

            // ⚠️ 必须在插入任何我们自己的标签【之前】处理：否则 <b> 也会被当成正文转义
            t = t.Replace("<", "<noparse><</noparse>");

            t = ReImage.Replace(t, m =>
            {
                string alt = m.Groups[1].Value;
                return "🖼 " + (string.IsNullOrEmpty(alt) ? m.Groups[2].Value : alt);
            });

            t = ReLink.Replace(t, m =>
                "<color=" + LinkColor + ">" + m.Groups[1].Value + "</color>（" + m.Groups[2].Value + "）");

            t = ReBold.Replace(t, "<b>$1</b>");
            t = ReStrike.Replace(t, "<i>$1</i>");
            t = ReItalic.Replace(t, "<i>$1</i>");
            return t;
        }
    }
}
