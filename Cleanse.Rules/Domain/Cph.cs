namespace Cleanse.Rules.Domain;

/// <summary>
/// A County Parish Holding number: two digits, three digits, four digits, slash separated.
/// </summary>
/// <remarks>
/// The strict format is load bearing rather than cosmetic. CTS keeps slaughterhouses in the same
/// open-locations dataset under a bare number such as <c>2045</c>, so refusing to parse anything
/// that is not <c>nn/nnn/nnnn</c> is exactly the slaughterhouse exclusion the rules require.
/// </remarks>
public readonly record struct Cph
{
    private readonly string? _value;

    private Cph(string value) => _value = value;

    public string Value => _value ?? throw new InvalidOperationException("Cph has not been initialised.");

    public bool HasValue => !string.IsNullOrEmpty(_value);

    /// <summary>The two digit county code, as an integer.</summary>
    public int CountyCode => int.Parse(Value.AsSpan(0, 2), provider: null);

    public static Cph Parse(string? value) =>
        TryParse(value, out var cph) ? cph : throw new FormatException($"'{value}' is not a valid CPH.");

    public static bool TryParse(string? value, out Cph cph)
    {
        cph = default;

        if (value is null)
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        if (span.Length != 11 || span[2] != '/' || span[6] != '/')
        {
            return false;
        }

        if (!AllDigits(span[..2]) || !AllDigits(span[3..6]) || !AllDigits(span[7..]))
        {
            return false;
        }

        cph = new Cph(span.ToString());
        return true;
    }

    private static bool AllDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}
