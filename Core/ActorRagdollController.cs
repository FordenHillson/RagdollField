using System;
using System.Collections.Generic;
using DeliriumRavenfield.Config;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    public enum RagdollPhase
    {
        Animated,
        ActiveStagger,
        ActiveFallen,
        BlendingToStand,
        WoundedStanding,
        WoundedCrawling,
        Surrendering,
        DyingStagger,
        DeadSimulating,
        DeadFrozen
    }

    public sealed class ActorRagdollController : IDisposable
    {
        public Actor Actor { get; }
        public RagdollPhase Phase { get; private set; } = RagdollPhase.Animated;
        public BodyHealthState HealthState { get; set; }
        public SharedWorldEntry WorldEntry { get; private set; }
        public IntPtr Agent { get; private set; }
        public IntPtr BuildPtr => build;
        public RigBuildResult LastRigSnapshot { get; private set; }
        public int BracingVehicleIndex { get; set; } = -1;
        public float BraceTimeLeft { get; set; }
        public string LastRigReport { get; private set; } = "";
        public float DyingStaggerElapsed => dyingStaggerElapsed;
        public int DyingWoundPart => dyingWoundPart;
        public int DyingFollowUpHits => dyingFollowUpHits;
        public bool EnginePhysicsActive => enginePhysicsProxy.IsActive;
        public bool IsSyncLockstepControlled { get; set; }
        public Vector3 SyncVisualOffset { get; private set; }
        private bool buildOwnedByController = true;
        private bool worldOwnedByController = true;

        public bool IsSimulating =>
            Agent != IntPtr.Zero &&
            (Phase == RagdollPhase.ActiveStagger ||
             Phase == RagdollPhase.ActiveFallen ||
             Phase == RagdollPhase.WoundedStanding ||
             Phase == RagdollPhase.WoundedCrawling ||
             Phase == RagdollPhase.Surrendering ||
             Phase == RagdollPhase.DyingStagger ||
             Phase == RagdollPhase.DeadSimulating);

        public bool SuppressMovement =>
            Phase == RagdollPhase.ActiveStagger ||
            Phase == RagdollPhase.ActiveFallen ||
            Phase == RagdollPhase.BlendingToStand ||
            Phase == RagdollPhase.WoundedCrawling ||
            Phase == RagdollPhase.Surrendering;

        public bool SuppressWeaponFire =>
            Phase == RagdollPhase.ActiveStagger ||
            Phase == RagdollPhase.ActiveFallen ||
            Phase == RagdollPhase.BlendingToStand ||
            Phase == RagdollPhase.WoundedStanding ||
            Phase == RagdollPhase.WoundedCrawling ||
            Phase == RagdollPhase.Surrendering;

        public bool IsControllingActor => SuppressMovement;

        public bool IsDeadOrFrozen =>
            Phase == RagdollPhase.DyingStagger ||
            Phase == RagdollPhase.DeadSimulating ||
            Phase == RagdollPhase.DeadFrozen;

        public bool SuppressVanillaGetUp =>
            Phase == RagdollPhase.ActiveStagger ||
            Phase == RagdollPhase.ActiveFallen ||
            Phase == RagdollPhase.WoundedStanding ||
            Phase == RagdollPhase.WoundedCrawling ||
            Phase == RagdollPhase.Surrendering;

        public bool IsPlayer => Actor != null && ActorManager.instance != null && Actor == ActorManager.instance.player;

        private readonly DeliriumRuntime runtime;
        private readonly ModConfig config;
        private static readonly System.Reflection.MethodInfo ActorGetUpMethod =
            HarmonyLib.AccessTools.Method(typeof(Actor), "GetUp");
        private static readonly System.Reflection.FieldInfo ForceAnimatorNeverCullField =
            HarmonyLib.AccessTools.Field(typeof(Actor), "forceAnimatorNeverCull");
        private static readonly int AnimParRagdolled = Animator.StringToHash("ragdolled");
        private static readonly int AnimParOnBack = Animator.StringToHash("onBack");
        private const int RagdollAnimLayer = 2;

        private IntPtr build;
        private IntPtr rig;
        private float scale = 1f;
        private Transform hips;
        private Transform[] bones;
        private float[] poseBuf;
        private float[] frameBuf;
        private readonly List<Rigidbody> kinematicBodies = new List<Rigidbody>();
        private readonly EnginePhysicsProxy enginePhysicsProxy = new EnginePhysicsProxy();

        // State timers
        private float staggerElapsed;
        private float pendingWoundedTransitionTimer;
        private bool aliveLeanArmed;
        private Vector3 aliveStaggerDir = Vector3.forward;
        private float aliveLeanAmount = 0.55f;
        private float settledElapsed;
        private float fallenElapsed;
        private float fallenStillElapsed;
        private Vector3 fallenAnchorPos;
        private float fallenAnchorElapsed;
        private bool fallenTaskStopped;
        private float deadStillElapsed;
        private float deadElapsed;
        private bool deadRelaxed;
        private Vector3 deadAnchorPos;
        private float deadAnchorElapsed;
        private float dyingStaggerElapsed;
        private float dyingStaggerDuration;
        private bool dyingRelaxTriggered;
        private bool dyingLeanArmed;
        private Vector3 dyingStaggerDir = Vector3.forward;
        private float dyingLeanAmount = 0.75f;
        private int dyingFollowUpHits;
        private int dyingWoundPart = -1;
        private Vector3 dyingWoundLocalPos;
        private Vector3 dyingWoundLocalNormal = Vector3.forward;
        private float dyingBleedTimer;
        private float dyingBleedNextPulse;
        private float dyingBleedEndTime;
        private int dyingBleedPulseCount;
        private bool dyingCollapseActive;
        private float worldReanchorCooldown;
        private bool deathAgonyScheduled;
        private bool deathAgonyActive;
        private bool deathAgonyDone;
        private int deathAgonyStyle;
        private float deathAgonyDuration;
        private float deathAgonyElapsed;
        private float deathAgonyNextUpdate;
        private float deathAgonyAsymmetry;
        private float lastNearMissTime = -10f;
        private float burstImpulseUsed;
        private float lastShotTaskTime = -10f;
        private float lastShotHitTime = -10f;
        private int consecutiveRapidHits;
        private int consecutiveAliveHits;
        private int lastExplosionFrame = -1;
        private bool wasFallenBeforeTakeover;
        private Vector3 lastComPos;
        private Vector3 lastComVel;
        private readonly float[] partMasses = new float[21];

        // Keep bot's active weapon glued to the ragdoll hand during ActiveStagger / Wounded / BlindFire (prevents floating gun!)
        public static bool IsAllowingDeferredDrop { get; private set; }
        public static void SetAllowDeferredDrop(bool allow) => IsAllowingDeferredDrop = allow;
        private readonly WoundedCombatController woundedCombat;
        private string activeWoundedBehavior;
        private float lastAppliedMuscleStrength = -1f;
        private Vector3 surrenderAnchorPos;
        private float woundedCrawlSyncTimer;
        private readonly byte[] feedbackBuf = new byte[64];
        private Transform[] animatedBones;
        private float[] incomingBuf;
        private bool partialKeyframeActive;
        private string partialKeyframeMask;
        private float hopBouncePhase;
        private Transform trackedWeaponParent;
        private Transform weaponRagdollHand;
        private Vector3 weaponParentSavedLocalPos;
        private Quaternion weaponParentSavedLocalRot;
        private Vector3 weaponHandOffsetPos;
        private Quaternion weaponHandOffsetRot;
        private bool weaponAttachedToRagdoll;
        private bool weaponHiddenByFallback;

        // Startled reflex accidental discharge ("ปืนลั่น") during stagger / sudden fall / fatal hit
        private Weapon blindFireWeapon;
        private int blindFireDeferredSlot = -1;
        private bool blindFirePendingStart;
        private bool blindFireActive;
        private float blindFireElapsed;
        private float blindFireDuration;
        private float blindFireNextShotTime;
        private int blindFireShotsFired;
        private int blindFireMaxShots;
        private Quaternion blindFireTwitchRot = Quaternion.identity;

        // Smooth blend from gun-holding/aiming animation INTO Delirium stagger (eliminates 1-frame snap)
        private Quaternion entryStartRootRot;
        private Quaternion[] entryStartLocalRot;
        private bool[] boneIsUpperLimb;
        private float entryBlendTimer;
        private float entryBlendDuration;
        private float armFollowTimer;
        private float armFollowDuration;
        private bool hasIncomingArmFollow;

        // Smooth blend back to standing animation (for alive bots that recovered without falling)
        private Transform[] animBlendBones;
        private Quaternion[] animBlendStartRot;
        private Vector3 animBlendStartHipPos;
        private Quaternion animBlendStartHipRot;
        private float blendTimer;
        private float blendDuration;

        public ActorRagdollController(Actor actor, DeliriumRuntime runtime, ModConfig config)
        {
            Actor = actor;
            this.runtime = runtime;
            this.config = config;
            woundedCombat = new WoundedCombatController(actor, config);
        }

        public bool EnsureTakenOver(bool startBalanced, bool alreadyFallen)
        {
            if (IsSimulating) return true;
            if (Actor == null || Actor.ragdoll == null || Actor.ragdoll.ragdollObject == null || !runtime.IsReady)
                return false;

            if (Phase == RagdollPhase.BlendingToStand)
            {
                animBlendBones = null;
            }
            if (Phase == RagdollPhase.DeadFrozen)
            {
                // Re-animating a previously frozen corpse is rare; destroy old state cleanly first
                ReleaseNativeOnly();
            }

            try
            {
                // 1. Make sure ActiveRaggy is showing ragdollObject with transforms copied from animatedObject
                wasFallenBeforeTakeover = alreadyFallen || Actor.fallenOver || Actor.ragdoll.IsRagdoll();
                if (!Actor.ragdoll.IsRagdoll())
                {
                    Actor.ragdoll.Ragdoll(Actor.Velocity());
                }

                // 2. Freeze Unity PhysX rigidbodies on ragdollObject so ConfigurableJoints never fight Delirium
                FreezeRagdollRigidbodies();
                EnsureRagdollCollidersEnabled();

                // Sample ground height ONCE and use the exact same groundY for rig build, world, and agent creation
                Physics.SyncTransforms();
                Vector3 approxHipPos = Actor.hipRigidbody != null ? Actor.hipRigidbody.transform.position : Actor.CenterPosition();
                float groundY = runtime.SampleGroundHeight(approxHipPos, Actor.transform, Actor.transform.position.y);

                // 3. Map Ravenfield ragdoll skeleton & build fitted Delirium body
                var rigBuild = runtime.SkeletonMapper.BuildRigForActor(Actor, runtime.BaseBuild, groundY);
                if (rigBuild == null)
                {
                    RestoreRagdollRigidbodies();
                    return false;
                }

                hips = rigBuild.Hips;
                bones = rigBuild.Bones;
                build = rigBuild.Build;
                scale = rigBuild.Scale;
                LastRigReport = rigBuild.CheckReport ?? "";
                LastRigSnapshot = rigBuild;
                CachePartMasses();

                // 4. Acquire spatial shared Delirium World (static BVH + floor)
                int sceneId = Actor.gameObject.scene.GetHashCode();
                WorldEntry = runtime.AcquireWorld(hips.position, groundY, Actor.transform, scale, sceneId);

                // 5. Create Delirium Agent and Rig
                float[] spawnPos = ToNmVec(new Vector3(hips.position.x, groundY, hips.position.z));
                Agent = nm_agent_create(build, spawnPos, 0f, UnityEngine.Random.value);
                if (Agent == IntPtr.Zero)
                    throw new Exception($"nm_agent_create failed: {LastError()}");

                ApplyAgentConfig();

                rig = nm_rig_create(Agent, bones.Length, rigBuild.Parents, rigBuild.RestFrames, rigBuild.Parts);
                if (rig == IntPtr.Zero)
                    throw new Exception($"nm_rig_create failed: {LastError()}");

                poseBuf = new float[bones.Length * 4 + 3];
                frameBuf = new float[bones.Length * 7];
                incomingBuf = new float[bones.Length * 7];
                animatedBones = new Transform[bones.Length];
                for (int bi = 0; bi < bones.Length; bi++)
                {
                    if (bones[bi] == null) continue;
                    try { animatedBones[bi] = Actor.ragdoll.RagdollToAnimated(bones[bi]); } catch { }
                }

                // 6. Seamless takeover from current Unity pose & velocity (zero snap!)
                CaptureCurrentFrames(frameBuf);
                Check(nm_rig_take(rig, Agent, frameBuf, null, 0f), "nm_rig_take");

                // 6b. Build Delirium EnginePhysics kinematic colliders for Unity prop/trigger interaction
                if (config.EnableEnginePhysicsColliders.Value)
                {
                    enginePhysicsProxy.Build(Actor, Agent, build, scale);
                }

                // Snapshot current animated gun-holding pose for smooth entry blend
                // (full smooth blend + upper-arm follow on alive stagger; brief 0.06s anti-pop blend on death)
                CaptureEntryPoseSnapshot(rigBuild.Parts,
                    isAliveStagger: startBalanced && !Actor.dead && !wasFallenBeforeTakeover,
                    isDeathTakeover: Actor.dead);

                Vector3 vel = Actor.Velocity();
                if (vel.sqrMagnitude > 0.01f)
                {
                    if (vel.magnitude > 8f) vel = vel.normalized * 8f;
                    nm_agent_add_velocity(Agent, ToNmVec(vel));
                }

                if (startBalanced)
                {
                    SendSimpleMessage("bodyBalance");
                }

                staggerElapsed = 0f;
                settledElapsed = 0f;
                fallenElapsed = 0f;
                fallenStillElapsed = 0f;
                fallenAnchorPos = hips != null ? hips.position : Actor.transform.position;
                fallenAnchorElapsed = 0f;
                fallenTaskStopped = false;
                deadStillElapsed = 0f;
                deadElapsed = 0f;
                deadRelaxed = false;
                deadAnchorElapsed = 0f;
                worldReanchorCooldown = 0.5f;
                ResetDyingStaggerState();
                ResetDeathAgonyState();
                burstImpulseUsed = 0f;
                lastShotTaskTime = -10f;
                lastShotHitTime = -10f;
                consecutiveRapidHits = 0;
                consecutiveAliveHits = 0;
                BracingVehicleIndex = -1;

                if (Actor.dead)
                {
                    if (startBalanced && !wasFallenBeforeTakeover)
                    {
                        // Standing fatal hit that may enter DyingStagger: keep muscles & env reactions alive!
                        nm_agent_set_dead(Agent, 0);
                        Phase = RagdollPhase.DyingStagger;
                    }
                    else
                    {
                        nm_agent_set_dead(Agent, 1);
                        nm_agent_set_reactions(Agent, 0);
                        Phase = RagdollPhase.DeadSimulating;
                    }
                }
                else if (wasFallenBeforeTakeover)
                {
                    Phase = RagdollPhase.ActiveFallen;
                }
                else
                {
                    Phase = RagdollPhase.ActiveStagger;
                    CaptureWeaponForRagdollHand();
                }

                if (config.DebugLog.Value)
                    Plugin.Log.LogInfo($"[Delirium] Takeover {Actor.name} -> {Phase} (skin={rigBuild.SkinKey}, bones={bones.Length}, scale={scale:F2})");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Delirium] Failed takeover for {Actor?.name}: {ex.Message}");
                ReleaseNativeOnly();
                RestoreRagdollRigidbodies();
                Phase = RagdollPhase.Animated;
                return false;
            }
        }

        /// <summary>
        /// Deterministic Playroom Lockstep Takeover: binds this actor to the dedicated Playroom mirror NmWorld
        /// at NM origin (z = 0) with visual shift +visualOffset (MirrorOrigin), supporting Exact Homer Parity or
        /// fitted Ravenfield Soldier mode with zero auto-getup / zero extra Ravenfield tasks.
        /// </summary>
        public bool EnsureTakenOverForPlayroomSync(
            SharedWorldEntry mirrorWorld,
            Vector3 visualOffset,
            Sync.SyncParityMode parityMode,
            Vector3 startNm,
            float yaw,
            float seed,
            float strength,
            int reactions,
            float[] initialFrames,
            int initialParts)
        {
            if (Actor == null || Actor.ragdoll == null || Actor.ragdoll.ragdollObject == null || !runtime.IsReady || mirrorWorld == null)
                return false;

            ReleaseNativeOnly();
            animBlendBones = null;

            try
            {
                // Compute EXACT facing mathematically from Playroom's live Thigh_Left (part 1) & Thigh_Right (part 4)
                // so the Soldier's bind pose ALWAYS matches Playroom's exact heading (even after reset_here/rotation!).
                Vector3 syncFacing = Quaternion.Euler(0f, -yaw * Mathf.Rad2Deg, 0f) * TestArena.PlayroomMirrorDefinitions.BotSpawnDir;
                if (initialFrames != null && initialParts >= 7 && initialFrames.Length >= initialParts * 7)
                {
                    Vector3 pL = new Vector3(initialFrames[1 * 7 + 0], initialFrames[1 * 7 + 2], initialFrames[1 * 7 + 1]);
                    Vector3 pR = new Vector3(initialFrames[4 * 7 + 0], initialFrames[4 * 7 + 2], initialFrames[4 * 7 + 1]);
                    Vector3 right = Vector3.ProjectOnPlane(pR - pL, Vector3.up);
                    if (right.sqrMagnitude > 1e-4f)
                    {
                        Vector3 f = Vector3.Cross(right.normalized, Vector3.up);
                        if (f.sqrMagnitude > 1e-4f) syncFacing = f.normalized;
                    }
                }

                // Snap Actor transform cleanly to Playroom's spawn spot & heading BEFORE ragdolling so hitboxes align!
                Vector3 actorSpawnUnity = visualOffset + new Vector3(startNm.x, startNm.z, startNm.y);
                try
                {
                    Actor.SetPositionAndRotation(actorSpawnUnity, Quaternion.LookRotation(syncFacing, Vector3.up));
                }
                catch { }

                if (!Actor.ragdoll.IsRagdoll())
                    Actor.ragdoll.Ragdoll(Vector3.zero);

                // NEVER deactivate Actor.ragdoll.animatedObject (Ravenfield's ActiveRaggy.HumanBoneTransformRagdoll
                // queries animatedObject's Animator.GetBoneTransform every frame!). Only ensure renderer visibility.
                if (Actor.skinnedRenderer != null)
                    Actor.skinnedRenderer.enabled = false;
                if (Actor.skinnedRendererRagdoll != null)
                    Actor.skinnedRendererRagdoll.enabled = true;
                ClearPartialKeyframe();

                FreezeRagdollRigidbodies();
                EnsureRagdollCollidersEnabled();

                // Build Soldier bone hierarchy & upright bind-pose rest frames at groundY=0 facing exact Playroom heading
                var rigBuild = runtime.SkeletonMapper.BuildRigForActor(Actor, runtime.BaseBuild, 0f, syncFacing);
                if (rigBuild == null)
                {
                    RestoreRagdollRigidbodies();
                    return false;
                }

                hips = rigBuild.Hips;
                bones = rigBuild.Bones;
                LastRigReport = rigBuild.CheckReport ?? "";
                LastRigSnapshot = rigBuild;

                if (parityMode == Sync.SyncParityMode.ExactHomer)
                {
                    // Discard fitted soldier build and run Playroom's exact BaseBuild (homer-own.json)
                    if (rigBuild.Build != IntPtr.Zero) nm_build_destroy(rigBuild.Build);
                    build = runtime.BaseBuild;
                    buildOwnedByController = false;
                    scale = 1f;

                    if (initialFrames != null && initialParts > 0 && initialFrames.Length >= initialParts * 7)
                    {
                        Agent = nm_agent_create_exact(build, initialFrames, initialParts, yaw, seed);
                    }
                    else
                    {
                        Agent = nm_agent_create(build, new[] { startNm.x, startNm.y, startNm.z }, yaw, seed);
                    }
                }
                else
                {
                    build = rigBuild.Build;
                    buildOwnedByController = true;
                    scale = rigBuild.Scale;
                    Agent = nm_agent_create(build, new[] { startNm.x, startNm.y, startNm.z }, yaw, seed);
                }

                if (Agent == IntPtr.Zero)
                    throw new Exception($"nm_agent_create(_exact) failed: {LastError()}");

                CachePartMasses();
                nm_agent_set_strength(Agent, strength > 0.01f ? strength : 1f);
                nm_agent_set_reactions(Agent, reactions);
                nm_agent_set_dead(Agent, 0);

                // Align Soldier bind-pose RestFrames horizontally (XY) to Agent pelvis AND vertically (Z) to the
                // Agent's actual floor level under its feet — NEVER force Soldier's low root 'Bone' up onto Buttocks
                // (which sits 18-22 cm higher than 'Bone' and would float the Soldier's boots 20+ cm off the floor!).
                float[] alignedRest = new float[rigBuild.RestFrames.Length];
                Array.Copy(rigBuild.RestFrames, alignedRest, alignedRest.Length);
                var p0 = new float[3];
                if (nm_agent_get_part_transform(Agent, 0, p0, null) >= 0)
                {
                    int hipsBoneIdx = 0;
                    for (int bi = 0; bi < rigBuild.Parts.Length; bi++)
                    {
                        if (rigBuild.Parts[bi] == 0) { hipsBoneIdx = bi; break; }
                    }
                    float dx = p0[0] - alignedRest[hipsBoneIdx * 7 + 0];
                    float dy = p0[1] - alignedRest[hipsBoneIdx * 7 + 1];

                    // Compute exact floor Z under the physics feet (boot soles in alignedRest are already at z = 0)
                    float agentFloorZ = startNm.z;
                    var fL = new float[3];
                    var fR = new float[3];
                    var bL = new float[3];
                    var bR = new float[3];
                    bool hasL = nm_agent_get_part_transform(Agent, 3, fL, null) >= 0;
                    bool hasR = nm_agent_get_part_transform(Agent, 6, fR, null) >= 0;
                    if (hasL && hasR)
                    {
                        float halfZL = (nm_agent_get_part_body(Agent, 3, null, null, bL) == 2 && bL[2] > 0.01f) ? bL[2] : 0.043f;
                        float halfZR = (nm_agent_get_part_body(Agent, 6, null, null, bR) == 2 && bR[2] > 0.01f) ? bR[2] : 0.043f;
                        agentFloorZ = Mathf.Min(fL[2] - halfZL, fR[2] - halfZR);
                    }
                    float dz = agentFloorZ;

                    for (int bi = 0; bi < bones.Length; bi++)
                    {
                        alignedRest[bi * 7 + 0] += dx;
                        alignedRest[bi * 7 + 1] += dy;
                        alignedRest[bi * 7 + 2] += dz;
                    }
                }

                rig = nm_rig_create(Agent, bones.Length, rigBuild.Parents, alignedRest, rigBuild.Parts);
                if (rig == IntPtr.Zero)
                    throw new Exception($"nm_rig_create failed: {LastError()}");

                poseBuf = new float[bones.Length * 4 + 3];
                frameBuf = new float[bones.Length * 7];
                incomingBuf = new float[bones.Length * 7];
                animatedBones = new Transform[bones.Length];

                WorldEntry = mirrorWorld;
                worldOwnedByController = false;
                IsSyncLockstepControlled = true;
                SyncVisualOffset = visualOffset;
                entryBlendDuration = 0f;
                hasIncomingArmFollow = false;
                Phase = RagdollPhase.ActiveStagger;

                // Hide bot weapon cleanly during Playroom parity testing so limbs/joints are unobstructed
                RestoreWeaponParentTransform();
                if (Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
                {
                    Actor.activeWeapon.gameObject.SetActive(false);
                    weaponHiddenByFallback = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Delirium] PlayroomSync takeover failed: {ex.Message}");
                ReleaseNativeOnly();
                RestoreRagdollRigidbodies();
                Phase = RagdollPhase.Animated;
                return false;
            }
        }

        private void ApplyAgentConfig()
        {
            float strength = config.MuscleStrength.Value;
            if (!Mathf.Approximately(strength, 1f))
                nm_agent_set_strength(Agent, strength);

            if (config.ArmsOutEarlyOnCar.Value)
            {
                nm_agent_set_override(Agent, "braceForImpact", "braceDistance", 1.3f);
                nm_agent_set_override(Agent, "braceForImpact", "targetPredictionTime", 0.45f);
            }

            ApplyEnvironmentReactionsBitmask(enabled: true);

            // Keep Playroom-authentic shockSpin torso whip on rapid hits while preventing upward corpse launches
            nm_agent_set_override(Agent, "shot", "shockSpinMax", 52f);
            nm_agent_set_override(Agent, "shot", "shockSpinMin", 16f);
            nm_agent_set_override(Agent, "shot", "shockSpinLiftForceMult", 0f);

            string rawOverrides = config.Overrides.Value;
            if (!string.IsNullOrWhiteSpace(rawOverrides))
            {
                foreach (var item in rawOverrides.Split(','))
                {
                    var kv = item.Split('=');
                    if (kv.Length != 2) continue;
                    var mp = kv[0].Trim().Split('.');
                    if (mp.Length == 2 && float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float val))
                    {
                        nm_agent_set_override(Agent, mp[0].Trim(), mp[1].Trim(), val);
                    }
                }
            }
        }

        private void ApplyEnvironmentReactionsBitmask(bool enabled)
        {
            if (Agent == IntPtr.Zero) return;
            if (!enabled)
            {
                nm_agent_set_reactions(Agent, 0);
                return;
            }
            int xr = 0;
            if (config.EnableEnvironmentReactions.Value)
            {
                // Playroom environmental muscle reactions: wall/railing brace, ledge stumble, smart fall, look at hit
                xr |= NM_XR_WALL | NM_XR_LEDGE | NM_XR_SMART_FALL | NM_XR_LOOK_HIT;
            }
            if (config.EnableExtraReactions.Value)
            {
                xr |= NM_XR_WINDMILL | NM_XR_CURL_SHOT;
            }
            nm_agent_set_reactions(Agent, xr);
        }

        public void ApplyShot(DamageInfo info, bool fatal, bool alreadyDeadCorpse = false)
        {
            if (IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
            {
                Plugin.PlayroomSync.ForwardShot(this, info, fatal, null, HealthState);
                return;
            }

            // Check phase BEFORE EnsureTakenOver so 1-shot kills, DyingStagger follow-ups, and DeadFrozen wakes are distinguished cleanly!
            bool wasInDyingStagger = Phase == RagdollPhase.DyingStagger;
            bool wasFrozen = Phase == RagdollPhase.DeadFrozen;
            bool wasAlreadyDead = !wasInDyingStagger && (alreadyDeadCorpse || Phase == RagdollPhase.DeadSimulating || wasFrozen);
            bool wasStandingBeforeShot = Phase != RagdollPhase.ActiveFallen &&
                                         Phase != RagdollPhase.DeadSimulating &&
                                         Phase != RagdollPhase.DeadFrozen &&
                                         !Actor.fallenOver;
            bool wantBalancedTakeover = !wasAlreadyDead && (!fatal || (config.EnableDyingStagger.Value && !info.isCriticalHit && wasStandingBeforeShot));

            if (!EnsureTakenOver(startBalanced: wantBalancedTakeover, alreadyFallen: wasAlreadyDead || Actor.fallenOver)) return;

            var shot = WeaponForceNormalizer.NormalizeShot(info, config, -Actor.transform.forward);
            Vector3 point = info.point;
            if (point == Vector3.zero && hips != null)
                point = hips.position + Vector3.up * 0.25f;

            int part = nm_agent_nearest_part(Agent, ToNmVec(point));
            if (part < 0 || part >= 21) part = 10; // default chest
            if (info.isCriticalHit) part = 12; // head

            float now = Time.time;
            if (now - lastShotHitTime < 0.14f)
                consecutiveRapidHits++;
            else
                consecutiveRapidHits = 1;

            bool isAlive = !fatal && !Actor.dead;

            if (isAlive)
            {
                if (now - lastShotHitTime < 0.70f)
                    consecutiveAliveHits++;
                else
                    consecutiveAliveHits = 1;
            }
            lastShotHitTime = now;

            // Gentle attenuation during sustained high-ROF spray so every round still visibly punches & whips like Playroom
            float rapidScale = consecutiveRapidHits <= 1
                ? 1f
                : Mathf.Lerp(0.95f, 0.72f, Mathf.Clamp01((consecutiveRapidHits - 1) / 8f));

            // Case 1: Follow-up bullet hitting a bot mid-DyingStagger (double-tap / finishing shots)
            if (wasInDyingStagger)
            {
                ApplyDyingFollowUpShot(shot, part, point, info.isCriticalHit, rapidScale);
                return;
            }

            // Case 2: Already-dead corpse (including woken DeadFrozen corpses) taking extra bullets
            if (wasAlreadyDead)
            {
                if (wasFrozen || !deadRelaxed)
                {
                    MarkDead(keepEntryBlend: false);
                    GoFullyLimp();
                    deadRelaxed = true;
                }
                else if (deathAgonyActive && deathAgonyElapsed >= 0.35f)
                {
                    CancelDeathAgonyAndGoLimp();
                }

                EmitImpactBlood(point, shot.Direction, heavy: true);

                float corpseDesired = shot.TotalImpulse * (wasFrozen ? 0.62f : 0.48f) * rapidScale;
                float corpseBudgeted = ConsumeBurstBudget(corpseDesired);
                ApplyNaturalDistributedImpulse(part, point, shot.Direction, corpseBudgeted, out float cLocal, out float cTorso);
                WeaponForceNormalizer.RecordTelemetry(shot, corpseBudgeted, cLocal, cTorso, part);
                deadStillElapsed = 0f;
                deadAnchorElapsed = 0f;
                deadAnchorPos = lastComPos;
                return;
            }

            // Case 3: Fresh fatal hit
            if (!isAlive)
            {
                bool canDyingStagger = ShouldEnterDyingStagger(part, info.isCriticalHit, isExplosion: false, wasStandingBeforeShot);
                if (canDyingStagger)
                {
                    StartDyingStagger(shot, part, point, rapidScale);
                    return;
                }

                // Instant-kill fatal shot (headshots, already fallen, or chance miss):
                // Relax joints FIRST using holdPose=true so bullet impulse whips the head/torso cleanly.
                MarkDead(keepEntryBlend: true);
                StartNaturalDeathCollapse();
                ScheduleDeathAgonyIfEligible(part, info.isCriticalHit, isExplosion: false);
                TryTriggerReflexBlindFire(isDeathShot: true);

                float fatalBoost = Mathf.Lerp(1.0f, 1.35f, shot.Power01);
                float fatalDesired = shot.TotalImpulse * fatalBoost * rapidScale;
                float fatalBudgeted = ConsumeBurstBudget(fatalDesired);
                ApplyNaturalDistributedImpulse(part, point, shot.Direction, fatalBudgeted, out float fLocal, out float fTorso);
                WeaponForceNormalizer.RecordTelemetry(shot, fatalBudgeted, fLocal, fTorso, part);
                return;
            }

            // Never let Rust independently flag a living Ravenfield actor as dead
            nm_agent_set_dead(Agent, 0);

            float hpRatio = Mathf.Clamp01(Actor.health / Mathf.Max(Actor.maxHealth, 1f));
            bool isStandingAlive = Phase != RagdollPhase.ActiveFallen && !Actor.fallenOver && Actor.balance > 0f;

            // Scale alive standing push dynamically with gun power so light guns force a crisp step-back and rifles/shotguns stagger hard
            float aliveScale = Mathf.Lerp(
                config.AliveShotPushScale.Value,
                Mathf.Min(config.AliveShotPushScale.Value * 1.45f, 1.25f),
                shot.Power01);
            float desiredImpulse = (isStandingAlive
                ? Mathf.Max(shot.TotalImpulse * aliveScale, Mathf.Lerp(32f, 64f, shot.Power01) * config.ShotForceScale.Value)
                : shot.TotalImpulse) * rapidScale;

            // Apply sliding burst budget (protects against multi-pellet shotguns & rapid-fire modded guns)
            float budgetedImpulse = ConsumeBurstBudget(desiredImpulse);

            // Distribute impulse by hit zone so upper-body hits tilt/lean the torso cleanly in the bullet direction
            ApplyNaturalDistributedImpulse(part, point, shot.Direction, budgetedImpulse, out float localImp, out float torsoImp);
            WeaponForceNormalizer.RecordTelemetry(shot, budgetedImpulse, localImp, torsoImp, part);

            // Delirium shottask.rs health thresholds:
            //   >= 100 : stayUpright + reachForWound + stagger/step to keep balance
            //   50..99 : shotRelax + catchFall (alive knockdown, can get back up)
            //   <  50  : fatal (Rust sets a.dead = true, disables get-up forever)
            float nmHealth;
            string weaponSet = shot.WeaponSet;
            if (isStandingAlive && hpRatio > 0.25f && consecutiveAliveHits < 4)
            {
                nmHealth = Mathf.Lerp(118f, 195f, hpRatio);
                // Scale lean amount, torso yield, and spine pain directly with gun power so light vs heavy guns
                // produce clearly different body lean & stumble reactions!
                float pwr = shot.Power01;
                nm_agent_set_override(Agent, "leanInDirection", "leanAmount", Mathf.Lerp(0.25f, 1.15f, pwr));
                nm_agent_set_override(Agent, "shot", "bodyStiffness", Mathf.Lerp(11.2f, 7.2f, pwr));
                nm_agent_set_override(Agent, "stayUpright", "stiffness", Mathf.Lerp(5.5f, 1.7f, pwr));
                nm_agent_set_override(Agent, "shot", "spinePainMultiplier", Mathf.Lerp(0.9f, 2.7f, pwr));
            }
            else
            {
                // Sustained burst (4+ hits) or low HP / broken balance: drops with catchFall while staying alive
                nmHealth = 82f;
            }

            // Batch only same-frame multi-pellet shotgun hits (35ms), so every SMG/AR/LMG round fires ShotTask::next_hit!
            if (now - lastShotTaskTime >= 0.035f)
            {
                lastShotTaskTime = now;
                nm_agent_task_shot_force(Agent, WorldEntry.World, weaponSet, part, ToNmVec(point), ToNmVec(shot.Direction), nmHealth, shot.TaskForce);
                nm_agent_set_dead(Agent, 0);
            }
            ApplyLimbInjuryIfApplicable(part, Mathf.Lerp(0.18f, 0.42f, shot.Power01));

            staggerElapsed = 0f;
            settledElapsed = 0f;
            aliveLeanArmed = false;
            aliveStaggerDir = shot.Direction;
            aliveLeanAmount = Mathf.Lerp(0.42f, 0.95f, shot.Power01);
            if (isStandingAlive && !blindFireActive)
            {
                TryTriggerReflexBlindFire(isDeathShot: false);
            }
            if (Phase == RagdollPhase.ActiveFallen)
            {
                // Fresh hit while lying down resets settle timer slightly without locking them down forever
                fallenStillElapsed = 0f;
                fallenAnchorElapsed = 0f;
                fallenAnchorPos = lastComPos;
                fallenTaskStopped = false;
            }
        }

        // =========================================================================
        // Locational Health, Bleedout, Hopping, Crawling, Clutching & Surrender
        // =========================================================================

        public void ApplyLocationalShot(DamageInfo info, HitOutcome outcome, BodyHealthState health)
        {
            if (Actor == null || health == null) return;
            HealthState = health;

            if (IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
            {
                Plugin.PlayroomSync.ForwardShot(this, info, outcome.Lethal, outcome, health);
                return;
            }

            bool wasGroundPhase = Phase == RagdollPhase.ActiveFallen ||
                                  Phase == RagdollPhase.WoundedCrawling ||
                                  Actor.fallenOver;
            bool wasStandingBeforeShot = !wasGroundPhase &&
                                         Phase != RagdollPhase.DeadSimulating &&
                                         Phase != RagdollPhase.DeadFrozen;

            var shot = WeaponForceNormalizer.NormalizeShot(info, config, -Actor.transform.forward);
            Vector3 point = info.point;
            if (point == Vector3.zero)
                point = hips != null ? hips.position + Vector3.up * 0.25f : Actor.CenterPosition();

            // 1. Instant Headshot Kill -> immediate limp collapse, NO dying stagger, NO agony!
            if (outcome.InstantHeadshot || outcome.Condition == WoundedCondition.DeadHeadshot)
            {
                if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;
                ClearPartialKeyframe();
                woundedCombat.StopPointArm(Agent, WorldEntry.World);
                woundedCombat.RestoreActorFlags(health);
                activeWoundedBehavior = null;
                EmitImpactBlood(point, shot.Direction, heavy: true);

                MarkDead(keepEntryBlend: true, keepEnvReactions: false);
                StartNaturalDeathCollapse();
                GoFullyLimp();
                deadRelaxed = true;
                ResetDeathAgonyState();

                float imp = ConsumeBurstBudget(shot.TotalImpulse * Mathf.Lerp(1.0f, 1.35f, shot.Power01));
                ApplyNaturalDistributedImpulse(12, point, shot.Direction, imp, out float hLocal, out float hTorso);
                WeaponForceNormalizer.RecordTelemetry(shot, imp, hLocal, hTorso, 12);
                return;
            }

            // 2. Fatal Left-Chest (Heart) or Ruined Chest -> DyingStagger if standing, immediate collapse if down
            if (outcome.Lethal || Actor.dead)
            {
                bool wantDyingStagger = outcome.EnterDyingStagger && config.EnableDyingStagger.Value && wasStandingBeforeShot;
                if (!EnsureTakenOver(startBalanced: wantDyingStagger, alreadyFallen: !wantDyingStagger)) return;
                ClearPartialKeyframe();
                woundedCombat.StopPointArm(Agent, WorldEntry.World);
                woundedCombat.RestoreActorFlags(health);
                StopActiveWoundedBehavior();

                if (wantDyingStagger)
                {
                    StartDyingStagger(shot, outcome.Part, point, rapidScale: 1f);
                }
                else
                {
                    EmitImpactBlood(point, shot.Direction, heavy: true);
                    MarkDead(keepEntryBlend: true, keepEnvReactions: false);
                    StartNaturalDeathCollapse();
                    ScheduleDeathAgonyIfEligible(outcome.Part, isCriticalOrHeadshot: false, isExplosion: false);
                    float imp = ConsumeBurstBudget(shot.TotalImpulse * 1.1f);
                    ApplyNaturalDistributedImpulse(outcome.Part, point, shot.Direction, imp, out float fLocal, out float fTorso);
                    WeaponForceNormalizer.RecordTelemetry(shot, imp, fLocal, fTorso, outcome.Part);
                }
                return;
            }

            // 3. Non-lethal locational wound -> take over & transition into exact wounded behavior
            bool isGroundCondition =
                outcome.Condition == WoundedCondition.CrawlingOneLeg ||
                outcome.Condition == WoundedCondition.CrawlingTwoLegs ||
                outcome.Condition == WoundedCondition.FallenStomachClutch ||
                outcome.Condition == WoundedCondition.SurrenderingGround;

            if (!EnsureTakenOver(startBalanced: !isGroundCondition && !wasGroundPhase, alreadyFallen: isGroundCondition || wasGroundPhase))
                return;

            nm_agent_set_dead(Agent, 0);

            int part = Mathf.Clamp(outcome.Part, 0, 20);
            UpdateWoundLocalFromAgent(health, outcome.Zone, part, point, -shot.Direction);
            EmitImpactBlood(point, shot.Direction, heavy: outcome.NewlyRuined || outcome.Zone == BodyZone.Stomach || outcome.Zone == BodyZone.ChestOther);

            float now = Time.time;
            if (now - lastShotHitTime < 0.16f)
                consecutiveRapidHits++;
            else
                consecutiveRapidHits = 1;
            if (now - lastShotHitTime < 0.75f)
                consecutiveAliveHits++;
            else
                consecutiveAliveHits = 1;
            lastShotHitTime = now;

            float rapidScale = consecutiveRapidHits <= 1
                ? 1f
                : Mathf.Lerp(0.95f, 0.72f, Mathf.Clamp01((consecutiveRapidHits - 1) / 8f));

            // Apply Playroom-style localized bullet impulse so EVERY rapid hit visibly snaps/twists the hit body part
            float pushScale = isGroundCondition ? 0.65f : Mathf.Lerp(0.78f, 1.10f, shot.Power01);
            float budgeted = ConsumeBurstBudget(shot.TotalImpulse * pushScale * rapidScale);
            ApplyNaturalDistributedImpulse(part, point, shot.Direction, budgeted, out float lImp, out float tImp);
            WeaponForceNormalizer.RecordTelemetry(shot, budgeted, lImp, tImp, part);

            // Progressive Playroom-style limb injury accumulation on every limb hit
            ApplyLimbInjuryIfApplicable(part, outcome.NewlyRuined ? 0.85f : Mathf.Lerp(0.20f, 0.44f, shot.Power01));

            // Physics check for 1-leg ruined: heavy shot or follow-up knock drops hopper cleanly into 1-leg drag crawl!
            if (outcome.Condition == WoundedCondition.HoppingOneLeg)
            {
                bool wasAlreadyHopping = Phase == RagdollPhase.WoundedStanding && activeWoundedBehavior == "woundedHop";
                bool knockedDown = wasGroundPhase || nm_agent_has_fallen(Agent) == 1 ||
                                   (outcome.NewlyRuined && (shot.Power01 >= 0.68f || budgeted >= 48f)) ||
                                   (wasAlreadyHopping && (shot.Power01 >= 0.45f || budgeted >= 34f));
                if (knockedDown)
                {
                    health.IsFallenWithOneDeadLeg = true;
                    health.ResolveCondition();
                }
            }

            // Ground & Surrender conditions transition immediately; standing torso/arm/leg hits run full Playroom
            // ShotTask::next_hit physics stagger during rapid gunfire, then smoothly enter WoundedStanding once fire pauses!
            bool mustImmediateTransition =
                health.Condition == WoundedCondition.Surrendering ||
                health.Condition == WoundedCondition.SurrenderingGround ||
                health.Condition == WoundedCondition.CrawlingOneLeg ||
                health.Condition == WoundedCondition.CrawlingTwoLegs ||
                health.Condition == WoundedCondition.FallenStomachClutch;

            if (mustImmediateTransition)
            {
                pendingWoundedTransitionTimer = 0f;
                TransitionToWoundedBehavior(health);
                return;
            }

            // If currently in partial-keyframe WoundedStanding, release legs back to full Delirium physics for the burst!
            if (Phase == RagdollPhase.WoundedStanding)
            {
                ClearPartialKeyframe();
                StopActiveWoundedBehavior();
                Phase = RagdollPhase.ActiveStagger;
            }

            // Drive continuous Playroom ShotTask::next_hit (spinePain, shockSpin, reachForWound, stumbling steps!)
            if (now - lastShotTaskTime >= 0.035f)
            {
                lastShotTaskTime = now;
                float hpRatio = Mathf.Clamp01(health.Blood / 100f);
                float nmHealth = (consecutiveAliveHits >= 5 || hpRatio <= 0.25f)
                    ? 82f
                    : Mathf.Lerp(118f, 185f, hpRatio);
                float pwr = shot.Power01;
                nm_agent_set_override(Agent, "shot", "bodyStiffness", Mathf.Lerp(10.5f, 7.2f, pwr));
                nm_agent_set_override(Agent, "stayUpright", "stiffness", Mathf.Lerp(4.8f, 1.7f, pwr));
                nm_agent_set_override(Agent, "shot", "spinePainMultiplier", Mathf.Lerp(1.1f, 2.7f, pwr));
                nm_agent_task_shot_force(Agent, WorldEntry.World, shot.WeaponSet, part, ToNmVec(point), ToNmVec(shot.Direction), nmHealth, shot.TaskForce);
                nm_agent_set_dead(Agent, 0);
            }

            staggerElapsed = 0f;
            settledElapsed = 0f;
            aliveLeanArmed = false;
            aliveStaggerDir = shot.Direction;
            aliveLeanAmount = Mathf.Lerp(0.45f, 0.95f, shot.Power01);
            pendingWoundedTransitionTimer = health.IsWounded ? 0.65f : 0f;
        }

        private bool now_should_stagger(float rawDmg)
        {
            return config.EnableLiveShotStagger.Value && rawDmg >= config.MinDamageForLiveStagger.Value;
        }

        public void SyncFromHealthState(BodyHealthState health)
        {
            if (IsSyncLockstepControlled) return;
            if (Actor == null || health == null || Actor.dead || !health.IsWounded) return;
            HealthState = health;

            bool isGround = health.Condition == WoundedCondition.CrawlingOneLeg ||
                            health.Condition == WoundedCondition.CrawlingTwoLegs ||
                            health.Condition == WoundedCondition.FallenStomachClutch ||
                            health.Condition == WoundedCondition.SurrenderingGround;
            if (!EnsureTakenOver(startBalanced: !isGround, alreadyFallen: isGround)) return;

            nm_agent_set_dead(Agent, 0);
            TransitionToWoundedBehavior(health);
        }

        public void ApplyBleedoutDeath(BodyHealthState health)
        {
            if (IsSyncLockstepControlled) return;
            if (Agent == IntPtr.Zero || WorldEntry == null) return;
            bool wasUpright = Phase == RagdollPhase.WoundedStanding ||
                              (Phase == RagdollPhase.Surrendering && (health == null || health.Condition != WoundedCondition.SurrenderingGround));

            ClearPartialKeyframe();
            woundedCombat.StopPointArm(Agent, WorldEntry.World);
            woundedCombat.RestoreActorFlags(health);
            StopActiveWoundedBehavior();
            RestoreWeaponParentTransform();

            if (wasUpright)
            {
                MarkDead(keepEntryBlend: false, keepEnvReactions: true);
                StartNaturalDeathCollapse();
                ScheduleDeathAgonyIfEligible(10, isCriticalOrHeadshot: false, isExplosion: false);
                Vector3 slumpDir = (Actor != null ? -Actor.transform.forward : Vector3.back) + Vector3.right * UnityEngine.Random.Range(-0.4f, 0.4f);
                Vector3 at = hips != null ? hips.position + Vector3.up * 0.35f : lastComPos;
                ApplyNaturalDistributedImpulse(10, at, slumpDir.normalized, 14f, out _, out _);
            }
            else
            {
                MarkDead(keepEntryBlend: false, keepEnvReactions: false);
                GoFullyLimp();
                deadRelaxed = true;
            }
        }

        private void SetPartialKeyframe(string mask)
        {
            if (Agent == IntPtr.Zero || rig == IntPtr.Zero || bones == null || animatedBones == null) return;
            if (Actor != null)
            {
                ForceAnimatorNeverCullField?.SetValue(Actor, true);
                if (Actor.animator != null)
                    Actor.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            if (partialKeyframeActive && partialKeyframeMask != mask)
            {
                nm_agent_keyframe(Agent, "fb", 0);
            }
            partialKeyframeActive = true;
            partialKeyframeMask = mask;
            FeedIncomingAnimatedPose(0f);
            nm_agent_keyframe(Agent, mask, 1);
        }

        private void ClearPartialKeyframe()
        {
            if (partialKeyframeActive && Agent != IntPtr.Zero)
            {
                nm_agent_keyframe(Agent, "fb", 0);
                if (rig != IntPtr.Zero)
                    nm_rig_incoming(rig, Agent, null);
            }
            partialKeyframeActive = false;
            partialKeyframeMask = null;
            if (Actor != null)
            {
                ForceAnimatorNeverCullField?.SetValue(Actor, false);
            }
        }

        public void FeedIncomingAnimatedPose(float dt)
        {
            if (!partialKeyframeActive || Phase != RagdollPhase.WoundedStanding) return;
            if (Agent == IntPtr.Zero || rig == IntPtr.Zero || animatedBones == null || incomingBuf == null) return;

            if (Actor != null)
            {
                ForceAnimatorNeverCullField?.SetValue(Actor, true);
                if (Actor.animator != null && Actor.animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    Actor.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            float hopY = 0f;
            if (HealthState != null && HealthState.Condition == WoundedCondition.HoppingOneLeg && Actor != null)
            {
                Vector3 v = Actor.Velocity();
                float hSpeed = new Vector2(v.x, v.z).magnitude;
                if (hSpeed > 0.12f)
                {
                    hopBouncePhase += dt * (1.45f + 0.20f * Mathf.Min(hSpeed, 2.5f)) * Mathf.PI * 2f;
                    // Smooth cosine wave (zero velocity cusp/jolt compared to |sin|)
                    hopY = 0.055f * (0.5f - 0.5f * Mathf.Cos(hopBouncePhase));
                }
            }

            Vector3 offset = Vector3.up * hopY;
            for (int i = 0; i < animatedBones.Length; i++)
            {
                var ab = animatedBones[i];
                var src = ab != null ? ab : bones[i];
                if (src == null) continue;
                WriteNmVec(src.position + offset, incomingBuf, i * 7);
                WriteNmQuat(src.rotation, incomingBuf, i * 7 + 3);
            }
            nm_rig_incoming(rig, Agent, incomingBuf);
        }

        private void ApplyAllRuinedLimbInjuries(BodyHealthState health)
        {
            if (Agent == IntPtr.Zero || WorldEntry == null || health == null) return;
            if (health.LeftArmRuined) nm_msg_send_text(Agent, WorldEntry.World, "injury mask=ul amount=0.94");
            if (health.RightArmRuined) nm_msg_send_text(Agent, WorldEntry.World, "injury mask=ur amount=0.94");
            if (health.LeftLegRuined) nm_msg_send_text(Agent, WorldEntry.World, "injury mask=ll amount=0.94");
            if (health.RightLegRuined) nm_msg_send_text(Agent, WorldEntry.World, "injury mask=lr amount=0.94");
        }

        private void StopActiveWoundedBehavior()
        {
            if (string.IsNullOrEmpty(activeWoundedBehavior) || Agent == IntPtr.Zero || WorldEntry == null)
            {
                activeWoundedBehavior = null;
                return;
            }
            nm_msg_begin(Agent, activeWoundedBehavior);
            nm_msg_set_bool(Agent, "start", 0);
            nm_msg_send(Agent, WorldEntry.World);
            activeWoundedBehavior = null;
        }

        private void TransitionToWoundedBehavior(BodyHealthState health)
        {
            if (Agent == IntPtr.Zero || WorldEntry == null || health == null) return;

            ClearIncomingArmFollow();
            entryBlendDuration = 0f;
            nm_agent_task_stop(Agent);
            ApplyAllRuinedLimbInjuries(health);

            Vector3 anchor = Actor != null ? Actor.transform.position : lastComPos;
            Vector3 facing = Actor != null ? Vector3.ProjectOnPlane(Actor.transform.forward, Vector3.up).normalized : Vector3.forward;
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;
            Vector3 threat = health.LastThreatPos != Vector3.zero ? health.LastThreatPos : (anchor + facing * 8f + Vector3.up * 1.4f);
            Vector3 hipsAnchor = hips != null ? hips.position : anchor;
            float safeGroundY = runtime.SampleGroundHeight(hipsAnchor + Vector3.up * 0.35f, Actor != null ? Actor.transform : null, hipsAnchor.y - 0.15f);

            switch (health.Condition)
            {
                case WoundedCondition.Surrendering:
                case WoundedCondition.SurrenderingGround:
                {
                    ClearPartialKeyframe();
                    StopActiveWoundedBehavior();
                    woundedCombat.StopPointArm(Agent, WorldEntry.World);
                    RestoreWeaponParentTransform();
                    woundedCombat.HandleSurrenderSetup(health);

                    bool onGround = health.Condition == WoundedCondition.SurrenderingGround;
                    surrenderAnchorPos = hips != null ? new Vector3(hips.position.x, safeGroundY, hips.position.z) : new Vector3(anchor.x, safeGroundY, anchor.z);

                    nm_msg_begin(Agent, "surrender");
                    nm_msg_set_bool(Agent, "start", 1);
                    nm_msg_set_vec3(Agent, "threatPos", ToNmVec(threat));
                    nm_msg_set_int(Agent, "injuredArm", 1);
                    nm_msg_set_bool(Agent, "otherArmInjured", health.LeftArmRuined ? 1 : 0);
                    nm_msg_set_float(Agent, "kneelAmount", health.SurrenderKneel01);
                    nm_msg_set_bool(Agent, "onGround", onGround ? 1 : 0);
                    nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(surrenderAnchorPos));
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_send(Agent, WorldEntry.World);

                    activeWoundedBehavior = "surrender";
                    Phase = RagdollPhase.Surrendering;
                    break;
                }

                case WoundedCondition.CrawlingOneLeg:
                case WoundedCondition.CrawlingTwoLegs:
                {
                    ClearPartialKeyframe();
                    if (activeWoundedBehavior != "woundedCrawl")
                    {
                        StopActiveWoundedBehavior();
                        nm_msg_begin(Agent, "woundedCrawl");
                        nm_msg_set_bool(Agent, "start", 1);
                        nm_msg_set_bool(Agent, "deadLeftLeg", health.LeftLegRuined ? 1 : 0);
                        nm_msg_set_bool(Agent, "deadRightLeg", health.RightLegRuined ? 1 : 0);
                        nm_msg_set_bool(Agent, "leftArmInjured", health.LeftArmRuined ? 1 : 0);
                        nm_msg_set_vec3(Agent, "targetPosition", ToNmVec(anchor + facing * 6f));
                        nm_msg_set_vec3(Agent, "aimTarget", ToNmVec(threat));
                        nm_msg_set_bool(Agent, "shooting", 0);
                        nm_msg_set_float(Agent, "speed", health.BothLegsRuined ? 0.75f : 0.95f);
                        nm_msg_set_float(Agent, "weakness", health.Weakness01);
                        nm_msg_set_float(Agent, "groundZ", safeGroundY);
                        nm_msg_send(Agent, WorldEntry.World);
                        activeWoundedBehavior = "woundedCrawl";
                    }
                    EnsureWeaponVisibleInRagdollHand();
                    Phase = RagdollPhase.WoundedCrawling;
                    break;
                }

                case WoundedCondition.FallenStomachClutch:
                {
                    ClearPartialKeyframe();
                    StopActiveWoundedBehavior();
                    nm_msg_begin(Agent, "woundedClutch");
                    nm_msg_set_bool(Agent, "start", 1);
                    nm_msg_set_int(Agent, "woundPart", health.PrimaryTorsoWoundPart);
                    nm_msg_set_bool(Agent, "onGround", 1);
                    nm_msg_set_vec3(Agent, "aimTarget", ToNmVec(threat));
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_set_bool(Agent, "useLeftArm", health.LeftArmRuined ? 0 : 1);
                    nm_msg_set_float(Agent, "groundZ", safeGroundY);
                    nm_msg_send(Agent, WorldEntry.World);

                    activeWoundedBehavior = "woundedClutch";
                    EnsureWeaponVisibleInRagdollHand();
                    Phase = RagdollPhase.WoundedCrawling;
                    break;
                }

                case WoundedCondition.HoppingOneLeg:
                {
                    // Partial ragdoll: pin Pelvis + HEALTHY leg to Ravenfield NavMesh hop animation;
                    // BROKEN leg & ENTIRE UPPER BODY stay in smooth Delirium physics (zero jitter!)
                    Phase = RagdollPhase.WoundedStanding;
                    SetPartialKeyframe(health.LeftLegRuined ? "lr" : "ll");
                    if (activeWoundedBehavior != "woundedHop")
                    {
                        StopActiveWoundedBehavior();
                        int deadLeg = health.LeftLegRuined ? 0 : 1;
                        int clutchPart = (health.StomachDamaged || health.ChestDamaged) ? health.PrimaryTorsoWoundPart : -1;

                        nm_msg_begin(Agent, "woundedHop");
                        nm_msg_set_bool(Agent, "start", 1);
                        nm_msg_set_int(Agent, "deadLeg", deadLeg);
                        nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(Vector3.zero));
                        nm_msg_set_vec3(Agent, "moveVel", ToNmVec(Actor != null ? Actor.Velocity() : Vector3.zero));
                        nm_msg_set_vec3(Agent, "facingDir", ToNmVec(facing));
                        nm_msg_set_float(Agent, "weakness", health.Weakness01);
                        nm_msg_set_int(Agent, "clutchPart", clutchPart);
                        nm_msg_set_bool(Agent, "leftArmInjured", health.LeftArmRuined ? 1 : 0);
                        nm_msg_send(Agent, WorldEntry.World);
                        activeWoundedBehavior = "woundedHop";
                    }
                    EnsureWeaponVisibleInRagdollHand();
                    break;
                }

                case WoundedCondition.StomachHurt:
                case WoundedCondition.ChestHurt:
                case WoundedCondition.ArmOffhandDisabled:
                {
                    // Partial ragdoll: pin Pelvis + Both Legs ("lb") to Ravenfield's smooth real walking animation;
                    // UPPER BODY runs Delirium physics (front belly/chest clutch, limp left arm, pointArm shooting!)
                    Phase = RagdollPhase.WoundedStanding;
                    SetPartialKeyframe("lb");
                    StopActiveWoundedBehavior();
                    bool clutchTorso = !health.LeftArmRuined && (health.StomachDamaged || health.ChestDamaged);
                    float hunch = health.Condition == WoundedCondition.StomachHurt ? 0.50f : (health.Condition == WoundedCondition.ChestHurt ? 0.28f : 0.10f);
                    float stag = health.Condition == WoundedCondition.ChestHurt ? 0.40f : 0.20f;

                    nm_msg_begin(Agent, "woundedClutch");
                    nm_msg_set_bool(Agent, "start", 1);
                    nm_msg_set_int(Agent, "woundPart", health.PrimaryTorsoWoundPart);
                    nm_msg_set_bool(Agent, "onGround", 0);
                    nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(Vector3.zero));
                    nm_msg_set_vec3(Agent, "moveVel", ToNmVec(Actor != null ? Actor.Velocity() : Vector3.zero));
                    nm_msg_set_vec3(Agent, "facingDir", ToNmVec(facing));
                    nm_msg_set_float(Agent, "hunchAmount", hunch);
                    nm_msg_set_float(Agent, "staggerAmount", stag);
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_set_bool(Agent, "useLeftArm", clutchTorso ? 1 : 0);
                    nm_msg_send(Agent, WorldEntry.World);

                    activeWoundedBehavior = "woundedClutch";
                    EnsureWeaponVisibleInRagdollHand();
                    break;
                }
            }
        }

        private void EnsureWeaponVisibleInRagdollHand()
        {
            if (Actor == null || Actor.dead) return;
            if (!weaponAttachedToRagdoll)
                CaptureWeaponForRagdollHand(allowDeadWeapon: false);
            if (Actor.activeWeapon != null && Actor.activeWeapon.gameObject != null && !Actor.activeWeapon.gameObject.activeSelf)
                Actor.activeWeapon.gameObject.SetActive(true);
        }

        private void UpdateWoundedPhaseLogic(float dt)
        {
            var health = HealthState;
            if (health == null || !health.IsWounded)
            {
                BeginBlendBackToStanding();
                return;
            }

            if (nm_agent_is_dead(Agent) == 1)
                nm_agent_set_dead(Agent, 0);

            // Progressive muscle weakening as blood drops below 35%
            float wantStrength = Mathf.Clamp(config.MuscleStrength.Value * (1f - 0.55f * health.Weakness01), 0.25f, 3.0f);
            if (Mathf.Abs(wantStrength - lastAppliedMuscleStrength) > 0.04f)
            {
                lastAppliedMuscleStrength = wantStrength;
                nm_agent_set_strength(Agent, wantStrength);
            }

            // Read native feedback from Rust wounded behaviors (fall transitions!)
            PollWoundedFeedback(health);

            // Emit arterial wound blood spurts & ground drops across all ruined wounds
            TickLocationalBleeding();

            bool testRangeIdle = Plugin.LodManager != null && Plugin.LodManager.TestRangeMode;
            Vector3 actorPos = Actor.transform.position;
            Vector3 actorVel = Actor.Velocity();
            Vector3 facing = Vector3.ProjectOnPlane(Actor.transform.forward, Vector3.up).normalized;
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;

            if (Phase == RagdollPhase.Surrendering)
            {
                woundedCombat.HandleSurrenderSetup(health);
                Vector3 threat = health.LastAttacker != null && !health.LastAttacker.dead
                    ? health.LastAttacker.CenterPosition()
                    : (health.LastThreatPos != Vector3.zero ? health.LastThreatPos : actorPos + facing * 6f + Vector3.up * 1.4f);

                nm_msg_begin(Agent, "surrender");
                nm_msg_set_vec3(Agent, "threatPos", ToNmVec(threat));
                nm_msg_set_bool(Agent, "otherArmInjured", health.LeftArmRuined ? 1 : 0);
                nm_msg_set_float(Agent, "kneelAmount", health.SurrenderKneel01);
                nm_msg_set_bool(Agent, "onGround", health.Condition == WoundedCondition.SurrenderingGround ? 1 : 0);
                nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(surrenderAnchorPos));
                nm_msg_set_float(Agent, "weakness", health.Weakness01);
                nm_msg_send(Agent, WorldEntry.World);
                return;
            }

            if (Phase == RagdollPhase.WoundedStanding)
            {
                woundedCombat.TickStandingWounded(health, Agent, WorldEntry.World, dt, weaponRagdollHand, allowFire: !testRangeIdle);

                if (woundedCombat.CurrentAimTarget != Vector3.zero)
                {
                    Vector3 toAim = Vector3.ProjectOnPlane(woundedCombat.CurrentAimTarget - actorPos, Vector3.up);
                    if (toAim.sqrMagnitude > 0.04f) facing = toAim.normalized;
                }

                if (activeWoundedBehavior == "woundedHop")
                {
                    nm_msg_begin(Agent, "woundedHop");
                    nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(Vector3.zero));
                    nm_msg_set_vec3(Agent, "moveVel", ToNmVec(actorVel));
                    nm_msg_set_vec3(Agent, "facingDir", ToNmVec(facing));
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_send(Agent, WorldEntry.World);
                }
                else if (activeWoundedBehavior == "woundedClutch")
                {
                    nm_msg_begin(Agent, "woundedClutch");
                    nm_msg_set_vec3(Agent, "anchorPos", ToNmVec(Vector3.zero));
                    nm_msg_set_vec3(Agent, "moveVel", ToNmVec(actorVel));
                    nm_msg_set_vec3(Agent, "facingDir", ToNmVec(facing));
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_set_bool(Agent, "useLeftArm", (!health.LeftArmRuined && (health.StomachDamaged || health.ChestDamaged)) ? 1 : 0);
                    nm_msg_send(Agent, WorldEntry.World);
                }
                return;
            }

            if (Phase == RagdollPhase.WoundedCrawling)
            {
                bool isStomachClutch = health.Condition == WoundedCondition.FallenStomachClutch;
                Vector3 pelvisPos = hips != null ? hips.position : lastComPos;
                woundedCombat.TickGroundWounded(health, Agent, WorldEntry.World, dt, pelvisPos, weaponRagdollHand, isStomachClutch, allowFire: !testRangeIdle);

                float safeGroundY = runtime.SampleGroundHeight(pelvisPos + Vector3.up * 0.35f, Actor.transform, pelvisPos.y - 0.15f);

                // Keep Ravenfield Actor root aligned with crawling pelvis so hitboxes, cover detection & enemy targeting match the crawling body!
                woundedCrawlSyncTimer += dt;
                if (woundedCrawlSyncTimer >= 0.12f && hips != null)
                {
                    woundedCrawlSyncTimer = 0f;
                    Vector3 wantRoot = new Vector3(pelvisPos.x, safeGroundY, pelvisPos.z);
                    if ((wantRoot - Actor.transform.position).sqrMagnitude > 0.04f)
                    {
                        Transform ragObjTf = Actor.ragdoll != null && Actor.ragdoll.ragdollObject != null ? Actor.ragdoll.ragdollObject.transform : null;
                        Transform origParent = ragObjTf != null ? ragObjTf.parent : null;
                        if (ragObjTf != null) ragObjTf.SetParent(null, true);
                        Actor.transform.position = wantRoot;
                        if (ragObjTf != null) ragObjTf.SetParent(origParent, true);
                    }
                }

                if (activeWoundedBehavior == "woundedCrawl")
                {
                    nm_msg_begin(Agent, "woundedCrawl");
                    nm_msg_set_bool(Agent, "deadLeftLeg", health.LeftLegRuined ? 1 : 0);
                    nm_msg_set_bool(Agent, "deadRightLeg", health.RightLegRuined ? 1 : 0);
                    nm_msg_set_bool(Agent, "leftArmInjured", health.LeftArmRuined ? 1 : 0);
                    if (woundedCombat.CurrentCoverWaypoint != Vector3.zero)
                        nm_msg_set_vec3(Agent, "targetPosition", ToNmVec(woundedCombat.CurrentCoverWaypoint));
                    if (woundedCombat.CurrentAimTarget != Vector3.zero)
                        nm_msg_set_vec3(Agent, "aimTarget", ToNmVec(woundedCombat.CurrentAimTarget));
                    nm_msg_set_bool(Agent, "shooting", woundedCombat.IsShootingBurst ? 1 : 0);
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_set_float(Agent, "groundZ", safeGroundY);
                    nm_msg_send(Agent, WorldEntry.World);
                }
                else if (activeWoundedBehavior == "woundedClutch")
                {
                    nm_msg_begin(Agent, "woundedClutch");
                    if (woundedCombat.CurrentAimTarget != Vector3.zero)
                        nm_msg_set_vec3(Agent, "aimTarget", ToNmVec(woundedCombat.CurrentAimTarget));
                    nm_msg_set_float(Agent, "weakness", health.Weakness01);
                    nm_msg_set_bool(Agent, "useLeftArm", health.LeftArmRuined ? 0 : 1);
                    nm_msg_set_float(Agent, "groundZ", safeGroundY);
                    nm_msg_send(Agent, WorldEntry.World);
                }
            }
        }

        private void PollWoundedFeedback(BodyHealthState health)
        {
            int count = nm_agent_feedback_count(Agent);
            for (int i = 0; i < count; i++)
            {
                int len = nm_agent_feedback_get(Agent, i, feedbackBuf, feedbackBuf.Length, out _, null);
                if (len <= 1) continue;
                int strLen = Mathf.Min(len - 1, feedbackBuf.Length - 1);
                string fb = System.Text.Encoding.UTF8.GetString(feedbackBuf, 0, strLen);

                if (fb == "woundedHopFell")
                {
                    health.IsFallenWithOneDeadLeg = true;
                    health.ResolveCondition();
                    TransitionToWoundedBehavior(health);
                    return;
                }
                if (fb == "surrenderFell")
                {
                    health.IsFallenWhileSurrendering = true;
                    health.ResolveCondition();
                    TransitionToWoundedBehavior(health);
                    return;
                }
                if (fb == "woundedClutchFell")
                {
                    if (health.AnyLegRuined)
                    {
                        health.IsFallenWithOneDeadLeg = true;
                        health.ResolveCondition();
                        TransitionToWoundedBehavior(health);
                    }
                    else
                    {
                        StopActiveWoundedBehavior();
                        EnterActiveFallenState();
                    }
                    return;
                }
            }
        }

        private void UpdateWoundLocalFromAgent(BodyHealthState health, BodyZone zone, int part, Vector3 worldPoint, Vector3 worldNormal)
        {
            Vector3 localPos = Vector3.zero;
            Vector3 localNrm = Vector3.forward;
            var outLocal = new float[3];
            if (Agent != IntPtr.Zero && nm_agent_part_local_point(Agent, part, ToNmVec(worldPoint), outLocal) == 0)
            {
                localPos = ToUnityVec(outLocal);
                if (localPos.magnitude > 0.22f) localPos = localPos.normalized * 0.10f;
            }
            var q = new float[4];
            if (Agent != IntPtr.Zero && nm_agent_get_part_transform(Agent, part, null, q) >= 0)
            {
                Quaternion partRot = ToUnityQuat(q);
                Vector3 nrm = worldNormal.sqrMagnitude > 1e-4f ? worldNormal.normalized : Vector3.forward;
                localNrm = Quaternion.Inverse(partRot) * nrm;
            }

            health.UpdateWoundLocalAnchor(zone, part, localPos, localNrm);
            if (zone == BodyZone.Stomach || zone == BodyZone.ChestOther || zone == BodyZone.ChestLeft)
            {
                health.SetTorsoWoundLocalAnchor(part, localPos, localNrm);
            }
        }

        private void TickLocationalBleeding()
        {
            if (!config.EnableDyingBleed.Value || Agent == IntPtr.Zero || Actor == null || HealthState == null)
                return;
            if (!HealthState.IsBleeding) return;

            float now = Time.time;
            var wounds = HealthState.ActiveWounds;
            var p = new float[3];
            var q = new float[4];

            for (int i = 0; i < wounds.Count; i++)
            {
                if (!HealthState.TryConsumeBloodPulse(i, now, out ActiveWound w))
                    continue;
                if (nm_agent_get_part_transform(Agent, w.Part, p, q) < 0)
                    continue;

                Vector3 partPos = ToUnityVec(p);
                Quaternion partRot = ToUnityQuat(q);
                Vector3 worldPos = partPos + partRot * w.LocalPos;
                Vector3 worldNrm = (partRot * w.LocalNormal).normalized;

                try
                {
                    Vector3 dropVel = (worldNrm * 0.55f + Vector3.down * 0.65f) + UnityEngine.Random.insideUnitSphere * 0.30f;
                    DecalManager.CreateBloodDrop(worldPos, dropVel, Actor.team);
                    if (w.BleedRate >= 8f || UnityEngine.Random.value < 0.35f)
                    {
                        DecalManager.EmitBloodEffect(worldPos, worldNrm * 0.9f, Actor.team);
                    }
                }
                catch { }
            }
        }

        public void ApplyMelee(DamageInfo info, bool fatal)
        {
            if (IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
            {
                Plugin.PlayroomSync.ForwardMelee(this, info, fatal);
                return;
            }

            bool wasInDyingStagger = Phase == RagdollPhase.DyingStagger;
            bool wasStandingBefore = Phase != RagdollPhase.ActiveFallen &&
                                     Phase != RagdollPhase.DeadSimulating &&
                                     Phase != RagdollPhase.DeadFrozen &&
                                     !Actor.fallenOver;
            bool wantBalanced = !fatal || (config.EnableDyingStagger.Value && !info.isCriticalHit && wasStandingBefore);

            if (!EnsureTakenOver(startBalanced: wantBalanced, alreadyFallen: Actor.fallenOver && !wantBalanced)) return;

            Vector3 dir = info.direction.sqrMagnitude > 1e-4f
                ? info.direction.normalized
                : (info.impactForce.sqrMagnitude > 1e-4f ? info.impactForce.normalized : -Actor.transform.forward);

            Vector3 point = info.point != Vector3.zero ? info.point : (hips != null ? hips.position + Vector3.up * 0.3f : Actor.transform.position + Vector3.up);
            float mag = ConsumeBurstBudget(WeaponForceNormalizer.NormalizeMeleeImpulse(info, config));
            Vector3 impulse = dir * mag;

            if (wasInDyingStagger)
            {
                int part = Mathf.Clamp(nm_agent_nearest_part(Agent, ToNmVec(point)), 0, 20);
                EmitImpactBlood(point, dir, heavy: true);
                TransitionDyingStaggerToCollapse(immediateLimp: true, cancelAgony: info.isCriticalHit || part == 12);
                ApplyNaturalDistributedImpulse(part, point, dir, mag, out _, out _);
                return;
            }

            if (fatal || Actor.dead)
            {
                int part = Mathf.Clamp(nm_agent_nearest_part(Agent, ToNmVec(point)), 0, 20);
                if (ShouldEnterDyingStagger(part, info.isCriticalHit, isExplosion: false, wasStandingBefore))
                {
                    var meleeShot = new NormalizedShot
                    {
                        TotalImpulse = mag,
                        TaskForce = 360f,
                        Power01 = Mathf.Clamp01((mag - 30f) / 60f),
                        WeaponSet = "MELEE",
                        Direction = dir,
                        RawDamage = info.healthDamage,
                        RawBalance = info.balanceDamage,
                        RawForce = info.impactForce.magnitude
                    };
                    StartDyingStagger(meleeShot, part, point, rapidScale: 1f);
                    return;
                }

                MarkDead();
                StartNaturalDeathCollapse();
                ScheduleDeathAgonyIfEligible(part, info.isCriticalHit, isExplosion: false);
                ApplyNaturalDistributedImpulse(part, point, dir, mag, out _, out _);
                if (!deathAgonyScheduled)
                {
                    GoFullyLimp();
                    deadRelaxed = true;
                }
            }
            else
            {
                Vector3 attackerHead = info.sourceActor != null
                    ? info.sourceActor.CenterPosition() + Vector3.up * 0.45f
                    : point - dir * 1.2f + Vector3.up * 0.3f;
                nm_agent_task_melee_hit(Agent, WorldEntry.World, ToNmVec(point), ToNmVec(impulse), ToNmVec(attackerHead), 0);
                staggerElapsed = 0f;
                settledElapsed = 0f;
            }
        }

        public void ApplyExplosion(Vector3 origin, float radius, float rawForceOrSpeed, bool fatal, bool isAlreadyNormalizedSpeed = false)
        {
            if (IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
            {
                Plugin.PlayroomSync.ForwardExplosion(this, origin, radius, rawForceOrSpeed, fatal, isAlreadyNormalizedSpeed);
                return;
            }

            if (Time.frameCount == lastExplosionFrame) return;
            lastExplosionFrame = Time.frameCount;

            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;

            float scaledSpeed = isAlreadyNormalizedSpeed
                ? Mathf.Clamp(rawForceOrSpeed * config.ExplosionForceScale.Value, 5f, config.MaxExplosionSpeed.Value * 1.25f)
                : WeaponForceNormalizer.NormalizeExplosionSpeed(rawForceOrSpeed, config);
            float clampedRadius = Mathf.Clamp(radius, 2f, 25f);
            nm_agent_task_explosion(Agent, WorldEntry.World, ToNmVec(origin), clampedRadius, scaledSpeed);

            if (fatal || Actor.dead)
            {
                MarkDead();
            }
            else
            {
                EnterActiveFallenState();
                staggerElapsed = 0f;
                settledElapsed = 0f;
            }
        }

        public void ApplyFallOver(Vector3 extraForce)
        {
            if (IsSyncLockstepControlled) return;
            bool highFall = Actor.Velocity().y < -5.5f;
            float normMag = extraForce.sqrMagnitude > 0.01f
                ? ConsumeBurstBudget(WeaponForceNormalizer.NormalizeKnockOverImpulse(extraForce.magnitude, config))
                : 0f;

            if (!EnsureTakenOver(startBalanced: !highFall && normMag < 65f, alreadyFallen: true)) return;

            if (normMag > 0.5f)
            {
                Vector3 dir = extraForce.normalized;
                if (dir.y > 0.40f) { dir.y = 0.40f; dir.Normalize(); }
                Vector3 point = hips != null ? hips.position + Vector3.up * 0.3f : Actor.transform.position + Vector3.up;
                ApplyNaturalDistributedImpulse(10, point, dir, normMag, out _, out _);
            }

            if (highFall)
            {
                nm_agent_task_high_fall(Agent, WorldEntry.World, null, IsPlayer ? 1 : 0);
            }
            else if (normMag >= 65f)
            {
                nm_agent_task_catch_fall(Agent, null);
            }

            if (Actor.dead)
                MarkDead();
            else
            {
                EnterActiveFallenState();
                TryTriggerReflexBlindFire(isDeathShot: false);
            }
        }

        public void ApplyHighFall()
        {
            if (IsSyncLockstepControlled) return;
            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;
            nm_agent_task_high_fall(Agent, WorldEntry.World, null, IsPlayer ? 1 : 0);
            if (Actor.dead)
                MarkDead();
            else
            {
                EnterActiveFallenState();
                TryTriggerReflexBlindFire(isDeathShot: false);
            }
        }

        public void ApplyPush(Vector3 impulse, bool balance = true)
        {
            if (IsSyncLockstepControlled && Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
            {
                Plugin.PlayroomSync.ForwardPush(this, impulse);
                return;
            }

            if (!EnsureTakenOver(startBalanced: balance, alreadyFallen: false)) return;
            Vector3 at = hips != null ? hips.position + Vector3.up * 0.35f * scale : Actor.transform.position + Vector3.up;
            nm_agent_apply_push(Agent, ToNmVec(at), ToNmVec(impulse));
            staggerElapsed = 0f;
            settledElapsed = 0f;
            aliveLeanArmed = true;
        }

        public void ApplyNearMiss(Vector3 from, Vector3 dir)
        {
            if (IsSyncLockstepControlled) return;
            if (Actor == null || Actor.dead || IsPlayer || Actor.IsSeated()) return;
            if (Time.time - lastNearMissTime < 1.25f) return;
            lastNearMissTime = Time.time;

            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;
            nm_agent_near_miss(Agent, WorldEntry.World, ToNmVec(from), ToNmVec(dir.normalized));
            staggerElapsed = 0f;
            settledElapsed = 0f;
            aliveLeanArmed = true;
        }

        public void ApplyDeath(DamageInfo info)
        {
            if (IsSyncLockstepControlled)
            {
                if (Plugin.PlayroomSync != null && Plugin.PlayroomSync.IsConnected)
                    Plugin.PlayroomSync.ForwardShot(this, info, fatal: true, null, HealthState);
                return;
            }
            if (info.type == DamageInfo.DamageSourceType.Explosion || info.type == DamageInfo.DamageSourceType.StickyExplosive)
            {
                float rawF = info.impactForce.magnitude > 1f ? info.impactForce.magnitude : (info.healthDamage * 4f);
                ApplyExplosion(info.point != Vector3.zero ? info.point : Actor.CenterPosition(), 6f, rawF, fatal: true, isAlreadyNormalizedSpeed: false);
                return;
            }
            else if (info.type == DamageInfo.DamageSourceType.Projectile || info.type == DamageInfo.DamageSourceType.ProjectileShatter)
            {
                ApplyShot(info, fatal: true);
                return;
            }
            else if (info.type == DamageInfo.DamageSourceType.Melee || info.isKick)
            {
                ApplyMelee(info, fatal: true);
                return;
            }

            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;

            if (info.impactForce.sqrMagnitude > 0.1f)
            {
                Vector3 at = info.point != Vector3.zero ? info.point : (hips != null ? hips.position + Vector3.up * 0.25f : Actor.CenterPosition());
                float mag = ConsumeBurstBudget(WeaponForceNormalizer.NormalizeKnockOverImpulse(info.impactForce.magnitude, config));
                int part = Mathf.Clamp(nm_agent_nearest_part(Agent, ToNmVec(at)), 0, 20);
                ApplyNaturalDistributedImpulse(part, at, info.impactForce.normalized, mag, out _, out _);
                MarkDead();
                GoFullyLimp();
                deadRelaxed = true;
                return;
            }
            else
            {
                // Silent/scripted death (zero bullet/explosion force): add a tiny natural slump push so they crumple instead of falling straight like a statue
                Vector3 slumpDir = (-Actor.transform.forward + Actor.transform.right * UnityEngine.Random.Range(-0.45f, 0.45f)).normalized;
                Vector3 at = hips != null ? hips.position + Vector3.up * 0.35f : Actor.CenterPosition();
                MarkDead();
                StartNaturalDeathCollapse();
                ScheduleDeathAgonyIfEligible(10, isCriticalOrHeadshot: false, isExplosion: false);
                ApplyNaturalDistributedImpulse(10, at, slumpDir, 12f, out _, out _);
                if (!deathAgonyScheduled)
                {
                    GoFullyLimp();
                    deadRelaxed = true;
                }
            }
        }

        public void MarkDead(bool keepEntryBlend = false, bool keepEnvReactions = false)
        {
            if (!blindFirePendingStart && !blindFireActive)
                RestoreWeaponParentTransform();
            ClearIncomingArmFollow();
            if (!keepEntryBlend)
                entryBlendDuration = 0f;
            if (Agent != IntPtr.Zero)
            {
                if (keepEnvReactions)
                {
                    nm_agent_set_dead(Agent, 0);
                    ApplyEnvironmentReactionsBitmask(enabled: true);
                }
                else
                {
                    nm_agent_set_dead(Agent, 1);
                    nm_agent_set_reactions(Agent, 0);
                }
            }
            if (Phase != RagdollPhase.DeadSimulating)
            {
                deadElapsed = 0f;
                deadStillElapsed = 0f;
                deadRelaxed = false;
                deadAnchorElapsed = 0f;
                deadAnchorPos = lastComPos;
                if (!keepEnvReactions)
                {
                    dyingCollapseActive = false;
                    ResetDeathAgonyState();
                }
            }
            Phase = RagdollPhase.DeadSimulating;
        }

        public void SendTextReaction(string line)
        {
            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;
            nm_msg_send_text(Agent, WorldEntry.World, line);
        }

        public void TriggerElectrocute(float seconds = 3.0f)
        {
            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;
            nm_agent_tase(Agent, WorldEntry.World, seconds);
        }

        public void TriggerRelax()
        {
            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;
            GoFullyLimp();
        }

        private void StartNaturalDeathCollapse()
        {
            if (Agent == IntPtr.Zero || WorldEntry == null) return;
            // Stage 1 (0..0.30s): holdPose=true preserves the exact gun-holding / staggered joint angles
            // (NEVER pulling toward [0,0,0] A-pose!) at low fading muscle tone with zero oppose_gravity
            // so the bullet impulse whips the hit zone cleanly and gravity buckles the body naturally.
            nm_agent_task_stop(Agent);
            nm_msg_send_text(Agent, WorldEntry.World, "stopAllBehaviours");
            nm_msg_send_text(Agent, WorldEntry.World, "bodyRelax relaxation=88 damping=0.18 holdPose=true");
        }

        private void GoFullyLimp()
        {
            if (Agent == IntPtr.Zero || WorldEntry == null) return;
            // Stage 2: relax 99% with low damping (0.12x) and holdPose=true so even the 0.01% residual
            // stiffness never pulls limbs back toward A-pose; body drapes completely naturally on the floor.
            dyingCollapseActive = false;
            nm_agent_task_stop(Agent);
            nm_msg_send_text(Agent, WorldEntry.World, "stopAllBehaviours");
            nm_msg_send_text(Agent, WorldEntry.World, "bodyRelax relaxation=99 damping=0.12 holdPose=true");
            if (Actor != null && Actor.dead)
            {
                nm_agent_set_dead(Agent, 1);
                nm_agent_set_reactions(Agent, 0);
            }
        }

        public void TriggerFlinch(Vector3 from)
        {
            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;
            nm_agent_task_flinch(Agent, WorldEntry.World, null, ToNmVec(from));
        }

        public void UpdateLogic(float dt)
        {
            if (Actor == null)
            {
                ReleaseImmediate(restoreGame: false);
                return;
            }

            if (Phase == RagdollPhase.BlendingToStand)
            {
                blendTimer += dt;
                if (blendTimer >= blendDuration)
                {
                    animBlendBones = null;
                    Phase = RagdollPhase.Animated;
                }
                return;
            }

            if (!IsSimulating) return;

            if (IsSyncLockstepControlled)
            {
                var pArr = new float[3];
                var vArr = new float[3];
                nm_agent_get_com(Agent, pArr, vArr);
                lastComPos = ToUnityVec(pArr) + SyncVisualOffset;
                lastComVel = ToUnityVec(vArr);
                return;
            }

            // Decay burst impulse budget swiftly (~0.22s window) so rapid gunfire keeps natural punch on every round
            if (burstImpulseUsed > 0f)
            {
                float decayRate = Mathf.Max(config.MaxBurstImpulse.Value, 80f) / 0.22f;
                burstImpulseUsed = Mathf.Max(0f, burstImpulseUsed - decayRate * dt);
            }

            // Keep alive/dead state synced with Ravenfield (preserve DyingStagger while the bot clutches wound & stumbles!)
            if (Actor.dead && Phase != RagdollPhase.DeadSimulating && Phase != RagdollPhase.DyingStagger)
            {
                MarkDead();
            }

            // Read COM position & velocity for state logic and diagnostics
            var posArr = new float[3];
            var velArr = new float[3];
            nm_agent_get_com(Agent, posArr, velArr);
            lastComPos = ToUnityVec(posArr);
            lastComVel = ToUnityVec(velArr);

            // Re-anchor Delirium static world geometry if the ragdoll moved far from its spawn world (e.g. blasted / dragged / hill tumble)
            worldReanchorCooldown -= dt;
            if (worldReanchorCooldown <= 0f)
            {
                worldReanchorCooldown = 0.5f;
                ReanchorWorldIfNeeded();
            }

            // Sync Ravenfield WaterLevel -> Delirium buoyancy
            if (config.EnableWaterBuoyancy.Value && WaterLevel.instance != null)
            {
                if (WaterLevel.IsInWater(lastComPos, out float depth))
                {
                    nm_agent_set_water(Agent, WorldEntry.World, lastComPos.y + depth);
                }
                else
                {
                    nm_agent_set_water(Agent, WorldEntry.World, float.NaN);
                }
            }

            // Smoothly fade upper-arm gun-holding carry-through so arms peel off the rifle fluidly into wound reaction
            if (hasIncomingArmFollow)
            {
                armFollowTimer += dt;
                if (armFollowTimer >= armFollowDuration || Phase != RagdollPhase.ActiveStagger)
                {
                    ClearIncomingArmFollow();
                }
                else
                {
                    float u = Mathf.SmoothStep(0f, 1f, armFollowTimer / armFollowDuration);
                    nm_agent_follow(Agent, "ua", 0.72f * (1f - u));
                }
            }

            // Pulsing wound bleeding during DyingStagger & early DeadSimulating (mirrors Playroom wounds_frame)
            if (dyingWoundPart >= 0 && (Phase == RagdollPhase.DyingStagger || Phase == RagdollPhase.DeadSimulating))
            {
                TickWoundBleeding(dt);
            }

            // Startled reflex blind-fire discharge ("ปืนลั่น") during stagger, sudden fall, or dying collapse
            if (blindFireActive || blindFirePendingStart)
            {
                TickBlindFire(dt);
            }

            // Wounded standing (1-leg hop, stomach/chest clutch, disabled off-hand), crawling (1/2-leg, fallen stomach), and surrendering
            if (Phase == RagdollPhase.WoundedStanding || Phase == RagdollPhase.WoundedCrawling || Phase == RagdollPhase.Surrendering)
            {
                UpdateWoundedPhaseLogic(dt);
                return;
            }

            // DyingStagger: bot is fatally wounded, clutching wound, bleeding, staggering & bracing walls before collapsing
            if (Phase == RagdollPhase.DyingStagger)
            {
                dyingStaggerElapsed += dt;
                // Keep native dead=0 during DyingStagger so NM_XR_WALL, NM_XR_LEDGE, rollDownStairs & ShotTask stay alive!
                if (nm_agent_is_dead(Agent) == 1)
                    nm_agent_set_dead(Agent, 0);

                // Re-arm leanInDirection after tick 1 (nm.rs clears self.lean when shot first activates dbm!)
                // so the dying soldier genuinely stumbles 2-4 steps backwards in the bullet direction while clutching the wound.
                if (!dyingLeanArmed && dyingStaggerElapsed >= 0.04f && !dyingRelaxTriggered)
                {
                    dyingLeanArmed = true;
                    Vector3 flatDir = Vector3.ProjectOnPlane(dyingStaggerDir, Vector3.up);
                    if (flatDir.sqrMagnitude > 1e-4f)
                    {
                        flatDir.Normalize();
                        var icL = System.Globalization.CultureInfo.InvariantCulture;
                        nm_msg_send_text(Agent, WorldEntry.World, string.Format(icL,
                            "leanInDirection start=true applyAsForce=true dir={0:F3},{1:F3},0 leanAmount={2:F2}",
                            flatDir.x, flatDir.z, dyingLeanAmount));
                    }
                }

                // Mid-stagger muscle fade: after clutching & staggering upright for ~45% of the window,
                // drop stayUpright and trigger native shotRelax so knees buckle and body collapses naturally.
                if (!dyingRelaxTriggered && dyingStaggerElapsed >= dyingStaggerDuration * 0.45f)
                {
                    dyingRelaxTriggered = true;
                    float rem = Mathf.Max(dyingStaggerDuration - dyingStaggerElapsed, 0.45f);
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    nm_msg_send_text(Agent, WorldEntry.World, "stayUpright start=false");
                    nm_msg_send_text(Agent, WorldEntry.World, string.Format(ic, "shotRelax relaxPeriod={0:F2}", rem));
                }

                bool dyingFallen = nm_agent_has_fallen(Agent) == 1;
                bool dyingBal = nm_agent_is_balancing(Agent) == 1;
                bool expired = dyingStaggerElapsed >= dyingStaggerDuration;
                bool collapsedLow = dyingStaggerElapsed >= 0.40f && !dyingBal && lastComVel.y < -0.5f;

                if (dyingFallen || expired || collapsedLow)
                {
                    TransitionDyingStaggerToCollapse(immediateLimp: false, cancelAgony: false);
                }
                return;
            }

            // Dead corpse dying collapse, agony, relaxation & settling check (frees LOD slot once limp and settled)
            if (Phase == RagdollPhase.DeadSimulating)
            {
                deadElapsed += dt;
                bool deadFallen = nm_agent_has_fallen(Agent) == 1;

                // Muscle-driven dying collapse phase: weakened catchFall + NM_XR_WALL/LEDGE/stairs active while going down
                if (dyingCollapseActive)
                {
                    bool hitFloor = (deadElapsed >= 0.45f && (deadFallen || lastComVel.magnitude < 1.25f)) || deadElapsed >= 0.90f;
                    if (!hitFloor)
                    {
                        return;
                    }
                    dyingCollapseActive = false;
                }

                // Optional dying movement before expiring (visible writhe / curl / weak reach on the ground)
                if (deathAgonyScheduled && !deathAgonyDone)
                {
                    if (!deathAgonyActive)
                    {
                        bool landed = (deadElapsed >= 0.45f && (deadFallen || lastComVel.magnitude < 1.35f)) || deadElapsed >= 0.75f;
                        if (landed)
                        {
                            deathAgonyActive = true;
                            deathAgonyElapsed = 0f;
                            // Fade once at halfway so the writhe wave runs uninterrupted through its full cycle
                            deathAgonyNextUpdate = deathAgonyDuration * 0.55f;
                            ApplyDeathAgonyWave(0f);
                            if (config.DebugLog.Value)
                                Plugin.Log.LogInfo($"[Delirium] Death agony started for {Actor.name} (style={deathAgonyStyle}, dur={deathAgonyDuration:F2}s)");
                        }
                        return;
                    }

                    deathAgonyElapsed += dt;
                    if (deathAgonyElapsed >= deathAgonyDuration)
                    {
                        // Final breath finished: smoothly drain all remaining muscle tension into full limp
                        CancelDeathAgonyAndGoLimp();
                    }
                    else
                    {
                        if (deathAgonyElapsed >= deathAgonyNextUpdate)
                        {
                            deathAgonyNextUpdate = deathAgonyDuration + 1f;
                            ApplyDeathAgonyWave(0.65f);
                        }
                        return;
                    }
                }

                // Transition from Stage 1 dying crumple into Stage 2 full 99% floppy limp
                if (!deadRelaxed && ((deadFallen && deadElapsed >= 0.25f) || deadElapsed >= 0.32f))
                {
                    GoFullyLimp();
                    deadRelaxed = true;
                    deadAnchorPos = lastComPos;
                    deadAnchorElapsed = 0f;
                    deadStillElapsed = 0f;
                }

                float hardFreezeLimit = 6.0f + (deathAgonyDone ? deathAgonyDuration : 0f);
                if (deadRelaxed)
                {
                    bool resting = nm_agent_is_resting(Agent, WorldEntry.World) == 1;
                    if (resting || lastComVel.magnitude < config.FreezeDeadVelocityThreshold.Value)
                        deadStillElapsed += dt;
                    else
                        deadStillElapsed = 0f;

                    // Displacement-based settling: freezes corpses cleanly even if contact micro-jitter spikes instantaneous v
                    if ((lastComPos - deadAnchorPos).sqrMagnitude < 0.08f * 0.08f)
                    {
                        deadAnchorElapsed += dt;
                    }
                    else
                    {
                        deadAnchorPos = lastComPos;
                        deadAnchorElapsed = 0f;
                    }

                    if (resting ||
                        deadStillElapsed >= config.FreezeDeadAfterSeconds.Value ||
                        deadAnchorElapsed >= 1.4f ||
                        deadElapsed >= hardFreezeLimit)
                    {
                        FreezeDeadCorpse();
                    }
                }
                else if (deadElapsed >= hardFreezeLimit)
                {
                    FreezeDeadCorpse();
                }
                return;
            }

            // Alive bot/player state transitions
            bool hasFallen = nm_agent_has_fallen(Agent) == 1;
            bool balancing = nm_agent_is_balancing(Agent) == 1;
            bool settled = nm_agent_is_settled(Agent) == 1;

            if (Phase == RagdollPhase.ActiveStagger)
            {
                staggerElapsed += dt;
                if (!aliveLeanArmed && staggerElapsed >= 0.04f && staggerElapsed < 0.25f)
                {
                    aliveLeanArmed = true;
                    Vector3 flatDir = Vector3.ProjectOnPlane(aliveStaggerDir, Vector3.up);
                    if (flatDir.sqrMagnitude > 1e-4f)
                    {
                        flatDir.Normalize();
                        var icL = System.Globalization.CultureInfo.InvariantCulture;
                        nm_msg_send_text(Agent, WorldEntry.World, string.Format(icL,
                            "leanInDirection start=true applyAsForce=true dir={0:F3},{1:F3},0 leanAmount={2:F2}",
                            flatDir.x, flatDir.z, aliveLeanAmount));
                    }
                }
                if (hasFallen)
                {
                    pendingWoundedTransitionTimer = 0f;
                    if (HealthState != null && HealthState.IsWounded)
                    {
                        if (HealthState.Condition == WoundedCondition.HoppingOneLeg)
                        {
                            HealthState.IsFallenWithOneDeadLeg = true;
                            HealthState.ResolveCondition();
                        }
                        if (HealthState.Condition == WoundedCondition.CrawlingOneLeg ||
                            HealthState.Condition == WoundedCondition.CrawlingTwoLegs ||
                            HealthState.Condition == WoundedCondition.FallenStomachClutch ||
                            HealthState.Condition == WoundedCondition.SurrenderingGround)
                        {
                            TransitionToWoundedBehavior(HealthState);
                            return;
                        }
                    }
                    EnterActiveFallenState();
                    return;
                }

                // After a rapid-fire burst pauses while still standing wounded, smoothly enter WoundedStanding!
                if (pendingWoundedTransitionTimer > 0f)
                {
                    pendingWoundedTransitionTimer -= dt;
                    if (pendingWoundedTransitionTimer <= 0f && HealthState != null && HealthState.IsWounded)
                    {
                        TransitionToWoundedBehavior(HealthState);
                        return;
                    }
                }

                bool standingStill = (settled || (balancing && lastComVel.magnitude < 0.35f)) && staggerElapsed > 0.35f;
                if (standingStill)
                    settledElapsed += dt;
                else
                    settledElapsed = 0f;

                if (settledElapsed >= config.StaggerRecoverSeconds.Value ||
                    staggerElapsed >= config.MaxStaggerTimeBeforeForceRecover.Value)
                {
                    BeginBlendBackToStanding();
                }
            }
            else if (Phase == RagdollPhase.ActiveFallen)
            {
                // Make sure Rust never considers an alive fallen actor dead
                if (nm_agent_is_dead(Agent) == 1)
                    nm_agent_set_dead(Agent, 0);

                fallenElapsed += dt;

                // If somehow recovered onto feet without falling, allow stand recovery
                if (!hasFallen && settled && !wasFallenBeforeTakeover)
                {
                    BeginBlendBackToStanding();
                    return;
                }

                // After ~0.9s on the floor, stop lingering shot/writhe tasks so limbs settle cleanly for get-up
                if (!fallenTaskStopped && fallenElapsed >= 0.9f)
                {
                    nm_agent_task_stop(Agent);
                    fallenTaskStopped = true;
                }

                float stillTarget = config.LyingStillBeforeGetUp.Value;
                if (lastComVel.magnitude < 0.28f)
                    fallenStillElapsed += dt;
                else
                    fallenStillElapsed = 0f;

                if ((lastComPos - fallenAnchorPos).sqrMagnitude < 0.12f * 0.12f)
                {
                    fallenAnchorElapsed += dt;
                }
                else
                {
                    fallenAnchorPos = lastComPos;
                    fallenAnchorElapsed = 0f;
                }

                // Hand back to Ravenfield native GetUp animation once lying still or failsafe timer elapses
                if (nm_agent_should_get_up(Agent, stillTarget) == 1 ||
                    fallenStillElapsed >= stillTarget ||
                    (fallenElapsed >= 1.1f && fallenAnchorElapsed >= stillTarget) ||
                    fallenElapsed >= config.MaxFallenBeforeGetUp.Value)
                {
                    HandBackToRavenfieldGetUp();
                }
            }
        }

        private void EnterActiveFallenState()
        {
            if (!blindFireActive)
                RestoreWeaponParentTransform();
            ClearIncomingArmFollow();
            entryBlendDuration = 0f;
            Phase = RagdollPhase.ActiveFallen;
            fallenElapsed = 0f;
            fallenStillElapsed = 0f;
            fallenAnchorPos = lastComPos != Vector3.zero ? lastComPos : (hips != null ? hips.position : Actor.transform.position);
            fallenAnchorElapsed = 0f;
            fallenTaskStopped = false;

            if (Actor == null || Actor.dead) return;
            if (!Actor.fallenOver)
            {
                Actor.fallenOver = true;
                Actor.wantsToGetUp = false;
                if (Actor.animator != null)
                {
                    Actor.animator.SetBool(AnimParRagdolled, true);
                    Actor.animator.SetLayerWeight(RagdollAnimLayer, 1f);
                }
                try { Actor.controller?.StartRagdoll(); } catch { }
                if (!blindFireActive && Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
                    Actor.activeWeapon.gameObject.SetActive(false);
            }
        }

        public void ApplyPoseLateUpdate()
        {
            if (Phase == RagdollPhase.BlendingToStand)
            {
                ApplyStandingBlendLateUpdate();
                return;
            }

            if (!IsSimulating || rig == IntPtr.Zero || Agent == IntPtr.Zero || bones == null || bones.Length == 0)
                return;

            int hipsIdx = nm_rig_pose(rig, Agent, poseBuf);
            if (hipsIdx < 0 || hipsIdx >= bones.Length) return;

            bool blendingEntry = (Phase == RagdollPhase.ActiveStagger || Phase == RagdollPhase.DyingStagger || Phase == RagdollPhase.DeadSimulating) &&
                                 entryBlendDuration > 0.005f &&
                                 entryBlendTimer < entryBlendDuration &&
                                 entryStartLocalRot != null &&
                                 entryStartLocalRot.Length == bones.Length;

            if (blendingEntry)
            {
                entryBlendTimer += Time.deltaTime;
                float tArm = Mathf.Clamp01(entryBlendTimer / entryBlendDuration);
                float tCore = Mathf.Clamp01(entryBlendTimer / Mathf.Max(entryBlendDuration * 0.62f, 0.01f));
                // Core/hips respond immediately (ease-out quad) while arms/shoulders peel smoothly off the rifle pose
                float kCore = 1f - (1f - tCore) * (1f - tCore);
                float kArm = Mathf.SmoothStep(0f, 1f, tArm);

                if (bones[0] != null)
                    bones[0].rotation = Quaternion.Slerp(entryStartRootRot, ToUnityQuat(poseBuf, 0), kCore);

                for (int b = 1; b < bones.Length; b++)
                {
                    if (bones[b] != null)
                    {
                        float k = (boneIsUpperLimb != null && boneIsUpperLimb[b]) ? kArm : kCore;
                        bones[b].localRotation = Quaternion.Slerp(entryStartLocalRot[b], ToUnityQuat(poseBuf, b * 4), k);
                    }
                }
            }
            else
            {
                if (bones[0] != null)
                    bones[0].rotation = ToUnityQuat(poseBuf, 0);

                for (int b = 1; b < bones.Length; b++)
                {
                    if (bones[b] != null)
                        bones[b].localRotation = ToUnityQuat(poseBuf, b * 4);
                }
            }

            Vector3 hipsWorldPos = ToUnityVec(poseBuf, bones.Length * 4);
            if (IsSyncLockstepControlled)
                hipsWorldPos += SyncVisualOffset;
            if (bones[hipsIdx] != null)
                bones[hipsIdx].position = hipsWorldPos;

            // Keep Actor's root synchronized horizontally for non-dead/player camera tracking without snapping ragdollObject
            if (Actor != null && Actor.ragdoll != null && Actor.ragdoll.ragdollObject != null)
            {
                // Keep hipRigidbody position synced (it is kinematic)
                if (Actor.hipRigidbody != null && Actor.hipRigidbody.transform != bones[hipsIdx])
                {
                    Actor.hipRigidbody.transform.position = hipsWorldPos;
                }
            }

            // Keep bot's active weapon glued to the ragdoll hand while staggering, wounded-firing, or reflex blind-firing
            bool holdWeaponOnHand = Phase == RagdollPhase.ActiveStagger ||
                                    Phase == RagdollPhase.WoundedStanding ||
                                    Phase == RagdollPhase.WoundedCrawling ||
                                    blindFireActive;
            if (holdWeaponOnHand && weaponAttachedToRagdoll &&
                trackedWeaponParent != null && weaponRagdollHand != null)
            {
                trackedWeaponParent.position = weaponRagdollHand.TransformPoint(weaponHandOffsetPos);
                trackedWeaponParent.rotation = weaponRagdollHand.rotation * weaponHandOffsetRot * (blindFireActive ? blindFireTwitchRot : Quaternion.identity);
            }
        }

        public void SyncEnginePhysicsFixedUpdate()
        {
            if (!IsSimulating || Agent == IntPtr.Zero) return;

            if (config.EnableEnginePhysicsColliders.Value)
            {
                if (!enginePhysicsProxy.IsActive && build != IntPtr.Zero)
                    enginePhysicsProxy.Build(Actor, Agent, build, scale);
                enginePhysicsProxy.SyncFixedUpdate(Agent);
            }
            else if (enginePhysicsProxy.IsActive)
            {
                enginePhysicsProxy.Dispose();
            }
        }

        private void BeginBlendBackToStanding()
        {
            if (Actor == null || Actor.ragdoll == null)
            {
                ReleaseImmediate(restoreGame: true);
                return;
            }

            // Snapshot current Delirium ragdoll bone orientations mapped to animated transforms
            Vector3 hipPos = hips != null ? hips.position : Actor.transform.position;
            float groundY = runtime.SampleGroundHeight(hipPos, Actor.transform, Actor.transform.position.y);
            Vector3 forward = hips != null ? Vector3.ProjectOnPlane(hips.forward, Vector3.up) : Actor.transform.forward;
            if (forward.sqrMagnitude < 1e-4f) forward = Actor.transform.forward;

            var ragdollRotMap = new Dictionary<Transform, Quaternion>();
            Transform animHips = null;
            Quaternion startHipRot = hips != null ? hips.rotation : Quaternion.identity;

            try
            {
                animHips = Actor.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Hips);
            }
            catch { }

            if (bones != null)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    Transform animBone = null;
                    try { animBone = Actor.ragdoll.RagdollToAnimated(bones[i]); } catch { }
                    if (animBone != null)
                        ragdollRotMap[animBone] = bones[i].localRotation;
                }
            }

            // Release Delirium native state and switch ActiveRaggy back to animated
            StopBlindFire(dropIfDead: false);
            RestoreWeaponParentTransform();
            ReleaseNativeOnly();
            RestoreRagdollRigidbodies();

            Actor.SetPositionAndRotation(new Vector3(hipPos.x, groundY, hipPos.z), Quaternion.LookRotation(forward.normalized, Vector3.up));
            if (Actor.ragdoll.transform != null && Actor.ragdoll.transform != Actor.transform)
            {
                Actor.ragdoll.transform.localPosition = Vector3.zero;
                Actor.ragdoll.transform.localRotation = Quaternion.identity;
            }
            Actor.ragdoll.InstantAnimate();
            Actor.fallenOver = false;
            Actor.wantsToGetUp = false;
            if (Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
                Actor.activeWeapon.gameObject.SetActive(true);

            // Set up smooth post-animation blend over GetUpBlendSeconds
            if (animHips != null && ragdollRotMap.Count > 0)
            {
                animBlendBones = new Transform[ragdollRotMap.Count];
                animBlendStartRot = new Quaternion[ragdollRotMap.Count];
                int idx = 0;
                foreach (var kv in ragdollRotMap)
                {
                    animBlendBones[idx] = kv.Key;
                    animBlendStartRot[idx] = kv.Value;
                    idx++;
                }
                animBlendStartHipPos = hipPos;
                animBlendStartHipRot = startHipRot;
                blendTimer = 0f;
                blendDuration = Mathf.Max(config.GetUpBlendSeconds.Value, 0.05f);
                Phase = RagdollPhase.BlendingToStand;
            }
            else
            {
                Phase = RagdollPhase.Animated;
            }
        }

        private void ApplyStandingBlendLateUpdate()
        {
            if (animBlendBones == null || Actor == null || Actor.dead)
            {
                animBlendBones = null;
                Phase = RagdollPhase.Animated;
                return;
            }

            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(blendTimer / blendDuration));
            for (int i = 0; i < animBlendBones.Length; i++)
            {
                var b = animBlendBones[i];
                if (b == null) continue;
                b.localRotation = Quaternion.Slerp(animBlendStartRot[i], b.localRotation, k);
            }
        }

        public void HandBackToRavenfieldGetUp()
        {
            if (Actor == null || Actor.ragdoll == null)
            {
                ReleaseImmediate(restoreGame: true);
                return;
            }

            bool faceUp = false;
            Vector3 desiredFacing = Actor.transform.forward;
            if (Agent != IntPtr.Zero)
            {
                var headArr = new float[3];
                nm_agent_get_lying(Agent, headArr, out int faceUpInt);
                faceUp = faceUpInt == 1;
                Vector3 headDir = ToUnityVec(headArr);
                Vector3 cand = headDir * (faceUp ? -1f : 1f);
                cand.y = 0f;
                if (cand.sqrMagnitude > 1e-4f) desiredFacing = cand.normalized;
            }

            Vector3 hipPos = hips != null ? hips.position : Actor.transform.position;
            float groundY = runtime.SampleGroundHeight(hipPos + Vector3.up * 0.35f, Actor.transform, Actor.transform.position.y);

            // Release Delirium FIRST and restore non-kinematic rigidbodies with zero velocity
            StopBlindFire(dropIfDead: false);
            RestoreWeaponParentTransform();
            ReleaseNativeOnly();
            RestoreRagdollRigidbodies();
            Phase = RagdollPhase.Animated;

            if (Actor.animator != null)
            {
                Actor.animator.SetBool(AnimParRagdolled, true);
                Actor.animator.SetBool(AnimParOnBack, faceUp);
                Actor.animator.SetLayerWeight(RagdollAnimLayer, 1f);
            }

            Actor.fallenOver = true;
            Actor.wantsToGetUp = false;

            // Invoke Ravenfield's native GetUp FIRST (before enabling standing hitboxes), then immediately enforce
            // exact groundY + facing so Actor.GetUp()'s short 1m raycast & SyncAnimatorPosition never leave the bot floating!
            ActorGetUpMethod?.Invoke(Actor, null);

            Transform ragObjTf = Actor.ragdoll.ragdollObject != null ? Actor.ragdoll.ragdollObject.transform : null;
            Transform origParent = ragObjTf != null ? ragObjTf.parent : null;
            if (ragObjTf != null) ragObjTf.SetParent(null, true);

            Actor.SetPositionAndRotation(new Vector3(hipPos.x, groundY, hipPos.z), Quaternion.LookRotation(desiredFacing, Vector3.up));
            if (Actor.ragdoll.transform != null && Actor.ragdoll.transform != Actor.transform)
            {
                Actor.ragdoll.transform.localPosition = Vector3.zero;
                Actor.ragdoll.transform.localRotation = Quaternion.identity;
            }

            if (ragObjTf != null) ragObjTf.SetParent(origParent, true);

            if (config.DebugLog.Value)
                Plugin.Log.LogInfo($"[Delirium] HandBackToRavenfieldGetUp {Actor.name} (faceUp={faceUp}, groundY={groundY:F2}, fallenElapsed={fallenElapsed:F2}s)");
        }

        public void FreezeDeadCorpse()
        {
            if (Phase == RagdollPhase.DeadFrozen) return;
            StopBlindFire(dropIfDead: true);
            // Keep ragdollObject & ragdollColliders enabled in their final Delirium pose so bullets/explosions/cars
            // can still hit and wake this corpse back into active simulation, while freeing native Rust memory!
            EnsureRagdollCollidersEnabled();
            Physics.SyncTransforms();
            ReleaseNativeOnly();
            Phase = RagdollPhase.DeadFrozen;
        }

        public void ReleaseImmediate(bool restoreGame)
        {
            ClearPartialKeyframe();
            StopBlindFire(dropIfDead: Actor != null && Actor.dead);
            woundedCombat.StopPointArm(Agent, WorldEntry != null ? WorldEntry.World : IntPtr.Zero);
            woundedCombat.RestoreActorFlags(HealthState);
            woundedCombat.Reset();
            activeWoundedBehavior = null;
            lastAppliedMuscleStrength = -1f;
            RestoreWeaponParentTransform();
            ReleaseNativeOnly();
            RestoreRagdollRigidbodies();
            animBlendBones = null;
            BracingVehicleIndex = -1;

            if (restoreGame && Actor != null && Actor.ragdoll != null && !Actor.dead && Actor.ragdoll.IsRagdoll())
            {
                if (!Actor.fallenOver)
                {
                    Actor.ragdoll.InstantAnimate();
                }
            }

            Phase = RagdollPhase.Animated;
        }

        public string GetDiagnosticSummary()
        {
            if (Agent == IntPtr.Zero) return $"Phase={Phase}";
            bool bal = nm_agent_is_balancing(Agent) == 1;
            bool fall = nm_agent_has_fallen(Agent) == 1;
            bool cf = nm_agent_is_catching_fall(Agent) == 1;
            bool dead = nm_agent_is_dead(Agent) == 1;
            int contacts = 0;
            for (int k = 0; k < 21; k++)
                if (nm_agent_get_part_contact(Agent, k) != 0) contacts++;
            string dyingTag = Phase == RagdollPhase.DyingStagger
                ? $" Stagger({dyingStaggerElapsed:F1}/{dyingStaggerDuration:F1}s w={dyingWoundPart} h={dyingFollowUpHits})"
                : "";
            string woundTag = !string.IsNullOrEmpty(activeWoundedBehavior) ? $" Beh={activeWoundedBehavior}" : "";
            string agonyTag = deathAgonyActive ? $" Agony=S{deathAgonyStyle}({deathAgonyElapsed:F1}/{deathAgonyDuration:F1}s)" : "";
            return $"Phase={Phase}{woundTag}{dyingTag}{agonyTag} | Bal={bal} Fall={fall} Catch={cf} Cnt={contacts} Dead={dead} | v={lastComVel.magnitude:F2}m/s";
        }

        public string RunSetupCheck()
        {
            if (Agent == IntPtr.Zero) return LastRigReport;
            string agentCheck = Str((b, n) => nm_agent_check(Agent, b, n)) ?? "";
            if (string.IsNullOrEmpty(LastRigReport)) return string.IsNullOrEmpty(agentCheck) ? "OK (no issues)" : agentCheck;
            if (string.IsNullOrEmpty(agentCheck)) return LastRigReport;
            return LastRigReport + "\n" + agentCheck;
        }

        private void FreezeRagdollRigidbodies()
        {
            kinematicBodies.Clear();
            if (Actor?.ragdoll?.rigidbodies == null) return;
            foreach (var rb in Actor.ragdoll.rigidbodies)
            {
                if (rb == null) continue;
                if (!rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = true;
                    rb.Sleep();
                    kinematicBodies.Add(rb);
                }
            }
        }

        private void RestoreRagdollRigidbodies()
        {
            for (int i = 0; i < kinematicBodies.Count; i++)
            {
                var rb = kinematicBodies[i];
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.Sleep();
                }
            }
            kinematicBodies.Clear();
        }

        private void CaptureWeaponForRagdollHand(bool allowDeadWeapon = false)
        {
            RestoreWeaponParentTransform();
            if (Actor == null || IsPlayer || (!allowDeadWeapon && Actor.dead) || Actor.ragdoll == null || Actor.controller == null)
                return;

            Transform wp = null;
            try { wp = Actor.controller.WeaponParent(); } catch { }
            if (wp == null) return;

            // Walk up from weaponParent to find the animated hand bone that maps to a ragdoll bone
            Transform animBone = null;
            Transform ragBone = null;
            for (Transform cur = wp.parent; cur != null; cur = cur.parent)
            {
                try { ragBone = Actor.ragdoll.AnimatedToRagdoll(cur); } catch { ragBone = null; }
                if (ragBone != null)
                {
                    animBone = cur;
                    break;
                }
            }

            if (ragBone == null)
            {
                try
                {
                    ragBone = Actor.ragdoll.HumanBoneTransformRagdoll(HumanBodyBones.RightHand);
                    animBone = Actor.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.RightHand);
                }
                catch { }
            }

            if (ragBone == null || animBone == null)
            {
                // Fallback if a custom skin lacks a mapped hand bone: hide active weapon during stagger so it never floats
                if (Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null && Actor.activeWeapon.gameObject.activeSelf)
                {
                    Actor.activeWeapon.gameObject.SetActive(false);
                    weaponHiddenByFallback = true;
                }
                return;
            }

            trackedWeaponParent = wp;
            weaponRagdollHand = ragBone;
            weaponParentSavedLocalPos = wp.localPosition;
            weaponParentSavedLocalRot = wp.localRotation;
            weaponHandOffsetPos = animBone.InverseTransformPoint(wp.position);
            weaponHandOffsetRot = Quaternion.Inverse(animBone.rotation) * wp.rotation;
            weaponAttachedToRagdoll = true;
        }

        private void RestoreWeaponParentTransform()
        {
            if (weaponAttachedToRagdoll && trackedWeaponParent != null)
            {
                trackedWeaponParent.localPosition = weaponParentSavedLocalPos;
                trackedWeaponParent.localRotation = weaponParentSavedLocalRot;
            }
            if (weaponHiddenByFallback && Actor != null && !Actor.dead && !Actor.fallenOver &&
                Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
            {
                Actor.activeWeapon.gameObject.SetActive(true);
            }
            trackedWeaponParent = null;
            weaponRagdollHand = null;
            weaponAttachedToRagdoll = false;
            weaponHiddenByFallback = false;
        }

        private void CaptureCurrentFrames(float[] dst)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                WriteNmVec(b.position, dst, i * 7);
                WriteNmQuat(b.rotation, dst, i * 7 + 3);
            }
        }

        private void SendSimpleMessage(string name)
        {
            if (Agent == IntPtr.Zero || WorldEntry == null) return;
            nm_msg_begin(Agent, name);
            nm_msg_set_bool(Agent, "start", 1);
            nm_msg_send(Agent, WorldEntry.World);
        }

        private void CachePartMasses()
        {
            var m = new float[1];
            for (int i = 0; i < 21; i++)
            {
                if (build != IntPtr.Zero && nm_build_part_info(build, i, m, null, null, out _) >= 0 && m[0] > 0.05f)
                    partMasses[i] = m[0];
                else
                    partMasses[i] = (i == 0 || i == 10) ? 10f : 2.5f;
            }
        }

        private float ConsumeBurstBudget(float desiredImpulse)
        {
            float cap = Mathf.Max(config.MaxBurstImpulse.Value, 80f);
            float remaining = Mathf.Max(0f, cap - burstImpulseUsed);
            if (remaining < desiredImpulse)
            {
                // Soft-knee floor (38%, up to 22 Ns) so sustained SMG/AR/LMG bursts still visibly jolt every bullet like Playroom
                float floorImp = Mathf.Min(desiredImpulse * 0.38f, 22f);
                float granted = Mathf.Max(remaining, floorImp);
                burstImpulseUsed = Mathf.Min(cap, burstImpulseUsed + granted);
                return granted;
            }

            burstImpulseUsed += desiredImpulse;
            return desiredImpulse;
        }

        private void ApplyNaturalDistributedImpulse(int hitPart, Vector3 hitPoint, Vector3 dir, float totalImpulse, out float localImpulse, out float torsoImpulse)
        {
            localImpulse = 0f;
            torsoImpulse = 0f;
            if (Agent == IntPtr.Zero || totalImpulse <= 0.01f) return;

            hitPart = Mathf.Clamp(hitPart, 0, 20);
            float partMass = partMasses[hitPart] > 0.05f ? partMasses[hitPart] : 3f;
            bool isCore = hitPart == 0 || (hitPart >= 7 && hitPart <= 10);

            // Playroom-style localized impact: put 78-90% directly onto the struck part at hitPoint so shoulders twist,
            // arms/legs kick back, and head snaps cleanly on each bullet; only excess above maxDeltaV overflows to torso!
            float maxDeltaV = isCore ? config.MaxLimbDeltaV.Value * 0.95f : config.MaxLimbDeltaV.Value;
            float maxLocalByMass = partMass * maxDeltaV;
            float preferredLocalShare = hitPart == 12 ? 0.78f : (isCore ? 0.90f : 0.82f);

            localImpulse = Mathf.Min(totalImpulse * preferredLocalShare, maxLocalByMass);
            torsoImpulse = Mathf.Max(0f, totalImpulse - localImpulse);

            if (localImpulse > 0.1f)
            {
                nm_agent_apply_impulse(Agent, hitPart, ToNmVec(hitPoint), ToNmVec(dir * localImpulse));
            }

            if (torsoImpulse > 0.1f)
            {
                // Route transferred momentum by hit zone to produce BOTH natural torso pitch AND whole-body backward stagger steps:
                // - Upper body (chest/shoulders/arms/head): 60% Chest (10) + 40% Pelvis (0) so the whole center of mass
                //   accelerates backward enough to force 2-4 authentic Delirium stumbling steps instead of leaning in place!
                // - Gut / pelvis (0, 7, 8): 65% Pelvis (0) + 35% MidSpine (9) so the soldier doubles over and stumbles back.
                // - Legs (1..6): 80% Pelvis (0) + 20% Spine0 (7) to sweep legs out naturally.
                var pA = new float[3];
                var pB = new float[3];
                int partA, partB;
                float shareA;
                if (hitPart >= 9)
                {
                    partA = 10; partB = 0; shareA = 0.60f;
                }
                else if (hitPart >= 1 && hitPart <= 6)
                {
                    partA = 0; partB = 7; shareA = 0.80f;
                }
                else
                {
                    partA = 0; partB = 9; shareA = 0.65f;
                }

                float impA = torsoImpulse * shareA;
                float impB = torsoImpulse * (1f - shareA);

                if (nm_agent_get_part_transform(Agent, partA, pA, null) >= 0)
                    nm_agent_apply_impulse(Agent, partA, pA, ToNmVec(dir * impA));
                else
                    nm_agent_apply_push(Agent, ToNmVec(hitPoint), ToNmVec(dir * impA));

                if (impB > 0.1f && nm_agent_get_part_transform(Agent, partB, pB, null) >= 0)
                    nm_agent_apply_impulse(Agent, partB, pB, ToNmVec(dir * impB));
            }
        }

        private void CaptureEntryPoseSnapshot(int[] boneParts, bool isAliveStagger, bool isDeathTakeover)
        {
            entryBlendTimer = 0f;
            entryBlendDuration = 0f;
            hasIncomingArmFollow = false;
            armFollowTimer = 0f;
            armFollowDuration = 0.26f;

            if ((!isAliveStagger && !isDeathTakeover) || bones == null || bones.Length == 0) return;

            float blendSec = isAliveStagger
                ? config.EntryBlendSeconds.Value
                : Mathf.Min(config.EntryBlendSeconds.Value, 0.07f);

            if (blendSec > 0.005f)
            {
                entryStartRootRot = bones[0] != null ? bones[0].rotation : Quaternion.identity;
                entryStartLocalRot = new Quaternion[bones.Length];
                boneIsUpperLimb = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    entryStartLocalRot[i] = bones[i] != null ? bones[i].localRotation : Quaternion.identity;
                    int p = (boneParts != null && i < boneParts.Length) ? boneParts[i] : -1;
                    // NM parts 11..20: Neck, Head, Clavicle/UpperArm/Forearm/Hand (L & R)
                    boneIsUpperLimb[i] = (p >= 11 && p <= 20);
                }
                entryBlendDuration = blendSec;
            }

            // On alive stagger, also feed the initial gun-holding pose into Delirium's native upper-arm follow
            // so shoulders smoothly release from aiming into clutching the wound instead of flashing toward A-pose
            if (isAliveStagger && Agent != IntPtr.Zero)
            {
                int partCount = nm_agent_part_count(Agent);
                if (partCount > 0 && partCount <= 21)
                {
                    var incoming = new float[partCount * 7];
                    if (nm_agent_get_part_transforms(Agent, incoming, partCount) >= 0)
                    {
                        nm_agent_set_incoming_transforms(Agent, incoming, partCount);
                        nm_agent_follow(Agent, "ua", 0.72f);
                        hasIncomingArmFollow = true;
                    }
                }
            }
        }

        private void ClearIncomingArmFollow()
        {
            if (hasIncomingArmFollow && Agent != IntPtr.Zero)
            {
                nm_agent_follow(Agent, "ua", 0f);
                nm_agent_set_incoming_transforms(Agent, null, 0);
            }
            hasIncomingArmFollow = false;
        }

        private void ResetDeathAgonyState()
        {
            deathAgonyScheduled = false;
            deathAgonyActive = false;
            deathAgonyDone = false;
            deathAgonyStyle = 0;
            deathAgonyDuration = 0f;
            deathAgonyElapsed = 0f;
            deathAgonyNextUpdate = 0f;
            deathAgonyAsymmetry = 0f;
        }

        private void ScheduleDeathAgonyIfEligible(int hitPart, bool isCriticalOrHeadshot, bool isExplosion)
        {
            ResetDeathAgonyState();
            if (!config.EnableDeathAgony.Value || isCriticalOrHeadshot || hitPart == 12 || isExplosion)
                return;
            if (UnityEngine.Random.value > config.DeathAgonyChance.Value)
                return;

            float minSec = Mathf.Max(config.DeathAgonyMinSeconds.Value, 0.5f);
            float maxSec = Mathf.Max(config.DeathAgonyMaxSeconds.Value, minSec);
            deathAgonyScheduled = true;
            deathAgonyDuration = UnityEngine.Random.Range(minSec, maxSec);
            deathAgonyAsymmetry = UnityEngine.Random.Range(-0.55f, 0.55f);

            bool torsoHit = hitPart == 0 || (hitPart >= 7 && hitPart <= 10);
            float r = UnityEngine.Random.value;
            if (torsoHit)
                deathAgonyStyle = r < 0.40f ? 0 : (r < 0.75f ? 1 : 2);
            else
                deathAgonyStyle = r < 0.60f ? 0 : (r < 0.80f ? 1 : 2);
        }

        private void ApplyDeathAgonyWave(float progress01)
        {
            if (Agent == IntPtr.Zero || WorldEntry == null) return;

            // Two-stage agony strength: clearly visible natural dying movement on stage 1 (progress01=0),
            // then noticeably weaker & slower fading breath on stage 2 (progress01=0.65) before going fully limp.
            bool fading = progress01 > 0.3f;
            float inten = Mathf.Clamp(config.DeathAgonyIntensity.Value, 0.3f, 1.5f);
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            if (deathAgonyStyle == 1)
            {
                // Style 1: Clearly curl knees & clutch arms inward in pain (bodyFoetal), then weaken
                float stiff = (fading ? 4.8f : 9.0f) * Mathf.Lerp(0.72f, 1.15f, (inten - 0.3f) / 1.2f);
                float damp = fading ? 1.05f : 0.85f;
                float twist = (fading ? 0.30f : 0.58f) * inten;
                string cmd = string.Format(ic,
                    "bodyFoetal start=true stiffness={0:F2} dampingFactor={1:F2} asymmetry={2:F2} backTwist={3:F2}",
                    stiff, damp, deathAgonyAsymmetry, twist);
                nm_msg_send_text(Agent, WorldEntry.World, cmd);
            }
            else if (deathAgonyStyle == 2)
            {
                // Style 2: Legs limp while arms, shoulders & head reach/twist visibly in agony (bodyWrithe mask=ub)
                float armS = fading ? 5.0f : 9.0f;
                float backS = fading ? 4.4f : 7.8f;
                float armP = fading ? 2.25f : 1.30f;
                float backP = fading ? 2.65f : 1.60f;
                float armA = (fading ? 0.42f : 0.92f) * inten;
                float elbA = (fading ? 0.48f : 1.00f) * inten;
                float backA = (fading ? 0.24f : 0.56f) * inten;
                string cmd = string.Format(ic,
                    "bodyWrithe start=true armStiffness={0:F2} backStiffness={1:F2} legStiffness=2.5 armDamping=0.78 backDamping=0.85 legDamping=0.95 armPeriod={2:F2} backPeriod={3:F2} legPeriod=3.2 armAmplitude={4:F2} elbowAmplitude={5:F2} backAmplitude={6:F2} legAmplitude=0.06 kneeAmplitude=0.08 rollOverFlag=false mask=ub",
                    armS, backS, armP, backP, armA, elbA, backA);
                nm_msg_send_text(Agent, WorldEntry.World, cmd);
            }
            else
            {
                // Style 0: Visible full-body dying writhe (arms, knees, torso & head moving slowly on the ground)
                float armS = fading ? 4.8f : 8.2f;
                float backS = fading ? 4.4f : 7.6f;
                float legS = fading ? 4.2f : 7.4f;
                float armP = fading ? 2.40f : 1.50f;
                float backP = fading ? 2.75f : 1.75f;
                float legP = fading ? 2.85f : 1.85f;
                float armA = (fading ? 0.38f : 0.80f) * inten;
                float elbA = (fading ? 0.45f : 0.92f) * inten;
                float backA = (fading ? 0.26f : 0.60f) * inten;
                float legA = (fading ? 0.24f : 0.56f) * inten;
                float kneeA = (fading ? 0.34f : 0.78f) * inten;
                string cmd = string.Format(ic,
                    "bodyWrithe start=true armStiffness={0:F2} backStiffness={1:F2} legStiffness={2:F2} armDamping=0.80 backDamping=0.82 legDamping=0.84 armPeriod={3:F2} backPeriod={4:F2} legPeriod={5:F2} armAmplitude={6:F2} elbowAmplitude={7:F2} backAmplitude={8:F2} legAmplitude={9:F2} kneeAmplitude={10:F2} rollOverFlag=false mask=fb",
                    armS, backS, legS, armP, backP, legP, armA, elbA, backA, legA, kneeA);
                nm_msg_send_text(Agent, WorldEntry.World, cmd);
            }
        }

        private void CancelDeathAgonyAndGoLimp()
        {
            deathAgonyScheduled = false;
            deathAgonyActive = false;
            deathAgonyDone = true;
            GoFullyLimp();
            deadRelaxed = true;
            deadAnchorPos = lastComPos;
            deadAnchorElapsed = 0f;
            deadStillElapsed = 0f;
        }

        public void TriggerTestDyingShot(Vector3 shotDir)
        {
            if (Actor == null) return;
            Vector3 dir = shotDir.sqrMagnitude > 1e-4f ? shotDir.normalized : -Actor.transform.forward;
            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;

            Vector3 chestPos = hips != null ? hips.position + Vector3.up * 0.35f * scale : Actor.CenterPosition() + Vector3.up * 0.25f;
            var p = new float[3];
            if (nm_agent_get_part_transform(Agent, 10, p, null) >= 0)
                chestPos = ToUnityVec(p);

            var testShot = new NormalizedShot
            {
                TotalImpulse = Mathf.Clamp(38f * config.ShotForceScale.Value, 22f, 65f),
                TaskForce = 420f,
                Power01 = 0.55f,
                WeaponSet = "POWERFUL_WEAPON",
                Direction = dir,
                RawDamage = 55f,
                RawBalance = 45f,
                RawForce = 180f
            };
            StartDyingStagger(testShot, 10, chestPos, rapidScale: 1f);
        }

        private bool ShouldEnterDyingStagger(int part, bool isCriticalOrHeadshot, bool isExplosion, bool wasStanding)
        {
            if (!config.EnableDyingStagger.Value) return false;
            if (isCriticalOrHeadshot || part == 12 || isExplosion) return false;
            if (!wasStanding) return false;
            return UnityEngine.Random.value <= config.DyingStaggerChance.Value;
        }

        private void StartDyingStagger(NormalizedShot shot, int part, Vector3 point, float rapidScale)
        {
            if (!blindFirePendingStart && !blindFireActive)
                RestoreWeaponParentTransform();
            ClearIncomingArmFollow();

            float minSec = Mathf.Max(config.DyingStaggerMinSeconds.Value, 0.5f);
            float maxSec = Mathf.Max(config.DyingStaggerMaxSeconds.Value, minSec);
            // Leg shots buckle faster than torso shots
            bool isLegHit = part >= 1 && part <= 6;
            bool isArmHit = part >= 13 && part <= 20;
            float baseDuration = UnityEngine.Random.Range(minSec, maxSec);
            if (isLegHit) baseDuration *= 0.65f;

            ResetDyingStaggerState();
            dyingStaggerDuration = Mathf.Max(baseDuration, 0.55f);
            dyingCollapseActive = false;

            // Keep native dead=0 and environmental reactions ON throughout DyingStagger!
            nm_agent_set_dead(Agent, 0);
            ApplyEnvironmentReactionsBitmask(enabled: true);

            // Configure Playroom-faithful ShotTask parameters: reachForWound, spine pain, strong backward lean, light stayUpright
            float pwr = shot.Power01;
            dyingStaggerDir = shot.Direction;
            dyingLeanAmount = Mathf.Lerp(0.68f, 1.05f, pwr);
            dyingLeanArmed = false;

            nm_agent_set_override(Agent, "shot", "reachForWound", 1f);
            nm_agent_set_override(Agent, "shot", "timeBeforeReachForWound", 0.06f);
            nm_agent_set_override(Agent, "shot", "grabHoldTime", dyingStaggerDuration + 1.5f);
            nm_agent_set_override(Agent, "shot", "bodyStiffness", Mathf.Lerp(9.2f, 8.0f, pwr));
            nm_agent_set_override(Agent, "shot", "spinePainMultiplier", Mathf.Lerp(1.6f, 2.7f, pwr));
            nm_agent_set_override(Agent, "shot", "useCatchFallOnFall", config.EnableEnvironmentReactions.Value ? 1f : 0f);
            nm_agent_set_override(Agent, "shot", "timeBeforeCollapseWoundLeg", isLegHit ? 0.25f : 1.0f);
            nm_agent_set_override(Agent, "stayUpright", "stiffness", isLegHit ? 0.8f : Mathf.Lerp(1.6f, 0.9f, pwr));
            nm_agent_set_override(Agent, "leanInDirection", "leanAmount", dyingLeanAmount);
            nm_agent_set_override(Agent, "shotRelax", "relaxPeriod", dyingStaggerDuration * 0.6f);

            // Apply strong whole-body backward stagger impulse (52..88 N*s scaled by ShotForceScale) so the soldier
            // visibly stumbles 2-4 steps backwards in the bullet direction while clutching the bleeding wound!
            float minStaggerImp = Mathf.Lerp(52f, 86f, pwr) * config.ShotForceScale.Value;
            float desiredImpulse = Mathf.Max(shot.TotalImpulse * Mathf.Lerp(1.05f, 1.30f, pwr), minStaggerImp) * rapidScale;
            float budgeted = ConsumeBurstBudget(desiredImpulse);
            ApplyNaturalDistributedImpulse(part, point, shot.Direction, budgeted, out float sLocal, out float sTorso);
            WeaponForceNormalizer.RecordTelemetry(shot, budgeted, sLocal, sTorso, part);

            // Launch native NM ShotTask:
            // - Torso/arm hits start at nmHealth=118 (stayUpright + reachForWound + bodyBalance steps) so the bot visibly
            //   clutches the bleeding wound and staggers 1-3 steps before mid-stagger shotRelax buckles their knees!
            // - Leg hits start at nmHealth=78 + shotRelax + injury so the wounded leg buckles immediately.
            lastShotTaskTime = Time.time;
            float initNmHealth = isLegHit ? 78f : 118f;
            nm_agent_task_shot_force(Agent, WorldEntry.World, shot.WeaponSet, part, ToNmVec(point), ToNmVec(shot.Direction), initNmHealth, shot.TaskForce);
            nm_agent_set_dead(Agent, 0);

            if (isLegHit)
            {
                dyingRelaxTriggered = true;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                nm_msg_send_text(Agent, WorldEntry.World, string.Format(ic, "shotRelax relaxPeriod={0:F2}", dyingStaggerDuration));
            }

            // Apply Playroom-style limb injury so wounded arms droop and wounded legs buckle realistically
            if (isLegHit)
                ApplyLimbInjuryIfApplicable(part, 0.58f);
            else if (isArmHit)
                ApplyLimbInjuryIfApplicable(part, 0.45f);

            // Anchor bleeding wound to the exact hit part & start pulsing blood drops/spurts
            CaptureWoundLocalAnchor(part, point, -shot.Direction);
            EmitImpactBlood(point, shot.Direction, heavy: true);
            dyingBleedTimer = 0f;
            dyingBleedNextPulse = 0.14f;
            dyingBleedEndTime = dyingStaggerDuration + 1.4f;
            dyingBleedPulseCount = 0;

            // Schedule ground DeathAgony for after the bot collapses
            ScheduleDeathAgonyIfEligible(part, isCriticalOrHeadshot: false, isExplosion: false);

            Phase = RagdollPhase.DyingStagger;
            TryTriggerReflexBlindFire(isDeathShot: true);
            if (config.DebugLog.Value)
                Plugin.Log.LogInfo($"[Delirium] DyingStagger started for {Actor.name} (part={part}, dur={dyingStaggerDuration:F2}s, imp={budgeted:F1} Ns)");
        }

        private void ApplyDyingFollowUpShot(NormalizedShot shot, int part, Vector3 point, bool isCritical, float rapidScale)
        {
            dyingFollowUpHits++;
            EmitImpactBlood(point, shot.Direction, heavy: true);

            bool fatalHeadshot = isCritical || part == 12;
            bool exceededHits = dyingFollowUpHits >= Mathf.Max(config.DyingStaggerMaxFollowUpHits.Value, 1);

            if (fatalHeadshot || exceededHits)
            {
                // Finishing shot or headshot: cut DyingStagger short and collapse immediately with full impulse!
                TransitionDyingStaggerToCollapse(immediateLimp: true, cancelAgony: fatalHeadshot);
                float finishDesired = shot.TotalImpulse * Mathf.Lerp(0.95f, 1.25f, shot.Power01) * rapidScale;
                float finishBudgeted = ConsumeBurstBudget(finishDesired);
                ApplyNaturalDistributedImpulse(part, point, shot.Direction, finishBudgeted, out float fLocal, out float fTorso);
                WeaponForceNormalizer.RecordTelemetry(shot, finishBudgeted, fLocal, fTorso, part);
                return;
            }

            // Additional stagger/recoil mid-dying: re-trigger ShotTask next_hit + crisp Playroom localized punch
            float recoilDesired = shot.TotalImpulse * 0.85f * rapidScale;
            float recoilBudgeted = ConsumeBurstBudget(recoilDesired);
            ApplyNaturalDistributedImpulse(part, point, shot.Direction, recoilBudgeted, out float rLocal, out float rTorso);
            WeaponForceNormalizer.RecordTelemetry(shot, recoilBudgeted, rLocal, rTorso, part);

            float now = Time.time;
            if (now - lastShotTaskTime >= 0.035f)
            {
                lastShotTaskTime = now;
                nm_agent_task_shot_force(Agent, WorldEntry.World, shot.WeaponSet, part, ToNmVec(point), ToNmVec(shot.Direction), 64f, shot.TaskForce);
                nm_agent_set_dead(Agent, 0);
            }

            ApplyLimbInjuryIfApplicable(part, 0.35f);
            CaptureWoundLocalAnchor(part, point, -shot.Direction);
            dyingBleedNextPulse = Mathf.Min(dyingBleedNextPulse, dyingBleedTimer + 0.06f);
            // Slightly shorten remaining stagger time on each follow-up hit
            dyingStaggerDuration = Mathf.Min(dyingStaggerDuration, dyingStaggerElapsed + 0.95f);
        }

        private void TransitionDyingStaggerToCollapse(bool immediateLimp, bool cancelAgony)
        {
            if (cancelAgony)
            {
                ResetDeathAgonyState();
            }

            if (immediateLimp || !config.EnableEnvironmentReactions.Value)
            {
                dyingCollapseActive = false;
                MarkDead(keepEntryBlend: false, keepEnvReactions: false);
                if (cancelAgony || !deathAgonyScheduled)
                {
                    GoFullyLimp();
                    deadRelaxed = true;
                }
                else
                {
                    StartNaturalDeathCollapse();
                }
                return;
            }

            // Muscle-driven collapse: keep env reactions (NM_XR_WALL / NM_XR_LEDGE / rollDownStairs) alive while falling
            // and trigger a weakened catchFall + shotRelax so hands brace against walls/floor naturally before expiring!
            dyingCollapseActive = true;
            MarkDead(keepEntryBlend: false, keepEnvReactions: true);
            if (Agent != IntPtr.Zero && WorldEntry != null)
            {
                nm_msg_send_text(Agent, WorldEntry.World, "catchFall start=true armsStiffness=7.2 torsoStiffness=6.2 legsStiffness=3.8");
                nm_msg_send_text(Agent, WorldEntry.World, "shotRelax relaxPeriod=0.70");
            }
            if (config.DebugLog.Value)
                Plugin.Log.LogInfo($"[Delirium] DyingStagger -> Muscle Collapse for {Actor?.name} (staggered {dyingStaggerElapsed:F2}s)");
        }

        private void ApplyLimbInjuryIfApplicable(int part, float amount)
        {
            if (Agent == IntPtr.Zero || WorldEntry == null) return;
            string mask = null;
            if (part >= 13 && part <= 16) mask = "ul";
            else if (part >= 17 && part <= 20) mask = "ur";
            else if (part >= 1 && part <= 3) mask = "ll";
            else if (part >= 4 && part <= 6) mask = "lr";
            if (mask == null) return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            nm_msg_send_text(Agent, WorldEntry.World, string.Format(ic, "injury mask={0} amount={1:F2}", mask, Mathf.Clamp(amount, 0.1f, 0.85f)));
        }

        private void CaptureWoundLocalAnchor(int part, Vector3 worldPoint, Vector3 worldNormal)
        {
            dyingWoundPart = Mathf.Clamp(part, 0, 20);
            var p = new float[3];
            var q = new float[4];
            if (Agent != IntPtr.Zero && nm_agent_get_part_transform(Agent, dyingWoundPart, p, q) >= 0)
            {
                Vector3 partPos = ToUnityVec(p);
                Quaternion partRot = ToUnityQuat(q);
                Quaternion invRot = Quaternion.Inverse(partRot);
                Vector3 localOff = invRot * (worldPoint - partPos);
                if (localOff.magnitude > 0.25f) localOff = localOff.normalized * 0.12f;
                dyingWoundLocalPos = localOff;
                Vector3 nrm = worldNormal.sqrMagnitude > 1e-4f ? worldNormal.normalized : Vector3.forward;
                dyingWoundLocalNormal = invRot * nrm;
            }
            else
            {
                dyingWoundLocalPos = Vector3.zero;
                dyingWoundLocalNormal = Vector3.forward;
            }
        }

        private void TickWoundBleeding(float dt)
        {
            if (!config.EnableDyingBleed.Value || Agent == IntPtr.Zero || Actor == null) return;
            dyingBleedTimer += dt;
            if (dyingBleedTimer > dyingBleedEndTime)
            {
                dyingWoundPart = -1;
                return;
            }
            if (dyingBleedTimer < dyingBleedNextPulse) return;

            float lifeFrac = Mathf.Clamp01(1f - dyingBleedTimer / Mathf.Max(dyingBleedEndTime, 0.1f));
            dyingBleedNextPulse = dyingBleedTimer + Mathf.Lerp(0.32f, 0.18f, lifeFrac);
            dyingBleedPulseCount++;

            var p = new float[3];
            var q = new float[4];
            if (nm_agent_get_part_transform(Agent, dyingWoundPart, p, q) < 0) return;

            Vector3 partPos = ToUnityVec(p);
            Quaternion partRot = ToUnityQuat(q);
            Vector3 worldPos = partPos + partRot * dyingWoundLocalPos;
            Vector3 worldNrm = (partRot * dyingWoundLocalNormal).normalized;

            try
            {
                Vector3 spurtVel = (worldNrm + Vector3.up * 0.25f) * (0.7f + 1.8f * lifeFrac) +
                                   UnityEngine.Random.insideUnitSphere * 0.35f;
                DecalManager.CreateBloodDrop(worldPos, spurtVel, Actor.team);
                if (Phase == RagdollPhase.DyingStagger && (dyingBleedPulseCount % 2 == 1))
                {
                    DecalManager.EmitBloodEffect(worldPos, spurtVel * 0.8f, Actor.team);
                }
            }
            catch { }
        }

        private void EmitImpactBlood(Vector3 point, Vector3 dir, bool heavy)
        {
            if (Actor == null) return;
            Vector3 vel = (dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up) * (heavy ? 2.4f : 1.4f);
            try
            {
                DecalManager.EmitBloodEffect(point, vel, Actor.team);
                DecalManager.CreateBloodDrop(point, vel * 0.85f + UnityEngine.Random.insideUnitSphere * 0.4f, Actor.team);
                if (heavy)
                    DecalManager.CreateBloodDrop(point, -vel * 0.5f + Vector3.up * 0.6f + UnityEngine.Random.insideUnitSphere * 0.4f, Actor.team);
            }
            catch { }
        }

        private void ResetDyingStaggerState()
        {
            dyingStaggerElapsed = 0f;
            dyingStaggerDuration = 0f;
            dyingRelaxTriggered = false;
            dyingLeanArmed = false;
            dyingStaggerDir = Vector3.forward;
            dyingLeanAmount = 0.75f;
            dyingFollowUpHits = 0;
            dyingWoundPart = -1;
            dyingWoundLocalPos = Vector3.zero;
            dyingWoundLocalNormal = Vector3.forward;
            dyingBleedTimer = 0f;
            dyingBleedNextPulse = 0f;
            dyingBleedEndTime = 0f;
            dyingBleedPulseCount = 0;
            dyingCollapseActive = false;
        }

        private void EnsureRagdollCollidersEnabled()
        {
            if (Actor?.ragdoll?.ragdollObject == null) return;
            var cols = Actor.ragdoll.ragdollObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c != null && !c.enabled) c.enabled = true;
            }
        }

        private void ReanchorWorldIfNeeded()
        {
            if (WorldEntry == null || Actor == null || !IsSimulating) return;
            Vector3 anchor = WorldEntry.Anchor;
            float horizDist = Vector2.Distance(new Vector2(lastComPos.x, lastComPos.z), new Vector2(anchor.x, anchor.z));
            float dropBelowAnchor = anchor.y - lastComPos.y;
            // Re-anchor when traveling near the edge of the sampled BVH bubble (e.g. rolling down long stairs or blown off a tower)
            if (horizDist < 8.5f * scale && dropBelowAnchor < 6.0f * scale) return;

            float groundY = runtime.SampleGroundHeight(lastComPos + Vector3.up * 0.4f, Actor.transform, anchor.y);
            int sceneId = Actor.gameObject.scene.GetHashCode();
            var newWorld = runtime.AcquireWorld(lastComPos, groundY, Actor.transform, scale, sceneId);
            if (newWorld != null && newWorld != WorldEntry)
            {
                var old = WorldEntry;
                WorldEntry = newWorld;
                runtime.ReleaseWorld(old);
                if (config.DebugLog.Value)
                    Plugin.Log.LogInfo($"[Delirium] Re-anchored world for {Actor.name} at {lastComPos} (moved h={horizDist:F1}m, drop={dropBelowAnchor:F1}m)");
            }
        }

        public static bool IsEligibleBlindFireWeapon(Weapon w)
        {
            if (w == null || w.gameObject == null || !w.gameObject.activeInHierarchy) return false;
            if (w.configuration == null || w.configuration.projectilePrefab == null) return false;
            if (w.IsMeleeWeapon() || w.IsMountedWeapon() || w.reloading || w.ammo <= 0 || !w.HasLoadedAmmo()) return false;
            if (w is ThrowableWeapon) return false;
            return true;
        }

        public bool TryDeferWeaponDropForBlindFire(Weapon w, int slot)
        {
            if (!config.EnableBlindFire.Value || IsPlayer || !IsEligibleBlindFireWeapon(w))
                return false;
            if (blindFireActive || blindFireWeapon != null)
                return false;
            if (UnityEngine.Random.value > config.BlindFireChance.Value)
                return false;

            blindFireWeapon = w;
            blindFireDeferredSlot = slot;
            blindFirePendingStart = true;
            blindFireElapsed = 0f;
            return true;
        }

        private void TryTriggerReflexBlindFire(bool isDeathShot)
        {
            if (!config.EnableBlindFire.Value || IsPlayer || Actor == null) return;
            if (blindFireActive) return;

            Weapon w = null;
            if (blindFirePendingStart && blindFireWeapon != null)
            {
                w = blindFireWeapon;
                blindFirePendingStart = false;
            }
            else if (!isDeathShot && !Actor.dead)
            {
                Weapon cand = Actor.activeWeapon;
                if (!IsEligibleBlindFireWeapon(cand)) return;
                if (UnityEngine.Random.value > config.BlindFireChance.Value) return;
                w = cand;
            }

            if (!IsEligibleBlindFireWeapon(w))
            {
                if (blindFireDeferredSlot >= 0) StopBlindFire(dropIfDead: true);
                return;
            }

            blindFireWeapon = w;
            blindFireActive = true;
            blindFireElapsed = 0f;
            blindFireShotsFired = 0;

            bool isAuto = w.configuration.auto;
            float maxSec = Mathf.Clamp(config.BlindFireMaxSeconds.Value, 0.15f, 1.5f);
            if (isAuto)
            {
                blindFireDuration = UnityEngine.Random.Range(Mathf.Min(0.22f, maxSec), maxSec);
                blindFireMaxShots = UnityEngine.Random.Range(2, 7);
            }
            else
            {
                blindFireDuration = Mathf.Min(0.28f, maxSec);
                blindFireMaxShots = 1;
            }

            // Immediate startled reflex trigger pull (0.02..0.09s after impact)
            blindFireNextShotTime = UnityEngine.Random.Range(0.02f, 0.09f);

            // Sudden startled wrist/gun jerk biased upward & sideways so barrel whips off-target immediately
            float spread = Mathf.Clamp(config.BlindFireWildSpreadDegrees.Value, 10f, 80f);
            blindFireTwitchRot = Quaternion.Euler(
                UnityEngine.Random.Range(-spread * 0.85f, -spread * 0.20f),
                UnityEngine.Random.Range(-spread * 0.90f, spread * 0.90f),
                UnityEngine.Random.Range(-25f, 25f));

            // Glue weapon to ragdoll hand so the player visually sees the flailing gun discharge!
            if (!weaponAttachedToRagdoll)
            {
                CaptureWeaponForRagdollHand(allowDeadWeapon: isDeathShot || Actor.dead);
            }
            if (w.gameObject != null && !w.gameObject.activeSelf)
                w.gameObject.SetActive(true);

            if (config.DebugLog.Value)
                Plugin.Log.LogInfo($"[Delirium] Reflex blind-fire triggered on {Actor.name} (auto={isAuto}, maxShots={blindFireMaxShots}, dur={blindFireDuration:F2}s)");
        }

        private void TickBlindFire(float dt)
        {
            blindFireElapsed += dt;

            // Safety cleanup if a deferred death weapon drop was scheduled (e.g. explosion) without starting a burst
            if (blindFirePendingStart && !blindFireActive)
            {
                if (blindFireElapsed >= 0.15f)
                    StopBlindFire(dropIfDead: true);
                return;
            }

            if (!blindFireActive) return;

            if (blindFireWeapon == null || !IsEligibleBlindFireWeapon(blindFireWeapon) ||
                blindFireElapsed >= blindFireDuration ||
                blindFireShotsFired >= blindFireMaxShots)
            {
                StopBlindFire(dropIfDead: Actor != null && Actor.dead);
                return;
            }

            if (blindFireElapsed < blindFireNextShotTime) return;

            try
            {
                if (blindFireWeapon.reloading || blindFireWeapon.ammo <= 0 || !blindFireWeapon.HasLoadedAmmo())
                {
                    StopBlindFire(dropIfDead: Actor != null && Actor.dead);
                    return;
                }

                Vector3 wildDir = ComputeWildBlindFireDirection(blindFireWeapon);
                int ammoBefore = blindFireWeapon.ammo;
                bool fired = blindFireWeapon.Fire(wildDir, useMuzzleDirection: false, ignoreCanFireCheck: true);
                if (fired)
                {
                    if (blindFireWeapon.ammo >= ammoBefore)
                        blindFireWeapon.ammo = Mathf.Max(0, ammoBefore - 1);
                    blindFireShotsFired++;
                    // Kick the ragdoll gun hand backward from the accidental shot's recoil!
                    if (Agent != IntPtr.Zero)
                    {
                        var hp = new float[3];
                        int gunPart = nm_agent_get_part_transform(Agent, 20, hp, null) >= 0 ? 20 : 19;
                        if (gunPart == 20 || nm_agent_get_part_transform(Agent, gunPart, hp, null) >= 0)
                        {
                            float kick = blindFireWeapon.configuration.auto ? 5.5f : 8.5f;
                            nm_agent_apply_impulse(Agent, gunPart, hp, ToNmVec(-wildDir * kick));
                        }
                    }

                    // Further wild wrist jerk on each accidental round
                    float spread = Mathf.Clamp(config.BlindFireWildSpreadDegrees.Value, 10f, 80f);
                    blindFireTwitchRot *= Quaternion.Euler(
                        UnityEngine.Random.Range(-14f, 6f),
                        UnityEngine.Random.Range(-18f, 18f),
                        UnityEngine.Random.Range(-10f, 10f));
                    // Clamp twitch so weapon doesn't flip backwards into wrist
                    if (Quaternion.Angle(Quaternion.identity, blindFireTwitchRot) > spread * 1.4f)
                        blindFireTwitchRot = Quaternion.Slerp(Quaternion.identity, blindFireTwitchRot, 0.7f);
                }

                float cd = blindFireWeapon.configuration != null ? blindFireWeapon.configuration.cooldown : 0.12f;
                blindFireNextShotTime = blindFireElapsed + Mathf.Max(cd, 0.065f) * UnityEngine.Random.Range(0.95f, 1.25f);

                if (blindFireShotsFired >= blindFireMaxShots || !blindFireWeapon.HasLoadedAmmo())
                {
                    StopBlindFire(dropIfDead: Actor != null && Actor.dead);
                }
            }
            catch (Exception ex)
            {
                if (config.DebugLog.Value)
                    Plugin.Log.LogWarning($"[Delirium] BlindFire exception on {Actor?.name}: {ex.Message}");
                StopBlindFire(dropIfDead: Actor != null && Actor.dead);
            }
        }

        private Vector3 ComputeWildBlindFireDirection(Weapon w)
        {
            Transform muzzle = null;
            try { muzzle = w.CurrentMuzzle(); } catch { }

            Vector3 baseDir;
            Vector3 muzzlePos;
            if (muzzle != null)
            {
                baseDir = muzzle.forward;
                muzzlePos = muzzle.position;
            }
            else if (weaponRagdollHand != null)
            {
                baseDir = weaponRagdollHand.forward;
                muzzlePos = weaponRagdollHand.position;
            }
            else
            {
                baseDir = Actor != null ? Actor.transform.forward : Vector3.forward;
                muzzlePos = lastComPos + Vector3.up * 0.25f;
            }

            if (baseDir.sqrMagnitude < 1e-4f) baseDir = Vector3.forward;
            baseDir.Normalize();

            // Add erratic reflex deflection (even wilder if already falling!)
            float spread = Mathf.Clamp(config.BlindFireWildSpreadDegrees.Value, 10f, 80f);
            if (Phase == RagdollPhase.ActiveFallen || Phase == RagdollPhase.DeadSimulating)
                spread *= 1.35f;

            float yaw = UnityEngine.Random.Range(-spread, spread);
            // Bias slightly upward like a startled pull on the trigger, while still allowing downward/sideways sprays
            float pitch = UnityEngine.Random.Range(-spread * 0.95f, spread * 0.55f);
            Vector3 wild = (Quaternion.Euler(pitch, yaw, 0f) * baseDir).normalized;

            // Avoid muzzle somehow pointing straight back into the bot's own torso
            Vector3 awayFromBody = muzzlePos - lastComPos;
            if (awayFromBody.sqrMagnitude > 0.02f && Vector3.Dot(wild, awayFromBody.normalized) < -0.15f)
            {
                wild = Vector3.Slerp(wild, awayFromBody.normalized + Vector3.up * 0.35f, 0.7f).normalized;
            }
            return wild;
        }

        private void StopBlindFire(bool dropIfDead)
        {
            Weapon w = blindFireWeapon;
            int deferredSlot = blindFireDeferredSlot;

            blindFireActive = false;
            blindFirePendingStart = false;
            blindFireWeapon = null;
            blindFireDeferredSlot = -1;
            blindFireTwitchRot = Quaternion.identity;

            if (w != null)
            {
                try { w.StopFire(); } catch { }
            }

            if (dropIfDead && deferredSlot >= 0)
            {
                RestoreWeaponParentTransform();
                bool dropped = false;
                if (Actor != null)
                {
                    IsAllowingDeferredDrop = true;
                    try
                    {
                        Actor.DropWeapon(deferredSlot);
                        dropped = true;
                    }
                    catch { }
                    finally
                    {
                        IsAllowingDeferredDrop = false;
                    }
                }
                if (!dropped && w != null)
                {
                    try { w.Drop(); } catch { }
                }
            }
            else if (Actor != null && !Actor.dead && Actor.fallenOver && Phase == RagdollPhase.ActiveFallen)
            {
                // Alive bot finished blind-firing mid-fall: hide weapon cleanly until get-up
                RestoreWeaponParentTransform();
                if (Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
                    Actor.activeWeapon.gameObject.SetActive(false);
            }
        }

        private void ReleaseNativeOnly()
        {
            enginePhysicsProxy.Dispose();
            hasIncomingArmFollow = false;
            entryBlendDuration = 0f;
            if (rig != IntPtr.Zero)
            {
                nm_rig_destroy(rig);
                rig = IntPtr.Zero;
            }
            if (Agent != IntPtr.Zero)
            {
                nm_agent_destroy(Agent);
                Agent = IntPtr.Zero;
            }
            if (build != IntPtr.Zero)
            {
                if (buildOwnedByController)
                    nm_build_destroy(build);
                build = IntPtr.Zero;
            }
            buildOwnedByController = true;
            if (WorldEntry != null)
            {
                if (worldOwnedByController)
                    runtime.ReleaseWorld(WorldEntry);
                WorldEntry = null;
            }
            worldOwnedByController = true;
            IsSyncLockstepControlled = false;
            SyncVisualOffset = Vector3.zero;
        }

        public void Dispose()
        {
            ReleaseImmediate(restoreGame: false);
        }
    }
}
