# Anywear Numbra

A Dalamud plugin for Final Fantasy XIV that changes **your own character's equipment** to one of your saved Glamourer designs whenever you change zones, teleport, or enter a duty, using whatever Penumbra mods you already have enabled.

> **Equipment only.** Anywear Numbra never sends hair, face, skin, body, race/clan/gender, customize colors, visual effects or advanced dyes to Glamourer. Weapons, facewear and hat/visor visibility are opt-in (default off).

It only affects your local character. It does not change other players, server-side data, or automate gameplay.

## Features

- **Triggers:** zone change (including leaving an instance), duty entry (dungeon, trial, raid, alliance raid, other duties), loading screen within the same zone (teleport, instance change). Duplicate events are debounced; retries are bounded.
- **Selection modes:**
  - **A – Random:** never the same design twice in a row when two or more are eligible.
  - **B – Rotation:** follows the Outfits list order; position survives restarts.
  - **C – Zone rules:** exact territory > duty type > category > fallback; ties go to the rule higher in the list.
  - **D – Fixed:** the same design every time.
  - **E – Random Penumbra mod outfit:** picks a random mod that is enabled in your character's Penumbra collection and changes armor, and wears every armor piece it changes (one item per slot; dyes, weapons and facewear untouched). Untick mods or set a minimum piece count in the Outfits tab. Uses the same equipment-only path as designs.
- Designs are tracked by Glamourer GUID: renames keep settings, deleted designs show as *missing* and are skipped.
- Manual actions: apply now, reapply current, refresh designs, test connectivity, release lock, reset configuration.
- Glamourer Automation conflict detection (post-apply re-check).
- `/anywear`, `/anywear reapply`, `/anywear on`, `/anywear off`.

## Requirements

| Dependency | Required | Used for |
|---|---|---|
| Dalamud (API 15) | yes | plugin host |
| Glamourer (IPC API 1.x) | yes | reading designs/state, applying equipment |
| Penumbra (IPC API 5.x) | for Mode E | read-only: status, mod list, changed items, enabled state. Never modified. |

## Build

Windows, .NET 10 SDK, and Dalamud dev assemblies (installed by XIVLauncher to `%AppData%\XIVLauncher\addon\Hooks\dev\`, or set `DALAMUD_HOME`).

```powershell
dotnet build AnywearNumbra.slnx -c Release
dotnet test AnywearNumbra.Tests/AnywearNumbra.Tests.csproj -c Release
```

Output: `AnywearNumbra\bin\x64\Release\AnywearNumbra\`.

## Install

1. `/xlsettings` → Experimental → Dev Plugin Locations → add the folder containing `AnywearNumbra.dll`.
2. `/xlplugins` → Dev Tools → enable **Anywear Numbra**.
3. `/anywear` → Outfits → **Refresh designs**, tick the designs you want (new designs start un-ticked).

## How the equipment-only guarantee works (Application path B)

Glamourer's `ApplyFlag.Equipment` also covers weapons, facewear, crests and visibility toggles, so a scoped apply (Path A) is not possible. Instead, `GlamourerService.ApplyEquipmentScoped` — the only code that sends appearance — does:

1. `Glamourer.GetDesignJObject(guid)` and `Glamourer.GetState(objectIndex, key)`.
2. `EquipmentScopeFilter.Merge`: copy the **current** state, set every `Apply`/`ApplyStain`/`ApplyCrest` marker to false, then copy only permitted fields from the design and mark just those. `Materials`, `Identifier`, `Mods`, `Links` are dropped.
3. `Glamourer.ApplyState(merged, objectIndex, key, Once | Equipment)` (+`Customization` only when a visibility toggle is applied — customizations remain unmarked; +`Lock` only if enabled).

Excluded fields are never applied, not applied-then-restored. No `Identifier` means no design links and no mod associations. `Once` avoids Glamourer's reset path that would reset skin/hair colors. If the API changes, the plugin switches to Path C and disables application rather than falling back.

**Limits:** Glamourer always re-sets body type (the current value is passed, so no change) and syncs the hidden highlight color when highlights are off. Advanced dyes and mod associations are never applied. Other plugins (e.g. Customize+) are not covered.

## Locking

Off by default. When enabled, the state is locked with key `0x414E4D42` after applying and released after the configured duration, from the Actions tab, on reset, and on unload.

## In-game acceptance test

1. Make two Glamourer designs with different hair, skin, face, visual settings **and** different armor, accessories, weapons and facewear.
2. Tick both, choose Rotation, keep all scope options off.
3. Teleport (automatic) and use Reapply / Apply now (manual).
4. Expected: armor and accessories change; hair, skin, face and visuals don't; weapons, facewear and hat/visor stay unchanged.

## Troubleshooting

- **No eligible designs:** tick designs in Outfits.
- **Outfit flips back / conflict warning:** Glamourer Automation is applying its own design; disable that set or enable locking.
- **Path C in Status:** Glamourer's API changed; update the plugin.
- Logs: `/xllog`, filter `AnywearNumbra`.

## License

MIT. The icon is original artwork for this project.
