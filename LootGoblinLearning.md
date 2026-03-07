# LootGoblin Learning Document

Living document tracking what works, what doesn't, and lessons learned.

---

## Phase 1: Overworld (Map → Dig → Chest → Portal) — ✅ WORKING

### Map Selection & Deciphering
- **Status:** Working
- Uses inventory scan order (not UI sort order) to match `/gaction decipher` menu index
- 1-based indexing for `FireCallback` on `SelectIconString`
- Controller mode (`numpad2` → `numpad0` × 2) for single-map-type fast path

### Flying to Location
- **Status:** Working
- `VNavIPC.FlyTo()` with `CultureInfo.InvariantCulture` formatting
- Stuck detection every 10s: re-pathfind if moved <5y (proven TickFlying pattern)
- Party mount wait before flying

### ⚠️ Party Mount Wait Fix: Members not in zone were not counted
- **Symptom:** Bot flew off with 3/4 party after teleporting — 4th member hadn't loaded into zone yet
- **Root cause:** `PartyService.UpdatePartyStatus()` iterated `_partyList` but only added members found in `_objectTable`. Members still loading into the zone were not in `_objectTable`, so they were silently skipped. With only 3 members tracked, `AllMembersMounted=true` prematurely.
- **Fix:** Members not found in ObjectTable are now added as `IsMounted=false, IsInSameZone=false, IsReady=false`. This ensures `AllMembersMounted` stays false until ALL party members are in the zone and mounted.
- **Key rule:** Always use `_partyList` for total member count. `_objectTable` only shows objects in the current zone.

### Digging
- **Status:** Working
- `/gaction dig` after dismount
- Retry dig if combat interrupts before chest spawns

### Overworld Chest Interaction
- **Status:** Working (with stuck detection fix)
- `ChestDetectionService.FindNearestCoffer()` + `lockon+automove`
- `TargetSystem.InteractWithObject()` every 2s
- `ClickYesIfVisible()` for dialogs
- Combat detection: stop automove, clear target, wait for combat end
- **Fix applied:** Stuck detection — if lockon+automove doesn't close 2y in 5s, switches to vnavmesh navigation

### Portal Detection & Interaction
- **Status:** Working
- Portal check runs FIRST every tick (even during combat)
- `TargetSystem.InteractWithObject()` every 1s + `ClickYesIfVisible()`
- `/gaction jump` for underwater portals with Y-axis range issues

---

## Phase 2: Dungeon Entry — ✅ WORKING

### Territory Change Detection
- **Status:** Working
- Territory change handler detects overworld → dungeon transition
- `BoundByDuty` flag confirms dungeon entry
- Objectives reset on new dungeon entry (floor, attempted objects, etc.)

### Object Loading Wait
- **Status:** Working (with fix)
- All dungeon objects start as `Targetable:False` on entry
- Sweep waits up to 30s for objects to become targetable
- **Fix applied:** If progression objects (Arcane Sphere) become targetable first, skip sweep wait

---

## Phase 3: Dungeon Floor Sweep (Coffers/Sacks) — ✅ WORKING

### Object Detection
- **Status:** Working
- `ObjectKind.Treasure` = coffers, sacks (PandorasBox pattern)
- `ObjectKind.EventObj` = arcane spheres, doors, portals
- Always scan BOTH types
- Sweep log throttled to once per 10s

### ProcessLootTarget 3-Phase Approach
- **Status:** Working
- **>6y:** vnavmesh `MoveToPosition()` + stuck detection every 10s
- **3-6y:** Stop vnavmesh, `lockon+automove` + interact every 2s
- **<3y:** Stop ALL movement, interact every 2s
- Multi-method interaction cycling: `TargetSystem.InteractWithObject` (even attempts) + `/interact` command (odd attempts)
- 60s timeout per object (was 15s — too short for dungeon corridors)

---

## Phase 4: Arcane Sphere Interaction — ✅ WORKING

### Detection
- **Status:** Working
- Named `"Arcane Sphere"` as `ObjectKind.EventObj`
- Detected by `GetProgressionObjects()` after sweep is complete

### Interaction
- **Status:** Working
- Same multi-method cycling as ProcessLootTarget
- `ClickYesIfVisible()` for the roulette confirmation dialog
- Stuck detection helped reach the sphere (was getting stuck at 6.3y)

---

## Phase 5: Post-Roulette (After Arcane Sphere) — ✅ WORKING (confirmed in territory 794)

All 5 fixes confirmed working. See `LootGoblinLearningRouletteDungeons.md` for full log analysis.

### Fixes Applied (all confirmed)
1. `CountNearbyUntargetableProgressionObjects()` filters out `attemptedCoffers`/`processedSpheres`
2. `ProcessingSpheres` detects sphere was used → transitions to `DungeonProgressing`
3. `GetProgressionObjects()` expanded to include "door", "gate", "high", "low"
4. `FindDungeonObjects()` includes unnamed targetable EventObj within 30y as potential doors
5. `TickDungeonLooting` checks `BetweenAreas` for floor transitions
6. All timeout/wait failures transition to `DungeonProgressing` instead of `HeadingToExit`

### ⚠️ Regression Fixed: Arcane Sphere classified as loot (infinite loop)
- **Symptom:** Bot entered roulette dungeon (territory 794) and looped forever at floor 1 without ever interacting with Arcane Sphere
- **Cause:** `FindDungeonObjects(lootOnly=true)` returned Arcane Sphere because line `return isSphere || isLoot` treated sphere as loot. The `ProcessingSpheres` loot-priority check (added for Canal fix) called this method, saw the sphere as "loot", reset to `ClearingChests`, which found nothing sweepable, went back to `ProcessingSpheres`, checked for loot again → infinite loop every 0.5s.
- **Fix:** `FindDungeonObjects(lootOnly=true)` now returns `isLoot` only (Treasure objects + named chests/coffers/sacks). Arcane Sphere is **progression**, NOT loot.
- **Also fixed:** Removed `"sphere"` from `doorNames` array — Arcane Sphere is already handled by dedicated `isSphere` check.
- **Key rule: Arcane Sphere is `ObjectKind.EventObj` and is a PROGRESSION object. It must NEVER be classified as loot.**

---

## Phase 6: Door Interaction (`TickDungeonProgressing`) — ✅ WORKING (roulette), 🔧 FIXED (canal)

### Logic
- `FindDungeonObjects(lootOnly: false)` finds doors
- Checks for loot first (bonus spawns after combat)
- Approaches door with lockon+automove (<10y) or vnavmesh (>10y)
- Interacts every 1s with `GameHelpers.InteractWithObject()`
- 60s stuck timer per door → try another door
- Detects loading screen → advance floor → `InDungeon` state

### Door Selection
- Pick closest door (per user: "just pick whichever door object is closer")
- If stuck 60s → exclude that door, try next one
- Golden aura door = guaranteed (no detection yet, relies on closest-first)

### Ejection Detection
- `!BoundByDuty` during progression → ejected (wrong door RNG)
- Transitions to `Completed` state

### Canal of Uznair Bug Fix (loot priority)
- **Bug:** Bot beelined to Sluice Gate before looting Treasure Coffer
- **Root cause 1:** `DungeonProgressing → DungeonLooting` transition didn't reset `currentObjective` to `ClearingChests` — sweep never re-ran
- **Root cause 2:** `ProcessingSpheres` didn't check for loot before targeting progression objects
- **Fix 1:** `DungeonProgressing` resets `currentObjective = ClearingChests` when loot found
- **Fix 2:** `ProcessingSpheres` calls `FindDungeonObjects(lootOnly:true)` before targeting progression — if loot exists, goes back to `ClearingChests`
- **Key learning:** Treasure Coffers can spawn AFTER the initial sweep completes (late object table population)

### ⚠️ Canal of Uznair Bug Fix 2: Opened coffer infinite loop (ClearingChests ↔ ProcessingSpheres)
- **Symptom:** After opening the room chest and clearing combat, bot loops forever between `ClearingChests` (0 sweep) and `ProcessingSpheres` (sees 1 loot → back to ClearingChests)
- **Root cause:** Opened EventObj coffer (ID 1073742525) had `obj.IsTargetable=false` but `IsObjectTargetable()` (TargetManager-based) returned `true`. `GetRoomSweepObjects()` uses `obj.IsTargetable` → correctly excluded. `FindDungeonObjects(lootOnly:true)` uses `IsObjectTargetable()` → false positive → returned as loot.
- **Fix:** Added `obj.IsTargetable` check in `FindDungeonObjects` for EventObj loot (both `allLoot` scan and `candidates` filter). Now both methods agree on targetability.
- **Key rule:** `obj.IsTargetable` and `IsObjectTargetable()` can disagree for opened/used objects. For loot detection, always require `obj.IsTargetable=true`.

### Exit Object Not Targeted (Roulette Final Floor)
- **Symptom:** After completing the final floor of a roulette map, bot failed to target and path to the "Exit" object.
- **Root cause:** `GetRoomSweepObjects()` excludes "exit" (line: `excludeExact = new[] { "exit" }`), but `GetProgressionObjects()` did NOT include "exit" in its match list. The Exit object fell through both phases.
- **Fix:** Added `"exit"` to `GetProgressionObjects()` `progressionPartial` array. Exit is now treated as a progression object alongside doors and sluice gates.

---

## Phase 7: Combat Detection — ✅ WORKING

### Pattern
- `ConditionFlag.InCombat` checked at top of every dungeon tick handler
- Transitions to `DungeonCombat` state, lets BMR handle fighting
- After combat: 2s grace period for despawn, then scan for loot
- `previouslyInCombat` edge detection for single "combat started" log

---

## Key Architecture Decisions

### State Machine Flow
```
Idle → SelectingMap → OpeningMap → DetectingLocation → Mounting → WaitingForParty 
→ Flying → OpeningChest → Completed → InDungeon → DungeonLooting → DungeonCombat 
→ DungeonProgressing → (loop to InDungeon or Completed/Error)
```

### Dungeon Objective Hierarchy
```
ClearingChests (sweep coffers/sacks) → ProcessingSpheres (Arcane Sphere/doors) 
→ DungeonProgressing (door navigation) → InDungeon (next floor)
```

### Important Object Rules
- `ObjectKind.Treasure` = coffers, sacks — interact via `TargetSystem.InteractWithObject()`
- `ObjectKind.EventObj` = spheres, doors, portals — same interaction method works
- Unnamed EventObj can be doors in some territories (e.g. 794)
- Named objects like "Shortcut", scenery are filtered by sweep exclude list
- `IsTargetable` is the key signal for interactability and despawn detection

### Interaction Patterns (Proven)
- **Primary:** `GameHelpers.InteractWithObject()` → `TargetSystem.Instance()->InteractWithObject()`
- **Secondary:** `CommandHelper.SendCommand("/interact")` (game native)
- **Dialog:** `GameHelpers.ClickYesIfVisible()` every tick
- **Approach:** `lockon+automove` for short range, `vnavmesh MoveToPosition` for long range

---

## Known Territories
| Territory ID | Name | Notes |
|---|---|---|
| 612 | The Fringes | Overworld dig location (Seemingly Special maps) |
| 620 | The Peaks | Overworld dig location |
| 621 | The Lochs | Overworld dig location (Seemingly Special maps, confirmed working) |
| 712 | Canals of Uznair | Room-based dungeon. Named objects (High, Low, Shortcut, Sluice Gate). Start: <0.02, 149.80, 388.27>. Treasure Coffer spawns late. |
| 794 | Unknown roulette dungeon | ALL objects unnamed except Arcane Sphere. Roulette confirmed working (regression fixed). |

---

## Settings Removed (Dead Code)
- `AllowPillionRiders` — Not a controllable game feature
- `TargetingMethod` enum — ProcessLootTarget always uses TargetSystem + /interact cycling now
- `InteractMethod1_Current/2/3` — Replaced by multi-method cycling
- `GetTargetCommand`, `PostInteractionTracking`, `TriggerControllerModeInteract`, `SendChatCommand` — All dead code

---

## Dungeon Map Bug Fix: Door Interaction Failed (Sluice Gate)

### Symptom
- Bot clicked Sluice Gate door in Canal of Uznair (territory 712), `InteractWithObject` reported success every 1s, but door never opened
- Bot stuck for 23s until user manually reset
- Log showed: `InteractWithObject called successfully for Sluice Gate` repeatedly with no loading screen or territory change

### Root Cause
`TickDungeonProgressing()` had its own custom interaction code that ONLY used `GameHelpers.InteractWithObject()` (TargetSystem API). This API returns `true` but doesn't always trigger the game interaction for door objects.

Meanwhile, `ProcessLootTarget()` (used by DungeonLooting) properly cycles between two methods:
- Odd attempts: `GameHelpers.InteractWithObject()` (TargetSystem pattern)
- Even attempts: `CommandHelper.SendCommand("/interact")` (game native command)

### Fix
Replaced `TickDungeonProgressing`'s custom interaction code with a call to `ProcessLootTarget(target)`. This reuses the proven multi-method cycling, 3-phase approach (>6y vnavmesh, 3-6y lockon+automove, <3y stop+interact), and stuck detection.

### Key Learning
- `TargetSystem.InteractWithObject()` is NOT reliable for all object types in dungeons
- Always cycle interaction methods for redundancy
- `ProcessLootTarget` is the proven interaction handler - use it everywhere instead of duplicating interaction logic

### Dungeon Map Objects Confirmed (Territory 712)
- **Sluice Gate** - Progression door, EventObj, targetable
- **High** / **Low** - Door choices (2 doors, 50% RNG)
- **Treasure Coffer** - Loot chest, spawns late
- **Unnamed EventObj** - Various scenery/effects, mostly untargetable

### Dungeon Map Flow (Confirmed Working for Roulette, Fixed for Canal)
1. Enter dungeon via Teleportation Portal
2. Navigate to dungeon start point
3. DungeonLooting: Sweep all coffers/sacks (ClearingChests)
4. DungeonLooting: Interact with progression (ProcessingSpheres)
5. DungeonProgressing: Find nearest door, interact with multi-method cycling
6. Loading screen detected -> advance floor -> InDungeon
7. Repeat until ejected (wrong door RNG) or all floors cleared

---

## Dungeon Map Bug Fix 2: Door Opened But Bot Didn't Walk Through

### Symptom
- Maps 1 & 2: Door interaction fix CONFIRMED WORKING (bot interacted with Sluice Gate, got ejected per normal Atomos mechanic)
- Map 3: Sluice Gate door opened successfully (became untargetable), but bot didn't walk through the doorway to trigger area transition
- Bot looped: found Sluice Gate  detected untargetable  added to attemptedCoffers  next tick found SAME gate again (`FindDungeonObjects` not filtering properly)
- After 60s stuck timer, bot tried other door at 34.6y

### Root Causes (TWO bugs)

**Bug 1: `FindDungeonObjects(lootOnly=false)` missing filters**
- Named progression objects (Sluice Gate, High, Low) hit `return true` without checking `attemptedCoffers` or `obj.IsTargetable`
- `IsObjectTargetable()` (TargetManager-based) returned `true` even though `obj.IsTargetable` was `false` for opened objects
- Same disagreement that caused the Canal loot loop bug, but on the progression side
- Fix: Added `attemptedCoffers.Contains()` and `!obj.IsTargetable` checks before `return true`

**Bug 2: No walk-through after door opens**
- After door opens and gets filtered out, `progressionObjects.Count == 0`  bot just waited 30s then rescanned
- Missing: Navigate to door transition XYZ to physically walk through the open doorway and trigger BetweenAreas loading screen
- Fix: Track `lastDoorOpenedPosition` when door disappears from results, then navigate to nearest `DungeonLocationData` door transition point

### Walk-Through Logic Added
1. `doorStuckStart != DateTime.MinValue` + `progressionObjects.Count == 0` = door we were tracking just opened
2. Look up `DungeonLocationData.FindNearestDoorTransition()` for current territory within 50y
3. Navigate to transition point via vnavmesh (>2y) or `/automove on` (2y)
4. BetweenAreas loading screen triggers  advance floor  InDungeon
5. 15s timeout fallback  rescan

### Key Learning
- `obj.IsTargetable` and `IsObjectTargetable()` DISAGREE for opened/used objects - always check `obj.IsTargetable` first
- Door transition in treasure map dungeons requires PHYSICALLY walking to the transition point - interaction alone is not enough
- `DungeonLocationData` door transition XYZ coordinates are critical for this

### Territories Confirmed Working
- Territory 620 (The Peaks) - Overworld dig, confirmed working
- Territory 622 (The Azim Steppe) - Overworld dig, confirmed working
- Territory 712 (Canals of Uznair) - Door interaction confirmed working (Maps 1 & 2 ejected normally)

---

## CONFIRMED PERFECT RUN: Canal of Uznair (Prompt 71, Territory 712)

### Run Summary
- **Map type**: Seemingly Special Timeworn Map (ID: 24794)
- **Territory**: The Lochs (621)  Canal of Uznair (712)
- **Result**: 4 rooms cleared, ejected on room 4 (normal Atomos RNG)
- **Defects**: NONE - perfect run

### What Worked (All Confirmed)
1. **Overworld**: 100% perfect (map decipher, teleport, mount, fly, dig, portal entry)
2. **Portal entry**: 5 tries needed (people rolling on loot) - worked fine
3. **Yes dialog**: Clicked automatically on Sluice Gate Atomos choice
4. **Chest interaction**: Multi-method cycling (TargetSystem + /interact), all coffers collected
5. **Extra loot**: Abharamu spawned extra Treasure Coffers - bot found and collected all
6. **Sluice Gate interaction**: ProcessLootTarget multi-method cycling works
7. **Door walk-through**: `DungeonLocationData.FindNearestDoorTransition()` found correct transition XYZ
8. **Room transitions**: BetweenAreas loading screen detected, floor incremented
9. **Combat handling**: DungeonCombat state properly preserved chest targeting
10. **Ejection detection**: `!BoundByDuty` correctly detected, clean shutdown

### Timing Analysis (Noted, Not Fixed - User Says Keep As-Is)
- **Slowness bottleneck**: ~30s delay between Sluice Gate opening and walk-through
- **Root cause**: Gate interaction happens in `ProcessingSpheres` phase of `DungeonLooting`
- Gate opens  becomes untargetable  ProcessingSpheres sees 0 progression objects  waits 30s timeout  transitions to `DungeonProgressing`  walk-through logic kicks in
- **Potential optimization (FUTURE)**: When a door/gate becomes untargetable during ProcessingSpheres, immediately transition to DungeonProgressing instead of waiting 30s
- **User says**: 'im not sure how safe it is to speed this part up as it worked perfectly' - DO NOT CHANGE unless explicitly asked

### Unnamed EventObj Cycling
- After gate opens, `FindDungeonObjects(lootOnly=false)` returns unnamed EventObjs with `obj.IsTargetable=false` but `IsObjectTargetable()=true`
- Each gets targeted, immediately detected as untargetable, added to `attemptedCoffers`
- ~6 objects  0.5s = ~3s wasted, but resolves naturally
- Fixed by `obj.IsTargetable` check added in prompt 70 fix

### Floor Counter Issue (Cosmetic Only)
- `dungeonFloor` only incremented to 2 after the 3rd room transition
- Loading screen not always detected during DungeonLooting phase (some transitions happen via automove proximity)
- Functionally irrelevant - bot progresses correctly regardless

### Objects Confirmed in Canal of Uznair (Territory 712)
- **Sluice Gate** (EntityId varies) - Progression door, EventObj, becomes untargetable when opened
- **Treasure Coffer** (ObjectKind.Treasure or EventObj) - Loot, multiple can spawn per room
- **High** / **Low** - Door choices (2 doors per room, Atomos RNG)
- **Unnamed EventObj** (`''`) - Scenery/effects, all `Targetable: False` after gate opens

---

## PROMPT 72 FINDINGS: Roulette Maps Confirmed Working

### Run Summary
- **Map type**: Seemingly Special Timeworn Map (ID: 24794) - Roulette/Guaranteed Portal
- **Result**: Multiple runs completed, autostart of next maps confirmed working
- **Defects**: NONE

### Key Confirmations
1. **Roulette maps**: Canal of Uznair runs confirmed perfect (repeat of prompt 71 success)
2. **AutoStartNextMap**: Setting works - bot automatically starts next map after dungeon ejection
3. **Multi-map sessions**: Bot successfully chains multiple map runs without intervention

### No Changes Required
- All existing code paths working as intended
- No new bugs introduced
- Timing/slowness issues remain acceptable per user direction

---

## MAP DATA MODEL UPDATE (Prompt 72)

### New Enums Added
- **MapCategory**: Outdoor, Dungeon, Roulette, GuaranteedPortal
- **ImplementationStatus**: NotStarted, WIP, Implemented

### TreasureMapInfo Extended Fields
- Category - What type of map (outdoor chest only, portal to dungeon, roulette, guaranteed)
- Status - Implementation progress (NotStarted, WIP, Implemented)
- DungeonTerritoryId - Territory ID of the dungeon this map leads to (0 = unknown/none)

### Implementation Status by Expansion
- **ARR**: All 5 maps = Implemented (all Outdoor)
- **HW**: 2/3 Implemented (Dragonskin Dungeon = NotStarted, needs Aquapolis territory ID)
- **SB**: All 3 Implemented (Gazelleskin Roulette + Seemingly Special GuaranteedPortal, both territory 712)
- **ShB**: 1/3 Implemented (Gliderskin Outdoor done, Zonureskin + Ostensibly = WIP, need territory IDs)
- **EW**: 1/4 Implemented (Saigaskin Outdoor done, rest = WIP, need territory IDs)
- **DT**: 1/4 Implemented (Loboskin Outdoor done, rest = WIP, need territory IDs)

### Map Completion UI Panel
- Added to MainWindow as collapsible section
- Groups maps by expansion with tree nodes
- Shows: Status icon [OK]/[WIP]/[--], Name, Category tag, Tier, Level, Territory ID
- Summary line: Total/Done/WIP/TODO counts

---

## FEATURES ADDED (Prompt 72)

### Auto Discard (/ays discard)
- **Setting**: EnableAutoDiscard checkbox in ConfigWindow Bot Automation section
- **Behavior**: Sends /ays discard command every 30 seconds
- **Conditions**: Only fires when logged in, not in combat, not between areas
- **Runs independently**: Works even when bot is idle/disabled (only needs the checkbox enabled)
- **Requires**: AutoRetainer plugin installed

---

## R&D ROADMAP

### Priority 1: Territory ID Research
- Need dungeon territory IDs for: Aquapolis (HW), Lyhe Ghiah (ShB), Excitatron 6000 (EW), DT dungeons
- Method: Run each map type, log territory change in plugin debug output
- Add XYZ door transition data to DungeonLocationData.cs for each new territory

### Priority 2: Aetheryte Teleport Fix
- Current: Uses closest aetheryte, sometimes picks wrong one
- Fix: Research correct aetheryte selection per zone, may need zone-to-aetheryte mapping
- Reference: Existing teleport logic in NavigationService works, just needs better target selection

### Priority 3: Landing Behavior Fix
- Current: Descent can be slow or get stuck on terrain
- Fix: Research altitude checks, faster descent method, terrain collision avoidance
- Reference: Existing Ctrl+Space descent in TickFlying works but could be faster

### Priority 4: Card Game Skip
- Some dungeons have a card game mechanic instead of Atomos roulette
- Need: Identify card game UI addon name, auto-select option to skip
- Reference: Similar to ClickYesIfVisible pattern but for card game addon

### Priority 5: ARR Relic Alexandrite Map Farming
- Special mode for farming Alexandrite from Mysterious Maps (ARR Relic weapon quest)
- Different from normal treasure maps - specific item, specific zones
- Need: Research Mysterious Map item ID, dig mechanics, loot handling

### Priority 6: Map Object Logger
- Log all objects in dungeon rooms for research (like BossMod Reborn logs)
- Output: Object name, ObjectKind, EntityId, position, targetable state
- Purpose: Populate DungeonObjectNames.yaml and DungeonLocationData.cs with verified data
- Implementation: Toggle in debug mode, writes to file on each room entry

---

## PROMPT 73 FINDINGS: Aetheryte Fix, Map Location Database, Flight Altitude

### Aetheryte Selection Fix (v2)
- **Problem**: FindNearestAetheryte was picking wrong aetheryte in Yanxia - chose Namai (south) instead of The House of the Fierce (north) which was closer to flag
- **Root Cause**: Level sheet lookup was silently failing - all aetheryte world positions came back as Vector3.Zero, causing fallback to cheapest gil cost
- **Fix Applied**: 
  - Added verbose logging to Level sheet iteration (logs each Level entry RowId, XYZ, count, exceptions)
  - Removed Territory filter on Level rows (just takes first entry with non-zero XYZ coords)
  - Verbose exception logging instead of silent catch {}
  - Kept XZ distance comparison for closest-to-flag selection
  - Falls back to cheapest cost ONLY if no position data available
- **Status**: DIAGNOSTIC BUILD - need logs to see what Level sheet returns
- **Reference**: FFXIV datamining coordinate docs at github.com/xivapi/ffxiv-datamining/blob/master/docs/MapCoordinates.md

### Teleport Within Zone Feature
- **New Behavior**: When already in the correct territory, if player is >200y from flag AND a valid aetheryte exists, teleport to closer aetheryte instead of just mounting
- **Logic**: In TickDetectingLocation, compares player XZ distance to flag vs aetheryte distance
- **Threshold**: 200 yalms (if closer than 200y, just mount up as before)

### Map Location Database (NEW)
- **File**: MapLocations.json in plugin config directory
- **Purpose**: Records successful dig XYZ positions, reuses them for future flights
- **Format**: Each entry has TerritoryId, ZoneName, MapName, FlagXYZ, RealXYZ, RecordedAt
- **Matching**: When a new flag is within 10 yalm XZ of a stored entry, uses stored RealXYZ for flying
- **Recording**: Captures player's actual ground position at moment of successful dismount (right before /gaction dig)
- **Shareable**: JSON file can be shared between users for community data contribution
- **Service**: MapLocationDatabase.cs - Load/Save/FindEntry/RecordLocation methods
- **Integration**: TickFlying checks DB first, uses stored real XYZ if found, else Y+50 fallback

### Flight Altitude Boost
- **Changed**: Y offset from +15 to +50 (temporary until Map Location DB has entries for each location)
- **Behavior**: Only applies when no stored real XYZ exists in MapLocationDatabase
- **Purpose**: Higher approach altitude prevents terrain collision in hilly zones (especially SB)
- **Long-term**: As MapLocations.json populates with real positions, Y+50 fallback used less and less

### Summon Chocobo Feature (Confirmed Working)
- **LootGoblin**: GameHelpers methods, Config fields, StateManager timer (15s), MainWindow display
- **ConfigWindow**: Checkbox + Companion Stance dropdown
- **Conditions**: Bot enabled, not in combat/mounted/duty/sanctuary, timer < 900s, has greens
- **Stance**: Set 3s after summoning via deferred command

### Auto Discard Fix
- **Fix**: Now gated behind Configuration.Enabled (only runs when bot is on)
- **Previously**: Was running independently of bot state

### Gitignore
- *.md pattern already in place with !README.md !CHANGELOG.md exceptions
- No tracked .md files to remove from repo
- LootGoblinLearning.md, LGdecisionflow.md, PROJECT_GAMEPLAN_UPDATED.md all properly gitignored

---

## WORKING FEATURES CONFIRMED (Prompt 73)

### All Previous Features Still Working
- Overworld: decipher, teleport, mount, fly, dig, portal entry
- Dungeon: combat, chest interaction, sluice gate, door walk-through, room transitions, ejection
- Multi-map sessions with AutoStartNextMap
- Roulette/Guaranteed Portal maps (Canal of Uznair)
- Auto Discard (gated by bot enabled)
- Summon Chocobo (timer, greens count, stance)

### New Features Added
- MapLocationDatabase (records and reuses real dig positions)
- Teleport within zone (>200y from flag)
- Y+50 altitude boost (fallback when no DB entry)
- Verbose aetheryte selection logging (diagnostic)

---

## Aetheryte Selection Bug (Prompt 74+)

### Symptom
- Bot teleports to Namai instead of House of the Fierce (closer aetheryte) in Yanxia
- Both aetherytes show NO_POS  Level sheet returns null, MapMarker DataKey matching fails
- Falls back to cheapest aetheryte (Namai at 100g = same as HotF, picks first)

### Root Cause Analysis
1. **Level sheet**: `aetheryte.Level` collection either empty or `ValueNullable` returns null
   - The `catch {}` swallowed all exceptions silently  no diagnostics
   - Fixed: added exception logging + direct Level sheet lookup by RowId as fallback
2. **MapMarker**: `DataKey.RowId` does NOT match Aetheryte RowId
   - Original code matched `DataKey.RowId` against candidate aetheryte IDs (107, 108)
   - This produced 0 matches  DataKey likely references PlaceName or another table
   - Fixed: now tries matching against both AetheryteId AND PlaceName.RowId
3. **Assignment bug**: Even if markers found, closest-to-target marker was assigned to first candidate
   - If Namai was first, it got House of Fierce's position (closer), making it appear closest
   - Fixed: proper ID-based matching first, sequential fallback only if IDs don't match

### Fixes Applied
- **Level**: Added verbose per-entry logging, direct `levelSheet.GetRow(rowId)` fallback
- **MapMarker**: DataType 3/4 filtering, PlaceName.RowId matching, ID-based assignment
- **Diagnostics**: Logs first 10 MapMarker entries, all aetheryte markers, assignment details
- **Next run logs will reveal**: actual DataKey values, Level RowIds, marker counts

### Mount Failure After Teleport

### Symptom
- Mount command `/mount "Regalia Type-G"` fails 3 times in ~5s after teleport arrival
- State goes to Idle with "Mount failed - please restart"

### Root Cause
- First mount attempt was 0.5s after teleport arrival  too fast
- Character likely still in post-teleport animation/loading state
- Only 3 attempts with 2s spacing = gave up too quickly

### Fix Applied
- Added **3-second grace period** after entering Mounting state before first attempt
- Increased retries from **3 to 5** with **3s spacing** (total window: 3s grace + 15s retries = 18s)
- Added **condition flag logging** per attempt (Casting, Occupied, BetweenAreas, Mounting71)
- Next run logs will show exactly which condition is blocking the mount

## AetherytePositionDatabase - Permanent Fix for Aetheryte Selection (2026-03-06)

### Problem
- Level sheet RowIds for aetherytes are composite/packed IDs (e.g., 6905594) that throw ArgumentOutOfRangeException
- MapMarker data has DataType=0, DataKey=0 for all markers in many maps (e.g., Yanxia map 354)
- Both fallback mechanisms completely fail, causing bot to select cheapest aetheryte (wrong one)
- Example: Bot teleported to Namai while already AT Namai instead of House of the Fierce

### Solution: AetherytePositionDatabase
- New service: `AetherytePositionDatabase.cs` stores aetheryte world XYZ in `AetherytePositions.json`
- **Passive recording**: Player position recorded on every teleport arrival in `TickTeleporting`
- **Active recording**: 'Cycle Missing Aetherytes' mode teleports to each unrecorded aetheryte
- **Lookup priority in FindNearestAetheryte**:
  1. Level sheet (ValueNullable + direct GetRow) - usually fails
  2. AetherytePositionDatabase stored positions - **permanent fix**
  3. MapMarker fallback - usually fails
  4. MapLocationDatabase name override
  5. Cheapest cost fallback

### Cycling Modes Added
- **CyclingAetherytes**: Teleport to each unlocked aetheryte missing stored position, record on arrival
- **CyclingMapLocations**: Visit map flag locations missing RealXYZ, teleport+fly+record

### UI Additions
- Aetheryte count: X/Y positions stored (Z missing) in Map Completion section
- 'Cycle Missing Aetherytes' and 'Cycle Missing XYZ' buttons
- 'Open Data Folder' button next to community DB sharing message
- Stop Cycling button shown during active cycling

### Files Created/Modified
- NEW: `Services/AetherytePositionDatabase.cs` - JSON storage, passive+active recording, missing list
- MOD: `Plugin.cs` - Wire AetherytePositionDatabase, populate aetheryte name on detection
- MOD: `Services/NavigationService.cs` - Method 1c: check stored positions before MapMarker
- MOD: `Services/StateManager.cs` - Passive recording on arrival, cycling tick methods
- MOD: `Models/BotState.cs` - Added CyclingAetherytes, CyclingMapLocations states
- MOD: `Windows/MainWindow.cs` - Aetheryte stats, cycle buttons, open folder button

---

## Aetheryte Cycling Crash Fix (2026-03-07)

### Symptom
- Game client crashes after the first teleport during "Cycle Missing Aetherytes"
- Crash occurred immediately when the new zone started loading in
- Initially appeared to be related to residential territories (Estate Hall) but was NOT location-dependent

### Root Cause: Unsafe Game Memory Access During Zone Transitions
The `Tick()` method in `StateManager.cs` had NO `BetweenAreas` guard at the top. During zone loading (teleport in progress), the following unsafe operations ran:
1. `Plugin.ClientState.TerritoryType` — game state access during load
2. `_plugin.InventoryService.ScanForMaps()` — `InventoryManager.Instance()` unsafe pointer during load
3. `Plugin.ObjectTable.LocalPlayer` — null/invalid during load
4. `Telepo.Instance()` — game struct invalid during load

The `TickCyclingAetherytes` method had `IsTeleporting()` check (which checks BetweenAreas) at Step 3, but the crash happened BEFORE reaching that step because `Tick()` itself called `ScanForMaps()` on territory change detection.

### What Was Tried (WRONG)
- Filtering residential territories from the cycling queue — this was a symptom fix, not root cause
- Adding `IsResidentialTerritory()` helper and filtering in `GetMissingAetherytes()`, `StartCyclingAetherytes()`, `TickCyclingAetherytes()` — all reverted

### Fix Applied (Defense in Depth)
Three layers of BetweenAreas guards:
1. **`Tick()` top-level**: `if (BetweenAreas || BetweenAreas51) { stateStartTime = DateTime.Now; return; }` — blocks ALL game memory access during zone transitions
2. **`TickCyclingAetherytes()`**: Same BetweenAreas guard as extra safety
3. **`GetMissingAetherytes()` / `GetTotalUnlockedCount()`**: BetweenAreas guard before `Telepo.Instance()` access

### Key Learning
- **NEVER access game memory (ObjectTable, InventoryManager, Telepo, ClientState) during BetweenAreas zone transitions** — this causes game client crashes
- The same `BetweenAreas` guard pattern is already used in `TickInDungeon`, `TickDungeonLooting`, `TickDungeonProgressing` — but was missing from the top-level `Tick()` method
- Residential territory filtering was a red herring — the crash had nothing to do with WHERE the teleport went, only WHEN game memory was accessed
- Always look for the root cause (unsafe memory access) rather than symptoms (specific destination)

---

## Embedded Default Aetheryte Positions (2026-03-07)

### What Was Done
- 109 aetheryte positions from the cycling process embedded as community defaults in `DefaultAetheryteData.cs`
- `AetherytePositionDatabase.Load()` now seeds from defaults first, then overlays user-recorded data on top
- User data always takes priority over defaults
- New users start with all 109 positions without needing to cycle

### How It Works
1. `DefaultAetheryteData.GetDefaults()` returns a static dictionary of 109 known aetheryte positions
2. `Load()` populates `_positions` from defaults, then overlays any user JSON data
3. `RecordPosition()` writes corrections organically as players teleport
4. Missing aetherytes can still be found via "Cycle Missing Aetherytes" button

### Note on Data Quality
Some default XYZ coordinates may be approximate (recorded during fast cycling before zone fully loaded). They get corrected automatically when a user teleports to that aetheryte normally (passive recording on arrival).

### Missing Aetheryte
- 109 of 110 unlocked aetherytes were recorded. The 1 missing aetheryte can be identified by clicking "Cycle Missing Aetherytes" — it will only try the remaining one
- Notable gap in IDs: 135 is missing from the 132-148 (Shadowbringers) range

---

## Aetheryte Selection Optimization (2026-03-07)

### Problem Solved
The bot was sometimes picking suboptimal aetherytes because:
1. Always used XZ-only distance comparison, ignoring Y coordinate differences
2. Didn't check if the player was already close enough to skip teleporting
3. No distinction between when community RealXYZ data was available vs when only flag position was known

### Fix Applied
- **`FindNearestAetheryte()`** now uses full XYZ distance when BOTH destination has community RealXYZ AND aetheryte has recorded position from AetherytePositionDatabase
- **XZ-only comparison** when either destination or aetheryte lacks real Y coordinate
- **Player distance check** in `TickDetectingLocation()` - compares player distance to destination vs aetheryte distance
- **Skip teleport** when player is closer than aetheryte (regardless of distance threshold)

### Implementation Details
```csharp
// NavigationService.cs - new signature
public uint FindNearestAetheryte(uint territoryId, Vector3 targetPosition, 
    out double bestAetheryteDistance, out bool usedXyzComparison)

// StateManager.cs - player vs aetheryte comparison
if (bestAethDist < playerDist)
{
    // Aetheryte is closer - teleport
}
else
{
    // Player is closer - mount up, no teleport needed
}
```

### Distance Comparison Rules
- **XYZ distance**: Used when community RealXYZ exists for destination AND aetheryte position is recorded
- **XZ distance**: Used when either lacks real Y coordinate (fallback for incomplete data)
- **Player distance**: Uses same comparison mode as aetheryte (XYZ or XZ) for fair comparison

### Result
More intelligent aetheryte selection that respects actual 3D distances when data is available, and skips unnecessary teleports when the player is already positioned optimally.
