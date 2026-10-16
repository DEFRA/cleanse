using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Engine;
using Cleanse.Rules.Groups.CtsSam;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Test.Support;

namespace Cleanse.Rules.Test;

public class CleanseRuleRegistryTests
{
    [Fact]
    public void Orders_rules_by_priority_not_by_number()
    {
        var registry = new CleanseRuleRegistry(CleanseRuleRegistry.CreateDefaultRules());

        Assert.Equal(["2", "1"], registry.All.Select(r => r.Descriptor.RuleNumber));
    }

    [Fact]
    public void Refuses_to_be_built_empty()
    {
        // The container resolves an unregistered IEnumerable to an empty sequence, which would
        // otherwise produce runs that silently find nothing.
        Assert.Throws<ArgumentException>(() => new CleanseRuleRegistry([]));
    }

    [Fact]
    public void Refuses_duplicate_rule_ids_because_issue_keys_would_collide()
    {
        var rules = new ICleanseRule[] { new Rule02CtsCphNotInSam(), new Rule02CtsCphNotInSam() };

        var ex = Assert.Throws<ArgumentException>(() => new CleanseRuleRegistry(rules));
        Assert.Contains("collide", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excludes_rules_on_hold_from_evaluation()
    {
        var registry = new CleanseRuleRegistry([new Rule02CtsCphNotInSam(), new OnHoldRule()]);

        Assert.Equal(2, registry.All.Count);
        Assert.Single(registry.Active);
    }

    [Fact]
    public void Finds_a_rule_by_its_business_number()
    {
        var registry = new CleanseRuleRegistry(CleanseRuleRegistry.CreateDefaultRules());

        Assert.Equal("No cattle unit in Sam", registry.ByNumber("1")!.Descriptor.Name);
        Assert.Null(registry.ByNumber("99"));
    }

    [Fact]
    public void Rule_1_stays_silent_when_rule_2_fires()
    {
        var registry = new CleanseRuleRegistry(CleanseRuleRegistry.CreateDefaultRules());

        var findings = registry.Evaluate(Records.Context(Records.CtsOnly()));

        Assert.Single(findings);
        Assert.Equal("2", findings[0].Descriptor.RuleNumber);
    }

    [Fact]
    public void Rule_1_is_evaluated_once_rule_2_has_passed()
    {
        var registry = new CleanseRuleRegistry(CleanseRuleRegistry.CreateDefaultRules());

        var findings = registry.Evaluate(Records.Context(Records.InBoth("01/036/8000", "SHP")));

        Assert.Single(findings);
        Assert.Equal("1", findings[0].Descriptor.RuleNumber);
    }

    [Fact]
    public void A_clean_record_produces_nothing()
    {
        var registry = new CleanseRuleRegistry(CleanseRuleRegistry.CreateDefaultRules());

        Assert.Empty(registry.Evaluate(Records.Context(Records.InBoth())));
    }

    private sealed class OnHoldRule : ICleanseRule
    {
        public RuleDescriptor Descriptor { get; } =
            new("99", "99", "Parked", "99", "Parked-rule", "Not yet agreed with the business.");

        public int Priority => 99;

        public RuleStatus Status => RuleStatus.OnHold;

        public bool AppliesTo(CtsSamRuleContext context) => true;

        public RuleFinding? Evaluate(CtsSamRuleContext context) => context.Issue(Descriptor);
    }
}
