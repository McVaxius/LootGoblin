using System;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace LootGoblin.Services;

public class MountService
{
    private readonly Plugin _plugin;
    private long _mountCooldownMs;
    private long _landingToggleCooldownMs;

    public MountService(Plugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// Mount the player with a specific mount name
    /// </summary>
    public void Mount(string mountName = "Company Chocobo")
    {
        if (Environment.TickCount64 < _mountCooldownMs)
        {
            _plugin.AddDebugLog($"Mount command on cooldown ({(_mountCooldownMs - Environment.TickCount64) / 1000.0:F1}s)");
            return;
        }

        if (IsMounted())
        {
            _plugin.AddDebugLog("Already mounted, skipping mount command");
            return;
        }

        _mountCooldownMs = Environment.TickCount64 + 2000; // 2s cooldown

        try
        {
            var command = string.IsNullOrEmpty(mountName) || mountName == "Mount Roulette" 
                ? "/mount \"Company Chocobo\"" 
                : $"/mount \"{mountName}\"";

            SendCommand(command);
            _plugin.AddDebugLog($"Mounting: {mountName}");
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"Mount command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Dismount the player
    /// </summary>
    public void Dismount()
    {
        if (Environment.TickCount64 < _mountCooldownMs)
        {
            _plugin.AddDebugLog($"Dismount command on cooldown ({(_mountCooldownMs - Environment.TickCount64) / 1000.0:F1}s)");
            return;
        }

        if (!IsMounted())
        {
            _plugin.AddDebugLog("Not mounted, skipping dismount command");
            return;
        }

        _mountCooldownMs = Environment.TickCount64 + 1500; // 1.5s cooldown

        try
        {
            SendCommand("/mount");
            _plugin.AddDebugLog("Dismounting...");
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"Dismount command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggle mount state specifically for landing recovery without inheriting the
    /// general dismount cooldown used by other mount flows.
    /// </summary>
    public void TryLandingToggle()
    {
        if (Environment.TickCount64 < _landingToggleCooldownMs)
            return;

        if (!IsMounted())
            return;

        _landingToggleCooldownMs = Environment.TickCount64 + 1000;

        try
        {
            SendCommand("/mount");
            _plugin.AddDebugLog("Landing toggle sent via /mount");
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"Landing toggle failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggle mount state (mount if dismounted, dismount if mounted)
    /// </summary>
    public void ToggleMount()
    {
        if (IsMounted())
        {
            Dismount();
        }
        else
        {
            Mount();
        }
    }

    /// <summary>
    /// Check if player is currently mounted
    /// </summary>
    public bool IsMounted()
    {
        return Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];
    }

    /// <summary>
    /// Check if player is currently flying
    /// </summary>
    public bool IsFlying()
    {
        return Plugin.Condition[ConditionFlag.InFlight];
    }

    /// <summary>
    /// Force land by toggling mount repeatedly
    /// </summary>
    public async void ForceLand()
    {
        if (!IsMounted())
        {
            _plugin.AddDebugLog("Not mounted, cannot land");
            return;
        }

        if (!IsFlying())
        {
            _plugin.AddDebugLog("Not flying, already landed");
            return;
        }

        _plugin.AddDebugLog("Force landing by toggling mount...");

        var attempts = 0;
        var maxAttempts = 5;

        while (attempts < maxAttempts && IsMounted())
        {
            SendCommand("/mount");
            _plugin.AddDebugLog($"Landing attempt {attempts + 1}/{maxAttempts}");
            attempts++;
            await System.Threading.Tasks.Task.Delay(1000);
        }

        // Wait a bit more to ensure landing is complete
        await System.Threading.Tasks.Task.Delay(2000);

        if (!IsMounted())
        {
            _plugin.AddDebugLog("Successfully landed");
        }
        else
        {
            _plugin.AddDebugLog("Failed to land after multiple attempts");
        }
    }

    private static unsafe void SendCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                Plugin.Log.Error("UIModule is null, cannot send command");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Mount command failed [{command}]: {ex.Message}");
        }
    }
}
