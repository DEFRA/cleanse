using System.Collections;

namespace Cleanse.Core.Domain;

/// <summary>
/// The anomaly's supporting data, e.g. the CPH and the two conflicting values that caused the
/// rule to fire. Immutable and ordinal-keyed so it maps cleanly onto a document field, a
/// Postgres jsonb column or a SQLite JSON text column.
/// </summary>
public sealed class IssueContext : IReadOnlyDictionary<string, string>, IEquatable<IssueContext>
{
    private readonly Dictionary<string, string> _values;

    private IssueContext(Dictionary<string, string> values) => _values = values;

    public static IssueContext Empty { get; } = new([]);

    public static IssueContext From(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Empty;
        }

        if (values is IssueContext existing)
        {
            return existing;
        }

        var copy = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Context keys must not be null or whitespace.", nameof(values));
            }

            copy[key.Trim()] = value ?? string.Empty;
        }

        return new IssueContext(copy);
    }

    public static IssueContext From(params (string Key, string Value)[] values) =>
        From(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));

    /// <summary>
    /// Field-level differences against <paramref name="other"/>, used to build the audit trail.
    /// Keys are reported as <c>context.{key}</c>.
    /// </summary>
    public IReadOnlyList<AuditChange> DiffFrom(IssueContext other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Equals(other))
        {
            return [];
        }

        var changes = new List<AuditChange>();
        foreach (var (key, value) in _values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (!other._values.TryGetValue(key, out var previous))
            {
                changes.Add(new AuditChange($"context.{key}", null, value));
            }
            else if (!string.Equals(previous, value, StringComparison.Ordinal))
            {
                changes.Add(new AuditChange($"context.{key}", previous, value));
            }
        }

        foreach (var (key, previous) in other._values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (!_values.ContainsKey(key))
            {
                changes.Add(new AuditChange($"context.{key}", previous, null));
            }
        }

        return changes;
    }

    public string this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(IssueContext? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_values.Count != other._values.Count)
        {
            return false;
        }

        foreach (var (key, value) in _values)
        {
            if (!other._values.TryGetValue(key, out var otherValue) ||
                !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as IssueContext);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
