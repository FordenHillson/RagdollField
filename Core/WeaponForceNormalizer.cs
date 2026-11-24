using System;
using DeliriumRavenfield.Config;
using UnityEngine;

namespace DeliriumRavenfield.Core
{
    public struct NormalizedShot
    {
        public float TotalImpulse;
        public float TaskForce;
        public float Power01;
        public string WeaponSet;
        public Vector3 Direction;
        public float RawDamage;
        public float RawBalance;
        public float RawForce;
    }

    public static class WeaponForceNormalizer
    {
        public static string LastHitTelemetry { get; private set; } = "(no hits yet)";

        /// <summary>
        /// Converts arbitrary Ravenfield / Workshop modded gun DamageInfo into a physically natural
        /// Delirium impulse (N*s), shot reaction force, and NM taskparams weapon set.
        /// Uses log1p + tanh soft-knee compression so extreme modded weapons (e.g. dmg=9999, force=50000)
        /// still feel heavy and knock characters back cleanly without causing joint spasms.
        /// </summary>
        public static NormalizedShot NormalizeShot(DamageInfo info, ModConfig config, Vector3 fallbackDir)
        {
            float rawDmg = Mathf.Max(info.healthDamage, 0f);
            float rawBal = Mathf.Max(info.balanceDamage, 0f);
            float rawForce = info.impactForce.magnitude;

            // If a mod weapon sets near-zero impactForce, synthesize a reasonable baseline from damage
            float effectiveForce = rawForce > 1f ? rawForce : (rawDmg * 3.2f + rawBal * 1.5f);

            // Dimensionless log compression: gracefully handles anything from 5 dmg to 100,000 dmg
            double dmgScore = Math.Log(1.0 + rawDmg / 45.0);
            double balScore = Math.Log(1.0 + rawBal / 60.0);
            double forceScore = Math.Log(1.0 + effectiveForce / 180.0);

            double power = 0.45 * dmgScore + 0.25 * balScore + 0.30 * forceScore;
            if (info.isPiercing) power += 0.15;

            // Smooth asymptotic saturation into [0, 1)
            float t = (float)Math.Tanh(power * 0.62);

            float minImp = config.MinShotImpulse.Value;
            float maxImp = Mathf.Max(config.MaxShotImpulse.Value, minImp + 5f);
            float impulse = Mathf.Lerp(minImp, maxImp, t) * config.ShotForceScale.Value;

            // Hard safety ceiling even if user raises ShotForceScale
            impulse = Mathf.Clamp(impulse, 8f, maxImp * 1.35f);

            // Choose NM shot reaction profile & bullet reaction speed scaled across full weapon power spectrum
            string set;
            if (t < 0.25f && !info.isCriticalHit)
                set = "SMG_WEAPON";
            else if (t < 0.42f && !info.isCriticalHit)
                set = "PISTOL_WEAPON";
            else if (t < 0.68f)
                set = "POWERFUL_WEAPON";
            else if (t < 0.84f)
                set = "SHOTGUN";
            else
                set = "SNIPER_RIFLE";

            float taskForce = Mathf.Lerp(240f, 660f, t);

            // Clean shot direction and clamp extreme upward spikes from modded force vectors
            Vector3 dir = info.direction.sqrMagnitude > 1e-4f
                ? info.direction.normalized
                : (info.impactForce.sqrMagnitude > 1e-4f ? info.impactForce.normalized : fallbackDir);
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();

            // Prevent modded upward-biased vectors from launching ragdolls straight into orbit
            if (dir.y > 0.40f)
            {
                dir.y = Mathf.Lerp(0.40f, dir.y, 0.25f);
                dir.Normalize();
            }

            return new NormalizedShot
            {
                TotalImpulse = impulse,
                TaskForce = taskForce,
                Power01 = t,
                WeaponSet = set,
                Direction = dir,
                RawDamage = rawDmg,
                RawBalance = rawBal,
                RawForce = rawForce
            };
        }

        /// <summary>
        /// Normalizes melee / kick impacts into natural Delirium impulses (30..95 N*s).
        /// </summary>
        public static float NormalizeMeleeImpulse(DamageInfo info, ModConfig config)
        {
            float raw = Mathf.Max(info.impactForce.magnitude, info.balanceDamage * 2.5f + info.healthDamage * 1.5f);
            double score = Math.Log(1.0 + raw / 140.0);
            float t = (float)Math.Tanh(score * 0.65);
            float baseMin = info.isKick ? 55f : 32f;
            float baseMax = info.isKick ? 92f : 82f;
            return Mathf.Clamp(Mathf.Lerp(baseMin, baseMax, t) * config.MeleeForceScale.Value, 18f, 110f);
        }

        /// <summary>
        /// Normalizes explosion throw speed (m/s) into Delirium's stable range (7..18 m/s).
        /// </summary>
        public static float NormalizeExplosionSpeed(float rawForce, ModConfig config)
        {
            double score = Math.Log(1.0 + Mathf.Max(rawForce, 0f) / 250.0);
            float t = (float)Math.Tanh(score * 0.65);
            float maxSpd = config.MaxExplosionSpeed.Value;
            float speed = Mathf.Lerp(7.5f, maxSpd, t) * config.ExplosionForceScale.Value;
            return Mathf.Clamp(speed, 5f, maxSpd * 1.25f);
        }

        /// <summary>
        /// Normalizes arbitrary KnockOver/FallOver forces into safe impulses (15..85 N*s).
        /// </summary>
        public static float NormalizeKnockOverImpulse(float rawForceMag, ModConfig config)
        {
            double score = Math.Log(1.0 + Mathf.Max(rawForceMag, 0f) / 180.0);
            float t = (float)Math.Tanh(score * 0.62);
            return Mathf.Clamp(Mathf.Lerp(18f, config.MaxShotImpulse.Value * 1.1f, t), 12f, 95f);
        }

        public static void RecordTelemetry(NormalizedShot shot, float appliedTotal, float localPartImp, float torsoImp, int partIndex)
        {
            LastHitTelemetry =
                $"raw(dmg={shot.RawDamage:F0}, bal={shot.RawBalance:F0}, F={shot.RawForce:F0}, pwr={shot.Power01:F2}) -> " +
                $"{appliedTotal:F1} Ns [part {partIndex}: {localPartImp:F1}, body: {torsoImp:F1}] ({shot.WeaponSet})";
        }
    }
}
