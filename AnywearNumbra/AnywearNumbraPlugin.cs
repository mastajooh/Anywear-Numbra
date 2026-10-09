using System.Collections.Concurrent;
using AnywearNumbra.Models;
using AnywearNumbra.Services;
using AnywearNumbra.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AnywearNumbra;

/// <summary> Plugin entry point: lifecycle, service wiring and the framework-thread action queue. </summary>
public sealed class AnywearNumbraPlugin : IDalamudPlugin
{
    private const string CommandName = "/anywear";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly WindowSystem _windowSystem = new("AnywearNumbra");
    private readonly ConcurrentQueue<Action> _frameworkActions = new();
    private readonly ConfigWindow _configWindow;

    public AnywearNumbraPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects,
        IDataManager data,
        INotificationManager notifications,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commands        = commands;
        _framework       = framework;
        _log             = log;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Settings ??= new AnywearSettings();
        if (SettingsMigrator.Migrate(Configuration.Settings))
            pluginInterface.SavePluginConfig(Configuration);

        Status      = new PluginStatus();
        Glamourer   = new GlamourerService(pluginInterface, log, Status);
        Penumbra    = new PenumbraService(pluginInterface, log);
        Territories = new TerritoryInfoProvider(data, log);
        Controller  = new OutfitController(Configuration, SaveConfiguration, Glamourer, Penumbra, Territories, objects, clientState,
            notifications, log, Status);
        Monitor = new TransitionMonitor(clientState, condition, objects, Territories, Controller, Configuration, Status, log);

        Glamourer.AvailabilityChanged += OnGlamourerAvailabilityChanged;
        if (Glamourer.IsAvailable)
            Controller.RefreshDesigns();

        _configWindow = new ConfigWindow(this);
        _windowSystem.AddWindow(_configWindow);
        _commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Anywear Numbra. '/anywear reapply' reapplies the current outfit; '/anywear on' or '/anywear off' toggles automatic changes.",
        });

        _pluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        _pluginInterface.UiBuilder.OpenMainUi   += ToggleConfigWindow;
        _framework.Update                       += OnFrameworkUpdate;

        Status.Initialized = true;
        _log.Information($"Anywear Numbra loaded. {Status.PathDetail}");
    }

    public Configuration Configuration { get; }
    public PluginStatus Status { get; }
    public GlamourerService Glamourer { get; }
    public PenumbraService Penumbra { get; }
    public TerritoryInfoProvider Territories { get; }
    public OutfitController Controller { get; }
    public TransitionMonitor Monitor { get; }

    /// <summary> Persist settings and push timing/trigger changes to the coordinator. </summary>
    public void SaveConfiguration()
    {
        SettingsMigrator.Sanitize(Configuration.Settings);
        _pluginInterface.SavePluginConfig(Configuration);
        Monitor?.ApplySettings();
    }

    /// <summary> Run an action on the framework thread (UI buttons use this for every IPC call). </summary>
    public void RunOnFramework(Action action)
        => _frameworkActions.Enqueue(action);

    public void ToggleConfigWindow()
        => _configWindow.Toggle();

    public void Dispose()
    {
        _framework.Update                       -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        _pluginInterface.UiBuilder.OpenMainUi   -= ToggleConfigWindow;
        _commands.RemoveHandler(CommandName);
        Glamourer.AvailabilityChanged -= OnGlamourerAvailabilityChanged;

        _windowSystem.RemoveAllWindows();
        Monitor.Dispose();
        SafeDispose(Controller); // releases any Glamourer lock held with our key
        Glamourer.Dispose();
        Penumbra.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            while (_frameworkActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "A queued Anywear Numbra action failed.");
                    Status.AddError($"Action failed: {ex.Message}");
                }
            }

            var now = DateTime.UtcNow;
            Monitor.Tick(now);
            Controller.Tick(now);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Anywear Numbra framework update failed.");
        }
    }

    private void OnGlamourerAvailabilityChanged()
    {
        // Glamourer's events may arrive off the framework thread; marshal the follow-up.
        RunOnFramework(() =>
        {
            if (Glamourer.IsAvailable)
                Controller.RefreshDesigns();
        });
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "reapply":
                RunOnFramework(Controller.ReapplyCurrent);
                break;
            case "on":
                Configuration.Settings.AutomaticChangesEnabled = true;
                SaveConfiguration();
                break;
            case "off":
                Configuration.Settings.AutomaticChangesEnabled = false;
                SaveConfiguration();
                break;
            default:
                ToggleConfigWindow();
                break;
        }
    }

    private void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error during dispose.");
        }
    }
}
