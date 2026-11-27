using System;
using System.Collections.Generic;
using DeliriumRavenfield.Core;
using DeliriumRavenfield.TestArena;
using HarmonyLib;
using MapEditor;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Patches
{
    public static class RavenfieldPatches
    {
        private static readonly Dictionary<Actor, float> nearMissCooldown = new Dictionary<Actor, float>();
        [ThreadStatic] private static Actor damageInProgressActor;

        // 1. Actor.Damage -> locational health override + live stagger, melee/kick, explosion, vehicle ram, fall, or fatal reaction
        [HarmonyPatch(typeof(Actor), nameof(Actor.Damage))]
        private static class Patch_Actor_Damage
        {
            private struct DamagePreState
            {
                public bool WasDead;
                public float PrevHealth;
                public bool WasFallen;
                public Actor PrevInProgress;
                public bool UsedLocational;
                public float RawHealthDamage;
                public BodyHealthState HealthState;
                public HitOutcome Outcome;
            }

            private static void Prefix(Actor __instance, ref DamageInfo info, out DamagePreState __state)
            {
                __state = new DamagePreState
                {
                    WasDead = __instance != null && __instance.dead,
                    PrevHealth = __instance != null ? __instance.health : 0f,
                    WasFallen = __instance != null && __instance.fallenOver,
                    PrevInProgress = damageInProgressActor,
                    UsedLocational = false,
                    RawHealthDamage = info.healthDamage
                };
                damageInProgressActor = __instance;

                if (__instance == null || __instance.dead || Plugin.LodManager == null || Plugin.ConfigInstance == null)
                    return;
                if (!Plugin.ConfigInstance.Enabled.Value || !Plugin.ConfigInstance.EnableLocationalHealth.Value)
                    return;
                if (LodManager.IsAllowingBleedoutKill)
                    return;

                // Only AI bots (not player, not seated in vehicles)
                bool isPlayer = ActorManager.instance != null && __instance == ActorManager.instance.player;
                if (isPlayer || __instance.IsSeated())
                    return;

                bool isBulletOrMelee =
                    info.type == DamageInfo.DamageSourceType.Projectile ||
                    info.type == DamageInfo.DamageSourceType.ProjectileShatter ||
                    info.type == DamageInfo.DamageSourceType.Melee ||
                    info.isKick;
                if (!isBulletOrMelee)
                    return;

                // Skip splash damage so explosions / fragmentation keep normal lethal area logic
                if (info.isSplashDamage)
                    return;

                var hs = Plugin.LodManager.GetOrCreateHealthState(__instance);
                if (hs == null) return;

                Vector3 hitPt = info.point != Vector3.zero ? info.point : __instance.CenterPosition();
                int part;
                bool isSyncedLockstep = false;
                if (Plugin.LodManager.TryGetController(__instance, out var existingCtrl) && existingCtrl.IsSimulating && existingCtrl.Agent != IntPtr.Zero)
                {
                    isSyncedLockstep = existingCtrl.IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected;
                    Vector3 queryPt = isSyncedLockstep ? (hitPt - existingCtrl.SyncVisualOffset) : hitPt;
                    part = nm_agent_nearest_part(existingCtrl.Agent, ToNmVec(queryPt));
                    if (part < 0 || part >= 21) part = 10;
                    if (info.isCriticalHit) part = 12;
                }
                else
                {
                    part = BodyHealthState.EstimatePartWithoutDelirium(__instance, hitPt, info.isCriticalHit);
                }

                BodyZone zone = BodyHealthState.ClassifyZone(__instance, part, hitPt, info.isCriticalHit);
                Vector3 hitNrm = info.direction.sqrMagnitude > 1e-4f ? -info.direction.normalized : __instance.transform.forward;
                float rawDmg = Mathf.Max(info.healthDamage, 1f);

                HitOutcome outcome = hs.ApplyHit(zone, part, rawDmg, hitPt, hitNrm, info.sourceActor);
                __state.UsedLocational = true;
                __state.HealthState = hs;
                __state.Outcome = outcome;

                if (isSyncedLockstep)
                {
                    // Keep the synced test subject alive in Ravenfield's match state so vanilla despawn/respawn timers
                    // never destroy it while we test shots/headshots/wounds live alongside Playroom!
                    info.healthDamage = 0f;
                    info.balanceDamage = 0f;
                    info.isCriticalHit = false;
                }
                else if (outcome.Lethal)
                {
                    // Ensure Ravenfield cleanly registers kill credit for headshots, heart hits, and chest destruction
                    info.healthDamage = Mathf.Max(info.healthDamage, __instance.health + 50f);
                    if (outcome.InstantHeadshot)
                        info.isCriticalHit = true;
                }
                else
                {
                    // Prevent vanilla Actor.Damage from prematurely killing or forcing vanilla FallOver on non-lethal limb/stomach hits
                    float targetFloor = Mathf.Max(5f, (outcome.BloodRemaining / 100f) * __instance.maxHealth);
                    float maxAllowedDmg = Mathf.Max(0f, __instance.health - targetFloor);
                    info.healthDamage = Mathf.Min(info.healthDamage, maxAllowedDmg);
                    info.balanceDamage = 0f;
                    info.isCriticalHit = false;
                }
            }

            private static void Postfix(Actor __instance, DamageInfo info, bool __result, DamagePreState __state)
            {
                damageInProgressActor = __state.PrevInProgress;
                if (__instance == null || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value)
                    return;

                // Handle hits on DyingStagger, DeadSimulating, and DeadFrozen actors BEFORE checking __result
                if (__state.WasDead)
                {
                    if (!Plugin.ConfigInstance.EnableCorpseShootAndWake.Value) return;
                    var deadCtrl = Plugin.LodManager.TryAcquireOrWakeCorpse(__instance);
                    if (deadCtrl == null) return;

                    if (info.type == DamageInfo.DamageSourceType.Explosion || info.type == DamageInfo.DamageSourceType.StickyExplosive)
                    {
                        float rawF = info.impactForce.magnitude > 1f ? info.impactForce.magnitude : (info.healthDamage * 4f);
                        deadCtrl.ApplyExplosion(info.point != Vector3.zero ? info.point : __instance.CenterPosition(), 6f, rawF, fatal: true, isAlreadyNormalizedSpeed: false);
                    }
                    else if (info.type == DamageInfo.DamageSourceType.Melee || info.isKick)
                    {
                        deadCtrl.ApplyMelee(info, fatal: true);
                    }
                    else
                    {
                        bool isAlreadyCorpse = deadCtrl.Phase != RagdollPhase.DyingStagger;
                        deadCtrl.ApplyShot(info, fatal: true, alreadyDeadCorpse: isAlreadyCorpse);
                    }
                    return;
                }

                // Locational health hit routing (fires even if clamped healthDamage was 0!)
                if (__state.UsedLocational && __state.HealthState != null)
                {
                    // Restore raw damage on local copy for accurate impulse normalization
                    DamageInfo rawInfo = info;
                    rawInfo.healthDamage = __state.RawHealthDamage;

                    if (__state.Outcome.Lethal || __instance.dead)
                    {
                        var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                        if (ctrl != null)
                        {
                            ctrl.HealthState = __state.HealthState;
                            ctrl.ApplyLocationalShot(rawInfo, __state.Outcome, __state.HealthState);
                        }
                        return;
                    }

                    // Non-lethal locational wound inside LOD range -> acquire Delirium and drive wounded/hop/crawl/clutch/surrender
                    var liveCtrl = Plugin.LodManager.TryAcquireForWounded(__instance);
                    if (liveCtrl != null)
                    {
                        liveCtrl.HealthState = __state.HealthState;
                        liveCtrl.ApplyLocationalShot(rawInfo, __state.Outcome, __state.HealthState);
                    }
                    else if (__state.HealthState.RightArmRuined)
                    {
                        // Even outside Delirium LOD range, gun-arm ruined bots drop their weapon & stop shooting
                        var fallback = new WoundedCombatController(__instance, Plugin.ConfigInstance);
                        fallback.HandleSurrenderSetup(__state.HealthState);
                    }
                    return;
                }

                if (!__result)
                    return;

                bool becameDead = !__state.WasDead && __instance.dead;
                bool isAlive = !__instance.dead;

                if (becameDead)
                {
                    if (LodManager.IsAllowingBleedoutKill)
                    {
                        // Handled directly by LodManager.KillActorFromBleedout -> ApplyBleedoutDeath
                        return;
                    }
                    var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                    ctrl?.ApplyDeath(info);
                    return;
                }

                if (!isAlive) return;

                // Alive reactions by damage source type (Player, Explosions, FallDamage, or when LocationalHealth is disabled)
                if (info.type == DamageInfo.DamageSourceType.Explosion || info.type == DamageInfo.DamageSourceType.StickyExplosive)
                {
                    var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                    float rawF = info.impactForce.magnitude > 1f ? info.impactForce.magnitude : (info.healthDamage * 4f);
                    ctrl?.ApplyExplosion(info.point != Vector3.zero ? info.point : __instance.CenterPosition(), 6f, rawF, fatal: false, isAlreadyNormalizedSpeed: false);
                }
                else if (info.type == DamageInfo.DamageSourceType.Melee || info.isKick)
                {
                    var ctrl = Plugin.LodManager.TryAcquireForLiveReaction(__instance);
                    ctrl?.ApplyMelee(info, fatal: false);
                }
                else if (info.type == DamageInfo.DamageSourceType.FallDamage)
                {
                    var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                    ctrl?.ApplyHighFall();
                }
                else if (info.type == DamageInfo.DamageSourceType.Projectile || info.type == DamageInfo.DamageSourceType.ProjectileShatter)
                {
                    if (!Plugin.ConfigInstance.EnableLiveShotStagger.Value && !__instance.fallenOver) return;
                    float totalImpact = info.healthDamage + info.balanceDamage * 0.5f;
                    if (totalImpact < Plugin.ConfigInstance.MinDamageForLiveStagger.Value && !__instance.fallenOver)
                        return;

                    var ctrl = Plugin.LodManager.TryAcquireForLiveReaction(__instance);
                    ctrl?.ApplyShot(info, fatal: false);
                }
            }

            private static Exception Finalizer(Exception __exception, DamagePreState __state)
            {
                damageInProgressActor = __state.PrevInProgress;
                return __exception;
            }
        }

        // 1b. Actor.ResupplyHealth -> Medipack stops bleeding & restores blood
        [HarmonyPatch(typeof(Actor), nameof(Actor.ResupplyHealth))]
        private static class Patch_Actor_ResupplyHealth
        {
            private static void Postfix(Actor __instance, bool __result)
            {
                if (!__result || __instance == null || Plugin.LodManager == null || Plugin.ConfigInstance == null)
                    return;
                if (!Plugin.ConfigInstance.EnableLocationalHealth.Value || !Plugin.ConfigInstance.EnableMedkitStopBleed.Value)
                    return;

                if (Plugin.LodManager.TryGetHealthState(__instance, out var hs))
                {
                    hs.OnHealed(35f);
                }
            }
        }

        // 2. Actor.FallOver & Actor.KnockOver -> route non-dead knockdowns through Delirium
        [HarmonyPatch(typeof(Actor), nameof(Actor.FallOver))]
        private static class Patch_Actor_FallOver
        {
            private static void Postfix(Actor __instance)
            {
                if (__instance == null || __instance == damageInProgressActor || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return;
                if (__instance.dead) return;
                if (Plugin.LodManager.TryGetController(__instance, out var existing) && existing.IsSimulating) return;

                var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                ctrl?.ApplyFallOver(Vector3.zero);
            }
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.KnockOver))]
        private static class Patch_Actor_KnockOver
        {
            private static void Postfix(Actor __instance, Vector3 force)
            {
                if (__instance == null || __instance == damageInProgressActor || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return;
                var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                ctrl?.ApplyFallOver(force);
            }
        }

        // 3. Actor.Die -> handle silent/scripted kills that bypassed Actor.Damage
        [HarmonyPatch(typeof(Actor), "Die")]
        private static class Patch_Actor_Die
        {
            private static void Postfix(Actor __instance, DamageInfo info, bool isSilentKill)
            {
                if (__instance == null || __instance == damageInProgressActor || isSilentKill || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return;
                if (LodManager.IsAllowingBleedoutKill) return;
                if (Plugin.LodManager.TryGetController(__instance, out var existing) && existing.IsDeadOrFrozen) return;

                var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                ctrl?.ApplyDeath(info);
            }
        }

        // 4. Suppress vanilla automatic GetUp while Delirium is still simulating active stagger/fall/wounded crawl
        [HarmonyPatch(typeof(Actor), "UpdateRagdollStates")]
        private static class Patch_Actor_UpdateRagdollStates
        {
            private static bool Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingVanillaGetUp(__instance);
            }
        }

        // 4b. Keep getting-up AI bots planted flush on the real floor (ActiveRaggy.SyncAnimatorPosition casts an unmasked
        // ray that hits the bot's own standing hitboxes/ragdoll colliders, lifting the model ~0.8-1.0m off the ground!)
        private static void SnapActorToGroundAfterGetUp(Actor actor)
        {
            if (actor == null || actor.dead || actor.IsSeated() || actor.IsOnLadder() || actor.immersedInWater) return;
            if (ActorManager.instance != null && actor == ActorManager.instance.player) return;
            if (Plugin.Runtime == null || !Plugin.Runtime.IsReady) return;

            if (actor.ragdoll != null && actor.ragdoll.transform != null && actor.ragdoll.transform != actor.transform)
            {
                if (actor.ragdoll.transform.localPosition.sqrMagnitude > 1e-4f)
                {
                    actor.ragdoll.transform.localPosition = Vector3.zero;
                    actor.ragdoll.transform.localRotation = Quaternion.identity;
                }
            }

            Vector3 pos = actor.transform.position;
            float groundY = Plugin.Runtime.SampleGroundHeight(pos + Vector3.up * 0.5f, actor.transform, pos.y);
            float diff = pos.y - groundY;
            if (Mathf.Abs(diff) > 0.05f && Mathf.Abs(diff) < 4.0f)
            {
                Transform ragObjTf = actor.ragdoll != null && actor.ragdoll.ragdollObject != null ? actor.ragdoll.ragdollObject.transform : null;
                Transform origParent = ragObjTf != null ? ragObjTf.parent : null;
                bool preserveRag = ragObjTf != null && ragObjTf.gameObject.activeSelf;
                if (preserveRag) ragObjTf.SetParent(null, true);

                actor.SetPositionAndRotation(new Vector3(pos.x, groundY, pos.z), actor.transform.rotation);

                if (preserveRag) ragObjTf.SetParent(origParent, true);
            }
        }

        [HarmonyPatch(typeof(Actor), "UpdateGetup")]
        private static class Patch_Actor_UpdateGetup
        {
            private static void Postfix(Actor __instance)
            {
                if (!Plugin.ConfigInstance.Enabled.Value) return;
                SnapActorToGroundAfterGetUp(__instance);
            }
        }

        [HarmonyPatch(typeof(Actor), "FinishGettingUp")]
        private static class Patch_Actor_FinishGettingUp
        {
            private static void Postfix(Actor __instance)
            {
                if (!Plugin.ConfigInstance.Enabled.Value) return;
                SnapActorToGroundAfterGetUp(__instance);
            }
        }

        // 5. Pause vanilla AI walking only when Delirium owns ground/stagger/surrender (WoundedStanding uses NavMesh anchor!)
        [HarmonyPatch(typeof(Actor), "UpdateMovement")]
        private static class Patch_Actor_UpdateMovement
        {
            private static bool Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingMovement(__instance);
            }
        }

        // Suppress vanilla AI firing while Delirium owns the ragdoll (WoundedCombatController fires controlled one-handed bursts)
        [HarmonyPatch(typeof(Actor), "CanFireWeapon")]
        private static class Patch_Actor_CanFireWeapon
        {
            private static bool Prefix(Actor __instance, ref bool __result)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                if (Plugin.LodManager.IsSuppressingWeaponFire(__instance))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }

        // Prevent automatic magazine reloads while a bot is wounded, staggering, crawling, or fallen
        [HarmonyPatch(typeof(Weapon), nameof(Weapon.Reload))]
        private static class Patch_Weapon_Reload
        {
            private static bool Prefix(Weapon __instance)
            {
                if (__instance == null || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return true;
                var user = __instance.user;
                if (user != null && Plugin.LodManager.IsSuppressingReload(user))
                {
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(Weapon), "AutoReload")]
        private static class Patch_Weapon_AutoReload
        {
            private static bool Prefix(Weapon __instance)
            {
                if (__instance == null || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return true;
                var user = __instance.user;
                if (user != null && Plugin.LodManager.IsSuppressingReload(user))
                {
                    return false;
                }
                return true;
            }
        }

        // 5a. Briefly defer weapon destruction on sudden death when startled reflex blind-fire triggers
        [HarmonyPatch(typeof(Actor), nameof(Actor.DropWeapon))]
        private static class Patch_Actor_DropWeapon
        {
            private static bool Prefix(Actor __instance, int slot)
            {
                if (ActorRagdollController.IsAllowingDeferredDrop) return true;
                if (__instance == null || Plugin.LodManager == null || Plugin.ConfigInstance == null) return true;
                if (!Plugin.ConfigInstance.Enabled.Value || !Plugin.ConfigInstance.EnableBlindFire.Value) return true;
                if (ActorManager.instance != null && __instance == ActorManager.instance.player) return true;
                if (!__instance.dead || __instance.IsSeated()) return true;
                if (slot < 0 || __instance.weapons == null || slot >= __instance.weapons.Length) return true;

                Weapon w = __instance.weapons[slot];
                if (w == null || w != __instance.activeWeapon) return true;
                if (!ActorRagdollController.IsEligibleBlindFireWeapon(w)) return true;

                var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                if (ctrl != null && ctrl.TryDeferWeaponDropForBlindFire(w, slot))
                {
                    return false;
                }
                return true;
            }
        }

        // 5b. Prevent ActiveRaggy & Unity PhysX from fighting or resetting Delirium-controlled ragdolls
        [HarmonyPatch(typeof(ActiveRaggy), nameof(ActiveRaggy.Ragdoll))]
        private static class Patch_ActiveRaggy_Ragdoll
        {
            private static bool Prefix(ActiveRaggy __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSimulatingRaggy(__instance);
            }
        }

        [HarmonyPatch(typeof(ActiveRaggy), "Update")]
        private static class Patch_ActiveRaggy_Update
        {
            private static bool Prefix(ActiveRaggy __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsOwnedByDelirium(__instance);
            }
        }

        [HarmonyPatch(typeof(ActiveRaggy), "LateUpdate")]
        private static class Patch_ActiveRaggy_LateUpdate
        {
            private static bool Prefix(ActiveRaggy __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsOwnedByDelirium(__instance);
            }
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyRigidbodyForce))]
        private static class Patch_Actor_ApplyRigidbodyForce
        {
            private static bool Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return true;
                if (__instance == damageInProgressActor && Plugin.LodManager.IsWithinLodRange(__instance)) return false;
                return !Plugin.LodManager.IsOwnedByDelirium(__instance.ragdoll);
            }
        }

        // 6. Clean lifecycle cleanup on respawn, seat entry, skin change, deactivate
        [HarmonyPatch(typeof(Actor), nameof(Actor.SpawnAt))]
        private static class Patch_Actor_SpawnAt
        {
            private static void Prefix(Actor __instance)
            {
                Plugin.LodManager?.ReleaseActor(__instance, restoreGame: false);
                Plugin.LodManager?.ResetHealthState(__instance);
            }

            private static void Postfix(Actor __instance)
            {
                Plugin.TestArenaManager?.OnActorSpawned(__instance);
            }
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.SetModelSkin))]
        private static class Patch_Actor_SetModelSkin
        {
            private static void Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return;
                // ActorManager.ApplyOverrideActorSkin only swaps sharedMesh & materials on the SAME ragdoll skeleton.
                // Never tear down an active/dying/dead Delirium ragdoll when a Gore/Dismemberment mutator swaps skins on death!
                if (__instance.dead) return;
                if (Plugin.LodManager.TryGetController(__instance, out var ctrl) && (ctrl.IsSimulating || ctrl.IsDeadOrFrozen))
                    return;
                Plugin.LodManager.ReleaseActor(__instance, restoreGame: true);
            }
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.EnterSeat))]
        private static class Patch_Actor_EnterSeat
        {
            private static void Prefix(Actor __instance) => Plugin.LodManager?.ReleaseActor(__instance, restoreGame: true);
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.Deactivate))]
        private static class Patch_Actor_Deactivate
        {
            private static void Prefix(Actor __instance)
            {
                Plugin.LodManager?.ReleaseActor(__instance, restoreGame: false);
                Plugin.LodManager?.ResetHealthState(__instance);
            }
        }

        // 7. ActorManager.Explode -> unified Delirium blast wave for nearby actors
        [HarmonyPatch(typeof(ActorManager), nameof(ActorManager.Explode), new[] { typeof(ExplosionInfo), typeof(bool) })]
        private static class Patch_ActorManager_Explode
        {
            private static void Postfix(ExplosionInfo info)
            {
                if (Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value) return;
                var am = ActorManager.instance;
                if (am == null || info.configuration == null) return;

                float radius = Mathf.Max(info.configuration.balanceRange, info.configuration.damageRange);
                if (radius <= 0.5f) return;

                var nearby = ActorManager.ActorsInRange(info.point, radius);
                if (nearby == null) return;

                float rawForce = info.configuration.force;
                for (int i = 0; i < nearby.Count; i++)
                {
                    var actor = nearby[i];
                    if (actor == null || actor.IsSeated()) continue;
                    var ctrl = actor.dead
                        ? Plugin.LodManager.TryAcquireOrWakeCorpse(actor)
                        : Plugin.LodManager.TryAcquireForFallOrDeath(actor);
                    ctrl?.ApplyExplosion(info.point, radius, rawForce, fatal: actor.dead, isAlreadyNormalizedSpeed: false);
                }
            }
        }

        // 8. Projectile.Travel -> lightweight near-miss flinch detection for nearby alive bots
        [HarmonyPatch(typeof(Projectile), "Travel")]
        private static class Patch_Projectile_Travel
        {
            private static readonly System.Reflection.FieldInfo OwnerTeamField =
                AccessTools.Field(typeof(Projectile), "ownerTeam");

            private static void Postfix(Projectile __instance, Vector3 delta, bool __result)
            {
                if (__result || __instance == null || Plugin.LodManager == null) return;
                var cfg = Plugin.ConfigInstance;
                if (!cfg.Enabled.Value || !cfg.EnableNearMissFlinch.Value) return;

                var cam = Camera.main;
                if (cam == null) return;

                Vector3 tip = __instance.transform.position;
                float camLimit = cfg.NearMissDistanceLimit.Value;
                if ((tip - cam.transform.position).sqrMagnitude > camLimit * camLimit) return;

                float segLen = delta.magnitude;
                if (segLen < 0.05f) return;
                Vector3 start = tip - delta;
                Vector3 dir = delta / segLen;

                var am = ActorManager.instance;
                if (am == null || am.actors == null) return;

                int projTeam = __instance.killCredit != null
                    ? __instance.killCredit.team
                    : (OwnerTeamField != null ? (int)OwnerTeamField.GetValue(__instance) : -1);

                float radius = cfg.NearMissRadius.Value;
                float radiusSqr = radius * radius;
                float now = Time.time;

                for (int i = 0; i < am.actors.Count; i++)
                {
                    var actor = am.actors[i];
                    if (actor == null || actor.dead || actor.fallenOver || actor.IsSeated() || actor == am.player)
                        continue;
                    if (projTeam >= 0 && actor.team == projTeam)
                        continue;
                    // Don't interrupt wounded/crawling/surrendering bots with near-miss stagger takeover
                    if (Plugin.LodManager.TryGetHealthState(actor, out var hs) && hs.IsWounded)
                        continue;

                    if (nearMissCooldown.TryGetValue(actor, out float nextOk) && now < nextOk)
                        continue;

                    Vector3 targetPos = actor.CenterPosition() + Vector3.up * 0.35f;
                    Vector3 toTarget = targetPos - start;
                    float proj = Vector3.Dot(toTarget, dir);
                    if (proj < 0f || proj > segLen) continue;

                    Vector3 closest = start + dir * proj;
                    if ((targetPos - closest).sqrMagnitude <= radiusSqr)
                    {
                        nearMissCooldown[actor] = now + 2.0f;
                        var ctrl = Plugin.LodManager.TryAcquireForLiveReaction(actor);
                        ctrl?.ApplyNearMiss(start, dir);
                    }
                }
            }
        }

        // 9. Dedicated Test Arena & Playroom Mirror Map Picker injection & virtual MapDescriptor interception
        [HarmonyPatch(typeof(MapPicker), "GetBuiltInEntries")]
        private static class Patch_MapPicker_GetBuiltInEntries
        {
            private static void Postfix(ref List<IPickerEntryData> __result)
            {
                if (__result == null) return;
                bool hasArena = false;
                bool hasMirror = false;
                for (int i = 0; i < __result.Count; i++)
                {
                    if (__result[i] is MapEntryData existing)
                    {
                        if (TestArenaConstants.IsVirtualTestMapPath(existing.sceneName)) hasArena = true;
                        if (PlayroomMirrorDefinitions.IsVirtualMirrorMapPath(existing.sceneName)) hasMirror = true;
                    }
                }
                if (!hasArena) __result.Add(TestArenaConstants.CreateMapEntry());
                if (!hasMirror) __result.Add(PlayroomMirrorDefinitions.CreateMapEntry());
            }
        }

        [HarmonyPatch(typeof(MapDescriptor), nameof(MapDescriptor.LoadFile))]
        private static class Patch_MapDescriptor_LoadFile
        {
            private static bool Prefix(string filePath, ref MapDescriptor.ParseResult __result)
            {
                if (TestArenaConstants.IsVirtualTestMapPath(filePath))
                {
                    __result = new MapDescriptor.ParseResult(null, new TestArenaSceneConstructor());
                    return false;
                }
                if (PlayroomMirrorDefinitions.IsVirtualMirrorMapPath(filePath))
                {
                    __result = new MapDescriptor.ParseResult(null, new PlayroomMirrorSceneConstructor());
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(MapEntryData), "LoadOrGenerateMetaData")]
        private static class Patch_MapEntryData_LoadOrGenerateMetaData
        {
            private static bool Prefix(MapEntryData __instance, string fullScenePath)
            {
                if (__instance != null && (TestArenaConstants.IsVirtualTestMapPath(__instance.sceneName) ||
                                           TestArenaConstants.IsVirtualTestMapPath(fullScenePath) ||
                                           PlayroomMirrorDefinitions.IsVirtualMirrorMapPath(__instance.sceneName) ||
                                           PlayroomMirrorDefinitions.IsVirtualMirrorMapPath(fullScenePath)))
                {
                    __instance.hasLoadedMetaData = true;
                    return false;
                }
                return true;
            }
        }

        // 10. Stationary Dummy AI Lock (prevents scanning, crouching/proning, squad orders, vehicle boarding, and firing)
        [HarmonyPatch(typeof(AiActorController), nameof(AiActorController.TickAiCoroutines))]
        private static class Patch_AiActorController_TickAiCoroutines
        {
            private static bool Prefix(AiActorController __instance)
            {
                if (__instance == null || __instance.actor == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingAiBrain(__instance.actor);
            }
        }

        [HarmonyPatch(typeof(AiActorController), "UpdateStance")]
        private static class Patch_AiActorController_UpdateStance
        {
            private static bool Prefix(AiActorController __instance)
            {
                if (__instance == null || __instance.actor == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingAiBrain(__instance.actor);
            }
        }

        [HarmonyPatch(typeof(AiActorController), nameof(AiActorController.WantsToFire))]
        private static class Patch_AiActorController_WantsToFire
        {
            private static bool Prefix(AiActorController __instance, ref bool __result)
            {
                if (__instance == null || __instance.actor == null || Plugin.LodManager == null) return true;
                if (Plugin.LodManager.IsSuppressingWeaponFire(__instance.actor))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }
    }
}
