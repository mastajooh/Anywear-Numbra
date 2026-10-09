using System.Numerics;
using AnywearNumbra.Models;
using AnywearNumbra.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnywearNumbra.Windows;

/// <summary>
/// The configuration window. Settings are edited directly and saved immediately; every action that talks to
/// Glamourer or Penumbra is queued onto the framework thread via <see cref="AnywearNumbraPlugin.RunOnFramework"/>.
/// </summary>
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);

    private const int MaxTerritoryRows = 200;

    private readonly AnywearNumbraPlugin _plugin;
    private Guid _manualDesign;
    private string _designFilter = string.Empty;
    private string _territoryFilter = string.Empty;
    private string _modFilter = string.Empty;
    private DateTime _resetArmedUntil = DateTime.MinValue;

    public ConfigWindow(AnywearNumbraPlugin plugin)
        : base("Anywear Numbra###AnywearNumbraConfig")
    {
        _plugin       = plugin;
        Size          = new Vector2(640, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(4000, 4000),
        };
    }

    private AnywearSettings Settings
        => _plugin.Configuration.Settings;

    private OutfitController Controller
        => _plugin.Controller;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##anywearTabs"))
            return;

        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneral();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Outfits"))
        {
            DrawOutfits();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Status"))
        {
            DrawStatus();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Actions"))
        {
            DrawActions();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---- General ------------------------------------------------------------------------------------

    private void DrawGeneral()
    {
        Checkbox("Enable automatic outfit changes", Settings.AutomaticChangesEnabled, v => Settings.AutomaticChangesEnabled = v,
            "Master switch. Manual buttons in the Actions tab always work.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Triggers");
        ImGui.Separator();
        Checkbox("Zone change (including leaving an instance)", Settings.TriggerOnZoneChange, v => Settings.TriggerOnZoneChange = v);
        Checkbox("Duty entry (dungeon, trial, raid, alliance raid, other duties)", Settings.TriggerOnDutyEntry,
            v => Settings.TriggerOnDutyEntry = v);
        Checkbox("After a loading screen in the same zone (teleport, instance change)", Settings.TriggerOnPostLoading,
            v => Settings.TriggerOnPostLoading = v);

        ImGui.Spacing();
        ImGui.TextUnformatted("Selection");
        ImGui.Separator();
        EnumCombo("Selection mode", Settings.Mode, v => Settings.Mode = v, ModeLabel);
        Help(Settings.Mode switch
        {
            SelectionMode.Random   => "Random pick from eligible designs; never the same design twice in a row when two or more are eligible.",
            SelectionMode.Rotation => "Cycles through eligible designs in the order of the Outfits list. Position is remembered across restarts.",
            SelectionMode.ZoneRules => "Uses the rules in the Outfits tab: exact territory > duty type > category > fallback.",
            SelectionMode.PenumbraMods => "Picks a random ENABLED Penumbra mod that changes armor and wears every armor piece it changes. "
              + "Manage the list under 'Penumbra mod outfits' in the Outfits tab. Glamourer designs are not used in this mode.",
            _                      => "Always applies the fixed design chosen in the Outfits tab.",
        });

        ImGui.Spacing();
        ImGui.TextUnformatted("Timing");
        ImGui.Separator();
        IntInput("Delay after loading (ms)", Settings.PostTransitionDelayMs, 250, v => Settings.PostTransitionDelayMs = v);
        IntInput("Retry count", Settings.RetryCount, 1, v => Settings.RetryCount = v);
        IntInput("Retry interval (ms)", Settings.RetryIntervalMs, 250, v => Settings.RetryIntervalMs = v);
        IntInput("Duplicate-event window (ms)", Settings.DuplicateWindowMs, 500, v => Settings.DuplicateWindowMs = v);
        Help("Events for the same territory inside this window count as one transition.");
        Checkbox("Show notifications", Settings.ShowNotifications, v => Settings.ShowNotifications = v);

        ImGui.Spacing();
        ImGui.TextUnformatted("Appearance scope");
        ImGui.Separator();
        ImGui.TextColored(Muted, "Always applied: head, body, hands, legs, feet, earrings, necklace, bracelets, rings (with their dyes and crests).");
        ImGui.TextColored(Muted, "Never applied: hair, face, skin, body, race/clan/gender, visual effects, advanced dyes.");
        Checkbox("Weapons", Settings.ApplyWeapons, v => Settings.ApplyWeapons = v,
            "Also apply main hand, off hand and weapon visibility. Off: your weapons stay as they are.");
        Checkbox("Facewear", Settings.ApplyFacewear, v => Settings.ApplyFacewear = v,
            "Also apply the glasses/facewear slot. Off: your facewear stays as it is.");
        Checkbox("Hat and visor visibility", Settings.ApplyHatVisorVisibility, v => Settings.ApplyHatVisorVisibility = v,
            "Also apply 'hat visible' and 'visor toggled' from the design. Off: your current visibility stays.");

        ImGui.BeginDisabled(true);
        var modAssociations = Settings.ApplyDesignModAssociations;
        ImGui.Checkbox("Apply design mod associations (unavailable)", ref modAssociations);
        ImGui.EndDisabled();
        Help("Not available: Glamourer applies mod associations only as part of a full-design application, "
          + "which would also change hair, face and body. Anywear Numbra never changes Penumbra mod settings.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Glamourer interplay");
        ImGui.Separator();
        Checkbox("Lock state after applying", Settings.LockStateAfterApply, v => Settings.LockStateAfterApply = v,
            "Locks your Glamourer state with Anywear Numbra's key so Glamourer Automation cannot immediately override it. "
          + "The lock is released automatically after the duration below, from the Actions tab, and when the plugin unloads.");
        if (Settings.LockStateAfterApply)
            IntInput("Lock duration (seconds)", Settings.LockDurationSeconds, 5, v => Settings.LockDurationSeconds = v);
        Checkbox("Check for overrides after applying", Settings.VerifyAfterApply, v => Settings.VerifyAfterApply = v,
            "Re-reads your equipment a few seconds later and warns if something else changed it (usually Glamourer Automation).");
    }

    // ---- Outfits ------------------------------------------------------------------------------------

    private void DrawOutfits()
    {
        ImGui.TextUnformatted($"Last applied: {Controller.LastAppliedLabel}");
        if (Settings.LastAppliedAtUtc is { } at)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"at {at.ToLocalTime():g}");
        }

        if (ImGui.Button("Refresh designs from Glamourer"))
            _plugin.RunOnFramework(() => Controller.RefreshDesigns());
        ImGui.SameLine();
        if (ImGui.Button("All eligible"))
            SetAllEligible(true);
        ImGui.SameLine();
        if (ImGui.Button("None eligible"))
            SetAllEligible(false);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Filter##designFilter", ref _designFilter, 128);

        DrawOutfitTable();

        ImGui.Spacing();
        ImGui.TextUnformatted("Fixed design (Mode D)");
        ImGui.Separator();
        if (DesignCombo("Fixed design##fixed", Settings.FixedDesignId, true, out var fixedId))
            Save(() => Settings.FixedDesignId = fixedId);

        ImGui.Spacing();
        ImGui.TextUnformatted("Rotation (Mode B)");
        ImGui.Separator();
        var rotationLast = Settings.RotationLastDesignId == Guid.Empty ? "start of list" : Controller.DesignName(Settings.RotationLastDesignId);
        ImGui.TextUnformatted($"Rotation continues after: {rotationLast}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Restart rotation"))
            Save(() =>
            {
                Settings.RotationLastDesignId = Guid.Empty;
                Settings.RotationIndex        = 0;
            });
        Help("Rotation follows the order of the list above; use the arrows to reorder.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Zone rules (Mode C)");
        ImGui.Separator();
        DrawRules();

        ImGui.Spacing();
        ImGui.TextUnformatted("Penumbra mod outfits (Mode E)");
        ImGui.Separator();
        DrawModOutfits();
    }

    private void DrawModOutfits()
    {
        Help("Enabled mods in your character's Penumbra collection that change armor. Each pick wears every armor piece "
          + "that mod changes; other slots, dyes, hair, face and body stay as they are. Untick a mod to skip it.");
        if (ImGui.Button("Scan Penumbra mods"))
            _plugin.RunOnFramework(() => Controller.RescanMods());
        ImGui.SameLine();
        ImGui.TextColored(Muted, Controller.ModsScannedAt is { } at ? $"{Controller.ModScanMessage} ({at:T})" : Controller.ModScanMessage);

        IntInput("Minimum armor pieces per mod", Settings.MinimumModPieces, 1, v => Settings.MinimumModPieces = v);
        Help("Mods that change fewer slots than this (for example a single ring) are never picked.");

        var outfits = Controller.ModOutfits;
        if (outfits.Count == 0)
            return;

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Filter##modFilter", ref _modFilter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Tick all"))
            Save(() => Settings.ExcludedModDirectories.Clear());
        ImGui.SameLine();
        if (ImGui.SmallButton("Untick all"))
            Save(() =>
            {
                Settings.ExcludedModDirectories.Clear();
                Settings.ExcludedModDirectories.AddRange(outfits.Select(o => o.ModDirectory));
            });

        var tableHeight = Math.Min(300f, 30f + 26f * outfits.Count);
        if (!ImGui.BeginTable("##modOutfits", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Slots", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var outfit in outfits)
        {
            if (_modFilter.Length > 0 && !outfit.ModName.Contains(_modFilter, StringComparison.CurrentCultureIgnoreCase))
                continue;

            ImGui.PushID(outfit.ModDirectory);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var used = !Settings.ExcludedModDirectories.Contains(outfit.ModDirectory);
            if (ImGui.Checkbox("##useMod", ref used))
                Save(() =>
                {
                    Settings.ExcludedModDirectories.Remove(outfit.ModDirectory);
                    if (!used)
                        Settings.ExcludedModDirectories.Add(outfit.ModDirectory);
                });

            ImGui.TableNextColumn();
            if (outfit.PieceCount < Settings.MinimumModPieces)
                ImGui.TextColored(Muted, outfit.ModName);
            else
                ImGui.TextUnformatted(outfit.ModName);

            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, string.Join(", ", outfit.Slots.Keys));
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawOutfitTable()
    {
        if (Settings.Outfits.Count == 0)
        {
            ImGui.TextColored(Warn, _plugin.Glamourer.IsAvailable
                ? "No designs yet. Create designs in Glamourer, then press Refresh."
                : "Glamourer is not available, so no designs can be listed.");
            return;
        }

        var tableHeight = Math.Min(260f, 30f + 26f * Settings.Outfits.Count);
        if (!ImGui.BeginTable("##outfits", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Design", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        int? moveUp = null, moveDown = null, remove = null;
        var filtering = _designFilter.Length > 0;
        for (var i = 0; i < Settings.Outfits.Count; ++i)
        {
            var entry   = Settings.Outfits[i];
            var name    = Controller.DesignName(entry.DesignId);
            var missing = Controller.IsMissing(entry.DesignId);
            if (filtering && !name.Contains(_designFilter, StringComparison.CurrentCultureIgnoreCase))
                continue;

            ImGui.PushID(entry.DesignId.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var eligible = entry.Eligible;
            if (ImGui.Checkbox("##eligible", ref eligible))
            {
                entry.Eligible = eligible;
                _plugin.SaveConfiguration();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Include in Random and Rotation.");

            ImGui.TableNextColumn();
            if (missing)
                ImGui.TextColored(Bad, name);
            else
                ImGui.TextUnformatted(name);

            ImGui.TableNextColumn();
            if (!filtering)
            {
                if (ImGui.ArrowButton("##up", ImGuiDir.Up) && i > 0)
                    moveUp = i;
                ImGui.SameLine();
                if (ImGui.ArrowButton("##down", ImGuiDir.Down) && i < Settings.Outfits.Count - 1)
                    moveDown = i;
            }

            ImGui.TableNextColumn();
            if (missing && ImGui.SmallButton("Remove"))
                remove = i;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (moveUp is { } up)
            Save(() => Swap(Settings.Outfits, up, up - 1));
        else if (moveDown is { } down)
            Save(() => Swap(Settings.Outfits, down, down + 1));
        else if (remove is { } index)
            Save(() => Settings.Outfits.RemoveAt(index));
    }

    private void DrawRules()
    {
        if (DesignCombo("Fallback design##fallback", Settings.FallbackDesignId, true, out var fallbackId))
            Save(() => Settings.FallbackDesignId = fallbackId);
        Help("Used when no rule matches. Rules higher in the list win ties within the same precedence level.");

        int? moveUp = null, moveDown = null, remove = null;
        for (var i = 0; i < Settings.Rules.Count; ++i)
        {
            var rule = Settings.Rules[i];
            ImGui.PushID(rule.Id.ToString());
            ImGui.Separator();

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
                Save(() => rule.Enabled = enabled);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            EnumCombo("##kind", rule.Kind, v => rule.Kind = v, KindLabel);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 150f));
            DrawRuleTarget(rule);
            ImGui.SameLine();
            if (ImGui.ArrowButton("##up", ImGuiDir.Up) && i > 0)
                moveUp = i;
            ImGui.SameLine();
            if (ImGui.ArrowButton("##down", ImGuiDir.Down) && i < Settings.Rules.Count - 1)
                moveDown = i;
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
                remove = i;

            ImGui.SetNextItemWidth(300);
            if (DesignCombo("Design##ruleDesign", rule.DesignId, false, out var designId))
                Save(() => rule.DesignId = designId);
            if (rule.DesignId != Guid.Empty && Controller.IsMissing(rule.DesignId))
            {
                ImGui.SameLine();
                ImGui.TextColored(Bad, "missing - rule is skipped");
            }

            ImGui.PopID();
        }

        if (moveUp is { } up)
            Save(() => Swap(Settings.Rules, up, up - 1));
        else if (moveDown is { } down)
            Save(() => Swap(Settings.Rules, down, down + 1));
        else if (remove is { } index)
            Save(() => Settings.Rules.RemoveAt(index));

        ImGui.Separator();
        if (ImGui.Button("Add rule"))
            Save(() => Settings.Rules.Add(new TerritoryRule { Kind = RuleKind.Category, Category = TerritoryCategory.City }));
        ImGui.SameLine();
        if (ImGui.Button("Add rule for current zone"))
        {
            var territory = _plugin.Territories.Get(_plugin.Monitor.Coordinator.KnownTerritory);
            if (territory.TerritoryId != 0)
                Save(() => Settings.Rules.Insert(0, new TerritoryRule
                {
                    Kind        = RuleKind.ExactTerritory,
                    TerritoryId = territory.TerritoryId,
                    TargetLabel = territory.Name,
                }));
        }
    }

    private void DrawRuleTarget(TerritoryRule rule)
    {
        switch (rule.Kind)
        {
            case RuleKind.ExactTerritory:
            {
                var preview = rule.TerritoryId == 0 ? "(choose territory)" : $"{rule.TargetLabel} (#{rule.TerritoryId})";
                if (!ImGui.BeginCombo("##territory", preview))
                    return;

                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##territoryFilter", ref _territoryFilter, 64);
                var shown = 0;
                foreach (var (id, name) in _plugin.Territories.Territories)
                {
                    if (_territoryFilter.Length > 0 && !name.Contains(_territoryFilter, StringComparison.CurrentCultureIgnoreCase)
                     && !id.ToString().StartsWith(_territoryFilter, StringComparison.Ordinal))
                        continue;
                    if (++shown > MaxTerritoryRows)
                    {
                        ImGui.TextColored(Muted, "More results: refine the filter.");
                        break;
                    }

                    if (ImGui.Selectable($"{name} (#{id})##t{id}", id == rule.TerritoryId))
                        Save(() =>
                        {
                            rule.TerritoryId = id;
                            rule.TargetLabel = name;
                        });
                }

                ImGui.EndCombo();
                return;
            }
            case RuleKind.DutyType:
            {
                var preview = rule.ContentTypeId == 0 ? "(choose duty type)" : _plugin.Territories.ContentTypeName(rule.ContentTypeId);
                if (!ImGui.BeginCombo("##dutyType", preview))
                    return;

                foreach (var (id, name) in _plugin.Territories.ContentTypes)
                {
                    if (ImGui.Selectable($"{name}##c{id}", id == rule.ContentTypeId))
                        Save(() =>
                        {
                            rule.ContentTypeId = id;
                            rule.TargetLabel   = name;
                        });
                }

                ImGui.EndCombo();
                return;
            }
            default:
                EnumCombo("##category", rule.Category, v => rule.Category = v, c => c.ToString(), skip: TerritoryCategory.Unknown);
                return;
        }
    }

    // ---- Status -------------------------------------------------------------------------------------

    private void DrawStatus()
    {
        var status = _plugin.Status;
        StatusLine("Plugin", status.Initialized, status.Initialized ? "Initialized." : "Starting...");
        StatusLine("Glamourer IPC", _plugin.Glamourer.IsAvailable, _plugin.Glamourer.AvailabilityText);
        StatusLine("Penumbra IPC", _plugin.Penumbra.IsAvailable, _plugin.Penumbra.StatusText);
        if (_plugin.Penumbra.IsAvailable)
            ImGui.TextColored(Muted, $"    Collection for your character: {_plugin.Penumbra.CollectionText}");
        var player = _plugin.Monitor.Coordinator.KnownTerritory != 0;
        StatusLine("Local player / territory", player,
            player ? _plugin.Territories.Get(_plugin.Monitor.Coordinator.KnownTerritory).Name : "Not in a territory yet.");

        ImGui.Spacing();
        var pathOk = status.Path is ApplicationPath.B;
        StatusLine($"Application path: {status.Path}", pathOk, status.PathDetail);
        if (status.Path is ApplicationPath.C)
            ImGui.TextColored(Bad, "Automatic and manual application are disabled until the missing Glamourer capability is available.");

        ImGui.Spacing();
        var coordinator = _plugin.Monitor.Coordinator;
        var detection = !Settings.AutomaticChangesEnabled
            ? "Automatic changes are OFF."
            : coordinator.Pending is { } pending
                ? $"Waiting to apply transition #{pending.Id} ({pending.Kind}), attempt {pending.Attempts + 1}."
                : "Idle, watching for transitions.";
        StatusLine("Transition detection", Settings.AutomaticChangesEnabled, detection);
        ImGui.TextWrapped($"Last transition: {status.LastTransition}");
        ImGui.TextColored(status.LastApplySucceeded ? Good : Muted, $"Last application: {status.LastApplyResult}");
        if (status.LastApplyAt is { } applyAt)
            ImGui.TextColored(Muted, $"    at {applyAt:T}; fields: {(status.LastAppliedFields.Length > 0 ? status.LastAppliedFields : "-")}");
        if (status.LockHeld && status.LockReleaseAt is { } releaseAt)
            ImGui.TextColored(Warn, $"Glamourer state locked by Anywear Numbra until {releaseAt:T}.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Glamourer Automation");
        ImGui.Separator();
        if (status.ConflictWarning is { } conflict)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Bad, $"Conflict detected{(status.ConflictAt is { } c ? $" at {c:T}" : string.Empty)}: {conflict}");
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Warn,
                "Possible conflict: Glamourer Automation can also apply designs on zone changes and may override Anywear Numbra. "
              + "Glamourer's API does not report whether Automation is active for your character, so this cannot be checked directly. "
              + "If you use Automation for this character, disable that set or enable 'Lock state after applying'.");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Errors");
        ImGui.Separator();
        if (status.Errors.Count == 0)
        {
            ImGui.TextColored(Muted, "None.");
        }
        else
        {
            foreach (var error in status.Errors)
                ImGui.TextWrapped(error);
            if (ImGui.SmallButton("Clear errors"))
                status.ClearErrors();
        }
    }

    // ---- Actions ------------------------------------------------------------------------------------

    private void DrawActions()
    {
        ImGui.TextWrapped("All actions apply only the permitted equipment fields, exactly like automatic changes.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(300);
        DesignCombo("##manualDesign", _manualDesign, false, out _manualDesign);
        ImGui.SameLine();
        if (ImGui.Button("Apply selected design now"))
        {
            var id = _manualDesign;
            _plugin.RunOnFramework(() => Controller.ApplyNow(id));
        }

        if (ImGui.Button("Apply random Penumbra mod outfit now"))
            _plugin.RunOnFramework(Controller.ApplyRandomModNow);

        if (ImGui.Button("Reapply current outfit"))
            _plugin.RunOnFramework(Controller.ReapplyCurrent);
        ImGui.SameLine();
        ImGui.TextColored(Muted, Controller.LastAppliedLabel);

        if (ImGui.Button("Refresh designs"))
            _plugin.RunOnFramework(() => Controller.RefreshDesigns());
        ImGui.SameLine();
        if (ImGui.Button("Test dependency connectivity"))
            _plugin.RunOnFramework(Controller.TestConnectivity);
        ImGui.SameLine();
        if (ImGui.Button("Release Glamourer lock"))
            _plugin.RunOnFramework(() => Controller.ReleaseLock("released from the Actions tab"));

        ImGui.Spacing();
        ImGui.Separator();
        var armed = DateTime.Now < _resetArmedUntil;
        if (!armed)
        {
            if (ImGui.Button("Reset configuration..."))
                _resetArmedUntil = DateTime.Now.AddSeconds(5);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            var confirmed = ImGui.Button("Click again within 5 seconds to reset ALL settings");
            ImGui.PopStyleColor();
            if (confirmed)
            {
                _resetArmedUntil = DateTime.MinValue;
                _plugin.RunOnFramework(() =>
                {
                    Controller.ReleaseLock("configuration reset");
                    _plugin.Configuration.Settings = new AnywearSettings();
                    _plugin.SaveConfiguration();
                    Controller.RefreshDesigns();
                });
            }
        }

        Help("Resets every setting, rule and eligibility checkbox. Your Glamourer designs and Penumbra mods are not touched.");
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private void Save(Action change)
    {
        change();
        _plugin.SaveConfiguration();
    }

    private void SetAllEligible(bool eligible)
        => Save(() =>
        {
            foreach (var entry in Settings.Outfits)
            {
                if (!Controller.IsMissing(entry.DesignId))
                    entry.Eligible = eligible;
            }
        });

    private void Checkbox(string label, bool value, Action<bool> set, string? help = null)
    {
        var v = value;
        if (ImGui.Checkbox(label, ref v))
            Save(() => set(v));
        if (help is not null)
            Help(help);
    }

    private void IntInput(string label, int value, int step, Action<int> set)
    {
        var v = value;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt(label, ref v, step, step * 4))
            Save(() => set(v));
    }

    private void EnumCombo<T>(string label, T current, Action<T> set, Func<T, string> name, T? skip = null) where T : struct, Enum
    {
        if (!ImGui.BeginCombo(label, name(current)))
            return;

        foreach (var value in Enum.GetValues<T>())
        {
            if (skip is { } s && EqualityComparer<T>.Default.Equals(value, s))
                continue;
            if (ImGui.Selectable(name(value), EqualityComparer<T>.Default.Equals(value, current)))
                Save(() => set(value));
        }

        ImGui.EndCombo();
    }

    private bool DesignCombo(string label, Guid current, bool allowNone, out Guid selected)
    {
        selected = current;
        var changed = false;
        if (!ImGui.BeginCombo(label, current == Guid.Empty ? "(none)" : Controller.DesignName(current)))
            return false;

        if (allowNone && ImGui.Selectable("(none)", current == Guid.Empty))
        {
            selected = Guid.Empty;
            changed  = true;
        }

        foreach (var (id, name) in _plugin.Glamourer.Designs.OrderBy(d => d.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            if (ImGui.Selectable($"{name}##{id}", id == current))
            {
                selected = id;
                changed  = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    private static void StatusLine(string label, bool ok, string text)
    {
        ImGui.TextColored(ok ? Good : Bad, ok ? "OK " : "-- ");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void Help(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Muted, text);
        ImGui.PopTextWrapPos();
    }

    private static void Swap<T>(List<T> list, int a, int b)
        => (list[a], list[b]) = (list[b], list[a]);

    private static string ModeLabel(SelectionMode mode)
        => mode switch
        {
            SelectionMode.Random    => "A - Random",
            SelectionMode.Rotation  => "B - Sequential rotation",
            SelectionMode.ZoneRules => "C - Zone-specific rules",
            SelectionMode.Fixed     => "D - Fixed design",
            SelectionMode.PenumbraMods => "E - Random Penumbra mod outfit",
            _                       => mode.ToString(),
        };

    private static string KindLabel(RuleKind kind)
        => kind switch
        {
            RuleKind.ExactTerritory => "Exact territory",
            RuleKind.DutyType       => "Duty type",
            RuleKind.Category       => "Category",
            _                       => kind.ToString(),
        };
}
