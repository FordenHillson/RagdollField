using System;
using System.Collections.Generic;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.TestArena;
using UnityEngine;

namespace DeliriumRavenfield.Core
{
    public sealed class LodManager : IDisposable
    {
        private readonly DeliriumRuntime runtime;
        private readonly ModConfig config;
        private readonly Dictionary<Actor, ActorRagdollController> controllers = new Dictionary<Actor, ActorRagdollController>();
        private readonly Dictionary<ActiveRaggy, ActorRagdollController> byRaggy = new Dictionary<ActiveRaggy, ActorRagdollController>();
        private readonly Dictionary<Actor, BodyHealthState> healthStates = new Dictionary<Actor, BodyHealthState>();
        private readonly List<ActorRagdollController> activeList = new List<ActorRagdollController>();
        private readonly List<Actor> pruneBuffer = new List<Actor>();
        private readonly List<Actor> bleedKillBuffer = new List<Actor>();

        public static bool IsAllowingBleedoutKill { get; private set; }

        public TestBotMode BotMode { get; set; } = TestBotMode.Normal;
        public bool TestRangeMode
        {
            get => BotMode != TestBotMode.Normal;
            set => BotMode = value ? TestBotMode.PureDummy : TestBotMode.Normal;
        }
        public int ActiveSimulatingCount { get; private set; }
        public int FrozenCorpseCount { get; private set; }
        public int WoundedTrackedCount { get; private set; }
        public IReadOnlyList<ActorRagdollController> ActiveControllers => activeList;

        public LodManager(DeliriumRuntime runtime, ModConfig config)
        {
            this.runtime = runtime;
            this.config = config;
        }

        public bool TryGetController(Actor actor, out ActorRagdollController ctrl)
        {
            if (actor == null)
            {
                ctrl = null;
                return false;
            }
            return controllers.TryGetValue(actor, out ctrl);
        }

        public bool TryGetHealthState(Actor actor, out BodyHealthState state)
        {
            if (actor == null)
            {
                state = null;
                return false;
            }
            return healthStates.TryGetValue(actor, out state);
        }

        public BodyHealthState GetOrCreateHealthState(Actor actor)
        {
            if (actor == null) return null;
            if (!healthStates.TryGetValue(actor, out var state))
            {
                state = new BodyHealthState(actor, config);
                healthStates[actor] = state;
            }
            return state;
        }

        public void ResetHealthState(Actor actor)
        {
            if (actor == null) return;
            if (healthStates.TryGetValue(actor, out var state))
            {
                if (state.AppliedAiIgnoreForSurrender)
                {
                    actor.isIgnored = false;
                }
                actor.movementSpeedMultiplier = 1f;
                state.Reset();
            }
        }

        public bool IsBotDummyStanding(Actor actor)
        {
            if (actor == null || BotMode == TestBotMode.Normal) return false;
            if (ActorManager.instance != null && actor == ActorManager.instance.player) return false;
            if (BotMode == TestBotMode.PureDummy) return true;
            // WoundedAiActive: bot stays completely stationary until wounded, then wounded AI activates
            return !(healthStates.TryGetValue(actor, out var hs) && hs.IsWounded);
        }

        public bool IsSuppressingAiBrain(Actor actor)
        {
            if (actor == null) return false;
            return IsBotDummyStanding(actor);
        }

        public bool IsSuppressingWoundedCombat(Actor actor)
        {
            if (actor == null) return false;
            if (ActorManager.instance != null && actor == ActorManager.instance.player) return false;
            return BotMode == TestBotMode.PureDummy;
        }

        public bool IsSuppressingMovement(Actor actor)
        {
            if (actor == null) return false;
            if (IsBotDummyStanding(actor))
                return true;
            return controllers.TryGetValue(actor, out var ctrl) && ctrl.SuppressMovement;
        }

        public bool IsSuppressingWeaponFire(Actor actor)
        {
            if (actor == null) return false;
            if (IsBotDummyStanding(actor))
                return true;
            if (healthStates.TryGetValue(actor, out var hs) && hs.RightArmRuined)
                return true;
            return controllers.TryGetValue(actor, out var ctrl) && ctrl.SuppressWeaponFire;
        }

        public bool IsSuppressingReload(Actor actor)
        {
            if (actor == null) return false;
            if (ActorManager.instance != null && actor == ActorManager.instance.player) return false;
            if (healthStates.TryGetValue(actor, out var hs) && hs.IsWounded)
                return true;
            return controllers.TryGetValue(actor, out var ctrl) && ctrl.IsSimulating;
        }

        public bool IsSuppressingControl(Actor actor) => IsSuppressingMovement(actor);

        public bool IsSuppressingVanillaGetUp(Actor actor)
        {
            if (actor == null) return false;
            if (healthStates.TryGetValue(actor, out var hs) &&
                (hs.IsFallenWithOneDeadLeg || hs.BothLegsRuined || hs.StomachRuined || hs.RightArmRuined))
                return true;
            return controllers.TryGetValue(actor, out var ctrl) && ctrl.SuppressVanillaGetUp;
        }

        public bool IsOwnedByDelirium(ActiveRaggy raggy)
        {
            if (raggy == null) return false;
            return byRaggy.TryGetValue(raggy, out var ctrl) &&
                   (ctrl.IsSimulating || ctrl.Phase == RagdollPhase.DeadFrozen || ctrl.Phase == RagdollPhase.BlendingToStand);
        }

        public bool IsSimulatingRaggy(ActiveRaggy raggy)
        {
            if (raggy == null) return false;
            return byRaggy.TryGetValue(raggy, out var ctrl) && ctrl.IsSimulating;
        }

        private ActorRagdollController RegisterController(Actor actor)
        {
            var ctrl = new ActorRagdollController(actor, runtime, config);
            if (healthStates.TryGetValue(actor, out var hs))
                ctrl.HealthState = hs;
            controllers[actor] = ctrl;
            if (actor.ragdoll != null) byRaggy[actor.ragdoll] = ctrl;
            return ctrl;
        }

        private void UnregisterActor(Actor actor)
        {
            if (actor != null && actor.ragdoll != null)
                byRaggy.Remove(actor.ragdoll);
            if (actor != null)
                controllers.Remove(actor);
        }

        public bool IsWithinLodRange(Actor actor)
        {
            if (actor == null || !config.Enabled.Value || !runtime.IsReady) return false;
            if (ActorManager.instance != null && actor == ActorManager.instance.player) return true;

            var cam = Camera.main;
            if (cam == null) return true;
            float maxDist = config.ActiveDistance.Value;
            return (actor.CenterPosition() - cam.transform.position).sqrMagnitude <= maxDist * maxDist;
        }

        public ActorRagdollController GetOrCreateControllerForSync(Actor actor)
        {
            if (actor == null || !runtime.IsReady) return null;
            if (controllers.TryGetValue(actor, out var existing))
                return existing;
            return RegisterController(actor);
        }

        public ActorRagdollController TryAcquireForLiveReaction(Actor actor)
        {
            if (actor == null || !config.Enabled.Value || !runtime.IsReady) return null;
            if (actor.IsSeated() || actor.IsOnLadder() || actor.parachuteDeployed) return null;

            bool isPlayer = ActorManager.instance != null && actor == ActorManager.instance.player;
            if (isPlayer && !actor.fallenOver && !actor.dead) return null;

            if (!IsWithinLodRange(actor)) return null;

            if (controllers.TryGetValue(actor, out var existing))
            {
                if (existing.IsSimulating) return existing;
                if (!EnsureSlotAvailable(actor, highPriority: !actor.dead || isPlayer)) return null;
                return existing;
            }

            if (!EnsureSlotAvailable(actor, highPriority: !actor.dead || isPlayer))
                return null;

            return RegisterController(actor);
        }

        public ActorRagdollController TryAcquireForWounded(Actor actor)
        {
            var ctrl = TryAcquireForLiveReaction(actor);
            if (ctrl != null && healthStates.TryGetValue(actor, out var hs))
                ctrl.HealthState = hs;
            return ctrl;
        }

        public ActorRagdollController TryAcquireForFallOrDeath(Actor actor)
        {
            if (actor == null || !config.Enabled.Value || !runtime.IsReady) return null;
            if (!IsWithinLodRange(actor)) return null;

            bool isPlayer = ActorManager.instance != null && actor == ActorManager.instance.player;
            if (controllers.TryGetValue(actor, out var existing))
            {
                if (existing.IsSimulating) return existing;
                if (!EnsureSlotAvailable(actor, highPriority: isPlayer || !actor.dead)) return null;
                return existing;
            }

            if (!EnsureSlotAvailable(actor, highPriority: isPlayer || !actor.dead))
                return null;

            return RegisterController(actor);
        }

        public ActorRagdollController TryAcquireOrWakeCorpse(Actor actor)
        {
            if (actor == null || !config.Enabled.Value || !runtime.IsReady) return null;
            if (!IsWithinLodRange(actor)) return null;

            if (controllers.TryGetValue(actor, out var existing))
            {
                if (existing.IsSimulating) return existing;
                if (!EnsureSlotAvailable(actor, highPriority: true)) return null;
                return existing;
            }

            if (!EnsureSlotAvailable(actor, highPriority: false))
                return null;

            return RegisterController(actor);
        }

        public void ReleaseActor(Actor actor, bool restoreGame)
        {
            if (actor == null) return;
            if (controllers.TryGetValue(actor, out var ctrl))
            {
                ctrl.ReleaseImmediate(restoreGame);
                UnregisterActor(actor);
            }
            if (!restoreGame)
            {
                ResetHealthState(actor);
            }
        }

        public void ReleaseAll(bool restoreGame)
        {
            foreach (var ctrl in controllers.Values)
            {
                ctrl.ReleaseImmediate(restoreGame);
            }
            controllers.Clear();
            byRaggy.Clear();
            activeList.Clear();

            if (!restoreGame)
            {
                foreach (var kv in healthStates)
                {
                    if (kv.Key != null)
                    {
                        if (kv.Value.AppliedAiIgnoreForSurrender)
                            kv.Key.isIgnored = false;
                        kv.Key.movementSpeedMultiplier = 1f;
                    }
                }
                healthStates.Clear();
            }

            ActiveSimulatingCount = 0;
            FrozenCorpseCount = 0;
            WoundedTrackedCount = 0;
        }

        public void TickUpdate(float dt)
        {
            if (!runtime.IsReady) return;

            // 1. Map-wide lightweight bleedout & LOD promotion for all wounded AI bots
            TickMapWideHealthStates(dt);

            // 2. Collect simulating Delirium controllers
            pruneBuffer.Clear();
            activeList.Clear();
            int simCount = 0;
            int frozenCount = 0;

            foreach (var kv in controllers)
            {
                var actor = kv.Key;
                var ctrl = kv.Value;
                if (actor == null || !actor.gameObject.activeInHierarchy)
                {
                    ctrl.ReleaseImmediate(restoreGame: false);
                    pruneBuffer.Add(actor);
                    continue;
                }

                if (ctrl.IsSimulating)
                {
                    activeList.Add(ctrl);
                    simCount++;
                }
                else if (ctrl.Phase == RagdollPhase.DeadFrozen)
                {
                    frozenCount++;
                }
            }

            for (int i = 0; i < pruneBuffer.Count; i++)
                UnregisterActor(pruneBuffer[i]);

            // 3. Feed live animated poses for partial-ragdoll WoundedStanding bots, then step Delirium at 60 Hz
            if (activeList.Count > 0)
            {
                for (int i = 0; i < activeList.Count; i++)
                {
                    activeList[i].FeedIncomingAnimatedPose(dt);
                }
                runtime.StepSimulation(activeList, dt);
            }

            // 4. Update per-actor state machines (wounded combat, crawl/hop/clutch/surrender, recovery, corpse freeze)
            pruneBuffer.Clear();
            foreach (var kv in controllers)
            {
                var ctrl = kv.Value;
                ctrl.UpdateLogic(dt);
                if (ctrl.Phase == RagdollPhase.Animated)
                {
                    pruneBuffer.Add(kv.Key);
                }
            }
            for (int i = 0; i < pruneBuffer.Count; i++)
                UnregisterActor(pruneBuffer[i]);

            ActiveSimulatingCount = simCount;
            FrozenCorpseCount = frozenCount;
        }

        private void TickMapWideHealthStates(float dt)
        {
            if (!config.Enabled.Value || !config.EnableLocationalHealth.Value)
            {
                WoundedTrackedCount = 0;
                return;
            }

            pruneBuffer.Clear();
            bleedKillBuffer.Clear();
            int woundedCount = 0;

            foreach (var kv in healthStates)
            {
                var actor = kv.Key;
                var hs = kv.Value;
                if (actor == null || !actor.gameObject.activeInHierarchy)
                {
                    pruneBuffer.Add(actor);
                    continue;
                }

                if (actor.dead) continue;
                if (controllers.TryGetValue(actor, out var syncCheck) && syncCheck.IsSyncLockstepControlled)
                    continue;
                if (!hs.IsWounded) continue;
                woundedCount++;

                // Bleedout tick for every wounded bot map-wide
                bool bledOut = hs.TickBleed(dt);
                if (bledOut)
                {
                    bleedKillBuffer.Add(actor);
                    continue;
                }

                // Keep vanilla health mirroring blood level (clamped > 5 so vanilla never steals bleedout death)
                float mirroredHp = Mathf.Max(5f, (hs.Blood / 100f) * actor.maxHealth);
                if (actor.health > mirroredHp + 1f)
                    actor.health = mirroredHp;

                // Promote distant wounded bot into full Delirium simulation when it enters LOD range
                bool isSimulating = controllers.TryGetValue(actor, out var existingCtrl) && existingCtrl.IsSimulating;
                if (!isSimulating && !actor.IsSeated() && !actor.IsOnLadder() && IsWithinLodRange(actor))
                {
                    var ctrl = TryAcquireForWounded(actor);
                    ctrl?.SyncFromHealthState(hs);
                }
            }

            for (int i = 0; i < pruneBuffer.Count; i++)
                healthStates.Remove(pruneBuffer[i]);

            for (int i = 0; i < bleedKillBuffer.Count; i++)
            {
                var actor = bleedKillBuffer[i];
                if (actor != null && !actor.dead && healthStates.TryGetValue(actor, out var hs))
                {
                    KillActorFromBleedout(actor, hs);
                }
            }

            WoundedTrackedCount = woundedCount;
        }

        public void ForceKillForTest(Actor actor, DamageInfo info)
        {
            if (actor == null || actor.dead) return;
            IsAllowingBleedoutKill = true;
            try
            {
                actor.Damage(info);
            }
            catch { }
            finally
            {
                IsAllowingBleedoutKill = false;
            }
        }

        public void KillActorFromBleedout(Actor actor, BodyHealthState hs)
        {
            if (actor == null || actor.dead) return;

            if (hs != null && hs.AppliedAiIgnoreForSurrender)
            {
                actor.isIgnored = false;
                hs.AppliedAiIgnoreForSurrender = false;
            }
            actor.movementSpeedMultiplier = 1f;

            // Ensure bleedout kill preserves kill credit to the shooter who inflicted the bleeding wound!
            DamageInfo bleedInfo = new DamageInfo
            {
                type = DamageInfo.DamageSourceType.Projectile,
                healthDamage = actor.maxHealth + 100f,
                balanceDamage = 100f,
                isCriticalHit = false,
                point = actor.CenterPosition(),
                direction = Vector3.down,
                impactForce = Vector3.zero,
                sourceActor = hs?.LastAttacker
            };

            IsAllowingBleedoutKill = true;
            try
            {
                actor.Damage(bleedInfo);
            }
            catch { }
            finally
            {
                IsAllowingBleedoutKill = false;
            }

            if (controllers.TryGetValue(actor, out var ctrl) && ctrl.IsSimulating)
            {
                ctrl.ApplyBleedoutDeath(hs);
            }
        }

        public void TickFixedUpdate()
        {
            if (!runtime.IsReady) return;
            for (int i = 0; i < activeList.Count; i++)
            {
                var ctrl = activeList[i];
                if (ctrl != null && ctrl.IsSimulating)
                    ctrl.SyncEnginePhysicsFixedUpdate();
            }
        }

        public void TickLateUpdate()
        {
            if (!runtime.IsReady) return;
            bool anyPosed = false;
            foreach (var ctrl in controllers.Values)
            {
                ctrl.ApplyPoseLateUpdate();
                if (ctrl.IsSimulating) anyPosed = true;
            }
            if (anyPosed)
                Physics.SyncTransforms();
        }

        private bool EnsureSlotAvailable(Actor requester, bool highPriority)
        {
            int cap = config.MaxConcurrentAgents.Value;
            int current = 0;
            ActorRagdollController victim = null;
            float worstScore = float.MinValue;

            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            float reqDist = cam != null ? Vector3.Distance(requester.CenterPosition(), camPos) : 0f;

            foreach (var ctrl in controllers.Values)
            {
                if (!ctrl.IsSimulating) continue;
                current++;

                if (ctrl.IsPlayer) continue;

                float dist = cam != null && ctrl.Actor != null ? Vector3.Distance(ctrl.Actor.CenterPosition(), camPos) : 0f;
                // Eviction priority: DeadSimulating (highest) > DyingStagger > ActiveStagger/Fallen > Live Wounded/Surrendering (lowest)
                float phaseBias;
                switch (ctrl.Phase)
                {
                    case RagdollPhase.DeadSimulating: phaseBias = 1200f; break;
                    case RagdollPhase.DyingStagger: phaseBias = 400f; break;
                    case RagdollPhase.ActiveStagger:
                    case RagdollPhase.ActiveFallen: phaseBias = 120f; break;
                    default: phaseBias = 0f; break; // WoundedStanding / WoundedCrawling / Surrendering protected!
                }
                float score = dist + phaseBias;
                if (score > worstScore)
                {
                    worstScore = score;
                    victim = ctrl;
                }
            }

            if (current < cap) return true;
            if (victim == null) return false;

            bool canSteal = victim.Phase == RagdollPhase.DeadSimulating ||
                            (highPriority && victim.Phase != RagdollPhase.WoundedStanding &&
                             victim.Phase != RagdollPhase.WoundedCrawling &&
                             victim.Phase != RagdollPhase.Surrendering &&
                             cam != null && Vector3.Distance(victim.Actor.CenterPosition(), camPos) > reqDist + 10f);

            if (!canSteal) return false;

            if (victim.Phase == RagdollPhase.DeadSimulating)
                victim.FreezeDeadCorpse();
            else
                victim.ReleaseImmediate(restoreGame: true);

            return true;
        }

        public void Dispose()
        {
            ReleaseAll(restoreGame: false);
        }
    }
}
