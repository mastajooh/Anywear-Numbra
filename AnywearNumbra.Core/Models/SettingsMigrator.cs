namespace AnywearNumbra.Models;

/// <summary> Upgrades and sanitizes persisted settings. Pure and idempotent. </summary>
public static class SettingsMigrator
{
    public const int MinDelayMs = 0;
    public const int MaxDelayMs = 30_000;
    public const int MaxRetries = 10;
    public const int MinRetryIntervalMs = 250;
    public const int MaxRetryIntervalMs = 30_000;
    public const int MaxDuplicateWindowMs = 60_000;
    public const int MinLockSeconds = 5;
    public const int MaxLockSeconds = 300;
    public const int MinVerifyDelayMs = 1_000;
    public const int MaxVerifyDelayMs = 30_000;
    public const int MaxModPieces = 10;

    /// <summary> Bring <paramref name="settings"/> to <see cref="AnywearSettings.CurrentVersion"/>. </summary>
    /// <returns> True if anything changed and the settings should be saved. </returns>
    public static bool Migrate(AnywearSettings settings)
    {
        var changed = false;

        // Version 0 means "written before versioning existed or field missing": no field renames exist yet,
        // so the upgrade is sanitizing only. Future versions add explicit steps here, in order.
        if (settings.SettingsVersion < 1)
        {
            settings.SettingsVersion = 1;
            changed = true;
        }

        changed |= Sanitize(settings);
        return changed;
    }

    /// <summary> Clamp values into supported ranges and repair collections. </summary>
    public static bool Sanitize(AnywearSettings s)
    {
        var changed = false;

        // Collections may be null after deserializing a hand-edited or truncated file.
        if (s.Outfits is null)
        {
            s.Outfits = new List<OutfitEntry>();
            changed = true;
        }

        if (s.Rules is null)
        {
            s.Rules = new List<TerritoryRule>();
            changed = true;
        }

        s.LastAppliedDesignName   ??= string.Empty;
        s.LastAppliedModDirectory ??= string.Empty;
        if (s.ExcludedModDirectories is null)
        {
            s.ExcludedModDirectories = new List<string>();
            changed = true;
        }

        changed |= Clamp(s.MinimumModPieces, 1, MaxModPieces, v => s.MinimumModPieces = v);

        // Drop null entries, empty GUIDs and duplicates (first occurrence wins, preserving user order).
        var seen = new HashSet<Guid>();
        var cleaned = new List<OutfitEntry>(s.Outfits.Count);
        foreach (var entry in s.Outfits)
        {
            if (entry is null || entry.DesignId == Guid.Empty || !seen.Add(entry.DesignId))
                continue;

            entry.LastKnownName ??= string.Empty;
            cleaned.Add(entry);
        }

        if (cleaned.Count != s.Outfits.Count)
        {
            s.Outfits = cleaned;
            changed = true;
        }

        var ruleCount = s.Rules.Count;
        s.Rules.RemoveAll(r => r is null);
        changed |= ruleCount != s.Rules.Count;
        foreach (var rule in s.Rules)
        {
            rule.TargetLabel ??= string.Empty;
            if (rule.Id == Guid.Empty)
            {
                rule.Id = Guid.NewGuid();
                changed = true;
            }
        }

        if (!Enum.IsDefined(s.ModDyeMode))
        {
            s.ModDyeMode = RandomDyeMode.Off;
            changed      = true;
        }

        if (!Enum.IsDefined(s.Mode))
        {
            s.Mode = SelectionMode.Random;
            changed = true;
        }

        changed |= Clamp(s.PostTransitionDelayMs, MinDelayMs, MaxDelayMs, v => s.PostTransitionDelayMs = v);
        changed |= Clamp(s.RetryCount, 0, MaxRetries, v => s.RetryCount = v);
        changed |= Clamp(s.RetryIntervalMs, MinRetryIntervalMs, MaxRetryIntervalMs, v => s.RetryIntervalMs = v);
        changed |= Clamp(s.DuplicateWindowMs, 0, MaxDuplicateWindowMs, v => s.DuplicateWindowMs = v);
        changed |= Clamp(s.LockDurationSeconds, MinLockSeconds, MaxLockSeconds, v => s.LockDurationSeconds = v);
        changed |= Clamp(s.VerifyDelayMs, MinVerifyDelayMs, MaxVerifyDelayMs, v => s.VerifyDelayMs = v);

        if (s.RotationIndex < 0)
        {
            s.RotationIndex = 0;
            changed = true;
        }

        return changed;
    }

    private static bool Clamp(int value, int min, int max, Action<int> set)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped == value)
            return false;

        set(clamped);
        return true;
    }
}
