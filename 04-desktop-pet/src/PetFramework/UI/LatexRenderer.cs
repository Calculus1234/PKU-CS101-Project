using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSharpMath.Atom;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// LaTeX / 数学公式渲染器（SkiaSharp + CSharpMath）。
    ///
    /// 为什么不是 uGUI 富文本：
    ///   · uGUI 的 Text 只会一行行摆字，没有盒模型，画不出分式线、根号、上下标、矩阵；
    ///   · 数学排版需要 TeX 解析 + 数学字体（Latin Modern Math）+ 基线对齐，
    ///     CSharpMath 把这三件事都做了（字体内嵌在 CSharpMath.Rendering.dll 里）。
    ///
    /// 做法：把【整条回答】按「文字 + $行内公式$ + $$独立公式$$」排版成一张透明纹理，
    ///       中文用系统字体（微软雅黑），公式用 CSharpMath。
    /// 纯文字消息不走这里（继续用 uGUI Text，可选中、更清晰）。
    ///
    /// ⚠️ 纹理由调用方（LatexBlock）负责销毁；这里只负责生成。
    /// </summary>
    public static class LatexRenderer
    {
        /// <summary>渲染倍率：1.5 = 在 1.5 倍像素下排版，显示时缩回 UI 单位，边缘更干净。</summary>
        public const float RenderScale = 1.5f;

        const string TextFontFamily = "Microsoft YaHei";
        const string MonoFontFamily = "Consolas";

        static SKTypeface regular, bold, mono;
        static bool initTried;

        // 公式里的中文（\text{证明}、\begin{cases} 里的汉字）在 Latin Modern 里没有字形，
        // 会显示成豆腐块。给 CSharpMath 挂一个中文字体做回退。
        static Typography.OpenFont.Typeface cjkMathFont;
        static bool cjkTried;

        static Typography.OpenFont.Typeface CjkMathFont()
        {
            if (cjkTried) return cjkMathFont;
            cjkTried = true;
            // ⚠️ 只加载【只含汉字】的子集字体（StreamingAssets/cjk_math_subset.ttf）。
            //    不能用完整的中文字体：CSharpMath 的 LocalTypefaces 会按字形覆盖，
            //    完整字体里的 ( ) √ { 会盖掉 Latin Modern，导致根号/矩阵括号全部错位（实测）。
            string[] paths =
            {
                Path.Combine(Application.streamingAssetsPath, "cjk_math_subset.ttf")
            };
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    if (!File.Exists(paths[i])) continue;
                    using (var fs = File.OpenRead(paths[i]))
                    {
                        var reader = new Typography.OpenFont.OpenFontReader();
                        cjkMathFont = reader.Read(fs);
                    }
                    if (cjkMathFont != null) break;
                }
                catch (Exception e) { Debug.LogWarning("[Latex] 中文字体回退加载失败：" + e.Message); }
            }
            return cjkMathFont;
        }

        // PNG 缓存：RefreshChat 会重建气泡，同一道题的公式不必每次重画。
        static readonly Dictionary<string, byte[]> pngCache = new Dictionary<string, byte[]>();

        /// <summary>这条消息里有没有 LaTeX 公式（决定要不要走图片渲染）。</summary>
        public static bool ContainsMath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int i = s.IndexOf('$');
            if (i >= 0) return true;
            if (s.IndexOf("\\(", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("\\[", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>SkiaSharp / 字体能不能用（DLL 没加载成功时退回普通文本）。</summary>
        public static bool Available
        {
            get { EnsureFonts(); return regular != null; }
        }

        static Typography.OpenFont.Typeface[] cjkArray;

        /// <summary>给 MathPainter 挂中文字体（只挂一次；重复赋值会重建字体缓存）。</summary>
        static void ApplyCjk(MathPainter p)
        {
            var cjk = CjkMathFont();
            if (cjk == null || p == null) return;
            if (cjkArray == null) cjkArray = new Typography.OpenFont.Typeface[] { cjk };
            if (!ReferenceEquals(p.LocalTypefaces, cjkArray)) p.LocalTypefaces = cjkArray;
        }

        static SKColor ToSk(Color c)
        {
            return new SKColor(
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255));
        }

        static void EnsureFonts()
        {
            if (initTried) return;
            initTried = true;
            try
            {
                regular = SKTypeface.FromFamilyName(TextFontFamily, SKFontStyle.Normal);
                bold = SKTypeface.FromFamilyName(TextFontFamily, SKFontStyle.Bold);
                mono = SKTypeface.FromFamilyName(MonoFontFamily, SKFontStyle.Normal);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Latex] SkiaSharp 加载失败：" + e.Message);
                regular = null;
            }
            if (regular == null)
            {
                try { regular = SKTypeface.Default; } catch { regular = null; }
            }
            if (bold == null) bold = regular;
            if (mono == null) mono = regular;
        }

        // =====================================================================
        //  对外入口
        // =====================================================================

        /// <summary>
        /// 把一段「文字 + LaTeX」渲染成 Texture2D。
        /// outWidth/outHeight 是 UI 单位（已经除回 RenderScale），直接给 RectTransform 用。
        /// </summary>
        public static Texture2D Render(string source, float fontSize, float maxWidth,
                                       Color color, out float outWidth, out float outHeight)
        {
            outWidth = Mathf.Max(80f, maxWidth);
            outHeight = fontSize * 1.6f;
            if (string.IsNullOrEmpty(source)) return null;

            string key = Mathf.RoundToInt(fontSize * 10) + "|" + Mathf.RoundToInt(maxWidth) + "|"
                       + ColorUtility.ToHtmlStringRGBA(color) + "|" + source;

            byte[] png;
            if (!pngCache.TryGetValue(key, out png))
            {
                try { png = RenderPng(source, fontSize, maxWidth, color, out outWidth, out outHeight); }
                catch (Exception e)
                {
                    Debug.LogWarning("[Latex] 渲染失败：" + e.Message);
                    return null;
                }
                if (png == null) return null;
                if (pngCache.Count > 48) pngCache.Clear();   // 简单粗暴：不搞 LRU 了
                pngCache[key] = png;
            }
            else
            {
                // 缓存里只有字节，尺寸按同样的规则重算一遍（很便宜，不排版）
                RecomputeSize(source, fontSize, maxWidth, out outWidth, out outHeight);
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        static void RecomputeSize(string source, float fontSize, float maxWidth,
                                  out float outWidth, out float outHeight)
        {
            outWidth = Mathf.Max(80f, maxWidth);
            outHeight = fontSize * 1.6f;
            try
            {
                float fs = fontSize * RenderScale;
                float maxW = Mathf.Max(120f, maxWidth) * RenderScale;
                var lines = Layout(ParseRuns(Preprocess(source)), fs, maxW);
                float h = 0f;
                for (int i = 0; i < lines.Count; i++) h += lines[i].Height(fs);
                outHeight = Mathf.Max(fontSize, h / RenderScale);
            }
            catch { }
        }

        static byte[] RenderPng(string source, float fontSize, float maxWidth, Color color,
                                out float outWidth, out float outHeight)
        {
            EnsureFonts();

            float fs = Mathf.Max(8f, fontSize) * RenderScale;
            float maxW = Mathf.Max(120f, maxWidth) * RenderScale;

            var lines = Layout(ParseRuns(Preprocess(source)), fs, maxW);
            float totalH = 0f;
            for (int i = 0; i < lines.Count; i++) totalH += lines[i].Height(fs);
            totalH = Mathf.Max(fs * 1.4f, totalH);

            int wPx = Mathf.CeilToInt(maxW);
            int hPx = Mathf.CeilToInt(totalH);

            outWidth = maxW / RenderScale;
            outHeight = totalH / RenderScale;

            using (var bmp = new SKBitmap(wPx, hPx, SKColorType.Rgba8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bmp))
            using (var textPaint = new SKPaint())
            {
                canvas.Clear(SKColors.Transparent);
                textPaint.IsAntialias = true;
                textPaint.TextSize = fs;
                textPaint.Typeface = regular;
                textPaint.Color = ToSk(color);

                float y = 0f;
                var math = new MathPainter();
                ApplyCjk(math);
                math.TextColor = textPaint.Color;

                for (int li = 0; li < lines.Count; li++)
                {
                    var line = lines[li];
                    float baseline = y + line.ascent + (line.displayMath != null ? fs * 0.55f : 0f);

                    if (line.displayMath != null)
                    {
                        // 独立公式：居中
                        float x = Mathf.Max(0f, (maxW - line.width) * 0.5f);
                        DrawMath(math, line.displayMath, canvas, x, baseline, fs, true);
                    }
                    else
                    {
                        for (int k = 0; k < line.items.Count; k++)
                        {
                            var it = line.items[k];
                            if (it.math)
                                DrawMath(math, it.text, canvas, it.x, baseline, fs, false);
                            else
                            {
                                textPaint.Typeface = it.bold ? bold : (it.mono ? mono : regular);
                                textPaint.Color = it.mono
                                    ? new SKColor(159, 220, 143, (byte)Mathf.Clamp(Mathf.RoundToInt(color.a * 255f), 0, 255))
                                    : ToSk(color);
                                canvas.DrawText(it.text, it.x, baseline, textPaint);
                                textPaint.Color = ToSk(color);
                            }
                        }
                    }
                    y += line.Height(fs);
                }

                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 92))
                    return data.ToArray();
            }
        }

        /// <summary>
        /// CSharpMath 的一个坑：控制词后面紧跟 = 会被当成命令名的一部分
        /// （实测 \Delta= 报 Invalid command \Delta=）。在 = 前补一个空格即可。
        /// </summary>
        static string FixLatex(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length + 8);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                sb.Append(c);
                if (c == '\\' && i + 1 < s.Length && char.IsLetter(s[i + 1]))
                {
                    int j = i + 1;
                    while (j < s.Length && char.IsLetter(s[j])) { sb.Append(s[j]); j++; }
                    if (j < s.Length && s[j] == '=') sb.Append(' ');
                    i = j;
                    continue;
                }
                i++;
            }
            return sb.ToString();
        }

        static void DrawMath(MathPainter p, string latex, SKCanvas canvas, float x, float baseline,
                             float fs, bool display)
        {
            try
            {
                p.FontSize = fs * (display ? 1.08f : 1.0f);
                p.LineStyle = display ? LineStyle.Display : LineStyle.Text;
                p.LaTeX = FixLatex(latex);
                if (!string.IsNullOrEmpty(p.ErrorMessage))
                {
                    // 不支持的宏：原样画出来（比白屏/红字好排查）
                    using (var paint = new SKPaint())
                    {
                        paint.IsAntialias = true;
                        paint.TextSize = fs;
                        paint.Typeface = mono;
                        paint.Color = new SKColor(255, 170, 120, 255);
                        canvas.DrawText(latex, x, baseline, paint);
                    }
                    return;
                }
                p.Draw(canvas, x, baseline);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Latex] 公式失败：" + latex + " → " + e.Message);
            }
        }

        // =====================================================================
        //  解析：文字 / $…$ / $$…$$ / \(…\) / \[…\]
        // =====================================================================

        class Run
        {
            public string text;
            public bool math;
            public bool display;
            public bool bold;
            public bool mono;
            public bool newline;
        }

        static List<Run> ParseRuns(string s)
        {
            var list = new List<Run>();
            var sb = new StringBuilder();
            Action flush = () =>
            {
                if (sb.Length == 0) return;
                string t = sb.ToString();
                sb.Length = 0;
                // **粗体** 和 `代码`
                int pos = 0;
                while (pos < t.Length)
                {
                    int b = t.IndexOf("**", pos, StringComparison.Ordinal);
                    int c = t.IndexOf('`', pos);
                    int next = -1; bool isBold = false, isMono = false;
                    if (b >= 0 && (c < 0 || b < c)) { next = b; isBold = true; }
                    else if (c >= 0) { next = c; isMono = true; }

                    if (next < 0) { list.Add(new Run { text = t.Substring(pos) }); break; }
                    if (next > pos) list.Add(new Run { text = t.Substring(pos, next - pos) });

                    if (isBold)
                    {
                        int e = t.IndexOf("**", next + 2, StringComparison.Ordinal);
                        if (e < 0) { list.Add(new Run { text = t.Substring(next) }); break; }
                        list.Add(new Run { text = t.Substring(next + 2, e - next - 2), bold = true });
                        pos = e + 2;
                    }
                    else
                    {
                        int e = t.IndexOf('`', next + 1);
                        if (e < 0) { list.Add(new Run { text = t.Substring(next) }); break; }
                        list.Add(new Run { text = t.Substring(next + 1, e - next - 1), mono = true });
                        pos = e + 1;
                    }
                }
            };

            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];

                if (ch == '\\' && i + 1 < s.Length && (s[i + 1] == '$' || s[i + 1] == '\\'))
                {
                    sb.Append(s[i + 1]); i += 2; continue;
                }

                if (ch == '$' && i + 1 < s.Length && s[i + 1] == '$')
                {
                    int e = s.IndexOf("$$", i + 2, StringComparison.Ordinal);
                    if (e > 0)
                    {
                        flush();
                        list.Add(new Run { math = true, display = true, text = s.Substring(i + 2, e - i - 2).Trim() });
                        i = e + 2; continue;
                    }
                }
                if (ch == '$')
                {
                    int e = s.IndexOf('$', i + 1);
                    if (e > i)
                    {
                        flush();
                        list.Add(new Run { math = true, text = s.Substring(i + 1, e - i - 1) });
                        i = e + 1; continue;
                    }
                }
                if (ch == '\\' && i + 1 < s.Length && s[i + 1] == '(')
                {
                    int e = s.IndexOf("\\)", i + 2, StringComparison.Ordinal);
                    if (e > 0)
                    {
                        flush();
                        list.Add(new Run { math = true, text = s.Substring(i + 2, e - i - 2) });
                        i = e + 2; continue;
                    }
                }
                if (ch == '\\' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    int e = s.IndexOf("\\]", i + 2, StringComparison.Ordinal);
                    if (e > 0)
                    {
                        flush();
                        list.Add(new Run { math = true, display = true, text = s.Substring(i + 2, e - i - 2).Trim() });
                        i = e + 2; continue;
                    }
                }

                if (ch == '\n')
                {
                    flush();
                    list.Add(new Run { newline = true });
                    i++; continue;
                }

                sb.Append(ch); i++;
            }
            flush();
            return list;
        }

        /// <summary>去掉 Markdown 的行首标记（标题 / 列表 / 引用 / 分隔线）。</summary>
        static string Preprocess(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i];
                string trimmed = t.TrimStart();
                int h = 0;
                while (h < trimmed.Length && trimmed[h] == '#') h++;
                if (h > 0 && h <= 6 && h < trimmed.Length && trimmed[h] == ' ')
                {
                    string head = trimmed.Substring(h + 1);
                    // 标题加粗（含公式时不能包 **，否则会和 $ 打架）
                    lines[i] = head.IndexOf('$') < 0 ? "**" + head + "**" : head;
                    continue;
                }
                if (trimmed.StartsWith(">"))
                    lines[i] = "▎ " + trimmed.TrimStart('>').TrimStart();
                else if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")))
                    lines[i] = t.Substring(0, t.Length - trimmed.Length) + "• " + trimmed.Substring(2);
                else if (trimmed.Length >= 3 && (trimmed.Replace("-", "").Length == 0 ||
                          trimmed.Replace("*", "").Length == 0 || trimmed.Replace("_", "").Length == 0))
                    lines[i] = "──────────────";
            }
            // ∎（U+220E）在 Latin Modern / 雅黑里都没有字形，会显示成豆腐块 —— 换成 ■
            return string.Join("\n", lines).Replace("∎", "■");
        }

        // =====================================================================
        //  排版：把 Run 流排成行（含自动换行 + 基线）
        // =====================================================================

        class Item
        {
            public string text;
            public bool math, bold, mono;
            public float x, width, ascent, descent;
        }

        class Line
        {
            public readonly List<Item> items = new List<Item>();
            public string displayMath;
            public float width, ascent, descent;

            public float Height(float fs)
            {
                // 独立公式上下留白多一点，别贴着正文
                if (displayMath != null) return ascent + descent + fs * 1.9f;
                return ascent + descent + fs * 0.42f;   // 行距
            }
        }

        static List<Line> Layout(List<Run> runs, float fs, float maxW)
        {
            var lines = new List<Line>();
            var cur = new Line();

            Action<float> newline = baselineH =>
            {
                if (cur.items.Count > 0 || cur.displayMath != null) lines.Add(cur);
                cur = new Line();
            };

            foreach (var run in runs)
            {
                if (run.newline) { newline(fs); continue; }

                if (run.math)
                {
                    float asc, desc, w;
                    bool ok = MeasureMath(run.text, fs, run.display, out w, out asc, out desc);
                    if (!ok)
                    {
                        // 公式解析失败 → 当普通文字处理
                        AddText(cur, run.text, false, false, fs, maxW, ref lines, ref cur);
                        continue;
                    }

                    if (run.display)
                    {
                        newline(fs);
                        var dl = new Line { displayMath = run.text, width = w, ascent = asc, descent = desc };
                        lines.Add(dl);
                        newline(fs);
                        continue;
                    }

                    if (cur.items.Count > 0 && cur.width + w > maxW) newline(fs);
                    var it = new Item { text = run.text, math = true, width = w, ascent = asc, descent = desc, x = cur.width };
                    cur.items.Add(it);
                    cur.width += w;
                    cur.ascent = Mathf.Max(cur.ascent, asc);
                    cur.descent = Mathf.Max(cur.descent, desc);
                    continue;
                }

                AddText(cur, run.text, run.bold, run.mono, fs, maxW, ref lines, ref cur);
            }

            if (cur.items.Count > 0 || cur.displayMath != null) lines.Add(cur);
            return lines;
        }

        static void AddText(Line line, string text, bool bold, bool mono, float fs,
                            float maxW, ref List<Line> lines, ref Line cur)
        {
            float asc, desc;
            MeasureTextBase(fs, out asc, out desc);

            int i = 0;
            while (i < text.Length)
            {
                int j = i + 1;
                char c = text[i];
                bool cjk = c >= 0x2E80;
                if (!cjk && char.IsLetterOrDigit(c))
                {
                    while (j < text.Length)
                    {
                        char d = text[j];
                        if (char.IsLetterOrDigit(d) || d == '_' || d == '-' || d == '.') j++;
                        else break;
                    }
                }
                else if (c == ' ') { while (j < text.Length && text[j] == ' ') j++; }

                string atom = text.Substring(i, j - i);
                float w = MeasureWidth(atom, fs, bold, mono);

                bool atLineStart = cur.items.Count == 0 && cur.displayMath == null;
                if (!atLineStart && cur.width + w > maxW)
                {
                    lines.Add(cur);
                    cur = new Line();
                    if (atom == " ") { i = j; continue; }   // 行首空格丢掉
                }

                var it = new Item
                {
                    text = atom, bold = bold, mono = mono,
                    x = cur.width, width = w, ascent = asc, descent = desc
                };
                cur.items.Add(it);
                cur.width += w;
                cur.ascent = Mathf.Max(cur.ascent, asc);
                cur.descent = Mathf.Max(cur.descent, desc);
                i = j;
            }
        }

        static readonly SKPaint measurePaint = new SKPaint();

        static void MeasureTextBase(float fs, out float ascent, out float descent)
        {
            EnsureFonts();
            measurePaint.TextSize = fs;
            measurePaint.Typeface = regular;
            var fm = measurePaint.FontMetrics;
            ascent = -fm.Ascent;
            descent = fm.Descent;
        }

        static float MeasureWidth(string s, float fs, bool bold, bool mono)
        {
            EnsureFonts();
            measurePaint.TextSize = fs;
            measurePaint.Typeface = bold ? LatexRenderer.bold : (mono ? LatexRenderer.mono : regular);
            return measurePaint.MeasureText(s);
        }

        static MathPainter measureMathPainter;

        static bool MeasureMath(string latex, float fs, bool display,
                                out float width, out float ascent, out float descent)
        {
            width = 0; ascent = fs * 0.8f; descent = fs * 0.25f;
            try
            {
                if (measureMathPainter == null)
                {
                    measureMathPainter = new MathPainter();
                    ApplyCjk(measureMathPainter);
                }
                measureMathPainter.FontSize = fs * (display ? 1.08f : 1.0f);
                measureMathPainter.LineStyle = display ? LineStyle.Display : LineStyle.Text;
                measureMathPainter.LaTeX = FixLatex(latex);
                if (!string.IsNullOrEmpty(measureMathPainter.ErrorMessage)) return false;
                var r = measureMathPainter.Measure(0);
                width = r.Width;
                ascent = Mathf.Max(fs * 0.6f, -r.Y);
                descent = Mathf.Max(fs * 0.15f, r.Height - ascent);
                return width > 0.1f;
            }
            catch { return false; }
        }

        // =====================================================================
        //  自检
        // =====================================================================

        /// <summary>诊断：返回 CSharpMath 对这个公式的解析错误（null = 解析成功）。</summary>
        public static string Diagnose(string latex)
        {
            try
            {
                var p = new MathPainter();
                p.FontSize = 24f;
                p.LaTeX = latex;
                return p.ErrorMessage;
            }
            catch (Exception e) { return "EX " + e.Message; }
        }

        /// <summary>编辑器/MCP 用：渲染一张样张，返回尺寸和落盘路径。</summary>
        public static string SelfTest()
        {
            string sample =
                "# 二次方程\n" +
                "设方程为 $ax^2+bx+c=0$（$a\\neq 0$），则\n" +
                "$$x=\\frac{-b\\pm\\sqrt{b^2-4ac}}{2a}$$\n" +
                "判别式 $\\Delta=b^2-4ac$；当 $\\Delta>0$ 时有两个不等实根。\n" +
                "矩阵例子：$\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}$\n" +
                "分段函数：$f(x)=\\begin{cases} x^2, & x\\ge 0 \\\\ -x, & x<0 \\end{cases}$\n" +
                "中文在公式里：$\\text{若 } x>0$ 且 $\\angle ABC=\\angle DEF$\n" +
                "证明：由 **德扎格定理**，$M,N,Q$ 三点共线。∎";
            float w, h;
            // 自检用深色画在白底上，方便直接看图（实际使用时由调用方给浅色）
            var tex = Render(sample, 16f, 760f, new Color(0.08f, 0.08f, 0.10f), out w, out h);
            if (tex == null) return "FAIL: Render returned null";

            byte[] png = tex.EncodeToPNG();
            string dir = Path.Combine(Application.dataPath, "Screenshots");
            try { Directory.CreateDirectory(dir); } catch { }
            string path = Path.Combine(dir, "latex_selftest.png");
            File.WriteAllBytes(path, png);
            UnityEngine.Object.DestroyImmediate(tex);
            return "OK " + png.Length + " bytes, ui=" + w.ToString("F0") + "x" + h.ToString("F0") + " → " + path;
        }
    }
}
