namespace Cleanse.Core.Abstractions;

/// <summary>
/// Lets a storage adapter describe and prove itself without Core knowing what it is.
/// </summary>
/// <remarks>
/// Probes report failure rather than throwing. A diagnostics page exists precisely for the
/// moments when something is broken, so it has to survive the thing it is reporting on.
/// </remarks>
public interface IStorageDiagnostics
{
    /// <summary>The provider in use, for display: <c>Postgres</c>, <c>Sqlite</c>, <c>InMemory</c>.</summary>
    string ProviderName { get; }

    /// <summary>Where it is pointed, with any credential removed. Null when not applicable.</summary>
    string? Target { get; }

    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The same contract for the KRDS snapshot source the rules engine reads, with a second probe:
/// a source can answer and still refuse to be read.
/// </summary>
public interface IKrdsDiagnostics
{
    string ProviderName { get; }

    string? Target { get; }

    /// <summary>Whether the source answers at all. For the Data Bridge, its anonymous health check.</summary>
    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the source can be read the way the rules engine reads it. For the Data Bridge, a
    /// call to one of its read APIs with the configured key, which only a genuine connection passes.
    /// </summary>
    Task<ProbeResult> ProbeAccessAsync(CancellationToken cancellationToken = default);
}

/// <summary>The same contract for wherever uploaded import files are kept.</summary>
public interface IImportStorageDiagnostics
{
    /// <summary>The store in use, for display: <c>S3</c> or <c>FileSystem</c>.</summary>
    string ProviderName { get; }

    /// <summary>The bucket or directory. Null when none is configured.</summary>
    string? Target { get; }

    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public enum ProbeState
{
    /// <summary>Checked, and working.</summary>
    Ok,

    /// <summary>Checked, and broken.</summary>
    Failed,

    /// <summary>Not checked, or not knowable from here. Reported honestly rather than guessed.</summary>
    Unknown
}

public sealed record ProbeResult(ProbeState State, string? Detail, double ElapsedMilliseconds)
{
    public static ProbeResult Ok(double elapsedMs, string? detail = null) =>
        new(ProbeState.Ok, detail, elapsedMs);

    public static ProbeResult Failed(double elapsedMs, string detail) =>
        new(ProbeState.Failed, detail, elapsedMs);

    public static ProbeResult Unknown(string detail) => new(ProbeState.Unknown, detail, 0);
}

public static class ProbeStateNames
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static string ToWire(this ProbeState state) => state switch
    {
        ProbeState.Ok => Ok,
        ProbeState.Failed => Failed,
        _ => Unknown
    };
}
