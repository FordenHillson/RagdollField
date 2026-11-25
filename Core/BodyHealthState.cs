using System;
using System.Collections.Generic;
using DeliriumRavenfield.Config;
using UnityEngine;

namespace DeliriumRavenfield.Core
{
    public enum BodyZone
    {
        Head,
        ChestLeft,
        ChestOther,
        Stomach,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg
    }

    public enum WoundedCondition
    {
        Healthy,
        ArmOffhandDisabled,
        StomachHurt,
        ChestHurt,
        HoppingOneLeg,
        CrawlingOneLeg,
        CrawlingTwoLegs,
        FallenStomachClutch,
        Surrendering,
        SurrenderingGround,
        DyingHeart,
        DyingChest,
        DyingBleedout,
        DeadHeadshot
    }

    public struct ActiveWound
    {
        public int Part;
        public BodyZone Zone;
        public Vector3 LocalPos;
        public Vector3 LocalNormal;
        public float BleedRate;
        public float NextPulseTime;
    }

    public struct HitOutcome
    {
        public BodyZone Zone;
        public int Part;
        public bool Lethal;
        public bool InstantHeadshot;
        public bool EnterDyingStagger;
        public bool NewlyRuined;
        public WoundedCondition Condition;
        public float BloodRemaining;
    }

    public sealed class BodyHealthState
    {
        public Actor Actor { get; }
        public float MaxScale { get; private set; }

        public float HeadHp { get; private set; }
        public float HeartAccumDamage { get; private set; }
        public bool HeartFatal { get; private set; }

        public float ChestHp { get; private set; }
        public float ChestMaxHp { get; private set; }
        public bool ChestDamaged { get; private set; }

        public float StomachHp { get; private set; }
        public float StomachMaxHp { get; private set; }
        public bool StomachDamaged { get; private set; }

        public float LeftArmHp { get; private set; }
        public float LeftArmMaxHp { get; private set; }
        public float RightArmHp { get; private set; }
        public float RightArmMaxHp { get; private set; }

        public float LeftLegHp { get; private set; }
        public float LeftLegMaxHp { get; private set; }
        public float RightLegHp { get; private set; }
        public float RightLegMaxHp { get; private set; }

        public float Blood { get; private set; } = 100f;
        public float TotalBleedRate { get; private set; }
        public bool BleedingStoppedByMedkit { get; private set; }
        public int BleedingFollowUpHits { get; private set; }
        public int BleedingFinishThreshold { get; private set; } = 3;
        private float lastBleedingFollowUpTime = -10f;

        public bool IsFallenWithOneDeadLeg { get; set; }
        public bool IsFallenWhileSurrendering { get; set; }
        public bool HasDroppedWeaponForSurrender { get; set; }
        public bool AppliedAiIgnoreForSurrender { get; set; }

        public Actor LastAttacker { get; set; }
        public Vector3 LastThreatPos { get; set; }
        public int LastHitPart { get; private set; } = 10;
        public BodyZone LastHitZone { get; private set; } = BodyZone.ChestOther;
        public int PrimaryTorsoWoundPart { get; private set; } = 8;
        public Vector3 PrimaryTorsoWoundLocalPos { get; private set; }
        public Vector3 PrimaryTorsoWoundLocalNormal { get; private set; } = Vector3.forward;

        public WoundedCondition Condition { get; private set; } = WoundedCondition.Healthy;

        private readonly List<ActiveWound> activeWounds = new List<ActiveWound>(4);
        public IReadOnlyList<ActiveWound> ActiveWounds => activeWounds;

        public bool LeftLegRuined => LeftLegHp <= 0.01f;
        public bool RightLegRuined => RightLegHp <= 0.01f;
        public bool BothLegsRuined => LeftLegRuined && RightLegRuined;
        public bool AnyLegRuined => LeftLegRuined || RightLegRuined;
        public bool LeftArmRuined => LeftArmHp <= 0.01f;
        public bool RightArmRuined => RightArmHp <= 0.01f;
        public bool StomachRuined => StomachHp <= 0.01f;
        public bool ChestRuined => ChestHp <= 0.01f;
        public bool IsBleeding => TotalBleedRate > 0.01f && !BleedingStoppedByMedkit;
        public bool IsWounded => Condition != WoundedCondition.Healthy;
        public bool IsDeadByLocational =>
            Condition == WoundedCondition.DeadHeadshot ||
            Condition == WoundedCondition.DyingHeart ||
            Condition == WoundedCondition.DyingChest ||
            Condition == WoundedCondition.DyingBleedout;

        public float Weakness01 => Mathf.Clamp01((35f - Blood) / 35f);
        public float SurrenderKneel01 => Mathf.Clamp01((60f - Blood) / 48f);

        private readonly ModConfig config;

        public BodyHealthState(Actor actor, ModConfig config)
        {
            Actor = actor;
            this.config = config;
            Reset();
        }

        public void Reset()
        {
            float maxHp = Actor != null && Actor.maxHealth > 1f ? Actor.maxHealth : 100f;
            MaxScale = Mathf.Clamp(maxHp / 100f, 0.25f, 10f);

            HeadHp = 1f;
            HeartAccumDamage = 0f;
            HeartFatal = false;

            ChestMaxHp = Mathf.Max(15f, maxHp * config.ChestHpRatio.Value);
            ChestHp = ChestMaxHp;
            ChestDamaged = false;

            StomachMaxHp = Mathf.Max(15f, maxHp * config.StomachHpRatio.Value);
            StomachHp = StomachMaxHp;
            StomachDamaged = false;

            LeftArmMaxHp = Mathf.Max(12f, maxHp * config.ArmHpRatio.Value);
            LeftArmHp = LeftArmMaxHp;

            RightArmMaxHp = Mathf.Max(12f, maxHp * config.ArmHpRatio.Value);
            RightArmHp = RightArmMaxHp;

            LeftLegMaxHp = Mathf.Max(15f, maxHp * config.LegHpRatio.Value);
            LeftLegHp = LeftLegMaxHp;

            RightLegMaxHp = Mathf.Max(15f, maxHp * config.LegHpRatio.Value);
            RightLegHp = RightLegMaxHp;

            Blood = 100f;
            TotalBleedRate = 0f;
            BleedingStoppedByMedkit = false;
            BleedingFollowUpHits = 0;
            int minFinish = Mathf.Clamp(config.BleedingFinishHitsMin.Value, 1, 8);
            int maxFinish = Mathf.Clamp(config.BleedingFinishHitsMax.Value, minFinish, 8);
            BleedingFinishThreshold = UnityEngine.Random.Range(minFinish, maxFinish + 1);
            lastBleedingFollowUpTime = -10f;
            IsFallenWithOneDeadLeg = false;
            IsFallenWhileSurrendering = false;
            HasDroppedWeaponForSurrender = false;
            AppliedAiIgnoreForSurrender = false;
            LastAttacker = null;
            LastThreatPos = Vector3.zero;
            LastHitPart = 10;
            LastHitZone = BodyZone.ChestOther;
            PrimaryTorsoWoundPart = 8;
            PrimaryTorsoWoundLocalPos = Vector3.zero;
            PrimaryTorsoWoundLocalNormal = Vector3.forward;
            activeWounds.Clear();
            Condition = WoundedCondition.Healthy;
        }

        public static int EstimatePartWithoutDelirium(Actor actor, Vector3 hitPoint, bool isCritical)
        {
            if (isCritical) return 12;
            if (actor == null) return 10;

            try
            {
                if (actor.ragdoll != null)
                {
                    bool useRag = actor.ragdoll.IsRagdoll();
                    var map = new (HumanBodyBones bone, int part)[]
                    {
                        (HumanBodyBones.Head, 12),
                        (HumanBodyBones.Neck, 11),
                        (HumanBodyBones.Chest, 10),
                        (HumanBodyBones.UpperChest, 10),
                        (HumanBodyBones.Spine, 8),
                        (HumanBodyBones.Hips, 0),
                        (HumanBodyBones.LeftUpperArm, 14),
                        (HumanBodyBones.LeftLowerArm, 15),
                        (HumanBodyBones.LeftHand, 16),
                        (HumanBodyBones.RightUpperArm, 18),
                        (HumanBodyBones.RightLowerArm, 19),
                        (HumanBodyBones.RightHand, 20),
                        (HumanBodyBones.LeftUpperLeg, 1),
                        (HumanBodyBones.LeftLowerLeg, 2),
                        (HumanBodyBones.LeftFoot, 3),
                        (HumanBodyBones.RightUpperLeg, 4),
                        (HumanBodyBones.RightLowerLeg, 5),
                        (HumanBodyBones.RightFoot, 6),
                    };

                    float bestDist = float.MaxValue;
                    int bestPart = 10;
                    for (int i = 0; i < map.Length; i++)
                    {
                        Transform tf = null;
                        try
                        {
                            tf = useRag
                                ? actor.ragdoll.HumanBoneTransformRagdoll(map[i].bone)
                                : actor.ragdoll.HumanBoneTransformAnimated(map[i].bone);
                        }
                        catch { }
                        if (tf == null) continue;
                        float d = (tf.position - hitPoint).sqrMagnitude;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestPart = map[i].part;
                        }
                    }
                    if (bestDist < 4f) return bestPart;
                }
            }
            catch { }

            // Fallback height + lateral heuristic relative to actor root
            Vector3 local = actor.transform.InverseTransformPoint(hitPoint);
            if (local.y > 1.48f) return 12;
            if (local.y < 0.85f) return local.x < 0f ? 2 : 5;
            if (Mathf.Abs(local.x) > 0.24f && local.y > 1.0f) return local.x < 0f ? 14 : 18;
            if (local.y < 1.16f) return 8;
            return 10;
        }

        public static BodyZone ClassifyZone(Actor actor, int part, Vector3 worldHitPoint, bool isCritical)
        {
            if (isCritical || part == 11 || part == 12) return BodyZone.Head;
            if (part >= 1 && part <= 3) return BodyZone.LeftLeg;
            if (part >= 4 && part <= 6) return BodyZone.RightLeg;
            if (part >= 13 && part <= 16) return BodyZone.LeftArm;
            if (part >= 17 && part <= 20) return BodyZone.RightArm;
            if (part == 0 || part == 7 || part == 8) return BodyZone.Stomach;

            // Upper torso (part 9 or 10): distinguish anatomical Left Chest (Heart) from Right/Center Chest
            if (part == 10 && actor != null && worldHitPoint != Vector3.zero)
            {
                if (IsAnatomicalLeftChest(actor, worldHitPoint))
                    return BodyZone.ChestLeft;
            }
            return BodyZone.ChestOther;
        }

        private static bool IsAnatomicalLeftChest(Actor actor, Vector3 worldHitPoint)
        {
            Vector3 chestCenter = actor.CenterPosition() + Vector3.up * 0.22f;
            Vector3 bodyRight = actor.transform.right;
            Vector3 bodyUp = actor.transform.up;

            try
            {
                if (actor.ragdoll != null)
                {
                    bool useRag = actor.ragdoll.IsRagdoll();
                    Transform chestTf = useRag
                        ? actor.ragdoll.HumanBoneTransformRagdoll(HumanBodyBones.Chest)
                        : actor.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Chest);
                    Transform lSh = useRag
                        ? actor.ragdoll.HumanBoneTransformRagdoll(HumanBodyBones.LeftUpperArm)
                        : actor.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.LeftUpperArm);
                    Transform rSh = useRag
                        ? actor.ragdoll.HumanBoneTransformRagdoll(HumanBodyBones.RightUpperArm)
                        : actor.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.RightUpperArm);

                    if (chestTf != null) chestCenter = chestTf.position;
                    if (lSh != null && rSh != null)
                    {
                        Vector3 lr = rSh.position - lSh.position;
                        if (lr.sqrMagnitude > 1e-4f)
                        {
                            bodyRight = lr.normalized;
                            chestCenter = (lSh.position + rSh.position) * 0.5f - Vector3.up * 0.08f;
                        }
                    }
                }
            }
            catch { }

            Vector3 rel = worldHitPoint - chestCenter;
            float sideDot = Vector3.Dot(rel, bodyRight); // negative = anatomical left side (heart!)
            float upDot = Vector3.Dot(rel, bodyUp);
            return sideDot < -0.025f && upDot > -0.14f && upDot < 0.20f;
        }

        public HitOutcome ApplyHit(BodyZone zone, int part, float rawDamage, Vector3 hitPoint, Vector3 hitNormal, Actor attacker)
        {
            bool wasAlreadyBleeding = IsBleeding;
            float dmg = Mathf.Max(rawDamage, 1f);
            LastHitPart = Mathf.Clamp(part, 0, 20);
            LastHitZone = zone;
            if (attacker != null && attacker != Actor)
            {
                LastAttacker = attacker;
                LastThreatPos = attacker.CenterPosition();
            }
            else if (hitPoint != Vector3.zero && hitNormal.sqrMagnitude > 1e-4f)
            {
                LastThreatPos = hitPoint + hitNormal.normalized * 8f;
            }

            bool newlyRuined = false;
            bool instantHeadshot = false;
            bool enterDyingStagger = false;
            bool lethal = false;

            switch (zone)
            {
                case BodyZone.Head:
                    HeadHp = 0f;
                    instantHeadshot = true;
                    lethal = true;
                    break;

                case BodyZone.ChestLeft:
                    HeartAccumDamage += dmg;
                    ChestDamaged = true;
                    ChestHp = Mathf.Max(0f, ChestHp - dmg);
                    PrimaryTorsoWoundPart = 10;
                    if (HeartAccumDamage >= config.HeartFatalDamageThreshold.Value * MaxScale)
                    {
                        HeartFatal = true;
                        lethal = true;
                        enterDyingStagger = true;
                        AddOrUpdateWound(10, BodyZone.ChestLeft, hitPoint, hitNormal, config.BleedRateChest.Value);
                    }
                    else
                    {
                        AddOrUpdateWound(10, BodyZone.ChestOther, hitPoint, hitNormal, 0f);
                    }
                    break;

                case BodyZone.ChestOther:
                {
                    bool wasRuined = ChestRuined;
                    ChestDamaged = true;
                    ChestHp = Mathf.Max(0f, ChestHp - dmg);
                    PrimaryTorsoWoundPart = Mathf.Clamp(part, 9, 10);
                    if (!wasRuined && ChestRuined)
                    {
                        newlyRuined = true;
                        lethal = true;
                        enterDyingStagger = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(PrimaryTorsoWoundPart, BodyZone.ChestOther, hitPoint, hitNormal, config.BleedRateChest.Value);
                    }
                    break;
                }

                case BodyZone.Stomach:
                {
                    bool wasRuined = StomachRuined;
                    StomachDamaged = true;
                    StomachHp = Mathf.Max(0f, StomachHp - dmg);
                    PrimaryTorsoWoundPart = (part == 0 || part == 7 || part == 8) ? part : 8;
                    if (!wasRuined && StomachRuined)
                    {
                        newlyRuined = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(PrimaryTorsoWoundPart, BodyZone.Stomach, hitPoint, hitNormal, config.BleedRateStomach.Value);
                    }
                    else if (wasRuined)
                    {
                        // Extra follow-up shots into ruined stomach accelerate bleedout
                        Blood = Mathf.Max(0f, Blood - dmg * 0.35f);
                    }
                    break;
                }

                case BodyZone.LeftArm:
                {
                    bool wasRuined = LeftArmRuined;
                    LeftArmHp = Mathf.Max(0f, LeftArmHp - dmg);
                    if (!wasRuined && LeftArmRuined)
                    {
                        newlyRuined = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(Mathf.Clamp(part, 13, 16), BodyZone.LeftArm, hitPoint, hitNormal, config.BleedRateArm.Value);
                    }
                    else if (wasRuined)
                    {
                        Blood = Mathf.Max(0f, Blood - dmg * 0.25f);
                    }
                    break;
                }

                case BodyZone.RightArm:
                {
                    bool wasRuined = RightArmRuined;
                    RightArmHp = Mathf.Max(0f, RightArmHp - dmg);
                    if (!wasRuined && RightArmRuined)
                    {
                        newlyRuined = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(Mathf.Clamp(part, 17, 20), BodyZone.RightArm, hitPoint, hitNormal, config.BleedRateArm.Value);
                    }
                    else if (wasRuined)
                    {
                        Blood = Mathf.Max(0f, Blood - dmg * 0.25f);
                    }
                    break;
                }

                case BodyZone.LeftLeg:
                {
                    bool wasRuined = LeftLegRuined;
                    LeftLegHp = Mathf.Max(0f, LeftLegHp - dmg);
                    if (!wasRuined && LeftLegRuined)
                    {
                        newlyRuined = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(Mathf.Clamp(part, 1, 3), BodyZone.LeftLeg, hitPoint, hitNormal, config.BleedRateLeg.Value);
                    }
                    else if (wasRuined)
                    {
                        Blood = Mathf.Max(0f, Blood - dmg * 0.25f);
                    }
                    break;
                }

                case BodyZone.RightLeg:
                {
                    bool wasRuined = RightLegRuined;
                    RightLegHp = Mathf.Max(0f, RightLegHp - dmg);
                    if (!wasRuined && RightLegRuined)
                    {
                        newlyRuined = true;
                        BleedingStoppedByMedkit = false;
                        AddOrUpdateWound(Mathf.Clamp(part, 4, 6), BodyZone.RightLeg, hitPoint, hitNormal, config.BleedRateLeg.Value);
                    }
                    else if (wasRuined)
                    {
                        Blood = Mathf.Max(0f, Blood - dmg * 0.25f);
                    }
                    break;
                }
            }

            RecalculateBleedRate();

            // Follow-up finish mechanic: 2-3 additional shots while already bleeding out kills immediately!
            if (wasAlreadyBleeding && !lethal)
            {
                float now = Time.time;
                if (now - lastBleedingFollowUpTime >= 0.06f)
                {
                    lastBleedingFollowUpTime = now;
                    BleedingFollowUpHits++;
                    if (BleedingFollowUpHits >= BleedingFinishThreshold)
                    {
                        Blood = 0f;
                        lethal = true;
                        enterDyingStagger = false;
                    }
                }
            }

            if (Blood <= 0f && !lethal)
            {
                lethal = true;
            }
            ResolveCondition();

            return new HitOutcome
            {
                Zone = zone,
                Part = LastHitPart,
                Lethal = lethal,
                InstantHeadshot = instantHeadshot,
                EnterDyingStagger = enterDyingStagger,
                NewlyRuined = newlyRuined,
                Condition = Condition,
                BloodRemaining = Blood
            };
        }

        public void SetTorsoWoundLocalAnchor(int part, Vector3 localPos, Vector3 localNormal)
        {
            PrimaryTorsoWoundPart = Mathf.Clamp(part, 0, 20);
            PrimaryTorsoWoundLocalPos = localPos;
            PrimaryTorsoWoundLocalNormal = localNormal.sqrMagnitude > 1e-4f ? localNormal.normalized : Vector3.forward;
        }

        public void UpdateWoundLocalAnchor(BodyZone zone, int part, Vector3 localPos, Vector3 localNormal)
        {
            for (int i = 0; i < activeWounds.Count; i++)
            {
                if (activeWounds[i].Zone == zone)
                {
                    var w = activeWounds[i];
                    w.Part = part;
                    w.LocalPos = localPos;
                    w.LocalNormal = localNormal.sqrMagnitude > 1e-4f ? localNormal.normalized : Vector3.forward;
                    activeWounds[i] = w;
                    return;
                }
            }
        }

        private void AddOrUpdateWound(int part, BodyZone zone, Vector3 worldPos, Vector3 worldNormal, float bleedRate)
        {
            for (int i = 0; i < activeWounds.Count; i++)
            {
                if (activeWounds[i].Zone == zone)
                {
                    var existing = activeWounds[i];
                    existing.Part = part;
                    existing.BleedRate = Mathf.Max(existing.BleedRate, bleedRate);
                    activeWounds[i] = existing;
                    return;
                }
            }
            if (activeWounds.Count >= 4)
            {
                activeWounds.RemoveAt(0);
            }
            activeWounds.Add(new ActiveWound
            {
                Part = part,
                Zone = zone,
                LocalPos = Vector3.zero,
                LocalNormal = Vector3.forward,
                BleedRate = bleedRate,
                NextPulseTime = Time.time + 0.12f
            });
        }

        public void RecalculateBleedRate()
        {
            if (BleedingStoppedByMedkit)
            {
                TotalBleedRate = 0f;
                return;
            }
            float rate = 0f;
            if (LeftArmRuined) rate += config.BleedRateArm.Value;
            if (RightArmRuined) rate += config.BleedRateArm.Value;
            if (LeftLegRuined) rate += config.BleedRateLeg.Value;
            if (RightLegRuined) rate += config.BleedRateLeg.Value;
            if (StomachRuined) rate += config.BleedRateStomach.Value;
            if (ChestRuined || HeartFatal) rate += config.BleedRateChest.Value;
            TotalBleedRate = rate;
        }

        public bool TickBleed(float dt)
        {
            if (dt <= 0f || IsDeadByLocational) return false;
            if (!IsBleeding) return false;

            Blood = Mathf.Max(0f, Blood - TotalBleedRate * dt);
            if (Blood <= 0f)
            {
                Condition = WoundedCondition.DyingBleedout;
                return true;
            }
            ResolveCondition();
            return false;
        }

        public void OnHealed(float healAmount)
        {
            if (!config.EnableMedkitStopBleed.Value) return;
            if (IsDeadByLocational) return;

            BleedingStoppedByMedkit = true;
            TotalBleedRate = 0f;
            BleedingFollowUpHits = 0;
            Blood = Mathf.Min(100f, Blood + Mathf.Max(healAmount * 0.6f, 35f));

            // Light recovery on non-ruined torso bruises; ruined limbs stop bleeding
            if (!StomachRuined && StomachDamaged)
            {
                StomachHp = Mathf.Min(StomachMaxHp, StomachHp + healAmount * 0.5f);
                if (StomachHp >= StomachMaxHp * 0.85f) StomachDamaged = false;
            }
            if (!ChestRuined && ChestDamaged)
            {
                ChestHp = Mathf.Min(ChestMaxHp, ChestHp + healAmount * 0.5f);
                if (ChestHp >= ChestMaxHp * 0.85f) ChestDamaged = false;
            }
            ResolveCondition();
        }

        public void ResolveCondition()
        {
            if (HeadHp <= 0f)
            {
                Condition = WoundedCondition.DeadHeadshot;
                return;
            }
            if (HeartFatal)
            {
                Condition = WoundedCondition.DyingHeart;
                return;
            }
            if (ChestRuined)
            {
                Condition = WoundedCondition.DyingChest;
                return;
            }
            if (Blood <= 0f)
            {
                Condition = WoundedCondition.DyingBleedout;
                return;
            }
            if (RightArmRuined)
            {
                Condition = (BothLegsRuined || IsFallenWithOneDeadLeg || StomachRuined || IsFallenWhileSurrendering)
                    ? WoundedCondition.SurrenderingGround
                    : WoundedCondition.Surrendering;
                return;
            }
            if (StomachRuined)
            {
                Condition = WoundedCondition.FallenStomachClutch;
                return;
            }
            if (BothLegsRuined)
            {
                Condition = WoundedCondition.CrawlingTwoLegs;
                return;
            }
            if (AnyLegRuined)
            {
                Condition = IsFallenWithOneDeadLeg
                    ? WoundedCondition.CrawlingOneLeg
                    : WoundedCondition.HoppingOneLeg;
                return;
            }
            if (ChestDamaged)
            {
                Condition = WoundedCondition.ChestHurt;
                return;
            }
            if (StomachDamaged)
            {
                Condition = WoundedCondition.StomachHurt;
                return;
            }
            if (LeftArmRuined)
            {
                Condition = WoundedCondition.ArmOffhandDisabled;
                return;
            }
            Condition = WoundedCondition.Healthy;
        }

        public bool TryConsumeBloodPulse(int index, float now, out ActiveWound wound)
        {
            wound = default;
            if (index < 0 || index >= activeWounds.Count) return false;
            var w = activeWounds[index];
            if (w.BleedRate <= 0.01f || BleedingStoppedByMedkit) return false;
            if (now < w.NextPulseTime) return false;

            float interval = Mathf.Lerp(0.45f, 0.16f, Mathf.Clamp01(w.BleedRate / 12f));
            w.NextPulseTime = now + interval * UnityEngine.Random.Range(0.8f, 1.25f);
            activeWounds[index] = w;
            wound = w;
            return true;
        }

        public string GetCompactSummary()
        {
            string bleed = IsBleeding
                ? $" (-{TotalBleedRate:F1}/s, Hits:{BleedingFollowUpHits}/{BleedingFinishThreshold})"
                : (BleedingStoppedByMedkit ? " (Healed)" : "");
            return $"Cond={Condition} | Blood={Blood:F0}%{bleed}";
        }

        public string GetDetailedZonesLine()
        {
            return $"Chest:{ChestHp:F0}/{ChestMaxHp:F0} HeartDmg:{HeartAccumDamage:F0} | Belly:{StomachHp:F0}/{StomachMaxHp:F0}\n" +
                   $"Arms L:{LeftArmHp:F0} R:{RightArmHp:F0} | Legs L:{LeftLegHp:F0} R:{RightLegHp:F0}";
        }
    }
}
