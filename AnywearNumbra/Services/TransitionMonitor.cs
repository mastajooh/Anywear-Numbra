using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AnywearNumbra.Services;

/// <summary>
/// Feeds Dalamud's territory, zone-init and condition events into the <see cref="TransitionCoordinator"/>
/// and executes transitions it releases. Uses three independent signals (TerritoryChanged, ZoneInit and the
/// end of BetweenAreas) so a missing or duplicated event does not lose or double a transition.
/// All handlers run on the framework thread.
/// </summary>
public sealed class TransitionMonitor : IDisposable
{
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectTable _objects;
    private readonly TerritoryInfoProvider _territories;
    private readonly OutfitController _controller;
    private readonly Configuration _configuration;
    private readonly PluginStatus _status;
    private readonly IPluginLog _log;

    public TransitionMonitor(IClientState clientState, ICondition condition, IObjectTable objects, TerritoryInfoProvider territories,
        OutfitController controller, Configuration configuration, PluginStatus status, IPluginLog log)
    {
        _clientState   = clientState;
        _condition     = condition;
        _objects       = objects;
        _territories   = territories;
        _controller    = controller;
        _configuration = configuration;
        _status        = status;
        _log           = log;

        // Plugin loaded mid-session: remember where we are, but do not treat it as a transition.
        Coordinator = new TransitionCoordinator(TransitionOptions.FromSettings(configuration.Settings), clientState.TerritoryType);

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.ZoneInit         += OnZoneInit;
        _condition.ConditionChange    += OnConditionChange;
    }

    public TransitionCoordinator Coordinator { get; }

    /// <summary> Re-read trigger and timing options after the settings changed. </summary>
    public void ApplySettings()
        => Coordinator.Options = TransitionOptions.FromSettings(_configuration.Settings);

    /// <summary> Called every framework update. Allocation-free unless a transition is pending. </summary>
    public void Tick(DateTime nowUtc)
    {
        if (!_configuration.Settings.AutomaticChangesEnabled)
        {
            if (Coordinator.Pending is not null)
                Coordinator.Cancel("Automatic changes are disabled.", nowUtc);
            return;
        }

        var loading = _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

        // Only look up the local player while something is pending (the lookup creates a wrapper object).
        var playerAvailable = Coordinator.Pending is not null && _clientState.IsLoggedIn && _objects.LocalPlayer is not null;

        var transition = Coordinator.Tick(nowUtc, new GameSnapshot(loading, playerAvailable));
        if (transition is null)
        {
            // A transition may have been skipped or timed out inside Tick.
            if (Coordinator.LastFinished is { } finished && finished.FinishedAt == nowUtc)
                Describe(finished);
            return;
        }

        (ApplyOutcome Outcome, string Detail) result;
        try
        {
            result = _controller.ApplyForTransition(transition);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected error while applying an outfit for a transition.");
            result = (ApplyOutcome.Permanent, $"Unexpected error: {ex.Message}");
        }

        Coordinator.Report(transition.Id, result.Outcome, result.Detail, nowUtc);
        Describe(Coordinator.LastFinished is { } done && done.Id == transition.Id ? done : transition);
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.ZoneInit         -= OnZoneInit;
        _condition.ConditionChange    -= OnConditionChange;
    }

    private void OnTerritoryChanged(uint territoryId)
        => Signal(TransitionSignal.TerritoryChanged, territoryId, 0);

    private void OnZoneInit(ZoneInitEventArgs args)
        => Signal(TransitionSignal.ZoneInit, args.TerritoryType.RowId, args.ContentFinderCondition.RowId);

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (value || flag is not (ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51))
            return;

        Signal(TransitionSignal.LoadingFinished, _clientState.TerritoryType, 0);
    }

    private void Signal(TransitionSignal signal, uint territoryId, uint contentFinderCondition)
    {
        try
        {
            if (!_configuration.Settings.AutomaticChangesEnabled)
                return;

            var info   = _territories.Get(territoryId, contentFinderCondition);
            var record = Coordinator.Signal(signal, territoryId, info.IsDuty, DateTime.UtcNow);
            if (record is null)
            {
                _log.Verbose($"[Transition] {Coordinator.LastIgnoredReason}");
                return;
            }

            if (record.MergedSignals == 0)
            {
                _log.Debug($"[Transition] #{record.Id} {record.Kind} to {info.Name} ({territoryId}) via {signal}.");
                Describe(record);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Error handling {signal}.");
        }
    }

    private void Describe(TransitionRecord record)
    {
        var name = _territories.Get(record.TerritoryId).Name;
        _status.LastTransition   = $"#{record.Id} {record.Kind} to {name}: {record.Phase}. {record.Detail}";
        _status.LastTransitionAt = DateTime.Now;
    }
}
