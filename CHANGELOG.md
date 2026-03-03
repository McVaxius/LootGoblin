# Changelog

All notable changes to LootGoblin will be documented in this file.

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
