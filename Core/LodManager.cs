using System;
using System.Collections.Generic;
using DeliriumRavenfield.Config;
using UnityEngine;

namespace DeliriumRavenfield.Core
{
    public sealed class LodManager : IDisposable
    {
        private readonly DeliriumRuntime runtime;
        private readonly ModConfig config;
        private readonly Dictionary<Actor, ActorRagdollController> controllers = new Dictionary<Actor, ActorRagdollController>();
        private readonly Dictionary<ActiveRaggy, ActorRagdollController> byRaggy = new Dictionary<ActiveRaggy, ActorRagdollController>();
        private readonly List<ActorRagdollController> activeList = new List<ActorRagdollController>();
        private readonly List<Actor> pruneBuffer = new List<Actor>();

        public bool TestRangeMode { get; set; }
        public int ActiveSimulatingCount { get; private set; }
        public int FrozenCorpseCount { get; private set; }
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

        public bool IsSuppressingControl(Actor actor)
        {
            if (actor == null) return false;
            if (TestRangeMode && ActorManager.instance != null && actor != ActorManager.instance.player)
                return true;
            return controllers.TryGetValue(actor, out var ctrl) && ctrl.IsControllingActor;
        }

        public bool IsSuppressingVanillaGetUp(Actor actor)
        {
            if (actor == null) return false;
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

        public ActorRagdollController TryAcquireForLiveReaction(Actor actor)
        {
            if (actor == null || !config.Enabled.Value || !runtime.IsReady) return null;
            if (actor.IsSeated() || actor.IsOnLadder() || actor.parachuteDeployed) return null;

            // Player only uses Delirium when fallen or dead (preserve clean FPS aiming)
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

        public void ReleaseActor(Actor actor, bool restoreGame)
        {
            if (actor == null) return;
            if (controllers.TryGetValue(actor, out var ctrl))
            {
                ctrl.ReleaseImmediate(restoreGame);
                UnregisterActor(actor);
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
            ActiveSimulatingCount = 0;
            FrozenCorpseCount = 0;
        }

        public void TickUpdate(float dt)
        {
            if (!runtime.IsReady) return;

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

            // Step Delirium physics at fixed 60 Hz across all active worlds
            if (activeList.Count > 0)
            {
                runtime.StepSimulation(activeList, dt);
            }

            // Update per-actor state machines (recovery, get-up handoff, corpse freeze, water)
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

        public void TickLateUpdate()
        {
            if (!runtime.IsReady) return;
            foreach (var ctrl in controllers.Values)
            {
                ctrl.ApplyPoseLateUpdate();
            }
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

                // Never evict the player
                if (ctrl.IsPlayer) continue;

                float dist = cam != null && ctrl.Actor != null ? Vector3.Distance(ctrl.Actor.CenterPosition(), camPos) : 0f;
                // Prefer evicting dead corpses far from camera
                float score = dist + (ctrl.Phase == RagdollPhase.DeadSimulating ? 1000f : 0f);
                if (score > worstScore)
                {
                    worstScore = score;
                    victim = ctrl;
                }
            }

            if (current < cap) return true;
            if (victim == null) return false;

            // Allow stealing a slot from a dead corpse, or from a much farther bot if requester is high priority
            bool canSteal = victim.Phase == RagdollPhase.DeadSimulating ||
                            (highPriority && cam != null && Vector3.Distance(victim.Actor.CenterPosition(), camPos) > reqDist + 10f);

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
