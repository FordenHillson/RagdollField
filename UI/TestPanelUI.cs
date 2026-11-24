using System;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using UnityEngine;

namespace DeliriumRavenfield.UI
{
    public sealed class TestPanelUI : MonoBehaviour
    {
        private ModConfig config;
        private DeliriumRuntime runtime;
        private LodManager lod;

        private bool panelOpen;
        private Rect windowRect = new Rect(20, 20, 420, 725);
        private string lastRigCheckOutput = "";
        private Actor lastAimedActor;
        private ActorRagdollController traceCtrl;
        private string tracePath;
        private float traceElapsed;
        private float traceNextSample;

        public void Initialize(ModConfig cfg, DeliriumRuntime rt, LodManager lodManager)
        {
            config = cfg;
            runtime = rt;
            lod = lodManager;
            panelOpen = cfg.UiStartOpen.Value;
        }

        private void Update()
        {
            if (config == null || runtime == null || lod == null) return;

            if (traceCtrl != null)
            {
                traceElapsed += Time.deltaTime;
                if (traceElapsed >= traceNextSample)
                {
                    if (traceCtrl.Agent != IntPtr.Zero)
                        RigDiagnostics.AppendTraceSample(tracePath, traceElapsed, traceCtrl);
                    traceNextSample += 0.25f;
                }
                if (traceElapsed >= 2.25f || traceCtrl.Agent == IntPtr.Zero)
                {
                    traceCtrl = null;
                    lastRigCheckOutput = $"Rig + 2s trace saved to:\n{tracePath}";
                    Plugin.Log.LogInfo($"[RigDiagnostics] Completed 2s trace -> {tracePath}");
                }
            }

            if (Input.GetKeyDown(config.UiHotkey.Value))
                panelOpen = !panelOpen;

            if (Input.GetKeyDown(config.TestRangeHotkey.Value))
            {
                lod.TestRangeMode = !lod.TestRangeMode;
                Plugin.Log.LogInfo($"[Delirium] Test Range Mode: {(lod.TestRangeMode ? "ON (bots idle)" : "OFF")}");
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

            if (config.DebugHud.Value && !panelOpen)
            {
                DrawCompactHud();
            }

            if (panelOpen)
            {
                windowRect = GUI.Window(948271, windowRect, DrawWindow, "Delirium Physics — Ravenfield (No-Game)");
            }
        }

        private void DrawCompactHud()
        {
            string line = $"[Delirium] Active: {lod.ActiveSimulatingCount}/{config.MaxConcurrentAgents.Value} | " +
                          $"Frozen: {lod.FrozenCorpseCount} | Step: {runtime.LastStepMs:F2}ms | " +
                          $"TestRange(F8): {(lod.TestRangeMode ? "ON" : "OFF")}";
            GUI.Box(new Rect(12, 12, 540, 26), line);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // Status header
            GUILayout.Label($"Engine: {(runtime.IsReady ? "READY (No-Game)" : "NOT READY")} | Step: {runtime.LastStepMs:F2} ms");
            GUILayout.Label($"Active Agents: {lod.ActiveSimulatingCount} / {config.MaxConcurrentAgents.Value} | Frozen Corpses: {lod.FrozenCorpseCount}");
            GUILayout.Label($"Worlds: {runtime.ActiveWorldCount} | Vehicles: {runtime.ActiveVehicleCount}");

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            bool newEnabled = GUILayout.Toggle(config.Enabled.Value, "Mod Enabled");
            if (newEnabled != config.Enabled.Value) config.Enabled.Value = newEnabled;
            lod.TestRangeMode = GUILayout.Toggle(lod.TestRangeMode, $"Test Range ({config.TestRangeHotkey.Value})");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            config.EnableLiveShotStagger.Value = GUILayout.Toggle(config.EnableLiveShotStagger.Value, "Live Stagger");
            config.EnableDeathAgony.Value = GUILayout.Toggle(config.EnableDeathAgony.Value, "Death Agony");
            config.EnableVehicles.Value = GUILayout.Toggle(config.EnableVehicles.Value, "Vehicles");
            config.EnableWaterBuoyancy.Value = GUILayout.Toggle(config.EnableWaterBuoyancy.Value, "Water");
            GUILayout.EndHorizontal();

            // Live tuning sliders
            GUILayout.Space(6);
            config.MuscleStrength.Value = DrawSlider("Muscle Strength", config.MuscleStrength.Value, 0.2f, 2.5f);
            config.ShotForceScale.Value = DrawSlider("Shot Force Scale", config.ShotForceScale.Value, 0.2f, 2.5f);
            config.AliveShotPushScale.Value = DrawSlider("Alive Shot Push", config.AliveShotPushScale.Value, 0.15f, 1.5f);
            config.DeathAgonyChance.Value = DrawSlider("Death Agony Chance", config.DeathAgonyChance.Value, 0f, 1f);
            config.MaxShotImpulse.Value = DrawSlider("Max Shot (N*s)", config.MaxShotImpulse.Value, 30f, 120f);
            config.MaxBurstImpulse.Value = DrawSlider("Max Burst (N*s)", config.MaxBurstImpulse.Value, 45f, 150f);
            config.MaxLimbDeltaV.Value = DrawSlider("Max Limb dV (m/s)", config.MaxLimbDeltaV.Value, 2.0f, 10.0f);
            config.ExplosionForceScale.Value = DrawSlider("Explosion Scale", config.ExplosionForceScale.Value, 0.2f, 2.5f);
            config.ActiveDistance.Value = DrawSlider("LOD Distance (m)", config.ActiveDistance.Value, 15f, 150f);
            config.MaxConcurrentAgents.Value = Mathf.RoundToInt(DrawSlider("Max Concurrent", config.MaxConcurrentAgents.Value, 4f, 40f));

            GUILayout.Space(4);
            GUILayout.Label($"Last Hit: {WeaponForceNormalizer.LastHitTelemetry}");

            // Aimed target diagnostics & quick actions
            GUILayout.Space(8);
            var aimed = FindAimedActor();
            if (aimed != null) lastAimedActor = aimed;
            var target = aimed ?? lastAimedActor;

            GUILayout.Label($"Target: {(target != null ? target.name : "(aim at a bot)")}");
            if (target != null && lod.TryGetController(target, out var tCtrl))
            {
                GUILayout.Label(tCtrl.GetDiagnosticSummary());
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Push (60)") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target) ?? lod.TryAcquireForFallOrDeath(target);
                ctrl?.ApplyPush(CameraDir() * 60f, balance: true);
            }
            if (GUILayout.Button("Hard Push (180)") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target) ?? lod.TryAcquireForFallOrDeath(target);
                ctrl?.ApplyPush(CameraDir() * 180f, balance: true);
            }
            if (GUILayout.Button("Blast") && target != null)
            {
                var ctrl = lod.TryAcquireForFallOrDeath(target);
                ctrl?.ApplyExplosion(target.CenterPosition() - CameraDir() * 1.2f - Vector3.up * 0.5f, 6f, 14f, fatal: false, isAlreadyNormalizedSpeed: true);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Drunk") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target);
                ctrl?.SendTextReaction("bodyBalance start=true bodyStiffness=7 damping=0.7");
                ctrl?.SendTextReaction("stumble start=true");
            }
            if (GUILayout.Button("Windmill") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target);
                ctrl?.SendTextReaction("armsWindmillAdaptive start=true");
            }
            if (GUILayout.Button("Electrocute") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target);
                ctrl?.TriggerElectrocute(3.5f);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Flinch") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target);
                ctrl?.TriggerFlinch(Camera.main != null ? Camera.main.transform.position : target.CenterPosition() + Vector3.forward);
            }
            if (GUILayout.Button("Relax") && target != null)
            {
                var ctrl = lod.TryAcquireForFallOrDeath(target);
                ctrl?.TriggerRelax();
            }
            if (GUILayout.Button("GetUp") && target != null)
            {
                if (lod.TryGetController(target, out var c)) c.HandBackToRavenfieldGetUp();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
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
            if (GUILayout.Button("Dump Rig Compare") && target != null)
            {
                var ctrl = lod.TryAcquireForLiveReaction(target) ?? lod.TryAcquireForFallOrDeath(target);
                if (ctrl != null && ctrl.EnsureTakenOver(startBalanced: !target.dead, alreadyFallen: target.fallenOver))
                {
                    tracePath = RigDiagnostics.DumpControllerRig(ctrl);
                    RigDiagnostics.AppendTraceSample(tracePath, 0f, ctrl);
                    traceCtrl = ctrl;
                    traceElapsed = 0f;
                    traceNextSample = 0.25f;
                    lastRigCheckOutput = $"Dumping rig + 2s trace...\n{tracePath}";
                    Plugin.Log.LogInfo($"[RigDiagnostics] Dumped rig -> {tracePath}");
                }
            }
            if (GUILayout.Button($"Release All ({config.ReleaseAllHotkey.Value})"))
            {
                lod.ReleaseAll(restoreGame: true);
            }
            if (GUILayout.Button("Reset Defaults"))
            {
                config.ResetGameplayDefaults();
                lastRigCheckOutput = "Sliders reset to recommended defaults (MuscleStrength=1.0).";
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(lastRigCheckOutput))
            {
                GUILayout.Space(4);
                GUILayout.TextArea(lastRigCheckOutput, GUILayout.Height(56));
            }

            GUILayout.Label($"Hotkeys: UI={config.UiHotkey.Value} | TestRange={config.TestRangeHotkey.Value} | Push={config.AimPushHotkey.Value} | Release={config.ReleaseAllHotkey.Value}");
            GUILayout.EndVertical();
            GUI.DragWindow();
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
                Vector3 to = actor.CenterPosition() - ray.origin;
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
    }
}
