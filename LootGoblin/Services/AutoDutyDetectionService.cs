using System;
using System.Linq;
using Dalamud.Plugin.Services;
using ECommons;
using LootGoblin.Windows;
using LootGoblin.Models;

namespace LootGoblin.Services;

public class AutoDutyDetectionService : IDisposable
{
    private readonly Plugin plugin;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly AutoDutyWarningWindow warningWindow;

    private DateTime lastCheck = DateTime.MinValue;
    private readonly TimeSpan checkInterval = TimeSpan.FromSeconds(5);
    private bool autoDutyDetected = false;
    private bool warningShown = false;

    public AutoDutyDetectionService(Plugin plugin, IChatGui chatGui, IFramework framework, IPluginLog log, AutoDutyWarningWindow warningWindow)
    {
        this.plugin = plugin;
        this.chatGui = chatGui;
        this.framework = framework;
        this.log = log;
        this.warningWindow = warningWindow;

        // Subscribe to framework update for periodic checking
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;
        if (now - lastCheck < checkInterval)
            return;
        
        lastCheck = now;
        CheckForAutoDuty();
    }

    private void CheckForAutoDuty()
    {
        try
        {
            // Method 1: Check if AutoDuty plugin is installed AND enabled
            bool autoDutyInstalled = false;
            bool autoDutyEnabled = false;
            try
            {
                var installedPlugins = Plugin.PluginInterface.InstalledPlugins;
                var autodutyPlugin = installedPlugins.FirstOrDefault(p => 
                    p.InternalName.Equals("AutoDuty", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains("AutoDuty", StringComparison.OrdinalIgnoreCase));
                
                 var ReActionPlugin = installedPlugins.FirstOrDefault(p => 
                    p.InternalName.Equals("ReAction", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains("ReAction", StringComparison.OrdinalIgnoreCase));
                
                if (autodutyPlugin != null)
                {
                    autoDutyInstalled = true;
                    autoDutyEnabled = autodutyPlugin.IsLoaded; // Check if actually loaded/enabled
                    
                    if (autoDutyEnabled)
                    {
                        log.Information("[LootGoblin.AutoDutyDetection] AutoDuty plugin is enabled and running");
                    }
                    else
                    {
                        log.Information("[LootGoblin.AutoDutyDetection] AutoDuty plugin is installed but disabled");
						if (ReActionPlugin != null)
						{
							autoDutyInstalled = true;
							autoDutyEnabled = ReActionPlugin.IsLoaded; // Check if actually loaded/enabled
						if (autoDutyEnabled)
						{
							log.Information("[LootGoblin.AutoDutyDetection] ReAction plugin is enabled and running");
						}
						else
						{
							log.Information("[LootGoblin.AutoDutyDetection] ReAction plugin is installed but disabled");
						}
						}
                    }
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[LootGoblin.AutoDutyDetection] Could not check installed plugins: {ex.Message}");
            }

            // Method 2: Try to detect AutoDuty IPC or specific chat patterns
            bool autoDutyActive = false;
            
            // Check for AutoDuty in chat (might show status messages)
            // This is a fallback method if plugin detection fails
            
            var wasDetected = autoDutyDetected;
            autoDutyDetected = autoDutyEnabled || autoDutyActive; // Only detect if actually enabled

            log.Debug($"[LootGoblin.AutoDutyDetection] Detection result: Installed={autoDutyInstalled}, Enabled={autoDutyEnabled}, Active={autoDutyActive}, Detected={autoDutyDetected}");

            // Log state changes and show warning if both conditions are met
            if (autoDutyDetected && !wasDetected)
            {
                log.Warning("[LootGoblin.AutoDutyDetection] AutoDuty plugin detected");
                // Don't show warning automatically - only when START is clicked
            }
            else if (!autoDutyDetected && wasDetected)
            {
                log.Information("[LootGoblin.AutoDutyDetection] AutoDuty plugin no longer detected - resetting warning state");
                warningShown = false;
                warningWindow.Reset();
                // Also close the window if it's open
                if (warningWindow.IsOpen)
                {
                    warningWindow.IsOpen = false;
                    log.Information("[LootGoblin.AutoDutyDetection] Closed warning window since AutoDuty is no longer detected");
                }
            }
            else if (autoDutyDetected && wasDetected)
            {
                log.Debug("[LootGoblin.AutoDutyDetection] AutoDuty still detected - no action taken");
                // Don't show warning automatically - only when START is clicked
            }
        }
        catch (Exception ex)
        {
            log.Error($"[LootGoblin.AutoDutyDetection] Error checking for AutoDuty: {ex.Message}");
        }
    }

    private void ShowWarning()
    {
        log.Debug($"[LootGoblin.AutoDutyDetection] ShowWarning called - warningShown={warningShown}");
        
        if (!warningShown)
        {
            warningShown = true;
            warningWindow.IsOpen = true;
            log.Warning("[LootGoblin.AutoDutyDetection] AutoDuty warning window opened");
        }
        else if (warningShown)
        {
            log.Debug("[LootGoblin.AutoDutyDetection] Warning already shown, not opening again");
        }
    }

    public bool IsAutoDutyDetected()
    {
        return autoDutyDetected;
    }

    public void ResetWarning()
    {
        warningShown = false;
        warningWindow.Reset();
    }

    public void ForceShowWarning()
    {
        log.Information("[LootGoblin.AutoDutyDetection] Force showing warning window");
        warningShown = false;
        ShowWarning();
    }

    public void ForceCheck()
    {
        log.Information("[LootGoblin.AutoDutyDetection] Force checking for AutoDuty");
        CheckForAutoDuty();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}
