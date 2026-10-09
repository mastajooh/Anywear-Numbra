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

    public const int SupportedBreakingVersion = 5;

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<object?> _initialized;
    private readonly ICallGateSubscriber<object?> _disposed;
    private readonly ICallGateSubscriber<bool> _enabledState;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _collectionForObject;

    public PenumbraService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log                 = log;
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
