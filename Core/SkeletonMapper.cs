using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Delirium;
using DeliriumRavenfield.Config;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    public sealed class RigBuildResult
    {
        public Transform Hips;
        public Transform[] Bones;
        public int[] Parents;
        public int[] Parts;
        public float[] RestFrames;
        public float[] FitJoints;
        public IntPtr Build;
        public float Scale;
        public string SkinKey;
        public string CheckReport;
        public int KnownBindPoseCount;
        public float FittedWeightKg;
        public float FinalWeightKg;
    }

    public sealed class SkeletonMapper
    {
        private readonly ModConfig config;
        private readonly Dictionary<string, IntPtr> tunedBuilds = new Dictionary<string, IntPtr>();
        private readonly HashSet<string> warnedSkins = new HashSet<string>();
        private readonly HashSet<string> loggedSkins = new HashSet<string>();
        private readonly object tuneLock = new object();

        // Standard Unity HumanBodyBones -> Delirium 21-part indices
        private static readonly (HumanBodyBones bone, int part)[] HumanBoneToPart =
        {
            (HumanBodyBones.Hips, 0),
            (HumanBodyBones.LeftUpperLeg, 1),
            (HumanBodyBones.LeftLowerLeg, 2),
            (HumanBodyBones.LeftFoot, 3),
            (HumanBodyBones.RightUpperLeg, 4),
            (HumanBodyBones.RightLowerLeg, 5),
            (HumanBodyBones.RightFoot, 6),
            (HumanBodyBones.Spine, 7),
            (HumanBodyBones.Chest, 9),
            (HumanBodyBones.UpperChest, 10),
            (HumanBodyBones.Neck, 11),
            (HumanBodyBones.Head, 12),
            (HumanBodyBones.LeftShoulder, 13),
            (HumanBodyBones.LeftUpperArm, 14),
            (HumanBodyBones.LeftLowerArm, 15),
            (HumanBodyBones.LeftHand, 16),
            (HumanBodyBones.RightShoulder, 17),
            (HumanBodyBones.RightUpperArm, 18),
            (HumanBodyBones.RightLowerArm, 19),
            (HumanBodyBones.RightHand, 20),
        };

        public SkeletonMapper(ModConfig config)
        {
            this.config = config;
        }

        public void DisposeAllTunedBuilds()
        {
            lock (tuneLock)
            {
                foreach (var ptr in tunedBuilds.Values)
                {
                    if (ptr != IntPtr.Zero) nm_build_destroy(ptr);
                }
                tunedBuilds.Clear();
            }
        }

        public RigBuildResult BuildRigForActor(Actor actor, IntPtr baseBuild, float groundY)
        {
            if (actor == null || actor.ragdoll == null || actor.ragdoll.ragdollObject == null)
                return null;

            var ragdollObj = actor.ragdoll.ragdollObject;
            var boneOverrides = ParseBoneMap(config.BoneMap.Value);

            // 1. Collect HumanBodyBones -> ragdoll Transform map from ActiveRaggy
            var humanMap = new Dictionary<Transform, int>();
            Transform hips = null;
            foreach (var (hb, part) in HumanBoneToPart)
            {
                Transform t = null;
                try { t = actor.ragdoll.HumanBoneTransformRagdoll(hb); } catch { }
                if (t != null)
                {
                    if (!humanMap.ContainsKey(t)) humanMap[t] = part;
                    if (part == 0 && hips == null) hips = t;
                }
            }

            if (hips == null && actor.hipRigidbody != null)
                hips = actor.hipRigidbody.transform;

            // Collect SkinnedMeshRenderers across the whole Actor (ragdollObject + actor root + explicit skinnedRendererRagdoll)
            var rendererSet = new HashSet<SkinnedMeshRenderer>();
            if (actor.skinnedRendererRagdoll != null) rendererSet.Add(actor.skinnedRendererRagdoll);
            if (actor.skinnedRenderer != null) rendererSet.Add(actor.skinnedRenderer);
            foreach (var r in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null) rendererSet.Add(r);
            var renderers = rendererSet.ToArray();

            if (hips == null)
            {
                var allMeshBones = renderers.SelectMany(r => r.bones ?? Array.Empty<Transform>())
                    .Where(b => b != null).Distinct().ToList();
                hips = FindHipsFallback(allMeshBones, boneOverrides);
            }

            if (hips == null)
            {
                Plugin.Log.LogWarning($"[SkeletonMapper] Could not find hips bone for actor '{actor.name}'.");
                return null;
            }

            // 2. Depth-first hierarchy under hips (parents guaranteed before children)
            var bones = hips.GetComponentsInChildren<Transform>(true);
            int n = bones.Length;
            var indexByTransform = new Dictionary<Transform, int>(n);
            for (int i = 0; i < n; i++) indexByTransform[bones[i]] = i;

            var parents = new int[n];
            var parts = new int[n];
            for (int i = 0; i < n; i++)
            {
                var b = bones[i];
                parents[i] = (b != hips && b.parent != null && indexByTransform.TryGetValue(b.parent, out int p)) ? p : -1;

                if (b == hips)
                {
                    parts[i] = 0;
                }
                else if (boneOverrides.TryGetValue(b.name, out int customPart))
                {
                    parts[i] = customPart;
                }
                else if (humanMap.TryGetValue(b, out int humanPart))
                {
                    parts[i] = humanPart;
                }
                else
                {
                    parts[i] = nm_rig_part_for_name(b.name);
                }
            }

            // 3. Extract bind pose from SkinnedMeshRenderers and align upright (fixes Blender Z-up / lying-flat bind poses!)
            var bindMatrices = ExtractBindPoseUpright(
                actor, renderers, bones, parents, parts, groundY,
                out float grow, out Vector3 bindHip, out Quaternion alignRot, out Vector3 alignedHipPos, out int knownBindCount);

            var restFrames = new float[n * 7];
            var restPlaces = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                Vector3 pos = bindMatrices[i].GetColumn(3);
                Quaternion rot = bindMatrices[i].rotation;
                WriteNmVec(pos, restFrames, i * 7);
                WriteNmQuat(rot, restFrames, i * 7 + 3);
                WriteNmVec(pos, restPlaces, i * 3);
            }

            // 4. Validate skeleton and compute 21 NM joints
            string skinKey = GetSkinKey(actor);
            var reportBuf = new byte[2048];
            int problems = nm_rig_check(n, parents, restPlaces, parts, reportBuf, reportBuf.Length);
            string checkReport = problems > 0
                ? Encoding.UTF8.GetString(reportBuf, 0, Math.Max(0, Array.IndexOf(reportBuf, (byte)0)))
                : "";
            if (problems > 0 && warnedSkins.Add(skinKey))
            {
                Plugin.Log.LogWarning($"[SkeletonMapper] Skin '{skinKey}' rig warnings:\n{checkReport}");
            }

            var fitJoints = new float[63];
            int jr = nm_rig_joints(n, parents, restPlaces, parts, fitJoints);
            if (jr < 0)
            {
                Plugin.Log.LogWarning($"[SkeletonMapper] nm_rig_joints failed for '{skinKey}': {LastError()}");
                return null;
            }

            // Fix anatomical pelvis & spine spacing when a skeleton (such as Ravenfield's default Soldier)
            // places its root hips bone below the thigh hip-socket line or places Spine0 right on top of Buttocks!
            // Note: restFrames keeps the true bone transforms so nm_rig_create records the exact offset automatically.
            float midHipX = 0.5f * (fitJoints[1 * 3 + 0] + fitJoints[4 * 3 + 0]);
            float midHipY = 0.5f * (fitJoints[1 * 3 + 1] + fitJoints[4 * 3 + 1]);
            float midHipZ = 0.5f * (fitJoints[1 * 3 + 2] + fitJoints[4 * 3 + 2]);
            bool pelvisAdjusted = false;
            if (fitJoints[2] < midHipZ - 0.04f)
            {
                fitJoints[0] = midHipX;
                fitJoints[1] = midHipY;
                fitJoints[2] = midHipZ + 0.01f;
                pelvisAdjusted = true;
            }

            // Ensure Spine0..Spine3 [7..10] never collapse onto Buttocks [0] (zero-length Spine0 causes explosive joint launches!)
            float dxS0 = fitJoints[7 * 3 + 0] - fitJoints[0];
            float dyS0 = fitJoints[7 * 3 + 1] - fitJoints[1];
            float dzS0 = fitJoints[7 * 3 + 2] - fitJoints[2];
            float spine0Dist = Mathf.Sqrt(dxS0 * dxS0 + dyS0 * dyS0 + dzS0 * dzS0);
            if (pelvisAdjusted || spine0Dist < 0.07f)
            {
                // Distribute Spine0..Spine3 along Buttocks [0] -> Neck [11] using Homer's anatomical proportions
                float[] spineFracs = { 0.17f, 0.35f, 0.54f, 0.74f };
                float nx = fitJoints[11 * 3 + 0], ny = fitJoints[11 * 3 + 1], nz = fitJoints[11 * 3 + 2];
                for (int s = 0; s < 4; s++)
                {
                    int idx = (7 + s) * 3;
                    float f = spineFracs[s];
                    fitJoints[idx + 0] = fitJoints[0] + (nx - fitJoints[0]) * f;
                    fitJoints[idx + 1] = fitJoints[1] + (ny - fitJoints[1]) * f;
                    fitJoints[idx + 2] = fitJoints[2] + (nz - fitJoints[2]) * f;
                }
            }

            // 5. Fit Delirium body build to skeleton proportions
            int sizeMode = ParseSizeMode(config.SizeMode.Value);
            IntPtr build = nm_build_fit_sized(baseBuild, fitJoints, sizeMode);
            if (build == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"[SkeletonMapper] nm_build_fit_sized failed for '{skinKey}': {LastError()}");
                return null;
            }

            // 6. Optional skin-based weight estimation (guarded to sane human mass range so blocky skins don't distort)
            float fittedWeight = nm_build_get_weight(build);
            if (config.AdaptiveTuning.Value)
            {
                TryWeighSkin(renderers, bones, parents, parts, indexByTransform, grow, bindHip, alignRot, alignedHipPos, fitJoints, build, fittedWeight);
            }

            // 7. Optional Body preset override
            string preset = (config.BodyPreset.Value ?? "Fitted").Trim();
            if (!string.IsNullOrEmpty(preset) && !preset.Equals("Fitted", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr reshaped = nm_build_named(build, preset);
                if (reshaped != IntPtr.Zero)
                {
                    nm_build_destroy(build);
                    build = reshaped;
                }
            }

            // 8. Background adaptive balance tuning cached per skin + preset
            string tuneKey = $"{skinKey}|{preset}|{sizeMode}";
            if (config.AdaptiveTuning.Value)
            {
                ApplyOrScheduleTune(tuneKey, baseBuild, fitJoints, sizeMode, preset, build);
            }

            float scale = nm_build_get_scale(build);
            if (scale <= 0f) scale = 1f;
            float finalWeight = nm_build_get_weight(build);

            if (loggedSkins.Add(skinKey))
            {
                Plugin.Log.LogInfo($"[SkeletonMapper] Skin '{skinKey}': bones={n}, bindPoses={knownBindCount}, weight={finalWeight:F1}kg (fitted={fittedWeight:F1}kg), scale={scale:F2}, rigIssues={problems}");
            }

            return new RigBuildResult
            {
                Hips = hips,
                Bones = bones,
                Parents = parents,
                Parts = parts,
                RestFrames = restFrames,
                FitJoints = fitJoints,
                Build = build,
                Scale = scale,
                SkinKey = skinKey,
                CheckReport = checkReport,
                KnownBindPoseCount = knownBindCount,
                FittedWeightKg = fittedWeight,
                FinalWeightKg = finalWeight
            };
        }

        private void TryWeighSkin(
            SkinnedMeshRenderer[] renderers,
            Transform[] bones,
            int[] parents,
            int[] parts,
            Dictionary<Transform, int> indexByTransform,
            float grow,
            Vector3 bindHip,
            Quaternion alignRot,
            Vector3 alignedHipPos,
            float[] fitJoints,
            IntPtr build,
            float fittedWeight)
        {
            var verts = new List<float>();
            var vparts = new List<int>();
            foreach (var r in renderers)
            {
                var m = r.sharedMesh;
                if (m == null || !m.isReadable) continue;
                var bw = m.boneWeights;
                var mv = m.vertices;
                var rBones = r.bones;
                if (bw == null || mv == null || rBones == null) continue;
                var w = r.transform.localToWorldMatrix;
                for (int i = 0; i < mv.Length && i < bw.Length; i++)
                {
                    int bi = bw[i].boneIndex0;
                    int b = (bi >= 0 && bi < rBones.Length && rBones[bi] != null && indexByTransform.TryGetValue(rBones[bi], out int idx)) ? idx : -1;
                    while (b >= 0 && parts[b] < 0) b = parents[b];
                    if (b < 0) continue;
                    Vector3 v = alignedHipPos + alignRot * ((w.MultiplyPoint3x4(mv[i]) - bindHip) * grow);
                    verts.Add(v.x);
                    verts.Add(v.z);
                    verts.Add(v.y);
                    vparts.Add(parts[b]);
                }
            }
            if (vparts.Count > 32)
            {
                float skinKg = nm_build_weigh_skin(build, fitJoints, verts.ToArray(), vparts.ToArray(), vparts.Count);
                if (skinKg < 55f || skinKg > 115f)
                {
                    // Guard against blocky/unusual skins producing extreme mass
                    nm_build_set_weight(build, Mathf.Clamp(fittedWeight, 68f, 92f));
                }
            }
        }

        private void ApplyOrScheduleTune(string tuneKey, IntPtr baseBuild, float[] fitJoints, int sizeMode, string preset, IntPtr build)
        {
            lock (tuneLock)
            {
                if (tunedBuilds.TryGetValue(tuneKey, out var cached))
                {
                    if (cached != IntPtr.Zero) nm_build_copy_tune(build, cached);
                    return;
                }
                tunedBuilds[tuneKey] = IntPtr.Zero;
            }

            var jointsCopy = (float[])fitJoints.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IntPtr temp = nm_build_fit_sized(baseBuild, jointsCopy, sizeMode);
                    if (temp == IntPtr.Zero) return;
                    if (!string.IsNullOrEmpty(preset) && !preset.Equals("Fitted", StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr r = nm_build_named(temp, preset);
                        if (r != IntPtr.Zero)
                        {
                            nm_build_destroy(temp);
                            temp = r;
                        }
                    }
                    int score = nm_build_tune(temp, 160);
                    lock (tuneLock)
                    {
                        if (tunedBuilds.ContainsKey(tuneKey))
                            tunedBuilds[tuneKey] = temp;
                        else
                            nm_build_destroy(temp);
                    }
                    if (config.DebugLog.Value)
                        Plugin.Log.LogInfo($"[SkeletonMapper] Tuned skin '{tuneKey}' (stands through {score}/3 pushes).");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[SkeletonMapper] Background tune error for '{tuneKey}': {ex.Message}");
                }
            });
        }

        private static Matrix4x4[] ExtractBindPoseUpright(
            Actor actor,
            SkinnedMeshRenderer[] renderers,
            Transform[] bones,
            int[] parents,
            int[] parts,
            float groundY,
            out float grow,
            out Vector3 hip,
            out Quaternion alignRot,
            out Vector3 alignedHipPos,
            out int knownBindCount)
        {
            var known = new Dictionary<Transform, Matrix4x4>();
            var boneSet = new HashSet<Transform>(bones);

            // First pass: renderers bound directly to ragdoll bones
            foreach (var r in renderers)
            {
                var poses = r.sharedMesh != null ? r.sharedMesh.bindposes : null;
                var rBones = r.bones;
                if (poses == null || rBones == null) continue;
                var l2w = r.transform.localToWorldMatrix;
                for (int i = 0; i < rBones.Length && i < poses.Length; i++)
                {
                    var rb = rBones[i];
                    if (rb == null) continue;
                    if (boneSet.Contains(rb) && !known.ContainsKey(rb))
                    {
                        known[rb] = l2w * poses[i].inverse;
                    }
                }
            }

            // Second pass: if any ragdoll bone was missing, map from animated SkinnedMeshRenderer via AnimatedToRagdoll
            if (known.Count < 8 && actor?.ragdoll != null)
            {
                foreach (var r in renderers)
                {
                    var poses = r.sharedMesh != null ? r.sharedMesh.bindposes : null;
                    var rBones = r.bones;
                    if (poses == null || rBones == null) continue;
                    var l2w = r.transform.localToWorldMatrix;
                    for (int i = 0; i < rBones.Length && i < poses.Length; i++)
                    {
                        var ab = rBones[i];
                        if (ab == null) continue;
                        Transform mapped = null;
                        try { mapped = actor.ragdoll.AnimatedToRagdoll(ab); } catch { }
                        if (mapped != null && boneSet.Contains(mapped) && !known.ContainsKey(mapped))
                        {
                            known[mapped] = l2w * poses[i].inverse;
                        }
                    }
                }
            }

            knownBindCount = known.Count;
            var m = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                if (known.TryGetValue(bones[b], out var k))
                    m[b] = k;
                else if (parents[b] >= 0)
                    m[b] = m[parents[b]] * (bones[parents[b]].worldToLocalMatrix * bones[b].localToWorldMatrix);
                else
                    m[b] = bones[b].localToWorldMatrix;
            }

            float nowLen = 0f, bindLen = 0f;
            for (int b = 1; b < bones.Length; b++)
            {
                if (parents[b] >= 0)
                {
                    nowLen += (bones[b].position - bones[parents[b]].position).magnitude;
                    bindLen += ((Vector3)m[b].GetColumn(3) - (Vector3)m[parents[b]].GetColumn(3)).magnitude;
                }
            }
            grow = bindLen > 1e-4f ? nowLen / bindLen : 1f;
            hip = m[0].GetColumn(3);

            // Detect anatomical basis of the raw bind pose (fixes Blender Z-up meshes whose bindpose lies flat on its back/front!)
            float gLocal = grow;
            Vector3 hipLocal = hip;
            int FindPart(int p) => Array.IndexOf(parts, p);
            Vector3 RawPos(int idx) => ((Vector3)m[idx].GetColumn(3) - hipLocal) * gLocal;

            int iHead = FindPart(12);
            int iFootL = FindPart(3), iFootR = FindPart(6);
            int iThighL = FindPart(1), iThighR = FindPart(4);
            int iArmL = FindPart(14), iArmR = FindPart(18);

            alignRot = Quaternion.identity;
            if (iHead >= 0 && iFootL >= 0 && iFootR >= 0 && (iThighL >= 0 && iThighR >= 0 || iArmL >= 0 && iArmR >= 0))
            {
                Vector3 midFeet = 0.5f * (RawPos(iFootL) + RawPos(iFootR));
                Vector3 bindUp = (RawPos(iHead) - midFeet).normalized;
                Vector3 bindRight = (iThighL >= 0 && iThighR >= 0)
                    ? (RawPos(iThighR) - RawPos(iThighL)).normalized
                    : (RawPos(iArmR) - RawPos(iArmL)).normalized;

                // Unity Vector3.Cross uses standard right-hand formula: Cross(Right=+X, Up=+Y) = Forward=+Z
                Vector3 bindFwd = Vector3.Cross(bindRight, bindUp);
                if (bindFwd.sqrMagnitude > 1e-4f && bindUp.sqrMagnitude > 1e-4f)
                {
                    bindFwd.Normalize();
                    Quaternion bindBasis = Quaternion.LookRotation(bindFwd, bindUp);

                    Vector3 targetFwd = actor != null
                        ? Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up)
                        : Vector3.forward;
                    if (targetFwd.sqrMagnitude < 1e-4f) targetFwd = Vector3.forward;
                    Quaternion targetBasis = Quaternion.LookRotation(targetFwd.normalized, Vector3.up);

                    alignRot = targetBasis * Quaternion.Inverse(bindBasis);
                }
            }

            // Place the upright-aligned bind pose standing cleanly on groundY (ankle ~0.04m above ground)
            // horizontally centered at the character's hips so restFrames matches nm_agent_create's standing height
            // even if the actor was crouching, prone, or already falling at takeover!
            float lowestFootRelY = -0.85f;
            if (iFootL >= 0 && iFootR >= 0)
            {
                float yL = (alignRot * RawPos(iFootL)).y;
                float yR = (alignRot * RawPos(iFootR)).y;
                lowestFootRelY = Mathf.Min(yL, yR);
            }
            else
            {
                lowestFootRelY = 0f;
                for (int b = 0; b < bones.Length; b++)
                    lowestFootRelY = Mathf.Min(lowestFootRelY, (alignRot * RawPos(b)).y);
            }

            Vector3 currentHip = bones[0].position;
            alignedHipPos = new Vector3(currentHip.x, groundY + 0.04f - lowestFootRelY, currentHip.z);

            for (int b = 0; b < bones.Length; b++)
            {
                Vector3 pos = alignedHipPos + alignRot * RawPos(b);
                Quaternion rot = alignRot * m[b].rotation;
                m[b] = Matrix4x4.TRS(pos, rot, Vector3.one);
            }
            return m;
        }

        private static Transform FindHipsFallback(List<Transform> bones, Dictionary<string, int> overrides)
        {
            var direct = bones.FirstOrDefault(b => PartOf(b.name, overrides) == 0);
            if (direct != null) return direct;
            var leftThigh = bones.FirstOrDefault(b => PartOf(b.name, overrides) == 1);
            var rightThigh = bones.FirstOrDefault(b => PartOf(b.name, overrides) == 4);
            return (leftThigh != null && rightThigh != null && leftThigh.parent == rightThigh.parent) ? leftThigh.parent : null;
        }

        private static int PartOf(string boneName, Dictionary<string, int> overrides)
        {
            return overrides.TryGetValue(boneName, out int p) ? p : nm_rig_part_for_name(boneName);
        }

        private static Dictionary<string, int> ParseBoneMap(string raw)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return map;

            var partNames = new List<string>(21);
            for (int i = 0; i < 21; i++)
                partNames.Add(Str((b, n) => nm_part_name(i, b, n)) ?? "");

            foreach (var pair in raw.Split(','))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2) continue;
                string boneName = kv[0].Trim();
                string partName = kv[1].Trim();
                if (string.IsNullOrEmpty(boneName)) continue;
                if (partName.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    map[boneName] = -1;
                }
                else
                {
                    int idx = partNames.FindIndex(p => p.Equals(partName, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) map[boneName] = idx;
                }
            }
            return map;
        }

        private static int ParseSizeMode(string mode)
        {
            if (string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase)) return NM_SIZE_REAL;
            if (string.Equals(mode, "Homer", StringComparison.OrdinalIgnoreCase)) return NM_SIZE_BASE;
            return NM_SIZE_AUTO;
        }

        private static string GetSkinKey(Actor actor)
        {
            if (actor.overrideActorSkin != null && !string.IsNullOrEmpty(actor.overrideActorSkin.name))
                return actor.overrideActorSkin.name;
            if (actor.skinnedRendererRagdoll != null && actor.skinnedRendererRagdoll.sharedMesh != null)
                return actor.skinnedRendererRagdoll.sharedMesh.name;
            return "DefaultSoldier";
        }
    }
}
