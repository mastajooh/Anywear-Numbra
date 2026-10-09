using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace AnywearNumbra.Services;

/// <summary>
/// Read-only Penumbra IPC, used for the Status tab. Verified against Penumbra.Api 5.x (IpcSubscribers).
/// <para>
/// Anywear Numbra never changes Penumbra collections, mod settings or files: Glamourer already redraws the
/// character after ApplyState, and Penumbra keeps resolving the user's enabled mods for the new equipment.
/// Penumbra being absent never blocks equipment application.
/// </para>
/// </summary>
public sealed class PenumbraService : IDisposable
{
    public const string LabelApiVersion = "Penumbra.ApiVersion.V5";                      // Func<(int Breaking, int Features)>
    public const string LabelInitialized = "Penumbra.Initialized";                       // Event()
    public const string LabelDisposed = "Penumbra.Disposed";                             // Event()
    public const string LabelGetEnabledState = "Penumbra.GetEnabledState";               // Func<bool>
    public const string LabelGetCollectionForObject = "Penumbra.GetCollectionForObject.V5"; // Func<int, (bool, bool, (Guid, string))>

    public const string LabelGetModList = "Penumbra.GetModList";                          // Func<Dictionary<string dir, string name>>
    public const string LabelGetChangedItems = "Penumbra.GetChangedItems.V5";             // Func<string, string, Dictionary<string, object?>>
    public const string LabelGetCurrentModSettings = "Penumbra.GetCurrentModSettings.V5"; // Func<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>

    public const int SupportedBreakingVersion = 5;

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<object?> _initialized;
    private readonly ICallGateSubscriber<object?> _disposed;
    private readonly ICallGateSubscriber<bool> _enabledState;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _collectionForObject;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _modList;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> _changedItems;
    private readonly ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _modSettings;

    public PenumbraService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log                 = log;
        _modList             = pluginInterface.GetIpcSubscriber<Dictionary<string, string>>(LabelGetModList);
        _changedItems        = pluginInterface.GetIpcSubscriber<string, string, Dictionary<string, object?>>(LabelGetChangedItems);
        _modSettings = pluginInterface
            .GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(LabelGetCurrentModSettings);
        _apiVersion          = pluginInterface.GetIpcSubscriber<(int, int)>(LabelApiVersion);
        _initialized         = pluginInterface.GetIpcSubscriber<object?>(LabelInitialized);
        _disposed            = pluginInterface.GetIpcSubscriber<object?>(LabelDisposed);
        _enabledState        = pluginInterface.GetIpcSubscriber<bool>(LabelGetEnabledState);
        _collectionForObject = pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>(LabelGetCollectionForObject);

        _initialized.Subscribe(OnPenumbraInitialized);
        _disposed.Subscribe(OnPenumbraDisposed);
        Refresh(null);
    }

    public bool IsAvailable { get; private set; }
    public string StatusText { get; private set; } = "Not checked.";
    public string CollectionText { get; private set; } = "Unknown.";

    /// <summary> Re-read Penumbra status. Called on load, on Penumbra events and from the UI, never per frame. </summary>
    public void Refresh(int? localPlayerObjectIndex)
    {
        try
        {
            var (breaking, features) = _apiVersion.InvokeFunc();
            if (breaking != SupportedBreakingVersion)
            {
                IsAvailable    = false;
                StatusText     = $"Penumbra API {breaking}.{features} is not the supported version {SupportedBreakingVersion}.x. Equipment still applies; status details are unavailable.";
                CollectionText = "Unknown.";
                return;
            }

            IsAvailable = true;
            var enabled = _enabledState.InvokeFunc();
            StatusText = enabled
                ? $"Ready (API {breaking}.{features}), mods enabled."
                : $"Ready (API {breaking}.{features}), but mods are DISABLED in Penumbra.";

            if (localPlayerObjectIndex is { } index)
            {
                var (objectValid, individualSet, (_, name)) = _collectionForObject.InvokeFunc(index);
                CollectionText = objectValid
                    ? $"{name}{(individualSet ? " (individual assignment)" : string.Empty)}"
                    : "Character not available.";
            }
        }
        catch (IpcNotReadyError)
        {
            IsAvailable    = false;
            StatusText     = "Penumbra is not installed or not loaded. Equipment still applies; modded models need Penumbra.";
            CollectionText = "Unknown.";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusText  = $"Penumbra check failed: {ex.Message}";
            _log.Debug($"[Penumbra] Status check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Read-only scan: every mod that is ENABLED in the collection used for <paramref name="objectIndex"/> and changes
    /// at least one armor piece. Makes a few IPC calls per mod, so call it rarely (results are cached by the caller).
    /// </summary>
    public (List<ModOutfit> Outfits, string Message) ScanModOutfits(int objectIndex)
    {
        var outfits = new List<ModOutfit>();
        try
        {
            var (objectValid, _, (collectionId, collectionName)) = _collectionForObject.InvokeFunc(objectIndex);
            if (!objectValid)
                return (outfits, "Penumbra could not find your character.");

            var mods = _modList.InvokeFunc() ?? new Dictionary<string, string>();
            var withArmor = 0;
            foreach (var (directory, name) in mods)
            {
                ModOutfit outfit;
                try
                {
                    var items  = _changedItems.InvokeFunc(directory, name);
                    var pieces = new List<(string Type, ulong ItemId)>();
                    if (items is not null)
                    {
                        foreach (var value in items.Values)
                        {
                            if (TryReadEquipItem(value, out var type, out var itemId))
                                pieces.Add((type, itemId));
                        }
                    }

                    outfit = ModOutfitBuilder.Build(directory, name, pieces);
                    if (outfit.PieceCount == 0)
                        continue;

                    ++withArmor;
                    var (code, settings) = _modSettings.InvokeFunc(collectionId, directory, string.Empty, false);
                    if (code != 0 || settings is not { Item1: true })
                        continue;
                }
                catch (Exception ex)
                {
                    _log.Debug($"[Penumbra] Skipped mod {directory}: {ex.Message}");
                    continue;
                }

                outfits.Add(outfit);
            }

            outfits.Sort((a, b) => string.Compare(a.ModName, b.ModName, StringComparison.CurrentCultureIgnoreCase));
            return (outfits,
                $"{outfits.Count} enabled mod(s) change armor in collection '{collectionName}' ({withArmor} of {mods.Count} mods change armor at all).");
        }
        catch (IpcNotReadyError)
        {
            return (outfits, "Penumbra is not installed or not loaded.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Penumbra] Mod scan failed.");
            return (outfits, $"Penumbra mod scan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Penumbra reports changed items as its own EquipItem struct (Penumbra.GameData), which this plugin cannot reference.
    /// Read the two public fields needed by name: <c>Type</c> (FullEquipType) and <c>Id</c> (CustomItemId, whose value field is <c>Id</c>).
    /// Anything else (customizations, emotes, actions, unknown shapes) is skipped.
    /// </summary>
    private static bool TryReadEquipItem(object? value, out string type, out ulong itemId)
    {
        type   = string.Empty;
        itemId = 0;
        if (value is null)
            return false;

        var t = value.GetType();
        if (t.Name != "EquipItem")
            return false;

        if (t.GetField("Type")?.GetValue(value) is not Enum equipType || t.GetField("Id")?.GetValue(value) is not { } idBox)
            return false;

        if (idBox.GetType().GetField("Id")?.GetValue(idBox) is not ulong raw || raw == 0)
            return false;

        type   = equipType.ToString();
        itemId = raw;
        return true;
    }

    public void Dispose()
    {
        _initialized.Unsubscribe(OnPenumbraInitialized);
        _disposed.Unsubscribe(OnPenumbraDisposed);
    }

    private void OnPenumbraInitialized()
        => Refresh(null);

    private void OnPenumbraDisposed()
    {
        IsAvailable    = false;
        StatusText     = "Penumbra was unloaded.";
        CollectionText = "Unknown.";
    }
}
