using Cleanse.Api.Diagnostics;

namespace Cleanse.Api.Test;

/// <summary>Which build is answering, read from the variables CDP injects into every container.</summary>
public class ServiceInfoTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static ServiceInfo Read(Dictionary<string, string?> variables) =>
        ServiceInfo.FromEnvironment(name => variables.GetValueOrDefault(name), new FixedClock(Started));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void The_version_and_environment_come_from_what_CDP_injects()
    {
        var info = Read(new() { ["SERVICE_VERSION"] = "0.5.0", ["ENVIRONMENT"] = "ext-test" });

        Assert.Equal("cleanse", info.Name);
        Assert.Equal("0.5.0", info.Version);
        Assert.Equal("ext-test", info.Environment);
        Assert.Equal(Started, info.StartedAtUtc);
        Assert.StartsWith(".NET", info.Runtime, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_values_are_reported_as_null_rather_than_guessed(string? value)
    {
        var info = Read(new() { ["SERVICE_VERSION"] = value, ["ENVIRONMENT"] = value });

        Assert.Null(info.Version);
        Assert.Null(info.Environment);
    }

    /// <summary>
    /// CDP runs every service with ASPNETCORE_ENVIRONMENT=Production, which ASP.NET Core also
    /// exposes as the configuration key ENVIRONMENT. It must never be reported as the CDP one.
    /// </summary>
    [Fact]
    public void The_hosting_environment_is_never_mistaken_for_the_CDP_environment()
    {
        var info = Read(new() { ["ASPNETCORE_ENVIRONMENT"] = "Production" });

        Assert.Null(info.Environment);
    }
}
