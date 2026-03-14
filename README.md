# Loot Goblin

---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

---

A Dalamud plugin for FFXIV that automates treasure map hunting with party coordination.

## Features

- Automated treasure map detection and opening
- GlobeTrotter integration for map location detection
- Teleport to nearest aetheryte
- Party coordination (FrenRider-compliant)
- vnavmesh navigation to treasure location
- Chest interaction and looting

## Installation

### Custom Repository (Recommended)

1. Open Dalamud Settings in-game
2. Go to **Experimental** tab
3. Add this URL to **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/McVaxius/LootGoblin/master/repo.json
   ```
4. Save and open Plugin Installer
5. Search for **Loot Goblin** and install

### Manual Installation

See [HOW_TO_IMPORT_PLUGINS.md](HOW_TO_IMPORT_PLUGINS.md) for detailed instructions.

## Usage

- `/lootgoblin` or `/lg` - Open the main window
- `/lg config` - Open settings

## Dependencies

- **vnavmesh** - Required for navigation
- **GlobeTrotter** - Recommended for map location detection

## Building from Source

```bash
git clone https://github.com/McVaxius/LootGoblin.git
cd LootGoblin
dotnet restore
dotnet build -c Release
```

## License

AGPL-3.0-or-later
