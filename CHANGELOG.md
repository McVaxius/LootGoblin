# Changelog

All notable changes to LootGoblin will be documented in this file.

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
