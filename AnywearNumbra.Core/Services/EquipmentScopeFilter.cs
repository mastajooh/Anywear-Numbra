using System.Text.Json.Nodes;
using AnywearNumbra.Models;

namespace AnywearNumbra.Services;

/// <summary> Which optional categories may be taken from a design. All default to false. </summary>
public readonly record struct ScopeOptions(bool Weapons, bool Facewear, bool HatVisorVisibility)
{
    public static ScopeOptions EquipmentOnly
        => default;

    public static ScopeOptions FromSettings(AnywearSettings settings)
        => new(settings.ApplyWeapons, settings.ApplyFacewear, settings.ApplyHatVisorVisibility);
}

/// <summary>
/// Glamourer's ApplyFlag values, mirrored from Glamourer.Api/Enums/ApplyFlag.cs (Glamourer API 1.x):
/// Once = 0x01, Equipment = 0x02, Customization = 0x04, Lock = 0x08.
/// </summary>
[Flags]
public enum GlamourerApplyFlags : ulong
{
    None = 0,
    Once = 0x01,
    Equipment = 0x02,
    Customization = 0x04,
    Lock = 0x08,
}

public enum MergeStatus
{
    /// <summary> A merged state with at least one permitted field to apply was produced. </summary>
    Ok,

    /// <summary> The design does not set any field that the current scope permits. Nothing should be sent. </summary>
    NothingToApply,

    /// <summary> The current Glamourer state could not be understood. Nothing should be sent. </summary>
    InvalidCurrentState,

    /// <summary> The design data could not be understood. Nothing should be sent. </summary>
    InvalidDesign,

    /// <summary> The character or the design is not a human model (for example a transformation). </summary>
    NonHuman,

    /// <summary> Glamourer returned a state file version this plugin was not written against. </summary>
    UnsupportedFormat,
}

/// <summary> Outcome of <see cref="EquipmentScopeFilter.Merge"/>. </summary>
public sealed class MergeResult
{
    public required MergeStatus Status { get; init; }
    public JsonObject? State { get; init; }
    public IReadOnlyList<string> AppliedFields { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// True when a visibility toggle (hat, visor, weapon) is applied. Glamourer drops all visibility
    /// toggles when the Customization flag is absent (DesignConverter.FromJsonElement calls
    /// ApplicationCollection.RemoveCustomize, which keeps only the Wetness meta flag), so the flag is
    /// required in that case. Customizations themselves stay protected by their Apply=false fields.
    /// </summary>
    public bool RequiresCustomizationFlag { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool IsSuccess
        => Status is MergeStatus.Ok;
}

/// <summary>
/// The single definition of what Anywear Numbra may change ("Path B").
/// <para>
/// Glamourer's own Equipment apply flag covers weapons, the bonus (facewear) slot, crests and
/// hat/visor/weapon/ear visibility at once, so it cannot be used to limit scope (Path A ruled out).
/// Instead this filter builds a complete state object from the character's CURRENT Glamourer state,
/// sets every "Apply" marker in it to false, and then copies only permitted fields from the design,
/// marking just those as applied. Glamourer's DesignBase.LoadDesignBase honors these per-field markers,
/// so everything else is never touched by the application — it is not overwritten and restored.
/// </para>
/// <para>
/// The produced object has no "Identifier" key, so Glamourer loads it as a plain DesignBase: no design
/// links are merged and no mod associations are applied. Advanced dyes ("Materials") are removed because
/// their keys use an internal encoding that also covers hair, face and body textures.
/// </para>
/// This class is pure: no Dalamud, no IPC, no game state.
/// </summary>
public static class EquipmentScopeFilter
{
    /// <summary> The only Glamourer state file version this filter was written against (DesignBase.FileVersion). </summary>
    public const int SupportedStateFileVersion = 1;

    /// <summary> Always-permitted equipment slots, as Glamourer names them in JSON (EquipSlot member names). </summary>
    public static readonly IReadOnlyList<string> ArmorSlots =
        ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary> Weapon slots, permitted only with <see cref="ScopeOptions.Weapons"/>. </summary>
    public static readonly IReadOnlyList<string> WeaponSlots = ["MainHand", "OffHand"];

    /// <summary> The facewear bonus slot (BonusItemFlag.Glasses), permitted only with <see cref="ScopeOptions.Facewear"/>. </summary>
    public const string FacewearSlot = "Glasses";

    public const string HatMeta = "Hat";
    public const string VisorMeta = "Visor";
    public const string WeaponMeta = "Weapon";

    /// <summary> Viera ear visibility. Treated as a body feature: never applied. </summary>
    public const string EarMeta = "VieraEars";

    internal const string EquipmentKey = "Equipment";
    internal const string BonusKey = "Bonus";
    internal const string CustomizeKey = "Customize";
    internal const string ParametersKey = "Parameters";

    /// <summary> Top-level keys kept in the merged state. Everything else (Materials, Identifier, Mods, Links...) is dropped. </summary>
    public static readonly IReadOnlyList<string> RetainedTopLevelKeys =
        ["FileVersion", EquipmentKey, BonusKey, CustomizeKey, ParametersKey];

    private static readonly string[] SectionsWithApplyMarkers = [EquipmentKey, BonusKey, CustomizeKey, ParametersKey];

    /// <summary> The equipment slots the given options permit. </summary>
    public static IReadOnlyList<string> PermittedEquipmentSlots(ScopeOptions options)
    {
        if (!options.Weapons)
            return ArmorSlots;

        var slots = new List<string>(ArmorSlots.Count + WeaponSlots.Count);
        slots.AddRange(ArmorSlots);
        slots.AddRange(WeaponSlots);
        return slots;
    }

    /// <summary> The flags to send with the merged state. Always Once (never Glamourer's material/parameter reset path). </summary>
    public static GlamourerApplyFlags ComputeApplyFlags(MergeResult merge, bool lockState)
    {
        var flags = GlamourerApplyFlags.Once | GlamourerApplyFlags.Equipment;
        if (merge.RequiresCustomizationFlag)
            flags |= GlamourerApplyFlags.Customization;
        if (lockState)
            flags |= GlamourerApplyFlags.Lock;
        return flags;
    }

    /// <summary>
    /// Build the state to send to Glamourer: <paramref name="currentState"/> with every Apply marker cleared,
    /// plus the permitted fields of <paramref name="design"/>. Inputs are not modified.
    /// </summary>
    public static MergeResult Merge(JsonObject currentState, JsonObject design, ScopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(design);

        // ---- Validate the current state (fail closed) ----
        if (!TryReadLong(currentState["FileVersion"], out var version) || version != SupportedStateFileVersion)
            return Fail(MergeStatus.UnsupportedFormat,
                $"Glamourer returned state file version '{currentState["FileVersion"]?.ToJsonString() ?? "none"}'; only version {SupportedStateFileVersion} is supported.");

        if (currentState[EquipmentKey] is not JsonObject currentEquipment || currentState[CustomizeKey] is not JsonObject currentCustomize)
            return Fail(MergeStatus.InvalidCurrentState, "The current Glamourer state has no Equipment or Customize section.");

        if (currentEquipment.ContainsKey("Array") || IsNonHumanModel(currentCustomize))
            return Fail(MergeStatus.NonHuman, "Your character is currently not a human model (for example transformed); nothing was applied.");

        foreach (var slot in ArmorSlots)
        {
            if (currentEquipment[slot] is not JsonObject)
                return Fail(MergeStatus.InvalidCurrentState, $"The current Glamourer state is missing the '{slot}' slot.");
        }

        // ---- Validate the design ----
        if (design[EquipmentKey] is not JsonObject designEquipment)
            return Fail(MergeStatus.InvalidDesign, "The design has no equipment data.");

        if (designEquipment.ContainsKey("Array") || (design[CustomizeKey] is JsonObject designCustomize && IsNonHumanModel(designCustomize)))
            return Fail(MergeStatus.NonHuman, "The design is for a non-human model; its equipment cannot be applied to a human character.");

        // ---- Build the neutral base: current values, nothing marked as applied ----
        var result = new JsonObject();
        foreach (var key in RetainedTopLevelKeys)
        {
            if (currentState[key] is { } node)
                result[key] = node.DeepClone();
        }

        ClearApplyMarkers(result);

        var applied = new List<string>();
        var notes = new List<string>();
        var needsCustomizationFlag = false;
        var resultEquipment = (JsonObject)result[EquipmentKey]!;

        // ---- Overlay permitted equipment slots (item, dyes, crest travel with their slot) ----
        foreach (var slot in PermittedEquipmentSlots(options))
            OverlaySlot(resultEquipment, designEquipment, slot, applied, notes);

        // ---- Optional visibility toggles ----
        if (options.HatVisorVisibility)
        {
            needsCustomizationFlag |= OverlayMeta(resultEquipment, designEquipment, HatMeta, "Show", applied);
            needsCustomizationFlag |= OverlayMeta(resultEquipment, designEquipment, VisorMeta, "IsToggled", applied);
        }

        if (options.Weapons)
            needsCustomizationFlag |= OverlayMeta(resultEquipment, designEquipment, WeaponMeta, "Show", applied);

        // ---- Optional facewear ----
        if (options.Facewear && design[BonusKey] is JsonObject designBonus && designBonus[FacewearSlot] is JsonObject designGlasses
         && ReadBool(designGlasses, "Apply"))
        {
            if (result[BonusKey] is not JsonObject resultBonus)
            {
                resultBonus = new JsonObject();
                result[BonusKey] = resultBonus;
            }

            if (resultBonus[FacewearSlot] is not JsonObject resultGlasses)
            {
                resultGlasses = new JsonObject();
                resultBonus[FacewearSlot] = resultGlasses;
            }

            CopyOrRemove(designGlasses, resultGlasses, "BonusId");
            resultGlasses["Apply"] = true;
            applied.Add($"{FacewearSlot}.Item");
        }

        if (applied.Count == 0)
        {
            return new MergeResult
            {
                Status = MergeStatus.NothingToApply,
                Notes = notes,
                Message = "The design does not set any equipment that Anywear Numbra is allowed to apply with the current scope settings.",
            };
        }

        return new MergeResult
        {
            Status = MergeStatus.Ok,
            State = result,
            AppliedFields = applied,
            Notes = notes,
            RequiresCustomizationFlag = needsCustomizationFlag,
            Message = $"Applying {applied.Count} equipment field(s).",
        };
    }

    /// <summary>
    /// Compare the fields that were applied in <paramref name="appliedState"/> with a state read later.
    /// Returns the applied fields whose values no longer match (for example because Glamourer Automation
    /// or another plugin re-applied something afterwards).
    /// </summary>
    public static IReadOnlyList<string> FindOverriddenFields(JsonObject appliedState, JsonObject laterState)
    {
        var overridden = new List<string>();
        if (appliedState[EquipmentKey] is JsonObject appliedEquipment && laterState[EquipmentKey] is JsonObject laterEquipment)
        {
            foreach (var (key, node) in appliedEquipment)
            {
                if (node is not JsonObject applied || laterEquipment[key] is not JsonObject later)
                    continue;

                if (key is HatMeta or WeaponMeta or VisorMeta)
                {
                    var valueKey = key is VisorMeta ? "IsToggled" : "Show";
                    if (ReadBool(applied, "Apply") && ReadBool(applied, valueKey) != ReadBool(later, valueKey))
                        overridden.Add(key);
                    continue;
                }

                if (ReadBool(applied, "Apply") && !ScalarEquals(applied["ItemId"], later["ItemId"]))
                    overridden.Add($"{key}.Item");
                if (ReadBool(applied, "ApplyStain") && StainSignature(applied) != StainSignature(later))
                    overridden.Add($"{key}.Dye");
                if (ReadBool(applied, "ApplyCrest") && ReadBool(applied, "Crest") != ReadBool(later, "Crest"))
                    overridden.Add($"{key}.Crest");
            }
        }

        if (appliedState[BonusKey] is JsonObject appliedBonus && appliedBonus[FacewearSlot] is JsonObject appliedGlasses
         && ReadBool(appliedGlasses, "Apply"))
        {
            var laterGlasses = (laterState[BonusKey] as JsonObject)?[FacewearSlot] as JsonObject;
            if (laterGlasses is null || !ScalarEquals(appliedGlasses["BonusId"], laterGlasses["BonusId"]))
                overridden.Add($"{FacewearSlot}.Item");
        }

        return overridden;
    }

    // ------------------------------------------------------------------------------------------------

    private static MergeResult Fail(MergeStatus status, string message)
        => new() { Status = status, Message = message };

    private static bool IsNonHumanModel(JsonObject customize)
        => customize.ContainsKey("Array") || (TryReadLong(customize["ModelId"], out var modelId) && modelId != 0);

    /// <summary> Set Apply (and, for equipment items, ApplyStain/ApplyCrest) to false on every entry of every section. </summary>
    private static void ClearApplyMarkers(JsonObject state)
    {
        foreach (var sectionKey in SectionsWithApplyMarkers)
        {
            if (state[sectionKey] is not JsonObject section)
                continue;

            foreach (var (_, child) in section)
            {
                if (child is not JsonObject entry)
                    continue;

                entry["Apply"] = false;
                if (sectionKey == EquipmentKey && entry.ContainsKey("ItemId"))
                {
                    entry["ApplyStain"] = false;
                    entry["ApplyCrest"] = false;
                }
            }
        }
    }

    private static void OverlaySlot(JsonObject resultEquipment, JsonObject designEquipment, string slot, List<string> applied,
        List<string> notes)
    {
        // Undefined in the design: leave the slot exactly as it is.
        if (designEquipment[slot] is not JsonObject design)
            return;

        if (resultEquipment[slot] is not JsonObject target)
        {
            notes.Add($"Slot '{slot}' is missing from the current state and was skipped.");
            return;
        }

        if (ReadBool(design, "Apply"))
        {
            // Includes an explicit "nothing" item when the design applies an empty slot.
            CopyOrRemove(design, target, "ItemId");
            target["Apply"] = true;
            applied.Add($"{slot}.Item");
        }

        if (ReadBool(design, "ApplyStain"))
        {
            foreach (var key in target.Select(p => p.Key).Where(IsStainKey).ToList())
                target.Remove(key);
            foreach (var (key, value) in design)
            {
                if (IsStainKey(key))
                    target[key] = value?.DeepClone();
            }

            target["ApplyStain"] = true;
            applied.Add($"{slot}.Dye");
        }

        if (ReadBool(design, "ApplyCrest"))
        {
            target["Crest"] = ReadBool(design, "Crest");
            target["ApplyCrest"] = true;
            applied.Add($"{slot}.Crest");
        }
    }

    /// <returns> True if the toggle was applied. </returns>
    private static bool OverlayMeta(JsonObject resultEquipment, JsonObject designEquipment, string meta, string valueKey,
        List<string> applied)
    {
        if (designEquipment[meta] is not JsonObject design || !ReadBool(design, "Apply"))
            return false;

        if (resultEquipment[meta] is not JsonObject target)
        {
            target = new JsonObject();
            resultEquipment[meta] = target;
        }

        CopyOrRemove(design, target, valueKey);
        target["Apply"] = true;
        applied.Add(meta);
        return true;
    }

    private static void CopyOrRemove(JsonObject from, JsonObject to, string key)
    {
        if (from[key] is { } value)
            to[key] = value.DeepClone();
        else
            to.Remove(key);
    }

    internal static bool IsStainKey(string key)
        => key == "Stain" || (key.Length > 5 && key.StartsWith("Stain", StringComparison.Ordinal) && key[5..].All(char.IsAsciiDigit));

    private static string StainSignature(JsonObject entry)
    {
        // Glamourer treats a missing stain key as stain 0.
        var parts = entry.Where(p => IsStainKey(p.Key))
            .Select(p => (p.Key, Value: TryReadLong(p.Value, out var v) ? v : 0))
            .Where(p => p.Value != 0)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}");
        return string.Join(";", parts);
    }

    internal static bool ReadBool(JsonObject entry, string key)
        => entry[key] is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    internal static bool TryReadLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        if (v.TryGetValue(out long l))
        {
            value = l;
            return true;
        }

        if (v.TryGetValue(out ulong ul) && ul <= long.MaxValue)
        {
            value = (long)ul;
            return true;
        }

        return false;
    }

    private static bool ScalarEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue(out decimal da) && vb.TryGetValue(out decimal db))
                return da == db;
            if (va.TryGetValue(out bool ba) && vb.TryGetValue(out bool bb))
                return ba == bb;
        }

        return a.ToJsonString() == b.ToJsonString();
    }
}
