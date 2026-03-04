using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    /// <summary>
    /// Use an item from inventory by item ID.
    /// For treasure maps: uses /gaction decipher then selects the map from the menu.
    /// Returns false if player is busy, item not found, or action fails.
    /// </summary>
    public static unsafe bool UseItem(uint itemId)
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

            // Check if we have the map in inventory
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
            
            // Find the map's index in the menu and trigger the callback
            var mapIndex = FindMapIndexInMenu(itemId);
            if (mapIndex >= 0)
            {
                // Trigger the callback after a longer delay to ensure menu is ready
                Plugin.Log.Information($"UseItem({itemId}): Found map at index {mapIndex}, waiting 500ms for menu...");
                System.Threading.Tasks.Task.Delay(500).ContinueWith(async _ => {
                    Plugin.Log.Information($"UseItem({itemId}): Delay complete, triggering callback for map index {mapIndex}");
                    TriggerMapDecipherCallback(mapIndex);
                });
                Plugin.Log.Information($"UseItem({itemId}): Callback scheduled for map index {mapIndex}");
            }
            else
            {
                Plugin.Log.Warning($"UseItem({itemId}): Could not find map in menu");
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
    private static unsafe int FindMapIndexInMenu(uint targetItemId)
    {
        Plugin.Log.Information($"[FIND] Looking for map ID {targetItemId} in SelectIconString addon");
        
        try
        {
            // Wait a bit for the addon to populate
            System.Threading.Thread.Sleep(100);
            
            // Get the SelectIconString addon
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectIconString", 1);
            if (addonPtr == 0)
            {
                Plugin.Log.Error("[FIND] SelectIconString addon not found");
                return -1;
            }

            var addon = (AddonSelectIconString*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
            {
                Plugin.Log.Error("[FIND] SelectIconString addon not visible");
                return -1;
            }

            // AtkValues don't contain item IDs, only UI display data (strings and icon IDs)
            // We need to use AddonMaster to access the actual entries
            Plugin.Log.Information($"[FIND] Using AddonMaster.SelectIconString to read entries");
            
            var addonMaster = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
            var entryCount = addonMaster.EntryCount;
            Plugin.Log.Information($"[FIND] AddonMaster reports {entryCount} entries");

            // Each entry in AddonMaster has a Text property we can check
            // The text should contain the map name, which we can match against our target
            for (int i = 0; i < entryCount; i++)
            {
                var entry = addonMaster.Entries[i];
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
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        try
        {
            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null) return false;

            var gameObjPtr = (GameObject*)obj.Address;
            if (gameObjPtr == null) return false;

            ts->InteractWithObject(gameObjPtr, true);
            Plugin.Log.Information($"InteractWithObject: {obj.Name.TextValue} at {obj.Position}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"InteractWithObject failed: {ex.Message}");
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
}
