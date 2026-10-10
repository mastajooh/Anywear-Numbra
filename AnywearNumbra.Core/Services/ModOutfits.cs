using System.Text.Json.Nodes;
using AnywearNumbra.Models;

namespace AnywearNumbra.Services;

/// <summary>
/// What to apply: either a Glamourer design (by GUID, read through Glamourer) or ready-made design data
/// (a Penumbra mod outfit). Both go through the same scoped application.
/// </summary>
public readonly record struct OutfitSource(Guid DesignId, JsonObject? Data, string Name)
{
    public static OutfitSource FromDesign(Guid designId, string name)
        => new(designId, null, name);

    public static OutfitSource FromMod(ModOutfit outfit, ModDesignOptions? options = null)
        => new(Guid.Empty, ModOutfitBuilder.ToDesign(outfit, options), outfit.ModName);
}

/// <summary> How a mod outfit becomes a design: which missing slots to empty and how to dye it. </summary>
public sealed record ModDesignOptions(
    IReadOnlyCollection<string> ClearSlots,
    RandomDyeMode DyeMode,
    bool DyeSecondChannel,
    IReadOnlyList<byte> Stains,
    IRandomSource Random)
{
    /// <summary> Slots to empty, from settings. Rings cover both ring slots. </summary>
    public static IReadOnlyCollection<string> ClearSlotsFrom(AnywearSettings s)
    {
        var slots = new List<string>();
        if (s.ModClearHead)
            slots.Add("Head");
        if (s.ModClearHands)
            slots.Add("Hands");
        if (s.ModClearLegs)
            slots.Add("Legs");
        if (s.ModClearFeet)
            slots.Add("Feet");
        if (s.ModClearEars)
            slots.Add("Ears");
        if (s.ModClearNeck)
            slots.Add("Neck");
        if (s.ModClearWrists)
            slots.Add("Wrists");
        if (s.ModClearRings)
        {
            slots.Add("RFinger");
            slots.Add("LFinger");
        }

        return slots;
    }

    public static ModDesignOptions FromSettings(AnywearSettings s, IReadOnlyList<byte> stains, IRandomSource random)
        => new(ClearSlotsFrom(s), s.ModDyeMode, s.ModDyeSecondChannel, stains, random);
}

/// <summary> The armor a single Penumbra mod changes, as Glamourer slot name to Glamourer (custom) item id. </summary>
public sealed record ModOutfit(string ModDirectory, string ModName, IReadOnlyDictionary<string, ulong> Slots)
{
    public int PieceCount
        => Slots.Count;
}

/// <summary>
/// Turns the items a Penumbra mod changes into an outfit, and an outfit into a design-shaped JSON object that
/// goes through <see cref="EquipmentScopeFilter"/> exactly like a Glamourer design. Pure: no Dalamud, no IPC.
/// </summary>
public static class ModOutfitBuilder
{
    /// <summary>
    /// Penumbra.GameData FullEquipType names of armor, mapped to Glamourer's JSON slot names.
    /// Rings ("Finger") fill RFinger first, then LFinger. Weapons, facewear and everything else are ignored.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TypeToSlot = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Head"]   = "Head",
        ["Body"]   = "Body",
        ["Hands"]  = "Hands",
        ["Legs"]   = "Legs",
        ["Feet"]   = "Feet",
        ["Ears"]   = "Ears",
        ["Neck"]   = "Neck",
        ["Wrists"] = "Wrists",
    };

    /// <summary> Build the outfit from (FullEquipType name, custom item id) pairs. The first item per slot wins. </summary>
    public static ModOutfit Build(string modDirectory, string modName, IEnumerable<(string Type, ulong ItemId)> items)
    {
        var slots = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (type, itemId) in items.OrderBy(i => i.ItemId))
        {
            if (itemId == 0)
                continue;

            if (type == "Finger")
            {
                if (!slots.ContainsKey("RFinger"))
                    slots["RFinger"] = itemId;
                else if (!slots.ContainsKey("LFinger") && slots["RFinger"] != itemId)
                    slots["LFinger"] = itemId;
                continue;
            }

            if (TypeToSlot.TryGetValue(type, out var slot) && !slots.ContainsKey(slot))
                slots[slot] = itemId;
        }

        return new ModOutfit(modDirectory, modName, slots);
    }

    /// <summary>
    /// Slots that may be emptied when a mod does not change them. The body slot never is. An empty legs or feet slot
    /// shows the character's default smallclothes (bare legs / bare feet).
    /// </summary>
    public static readonly IReadOnlyList<string> ClearableSlots = ["Head", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary>
    /// Glamourer's "Nothing" item for an armor slot: ItemManager.NothingId(slot) = uint.MaxValue - 128 - (uint)slot.ToSlot(),
    /// with EquipSlot values Head 3, Body 4, Hands 5, Legs 7, Feet 8, Ears 9, Neck 10, Wrists 11, RFinger 12 (LFinger maps to RFinger).
    /// </summary>
    public static ulong NothingItemId(string slot)
    {
        uint slotValue = slot switch
        {
            "Head"                 => 3,
            "Body"                 => 4,
            "Hands"                => 5,
            "Legs"                 => 7,
            "Feet"                 => 8,
            "Ears"                 => 9,
            "Neck"                 => 10,
            "Wrists"               => 11,
            "RFinger" or "LFinger" => 12,
            _                      => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not an armor slot."),
        };
        return uint.MaxValue - 128u - slotValue;
    }

    /// <summary>
    /// A minimal Glamourer-shaped design: the outfit's slots (items, plus random dyes if enabled), and explicit
    /// "Nothing" for chosen slots the mod does not change. Everything else is left out, i.e. stays as it is.
    /// </summary>
    public static JsonObject ToDesign(ModOutfit outfit, ModDesignOptions? options = null)
    {
        var equipment = new JsonObject();
        var stains    = options?.Stains.Where(s => s != 0).ToList() ?? new List<byte>();
        var dye       = options is { DyeMode: not RandomDyeMode.Off } && stains.Count > 0;
        byte outfitDye1 = 0, outfitDye2 = 0;
        if (dye && options!.DyeMode is RandomDyeMode.SameColor)
        {
            outfitDye1 = stains[options.Random.Next(stains.Count)];
            outfitDye2 = stains[options.Random.Next(stains.Count)];
        }

        foreach (var (slot, itemId) in outfit.Slots)
        {
            var entry = new JsonObject { ["ItemId"] = itemId, ["Apply"] = true };
            if (dye)
            {
                var perPiece = options!.DyeMode is RandomDyeMode.PerPiece;
                entry["Stain"]  = perPiece ? stains[options.Random.Next(stains.Count)] : outfitDye1;
                entry["Stain2"] = options.DyeSecondChannel ? perPiece ? stains[options.Random.Next(stains.Count)] : outfitDye2 : (byte)0;
                entry["ApplyStain"] = true;
            }

            equipment[slot] = entry;
        }

        if (options is not null)
        {
            foreach (var slot in options.ClearSlots)
            {
                if (!ClearableSlots.Contains(slot) || outfit.Slots.ContainsKey(slot))
                    continue;

                equipment[slot] = new JsonObject { ["ItemId"] = NothingItemId(slot), ["Apply"] = true, ["Stain"] = (byte)0, ["Stain2"] = (byte)0, ["ApplyStain"] = true };
            }
        }

        // Round-trip so values behave exactly like data parsed from Glamourer.
        var design = new JsonObject { ["FileVersion"] = EquipmentScopeFilter.SupportedStateFileVersion, ["Equipment"] = equipment };
        return (JsonObject)JsonNode.Parse(design.ToJsonString())!;
    }

    /// <summary> Mods that may be picked: not excluded and changing at least the minimum number of pieces. </summary>
    public static IReadOnlyList<ModOutfit> Eligible(IReadOnlyList<ModOutfit> outfits, AnywearSettings settings)
    {
        var excluded = new HashSet<string>(settings.ExcludedModDirectories, StringComparer.Ordinal);
        return outfits
            .Where(o => o.PieceCount >= Math.Max(1, settings.MinimumModPieces) && !excluded.Contains(o.ModDirectory))
            .ToList();
    }

    /// <summary> Random pick that never repeats the previous mod when two or more are eligible. </summary>
    public static (ModOutfit? Outfit, string Reason) SelectRandom(IReadOnlyList<ModOutfit> outfits, AnywearSettings settings,
        IRandomSource random)
    {
        var eligible = Eligible(outfits, settings);
        switch (eligible.Count)
        {
            case 0:
                return (null, outfits.Count == 0
                    ? "No enabled Penumbra mods that change armor were found for your character's collection."
                    : "All mods that change armor are unticked or below the minimum piece count.");
            case 1: return (eligible[0], "Random mod (only one eligible)");
        }

        var candidates = eligible.Where(o => o.ModDirectory != settings.LastAppliedModDirectory).ToList();
        var pick       = Math.Clamp(random.Next(candidates.Count), 0, candidates.Count - 1);
        return (candidates[pick], $"Random mod ({eligible.Count} eligible)");
    }
}
