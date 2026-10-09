using AnywearNumbra.Services;

namespace AnywearNumbra.Tests;

public class TransitionCoordinatorTests
{
    private const uint Limsa = 128;
    private const uint Gridania = 132;
    private const uint Sastasha = 1036;

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GameSnapshot Ready = new(IsLoading: false, PlayerAvailable: true);
    private static readonly GameSnapshot Loading = new(IsLoading: true, PlayerAvailable: false);
    private static readonly GameSnapshot NoPlayer = new(IsLoading: false, PlayerAvailable: false);

    private static TransitionOptions Options(int retries = 2)
        => new()
        {
            SettleDelay     = TimeSpan.FromSeconds(1),
            MaxRetries      = retries,
            RetryInterval   = TimeSpan.FromSeconds(2),
            DuplicateWindow = TimeSpan.FromSeconds(5),
            MaxWait         = TimeSpan.FromSeconds(60),
        };

    private static DateTime At(double seconds)
        => T0.AddSeconds(seconds);

    /// <summary> Drive the coordinator frame by frame and count how often it asks to apply. </summary>
    private static List<TransitionRecord> Run(TransitionCoordinator c, double from, double to, GameSnapshot snapshot,
        ApplyOutcome outcome = ApplyOutcome.Success)
    {
        var applied = new List<TransitionRecord>();
        for (var t = from; t <= to; t += 0.1)
        {
            if (c.Tick(At(t), snapshot) is { } transition)
            {
                applied.Add(transition);
                c.Report(transition.Id, outcome, "test", At(t));
            }
        }

        return applied;
    }

    [Fact]
    public void ZoneChange_WithDuplicatedSignals_IsAppliedExactlyOnce()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        c.Signal(TransitionSignal.ZoneInit, Gridania, false, At(0.2));
        c.Signal(TransitionSignal.LoadingFinished, Gridania, false, At(2));
        c.Signal(TransitionSignal.LoadingFinished, Gridania, false, At(2.05)); // BetweenAreas51 ends too

        Assert.Empty(Run(c, 0, 2.9, Ready));         // settle delay runs from the last signal
        var applied = Run(c, 3.0, 20, Ready);
        Assert.Single(applied);
        Assert.Equal(TransitionKind.ZoneChange, applied[0].Kind);
        Assert.Equal(TransitionPhase.Succeeded, c.LastFinished!.Phase);
    }

    [Fact]
    public void NothingIsApplied_WhileLoading_OrWithoutAPlayer()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        Assert.Empty(Run(c, 0, 10, Loading));
        Assert.Empty(Run(c, 10, 20, NoPlayer));
        // Settle delay is measured from the end of the loading screen (last seen at t=10).
        Assert.Single(Run(c, 20, 25, Ready));
    }

    [Fact]
    public void DuplicateSignalsAfterCompletion_AreIgnoredInsideTheWindow_ButNotAfterIt()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        Assert.Single(Run(c, 0, 3, Ready));

        Assert.Null(c.Signal(TransitionSignal.LoadingFinished, Gridania, false, At(4)));
        Assert.Empty(Run(c, 4, 10, Ready));

        // Re-entering (or teleporting within) the territory later is a new transition.
        Assert.NotNull(c.Signal(TransitionSignal.LoadingFinished, Gridania, false, At(30)));
        var again = Run(c, 30, 35, Ready);
        Assert.Single(again);
        Assert.Equal(TransitionKind.PostLoading, again[0].Kind);
    }

    [Fact]
    public void DutyEntry_IsClassified_AndAZoneInitCanUpgradeTheKind()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        var record = c.Signal(TransitionSignal.TerritoryChanged, Sastasha, false, At(0)); // sheet had no duty info
        Assert.Equal(TransitionKind.ZoneChange, record!.Kind);
        c.Signal(TransitionSignal.ZoneInit, Sastasha, true, At(0.5));                       // ZoneInit knows it's a duty
        var applied = Run(c, 0, 5, Ready);
        Assert.Single(applied);
        Assert.Equal(TransitionKind.DutyEntry, applied[0].Kind);
    }

    [Fact]
    public void RetryableFailures_AreRetriedWithinLimits_ThenGivenUp()
    {
        var c = new TransitionCoordinator(Options(retries: 2), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        var attempts = Run(c, 0, 30, Ready, ApplyOutcome.Retryable);
        Assert.Equal(3, attempts.Count); // first attempt + 2 retries
        Assert.Equal(TransitionPhase.Failed, c.LastFinished!.Phase);
        Assert.Null(c.Pending);
    }

    [Fact]
    public void RetriesAreSpacedByTheRetryInterval()
    {
        var c = new TransitionCoordinator(Options(retries: 1), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        var first = c.Tick(At(1.5), Ready)!;
        c.Report(first.Id, ApplyOutcome.Retryable, "actor not ready", At(1.5));
        Assert.Null(c.Tick(At(3.0), Ready));
        Assert.NotNull(c.Tick(At(3.6), Ready));
    }

    [Fact]
    public void PermanentFailures_AreNotRetried()
    {
        var c = new TransitionCoordinator(Options(retries: 5), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        Assert.Single(Run(c, 0, 30, Ready, ApplyOutcome.Permanent));
        Assert.Equal(TransitionPhase.Failed, c.LastFinished!.Phase);
    }

    [Fact]
    public void DisabledTriggerKinds_AreSkipped()
    {
        var options = Options();
        options.ZoneChangeEnabled = false;
        var c = new TransitionCoordinator(options, Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        Assert.Empty(Run(c, 0, 10, Ready));
        Assert.Equal(TransitionPhase.Skipped, c.LastFinished!.Phase);

        c.Signal(TransitionSignal.TerritoryChanged, Sastasha, true, At(20));
        Assert.Single(Run(c, 20, 25, Ready)); // duty entry still enabled
    }

    [Fact]
    public void ANewDestination_SupersedesAnUnappliedTransition()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        c.Signal(TransitionSignal.TerritoryChanged, Sastasha, true, At(0.5));
        var applied = Run(c, 0, 10, Ready);
        Assert.Single(applied);
        Assert.Equal(Sastasha, applied[0].TerritoryId);

        // The superseded destination is not considered "done": going back there later still applies.
        Assert.NotNull(c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(11)));
    }

    [Fact]
    public void TransitionsTimeOut_WhenThePlayerNeverBecomesAvailable()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        Assert.Empty(Run(c, 0, 70, NoPlayer));
        Assert.Equal(TransitionPhase.TimedOut, c.LastFinished!.Phase);
        Assert.Null(c.Pending);
    }

    [Fact]
    public void SignalsWithoutATerritory_AreIgnored()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        Assert.Null(c.Signal(TransitionSignal.LoadingFinished, 0, false, At(0)));
        Assert.Null(c.Pending);
    }

    [Fact]
    public void ReportsForStaleTransitions_AreIgnored()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        var t = c.Tick(At(2), Ready)!;
        c.Report(t.Id + 100, ApplyOutcome.Success, "wrong id", At(2));
        Assert.Equal(TransitionPhase.InFlight, c.Pending!.Phase);
        c.Report(t.Id, ApplyOutcome.Success, "right id", At(2));
        Assert.Null(c.Pending);
    }

    [Fact]
    public void Cancel_DropsThePendingTransition()
    {
        var c = new TransitionCoordinator(Options(), Limsa);
        c.Signal(TransitionSignal.TerritoryChanged, Gridania, false, At(0));
        c.Cancel("disabled", At(0.5));
        Assert.Empty(Run(c, 0.5, 10, Ready));
        Assert.Equal(TransitionPhase.Skipped, c.LastFinished!.Phase);
    }
}
