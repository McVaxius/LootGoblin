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

### **Phase 7: Combat Handling** ⚔️
**Goal:** Enable BMR AI to handle combat encounters at treasure locations

**Status:** ✅ COMPLETE - Combat is handled by BMR with FrenRider autorot preset

**Implementation:**
- Bot enables BMR AI via `/bmrai on` after dismounting at treasure location
- BMR handles all combat mechanics using FrenRider autorot preset
- Bot waits during combat, checking for portal appearance every tick
- Bot clears target during combat so player can fight freely (no lockon to chest)
- After combat ends, bot resumes chest interaction or proceeds to portal

**Configuration Settings (Phase 7):**
```csharp
// No configuration needed - BMR is always enabled after dismount
// Command: /bmrai on (sent automatically in TickFlying after dismount)
```

**Key Points:**
- ✅ BMR is the ONLY combat plugin used (with FrenRider autorot preset)
- ✅ No retreat logic - BMR handles all combat decisions
- ✅ No HP threshold checks - BMR handles survival
- ✅ No combat plugin IPC needed - just send `/bmrai on` command
- ✅ Bot does NOT interfere with combat - clears target and waits

**Testing Checklist:**
- [x] BMR AI enables after dismount
- [x] Bot waits during combat without interfering
- [x] Portal detection works during/after combat
- [x] Target cleared during combat for free movement
- [x] Bot resumes normal operation after combat ends

---

### **Phase 8: Dungeon Handling (Future)** 🏰
**Goal:** Navigate and complete treasure map dungeons (Aquapolis, Uznair variants, Lyhe Ghiah, Excitatron 6000)

**Status:** ⏳ PLANNING - Requires comprehensive research and mapping before implementation

**⚠️ CRITICAL:** This phase requires extensive planning and research. DO NOT implement without detailed discussion and verification of all mechanics.

---

#### **8.1 Dungeon Types & Mechanics Research**
//comments from user:  general comment, you didn't list the lower-level / non-dungeon maps. pleaes include those as they are valid options. finishing outside of a dungeon in the overworld is still something that some people engage in. specially for the Alexandrite maps for the ARR relic quests.

**Known Dungeon Types:**
1. **The Aquapolis** (Dragonsong War maps - Lv60)
   - Entry: Dragonskin, Gaganaskin, Gazelleskin maps
   - Floors: 7 maximum
   - Mechanics: Basic combat, simple loot
   - Best for initial testing

2. **Lost Canals of Uznair** (Stormblood maps - Lv70)
   - Entry: Gaganaskin, Gazelleskin maps
   - Floors: 7 maximum
   - Mechanics: Mini-games, traps, special events
   - More complex than Aquapolis

3. **Hidden Canals of Uznair** (Stormblood maps - Lv70, solo variant) //comments from user: no it isnt. remove ", solo variant" that is not a thing.
   - Entry: Thief's Map
   - Floors: 7 maximum
   - Mechanics: Solo-balanced, similar to Lost Canals
   - Good for solo testing

4. **Shifting Altars of Uznair** (Stormblood maps - Lv70)
   - Entry: Gazelleskin maps
   - Floors: 7 maximum
   - Mechanics: Similar to Lost Canals
   - Variant dungeon

5. **The Dungeons of Lyhe Ghiah** (Shadowbringers maps - Lv80)
   - Entry: Zonureskin maps
   - Floors: 7 maximum
   - Mechanics: Harder combat, better loot
   - No mini-games (simpler than Uznair)

6. **The Excitatron 6000** (Endwalker maps - Lv90)
   - Entry: Kumbhiraskin, Ophiotauroskin maps
   - Floors: 9 maximum
   - Mechanics: Most complex, special events, highest rewards
   - Requires extensive testing

**Common Mechanics Across All Dungeons:**
//comments from user: there is a mini-game that I believe started in endwalker maps with a higher/lower card game. instead of playing the game we will aim to just open the lockbox immediately to save time and sanity.  a single action and easy to test.  this minigame pops up after some floors are completed and the main floor chest is clicked. it can also happen in the final room.
- Sequential room progression (7-9 floors depending on dungeon) //comments from user: some of them have alot of room but its not normalized between expansions or map types. 
- Combat encounters in each room
- Loot distribution after each floor
- Binary choice system: Continue deeper OR take loot and leave //comments from user: no. it isn't how people play. the system itself decides if you are being kicked out or not.
- Random chance of being forced to exit (RNG "bad luck" exit) //comments from user: this is true. there is also a good luck door (guaranteed open and no kick out. golden aura or something appears. we aren't going to try to detect it). which can cause you to get stuck if you pick the "not good luck door" because it locks that door when you try to interact.  when this happens we will just try a different door if we get stuck for 1 minute trying to get through a door.
- Treasure coffers in each room //comments from user:  yes sometimes there are sacks, and additional chests / coffers depending on the bonus loots that appear.  these should be grabbed when combat isn't on anymore
- Time limits per floor (varies by dungeon) //comments from user: no the time limits are per dungeon, not by floor
- Party-based (except Hidden Canals solo variant) //comments from user:  there is no hidden canals solo variant. its still a group map

---

#### **8.2 Object Detection & Interaction**

**Interactive Objects to Detect:**

1. **Treasure Coffers** (loot containers)
   - Object Name: "Treasure Coffer" (verify in-game)
   - Interaction: Target + Interact
   - May trigger mini-games or traps in Uznair variants
   - Appears after combat ends

2. **Exit Portals** (leave dungeon, take current loot)
   - Object Name: "Accursed Hoard" or similar (NEEDS VERIFICATION)
   - Interaction: Target + Interact + Yes/No dialog
   - Ends dungeon run, returns to overworld with loot
   - Always available after clearing floor

3. **Progression Portals** (continue to next floor)
   - Object Name: "Passage" or "Door" or similar (NEEDS VERIFICATION)
   - Interaction: Target + Interact + Yes/No dialog
   - Advances to next floor, forfeits current floor loot
   - Only available if RNG allows (not forced exit)

4. **Mini-game Objects** (Uznair variants only)
   - Object Names: Varies by mini-game type (NEEDS RESEARCH)
   - Interaction: Varies by game
   - Success/failure affects loot or progression
   - Examples: Roulette wheel, treasure selection, traps

5. **Loot Items** (ground drops)
   - Object Kind: `ObjectKind.Treasure`
   - Interaction: Standard FFXIV loot system
   - Need/Greed/Pass system in parties
   - Auto-loot if solo

**Detection Strategy:**
```csharp
// Scan ObjectTable for dungeon-specific objects
private IGameObject? FindDungeonObject(string objectName)
{
    return Plugin.ObjectTable.FirstOrDefault(obj => 
        obj != null && 
        obj.IsTargetable && 
        obj.Name.ToString() == objectName);
}

// Find all interactive objects in current room
private List<IGameObject> ScanDungeonRoom()
{
    return Plugin.ObjectTable
        .Where(obj => obj != null && 
               obj.IsTargetable &&
               (obj.Name.ToString().Contains("Coffer") ||
                obj.Name.ToString().Contains("Hoard") ||
                obj.Name.ToString().Contains("Passage") ||
                obj.Name.ToString().Contains("Portal") ||
                obj.Name.ToString().Contains("Door")))
        .ToList();
}
```

**Research Required:**
- [ ] Log all object names in each dungeon type
- [ ] Verify exact names for exit portals
- [ ] Verify exact names for progression portals
- [ ] Document mini-game object names
- [ ] Test object detection reliability

---

#### **8.3 Navigation & Positioning**

**Navigation Challenges:**
1. **Small Rooms:** Limited space, vnavmesh may struggle with precision
2. **Obstacles:** Furniture, decorations, terrain features block paths
3. **Party Stacking:** Multiple players in small area causes collision
4. **Combat Movement:** BMR handles movement during combat, bot should not interfere
5. **Vertical Positioning:** Some dungeons have elevation changes

**Proposed Navigation Strategy:**
- **Long-range (>10y):** Use vnavmesh for initial positioning
- **Short-range (<10y):** Use lockon+automove for object interaction
- **During Combat:** Do nothing, let BMR handle positioning
- **Safe Zones:** Calculate dynamically based on room center or party position

**Safe Positioning Strategy:**
```csharp
// Option 1: Room center calculation (preferred - no hardcoding)
private Vector3 CalculateRoomCenter()
{
    var partyMembers = Plugin.PartyList
        .Where(m => m != null && m.GameObject != null)
        .Select(m => m.GameObject.Position)
        .ToList();
    
    if (partyMembers.Any())
    {
        return new Vector3(
            partyMembers.Average(p => p.X),
            partyMembers.Average(p => p.Y),
            partyMembers.Average(p => p.Z)
        );
    }
    
    return Plugin.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
}

// Option 2: Hardcoded safe positions (last resort, only if needed)
private Dictionary<uint, Vector3> SafePositions = new()
{
    // [TerritoryId] = SafePosition
    // Only populate if dynamic calculation fails
};
```

**Implementation Notes:**
- Prefer dynamic positioning over hardcoded coordinates
- Only hardcode if absolutely necessary (user can provide via Hyperborea)
- Use party member positions as reference for "safe" areas
- Avoid standing near walls or corners
//comments from user: a thought i had was perhaps to prove .txt versions of bossmod replays for datamining to get object names, entity names etc and make a database fed by a script that will look for new things and organize them in a useful way for ths plugin.  something to think about if you want to hardcode things. otherwise we will have to come up with some regex.  just more thoughts and research to include for now.
---

#### **8.4 Decision Making Logic**

**Continue vs. Exit Decision:**
The bot must decide whether to continue to the next floor or exit with current loot.

**Decision Factors:**
1. **Floor Number:** Higher floors = higher risk, higher reward
2. **Inventory Space:** Full inventory = must exit
3. **Party Status:** Party wants to continue/exit
4. **RNG:** Forced exit (no choice)
5. **User Strategy:** Configuration preference

**Configuration:**
```csharp
public enum DungeonExitStrategy
{
    AlwaysContinue,      // Go until forced exit (greedy)
    ExitAfterFloor3,     // Safe, guaranteed loot
    ExitAfterFloor5,     // Balanced risk/reward (recommended)
    ExitAfterFloor7,     // High risk, high reward
    ExitOnFullInventory, // Practical, inventory-based
    Random               // Randomize to appear human
}

public DungeonExitStrategy ExitStrategy { get; set; } = DungeonExitStrategy.ExitAfterFloor5;
```

**Implementation:**
```csharp
private bool ShouldContinueToNextFloor(int currentFloor)
{
    // Check inventory space first
    var freeSlots = GetFreeInventorySlots();
    if (freeSlots < Configuration.MinInventorySlots)
    {
        _plugin.AddDebugLog($"[Dungeon] Inventory nearly full ({freeSlots} slots) - exiting");
        return false;
    }
    
    // Apply strategy
    return Configuration.ExitStrategy switch
    {
        DungeonExitStrategy.AlwaysContinue => true,
        DungeonExitStrategy.ExitAfterFloor3 => currentFloor < 3,
        DungeonExitStrategy.ExitAfterFloor5 => currentFloor < 5,
        DungeonExitStrategy.ExitAfterFloor7 => currentFloor < 7,
        DungeonExitStrategy.ExitOnFullInventory => freeSlots > 5,
        DungeonExitStrategy.Random => new Random().Next(0, 2) == 0,
        _ => false
    };
}

private int GetFreeInventorySlots()
{
    // Count empty slots in main inventory
    var inventory = Plugin.InventoryManager.GetInventoryContainer(InventoryType.Inventory1);
    return inventory?.Count(slot => slot.ItemId == 0) ?? 0;
}
```

---

#### **8.5 State Machine Extensions**

**New States Required:**
```csharp
public enum BotState
{
    // ... existing states ...
    InDungeon,           // Already exists - entered dungeon
    DungeonCombat,       // Combat encounter in dungeon floor
    DungeonLooting,      // Collecting loot after combat
    DungeonDecision,     // Waiting at continue/exit choice
    DungeonProgressing,  // Moving to next floor
    DungeonExiting,      // Leaving dungeon via exit portal
    DungeonComplete      // Returned to overworld, ready for next map
}
```

**State Transitions:**
```
InDungeon → DungeonCombat (combat detected via ConditionFlag.InCombat)
DungeonCombat → DungeonLooting (combat ends, enemies despawned)
DungeonLooting → DungeonDecision (all loot collected, coffers opened)
DungeonDecision → DungeonProgressing (user chose to continue)
DungeonDecision → DungeonExiting (user chose to exit OR forced exit)
DungeonProgressing → InDungeon (next floor loaded, new room)
DungeonExiting → DungeonComplete (back in overworld)
DungeonComplete → Idle (ready for next map)
```

**State Tick Methods:**
```csharp
private void TickInDungeon()
{
    // Wait for combat to start or detect objects
    // Enable BMR AI if not already enabled
    // Monitor for combat initiation
}

private void TickDungeonCombat()
{
    // Wait for combat to end
    // Clear target so BMR can fight freely
    // Monitor party status (wipes)
}

private void TickDungeonLooting()
{
    // Find and open treasure coffers
    // Collect ground loot
    // Handle Need/Greed/Pass windows
    // Wait for party to finish looting
}

private void TickDungeonDecision()
{
    // Detect exit portal and progression portal
    // Make continue/exit decision
    // Interact with chosen portal
    // Handle Yes/No dialog
}

private void TickDungeonProgressing()
{
    // Wait for loading screen
    // Detect new floor
    // Increment floor counter
    // Transition back to InDungeon
}

private void TickDungeonExiting()
{
    // Wait for loading screen
    // Detect overworld return
    // Disable BMR AI
    // Transition to DungeonComplete
}
```

---

#### **8.6 Combat Handling in Dungeons**

**Combat Detection:**
```csharp
private bool IsInDungeonCombat()
{
    // Check both InCombat flag and BoundByDuty flag
    return Plugin.Condition[ConditionFlag.InCombat] && 
           Plugin.Condition[ConditionFlag.BoundByDuty];
}
```

**Combat Strategy:**
- BMR AI handles ALL combat (same as overworld)
- Bot waits for combat to end //comments from user: wait for ending to do things non combat related.
- Bot clears target during combat (no interference) //comments from user: that sounds like interference. don't help with targeting during combat.
- No special dungeon combat logic needed
- BMR with FrenRider autorot handles all mechanics //comments from user: look at FrenRider for that IPC to install the rotation if it is missing. and all associated commands / ipc

**Post-Combat Actions:**
1. Wait for all enemies to despawn
2. Verify combat flag cleared
3. Scan for treasure coffers
4. Scan for loot items on ground
5. Transition to DungeonLooting state
//comments from user: 6. Scan for doors
//comments from user: 7. the Card Game menu popup (then click on the open lockbox (sp?) option)

**Error Handling:**
```csharp
// Detect party wipe
private bool IsPartyWiped()
{
    var aliveCount = Plugin.PartyList.Count(m => m != null && m.CurrentHP > 0);
    return aliveCount == 0;
}

// Handle wipe
if (IsPartyWiped())
{
    _plugin.AddDebugLog("[Dungeon] Party wipe detected - waiting for respawn");
    // Wait for respawn, then exit dungeon
    TransitionTo(BotState.DungeonExiting);
}
```

---

#### **8.7 Loot Management**

**Loot Detection:**
```csharp
// Detect loot items on ground
private List<IGameObject> FindGroundLoot()
{
    return Plugin.ObjectTable
        .Where(obj => obj != null && 
               obj.ObjectKind == ObjectKind.Treasure)
        .ToList();
}

// Detect Need/Greed/Pass windows
private bool IsLootWindowOpen()
{
    var lootWindow = Plugin.GameGui.GetAddonByName("NeedGreed", 1);
    return lootWindow != IntPtr.Zero;
}
```

**Loot Strategy:**
```csharp
public enum LootStrategy
{
    NeedAll,    // Need on everything (greedy)
    GreedAll,   // Greed on everything (polite)
    PassAll,    // Pass on everything (testing mode)
    Smart       // Need on usable, Greed on rest (future)
}

public LootStrategy DungeonLootStrategy { get; set; } = LootStrategy.GreedAll;
```

**Implementation:**
```csharp
private void HandleLootWindow()
{
    // Click Need/Greed/Pass based on strategy
    // Similar to YesNo dialog handling
    switch (Configuration.DungeonLootStrategy)
    {
        case LootStrategy.NeedAll:
            // Click Need button
            break;
        case LootStrategy.GreedAll:
            // Click Greed button
            break;
        case LootStrategy.PassAll:
            // Click Pass button
            break;
    }
}

private void CollectGroundLoot()
{
    var lootItems = FindGroundLoot();
    foreach (var item in lootItems)
    {
        if (Vector3.Distance(player.Position, item.Position) <= 3f)
        {
            Plugin.TargetManager.Target = item;
            GameHelpers.InteractWithObject(item);
        }
    }
}
```

**Loot Completion Check:**
- All ground loot collected
- All Need/Greed/Pass windows resolved
- All treasure coffers opened
- Party members finished looting (if in party)

---

#### **8.8 Mini-Game Handling (Uznair Variants)**

**Known Mini-Games (NEEDS VERIFICATION):**
1. **Roulette Wheel** - Click to stop spinning wheel on good result --REVIEWED-- //comments from user: this isn't a minigame its just a door cutscene
2. **Treasure Selection** - Choose correct coffer from multiple options --REVIEWED-- //comments from user: this is not a thing. you choose ALL coffers everytime. there is never a coffer choice
3. **Monster Match** - Memory matching game --REVIEWED-- //comments from user: this is not a thing. i told you not to make shit up. this is "made up shit"
4. **Trap Avoidance** - Don't trigger trap coffers --REVIEWED-- //comments from user: this isnt deep dungeon.

**Detection Strategy:**
- Scan for mini-game specific UI elements (NEEDS RESEARCH)
- Detect special object names (NEEDS RESEARCH)
- Monitor chat messages for mini-game prompts
- Look for unique ConditionFlags or AddonIds

**Handling Strategy:**
- **Phase 8.0-8.2:** Skip mini-games, just wait for timeout --REVIEWED-- //comments from user: no thats terrible idea. you can always use generic callbacks and or controllermode Numpad0 to try and bypass it or get to the yes/no dialog (choose yes) and log all of the callbacks for refinement later.
- **Phase 8.3:** Implement simple random selection --REVIEWED-- //comments from user: no. just pick whichever door object is closer.  if you get stuck there for 1 minute that means the toher one was the guaranteed door. so go to it.
- **Phase 8.4:** Implement optimal strategies (if patterns exist) --REVIEWED-- //comments from user: for what exactly? BMR will do all combat. doors are RNG we don't care which door we open.  And we aren't playing the card game - we are skipping by choosing open lockbox (i thin its called lockbox id have to see in game or you can research).

**Configuration:**
```csharp
public bool ParticipateInMinigames { get; set; } = false; // Default: skip/timeout
public bool UseOptimalMinigameStrategy { get; set; } = false; // Future feature
public int MinigameTimeout { get; set; } = 30; // Wait 30s then proceed
```

**Research Requirements:**
- [ ] Spawn Uznair dungeons with Hyperborea --REVIEWED-- //comments from user: for what purpose. i can't spawn creatures in
- [ ] Document all mini-game types --REVIEWED-- //comments from user: already explained
- [ ] Log all mini-game UI elements
- [ ] Test timeout behavior
- [ ] Identify optimal strategies (if any) --REVIEWED-- //comments from user: see above

---

#### **8.9 Party Coordination**

**Party Considerations:**
1. **Wait for Party:** Don't rush ahead alone --REVIEWED-- //comments from user: this doesnt matter they will get summoned. always go to next room
2. **Loot Distribution:** Respect party loot rules --REVIEWED-- //comments from user: that is going to be automatic based on lazyloot rules. don't worry baout it aside from assigning configuration exactly how its setup in FrenRider
3. **Decision Sync:** Leader makes continue/exit decision --REVIEWED-- //comments from user: no. the only thing the "leader" does is click on teh chests, sacks and card mini game and pick and click the doors.  Exiting on purpose is not a thing unless we are fully finished.  In most cases we will be ejected anyways since each door is just 50% chance to open on the regular dungeons and the secret thief maps have 33.3333% since 3 doors.
4. **Combat Positioning:** Don't block party members --REVIEWED-- //comments from user: thats not a thing in ff14 we can all stand in teh same x,y,z coordinate with no issues or effect.
5. **Respawn Coordination:** Wait for all to respawn after wipe --REVIEWED-- //comments from user: there is no respawn, if we wipe we are kicked out don't even consider this concept at all for treasure maps.  everytime we leave the map its for the same reason. we are done with that map. we cannot re enter it. so we are either done the loop or we go to the next map configured whether its more of hte same (stacked maps) or a different one.

**Leader vs. Follower Mode:** --REVIEWED-- //comments from user: no this is not a thing. the player running lootgoblin is always the leader. there are no exceptions we aren't going to re-implement FrenRider in LootGoblin
```csharp
public bool IsDungeonLeader { get; set; } = false; // Default: follow party leader

// Leader Mode:
// - Makes continue/exit decisions
// - Initiates portal interactions
// - Coordinates party actions

// Follower Mode:
// - Waits for leader to interact with portals
// - Follows through portals after leader
// - Mirrors leader's loot strategy
```

**Implementation:**
```csharp
private void WaitForParty()
{
    var partyMembers = Plugin.PartyList.Where(m => m != null).ToList();
    var localPlayer = Plugin.ClientState.LocalPlayer;
    
    if (partyMembers.Count == 0)
    {
        // Solo mode - no waiting needed
        return;
    }
    
    // Check if all party members are in range
    var allInRange = partyMembers.All(m => 
        m.GameObject != null && 
        Vector3.Distance(localPlayer.Position, m.GameObject.Position) <= 20f);
    
    if (!allInRange)
    {
        StateDetail = "Waiting for party members...";
        return;
    }
}

private bool IsPartyReady()
{
    // All members alive, in range, not in combat
    var partyMembers = Plugin.PartyList.Where(m => m != null).ToList();
    return partyMembers.All(m => 
        m.CurrentHP > 0 && 
        !Plugin.Condition[ConditionFlag.InCombat]);
}
```

---

#### **8.10 Floor Tracking & Progression**

**Floor Counter:**
```csharp
private int currentFloor = 0;
private int maxFloorsReached = 0;

private void IncrementFloor()
{
    currentFloor++;
    if (currentFloor > maxFloorsReached)
        maxFloorsReached = currentFloor;
    
    _plugin.AddDebugLog($"[Dungeon] Advanced to floor {currentFloor}");
}
```

**Floor Detection:**
```csharp
// Detect floor change via territory change or loading screen
private bool IsLoadingScreen()
{
    return Plugin.Condition[ConditionFlag.BetweenAreas] ||
           Plugin.Condition[ConditionFlag.BetweenAreas51];
}

// Reset floor counter on dungeon entry
private void OnDungeonEntry()
{
    currentFloor = 1;
    _plugin.AddDebugLog("[Dungeon] Entered dungeon - starting at floor 1");
}
```

**Progression Logic:**
```csharp
private void HandleFloorProgression()
{
    // Find progression portal
    var progressionPortal = FindDungeonObject("Passage"); // VERIFY NAME
    
    if (progressionPortal == null)
    {
        // Forced exit or no progression available
        _plugin.AddDebugLog("[Dungeon] No progression portal - forced exit");
        TransitionTo(BotState.DungeonExiting);
        return;
    }
    
    // Make decision
    bool shouldContinue = ShouldContinueToNextFloor(currentFloor);
    
    if (shouldContinue)
    {
        _plugin.AddDebugLog($"[Dungeon] Continuing to floor {currentFloor + 1}");
        InteractWithPortal(progressionPortal);
        TransitionTo(BotState.DungeonProgressing);
    }
    else
    {
        _plugin.AddDebugLog($"[Dungeon] Exiting after floor {currentFloor}");
        var exitPortal = FindDungeonObject("Accursed Hoard"); // VERIFY NAME
        if (exitPortal != null)
        {
            InteractWithPortal(exitPortal);
            TransitionTo(BotState.DungeonExiting);
        }
    }
}
```

---

#### **8.11 Error Handling & Recovery**

**Failure Scenarios:**

1. **Forced Exit (RNG)**
   - Detection: No progression portal available
   - Handling: Normal, not an error - exit gracefully
   - Recovery: Return to overworld, proceed to next map

2. **Party Wipe** --REVIEWED-- //comments from user: not needed
   - Detection: All party members HP = 0 --REVIEWED-- //comments from user: pointless
   - Handling: Wait for respawn, then exit dungeon --REVIEWED-- //comments from user: this is not a thing we are immediately kicked out
   - Recovery: Respawn at entrance, use exit portal --REVIEWED-- //comments from user: this is not a thing we are immediately kicked out

3. **Navigation Stuck** //comments from user: no. quitting everything is not how to resolve. the only time we will get stuck inside of a dungeon is if we dont run onto the activation point for the area transition. normalyl these are quite large but they can be missed if you try hard enough. such as in the Excitatron6000 its a smal circular panel you have to step onto.  To solve this, we just need the object names (hidden objects) and we can path to them if we are <10 yalms away and in area transition step
   - Detection: No movement for 30+ seconds
   - Handling: Timeout and manual intervention
   - Recovery: User must manually exit or reset

4. **Loot Window Stuck** //comments from user:  this won't happen. we can warn users if lazyloot isn't installed. otherwise we don't care about stuck loot
   - Detection: Loot window open for 60+ seconds
   - Handling: Force Pass and continue
   - Recovery: Close window, proceed to next action

5. **Unknown Object** //comments from user:  generally a good idea to have some kind of separate database not part of the dalamud.log with timestamps etc so we can datamine for knowledge growth
   - Detection: Object name not in known list
   - Handling: Log object details, skip interaction
   - Recovery: Continue with known objects

6. **Disconnect/Crash** //comments from user: this is fine. same as just restarting game or resetting plugin, we don't need resuming logic
   - Detection: Connection lost
   - Handling: Plugin stops, waits for reconnect
   - Recovery: User must manually exit dungeon

**Error Recovery Implementation:**
```csharp
private void HandleDungeonError(DungeonError error)
{
    switch (error)
    {
        case DungeonError.ForcedExit:
            _plugin.AddDebugLog("[Dungeon] RNG forced exit - normal behavior");
            TransitionTo(BotState.DungeonExiting);
            break;
            
        case DungeonError.PartyWipe:
            _plugin.AddDebugLog("[Dungeon] Party wipe - waiting for respawn");
            // Wait for respawn, then find exit portal
            TransitionTo(BotState.DungeonExiting);
            break;
            
        case DungeonError.NavigationStuck:
            _plugin.AddDebugLog("[Dungeon] Navigation stuck - manual intervention needed");
            _plugin.NotifyError("Stuck in dungeon - please exit manually");
            TransitionTo(BotState.Error);
            break;
            
        case DungeonError.LootWindowStuck:
            _plugin.AddDebugLog("[Dungeon] Loot window stuck - forcing pass");
            // Force close loot window, continue
            break;
            
        case DungeonError.UnknownObject:
            _plugin.AddDebugLog($"[Dungeon] Unknown object detected - skipping");
            // Log details for future research
            break;
            
        default:
            _plugin.AddDebugLog($"[Dungeon] Unknown error: {error}");
            TransitionTo(BotState.Error);
            break;
    }
}

public enum DungeonError
{
    ForcedExit,
    PartyWipe,
    NavigationStuck,
    LootWindowStuck,
    UnknownObject,
    Timeout
}
```

---

#### **8.12 Dungeon-Specific Logic**

**Per-Dungeon Configuration:**
```csharp
public class DungeonSettings
{
    public bool Enabled { get; set; }
    public int MaxFloors { get; set; }
    public DungeonExitStrategy ExitStrategy { get; set; }
    public bool HasMinigames { get; set; }
    public int FloorTimeout { get; set; } // seconds per floor
}

public Dictionary<string, DungeonSettings> DungeonConfigs { get; set; } = new()
{
    ["Aquapolis"] = new DungeonSettings 
    { 
        Enabled = true, 
        MaxFloors = 7,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor5,
        HasMinigames = false,
        FloorTimeout = 300
    },
    ["LostCanalsOfUznair"] = new DungeonSettings 
    { 
        Enabled = false, // Disabled until mini-games implemented
        MaxFloors = 7,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor3,
        HasMinigames = true,
        FloorTimeout = 300
    },
    ["HiddenCanalsOfUznair"] = new DungeonSettings 
    { 
        Enabled = false,
        MaxFloors = 7,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor5,
        HasMinigames = true,
        FloorTimeout = 300
    },
    ["ShiftingAltarsOfUznair"] = new DungeonSettings 
    { 
        Enabled = false,
        MaxFloors = 7,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor3,
        HasMinigames = true,
        FloorTimeout = 300
    },
    ["DungeonsOfLyheGhiah"] = new DungeonSettings 
    { 
        Enabled = true,
        MaxFloors = 7,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor5,
        HasMinigames = false,
        FloorTimeout = 300
    },
    ["Excitatron6000"] = new DungeonSettings 
    { 
        Enabled = false, // Most complex - implement last
        MaxFloors = 9,
        ExitStrategy = DungeonExitStrategy.ExitAfterFloor5,
        HasMinigames = false, // Has special events instead
        FloorTimeout = 300
    }
};
```

**Dungeon Detection:**
```csharp
private string? GetCurrentDungeonType()
{
    var territoryId = Plugin.ClientState.TerritoryType;
    
    // Map territory IDs to dungeon types (NEEDS VERIFICATION)
    return territoryId switch
    {
        // Aquapolis territory ID
        570 => "Aquapolis", // VERIFY THIS
        
        // Uznair variants (VERIFY ALL)
        725 => "LostCanalsOfUznair",
        768 => "HiddenCanalsOfUznair",
        769 => "ShiftingAltarsOfUznair",
        
        // Lyhe Ghiah (VERIFY)
        827 => "DungeonsOfLyheGhiah",
        
        // Excitatron (VERIFY)
        1055 => "Excitatron6000",
        
        _ => null
    };
}
```

---
--REVIEWED-- //comments from user: i stopped reading here. you are repeating yourself. please update everything accordingly and mark my comments as --REVIEWED-- at the start of the comment so you and I don't consider it again but it is there for analysis purposes and decision making
#### **8.13 Implementation Phases**

**Phase 8.0: Basic Detection & Entry**
- [ ] Detect dungeon entry (territory change to known dungeon ID)
- [ ] Identify dungeon type via territory ID
- [ ] Initialize floor counter
- [ ] Enable BMR AI for combat
- [ ] Log dungeon entry
- [ ] Wait in InDungeon state

**Phase 8.1: Combat Waiting**
- [ ] Detect combat start
- [ ] Transition to DungeonCombat state
- [ ] Clear target during combat
- [ ] Wait for combat to end
- [ ] Detect party wipes
- [ ] Transition to DungeonLooting after combat

**Phase 8.2: Basic Looting**
- [ ] Detect treasure coffers
- [ ] Interact with coffers (lockon+automove)
- [ ] Handle Yes/No dialogs for coffers
- [ ] Detect ground loot items
- [ ] Collect ground loot
- [ ] Handle loot windows (Pass all for now)
- [ ] Transition to DungeonDecision when done

**Phase 8.3: Exit Only (No Progression)**
- [ ] Detect exit portal
- [ ] Interact with exit portal
- [ ] Handle Yes/No dialog
- [ ] Wait for loading screen
- [ ] Detect overworld return
- [ ] Disable BMR AI
- [ ] Transition to DungeonComplete

**Phase 8.4: Decision Making & Progression**
- [ ] Detect both exit and progression portals
- [ ] Implement continue/exit decision logic
- [ ] Interact with chosen portal based on strategy
- [ ] Handle progression to next floor
- [ ] Increment floor counter
- [ ] Loop back to InDungeon state

**Phase 8.5: Loot Strategy Implementation**
- [ ] Implement Need/Greed/Pass logic
- [ ] Add Smart loot strategy (Need usable, Greed rest)
- [ ] Track inventory space
- [ ] Exit on full inventory
- [ ] Optimize loot collection

**Phase 8.6: Party Coordination**
- [ ] Implement leader/follower modes
- [ ] Wait for party before major actions
- [ ] Sync decisions with party (if possible)
- [ ] Handle party member disconnects
- [ ] Coordinate respawns after wipes

**Phase 8.7: Mini-Games (Uznair)**
- [ ] Research all mini-game types
- [ ] Detect mini-game activation
- [ ] Implement skip/timeout logic
- [ ] Implement random selection
- [ ] (Future) Implement optimal strategies

**Phase 8.8: Advanced Features & Polish**
- [ ] Per-dungeon configuration
- [ ] Performance optimization
- [ ] Comprehensive error recovery
- [ ] Detailed logging and debugging
- [ ] Extensive testing and refinement

---

#### **8.14 Research Requirements**

**MUST COMPLETE BEFORE IMPLEMENTATION:**

1. **Territory IDs**
   - [ ] Verify territory ID for each dungeon type
   - [ ] Test detection on dungeon entry
   - [ ] Document in code comments

2. **Object Names**
   - [ ] Log all object names in each dungeon
   - [ ] Verify "Treasure Coffer" name
   - [ ] Verify exit portal name (Accursed Hoard?)
   - [ ] Verify progression portal name (Passage? Door?)
   - [ ] Document all variants

3. **Mini-Game Mechanics**
   - [ ] Spawn Uznair dungeons with Hyperborea
   - [ ] Document each mini-game type
   - [ ] Log UI elements and object names
   - [ ] Test timeout behavior
   - [ ] Identify detection patterns

4. **Safe Positioning**
   - [ ] Test dynamic room center calculation
   - [ ] Verify party-based positioning works
   - [ ] Only hardcode XYZ if dynamic fails
   - [ ] Document safe zones per dungeon (if needed)

5. **Loot System**
   - [ ] Test Need/Greed/Pass UI detection
   - [ ] Verify ground loot collection
   - [ ] Test inventory space tracking
   - [ ] Document loot distribution timing

6. **Failure Modes**
   - [ ] Test party wipe scenario
   - [ ] Test forced exit (RNG)
   - [ ] Test navigation failures
   - [ ] Test disconnect/reconnect
   - [ ] Document all edge cases

**Research Tools:**
- **Hyperborea:** Spawn dungeons for testing and mapping
- **ObjectTable Logging:** Log all objects in dungeon rooms
- **Chat Log Monitoring:** Capture all dungeon-related messages
- **Position Logging:** Record coordinates for reference
- **Video Recording:** Document mechanics visually
- **Debug Overlay:** Real-time object visualization

**Research Deliverables:**
- Comprehensive object name list with exact strings
- Territory ID mapping for all dungeon types
- Safe position coordinates (only if hardcoding necessary)
- Mini-game detection patterns and strategies
- Failure scenario documentation with recovery steps
- Loot system timing and interaction patterns

---

#### **8.15 Configuration Settings**

```csharp
public class DungeonConfiguration
{
    // === ENABLE/DISABLE ===
    public bool AutoDungeon { get; set; } = false;
    
    // === EXIT STRATEGY ===
    public DungeonExitStrategy ExitStrategy { get; set; } = DungeonExitStrategy.ExitAfterFloor5;
    
    // === LOOT SETTINGS ===
    public LootStrategy LootStrategy { get; set; } = LootStrategy.GreedAll;
    public bool AutoLoot { get; set; } = true;
    public bool WaitForPartyLoot { get; set; } = true;
    public int LootTimeout { get; set; } = 30; // seconds to wait for loot
    
    // === PARTY SETTINGS ===
    public bool WaitForParty { get; set; } = true;
    public bool IsDungeonLeader { get; set; } = false;
    public int PartyWaitTimeout { get; set; } = 30; // seconds
    public bool ExitOnPartyWipe { get; set; } = true;
    
    // === MINI-GAMES ===
    public bool ParticipateInMinigames { get; set; } = false;
    public bool UseOptimalMinigameStrategy { get; set; } = false;
    public int MinigameTimeout { get; set; } = 30; // seconds
    
    // === TIMEOUTS ===
    public int FloorTimeout { get; set; } = 300; // 5 minutes per floor
    public int DungeonTimeout { get; set; } = 1800; // 30 minutes total
    public int CombatTimeout { get; set; } = 180; // 3 minutes per combat
    
    // === SAFETY ===
    public int MinInventorySlots { get; set; } = 5;
    public bool StopOnFullInventory { get; set; } = true;
    public bool StopOnError { get; set; } = true;
    
    // === PER-DUNGEON SETTINGS ===
    public Dictionary<string, DungeonSettings> DungeonConfigs { get; set; } = new();
}
```

---

#### **8.16 Testing Strategy**

**Test Order (Simplest to Most Complex):**
1. **Aquapolis** - Simplest, no mini-games, good for basic testing
2. **Lyhe Ghiah** - Similar to Aquapolis, harder combat
3. **Hidden Canals** - Solo variant, good for solo testing
4. **Lost Canals** - Mini-games, party coordination
5. **Shifting Altars** - Variant mechanics
6. **Excitatron 6000** - Most complex, test last

**Test Scenarios Per Dungeon:**
1. **Solo Run** (if dungeon allows)
   - Test basic navigation
   - Test combat waiting
   - Test loot collection
   - Test exit logic

2. **Party Run (Leader Mode)**
   - Test party coordination
   - Test decision making
   - Test portal interaction timing
   - Test loot distribution

3. **Party Run (Follower Mode)**
   - Test following leader
   - Test waiting for leader actions
   - Test loot collection as follower

4. **Edge Cases**
   - Forced exit (RNG)
   - Party wipe
   - Full inventory
   - Navigation failures
   - Mini-game encounters (Uznair)
   - Timeout scenarios

**Success Criteria Per Phase:**
- **Phase 8.0:** Bot detects dungeon entry and enables BMR
- **Phase 8.1:** Bot waits during combat without interfering
- **Phase 8.2:** Bot collects loot after combat
- **Phase 8.3:** Bot exits dungeon successfully
- **Phase 8.4:** Bot makes correct continue/exit decisions
- **Phase 8.5:** Bot uses appropriate loot strategy
- **Phase 8.6:** Bot coordinates with party
- **Phase 8.7:** Bot handles mini-games (skip or participate)
- **Phase 8.8:** Bot completes dungeons with 90%+ success rate

---

#### **8.17 Known Unknowns & Research Questions**

**Critical Questions to Answer:**
1. ❓ What are the exact territory IDs for each dungeon type?
2. ❓ What are the exact object names for exit portals?
3. ❓ What are the exact object names for progression portals?
4. ❓ How do mini-games appear in the ObjectTable?
5. ❓ Can we detect forced exit before it happens?
6. ❓ How does party vote system work for continue/exit (if any)?
7. ❓ Are there dungeon-specific ConditionFlags we should monitor?
8. ❓ How to detect floor number programmatically (or must we track it)?
9. ❓ What happens if bot is too slow to loot (does floor timeout)?
10. ❓ How long do we have between floors to make decisions?
11. ❓ Do all dungeons use the same portal names or are they different?
12. ❓ How to detect when all party members have finished looting?

**Research Methods:**
1. **Hyperborea Spawning:** Spawn each dungeon type for controlled testing
2. **ObjectTable Logging:** Log all objects in each room
3. **Chat Log Monitoring:** Capture all system messages
4. **UI Element Inspection:** Document all addons and windows
5. **Position Recording:** Record player positions throughout dungeon
6. **Video Recording:** Visual documentation of mechanics
7. **Party Testing:** Test with multiple accounts if possible

**Documentation Format:**
```markdown
## Dungeon: [Name]
- Territory ID: [ID]
- Max Floors: [N]
- Exit Portal Name: "[Exact Name]"
- Progression Portal Name: "[Exact Name]"
- Treasure Coffer Name: "[Exact Name]"
- Mini-Games: [Yes/No] - [List if yes]
- Special Mechanics: [Description]
- Safe Positions: [Dynamic/Hardcoded] - [Coordinates if hardcoded]
- Notes: [Any special considerations]
```

---

#### **8.18 Implementation Priority**

**High Priority (Phase 8.0-8.3):**
- Dungeon detection and entry
- Combat waiting (reuse existing logic)
- Basic loot collection
- Exit portal interaction
- Return to overworld

**Medium Priority (Phase 8.4-8.5):**
- Continue/exit decision making
- Floor progression
- Loot strategy implementation
- Inventory management

**Low Priority (Phase 8.6-8.8):**
- Party coordination (leader/follower)
- Mini-game handling
- Advanced error recovery
- Performance optimization

**Implementation Order:**
1. Start with **Aquapolis** only (simplest)
2. Get basic flow working (enter → combat → loot → exit)
3. Add decision making (continue vs exit)
4. Add **Lyhe Ghiah** support (similar to Aquapolis)
5. Research and add **Uznair variants** (mini-games)
6. Finally add **Excitatron 6000** (most complex)

---

#### **8.19 Dependencies & Integration**

**Required:**
- BMR AI (combat handling)
- vnavmesh (navigation, if needed)
- YesAlready (paused during dungeon)

**Optional:**
- FrenRider (party coordination reference)
- AutoDiscard (inventory management)
- Hyperborea (research and testing)

**IPC Requirements:**
- None expected - use command-based approach
- BMR: `/bmrai on` (already implemented)
- YesAlready: Pause via IPC (already implemented)

---

#### **8.20 Risk Assessment**

**High Risk Areas:**
1. **Mini-Games:** Complex, varied mechanics, may be unreliable
2. **Party Coordination:** Timing issues, sync problems
3. **Navigation:** Small rooms, obstacles, collision
4. **RNG Exits:** Unpredictable, must handle gracefully
5. **Performance:** Multiple object scans per tick

**Mitigation Strategies:**
1. **Mini-Games:** Start with skip/timeout, add strategies later
2. **Party Coordination:** Start with solo/leader mode, add follower later
3. **Navigation:** Prefer lockon+automove over vnavmesh in dungeons
4. **RNG Exits:** Detect and handle as normal behavior, not error
5. **Performance:** Optimize object scans, cache results, throttle checks

**Rollback Plan:**
- If Phase 8 proves too complex, make it opt-in only
- Provide manual mode where user controls dungeon
- Focus on overworld map completion as core feature
- Dungeons as "advanced" feature for power users

---

**⚠️ CRITICAL REMINDERS:**
- **DO NOT START IMPLEMENTATION** without completing research phase
- **DO NOT HARDCODE** positions unless absolutely necessary and user-approved
- **DO NOT ASSUME** mechanics - verify everything in-game first
- **DO NOT RUSH** - this is the most complex phase of the entire project
- **DO COORDINATE** with user before making any decisions
- **DO RESEARCH** using Hyperborea and other tools before coding
- **DO DOCUMENT** all findings thoroughly
- **DO TEST** incrementally, one dungeon type at a time

**Next Steps:**
1. ✅ User reviews and approves this plan
2. ⏳ Research phase using Hyperborea (user may assist)
3. ⏳ Document all findings in research notes
4. ⏳ Refine plan based on research results
5. ⏳ Begin Phase 8.0 implementation (detection only)
6. ⏳ Iterative testing and refinement per sub-phase

---

### **Phase 9: Inventory Management (Future)** 🎒
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




