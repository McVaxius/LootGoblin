# Changelog

All notable changes to LootGoblin will be documented in this file.

## [0.0.1.49] - 2026-03-04

### Fixed - Yes/No dialog handling and timeouts
- **Generic ClickYesIfVisible()** - New method using FrenRider's AddonMaster.SelectYesno pattern, handles ALL Yes/No dialogs
- **Chest "Open the treasure coffer?" dialog** - Now auto-clicks Yes after chest interaction
- **Portal "Journey through the portal?" dialog** - Now auto-clicks Yes during portal search
- **Map decipher confirmation safety net** - TickOpeningMap also polls ClickYesIfVisible() in case async callback fails
- **OpeningMap timeout** - Increased from 15s to 30s (callback chain needs time)
- **OpeningChest timeout** - Increased from 60s to 120s (dialog + combat needs time)
- **YesAlready** - Confirmed NOT in our codebase; separate Dalamud plugin on user's system

## [0.0.1.48] - 2026-03-04

### Fixed - Multiple issues from testing feedback
- **Fixed re-pathfinding** - Was re-navigating every 30s unnecessarily; now only re-pathfinds if stuck (10+ seconds without moving 5+ yalms)
- **Send /bmrai on** - Now sent after landing, after dismount, and after chest targeting to enable combat AI
- **Portal interaction** - Now retries every 2s for 10s using FrenRider-style targeting, with /target fallback
- **Fixed FindNearestPortal** - Was using complex ObjectKind filter; now uses simple exact name match like chest
- **Reset retries on Stop** - RetryCount resets to 0 when Stop is clicked or bot completes
- **Added RESET button** - Next to Settings and Krangle; resets all plugin states (not saved configs)

## [0.0.1.47] - 2026-03-04

### Fixed - Chest detection using FrenRider pattern
- **Replaced complex ObjectTable scan** - Was filtering by ObjectKind + partial name matches
- **Now uses FrenRider-style** - Simple exact name match: `ObjectTable.FirstOrDefault(obj => obj.Name.ToString() == "Treasure Coffer")`
- **Added fallback targeting** - If ObjectTable fails, tries `/target "Treasure Coffer"` every 10s
- **Simplified chest detection** - Much more reliable, matches proven FrenRider targeting method

## [0.0.1.46] - 2026-03-04

### Fixed - Combat detection was blocking chest search
- **Fixed combat detection order** - Was checking combat at TOP of TickOpeningChest, preventing chest search entirely
- **Now searches chest first** - Only checks for combat if no chest found OR after chest interaction
- **Added debug logs** - Shows when combat detected without chest vs after chest interaction

## [0.0.1.45] - 2026-03-04

### Fixed - 4 critical bugs found via FrenRider code review
- **Fixed mount command** - Was `/gaction "mount"` (invalid), now `/mount "mount"` matching FrenRider pattern
- **Fixed landing state transition** - Async landing paths went to InCombat, skipping chest entirely
- **Fixed chest interaction** - `stateActionIssued` was reused for nav AND interact; added `chestInteracted` flag
- **Fixed combat end timer** - Was using state entry time (broken); now tracks actual combat end time
- **Reduced log spam** - Chest search logs every 5s instead of every tick

## [0.0.1.44] - 2026-03-04

### Fixed - Mount selection and chest interaction debugging
- **Fixed mount selection** - Now uses selected mount instead of hardcoded "Mount Roulette"
- **Increased chest timeout** - From 15s to 60s to prevent premature completion
- **Added extensive debug logging** - Chest detection, interaction attempts, state transitions
- **Detailed interaction logging** - Shows TargetSystem calls and object addresses
- **Timeout debugging** - Logs when states timeout with elapsed time

## [0.0.1.43] - 2026-03-04

### Fixed - Simplified chest interaction flow
- **Simple chest targeting** - Just find chest and interact, nothing complex
- **First interaction** - Triggers combat (no async delays, no state transitions)
- **Second interaction** - After combat ends, interact with chest again
- **Clear flow** - Chest → Combat → Chest → Portal/Complete
- **No remounting** - Bot stays focused on chest interaction

## [0.0.1.42] - 2026-03-04

### Fixed - Chest interaction and combat flow
- **Fixed chest targeting** - Now properly finds and interacts with "Treasure Coffer"
- **Correct flow sequence** - Land → /gaction dig → find chest → interact → combat → post-combat check
- **No more remounting** - Bot stays dismounted during chest interaction
- **Combat detection** - Properly waits for combat to start after chest interaction
- **Post-combat handling** - Rechecks chest for portal after combat ends

## [0.0.1.41] - 2026-03-04

### Added - Mount selector and navigation fixes
- **Mount selector UI** - Searchable dropdown with all game mounts
- **Mount configuration** - Save selected mount in settings
- **Fixed re-pathfinding** - Only re-nav if >5y from target after 10s
- **Navigation stability** - Prevents infinite re-pathfinding loops
- **Mount data loading** - Loads mount names from game data like FrenRider

## [0.0.1.40] - 2026-03-04

### Added - MountService and improved landing
- **MountService** - New service for mount management (like FrenRider)
- **Proper landing** - Uses MountService.ForceLand() instead of manual toggling
- **Mount cooldowns** - Prevents rapid mount/dismount spam
- **Mount state tracking** - Better detection of mounted/flying states
- **Fixed remount issue** - Should no longer remount after landing

## [0.0.1.39] - 2026-03-04

### Fixed - Proper landing with /mount command
- **Removed /vnav land** - Not a real command
- **Mount toggle** - Uses /mount once per second to trigger landing
- **Rate limited** - Maximum 1 attempt per second, 5 attempts total
- **Async landing** - Proper async/await pattern for landing attempts

## [0.0.1.38] - 2026-03-04

### Fixed - Landing at treasure location
- **X,Z proximity check** - Lands when within 5 yalms of target coordinates
- **Force landing** - Uses /vnav land command when close enough
- **Landing sequence** - Land → wait 2s → dismount → /gaction dig
- **Fixes flying too high** - No more infinite climbing at location

## [0.0.1.37] - 2026-03-04

### Fixed - Proper portal interaction
- **Teleportation Portal** - Now looks for correct portal name
- **Portal interaction** - Interacts with portal first to get popup
- **Journey confirmation** - Clicks yes to "Journey through the portal?"
- **Two-step process** - Interact → wait for popup → confirm

## [0.0.1.36] - 2026-03-04

### Fixed - Correct map content flow order
- **Fixed flow** - Dismount → /gaction dig → combat → /bmrai on → wait → check chest → portal detection
- **Combat handling** - Enables BMR AI when combat starts
- **Post-combat wait** - Waits 6 seconds after combat ends
- **Portal detection** - Checks for portal after chest interaction
- **Dungeon state** - Waits in dungeon until territory changes
- **Proper sequencing** - Now follows correct treasure map flow

## [0.0.1.35] - 2026-03-04

### Added - Full map content automation
- **/gaction dig** - Triggers map content when at flag location
- **Auto dismount** - Dismounts when arriving at treasure location
- **Re-navigation** - Re-navigates every 30s if stuck during flight
- **8-second wait** - Waits 8s before chest interaction (if not in combat)
- **Double-yes confirmation** - Sends two /click yes commands for chest loot
- **Chest interaction** - Finds and interacts with treasure coffer

## [0.0.1.34] - 2026-03-04

### Fixed - Use inventory order (no sorting) for correct index
- **Removed sorting** - Don't sort maps in StateManager selection
- **Inventory order** - Use natural inventory scan order for menu index
- **Correct index** - Now matches what user sees in /lg window
- **Simple solution** - No complex sorting, just use inventory order

## [0.0.1.33] - 2026-03-03

### Fixed - Add 1 for 1-based indexing in callback
- **1-based indexing** - Callback parameter uses 1-based, not 0-based
- **Correct offset** - Send `mapIndex + 1` to select the right map
- **Verified working** - Tested and confirmed correct map selection

## [0.0.1.32] - 2026-03-03

### Fixed - Sort maps by item ID to match game menu order
- **Item ID sorting** - Menu sorts maps by item ID ascending, not inventory order
- **Correct index** - Find target map position in sorted list
- **Log all maps** - Show sorted map IDs for debugging
- **Fixes wrong selection** - Previous versions used wrong sort order

## [0.0.1.31] - 2026-03-03

### Fixed - Simplified to always select index 0 (first map in sorted menu)
- **Skip menu reading** - AddonMaster entries not populating in time
- **Always index 0** - Game sorts maps, target is always first (checked)
- **Simpler approach** - Just fire callback with index 0 directly
- **Removes FindMapIndexInMenu** - Complex menu parsing not needed

## [0.0.1.30] - 2026-03-03

### Fixed - Wait for addon to populate with retry loop
- **Retry loop** - Poll addon up to 10 times (50ms intervals) until entries appear
- **Entry count check** - Only proceed when EntryCount > 0
- **Better logging** - Report how long it took to get entries
- **Fixes 0 entries** - Previous version checked too early before addon populated

## [0.0.1.29] - 2026-03-03

### Fixed - Use AddonMaster to read menu entry text and match by item name
- **AddonMaster.Entries** - Read actual menu entries instead of raw AtkValues
- **Text matching** - Match menu entry text against item name from game data
- **Correct approach** - AtkValues only contain UI data (strings/icons), not item IDs
- **Item lookup** - Use DataManager to get item name for target map ID

## [0.0.1.28] - 2026-03-03

### Debug - Log all AtkValues to understand SelectIconString format
- **Full debug** - Log every AtkValue with type and value
- **Pattern detection** - Identify the actual data structure used by the addon
- **Find item IDs** - Search all values for the target map ID
- **Adaptive parsing** - Calculate menu index based on discovered pattern

## [0.0.1.27] - 2026-03-03

### Fixed - Read menu order directly from SelectIconString addon
- **Critical fix** - Menu order does NOT match inventory order
- **Read addon** - Parse SelectIconString AtkValues to find actual menu positions
- **Correct index** - Now finds the map's true position in the sorted menu
- **Item IDs** - Reads item IDs from addon values instead of guessing from inventory

## [0.0.1.26] - 2026-03-03

### Fixed - Revert to 0-based indexing for SelectIconString callback
- **Index fix** - Removed the `+ 1` offset, callback uses 0-based indexing
- **Correct selection** - Now properly selects the first map (index 0) instead of second
- **Menu order** - FindMapIndexInMenu correctly identifies map position in menu

## [0.0.1.25] - 2026-03-03

### Fixed - Use 1-based indexing for SelectIconString callback
- **Index fix** - Changed from `mapIndex` to `mapIndex + 1` for callback
- **1-based UI** - Game UI uses 1-based indexing, not 0-based
- **Correct map** - Now selects the correct map from the decipher menu
- **Better logging** - Shows both 0-based and 1-based index values

## [0.0.1.24] - 2026-03-03

### Fixed - Use raw AtkUnitBase.FireCallback with manual AtkValue construction
- **Raw callback** - Direct `AtkUnitBase.FireCallback(2, atkValues)` call
- **Manual AtkValue** - Construct AtkValue array with Bool and Int types
- **Matches /callback** - Same pattern as `/callback SelectIconString true 4`
- **Detailed logging** - Stack traces and inner exceptions for debugging
- **2 parameters** - Bool (true) and Int (mapIndex) as shown in user's callback examples

## [0.0.1.23] - 2026-03-03

### Fixed - Use direct Callback.Fire for addon interaction
- **Direct callback** - Try `Callback.Fire(&addon->AtkUnitBase, true, (uint)mapIndex)` first
- **SND pattern** - This is how Something Need Doing actually does callbacks
- **Fallback method** - AddonMaster as backup if direct callback fails
- **Better error handling** - Separate error messages for each approach
- **Proper casting** - Convert mapIndex to uint for Callback.Fire

## [0.0.1.22] - 2026-03-03

### Fixed - Stop button now fully disables bot and null check for callbacks
- **Stop button fix** - Now acts like `/lg off`, sets Enabled=false and saves config
- **Null reference fix** - Added null check for AddonMaster.Entries[mapIndex]
- **Callback safety** - Verifies entry exists before calling Select()
- **Full disable** - Stop button no longer leaves bot in "enabled" state
- **Proper cleanup** - Bot fully stops and won't continue running

## [0.0.1.21] - 2026-03-03

### Fixed - Added extensive logging and slower timing for debugging
- **Map scan throttling** - Only scans inventory every 3 seconds to reduce log spam
- **Extended delays** - Increased menu wait from 300ms to 500ms for addon readiness
- **Detailed logging** - Added [CALLBACK] and [FIND] tags throughout the process
- **Step-by-step tracking** - Logs each phase of addon interaction
- **Addons found** - Logs addon addresses and visibility status
- **Entry count** - Logs how many entries are in the SelectIconString menu
- **Index tracking** - Shows which map index is being selected

## [0.0.1.20] - 2026-03-03

### Fixed - Use async/await pattern for addon interactions like SND
- **Async callbacks** - Changed to async/await pattern like Something Need Doing
- **Map selection** - Uses `await Task.Delay()` before addon interactions
- **Confirmation** - Async delay before clicking Yes button
- **Navigation stop** - Added `/vnav clearflag` to prevent old flag pathing
- **SND pattern** - Follows SND's async/await pattern, not yield()
- **Proper timing** - Waits for addons to be ready before interaction

## [0.0.1.19] - 2026-03-03

### Fixed - Install ECommons.Callback hook for addon interactions
- **Callback hook** - Added `Callback.InstallHook()` in Plugin constructor
- **Proper disposal** - Added `Callback.UninstallHook()` in Plugin.Dispose
- **Fixed pointer** - Use `&addon->AtkUnitBase` instead of cast
- **Addon interactions** - Callback.Fire now properly initialized
- **Required for callbacks** - ECommons needs hook installed for addon callbacks to work

## [0.0.1.18] - 2026-03-03

### Fixed - Use ECommons.Callback.Fire instead of /callback
- **Callback method** - Replaced `/callback` commands with `ECommons.Automation.Callback.Fire`
- **Map selection** - `Callback.Fire((AtkUnitBase*)addon, true, mapIndex)`
- **Confirmation** - `Callback.Fire((AtkUnitBase*)addon, true, 0)`
- **Proper API** - Uses ECommons internal callback system like Jaksuhn's SND
- **Added using** - `using ECommons.Automation;` for Callback access

## [0.0.1.17] - 2026-03-03

### Fixed - AddonMaster null reference with fallback
- **Map selection** - Added null checks and error handling for AddonMaster
- **Fallback method** - If AddonMaster fails, uses `/callback SelectIconString true [index]`
- **Confirmation fallback** - If AddonMaster.SelectYesno fails, uses `/callback SelectYesno true 0`
- **Robust error handling** - Won't crash if addon not ready, tries both methods
- **Updated SND repo** - Added memory: New SND is at https://github.com/Jaksuhn/SomethingNeedDoing

## [0.0.1.16] - 2026-03-03

### Fixed - Full auto map decipher with AddonMaster
- **Map selection** - Uses `AddonMaster.SelectIconString.Entries[index].Select()` 
- **Confirmation** - Uses `AddonMaster.SelectYesno.Yes()` for OK button
- **Complete automation** - Bot opens menu → selects correct map → confirms → continues
- Follows exact FrenRider pattern for addon interactions
- No more manual interaction required for map deciphering

## [0.0.1.15] - 2026-03-03

### Fixed - Add ECommons dependency for AddonMaster
- **Dependencies** - Added ECommons package for AddonMaster implementations
- **Map decipher** - Back to manual selection while investigating AddonMaster.SelectIconString API
- Previous v0.0.1.14 attempted AddonMaster but API methods need investigation
- Menu opens correctly with `/gaction decipher` using CommandHelper.SendCommand

## [0.0.1.14] - 2026-03-03

### Fixed - Version bump for testing
- Bumped version to ensure new release with auto map decipher functionality
- Previous v0.0.1.13 had same functionality but wasn't properly released

## [0.0.1.13] - 2026-03-03

### Fixed - Auto map selection and confirmation
- **Map decipher** - Now fully automated using `/callback SelectIconString true [index]`
- **Map selection** - Finds map index in menu and triggers callback automatically
- **Confirmation dialog** - Uses `/callback SelectYesno true 0` to click OK on "Decipher the [map name]?" dialog
- Complete automation: Bot opens menu → selects correct map → confirms → continues to DetectingLocation
- No more manual interaction required for map deciphering

## [0.0.1.12] - 2026-03-03

### Fixed - Map decipher menu opens
- **Map decipher** - `/gaction decipher` now properly opens the map selection menu using `CommandHelper.SendCommand`
- Follows FrenRider/HFH pattern: `UIModule.ProcessChatBoxEntry` for game commands
- Menu appears with all available treasure maps in inventory order
- TODO: Implement menu callback to auto-select correct map by index
- For now, user must manually select the map from the menu

## [0.0.1.11] - 2026-03-03

### Fixed - Critical Hotfix v5
- **Map decipher** - Use `CommandHelper.SendCommand` instead of `CommandManager.ProcessCommand`
- Follows FrenRider/HFH pattern: `UIModule.ProcessChatBoxEntry` for game commands
- v0.0.1.10 didn't actually send the command to the game client
- Now `/gaction decipher` should properly open the map selection menu

## [0.0.1.10] - 2026-03-03

### Fixed - Critical Hotfix v4
- **Duplicate Start buttons** - Removed duplicate "Enable Bot" button from Controls section, keep only "Start Bot" in Bot Control
- **Map decipher** - Switch to `/gaction decipher` command which opens the map selection menu
- Previous attempts failed:
  - v0.0.1.9: Context menu approach returned success but didn't actually decipher
  - v0.0.1.8: AgentInventoryContext.UseItem(itemId) returned success but no action
  - v0.0.1.7: /item command doesn't exist
  - v0.0.1.6: ActionManager.UseAction doesn't support treasure maps
- Current implementation opens decipher menu; TODO: Add menu callback to auto-select correct map

## [0.0.1.9] - 2026-03-03

### Fixed - Critical Hotfix v3
- **Map decipher** - Proper context menu approach: `OpenForItemSlot` + `UseItem` with container/slot parameters
- v0.0.1.8 returned success but didn't actually use the map in-game
- Now finds item in inventory, opens context menu for that slot, then triggers Use action
- This simulates the right-click → Use interaction that treasure maps require

## [0.0.1.8] - 2026-03-03

### Fixed - Critical Hotfix v2
- **Map decipher** - Replaced with `AgentInventoryContext.UseItem(itemId)` API; this is the correct way to use items from inventory in FFXIV
- Previous attempts failed:
  - v0.0.1.6: `ActionManager.UseAction` doesn't support treasure maps (always returns "not ready")
  - v0.0.1.7: `/item "Map Name"` command doesn't exist in FFXIV
- `AgentInventoryContext.UseItem` automatically searches all inventory containers and triggers the item use action

### Notes
- `AgentInventoryContext.UseItem(itemId, InventoryType.Invalid, 0)` is the proper API for using items
- Returns `long` result code (>= 0 = success)
- This is how the game internally handles right-click → Use on inventory items

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
