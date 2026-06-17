using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class GatherBuddyRebornService : IDisposable
{
    private const string IpcPrefix = "GatherBuddyReborn";
    private const string ManagedListPrefix = "LootGoblin One-Shot";
    private static readonly string[] KnownInternalNames = { "GatherBuddyReborn", "GatherbuddyReborn" };

    private readonly Plugin plugin;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private object? managedList;
    private readonly List<SuppressedListState> suppressedLists = new();
    private DateTime lastAvailabilityCheckUtc = DateTime.MinValue;
    private uint desiredQuantity;

    public bool IsAvailable { get; private set; }
    public int Version { get; private set; }
    public string StatusText { get; private set; } = "Not checked.";
    public uint TargetItemId { get; private set; }
    public string TargetMapName { get; private set; } = string.Empty;
    public bool HasManagedList => managedList != null;

    private readonly record struct SuppressedListState(object List, bool Enabled);
    private readonly record struct ActiveGatherItem(uint ItemId, uint Quantity, string ItemType);

    public GatherBuddyRebornService(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.plugin = plugin;
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public void Dispose()
    {
        Cancel();
    }

    public void CheckAvailability(bool logStatus = false)
    {
        var now = DateTime.UtcNow;
        if (!logStatus && now - lastAvailabilityCheckUtc < TimeSpan.FromSeconds(2))
            return;

        lastAvailabilityCheckUtc = now;
        IsAvailable = false;
        Version = 0;

        if (!IsLoaded())
        {
            StatusText = "GatherBuddy Reborn not loaded.";
            return;
        }

        try
        {
            Version = pluginInterface.GetIpcSubscriber<int>($"{IpcPrefix}.Version").InvokeFunc();
            if (Version < 2)
            {
                StatusText = $"GatherBuddy Reborn IPC version {Version} is unsupported; version 2 is required.";
                if (logStatus)
                    plugin.AddDebugLog($"[GatherBuddy] {StatusText}");
                return;
            }

            IsAvailable = true;
            StatusText = "Available.";
            if (logStatus)
                plugin.AddDebugLog("[GatherBuddy] GatherBuddy Reborn available.");
        }
        catch (Exception ex)
        {
            StatusText = $"GatherBuddy Reborn IPC unavailable: {ex.Message}";
            if (logStatus)
                plugin.AddDebugLog($"[GatherBuddy] {StatusText}");
        }
    }

    public bool StartOneShot(uint itemId, string mapName, uint gatherJobId, out string detail)
    {
        TargetItemId = 0;
        TargetMapName = string.Empty;
        desiredQuantity = 0;
        plugin.AddDebugLog(
            $"[GatherBuddy] One-shot setup requested; itemId={itemId}; map={mapName}; configuredJob={FormatGatherJob(gatherJobId)}.");
        CheckAvailability(logStatus: true);
        if (!IsAvailable)
        {
            detail = StatusText;
            plugin.AddDebugLog($"[GatherBuddy] One-shot setup unavailable; itemId={itemId}; map={mapName}; detail={detail}");
            return false;
        }

        try
        {
            DisableAutoGather();
            RestoreSuppressedLists();
            CleanupManagedLists();

            if (!TryCreateManagedList(itemId, mapName, gatherJobId, out var list, out var quantity, out detail))
            {
                plugin.AddDebugLog($"[GatherBuddy] One-shot setup failed; itemId={itemId}; map={mapName}; detail={detail}");
                Cancel();
                return false;
            }

            managedList = list;
            TargetItemId = itemId;
            TargetMapName = mapName;
            desiredQuantity = quantity;

            if (!SuppressEnabledNonManagedLists(out detail))
            {
                plugin.AddDebugLog($"[GatherBuddy] One-shot suppression failed; itemId={itemId}; map={mapName}; detail={detail}");
                Cancel();
                return false;
            }

            SetMember(managedList, "Enabled", true);
            if (!RebuildActiveItems(out detail))
            {
                plugin.AddDebugLog($"[GatherBuddy] One-shot active item rebuild failed; itemId={itemId}; map={mapName}; detail={detail}");
                Cancel();
                return false;
            }

            if (!VerifyOneShotActiveTarget(out detail, logSuccess: true))
            {
                plugin.AddDebugLog($"[GatherBuddy] One-shot target verification failed; itemId={itemId}; map={mapName}; detail={detail}");
                Cancel();
                return false;
            }

            SetAutoGatherEnabled(true);
            detail = $"GatherBuddy Reborn gathering one {mapName}.";
            StatusText = detail;
            plugin.AddDebugLog($"[GatherBuddy] {detail} DesiredQuantity={desiredQuantity}.");
            return true;
        }
        catch (Exception ex)
        {
            detail = $"GatherBuddy Reborn start failed: {ex.Message}";
            StatusText = detail;
            plugin.AddDebugLog($"[GatherBuddy] One-shot setup exception; itemId={itemId}; map={mapName}; detail={detail}");
            Plugin.LogWarning($"[GatherBuddy] {detail}");
            Cancel();
            return false;
        }
    }

    public bool ValidateOneShotTarget(out string detail)
    {
        if (TargetItemId == 0 || managedList == null)
        {
            detail = "GatherBuddy Reborn one-shot target is not active.";
            return false;
        }

        return VerifyOneShotActiveTarget(out detail);
    }

    public void Cancel()
    {
        try
        {
            DisableAutoGather();
        }
        catch (Exception ex)
        {
            log.Debug($"[GatherBuddy] Disable failed during cancel: {ex.Message}");
        }

        try
        {
            RestoreSuppressedLists();
            CleanupManagedLists();
            RebuildActiveItems(out _);
        }
        catch (Exception ex)
        {
            log.Debug($"[GatherBuddy] Cleanup failed during cancel: {ex.Message}");
        }

        managedList = null;
        TargetItemId = 0;
        TargetMapName = string.Empty;
        desiredQuantity = 0;
    }

    public bool TryGetAutoGatherStatus(out string status)
    {
        status = string.Empty;
        CheckAvailability();
        if (!IsAvailable)
        {
            status = StatusText;
            return false;
        }

        try
        {
            status = pluginInterface.GetIpcSubscriber<string>($"{IpcPrefix}.GetAutoGatherStatusText").InvokeFunc() ?? string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            status = $"GatherBuddy status IPC failed: {ex.Message}";
            return false;
        }
    }

    public bool IsAutoGatherEnabled()
    {
        try
        {
            CheckAvailability();
            return IsAvailable && pluginInterface.GetIpcSubscriber<bool>($"{IpcPrefix}.IsAutoGatherEnabled").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool IsAutoGatherWaiting()
    {
        try
        {
            CheckAvailability();
            return IsAvailable && pluginInterface.GetIpcSubscriber<bool>($"{IpcPrefix}.IsAutoGatherWaiting").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private bool IsLoaded()
    {
        try
        {
            return pluginInterface.InstalledPlugins.Any(p =>
                p.IsLoaded &&
                KnownInternalNames.Any(name => string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private void SetAutoGatherEnabled(bool enabled)
        => pluginInterface.GetIpcSubscriber<bool, object>($"{IpcPrefix}.SetAutoGatherEnabled").InvokeAction(enabled);

    private void DisableAutoGather()
    {
        if (IsLoaded())
            SetAutoGatherEnabled(false);
    }

    private bool TryCreateManagedList(uint itemId, string mapName, uint gatherJobId, out object list, out uint quantity, out string detail)
    {
        list = null!;
        quantity = 0;
        if (!TryGetReflectionContext(out var manager, out var assembly, out detail))
            return false;

        var gatherable = FindGatherable(assembly, itemId);
        plugin.AddDebugLog(
            $"[GatherBuddy] Lookup result; itemId={itemId}; map={mapName}; type={gatherable?.GetType().FullName ?? "null"}");
        if (gatherable == null)
        {
            detail = $"GatherBuddy Reborn does not expose gatherable item {itemId} ({mapName}).";
            return false;
        }

        if (!TryGetGatherableTotalCount(assembly, gatherable, out var currentTotal, out var countDetail))
        {
            detail = countDetail;
            return false;
        }

        if (currentTotal >= 999999)
        {
            detail = $"GatherBuddy Reborn already reports {currentTotal:N0} total {mapName}; cannot request one more.";
            return false;
        }

        quantity = (uint)(Math.Max(0, currentTotal) + 1);
        plugin.AddDebugLog(
            $"[GatherBuddy] Target count resolved; itemId={itemId}; map={mapName}; currentTotal={currentTotal}; desiredQuantity={quantity}; configuredJob={FormatGatherJob(gatherJobId)}.");

        var listType = assembly.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList");
        if (listType == null)
        {
            detail = "GatherBuddy Reborn AutoGatherList type not found.";
            return false;
        }

        list = Activator.CreateInstance(listType)!;
        if (list == null)
        {
            detail = "Could not create GatherBuddy Reborn auto-gather list.";
            return false;
        }

        SetMember(list, "Name", $"{ManagedListPrefix} {itemId}");
        SetMember(list, "Description", $"Created by LootGoblin for one {mapName}.");
        SetMember(list, "FolderPath", "LootGoblin");
        SetMember(list, "Enabled", false);
        SetMember(list, "Fallback", false);
        SetMember(list, "RemoveCompletedItems", true);

        var addMethod = SelectAutoGatherAddMethod(listType, gatherable);
        if (addMethod == null)
        {
            detail = "GatherBuddy Reborn AutoGatherList.Add method not found.";
            return false;
        }

        plugin.AddDebugLog(
            $"[GatherBuddy] AutoGatherList.Add selected; signature={FormatMethodSignature(addMethod)}; itemType={gatherable.GetType().FullName}; quantity={quantity}.");
        var args = BuildAutoGatherAddArgs(addMethod, gatherable, quantity);
        var addResult = addMethod.Invoke(list, args);
        if (addResult is bool added && !added)
        {
            detail = $"GatherBuddy Reborn did not add {mapName} to the auto-gather list.";
            return false;
        }

        var preferredLocation = SelectPreferredLocation(gatherable, gatherJobId);
        if (preferredLocation != null)
        {
            if (!TrySetPreferredLocation(list, gatherable, preferredLocation, out detail))
                return false;
        }

        plugin.AddDebugLog(
            $"[GatherBuddy] Preferred location; itemId={itemId}; map={mapName}; {DescribeLocation(preferredLocation, gatherJobId)}.");

        var addListMethod = manager.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "AddList" && method.GetParameters().Length >= 1);
        if (addListMethod == null)
        {
            detail = "GatherBuddy Reborn AutoGatherListsManager.AddList method not found.";
            return false;
        }

        var addListParameters = addListMethod.GetParameters();
        var addListArgs = new object?[addListParameters.Length];
        addListArgs[0] = list;
        for (var i = 1; i < addListArgs.Length; i++)
            addListArgs[i] = Type.Missing;
        addListMethod.Invoke(manager, addListArgs);

        detail = $"Created GatherBuddy Reborn one-shot list for {mapName}.";
        return true;
    }

    private bool SuppressEnabledNonManagedLists(out string detail)
    {
        suppressedLists.Clear();
        if (!TryGetReflectionContext(out var manager, out _, out detail))
            return false;

        foreach (var list in EnumerateAutoGatherLists(manager))
        {
            if (IsManagedList(list))
                continue;

            if (!GetMember<bool>(list, "Enabled"))
                continue;

            suppressedLists.Add(new SuppressedListState(list, true));
            SetMember(list, "Enabled", false);
        }

        detail = $"Suppressed {suppressedLists.Count} enabled GatherBuddy Reborn list(s) for LootGoblin one-shot.";
        plugin.AddDebugLog($"[GatherBuddy] {detail}");
        return true;
    }

    private void RestoreSuppressedLists()
    {
        if (suppressedLists.Count == 0)
            return;

        foreach (var (list, enabled) in suppressedLists)
            SetMember(list, "Enabled", enabled);

        plugin.AddDebugLog($"[GatherBuddy] Restored {suppressedLists.Count} suppressed GatherBuddy Reborn list(s).");
        suppressedLists.Clear();
    }

    private bool RebuildActiveItems(out string detail)
    {
        if (!TryGetReflectionContext(out var manager, out _, out detail))
            return false;

        return InvokeSetActiveItems(manager, out detail);
    }

    private bool VerifyOneShotActiveTarget(out string detail, bool logSuccess = false)
    {
        if (!TryGetReflectionContext(out var manager, out _, out detail))
            return false;

        var enabledNonManagedLists = EnumerateAutoGatherLists(manager)
            .Where(list => !IsManagedList(list) && GetMember<bool>(list, "Enabled"))
            .Select(FormatListName)
            .ToArray();
        if (enabledNonManagedLists.Length > 0)
        {
            detail =
                $"GatherBuddy Reborn one-shot exclusivity failed; enabled non-LootGoblin lists: {string.Join(", ", enabledNonManagedLists)}.";
            return false;
        }

        if (!ReadActiveItems(manager, out var activeItems, out detail))
            return false;

        if (activeItems.Count != 1 ||
            activeItems[0].ItemId != TargetItemId ||
            (desiredQuantity != 0 && activeItems[0].Quantity != desiredQuantity))
        {
            detail =
                $"GatherBuddy Reborn active target mismatch; expected item={TargetItemId} quantity={desiredQuantity}; active={FormatActiveItems(activeItems)}.";
            return false;
        }

        detail =
            $"GatherBuddy Reborn active target verified; item={TargetItemId}; quantity={activeItems[0].Quantity}; suppressedLists={suppressedLists.Count}.";
        if (logSuccess)
            plugin.AddDebugLog($"[GatherBuddy] {detail}");
        return true;
    }

    private void CleanupManagedLists()
    {
        if (!TryGetReflectionContext(out var manager, out _, out _))
            return;

        foreach (var list in EnumerateAutoGatherLists(manager).ToList())
        {
            var name = GetMember<string>(list, "Name") ?? string.Empty;
            if (!IsManagedListName(name))
                continue;

            DeleteList(manager, list);
        }

        managedList = null;
    }

    private static void DeleteList(object manager, object list)
    {
        var method = manager.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "DeleteList" && method.GetParameters().Length == 1);
        method?.Invoke(manager, new[] { list });
    }

    private static bool InvokeSetActiveItems(object manager, out string detail)
    {
        var method = manager.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "SetActiveItems");
        if (method == null)
        {
            detail = "GatherBuddy Reborn AutoGatherListsManager.SetActiveItems method not found.";
            return false;
        }

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            args[i] = parameters[i].ParameterType == typeof(bool) ? false : Type.Missing;

        method.Invoke(manager, args);
        detail = "GatherBuddy Reborn active items rebuilt.";
        return true;
    }

    private bool TryGetReflectionContext(out object manager, out Assembly assembly, out string detail)
    {
        manager = null!;
        assembly = null!;

        var pluginInstance = FindGatherBuddyPluginInstance();
        if (pluginInstance == null)
        {
            detail = "GatherBuddy Reborn loaded, but plugin instance reflection failed.";
            return false;
        }

        assembly = pluginInstance.GetType().Assembly;
        var reflectedManager = GetMember<object>(pluginInstance, "AutoGatherListsManager")
                               ?? FindMemberValue(pluginInstance, value =>
                                   value.GetType().FullName?.Contains("AutoGatherListsManager", StringComparison.Ordinal) == true);

        if (reflectedManager == null)
        {
            detail = "GatherBuddy Reborn AutoGatherListsManager reflection failed.";
            return false;
        }

        manager = reflectedManager;
        detail = "ok";
        return true;
    }

    private object? FindGatherBuddyPluginInstance()
    {
        foreach (var exposed in pluginInterface.InstalledPlugins)
        {
            if (!exposed.IsLoaded ||
                !KnownInternalNames.Any(name => string.Equals(exposed.InternalName, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var direct = FindObjectInMembers(exposed, IsGatherBuddyRoot, depth: 3);
            if (direct != null)
                return direct;
        }

        return null;
    }

    private static bool IsGatherBuddyRoot(object value)
    {
        var type = value.GetType();
        return string.Equals(type.FullName, "GatherBuddy.Plugin.GatherBuddy", StringComparison.Ordinal) ||
               string.Equals(type.FullName, "GatherBuddy.GatherBuddy", StringComparison.Ordinal);
    }

    private static object? FindGatherable(Assembly assembly, uint itemId)
    {
        var gatherBuddyType = assembly.GetTypes()
            .FirstOrDefault(type => string.Equals(type.FullName, "GatherBuddy.Plugin.GatherBuddy", StringComparison.Ordinal))
            ?? assembly.GetTypes().FirstOrDefault(type => string.Equals(type.FullName, "GatherBuddy.GatherBuddy", StringComparison.Ordinal));
        var gameData = gatherBuddyType == null ? null : GetStaticMember<object>(gatherBuddyType, "GameData");
        if (gameData == null)
            return null;

        return FindInLookup(GetMember<object>(gameData, "Gatherables"), itemId)
               ?? FindInLookup(GetMember<object>(gameData, "Fishes"), itemId);
    }

    private static MethodInfo? SelectAutoGatherAddMethod(Type listType, object gatherable)
    {
        var addMethods = listType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "Add")
            .OrderByDescending(method => method.GetParameters().Length == 2)
            .ToList();

        return addMethods.FirstOrDefault(method => IsCompatibleAutoGatherAddMethod(method, gatherable, requireQuantity: true))
               ?? addMethods.FirstOrDefault(method => IsCompatibleAutoGatherAddMethod(method, gatherable, requireQuantity: false));
    }

    private static bool IsCompatibleAutoGatherAddMethod(MethodInfo method, object gatherable, bool requireQuantity)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != 1 && parameters.Length != 2)
            return false;

        if (requireQuantity && parameters.Length != 2)
            return false;

        if (!parameters[0].ParameterType.IsInstanceOfType(gatherable))
            return false;

        return parameters.Length == 1 || CanPassQuantity(parameters[1].ParameterType);
    }

    private static object?[] BuildAutoGatherAddArgs(MethodInfo addMethod, object gatherable, uint quantity)
    {
        var parameters = addMethod.GetParameters();
        if (parameters.Length == 1)
            return new object?[] { gatherable };

        return new object?[] { gatherable, ConvertTo(quantity, parameters[1].ParameterType) };
    }

    private static bool CanPassQuantity(Type parameterType)
    {
        var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string FormatMethodSignature(MethodInfo method)
        => $"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name))})";

    private static bool TryGetGatherableTotalCount(Assembly assembly, object gatherable, out int totalCount, out string detail)
    {
        totalCount = 0;
        var extensionType = assembly.GetType("GatherBuddy.AutoGather.Extensions.GatherableExtensions");
        var method = extensionType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "GetTotalCount")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(gatherable);
            });

        method ??= extensionType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "GetInventoryCount")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(gatherable);
            });

        if (method == null)
        {
            detail = "GatherBuddy Reborn gatherable count method not found.";
            return false;
        }

        try
        {
            var value = method.Invoke(null, new[] { gatherable });
            totalCount = Convert.ToInt32(value);
            detail = $"GatherBuddy Reborn count read by {method.Name}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"GatherBuddy Reborn gatherable count failed: {ex.Message}";
            return false;
        }
    }

    private static object? SelectPreferredLocation(object gatherable, uint gatherJobId)
    {
        if (gatherJobId == 0 || GetMember<object>(gatherable, "Locations") is not IEnumerable locations)
            return null;

        foreach (var location in locations)
        {
            if (location != null && LocationMatchesGatherJob(location, gatherJobId))
                return location;
        }

        return null;
    }

    private static bool LocationMatchesGatherJob(object location, uint gatherJobId)
    {
        var gatheringType = GetMember<object>(location, "GatheringType")?.ToString() ?? string.Empty;
        return gatherJobId switch
        {
            ClassJobOptions.Miner => gatheringType is "Miner" or "Mining" or "Quarrying",
            ClassJobOptions.Botanist => gatheringType is "Botanist" or "Logging" or "Harvesting",
            ClassJobOptions.Fisher => gatheringType is "Fisher" or "Spearfishing",
            _ => false
        };
    }

    private static bool TrySetPreferredLocation(object list, object gatherable, object location, out string detail)
    {
        var method = list.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SetPreferredLocation")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType.IsInstanceOfType(gatherable) &&
                       parameters[1].ParameterType.IsInstanceOfType(location);
            });

        if (method == null)
        {
            detail = "GatherBuddy Reborn AutoGatherList.SetPreferredLocation method not found.";
            return false;
        }

        method.Invoke(list, new[] { gatherable, location });
        detail = "GatherBuddy Reborn preferred location set.";
        return true;
    }

    private static bool ReadActiveItems(object manager, out List<ActiveGatherItem> activeItems, out string detail)
    {
        activeItems = new List<ActiveGatherItem>();
        if (GetMember<object>(manager, "ActiveItems") is not IEnumerable enumerable)
        {
            detail = "GatherBuddy Reborn active item list not found.";
            return false;
        }

        foreach (var entry in enumerable)
        {
            if (entry == null)
                continue;

            var item = GetTupleValue(entry, 0, "Item");
            if (item == null || !TryGetUIntMember(item, "ItemId", out var itemId))
            {
                detail = $"GatherBuddy Reborn active item entry could not be read: {entry.GetType().FullName}.";
                return false;
            }

            var quantity = 0u;
            var quantityValue = GetTupleValue(entry, 1, "Quantity");
            if (quantityValue != null)
                quantity = Convert.ToUInt32(quantityValue);

            activeItems.Add(new ActiveGatherItem(itemId, quantity, item.GetType().FullName ?? item.GetType().Name));
        }

        detail = "GatherBuddy Reborn active item list read.";
        return true;
    }

    private static object? GetTupleValue(object tuple, int index, string memberName)
    {
        if (tuple is ITuple runtimeTuple && runtimeTuple.Length > index)
            return runtimeTuple[index];

        return GetMember<object>(tuple, memberName) ?? GetMember<object>(tuple, $"Item{index + 1}");
    }

    private static string FormatActiveItems(IReadOnlyCollection<ActiveGatherItem> activeItems)
        => activeItems.Count == 0
            ? "none"
            : string.Join(", ", activeItems.Select(item => $"{item.ItemId}x{item.Quantity} ({item.ItemType})"));

    private static string DescribeLocation(object? location, uint gatherJobId)
    {
        if (location == null)
            return $"id=none; type=none; territory=none; configuredJob={FormatGatherJob(gatherJobId)}";

        var id = TryGetUIntMember(location, "Id", out var locationId) ? locationId.ToString() : "unknown";
        var type = GetMember<object>(location, "GatheringType")?.ToString() ?? "unknown";
        var territory = GetMember<object>(location, "Territory");
        var territoryId = territory != null && TryGetUIntMember(territory, "Id", out var parsedTerritory)
            ? parsedTerritory.ToString()
            : "unknown";

        return $"id={id}; type={type}; territory={territoryId}; configuredJob={FormatGatherJob(gatherJobId)}";
    }

    private static bool IsManagedList(object list)
        => IsManagedListName(GetMember<string>(list, "Name") ?? string.Empty);

    private static bool IsManagedListName(string name)
        => name.StartsWith(ManagedListPrefix, StringComparison.Ordinal);

    private static string FormatListName(object list)
    {
        var name = GetMember<string>(list, "Name") ?? "(unnamed)";
        return $"'{name}'";
    }

    private static string FormatGatherJob(uint jobId)
        => jobId == 0 ? "unavailable (0)" : $"{ClassJobOptions.GetName(jobId)} ({jobId})";

    private static bool TryGetUIntMember(object root, string name, out uint value)
    {
        value = 0;
        var raw = GetMember<object>(root, name);
        if (raw == null)
            return false;

        try
        {
            value = Convert.ToUInt32(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? FindInLookup(object? lookup, uint itemId)
    {
        if (lookup == null)
            return null;

        var tryGetValue = lookup.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryGetValue" && method.GetParameters().Length == 2);
        if (tryGetValue != null)
        {
            var parameters = tryGetValue.GetParameters();
            var key = Convert.ChangeType(itemId, parameters[0].ParameterType);
            var valueType = parameters[1].ParameterType.IsByRef
                ? parameters[1].ParameterType.GetElementType()!
                : parameters[1].ParameterType;
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            var args = new object?[] { key, value };
            var found = tryGetValue.Invoke(lookup, args) is true;
            return found ? args[1] : null;
        }

        if (lookup is IDictionary dictionary && dictionary.Contains(itemId))
            return dictionary[itemId];

        return null;
    }

    private static IEnumerable<object> EnumerateAutoGatherLists(object manager)
    {
        foreach (var memberName in new[] { "Lists", "AutoGatherLists" })
        {
            if (GetMember<object>(manager, memberName) is IEnumerable enumerable)
            {
                foreach (var entry in enumerable)
                {
                    var list = CoerceAutoGatherList(entry);
                    if (list != null)
                        yield return list;
                }

                yield break;
            }
        }

        if (manager is IEnumerable managerEnumerable)
        {
            foreach (var entry in managerEnumerable)
            {
                var list = CoerceAutoGatherList(entry);
                if (list != null)
                    yield return list;
            }

            yield break;
        }

        var fileSystem = FindMemberValue(manager, value => value is IEnumerable);
        if (fileSystem is not IEnumerable fileSystemEnumerable)
            yield break;

        foreach (var entry in fileSystemEnumerable)
        {
            var list = CoerceAutoGatherList(entry);
            if (list != null)
                yield return list;
        }
    }

    private static object? CoerceAutoGatherList(object? entry)
    {
        if (entry == null)
            return null;

        if (entry.GetType().FullName == "GatherBuddy.AutoGather.Lists.AutoGatherList")
            return entry;

        var key = GetMember<object>(entry, "Key");
        if (key?.GetType().FullName == "GatherBuddy.AutoGather.Lists.AutoGatherList")
            return key;

        var value = GetMember<object>(entry, "Value");
        return value?.GetType().FullName == "GatherBuddy.AutoGather.Lists.AutoGatherList"
            ? value
            : null;
    }

    private static object? FindObjectInMembers(object root, Func<object, bool> predicate, int depth)
    {
        if (root == null || depth < 0)
            return null;

        if (predicate(root))
            return root;

        foreach (var value in EnumerateMemberValues(root))
        {
            if (value == null)
                continue;

            if (predicate(value))
                return value;

            var type = value.GetType();
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
                continue;

            var nested = FindObjectInMembers(value, predicate, depth - 1);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static object? FindMemberValue(object root, Func<object, bool> predicate)
        => EnumerateMemberValues(root).FirstOrDefault(value => value != null && predicate(value));

    private static IEnumerable<object?> EnumerateMemberValues(object root)
    {
        var type = root.GetType();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object? value;
            try { value = field.GetValue(root); }
            catch { continue; }
            yield return value;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? value;
            try { value = property.GetValue(root); }
            catch { continue; }
            yield return value;
        }
    }

    private static T? GetMember<T>(object root, string name)
    {
        var type = root.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null)
        {
            try
            {
                var value = property.GetValue(root);
                return value is T typed ? typed : default;
            }
            catch
            {
            }
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            try
            {
                var value = field.GetValue(root);
                return value is T typed ? typed : default;
            }
            catch
            {
            }
        }

        return default;
    }

    private static T? GetStaticMember<T>(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property != null)
        {
            var value = property.GetValue(null);
            return value is T typed ? typed : default;
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null)
        {
            var value = field.GetValue(null);
            return value is T typed ? typed : default;
        }

        return default;
    }

    private static void SetMember(object root, string name, object value)
    {
        var type = root.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null && property.CanWrite)
        {
            property.SetValue(root, ConvertTo(value, property.PropertyType));
            return;
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(root, ConvertTo(value, field.FieldType));
    }

    private static object ConvertTo(object value, Type targetType)
    {
        if (targetType.IsInstanceOfType(value))
            return value;

        return Convert.ChangeType(value, targetType);
    }
}
