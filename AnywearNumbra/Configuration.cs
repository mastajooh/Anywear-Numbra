using AnywearNumbra.Models;
using Dalamud.Configuration;

namespace AnywearNumbra;

/// <summary>
/// Dalamud-persisted configuration. All settings live in <see cref="AnywearSettings"/> (in AnywearNumbra.Core)
/// so their defaults and migration can be unit-tested without Dalamud.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary> Dalamud's configuration version. Settings-level migration uses <see cref="AnywearSettings.SettingsVersion"/>. </summary>
    public int Version { get; set; } = 1;

    public AnywearSettings Settings { get; set; } = new();
}
