# Loot Goblin

Loot Goblin is a Dalamud plugin for automating FFXIV treasure-map runs. It selects configured maps, deciphers them, reads the local map flag, travels with Lifestream, moves with vnavmesh, opens coffers, handles portals, and can hand treasure dungeons to ADS.

## Install

Add the Aethertek custom repository in Dalamud:

```text
https://aethertek.io/x.json
```

Then open the plugin installer and install **Loot Goblin**.

Support development: https://ko-fi.com/mcvaxius  
Plugins and guides: https://aethertek.io/

## Quick Start

Open the main window:

```text
/lootgoblin
/lg
```

Common commands:

```text
/lg config
/lg settings
/lg on
/lg enable
/lg off
/lg disable
/lg status
/lg debug
/lg fetchretainer
```

Every `/lg` command also works with `/lootgoblin`. `/lg debug` toggles map diagnostics in the UI. `/lg fetchretainer` starts manual retainer map retrieval when XADB is available and a configured map exists on a retainer.

## Interface

**Transparency** applies to the complete plugin window, including its titlebar and popups. Settings provides normal opacity, automatic focus fade, faded opacity and delay; defaults are 100%, fading to 50% after 10 seconds without focus and restoring on focus. Compact and language controls can be hidden independently on Main while remaining available in Settings.

Main's titlebar opens Settings or Alexandrite and provides Start, Pause/Resume and Stop with the same runtime readiness checks as the body controls. The packaged icon appears in Main branding and its titlebar, including when collapsed. Combat command triggers remain editable in Settings and run at their configured landing, duty-entry and finish points.

The main window groups controls, a live Bot State/Party/Food summary, Map Queue, Current Run, Party Status, Navigation and Commands into panels. Current Run and Party Status appear side by side when the window is wide enough. Existing section disclosure controls keep their state, and map diagnostics remain available through the existing debug settings.

Use **C** in the header for compact mode. It reduces padding, gaps, action heights and title size across the main, Settings and Alexandrite windows. The header colour swatch offers teal, blue, pink and custom RGB colours; the language selector offers English, German, French, Spanish, Italian, Russian, Japanese, Korean, Simplified Chinese, Vietnamese, Brazilian Portuguese, Indonesian, Polish, Turkish and Hindi. The same appearance controls are in Settings → Interface. These choices save through the existing plugin configuration and apply to all three windows. Status colours retain their meaning when the decorative theme changes.

Settings search highlights tabs matching every search word, including conditional settings. Search accepts translated labels and the original English aliases. Clear or closing Settings resets it. Plugin names, game catalogue names, commands and diagnostic logs retain their original text; UI numbers use the selected language's formatting.

Loot Goblin uses managed host fonts for the selected language and the native language names. A loading or error window appears when the required fonts are unavailable; details are written to the plugin log. Game font and screenshot acceptance remain pending.

Hindi is enabled only when the local font check passes. Otherwise the selector shows disabled **Hindi (unavailable)** while other languages remain usable. A saved Hindi choice that fails its required-font check shows an English status and **Use English**; that button explicitly saves English. Font failures never change the saved language automatically.

## Core Workflow

1. Enable Loot Goblin from the main window or with `/lg on`.
2. Preflight clears stale map flags, attempts to dismount, refreshes dependencies, and prepares configured job/repair state.
3. Map selection scans inventory first, then loaded saddlebags, then retainer data through XADB. Enabled map types and per-map run counts control what is eligible.
4. Opening a map uses the game item API, accepts the decipher confirmation dialog, and waits for a map flag or captured treasure spot.
5. Location detection uses the local AgentMap flag reader, captured TreasureSpot data, and the bundled/community map-location database.
6. Travel uses Lifestream `/li` commands to the nearest known aetheryte, then vnavmesh `/vnav flyto` or `/vnav moveto` to reach the map area.
7. Party waits can delay teleporting, mounting, underwater descent, or dismounting until party members are ready or nearby.
8. Chest handling targets and opens overworld treasure coffers, handles combat waits, loots, and solves or skips the Higher/Lower minigame based on config.
9. Portal handling approaches the portal, clears old map flags, accepts the portal dialog, and waits for duty state or a treasure dungeon territory.
10. Dungeon handling either starts an acknowledged ADS sweep-without-exit handoff, or falls back to Loot Goblin's legacy dungeon solver.
11. Completion runs finish commands, checks for remaining enabled inventory/saddlebag/retainer maps, optionally retrieves another map, and loops when auto-start is enabled.

When a character loads during an active **Moogle Treasure Trove** event, Loot Goblin shows one normal reminder toast. Event timing is checked dynamically against Eventy's public event feed, cached for six hours, and failures remain silent.

## Configuration

Use **Search settings** above the nine settings tabs to highlight matching tabs, including settings currently hidden behind another option. Search ignores case and requires every word to match within a tab's terms: try `discard`, `inventory space`, `leave duty`, or `repair`. Select a highlighted tab to view its controls; **Clear** or closing settings resets the search. Brief guidance in each tab explains setup requirements and labels factory defaults separately from your current selections.

**Map Queue** in the main window controls enabled map types, per-map run counts, gatherable-map choices, and whether all known map types are shown. The **Maps** settings tab controls saddlebag/retainer retrieval and location updates.

Marketboard settings control optional Emptor purchasing after inventory, saddlebag, retainer, and gathering sources are exhausted. Limsa Lominsa is the default for new configurations, and existing blank/Ul'dah defaults migrate to Limsa. Emptor API v4 or newer supplies the available city list dynamically; when that IPC is missing or invalid, Loot Goblin retains its built-in choices. Selecting Ul'dah stores the existing blank compatibility key, while any nonblank city requires Emptor v4+. Existing nonblank city choices are preserved. If Emptor is missing, the Marketboard tab shows its repository URL and buttons for `/xlsettings` and `/xlplugins`.

With Emptor v5+, Loot Goblin requests one session-only batch of NQ minimum-listing hints for all known marketable maps after load, deferring it to first login when needed. The lookup scope follows the travel settings: current world, current data center, current region, or reachable regions plus Materia. Hover a map's gil ceiling to see its hint, source world/location, age, scope, and staleness warning; **Use** explicitly copies a positive hint into that ceiling without enabling the cart. Prices are never cached to disk or written into ceilings automatically. A global manual refresh is available after a visible five-minute cooldown, and changing travel settings does not itself refresh prices.

Each Emptor order requests exactly one map at the configured per-map gil cap. A trip is capped by that map's remaining run count: one run leaves one map in inventory; two runs use saddlebag + inventory when saddlebag retrieval is enabled, or deciphered key item + inventory when it is disabled; three or unlimited runs use saddlebag + deciphered key item + inventory. With saddlebag retrieval disabled, a trip can prepare at most two. Because every map has its own cap, one trip can spend the configured cap up to three times. If an extra purchase, guarded saddlebag move, or decipher step fails after a map is secured, Loot Goblin returns and restores the party, warns, and continues with the secured stock.

Party settings control wait-for-party behavior, thief-map underwater waits, mounted-party checks, teleport delay, dismount waits, and optional required-party-count thresholds.

Dungeon settings control ADS handoff. When enabled and ADS is loaded, Loot Goblin starts the confirmed treasure duty through ADS's existing operator API and selects a final-coffer sweep without exit. It keeps ADS running at duty completion and waits for a positive matching sweep result before applying the existing manual, delay or party-leave exit setting. The original delay runs from duty completion; sweeping does not restart it. Missing, unsupported, cancelled or unreadable sweep completion visibly holds automatic exit. Explicit manual ADS Stop/Leave remains available. The result confirms ADS's sweep checks, not inventory receipt. If ADS is missing before handoff, Loot Goblin warns and can fall back to its legacy solver.

Return-when-done can send the selected Lifestream return only after no enabled inventory, loaded saddlebag, or retainer maps remain. Destinations are FC, personal house, or inn.

Command triggers run configured slash commands at landing/duty entry and at finish. Defaults include rotation/BossMod/FrenRider follow-control commands, but they are editable in settings.

Loot Goblin bundles FrenRider's six BossMod presets in `data/bm` and refreshes those named definitions when a map run starts with BMR loaded. Before `/bmrai on`, it confirms both BMR's saved AI preset and its active preset use `passive - tank`, `passive - melee` or `passive - ranged` for the current job. The passive presets supply movement while the existing commands enable the combat rotation. Refreshing replaces custom edits under those six preset names. Keep the `data/bm` folder with the DLL when installing manually. Unavailable or rejected preset setup warns and withholds BMR activation; having both BossMod providers loaded prevents preset writes.

Automation settings cover ADS repair threshold/mode, food selection and search, auto-discard through AutoRetainer `/ays discard`, chocobo companion summon/stance, combat automation, Krangle names, and diagnostics.

Diagnostics include the main debug log, map diagnostics, and an optional dedicated LootGoblin diagnostic log under the plugin config directory.

## Integrations

Required for normal map travel:

- **Lifestream**: required by plugin metadata and used for `/li` aetheryte travel.
- **vnavmesh**: required for route movement and checked through plugin metadata or vnavmesh IPC.

Optional:

- **Emptor**: optional missing-map market purchases. Add `https://raw.githubusercontent.com/Evernow/DalamudPlugins/main/pluginmaster.json` in Dalamud Settings, then install Emptor from `/xlplugins`. Dynamic city selection and the default Limsa route require API v4+; session price hints require v5+. Blank Ul'dah remains the v1-v3 purchase compatibility choice.
- **ADS**: dungeon solver handoff, ADS loot UI, ADS repair, and BMR reflection settings.
- **XADB**: retainer map lookup. Loot Goblin handles retainer and saddlebag retrieval through the game UI.
- **AutoRetainer**: auto-discard command support through `/ays discard`; requires a configured discard list. Auto Discard is off by factory default. When selected, it runs while Loot Goblin is enabled, during safe mounted windows.
- **RotationSolver Reborn, BossMod Reborn, VBM, Wrath**: command-trigger and combat automation support. BossMod Reborn is detected as the map-AI-capable option.
- **TextAdvance**: optional Alexandrite dialogue support.
- **GatherBuddyReborn**: optional map gathering support.
- **MapPartyAssist**: optional treasure-map statistics display.

## Death Return And Party State

FFXIV treats death Return differently depending on context. A normal overworld Return should not leave a party by itself.

If the character is dead while `BoundByDuty`, `BoundByDuty56`, or treasure-map duty state is still active, accepting the Return prompt can remove the character from the party without a separate "Leave party?" confirmation. This can happen from normal game behavior; a plugin or external tool only needs to accept the Return prompt for the game-side party drop to occur.

When investigating unexpected party drops, treat death Return from a bound treasure-map context as different from ordinary overworld Return.

## Troubleshooting

Use the Dalamud log first. If enabled, also check the dedicated LootGoblin diagnostic log from the settings window.

Useful search strings:

```text
Return to
BoundByDuty
You leave ... party
Unconscious
defeated
[SelectYesno] accepted
[YES/NO] Clicked Yes
[OpeningMap] Clicked Yes
[Portal] Clicked Yes
[ADS]
[RetainerMap]
```

For party-drop investigations, line up the death/respawn line, `Return to` prompt, `BoundByDuty` state, LootGoblin dialog acceptance line, and the system party-leave message.

## Build

Local builds require installed SDK **10.0.201**, the Dalamud API 15 distribution, and an authorized AethertekUI checkout beside LootGoblin. Its project reference resolves to `../aethertekUI` relative to the repository root. A consumer-only checkout cannot resolve that reference. The existing workspace launcher enters AethertekUI's tool environment and builds the plugin project directly.

For a compilation-only Debug/x64 check from the sibling workspace:

```powershell
. .\aethertekUI\eng\Enter-RepoEnv.ps1
Set-Location .\aethertekUI
dotnet restore ..\LootGoblin\LootGoblin\LootGoblin.csproj -p:Configuration=Debug -p:Platform=x64
dotnet build ..\LootGoblin\LootGoblin\LootGoblin.csproj --no-restore -c Debug -p:Platform=x64 -p:Use_DalamudPackager=false -p:ImportNuGetBuildTargets=false
```

Stop if restore fails. The final flag skips NuGet build targets for this compilation check; release packaging uses normal imports. Distributions include `AethertekUI.dll` beside `LootGoblin.dll` and exclude host-owned Dalamud and ImGui assemblies.

## License

AGPL-3.0-or-later

## Support logs

Use **Copy / ZIP Dalamud log** in Settings > Advanced to create a local ZIP and open its folder. At 100 MiB or above, the first click warns that logging may have stopped and recent activity may be missing; click **Export capped log anyway** only if you still want that snapshot. Share the ZIP manually and remove exports when no longer needed. **Open Export Folder** reopens the completed export’s folder.

When XA Slave is loaded, **Open XA Slave log tools** opens its **Utility > XA Mods** panel, which contains Dalamud Log Cleaner. The existing **Copy / ZIP Dalamud log** action remains separate. Opening the panel does not run cleanup or change XA Slave settings.

Compact mode defaults on. The main Compact and Transparency controls start hidden; Appearance settings keeps density, transparency and independent main-control visibility choices. The one-time migration preserves opacity and unrelated preferences, and later loads retain your choices.
