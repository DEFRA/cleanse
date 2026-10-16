using Cleanse.Rules.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cleanse.Rules.Test;

public class RuleScheduleServiceTests
{
    [Theory]
    // Before the hour: wait until today's slot.
    [InlineData("2026-10-15T00:00:00Z", 1.0)]
    [InlineData("2026-10-15T00:30:00Z", 0.5)]
    // On or after it: wait until tomorrow's, so a restart at 01:00 does not run twice.
    [InlineData("2026-10-15T01:00:00Z", 24.0)]
    [InlineData("2026-10-15T01:00:01Z", 23.999722)]
    [InlineData("2026-10-15T13:00:00Z", 12.0)]
    [InlineData("2026-10-15T23:59:59Z", 1.000278)]
    public void Waits_until_the_next_occurrence_of_the_configured_time(string nowUtc, double expectedHours)
    {
        var service = Create(TimeSpan.FromHours(1));

        var delay = service.DelayUntilNextRun(DateTimeOffset.Parse(nowUtc, null));

        Assert.Equal(expectedHours, delay.TotalHours, 3);
    }

    [Fact]
    public void Honours_a_non_default_time()
    {
        var service = Create(new TimeSpan(3, 30, 0));

        var delay = service.DelayUntilNextRun(DateTimeOffset.Parse("2026-10-15T02:00:00Z", null));

        Assert.Equal(1.5, delay.TotalHours, 3);
    }

    [Fact]
    public void Is_unaffected_by_the_offset_of_the_supplied_instant()
    {
        var service = Create(TimeSpan.FromHours(1));

        // 02:00+01:00 is 01:00 UTC, so the next slot is tomorrow's.
        var delay = service.DelayUntilNextRun(
            new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.FromHours(1)));

        Assert.Equal(24.0, delay.TotalHours, 3);
    }

    private static RuleScheduleService Create(TimeSpan dailyRunAtUtc)
    {
        var options = new CleanseRulesOptions { DailyRunAtUtc = dailyRunAtUtc };
        var provider = new ServiceCollection().BuildServiceProvider();

        return new RuleScheduleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RuleScheduleService>.Instance);
    }
}
