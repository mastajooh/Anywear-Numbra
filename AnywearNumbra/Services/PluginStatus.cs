namespace AnywearNumbra.Services;

/// <summary> Which Glamourer application path is active (see README, "Application path"). </summary>
public enum ApplicationPath
{
    /// <summary> Not determined yet (Glamourer not checked). </summary>
    Unknown,

    /// <summary> Path B: merged current state + permitted design fields, sent with Glamourer.ApplyState. </summary>
    B,

    /// <summary> Path C: a required Glamourer capability is missing; automatic and manual application are disabled. </summary>
    C,
}

/// <summary> Runtime status shown in the Status tab. Only touched from the framework/UI thread. </summary>
public sealed class PluginStatus
{
    private const int MaxErrors = 12;
    private readonly List<string> _errors = new();

    public bool Initialized { get; set; }

    public ApplicationPath Path { get; set; } = ApplicationPath.Unknown;
    public string PathDetail { get; set; } = "Glamourer has not been checked yet.";

    public string LastTransition { get; set; } = "None yet.";
    public DateTime? LastTransitionAt { get; set; }

    public string LastApplyResult { get; set; } = "Nothing applied yet.";
    public bool LastApplySucceeded { get; set; }
    public DateTime? LastApplyAt { get; set; }
    public string LastAppliedFields { get; set; } = string.Empty;

    /// <summary> Set when a verification re-read shows applied fields were changed by something else. </summary>
    public string? ConflictWarning { get; set; }
    public DateTime? ConflictAt { get; set; }

    public bool LockHeld { get; set; }
    public DateTime? LockReleaseAt { get; set; }

    public IReadOnlyList<string> Errors
        => _errors;

    public void AddError(string message)
    {
        _errors.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_errors.Count > MaxErrors)
            _errors.RemoveRange(MaxErrors, _errors.Count - MaxErrors);
    }

    public void ClearErrors()
        => _errors.Clear();
}
