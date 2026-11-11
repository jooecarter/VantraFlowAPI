using VantraFlow.Api.Models;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>The flows list's sparkline: seven days of runs and failures, oldest first, today last.</summary>
public sealed class FlowDaysTests
{
    [Fact]
    public void Spreads_runs_over_the_last_seven_days()
    {
        var now = new DateTime(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 5), FlowDays.Since(now));
        var days = FlowDays.Spread([
            new FlowDayRow("a", new DateTime(2026, 11, 11), 4, 1),
            new FlowDayRow("a", new DateTime(2026, 11, 5), 2, 0),
            new FlowDayRow("a", new DateTime(2026, 11, 4), 9, 9),
            new FlowDayRow("b", new DateTime(2026, 11, 8), 3, 3),
        ], now);
        Assert.Equal([2, 0, 0, 0, 0, 0, 4], days["a"].Runs);
        Assert.Equal([0, 0, 0, 0, 0, 0, 1], days["a"].Failures);
        Assert.Equal([0, 0, 0, 3, 0, 0, 0], days["b"].Runs);
        Assert.False(days.ContainsKey("c"));
    }
}
