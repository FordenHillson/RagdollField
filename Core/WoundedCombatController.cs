using System;
using DeliriumRavenfield.Config;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    public sealed class WoundedCombatController
    {
        private readonly Actor actor;
        private readonly ModConfig config;

        private float coverSeekCooldown;
        private float burstStateTimer;
        private float nextShotTime;
        private int burstShotsLeft;
        private bool isCrawlingBurstPause;
        private bool isPointingArm;
        private float pointArmRefreshTimer;

        public bool IsShootingBurst => isCrawlingBurstPause;
        public int CompletedBurstCount { get; private set; }
        public Vector3 CurrentAimTarget { get; private set; }
        public Vector3 CurrentCoverWaypoint { get; private set; }

        public WoundedCombatController(Actor actor, ModConfig config)
        {
            this.actor = actor;
            this.config = config;
        }

        public void Reset()
        {
            coverSeekCooldown = 0f;
            burstStateTimer = 0f;
            nextShotTime = 0f;
            burstShotsLeft = 0;
            isCrawlingBurstPause = false;
            isPointingArm = false;
            pointArmRefreshTimer = 0f;
            CompletedBurstCount = 0;
            CurrentAimTarget = Vector3.zero;
            CurrentCoverWaypoint = Vector3.zero;
        }

        public bool HasUsableFirearm()
        {
            if (actor == null || actor.dead) return false;
            if (ActorRagdollController.IsEligibleBlindFireWeapon(actor.activeWeapon)) return true;
            if (actor.weapons == null) return false;
            for (int slot = 0; slot < actor.weapons.Length; slot++)
            {
                Weapon cand = actor.weapons[slot];
                if (ActorRagdollController.IsEligibleBlindFireWeapon(cand))
                    return true;
            }
            return false;
        }

        public void RestoreActorFlags(BodyHealthState health)
        {
            if (actor == null) return;
            actor.movementSpeedMultiplier = 1f;
            if (health != null && health.AppliedAiIgnoreForSurrender)
            {
                actor.isIgnored = false;
                health.AppliedAiIgnoreForSurrender = false;
            }
        }

        public void HandleSurrenderSetup(BodyHealthState health)
        {
            if (actor == null || health == null) return;

            if (!health.HasDroppedWeaponForSurrender)
            {
                health.HasDroppedWeaponForSurrender = true;
                DropActiveWeaponForSurrender();
            }

            if (config.EnableSurrenderAiIgnore.Value && !health.AppliedAiIgnoreForSurrender)
            {
                actor.isIgnored = true;
                health.AppliedAiIgnoreForSurrender = true;
            }
            actor.movementSpeedMultiplier = 0f;
            isCrawlingBurstPause = false;
            burstShotsLeft = 0;
        }

        private void DropActiveWeaponForSurrender()
        {
            if (actor == null || actor.dead) return;
            try
            {
                int slot = actor.activeWeaponSlot;
                if (slot >= 0 && actor.weapons != null && slot < actor.weapons.Length && actor.weapons[slot] != null)
                {
                    ActorRagdollController.SetAllowDeferredDrop(true);
                    try
                    {
                        actor.DropWeapon(slot);
                    }
                    finally
                    {
                        ActorRagdollController.SetAllowDeferredDrop(false);
                    }
                }
                else if (actor.activeWeapon != null)
                {
                    actor.activeWeapon.Drop();
                }
            }
            catch { }
        }

        public void TickStandingWounded(BodyHealthState health, IntPtr agent, IntPtr world, float dt, Transform ragdollRightHand, bool allowFire)
        {
            if (actor == null || health == null) return;

            bool suppressWoundedAi = Plugin.LodManager != null && Plugin.LodManager.IsSuppressingWoundedCombat(actor);
            if (suppressWoundedAi)
            {
                actor.movementSpeedMultiplier = 0f;
                isCrawlingBurstPause = false;
                burstShotsLeft = 0;
                StopPointArm(agent, world);
                return;
            }

            // Movement speed scaled by wound severity & blood weakness
            float speedMul = 1f;
            switch (health.Condition)
            {
                case WoundedCondition.ArmOffhandDisabled: speedMul = 0.90f; break;
                case WoundedCondition.StomachHurt: speedMul = 0.62f; break;
                case WoundedCondition.ChestHurt: speedMul = 0.58f; break;
                case WoundedCondition.HoppingOneLeg: speedMul = 0.46f; break;
                default: speedMul = 0.75f; break;
            }
            speedMul *= Mathf.Lerp(1f, 0.55f, health.Weakness01);
            actor.movementSpeedMultiplier = Mathf.Clamp(speedMul, 0.22f, 1f);

            // Ask Ravenfield AI to seek cover away from the shooter
            TickCoverSeeking(health, dt);

            // Handle one-handed weapon choice + aiming/firing
            if (allowFire && !health.RightArmRuined)
            {
                EnsureOneHandedWeaponReady(health);
                TickAimAndShoot(health, agent, world, dt, ragdollRightHand, isCrawling: false, isOnGroundClutch: false);
            }
            else
            {
                StopPointArm(agent, world);
            }
        }

        public void TickGroundWounded(BodyHealthState health, IntPtr agent, IntPtr world, float dt, Vector3 pelvisPos, Transform ragdollRightHand, bool isStomachGroundClutch, bool allowFire)
        {
            if (actor == null) return;

            actor.movementSpeedMultiplier = 0f;
            bool suppressWoundedAi = Plugin.LodManager != null && Plugin.LodManager.IsSuppressingWoundedCombat(actor);
            if (suppressWoundedAi)
            {
                isCrawlingBurstPause = false;
                burstShotsLeft = 0;
                StopPointArm(agent, world);
                return;
            }

            if (!isStomachGroundClutch && health != null)
            {
                TickCoverSeeking(health, dt);
                CurrentCoverWaypoint = ResolveCrawlWaypoint(health, pelvisPos);
            }

            bool rightArmRuined = health != null && health.RightArmRuined;
            if (allowFire && !rightArmRuined)
            {
                EnsureOneHandedWeaponReady(health);
                TickAimAndShoot(health, agent, world, dt, ragdollRightHand, isCrawling: !isStomachGroundClutch, isOnGroundClutch: isStomachGroundClutch);
            }
            else
            {
                isCrawlingBurstPause = false;
                CurrentAimTarget = Vector3.zero;
                StopPointArm(agent, world);
            }
        }

        private void TickCoverSeeking(BodyHealthState health, float dt)
        {
            coverSeekCooldown -= dt;
            if (coverSeekCooldown > 0f) return;
            coverSeekCooldown = 1.8f;

            var ai = actor.controller as AiActorController;
            if (ai == null) return;

            try
            {
                if (ai.IsInCover() || ai.IsMovingToCover() || ai.HasPath()) return;
                Vector3 threat = ResolveThreatPos(health, ai);
                bool found = false;
                if (threat != Vector3.zero)
                {
                    found = ai.FindCoverAwayFrom(threat);
                }
                if (!found)
                {
                    found = ai.FindCover();
                }
                if (!found)
                {
                    // Fallback when indoor/open map lacks cover nodes: actively path away from shooter!
                    Vector3 myPos = actor.transform.position;
                    Vector3 away = threat != Vector3.zero
                        ? Vector3.ProjectOnPlane(myPos - threat, Vector3.up)
                        : Vector3.ProjectOnPlane(-actor.transform.forward, Vector3.up);
                    if (away.sqrMagnitude < 0.04f) away = actor.transform.right;
                    Vector3 dest = myPos + away.normalized * 8f;
                    ai.Goto(dest, false);
                }
            }
            catch { }
        }

        private Vector3 ResolveCrawlWaypoint(BodyHealthState health, Vector3 pelvisPos)
        {
            var ai = actor.controller as AiActorController;
            if (ai != null)
            {
                try
                {
                    if (ai.HasPath())
                    {
                        if (ai.CurrentWaypoint(out Vector3 wp) && (wp - pelvisPos).sqrMagnitude > 0.25f)
                            return wp;
                        Vector3 end = ai.PathEndPoint();
                        if ((end - pelvisPos).sqrMagnitude > 0.25f)
                            return end;
                    }
                }
                catch { }
            }

            // Fallback: crawl away from last threat direction
            Vector3 threat = health.LastThreatPos;
            if (threat != Vector3.zero)
            {
                Vector3 away = Vector3.ProjectOnPlane(pelvisPos - threat, Vector3.up);
                if (away.sqrMagnitude > 0.04f)
                    return pelvisPos + away.normalized * 6f;
            }
            return pelvisPos + Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up).normalized * 4f;
        }

        private Vector3 ResolveThreatPos(BodyHealthState health, AiActorController ai)
        {
            if (ai != null)
            {
                try
                {
                    if (ai.HasTarget())
                    {
                        var t = ai.GetTarget();
                        if (t != null && !t.dead)
                            return t.CenterPosition();
                    }
                }
                catch { }
            }
            if (health.LastAttacker != null && !health.LastAttacker.dead)
                return health.LastAttacker.CenterPosition();
            return health.LastThreatPos;
        }

        private void EnsureOneHandedWeaponReady(BodyHealthState health)
        {
            Weapon active = actor.activeWeapon;
            bool needSidearm = false;

            if (active == null || active.IsMeleeWeapon())
            {
                needSidearm = true;
            }
            else if (health != null && (health.LeftArmRuined || health.StomachRuined || health.BothLegsRuined || health.AnyLegRuined))
            {
                // Heavy/anti-armor launchers or empty primary mag when off-hand is ruined -> prefer sidearm pistol if available
                bool isHeavy = active.configuration != null &&
                               (active.configuration.effectiveRange > 250f ||
                                active.configuration.loud ||
                                active.slot == 2 ||
                                active.slot == 3);
                bool emptyBrokenOffhand = !active.HasLoadedAmmo() || active.ammo <= 0;
                if (isHeavy || emptyBrokenOffhand)
                {
                    needSidearm = true;
                }
            }

            if (!needSidearm || actor.weapons == null) return;

            // Ravenfield slot 1 is typically secondary/pistol
            for (int slot = 1; slot < actor.weapons.Length; slot++)
            {
                Weapon cand = actor.weapons[slot];
                if (cand == null || cand == active) continue;
                if (cand.IsMeleeWeapon() || cand.IsMountedWeapon() || cand is ThrowableWeapon) continue;
                if (cand.reloading || cand.ammo <= 0 || !cand.HasLoadedAmmo()) continue;
                if (cand.configuration == null || cand.configuration.projectilePrefab == null) continue;
                // Prefer slot 1 sidearm over gear slots
                if (slot == 1 || cand.configuration.ammo <= 24)
                {
                    try { actor.SwitchWeapon(slot); } catch { }
                    return;
                }
            }
        }

        private void TickAimAndShoot(BodyHealthState health, IntPtr agent, IntPtr world, float dt, Transform ragdollRightHand, bool isCrawling, bool isOnGroundClutch)
        {
            var ai = actor.controller as AiActorController;
            Actor target = null;
            Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (health != null && health.LastAttacker != null && !health.LastAttacker.dead && health.LastAttacker.team != actor.team)
            {
                float d2 = (health.LastAttacker.CenterPosition() - actor.CenterPosition()).sqrMagnitude;
                if (d2 < 65f * 65f)
                    target = health.LastAttacker;
            }
            if (target == null && ai != null)
            {
                try
                {
                    if (ai.HasTarget()) target = ai.GetTarget();
                }
                catch { }
            }
            if (target == null && player != null && !player.dead && player.team != actor.team)
            {
                float d2 = (player.CenterPosition() - actor.CenterPosition()).sqrMagnitude;
                if (d2 < 55f * 55f)
                    target = player;
            }

            float weakness = health != null ? health.Weakness01 : 0f;
            Weapon w = actor.activeWeapon;
            bool hasMagAmmo = w != null && !w.reloading && w.ammo > 0 && w.HasLoadedAmmo();
            bool canUseWeapon = hasMagAmmo && ActorRagdollController.IsEligibleBlindFireWeapon(w);

            if (target == null || target.dead || !canUseWeapon)
            {
                if (w != null && !hasMagAmmo)
                {
                    try { w.StopFire(); } catch { }
                }
                isCrawlingBurstPause = false;
                burstShotsLeft = 0;
                CurrentAimTarget = Vector3.zero;
                StopPointArm(agent, world);
                return;
            }

            Vector3 targetPos = target.CenterPosition() + Vector3.up * 0.15f;
            Vector3 myPos = ragdollRightHand != null ? ragdollRightHand.position : actor.CenterPosition();
            float dist = Vector3.Distance(myPos, targetPos);
            bool hasLos = dist < 75f && HasLineOfSight(myPos, targetPos, target);

            CurrentAimTarget = targetPos;

            if (!hasLos)
            {
                isCrawlingBurstPause = false;
                burstShotsLeft = 0;
                CurrentAimTarget = Vector3.zero;
                StopPointArm(agent, world);
                return;
            }

            // Crawling cadence: alternate crawling (~2.0s) and pausing to prop & fire a burst (~1.0s)
            burstStateTimer -= dt;
            if (isCrawling)
            {
                if (burstStateTimer <= 0f)
                {
                    isCrawlingBurstPause = !isCrawlingBurstPause;
                    if (isCrawlingBurstPause)
                    {
                        burstStateTimer = UnityEngine.Random.Range(0.95f, 1.45f);
                        burstShotsLeft = w.configuration.auto ? UnityEngine.Random.Range(3, 7) : UnityEngine.Random.Range(1, 3);
                        nextShotTime = 0.22f; // brief aim settle after propping up
                    }
                    else
                    {
                        burstStateTimer = UnityEngine.Random.Range(1.7f, 2.7f);
                        burstShotsLeft = 0;
                    }
                }
            }
            else
            {
                // Standing wounded or fallen ground aim: fire paced bursts continuously when enemy is visible
                isCrawlingBurstPause = true;
                if (burstShotsLeft <= 0 && burstStateTimer <= 0f)
                {
                    burstShotsLeft = w.configuration.auto ? UnityEngine.Random.Range(2, 6) : UnityEngine.Random.Range(1, 3);
                    burstStateTimer = UnityEngine.Random.Range(1.2f, 2.2f) * Mathf.Lerp(1f, 1.5f, weakness);
                    nextShotTime = isOnGroundClutch ? 0.30f : 0.14f;
                }
            }

            // Drive Delirium pointArm on right arm (layers cleanly after extras.tick!)
            if (isCrawlingBurstPause && agent != IntPtr.Zero && world != IntPtr.Zero)
            {
                pointArmRefreshTimer -= dt;
                if (!isPointingArm || pointArmRefreshTimer <= 0f)
                {
                    bool firstStart = !isPointingArm;
                    pointArmRefreshTimer = 0.14f;
                    isPointingArm = true;
                    float baseStiff = (isOnGroundClutch || isCrawling) ? 8.8f : 7.8f;
                    float stiff = Mathf.Lerp(baseStiff, 6.5f, weakness);
                    nm_msg_begin(agent, "pointArm");
                    if (firstStart)
                        nm_msg_set_bool(agent, "start", 1);
                    nm_msg_set_bool(agent, "useLeftArm", 0);
                    nm_msg_set_vec3(agent, "target", ToNmVec(targetPos));
                    nm_msg_set_float(agent, "armStiffness", stiff);
                    nm_msg_set_float(agent, "armDamping", 1.55f);
                    nm_msg_set_float(agent, "armStraightness", (isOnGroundClutch || isCrawling) ? 0.78f : 0.72f);
                    nm_msg_send(agent, world);
                }
            }
            else
            {
                StopPointArm(agent, world);
            }

            // Trigger physical shots with wounded spread penalty + hand recoil kick
            if (isCrawlingBurstPause && burstShotsLeft > 0)
            {
                nextShotTime -= dt;
                if (nextShotTime <= 0f)
                {
                    Vector3 aimDir = (targetPos - myPos).normalized;
                    float spreadDeg = 4.5f * config.WoundedSpreadMultiplier.Value * Mathf.Lerp(1f, 1.75f, weakness);
                    if (isCrawling || isOnGroundClutch) spreadDeg *= 1.35f;
                    if (health != null && health.Condition == WoundedCondition.HoppingOneLeg) spreadDeg *= 1.25f;

                    Quaternion err = Quaternion.Euler(
                        UnityEngine.Random.Range(-spreadDeg, spreadDeg),
                        UnityEngine.Random.Range(-spreadDeg, spreadDeg),
                        0f);
                    Vector3 fireDir = (err * aimDir).normalized;

                    try
                    {
                        if (w.reloading || w.ammo <= 0 || !w.HasLoadedAmmo())
                        {
                            w.StopFire();
                            burstShotsLeft = 0;
                            isCrawlingBurstPause = false;
                            StopPointArm(agent, world);
                            return;
                        }

                        if (!w.gameObject.activeSelf) w.gameObject.SetActive(true);
                        int ammoBefore = w.ammo;
                        if (w.Fire(fireDir, useMuzzleDirection: false, ignoreCanFireCheck: true))
                        {
                            // Guarantee real magazine consumption (even if a modded weapon ignores ammo under ignoreCanFireCheck)
                            if (w.ammo >= ammoBefore)
                                w.ammo = Mathf.Max(0, ammoBefore - 1);

                            burstShotsLeft--;
                            if (burstShotsLeft <= 0)
                                CompletedBurstCount++;
                            if (agent != IntPtr.Zero)
                            {
                                var hp = new float[3];
                                if (nm_agent_get_part_transform(agent, 20, hp, null) >= 0)
                                {
                                    float kick = w.configuration.auto ? 3.5f : 6.0f;
                                    nm_agent_apply_impulse(agent, 20, hp, ToNmVec(-fireDir * kick));
                                }
                            }

                            if (w.ammo <= 0 || !w.HasLoadedAmmo())
                            {
                                w.StopFire();
                                burstShotsLeft = 0;
                                isCrawlingBurstPause = false;
                                StopPointArm(agent, world);
                                return;
                            }
                        }
                    }
                    catch { }

                    float cd = w.configuration != null ? w.configuration.cooldown : 0.14f;
                    nextShotTime = Mathf.Max(cd, 0.09f) * UnityEngine.Random.Range(1.25f, 1.85f);
                }
            }
        }

        public void StopPointArm(IntPtr agent, IntPtr world)
        {
            if (!isPointingArm) return;
            isPointingArm = false;
            if (agent != IntPtr.Zero && world != IntPtr.Zero)
            {
                nm_msg_begin(agent, "pointArm");
                nm_msg_set_bool(agent, "start", 0);
                nm_msg_send(agent, world);
            }
        }

        private static bool HasLineOfSight(Vector3 from, Vector3 to, Actor target)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;
            // Ignore Hitbox & Ragdoll layers; check world geometry only (layer 0 = Default)
            if (Physics.Raycast(from, dir / dist, out RaycastHit hit, dist - 0.4f, 1 << 0, QueryTriggerInteraction.Ignore))
            {
                if (target != null && hit.transform != null && hit.transform.IsChildOf(target.transform))
                    return true;
                return false;
            }
            return true;
        }
    }
}
