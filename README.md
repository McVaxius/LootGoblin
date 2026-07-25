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

Every `/lg` command also works with `/lootgoblin`. `/lg debug` toggles map diagnostics in the UI. `/lg fetchretainer` starts manual retainer map retrieval when XADB/XASlave support is available and a configured map exists on a retainer.

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
10. Dungeon handling either sends `/ads inside` and waits for ADS ownership, or falls back to Loot Goblin's legacy dungeon solver.
11. Completion runs finish commands, checks for remaining enabled inventory/saddlebag/retainer maps, optionally retrieves another map, and loops when auto-start is enabled.

When a character loads during an active **Moogle Treasure Trove** event, Loot Goblin shows one normal reminder toast. Event timing is checked dynamically against Eventy's public event feed, cached for six hours, and failures remain silent.

## Configuration

Map settings control enabled map types, per-map run counts, gatherable-map choices, saddlebag retrieval, retainer retrieval, and whether all known map types are shown.

Party settings control wait-for-party behavior, thief-map underwater waits, mounted-party checks, teleport delay, dismount waits, and optional required-party-count thresholds.

Dungeon settings control ADS handoff. When enabled and ADS is loaded, Loot Goblin sends `/ads inside` after a treasure dungeon is confirmed and waits for ADS to finish. If ADS is missing, Loot Goblin warns and can fall back to its legacy solver.

Return-when-done can send the selected Lifestream return only after no enabled inventory, loaded saddlebag, or retainer maps remain. Destinations are FC, personal house, or inn.

Command triggers run configured slash commands at landing/duty entry and at finish. Defaults include rotation/BossMod/FrenRider follow-control commands, but they are editable in settings.

Automation settings cover ADS repair threshold/mode, food selection and search, auto-discard through AutoRetainer `/ays discard`, chocobo companion summon/stance, combat automation, Krangle names, and diagnostics.

Diagnostics include the main debug log, map diagnostics, and an optional dedicated LootGoblin diagnostic log under the plugin config directory.

## Integrations

Required for normal map travel:

- **Lifestream**: required by plugin metadata and used for `/li` aetheryte travel.
- **vnavmesh**: required for route movement and checked through plugin metadata or vnavmesh IPC.

Optional:

- **ADS**: dungeon solver handoff, ADS loot UI, ADS repair, and BMR reflection settings.
- **XADB** and **XASlave**: retainer/saddlebag map retrieval support.
- **AutoRetainer**: auto-discard command support through `/ays discard`.
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

```bash
git clone https://github.com/McVaxius/LootGoblin.git
cd LootGoblin
dotnet restore
dotnet build -c Release
```

## License

AGPL-3.0-or-later
