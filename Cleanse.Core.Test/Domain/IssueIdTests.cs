using Cleanse.Core.Domain;

namespace Cleanse.Core.Test.Domain;

public class IssueIdTests
{
    [Fact]
    public void Derivation_is_stable_across_calls()
    {
        var first = IssueId.Derive("rule2", "12/345/6789");
        var second = IssueId.Derive("rule2", "12/345/6789");

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void The_rule_id_discriminates()
    {
        Assert.NotEqual(IssueId.Derive("rule2", "12/345/6789"), IssueId.Derive("rule3", "12/345/6789"));
    }

    [Fact]
    public void Part_boundaries_cannot_be_forged()
    {
        // Without length-prefixing, two parts 'ab','c' and 'a','bc' would hash identically.
        Assert.NotEqual(IssueId.Derive("r", "ab", "c"), IssueId.Derive("r", "a", "bc"));
        Assert.NotEqual(IssueId.Derive("r", "a|b", "c"), IssueId.Derive("r", "a", "b|c"));
        Assert.NotEqual(IssueId.Derive("r", string.Empty, "a"), IssueId.Derive("r", "a", string.Empty));
    }

    [Theory]
    [InlineData("12/345/6789")]
    [InlineData("a b c")]
    [InlineData("%41")]
    [InlineData("../../etc/passwd")]
    [InlineData("caf\u00e9")]
    public void A_derived_id_is_always_url_safe(string part)
    {
        var id = IssueId.Derive("rule2", part);

        Assert.True(IssueId.IsUrlSafe(id.Value), id.Value);
        Assert.Equal(id.Value, Uri.EscapeDataString(id.Value));
    }

    [Fact]
    public void A_derived_id_is_prefixed_with_its_rule()
    {
        var id = IssueId.Derive("rule2", "12/345/6789");

        Assert.StartsWith("rule2-", id.Value, StringComparison.Ordinal);

        // 16 base32 characters: the 80 bits of digest that are kept.
        Assert.Equal(16, id.Value["rule2-".Length..].Length);
    }

    [Fact]
    public void The_inputs_do_not_leak_into_the_id()
    {
        var id = IssueId.Derive("rule2", "12/345/6789");

        Assert.DoesNotContain("12/345/6789", id.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rule 2")]
    [InlineData("rule/2")]
    [InlineData("rule|2")]
    public void A_rule_id_that_is_not_url_safe_is_rejected(string ruleId) =>
        Assert.Throws<ArgumentException>(() => IssueId.Derive(ruleId, "12/345/6789"));

    [Theory]
    [InlineData("2-ABCDEFGHIJKLMNOP", true)]
    [InlineData("legacy-key_1.0~x", true)]
    [InlineData("2:01/036/8000", false)]
    [InlineData("2:ABCDEFGHIJKLMNOP", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Url_safety_is_reported_accurately(string value, bool expected) =>
        Assert.Equal(expected, IssueId.IsUrlSafe(value));

    [Fact]
    public void An_externally_supplied_key_is_taken_verbatim_once_trimmed()
    {
        Assert.Equal("legacy-key-1", IssueId.From("  legacy-key-1  ").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_key_is_rejected(string? value) =>
        Assert.ThrowsAny<ArgumentException>(() => IssueId.From(value!));

    [Fact]
    public void Deriving_without_a_discriminator_is_rejected() =>
        Assert.Throws<ArgumentException>(() => IssueId.Derive("rule2"));

    [Fact]
    public void An_uninitialised_id_reports_no_value()
    {
        IssueId id = default;

        Assert.False(id.HasValue);
        Assert.Throws<InvalidOperationException>(() => id.Value);
    }
}

public class IssueContextTests
{
    [Fact]
    public void Equality_ignores_insertion_order()
    {
        var a = IssueContext.From(("x", "1"), ("y", "2"));
        var b = IssueContext.From(("y", "2"), ("x", "1"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Empty(a.DiffFrom(b));
    }

    [Fact]
    public void Diff_reports_additions_changes_and_removals()
    {
        var previous = IssueContext.From(("keeper", "J Smith"), ("gone", "yes"));
        var current = IssueContext.From(("keeper", "A Jones"), ("added", "1"));

        var changes = current.DiffFrom(previous);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c is { Field: "context.added", From: null, To: "1" });
        Assert.Contains(changes, c => c is { Field: "context.keeper", From: "J Smith", To: "A Jones" });
        Assert.Contains(changes, c => c is { Field: "context.gone", From: "yes", To: null });
    }

    [Fact]
    public void Context_is_a_defensive_copy()
    {
        var source = new Dictionary<string, string> { ["cph"] = "12/345/6789" };
        var context = IssueContext.From(source);

        source["cph"] = "tampered";

        Assert.Equal("12/345/6789", context["cph"]);
    }

    [Fact]
    public void A_blank_key_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            IssueContext.From(new Dictionary<string, string> { ["  "] = "value" }));

    [Fact]
    public void Empty_sources_collapse_onto_the_shared_empty_instance()
    {
        Assert.Same(IssueContext.Empty, IssueContext.From((IReadOnlyDictionary<string, string>?)null));
        Assert.Same(IssueContext.Empty, IssueContext.From(new Dictionary<string, string>()));
    }
}
