namespace AnywearNumbra.Models;

/// <summary>
/// Broad territory categories. The plugin derives these from the game's TerritoryIntendedUse value
/// (TerritoryType sheet) and ContentFinderCondition; nothing here is a hardcoded list of territory IDs.
/// </summary>
public enum TerritoryCategory
{
    Unknown = 0,
    City = 1,
    Overworld = 2,
    Housing = 3,
    Dungeon = 4,
    Trial = 5,
    Raid = 6,
    AllianceRaid = 7,

    /// <summary> Any other content with a duty finder entry (for example PvP or guildhests). </summary>
    OtherDuty = 8,
}

/// <summary> What a rule matches on. Lower values take precedence. </summary>
public enum RuleKind
{
    /// <summary> One specific territory. Highest precedence. </summary>
    ExactTerritory = 0,

    /// <summary> A duty type (a ContentType row, for example "Dungeons" or "Trials"). </summary>
    DutyType = 1,

    /// <summary> A general category such as City or Overworld. </summary>
    Category = 2,
}

/// <summary> A zone-specific rule for selection Mode C. </summary>
public sealed class TerritoryRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public RuleKind Kind { get; set; } = RuleKind.Category;

    /// <summary> TerritoryType row id, used when <see cref="Kind"/> is ExactTerritory. </summary>
    public uint TerritoryId { get; set; }

    /// <summary> ContentType row id, used when <see cref="Kind"/> is DutyType. </summary>
    public uint ContentTypeId { get; set; }

    /// <summary> Category, used when <see cref="Kind"/> is Category. </summary>
    public TerritoryCategory Category { get; set; } = TerritoryCategory.City;

    /// <summary> The Glamourer design GUID to apply. </summary>
    public Guid DesignId { get; set; }

    /// <summary> Cached display text for the matched territory or duty type. Display-only. </summary>
    public string TargetLabel { get; set; } = string.Empty;
}

/// <summary> Everything the selection logic needs to know about a territory, resolved from game data. </summary>
public sealed record TerritoryInfo(
    uint TerritoryId,
    string Name,
    TerritoryCategory Category,
    uint ContentFinderConditionId,
    uint ContentTypeId,
    string ContentTypeName)
{
    /// <summary> True for content entered through the duty system. </summary>
    public bool IsDuty
        => ContentFinderConditionId != 0
         || Category is TerritoryCategory.Dungeon or TerritoryCategory.Trial or TerritoryCategory.Raid
                or TerritoryCategory.AllianceRaid or TerritoryCategory.OtherDuty;

    public static TerritoryInfo Unknown(uint territoryId)
        => new(territoryId, $"Territory {territoryId}", TerritoryCategory.Unknown, 0, 0, string.Empty);
}
