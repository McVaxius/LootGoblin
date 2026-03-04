# Changelog

All notable changes to LootGoblin will be documented in this file.

## [0.0.1.7] - 2026-03-03

### Fixed - Critical Hotfix
- **Map decipher** - Replaced `GameHelpers.UseItem` with `/item "Map Name"` command; treasure maps require text command, not `ActionManager.UseAction` API
- **Map sorting** - Added missing "Special Timeworn" maps to `TreasureMapData.KnownMaps` with correct MinLevel values:
  - Seemingly Special (ID 24794, Lvl 70, SB)
  - Ostensibly Special (ID 33328, Lvl 80, ShB)
  - Potentially Special (ID 39593, Lvl 90, EW)
  - Conceivably Special (ID 39918, Lvl 90, EW)
  - Timeworn Br'aaxskin duplicate (ID 43557, Lvl 100, DT)
  - Timeworn Gargantuaskin (ID 46185, Lvl 100, DT)
- **Lowest tier first** - Now correctly sorts by `TreasureMapData.MinLevel` ascending (was defaulting unknown maps to 999)

### Notes
- v0.0.1.6 had critical bug: `UseItem` spam with "not ready" because `ActionManager` doesn't support treasure map items
- Special Timeworn maps are dungeon portal guarantee variants (e.g., Seemingly Special → Lost Canals of Uznair 100%)

## [0.0.1.6] - 2026-03-03

### Added - Phase 6: Map Selection, Location Detection, Chest Interaction
- **Map checkboxes** - Each map in inventory now has a checkbox; only checked maps run (unchecked = all)
- **Lowest tier first** - Bot processes checked maps sorted lowest MinLevel → highest (preserves best maps)
- **GameHelpers.cs** - `UseItem(itemId)` via `ActionManager.UseAction(ActionType.Item, extraParam:65535)` (FrenRider pattern); `InteractWithObject()` via `TargetSystem.InteractWithObject`
- **MapFlagReader.cs** - Reads `AgentMap.FlagMapMarkers[0]` after decipher; uses `XFloat`/`YFloat` world coords + `TerritoryId`; `FlagMarkerCount > 0` check
- **ChestDetectionService.cs** - Scans ObjectTable for `EventObj` objects named "Treasure"/"Coffer"; tracks nearest coffer and distance
- **GlobeTrotterIPC.TryGetMapLocation()** - Delegates to MapFlagReader; reads live AgentMap flag
- **StateManager - OpeningMap** - Calls `GameHelpers.UseItem()` to decipher map; waits 4s for flag to set
- **StateManager - DetectingLocation** - Polls `TryGetMapLocation()` each tick; auto-detects if already in zone (skips teleport); finds nearest aetheryte
- **StateManager - OpeningChest** - Finds nearest coffer; navigates within range; calls `InteractWithObject`; transitions to InCombat
- **ITargetManager** - Added as Dalamud `[PluginService]` in Plugin.cs
- **Config: EnabledMapTypes** (`List<uint>`), **ChestInteractionRange** (float, 5y), **AutoLootChest** (bool), **ChestOpenTimeout** (int, 10s)
- **ConfigWindow** - New "Chest Interaction" section: Auto Loot, Interaction Range slider, Timeout slider

### Fixed
- **Krangle Names / Un-Krangle button** - Removed square brackets from both states

### Notes
- Map decipher triggers a dialog in-game; bot uses `UseItem` then waits 4s for the flag to propagate
- `FlagMapMarker.XFloat` = world X, `FlagMapMarker.YFloat` = world Z (FFXIV convention)
- InDungeon remains a stub (Phase 8)

## [0.0.1.5] - 2026-03-03

### Added - Phase 5: State Machine & Bot Logic
- **StateManager** - Framework.Update-driven state machine (0.5s tick) with full workflow
- **BotState machine** - States: Idle → SelectingMap → OpeningMap → DetectingLocation → Teleporting → Mounting → WaitingForParty → Flying → OpeningChest → InCombat → InDungeon → Completed → Idle
- **Bot Control UI** - New "Bot Control" section in MainWindow with Start/Stop/Pause buttons, state + detail display, retry counter, current map and zone info
- **State logging** - Configurable per-transition debug log entries
- **Auto-start next map** - Optional: automatically run next map from inventory on completion
- **Retry logic** - Configurable max retries (0-10) on error before stopping
- **Config settings** - AutoStartNextMap, MaxRetries, StopOnError, EnableStateLogging in Bot Automation section

### Notes
- OpeningMap, DetectingLocation, OpeningChest are stubs pending Phase 6 (GlobeTrotter IPC + chest interaction)
- InDungeon is a stub pending Phase 8

## [0.0.1.4] - 2026-03-03

### Fixed
- **[Un-Krangle] button text** - Was incorrectly labelled "Unkreangle"
- **Map tier for older maps** - Now searches for "grade X" broadly, catching "classified as grade X" (pre-DT format) so all map tiers display correctly

## [0.0.1.3] - 2026-03-03

### Added
- **[Krangle Names] toggle button** - Click to toggle name obfuscation on/off for all player names

### Fixed
- **Map tier/level extraction** - Now parses from item description ("risk-reward grade X", "Level Y") instead of hardcoded lookup
- **Map tier accuracy** - Br'aaxskin now correctly shows Tier 17, Level 100 (parsed from description)
- **Party error on area change** - "Local player not found" changed to "Loading..." during zone transitions (not an error)

## [0.0.1.2] - 2026-03-03

### Added
- **Version display** - Assembly version shown automatically at top of main window
- **KrangleService** - Deterministic player name obfuscation for screenshots (from FrenRider)
- **Map tier display** - Shows actual map tier number (1-16) and item level instead of Solo/Party

### Removed
- **FrenRiderIPC** - Removed entirely (FrenRider is for other party members, not this character)
- **Navigation buttons** - Removed Fly to Flag, Mount Up, Stop Nav (will be automated by state machine)
- **Party buttons** - Removed Check Party, Wait for Mounts, Stop FrenRider (not needed as manual controls)
- **Solo/Party map tags** - Replaced with actual tier numbers

### Fixed
- **Debug log spam** - Party status only logs when member count or mount status changes
- **Player names** - Krangled in status display and party member list

## [0.0.1.1] - 2026-03-03

### Added
- **Phase 4: Party Coordination**
- **PartyService** - Detects party members, checks mount status via Character* cast (FrenRider pattern)
- **FrenRiderIPC** - Detects FrenRider plugin, stop/pause/resume commands
- **Party Coordination UI** - Shows member count, mounted/ready status, per-member details
- **Check Party button** - Manual party status refresh
- **Wait for Mounts button** - Waits for all party members to mount (with timeout)
- **Stop FrenRider button** - Sends FrenRider stop command
- **Party config settings** - Wait for Party, Require All Mounted, Allow Pillion Riders, Party Wait Timeout

### Changed
- Dependencies section now also shows FrenRider detection status
- ConfigWindow expanded with Party Coordination settings section

## [0.0.1.0] - 2026-03-03

### Added
- **Phase 3: Navigation & Teleportation**
- **NavigationService** - Core navigation coordination (teleport, mount, fly, stop)
- **CommandHelper** - Shared utility for sending slash commands to game (from FrenRider pattern)
- **VNavIPC expanded** - FlyTo, MoveTo, Stop methods via `/vnav flyto`, `/vnav moveto`, `/vnavmesh stop`
- **Fly to Flag button** - Sends `/vnav flyflag` to fly to current map flag
- **Mount Up button** - Mount Roulette via game action
- **Stop Nav button** - Stops vnavmesh navigation
- **Navigation UI section** - Shows state, mounted/flying/combat indicators, nav controls
- **Navigation settings** - Auto Teleport, Require vnavmesh, Nav Timeout slider in config
- **Map tier display** - Shows [Party] or [Solo] tag from item description (cyan/yellow)
- **FindNearestAetheryte** - Searches unlocked aetherytes by territory for cheapest teleport
- **TeleportToAetheryte** - Teleports via `/tp` command with safety checks (combat, between areas)
- **Condition checks** - IsMounted, IsFlying, IsInCombat, IsTeleporting helpers

### Changed
- Bot State in status bar now reflects actual NavigationState (color-coded)
- ConfigWindow expanded with Navigation settings section

## [0.0.0.9] - 2026-03-03

### Fixed
- **RSR detection** - Corrected InternalName from "RotationSolverReborn" to "RotationSolver" (RSR now detects properly)

## [0.0.0.8] - 2026-03-03

### Fixed
- **Map inventory scanner** - Now uses name pattern matching ("Timeworn" + "Map") instead of hardcoded ItemId list
- **Emergent map discovery** - Scanner will detect new maps added in future patches automatically
- **IPC plugin detection** - Added debug logging to diagnose InternalName mismatches
- **Lumina API compatibility** - Fixed Item sheet access (removed incorrect .Value/.HasValue usage)
- **Refresh Dependencies button** - Now properly rechecks all plugin availability

### Changed
- InventoryService now iterates through inventory containers and checks item names from game data
- All IPC checks now use case-insensitive string comparison
- Debug mode shows full list of installed plugin InternalNames for troubleshooting

## [0.0.0.6] - 2026-03-03

### Added
- **Treasure Map Inventory Scanner** - Detects all known treasure maps in player inventory
- **Map data model** - 16 known map types across ARR, HW, SB, ShB, EW, DT expansions
- **Map display** - Color-coded by tier (Solo/Party), shows expansion, dungeon indicator
- **Refresh Maps button** - Manual inventory rescan with 2-second cooldown
- **InventoryService** - Uses InventoryManager to scan for treasure maps (unsafe, pattern from ChocoboColourized)
- **MapDetectionService** - Detects when AreaMap addon is open
- **GlobeTrotterIPC** - Checks if GlobeTrotter plugin is installed and loaded
- **VNavIPC** - Checks if vnavmesh plugin is installed and loaded
- **RotationPluginIPC** - Checks RSR, BMR, VBM, Wrath availability
- **BMR marked with [Map AI]** tag - Has treasure map dungeon modules
- **Live dependency status** - Green/Red/Yellow status for all plugins
- **Refresh Dependencies button** - Manual recheck of all plugin dependencies
- **MapLocation model** - Territory, coordinates, nearest aetheryte
- **BotState enum** - Full state machine: Idle through Completed/Error
- **DrawPluginStatus helper** - Reusable status display for required vs optional plugins

### Changed
- MainWindow now has collapsible Treasure Maps, Dependencies, Commands sections
- Dependencies section shows real plugin availability instead of placeholders

## [0.0.0.4] - 2026-03-03

### Added
- **Enable/Disable toggle** - Bot can be enabled/disabled via UI button or `/lg on` / `/lg off`
- **Command arguments** - `/lg config`, `/lg on`, `/lg off`, `/lg status`
- **Status display** - Shows enabled state, bot state, login status, character name, party count
- **Dependency section** - Placeholder for vnavmesh and GlobeTrotter status checks
- **Commands section** - Collapsible reference for all slash commands
- **Debug log panel** - Scrollable debug log visible when Debug Mode is enabled
- **Chat output** - `[LootGoblin]` prefixed messages to game chat
- **Configuration: Enabled** - Master enable/disable for bot automation
- **Configuration: ShowMainWindow** - Option to show main window on login
- **Enhanced ConfigWindow** - Resizable, organized into sections (Bot, UI Settings)
- **Internal debug log** - Capped at 200 lines with timestamps

### Changed
- MainWindow now 420x400 minimum with multiple sections
- ConfigWindow now resizable (350x220 default) instead of fixed size
- Command help message updated to show available arguments

## [0.0.0.1] - 2026-03-03

### Added
- Initial project structure (Phase 0)
- Basic plugin skeleton with Dalamud services
- Main window with status display
- Settings window with debug mode toggle
- Slash commands: `/lootgoblin` and `/lg`
- Plugin manifest (LootGoblin.json)
- Dalamud repo manifest (repo.json)
- GitHub Actions auto-release workflow (build-release.yml)
- CopyPluginArtifacts target in .csproj (proven pattern)
- Project gameplan (PROJECT_GAMEPLAN.md)
- Plugin installation guide (HOW_TO_IMPORT_PLUGINS.md)
- Dummy icon.png placeholder
- .gitignore (excludes backups/ and learning/)

### Notes
- This is the foundation build. No automation features yet.
- Plugin loads, shows UI, saves configuration.
- Ready for Phase 1 feature implementation.
