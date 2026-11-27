using System;
using System.Globalization;
using System.Text;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using DeliriumRavenfield.TestArena;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.UI
{
    public sealed class TestPanelUI : MonoBehaviour
    {
        private ModConfig config;
        private DeliriumRuntime runtime;
        private LodManager lod;
        private TestArenaManager arena;
        private DeliriumDebugOverlay debugOverlay;

        private bool panelOpen;
        private bool jointInspectorOpen;
        private Rect windowRect = new Rect(20, 20, 490, 960);
        private Rect inspectorRect = new Rect(522, 20, 580, 650);
        private Vector2 scrollPos;
        private Vector2 inspectorScrollPos;
        private int selectedJoint;
        private string lastRigCheckOutput = "";
        private Actor lastAimedActor;
        private ActorRagdollController traceCtrl;
        private string tracePath = "";
        private float traceElapsed;
        private float traceNextSample;

        // Reusable native buffers & textures for Playroom-style Joint Inspector
        private readonly float[] effActual = new float[3];
        private readonly float[] effDesired = new float[3];
        private readonly float[] effLimits = new float[6];
        private readonly float[] effStats = new float[4];
        private readonly int[] effParts = new int[2];
        private readonly byte[] effNameBuf = new byte[64];
        private Texture2D solidTex;
        private GUIStyle monoStyle;

        public void Initialize(ModConfig cfg, DeliriumRuntime rt, LodManager lodManager, TestArenaManager arenaManager, DeliriumDebugOverlay overlay = null)
        {
            config = cfg;
            runtime = rt;
            lod = lodManager;
            arena = arenaManager;
            debugOverlay = overlay;
            panelOpen = cfg.UiStartOpen.Value;
        }

        private void OnDestroy()
        {
            if (solidTex != null)
            {
                Destroy(solidTex);
                solidTex = null;
            }
        }

        private void Update()
        {
            if (config == null || runtime == null || lod == null) return;

            if (traceCtrl != null)
            {
                traceElapsed += Time.deltaTime;
                if (traceElapsed >= traceNextSample)
                {
                    RigDiagnostics.AppendTraceSample(tracePath, traceElapsed, traceCtrl);
                    traceNextSample += 0.10f;
                }
                if (traceElapsed >= 3.05f || traceCtrl.Actor == null)
                {
                    traceCtrl = null;
                    lastRigCheckOutput = $"Rig + 3s Joint Trace saved to:\n{tracePath}";
                    Plugin.Log.LogInfo($"[RigDiagnostics] Completed 3s trace -> {tracePath}");
                }
            }

            if (Input.GetKeyDown(config.UiHotkey.Value))
                panelOpen = !panelOpen;

            if (Input.GetKeyDown(config.TestRangeHotkey.Value))
            {
                lod.BotMode = lod.BotMode == TestBotMode.Normal ? TestBotMode.PureDummy : TestBotMode.Normal;
                Plugin.Log.LogInfo($"[Delirium] Bot Test Mode: {lod.BotMode}");
            }

            if (Input.GetKeyDown(config.SpawnDummyHotkey.Value) && arena != null)
            {
                var spawned = arena.SpawnDummyAtCrosshair();
                if (spawned != null)
                {
                    lastAimedActor = spawned;
                    lastRigCheckOutput = $"Placed stationary dummy '{spawned.name}' at crosshair.";
                }
            }

            if (Input.GetKeyDown(config.AimPushHotkey.Value))
            {
                var actor = FindAimedActor();
                if (actor != null)
                {
                    var ctrl = lod.TryAcquireForLiveReaction(actor) ?? lod.TryAcquireForFallOrDeath(actor);
                    var cam = Camera.main;
                    Vector3 pushDir = cam != null ? cam.transform.forward : Vector3.forward;
                    ctrl?.ApplyPush(pushDir * config.AimPushForce.Value, balance: true);
                }
            }

            if (Input.GetKeyDown(config.ReleaseAllHotkey.Value))
            {
                lod.ReleaseAll(restoreGame: true);
                Plugin.Log.LogInfo("[Delirium] Released all ragdolls back to Ravenfield.");
            }
        }

        private void OnGUI()
        {
            if (config == null || runtime == null || lod == null) return;

            if (arena != null && config.ShowMainMenuTestButton.Value && IsOnMainMenu())
            {
                DrawMainMenuQuickLaunch();
            }

            if (config.DebugHud.Value && !panelOpen && !IsOnMainMenu())
            {
                DrawCompactHud();
            }

            if (panelOpen)
            {
                windowRect = GUI.Window(948271, windowRect, DrawWindow, "Delirium Physics — Ravenfield (Locational Overhaul)");
            }

            if (jointInspectorOpen && !IsOnMainMenu())
            {
                inspectorRect = GUI.Window(948272, inspectorRect, DrawJointInspectorWindow, "Joint Inspector (Delirium Playroom)");
            }
        }

        private static bool IsOnMainMenu()
        {
            try
            {
                return GameManager.instance != null && GameManager.IsInMainMenu();
            }
            catch
            {
                return false;
            }
        }

        private void DrawMainMenuQuickLaunch()
        {
            const float btnW = 240f;
            const float btnH = 34f;
            Rect rect = new Rect(Screen.width - btnW - 18f, 16f, btnW, btnH);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.95f, 0.52f, 0.14f, 0.95f);
            if (GUI.Button(rect, "Delirium: Launch Test Range"))
            {
                arena.LaunchTestArena();
            }
            if (Plugin.PlayroomSync != null)
            {
                Rect syncRect = new Rect(Screen.width - btnW - 18f, 56f, btnW, btnH);
                GUI.backgroundColor = new Color(0.16f, 0.68f, 0.92f, 0.95f);
                if (GUI.Button(syncRect, "Delirium: Playroom Mirror Map"))
                {
                    Plugin.PlayroomSync.LaunchPlayroomMirrorMap();
                }
            }
            GUI.backgroundColor = prev;
        }

        private void DrawCompactHud()
        {
            string line = $"[Delirium] Active: {lod.ActiveSimulatingCount}/{config.MaxConcurrentAgents.Value} | " +
                          $"Wounded: {lod.WoundedTrackedCount} | Frozen: {lod.FrozenCorpseCount} | " +
                          $"Step: {runtime.LastStepMs:F2}ms | BotMode(F8): {lod.BotMode}";
            GUI.Box(new Rect(12, 12, 660, 26), line);

            var aimed = FindAimedActor();
            if (aimed != null && lod.TryGetHealthState(aimed, out var hs))
            {
                string info = $"{aimed.name} | {hs.GetCompactSummary()}\n{hs.GetDetailedZonesLine()}";
                GUI.Box(new Rect(12, 42, 480, 58), info);
            }
        }

        private void DrawWindow(int id)
        {
            scrollPos = GUILayout.BeginScrollView(scrollPos);
            GUILayout.BeginVertical();

            // Status header
            GUILayout.Label($"Engine: {(runtime.IsReady ? "READY (No-Game)" : "NOT READY")} | Step: {runtime.LastStepMs:F2} ms");
            GUILayout.Label($"Active Agents: {lod.ActiveSimulatingCount}/{config.MaxConcurrentAgents.Value} | Wounded: {lod.WoundedTrackedCount} | Frozen: {lod.FrozenCorpseCount}");
            GUILayout.Label($"Worlds: {runtime.ActiveWorldCount} | Vehicles: {runtime.ActiveVehicleCount}");

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            config.Enabled.Value = GUILayout.Toggle(config.Enabled.Value, "Mod Enabled");
            config.EnableLocationalHealth.Value = GUILayout.Toggle(config.EnableLocationalHealth.Value, "Locational HP");
            config.DebugHud.Value = GUILayout.Toggle(config.DebugHud.Value, "Debug HUD");
            GUILayout.EndHorizontal();

            // Dedicated Test Range & Stationary Dummy Controls
            GUILayout.Space(4);
            GUILayout.Label($"--- Test Range & Dummy Bots ({(arena != null && arena.IsInTestArena ? "IN TEST RANGE" : "Standard Map")}) ---");
            GUILayout.BeginHorizontal();
            DrawBotModeButton("Pure Dummy", TestBotMode.PureDummy);
            DrawBotModeButton("Wounded AI", TestBotMode.WoundedAiActive);
            DrawBotModeButton("Normal AI", TestBotMode.Normal);
            GUILayout.EndHorizontal();

            if (arena != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Launch Test Range Map"))
                {
                    arena.LaunchTestArena();
                }
                if (GUILayout.Button("Reset / Arrange Dummies"))
                {
                    int n = arena.ResetAndArrangeAllDummies();
                    lastRigCheckOutput = $"Arranged {n} stationary dummy bots.";
                }
                if (GUILayout.Button($"Spawn Dummy ({config.SpawnDummyHotkey.Value})"))
                {
                    var spawned = arena.SpawnDummyAtCrosshair();
                    if (spawned != null)
                    {
                        lastAimedActor = spawned;
                        lastRigCheckOutput = $"Placed stationary dummy '{spawned.name}' at crosshair.";
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                config.TestArenaAutoRespawn.Value = GUILayout.Toggle(config.TestArenaAutoRespawn.Value, "Auto-Respawn Dead");
                config.ShowMainMenuTestButton.Value = GUILayout.Toggle(config.ShowMainMenuTestButton.Value, "Main Menu Button");
                GUILayout.EndHorizontal();

                if (config.TestArenaAutoRespawn.Value)
                {
                    config.TestArenaRespawnDelay.Value = DrawSlider("Respawn Delay (s)", config.TestArenaRespawnDelay.Value, 1.0f, 20.0f);
                }

                if (arena.IsInTestArena)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Warp:", GUILayout.Width(42));
                    for (int si = 0; si < TestArenaConstants.TeleportPoints.Length; si++)
                    {
                        if (GUILayout.Button(TestArenaConstants.TeleportPoints[si].Name))
                        {
                            arena.TeleportPlayerToStation(si);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(4);
            DrawPlayroomSyncSection();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            config.EnableLiveShotStagger.Value = GUILayout.Toggle(config.EnableLiveShotStagger.Value, "Live Stagger");
            config.EnableDyingStagger.Value = GUILayout.Toggle(config.EnableDyingStagger.Value, "Dying Stagger");
            config.EnableDyingBleed.Value = GUILayout.Toggle(config.EnableDyingBleed.Value, "Bleed");
            config.EnableBlindFire.Value = GUILayout.Toggle(config.EnableBlindFire.Value, "BlindFire");
            config.EnableDeathAgony.Value = GUILayout.Toggle(config.EnableDeathAgony.Value, "Agony");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            config.EnableSurrenderAiIgnore.Value = GUILayout.Toggle(config.EnableSurrenderAiIgnore.Value, "Surrender AI Ignore");
            config.EnableMedkitStopBleed.Value = GUILayout.Toggle(config.EnableMedkitStopBleed.Value, "Medkit Stops Bleed");
            config.EnableDownedFightBack.Value = GUILayout.Toggle(config.EnableDownedFightBack.Value, "Downed Shoot");
            config.EnableDownedBreathing.Value = GUILayout.Toggle(config.EnableDownedBreathing.Value, "Breathing");
            config.EnablePureDeliriumGetUp.Value = GUILayout.Toggle(config.EnablePureDeliriumGetUp.Value, "Delirium GetUp");
            GUILayout.EndHorizontal();

            // Aimed target diagnostics & Locational Wound Test Buttons
            GUILayout.Space(6);
            var aimed = FindAimedActor();
            if (aimed != null) lastAimedActor = aimed;
            var target = aimed ?? lastAimedActor;
            if (target == null && Plugin.PlayroomSync != null && Plugin.PlayroomSync.SyncedController != null)
            {
                target = Plugin.PlayroomSync.SyncedController.Actor;
            }
            if (debugOverlay != null) debugOverlay.FocusedActor = target;

            GUILayout.Label($"Target: {(target != null ? target.name : "(aim at a bot)")}");
            if (target != null && lod.TryGetHealthState(target, out var tHs))
            {
                GUILayout.Label(tHs.GetCompactSummary());
                GUILayout.Label(tHs.GetDetailedZonesLine());
            }
            if (target != null && lod.TryGetController(target, out var tCtrl))
            {
                GUILayout.Label(tCtrl.GetDiagnosticSummary());
            }

            // Playroom-style Inspector, 3D Skeleton / BVH Overlay & 3s Trace Recorder
            GUILayout.BeginHorizontal();
            Color prevBg = GUI.backgroundColor;
            if (jointInspectorOpen) GUI.backgroundColor = new Color(0.25f, 0.72f, 0.95f, 1f);
            if (GUILayout.Button(jointInspectorOpen ? "[Joint Inspector]" : "Joint Inspector"))
                jointInspectorOpen = !jointInspectorOpen;
            GUI.backgroundColor = prevBg;

            if (debugOverlay != null)
            {
                if (debugOverlay.ShowSkeletonOverlay) GUI.backgroundColor = new Color(0.25f, 0.78f, 0.35f, 1f);
                if (GUILayout.Button(debugOverlay.ShowSkeletonOverlay ? "[3D Skeleton]" : "3D Skeleton"))
                    debugOverlay.ShowSkeletonOverlay = !debugOverlay.ShowSkeletonOverlay;
                GUI.backgroundColor = prevBg;

                if (debugOverlay.ShowWorldBvhOverlay) GUI.backgroundColor = new Color(0.95f, 0.62f, 0.18f, 1f);
                if (GUILayout.Button(debugOverlay.ShowWorldBvhOverlay ? "[3D World BVH]" : "3D World BVH"))
                    debugOverlay.ShowWorldBvhOverlay = !debugOverlay.ShowWorldBvhOverlay;
                GUI.backgroundColor = prevBg;
            }

            bool tracing = traceCtrl != null;
            if (tracing) GUI.backgroundColor = new Color(0.95f, 0.28f, 0.25f, 1f);
            if (GUILayout.Button(tracing ? $"Recording ({traceElapsed:F1}s)..." : "Record 3s Trace") && target != null && !tracing)
            {
                if (lod.TryGetController(target, out var ctrl))
                {
                    tracePath = RigDiagnostics.DumpControllerRig(ctrl);
                    traceCtrl = ctrl;
                    traceElapsed = 0f;
                    traceNextSample = 0f;
                    lastRigCheckOutput = $"Recording 3s joint trace for {target.name}...";
                }
                else
                {
                    lastRigCheckOutput = $"Target '{target.name}' is not active in Delirium yet (shoot/push first).";
                }
            }
            GUI.backgroundColor = prevBg;
            GUILayout.EndHorizontal();

            GUILayout.Label("--- Locational Wound Tests (Aim at Bot) ---");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Break L-Leg") && target != null)
                ApplyTestZoneDamage(target, BodyZone.LeftLeg, 2, 100f);
            if (GUILayout.Button("Break Both Legs") && target != null)
            {
                ApplyTestZoneDamage(target, BodyZone.LeftLeg, 2, 100f);
                ApplyTestZoneDamage(target, BodyZone.RightLeg, 5, 100f);
            }
            if (GUILayout.Button("Break L-Arm") && target != null)
                ApplyTestZoneDamage(target, BodyZone.LeftArm, 15, 100f);
            if (GUILayout.Button("Surrender (R-Arm)") && target != null)
                ApplyTestZoneDamage(target, BodyZone.RightArm, 19, 100f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stomach 50%") && target != null)
                ApplyTestZoneDamage(target, BodyZone.Stomach, 8, 25f);
            if (GUILayout.Button("Stomach 0%") && target != null)
                ApplyTestZoneDamage(target, BodyZone.Stomach, 8, 100f);
            if (GUILayout.Button("Chest Right") && target != null)
                ApplyTestZoneDamage(target, BodyZone.ChestOther, 10, 28f);
            if (GUILayout.Button("Heart (L-Chest)") && target != null)
                ApplyTestZoneDamage(target, BodyZone.ChestLeft, 10, 40f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Headshot") && target != null)
                ApplyTestZoneDamage(target, BodyZone.Head, 12, 100f);
            if (GUILayout.Button("Heal / Stop Bleed") && target != null)
            {
                if (lod.TryGetHealthState(target, out var hs)) hs.OnHealed(60f);
            }
            if (GUILayout.Button("Push (60)") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target) ?? lod.TryAcquireForFallOrDeath(target);
                ctrl?.ApplyPush(CameraDir() * 60f, balance: true);
            }
            if (GUILayout.Button("Blast") && target != null)
            {
                var ctrl = lod.TryAcquireForFallOrDeath(target);
                ctrl?.ApplyExplosion(target.CenterPosition() - CameraDir() * 1.2f - Vector3.up * 0.5f, 6f, 14f, fatal: false, isAlreadyNormalizedSpeed: true);
            }
            GUILayout.EndHorizontal();

            // Live tuning sliders
            GUILayout.Space(6);
            GUILayout.Label("--- Locational HP & Bleedout Tuning ---");
            config.LegHpRatio.Value = DrawSlider("Leg HP Ratio", config.LegHpRatio.Value, 0.15f, 1.0f);
            config.ArmHpRatio.Value = DrawSlider("Arm HP Ratio", config.ArmHpRatio.Value, 0.15f, 1.0f);
            config.StomachHpRatio.Value = DrawSlider("Stomach HP Ratio", config.StomachHpRatio.Value, 0.20f, 1.2f);
            config.ChestHpRatio.Value = DrawSlider("Chest HP Ratio", config.ChestHpRatio.Value, 0.20f, 1.2f);
            config.BleedRateLeg.Value = DrawSlider("Bleed Leg (/s)", config.BleedRateLeg.Value, 1f, 20f);
            config.BleedRateArm.Value = DrawSlider("Bleed Arm (/s)", config.BleedRateArm.Value, 1f, 20f);
            config.BleedRateStomach.Value = DrawSlider("Bleed Stomach (/s)", config.BleedRateStomach.Value, 1f, 25f);
            config.WoundedSpreadMultiplier.Value = DrawSlider("Wounded Spread", config.WoundedSpreadMultiplier.Value, 1f, 5f);
            config.DownedFightBackChance.Value = DrawSlider("Downed Fight %", config.DownedFightBackChance.Value, 0f, 1f);
            config.DownedBreathingIntensity.Value = DrawSlider("Breathing Scale", config.DownedBreathingIntensity.Value, 0.2f, 2.5f);
            config.BleedingFinishHitsMin.Value = Mathf.RoundToInt(DrawSlider("Bleed Finish Min", config.BleedingFinishHitsMin.Value, 1f, 6f));
            config.BleedingFinishHitsMax.Value = Mathf.RoundToInt(DrawSlider("Bleed Finish Max", Mathf.Max(config.BleedingFinishHitsMax.Value, config.BleedingFinishHitsMin.Value), 1f, 6f));

            GUILayout.Space(4);
            GUILayout.Label("--- Physics & LOD Tuning ---");
            config.MuscleStrength.Value = DrawSlider("Muscle Strength", config.MuscleStrength.Value, 0.2f, 2.5f);
            config.ShotForceScale.Value = DrawSlider("Shot Force Scale", config.ShotForceScale.Value, 0.2f, 2.5f);
            config.ActiveDistance.Value = DrawSlider("LOD Distance (m)", config.ActiveDistance.Value, 15f, 150f);
            config.MaxConcurrentAgents.Value = Mathf.RoundToInt(DrawSlider("Max Concurrent", config.MaxConcurrentAgents.Value, 4f, 40f));

            GUILayout.Space(4);
            GUILayout.Label($"Last Hit: {WeaponForceNormalizer.LastHitTelemetry}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Check Skeleton") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target) ?? lod.TryAcquireForFallOrDeath(target);
                if (ctrl != null)
                {
                    ctrl.EnsureTakenOver(startBalanced: true, alreadyFallen: false);
                    lastRigCheckOutput = ctrl.RunSetupCheck();
                }
            }
            if (GUILayout.Button($"Release All ({config.ReleaseAllHotkey.Value})"))
            {
                lod.ReleaseAll(restoreGame: true);
            }
            if (GUILayout.Button("Reset Defaults"))
            {
                config.ResetGameplayDefaults();
                lastRigCheckOutput = "Sliders reset to defaults.";
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(lastRigCheckOutput))
            {
                GUILayout.Space(4);
                GUILayout.TextArea(lastRigCheckOutput, GUILayout.Height(48));
            }

            GUILayout.Label($"Hotkeys: UI={config.UiHotkey.Value} | Dummy={config.SpawnDummyHotkey.Value} | Mode={config.TestRangeHotkey.Value} | Push={config.AimPushHotkey.Value} | Release={config.ReleaseAllHotkey.Value}");
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void DrawBotModeButton(string label, TestBotMode mode)
        {
            bool active = lod.BotMode == mode;
            Color prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.25f, 0.78f, 0.35f, 1f);
            if (GUILayout.Button(active ? $"[{label}]" : label))
            {
                lod.BotMode = mode;
                Plugin.Log.LogInfo($"[Delirium] Bot Test Mode set to: {mode}");
            }
            GUI.backgroundColor = prev;
        }

        private void ApplyTestZoneDamage(Actor target, BodyZone zone, int part, float damage)
        {
            if (target == null || target.dead) return;
            var hs = lod.GetOrCreateHealthState(target);
            if (hs == null) return;

            Vector3 dir = CameraDir();
            Vector3 hitPt = target.CenterPosition();
            Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
            HitOutcome outcome = hs.ApplyHit(zone, part, damage, hitPt, -dir, player);

            DamageInfo fakeInfo = new DamageInfo
            {
                type = DamageInfo.DamageSourceType.Projectile,
                healthDamage = damage,
                balanceDamage = 25f,
                isCriticalHit = outcome.InstantHeadshot,
                point = hitPt,
                direction = dir,
                impactForce = dir * 180f,
                sourceActor = player
            };

            bool isSynced = lod.TryGetController(target, out var existingCtrl) && existingCtrl.IsSyncLockstepControlled;
            if (isSynced)
            {
                existingCtrl.HealthState = hs;
                existingCtrl.ApplyLocationalShot(fakeInfo, outcome, hs);
                return;
            }

            if (outcome.Lethal)
            {
                var ctrl = lod.TryAcquireForFallOrDeath(target);
                fakeInfo.healthDamage = target.maxHealth + 100f;
                lod.ForceKillForTest(target, fakeInfo);
                if (ctrl != null)
                {
                    ctrl.HealthState = hs;
                    ctrl.ApplyLocationalShot(fakeInfo, outcome, hs);
                }
            }
            else
            {
                var ctrl = lod.TryAcquireForWounded(target);
                if (ctrl != null)
                {
                    ctrl.HealthState = hs;
                    ctrl.ApplyLocationalShot(fakeInfo, outcome, hs);
                }
            }
        }

        private static float DrawSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {value:F2}", GUILayout.Width(160));
            float next = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return next;
        }

        private static Vector3 CameraDir()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.forward : Vector3.forward;
        }

        private static Actor FindAimedActor()
        {
            var cam = Camera.main;
            var am = ActorManager.instance;
            if (cam == null || am == null || am.actors == null) return null;

            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            Actor best = null;
            float bestOff = 2.0f;

            for (int i = 0; i < am.actors.Count; i++)
            {
                var actor = am.actors[i];
                if (actor == null || !actor.gameObject.activeInHierarchy || actor == am.player) continue;
                Vector3 center;
                try { center = actor.CenterPosition(); } catch { continue; }
                Vector3 to = center - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along <= 0.5f || along > 80f) continue;
                float off = Vector3.Cross(ray.direction, to).magnitude;
                if (off < bestOff)
                {
                    bestOff = off;
                    best = actor;
                }
            }
            return best;
        }

        private void EnsureInspectorStyles()
        {
            if (solidTex == null)
            {
                solidTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                solidTex.SetPixel(0, 0, Color.white);
                solidTex.Apply();
            }
            if (monoStyle == null)
            {
                monoStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    richText = false,
                    wordWrap = false
                };
            }
        }

        private void DrawRect(Rect r, Color col)
        {
            Color prev = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(r, solidTex);
            GUI.color = prev;
        }

        private void DrawPlayroomSyncSection()
        {
            var sync = Plugin.PlayroomSync;
            if (sync == null) return;

            GUILayout.Label($"--- Playroom Live Sync (2-Way Lockstep) [{sync.StatusText}] ---");
            Color prev = GUI.backgroundColor;

            GUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.18f, 0.65f, 0.92f, 1f);
            if (GUILayout.Button("Launch Mirror Map"))
            {
                sync.LaunchPlayroomMirrorMap();
            }
            GUI.backgroundColor = sync.IsConnected ? new Color(0.24f, 0.78f, 0.36f, 1f) : prev;
            if (GUILayout.Button(sync.IsConnected ? "Disconnect" : "Connect (47291)"))
            {
                if (sync.IsConnected) sync.Disconnect();
                else sync.Connect();
            }
            GUI.backgroundColor = prev;
            if (GUILayout.Button("Reset Both (Stand)") && sync.IsConnected)
            {
                sync.RequestResyncAndReset(stand: true);
            }
            if (GUILayout.Button("Snap View"))
            {
                sync.ResetPlayerView();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.backgroundColor = sync.ParityMode == Sync.SyncParityMode.ExactHomer ? new Color(0.24f, 0.75f, 0.95f, 1f) : prev;
            if (GUILayout.Button("Body: Exact Homer"))
                sync.SetParityMode(Sync.SyncParityMode.ExactHomer);
            GUI.backgroundColor = sync.ParityMode == Sync.SyncParityMode.RavenfieldSoldier ? new Color(0.95f, 0.62f, 0.18f, 1f) : prev;
            if (GUILayout.Button("Body: RF Soldier"))
                sync.SetParityMode(Sync.SyncParityMode.RavenfieldSoldier);

            sync.AutoRecordOnSync = GUILayout.Toggle(sync.AutoRecordOnSync, "Auto-Log", GUILayout.Width(74));

            GUI.backgroundColor = sync.HideSoldierMesh ? new Color(0.75f, 0.40f, 0.95f, 1f) : prev;
            if (GUILayout.Button(sync.HideSoldierMesh ? "Mesh: OFF" : "Mesh: ON", GUILayout.Width(72)))
            {
                sync.HideSoldierMesh = !sync.HideSoldierMesh;
                if (sync.HideSoldierMesh && debugOverlay != null)
                    debugOverlay.ShowSkeletonOverlay = true;
            }

            GUI.backgroundColor = sync.IsRecording ? new Color(0.94f, 0.25f, 0.22f, 1f) : prev;
            if (GUILayout.Button(sync.IsRecording ? "Stop Log" : "Start Log", GUILayout.Width(70)))
                sync.ToggleRecording();
            GUI.backgroundColor = prev;
            GUILayout.EndHorizontal();

            if (sync.IsConnected)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Stage:", GUILayout.Width(42));
                string[] quickStages = { "room", "bigstairs", "lowwall", "rooftop", "street" };
                for (int si = 0; si < quickStages.Length; si++)
                {
                    string st = quickStages[si];
                    GUI.backgroundColor = string.Equals(sync.CurrentStage, st, StringComparison.OrdinalIgnoreCase)
                        ? new Color(0.24f, 0.72f, 0.95f, 1f)
                        : prev;
                    if (GUILayout.Button(st))
                    {
                        sync.RequestStage(st);
                    }
                }
                GUI.backgroundColor = prev;
                GUILayout.EndHorizontal();

                Color prevContent = GUI.contentColor;
                if (sync.HasPlayroomRef)
                {
                    if (sync.DeltaComMm < 1.0f && sync.MaxJointErrDeg < 0.5f && sync.FlagsMatch)
                        GUI.contentColor = new Color(0.45f, 1.0f, 0.55f, 1f);
                    else if (sync.DeltaComMm < 5.0f && sync.MaxJointErrDeg < 2.0f)
                        GUI.contentColor = new Color(1.0f, 0.88f, 0.30f, 1f);
                    else
                        GUI.contentColor = new Color(1.0f, 0.42f, 0.38f, 1f);
                }
                string divTxt = sync.FirstDivergenceStep >= 0 ? $"FirstDiv={sync.FirstDivergenceStep}" : "PARITY OK";
                GUILayout.Label($"Stage: {sync.CurrentStage} | Step: {sync.LastStep} | {divTxt}");
                GUILayout.Label($"dCOM: {sync.DeltaComMm:F2}mm | dVel: {sync.DeltaVelMs:F3}m/s | MaxJoint: {sync.MaxJointErrDeg:F2}deg ({sync.WorstJointName})");
                GUILayout.Label($"Flags={(sync.FlagsMatch ? "MATCH" : "DIFF")} | Contacts={(sync.ContactsMatch ? "MATCH" : "DIFF")} | dKE={sync.DeltaKe:F1}J");
                GUI.contentColor = prevContent;
                if (sync.IsRecording)
                {
                    GUILayout.Label($"Auto-Log [{sync.SessionId}] -> {sync.GetRavenfieldLogPath()}");
                }
            }
        }

        private void DrawJointInspectorWindow(int id)
        {
            EnsureInspectorStyles();
            var ci = CultureInfo.InvariantCulture;

            var aimed = FindAimedActor();
            if (aimed != null) lastAimedActor = aimed;
            Actor target = aimed ?? lastAimedActor;
            var sync = Plugin.PlayroomSync;
            if (target == null && sync != null && sync.SyncedController != null && sync.SyncedController.Actor != null)
            {
                target = sync.SyncedController.Actor;
            }
            bool showSyncDiff = sync != null && sync.IsConnected && sync.HasPlayroomRef &&
                                sync.SyncedController != null && target == sync.SyncedController.Actor;
            inspectorRect.width = showSyncDiff ? 655f : 580f;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Select a joint for muscle settings & limits. Angles in deg: actual -> target.");
            if (GUILayout.Button("X", GUILayout.Width(26)))
                jointInspectorOpen = false;
            GUILayout.EndHorizontal();

            if (target == null || !lod.TryGetController(target, out var ctrl) || ctrl.Agent == IntPtr.Zero)
            {
                GUILayout.Space(8);
                GUILayout.Label(target == null
                    ? "Aim at an active Delirium bot to inspect its joints."
                    : $"Target '{target.name}' has no active native agent (Phase={((target != null && lod.TryGetController(target, out var c0)) ? c0.Phase.ToString() : "Animated")}).");
                GUI.DragWindow();
                return;
            }

            IntPtr agent = ctrl.Agent;
            int worldTris = ctrl.WorldEntry != null ? ctrl.WorldEntry.TriangleCount : 0;
            int unread = ctrl.WorldEntry != null ? ctrl.WorldEntry.UnreadableMeshCount : 0;
            float ke = nm_agent_get_kinetic_energy(agent);
            float mass = nm_agent_get_mass(agent);
            GUILayout.Label($"{target.name} | {ctrl.GetDiagnosticSummary()}");
            GUILayout.Label($"World: {worldTris} tris (sampled meshes: {unread}) | Mass: {mass:F1} kg | KE: {ke:F0} J");

            GUILayout.Space(4);
            // Column headers
            GUILayout.BeginHorizontal();
            GUILayout.Label("Joint", GUILayout.Width(125));
            GUILayout.Label("Delirium angles (actual -> target)", GUILayout.Width(230));
            GUILayout.Label("Drive", GUILayout.Width(82));
            GUILayout.Label("Str / damp", GUILayout.Width(85));
            if (showSyncDiff) GUILayout.Label("PR dDeg", GUILayout.Width(65));
            GUILayout.EndHorizontal();

            int effCount = nm_agent_effector_count(agent);
            if (effCount <= 0)
            {
                GUILayout.Label("(No character effectors active on this agent)");
                GUI.DragWindow();
                return;
            }
            if (selectedJoint < 0 || selectedJoint >= effCount) selectedJoint = 0;

            inspectorScrollPos = GUILayout.BeginScrollView(inspectorScrollPos, GUILayout.Height(390));
            for (int j = 0; j < effCount; j++)
            {
                int dof = nm_agent_get_effector(agent, j, effActual, effDesired, null, effStats, null);
                if (dof <= 0) continue;

                int nlen = nm_agent_effector_name(agent, j, effNameBuf, effNameBuf.Length);
                string jname = nlen > 1 ? Encoding.UTF8.GetString(effNameBuf, 0, Math.Min(nlen - 1, effNameBuf.Length - 1)) : $"joint_{j}";

                GUILayout.BeginHorizontal();
                Color prevBg = GUI.backgroundColor;
                if (selectedJoint == j) GUI.backgroundColor = new Color(0.22f, 0.55f, 0.90f, 1f);
                if (GUILayout.Button(jname, GUILayout.Width(125), GUILayout.Height(20)))
                    selectedJoint = j;
                GUI.backgroundColor = prevBg;

                string angText = dof == 3
                    ? string.Format(ci, "{0,4:F0} {1,4:F0} {2,4:F0} -> {3,4:F0} {4,4:F0} {5,4:F0}",
                        effActual[0] * Mathf.Rad2Deg, effActual[1] * Mathf.Rad2Deg, effActual[2] * Mathf.Rad2Deg,
                        effDesired[0] * Mathf.Rad2Deg, effDesired[1] * Mathf.Rad2Deg, effDesired[2] * Mathf.Rad2Deg)
                    : string.Format(ci, "{0,4:F0} -> {1,4:F0}",
                        effActual[0] * Mathf.Rad2Deg, effDesired[0] * Mathf.Rad2Deg);
                GUILayout.Label(angText, monoStyle, GUILayout.Width(230));

                // Drive strain bar (green < 10 deg, yellow 10-25 deg, red > 25 deg — matching Playroom!)
                float errSq = 0f;
                for (int k = 0; k < dof; k++)
                {
                    float d = effActual[k] - effDesired[k];
                    errSq += d * d;
                }
                float errDeg = Mathf.Sqrt(errSq) * Mathf.Rad2Deg;
                float driveFrac = Mathf.Clamp01((errDeg / 35f) * Mathf.Clamp01(effStats[0] / 40f));
                Rect barSlot = GUILayoutUtility.GetRect(78f, 16f, GUILayout.Width(78f));
                Rect barBg = new Rect(barSlot.x, barSlot.y + 4f, barSlot.width, 8f);
                DrawRect(barBg, new Color(0.18f, 0.20f, 0.24f, 0.9f));
                Color barCol = errDeg < 10f
                    ? new Color(0.24f, 0.82f, 0.34f, 1f)
                    : (errDeg < 24f ? new Color(0.95f, 0.78f, 0.16f, 1f) : new Color(0.94f, 0.24f, 0.20f, 1f));
                DrawRect(new Rect(barBg.x, barBg.y, Mathf.Max(2f, barBg.width * driveFrac), barBg.height), barCol);

                GUILayout.Label(string.Format(ci, "{0,3:F0}/{1,-3:F0}", effStats[0], effStats[1]), monoStyle, GUILayout.Width(85));
                if (showSyncDiff && j < sync.PerJointDeltaDeg.Length)
                {
                    float dDeg = sync.PerJointDeltaDeg[j];
                    Color prevC = GUI.contentColor;
                    GUI.contentColor = dDeg < 0.5f
                        ? new Color(0.35f, 0.95f, 0.45f, 1f)
                        : (dDeg < 3.0f ? new Color(1.0f, 0.85f, 0.25f, 1f) : new Color(1.0f, 0.35f, 0.30f, 1f));
                    GUILayout.Label(string.Format(ci, "{0,5:F2}", dDeg), monoStyle, GUILayout.Width(65));
                    GUI.contentColor = prevC;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            // Detailed selected joint footer (Playroom-style limits + actual/desired ticks!)
            int selDof = nm_agent_get_effector(agent, selectedJoint, effActual, effDesired, effLimits, effStats, effParts);
            if (selDof > 0)
            {
                int nlen = nm_agent_effector_name(agent, selectedJoint, effNameBuf, effNameBuf.Length);
                string selName = nlen > 1 ? Encoding.UTF8.GetString(effNameBuf, 0, Math.Min(nlen - 1, effNameBuf.Length - 1)) : $"joint_{selectedJoint}";
                string pName = Str((b, n) => nm_part_name(effParts[0], b, n)) ?? $"part_{effParts[0]}";
                string cName = Str((b, n) => nm_part_name(effParts[1], b, n)) ?? $"part_{effParts[1]}";

                GUILayout.Space(6);
                GUILayout.Label($"{selName}: parent {pName} -> child {cName}");
                GUILayout.Label(string.Format(ci, "muscleStiffness {0:F1}  |  strength {1:F1}  |  damping {2:F1}  |  opposeGravity {3:F2}",
                    effStats[2], effStats[0], effStats[1], effStats[3]), monoStyle);

                string[] axisNames = { "lean1", "lean2", "twist" };
                for (int k = 0; k < selDof; k++)
                {
                    float lo = effLimits[k * 2];
                    float hi = effLimits[k * 2 + 1];
                    float loMin = Mathf.Min(lo, hi);
                    float hiMax = Mathf.Max(lo, hi);
                    float spanLo = loMin - 0.30f;
                    float spanHi = hiMax + 0.30f;
                    float invSpan = 1f / Mathf.Max(spanHi - spanLo, 0.05f);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(string.Format(ci, "{0,-6} [{1,6:F1} .. {2,6:F1}]",
                        axisNames[k], lo * Mathf.Rad2Deg, hi * Mathf.Rad2Deg), monoStyle, GUILayout.Width(195));

                    Rect r = GUILayoutUtility.GetRect(260f, 16f, GUILayout.Width(260f));
                    Rect track = new Rect(r.x, r.y + 4f, r.width, 8f);
                    DrawRect(track, new Color(0.26f, 0.26f, 0.28f, 1f));

                    float limX0 = track.x + Mathf.Clamp01((loMin - spanLo) * invSpan) * track.width;
                    float limX1 = track.x + Mathf.Clamp01((hiMax - spanLo) * invSpan) * track.width;
                    DrawRect(new Rect(limX0, track.y, Mathf.Max(2f, limX1 - limX0), track.height), new Color(0.46f, 0.48f, 0.52f, 1f));

                    float desX = track.x + Mathf.Clamp01((effDesired[k] - spanLo) * invSpan) * track.width;
                    float actX = track.x + Mathf.Clamp01((effActual[k] - spanLo) * invSpan) * track.width;
                    DrawRect(new Rect(desX - 1.5f, track.y - 2f, 3f, 12f), Color.yellow);
                    DrawRect(new Rect(actX - 1.5f, track.y - 2f, 3f, 12f), Color.white);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label("white = actual, yellow = target, grey bar = joint limits");
            }

            GUI.DragWindow();
        }
    }
}
