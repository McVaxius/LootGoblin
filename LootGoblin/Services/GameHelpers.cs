using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Memory;
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
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace LootGoblin.Services;

/// <summary>
/// Static unsafe helpers for game state queries and item/object interaction.
/// Patterns adapted from FrenRider's GameHelpers.cs.
/// </summary>
public static class GameHelpers
{
    private const float AetheryteSanctuaryFallbackDistance = 50.0f;
    private const float AetheryteSanctuaryFallbackDistanceSquared = AetheryteSanctuaryFallbackDistance * AetheryteSanctuaryFallbackDistance;

    public const uint WellFedStatusId = 48;
    private static readonly object ItemLookupLock = new();
    private static readonly Dictionary<uint, string> ItemNameCache = new();
    private static readonly Dictionary<string, (uint Id, string Name)> FoodLookupByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, uint?> TerritoryMapIdCache = new();
    private static bool foodLookupLoaded;

    // Known food items in order of priority (least to most preferred), matching FrenRider.
    public static readonly (uint Id, string Name)[] FoodList =
    {
        (4745,  "Orange Juice"),
        (12855, "Grilled Sweetfish"),
        (19816, "Popoto Soba"),
        (19822, "Grilled Turban"),
        (39872, "Baked Eggplant"),
        (44182, "Pineapple Orange Jelly"),
        (44178, "Moqecka"),
        (46003, "Mate Cookie"),
    };

    // Static fields for delayed callback handling
    private static int _pendingMenuIndex = -1;
    private static DateTime _callbackReadyAt = DateTime.MinValue;
    private static DateTime _callbackTimeoutAt = DateTime.MinValue;
    private static bool _waitingForSecondCallback = false;
    private static uint _pendingItemId = 0;
    private static DateTime _mapLookupNextAttemptAt = DateTime.MinValue;
    private static DateTime _mapLookupTimeoutAt = DateTime.MinValue;
    private static bool _waitingForMapLookup = false;
    private static bool _waitingForConfirmDialog = false;
    private static DateTime _confirmDialogStartTime = DateTime.MinValue;
    private static DateTime _confirmDialogReadyAt = DateTime.MinValue;
    private static DateTime _lastConfirmDialogLogTime = DateTime.MinValue;
    private static string? _pendingSequenceAddonName;
    private static bool _pendingSequenceSecondUpdateState;
    private static object[]? _pendingSequenceSecondArgs;
    private static DateTime _pendingSequenceSecondReadyAt = DateTime.MinValue;
    private static bool _pendingSequenceWaitingForSecond = false;
    private const double MapLookupInitialDelaySeconds = 0.5;
    private const double MapLookupRetryIntervalSeconds = 0.25;
    private const double MapLookupTimeoutSeconds = 4.0;
    private const double MapSelectionDelaySeconds = 0.2;
    private const double MapSelectionTimeoutSeconds = 2.0;
    private const double ConfirmDialogWatchTimeoutSeconds = 5.0;
    private const double ConfirmDialogLogIntervalSeconds = 1.0;
    /// <summary>
    /// Check if we need to fire the delayed second callback for SelectIconString.
    /// Call this method regularly from the main tick loop.
    /// </summary>
    public static void UpdateDelayedCallbacks()
    {
        var now = DateTime.Now;

        // Handle map lookup delay
        if (_waitingForMapLookup && _pendingItemId > 0)
        {
            if (now >= _mapLookupTimeoutAt)
            {
                Plugin.LogWarning($"[MAP_LOOKUP] Timed out finding menu entry for map {_pendingItemId}");
                ResetPendingMapLookup();
            }
            else if (now >= _mapLookupNextAttemptAt)
            {
                var realMenuIndex = FindMapIndexInMenu(_pendingItemId);
                if (realMenuIndex >= 0)
                {
                    _pendingMenuIndex = realMenuIndex;
                    _callbackReadyAt = now.AddSeconds(MapSelectionDelaySeconds);
                    _callbackTimeoutAt = now.AddSeconds(MapSelectionTimeoutSeconds);
                    _waitingForSecondCallback = true;
                    Plugin.Log.Information($"[MAP_LOOKUP] Resolved map {_pendingItemId} to menu index {realMenuIndex}");
                    ResetPendingMapLookup();
                }
                else
                {
                    _mapLookupNextAttemptAt = now.AddSeconds(MapLookupRetryIntervalSeconds);
                }
            }
        }
        
        // Handle single callback delay (renamed from "second callback")
        if (_waitingForSecondCallback && _pendingMenuIndex >= 0)
        {
            if (now >= _callbackTimeoutAt)
            {
                Plugin.LogWarning($"[CALLBACK] Timed out waiting to fire SelectIconString selection for index {_pendingMenuIndex}");
                ResetPendingMapSelection();
            }
            else if (now >= _callbackReadyAt && IsAddonVisible("SelectIconString"))
            {
                FireAddonCallback("SelectIconString", true, _pendingMenuIndex);
                Plugin.Log.Information($"[CALLBACK] Fired SelectIconString selection for index {_pendingMenuIndex}");
                TriggerConfirmDialog();
                ResetPendingMapSelection();
            }
        }

        UpdatePendingConfirmDialogWatch();
        UpdatePendingAddonCallbackSequence();
    }

    public static bool IsAddonCallbackSequencePending(string addonName)
    {
        return _pendingSequenceWaitingForSecond &&
               string.Equals(_pendingSequenceAddonName, addonName, StringComparison.Ordinal);
    }

    public static bool QueueTwoStepAddonCallbackSequence(
        string addonName,
        bool updateState,
        TimeSpan secondDelay,
        object[] firstArgs,
        object[] secondArgs)
        => QueueTwoStepAddonCallbackSequence(addonName, updateState, updateState, secondDelay, firstArgs, secondArgs);

    public static bool QueueTwoStepAddonCallbackSequence(
        string addonName,
        bool firstUpdateState,
        bool secondUpdateState,
        TimeSpan secondDelay,
        object[] firstArgs,
        object[] secondArgs)
    {
        try
        {
            if (_pendingSequenceWaitingForSecond)
            {
                Plugin.LogWarning(
                    $"[CALLBACKSEQ] Sequence already pending for '{_pendingSequenceAddonName}' - " +
                    $"cannot queue '{addonName}'");
                return false;
            }

            if (!IsAddonVisible(addonName))
            {
                Plugin.LogWarning($"[CALLBACKSEQ] Addon '{addonName}' not visible - cannot queue sequence");
                return false;
            }

            Plugin.Log.Information(
                $"[CALLBACKSEQ] Firing first step for '{addonName}' updateState={FormatCallbackArg(firstUpdateState)} " +
                $"args=[{FormatCallbackArgs(firstArgs)}]");
            FireAddonCallback(addonName, firstUpdateState, firstArgs);

            _pendingSequenceAddonName = addonName;
            _pendingSequenceSecondUpdateState = secondUpdateState;
            _pendingSequenceSecondArgs = secondArgs;
            _pendingSequenceSecondReadyAt = DateTime.Now.Add(secondDelay);
            _pendingSequenceWaitingForSecond = true;

            Plugin.Log.Information(
                $"[CALLBACKSEQ] Queued second step for '{addonName}' in {secondDelay.TotalMilliseconds:F0}ms " +
                $"updateState={FormatCallbackArg(secondUpdateState)} args=[{FormatCallbackArgs(secondArgs)}]");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[CALLBACKSEQ] Failed to queue '{addonName}': {ex.Message}");
            ResetPendingAddonCallbackSequence();
            return false;
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
                Plugin.LogWarning($"UseItem({itemId}): InventoryManager is null");
                return false;
            }

            var count = inventoryService.GetMapCount(itemId);
            if (count <= 0)
            {
                Plugin.LogWarning($"UseItem({itemId}): Item not found in inventory");
                return false;
            }

            // Use /gaction decipher to open the map selection menu
            LootGoblinActionTrace.Record("map-action", $"open-decipher-menu item={itemId} count={count}");
            CommandHelper.SendCommand("/gaction decipher");
            var allMaps = inventoryService.ScanForMaps();
            Plugin.Log.Information($"UseItem({itemId}): Opened decipher menu for {count} map(s) across {allMaps.Count} map type(s)");
            QueueMapMenuLookup(itemId);
            
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"UseItem({itemId}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Find the index of a specific map in the decipher menu by reading the SelectIconString addon.
    /// The menu order does NOT match inventory order - it's sorted by the game.
    /// </summary>
    public static unsafe int FindMapIndexInMenu(uint targetItemId)
    {
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectIconString", 1);
            if (addonPtr == 0)
                return -1;

            var addon = (AddonSelectIconString*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
                return -1;

            var addonMaster = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
            var entryCount = addonMaster.EntryCount;

            if (entryCount == 0)
                return -1;

            // AtkValues don't contain item IDs, only UI display data (strings and icon IDs)
            // We need to use AddonMaster to access the actual entries

            // Each entry in AddonMaster has a Text property we can check
            // The text should contain the map name, which we can match against our target
            var targetItemName = LookupItemName(targetItemId);
            if (string.IsNullOrWhiteSpace(targetItemName))
                return -1;

            for (int i = 0; i < entryCount; i++)
            {
                var entry = addonMaster.Entries[i];
                var text = entry.Text;
                Plugin.Log.Debug($"[FIND] Entry[{i}]: Text='{text}'");

                if (text.Contains(targetItemName))
                {
                    Plugin.Log.Information($"[FIND] Found target map '{targetItemName}' at entry index {i}");
                    return i;
                }
            }

            Plugin.Log.Debug($"[FIND] Target map ID {targetItemId} not found in {entryCount} entries");
            return -1;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FIND] FindMapIndexInMenu failed: {ex.Message}\n{ex.StackTrace}");
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
            Plugin.LogError($"[CALLBACK] TriggerMapDecipherCallback failed: {ex.Message}");
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
            Plugin.LogError("[CALLBACK] Could not find SelectIconString addon");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Found SelectIconString addon at 0x{addonPtr:X}");

        var addon = (AddonSelectIconString*)addonPtr;
        if (!addon->AtkUnitBase.IsVisible)
        {
            Plugin.LogError("[CALLBACK] SelectIconString addon is not visible");
            return;
        }

        Plugin.Log.Information($"[CALLBACK] Addon is visible, creating AddonMaster...");

        // Use raw AtkUnitBase callback to avoid stale ECommons callback wrappers.
        Plugin.Log.Information($"[CALLBACK] Addon AtkValuesCount={addon->AtkUnitBase.AtkValuesCount}");
        Plugin.Log.Information($"[CALLBACK] Attempting raw callback with 2 params: true, {mapIndex}");
        
        try
        {
            var atkValues = stackalloc AtkValue[2];
            atkValues[0] = default;
            atkValues[0].Type = AtkValueType.Bool;
            atkValues[0].Byte = 1; // true = confirm selection
            atkValues[1] = default;
            atkValues[1].Type = AtkValueType.Int;
            atkValues[1].Int = mapIndex; // 0-based index
            
            Plugin.Log.Information($"[CALLBACK] Calling FireCallback with Bool=true, Int={mapIndex} (0-based)");
            addon->AtkUnitBase.FireCallback(2, atkValues);
            Plugin.Log.Information($"[CALLBACK] FireCallback completed - selected index {mapIndex}");

            // Wait for the confirmation dialog, then click OK
            Plugin.Log.Information($"[CALLBACK] Waiting 1000ms for confirmation dialog...");
            System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => {
                try
                {
                    Plugin.Log.Information("[CALLBACK] Triggering confirmation dialog callback");
                    TriggerConfirmDialog();
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"[GameHelpers] ContinueWith exception in TriggerConfirmDialog: {ex.Message}");
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[CALLBACK] Raw callback failed: {ex.Message}");
            Plugin.LogError($"[CALLBACK] Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Plugin.LogError($"[CALLBACK] Inner exception: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Click OK on the "Decipher the [map name]?" confirmation dialog.
    /// Uses async/await pattern like SND for reliable addon interactions.
    /// </summary>
    private static void TriggerConfirmDialog()
    {
        Plugin.Log.Information("[CALLBACK] Starting confirmation dialog callback");
        QueueConfirmDialogWatch("decipher confirmation");
    }

    private static void QueueMapMenuLookup(uint itemId)
    {
        ResetPendingMapLookup();
        ResetPendingMapSelection();
        _pendingItemId = itemId;
        _mapLookupNextAttemptAt = DateTime.Now.AddSeconds(MapLookupInitialDelaySeconds);
        _mapLookupTimeoutAt = DateTime.Now.AddSeconds(MapLookupTimeoutSeconds);
        _waitingForMapLookup = true;
        Plugin.Log.Information($"[MAP_LOOKUP] Queued SelectIconString lookup for map {itemId}");
    }

    private static void ResetPendingMapLookup()
    {
        _pendingItemId = 0;
        _mapLookupNextAttemptAt = DateTime.MinValue;
        _mapLookupTimeoutAt = DateTime.MinValue;
        _waitingForMapLookup = false;
    }

    private static void ResetPendingMapSelection()
    {
        _pendingMenuIndex = -1;
        _callbackReadyAt = DateTime.MinValue;
        _callbackTimeoutAt = DateTime.MinValue;
        _waitingForSecondCallback = false;
    }

    private static void QueueConfirmDialogWatch(string reason)
    {
        ResetPendingConfirmDialogWatch();
        _waitingForConfirmDialog = true;
        _confirmDialogStartTime = DateTime.Now;
        _confirmDialogReadyAt = DateTime.Now.AddMilliseconds(100);
        _lastConfirmDialogLogTime = DateTime.MinValue;
        Plugin.Log.Information($"[CALLBACK] Queued SelectYesno watch for {reason}");
    }

    private static unsafe void UpdatePendingConfirmDialogWatch()
    {
        if (!_waitingForConfirmDialog)
            return;

        var now = DateTime.Now;
        if (now < _confirmDialogReadyAt)
            return;

        try
        {
            var pluginInstance = Plugin.PluginInterface.GetPluginConfig() as Configuration;
            if (pluginInstance != null && !pluginInstance.Enabled)
            {
                Plugin.Log.Debug("[CALLBACK] Bot is disabled, cancelling pending decipher confirmation watch");
                ResetPendingConfirmDialogWatch();
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[CALLBACK] Error checking bot enabled state: {ex.Message}");
        }

        nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
        if (addonPtr != 0)
        {
            var addon = (AddonSelectYesno*)addonPtr;
            if (addon->AtkUnitBase.IsVisible)
            {
                Plugin.Log.Information("[CALLBACK] Pending SelectYesno became visible, clicking Yes...");

                var accepted = Plugin.Instance?.StateManager?.ClickYesIfVisibleWithDiagnostics("GameHelpers.decipher-confirm-watch")
                    ?? ClickYesIfVisible("GameHelpers.decipher-confirm-watch", out _);
                if (accepted)
                {
                    Plugin.Log.Information("[CALLBACK] Successfully clicked Yes on decipher confirmation");
                    ResetPendingConfirmDialogWatch();
                    return;
                }

                Plugin.LogWarning("[CALLBACK] Pending decipher confirmation click failed; will retry until timeout");
                return;
            }
        }

        var elapsed = (now - _confirmDialogStartTime).TotalSeconds;
        if (elapsed >= ConfirmDialogWatchTimeoutSeconds)
        {
            Plugin.LogWarning("[CALLBACK] Timed out waiting for decipher confirmation dialog; leaving further handling to state ticks");
            ResetPendingConfirmDialogWatch();
            return;
        }

        if ((now - _lastConfirmDialogLogTime).TotalSeconds >= ConfirmDialogLogIntervalSeconds)
        {
            Plugin.Log.Information($"[CALLBACK] Waiting for decipher confirmation dialog... ({elapsed:F1}/{ConfirmDialogWatchTimeoutSeconds:F1}s)");
            _lastConfirmDialogLogTime = now;
        }
    }

    private static void ResetPendingConfirmDialogWatch()
    {
        _waitingForConfirmDialog = false;
        _confirmDialogStartTime = DateTime.MinValue;
        _confirmDialogReadyAt = DateTime.MinValue;
        _lastConfirmDialogLogTime = DateTime.MinValue;
    }

    public static void CancelPendingMapDecipher()
    {
        ResetPendingMapLookup();
        ResetPendingMapSelection();
        ResetPendingConfirmDialogWatch();
    }

    /// <summary>
    /// Generic SelectYesno handler - clicks Yes on any visible SelectYesno dialog.
    /// Call this from state ticks whenever we expect a Yes/No dialog.
    /// Returns true if a dialog was found and clicked.
    /// </summary>
    public static unsafe bool ClickYesIfVisible()
        => ClickYesIfVisible("GameHelpers.ClickYesIfVisible", out _);

    public static unsafe bool ClickYesIfVisible(string source)
        => ClickYesIfVisible(source, out _);

    public static unsafe bool ClickYesIfVisible(string source, out string prompt, Action<string>? beforeCallback = null)
    {
        prompt = string.Empty;
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AddonSelectYesno*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
                return false;

            prompt = TryReadSelectYesnoPrompt(out var readPrompt) ? readPrompt : "<unreadable>";
            if (beforeCallback != null)
            {
                beforeCallback(prompt);
            }
            else
            {
                Plugin.AddDebugLogStatic(
                    $"[SelectYesno] observed prompt='{EscapeSelectYesnoDiagnostic(prompt)}' source={source} state=unavailable party.total=unavailable territory={Plugin.ClientState.TerritoryType}");
            }

            if (TryFireAddonCallback("SelectYesno", true, 0))
            {
                ResetPendingConfirmDialogWatch();
                Plugin.Log.Information($"[YES/NO] Clicked Yes on SelectYesno dialog from {source}: {prompt}");
                if (beforeCallback == null)
                {
                    Plugin.AddDebugLogStatic(
                        $"[SelectYesno] accepted prompt='{EscapeSelectYesnoDiagnostic(prompt)}' source={source} result=callback-sent recent='{EscapeSelectYesnoDiagnostic(LootGoblinActionTrace.FormatRecent())}'");
                }
                return true;
            }

            Plugin.LogWarning("[YES/NO] SelectYesno direct callback failed");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[YES/NO] ClickYesIfVisible failed: {ex.Message}");
            return false;
        }
    }

    public static unsafe bool TryReadSelectYesnoPrompt(out string prompt)
    {
        prompt = string.Empty;
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AddonSelectYesno*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
                return false;

            var promptNode = addon->PromptText;
            if (promptNode == null || !promptNode->NodeText.StringPtr.HasValue)
                return false;

            var promptSeString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(promptNode->NodeText.StringPtr));
            prompt = promptSeString.TextValue?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(prompt);
        }
        catch
        {
            prompt = string.Empty;
            return false;
        }
    }

    private static string EscapeSelectYesnoDiagnostic(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        return InteractWithObject(obj, true);
    }

    /// <summary>
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj, bool useCameraRaycast)
    {
        try
        {
            Plugin.Log.Information($"[INTERACT] Starting interaction with {obj.Name.TextValue} (Address: {obj.Address:X}, useCameraRaycast={useCameraRaycast})");
            LootGoblinActionTrace.Record(
                "interact-attempt",
                $"{obj.Name.TextValue} kind={obj.ObjectKind} entity={obj.EntityId} xyz={obj.Position} useCameraRaycast={useCameraRaycast}");
            
            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                Plugin.LogError("[INTERACT] TargetSystem.Instance() returned null");
                return false;
            }

            var gameObjPtr = (GameObject*)obj.Address;
            if (gameObjPtr == null)
            {
                Plugin.LogError($"[INTERACT] Failed to cast GameObject* from address {obj.Address:X}");
                return false;
            }

            Plugin.Log.Information($"[INTERACT] Calling TargetSystem.InteractWithObject for {obj.Name.TextValue}");
            ts->InteractWithObject(gameObjPtr, useCameraRaycast);
            Plugin.Log.Information($"[INTERACT] InteractWithObject called successfully for {obj.Name.TextValue} at {obj.Position}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[INTERACT] InteractWithObject failed: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    public static unsafe bool RequestCameraResetBeforeInteract()
    {
        try
        {
            var cameraManager = CameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera == null)
            {
                Plugin.LogWarning("[INTERACT] CameraManager unavailable for pre-interact reset");
                return false;
            }

            cameraManager->Camera->ShouldResetAngles = true;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogWarning($"[INTERACT] Failed to request camera reset before interact: {ex.Message}");
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

    public static bool IsPlayerAlive()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player != null && player.CurrentHp > 0;
    }

    public static bool CanAutoDiscardNow(out string reason)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "player unavailable";
            return false;
        }

        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            reason = "not mounted";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            reason = "in combat";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "between areas";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            reason = "busy or in cutscene";
            return false;
        }

        reason = "ready";
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
            return GetInventoryItemCount(itemId, false) + GetInventoryItemCount(itemId, true);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"GetInventoryItemCount({itemId}) failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get exact NQ or HQ count of an item in player inventory.
    /// </summary>
    public static unsafe int GetInventoryItemCount(uint itemId, bool highQuality)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            return im->GetInventoryItemCount(itemId, highQuality);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"GetInventoryItemCount({itemId}, HQ={highQuality}) failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Return remaining time for a status effect in seconds, or 0 if missing.
    /// </summary>
    public static unsafe float GetStatusTimeRemaining(uint statusId)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return 0f;

            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
            if (chara == null) return 0f;

            var statusManager = chara->GetStatusManager();
            if (statusManager == null) return 0f;

            for (var i = 0; i < statusManager->NumValidStatuses; i++)
            {
                var status = statusManager->Status[i];
                if (status.StatusId == statusId)
                    return status.RemainingTime;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"GetStatusTimeRemaining({statusId}) failed: {ex.Message}");
        }

        return 0f;
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
            Plugin.LogError($"GetBuddyTimeRemaining() failed: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// Check if the player is in a sanctuary (rest area where you can't summon companion).
    /// Ported from FrenRider GameHelpers.
    /// Also allows ADS NPC no-inn repair near Aetheryte/Aethernet objects.
    /// </summary>
    public static unsafe bool IsInSanctuary()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return true;
            var status = am->GetActionStatus(ActionType.GeneralAction, 9);
            return status != 0 || IsNearAetheryteOrAethernet();
        }
        catch
        {
            return true;
        }
    }

    private static bool IsNearAetheryteOrAethernet()
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;

            var playerPosition = player.Position;
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || !IsAetheryteOrAethernet(obj))
                    continue;

                if (Vector3.DistanceSquared(playerPosition, obj.Position) <= AetheryteSanctuaryFallbackDistanceSquared)
                    return true;
            }
        }
        catch
        {
            // Keep old sanctuary heuristic behavior if object-table proximity cannot be read.
        }

        return false;
    }

    private static bool IsAetheryteOrAethernet(IGameObject obj)
    {
        if (obj.ObjectKind == DalamudObjectKind.Aetheryte)
            return true;

        var name = obj.Name.TextValue;
        return !string.IsNullOrEmpty(name) &&
               (name.Contains("Aetheryte", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Aethernet", StringComparison.OrdinalIgnoreCase));
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
            Plugin.LogError($"UseGysahlGreens failed: {ex.Message}");
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
            Plugin.LogError($"[KEY] KeyHold({key}) failed: {ex.Message}");
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
            Plugin.LogError($"[KEY] KeyRelease({key}) failed: {ex.Message}");
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
            Plugin.LogError($"[KEY] KeyPress({key}) failed: {ex.Message}");
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

    public static async Task PerformForwardDescentAsync(int automoveHoldMs, int totalHoldMs)
    {
        totalHoldMs = Math.Max(50, totalHoldMs);
        automoveHoldMs = Math.Clamp(automoveHoldMs, 50, totalHoldMs);

        var automoveActiveThisPulse = false;

        Plugin.Log.Information("[KEY] Performing automove Ctrl+Space descent...");
        try
        {
            await CommandHelper.SendCommandOnFrameworkThreadAsync("/automove on");
            automoveActiveThisPulse = true;
            KeyHold(VirtualKey.CONTROL);
            KeyHold(VirtualKey.SPACE);
            await Task.Delay(automoveHoldMs);
            await CommandHelper.SendCommandOnFrameworkThreadAsync("/automove off");
            automoveActiveThisPulse = false;
            await Task.Delay(totalHoldMs - automoveHoldMs);
        }
        finally
        {
            if (automoveActiveThisPulse)
                await CommandHelper.SendCommandOnFrameworkThreadAsync("/automove off");

            KeyRelease(VirtualKey.W);
            KeyRelease(VirtualKey.CONTROL);
            KeyRelease(VirtualKey.SPACE);
        }
    }

    // ─── Map Flag ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Clear the current map flag from both vnavmesh and the game's AgentMap marker state.
    /// </summary>
    public static unsafe bool ClearMapFlag(Func<LootGoblin.Models.MapLocation?>? tryReadFlag = null)
    {
        CommandHelper.SendCommand("/vnav clearflag");

        try
        {
            var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                          Plugin.Condition[ConditionFlag.BetweenAreas51];
            if (loading)
            {
                Plugin.LogWarning("[MapFlag] Clear requested during loading - vnav cleared, AgentMap clear skipped");
                return false;
            }

            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                Plugin.LogWarning("[MapFlag] AgentMap is null during clear");
                return tryReadFlag != null && tryReadFlag() == null;
            }

            var beforeCount = agentMap->FlagMarkerCount;
            var beforeTerritory = beforeCount > 0 ? agentMap->FlagMapMarkers[0].TerritoryId : 0;

            agentMap->FlagMarkerCount = 0;
            agentMap->FlagMapMarkers[0] = default;

            var afterCount = agentMap->FlagMarkerCount;
            var afterTerritory = agentMap->FlagMapMarkers[0].TerritoryId;
            var cleared = tryReadFlag != null
                ? tryReadFlag() == null
                : afterCount == 0 && afterTerritory == 0;

            Plugin.Log.Information(
                $"[MapFlag] Clear AgentMap: count {beforeCount}->{afterCount}, first territory {beforeTerritory}->{afterTerritory}, verified={cleared}");

            return cleared;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[MapFlag] ClearMapFlag failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Place a flag marker on the map at the given world coordinates.
    /// Uses AgentMap.SetFlagMapMarker with Vector3 overload (handles coord conversion internally).
    /// </summary>
    public static unsafe void SetMapFlag(uint territoryId, float worldX, float worldZ)
    {
        try
        {
            if (territoryId == 0)
            {
                ClearMapFlag();
                return;
            }

            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                Plugin.LogWarning("[MapFlag] AgentMap is null");
                return;
            }

            if (!TryGetTerritoryMapId(territoryId, out var mapId))
            {
                Plugin.LogWarning($"[MapFlag] Territory {territoryId} not found");
                return;
            }

            SetMapFlag(territoryId, mapId, worldX, worldZ);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[MapFlag] SetMapFlag failed: {ex.Message}");
        }
    }

    public static unsafe void SetMapFlag(uint territoryId, uint mapId, float worldX, float worldZ)
    {
        try
        {
            if (territoryId == 0 || mapId == 0)
            {
                ClearMapFlag();
                return;
            }

            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                Plugin.LogWarning("[MapFlag] AgentMap is null");
                return;
            }

            var worldPos = new Vector3(worldX, 0f, worldZ);
            agentMap->SetFlagMapMarker(territoryId, mapId, worldPos);
            Plugin.Log.Information($"[MapFlag] Set flag at territory {territoryId}, map {mapId}, world ({worldX:F1}, {worldZ:F1})");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[MapFlag] SetMapFlag failed: {ex.Message}");
        }
    }

    // ─── Currency ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Get the player's current Tomestones of Poetics count.
    /// Poetics item ID = 28, use GetItemCount since it's just an untradeable item.
    /// </summary>
    public static unsafe int GetCurrentPoetics()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            
            // Poetics is item ID 28, just use GetInventoryItemCount like any other item
            var count = im->GetInventoryItemCount(28);
            Plugin.Log.Debug($"[POETICS] Poetics (item ID 28) count: {count}");
            return (int)count;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"GetCurrentPoetics failed: {ex.Message}");
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
        CommandHelper.SendCommand("/automove on");
    }

    /// <summary>
    /// Stop automove.
    /// </summary>
    public static void StopAutoMove()
    {
        CommandHelper.SendCommand("/automove off");
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

    private static void UpdatePendingAddonCallbackSequence()
    {
        if (!_pendingSequenceWaitingForSecond ||
            string.IsNullOrEmpty(_pendingSequenceAddonName) ||
            _pendingSequenceSecondArgs == null)
            return;

        if (DateTime.Now < _pendingSequenceSecondReadyAt)
            return;

        if (!IsAddonVisible(_pendingSequenceAddonName))
        {
            Plugin.LogWarning(
                $"[CALLBACKSEQ] Addon '{_pendingSequenceAddonName}' disappeared before second step");
            ResetPendingAddonCallbackSequence();
            return;
        }

        try
        {
            Plugin.Log.Information(
                $"[CALLBACKSEQ] Firing second step for '{_pendingSequenceAddonName}' " +
                $"updateState={FormatCallbackArg(_pendingSequenceSecondUpdateState)} " +
                $"args=[{FormatCallbackArgs(_pendingSequenceSecondArgs)}]");
            FireAddonCallback(_pendingSequenceAddonName, _pendingSequenceSecondUpdateState, _pendingSequenceSecondArgs);
            Plugin.Log.Information($"[CALLBACKSEQ] Completed sequence for '{_pendingSequenceAddonName}'");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[CALLBACKSEQ] Failed second step for '{_pendingSequenceAddonName}': {ex.Message}");
        }
        finally
        {
            ResetPendingAddonCallbackSequence();
        }
    }

    private static void ResetPendingAddonCallbackSequence()
    {
        _pendingSequenceAddonName = null;
        _pendingSequenceSecondArgs = null;
        _pendingSequenceSecondReadyAt = DateTime.MinValue;
        _pendingSequenceWaitingForSecond = false;
        _pendingSequenceSecondUpdateState = false;
    }

    private static string FormatCallbackArgs(object[] args)
    {
        return string.Join(", ", args.Select(FormatCallbackArg));
    }

    private static string FormatCallbackArg(object? arg)
        => arg switch
        {
            null => "<null>",
            bool boolValue => boolValue ? "true" : "false",
            _ => arg.ToString() ?? string.Empty,
        };

    /// <summary>
    /// Fire a callback on a named addon with variable arguments.
    /// Uses AtkUnitBase.FireCallback pattern from map decipher solution.
    /// </summary>
    public static void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        _ = TryFireAddonCallback(addonName, updateState, args);
    }

    /// <summary>
    /// Fire a callback on a named addon and report whether it was sent.
    /// </summary>
    public static bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
        => TryFireAddonCallbackInternal(addonName, updateState, true, args);

    /// <summary>
    /// Fire a callback on a named addon that may be present but hidden behind a child addon.
    /// </summary>
    public static bool TryFireAddonCallbackIfExists(string addonName, bool updateState, params object[] args)
        => TryFireAddonCallbackInternal(addonName, updateState, false, args);

    private static unsafe bool TryFireAddonCallbackInternal(
        string addonName,
        bool updateState,
        bool requireVisible,
        params object[] args)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || (requireVisible && !addon->IsVisible))
            {
                var reason = addon == null ? "not found" : "not visible";
                Plugin.LogWarning($"[FireAddonCallback] Addon '{addonName}' {reason}");
                return false;
            }

            var atkValues = new AtkValue[args.Length];
            for (int i = 0; i < args.Length; i++)
                atkValues[i] = CreateCallbackValue(args[i]);

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }

            LootGoblinActionTrace.Record(
                "addon-callback",
                $"{addonName} updateState={FormatCallbackArg(updateState)} args=[{FormatCallbackArgs(args)}]");
            Plugin.Log.Information(
                $"[FireAddonCallback] Fired callback on '{addonName}' updateState={FormatCallbackArg(updateState)} args=[{FormatCallbackArgs(args)}]");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FireAddonCallback] Failed for '{addonName}': {ex.Message}");
            return false;
        }
    }

    private static AtkValue CreateCallbackValue(object? arg)
    {
        var value = default(AtkValue);
        switch (arg)
        {
            case bool boolVal:
                value.Type = AtkValueType.Bool;
                value.Byte = (byte)(boolVal ? 1 : 0);
                break;
            case uint uintVal:
                value.Type = AtkValueType.UInt;
                value.UInt = uintVal;
                break;
            case int intVal:
                value.Type = AtkValueType.Int;
                value.Int = intVal;
                break;
            default:
                value.Type = AtkValueType.Int;
                value.Int = arg == null ? 0 : Convert.ToInt32(arg);
                break;
        }

        return value;
    }

    /// <summary>
    /// Close a visible addon by firing its cancel callback.
    /// </summary>
    public static unsafe bool TryCloseAddonByCallback(string addonName)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
            {
                Plugin.Log.Debug($"[CloseAddonCallback] Addon '{addonName}' not found or not visible");
                return false;
            }

            var atkValues = stackalloc AtkValue[1];
            atkValues[0] = default;
            atkValues[0].Type = AtkValueType.Int;
            atkValues[0].Int = -1;
            addon->FireCallback(1, atkValues, true);

            Plugin.Log.Information($"[CloseAddonCallback] Fired cancel callback on '{addonName}'");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogWarning($"[CloseAddonCallback] Failed for '{addonName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Close the currently focused addon by sending Escape key.
    /// </summary>
    public static void CloseCurrentAddon()
    {
        KeyPress(VirtualKey.ESCAPE);
    }

    public static uint LookupFoodItemId(string foodName)
        => LookupFoodItem(foodName).Id;

    public static (uint Id, string Name) LookupFoodItem(string foodName)
    {
        if (string.IsNullOrWhiteSpace(foodName)) return (0, "");

        try
        {
            var trimmedName = foodName.Trim();
            lock (ItemLookupLock)
            {
                EnsureFoodLookupLoadedLocked();
                return FoodLookupByName.TryGetValue(trimmedName, out var food)
                    ? food
                    : (0, "");
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"LookupFoodItem(\"{foodName}\") failed: {ex.Message}");
        }

        return (0, "");
    }

    private static void EnsureFoodLookupLoadedLocked()
    {
        if (foodLookupLoaded)
            return;

        var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (itemSheet == null)
            return;

        foreach (var row in itemSheet)
        {
            if (row.ItemUICategory.RowId != 46)
                continue;

            var name = row.Name.ToString();
            if (!string.IsNullOrEmpty(name) && !FoodLookupByName.ContainsKey(name))
            {
                FoodLookupByName[name] = (row.RowId, name);
                ItemNameCache.TryAdd(row.RowId, name);
            }
        }

        foodLookupLoaded = true;
    }

    public static string LookupItemName(uint itemId)
    {
        if (itemId == 0) return "";

        try
        {
            lock (ItemLookupLock)
            {
                if (ItemNameCache.TryGetValue(itemId, out var cachedName))
                    return cachedName;

                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                if (itemSheet == null)
                    return "";

                if (itemSheet.TryGetRow(itemId, out var item))
                {
                    var name = item.Name.ToString();
                    ItemNameCache[itemId] = name;
                    return name;
                }

                ItemNameCache[itemId] = "";
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"LookupItemName({itemId}) failed: {ex.Message}");
        }

        return "";
    }

    private static bool TryGetTerritoryMapId(uint territoryId, out uint mapId)
    {
        mapId = 0;
        lock (ItemLookupLock)
        {
            if (TerritoryMapIdCache.TryGetValue(territoryId, out var cachedMapId))
            {
                mapId = cachedMapId ?? 0;
                return cachedMapId.HasValue;
            }

            try
            {
                var territorySheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
                if (territorySheet != null && territorySheet.TryGetRow(territoryId, out var territory))
                {
                    mapId = territory.Map.RowId;
                    TerritoryMapIdCache[territoryId] = mapId;
                    return true;
                }
            }
            catch
            {
                // Fall through and cache a miss for this static lookup.
            }

            TerritoryMapIdCache[territoryId] = null;
            return false;
        }
    }

    public static (uint Id, string Name, bool HighQuality, int Count) FindBestAvailableFood()
    {
        for (var i = FoodList.Length - 1; i >= 0; i--)
        {
            var nqCount = GetInventoryItemCount(FoodList[i].Id, false);
            if (nqCount > 0)
                return (FoodList[i].Id, FoodList[i].Name, false, nqCount);

            var hqCount = GetInventoryItemCount(FoodList[i].Id, true);
            if (hqCount > 0)
                return (FoodList[i].Id, FoodList[i].Name, true, hqCount);
        }

        return (0, "", false, 0);
    }

    /// <summary>
    /// Simple UseItem that uses the item directly via ActionManager (non-map items).
    /// For items like Mysterious Map that need direct use, not /gaction decipher.
    /// </summary>
    public static unsafe bool UseItem(uint itemId)
        => UseItem(itemId, false);

    /// <summary>
    /// Use a direct inventory item action, with HQ item action offset when requested.
    /// </summary>
    public static unsafe bool UseItem(uint itemId, bool highQuality)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null || player.IsCasting)
                return false;

            if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
                Plugin.Condition[ConditionFlag.BetweenAreas51] ||
                Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[ConditionFlag.Occupied33] ||
                Plugin.Condition[ConditionFlag.Occupied39] ||
                Plugin.Condition[ConditionFlag.WatchingCutscene])
            {
                return false;
            }

            var count = GetInventoryItemCount(itemId, highQuality);
            if (count <= 0)
            {
                Plugin.LogWarning($"UseItem({itemId}, HQ={highQuality}): Not in inventory");
                return false;
            }

            var am = ActionManager.Instance();
            if (am == null) return false;

            var actionItemId = highQuality ? itemId + 1_000_000u : itemId;
            var status = am->GetActionStatus(ActionType.Item, actionItemId);
            if (status != 0)
            {
                Plugin.Log.Debug($"UseItem({itemId}, HQ={highQuality}): ActionStatus={status}, not ready");
                return false;
            }

            var result = am->UseAction(ActionType.Item, actionItemId, extraParam: 65535);
            LootGoblinActionTrace.Record("item-use", $"item={itemId} hq={highQuality} actionItem={actionItemId} result={result}");
            Plugin.Log.Information($"UseItem({itemId}, HQ={highQuality}): ActionManager result = {result}");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"UseItem({itemId}, HQ={highQuality}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Use an active key item/event item directly.
    /// Treasure-map key items use EventItem, not normal Item inventory rows.
    /// </summary>
    public static unsafe bool UseEventItem(uint eventItemId, string displayName)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;
            if (player.IsCasting) return false;

            if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
                Plugin.Condition[ConditionFlag.BetweenAreas51] ||
                Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[ConditionFlag.Occupied33] ||
                Plugin.Condition[ConditionFlag.Occupied39])
                return false;

            var am = ActionManager.Instance();
            if (am == null) return false;

            var status = am->GetActionStatus(ActionType.EventItem, eventItemId);
            if (status != 0)
            {
                Plugin.LogWarning($"UseEventItem({eventItemId}): action status {status} for {displayName}");
                return false;
            }

            var result = am->UseAction(ActionType.EventItem, eventItemId, 0xE0000000, 65535, 0, 0, null);
            LootGoblinActionTrace.Record("event-item-use", $"{displayName} eventItem={eventItemId} result={result}");
            Plugin.Log.Information($"UseEventItem({eventItemId}): {displayName}, ActionManager result = {result}");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"UseEventItem({eventItemId}) failed: {ex.Message}");
            return false;
        }
    }
}
