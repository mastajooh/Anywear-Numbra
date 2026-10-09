using System.Text.Json.Nodes;
using AnywearNumbra.Models;
using AnywearNumbra.Services;
using static AnywearNumbra.Tests.GlamourerJsonFixtures;

namespace AnywearNumbra.Tests;

public class ModOutfitTests
{
    private sealed class FixedRandom(int value) : IRandomSource
    {
        public int Next(int maxExclusive)
            => value % maxExclusive;
    }

    private static ModOutfit Mod(string dir, params string[] slots)
        => new(dir, dir.ToUpperInvariant(), slots.Select((s, i) => (s, (ulong)(1000 + i))).ToDictionary(p => p.s, p => p.Item2));

    [Fact]
    public void Build_MapsArmorTypesToGlamourerSlots_AndIgnoresEverythingElse()
    {
        var outfit = ModOutfitBuilder.Build("dir", "Name", new[]
        {
            ("Body", 10UL), ("Legs", 11UL), ("Head", 12UL), ("Ears", 13UL), ("Neck", 14UL), ("Wrists", 15UL),
            ("Hands", 16UL), ("Feet", 17UL),
            ("Sword", 20UL), ("Shield", 21UL), ("Glasses", 22UL), ("Unknown", 23UL), ("Body", 0UL),
        });

        Assert.Equal(8, outfit.PieceCount);
        Assert.Equal(10UL, outfit.Slots["Body"]);
        Assert.False(outfit.Slots.ContainsKey("MainHand"));
        Assert.False(outfit.Slots.ContainsKey("Glasses"));
    }

    [Fact]
    public void Build_FillsRightRingThenLeftRing_AndKeepsTheFirstItemPerSlot()
    {
        var outfit = ModOutfitBuilder.Build("dir", "Name", new[] { ("Finger", 31UL), ("Finger", 30UL), ("Finger", 32UL), ("Body", 41UL), ("Body", 40UL) });
        Assert.Equal(30UL, outfit.Slots["RFinger"]);
        Assert.Equal(31UL, outfit.Slots["LFinger"]);
        Assert.Equal(40UL, outfit.Slots["Body"]);
        Assert.Equal(3, outfit.PieceCount);
    }

    [Fact]
    public void ModOutfit_GoesThroughTheScopeFilter_ChangingOnlyItsSlotItems()
    {
        var current = Build(seed: 2);
        var outfit  = ModOutfitBuilder.Build("dir", "Name", new[] { ("Body", 777UL), ("Feet", 888UL) });
        var result  = EquipmentScopeFilter.Merge(current, ModOutfitBuilder.ToDesign(outfit), ScopeOptions.EquipmentOnly);

        Assert.True(result.IsSuccess, result.Message);
        var merged = result.State!;
        Assert.Equal("777", Slot(merged, "Body")["ItemId"]!.ToJsonString());
        Assert.Equal("888", Slot(merged, "Feet")["ItemId"]!.ToJsonString());
        Assert.True(Bool(Slot(merged, "Body"), "Apply"));
        Assert.False(Bool(Slot(merged, "Body"), "ApplyStain")); // dyes stay as they are

        foreach (var slot in ArmorSlots.Where(s => s is not ("Body" or "Feet")))
        {
            Assert.Equal(ValuesOnly(Slot(current, slot)), ValuesOnly(Slot(merged, slot)));
            Assert.All(ApplyMarkers(Slot(merged, slot)), m => Assert.False(m.Value, $"{slot}.{m.Path}"));
        }

        Assert.Equal(ValuesOnly(current["Customize"]), ValuesOnly(merged["Customize"]));
        Assert.All(ApplyMarkers(merged["Customize"]), m => Assert.False(m.Value, m.Path));
        Assert.All(ApplyMarkers(merged["Parameters"]), m => Assert.False(m.Value, m.Path));
    }

    [Fact]
    public void SelectRandom_NeverRepeatsThePreviousMod_WhenTwoOrMoreAreEligible()
    {
        var mods     = new[] { Mod("a", "Body"), Mod("b", "Body"), Mod("c", "Body") };
        var settings = new AnywearSettings();
        for (var r = 0; r < 12; ++r)
        {
            var previous = settings.LastAppliedModDirectory;
            var (outfit, _) = ModOutfitBuilder.SelectRandom(mods, settings, new FixedRandom(r));
            Assert.NotNull(outfit);
            Assert.NotEqual(previous, outfit!.ModDirectory);
            settings.LastAppliedModDirectory = outfit.ModDirectory;
        }
    }

    [Fact]
    public void SelectRandom_RespectsUntickedModsAndTheMinimumPieceCount()
    {
        var mods     = new[] { Mod("ring", "RFinger"), Mod("full", "Head", "Body", "Legs"), Mod("skipped", "Body", "Legs") };
        var settings = new AnywearSettings { MinimumModPieces = 2 };
        settings.ExcludedModDirectories.Add("skipped");

        var (outfit, reason) = ModOutfitBuilder.SelectRandom(mods, settings, new FixedRandom(0));
        Assert.Equal("full", outfit!.ModDirectory);
        Assert.Contains("only one", reason);

        settings.ExcludedModDirectories.Add("full");
        Assert.Null(ModOutfitBuilder.SelectRandom(mods, settings, new FixedRandom(0)).Outfit);
        Assert.Null(ModOutfitBuilder.SelectRandom([], new AnywearSettings(), new FixedRandom(0)).Outfit);
    }

    [Fact]
    public void ModeE_DoesNotSelectGlamourerDesigns()
    {
        var settings = new AnywearSettings { Mode = SelectionMode.PenumbraMods, FixedDesignId = Guid.NewGuid() };
        Assert.False(OutfitSelectionService.Select(settings, new HashSet<Guid> { settings.FixedDesignId }, null,
            SharedRandomSource.Instance).HasDesign);
    }

    [Fact]
    public void ModSettings_DefaultAndSanitize()
    {
        var s = new AnywearSettings();
        Assert.Equal(1, s.MinimumModPieces);
        Assert.Empty(s.ExcludedModDirectories);
        Assert.Equal(string.Empty, s.LastAppliedModDirectory);

        s.MinimumModPieces       = 0;
        s.ExcludedModDirectories = null!;
        s.LastAppliedModDirectory = null!;
        Assert.True(SettingsMigrator.Sanitize(s));
        Assert.Equal(1, s.MinimumModPieces);
        Assert.NotNull(s.ExcludedModDirectories);
        Assert.Equal(string.Empty, s.LastAppliedModDirectory);
    }
}
