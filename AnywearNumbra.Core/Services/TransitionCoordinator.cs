using AnywearNumbra.Models;

namespace AnywearNumbra.Services;

public enum TransitionKind
{
    /// <summary> Moved to a different, non-duty territory (also: leaving an instance). </summary>
    ZoneChange,

    /// <summary> Entered a dungeon, trial, raid, alliance raid or other duty. </summary>
    DutyEntry,

    /// <summary> Loading screen ended in the same territory (same-zone teleport, instance change). </summary>
    PostLoading,
}

public enum TransitionSignal
{
    TerritoryChanged,
    ZoneInit,
    LoadingFinished,
}

public enum TransitionPhase
{
    Waiting,
    InFlight,
    Succeeded,
    Failed,
    TimedOut,
    Superseded,
    Skipped,
}

public enum ApplyOutcome
{
    Success,

    /// <summary> Might work later (actor not ready, Glamourer not loaded yet). Retried within limits. </summary>
    Retryable,

    /// <summary> Will not work by retrying (design missing, nothing to apply, state locked by someone else). </summary>
    Permanent,
}

/// <summary> Timing and trigger options for <see cref="TransitionCoordinator"/>. </summary>
public sealed class TransitionOptions
{
    public bool ZoneChangeEnabled { get; set; } = true;
    public bool DutyEntryEnabled { get; set; } = true;
    public bool PostLoadingEnabled { get; set; } = true;
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(1500);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary> Give up if the player never becomes available after a transition. </summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(2);

    public static TransitionOptions FromSettings(AnywearSettings s)
        => new()
        {
            ZoneChangeEnabled  = s.TriggerOnZoneChange,
            DutyEntryEnabled   = s.TriggerOnDutyEntry,
            PostLoadingEnabled = s.TriggerOnPostLoading,
            SettleDelay        = TimeSpan.FromMilliseconds(Math.Max(0, s.PostTransitionDelayMs)),
            MaxRetries         = Math.Max(0, s.RetryCount),
            RetryInterval      = TimeSpan.FromMilliseconds(Math.Max(0, s.RetryIntervalMs)),
            DuplicateWindow    = TimeSpan.FromMilliseconds(Math.Max(0, s.DuplicateWindowMs)),
        };
}

/// <summary> One detected transition. </summary>
public sealed class TransitionRecord
{
    public required long Id { get; init; }
    public required uint TerritoryId { get; init; }
    public TransitionKind Kind { get; internal set; }
    public required DateTime DetectedAt { get; init; }
    public DateTime LastSignalAt { get; internal set; }
    public TransitionPhase Phase { get; internal set; } = TransitionPhase.Waiting;
    public int Attempts { get; internal set; }
    public DateTime NextAttemptAt { get; internal set; }
    public DateTime? FinishedAt { get; internal set; }
    public int MergedSignals { get; internal set; }
    public string Detail { get; internal set; } = string.Empty;
}

/// <summary> What the game looks like this frame. Cheap booleans only. </summary>
public readonly record struct GameSnapshot(bool IsLoading, bool PlayerAvailable);

/// <summary>
/// Turns noisy, duplicated and partially ordered game signals into at most one application per transition.
/// <para>
/// Single-threaded by design: every method must be called from the game's framework thread.
/// There are no timers or sleeps — the caller drives it with <see cref="Tick"/> once per framework update,
/// passing the current time, which also makes it fully deterministic under test.
/// </para>
/// </summary>
public sealed class TransitionCoordinator
{
    private long _nextId = 1;
    private TransitionRecord? _pending;
    private uint _knownTerritory;
    private uint _lastFinishedTerritory;
    private DateTime _lastFinishedAt = DateTime.MinValue;
    private DateTime _lastLoadingSeenAt = DateTime.MinValue;

    public TransitionCoordinator(TransitionOptions options, uint currentTerritory = 0)
    {
        Options = options;
        _knownTerritory = currentTerritory;
    }

    public TransitionOptions Options { get; set; }

    /// <summary> The transition currently waiting or being applied, if any. </summary>
    public TransitionRecord? Pending
        => _pending;

    /// <summary> The most recently finished transition. </summary>
    public TransitionRecord? LastFinished { get; private set; }

    /// <summary> Why the most recent signal did not create or update a transition. </summary>
    public string LastIgnoredReason { get; private set; } = string.Empty;

    public uint KnownTerritory
        => _knownTerritory;

    /// <summary> Feed a game signal. Returns the transition it created or merged into, or null if it was ignored. </summary>
    public TransitionRecord? Signal(TransitionSignal signal, uint territoryId, bool isDuty, DateTime now)
    {
        if (territoryId == 0)
        {
            LastIgnoredReason = $"{signal} without a territory.";
            return null;
        }

        var territoryChanged = territoryId != _knownTerritory;
        _knownTerritory = territoryId;

        // Merge into the pending transition for the same destination.
        if (_pending is { } pending && pending.TerritoryId == territoryId)
        {
            pending.LastSignalAt = now;
            pending.MergedSignals++;
            if (isDuty && pending.Kind != TransitionKind.DutyEntry)
                pending.Kind = TransitionKind.DutyEntry;
            LastIgnoredReason = string.Empty;
            return pending;
        }

        // Duplicate signals arriving after the transition already finished.
        if (_pending is null && territoryId == _lastFinishedTerritory && now - _lastFinishedAt < Options.DuplicateWindow)
        {
            LastIgnoredReason = $"{signal} for territory {territoryId} ignored: duplicate within {Options.DuplicateWindow.TotalSeconds:0.#}s.";
            return null;
        }

        // A newer destination replaces a transition that never got applied.
        if (_pending is { } superseded)
            Finish(superseded, TransitionPhase.Superseded, $"Superseded by a transition to territory {territoryId}.", now);

        var kind = isDuty
            ? TransitionKind.DutyEntry
            : territoryChanged
                ? TransitionKind.ZoneChange
                : TransitionKind.PostLoading;

        _pending = new TransitionRecord
        {
            Id            = _nextId++,
            TerritoryId   = territoryId,
            Kind          = kind,
            DetectedAt    = now,
            LastSignalAt  = now,
            NextAttemptAt = now,
            Detail        = $"Detected via {signal}.",
        };
        LastIgnoredReason = string.Empty;
        return _pending;
    }

    /// <summary>
    /// Advance time. Returns a transition exactly when it should be applied now (it is then InFlight and the
    /// caller must call <see cref="Report"/>). Returns null otherwise.
    /// </summary>
    public TransitionRecord? Tick(DateTime now, GameSnapshot snapshot)
    {
        if (snapshot.IsLoading)
            _lastLoadingSeenAt = now;

        if (_pending is not { Phase: TransitionPhase.Waiting } pending)
            return null;

        if (now - pending.DetectedAt > Options.MaxWait)
        {
            Finish(pending, TransitionPhase.TimedOut, "Gave up: the character never became available after the transition.", now);
            return null;
        }

        if (snapshot.IsLoading || !snapshot.PlayerAvailable)
            return null;

        var settleFrom = pending.LastSignalAt > _lastLoadingSeenAt ? pending.LastSignalAt : _lastLoadingSeenAt;
        if (now - settleFrom < Options.SettleDelay || now < pending.NextAttemptAt)
            return null;

        if (!IsEnabled(pending.Kind))
        {
            Finish(pending, TransitionPhase.Skipped, $"{pending.Kind} trigger is disabled.", now);
            return null;
        }

        pending.Phase = TransitionPhase.InFlight;
        pending.Attempts++;
        return pending;
    }

    /// <summary> Report the result of an application returned by <see cref="Tick"/>. </summary>
    public void Report(long transitionId, ApplyOutcome outcome, string detail, DateTime now)
    {
        if (_pending is not { Phase: TransitionPhase.InFlight } pending || pending.Id != transitionId)
            return;

        switch (outcome)
        {
            case ApplyOutcome.Success:
                Finish(pending, TransitionPhase.Succeeded, detail, now);
                break;
            case ApplyOutcome.Permanent:
                Finish(pending, TransitionPhase.Failed, detail, now);
                break;
            default:
                if (pending.Attempts >= 1 + Options.MaxRetries)
                {
                    Finish(pending, TransitionPhase.Failed, $"Gave up after {pending.Attempts} attempt(s): {detail}", now);
                }
                else
                {
                    pending.Phase         = TransitionPhase.Waiting;
                    pending.NextAttemptAt = now + Options.RetryInterval;
                    pending.Detail        = $"Attempt {pending.Attempts} failed, retrying: {detail}";
                }

                break;
        }
    }

    /// <summary> Forget the pending transition (for example when automatic changes are switched off). </summary>
    public void Cancel(string reason, DateTime now)
    {
        if (_pending is { } pending)
            Finish(pending, TransitionPhase.Skipped, reason, now);
    }

    public bool IsEnabled(TransitionKind kind)
        => kind switch
        {
            TransitionKind.ZoneChange  => Options.ZoneChangeEnabled,
            TransitionKind.DutyEntry   => Options.DutyEntryEnabled,
            TransitionKind.PostLoading => Options.PostLoadingEnabled,
            _                          => false,
        };

    private void Finish(TransitionRecord record, TransitionPhase phase, string detail, DateTime now)
    {
        record.Phase      = phase;
        record.Detail     = detail;
        record.FinishedAt = now;
        LastFinished      = record;
        if (ReferenceEquals(_pending, record))
            _pending = null;

        // Superseded transitions did not complete their destination, so they must not suppress it later.
        if (phase is not TransitionPhase.Superseded)
        {
            _lastFinishedTerritory = record.TerritoryId;
            _lastFinishedAt        = now;
        }
    }
}
