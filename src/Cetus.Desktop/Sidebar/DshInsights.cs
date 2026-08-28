namespace Cetus.Sidebar;

public enum InsightSeverity
{
    Info,
    Warn,
    Critical,
}

public enum InsightAction
{
    None,
    NewSession,
    CancelSession,
}

public sealed record DshInsight(InsightSeverity Severity, string Message, InsightAction Action = InsightAction.None);

/// <summary>
/// Turns raw status numbers into ordered, actionable insights — critical
/// first, then warnings, then informational notes. Pure rules with no I/O,
/// so thresholds and wording are unit-testable.
/// </summary>
public static class DshInsights
{
    public const double ContextWarnPercent = 70;
    public const double ContextCriticalPercent = 85;
    public const double GoodCacheHitPercent = 70;
    public const double LowCacheHitPercent = 30;
    public const double SpikeTokensPerMinute = 2000;

    /// <param name="contextOccupancyPercent">
    /// Occupancy of the scope's focus session; null when the scope has no
    /// session reporting context pressure.
    /// </param>
    /// <param name="cacheHitPercent">0-100, or -1 when there is no input yet.</param>
    /// <param name="scopedInputTokens">Uncached input tokens in the selected scope.</param>
    /// <param name="anyRunning">Whether any session in scope has its agent loop in flight.</param>
    /// <param name="outputTokensPerMinute">
    /// Measured output velocity from poll deltas; -1 when unknown (no running
    /// session or no previous sample).
    /// </param>
    public static List<DshInsight> Evaluate(
        double? contextOccupancyPercent,
        double cacheHitPercent,
        long scopedInputTokens,
        bool anyRunning,
        double outputTokensPerMinute)
    {
        var insights = new List<DshInsight>();

        if (contextOccupancyPercent is { } occupancy)
        {
            if (occupancy >= ContextCriticalPercent)
            {
                insights.Add(new DshInsight(
                    InsightSeverity.Critical,
                    $"上下文已占用 {occupancy:0}%，接近上限；DSH 达到阈值会自动压缩，也可新建会话继续",
                    InsightAction.NewSession));
            }
            else if (occupancy >= ContextWarnPercent)
            {
                insights.Add(new DshInsight(
                    InsightSeverity.Warn,
                    $"上下文占用 {occupancy:0}%，偏高——注意长对话效率",
                    InsightAction.NewSession));
            }
        }

        if (cacheHitPercent >= 0)
        {
            if (cacheHitPercent >= GoodCacheHitPercent)
            {
                insights.Add(new DshInsight(
                    InsightSeverity.Info,
                    $"缓存命中率 {cacheHitPercent:0}%，长上下文复用良好"));
            }
            else if (cacheHitPercent < LowCacheHitPercent && scopedInputTokens > 0)
            {
                insights.Add(new DshInsight(
                    InsightSeverity.Info,
                    $"缓存命中率 {cacheHitPercent:0}%，偏低——保持会话连续可提升复用"));
            }
        }

        if (anyRunning && outputTokensPerMinute >= SpikeTokensPerMinute)
        {
            insights.Add(new DshInsight(
                InsightSeverity.Warn,
                $"输出速率 {outputTokensPerMinute:N0} tokens/分，明显升高——注意重复修改或任务循环",
                InsightAction.CancelSession));
        }

        if (insights.Count == 0)
        {
            insights.Add(new DshInsight(InsightSeverity.Info, "运行正常"));
        }

        return insights
            .OrderBy(insight => insight.Severity, SeverityComparer.Instance)
            .ToList();
    }

    private sealed class SeverityComparer : IComparer<InsightSeverity>
    {
        public static readonly SeverityComparer Instance = new();

        public int Compare(InsightSeverity x, InsightSeverity y) => y - x; // Critical > Warn > Info
    }
}
