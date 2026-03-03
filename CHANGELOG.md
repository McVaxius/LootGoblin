# Changelog

All notable changes to LootGoblin will be documented in this file.

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
