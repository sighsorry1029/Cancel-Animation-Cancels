using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace ServerSyncModTemplate;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class ServerSyncModTemplatePlugin : BaseUnityPlugin
{
    internal const string ModName = "CancelAnimationCancels";
    internal const string ModVersion = "1.0.2";
    internal const string Author = "sighsorry";
    private const string ModGUID = "sighsorry.CancelAnimationCancel";
    private static string ConfigFileName = $"{ModGUID}.cfg";
    private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource ServerSyncModTemplateLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion, ModRequired = true };
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        try
        {
            _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            PreventBlockComboCarry = config("1 - General", "Prevent Block Combo Carry", Toggle.On, "If on, one-handed swords, knives, and one-handed clubs will lose combo carry when block is used between attacks.");
            PreventEmoteCancel = config("1 - General", "Prevent Emote Cancel", Toggle.On, "If on, recent attacks from affected weapons cannot be canceled into emotes, and combo carry will be reset if an emote still slips through.");
            BlockEmoteConsoleBinds = config("1 - General", "Block Emote Console Binds", Toggle.Off, "If on, console bind commands cannot assign emotes, and existing emote binds are disabled until this is turned off.");
            PreventDodgeAttackQueue = config("1 - General", "Prevent Dodge Attack Queue", Toggle.On, "If on, unarmed, spears, axes, battleaxes, and atgeirs will lose queued attacks and combo carry when a dodge starts, and dodge-cancel attack input will be blocked.");
            BlockEmoteConsoleBinds.SettingChanged += (_, _) => ComboChainResetPatches.RefreshConsoleBinds();

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        try
        {
            SaveWithRespectToConfigSet();
        }
        finally
        {
            _watcher?.Dispose();
        }
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.IncludeSubdirectories = true;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastConfigReloadTime.Ticks;
        if (time < RELOAD_DELAY)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                ServerSyncModTemplateLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                ServerSyncModTemplateLogger.LogDebug("Reloading configuration...");
                SaveWithRespectToConfigSet(true);
                ServerSyncModTemplateLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ServerSyncModTemplateLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            if (reload)
            {
                Config.Reload();
            }

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }


    #region ConfigOptions

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    internal static ConfigEntry<Toggle> PreventBlockComboCarry = null!;
    internal static ConfigEntry<Toggle> PreventEmoteCancel = null!;
    internal static ConfigEntry<Toggle> BlockEmoteConsoleBinds = null!;
    internal static ConfigEntry<Toggle> PreventDodgeAttackQueue = null!;

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"), description.AcceptableValues, description.Tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    #endregion
}

public static class KeyboardExtensions
{
    extension(KeyboardShortcut shortcut)
    {
        public bool IsKeyDown()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }

        public bool IsKeyHeld()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }
    }
}

public static class ToggleExtentions
{
    extension(ServerSyncModTemplatePlugin.Toggle value)
    {
        public bool IsOn()
        {
            return value == ServerSyncModTemplatePlugin.Toggle.On;
        }

        public bool IsOff()
        {
            return value == ServerSyncModTemplatePlugin.Toggle.Off;
        }
    }
}
