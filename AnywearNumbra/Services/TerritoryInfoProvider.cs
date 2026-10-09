using AnywearNumbra.Models;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TerritoryUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace AnywearNumbra.Services;

/// <summary>
/// Resolves territories from game data (Lumina sheets TerritoryType, ContentFinderCondition, ContentType).
/// Categories come from TerritoryType.TerritoryIntendedUse, interpreted with FFXIVClientStructs'
/// TerritoryIntendedUse enum, so no territory ID lists are hardcoded. Results are cached per territory.
/// </summary>
public sealed class TerritoryInfoProvider
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Dictionary<(uint Territory, uint Cfc), TerritoryInfo> _cache = new();
    private List<(uint Id, string Name)>? _contentTypes;
    private List<(uint Id, string Name)>? _territories;

    public TerritoryInfoProvider(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary> Resolve a territory. <paramref name="contentFinderCondition"/> may come from ZoneInit and overrides the sheet value. </summary>
    public TerritoryInfo Get(uint territoryId, uint contentFinderCondition = 0)
    {
        if (territoryId == 0)
            return TerritoryInfo.Unknown(0);

        var key = (territoryId, contentFinderCondition);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var info = Resolve(territoryId, contentFinderCondition);
        _cache[key] = info;
        return info;
    }

    /// <summary> All duty types (ContentType rows with a name), for the rule editor. </summary>
    public IReadOnlyList<(uint Id, string Name)> ContentTypes
    {
        get
        {
            if (_contentTypes is not null)
                return _contentTypes;

            var list = new List<(uint Id, string Name)>();
            try
            {
                foreach (var row in _data.GetExcelSheet<ContentType>())
                {
                    var name = row.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add((row.RowId, name));
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not read the ContentType sheet.");
            }

            _contentTypes = list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return _contentTypes;
        }
    }

    /// <summary> Territories that have a place name, for the rule editor. </summary>
    public IReadOnlyList<(uint Id, string Name)> Territories
    {
        get
        {
            if (_territories is not null)
                return _territories;

            var list = new List<(uint Id, string Name)>();
            try
            {
                foreach (var row in _data.GetExcelSheet<TerritoryType>())
                {
                    var name = row.PlaceName.ValueNullable?.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add((row.RowId, name));
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not read the TerritoryType sheet.");
            }

            _territories = list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id).ToList();
            return _territories;
        }
    }

    public string ContentTypeName(uint contentTypeId)
        => ContentTypes.FirstOrDefault(t => t.Id == contentTypeId).Name ?? $"Duty type #{contentTypeId}";

    public static TerritoryCategory Classify(TerritoryUse use, uint contentFinderCondition)
        => use switch
        {
            TerritoryUse.Town or TerritoryUse.Inn                         => TerritoryCategory.City,
            TerritoryUse.Overworld                                        => TerritoryCategory.Overworld,
            TerritoryUse.HousingOutdoor or TerritoryUse.HousingIndoor     => TerritoryCategory.Housing,
            TerritoryUse.Dungeon or TerritoryUse.VariantDungeon or TerritoryUse.DeepDungeon
                or TerritoryUse.CriterionDungeon or TerritoryUse.CriterionDungeonSavage => TerritoryCategory.Dungeon,
            TerritoryUse.Trial                                            => TerritoryCategory.Trial,
            TerritoryUse.Raid1 or TerritoryUse.Raid2                      => TerritoryCategory.Raid,
            TerritoryUse.AllianceRaid or TerritoryUse.ChaoticRaid         => TerritoryCategory.AllianceRaid,
            _ when contentFinderCondition != 0                            => TerritoryCategory.OtherDuty,
            _                                                             => TerritoryCategory.Unknown,
        };

    private TerritoryInfo Resolve(uint territoryId, uint cfcOverride)
    {
        try
        {
            if (!_data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
                return TerritoryInfo.Unknown(territoryId);

            var name = territory.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Territory {territoryId}";

            var cfcId           = cfcOverride != 0 ? cfcOverride : territory.ContentFinderCondition.RowId;
            uint contentTypeId  = 0;
            var contentTypeName = string.Empty;
            if (cfcId != 0 && _data.GetExcelSheet<ContentFinderCondition>().TryGetRow(cfcId, out var cfc))
            {
                contentTypeId   = cfc.ContentType.RowId;
                contentTypeName = cfc.ContentType.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var dutyName = cfc.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(dutyName))
                    name = dutyName;
            }

            var use = (TerritoryUse)(byte)territory.TerritoryIntendedUse.RowId;
            return new TerritoryInfo(territoryId, name, Classify(use, cfcId), cfcId, contentTypeId, contentTypeName);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"Could not resolve territory {territoryId}.");
            return TerritoryInfo.Unknown(territoryId);
        }
    }
}
