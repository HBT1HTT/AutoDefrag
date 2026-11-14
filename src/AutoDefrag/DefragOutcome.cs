using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoDefrag;

/// <summary>
/// defrag 退出码/错误码的统一解释。
/// 目的：把“失败原因”集中到一处，避免各处各写一套判断；
/// 解析不到时只返回“未知”，绝不猜成成功。
/// </summary>
public static class DefragResultCodes
{
    /// <summary>FACILITY_DEFRAG 错误码：需要管理员权限。</summary>
    public const uint PermissionDenied = 0x89000024;

    /// <summary>FACILITY_DEFRAG 错误码：卷上已有操作在进行。</summary>
    public const uint VolumeBusy = 0x89000000;

    public static bool TryParse(string? errorCode, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(errorCode)) return false;

        var text = errorCode.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public static bool IsPermissionDenied(string? errorCode) =>
        TryParse(errorCode, out var value) && value == PermissionDenied;

    public static bool IsVolumeBusy(string? errorCode) =>
        TryParse(errorCode, out var value) && value == VolumeBusy;

    /// <summary>给日志/报告用的一句话解释；未知错误码原样返回。</summary>
    public static string? Describe(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return null;
        if (IsPermissionDenied(errorCode)) return "权限不足：需要管理员权限";
        if (IsVolumeBusy(errorCode)) return "卷上已有操作正在进行";
        return "系统返回错误码 " + errorCode.Trim();
    }

    /// <summary>退出码的简短解释；null 表示没有拿到退出码。</summary>
    public static string DescribeExit(int? exitCode) => exitCode switch
    {
        null => "未知（没有拿到退出码）",
        0 => "成功（仅代表命令执行完毕）",
        _ => $"失败（exit={exitCode}）"
    };
}

/// <summary>/A 碎片率的解析结果。Percent=null 表示“无法确认”，调用方必须按未知处理。</summary>
public readonly record struct FragParseResult(double? Percent, string Method)
{
    public bool Success => Percent is not null;
}

/// <summary>
/// defrag /A 输出解析（多语言容错 + 安全降级）。
/// 规则：
///   1. 优先匹配“碎片空间”类键名（中/英及常见本地化关键词）；
///   2. 键名不认识时，只接受全报告里唯一一个“键 = 百分比”的值；
///      出现多个不同百分比视为歧义，返回“未知”，不取第一个凑数；
///   3. 任何解析失败都不影响退出码判断，也不触发“无需整理”之类的策略。
/// </summary>
public static class DefragOutputParser
{
    private static readonly Regex FragLabelRegex = new(
        @"(fragmented\s+space|total\s+fragmented|总碎片空间|碎片空间|總碎片空間|碎片空間|zerstreuter|fragmentierter|espace\s+fragment|espacio\s+fragmentado|断片化)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 明确不是“碎片率”的百分比指标，例如 VHD 上的“空间效率 = 100%”。
    /// 这类键名不能进入“唯一百分比兜底”，否则会把 100% 空间效率误当成 100% 碎片率。
    /// </summary>
    private static readonly Regex NonFragLabelRegex = new(
        @"(空间效率|空間效率|space\s+efficiency|可用空间|free\s+space|slab|缓存|cache|已整理|重新剪裁|retrim|optimiz)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentValueRegex = new(
        @"^([\d.,]+)\s*%$", RegexOptions.Compiled);

    private static readonly Regex SizeValueRegex = new(
        @"^([\d.,]+)\s*(TB|GB|MB|KB|B)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FragParseResult Parse(string? report, long volumeBytes)
    {
        if (string.IsNullOrWhiteSpace(report)) return new FragParseResult(null, "无输出");

        var candidates = new List<double>();
        // OutputCapture 为了日志紧凑把换行替换成 " | "；真实运行与测试报告都可能用这两种分隔。
        foreach (var rawLine in report.Split(new[] { "\r\n", "\n", " | " }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var separator = IndexOfSeparator(line);
            if (separator < 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0) continue;

            var isFragKey = FragLabelRegex.IsMatch(key);
            var isNonFragKey = NonFragLabelRegex.IsMatch(key);

            var percentMatch = PercentValueRegex.Match(value);
            if (percentMatch.Success)
            {
                var number = ParseNumber(percentMatch.Groups[1].Value);
                if (isFragKey) return new FragParseResult(number, "键名匹配");
                // “空间效率 100%”“缓存 80%”等不是碎片率，不能参与唯一值兜底
                if (isNonFragKey) continue;
                candidates.Add(number);
                continue;
            }

            if (isFragKey && volumeBytes > 0)
            {
                var sizeMatch = SizeValueRegex.Match(value);
                if (sizeMatch.Success)
                {
                    var multiplier = sizeMatch.Groups[2].Value.ToUpperInvariant() switch
                    {
                        "TB" => 1099511627776.0,
                        "GB" => 1073741824.0,
                        "MB" => 1048576.0,
                        "KB" => 1024.0,
                        _ => 1.0
                    };
                    var bytes = ParseNumber(sizeMatch.Groups[1].Value) * multiplier;
                    return new FragParseResult(Math.Round(bytes / volumeBytes * 100, 2), "碎片大小换算");
                }
            }
        }

        // 键名不认识（本地化/格式变化）：只有唯一值时才采用，避免误取进度或其它百分比
        var distinct = candidates.Distinct().ToList();
        if (distinct.Count == 1) return new FragParseResult(distinct[0], "唯一百分比兜底");
        if (distinct.Count > 1) return new FragParseResult(null, "多个百分比，无法判定");

        return new FragParseResult(null, "未找到碎片率字段");
    }

    private static int IndexOfSeparator(string line)
    {
        var eq = line.IndexOf('=');
        var colon = line.IndexOf(':');
        var fullWidth = line.IndexOf('：');

        var index = int.MaxValue;
        if (eq >= 0) index = Math.Min(index, eq);
        if (colon >= 0) index = Math.Min(index, colon);
        if (fullWidth >= 0) index = Math.Min(index, fullWidth);
        return index == int.MaxValue ? -1 : index;
    }

    private static double ParseNumber(string raw) =>
        double.TryParse(raw.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0.0;
}

/// <summary>
/// 单块磁盘整理结果的判定输入。抽成纯数据是为了能在不启动 defrag 的情况下做逻辑测试。
/// </summary>
public readonly record struct DefragVerdictInput(
    double? PreFrag,
    int? ExitCode,
    string? ErrorCode,
    string? Output,
    TimeSpan Elapsed,
    bool IsLargeCluster,
    bool IsHdd,
    bool? IsVirtualVolume);

/// <summary>
/// 整理结果判定：
///   - 非 0 退出码、输出中的 FACILITY_DEFRAG 错误码、用户停止，仍按失败/停止处理；
///   - 虚拟/非物理卷不提供碎片百分比是正常口径，退出码 0 时记为“已执行”，不判失败；
///   - 退出码 0 且系统明确输出“操作成功完成”时，即使没有碎片统计也按已执行记录（识别不出虚拟盘时的兜底）；
///   - 其余“退出码 0 但无任何证据”的情况保持原有的“无法确认”，不猜成成功。
/// </summary>
public static class DefragOutcomeClassifier
{
    public const string ExecutedWithoutFragStats = "已执行（该卷不提供碎片统计）";

    public static bool IsSuccessful(string? status) =>
        status is "成功" or ExecutedWithoutFragStats;

    public static string Classify(DefragVerdictInput input, out string reason)
    {
        if (input.ErrorCode is not null)
        {
            reason = "defrag 输出包含错误码 " + input.ErrorCode;
            return "失败";
        }

        if (input.ExitCode != 0)
        {
            reason = "defrag 退出码为 " + (input.ExitCode?.ToString() ?? "未知");
            return "失败";
        }

        var output = input.Output ?? string.Empty;
        var hasEvidence = output.Length > 0 && input.Elapsed.TotalSeconds >= 5;
        var explicitSuccess = HasExplicitSuccess(output);

        if (input.IsVirtualVolume == true && input.PreFrag is null)
        {
            reason = "虚拟/非物理卷未提供碎片百分比，退出码 0，按“已执行”记录";
            return ExecutedWithoutFragStats;
        }

        if (input.PreFrag is null && explicitSuccess && !hasEvidence)
        {
            reason = "退出码 0 且系统报告成功，但未提供碎片百分比，按“已执行”记录";
            return ExecutedWithoutFragStats;
        }

        if (!hasEvidence && input.IsLargeCluster)
        {
            if (input.Elapsed.TotalSeconds < 2)
            {
                reason = $"大簇卷在 {input.Elapsed.TotalSeconds:0.#}s 内结束且无有效输出";
                return "无法确认";
            }

            reason = "大簇卷未报告进度明细，退出码 0，按成功记录（建议 /A 前后对比）";
            return "成功";
        }

        if (!hasEvidence && input.IsHdd && input.PreFrag is null)
        {
            reason = "机械盘无有效输出且未解析到碎片率";
            return "无法确认";
        }

        reason = input.PreFrag is not null
            ? "退出码 0，且已解析到碎片率"
            : "退出码 0，有输出或非机械盘，按成功记录";
        return "成功";
    }

    /// <summary>系统明确表示操作成功；用于没有碎片统计时的兜底，不覆盖非 0 退出码/错误码。</summary>
    public static bool HasExplicitSuccess(string output) =>
        output.Contains("操作成功完成", StringComparison.Ordinal) ||
        output.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("operation completed successfully", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 整理策略中与“信任系统报告”有关的最小规则集。
/// 大簇卷（&gt; 4KB）上 defrag /A 的“无需整理”不可信，不能作为跳过文件级重写的依据。
/// </summary>
public static class DefragPolicy
{
    /// <summary>系统 defrag 能可靠工作的最大簇大小。</summary>
    public const int SystemDefragClusterLimit = 4096;

    /// <summary>簇大于 4KB 时，忽略系统 /A 的“无需整理”结论。</summary>
    public static bool IgnoresSystemNoAction(int clusterBytes) => clusterBytes > SystemDefragClusterLimit;

    /// <summary>
    /// 用户手动开启“大簇卷允许系统整理”后，是否应改走系统 defrag。
    /// 只对“Windows 自带”和“自动”生效；明确选择“本程序整理”时仍走文件级重写，
    /// 避免覆盖开关与整理方式形成两套互相矛盾的语义。
    /// </summary>
    public static bool UseSystemOnLargeCluster(string? engineMode, int clusterBytes, bool allow)
        => allow && clusterBytes > SystemDefragClusterLimit &&
           (string.Equals(engineMode, "System", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(engineMode, "Auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>系统 /A 是否明确表示“无需整理”（碎片率 0 或输出含对应措辞）。</summary>
    public static bool SystemReportsNoAction(ExternalFragResult? result)
    {
        if (result is null) return false;
        if (result.FragPercent is 0) return true;
        if (string.IsNullOrWhiteSpace(result.Output)) return false;

        return result.Output.Contains("不需要对该卷", StringComparison.OrdinalIgnoreCase)
               || result.Output.Contains("does not need", StringComparison.OrdinalIgnoreCase)
               || result.Output.Contains("no need to defragment", StringComparison.OrdinalIgnoreCase);
    }
}
