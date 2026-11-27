using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using DeliriumRavenfield.Sync;
using DeliriumRavenfield.TestArena;
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
        public const string PluginVersion = "1.2.0";

        public static ManualLogSource Log { get; private set; }
        public static ModConfig ConfigInstance { get; private set; }
        public static DeliriumRuntime Runtime { get; private set; }
        public static LodManager LodManager { get; private set; }
        public static TestArenaManager TestArenaManager { get; private set; }
        public static PlayroomSyncClient PlayroomSync { get; private set; }

        private Harmony harmony;
        private TestPanelUI testUi;
        private DeliriumDebugOverlay debugOverlay;

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
            TestArenaManager = new TestArenaManager(ConfigInstance, LodManager);
            PlayroomSync = new PlayroomSyncClient(Runtime, LodManager, ConfigInstance);

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Patches.RavenfieldPatches).Assembly);

            debugOverlay = gameObject.AddComponent<DeliriumDebugOverlay>();
            debugOverlay.Initialize(Runtime, LodManager);

            testUi = gameObject.AddComponent<TestPanelUI>();
            testUi.Initialize(ConfigInstance, Runtime, LodManager, TestArenaManager, debugOverlay);

            SceneManager.sceneUnloaded += OnSceneUnloaded;

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded! UI: {ConfigInstance.UiHotkey.Value} | Dummy: {ConfigInstance.SpawnDummyHotkey.Value} | TestRange: {ConfigInstance.TestRangeHotkey.Value} | Push: {ConfigInstance.AimPushHotkey.Value} | Release: {ConfigInstance.ReleaseAllHotkey.Value}");
        }

        private void Update()
        {
            if (LodManager == null || Runtime == null || !Runtime.IsReady) return;
            PlayroomSync?.TickUpdate(Time.deltaTime);
            LodManager.TickUpdate(Time.deltaTime);
            TestArenaManager?.TickUpdate(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            if (LodManager == null || Runtime == null || !Runtime.IsReady) return;
            LodManager.TickFixedUpdate();
        }

        private void LateUpdate()
        {
            if (LodManager == null || Runtime == null || !Runtime.IsReady) return;
            LodManager.TickLateUpdate();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // Ignore unloading of an old menu/loading scene that finishes AFTER our new map already loaded!
            Scene active = SceneManager.GetActiveScene();
            bool unloadingOldWhileMirrorActive = PlayroomSync != null && PlayroomSync.IsInMirrorMap &&
                                                 PlayroomSync.MirrorSceneHandle != 0 && scene.handle != PlayroomSync.MirrorSceneHandle;
            bool unloadingOldWhileArenaActive = TestArenaManager != null && TestArenaManager.IsInTestArena &&
                                                TestArenaManager.ArenaSceneHandle != 0 && scene.handle != TestArenaManager.ArenaSceneHandle;
            if (unloadingOldWhileMirrorActive || unloadingOldWhileArenaActive)
                return;

            if (PlayroomSync != null && (PlayroomSync.MirrorSceneHandle == 0 || scene.handle == PlayroomSync.MirrorSceneHandle))
                PlayroomSync.OnSceneUnloaded();
            if (TestArenaManager != null && (TestArenaManager.ArenaSceneHandle == 0 || scene.handle == TestArenaManager.ArenaSceneHandle))
                TestArenaManager.OnSceneUnloaded();

            // Only tear down worlds/controllers when leaving the active gameplay scene (not when an auxiliary scene unloads)
            if (!active.IsValid() || active.handle == scene.handle)
            {
                LodManager?.ReleaseAll(restoreGame: false);
                Runtime?.ClearAllWorlds();
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            harmony?.UnpatchSelf();
            PlayroomSync?.Dispose();
            LodManager?.Dispose();
            Runtime?.Dispose();
        }
    }
}
