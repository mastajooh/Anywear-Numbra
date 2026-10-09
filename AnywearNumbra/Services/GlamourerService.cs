using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnywearNumbra.Services;

/// <summary> Result of <see cref="GlamourerService.ApplyEquipmentScoped"/>. </summary>
public sealed record GlamourerApplyResult(
    bool Success,
    bool Retryable,
    string Message,
    IReadOnlyList<string> AppliedFields,
    JsonObject? AppliedState)
{
    public static GlamourerApplyResult Retry(string message)
        => new(false, true, message, [], null);

    public static GlamourerApplyResult Fail(string message)
        => new(false, false, message, [], null);
}

/// <summary>
/// All Glamourer IPC. THE ONLY CLASS THAT SENDS APPEARANCE CHANGES, through exactly one method:
/// <see cref="ApplyEquipmentScoped"/>. A unit test enforces this structurally.
/// <para>
/// Application path: <b>Path B</b> (merged state). Verified against Glamourer API 1.x
/// (Glamourer.Api IpcSubscribers and Glamourer/Api/{DesignsApi,StateApi,ApiHelpers}.cs, October 2026):
/// </para>
/// <list type="bullet">
/// <item>Path A is not possible: the only scoping is ApplyFlag.Equipment, which maps to
/// ApplicationCollection.Equipment = all equip slots INCLUDING weapons, the bonus (facewear) slot,
/// crests and hat/weapon/visor/ear visibility.</item>
/// <item>Path B is possible: <c>Glamourer.GetState</c> returns the current state as a JObject,
/// <c>Glamourer.GetDesignJObject</c> returns a design's data, and <c>Glamourer.ApplyState</c> accepts a
/// JObject whose per-field "Apply" markers are honored by DesignBase.LoadDesignBase. EquipmentScopeFilter
/// builds that object; this class only transports it.</item>
/// <item>Flags are always Once|Equipment (plus Customization only when a visibility toggle is applied,
/// plus Lock only when the user enabled locking). Once matters: without it, Glamourer's ResetMaterials
/// path would reset customize parameters (skin/hair colors) to game values.</item>
/// <item>The JObject has no "Identifier", so Glamourer loads it as a DesignBase: no design links, no mod
/// associations.</item>
/// </list>
/// IPC is bound directly through Dalamud call gates with the verified labels and generic signatures, instead
/// of the Glamourer.Api package (which now builds against Glamourer's internal Luna library).
/// </summary>
public sealed class GlamourerService : IDisposable
{
    // ---- Verified IPC labels (Glamourer.Api/IpcSubscribers) -------------------------------------------
    public const string LabelApiVersion = "Glamourer.ApiVersion.V2";          // Func<(int, int)>
    public const string LabelInitialized = "Glamourer.Initialized";           // Event()
    public const string LabelDisposed = "Glamourer.Disposed";                 // Event()
    public const string LabelGetDesignList = "Glamourer.GetDesignList.V2";    // Func<Dictionary<Guid, string>>
    public const string LabelGetDesignJObject = "Glamourer.GetDesignJObject"; // Func<Guid, JObject?>
    public const string LabelGetState = "Glamourer.GetState";                 // Func<int, uint, (int, JObject?)>
    public const string LabelApplyState = "Glamourer.ApplyState";             // Func<object, int, uint, ulong, int>
    public const string LabelUnlockAll = "Glamourer.UnlockAll";               // Func<uint, int>

    /// <summary> Supported Glamourer API major version. </summary>
    public const int SupportedApiMajor = 1;

    /// <summary> This plugin's Glamourer lock key ("ANMB"). Only used to unlock our own states or lock when enabled. </summary>
    public const uint LockKey = 0x414E4D42;

    // GlamourerApiEc values (Glamourer.Api/Enums/GlamourerApiEc.cs).
    private const int EcSuccess = 0;
    private const int EcNothingDone = 1;
    private const int EcActorNotFound = 2;
    private const int EcActorNotHuman = 3;
    private const int EcDesignNotFound = 4;
    private const int EcInvalidKey = 6;
    private const int EcInvalidState = 7;

    private readonly IPluginLog _log;
    private readonly PluginStatus _status;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<object?> _initialized;
    private readonly ICallGateSubscriber<object?> _disposed;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getDesignList;
    private readonly ICallGateSubscriber<Guid, JObject?> _getDesignJObject;
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)> _getState;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _applyState;
    private readonly ICallGateSubscriber<uint, int> _unlockAll;

    private Dictionary<Guid, string> _designs = new();

    public GlamourerService(IDalamudPluginInterface pluginInterface, IPluginLog log, PluginStatus status)
    {
        _log    = log;
        _status = status;

        _apiVersion       = pluginInterface.GetIpcSubscriber<(int, int)>(LabelApiVersion);
        _initialized      = pluginInterface.GetIpcSubscriber<object?>(LabelInitialized);
        _disposed         = pluginInterface.GetIpcSubscriber<object?>(LabelDisposed);
        _getDesignList    = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>(LabelGetDesignList);
        _getDesignJObject = pluginInterface.GetIpcSubscriber<Guid, JObject?>(LabelGetDesignJObject);
        _getState         = pluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>(LabelGetState);
        _applyState       = pluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>(LabelApplyState);
        _unlockAll        = pluginInterface.GetIpcSubscriber<uint, int>(LabelUnlockAll);

        _initialized.Subscribe(OnGlamourerInitialized);
        _disposed.Subscribe(OnGlamourerDisposed);

        CheckAvailability();
    }

    /// <summary> True when Glamourer is loaded with a supported API and every endpoint Path B needs. </summary>
    public bool IsAvailable { get; private set; }

    public (int Major, int Minor)? ApiVersion { get; private set; }

    public string AvailabilityText { get; private set; } = "Not checked.";

    /// <summary> Designs reported by Glamourer, GUID to display name. Display names are for the UI only. </summary>
    public IReadOnlyDictionary<Guid, string> Designs
        => _designs;

    public DateTime? DesignsRefreshedAt { get; private set; }

    /// <summary> Raised (on the thread Glamourer used) when Glamourer loads or unloads. </summary>
    public event Action? AvailabilityChanged;

    /// <summary> Probe Glamourer and decide between Path B and Path C. Cheap; never called per frame. </summary>
    public bool CheckAvailability()
    {
        var wasAvailable = IsAvailable;
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();
            ApiVersion = (major, minor);

            var missing = new List<string>();
            if (!_getDesignList.HasFunction)
                missing.Add(LabelGetDesignList);
            if (!_getDesignJObject.HasFunction)
                missing.Add(LabelGetDesignJObject);
            if (!_getState.HasFunction)
                missing.Add(LabelGetState);
            if (!_applyState.HasFunction)
                missing.Add(LabelApplyState);

            if (major != SupportedApiMajor)
            {
                SetUnavailable(ApplicationPath.C,
                    $"Glamourer API {major}.{minor} is not supported (expected {SupportedApiMajor}.x). Application is disabled until Anywear Numbra is updated.");
            }
            else if (missing.Count > 0)
            {
                SetUnavailable(ApplicationPath.C,
                    $"Glamourer API {major}.{minor} is missing required endpoints: {string.Join(", ", missing)}. Application is disabled.");
            }
            else
            {
                IsAvailable       = true;
                AvailabilityText  = $"Ready (API {major}.{minor}).";
                _status.Path       = ApplicationPath.B;
                _status.PathDetail = "Path B: merged current state + permitted design fields via Glamourer.ApplyState (Once|Equipment).";
            }
        }
        catch (IpcNotReadyError)
        {
            ApiVersion = null;
            SetUnavailable(ApplicationPath.Unknown, "Glamourer is not installed or not loaded.");
        }
        catch (Exception ex)
        {
            ApiVersion = null;
            SetUnavailable(ApplicationPath.Unknown, $"Glamourer check failed: {ex.Message}");
            _log.Warning(ex, "[Glamourer] Availability check failed.");
        }

        if (wasAvailable != IsAvailable)
            _log.Information($"[Glamourer] {AvailabilityText}");
        return IsAvailable;
    }

    /// <summary> Reload the design list from Glamourer. </summary>
    public bool RefreshDesigns()
    {
        if (!IsAvailable && !CheckAvailability())
            return false;

        try
        {
            var list = _getDesignList.InvokeFunc();
            _designs           = list is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string>(list);
            DesignsRefreshedAt = DateTime.Now;
            return true;
        }
        catch (IpcNotReadyError)
        {
            SetUnavailable(ApplicationPath.Unknown, "Glamourer is not installed or not loaded.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Glamourer] GetDesignList failed.");
            _status.AddError($"Could not read Glamourer designs: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// THE single appearance-changing operation of the plugin.
    /// Reads the actor's current state and the design, lets <see cref="EquipmentScopeFilter"/> build a state
    /// in which only permitted equipment fields are marked as applied, and sends that to Glamourer.ApplyState.
    /// Must be called on the framework thread.
    /// </summary>
    public GlamourerApplyResult ApplyEquipmentScoped(Guid designId, int objectIndex, ScopeOptions scope, bool lockState)
    {
        if (!IsAvailable && !CheckAvailability())
            return _status.Path is ApplicationPath.C
                ? GlamourerApplyResult.Fail(AvailabilityText)
                : GlamourerApplyResult.Retry(AvailabilityText);

        try
        {
            // 1. The design's data (read-only).
            var designJObject = _getDesignJObject.InvokeFunc(designId);
            if (designJObject is null)
                return GlamourerApplyResult.Fail("The design no longer exists in Glamourer.");

            // 2. The actor's current state (read-only). Our key lets us read a state we locked ourselves.
            var (stateCode, stateJObject) = _getState.InvokeFunc(objectIndex, LockKey);
            switch (stateCode)
            {
                case EcSuccess when stateJObject is not null: break;
                case EcSuccess:
                case EcActorNotFound: return GlamourerApplyResult.Retry("Glamourer could not find your character yet.");
                case EcInvalidKey:
                    return GlamourerApplyResult.Fail("Your Glamourer state is locked by another plugin; Anywear Numbra will not override it.");
                default: return GlamourerApplyResult.Fail($"Glamourer.GetState returned error {stateCode}.");
            }

            // 3. Build the equipment-only state (pure, unit-tested).
            if (ToJsonObject(stateJObject!) is not { } current)
                return GlamourerApplyResult.Fail("Could not read the current Glamourer state.");
            if (ToJsonObject(designJObject) is not { } design)
                return GlamourerApplyResult.Fail("Could not read the design data.");

            var merge = EquipmentScopeFilter.Merge(current, design, scope);
            foreach (var note in merge.Notes)
                _log.Debug($"[Glamourer] {note}");
            if (!merge.IsSuccess || merge.State is null)
                return GlamourerApplyResult.Fail(merge.Message);

            // 4. Send it. This is the only Glamourer apply call in the plugin.
            var flags   = EquipmentScopeFilter.ComputeApplyFlags(merge, lockState);
            var payload = JObject.Parse(merge.State.ToJsonString());
            var code    = _applyState.InvokeFunc(payload, objectIndex, LockKey, (ulong)flags);

            return code switch
            {
                EcSuccess or EcNothingDone => new GlamourerApplyResult(true, false,
                    $"Applied {merge.AppliedFields.Count} equipment field(s) (flags {flags}).", merge.AppliedFields, merge.State),
                EcActorNotFound   => GlamourerApplyResult.Retry("Glamourer could not find your character yet."),
                EcActorNotHuman   => GlamourerApplyResult.Fail("Your character is not a human model right now."),
                EcInvalidKey      => GlamourerApplyResult.Fail("Your Glamourer state is locked by another plugin; Anywear Numbra will not override it."),
                EcInvalidState    => GlamourerApplyResult.Fail("Glamourer rejected the equipment state (InvalidState)."),
                EcDesignNotFound  => GlamourerApplyResult.Fail("The design no longer exists in Glamourer."),
                _                 => GlamourerApplyResult.Fail($"Glamourer.ApplyState returned error {code}."),
            };
        }
        catch (IpcNotReadyError)
        {
            SetUnavailable(ApplicationPath.Unknown, "Glamourer is not installed or not loaded.");
            return GlamourerApplyResult.Retry("Glamourer is not loaded.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Glamourer] Equipment application failed.");
            return GlamourerApplyResult.Fail($"Glamourer IPC error: {ex.Message}");
        }
    }

    /// <summary> Read-only: the actor's current state, used to verify an application afterwards. </summary>
    public JsonObject? ReadState(int objectIndex)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var (code, state) = _getState.InvokeFunc(objectIndex, LockKey);
            return code == EcSuccess && state is not null ? ToJsonObject(state) : null;
        }
        catch (Exception ex)
        {
            _log.Debug($"[Glamourer] GetState for verification failed: {ex.Message}");
            return null;
        }
    }

    /// <summary> Release every Glamourer state locked with this plugin's key. Returns the number unlocked. </summary>
    public int ReleaseLocks()
    {
        try
        {
            return _unlockAll.InvokeFunc(LockKey);
        }
        catch (Exception ex)
        {
            _log.Debug($"[Glamourer] UnlockAll failed: {ex.Message}");
            return 0;
        }
    }

    public void Dispose()
    {
        _initialized.Unsubscribe(OnGlamourerInitialized);
        _disposed.Unsubscribe(OnGlamourerDisposed);
    }

    private void OnGlamourerInitialized()
    {
        CheckAvailability();
        AvailabilityChanged?.Invoke();
    }

    private void OnGlamourerDisposed()
    {
        SetUnavailable(ApplicationPath.Unknown, "Glamourer was unloaded.");
        AvailabilityChanged?.Invoke();
    }

    private void SetUnavailable(ApplicationPath path, string reason)
    {
        IsAvailable       = false;
        AvailabilityText  = reason;
        _status.Path       = path;
        _status.PathDetail = path is ApplicationPath.C
            ? $"Path C: {reason}"
            : reason;
    }

    private static JsonObject? ToJsonObject(JObject value)
        => JsonNode.Parse(value.ToString(Formatting.None)) as JsonObject;
}
