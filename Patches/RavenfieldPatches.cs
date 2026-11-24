using System;
using System.Collections.Generic;
using DeliriumRavenfield.Core;
using HarmonyLib;
using UnityEngine;

namespace DeliriumRavenfield.Patches
{
    public static class RavenfieldPatches
    {
        private static readonly Dictionary<Actor, float> nearMissCooldown = new Dictionary<Actor, float>();
        [ThreadStatic] private static Actor damageInProgressActor;

        // 1. Actor.Damage -> live shot stagger, melee/kick, explosion, vehicle ram, fall damage, or fatal reaction
        [HarmonyPatch(typeof(Actor), nameof(Actor.Damage))]
        private static class Patch_Actor_Damage
        {
            private struct DamagePreState
            {
                public bool WasDead;
                public float PrevHealth;
                public bool WasFallen;
                public Actor PrevInProgress;
            }

            private static void Prefix(Actor __instance, out DamagePreState __state)
            {
                __state = new DamagePreState
                {
                    WasDead = __instance != null && __instance.dead,
                    PrevHealth = __instance != null ? __instance.health : 0f,
                    WasFallen = __instance != null && __instance.fallenOver,
                    PrevInProgress = damageInProgressActor
                };
                damageInProgressActor = __instance;
            }

            private static void Postfix(Actor __instance, DamageInfo info, bool __result, DamagePreState __state)
            {
                damageInProgressActor = __state.PrevInProgress;
                if (!__result || __instance == null || Plugin.LodManager == null || !Plugin.ConfigInstance.Enabled.Value)
                    return;

                bool becameDead = !__state.WasDead && __instance.dead;
                bool isAlive = !__instance.dead;

                if (becameDead)
                {
                    var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                    ctrl?.ApplyDeath(info);
                    return;
                }

                // Already dead corpse taking extra bullets/explosions
                if (__state.WasDead)
                {
                    if (Plugin.LodManager.TryGetController(__instance, out var deadCtrl) && deadCtrl.IsSimulating)
                    {
                        if (info.type == DamageInfo.DamageSourceType.Explosion || info.type == DamageInfo.DamageSourceType.StickyExplosive)
                            deadCtrl.ApplyExplosion(info.point != Vector3.zero ? info.point : __instance.CenterPosition(), 6f, info.impactForce.magnitude, fatal: true, isAlreadyNormalizedSpeed: false);
                        else
                            deadCtrl.ApplyShot(info, fatal: true, alreadyDeadCorpse: true);
                    }
                    return;
                }

                if (!isAlive) return;

                // Alive reactions by damage source type
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

        // 2. Actor.FallOver & Actor.KnockOver -> route non-dead knockdowns through Delirium
        //    Skipped while inside Actor.Damage so we never double-apply KnockOver + Shot impulses in one hit!
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
                if (Plugin.LodManager.TryGetController(__instance, out var existing) && existing.IsDeadOrFrozen) return;

                var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(__instance);
                ctrl?.ApplyDeath(info);
            }
        }

        // 4. Suppress vanilla automatic GetUp while Delirium is still simulating active stagger/fall
        [HarmonyPatch(typeof(Actor), "UpdateRagdollStates")]
        private static class Patch_Actor_UpdateRagdollStates
        {
            private static bool Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingVanillaGetUp(__instance);
            }
        }

        // 5. Pause AI walking & firing while Delirium controls the actor (or when Test Range mode is active)
        [HarmonyPatch(typeof(Actor), "UpdateMovement")]
        private static class Patch_Actor_UpdateMovement
        {
            private static bool Prefix(Actor __instance)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                return !Plugin.LodManager.IsSuppressingControl(__instance);
            }
        }

        [HarmonyPatch(typeof(Actor), "CanFireWeapon")]
        private static class Patch_Actor_CanFireWeapon
        {
            private static bool Prefix(Actor __instance, ref bool __result)
            {
                if (__instance == null || Plugin.LodManager == null) return true;
                if (Plugin.LodManager.IsSuppressingControl(__instance))
                {
                    __result = false;
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
                // If Delirium is already simulating this ragdoll (e.g. bot dies mid-stagger), do NOT reset bones back to animatedTransforms!
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
            private static void Prefix(Actor __instance) => Plugin.LodManager?.ReleaseActor(__instance, restoreGame: false);
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.SetModelSkin))]
        private static class Patch_Actor_SetModelSkin
        {
            private static void Prefix(Actor __instance) => Plugin.LodManager?.ReleaseActor(__instance, restoreGame: true);
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.EnterSeat))]
        private static class Patch_Actor_EnterSeat
        {
            private static void Prefix(Actor __instance) => Plugin.LodManager?.ReleaseActor(__instance, restoreGame: true);
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.Deactivate))]
        private static class Patch_Actor_Deactivate
        {
            private static void Prefix(Actor __instance) => Plugin.LodManager?.ReleaseActor(__instance, restoreGame: false);
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
                    var ctrl = Plugin.LodManager.TryAcquireForFallOrDeath(actor);
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
                // __result == true means the projectile hit something; only check near-miss while still flying
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
    }
}
