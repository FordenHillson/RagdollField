using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using DeliriumRavenfield.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliriumRavenfield
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.fordg.deliriumravenfield";
        public const string PluginName = "Delirium Ravenfield";
        public const string PluginVersion = "1.0.0";

        public static ManualLogSource Log { get; private set; }
        public static ModConfig ConfigInstance { get; private set; }
        public static DeliriumRuntime Runtime { get; private set; }
        public static LodManager LodManager { get; private set; }

        private Harmony harmony;
        private TestPanelUI testUi;

        private void Awake()
        {
            Log = Logger;
            ConfigInstance = new ModConfig(Config);
            Runtime = new DeliriumRuntime(ConfigInstance);

            string pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            if (!Runtime.Initialize(pluginDir))
            {
                Log.LogError("[DeliriumRavenfield] Failed to initialize native Delirium engine. Plugin disabled.");
                enabled = false;
                return;
            }

            LodManager = new LodManager(Runtime, ConfigInstance);

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Patches.RavenfieldPatches).Assembly);

            testUi = gameObject.AddComponent<TestPanelUI>();
            testUi.Initialize(ConfigInstance, Runtime, LodManager);

            SceneManager.sceneUnloaded += OnSceneUnloaded;

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded! UI: {ConfigInstance.UiHotkey.Value} | TestRange: {ConfigInstance.TestRangeHotkey.Value} | Push: {ConfigInstance.AimPushHotkey.Value} | Release: {ConfigInstance.ReleaseAllHotkey.Value}");
        }

        private void Update()
        {
            if (LodManager == null || Runtime == null || !Runtime.IsReady) return;
            LodManager.TickUpdate(Time.deltaTime);
        }

        private void LateUpdate()
        {
            if (LodManager == null || Runtime == null || !Runtime.IsReady) return;
            LodManager.TickLateUpdate();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            LodManager?.ReleaseAll(restoreGame: false);
            Runtime?.ClearAllWorlds();
        }

        private void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            harmony?.UnpatchSelf();
            LodManager?.Dispose();
            Runtime?.Dispose();
        }
    }
}
