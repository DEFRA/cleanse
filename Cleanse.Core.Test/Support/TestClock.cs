namespace Cleanse.Core.Test.Support;

/// <summary>
/// A manually advanced clock. Every nightly scenario depends on controlling "now", so the
/// tests never touch the wall clock.
/// </summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTimeOffset Advance(TimeSpan by)
    {
        _now = _now.Add(by);
        return _now;
    }

    public DateTimeOffset AdvanceDays(double days) => Advance(TimeSpan.FromDays(days));

    public DateTimeOffset Set(DateTimeOffset at)
    {
        _now = at;
        return _now;
    }
}
