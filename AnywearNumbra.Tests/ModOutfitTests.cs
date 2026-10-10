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

    private sealed class SequenceRandom(params int[] values) : IRandomSource
    {
        private int _i;

        public int Next(int maxExclusive)
            => values[_i++ % values.Length] % maxExclusive;
    }

    [Fact]
    public void NothingItemIds_MatchGlamourersFormula()
    {
        Assert.Equal(4294967164UL, ModOutfitBuilder.NothingItemId("Head"));
        Assert.Equal(4294967162UL, ModOutfitBuilder.NothingItemId("Hands"));
        Assert.Equal(4294967160UL, ModOutfitBuilder.NothingItemId("Legs"));
        Assert.Equal(4294967159UL, ModOutfitBuilder.NothingItemId("Feet"));
        Assert.Equal(4294967158UL, ModOutfitBuilder.NothingItemId("Ears"));
        Assert.Equal(4294967157UL, ModOutfitBuilder.NothingItemId("Neck"));
        Assert.Equal(4294967156UL, ModOutfitBuilder.NothingItemId("Wrists"));
        Assert.Equal(4294967155UL, ModOutfitBuilder.NothingItemId("RFinger"));
        Assert.Equal(4294967155UL, ModOutfitBuilder.NothingItemId("LFinger"));
    }

    [Fact]
    public void MissingAccessories_AreEmptied_WhenTicked_AndLeftAloneOtherwise()
    {
        var current  = Build(seed: 2);
        var outfit   = ModOutfitBuilder.Build("dir", "Name", new[] { ("Body", 777UL), ("Hands", 778UL), ("Finger", 779UL) });
        var settings = new AnywearSettings(); // defaults: head, legs, feet, ears, neck, wrists, rings; not hands
        var options  = ModDesignOptions.FromSettings(settings, [], new SequenceRandom(0));
        var merged   = EquipmentScopeFilter.Merge(current, ModOutfitBuilder.ToDesign(outfit, options), ScopeOptions.EquipmentOnly).State!;

        foreach (var slot in new[] { "Head", "Legs", "Feet", "Ears", "Neck", "Wrists", "LFinger" })
        {
            Assert.Equal(ModOutfitBuilder.NothingItemId(slot).ToString(), Slot(merged, slot)["ItemId"]!.ToJsonString());
            Assert.True(Bool(Slot(merged, slot), "Apply"));
        }

        Assert.Equal("779", Slot(merged, "RFinger")["ItemId"]!.ToJsonString()); // the mod's ring stays
        Assert.Equal("778", Slot(merged, "Hands")["ItemId"]!.ToJsonString());

        // The body slot is never emptied, even when the mod has no body piece.
        var noBody = ModOutfitBuilder.Build("dir2", "NoBody", new[] { ("Feet", 900UL) });
        var kept   = EquipmentScopeFilter.Merge(current, ModOutfitBuilder.ToDesign(noBody, options), ScopeOptions.EquipmentOnly).State!;
        Assert.Equal(ValuesOnly(Slot(current, "Body")), ValuesOnly(Slot(kept, "Body")));
        Assert.False(Bool(Slot(kept, "Body"), "Apply"));
        Assert.Equal(ModOutfitBuilder.NothingItemId("Legs").ToString(), Slot(kept, "Legs")["ItemId"]!.ToJsonString()); // bare legs

        // With nothing ticked, missing slots keep what you wear.
        settings.ModClearHead = settings.ModClearEars = settings.ModClearNeck = settings.ModClearWrists = settings.ModClearRings = false;
        settings.ModClearLegs = settings.ModClearFeet = false;
        var keep = EquipmentScopeFilter.Merge(current,
            ModOutfitBuilder.ToDesign(outfit, ModDesignOptions.FromSettings(settings, [], new SequenceRandom(0))), ScopeOptions.EquipmentOnly).State!;
        foreach (var slot in new[] { "Head", "Legs", "Feet" })
        {
            Assert.Equal(ValuesOnly(Slot(current, slot)), ValuesOnly(Slot(keep, slot)));
            Assert.False(Bool(Slot(keep, slot), "Apply"));
        }
    }

    [Fact]
    public void RandomDyes_SameColor_UsesOneColorForEveryPiece()
    {
        var outfit  = ModOutfitBuilder.Build("dir", "Name", new[] { ("Body", 1UL), ("Legs", 2UL), ("Feet", 3UL) });
        var options = new ModDesignOptions([], RandomDyeMode.SameColor, true, new byte[] { 5, 6, 7, 8 }, new SequenceRandom(1, 3));
        var design  = ModOutfitBuilder.ToDesign(outfit, options);

        foreach (var slot in new[] { "Body", "Legs", "Feet" })
        {
            Assert.Equal("6", Slot(design, slot)["Stain"]!.ToJsonString());
            Assert.Equal("8", Slot(design, slot)["Stain2"]!.ToJsonString());
            Assert.True(Bool(Slot(design, slot), "ApplyStain"));
        }
    }

    [Fact]
    public void RandomDyes_PerPiece_DiffersPerPiece_AndSecondChannelCanStayEmpty()
    {
        var outfit  = ModOutfitBuilder.Build("dir", "Name", new[] { ("Body", 1UL), ("Legs", 2UL) });
        var options = new ModDesignOptions([], RandomDyeMode.PerPiece, false, new byte[] { 5, 6, 7 }, new SequenceRandom(0, 1, 2));
        var design  = ModOutfitBuilder.ToDesign(outfit, options);

        var dyes = new[] { "Body", "Legs" }.Select(slot => Slot(design, slot)["Stain"]!.ToJsonString()).ToList();
        Assert.NotEqual(dyes[0], dyes[1]);
        Assert.Equal("0", Slot(design, "Body")["Stain2"]!.ToJsonString());
    }

    [Fact]
    public void RandomDyes_Off_LeavesDyesAlone()
    {
        var outfit = ModOutfitBuilder.Build("dir", "Name", new[] { ("Body", 1UL) });
        var design = ModOutfitBuilder.ToDesign(outfit, new ModDesignOptions([], RandomDyeMode.Off, true, new byte[] { 5 }, new SequenceRandom(0)));
        Assert.False(Slot(design, "Body").ContainsKey("Stain"));
        Assert.False(Bool(Slot(design, "Body"), "ApplyStain"));
    }
}
