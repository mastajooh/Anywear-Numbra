using System.Text.Json.Nodes;
using AnywearNumbra.Models;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace AnywearNumbra.Services;

/// <summary>
/// Orchestrates outfit changes: selection, the single scoped application, post-apply verification
/// (Glamourer Automation conflict detection) and lock release. Framework thread only.
/// Every path that changes appearance — automatic transitions and all manual buttons — ends in
/// <see cref="ApplyDesign"/>, which calls <see cref="GlamourerService.ApplyEquipmentScoped"/>.
/// </summary>
public sealed class OutfitController : IDisposable
{
    private readonly Configuration _configuration;
    private readonly Action _save;
    private readonly GlamourerService _glamourer;
    private readonly PenumbraService _penumbra;
    private readonly TerritoryInfoProvider _territories;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly INotificationManager _notifications;
    private readonly IPluginLog _log;
    private readonly PluginStatus _status;
    private readonly IRandomSource _random = SharedRandomSource.Instance;

    private JsonObject? _verifyState;
    private (string Directory, OutfitSource Source)? _lastModSource;
    private DateTime _verifyDueAt;
    private int _verifyObjectIndex;

    public OutfitController(Configuration configuration, Action save, GlamourerService glamourer, PenumbraService penumbra,
        TerritoryInfoProvider territories, IObjectTable objects, IClientState clientState, INotificationManager notifications,
        IPluginLog log, PluginStatus status)
    {
        _configuration = configuration;
        _save          = save;
        _glamourer     = glamourer;
        _penumbra      = penumbra;
        _territories   = territories;
        _objects       = objects;
        _clientState   = clientState;
        _notifications = notifications;
        _log           = log;
        _status        = status;
    }

    private AnywearSettings Settings
        => _configuration.Settings;

    public IReadOnlySet<Guid> AvailableDesigns { get; private set; } = new HashSet<Guid>();

    /// <summary> Mode E: enabled mods that change armor, from the last scan. </summary>
    public IReadOnlyList<ModOutfit> ModOutfits { get; private set; } = [];

    public DateTime? ModsScannedAt { get; private set; }

    public string ModScanMessage { get; private set; } = "Not scanned yet.";

    /// <summary> How long a mod scan is reused before Mode E scans again on its own. </summary>
    private static readonly TimeSpan ModScanMaxAge = TimeSpan.FromMinutes(10);

    /// <summary> Scan Penumbra for enabled mods that change armor. Framework thread. </summary>
    public bool RescanMods()
    {
        if (LocalPlayerIndex() is not { } objectIndex)
        {
            ModScanMessage = "Your character is not available right now.";
            return false;
        }

        var (outfits, message) = _penumbra.ScanModOutfits(objectIndex);
        ModOutfits     = outfits;
        ModsScannedAt  = DateTime.Now;
        ModScanMessage = message;
        _log.Information($"[Mods] {message}");
        return outfits.Count > 0;
    }

    /// <summary> Manual: apply a random mod outfit now (Mode E's pick), regardless of the selected mode. </summary>
    public void ApplyRandomModNow()
        => ApplyRandomMod("Manual random mod");

    /// <summary> Display text for whatever was applied last (design or mod). </summary>
    public string LastAppliedLabel
        => Settings.LastAppliedModDirectory.Length > 0
            ? $"{Settings.LastAppliedDesignName} (Penumbra mod)"
            : Settings.LastAppliedDesignId == Guid.Empty
                ? "nothing yet"
                : DesignName(Settings.LastAppliedDesignId);

    private (ApplyOutcome Outcome, string Detail) ApplyRandomMod(string trigger)
    {
        if (ModsScannedAt is not { } scanned || DateTime.Now - scanned > ModScanMaxAge || ModOutfits.Count == 0)
            RescanMods();

        var (outfit, reason) = ModOutfitBuilder.SelectRandom(ModOutfits, Settings, _random);
        if (outfit is null)
            return Report(false, $"No mod outfit applied: {reason}");

        var source = OutfitSource.FromMod(outfit, ModDesignOptions.FromSettings(Settings, _territories.Stains, _random));
        return ApplyOutfit(source, $"{trigger} — {reason}", outfit.ModDirectory);
    }

    /// <summary> Reload designs from Glamourer and add newly found ones (not eligible) to the outfit list. </summary>
    public bool RefreshDesigns()
    {
        if (!_glamourer.RefreshDesigns())
            return false;

        AvailableDesigns = new HashSet<Guid>(_glamourer.Designs.Keys);
        var changed = false;
        var known   = new HashSet<Guid>(Settings.Outfits.Select(o => o.DesignId));
        foreach (var entry in Settings.Outfits)
        {
            if (_glamourer.Designs.TryGetValue(entry.DesignId, out var name) && name != entry.LastKnownName)
            {
                entry.LastKnownName = name;
                changed             = true;
            }
        }

        foreach (var (id, name) in _glamourer.Designs.OrderBy(d => d.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            if (known.Contains(id))
                continue;

            Settings.Outfits.Add(new OutfitEntry { DesignId = id, Eligible = false, LastKnownName = name });
            changed = true;
        }

        if (changed)
            _save();
        return true;
    }

    /// <summary> Display name for a design GUID: current Glamourer name, else the last known one marked missing. </summary>
    public string DesignName(Guid id)
    {
        if (id == Guid.Empty)
            return "(none)";
        if (_glamourer.Designs.TryGetValue(id, out var name))
            return name;

        var known = Settings.Outfits.FirstOrDefault(o => o.DesignId == id)?.LastKnownName;
        return string.IsNullOrEmpty(known) ? $"Missing design {id}" : $"{known} (missing)";
    }

    public bool IsMissing(Guid id)
        => id != Guid.Empty && !_glamourer.Designs.ContainsKey(id);

    /// <summary> Called by the transition monitor when a transition is ready. </summary>
    public (ApplyOutcome Outcome, string Detail) ApplyForTransition(TransitionRecord transition)
    {
        if (!_glamourer.IsAvailable && !_glamourer.CheckAvailability())
            return _status.Path is ApplicationPath.C
                ? (ApplyOutcome.Permanent, _glamourer.AvailabilityText)
                : (ApplyOutcome.Retryable, _glamourer.AvailabilityText);

        if (AvailableDesigns.Count == 0 || _glamourer.DesignsRefreshedAt is null)
            RefreshDesigns();

        var territory = _territories.Get(transition.TerritoryId);
        if (Settings.Mode is SelectionMode.PenumbraMods)
            return ApplyRandomMod($"{transition.Kind} to {territory.Name}");

        var selection = OutfitSelectionService.Select(Settings, AvailableDesigns, territory, _random);
        if (!selection.HasDesign)
        {
            _status.LastApplyResult    = $"No design applied: {selection.Reason}";
            _status.LastApplySucceeded = false;
            _status.LastApplyAt        = DateTime.Now;
            return (ApplyOutcome.Permanent, selection.Reason);
        }

        return ApplyDesign(selection.DesignId, $"{transition.Kind} to {territory.Name} — {selection.Reason}");
    }

    /// <summary> Manual: apply a specific design now. Works regardless of the automatic-change toggle. </summary>
    public void ApplyNow(Guid designId)
    {
        if (designId == Guid.Empty)
        {
            Report(false, "Select a design first.");
            return;
        }

        ApplyDesign(designId, "Manual apply");
    }

    /// <summary> Manual: reapply the last successfully applied design. </summary>
    public void ReapplyCurrent()
    {
        if (Settings.LastAppliedModDirectory.Length > 0)
        {
            // Same outfit with the same dyes, when it was applied this session.
            if (_lastModSource is { } last && last.Directory == Settings.LastAppliedModDirectory)
            {
                ApplyOutfit(last.Source, "Manual reapply", last.Directory);
                return;
            }

            if (ModOutfits.Count == 0)
                RescanMods();
            var mod = ModOutfits.FirstOrDefault(o => o.ModDirectory == Settings.LastAppliedModDirectory);
            if (mod is null)
                Report(false, $"The mod '{Settings.LastAppliedDesignName}' is no longer enabled or no longer changes armor.");
            else
                ApplyOutfit(OutfitSource.FromMod(mod, ModDesignOptions.FromSettings(Settings, _territories.Stains, _random)), "Manual reapply",
                    mod.ModDirectory);
            return;
        }

        if (Settings.LastAppliedDesignId == Guid.Empty)
        {
            Report(false, "Nothing has been applied yet, so there is nothing to reapply.");
            return;
        }

        ApplyDesign(Settings.LastAppliedDesignId, "Manual reapply");
    }

    /// <summary> Manual: re-check both dependencies and refresh the design list. </summary>
    public void TestConnectivity()
    {
        _glamourer.CheckAvailability();
        var designsOk = RefreshDesigns();
        _penumbra.Refresh(LocalPlayerIndex());
        var message = $"Glamourer: {_glamourer.AvailabilityText}" + (designsOk ? $" {_glamourer.Designs.Count} design(s)." : string.Empty)
          + $" Penumbra: {_penumbra.StatusText}";
        _log.Information($"[Connectivity] {message}");
        Notify(_glamourer.IsAvailable ? NotificationType.Info : NotificationType.Warning, message, force: true);
    }

    /// <summary> Called once per framework update; only does work when a verification or lock release is due. </summary>
    public void Tick(DateTime now)
    {
        if (_verifyState is not null && now >= _verifyDueAt)
            RunVerification();

        if (_status.LockHeld && _status.LockReleaseAt is { } releaseAt && DateTime.Now >= releaseAt)
            ReleaseLock("lock duration elapsed");
    }

    public void ReleaseLock(string reason)
    {
        var count = _glamourer.ReleaseLocks();
        _status.LockHeld      = false;
        _status.LockReleaseAt = null;
        _log.Debug($"[Lock] Released {count} Glamourer lock(s): {reason}.");
    }

    public void Dispose()
    {
        // Never leave the user locked out of manual Glamourer edits.
        ReleaseLock("plugin unloading");
    }

    // ------------------------------------------------------------------------------------------------

    private (ApplyOutcome Outcome, string Detail) ApplyDesign(Guid designId, string trigger)
        => ApplyOutfit(OutfitSource.FromDesign(designId, DesignName(designId)), trigger, null);

    /// <summary> The one place that asks Glamourer to change appearance (designs and mod outfits alike). </summary>
    private (ApplyOutcome Outcome, string Detail) ApplyOutfit(OutfitSource source, string trigger, string? modDirectory)
    {
        if (LocalPlayerIndex() is not { } objectIndex)
            return Report(false, "Your character is not available right now.", ApplyOutcome.Retryable);

        var name   = source.Name;
        var scope  = ScopeOptions.FromSettings(Settings);
        var result = _glamourer.ApplyEquipmentScoped(source, objectIndex, scope, Settings.LockStateAfterApply);
        if (!result.Success)
        {
            var outcome = result.Retryable ? ApplyOutcome.Retryable : ApplyOutcome.Permanent;
            if (outcome is ApplyOutcome.Permanent)
                _status.AddError($"{trigger}: '{name}': {result.Message}");
            return Report(false, $"'{name}' not applied ({trigger}): {result.Message}", outcome);
        }

        if (modDirectory is not null)
        {
            _lastModSource                   = (modDirectory, source);
            Settings.LastAppliedModDirectory = modDirectory;
            Settings.LastAppliedDesignId     = Guid.Empty;
            Settings.LastAppliedDesignName   = name;
            Settings.LastAppliedAtUtc        = DateTime.UtcNow;
        }
        else
        {
            if (AvailableDesigns.Count == 0)
                AvailableDesigns = new HashSet<Guid>(_glamourer.Designs.Keys);
            Settings.LastAppliedModDirectory = string.Empty;
            OutfitSelectionService.RecordApplied(Settings, source.DesignId, name, AvailableDesigns, DateTime.UtcNow);
        }

        _save();

        _status.LastAppliedFields = string.Join(", ", result.AppliedFields);
        _status.ConflictWarning   = null;
        if (Settings.LockStateAfterApply)
        {
            _status.LockHeld      = true;
            _status.LockReleaseAt = DateTime.Now.AddSeconds(Settings.LockDurationSeconds);
        }

        if (Settings.VerifyAfterApply && result.AppliedState is not null)
        {
            _verifyState       = result.AppliedState;
            _verifyObjectIndex = objectIndex;
            _verifyDueAt       = DateTime.UtcNow.AddMilliseconds(Settings.VerifyDelayMs);
        }

        return Report(true, $"Applied '{name}' ({trigger}). {result.Message}", ApplyOutcome.Success);
    }

    private void RunVerification()
    {
        var applied = _verifyState!;
        _verifyState = null;

        var later = _glamourer.ReadState(_verifyObjectIndex);
        if (later is null)
            return;

        var overridden = EquipmentScopeFilter.FindOverriddenFields(applied, later);
        if (overridden.Count == 0)
            return;

        var message = $"Something changed {overridden.Count} field(s) right after Anywear Numbra applied them ({string.Join(", ", overridden)}). "
          + "This is usually Glamourer Automation or another plugin. Disable the conflicting automation set, or enable 'Lock state after applying'.";
        _status.ConflictWarning = message;
        _status.ConflictAt      = DateTime.Now;
        _log.Warning($"[Conflict] {message}");
        Notify(NotificationType.Warning, message, force: false);
    }

    private int? LocalPlayerIndex()
    {
        if (!_clientState.IsLoggedIn)
            return null;

        return _objects.LocalPlayer?.ObjectIndex;
    }

    private (ApplyOutcome Outcome, string Detail) Report(bool success, string message, ApplyOutcome outcome = ApplyOutcome.Permanent)
    {
        _status.LastApplyResult    = message;
        _status.LastApplySucceeded = success;
        _status.LastApplyAt        = DateTime.Now;
        if (success)
            _log.Information($"[Apply] {message}");
        else if (outcome is ApplyOutcome.Retryable)
            _log.Debug($"[Apply] {message}");
        else
            _log.Warning($"[Apply] {message}");

        if (success || outcome is ApplyOutcome.Permanent)
            Notify(success ? NotificationType.Success : NotificationType.Warning, message, force: false);
        return (outcome, message);
    }

    private void Notify(NotificationType type, string message, bool force)
    {
        if (!force && !Settings.ShowNotifications)
            return;

        try
        {
            _notifications.AddNotification(new Notification
            {
                Title   = "Anywear Numbra",
                Content = message,
                Type    = type,
            });
        }
        catch (Exception ex)
        {
            _log.Debug($"Notification failed: {ex.Message}");
        }
    }
}
