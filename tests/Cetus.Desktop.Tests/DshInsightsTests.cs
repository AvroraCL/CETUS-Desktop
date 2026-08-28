using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshInsightsTests
{
    [Fact]
    public void CriticalContextOccupancy_RanksFirstWithNewSessionAction()
    {
        var insights = DshInsights.Evaluate(90, 80, 1000, anyRunning: false, -1);

        Assert.Equal(2, insights.Count);
        Assert.Equal(InsightSeverity.Critical, insights[0].Severity);
        Assert.Contains("90", insights[0].Message);
        Assert.Equal(InsightAction.NewSession, insights[0].Action);
        Assert.Equal(InsightSeverity.Info, insights[1].Severity);
    }

    [Fact]
    public void WarnContextOccupancy_SuggestsNewSession()
    {
        var insights = DshInsights.Evaluate(75, 50, 1000, anyRunning: false, -1);

        Assert.Equal(InsightSeverity.Warn, insights[0].Severity);
        Assert.Contains("偏高", insights[0].Message);
        Assert.Equal(InsightAction.NewSession, insights[0].Action);
    }

    [Fact]
    public void HealthyOccupancy_ProducesNoContextInsight()
    {
        var insights = DshInsights.Evaluate(50, 50, 1000, anyRunning: false, -1);

        Assert.DoesNotContain(insights, insight => insight.Message.Contains("上下文"));
    }

    [Fact]
    public void CacheHitLevels_ProduceMatchingNotes()
    {
        var good = DshInsights.Evaluate(null, 80, 1000, false, -1);
        Assert.Contains(good, insight => insight.Message.Contains("复用良好"));

        var low = DshInsights.Evaluate(null, 20, 1000, false, -1);
        Assert.Contains(low, insight => insight.Message.Contains("偏低"));

        var noInput = DshInsights.Evaluate(null, 0, 0, false, -1);
        Assert.DoesNotContain(noInput, insight => insight.Message.Contains("偏低"));
    }

    [Fact]
    public void OutputSpike_OnlyWarnsWhileRunning()
    {
        var running = DshInsights.Evaluate(null, 80, 1000, anyRunning: true, 3000);
        var spike = Assert.Single(running, insight => insight.Severity == InsightSeverity.Warn);
        Assert.Equal(InsightAction.CancelSession, spike.Action);
        Assert.Contains("3,000", spike.Message);

        var idle = DshInsights.Evaluate(null, 80, 1000, anyRunning: false, 3000);
        Assert.DoesNotContain(idle, insight => insight.Severity == InsightSeverity.Warn);
    }

    [Fact]
    public void NoSignals_ProducesAllClear()
    {
        var insights = DshInsights.Evaluate(null, -1, 0, anyRunning: false, -1);

        var allClear = Assert.Single(insights);
        Assert.Equal(InsightSeverity.Info, allClear.Severity);
        Assert.Equal("运行正常", allClear.Message);
    }

    [Fact]
    public void Insights_AreOrderedCriticalFirst()
    {
        var insights = DshInsights.Evaluate(90, 20, 1000, anyRunning: true, 3000);

        Assert.Equal(InsightSeverity.Critical, insights[0].Severity);
        Assert.Equal(InsightSeverity.Warn, insights[1].Severity);
        Assert.Equal(InsightSeverity.Info, insights[2].Severity);
    }
}
