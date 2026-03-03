# How to Import and Install Dalamud Plugins

This guide explains how to install custom Dalamud plugins like LootGoblin into FFXIV.

---

## Prerequisites

1. **FFXIV with XIVLauncher installed**
   - Download from: https://goatcorp.github.io/
   - Launch FFXIV through XIVLauncher at least once

2. **Dalamud enabled**
   - Dalamud loads automatically with XIVLauncher
   - You'll see the Dalamud plugin menu in-game

---

## Method 1: Install from Custom Repository (Recommended)

### Step 1: Add Custom Repository

1. **Open Dalamud Settings in-game:**
   - Press `Esc` or click the Dalamud icon
   - Click **"Dalamud Settings"**

2. **Navigate to Experimental tab:**
   - Click the **"Experimental"** tab
   - Scroll down to **"Custom Plugin Repositories"**

3. **Add LootGoblin repository:**
   - Click the **"+"** button
   - Paste this URL:
     ```
     https://raw.githubusercontent.com/McVaxius/LootGoblin/master/repo.json
     ```
   - Click **"Save"**

4. **Enable the repository:**
   - Make sure the checkbox next to the URL is **checked**
   - Click **"Save and Close"**

### Step 2: Install the Plugin

1. **Open Plugin Installer:**
   - Press `Esc` or click the Dalamud icon
   - Click **"Plugin Installer"**

2. **Search for LootGoblin:**
   - In the search box, type: `LootGoblin`
   - You should see **LootGoblin** in the list

3. **Install the plugin:**
   - Click **"Install"** next to LootGoblin
   - Wait for installation to complete
   - You'll see a success message

4. **Verify installation:**
   - Go to the **"Installed Plugins"** tab
   - LootGoblin should appear in the list
   - Make sure it's **enabled** (checkbox checked)

---

## Method 2: Manual Installation from Release

### Step 1: Download the Plugin

1. **Go to GitHub Releases:**
   - Visit: https://github.com/McVaxius/LootGoblin/releases
   - Find the latest release

2. **Download latest.zip:**
   - Click on **`latest.zip`** to download
   - Save it to your Downloads folder

### Step 2: Extract to Plugins Folder

1. **Locate your Dalamud plugins folder:**
   - Default location:
     ```
     %APPDATA%\XIVLauncher\installedPlugins\
     ```
   - Or navigate to:
     ```
     C:\Users\[YourUsername]\AppData\Roaming\XIVLauncher\installedPlugins\
     ```

2. **Create LootGoblin folder:**
   - Inside `installedPlugins`, create a new folder named: `LootGoblin`
   - Full path should be:
     ```
     %APPDATA%\XIVLauncher\installedPlugins\LootGoblin\
     ```

3. **Extract the zip:**
   - Open the downloaded `latest.zip`
   - Extract **all contents** directly into the `LootGoblin` folder
   - You should see files like:
     - `LootGoblin.dll`
     - `LootGoblin.json`
     - `images/icon.png`

### Step 3: Load the Plugin

1. **Restart the game or reload Dalamud:**
   - Type in chat: `/xlplugins`
   - Click **"Installed Plugins"** tab
   - Click **"Scan Dev Plugins"** button

2. **Enable LootGoblin:**
   - Find **LootGoblin** in the list
   - Check the box to enable it
   - You should see a success message

---

## Method 3: Install from Source (Developers)

### Step 1: Clone the Repository

```bash
cd d:\temp
git clone https://github.com/McVaxius/LootGoblin.git
cd LootGoblin
```

### Step 2: Build the Plugin

```bash
dotnet restore
dotnet build -c Release
```

### Step 3: Copy to Dalamud

1. **Locate build output:**
   ```
   LootGoblin\bin\x64\Release\LootGoblin\
   ```

2. **Copy to installedPlugins:**
   - Copy the entire `LootGoblin` folder to:
     ```
     %APPDATA%\XIVLauncher\installedPlugins\LootGoblin\
     ```

3. **Reload Dalamud** (see Method 2, Step 3)

---

## Using LootGoblin

### Opening the Plugin

Once installed, you can open LootGoblin using:

- **Slash command:** `/lootgoblin` or `/lg`
- **Plugin menu:** Dalamud menu → Installed Plugins → LootGoblin

### First-Time Setup

1. **Open Settings:**
   - Click the **gear icon** in the main window
   - Or use command: `/lg config`

2. **Configure basic settings:**
   - Enable the plugin
   - Set your preferences
   - Save configuration

3. **Verify dependencies:**
   - Check if GlobeTrotter is installed (recommended)
   - Check if vnavmesh is installed (required)
   - Install missing dependencies if needed

### Basic Usage

1. **Have treasure maps in inventory**
2. **Open LootGoblin main window:** `/lg`
3. **Select maps to run**
4. **Click "Start Bot"**
5. **Monitor progress in status window**

---

## Troubleshooting

### Plugin Not Appearing

**Problem:** LootGoblin doesn't show up in Plugin Installer

**Solutions:**
1. Verify custom repository URL is correct
2. Make sure repository is enabled (checkbox checked)
3. Click "Refresh" in Plugin Installer
4. Restart the game

---

### Installation Failed

**Problem:** Error message during installation

**Solutions:**
1. Check your internet connection
2. Verify you have the latest XIVLauncher version
3. Try manual installation (Method 2)
4. Check Dalamud log for errors: `/xllog`

---

### Plugin Won't Load

**Problem:** Plugin installed but won't enable

**Solutions:**
1. Check Dalamud log: `/xllog`
2. Verify all files extracted correctly
3. Make sure .NET 10.0 runtime is installed
4. Try reinstalling the plugin
5. Check for conflicting plugins

---

### Missing Dependencies

**Problem:** "GlobeTrotter not found" or "vnavmesh not found"

**Solutions:**
1. **Install GlobeTrotter:**
   - Open Plugin Installer
   - Search for "GlobeTrotter"
   - Click Install

2. **Install vnavmesh:**
   - Open Plugin Installer
   - Search for "vnavmesh"
   - Click Install

3. **Restart LootGoblin** after installing dependencies

---

### Plugin Crashes Game

**Problem:** Game crashes when using LootGoblin

**Solutions:**
1. Disable LootGoblin immediately
2. Check Dalamud log for error details
3. Report the issue on GitHub with log file
4. Wait for update/fix
5. Try older version if available

---

### Configuration Not Saving

**Problem:** Settings reset after closing game

**Solutions:**
1. Make sure you click "Save" in settings
2. Check file permissions in:
   ```
   %APPDATA%\XIVLauncher\pluginConfigs\LootGoblin.json
   ```
3. Try running game as administrator
4. Verify antivirus isn't blocking file writes

---

## Updating LootGoblin

### Automatic Updates (Custom Repo)

If installed via custom repository:
1. Updates appear automatically in Plugin Installer
2. Click **"Update"** button when available
3. Restart game to apply update

### Manual Updates

If installed manually:
1. Download latest `latest.zip` from GitHub releases
2. Delete old files in `installedPlugins\LootGoblin\`
3. Extract new files to the folder
4. Reload Dalamud: `/xlplugins` → Scan Dev Plugins

---

## Uninstalling LootGoblin

### Via Plugin Installer

1. Open Plugin Installer
2. Go to **"Installed Plugins"** tab
3. Find **LootGoblin**
4. Click **"Disable"** then **"Delete"**
5. Confirm deletion

### Manual Uninstall

1. Navigate to:
   ```
   %APPDATA%\XIVLauncher\installedPlugins\
   ```
2. Delete the `LootGoblin` folder
3. Reload Dalamud

---

## Getting Help

### Support Channels

- **GitHub Issues:** https://github.com/McVaxius/LootGoblin/issues
- **Discord:** (if available)
- **Reddit:** r/FFXIVDalamud

### Before Asking for Help

1. Check this guide thoroughly
2. Search existing GitHub issues
3. Check Dalamud log: `/xllog`
4. Note your game version and Dalamud version
5. List other installed plugins

### Reporting Bugs

Include in your report:
1. **LootGoblin version number**
2. **Steps to reproduce the issue**
3. **Expected behavior**
4. **Actual behavior**
5. **Dalamud log file** (if crash/error)
6. **Screenshots** (if UI issue)

---

## Additional Resources

- **Dalamud Documentation:** https://dalamud.dev
- **XIVLauncher FAQ:** https://goatcorp.github.io/faq
- **Plugin Development:** https://github.com/goatcorp/SamplePlugin
- **LootGoblin GitHub:** https://github.com/McVaxius/LootGoblin

---

## Safety and Terms of Service

**Important Notes:**

1. **Use at your own risk**
   - Plugins modify game behavior
   - Square Enix does not officially support plugins

2. **Be responsible**
   - Don't use plugins to harass other players
   - Don't use plugins to gain unfair advantages in competitive content
   - Be respectful of the community

3. **Stay updated**
   - Keep plugins updated for compatibility
   - Check for updates after game patches
   - Disable plugins if they cause issues

4. **Backup your data**
   - Configuration files are stored locally
   - Consider backing up settings periodically

---

**Last Updated:** 2026-03-03  
**Plugin Version:** 0.0.1 (Initial Release)

