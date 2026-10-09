using System.Text.RegularExpressions;

namespace AnywearNumbra.Tests;

/// <summary>
/// Source-level guarantees that cannot be expressed as runtime tests without the game:
/// exactly one code path sends appearance to Glamourer, and no code mutates Penumbra.
/// </summary>
public partial class StructuralTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PluginDir = Path.Combine(RepoRoot, "AnywearNumbra");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AnywearNumbra.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (AnywearNumbra.slnx).");
    }

    private static IEnumerable<(string File, string Text)> Sources(params string[] directories)
        => directories
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [GeneratedRegex("\"(Glamourer|Penumbra)\\.[A-Za-z0-9.]+\"")]
    private static partial Regex IpcLabel();

    [Fact]
    public void OnlyGlamourerServiceReferencesGlamourerApplyEndpoints()
    {
        var applyLabels = new[] { "Glamourer.ApplyState", "Glamourer.ApplyStateName", "Glamourer.ApplyDesign", "Glamourer.ApplyDesignName" };
        foreach (var (file, text) in Sources("AnywearNumbra", "AnywearNumbra.Core"))
        {
            if (Path.GetFileName(file) == "GlamourerService.cs")
                continue;

            foreach (var label in applyLabels)
                Assert.False(text.Contains($"\"{label}"), $"{file} references {label}.");
            Assert.DoesNotContain("_applyState", text);
        }
    }

    [Fact]
    public void GlamourerService_InvokesApplyStateExactlyOnce_InsideApplyEquipmentScoped_AndNeverApplyDesign()
    {
        var text = File.ReadAllText(Path.Combine(PluginDir, "Services", "GlamourerService.cs"));

        Assert.DoesNotContain("\"Glamourer.ApplyDesign", text);
        Assert.DoesNotContain("\"Glamourer.ApplyStateName", text);
        Assert.DoesNotContain("\"Glamourer.Revert", text);
        Assert.DoesNotContain("\"Glamourer.SetItem", text);

        var invocations = Regex.Matches(text, @"_applyState\.InvokeFunc\(");
        Assert.Single(invocations);

        var methodStart = text.IndexOf("public GlamourerApplyResult ApplyEquipmentScoped(", StringComparison.Ordinal);
        var nextMember  = text.IndexOf("public JsonObject? ReadState(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && nextMember > methodStart, "ApplyEquipmentScoped or ReadState not found.");
        Assert.InRange(invocations[0].Index, methodStart, nextMember);

        // The payload sent must be the filter's output.
        var body = text[methodStart..nextMember];
        Assert.Contains("EquipmentScopeFilter.Merge(", body);
        Assert.Contains("EquipmentScopeFilter.ComputeApplyFlags(", body);
        Assert.Contains("JObject.Parse(merge.State.ToJsonString())", body);
    }

    [Fact]
    public void OnlyOutfitControllerCallsApplyEquipmentScoped_FromASingleMethod()
    {
        foreach (var (file, text) in Sources("AnywearNumbra"))
        {
            var name  = Path.GetFileName(file);
            var calls = Regex.Matches(text, @"\.ApplyEquipmentScoped\(").Count;
            if (name == "OutfitController.cs")
                Assert.Equal(1, calls);
            else if (name != "GlamourerService.cs")
                Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void OnlyVerifiedIpcLabelsAreUsed_AndPenumbraIsReadOnly()
    {
        var allowed = new HashSet<string>
        {
            "Glamourer.ApiVersion.V2", "Glamourer.Initialized", "Glamourer.Disposed", "Glamourer.GetDesignList.V2",
            "Glamourer.GetDesignJObject", "Glamourer.GetState", "Glamourer.ApplyState", "Glamourer.UnlockAll",
            "Penumbra.ApiVersion.V5", "Penumbra.Initialized", "Penumbra.Disposed", "Penumbra.GetEnabledState",
            "Penumbra.GetCollectionForObject.V5", "Penumbra.GetModList", "Penumbra.GetChangedItems.V5",
            "Penumbra.GetCurrentModSettings.V5",
        };

        foreach (var (file, text) in Sources("AnywearNumbra", "AnywearNumbra.Core"))
        {
            foreach (Match match in IpcLabel().Matches(text))
            {
                var label = match.Value.Trim('"');
                Assert.True(allowed.Contains(label), $"{Path.GetFileName(file)} uses unverified or non-read-only IPC label '{label}'.");
            }
        }
    }

    [Fact]
    public void PluginNeverReadsGlamourerOrPenumbraFilesDirectly()
    {
        foreach (var (file, text) in Sources("AnywearNumbra", "AnywearNumbra.Core"))
        {
            Assert.DoesNotContain("File.Read", text);
            Assert.DoesNotContain("File.Write", text);
            Assert.False(Regex.IsMatch(text, @"(?<![A-Za-z_])Directory\."), $"{file} uses System.IO.Directory.");
            Assert.False(text.Contains("pluginConfigs", StringComparison.OrdinalIgnoreCase), $"{file} references plugin config folders.");
        }
    }
}
