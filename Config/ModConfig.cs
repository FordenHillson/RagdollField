using BepInEx.Configuration;
using UnityEngine;

namespace DeliriumRavenfield.Config
{
    public sealed class ModConfig
    {
        // [General]
        public ConfigEntry<bool> Enabled { get; }
        public ConfigEntry<bool> DebugLog { get; }
        public ConfigEntry<bool> DebugHud { get; }

        // [LOD]
        public ConfigEntry<float> ActiveDistance { get; }
        public ConfigEntry<int> MaxConcurrentAgents { get; }
        public ConfigEntry<float> FreezeDeadAfterSeconds { get; }
        public ConfigEntry<float> FreezeDeadVelocityThreshold { get; }

        // [Reactions]
        public ConfigEntry<bool> EnableLiveShotStagger { get; }
        public ConfigEntry<bool> HeadshotKills { get; }
        public ConfigEntry<float> ShotForceScale { get; }
        public ConfigEntry<float> AliveShotPushScale { get; }
        public ConfigEntry<float> MinShotImpulse { get; }
        public ConfigEntry<float> MaxShotImpulse { get; }
        public ConfigEntry<float> MaxBurstImpulse { get; }
        public ConfigEntry<float> MaxLimbDeltaV { get; }
        public ConfigEntry<float> MaxExplosionSpeed { get; }
        public ConfigEntry<float> MeleeForceScale { get; }
        public ConfigEntry<float> ExplosionForceScale { get; }
        public ConfigEntry<float> MuscleStrength { get; }
        public ConfigEntry<bool> StepLimit { get; }
        public ConfigEntry<bool> ArmsOutEarlyOnCar { get; }
        public ConfigEntry<bool> EnableExtraReactions { get; }
        public ConfigEntry<bool> EnableEnvironmentReactions { get; }
        public ConfigEntry<bool> EnableDyingStagger { get; }
        public ConfigEntry<float> DyingStaggerChance { get; }
        public ConfigEntry<float> DyingStaggerMinSeconds { get; }
        public ConfigEntry<float> DyingStaggerMaxSeconds { get; }
        public ConfigEntry<int> DyingStaggerMaxFollowUpHits { get; }
        public ConfigEntry<bool> EnableDyingBleed { get; }
        public ConfigEntry<bool> EnableBlindFire { get; }
        public ConfigEntry<float> BlindFireChance { get; }
        public ConfigEntry<float> BlindFireMaxSeconds { get; }
        public ConfigEntry<float> BlindFireWildSpreadDegrees { get; }
        public ConfigEntry<bool> EnableDeathAgony { get; }
        public ConfigEntry<float> DeathAgonyChance { get; }
        public ConfigEntry<float> DeathAgonyMinSeconds { get; }
        public ConfigEntry<float> DeathAgonyMaxSeconds { get; }
        public ConfigEntry<float> DeathAgonyIntensity { get; }
        public ConfigEntry<string> Overrides { get; }

        // [StaggerAndGetUp]
        public ConfigEntry<float> MinDamageForLiveStagger { get; }
        public ConfigEntry<float> StaggerRecoverSeconds { get; }
        public ConfigEntry<float> LyingStillBeforeGetUp { get; }
        public ConfigEntry<float> MaxFallenBeforeGetUp { get; }
        public ConfigEntry<float> EntryBlendSeconds { get; }
        public ConfigEntry<float> GetUpBlendSeconds { get; }
        public ConfigEntry<float> MaxStaggerTimeBeforeForceRecover { get; }

        // [Environment]
        public ConfigEntry<bool> EnableVehicles { get; }
        public ConfigEntry<bool> EnableWaterBuoyancy { get; }
        public ConfigEntry<bool> EnableNearMissFlinch { get; }
        public ConfigEntry<float> NearMissRadius { get; }
        public ConfigEntry<float> NearMissDistanceLimit { get; }
        public ConfigEntry<bool> EnableEnginePhysicsColliders { get; }
        public ConfigEntry<bool> EnableCorpseShootAndWake { get; }

        // [Skeleton]
        public ConfigEntry<string> SizeMode { get; }
        public ConfigEntry<bool> AdaptiveTuning { get; }
        public ConfigEntry<string> BodyPreset { get; }
        public ConfigEntry<string> BoneMap { get; }

        // [LocationalHealth]
        public ConfigEntry<bool> EnableLocationalHealth { get; }
        public ConfigEntry<float> LegHpRatio { get; }
        public ConfigEntry<float> ArmHpRatio { get; }
        public ConfigEntry<float> StomachHpRatio { get; }
        public ConfigEntry<float> ChestHpRatio { get; }
        public ConfigEntry<float> HeartFatalDamageThreshold { get; }
        public ConfigEntry<float> BleedRateArm { get; }
        public ConfigEntry<float> BleedRateLeg { get; }
        public ConfigEntry<float> BleedRateStomach { get; }
        public ConfigEntry<float> BleedRateChest { get; }
        public ConfigEntry<bool> EnableSurrenderAiIgnore { get; }
        public ConfigEntry<bool> EnableMedkitStopBleed { get; }
        public ConfigEntry<float> WoundedSpreadMultiplier { get; }
        public ConfigEntry<int> BleedingFinishHitsMin { get; }
        public ConfigEntry<int> BleedingFinishHitsMax { get; }

        // [TestRange]
        public ConfigEntry<KeyCode> UiHotkey { get; }
        public ConfigEntry<KeyCode> TestRangeHotkey { get; }
        public ConfigEntry<KeyCode> AimPushHotkey { get; }
        public ConfigEntry<KeyCode> ReleaseAllHotkey { get; }
        public ConfigEntry<KeyCode> SpawnDummyHotkey { get; }
        public ConfigEntry<float> AimPushForce { get; }
        public ConfigEntry<bool> UiStartOpen { get; }
        public ConfigEntry<bool> ShowMainMenuTestButton { get; }
        public ConfigEntry<bool> TestArenaAutoRespawn { get; }
        public ConfigEntry<float> TestArenaRespawnDelay { get; }

        public ModConfig(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Master toggle for Delirium active ragdolls.");
            DebugLog = cfg.Bind("General", "DebugLog", false, "Log detailed lifecycle and reaction events to BepInEx log.");
            DebugHud = cfg.Bind("General", "DebugHud", false, "Show compact on-screen performance & target diagnostics.");

            ActiveDistance = cfg.Bind("LOD", "ActiveDistance", 60f,
                new ConfigDescription("Maximum distance from camera (m) to run Delirium physics. Distant actors use vanilla ragdolls.",
                    new AcceptableValueRange<float>(15f, 300f)));
            MaxConcurrentAgents = cfg.Bind("LOD", "MaxConcurrentAgents", 18,
                new ConfigDescription("Maximum simultaneous active Delirium ragdolls. Settled corpses freeze to free slots.",
                    new AcceptableValueRange<int>(2, 64)));
            FreezeDeadAfterSeconds = cfg.Bind("LOD", "FreezeDeadAfterSeconds", 2.0f,
                new ConfigDescription("Seconds a dead ragdoll must stay nearly still before freezing its pose and freeing its Delirium slot.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            FreezeDeadVelocityThreshold = cfg.Bind("LOD", "FreezeDeadVelocityThreshold", 0.22f,
                new ConfigDescription("Center-of-mass speed (m/s) below which a dead ragdoll counts as settled.",
                    new AcceptableValueRange<float>(0.05f, 1.0f)));

            EnableLiveShotStagger = cfg.Bind("Reactions", "EnableLiveShotStagger", true,
                "Alive bots react immediately to gunshots with Delirium stagger, balance stepping, and wound clutching.");
            HeadshotKills = cfg.Bind("Reactions", "HeadshotKills", true,
                "Shots striking the head trigger Delirium's fatal headshot reaction.");
            ShotForceScale = cfg.Bind("Reactions", "ShotForceScale", 1.0f,
                new ConfigDescription("Multiplier for normalized bullet impact impulses into Delirium.",
                    new AcceptableValueRange<float>(0.2f, 3.0f)));
            AliveShotPushScale = cfg.Bind("Reactions", "AliveShotPushScale", 0.78f,
                new ConfigDescription("Impulse scale for non-lethal shots on standing bots so they visibly stagger backwards and clutch wounds before going down.",
                    new AcceptableValueRange<float>(0.15f, 1.5f)));
            MinShotImpulse = cfg.Bind("Reactions", "MinShotImpulse", 16f,
                new ConfigDescription("Minimum normalized impulse (N*s) for light SMG/pistol hits.",
                    new AcceptableValueRange<float>(5f, 40f)));
            MaxShotImpulse = cfg.Bind("Reactions", "MaxShotImpulse", 72f,
                new ConfigDescription("Soft-cap normalized single-shot impulse (N*s) so extreme modded guns knock back naturally without twitching.",
                    new AcceptableValueRange<float>(30f, 140f)));
            MaxBurstImpulse = cfg.Bind("Reactions", "MaxBurstImpulse", 260f,
                new ConfigDescription("Maximum combined impulse (N*s) within a short burst window (high enough for realistic Playroom rapid-fire bursts while capping extreme modded guns).",
                    new AcceptableValueRange<float>(60f, 500f)));
            MaxLimbDeltaV = cfg.Bind("Reactions", "MaxLimbDeltaV", 8.5f,
                new ConfigDescription("Max instantaneous velocity jump (m/s) applied to a light limb/head; excess impulse transfers naturally into torso/pelvis.",
                    new AcceptableValueRange<float>(3.0f, 16.0f)));
            MaxExplosionSpeed = cfg.Bind("Reactions", "MaxExplosionSpeed", 16f,
                new ConfigDescription("Soft-cap throw speed (m/s) for explosions so modded blast weapons stay natural.",
                    new AcceptableValueRange<float>(8f, 28f)));
            MeleeForceScale = cfg.Bind("Reactions", "MeleeForceScale", 1.0f,
                new ConfigDescription("Multiplier for melee/kick impact impulses into Delirium.",
                    new AcceptableValueRange<float>(0.2f, 4.0f)));
            ExplosionForceScale = cfg.Bind("Reactions", "ExplosionForceScale", 1.0f,
                new ConfigDescription("Multiplier for explosion throw speed into Delirium.",
                    new AcceptableValueRange<float>(0.2f, 3.0f)));
            MuscleStrength = cfg.Bind("Reactions", "MuscleStrength", 1.0f,
                new ConfigDescription("Global Delirium muscle strength multiplier (1.0 = default, >1 tougher, <1 looser).",
                    new AcceptableValueRange<float>(0.2f, 3.0f)));
            StepLimit = cfg.Bind("Reactions", "StepLimit", true,
                "When on, balancing runs out of recovery steps on hard hits and drops the character naturally.");
            ArmsOutEarlyOnCar = cfg.Bind("Reactions", "ArmsOutEarlyOnCar", true,
                "Throw arms out early when bracing against an approaching vehicle.");
            EnableExtraReactions = cfg.Bind("Reactions", "EnableExtraReactions", false,
                "Enable experimental Playroom extra reactions (arms windmill, curl on shot). Off avoids floor spasms.");
            EnableEnvironmentReactions = cfg.Bind("Reactions", "EnableEnvironmentReactions", true,
                "Enable Delirium muscle-driven environment reactions during stagger and dying collapse (wall bracing NM_XR_WALL, ledge stumble NM_XR_LEDGE, smart fall, stairs roll, weakened catchFall).");
            EnableDyingStagger = cfg.Bind("Reactions", "EnableDyingStagger", true,
                "Non-headshot, non-explosion fatal hits enter standing dying stagger with wound-clutching and gradual muscle decay before collapsing.");
            DyingStaggerChance = cfg.Bind("Reactions", "DyingStaggerChance", 0.85f,
                new ConfigDescription("Probability (0-1) that an eligible fatal hit enters dying stagger + wound clutching.",
                    new AcceptableValueRange<float>(0f, 1f)));
            DyingStaggerMinSeconds = cfg.Bind("Reactions", "DyingStaggerMinSeconds", 1.2f,
                new ConfigDescription("Minimum duration (s) of standing dying stagger before muscles give out.",
                    new AcceptableValueRange<float>(0.5f, 4.0f)));
            DyingStaggerMaxSeconds = cfg.Bind("Reactions", "DyingStaggerMaxSeconds", 2.8f,
                new ConfigDescription("Maximum duration (s) of standing dying stagger before collapsing.",
                    new AcceptableValueRange<float>(1.0f, 6.0f)));
            DyingStaggerMaxFollowUpHits = cfg.Bind("Reactions", "DyingStaggerMaxFollowUpHits", 2,
                new ConfigDescription("Number of follow-up body hits during dying stagger before forcing immediate collapse (headshots always drop immediately).",
                    new AcceptableValueRange<int>(1, 6)));
            EnableDyingBleed = cfg.Bind("Reactions", "EnableDyingBleed", true,
                "Emit pulsing blood drops and spurts from the wound bone during dying stagger and early collapse.");
            EnableBlindFire = cfg.Bind("Reactions", "EnableBlindFire", true,
                "Shot bots have a chance to accidentally discharge their firearm in wild erratic directions while startled, staggering, or falling.");
            BlindFireChance = cfg.Bind("Reactions", "BlindFireChance", 0.45f,
                new ConfigDescription("Probability (0-1) that a shot bot carrying a loaded gun accidentally pulls the trigger while staggering or collapsing.",
                    new AcceptableValueRange<float>(0f, 1f)));
            BlindFireMaxSeconds = cfg.Bind("Reactions", "BlindFireMaxSeconds", 0.65f,
                new ConfigDescription("Maximum duration (s) of an accidental reflex burst on full-auto weapons (semi-auto/shotguns fire 1 sudden round).",
                    new AcceptableValueRange<float>(0.15f, 1.5f)));
            BlindFireWildSpreadDegrees = cfg.Bind("Reactions", "BlindFireWildSpreadDegrees", 38f,
                new ConfigDescription("Extra wild angular deflection (degrees) added on top of the flailing ragdoll muzzle direction during accidental discharge.",
                    new AcceptableValueRange<float>(10f, 80f)));
            EnableDeathAgony = cfg.Bind("Reactions", "EnableDeathAgony", true,
                "Non-headshot fatal hits trigger subtle dying movement (writhing, curling, weak reaching) before going fully limp.");
            DeathAgonyChance = cfg.Bind("Reactions", "DeathAgonyChance", 0.75f,
                new ConfigDescription("Probability (0-1) that a non-headshot, non-explosion death triggers final-breath movement.",
                    new AcceptableValueRange<float>(0f, 1f)));
            DeathAgonyMinSeconds = cfg.Bind("Reactions", "DeathAgonyMinSeconds", 2.2f,
                new ConfigDescription("Minimum duration (s) of dying movement before expiring.",
                    new AcceptableValueRange<float>(0.5f, 6f)));
            DeathAgonyMaxSeconds = cfg.Bind("Reactions", "DeathAgonyMaxSeconds", 4.2f,
                new ConfigDescription("Maximum duration (s) of dying movement before expiring.",
                    new AcceptableValueRange<float>(1f, 10f)));
            DeathAgonyIntensity = cfg.Bind("Reactions", "DeathAgonyIntensity", 1.0f,
                new ConfigDescription("Strength/amplitude multiplier for dying agony movement (0.3 = very subtle, 1.5 = dramatic).",
                    new AcceptableValueRange<float>(0.3f, 1.5f)));
            Overrides = cfg.Bind("Reactions", "Overrides", "",
                "Optional Delirium behaviour overrides: behaviour.param=value, comma separated (e.g. shot.bodyStiffness=8).");

            MinDamageForLiveStagger = cfg.Bind("StaggerAndGetUp", "MinDamageForLiveStagger", 12f,
                new ConfigDescription("Minimum Ravenfield health+balance damage to trigger live Delirium stagger on standing bots.",
                    new AcceptableValueRange<float>(0f, 100f)));
            StaggerRecoverSeconds = cfg.Bind("StaggerAndGetUp", "StaggerRecoverSeconds", 1.1f,
                new ConfigDescription("Seconds a standing/balancing staggered bot stays settled before smoothly returning to combat.",
                    new AcceptableValueRange<float>(0.3f, 5.0f)));
            LyingStillBeforeGetUp = cfg.Bind("StaggerAndGetUp", "LyingStillBeforeGetUp", 0.8f,
                new ConfigDescription("Seconds a fallen alive bot lies still before handing back to Ravenfield's get-up animation.",
                    new AcceptableValueRange<float>(0.2f, 5.0f)));
            MaxFallenBeforeGetUp = cfg.Bind("StaggerAndGetUp", "MaxFallenBeforeGetUp", 3.0f,
                new ConfigDescription("Failsafe max seconds a fallen alive bot stays on the ground before getting back up.",
                    new AcceptableValueRange<float>(1.0f, 10.0f)));
            EntryBlendSeconds = cfg.Bind("StaggerAndGetUp", "EntryBlendSeconds", 0.16f,
                new ConfigDescription("Smooth blend duration (s) when transitioning from holding/aiming a weapon into Delirium injury stagger.",
                    new AcceptableValueRange<float>(0.0f, 0.40f)));
            GetUpBlendSeconds = cfg.Bind("StaggerAndGetUp", "GetUpBlendSeconds", 0.35f,
                new ConfigDescription("Blend duration (s) when returning from Delirium standing stagger back to Ravenfield animation.",
                    new AcceptableValueRange<float>(0.1f, 1.5f)));
            MaxStaggerTimeBeforeForceRecover = cfg.Bind("StaggerAndGetUp", "MaxStaggerTimeBeforeForceRecover", 3.5f,
                new ConfigDescription("Failsafe max seconds in alive standing stagger before forcing recovery back to combat.",
                    new AcceptableValueRange<float>(1.5f, 10.0f)));

            EnableVehicles = cfg.Bind("Environment", "EnableVehicles", true,
                "Sync moving Ravenfield vehicles to Delirium for pre-impact bracing and realistic vehicle hits.");
            EnableWaterBuoyancy = cfg.Bind("Environment", "EnableWaterBuoyancy", true,
                "Sync Ravenfield water level to Delirium for realistic floating in water.");
            EnableNearMissFlinch = cfg.Bind("Environment", "EnableNearMissFlinch", true,
                "Detect bullets whizzing past nearby alive bots and trigger Delirium near-miss flinch.");
            NearMissRadius = cfg.Bind("Environment", "NearMissRadius", 1.6f,
                new ConfigDescription("Radius (m) around bot head/chest to trigger near-miss flinch.",
                    new AcceptableValueRange<float>(0.5f, 3.5f)));
            NearMissDistanceLimit = cfg.Bind("Environment", "NearMissDistanceLimit", 45f,
                new ConfigDescription("Max distance from camera (m) to process near-miss flinch checks.",
                    new AcceptableValueRange<float>(10f, 120f)));
            EnableEnginePhysicsColliders = cfg.Bind("Environment", "EnableEnginePhysicsColliders", true,
                "Sync 21-part kinematic Unity colliders in FixedUpdate (Delirium EnginePhysics) so dying/dead ragdolls physically push loose props and interact with triggers without snagging the player.");
            EnableCorpseShootAndWake = cfg.Bind("Environment", "EnableCorpseShootAndWake", true,
                "Allow bullets, melee, explosions, and vehicles to hit dying/dead corpses and wake frozen corpses back into Delirium simulation.");

            SizeMode = cfg.Bind("Skeleton", "SizeMode", "Auto",
                new ConfigDescription("Auto (recommended), Real (exact bone scale), or Homer (person-scaled world).",
                    new AcceptableValueList<string>("Auto", "Real", "Homer")));
            AdaptiveTuning = cfg.Bind("Skeleton", "AdaptiveTuning", true,
                "Automatically weigh skin and background-tune balance per Workshop skin.");
            BodyPreset = cfg.Bind("Skeleton", "BodyPreset", "Fitted",
                new ConfigDescription("Fitted (uses skeleton proportions) or a Delirium body preset.",
                    new AcceptableValueList<string>("Fitted", "Small", "Average", "Large", "Lanky", "Fat", "Stocky", "Waddler", "Guard", "Brute", "Brawler", "Bruiser")));
            BoneMap = cfg.Bind("Skeleton", "BoneMap", "",
                "Custom Workshop skin bone overrides: BoneName=Part pairs (e.g. Pelvis_jnt=Buttocks, Tail=None).");

            EnableLocationalHealth = cfg.Bind("LocationalHealth", "EnableLocationalHealth", true,
                "Enable locational body-part health, bleedout, hopping/crawling, wound clutching, and gun-arm surrender on AI bots.");
            LegHpRatio = cfg.Bind("LocationalHealth", "LegHpRatio", 0.45f,
                new ConfigDescription("Each leg's HP as a fraction of actor maxHealth.",
                    new AcceptableValueRange<float>(0.15f, 1.0f)));
            ArmHpRatio = cfg.Bind("LocationalHealth", "ArmHpRatio", 0.40f,
                new ConfigDescription("Each arm's HP as a fraction of actor maxHealth.",
                    new AcceptableValueRange<float>(0.15f, 1.0f)));
            StomachHpRatio = cfg.Bind("LocationalHealth", "StomachHpRatio", 0.55f,
                new ConfigDescription("Stomach HP as a fraction of actor maxHealth.",
                    new AcceptableValueRange<float>(0.20f, 1.2f)));
            ChestHpRatio = cfg.Bind("LocationalHealth", "ChestHpRatio", 0.60f,
                new ConfigDescription("Right/center chest HP as a fraction of actor maxHealth.",
                    new AcceptableValueRange<float>(0.20f, 1.2f)));
            HeartFatalDamageThreshold = cfg.Bind("LocationalHealth", "HeartFatalDamageThreshold", 15f,
                new ConfigDescription("Minimum cumulative damage on anatomical left chest (heart) to trigger fatal dying stagger.",
                    new AcceptableValueRange<float>(1f, 60f)));
            BleedRateArm = cfg.Bind("LocationalHealth", "BleedRateArm", 5.5f,
                new ConfigDescription("Blood drain per second (out of 100) per ruined arm.",
                    new AcceptableValueRange<float>(1f, 25f)));
            BleedRateLeg = cfg.Bind("LocationalHealth", "BleedRateLeg", 6.0f,
                new ConfigDescription("Blood drain per second (out of 100) per ruined leg.",
                    new AcceptableValueRange<float>(1f, 25f)));
            BleedRateStomach = cfg.Bind("LocationalHealth", "BleedRateStomach", 9.5f,
                new ConfigDescription("Blood drain per second (out of 100) when stomach reaches 0 HP.",
                    new AcceptableValueRange<float>(1f, 30f)));
            BleedRateChest = cfg.Bind("LocationalHealth", "BleedRateChest", 10.0f,
                new ConfigDescription("Blood drain per second (out of 100) when chest reaches 0 HP.",
                    new AcceptableValueRange<float>(1f, 30f)));
            EnableSurrenderAiIgnore = cfg.Bind("LocationalHealth", "EnableSurrenderAiIgnore", true,
                "When a bot drops its gun and surrenders (gun arm ruined), enemy AI stops targeting it while the player can still shoot.");
            EnableMedkitStopBleed = cfg.Bind("LocationalHealth", "EnableMedkitStopBleed", true,
                "Medipack healing stops active bleeding and restores blood.");
            WoundedSpreadMultiplier = cfg.Bind("LocationalHealth", "WoundedSpreadMultiplier", 2.5f,
                new ConfigDescription("Extra aim spread multiplier while firing one-handed, hopping, clutching, or crawling.",
                    new AcceptableValueRange<float>(1.0f, 6.0f)));
            BleedingFinishHitsMin = cfg.Bind("LocationalHealth", "BleedingFinishHitsMin", 2,
                new ConfigDescription("Minimum follow-up hits on an actively bleeding bot to kill them immediately.",
                    new AcceptableValueRange<int>(1, 8)));
            BleedingFinishHitsMax = cfg.Bind("LocationalHealth", "BleedingFinishHitsMax", 3,
                new ConfigDescription("Maximum follow-up hits on an actively bleeding bot to kill them immediately.",
                    new AcceptableValueRange<int>(1, 8)));

            UiHotkey = cfg.Bind("TestRange", "UiHotkey", KeyCode.F7, "Toggle the in-game Delirium Test Panel.");
            TestRangeHotkey = cfg.Bind("TestRange", "TestRangeHotkey", KeyCode.F8, "Toggle Test Range mode (Pure Dummy <-> Normal AI).");
            AimPushHotkey = cfg.Bind("TestRange", "AimPushHotkey", KeyCode.F9, "Push/ragdoll the bot under your crosshair.");
            ReleaseAllHotkey = cfg.Bind("TestRange", "ReleaseAllHotkey", KeyCode.F10, "Release all active Delirium ragdolls back to Ravenfield.");
            SpawnDummyHotkey = cfg.Bind("TestRange", "SpawnDummyHotkey", KeyCode.F6, "Spawn/move a stationary enemy dummy bot at your crosshair.");
            AimPushForce = cfg.Bind("TestRange", "AimPushForce", 60f,
                new ConfigDescription("Impulse (N s) applied when pressing AimPushHotkey.",
                    new AcceptableValueRange<float>(10f, 300f)));
            UiStartOpen = cfg.Bind("TestRange", "UiStartOpen", false, "Open the Delirium Test Panel automatically on game start.");
            ShowMainMenuTestButton = cfg.Bind("TestRange", "ShowMainMenuTestButton", true,
                "Show a 1-click 'Launch Delirium Test Range' button on the Ravenfield Main Menu.");
            TestArenaAutoRespawn = cfg.Bind("TestRange", "TestArenaAutoRespawn", true,
                "Automatically respawn dead station dummies back at their test positions on Delirium Test Range.");
            TestArenaRespawnDelay = cfg.Bind("TestRange", "TestArenaRespawnDelay", 4.5f,
                new ConfigDescription("Seconds after a test dummy dies before auto-respawning at its station slot.",
                    new AcceptableValueRange<float>(1.0f, 30.0f)));
        }

        public void ResetGameplayDefaults()
        {
            ActiveDistance.Value = 60f;
            MaxConcurrentAgents.Value = 18;
            FreezeDeadAfterSeconds.Value = 2.0f;
            FreezeDeadVelocityThreshold.Value = 0.22f;

            MuscleStrength.Value = 1.0f;
            ShotForceScale.Value = 1.0f;
            AliveShotPushScale.Value = 0.78f;
            MinShotImpulse.Value = 16f;
            MaxShotImpulse.Value = 72f;
            MaxBurstImpulse.Value = 92f;
            MaxLimbDeltaV.Value = 5.5f;
            ExplosionForceScale.Value = 1.0f;
            MaxExplosionSpeed.Value = 16f;
            MeleeForceScale.Value = 1.0f;
            EnableExtraReactions.Value = false;
            EnableEnvironmentReactions.Value = true;
            EnableDyingStagger.Value = true;
            DyingStaggerChance.Value = 0.85f;
            DyingStaggerMinSeconds.Value = 1.2f;
            DyingStaggerMaxSeconds.Value = 2.8f;
            DyingStaggerMaxFollowUpHits.Value = 2;
            EnableDyingBleed.Value = true;
            EnableBlindFire.Value = true;
            BlindFireChance.Value = 0.45f;
            BlindFireMaxSeconds.Value = 0.65f;
            BlindFireWildSpreadDegrees.Value = 38f;
            EnableEnginePhysicsColliders.Value = true;
            EnableCorpseShootAndWake.Value = true;
            EnableDeathAgony.Value = true;
            DeathAgonyChance.Value = 0.75f;
            DeathAgonyMinSeconds.Value = 2.2f;
            DeathAgonyMaxSeconds.Value = 4.2f;
            DeathAgonyIntensity.Value = 1.0f;

            StaggerRecoverSeconds.Value = 1.1f;
            LyingStillBeforeGetUp.Value = 0.8f;
            MaxFallenBeforeGetUp.Value = 3.0f;
            EntryBlendSeconds.Value = 0.16f;

            EnableLocationalHealth.Value = true;
            LegHpRatio.Value = 0.45f;
            ArmHpRatio.Value = 0.40f;
            StomachHpRatio.Value = 0.55f;
            ChestHpRatio.Value = 0.60f;
            HeartFatalDamageThreshold.Value = 15f;
            BleedRateArm.Value = 5.5f;
            BleedRateLeg.Value = 6.0f;
            BleedRateStomach.Value = 9.5f;
            BleedRateChest.Value = 10.0f;
            EnableSurrenderAiIgnore.Value = true;
            EnableMedkitStopBleed.Value = true;
            WoundedSpreadMultiplier.Value = 2.5f;
            BleedingFinishHitsMin.Value = 2;
            BleedingFinishHitsMax.Value = 3;
        }
    }
}
