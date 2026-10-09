using AnywearNumbra.Models;

namespace AnywearNumbra.Services;

/// <summary> Matches Mode C rules against a territory. Pure. </summary>
public static class TerritoryRuleService
{
    /// <summary> Precedence tier: exact territory (0) &gt; duty type (1) &gt; category (2). </summary>
    public static int Tier(RuleKind kind)
        => kind switch
        {
            RuleKind.ExactTerritory => 0,
            RuleKind.DutyType       => 1,
            RuleKind.Category       => 2,
            _                       => 3,
        };

    public static bool Matches(TerritoryRule rule, TerritoryInfo territory)
        => rule.Kind switch
        {
            RuleKind.ExactTerritory => rule.TerritoryId != 0 && rule.TerritoryId == territory.TerritoryId,
            RuleKind.DutyType       => rule.ContentTypeId != 0 && rule.ContentTypeId == territory.ContentTypeId,
            RuleKind.Category       => rule.Category != TerritoryCategory.Unknown && rule.Category == territory.Category,
            _                       => false,
        };

    /// <summary>
    /// All enabled rules matching <paramref name="territory"/>, best first.
    /// Within a tier, rules keep the user-defined order shown in the UI.
    /// </summary>
    public static IReadOnlyList<TerritoryRule> MatchingRules(IReadOnlyList<TerritoryRule> rules, TerritoryInfo territory)
        => rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(x => x.Rule.Enabled && Matches(x.Rule, territory))
            .OrderBy(x => Tier(x.Rule.Kind))
            .ThenBy(x => x.Index)
            .Select(x => x.Rule)
            .ToList();

    public static string Describe(TerritoryRule rule)
        => rule.Kind switch
        {
            RuleKind.ExactTerritory => $"Territory: {Label(rule.TargetLabel, $"#{rule.TerritoryId}")}",
            RuleKind.DutyType       => $"Duty type: {Label(rule.TargetLabel, $"#{rule.ContentTypeId}")}",
            RuleKind.Category       => $"Category: {rule.Category}",
            _                       => "Unknown rule",
        };

    private static string Label(string label, string fallback)
        => string.IsNullOrWhiteSpace(label) ? fallback : label;
}
