namespace Cleanse.Rules.Domain;

/// <summary>
/// One CTS open location joined to its SAM holding, if SAM has one.
/// </summary>
/// <remarks>
/// This is the unit of work for the CTS primary pass: the adapter produces it, and every rule in
/// the pass sees exactly this and nothing else. The SAM side is nullable because its absence is
/// itself what rule 2 detects.
/// </remarks>
public sealed record CtsSamRecord
{
    public required string CtsLocationNumber { get; init; }

    public required Cph Cph { get; init; }

    public string? CtsPremisesType { get; init; }

    public string? CtsCountyName { get; init; }

    public string? CtsKeeperSurname { get; init; }

    public string? CtsKeeperPostCode { get; init; }

    /// <summary>Null when SAM holds no holding for this CPH.</summary>
    public string? SamHoldingId { get; init; }

    public string? SamFeatureName { get; init; }

    /// <summary>The distinct animal species codes SAM records against the holding.</summary>
    public IReadOnlySet<string> SamSpeciesCodes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
