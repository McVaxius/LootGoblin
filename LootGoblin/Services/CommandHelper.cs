using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace LootGoblin.Services;

public static class CommandHelper
{
    public static unsafe void SendCommand(string command)
    {
        TrySendCommand(command);
    }

    public static unsafe void SendChatCommand(string command)
    {
        TrySendChatCommand(command);
    }

    public static async Task SendCommandOnFrameworkThreadAsync(string command)
    {
        await Plugin.Framework.RunOnFrameworkThread(() => SendCommand(command)).ConfigureAwait(false);
    }

    public static unsafe bool TrySendCommand(string command)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            {
                Plugin.LogWarning($"Skipped command while not logged in: {command}");
                return false;
            }

            LootGoblinActionTrace.Record("chat-command", command);

            if (Plugin.CommandManager.ProcessCommand(command))
                return true;

            return TryProcessChatBoxEntry(command);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Command failed [{command}]: {ex.Message}");
            return false;
        }
    }

    public static unsafe bool TrySendChatCommand(string command)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            {
                Plugin.LogWarning($"Skipped command while not logged in: {command}");
                return false;
            }

            LootGoblinActionTrace.Record("chat-command", command);

            return TryProcessChatBoxEntry(command);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Command failed [{command}]: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool TryProcessChatBoxEntry(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            Plugin.LogError("UIModule is null, cannot send command");
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(command);
        var utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromSequence(bytes);
        uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
        return true;
    }

    public static string FormatVector(Vector3 value)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2}", value.X, value.Y, value.Z);
    }
}
