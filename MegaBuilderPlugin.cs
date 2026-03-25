using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.IO;
using UnityEngine;

namespace MegaBuilder
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaBuilderPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.rik.megabuilder";
        public const string PluginName = "Mega Builder";
        public const string PluginVersion = "1.0.12";

        internal static ManualLogSource Log;
        private static Harmony _harmony;
        private static ConfigFile _config;
        private static FileSystemWatcher _configWatcher;

        // Grid Alignment
        public static ConfigEntry<bool> EnableGridAlignment;
        public static ConfigEntry<KeyCode> GridToggleKey;
        public static ConfigEntry<KeyCode> GridSizeCycleKey;

        // Debug
        public static ConfigEntry<bool> DebugMode;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            _config = Config;
            BindConfig();
            SetupConfigWatcher();

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void BindConfig()
        {
            EnableGridAlignment = _config.Bind("1. Grid Alignment", "Enable", true,
                "Enable grid alignment feature for building placement");

            GridToggleKey = _config.Bind("1. Grid Alignment", "ToggleKey", KeyCode.F7,
                "Key to toggle grid alignment on/off");

            GridSizeCycleKey = _config.Bind("1. Grid Alignment", "CycleGridSizeKey", KeyCode.F6,
                "Key to cycle grid size (0.5 → 1 → 2 → 4)");

            DebugMode = _config.Bind("9. Debug", "DebugMode", false,
                "Enable verbose debug logging for grid alignment (check BepInEx console/log)");
        }

        private void SetupConfigWatcher()
        {
            var configDir = Path.GetDirectoryName(_config.ConfigFilePath);
            var configFile = Path.GetFileName(_config.ConfigFilePath);

            _configWatcher = new FileSystemWatcher(configDir, configFile);
            _configWatcher.Changed += (_, __) =>
            {
                _config.Reload();
                Log.LogInfo("Config reloaded.");
            };
            _configWatcher.EnableRaisingEvents = true;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _configWatcher?.Dispose();
        }
    }
}
