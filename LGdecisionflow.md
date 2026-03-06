# LootGoblin Treasure Dungeon Decision Flow

## 1. Map Opening Phase
**State: `SelectingMap`**
- Scan inventory for enabled maps
- Select first enabled map (inventory order)
- Record initial map count for validation
- Transition to `OpeningMap`

**State: `OpeningMap`**
- Wait for player availability
- Use `GameHelpers.UseItem()` to decipher map
- Triggers controller mode (numpad2 + numpad0 twice) for single map //user comments: did we forget what happens with map selection mode? or is this assumed? --REVIEWED--
- Wait 4 seconds then transition to `DetectingLocation`

**State: `DetectingLocation`**
- **Map Count Validation**: Check if map count decreased
  - If count ≥ initial: Retry opening once, then error if still failed
  - If count < initial: Success, continue
- Read map flag location via GlobeTrotter IPC
- Find nearest aetheryte
- Set location data
- Transition to `Teleporting` or `Mounting` (if already in zone)

## 2. Travel Phase
**State: `Teleporting`**
- Teleport to nearest aetheryte
- Wait for teleport completion
- Transition to `Mounting`

**State: `Mounting`**
- Wait for player availability
- Mount up
- Transition to `Navigating`

**State: `Navigating`**
- Navigate to map flag location using vnavmesh
- Handle combat (transition to `InCombat`)
- Handle arrival at flag (transition to `OpeningChest`)

## 3. Dungeon Entry Phase
**State: `OpeningChest`**
- Execute `/gaction dig` at flag location
- Wait for treasure chests/portals to spawn
- Handle combat interruptions: If combat starts during dig, stay in combat state and retry dig after combat ends at flag location --REVIEWED--
- Transition to `InDungeon` when portal detected

**State: `InDungeon`**
- Wait for territory confirmation
- Handle loading screens
- Scan for loot objects (priority) → `DungeonLooting`
- Scan for progression objects → `DungeonProgressing`
- Handle combat → `DungeonCombat`

## 4. Dungeon Clearing Phase
**State: `DungeonCombat`**
- Wait for combat to end
- Check for loot post-combat
- Handle interrupted dig (retry at flag location): If combat ends at flag location with no chests, retry /gaction dig to complete dungeon entry --REVIEWED--
- Transition back to `InDungeon`

**State: `DungeonLooting`**
- Find unattempted loot objects: "Treasure Coffer" (verified), case-insensitive partial match for treasure/coffer/chest/sack (implemented using ToLowerInvariant()) --REVIEWED--
- **Target & Navigate**: Use vnavmesh first (10s timeout), then `/lockon` + `/automove` (15s timeout, ~3 attempts) --REVIEWED--
- **Interact**: Use `TargetSystem.InteractWithObject` every 1 second
- **Validation**: Check if chest disappears (success) or mark as attempted (failure/timeout): Add EntityId to attemptedCoffers HashSet to prevent re-targeting --REVIEWED--
- Transition to `InDungeon` when all loot attempted

**State: `DungeonProgressing`**
- Find progression objects: "RESEARCH_NEEDED" for locked doors (yaml shows object names not yet discovered), partial match for door/gate/levers --REVIEWED--
- **Priority Logic**: Skip doors if unopened loot exists within 50y
- **Target & Navigate**: Use `/lockon` + `/automove` (60s timeout)
- **Interact**: Use `TargetSystem.InteractWithObject`
- Handle stuck doors (exclude and try others): Set excludedDoorEntityId to prevent re-targeting stuck door, try other available doors --REVIEWED--
- Handle floor transitions (loading screens)
- Transition to `InDungeon` for next floor

## 5. Completion Phase
**State: `Completed`**
- Find exit portal (`Teleportation Portal`)
- **Targetability Check**: Verify portal is targetable before movement
- **Navigate**: Use `/lockon` + `/automove` to portal
- **Interact**: Use portal to exit dungeon
- Handle auto-start next map (if enabled)

## Key Decision Points
- **Map Opening Success**: Validated by inventory count decrease
- **Combat Handling**: Resets attempted coffers, interrupts current action
- **Object Priority**: Loot > Progression (except when unopened loot nearby)
- **Navigation Method**: `/lockon` + `/automove` for reliable targeting
- **Timeout Safety**: 15s for chests, 60s for doors, 30s for map operations
- **Retry Logic**: 1 retry for map opening, 3 attempts for chest navigation

## Error Recovery
- Map opening failures → Retry once, then error
- Navigation timeouts → Mark object as attempted, try next
- Combat interruptions → Resume after combat ends
- Portal targeting issues → Wait for targetability, don't move to ghost objects
