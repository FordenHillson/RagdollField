using System;
using System.Collections.Generic;
using System.Reflection;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using HarmonyLib;
using UnityEngine;

namespace DeliriumRavenfield.TestArena
{
    /// <summary>
    /// Manages the dedicated "Delirium Test Range" match lifecycle, automatic stationary dummy slot
    /// placement, auto-respawn timers, crosshair dummy spawning, and station teleports.
    /// </summary>
    public sealed class TestArenaManager
    {
        private readonly ModConfig config;
        private readonly LodManager lod;

        private readonly Dictionary<Actor, int> actorToSlot = new Dictionary<Actor, int>();
        private readonly Actor[] slotToActor = new Actor[TestArenaConstants.DummySlots.Length];
        private readonly Dictionary<Actor, float> deadRespawnAt = new Dictionary<Actor, float>();
        private readonly List<Actor> respawnBuffer = new List<Actor>();

        private bool isInTestArena;
        private int arenaSceneHandle;
        private bool initialArrangeDone;
        private float arrangeRetryTimer;
        private bool suppressSpawnSnap;

        public bool IsInTestArena => isInTestArena;
        public int ArenaSceneHandle => arenaSceneHandle;

        public TestArenaManager(ModConfig config, LodManager lod)
        {
            this.config = config;
            this.lod = lod;
        }

        /// <summary>
        /// 1-click launch into Delirium Test Range with player-only Blue team, 24 Red dummies,
        /// and all weapons unlocked for immediate testing.
        /// </summary>
        public void LaunchTestArena()
        {
            try
            {
                var entry = TestArenaConstants.CreateMapEntry();
                var parameters = GameModeParameters.GetDefault();
                parameters.playerHasAllWeapons = true;
                parameters.playerTeam = 0;
                parameters.nightMode = false;
                parameters.configFlags = false;
                if (parameters.configuration != null)
                {
                    parameters.configuration.SetInt("countTeam0", 1);
                    parameters.configuration.SetInt("countTeam1", TestArenaConstants.DummySlots.Length);
                }

                Plugin.Log?.LogInfo($"[TestArena] Launching '{entry.metaData.displayName}' (1v{TestArenaConstants.DummySlots.Length}, all weapons unlocked).");
                GameManager.StartLevel(entry, parameters);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[TestArena] LaunchTestArena failed: {ex}");
            }
        }

        public void OnTestArenaSceneLoaded()
        {
            isInTestArena = true;
            arenaSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            initialArrangeDone = false;
            arrangeRetryTimer = 0.6f;
            ClearSlotTracking();

            // Automatically enter PureDummy mode when entering the dedicated test arena
            lod.BotMode = TestBotMode.PureDummy;
            Plugin.Log?.LogInfo("[TestArena] Entered Delirium Test Range — BotMode set to PureDummy.");
        }

        public void OnSceneUnloaded()
        {
            bool wasInArena = isInTestArena;
            isInTestArena = false;
            arenaSceneHandle = 0;
            initialArrangeDone = false;
            ClearSlotTracking();

            // Restore Normal AI when leaving the dedicated test arena so normal maps play naturally
            if (wasInArena)
            {
                lod.BotMode = TestBotMode.Normal;
            }
        }

        public void OnActorSpawned(Actor actor)
        {
            if (!isInTestArena || suppressSpawnSnap || actor == null) return;
            var am = ActorManager.instance;
            if (am == null || actor == am.player) return;

            deadRespawnAt.Remove(actor);

            // Team Red (1) -> snap to assigned station slot (or claim first free slot)
            if (actor.team == 1)
            {
                int slotIdx = GetOrAssignSlot(actor);
                if (slotIdx >= 0)
                {
                    var slot = TestArenaConstants.DummySlots[slotIdx];
                    SnapBotPoseAndFreezeBrain(actor, slot.Position, slot.FacingDir);
                }
            }
            else if (actor.team == 0 && lod.BotMode == TestBotMode.PureDummy)
            {
                // Keep any friendly bots (if launched from Instant Action with Blue bots) parked neatly behind booth
                int idx = Mathf.Abs(actor.GetInstanceID()) % 8;
                Vector3 parkPos = new Vector3(-6f + (idx % 4) * 4f, TestArenaConstants.GroundY, -30f - (idx / 4) * 2.5f);
                SnapBotPoseAndFreezeBrain(actor, parkPos, Vector3.forward);
            }
        }

        public void TickUpdate(float dt)
        {
            if (!isInTestArena) return;
            var am = ActorManager.instance;
            if (am == null || am.actors == null || am.actors.Count == 0) return;

            // Keep capture points uncapturable so test sessions never end
            if (!initialArrangeDone)
            {
                arrangeRetryTimer -= dt;
                if (arrangeRetryTimer <= 0f)
                {
                    int aliveRed = CountAliveTeam(am, 1);
                    if (aliveRed > 0)
                    {
                        ArrangeAllDummies(respawnDead: false);
                        initialArrangeDone = true;
                        Plugin.Log?.LogInfo($"[TestArena] Auto-arranged {actorToSlot.Count} stationary Red dummies across 6 stations.");
                    }
                    else
                    {
                        arrangeRetryTimer = 0.5f;
                    }
                }
            }

            // Auto-respawn dead station dummies after configurable delay
            if (config.TestArenaAutoRespawn.Value)
            {
                float now = Time.time;
                float delay = Mathf.Clamp(config.TestArenaRespawnDelay.Value, 1.0f, 30.0f);
                respawnBuffer.Clear();

                for (int i = 0; i < slotToActor.Length; i++)
                {
                    Actor actor = slotToActor[i];
                    if (actor == null) continue;

                    if (actor.dead)
                    {
                        if (!deadRespawnAt.TryGetValue(actor, out float when))
                        {
                            deadRespawnAt[actor] = now + delay;
                        }
                        else if (now >= when)
                        {
                            respawnBuffer.Add(actor);
                        }
                    }
                    else
                    {
                        deadRespawnAt.Remove(actor);
                    }
                }

                for (int i = 0; i < respawnBuffer.Count; i++)
                {
                    Actor actor = respawnBuffer[i];
                    deadRespawnAt.Remove(actor);
                    if (actor != null && actorToSlot.TryGetValue(actor, out int slotIdx))
                    {
                        PlaceBotAtSlot(actor, TestArenaConstants.DummySlots[slotIdx], forceRespawn: true);
                    }
                }
            }
        }

        /// <summary>
        /// Heals/respawns all Red dummies and snaps them cleanly back into their 6 station slots.
        /// Works on any map too (falls back to arranging in a neat line in front of the player).
        /// </summary>
        public int ResetAndArrangeAllDummies()
        {
            lod.ReleaseAll(restoreGame: true);
            deadRespawnAt.Clear();
            return ArrangeAllDummies(respawnDead: true);
        }

        private int ArrangeAllDummies(bool respawnDead)
        {
            var am = ActorManager.instance;
            if (am == null || am.actors == null) return 0;

            int arranged = 0;
            if (isInTestArena)
            {
                ClearSlotTracking();
                int nextSlot = 0;

                for (int i = 0; i < am.actors.Count && nextSlot < TestArenaConstants.DummySlots.Length; i++)
                {
                    Actor actor = am.actors[i];
                    if (actor == null || actor == am.player || actor.team != 1) continue;
                    if (actor.dead && !respawnDead) continue;

                    AssignSlot(actor, nextSlot);
                    PlaceBotAtSlot(actor, TestArenaConstants.DummySlots[nextSlot], forceRespawn: respawnDead || actor.dead);
                    nextSlot++;
                    arranged++;
                }
            }
            else
            {
                // On regular maps: line up nearby/available enemy bots in a neat row 10m ahead of the player
                var cam = Camera.main;
                if (cam == null) return 0;
                Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 baseOrigin = (am.player != null ? am.player.Position() : cam.transform.position) + fwd * 10f;
                int enemyTeam = am.player != null ? (1 - am.player.team) : 1;

                int count = 0;
                for (int i = 0; i < am.actors.Count && count < 8; i++)
                {
                    Actor actor = am.actors[i];
                    if (actor == null || actor == am.player || actor.team != enemyTeam) continue;
                    Vector3 pos = baseOrigin + right * ((count - 3.5f) * 2.2f);
                    pos.y = SampleFloorY(pos);
                    PlaceBotAtSlot(actor, new DummySlot("Custom Line", pos, -fwd), forceRespawn: respawnDead || actor.dead);
                    count++;
                    arranged++;
                }
            }

            return arranged;
        }

        /// <summary>
        /// Spawns or teleports a fresh stationary enemy dummy right where the player's crosshair hits the ground.
        /// Works on both Delirium Test Range and any normal Ravenfield map.
        /// </summary>
        public Actor SpawnDummyAtCrosshair()
        {
            var cam = Camera.main;
            var am = ActorManager.instance;
            if (cam == null || am == null || am.actors == null) return null;

            int mask = ~(1 << Hitbox.LAYER | 1 << Hitbox.RAGDOLL_LAYER | 1 << Hitbox.SEATED_LAYER);
            Vector3 spawnPos;
            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 120f, mask, QueryTriggerInteraction.Ignore))
            {
                spawnPos = hit.point + Vector3.up * 0.05f;
            }
            else
            {
                Vector3 flatFwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                spawnPos = cam.transform.position + flatFwd * 10f;
                spawnPos.y = SampleFloorY(spawnPos);
            }

            Vector3 faceDir = Vector3.ProjectOnPlane(cam.transform.position - spawnPos, Vector3.up);
            if (faceDir.sqrMagnitude < 1e-4f) faceDir = -cam.transform.forward;
            faceDir.Normalize();

            int enemyTeam = am.player != null ? (1 - am.player.team) : 1;
            Actor candidate = FindBestDummyCandidate(am, enemyTeam, cam.transform.position);
            if (candidate == null) return null;

            deadRespawnAt.Remove(candidate);
            // Detach from any previous station slot so auto-respawn doesn't pull it away from crosshair spot
            if (actorToSlot.TryGetValue(candidate, out int oldSlot))
            {
                if (oldSlot >= 0 && oldSlot < slotToActor.Length && slotToActor[oldSlot] == candidate)
                    slotToActor[oldSlot] = null;
                actorToSlot.Remove(candidate);
            }
            PlaceBotAtSlot(candidate, new DummySlot("Crosshair", spawnPos, faceDir), forceRespawn: true);

            if (lod.BotMode == TestBotMode.Normal)
            {
                lod.BotMode = TestBotMode.PureDummy;
            }
            return candidate;
        }

        public bool TeleportPlayerToStation(int stationIndex)
        {
            if (!isInTestArena) return false;
            if (stationIndex < 0 || stationIndex >= TestArenaConstants.TeleportPoints.Length) return false;

            var am = ActorManager.instance;
            if (am == null || am.player == null || am.player.dead) return false;

            var pt = TestArenaConstants.TeleportPoints[stationIndex];
            if (am.player.IsSeated())
            {
                try { am.player.LeaveSeat(false); } catch { }
            }

            Quaternion rot = Quaternion.LookRotation(pt.LookDir, Vector3.up);
            am.player.SetPositionAndRotation(pt.Position, rot);
            return true;
        }

        private void PlaceBotAtSlot(Actor actor, DummySlot slot, bool forceRespawn)
        {
            if (actor == null) return;

            lod.ReleaseActor(actor, restoreGame: !forceRespawn);
            lod.ResetHealthState(actor);

            if (actor.IsSeated())
            {
                try { actor.LeaveSeat(false); } catch { }
            }

            Quaternion rot = Quaternion.LookRotation(slot.FacingDir, Vector3.up);
            Vector3 pos = slot.Position + Vector3.up * 0.05f;

            try
            {
                if (actor.dead || forceRespawn)
                {
                    suppressSpawnSnap = true;
                    try
                    {
                        actor.SpawnAt(pos, rot, null);
                    }
                    finally
                    {
                        suppressSpawnSnap = false;
                    }
                }
                else
                {
                    actor.SetPositionAndRotation(pos, rot);
                    actor.SetHealth(actor.maxHealth);
                    actor.ForceStance(Actor.Stance.Stand);
                    actor.ForceStopAnimatorMovement();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[TestArena] PlaceBotAtSlot warning for {actor.name}: {ex.Message}");
            }

            SnapBotPoseAndFreezeBrain(actor, pos, slot.FacingDir);
        }

        private static readonly MethodInfo AiDropTargetMethod =
            AccessTools.Method(typeof(AiActorController), "DropTarget");
        private static readonly MethodInfo AiDropTargetVehicleMethod =
            AccessTools.Method(typeof(AiActorController), "DropTargetVehicle");
        private static readonly MethodInfo AiLookDirectionMethod =
            AccessTools.Method(typeof(AiActorController), "LookDirection", new[] { typeof(Vector3) });

        private static void SnapBotPoseAndFreezeBrain(Actor actor, Vector3 pos, Vector3 facingDir)
        {
            if (actor == null) return;
            try
            {
                Vector3 flatDir = Vector3.ProjectOnPlane(facingDir, Vector3.up);
                if (flatDir.sqrMagnitude < 1e-4f) flatDir = Vector3.back;
                flatDir.Normalize();

                Quaternion rot = Quaternion.LookRotation(flatDir, Vector3.up);
                actor.SetPositionAndRotation(pos, rot);
                actor.ForceStance(Actor.Stance.Stand);
                actor.ForceStopAnimatorMovement();
                actor.ResetWeaponInputs();

                var ai = actor.controller as AiActorController;
                if (ai != null)
                {
                    ai.CancelPath(true);
                    AiDropTargetMethod?.Invoke(ai, null);
                    AiDropTargetVehicleMethod?.Invoke(ai, null);
                    ai.LeaveCover();
                    if (AiLookDirectionMethod != null)
                        AiLookDirectionMethod.Invoke(ai, new object[] { flatDir });
                    else
                        ai.LookAt(pos + flatDir * 10f);
                    ai.FreezeFacingDirection();
                }
            }
            catch { }
        }

        private int GetOrAssignSlot(Actor actor)
        {
            if (actorToSlot.TryGetValue(actor, out int existing))
                return existing;

            for (int i = 0; i < slotToActor.Length; i++)
            {
                if (slotToActor[i] == null)
                {
                    AssignSlot(actor, i);
                    return i;
                }
            }
            return -1;
        }

        private void AssignSlot(Actor actor, int slotIndex)
        {
            if (actorToSlot.TryGetValue(actor, out int prev) && prev >= 0 && prev < slotToActor.Length)
            {
                if (slotToActor[prev] == actor) slotToActor[prev] = null;
            }
            if (slotToActor[slotIndex] != null)
            {
                actorToSlot.Remove(slotToActor[slotIndex]);
            }
            slotToActor[slotIndex] = actor;
            actorToSlot[actor] = slotIndex;
        }

        private void ClearSlotTracking()
        {
            actorToSlot.Clear();
            deadRespawnAt.Clear();
            Array.Clear(slotToActor, 0, slotToActor.Length);
        }

        private static int CountAliveTeam(ActorManager am, int team)
        {
            int count = 0;
            for (int i = 0; i < am.actors.Count; i++)
            {
                var a = am.actors[i];
                if (a != null && a != am.player && a.team == team && !a.dead)
                    count++;
            }
            return count;
        }

        private static Actor FindBestDummyCandidate(ActorManager am, int enemyTeam, Vector3 camPos)
        {
            Actor deadPick = null;
            Actor farthestAlive = null;
            float farthestDist = -1f;

            for (int i = 0; i < am.actors.Count; i++)
            {
                Actor a = am.actors[i];
                if (a == null || a == am.player || a.team != enemyTeam) continue;
                if (a.dead)
                {
                    deadPick = a;
                    break;
                }
                float d = (a.CenterPosition() - camPos).sqrMagnitude;
                if (d > farthestDist)
                {
                    farthestDist = d;
                    farthestAlive = a;
                }
            }
            return deadPick ?? farthestAlive;
        }

        private static float SampleFloorY(Vector3 pos)
        {
            int mask = ~(1 << Hitbox.LAYER | 1 << Hitbox.RAGDOLL_LAYER | 1 << Hitbox.SEATED_LAYER);
            if (Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 40f, mask, QueryTriggerInteraction.Ignore))
                return hit.point.y + 0.05f;
            return pos.y;
        }
    }
}
