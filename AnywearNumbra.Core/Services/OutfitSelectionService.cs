using AnywearNumbra.Models;

namespace AnywearNumbra.Services;

/// <summary> Random source abstraction so selection is testable. </summary>
public interface IRandomSource
{
    /// <summary> Uniform integer in [0, maxExclusive). </summary>
    int Next(int maxExclusive);
}

/// <summary> Uses the thread-safe shared <see cref="Random"/> instance. </summary>
public sealed class SharedRandomSource : IRandomSource
{
    public static readonly SharedRandomSource Instance = new();

    public int Next(int maxExclusive)
        => Random.Shared.Next(maxExclusive);
}

public sealed record SelectionResult(Guid DesignId, string Reason)
{
    public bool HasDesign
        => DesignId != Guid.Empty;

    public static SelectionResult None(string reason)
        => new(Guid.Empty, reason);
}

/// <summary>
/// Chooses which design to apply. Pure: reads settings and the set of designs Glamourer currently has,
/// never talks to Glamourer itself. Designs are always identified by GUID.
/// </summary>
public static class OutfitSelectionService
{
    /// <summary> Eligible designs for Random and Rotation, in the user's list order, skipping missing designs. </summary>
    public static IReadOnlyList<Guid> EligibleDesigns(AnywearSettings settings, IReadOnlySet<Guid> available)
    {
        var result = new List<Guid>();
        foreach (var entry in settings.Outfits)
        {
            if (entry.Eligible && entry.DesignId != Guid.Empty && available.Contains(entry.DesignId) && !result.Contains(entry.DesignId))
                result.Add(entry.DesignId);
        }

        return result;
    }

    public static SelectionResult Select(AnywearSettings settings, IReadOnlySet<Guid> available, TerritoryInfo? territory,
        IRandomSource random)
        => settings.Mode switch
        {
            SelectionMode.Random    => SelectRandom(settings, available, random),
            SelectionMode.Rotation  => SelectRotation(settings, available),
            SelectionMode.ZoneRules => SelectByRules(settings, available, territory),
            SelectionMode.Fixed     => SelectFixed(settings, available),
            SelectionMode.PenumbraMods => SelectionResult.None("Mode E picks Penumbra mods, not Glamourer designs."),
            _                       => SelectionResult.None($"Unknown selection mode {settings.Mode}."),
        };

    public static SelectionResult SelectRandom(AnywearSettings settings, IReadOnlySet<Guid> available, IRandomSource random)
    {
        var eligible = EligibleDesigns(settings, available);
        switch (eligible.Count)
        {
            case 0: return SelectionResult.None("No eligible designs. Enable some designs in the Outfits tab.");
            case 1: return new SelectionResult(eligible[0], "Random (only one eligible design)");
        }

        // Two or more: never repeat the previous design immediately.
        var candidates = eligible.Where(id => id != settings.LastAppliedDesignId).ToList();
        var pick = Math.Clamp(random.Next(candidates.Count), 0, candidates.Count - 1);
        return new SelectionResult(candidates[pick], "Random");
    }

    public static SelectionResult SelectRotation(AnywearSettings settings, IReadOnlySet<Guid> available)
    {
        var eligible = EligibleDesigns(settings, available);
        if (eligible.Count == 0)
            return SelectionResult.None("No eligible designs. Enable some designs in the Outfits tab.");

        int next;
        var lastPosition = IndexOf(eligible, settings.RotationLastDesignId);
        if (lastPosition >= 0)
        {
            next = (lastPosition + 1) % eligible.Count;
        }
        else
        {
            // The last design was removed or became ineligible: the design that followed it now sits at its old index.
            next = Mod(settings.RotationIndex, eligible.Count);
        }

        return new SelectionResult(eligible[next], $"Rotation ({next + 1} of {eligible.Count})");
    }

    public static SelectionResult SelectFixed(AnywearSettings settings, IReadOnlySet<Guid> available)
    {
        if (settings.FixedDesignId == Guid.Empty)
            return SelectionResult.None("No fixed design selected.");

        return available.Contains(settings.FixedDesignId)
            ? new SelectionResult(settings.FixedDesignId, "Fixed design")
            : SelectionResult.None("The fixed design no longer exists in Glamourer.");
    }

    public static SelectionResult SelectByRules(AnywearSettings settings, IReadOnlySet<Guid> available, TerritoryInfo? territory)
    {
        var skippedMissing = 0;
        if (territory is not null)
        {
            foreach (var rule in TerritoryRuleService.MatchingRules(settings.Rules, territory))
            {
                if (rule.DesignId != Guid.Empty && available.Contains(rule.DesignId))
                    return new SelectionResult(rule.DesignId, $"Rule: {TerritoryRuleService.Describe(rule)}");

                ++skippedMissing;
            }
        }

        var suffix = skippedMissing > 0 ? $" ({skippedMissing} matching rule(s) skipped: design missing)" : string.Empty;
        if (settings.FallbackDesignId != Guid.Empty && available.Contains(settings.FallbackDesignId))
            return new SelectionResult(settings.FallbackDesignId, "Fallback" + suffix);

        return SelectionResult.None("No rule matched and no valid fallback design is set" + suffix + ".");
    }

    /// <summary> Record a successful application: last applied design and the rotation position. </summary>
    public static void RecordApplied(AnywearSettings settings, Guid designId, string designName, IReadOnlySet<Guid> available,
        DateTime nowUtc)
    {
        settings.LastAppliedDesignId = designId;
        settings.LastAppliedDesignName = designName;
        settings.LastAppliedAtUtc = nowUtc;

        var position = IndexOf(EligibleDesigns(settings, available), designId);
        if (position >= 0)
        {
            settings.RotationLastDesignId = designId;
            settings.RotationIndex = position;
        }
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid id)
    {
        if (id == Guid.Empty)
            return -1;

        for (var i = 0; i < list.Count; ++i)
        {
            if (list[i] == id)
                return i;
        }

        return -1;
    }

    private static int Mod(int value, int modulus)
        => ((value % modulus) + modulus) % modulus;
}
