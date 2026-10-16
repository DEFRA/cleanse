namespace Cleanse.Core.Domain;

public enum ActorKind
{
    System,
    Human
}

/// <summary>
/// Who performed an action. Supplied explicitly on every mutating command so that Core
/// stays independent of any host's ambient identity mechanism.
/// </summary>
public sealed record Actor
{
    public const string SystemId = "system";

    private Actor(string id, string displayName, ActorKind kind)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ActorKind Kind { get; }

    public static Actor System { get; } = new(SystemId, "Cleanse rules engine", ActorKind.System);

    public static Actor Human(string id, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var trimmed = id.Trim();
        return new Actor(trimmed, string.IsNullOrWhiteSpace(displayName) ? trimmed : displayName.Trim(), ActorKind.Human);
    }

    public static Actor Machine(string id, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var trimmed = id.Trim();
        return new Actor(trimmed, string.IsNullOrWhiteSpace(displayName) ? trimmed : displayName.Trim(), ActorKind.System);
    }

    public override string ToString() => $"{DisplayName} ({Kind.ToString().ToLowerInvariant()})";
}
