# LootGoblin - FFXIV Treasure Map Automation Plugin
## Project Gameplan & Implementation Strategy

---

## 📋 Project Overview

**Plugin Name:** LootGoblin  
**Project Path:** `d:\temp\LootGoblin`  
**Repository:** https://github.com/McVaxius/LootGoblin (to be created)  
**Purpose:** Automated treasure map hunting bot for FFXIV with party coordination  
**Target Framework:** .NET 10.0-windows  
**Dalamud API Level:** 14  

---

## 🎯 High-Level Concept

LootGoblin automates the treasure map hunting process by:
1. Opening treasure maps from inventory
2. Detecting map location using GlobeTrotter integration
3. Teleporting to nearest aetheryte
4. Coordinating with party members (FrenRider-compliant)
5. Flying to treasure location and opening chest
6. Completing Activities within the Treasure Maps (HighCombat, /Low, click orb, pick door, etc)

---

## 🛠️ Technology Stack & Dependencies

### Core Technologies
- **.NET 10.0** - Target framework
- **C# 13** - Programming language
- **Dalamud.NET.Sdk/14.0.2** - Plugin SDK
- **ImGui.NET** - UI framework (via Dalamud)

### Required Dalamud Services
- `IClientState` - Player/game state
- `ICommandManager` - Slash commands
- `IChatGui` - Chat output
- `IDataManager` - Game data access
- `IObjectTable` - Game objects
- `IPartyList` - Party information
- `ICondition` - Player conditions (mounted, in combat, etc.)
- `IGameGui` - Game UI interaction
- `IPluginLog` - Logging

### External Plugin Dependencies (IPC)
- **GlobeTrotter** - Map location detection
  - IPC: Get map coordinates and zone information
  - Fallback: Manual coordinate input if not available
- **vnavmesh** - Navigation and pathfinding
  - IPC: `vnav.Nav.PathfindAndMoveTo` for flying
  - IPC: `vnav.Nav.FlyFlag` for flag-based navigation
- **FrenRider** (Optional) - Party coordination
  - Ensures compatibility with multi-boxing setups
  - Party mount/pillion detection

### Future Dependencies (Later Phases)
- **AutoDuty** - Repair logic reference
- **AutoDiscard** - Inventory management via slash commands

---

## 📁 Project Structure

```
d:\temp\LootGoblin\
├── .git/                           # Git repository
├── .github/
│   └── workflows/
│       └── build-release.yml       # Auto-release workflow (from memory)
├── .gitignore                      # Ignore backups, learning docs, etc.
├── LootGoblin/                     # Main plugin project
│   ├── Configuration.cs            # Plugin settings
│   ├── Plugin.cs                   # Main plugin class
│   ├── LootGoblin.csproj          # Project file with CopyPluginArtifacts
│   ├── LootGoblin.json            # Plugin manifest
│   ├── images/
│   │   └── icon.png               # Plugin icon (user-provided)
│   ├── Models/
│   │   ├── TreasureMap.cs         # Map data model
│   │   ├── MapLocation.cs         # Location data
│   │   └── BotState.cs            # State machine states
│   ├── Services/
│   │   ├── MapDetectionService.cs # Map scanning & GlobeTrotter IPC
│   │   ├── NavigationService.cs   # vnavmesh IPC & teleport logic
│   │   ├── PartyService.cs        # Party coordination & mount checks
│   │   ├── InventoryService.cs    # Map inventory management
│   │   └── StateManager.cs        # Bot state machine
│   ├── Windows/
│   │   ├── MainWindow.cs          # Main control UI
│   │   └── ConfigWindow.cs        # Settings window
│   └── IPC/
│       ├── GlobeTrotterIPC.cs     # GlobeTrotter integration
│       └── VNavIPC.cs              # vnavmesh integration
├── LootGoblin.sln                 # Solution file
├── README.md                       # User documentation
├── CHANGELOG.md                    # Version history & changes
├── PROJECT_GAMEPLAN.md            # This file
├── HOW_TO_IMPORT_PLUGINS.md       # Plugin installation guide
├── repo.json                       # Dalamud repo manifest
├── backups/                        # Timestamped backups (gitignored)
└── learning/                       # Development notes (gitignored)
```

---

## 🔄 Development Workflow

### Before Every Code Change
1. **Create timestamped backup** in `backups/` folder
   - Format: `YYYYMMDD_HHMMSS_filename.ext`
2. **Update CHANGELOG.md** with implementation details
3. **Syntax validation** before committing
4. **Memory leak checks** (dispose patterns, event unsubscription)
5. **Release verification** (build succeeds, no zip-within-zip issues)

### After Every Update
1. **Document testing steps** for user verification
2. **Commit to git** with descriptive message
3. **Push to GitHub** (triggers auto-release if on master)
4. **Wait for user confirmation** before next phase

---

## 📊 Implementation Phases

### **Phase 0: Project Initialization** ✅
**Goal:** Set up project structure and GitHub repository

**Tasks:**
- [ ] Create project gameplan (this file)
- [ ] Create `HOW_TO_IMPORT_PLUGINS.md`
- [ ] Initialize git repository
- [ ] Create GitHub repository (public)
- [ ] Set up `.gitignore` (exclude backups/, learning/)
- [ ] Create basic project structure from SamplePlugin
- [ ] Add dummy `icon.png` to `images/` folder
- [ ] Configure `.csproj` with `CopyPluginArtifacts` target
- [ ] Set up `build-release.yml` workflow (no zip-within-zip!)
- [ ] Create `repo.json` for Dalamud

**Testing:**
- Verify project builds locally: `dotnet build -c Release`
- Verify `latest.zip` contains correct files (not nested)
- Verify GitHub Actions workflow is configured correctly

---

### **Phase 1: Basic Plugin Skeleton** 🔨
**Goal:** Get plugin loading in-game with basic UI

**Tasks:**
- [ ] Implement `Plugin.cs` with Dalamud services
- [ ] Implement `Configuration.cs` with basic settings
- [ ] Create `MainWindow.cs` with simple UI
- [ ] Create `ConfigWindow.cs` for settings
- [ ] Add slash commands: `/lootgoblin`, `/lg`
- [ ] Implement plugin enable/disable logic
- [ ] Add basic logging and error handling
- [ ] Create initial `CHANGELOG.md` entry

**Configuration Settings (Phase 1):**
```csharp
public bool Enabled { get; set; } = false;
public bool ShowMainWindow { get; set; } = true;
public bool DebugMode { get; set; } = false;
```

**UI Elements (Phase 1):**
- Main window with "Start Bot" button (disabled)
- Status display: "Plugin loaded successfully"
- Settings window with enable/disable toggle
- Debug log output area

**Testing:**
- [ ] Plugin appears in Dalamud plugin list
- [ ] `/lootgoblin` command opens main window
- [ ] Settings window opens and saves configuration
- [ ] Plugin can be enabled/disabled without errors
- [ ] No errors in Dalamud log

---

### **Phase 2: Map Detection & Inventory** 🗺️
**Goal:** Detect treasure maps in inventory and parse map data

**Tasks:**
- [ ] Implement `InventoryService.cs`
  - Scan player inventory for treasure maps
  - Identify map type (Timeworn, Gazelleskin, etc.)
  - Track map quantities
- [ ] Implement `TreasureMap.cs` model
  - Map item ID
  - Map type/tier
  - Location data (if opened)
  - Status (unopened, opened, completed)
- [ ] Implement `MapDetectionService.cs`
  - Detect when map is opened
  - Parse map flag location from game data
- [ ] Implement `GlobeTrotterIPC.cs`
  - Check if GlobeTrotter is installed
  - Request map location data via IPC
  - Fallback to manual detection if unavailable
- [ ] Add UI for map selection
  - List available maps in inventory
  - Multi-select for batch processing
  - Display map type and quantity

**Configuration Settings (Phase 2):**
```csharp
public List<uint> EnabledMapTypes { get; set; } = new();
public bool AutoOpenMaps { get; set; } = false;
public bool RequireGlobeTrotter { get; set; } = true;
```

**Data Collection:**
- Map item IDs from game data
- Map type classifications
- Zone/territory IDs for each map
- Aetheryte locations per zone

**Testing:**
- [ ] Plugin detects maps in inventory
- [ ] Map list displays correctly in UI
- [ ] Map selection works (single and multi-select)
- [ ] GlobeTrotter IPC connection status shown
- [ ] Map opening detection works
- [ ] Location data retrieved correctly

---

### **Phase 3: Navigation & Teleportation** 🧭
**Goal:** Implement teleport and navigation to map locations

**Tasks:**
- [ ] Implement `NavigationService.cs`
  - Find nearest aetheryte to map location
  - Execute teleport command
  - Verify teleport success
- [ ] Implement `VNavIPC.cs`
  - Check if vnavmesh is installed and running
  - Execute `vnav.Nav.FlyFlag` to map location
  - Monitor navigation status
  - Handle navigation failures
- [ ] Implement `MapLocation.cs` model
  - Territory/zone ID
  - X, Y, Z coordinates
  - Nearest aetheryte ID
  - Distance calculations
- [ ] Add teleport logic
  - Check if player can teleport (not in combat, etc.)
  - Calculate teleport cost
  - Execute teleport via game command
  - Wait for teleport completion
- [ ] Add navigation safety checks
  - Verify player is in correct zone
  - Check for obstacles
  - Handle navigation interruptions

**Configuration Settings (Phase 3):**
```csharp
public bool AutoTeleport { get; set; } = true;
public bool RequireVNav { get; set; } = true;
public int MaxTeleportCost { get; set; } = 999;
public float NavigationTimeout { get; set; } = 300f; // seconds
```

**Data Collection:**
- Aetheryte IDs and locations
- Zone territory IDs
- Teleport command syntax
- vnavmesh IPC method signatures

**Testing:**
- [ ] Nearest aetheryte calculation is correct
- [ ] Teleport executes successfully
- [ ] Player arrives at correct location
- [ ] vnavmesh IPC connection works
- [ ] Navigation to flag location works
- [ ] Navigation handles obstacles
- [ ] Timeout handling works correctly

---

### **Phase 4: Party Coordination** 👥
**Goal:** FrenRider-compliant party coordination and mount checks

**Tasks:**
- [ ] Implement `PartyService.cs`
  - Detect party members
  - Check mount status of all members
  - Check pillion riding status
  - Verify all members ready before flying
- [ ] Implement mount detection
  - Check if player is mounted
  - Check mount type (flying capable)
  - Detect pillion riders
- [ ] Add party coordination logic
  - Wait for all party members to mount
  - Verify all members in same zone
  - Handle party member disconnects
  - Handle party member deaths
- [ ] Add FrenRider integration (optional)
  - Check if FrenRider is active
  - Coordinate with FrenRider follow logic
  - Respect FrenRider party formation

**Configuration Settings (Phase 4):**
```csharp
public bool WaitForParty { get; set; } = true;
public bool RequireAllMounted { get; set; } = true;
public bool AllowPillionRiders { get; set; } = true;
public int PartyWaitTimeout { get; set; } = 60; // seconds
```

**Data Collection:**
- Party member mount IDs
- Pillion riding condition flags
- FrenRider IPC methods (if available)

**Testing:**
- [ ] Party member detection works
- [ ] Mount status detection accurate
- [ ] Pillion rider detection works
- [ ] Bot waits for all members to mount
- [ ] Bot handles party member issues gracefully
- [ ] FrenRider integration works (if installed)

---

### **Phase 5: State Machine & Bot Logic** 🤖
**Goal:** Implement core bot automation loop

**Tasks:**
- [ ] Implement `StateManager.cs`
  - State machine for bot workflow
  - State transitions and validation
  - Error recovery and retry logic
- [ ] Implement `BotState.cs` enum
  - States: Idle, SelectingMap, OpeningMap, Teleporting, Mounting, WaitingForParty, Flying, OpeningChest, InCombat, InDungeon, Completed, Error
- [ ] Implement bot workflow
  1. **Idle** → Select map from inventory
  2. **SelectingMap** → Open selected map
  3. **OpeningMap** → Get location from GlobeTrotter
  4. **Teleporting** → Teleport to nearest aetheryte
  5. **Mounting** → Mount up on flying mount
  6. **WaitingForParty** → Wait for all party members mounted
  7. **Flying** → Navigate to map location via vnavmesh
  8. **OpeningChest** → Interact with treasure coffer
  9. **InCombat** → Handle combat (future phase)
  10. **InDungeon** → Handle dungeon (future phase)
  11. **Completed** → Return to Idle or next map
  12. **Error** → Log error and retry or stop
- [ ] Add state transition logging
- [ ] Add error recovery mechanisms
- [ ] Add manual override controls

**Configuration Settings (Phase 5):**
```csharp
public bool AutoStartNextMap { get; set; } = false;
public int MaxRetries { get; set; } = 3;
public bool StopOnError { get; set; } = true;
public bool EnableStateLogging { get; set; } = true;
```

**Testing:**
- [ ] State machine transitions correctly
- [ ] Each state executes expected actions
- [ ] Error states handled gracefully
- [ ] Retry logic works correctly
- [ ] Manual stop/pause works
- [ ] Full workflow completes successfully

---

### **Phase 6: Chest Interaction** 📦
**Goal:** Detect and open treasure coffers

**Tasks:**
- [ ] Implement chest detection
  - Scan for treasure coffer objects
  - Verify correct coffer for current map
  - Calculate distance to coffer
- [ ] Implement chest interaction
  - Target coffer object
  - Execute interaction command
  - Verify chest opened successfully
  - Handle chest opening failures
- [ ] Add proximity checks
  - Verify player is close enough to interact
  - Move closer if needed
  - Handle obstacles blocking interaction

**Configuration Settings (Phase 6):**
```csharp
public float ChestInteractionRange { get; set; } = 5f;
public bool AutoLootChest { get; set; } = true;
public int ChestOpenTimeout { get; set; } = 10; // seconds
```

**Data Collection:**
- Treasure coffer object IDs
- Interaction range limits
- Chest opening command syntax

**Testing:**
- [ ] Chest detection works at various distances
- [ ] Chest interaction executes correctly
- [ ] Chest opening verified
- [ ] Timeout handling works
- [ ] Obstacle handling works

---

### **Phase 7: Combat Handling (Future)** ⚔️
**Goal:** Handle monsters guarding treasure

**Tasks:**
- [ ] Detect combat initiation
- [ ] Integrate with combat rotation plugins (RSR, WRATH, BMR, VBM)
- [ ] Monitor combat status
- [ ] Handle combat completion
- [ ] Handle party wipes
- [ ] Implement retreat logic

**Configuration Settings (Phase 7):**
```csharp
public bool AutoCombat { get; set; } = false;
public string CombatPlugin { get; set; } = "RSR";
public bool RetreatOnLowHP { get; set; } = true;
public int RetreatHPThreshold { get; set; } = 30;
```

**Note:** This phase requires extensive testing and may need combat plugin IPC integration.

---

### **Phase 8: Dungeon Handling (Future)** 🏰
**Goal:** Navigate and complete treasure map dungeons

**Tasks:**
- [ ] Detect dungeon entry
- [ ] Implement dungeon navigation logic
- [ ] Handle dungeon combat encounters
- [ ] Detect dungeon completion
- [ ] Handle loot distribution
- [ ] Exit dungeon logic

**Configuration Settings (Phase 8):**
```csharp
public bool AutoDungeon { get; set; } = false;
public bool WaitForPartyInDungeon { get; set; } = true;
public int DungeonTimeout { get; set; } = 1800; // seconds
```

**Note:** This is the most complex phase and may require multiple sub-phases.

---

### **Phase 9: Inventory Management (Future)** 🎒
**Goal:** Manage inventory during map runs

**Tasks:**
- [ ] Implement inventory space checks
- [ ] Integrate with AutoDiscard plugin
- [ ] Implement auto-repair logic (reference AutoDuty)
- [ ] Handle full inventory scenarios
- [ ] Implement loot filtering

**Configuration Settings (Phase 9):**
```csharp
public bool AutoDiscard { get; set; } = false;
public bool AutoRepair { get; set; } = false;
public int MinInventorySlots { get; set; } = 5;
public bool StopOnFullInventory { get; set; } = true;
```

---

### **Phase 10: Advanced Features (Future)** 🚀
**Goal:** Quality of life improvements and optimization

**Tasks:**
- [ ] Multi-map batch processing
- [ ] Map priority/ordering system
- [ ] Statistics tracking (maps completed, loot value, etc.)
- [ ] Performance optimization
- [ ] Advanced error recovery
- [ ] Party leader coordination
- [ ] Custom waypoint support

---

## 🔍 Data Collection Requirements

### Game Data to Collect
1. **Treasure Map Item IDs**
   - All map types and tiers
   - Map item categories
   - Map level requirements

2. **Zone/Territory Data**
   - Territory IDs for all map locations
   - Zone names and coordinates
   - Aetheryte locations and IDs
   - Flying unlock requirements

3. **Object IDs**
   - Treasure coffer object IDs
   - Monster spawn IDs (for combat phase)
   - Dungeon entrance IDs

4. **Command Syntax**
   - Teleport commands
   - Mount commands
   - Interaction commands
   - Plugin IPC commands

### Why We Need This Data
- **Map IDs:** To detect and filter maps in inventory
- **Zone Data:** To calculate nearest aetheryte and navigation paths
- **Object IDs:** To detect and interact with coffers and enemies
- **Commands:** To execute game actions programmatically

### How We'll Collect It
1. **Dalamud Data Manager:** Access game sheets and data
2. **Manual Testing:** Verify IDs and coordinates in-game
3. **Community Resources:** Use existing databases (GamerEscape, etc.)
4. **Plugin Inspection:** Examine GlobeTrotter and vnavmesh for reference
5. **Debug Logging:** Log all detected objects during testing

---

## 🔐 Security & Safety Considerations

### Anti-Detection Measures
- **Randomized delays** between actions
- **Human-like movement** patterns
- **Configurable action speeds**
- **Manual override** always available

### Safety Checks
- **Zone verification** before teleporting
- **Combat state checks** before actions
- **Inventory space checks** before looting
- **Party member verification** before flying
- **Timeout mechanisms** for all operations

### Error Handling
- **Graceful degradation** if dependencies unavailable
- **Comprehensive logging** for debugging
- **User notifications** for critical errors
- **Automatic retry** with backoff
- **Manual recovery** options

---

## 📝 Documentation Requirements

### User Documentation
1. **README.md**
   - Plugin description and features
   - Installation instructions
   - Basic usage guide
   - FAQ section
   - Troubleshooting

2. **HOW_TO_IMPORT_PLUGINS.md**
   - Step-by-step Dalamud plugin installation
   - Custom repo setup
   - Plugin configuration
   - Common issues and solutions

3. **CHANGELOG.md**
   - Version history
   - Feature additions
   - Bug fixes
   - Breaking changes

### Developer Documentation
1. **PROJECT_GAMEPLAN.md** (this file)
   - Project overview
   - Implementation phases
   - Technical details

2. **learning/** folder (gitignored)
   - Development notes
   - API research
   - Testing results
   - Performance benchmarks

---

## 🧪 Testing Strategy

### Unit Testing
- Service method validation
- State machine transitions
- IPC communication
- Data parsing accuracy

### Integration Testing
- Plugin loading in Dalamud
- UI rendering and interaction
- Service coordination
- External plugin IPC

### In-Game Testing
- Map detection accuracy
- Navigation reliability
- Party coordination
- Chest interaction
- Error recovery

### Performance Testing
- Memory usage monitoring
- CPU usage profiling
- Network traffic analysis
- Frame rate impact

---

## 🚀 Release Strategy

### Version Numbering
- **0.0.x** - Development/Alpha (Phases 0-1)
- **0.1.x** - Beta (Phases 2-6)
- **0.2.x** - Stable (Phase 7+)
- **1.0.0** - Full release (All phases complete)

### Release Checklist
- [ ] All phase tasks completed
- [ ] Testing verification passed
- [ ] CHANGELOG.md updated
- [ ] Version number incremented
- [ ] Git commit with descriptive message
- [ ] Git push to trigger auto-release
- [ ] Verify GitHub Actions build succeeds
- [ ] Verify release.zip contains correct files (no zip-within-zip!)
- [ ] Test installation from release
- [ ] Update repo.json with new version

### GitHub Actions Workflow
- **Trigger:** Push to master branch or version tag
- **Build:** Windows runner, .NET 10.0
- **Artifact:** Upload latest.zip and LootGoblin.json
- **Release:** Create GitHub release with artifacts
- **No zip-within-zip:** Verified via memory pattern

---

## 🎓 Learning from Previous Plugins

### From ChocoboColourized
- ✅ Proper `CopyPluginArtifacts` target setup
- ✅ GitHub Actions auto-release workflow
- ✅ No zip-within-zip issues
- ✅ Correct artifact naming and paths

### From FrenRider
- ✅ Multi-service architecture
- ✅ State machine pattern
- ✅ Party coordination logic
- ✅ Mount detection and waiting
- ✅ Configuration management
- ✅ Command handling

### From HelloFellowHuman
- ✅ Simple UI patterns
- ✅ Basic plugin structure
- ✅ Settings window implementation

### From PlogonRules
- ✅ Build system best practices
- ✅ Release process guidelines
- ✅ IPC integration patterns
- ✅ Dalamud API usage

---

## 📦 Dependencies & References

### Required Plugins (Runtime)
- **GlobeTrotter** - Map location detection (optional but recommended)
- **vnavmesh** - Navigation and pathfinding (required)

### Optional Plugins (Runtime)
- **FrenRider** - Party coordination enhancement
- **AutoDiscard** - Inventory management
- **AutoDuty** - Repair logic reference

### Development References
- **SamplePlugin:** https://github.com/goatcorp/SamplePlugin
- **Dalamud Docs:** https://dalamud.dev
- **GlobeTrotter:** Research IPC methods
- **vnavmesh:** Research navigation API

---

## ⚠️ Known Challenges & Considerations

### Technical Challenges
1. **IPC Reliability:** External plugin dependencies may not always be available
2. **Navigation Accuracy:** vnavmesh pathfinding may fail in complex terrain
3. **Combat Complexity:** Combat automation requires extensive testing
4. **Dungeon Logic:** Dungeon navigation is highly complex
5. **Performance:** Multiple service polling may impact game performance

### Design Decisions
1. **Modular Architecture:** Services can be disabled if dependencies unavailable
2. **Graceful Degradation:** Plugin functions with reduced features if IPC fails
3. **Manual Override:** User can always take manual control
4. **Configurable Automation:** All automation features are opt-in
5. **Safety First:** Conservative timeouts and error handling

### Future Considerations
1. **Multi-map Optimization:** Batch processing multiple maps efficiently
2. **Party Leader Mode:** Coordinate multiple bots in party
3. **Custom Routes:** User-defined navigation paths
4. **Advanced Loot Filtering:** Intelligent inventory management
5. **Performance Tuning:** Optimize polling and update frequencies

---

## 📅 Timeline Estimate

### Phase 0-1: Foundation (Week 1)
- Project setup and basic plugin loading
- **Goal:** Plugin appears in Dalamud and opens UI

### Phase 2-3: Core Features (Week 2-3)
- Map detection and navigation
- **Goal:** Bot can teleport to map location

### Phase 4-5: Automation (Week 3-4)
- Party coordination and state machine
- **Goal:** Bot completes basic map workflow

### Phase 6: Chest Interaction (Week 4)
- Coffer detection and opening
- **Goal:** Bot opens treasure chests

### Phase 7+: Advanced Features (Week 5+)
- Combat, dungeons, inventory management
- **Goal:** Full automation with minimal user input

**Note:** Timeline is approximate and depends on testing and iteration.

---

## ✅ Success Criteria

### Phase 0-1 Success
- [ ] Plugin loads in Dalamud without errors
- [ ] UI windows open and display correctly
- [ ] Configuration saves and loads
- [ ] Slash commands work

### Phase 2-3 Success
- [ ] Maps detected in inventory
- [ ] GlobeTrotter integration works
- [ ] Teleport to map location succeeds
- [ ] vnavmesh navigation works

### Phase 4-5 Success
- [ ] Party coordination works
- [ ] State machine transitions correctly
- [ ] Full workflow completes without errors
- [ ] Error recovery works

### Phase 6+ Success
- [ ] Chest interaction works reliably
- [ ] Combat handling functional (future)
- [ ] Dungeon completion successful (future)
- [ ] Inventory management working (future)

### Final Success Criteria
- [ ] Bot completes maps with 95%+ success rate
- [ ] No game crashes or freezes
- [ ] Minimal user intervention required
- [ ] Performance impact < 5% FPS drop
- [ ] Community feedback positive

---

## 🔄 Continuous Improvement

### After Each Phase
1. **User Feedback:** Collect and analyze user reports
2. **Bug Fixes:** Address critical issues immediately
3. **Performance:** Profile and optimize bottlenecks
4. **Documentation:** Update guides and FAQs
5. **Planning:** Adjust future phases based on learnings

### Long-Term Maintenance
1. **Dalamud Updates:** Keep compatible with API changes
2. **Game Patches:** Verify functionality after FFXIV updates
3. **Dependency Updates:** Monitor external plugin changes
4. **Feature Requests:** Evaluate and prioritize community requests
5. **Security:** Monitor for detection risks and adjust

---

## 📞 Support & Community

### Issue Reporting
- GitHub Issues for bug reports
- Detailed reproduction steps required
- Log files and screenshots helpful
- Version information mandatory

### Feature Requests
- GitHub Discussions for suggestions
- Community voting on priorities
- Feasibility assessment before implementation
- Transparent roadmap updates

### Contributing
- Pull requests welcome
- Code review required
- Testing verification needed
- Documentation updates expected

---

## 🎯 Next Steps

1. **User Confirmation:** Wait for approval of this gameplan
2. **Repository Setup:** Create GitHub repository
3. **Project Initialization:** Set up basic structure
4. **Phase 1 Implementation:** Begin coding basic plugin
5. **Testing:** Verify plugin loads in-game
6. **Iteration:** Refine based on feedback

---

**Last Updated:** 2026-03-03  
**Status:** Awaiting user approval to begin implementation  
**Current Phase:** Phase 0 - Planning Complete

---

