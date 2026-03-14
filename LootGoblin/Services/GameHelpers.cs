using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.Automation;
using LootGoblin.Services;

namespace LootGoblin.Services;

/// <summary>
/// Static unsafe helpers for game state queries and item/object interaction.
/// Patterns adapted from FrenRider's GameHelpers.cs.
/// </summary>
public static class GameHelpers
{
    // Static fields for delayed callback handling
    private static int _pendingMenuIndex = -1;
    private static DateTime _callbackStartTime = DateTime.MinValue;
    private static bool _waitingForSecondCallback = false;
    private static uint _pendingItemId = 0;
    private static DateTime _mapLookupStartTime = DateTime.MinValue;
    private static bool _waitingForMapLookup = false;
    /// <summary>
    /// Check if we need to fire the delayed second callback for SelectIconString.
    /// Call this method regularly from the main tick loop.
    /// </summary>
    public static void UpdateDelayedCallbacks()
    {
        // Handle map lookup delay
        if (_waitingForMapLookup && _pendingItemId > 0)
        {
            var elapsed = (DateTime.Now - _mapLookupStartTime).TotalSeconds;
            if (elapsed >= 0.5) // 500ms delay for map lookup
            {
                Plugin.Log.Information($"[MAP_LOOKUP] Looking for real menu index for map {_pendingItemId}...");
                var realMenuIndex = FindMapIndexInMenu(_pendingItemId);
                if (realMenuIndex >= 0)
                {
                    // Use 0-based index directly (no conversion needed)
                    Plugin.Log.Information($"[MAP_LOOKUP] Found real menu index {realMenuIndex}, firing first callback");
                    FireAddonCallback("SelectIconString", true, -2);
                    // Store the menu index for the delayed second callback
                    _pendingMenuIndex = realMenuIndex;
                    _callbackStartTime = DateTime.Now;
                    _waitingForSecondCallback = true;
                }
                else
                {
                    Plugin.Log.Error($"[MAP_LOOKUP] Could not find map in menu, retrying with longer delay...");
                    // Retry with longer delay for SelectIconString to populate
                    _mapLookupStartTime = DateTime.Now; // Reset start time for retry
                    // Don't change state, will retry after additional time
                }
                
                // Reset map lookup state
                _pendingItemId = 0;
                _mapLookupStartTime = DateTime.MinValue;
                _waitingForMapLookup = false;
            }
        }
        
        // Handle second callback delay
        if (_waitingForSecondCallback && _pendingMenuIndex >= 0)
        {
            var elapsed = (DateTime.Now - _callbackStartTime).TotalSeconds;
            if (elapsed >= 0.5) // 500ms delay
            {
                Plugin.Log.Information($"[DELAYED] Firing second SelectIconString callback with index {_pendingMenuIndex}");
                FireAddonCallback("SelectIconString", true, _pendingMenuIndex);
                
                // Reset state
                _pendingMenuIndex = -1;
                _callbackStartTime = DateTime.MinValue;
                _waitingForSecondCallback = false;
            }
        }
    }

    /// <summary>
    /// Use an item from inventory by item ID.
    /// For treasure maps: uses /gaction decipher then selects the map from the menu.
    /// Returns false if player is busy, item not found, or action fails.
    /// </summary>
    public static unsafe bool UseItem(uint itemId, InventoryService inventoryService)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;
            if (player.IsCasting) return false;

            if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[ConditionFlag.Occupied33] ||
                Plugin.Condition[ConditionFlag.Occupied39])
                return false;

            var im = InventoryManager.Instance();
            if (im == null)
            {
                Plugin.Log.Warning($"UseItem({itemId}): InventoryManager is null");
                return false;
            }

            var count = im->GetInventoryItemCount(itemId);
            if (count <= 0)
            {
                Plugin.Log.Warning($"UseItem({itemId}): Item not found in inventory");
                return false;
            }

            // Use /gaction decipher to open the map selection menu
            CommandHelper.SendCommand("/gaction decipher");
            Plugin.Log.Information($"UseItem({itemId}): Opened decipher menu for {count} maps");
            
            // Check if there's only one map type in inventory
            var allMaps = inventoryService.ScanForMaps();
            if (allMaps.Count == 1)
            {
                // Single map type - use AddonMaster.SelectIconString to click the first entry (index 0)
                Plugin.Log.Information($"UseItem({itemId}): Only 1 map type detected, using AddonMaster.SelectIconString");
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => {
                    Plugin.Log.Information($"UseItem({itemId}): Looking for SelectIconString addon...");
                    TriggerSelectIconStringClick();
                });
            }
            else
            {
                // Multiple map types - use the corrected approach: Find real menu index first, then callback
                Plugin.Log.Information($"UseItem({itemId}): {allMaps.Count} map types detected, using corrected menu index approach");
                
                // Start delayed map lookup using state-based timing
                _pendingItemId = itemId;
                _mapLookupStartTime = DateTime.Now;
                _waitingForMapLookup = true;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"UseItem({itemId}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Find the index of a specific map in the decipher menu by reading the SelectIconString addon.
    /// The menu order does NOT match inventory order - it's sorted by the game.
    /// </summary>
    public static unsafe int FindMapIndexInMenu(uint targetItemId)
    {
        Plugin.Log.Information($"[FIND] Looking for map ID {targetItemId} in SelectIconString addon");
        
        try
        {
            // Wait for the addon to populate with entries (may take a few frames)
            AddonSelectIconString* addon = null;
            int entryCount = 0;
            
            for (int attempt = 0; attempt < 10; attempt++)
            {
                System.Threading.Thread.Sleep(50);
                
                nint addonPtr = Plugin.GameGui.GetAddonByName("SelectIconString", 1);
                if (addonPtr == 0) continue;

                addon = (AddonSelectIconString*)addonPtr;
                if (!addon->AtkUnitBase.IsVisible) continue;

                var addonMaster = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
                entryCount = addonMaster.EntryCount;
                
                if (entryCount > 0)
                {
                    Plugin.Log.Information($"[FIND] Addon ready with {entryCount} entries after {(attempt + 1) * 50}ms");
                    break;
                }
            }

            if (addon == null || entryCount == 0)
            {
                Plugin.Log.Error($"[FIND] SelectIconString addon not ready after 500ms (entries: {entryCount})");
                return -1;
            }

            // AtkValues don't contain item IDs, only UI display data (strings and icon IDs)
            // We need to use AddonMaster to access the actual entries
            Plugin.Log.Information($"[FIND] Using AddonMaster.SelectIconString to read entries");
            
            var addonMaster2 = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
            entryCount = addonMaster2.EntryCount;
            Plugin.Log.Information($"[FIND] AddonMaster reports {entryCount} entries");

            // Each entry in AddonMaster has a Text property we can check
            // The text should contain the map name, which we can match against our target
            for (int i = 0; i < entryCount; i++)
            {
                var entry = addonMaster2.Entries[i];
                var text = entry.Text;
                Plugin.Log.Information($"[FIND] Entry[{i}]: Text='{text}'");
                
                // Get the item name for our target map
                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                var item = itemSheet?.GetRow(targetItemId);
                if (item != null)
                {
                    var targetItemName = item.Value.Name.ToString();
                    if (text.Contains(targetItemName))
                    {
                        Plugin.Log.Information($"[FIND] Found target map '{targetItemName}' at entry index {i}");
                        return i;
                    }
                }
            }

            Plugin.Log.Warning($"[FIND] Target map ID {targetItemId} not found in {entryCount} entries");
            return -1;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FIND] FindMapIndexInMenu failed: {ex.Message}\n{ex.StackTrace}");
            return -1;
        }
    }

    /// <summary>
    /// Check if an item ID is a treasure map.
    /// </summary>
    private static bool IsTreasureMap(uint itemId)
    {
        // Check against known treasure map data
        return LootGoblin.Models.TreasureMapData.KnownMaps.ContainsKey(itemId);
    }

    /// <summary>
    /// Use controller mode to activate single map: numpad2 then numpad0 twice.
    /// Used when there's only 1 map type in inventory for reliable activation.
    /// </summary>
    private static async void TriggerSelectIconStringClick()
    {
        Plugin.Log.Information($"[SELECTICON] Starting controller mode for single map");
        
        try
        {
            // Wait a bit for the addon to be ready
            Plugin.Log.Information($"[SELECTICON] Waiting 100ms for SelectIconString addon...");
            await System.Threading.Tasks.Task.Delay(100);
            Plugin.Log.Information($"[SELECTICON] Wait complete, using controller mode");

            // Controller mode sequence: numpad2, then numpad0 twice
            Plugin.Log.Information($"[SELECTICON] Pressing numpad2 (switch to controller mode)...");
            KeyPress(VirtualKey.NUMPAD2);
            
            await System.Threading.Tasks.Task.Delay(200);
            
            Plugin.Log.Information($"[SELECTICON] Pressing numpad0 (first time)...");
            KeyPress(VirtualKey.NUMPAD0);
            
            await System.Threading.Tasks.Task.Delay(200);
            
            Plugin.Log.Information($"[SELECTICON] Pressing numpad0 (second time)...");
            KeyPress(VirtualKey.NUMPAD0);
            
            Plugin.Log.Information("[SELECTICON] Controller mode sequence completed");
            
            // Wait for the confirmation dialog, then click OK
            Plugin.Log.Information("[SELECTICON] Waiting 500ms for confirmation dialog...");
            await System.Threading.Tasks.Task.Delay(500);
            Plugin.Log.Information("[SELECTICON] Triggering confirmation dialog callback");
            TriggerConfirmDialog();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[SELECTICON] Controller mode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Trigger the menu callback to select a map by index.
    /// Uses async/await pattern like SND for reliable addon interactions.
    /// </summary>
    private static async void TriggerMapDecipherCallback(int mapIndex)
    {
        Plugin.Log.Information($"[CALLBACK] Starting map decipher callback for index {mapIndex}");
        
        try
        {
            // Wait a bit for the addon to be ready
            Plugin.Log.Information($"[CALLBACK] Waiting 100ms for SelectIconString addon...");
            await System.Threading.Tasks.Task.Delay(100);
            Plugin.Log.Information($"[CALLBACK] Wait complete, triggering unsafe callback");

            // Trigger the actual callback
            TriggerMapDecipherCallbackUnsafe(mapIndex);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[CALLBACK] TriggerMapDecipherCallback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Unsafe part of the map decipher callback.
    /// </summary>
    private static unsafe void TriggerMapDecipherCallbackUnsafe(int mapIndex)
    {
        Plugin.Log.Information($"[CALLBACK] Looking for SelectIconString addon...");
        
        // Find the SelectIconString addon
        nint addonPtr = Plugin.GameGui.GetAddonByName("SelectIconString", 1);
        if (addonPtr == 0)
        {
            Plugin.Log.Error("[CALLBACK] Could not find SelectIconString addon");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Found SelectIconString addon at 0x{addonPtr:X}");

        var addon = (AddonSelectIconString*)addonPtr;
        if (!addon->AtkUnitBase.IsVisible)
        {
            Plugin.Log.Error("[CALLBACK] SelectIconString addon is not visible");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Addon is visible, creating AddonMaster...");

        // Both Callback.Fire and AddonMaster are failing with null reference
        // Let's get detailed error info and try raw AtkUnitBase callback
        Plugin.Log.Information($"[CALLBACK] Addon AtkValuesCount={addon->AtkUnitBase.AtkValuesCount}");
        Plugin.Log.Information($"[CALLBACK] Attempting raw callback with 2 params: true, {mapIndex}");
        
        try
        {
            // The callback uses 0-based indexing
            // First parameter: bool (true = confirm selection)
            // Second parameter: int (0-based index of the item to select)
            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool;
            atkValues[0].Byte = 1; // true = confirm selection
            atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[1].Int = mapIndex; // 0-based index
            
            Plugin.Log.Information($"[CALLBACK] Calling FireCallback with Bool=true, Int={mapIndex} (0-based)");
            addon->AtkUnitBase.FireCallback(2, atkValues);
            Plugin.Log.Information($"[CALLBACK] FireCallback completed - selected index {mapIndex}");

            // Wait for the confirmation dialog, then click OK
            Plugin.Log.Information($"[CALLBACK] Waiting 500ms for confirmation dialog...");
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => {
                Plugin.Log.Information($"[CALLBACK] Triggering confirmation dialog callback");
                TriggerConfirmDialog();
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[CALLBACK] Raw callback failed: {ex.Message}");
            Plugin.Log.Error($"[CALLBACK] Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Plugin.Log.Error($"[CALLBACK] Inner exception: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Click OK on the "Decipher the [map name]?" confirmation dialog.
    /// Uses async/await pattern like SND for reliable addon interactions.
    /// </summary>
    private static async void TriggerConfirmDialog()
    {
        Plugin.Log.Information($"[CALLBACK] Starting confirmation dialog callback");
        
        try
        {
            // Wait a bit for the addon to be ready
            Plugin.Log.Information($"[CALLBACK] Waiting 100ms for SelectYesno addon...");
            await System.Threading.Tasks.Task.Delay(100);
            Plugin.Log.Information($"[CALLBACK] Wait complete, triggering unsafe confirmation");

            // Trigger the actual callback
            TriggerConfirmDialogUnsafe();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[CALLBACK] TriggerConfirmDialog failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Unsafe part of the confirmation dialog callback.
    /// </summary>
    private static unsafe void TriggerConfirmDialogUnsafe()
    {
        Plugin.Log.Information($"[CALLBACK] Looking for SelectYesno addon...");
        
        // Find the SelectYesno addon
        nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
        if (addonPtr == 0)
        {
            Plugin.Log.Error("[CALLBACK] Could not find SelectYesno addon");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Found SelectYesno addon at 0x{addonPtr:X}");

        var addon = (AddonSelectYesno*)addonPtr;
        if (!addon->AtkUnitBase.IsVisible)
        {
            Plugin.Log.Error("[CALLBACK] SelectYesno addon is not visible");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Addon is visible, clicking Yes...");

        // Use AddonMaster to click Yes - same pattern as FrenRider
        new AddonMaster.SelectYesno(&addon->AtkUnitBase).Yes();
        Plugin.Log.Information("[CALLBACK] Successfully clicked Yes on decipher confirmation");
    }

    /// <summary>
    /// Generic SelectYesno handler - clicks Yes on any visible SelectYesno dialog.
    /// Uses exact same pattern as FrenRider's AcceptInvite.
    /// Call this from state ticks whenever we expect a Yes/No dialog.
    /// Returns true if a dialog was found and clicked.
    /// </summary>
    public static unsafe bool ClickYesIfVisible()
    {
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AddonSelectYesno*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
                return false;

            new AddonMaster.SelectYesno(&addon->AtkUnitBase).Yes();
            Plugin.Log.Information("[YES/NO] Clicked Yes on SelectYesno dialog");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[YES/NO] ClickYesIfVisible failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        try
        {
            Plugin.Log.Information($"[INTERACT] Starting interaction with {obj.Name.TextValue} (Address: {obj.Address:X})");
            
            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                Plugin.Log.Error("[INTERACT] TargetSystem.Instance() returned null");
                return false;
            }

            var gameObjPtr = (GameObject*)obj.Address;
            if (gameObjPtr == null)
            {
                Plugin.Log.Error($"[INTERACT] Failed to cast GameObject* from address {obj.Address:X}");
                return false;
            }

            Plugin.Log.Information($"[INTERACT] Calling TargetSystem.InteractWithObject for {obj.Name.TextValue}");
            ts->InteractWithObject(gameObjPtr, true);
            Plugin.Log.Information($"[INTERACT] InteractWithObject called successfully for {obj.Name.TextValue} at {obj.Position}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[INTERACT] InteractWithObject failed: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Check if the player is available (logged in, not casting, not occupied, not in combat).
    /// </summary>
    public static bool IsPlayerAvailable()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        if (player.IsCasting) return false;
        if (Plugin.Condition[ConditionFlag.InCombat]) return false;
        if (Plugin.Condition[ConditionFlag.Casting]) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        return true;
    }

    // ─── Companion / Gysahl Greens ─────────────────────────────────────────────

    public const uint GysahlGreensItemId = 4868;

    /// <summary>
    /// Get the count of an item in the player's inventory (NQ + HQ).
    /// Ported from FrenRider GameHelpers.
    /// </summary>
    public static unsafe int GetInventoryItemCount(uint itemId)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            return im->GetInventoryItemCount(itemId) + im->GetInventoryItemCount(itemId, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"GetInventoryItemCount({itemId}) failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get companion (chocobo buddy) time remaining in seconds.
    /// Returns 0 if no companion is active.
    /// Ported from FrenRider GameHelpers.
    /// </summary>
    public static unsafe float GetBuddyTimeRemaining()
    {
        try
        {
            var uiState = UIState.Instance();
            if (uiState == null) return 0f;
            return uiState->Buddy.CompanionInfo.TimeLeft;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"GetBuddyTimeRemaining() failed: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// Check if the player is in a sanctuary (rest area where you can't summon companion).
    /// Ported from FrenRider GameHelpers.
    /// </summary>
    public static unsafe bool IsInSanctuary()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return true;
            var status = am->GetActionStatus(ActionType.GeneralAction, 9);
            return status != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Use Gysahl Greens to summon companion chocobo.
    /// Ported from FrenRider GameHelpers.UseItem pattern.
    /// </summary>
    public static unsafe bool UseGysahlGreens()
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;
            if (player.IsCasting) return false;

            if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[ConditionFlag.Occupied33] ||
                Plugin.Condition[ConditionFlag.Occupied39])
                return false;

            var am = ActionManager.Instance();
            if (am == null) return false;

            var status = am->GetActionStatus(ActionType.Item, GysahlGreensItemId);
            if (status != 0) return false;

            var result = am->UseAction(ActionType.Item, GysahlGreensItemId, extraParam: 65535);
            Plugin.Log.Information($"UseGysahlGreens: result={result}");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"UseGysahlGreens failed: {ex.Message}");
            return false;
        }
    }

    // ─── Keyboard Input (SND WindowsKeypress pattern) ─────────────────────────

    /// <summary>
    /// Hold a key down. Uses ECommons.Automation.WindowsKeypress.SendKeyHold.
    /// Same pattern as SND's /hold command.
    /// </summary>
    public static void KeyHold(VirtualKey key)
    {
        try
        {
            WindowsKeypress.SendKeyHold(key, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[KEY] KeyHold({key}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Release a held key. Uses ECommons.Automation.WindowsKeypress.SendKeyRelease.
    /// Same pattern as SND's /release command.
    /// </summary>
    public static void KeyRelease(VirtualKey key)
    {
        try
        {
            WindowsKeypress.SendKeyRelease(key, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[KEY] KeyRelease({key}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Press and release a key (single keypress). Uses ECommons.Automation.WindowsKeypress.
    /// Same pattern as SND's /keypress command.
    /// </summary>
    public static void KeyPress(VirtualKey key)
    {
        try
        {
            WindowsKeypress.SendKeypress(key, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[KEY] KeyPress({key}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Perform underwater descent by holding Ctrl+Space for 1 second then releasing.
    /// Equivalent to SND: /hold CONTROL, /hold SPACE, /wait 1, /release CONTROL, /release SPACE
    /// </summary>
    public static async Task PerformDescentAsync()
    {
        Plugin.Log.Information("[KEY] Performing Ctrl+Space descent...");
        KeyHold(VirtualKey.CONTROL);
        KeyHold(VirtualKey.SPACE);
        await Task.Delay(1000);
        KeyRelease(VirtualKey.CONTROL);
        KeyRelease(VirtualKey.SPACE);
        Plugin.Log.Information("[KEY] Descent key sequence complete.");
    }

    // ─── Map Flag ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Place a flag marker on the map at the given world coordinates.
    /// Uses AgentMap.SetFlagMapMarker with Vector3 overload (handles coord conversion internally).
    /// </summary>
    public static unsafe void SetMapFlag(uint territoryId, float worldX, float worldZ)
    {
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                Plugin.Log.Warning("[MapFlag] AgentMap is null");
                return;
            }

            var territorySheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            var territory = territorySheet?.GetRow(territoryId);
            if (territory == null)
            {
                Plugin.Log.Warning($"[MapFlag] Territory {territoryId} not found");
                return;
            }

            var mapId = territory.Value.Map.RowId;
            var worldPos = new Vector3(worldX, 0f, worldZ);
            agentMap->SetFlagMapMarker(territoryId, mapId, worldPos);
            Plugin.Log.Information($"[MapFlag] Set flag at territory {territoryId}, world ({worldX:F1}, {worldZ:F1})");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MapFlag] SetMapFlag failed: {ex.Message}");
        }
    }

    // ─── Currency ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Get the player's current Tomestones of Poetics count.
    /// Poetics currency ID = 28 (Tomestone type).
    /// </summary>
    public static unsafe int GetCurrentPoetics()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            return (int)im->GetTomestoneCount(0); // Index 0 = Poetics (first tomestone type)
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"GetCurrentPoetics failed: {ex.Message}");
            return 0;
        }
    }

    // ─── Lockon + Automove (short-range approach) ─────────────────────────────

    /// <summary>
    /// Lock on to current target and start automove towards it.
    /// For short-range chest/portal approach where navigation is overkill.
    /// </summary>
    public static void LockOnAndAutoMove()
    {
        CommandHelper.SendCommand("/lockon");
        CommandHelper.SendCommand("/automove");
    }

    /// <summary>
    /// Stop automove.
    /// </summary>
    public static void StopAutoMove()
    {
        CommandHelper.SendCommand("/automove");
    }

    // ─── NPC + Addon Helpers (Alexandrite Farming) ────────────────────────────

    /// <summary>
    /// Find an NPC by name in the object table.
    /// </summary>
    public static IGameObject? FindNpcByName(string name)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ||
                obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
            {
                if (obj.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return obj;
            }
        }
        return null;
    }

    /// <summary>
    /// Check if a UI addon is currently visible.
    /// </summary>
    public static unsafe bool IsAddonVisible(string addonName)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fire a callback on a named addon with variable arguments.
    /// Uses AtkUnitBase.FireCallback pattern from map decipher solution.
    /// </summary>
    public static unsafe void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
            {
                Plugin.Log.Warning($"[FireAddonCallback] Addon '{addonName}' not found or not visible");
                return;
            }

            var atkValues = new AtkValue[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                atkValues[i] = args[i] switch
                {
                    int intVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = intVal },
                    uint uintVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = uintVal },
                    bool boolVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte = (byte)(boolVal ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = Convert.ToInt32(args[i]) },
                };
            }

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }

            Plugin.Log.Information($"[FireAddonCallback] Fired callback on '{addonName}' with {args.Length} args");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FireAddonCallback] Failed for '{addonName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Close the currently focused addon by sending Escape key.
    /// </summary>
    public static void CloseCurrentAddon()
    {
        KeyPress(VirtualKey.ESCAPE);
    }

    /// <summary>
    /// Simple UseItem that uses the item directly via ActionManager (non-map items).
    /// For items like Mysterious Map that need direct use, not /gaction decipher.
    /// </summary>
    public static unsafe bool UseItem(uint itemId)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return false;

            var count = im->GetInventoryItemCount(itemId);
            if (count <= 0)
            {
                Plugin.Log.Warning($"UseItem({itemId}): Not in inventory");
                return false;
            }

            var am = ActionManager.Instance();
            if (am == null) return false;

            var result = am->UseAction(ActionType.Item, itemId, 0xE0000000, 65535, 0, 0, null);
            Plugin.Log.Information($"UseItem({itemId}): ActionManager result = {result}");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"UseItem({itemId}) failed: {ex.Message}");
            return false;
        }
    }
}
