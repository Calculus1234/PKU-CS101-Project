using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 中文字体工具：TextMeshPro 默认字体不含中文字形，
    /// 本工具优先加载由编辑器菜单（HilbertLoop → Rebuild Chinese Font Asset）生成的
    /// 中文字体资产（Assets/Resources/Fonts/HL_ChineseFont_SDF.asset，Dynamic 模式），
    /// 缺失时在运行时直接从 Resources/Fonts 下的字体文件动态创建。
    /// 注意：当前使用微软雅黑 / 黑体作为开发占位字体，正式发行（商业发布）前请替换为已获授权的字体文件。
    /// </summary>
    public static class ChineseFontUtility
    {
        const string SdfAssetResourcePath = "Fonts/HL_ChineseFont_SDF";
        static readonly string[] SourceFontResourcePaths = { "Fonts/msyh", "Fonts/simhei" };

        private static TMP_FontAsset _cached;
        private static bool _searched;

        /// <summary>获取可用的中文字体资产（找不到返回 null，调用方保留默认字体）。</summary>
        public static TMP_FontAsset Get()
        {
            if (_searched) return _cached;
            _searched = true;

            // 1) 预生成字体资产（推荐路径）
            _cached = Resources.Load<TMP_FontAsset>(SdfAssetResourcePath);
            if (_cached != null) return _cached;

            // 2) 兜底：从项目内字体文件在运行时创建
            for (int i = 0; i < SourceFontResourcePaths.Length && _cached == null; i++)
            {
                var srcFont = Resources.Load<Font>(SourceFontResourcePaths[i]);
                if (srcFont == null) continue;

                _cached = TMP_FontAsset.CreateFontAsset(srcFont);
                if (_cached != null) _cached.name = "HL_ChineseFont (Runtime)";
            }
            if (_cached != null) return _cached;

            Debug.LogWarning("[HilbertLoop] 未找到中文字体资产。请执行菜单 HilbertLoop → Rebuild Chinese Font Asset，或确认 Assets/Resources/Fonts 下有字体文件。");
            return null;
        }

        /// <summary>把 TMP 文本切换到中文可用字体（找不到中文字体时保留原字体）。</summary>
        public static void Apply(TMP_Text text)
        {
            if (text == null) return;
            var font = Get();
            if (font != null) text.font = font;
        }
    }
}
