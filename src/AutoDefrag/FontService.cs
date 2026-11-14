using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace AutoDefrag;

/// <summary>
/// 界面字体：固定使用 Segoe UI Variable（Windows 11 自带可变字体），
/// 并以微软雅黑 UI 兜底中文字形，避免缺字时出现方框。
/// 1.9.6 起不再提供字体选择入口，Apply 始终传空值使用推荐默认。
/// </summary>
public static class FontService
{
    public const string RecommendedFamily = "Segoe UI Variable Text";

    private const string CjkFallback = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";
    private const string TextChain = "Segoe UI Variable Text, " + CjkFallback;
    private const string DisplayChain = "Segoe UI Variable Display, " + TextChain;

    private static List<string>? _names;

    /// <summary>系统已安装字体族名（去重、忽略大小写排序），首次访问时枚举并缓存。</summary>
    public static IReadOnlyList<string> Names => _names ??= LoadNames();

    /// <summary>推荐默认字体在当前系统上的可用名称；都不可用时返回空字符串。</summary>
    public static string ResolveRecommended()
    {
        foreach (var family in new[] { RecommendedFamily, "Segoe UI", "Microsoft YaHei UI" })
        {
            if (IsInstalled(family)) return family;
        }
        return string.Empty;
    }

    public static bool IsInstalled(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        return Names.Any(name => string.Equals(name, family, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>应用字体：写入 AppFont（正文）与 AppDisplayFont（大标题）两个资源。</summary>
    public static void Apply(string? family)
    {
        var chosen = (family ?? string.Empty).Trim();
        var isRecommended = chosen.Length == 0 ||
                            string.Equals(chosen, RecommendedFamily, StringComparison.OrdinalIgnoreCase);
        var text = isRecommended ? TextChain : chosen + ", " + CjkFallback;
        var display = isRecommended ? DisplayChain : text;

        try
        {
            System.Windows.Application.Current.Resources["AppFont"] = new FontFamily(text);
            System.Windows.Application.Current.Resources["AppDisplayFont"] = new FontFamily(display);
        }
        catch
        {
            // 字体创建失败（极罕见）时保持现有资源，不影响界面启动
        }
    }

    private static List<string> LoadNames()
    {
        var names = new List<string>();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in Fonts.SystemFontFamilies)
            {
                var name = family.Source?.Trim();
                if (string.IsNullOrEmpty(name) || name.StartsWith("@", StringComparison.Ordinal)) continue;
                if (seen.Add(name)) names.Add(name);
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
        }
        catch
        {
            // 字体缓存损坏等异常：返回已收集到的部分，界面仍可用
        }
        return names;
    }
}
