using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

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
    private DateTime lastAvailabilityCheckUtc = DateTime.MinValue;

    public bool IsAvailable { get; private set; }
    public int Version { get; private set; }
    public string StatusText { get; private set; } = "Not checked.";
    public uint TargetItemId { get; private set; }
    public string TargetMapName { get; private set; } = string.Empty;
    public bool HasManagedList => managedList != null;

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

    public bool StartOneShot(uint itemId, string mapName, out string detail)
    {
        TargetItemId = 0;
        TargetMapName = string.Empty;
        CheckAvailability(logStatus: true);
        if (!IsAvailable)
        {
            detail = StatusText;
            return false;
        }

        try
        {
            DisableAutoGather();
            CleanupManagedLists();

            if (!TryCreateManagedList(itemId, mapName, out var list, out detail))
                return false;

            managedList = list;
            TargetItemId = itemId;
            TargetMapName = mapName;
            SetAutoGatherEnabled(true);
            detail = $"GatherBuddy Reborn gathering one {mapName}.";
            StatusText = detail;
            plugin.AddDebugLog($"[GatherBuddy] {detail}");
            return true;
        }
        catch (Exception ex)
        {
            detail = $"GatherBuddy Reborn start failed: {ex.Message}";
            StatusText = detail;
            Plugin.LogWarning($"[GatherBuddy] {detail}");
            Cancel();
            return false;
        }
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
            CleanupManagedLists();
        }
        catch (Exception ex)
        {
            log.Debug($"[GatherBuddy] Cleanup failed during cancel: {ex.Message}");
        }

        managedList = null;
        TargetItemId = 0;
        TargetMapName = string.Empty;
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

    private bool TryCreateManagedList(uint itemId, string mapName, out object list, out string detail)
    {
        list = null!;
        if (!TryGetReflectionContext(out var manager, out var assembly, out detail))
            return false;

        var gatherable = FindGatherable(assembly, itemId);
        if (gatherable == null)
        {
            detail = $"GatherBuddy Reborn does not expose gatherable item {itemId} ({mapName}).";
            return false;
        }

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
        SetMember(list, "Enabled", true);
        SetMember(list, "Fallback", false);
        SetMember(list, "RemoveCompletedItems", true);

        var addMethod = listType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "Add" && method.GetParameters().Length is 1 or 2);
        if (addMethod == null)
        {
            detail = "GatherBuddy Reborn AutoGatherList.Add method not found.";
            return false;
        }

        var parameters = addMethod.GetParameters();
        var args = parameters.Length == 1
            ? new[] { gatherable }
            : new[] { gatherable, Convert.ChangeType(1u, parameters[1].ParameterType) };
        var addResult = addMethod.Invoke(list, args);
        if (addResult is bool added && !added)
        {
            detail = $"GatherBuddy Reborn did not add {mapName} to the auto-gather list.";
            return false;
        }

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

    private void CleanupManagedLists()
    {
        if (!TryGetReflectionContext(out var manager, out _, out _))
            return;

        foreach (var list in EnumerateAutoGatherLists(manager).ToList())
        {
            var name = GetMember<string>(list, "Name") ?? string.Empty;
            if (!name.StartsWith(ManagedListPrefix, StringComparison.Ordinal))
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
            var value = parameters[1].ParameterType.IsByRef
                ? Activator.CreateInstance(parameters[1].ParameterType.GetElementType()!)
                : null;
            var args = new[] { key, value };
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
