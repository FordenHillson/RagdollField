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
        DeadSimulating,
        DeadFrozen
    }

    public sealed class ActorRagdollController : IDisposable
    {
        public Actor Actor { get; }
        public RagdollPhase Phase { get; private set; } = RagdollPhase.Animated;
        public SharedWorldEntry WorldEntry { get; private set; }
        public IntPtr Agent { get; private set; }
        public IntPtr BuildPtr => build;
        public RigBuildResult LastRigSnapshot { get; private set; }
        public int BracingVehicleIndex { get; set; } = -1;
        public float BraceTimeLeft { get; set; }
        public string LastRigReport { get; private set; } = "";

        public bool IsSimulating =>
            Agent != IntPtr.Zero &&
            (Phase == RagdollPhase.ActiveStagger || Phase == RagdollPhase.ActiveFallen || Phase == RagdollPhase.DeadSimulating);

        public bool IsControllingActor =>
            Phase == RagdollPhase.ActiveStagger ||
            Phase == RagdollPhase.ActiveFallen ||
            Phase == RagdollPhase.BlendingToStand;

        public bool IsDeadOrFrozen =>
            Phase == RagdollPhase.DeadSimulating || Phase == RagdollPhase.DeadFrozen;

        public bool SuppressVanillaGetUp =>
            Phase == RagdollPhase.ActiveStagger || Phase == RagdollPhase.ActiveFallen;

        public bool IsPlayer => Actor != null && ActorManager.instance != null && Actor == ActorManager.instance.player;

        private readonly DeliriumRuntime runtime;
        private readonly ModConfig config;
        private static readonly System.Reflection.MethodInfo ActorGetUpMethod =
            HarmonyLib.AccessTools.Method(typeof(Actor), "GetUp");
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

        // State timers
        private float staggerElapsed;
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

        // Keep bot's active weapon glued to the ragdoll hand during ActiveStagger (prevents floating gun!)
        private Transform trackedWeaponParent;
        private Transform weaponRagdollHand;
        private Vector3 weaponParentSavedLocalPos;
        private Quaternion weaponParentSavedLocalRot;
        private Vector3 weaponHandOffsetPos;
        private Quaternion weaponHandOffsetRot;
        private bool weaponAttachedToRagdoll;
        private bool weaponHiddenByFallback;

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

                // 6. Seamless takeover from current Unity pose & velocity (zero snap!)
                CaptureCurrentFrames(frameBuf);
                Check(nm_rig_take(rig, Agent, frameBuf, null, 0f), "nm_rig_take");

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

                if (startBalanced && !Actor.dead)
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
                ResetDeathAgonyState();
                burstImpulseUsed = 0f;
                lastShotTaskTime = -10f;
                lastShotHitTime = -10f;
                consecutiveRapidHits = 0;
                consecutiveAliveHits = 0;
                BracingVehicleIndex = -1;

                if (Actor.dead)
                {
                    nm_agent_set_dead(Agent, 1);
                    nm_agent_set_reactions(Agent, 0);
                    Phase = RagdollPhase.DeadSimulating;
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

            if (config.EnableExtraReactions.Value)
            {
                int xr = NM_XR_WINDMILL | NM_XR_LOOK_HIT | NM_XR_CURL_SHOT | NM_XR_LEDGE | NM_XR_SMART_FALL;
                nm_agent_set_reactions(Agent, xr);
            }

            // Tame shockSpin so fatal shots near walls/corners don't helicopter-launch corpses
            nm_agent_set_override(Agent, "shot", "shockSpinMax", 22f);
            nm_agent_set_override(Agent, "shot", "shockSpinMin", 8f);
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

        public void ApplyShot(DamageInfo info, bool fatal, bool alreadyDeadCorpse = false)
        {
            // IMPORTANT: Check whether this actor was ALREADY a dead corpse BEFORE EnsureTakenOver,
            // because EnsureTakenOver sets Phase=DeadSimulating immediately for 1-shot kills (like headshots)!
            bool wasAlreadyDead = alreadyDeadCorpse || (Phase == RagdollPhase.DeadSimulating);

            if (!EnsureTakenOver(startBalanced: !fatal, alreadyFallen: Actor.fallenOver)) return;

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

            // Progressive attenuation during sustained high-ROF spray (SMG/AR/Minigun/modded rapid-fire)
            float rapidScale = consecutiveRapidHits <= 1
                ? 1f
                : Mathf.Lerp(0.85f, 0.45f, Mathf.Clamp01((consecutiveRapidHits - 1) / 6f));

            // Already-dead corpse taking follow-up bullets from a spray:
            // Only finish off death agony if the bot is ALREADY visibly writhing on the floor (deliberate double-tap),
            // so stray extra bullets in the initial kill burst don't accidentally cancel agony before it begins!
            if (wasAlreadyDead)
            {
                if (deathAgonyActive && deathAgonyElapsed >= 0.35f)
                {
                    CancelDeathAgonyAndGoLimp();
                }
                float corpseDesired = shot.TotalImpulse * 0.42f * rapidScale;
                float corpseBudgeted = ConsumeBurstBudget(corpseDesired);
                ApplyNaturalDistributedImpulse(part, point, shot.Direction, corpseBudgeted, out float cLocal, out float cTorso);
                WeaponForceNormalizer.RecordTelemetry(shot, corpseBudgeted, cLocal, cTorso, part);
                deadStillElapsed = 0f;
                deadAnchorElapsed = 0f;
                deadAnchorPos = lastComPos;
                return;
            }

            if (!isAlive)
            {
                // Fatal shot (including 1-shot headshots!):
                // Relax joints FIRST using holdPose=true (zero A-pose snap AND zero stiff default joint resistance
                // swallowing the bullet impulse!), then apply the full power-scaled impact so headshots & heavy guns
                // visibly whip the head and throw the upper body cleanly in the bullet direction.
                MarkDead(keepEntryBlend: true);
                StartNaturalDeathCollapse();
                ScheduleDeathAgonyIfEligible(part, info.isCriticalHit, isExplosion: false);

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

            // Scale alive standing push dynamically with gun power (light guns = crisp flinch; heavy rifles/snipers = strong torso whip & stagger)
            float aliveScale = Mathf.Lerp(
                config.AliveShotPushScale.Value,
                Mathf.Min(config.AliveShotPushScale.Value * 1.9f, 0.92f),
                shot.Power01);
            float desiredImpulse = (isStandingAlive
                ? shot.TotalImpulse * aliveScale
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

            // Cooldown task_shot spam on multi-pellet shotguns & 1000+ RPM guns
            if (now - lastShotTaskTime >= 0.085f)
            {
                lastShotTaskTime = now;
                nm_agent_task_shot_force(Agent, WorldEntry.World, weaponSet, part, ToNmVec(point), ToNmVec(shot.Direction), nmHealth, shot.TaskForce);
                nm_agent_set_dead(Agent, 0);
            }

            staggerElapsed = 0f;
            settledElapsed = 0f;
            if (Phase == RagdollPhase.ActiveFallen)
            {
                // Fresh hit while lying down resets settle timer slightly without locking them down forever
                fallenStillElapsed = 0f;
                fallenAnchorElapsed = 0f;
                fallenAnchorPos = lastComPos;
                fallenTaskStopped = false;
            }
        }

        public void ApplyMelee(DamageInfo info, bool fatal)
        {
            if (!EnsureTakenOver(startBalanced: !fatal, alreadyFallen: Actor.fallenOver)) return;

            Vector3 dir = info.direction.sqrMagnitude > 1e-4f
                ? info.direction.normalized
                : (info.impactForce.sqrMagnitude > 1e-4f ? info.impactForce.normalized : -Actor.transform.forward);

            Vector3 point = info.point != Vector3.zero ? info.point : (hips != null ? hips.position + Vector3.up * 0.3f : Actor.transform.position + Vector3.up);
            float mag = ConsumeBurstBudget(WeaponForceNormalizer.NormalizeMeleeImpulse(info, config));
            Vector3 impulse = dir * mag;

            if (fatal || Actor.dead)
            {
                int part = Mathf.Clamp(nm_agent_nearest_part(Agent, ToNmVec(point)), 0, 20);
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
                EnterActiveFallenState();
        }

        public void ApplyHighFall()
        {
            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;
            nm_agent_task_high_fall(Agent, WorldEntry.World, null, IsPlayer ? 1 : 0);
            if (Actor.dead)
                MarkDead();
            else
                EnterActiveFallenState();
        }

        public void ApplyPush(Vector3 impulse, bool balance = true)
        {
            if (!EnsureTakenOver(startBalanced: balance, alreadyFallen: false)) return;
            Vector3 at = hips != null ? hips.position + Vector3.up * 0.35f * scale : Actor.transform.position + Vector3.up;
            nm_agent_apply_push(Agent, ToNmVec(at), ToNmVec(impulse));
            staggerElapsed = 0f;
            settledElapsed = 0f;
        }

        public void ApplyNearMiss(Vector3 from, Vector3 dir)
        {
            if (Actor == null || Actor.dead || IsPlayer || Actor.IsSeated()) return;
            if (Time.time - lastNearMissTime < 1.25f) return;
            lastNearMissTime = Time.time;

            if (!EnsureTakenOver(startBalanced: true, alreadyFallen: false)) return;
            nm_agent_near_miss(Agent, WorldEntry.World, ToNmVec(from), ToNmVec(dir.normalized));
            staggerElapsed = 0f;
            settledElapsed = 0f;
        }

        public void ApplyDeath(DamageInfo info)
        {
            if (!EnsureTakenOver(startBalanced: false, alreadyFallen: true)) return;

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
            else if (info.impactForce.sqrMagnitude > 0.1f)
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

        public void MarkDead(bool keepEntryBlend = false)
        {
            RestoreWeaponParentTransform();
            ClearIncomingArmFollow();
            if (!keepEntryBlend)
                entryBlendDuration = 0f;
            if (Agent != IntPtr.Zero)
            {
                nm_agent_set_dead(Agent, 1);
                // Disable extra active reactions (curl/windmill/look) so corpses collapse naturally and settle cleanly
                nm_agent_set_reactions(Agent, 0);
            }
            if (Phase != RagdollPhase.DeadSimulating)
            {
                deadElapsed = 0f;
                deadStillElapsed = 0f;
                deadRelaxed = false;
                deadAnchorElapsed = 0f;
                deadAnchorPos = lastComPos;
                ResetDeathAgonyState();
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
            nm_agent_task_stop(Agent);
            nm_msg_send_text(Agent, WorldEntry.World, "stopAllBehaviours");
            nm_msg_send_text(Agent, WorldEntry.World, "bodyRelax relaxation=99 damping=0.12 holdPose=true");
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

            // Decay burst impulse budget over ~0.30s window so shotguns/SMGs/miniguns don't stack violently
            if (burstImpulseUsed > 0f)
            {
                float decayRate = Mathf.Max(config.MaxBurstImpulse.Value, 40f) / 0.30f;
                burstImpulseUsed = Mathf.Max(0f, burstImpulseUsed - decayRate * dt);
            }

            // Keep alive/dead state synced with Ravenfield
            if (Actor.dead && Phase != RagdollPhase.DeadSimulating)
            {
                MarkDead();
            }

            // Read COM position & velocity for state logic and diagnostics
            var posArr = new float[3];
            var velArr = new float[3];
            nm_agent_get_com(Agent, posArr, velArr);
            lastComPos = ToUnityVec(posArr);
            lastComVel = ToUnityVec(velArr);

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

            // Dead corpse dying agony, relaxation & settling check (frees LOD slot once limp and settled)
            if (Phase == RagdollPhase.DeadSimulating)
            {
                deadElapsed += dt;
                bool deadFallen = nm_agent_has_fallen(Agent) == 1;

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
                if (hasFallen)
                {
                    EnterActiveFallenState();
                    return;
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
                if (Actor.HasUnholsteredWeapon() && Actor.activeWeapon != null)
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

            bool blendingEntry = (Phase == RagdollPhase.ActiveStagger || Phase == RagdollPhase.DeadSimulating) &&
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

            // Keep bot's active weapon glued to the ragdoll hand while staggering (no floating gun!)
            if (Phase == RagdollPhase.ActiveStagger && weaponAttachedToRagdoll &&
                trackedWeaponParent != null && weaponRagdollHand != null)
            {
                trackedWeaponParent.position = weaponRagdollHand.TransformPoint(weaponHandOffsetPos);
                trackedWeaponParent.rotation = weaponRagdollHand.rotation * weaponHandOffsetRot;
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
            RestoreWeaponParentTransform();
            ReleaseNativeOnly();
            RestoreRagdollRigidbodies();

            Actor.SetPositionAndRotation(new Vector3(hipPos.x, groundY, hipPos.z), Quaternion.LookRotation(forward.normalized, Vector3.up));
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
            float groundY = runtime.SampleGroundHeight(hipPos, Actor.transform, hipPos.y);

            // Release Delirium FIRST and restore non-kinematic rigidbodies with zero velocity
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

            // Reposition Actor root to the fallen body WITHOUT shifting ragdollObject's world bone poses,
            // so ActiveRaggy.Animate() inside Actor.GetUp() interpolates seamlessly from Delirium's exact lying pose!
            Transform ragObjTf = Actor.ragdoll.ragdollObject != null ? Actor.ragdoll.ragdollObject.transform : null;
            Transform origParent = ragObjTf != null ? ragObjTf.parent : null;
            if (ragObjTf != null) ragObjTf.SetParent(null, true);

            Actor.SetPositionAndRotation(new Vector3(hipPos.x, groundY, hipPos.z), Quaternion.LookRotation(desiredFacing, Vector3.up));

            if (ragObjTf != null) ragObjTf.SetParent(origParent, true);

            Actor.fallenOver = true;
            Actor.wantsToGetUp = false;
            ActorGetUpMethod?.Invoke(Actor, null);

            if (config.DebugLog.Value)
                Plugin.Log.LogInfo($"[Delirium] HandBackToRavenfieldGetUp {Actor.name} (faceUp={faceUp}, fallenElapsed={fallenElapsed:F2}s)");
        }

        public void FreezeDeadCorpse()
        {
            if (Phase == RagdollPhase.DeadFrozen) return;
            // Keep ragdollObject & kinematic rigidbodies in their final Delirium pose, free all native Rust memory!
            ReleaseNativeOnly();
            Phase = RagdollPhase.DeadFrozen;
        }

        public void ReleaseImmediate(bool restoreGame)
        {
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
            bool water = nm_agent_in_water(Agent) == 1;
            bool dead = nm_agent_is_dead(Agent) == 1;
            string agonyTag = deathAgonyActive ? $" Agony=S{deathAgonyStyle}({deathAgonyElapsed:F1}/{deathAgonyDuration:F1}s)" : "";
            return $"Phase={Phase}{agonyTag} | Bal={bal} Fall={fall} Catch={cf} Water={water} Dead={dead} | v={lastComVel.magnitude:F2}m/s";
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

        private void CaptureWeaponForRagdollHand()
        {
            RestoreWeaponParentTransform();
            if (Actor == null || IsPlayer || Actor.dead || Actor.ragdoll == null || Actor.controller == null)
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
            float cap = Mathf.Max(config.MaxBurstImpulse.Value, 40f);
            float remaining = Mathf.Max(0f, cap - burstImpulseUsed);
            if (remaining <= 0.5f)
            {
                // Still allow a subtle micro-recoil (5%) for rapid-fire feedback without launching
                float micro = Mathf.Min(desiredImpulse * 0.05f, 3.5f);
                burstImpulseUsed = Mathf.Min(cap, burstImpulseUsed + micro);
                return micro;
            }

            float granted = Mathf.Min(desiredImpulse, remaining);
            burstImpulseUsed += granted;
            return granted;
        }

        private void ApplyNaturalDistributedImpulse(int hitPart, Vector3 hitPoint, Vector3 dir, float totalImpulse, out float localImpulse, out float torsoImpulse)
        {
            localImpulse = 0f;
            torsoImpulse = 0f;
            if (Agent == IntPtr.Zero || totalImpulse <= 0.01f) return;

            hitPart = Mathf.Clamp(hitPart, 0, 20);
            float partMass = partMasses[hitPart] > 0.05f ? partMasses[hitPart] : 3f;
            bool isCore = hitPart == 0 || (hitPart >= 7 && hitPart <= 10);

            // Cap local instantaneous velocity jump on light limbs/head so joints never violently snap/twitch
            float maxDeltaV = isCore ? config.MaxLimbDeltaV.Value * 0.9f : config.MaxLimbDeltaV.Value;
            float maxLocalByMass = partMass * maxDeltaV;
            float preferredLocalShare = hitPart == 12 ? 0.42f : (isCore ? 0.65f : 0.35f);

            localImpulse = Mathf.Min(totalImpulse * preferredLocalShare, maxLocalByMass);
            torsoImpulse = Mathf.Max(0f, totalImpulse - localImpulse);

            if (localImpulse > 0.1f)
            {
                nm_agent_apply_impulse(Agent, hitPart, ToNmVec(hitPoint), ToNmVec(dir * localImpulse));
            }

            if (torsoImpulse > 0.1f)
            {
                // Route transferred momentum by hit zone to preserve natural lean/pitch torque:
                // - Upper body (chest/shoulders/arms/head): push Chest (85%) + Neck/UpperSpine (15%), NOT pelvis,
                //   so the torso clearly pitches & leans in the bullet direction over planted hips!
                // - Gut / pelvis (0, 7, 8): push Pelvis (65%) + MidSpine (35%) so the soldier doubles over.
                // - Legs (1..6): push Pelvis (80%) + Spine0 (20%) to sweep legs out naturally.
                var pA = new float[3];
                var pB = new float[3];
                int partA, partB;
                float shareA;
                if (hitPart >= 9)
                {
                    partA = 10; partB = 11; shareA = 0.85f;
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

        private void ReleaseNativeOnly()
        {
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
                nm_build_destroy(build);
                build = IntPtr.Zero;
            }
            if (WorldEntry != null)
            {
                runtime.ReleaseWorld(WorldEntry);
                WorldEntry = null;
            }
        }

        public void Dispose()
        {
            ReleaseImmediate(restoreGame: false);
        }
    }
}
