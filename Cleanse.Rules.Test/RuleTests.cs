using Cleanse.Core.Domain;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Test.Support;

namespace Cleanse.Rules.Test;

public class Rule02Tests
{
    private readonly Rule02CtsCphNotInSam _rule = new();

    [Fact]
    public void Applies_to_every_record()
    {
        Assert.True(_rule.AppliesTo(Records.Context(Records.InBoth())));
        Assert.True(_rule.AppliesTo(Records.Context(Records.CtsOnly())));
    }

    [Fact]
    public void Is_silent_when_the_cph_exists_in_sam()
    {
        Assert.Null(_rule.Evaluate(Records.Context(Records.InBoth())));
    }

    [Fact]
    public void Fires_when_the_cph_is_missing_from_sam()
    {
        var finding = _rule.Evaluate(Records.Context(Records.CtsOnly("02/100/0001")));

        Assert.NotNull(finding);
        Assert.Equal("2", finding.Descriptor.RuleId);
        Assert.Equal("02", finding.Descriptor.ErrorCode);
        Assert.Equal("CTS-CPH-missing-in-Sam", finding.Descriptor.ErrorDescription);
        Assert.Equal("02/100/0001", finding.Cph.Value);
    }

    [Fact]
    public void Keys_the_issue_on_the_rule_number_and_the_cph()
    {
        var finding = _rule.Evaluate(Records.Context(Records.CtsOnly("02/100/0001")));

        Assert.Equal(IssueId.Derive("2", "02/100/0001"), finding!.IssueId);

        // The same CPH under a different rule is a different issue.
        Assert.NotEqual(IssueId.Derive("1", "02/100/0001"), finding.IssueId);
    }

    [Fact]
    public void Carries_the_cts_identity_on_the_finding()
    {
        var finding = _rule.Evaluate(Records.Context(Records.CtsOnly("02/100/0001")));

        Assert.Equal("AH-02/100/0001", finding!.Context["ctsLocationNumber"]);
        Assert.Equal("AH", finding.Context["ctsPremisesType"]);
        Assert.Equal("Bedfordshire", finding.Context["ctsCountyName"]);
    }
}

public class Rule01Tests
{
    private readonly Rule01SamNoCattleUnit _rule = new();

    [Fact]
    public void Requires_the_holding_to_exist_in_sam()
    {
        Assert.False(_rule.AppliesTo(Records.Context(Records.CtsOnly())));
        Assert.True(_rule.AppliesTo(Records.Context(Records.InBoth())));
    }

    [Theory]
    [InlineData("CTT")]
    [InlineData("BSN")]
    [InlineData("BFF")]
    [InlineData("SHP", "CTT")]
    [InlineData("ctt")]
    public void Is_silent_when_a_cattle_species_is_recorded(params string[] codes)
    {
        Assert.Null(_rule.Evaluate(Records.Context(Records.InBoth("01/036/8000", codes))));
    }

    [Theory]
    [InlineData("SHP")]
    [InlineData("CHK", "PG", "GT")]
    public void Fires_when_no_cattle_species_is_recorded(params string[] codes)
    {
        var finding = _rule.Evaluate(Records.Context(Records.InBoth("01/036/8000", codes)));

        Assert.NotNull(finding);
        Assert.Equal("1", finding.Descriptor.RuleId);
        Assert.Equal("01", finding.Descriptor.ErrorCode);
        Assert.Equal("No-cattle-unit-in-Sam", finding.Descriptor.ErrorDescription);
    }

    [Fact]
    public void Fires_when_the_holding_records_no_species_at_all()
    {
        var record = Records.InBoth() with
        {
            SamSpeciesCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.False(finding.Context.ContainsKey("samSpeciesCodes"));
    }

    [Fact]
    public void Reports_the_species_actually_found()
    {
        var finding = _rule.Evaluate(Records.Context(Records.InBoth("01/036/8000", "SHP", "CHK")));

        Assert.Equal("CHK,SHP", finding!.Context["samSpeciesCodes"]);
        Assert.Equal("a8c4dd20-10ad-5f1e-86b3-9b0fdc54db57", finding.Context["samHoldingId"]);
        Assert.Equal("Wood Farm", finding.Context["samFeatureName"]);
    }

    [Fact]
    public void Keys_the_issue_on_the_rule_number_and_the_cph()
    {
        var finding = _rule.Evaluate(Records.Context(Records.InBoth("01/036/8000", "SHP")));

        Assert.Equal(IssueId.Derive("1", "01/036/8000"), finding!.IssueId);

        // A different CPH under the same rule must not collapse onto the same issue.
        var other = _rule.Evaluate(Records.Context(Records.InBoth("01/036/8001", "SHP")));
        Assert.NotEqual(finding.IssueId, other!.IssueId);
    }
}
