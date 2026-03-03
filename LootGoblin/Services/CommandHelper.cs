using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace LootGoblin.Services;

public static class CommandHelper
{
    public static unsafe void SendCommand(string command)
    {
        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
                return;

            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                Plugin.Log.Error("UIModule is null, cannot send command");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Command failed [{command}]: {ex.Message}");
        }
    }

    public static string FormatVector(Vector3 value)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2}", value.X, value.Y, value.Z);
    }
}
