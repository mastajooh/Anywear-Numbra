using System.Text.Json;
using AnywearNumbra.Models;
using AnywearNumbra.Services;

namespace AnywearNumbra.Tests;

public class SettingsTests
{
    [Fact]
    public void AllAppearanceScopeOptions_DefaultToOff()
    {
        var s = new AnywearSettings();
        Assert.False(s.ApplyWeapons);
        Assert.False(s.ApplyFacewear);
        Assert.False(s.ApplyHatVisorVisibility);
        Assert.False(s.ApplyDesignModAssociations);
        Assert.False(s.LockStateAfterApply);
        Assert.Equal(ScopeOptions.EquipmentOnly, ScopeOptions.FromSettings(s));
    }

    [Fact]
    public void Defaults_AreSensible()
    {
        var s = new AnywearSettings();
        Assert.Equal(AnywearSettings.CurrentVersion, s.SettingsVersion);
        Assert.True(s.AutomaticChangesEnabled);
        Assert.True(s.TriggerOnZoneChange && s.TriggerOnDutyEntry && s.TriggerOnPostLoading);
        Assert.Equal(SelectionMode.Random, s.Mode);
        Assert.Empty(s.Outfits);
        Assert.Empty(s.Rules);
        Assert.Equal(Guid.Empty, s.LastAppliedDesignId);
        Assert.False(new OutfitEntry().Eligible); // newly discovered designs are opt-in
        Assert.False(SettingsMigrator.Sanitize(s)); // defaults are already in range
    }

    [Fact]
    public void Migration_FromVersion0_UpgradesAndRepairs_AndIsIdempotent()
    {
        var s = new AnywearSettings
        {
            SettingsVersion   = 0,
            Outfits           = null!,
            Rules             = null!,
            RetryCount        = 99,
            RetryIntervalMs   = 1,
            PostTransitionDelayMs = -5,
            RotationIndex     = -2,
            Mode              = (SelectionMode)42,
        };

        Assert.True(SettingsMigrator.Migrate(s));
        Assert.Equal(AnywearSettings.CurrentVersion, s.SettingsVersion);
        Assert.NotNull(s.Outfits);
        Assert.NotNull(s.Rules);
        Assert.Equal(SettingsMigrator.MaxRetries, s.RetryCount);
        Assert.Equal(SettingsMigrator.MinRetryIntervalMs, s.RetryIntervalMs);
        Assert.Equal(0, s.PostTransitionDelayMs);
        Assert.Equal(0, s.RotationIndex);
        Assert.Equal(SelectionMode.Random, s.Mode);
        Assert.False(s.ApplyWeapons || s.ApplyFacewear || s.ApplyHatVisorVisibility); // migration never enables scope

        Assert.False(SettingsMigrator.Migrate(s));
    }

    [Fact]
    public void Sanitize_RemovesEmptyAndDuplicateDesignEntries_KeepingFirstOccurrence()
    {
        var id = Guid.NewGuid();
        var s  = new AnywearSettings();
        s.Outfits.Add(new OutfitEntry { DesignId = id, Eligible = true, LastKnownName = "first" });
        s.Outfits.Add(new OutfitEntry { DesignId = Guid.Empty });
        s.Outfits.Add(new OutfitEntry { DesignId = id, Eligible = false, LastKnownName = "dupe" });

        Assert.True(SettingsMigrator.Sanitize(s));
        var entry = Assert.Single(s.Outfits);
        Assert.Equal("first", entry.LastKnownName);
        Assert.True(entry.Eligible);
    }

    [Fact]
    public void Settings_RoundTripThroughJson_IncludingRotationAndRules()
    {
        var s = new AnywearSettings { Mode = SelectionMode.Rotation, RotationIndex = 3, RotationLastDesignId = Guid.NewGuid() };
        s.Outfits.Add(new OutfitEntry { DesignId = Guid.NewGuid(), Eligible = true, LastKnownName = "x" });
        s.Rules.Add(new TerritoryRule { Kind = RuleKind.DutyType, ContentTypeId = 2, DesignId = Guid.NewGuid() });

        var restored = JsonSerializer.Deserialize<AnywearSettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(s.RotationIndex, restored.RotationIndex);
        Assert.Equal(s.RotationLastDesignId, restored.RotationLastDesignId);
        Assert.Equal(s.Outfits[0].DesignId, restored.Outfits[0].DesignId);
        Assert.Equal(s.Rules[0].Id, restored.Rules[0].Id);
        Assert.Equal(RuleKind.DutyType, restored.Rules[0].Kind);
    }
}
