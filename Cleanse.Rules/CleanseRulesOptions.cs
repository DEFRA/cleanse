namespace Cleanse.Rules;

public sealed class CleanseRulesOptions
{
    /// <summary>Whether the scheduler starts. Off by default so a test host never fires a run.</summary>
    public bool Enabled { get; set; }

    /// <summary>The group all runs are scoped to. Only <c>cts-sam</c> exists today.</summary>
    public string GroupId { get; set; } = Engine.CleanseRuleRegistry.CtsSamGroupId;

    /// <summary>Time of day, in UTC, at which the nightly run starts.</summary>
    public TimeSpan DailyRunAtUtc { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Records evaluated between flushes. Also the progress reporting interval.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// How long a run may go without a heartbeat before it is presumed dead and abandoned.
    /// Without this, a process that died mid-run would block its group's single run slot forever.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(GroupId))
        {
            throw new InvalidOperationException("Rules:GroupId must be set.");
        }

        if (DailyRunAtUtc < TimeSpan.Zero || DailyRunAtUtc >= TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("Rules:DailyRunAtUtc must be a time of day.");
        }

        if (BatchSize is < 1 or > 5000)
        {
            throw new InvalidOperationException("Rules:BatchSize must be between 1 and 5000.");
        }

        if (StaleAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Rules:StaleAfter must be positive.");
        }
    }
}
