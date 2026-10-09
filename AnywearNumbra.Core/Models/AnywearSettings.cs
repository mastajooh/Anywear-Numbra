namespace AnywearNumbra.Models;

/// <summary> How the next design is chosen after a transition. </summary>
public enum SelectionMode
{
    /// <summary> Mode A: random pick from eligible designs, avoiding an immediate repeat. </summary>
    Random = 0,

    /// <summary> Mode B: sequential rotation through eligible designs, persisted across restarts. </summary>
    Rotation = 1,

    /// <summary> Mode C: per-territory, per-duty-type and per-category rules with a fallback. </summary>
    ZoneRules = 2,

    /// <summary> Mode D: always the same design. </summary>
    Fixed = 3,

    /// <summary> Mode E: a random enabled Penumbra mod that changes armor; all armor pieces it changes are worn. </summary>
    PenumbraMods = 4,
}

/// <summary> Random dye behavior for Penumbra mod outfits. </summary>
public enum RandomDyeMode
{
    /// <summary> Keep the dyes you are already wearing. </summary>
    Off = 0,

    /// <summary> One random color on every piece of the outfit. </summary>
    SameColor = 1,

    /// <summary> A different random color on each piece. </summary>
    PerPiece = 2,
}

/// <summary> A Glamourer design known to the plugin, keyed by its stable GUID. </summary>
public sealed class OutfitEntry
{
    /// <summary> The Glamourer design GUID. This is the primary key; names are display-only. </summary>
    public Guid DesignId { get; set; }

    /// <summary>
    /// Whether the design takes part in Random and Rotation selection.
    /// Newly discovered designs start as not eligible, so nothing is worn until the user opts designs in.
    /// </summary>
    public bool Eligible { get; set; }

    /// <summary> Last display name seen from Glamourer, used only to label missing designs. </summary>
    public string LastKnownName { get; set; } = string.Empty;
}

/// <summary>
/// All persisted settings. Kept free of Dalamud types so defaults and migration are unit-testable.
/// The plugin's <c>Configuration</c> (which implements Dalamud's IPluginConfiguration) wraps this object.
/// </summary>
public sealed class AnywearSettings
{
    /// <summary> Schema version of this settings object. See <see cref="SettingsMigrator"/>. </summary>
    public const int CurrentVersion = 1;

    public int SettingsVersion { get; set; } = CurrentVersion;

    // ---- General ------------------------------------------------------------

    public bool AutomaticChangesEnabled { get; set; } = true;
    public bool TriggerOnZoneChange { get; set; } = true;
    public bool TriggerOnDutyEntry { get; set; } = true;
    public bool TriggerOnPostLoading { get; set; } = true;
    public SelectionMode Mode { get; set; } = SelectionMode.Random;

    /// <summary> Wait after loading finished before applying, in milliseconds. </summary>
    public int PostTransitionDelayMs { get; set; } = 1500;

    /// <summary> Additional attempts after the first one fails with a retryable error. </summary>
    public int RetryCount { get; set; } = 3;

    public int RetryIntervalMs { get; set; } = 2000;

    /// <summary> Signals for the same territory inside this window are treated as one transition. </summary>
    public int DuplicateWindowMs { get; set; } = 5000;

    public bool ShowNotifications { get; set; } = true;

    // ---- Appearance scope (all default OFF; see EquipmentScopeFilter) --------

    /// <summary> Also apply main hand, off hand and weapon visibility from the design. </summary>
    public bool ApplyWeapons { get; set; }

    /// <summary> Also apply the facewear (glasses) bonus slot from the design. </summary>
    public bool ApplyFacewear { get; set; }

    /// <summary> Also apply hat visibility and visor state from the design. </summary>
    public bool ApplyHatVisorVisibility { get; set; }

    /// <summary>
    /// Requested by users, but not supported: Glamourer only applies mod associations as part of a full
    /// design application, which would break the equipment-only guarantee. Kept so the choice persists
    /// and the UI can explain the limitation. The plugin never reads this to change behavior.
    /// </summary>
    public bool ApplyDesignModAssociations { get; set; }

    // ---- Glamourer interplay ---------------------------------------------------

    /// <summary> Lock the Glamourer state with this plugin's key after applying, to hold off Glamourer Automation. </summary>
    public bool LockStateAfterApply { get; set; }

    /// <summary> How long a lock is held before it is released automatically. </summary>
    public int LockDurationSeconds { get; set; } = 30;

    /// <summary> Re-read the state shortly after applying to detect another source overriding the outfit. </summary>
    public bool VerifyAfterApply { get; set; } = true;

    public int VerifyDelayMs { get; set; } = 4000;

    // ---- Outfits ---------------------------------------------------------------

    public List<OutfitEntry> Outfits { get; set; } = new();
    public Guid FixedDesignId { get; set; }
    public Guid FallbackDesignId { get; set; }
    public List<TerritoryRule> Rules { get; set; } = new();

    // ---- Penumbra mod outfits (Mode E) ------------------------------------------

    /// <summary> Mod directories the user unticked; they are never picked in Mode E. </summary>
    public List<string> ExcludedModDirectories { get; set; } = new();

    /// <summary> Only mods that change at least this many armor slots are picked. </summary>
    public int MinimumModPieces { get; set; } = 1;

    // Slots emptied when the picked mod does not change them (so the previous outfit's pieces don't linger).
    // Separate bools rather than a list: Dalamud's JSON loader appends to list defaults instead of replacing them.
    public bool ModClearHead { get; set; } = true;
    public bool ModClearHands { get; set; }
    public bool ModClearEars { get; set; } = true;
    public bool ModClearNeck { get; set; } = true;
    public bool ModClearWrists { get; set; } = true;
    public bool ModClearRings { get; set; } = true;

    /// <summary> Random dyes for Mode E outfits. </summary>
    public RandomDyeMode ModDyeMode { get; set; } = RandomDyeMode.Off;

    /// <summary> Also give the second dye channel a random color. </summary>
    public bool ModDyeSecondChannel { get; set; } = true;

    /// <summary> Directory of the mod applied last in Mode E, or empty when the last outfit was a Glamourer design. </summary>
    public string LastAppliedModDirectory { get; set; } = string.Empty;

    // ---- Persisted runtime state -------------------------------------------------

    public Guid LastAppliedDesignId { get; set; }
    public string LastAppliedDesignName { get; set; } = string.Empty;
    public DateTime? LastAppliedAtUtc { get; set; }

    /// <summary> Position in the eligible list of the design last applied by rotation bookkeeping. </summary>
    public int RotationIndex { get; set; }

    /// <summary> GUID of the design the rotation last landed on. </summary>
    public Guid RotationLastDesignId { get; set; }
}
