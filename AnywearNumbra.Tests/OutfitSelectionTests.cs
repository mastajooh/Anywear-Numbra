using AnywearNumbra.Models;
using AnywearNumbra.Services;

namespace AnywearNumbra.Tests;

public class OutfitSelectionTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid D = new("dddddddd-0000-0000-0000-000000000004");

    /// <summary> Returns a fixed sequence of values, cycling. </summary>
    private sealed class SequenceRandom(params int[] values) : IRandomSource
    {
        private int _i;

        public int Next(int maxExclusive)
            => values[_i++ % values.Length] % maxExclusive;
    }

    private static AnywearSettings SettingsWith(SelectionMode mode, params (Guid Id, bool Eligible)[] outfits)
    {
        var settings = new AnywearSettings { Mode = mode };
        foreach (var (id, eligible) in outfits)
            settings.Outfits.Add(new OutfitEntry { DesignId = id, Eligible = eligible, LastKnownName = id.ToString()[..4] });
        return settings;
    }

    private static HashSet<Guid> Available(params Guid[] ids)
        => [.. ids];

    // ---- Random ---------------------------------------------------------------------------------

    [Fact]
    public void Random_NeverRepeatsThePreviousDesign_WhenTwoOrMoreAreEligible()
    {
        var settings  = SettingsWith(SelectionMode.Random, (A, true), (B, true), (C, true));
        var available = Available(A, B, C);
        var random    = new SequenceRandom(0, 1, 2, 0, 0, 1, 2, 2, 1, 0);
        for (var i = 0; i < 50; ++i)
        {
            var previous = settings.LastAppliedDesignId;
            var result   = OutfitSelectionService.Select(settings, available, null, random);
            Assert.True(result.HasDesign);
            Assert.NotEqual(previous, result.DesignId);
            OutfitSelectionService.RecordApplied(settings, result.DesignId, "x", available, DateTime.UtcNow);
        }
    }

    [Fact]
    public void Random_CanReachEveryOtherEligibleDesign()
    {
        var settings = SettingsWith(SelectionMode.Random, (A, true), (B, true), (C, true));
        settings.LastAppliedDesignId = A;
        var picks = new HashSet<Guid>();
        for (var r = 0; r < 4; ++r)
            picks.Add(OutfitSelectionService.SelectRandom(settings, Available(A, B, C), new SequenceRandom(r)).DesignId);

        Assert.True(picks.SetEquals(new[] { B, C }));
    }

    [Fact]
    public void Random_WithOneEligibleDesign_ReturnsIt_EvenIfItWasJustApplied()
    {
        var settings = SettingsWith(SelectionMode.Random, (A, true), (B, false));
        settings.LastAppliedDesignId = A;
        Assert.Equal(A, OutfitSelectionService.SelectRandom(settings, Available(A, B), SharedRandomSource.Instance).DesignId);
    }

    [Fact]
    public void Random_WithNoEligibleDesigns_ReturnsNone()
    {
        Assert.False(OutfitSelectionService.SelectRandom(SettingsWith(SelectionMode.Random), Available(A), SharedRandomSource.Instance).HasDesign);
        Assert.False(OutfitSelectionService.SelectRandom(SettingsWith(SelectionMode.Random, (A, false)), Available(A),
            SharedRandomSource.Instance).HasDesign);
    }

    [Fact]
    public void Random_WithRealRandomSource_OnlyReturnsEligibleDesigns()
    {
        var settings = SettingsWith(SelectionMode.Random, (A, true), (B, false), (C, true));
        for (var i = 0; i < 200; ++i)
        {
            var id = OutfitSelectionService.SelectRandom(settings, Available(A, B, C), SharedRandomSource.Instance).DesignId;
            Assert.Contains(id, new[] { A, C });
        }
    }

    // ---- Eligibility, deleted and renamed designs ------------------------------------------------

    [Fact]
    public void DeletedDesigns_AreSkipped_NotCrashedOn()
    {
        var settings = SettingsWith(SelectionMode.Random, (A, true), (B, true));
        var result   = OutfitSelectionService.SelectRandom(settings, Available(B), SharedRandomSource.Instance); // A was deleted
        Assert.Equal(B, result.DesignId);
        Assert.Equal(new[] { B }, OutfitSelectionService.EligibleDesigns(settings, Available(B)));
    }

    [Fact]
    public void RenamedDesigns_KeepTheirSettings_BecauseTheyAreKeyedByGuid()
    {
        var settings = SettingsWith(SelectionMode.Random, (A, true));
        settings.Outfits[0].LastKnownName = "Old name";
        // Glamourer now reports the same GUID under a new name; only the GUID set matters for selection.
        Assert.Equal(A, OutfitSelectionService.SelectRandom(settings, Available(A), SharedRandomSource.Instance).DesignId);
    }

    // ---- Rotation -----------------------------------------------------------------------------------

    [Fact]
    public void Rotation_CyclesInListOrder_AndWraps()
    {
        var settings  = SettingsWith(SelectionMode.Rotation, (A, true), (B, true), (C, true));
        var available = Available(A, B, C);
        var sequence  = new List<Guid>();
        for (var i = 0; i < 7; ++i)
        {
            var id = OutfitSelectionService.SelectRotation(settings, available).DesignId;
            sequence.Add(id);
            OutfitSelectionService.RecordApplied(settings, id, "x", available, DateTime.UtcNow);
        }

        Assert.Equal(new[] { A, B, C, A, B, C, A }, sequence);
    }

    [Fact]
    public void Rotation_PositionSurvivesARestart()
    {
        var settings  = SettingsWith(SelectionMode.Rotation, (A, true), (B, true), (C, true));
        var available = Available(A, B, C);
        OutfitSelectionService.RecordApplied(settings, A, "x", available, DateTime.UtcNow);
        OutfitSelectionService.RecordApplied(settings, B, "x", available, DateTime.UtcNow);

        // Simulate a restart: only persisted fields carry over.
        var restored = SettingsWith(SelectionMode.Rotation, (A, true), (B, true), (C, true));
        restored.RotationIndex        = settings.RotationIndex;
        restored.RotationLastDesignId = settings.RotationLastDesignId;

        Assert.Equal(C, OutfitSelectionService.SelectRotation(restored, available).DesignId);
    }

    [Fact]
    public void Rotation_WhenTheLastDesignWasRemoved_ContinuesWithTheDesignThatFollowedIt()
    {
        var settings  = SettingsWith(SelectionMode.Rotation, (A, true), (B, true), (C, true), (D, true));
        var available = Available(A, B, C, D);
        OutfitSelectionService.RecordApplied(settings, B, "x", available, DateTime.UtcNow); // index 1

        Assert.Equal(C, OutfitSelectionService.SelectRotation(settings, Available(A, C, D)).DesignId); // B deleted
        settings.Outfits[1].Eligible = false;
        Assert.Equal(C, OutfitSelectionService.SelectRotation(settings, available).DesignId);        // B made ineligible
    }

    [Fact]
    public void Rotation_NewDesignsAreIncludedInListOrder()
    {
        var settings  = SettingsWith(SelectionMode.Rotation, (A, true), (B, true));
        var available = Available(A, B, C);
        OutfitSelectionService.RecordApplied(settings, B, "x", available, DateTime.UtcNow);
        settings.Outfits.Add(new OutfitEntry { DesignId = C, Eligible = true });
        Assert.Equal(C, OutfitSelectionService.SelectRotation(settings, available).DesignId);
    }

    [Fact]
    public void Rotation_NeverUsesAnInvalidIndex()
    {
        var settings = SettingsWith(SelectionMode.Rotation, (A, true), (B, true));
        settings.RotationIndex = 57;
        Assert.Equal(B, OutfitSelectionService.SelectRotation(settings, Available(A, B)).DesignId);
        settings.RotationIndex = -3;
        Assert.True(OutfitSelectionService.SelectRotation(settings, Available(A, B)).HasDesign);
        Assert.False(OutfitSelectionService.SelectRotation(SettingsWith(SelectionMode.Rotation), Available()).HasDesign);
    }

    // ---- Fixed --------------------------------------------------------------------------------------

    [Fact]
    public void Fixed_ReturnsTheFixedDesign_OrNoneWhenMissing()
    {
        var settings = SettingsWith(SelectionMode.Fixed);
        Assert.False(OutfitSelectionService.Select(settings, Available(A), null, SharedRandomSource.Instance).HasDesign);
        settings.FixedDesignId = A;
        Assert.Equal(A, OutfitSelectionService.Select(settings, Available(A), null, SharedRandomSource.Instance).DesignId);
        Assert.False(OutfitSelectionService.Select(settings, Available(B), null, SharedRandomSource.Instance).HasDesign);
    }

    // ---- Rules --------------------------------------------------------------------------------------

    private static readonly TerritoryInfo Sastasha = new(1036, "Sastasha", TerritoryCategory.Dungeon, 4, 2, "Dungeons");
    private static readonly TerritoryInfo Limsa = new(128, "Limsa Lominsa", TerritoryCategory.City, 0, 0, string.Empty);

    private static AnywearSettings RuleSettings()
    {
        var settings = SettingsWith(SelectionMode.ZoneRules);
        settings.FallbackDesignId = D;
        settings.Rules.Add(new TerritoryRule { Kind = RuleKind.Category, Category = TerritoryCategory.Dungeon, DesignId = C });
        settings.Rules.Add(new TerritoryRule { Kind = RuleKind.DutyType, ContentTypeId = 2, DesignId = B });
        settings.Rules.Add(new TerritoryRule { Kind = RuleKind.ExactTerritory, TerritoryId = 1036, DesignId = A });
        return settings;
    }

    [Fact]
    public void Rules_ExactTerritoryBeatsDutyTypeBeatsCategoryBeatsFallback()
    {
        var settings  = RuleSettings();
        var available = Available(A, B, C, D);
        Assert.Equal(A, OutfitSelectionService.SelectByRules(settings, available, Sastasha).DesignId);

        settings.Rules[2].Enabled = false;
        Assert.Equal(B, OutfitSelectionService.SelectByRules(settings, available, Sastasha).DesignId);

        settings.Rules[1].Enabled = false;
        Assert.Equal(C, OutfitSelectionService.SelectByRules(settings, available, Sastasha).DesignId);

        settings.Rules[0].Enabled = false;
        Assert.Equal(D, OutfitSelectionService.SelectByRules(settings, available, Sastasha).DesignId);
    }

    [Fact]
    public void Rules_NoMatch_UsesFallback_AndMissingFallbackGivesNone()
    {
        var settings = RuleSettings();
        Assert.Equal(D, OutfitSelectionService.SelectByRules(settings, Available(A, B, C, D), Limsa).DesignId);
        Assert.False(OutfitSelectionService.SelectByRules(settings, Available(A, B, C), Limsa).HasDesign);
        Assert.Equal(D, OutfitSelectionService.SelectByRules(settings, Available(D), null).DesignId);
    }

    [Fact]
    public void Rules_WithAMissingDesign_AreSkippedInFavorOfTheNextMatch()
    {
        var settings = RuleSettings();
        Assert.Equal(B, OutfitSelectionService.SelectByRules(settings, Available(B, C, D), Sastasha).DesignId); // A deleted
        var result = OutfitSelectionService.SelectByRules(settings, Available(D), Sastasha);
        Assert.Equal(D, result.DesignId);
        Assert.Contains("skipped", result.Reason);
    }

    [Fact]
    public void Rules_TiesAreResolvedByListOrder()
    {
        var settings = SettingsWith(SelectionMode.ZoneRules);
        settings.Rules.Add(new TerritoryRule { Kind = RuleKind.Category, Category = TerritoryCategory.City, DesignId = B });
        settings.Rules.Add(new TerritoryRule { Kind = RuleKind.Category, Category = TerritoryCategory.City, DesignId = C });
        Assert.Equal(B, OutfitSelectionService.SelectByRules(settings, Available(B, C), Limsa).DesignId);

        (settings.Rules[0], settings.Rules[1]) = (settings.Rules[1], settings.Rules[0]);
        Assert.Equal(C, OutfitSelectionService.SelectByRules(settings, Available(B, C), Limsa).DesignId);
    }

    [Fact]
    public void Rules_IncompleteRulesNeverMatch()
    {
        var territory = new TerritoryInfo(1, "x", TerritoryCategory.Unknown, 0, 0, string.Empty);
        Assert.False(TerritoryRuleService.Matches(new TerritoryRule { Kind = RuleKind.ExactTerritory, TerritoryId = 0 }, TerritoryInfo.Unknown(0)));
        Assert.False(TerritoryRuleService.Matches(new TerritoryRule { Kind = RuleKind.DutyType, ContentTypeId = 0 }, territory));
        Assert.False(TerritoryRuleService.Matches(new TerritoryRule { Kind = RuleKind.Category, Category = TerritoryCategory.Unknown }, territory));
    }
}
