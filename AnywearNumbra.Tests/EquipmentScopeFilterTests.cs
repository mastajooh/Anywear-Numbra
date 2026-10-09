using System.Text.Json.Nodes;
using AnywearNumbra.Services;
using static AnywearNumbra.Tests.GlamourerJsonFixtures;

namespace AnywearNumbra.Tests;

public class EquipmentScopeFilterTests
{
    private static readonly JsonObject Current = Build(seed: 2);   // what the character wears now
    private static readonly JsonObject Design = FullDesign(seed: 7); // a design that sets every field

    private static MergeResult MergeWith(ScopeOptions options, JsonObject? design = null, JsonObject? current = null)
    {
        var result = EquipmentScopeFilter.Merge(current ?? Current, design ?? Design, options);
        return result;
    }

    private static JsonObject State(MergeResult result)
    {
        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(result.State);
        return result.State!;
    }

    [Fact]
    public void PermittedArmorSlots_TakeTheDesignsItemsDyesAndCrests()
    {
        var merged = State(MergeWith(ScopeOptions.EquipmentOnly));
        foreach (var slot in ArmorSlots)
        {
            var target = Slot(merged, slot);
            var source = Slot(Design, slot);
            Assert.Equal(source["ItemId"]!.ToJsonString(), target["ItemId"]!.ToJsonString());
            Assert.Equal(source["Stain"]!.ToJsonString(), target["Stain"]!.ToJsonString());
            Assert.Equal(source["Stain2"]!.ToJsonString(), target["Stain2"]!.ToJsonString());
            Assert.Equal(Bool(source, "Crest"), Bool(target, "Crest"));
            Assert.True(Bool(target, "Apply"));
            Assert.True(Bool(target, "ApplyStain"));
            Assert.True(Bool(target, "ApplyCrest"));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public void HairFaceSkinBodyIdentityAndEffects_AreNeverMarkedAndKeepCurrentValuesByteForByte(bool weapons, bool facewear, bool visibility)
    {
        var merged = State(MergeWith(new ScopeOptions(weapons, facewear, visibility)));

        // Every customization value (hair, face, skin, body, race/clan/gender, wetness) is the CURRENT value...
        Assert.Equal(ValuesOnly(Current["Customize"]), ValuesOnly(merged["Customize"]));
        // ...and so is every customize parameter (skin/hair/lip colors etc.).
        Assert.Equal(ValuesOnly(Current["Parameters"]), ValuesOnly(merged["Parameters"]));

        // And none of them is marked to be applied.
        Assert.All(ApplyMarkers(merged["Customize"]), m => Assert.False(m.Value, m.Path));
        Assert.All(ApplyMarkers(merged["Parameters"]), m => Assert.False(m.Value, m.Path));

        // Viera ears are a body feature: never applied, value unchanged.
        Assert.False(Bool(Slot(merged, "VieraEars"), "Apply"));
        Assert.Equal(ValuesOnly(Slot(Current, "VieraEars")), ValuesOnly(Slot(merged, "VieraEars")));

        // Advanced dyes (which also cover hair/face/body textures), identity and design metadata are dropped.
        Assert.False(merged.ContainsKey("Materials"));
        Assert.False(merged.ContainsKey("Identifier"));
        Assert.False(merged.ContainsKey("Mods"));
        Assert.False(merged.ContainsKey("Links"));
        Assert.False(merged.ContainsKey("Name"));
    }

    [Fact]
    public void WeaponsFacewearAndVisibility_AreUnchangedAndUnmarked_WhenOptionsAreOff()
    {
        var merged = State(MergeWith(ScopeOptions.EquipmentOnly));

        foreach (var slot in new[] { "MainHand", "OffHand", "Hat", "Visor", "Weapon" })
        {
            Assert.Equal(ValuesOnly(Slot(Current, slot)), ValuesOnly(Slot(merged, slot)));
            Assert.All(ApplyMarkers(Slot(merged, slot)), m => Assert.False(m.Value, $"{slot}.{m.Path}"));
        }

        var glasses = (JsonObject)Section(merged, "Bonus")["Glasses"]!;
        Assert.Equal(ValuesOnly(Section(Current, "Bonus")), ValuesOnly(Section(merged, "Bonus")));
        Assert.False(Bool(glasses, "Apply"));
        Assert.False(MergeWith(ScopeOptions.EquipmentOnly).RequiresCustomizationFlag);
    }

    [Fact]
    public void Weapons_ChangeOnlyWhenEnabled()
    {
        var merged = State(MergeWith(new ScopeOptions(Weapons: true, Facewear: false, HatVisorVisibility: false)));
        foreach (var slot in new[] { "MainHand", "OffHand" })
        {
            Assert.Equal(Slot(Design, slot)["ItemId"]!.ToJsonString(), Slot(merged, slot)["ItemId"]!.ToJsonString());
            Assert.True(Bool(Slot(merged, slot), "Apply"));
        }

        Assert.True(Bool(Slot(merged, "Weapon"), "Apply"));
        Assert.Equal(Bool(Slot(Design, "Weapon"), "Show"), Bool(Slot(merged, "Weapon"), "Show"));
        // Hat/visor stay untouched: that is a separate option.
        Assert.False(Bool(Slot(merged, "Hat"), "Apply"));
        Assert.False(Bool(Slot(merged, "Visor"), "Apply"));
    }

    [Fact]
    public void Facewear_ChangesOnlyWhenEnabled_AndOnlyTheGlassesSlot()
    {
        var merged  = State(MergeWith(new ScopeOptions(Weapons: false, Facewear: true, HatVisorVisibility: false)));
        var bonus   = Section(merged, "Bonus");
        var glasses = (JsonObject)bonus["Glasses"]!;
        Assert.True(Bool(glasses, "Apply"));
        Assert.Equal(((JsonObject)Section(Design, "Bonus")["Glasses"]!)["BonusId"]!.ToJsonString(), glasses["BonusId"]!.ToJsonString());

        var other = (JsonObject)bonus["UnkSlot"]!;
        Assert.False(Bool(other, "Apply"));
        Assert.Equal(ValuesOnly(Section(Current, "Bonus")["UnkSlot"]), ValuesOnly(other));
    }

    [Fact]
    public void HatAndVisorVisibility_ChangeOnlyWhenEnabled_AndRequireTheCustomizationFlag()
    {
        var result = MergeWith(new ScopeOptions(Weapons: false, Facewear: false, HatVisorVisibility: true));
        var merged = State(result);
        Assert.True(Bool(Slot(merged, "Hat"), "Apply"));
        Assert.True(Bool(Slot(merged, "Visor"), "Apply"));
        Assert.Equal(Bool(Slot(Design, "Hat"), "Show"), Bool(Slot(merged, "Hat"), "Show"));
        Assert.Equal(Bool(Slot(Design, "Visor"), "IsToggled"), Bool(Slot(merged, "Visor"), "IsToggled"));
        Assert.False(Bool(Slot(merged, "Weapon"), "Apply"));

        Assert.True(result.RequiresCustomizationFlag);
        var flags = EquipmentScopeFilter.ComputeApplyFlags(result, lockState: false);
        Assert.True(flags.HasFlag(GlamourerApplyFlags.Customization));
        // Customization flag is safe only because every customization field is explicitly unmarked:
        Assert.All(ApplyMarkers(merged["Customize"]), m => Assert.False(m.Value, m.Path));
    }

    [Fact]
    public void UndefinedOrNotAppliedSlots_StayExactlyAsTheyAre()
    {
        var design = FullDesign(seed: 9);
        var equipment = Section(design, "Equipment");
        equipment.Remove("Hands");                        // undefined in the design
        ((JsonObject)equipment["Feet"]!)["Apply"] = false; // "do not apply" for the item
        ((JsonObject)equipment["Feet"]!)["ApplyStain"] = false;
        ((JsonObject)equipment["Feet"]!)["ApplyCrest"] = false;

        var merged = State(MergeWith(ScopeOptions.EquipmentOnly, design));
        foreach (var slot in new[] { "Hands", "Feet" })
        {
            Assert.Equal(ValuesOnly(Slot(Current, slot)), ValuesOnly(Slot(merged, slot)));
            Assert.All(ApplyMarkers(Slot(merged, slot)), m => Assert.False(m.Value, $"{slot}.{m.Path}"));
        }

        // Other slots still apply.
        Assert.True(Bool(Slot(merged, "Body"), "Apply"));
    }

    [Fact]
    public void ExplicitlyEmptySlot_IsApplied()
    {
        var design = FullDesign(seed: 9);
        const ulong nothingItem = 4294967164; // any id Glamourer uses for "nothing" in that slot; the filter just transports it
        ((JsonObject)Section(design, "Equipment")["Head"]!)["ItemId"] = nothingItem;

        var merged = State(MergeWith(ScopeOptions.EquipmentOnly, design));
        Assert.Equal(nothingItem.ToString(), Slot(merged, "Head")["ItemId"]!.ToJsonString());
        Assert.True(Bool(Slot(merged, "Head"), "Apply"));
    }

    [Fact]
    public void DyesAndCrests_TravelWithTheirSlot_AndFollowTheDesignsOwnMarkers()
    {
        var design = FullDesign(seed: 9);
        var body   = (JsonObject)Section(design, "Equipment")["Body"]!;
        body["ApplyStain"] = false;  // item applies, dye does not
        body.Remove("Stain2");       // missing Stain2 means "no second dye" to Glamourer
        var legs = (JsonObject)Section(design, "Equipment")["Legs"]!;
        legs["Apply"]      = false;  // dye applies, item does not
        legs["ApplyCrest"] = false;

        var merged = State(MergeWith(ScopeOptions.EquipmentOnly, design));
        var mBody  = Slot(merged, "Body");
        Assert.True(Bool(mBody, "Apply"));
        Assert.False(Bool(mBody, "ApplyStain"));
        Assert.Equal(Slot(Current, "Body")["Stain"]!.ToJsonString(), mBody["Stain"]!.ToJsonString());

        var mLegs = Slot(merged, "Legs");
        Assert.False(Bool(mLegs, "Apply"));
        Assert.Equal(Slot(Current, "Legs")["ItemId"]!.ToJsonString(), mLegs["ItemId"]!.ToJsonString());
        Assert.True(Bool(mLegs, "ApplyStain"));
        Assert.Equal(legs["Stain"]!.ToJsonString(), mLegs["Stain"]!.ToJsonString());
        Assert.False(Bool(mLegs, "ApplyCrest"));
    }

    [Fact]
    public void AppliedDyes_MirrorTheDesignsStainSetExactly()
    {
        var design = FullDesign(seed: 9);
        var head   = (JsonObject)Section(design, "Equipment")["Head"]!;
        head.Remove("Stain2");

        var merged = State(MergeWith(ScopeOptions.EquipmentOnly, design));
        Assert.False(Slot(merged, "Head").ContainsKey("Stain2"));
    }

    [Fact]
    public void InputsAreNotModified()
    {
        var current = Build(seed: 2);
        var design  = FullDesign(seed: 7);
        var before  = (current.ToJsonString(), design.ToJsonString());

        EquipmentScopeFilter.Merge(current, design, new ScopeOptions(true, true, true));

        Assert.Equal(before, (current.ToJsonString(), design.ToJsonString()));
    }

    [Fact]
    public void DesignWithoutPermittedFields_ProducesNothingToApply()
    {
        var design = Build(seed: 9, applyAll: false);
        var result = MergeWith(ScopeOptions.EquipmentOnly, design);
        Assert.Equal(MergeStatus.NothingToApply, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public void DesignThatOnlyChangesCustomization_ProducesNothingToApply()
    {
        var design = Build(seed: 9, applyAll: false);
        foreach (var (_, entry) in Section(design, "Customize"))
            ((JsonObject)entry!)["Apply"] = true;

        var result = MergeWith(new ScopeOptions(true, true, true), design);
        Assert.Equal(MergeStatus.NothingToApply, result.Status);
    }

    [Fact]
    public void NonHumanCharacter_IsRefused()
    {
        var current = Build(seed: 2);
        Section(current, "Customize")["ModelId"] = 1234;
        Assert.Equal(MergeStatus.NonHuman, MergeWith(ScopeOptions.EquipmentOnly, current: current).Status);
    }

    [Fact]
    public void NonHumanDesign_IsRefused()
    {
        var design = FullDesign(seed: 9);
        Section(design, "Customize")["ModelId"] = 1234;
        Assert.Equal(MergeStatus.NonHuman, MergeWith(ScopeOptions.EquipmentOnly, design).Status);
    }

    [Fact]
    public void UnknownStateFileVersion_IsRefused()
    {
        var current = Build(seed: 2);
        current["FileVersion"] = 2;
        Assert.Equal(MergeStatus.UnsupportedFormat, MergeWith(ScopeOptions.EquipmentOnly, current: current).Status);
    }

    [Fact]
    public void CurrentStateMissingASlot_IsRefused()
    {
        var current = Build(seed: 2);
        Section(current, "Equipment").Remove("Wrists");
        Assert.Equal(MergeStatus.InvalidCurrentState, MergeWith(ScopeOptions.EquipmentOnly, current: current).Status);
    }

    [Fact]
    public void ApplyFlags_AreAlwaysOnceAndEquipment_PlusLockOnlyWhenRequested()
    {
        var result = MergeWith(ScopeOptions.EquipmentOnly);
        Assert.Equal(GlamourerApplyFlags.Once | GlamourerApplyFlags.Equipment, EquipmentScopeFilter.ComputeApplyFlags(result, false));
        Assert.Equal(GlamourerApplyFlags.Once | GlamourerApplyFlags.Equipment | GlamourerApplyFlags.Lock,
            EquipmentScopeFilter.ComputeApplyFlags(result, true));

        // Values must match Glamourer.Api's ApplyFlag enum.
        Assert.Equal(0x01UL, (ulong)GlamourerApplyFlags.Once);
        Assert.Equal(0x02UL, (ulong)GlamourerApplyFlags.Equipment);
        Assert.Equal(0x04UL, (ulong)GlamourerApplyFlags.Customization);
        Assert.Equal(0x08UL, (ulong)GlamourerApplyFlags.Lock);
    }

    [Fact]
    public void PermittedSlotList_MatchesTheSpecification()
    {
        Assert.Equal(new[] { "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger" },
            EquipmentScopeFilter.PermittedEquipmentSlots(ScopeOptions.EquipmentOnly));
        Assert.Contains("MainHand", EquipmentScopeFilter.PermittedEquipmentSlots(new ScopeOptions(true, false, false)));
        Assert.DoesNotContain("MainHand", EquipmentScopeFilter.PermittedEquipmentSlots(new ScopeOptions(false, true, true)));
    }

    [Fact]
    public void Verification_FindsFieldsChangedAfterApplying()
    {
        var applied = State(MergeWith(ScopeOptions.EquipmentOnly));
        var same    = (JsonObject)applied.DeepClone();
        Assert.Empty(EquipmentScopeFilter.FindOverriddenFields(applied, same));

        var later = (JsonObject)applied.DeepClone();
        ((JsonObject)Section(later, "Equipment")["Body"]!)["ItemId"] = 1UL;
        ((JsonObject)Section(later, "Equipment")["Feet"]!)["Stain"]  = 99;
        var overridden = EquipmentScopeFilter.FindOverriddenFields(applied, later);
        Assert.Contains("Body.Item", overridden);
        Assert.Contains("Feet.Dye", overridden);

        // Changes to fields we did not apply are not our business.
        var unrelated = (JsonObject)applied.DeepClone();
        ((JsonObject)Section(unrelated, "Equipment")["MainHand"]!)["ItemId"] = 1UL;
        ((JsonObject)Section(unrelated, "Customize")["Hairstyle"]!)["Value"] = 1;
        Assert.Empty(EquipmentScopeFilter.FindOverriddenFields(applied, unrelated));
    }
}
