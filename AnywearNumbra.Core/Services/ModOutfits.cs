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

    public static OutfitSource FromMod(ModOutfit outfit)
        => new(Guid.Empty, ModOutfitBuilder.ToDesign(outfit), outfit.ModName);
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

    /// <summary> A minimal Glamourer-shaped design: only the outfit's slots, items only (dyes stay as they are). </summary>
    public static JsonObject ToDesign(ModOutfit outfit)
    {
        var equipment = new JsonObject();
        foreach (var (slot, itemId) in outfit.Slots)
            equipment[slot] = new JsonObject { ["ItemId"] = itemId, ["Apply"] = true };

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
