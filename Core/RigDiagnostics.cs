using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    public static class RigDiagnostics
    {
        public static string DefaultDumpPath => Path.Combine(BepInEx.Paths.ConfigPath, "delirium-rig-dump.txt");

        public static string DumpControllerRig(ActorRagdollController ctrl, string path = null)
        {
            path = string.IsNullOrEmpty(path) ? DefaultDumpPath : path;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(16384);

            sb.AppendLine("=== DELIRIUM RAVENFIELD RIG & POSE DUMP ===");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (ctrl == null || ctrl.Actor == null)
            {
                sb.AppendLine("ERROR: No target controller/actor.");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }

            var actor = ctrl.Actor;
            var snap = ctrl.LastRigSnapshot;
            sb.AppendLine($"Actor: {actor.name} | Dead={actor.dead} Fallen={actor.fallenOver} Phase={ctrl.Phase}");
            sb.AppendLine($"ActorPos: {Fmt(actor.transform.position, ci)} | ActorRot: {FmtQ(actor.transform.rotation, ci)} | Scale: {Fmt(actor.transform.lossyScale, ci)}");
            if (actor.ragdoll?.ragdollObject != null)
            {
                var rt = actor.ragdoll.ragdollObject.transform;
                sb.AppendLine($"RagdollObj: {rt.name} Pos={Fmt(rt.position, ci)} Rot={FmtQ(rt.rotation, ci)} Scale={Fmt(rt.lossyScale, ci)} State={actor.ragdoll.IsRagdoll()}");
                int totalRb = actor.ragdoll.rigidbodies != null ? actor.ragdoll.rigidbodies.Length : 0;
                int kinRb = 0;
                if (actor.ragdoll.rigidbodies != null)
                    foreach (var rb in actor.ragdoll.rigidbodies) if (rb != null && rb.isKinematic) kinRb++;
                int jointCount = actor.ragdoll.ragdollObject.GetComponentsInChildren<ConfigurableJoint>(true).Length;
                sb.AppendLine($"RagdollRigidbodies: {totalRb} (kinematic={kinRb}) | Joints={jointCount}");
            }

            sb.AppendLine($"SkinnedMeshRenderers on Actor:");
            foreach (var r in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                var m = r.sharedMesh;
                sb.AppendLine($"  SMR '{r.name}' active={r.gameObject.activeInHierarchy} enabled={r.enabled} mesh='{m?.name ?? "null"}' readable={m?.isReadable} bones={r.bones?.Length ?? 0} bindposes={m?.bindposes?.Length ?? 0} scale={Fmt(r.transform.lossyScale, ci)}");
            }

            if (snap != null)
            {
                sb.AppendLine();
                sb.AppendLine($"--- RigBuild Summary ---");
                sb.AppendLine($"SkinKey: {snap.SkinKey}");
                sb.AppendLine($"BoneCount: {snap.Bones.Length} | KnownBindPoses: {snap.KnownBindPoseCount} | Scale: {snap.Scale.ToString("F4", ci)}");
                sb.AppendLine($"Weight: final={snap.FinalWeightKg.ToString("F2", ci)}kg fitted={snap.FittedWeightKg.ToString("F2", ci)}kg");
                sb.AppendLine($"RigCheck: {(string.IsNullOrEmpty(snap.CheckReport) ? "OK" : snap.CheckReport.Replace("\n", " ; "))}");
                sb.AppendLine($"AgentCheck: {ctrl.RunSetupCheck().Replace("\n", " ; ")}");

                sb.AppendLine();
                sb.AppendLine("--- 21 Fitted NM Joints (NM space Z-up, metres, relative to Buttocks [0]) ---");
                var j = snap.FitJoints;
                float j0x = j[0], j0y = j[1], j0z = j[2];
                for (int k = 0; k < 21; k++)
                {
                    string pname = Str((b, n) => nm_part_name(k, b, n)) ?? $"part_{k}";
                    float x = j[k * 3] - j0x, y = j[k * 3 + 1] - j0y, z = j[k * 3 + 2] - j0z;
                    sb.AppendLine(string.Format(ci, "  [{0,2}] {1,-16} rel=({2,7:F4}, {3,7:F4}, {4,7:F4}) abs=({5,7:F4}, {6,7:F4}, {7,7:F4})",
                        k, pname, x, y, z, j[k * 3], j[k * 3 + 1], j[k * 3 + 2]));
                }

                // Raw 63-float vector for direct replay in headless SmokeTest!
                sb.Append("FIT_JOINTS_63: ");
                for (int i = 0; i < 63; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(j[i].ToString("R", ci));
                }
                sb.AppendLine();

                // Limb segment lengths
                float Dist(int a, int b)
                {
                    float dx = j[a * 3] - j[b * 3], dy = j[a * 3 + 1] - j[b * 3 + 1], dz = j[a * 3 + 2] - j[b * 3 + 2];
                    return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                sb.AppendLine(string.Format(ci,
                    "SegmentLengths(m): LThigh={0:F3} LShin={1:F3} RThigh={2:F3} RShin={3:F3} | PelvisToHead={4:F3} | HipWidth={5:F3} ShoulderWidth={6:F3} | LUpArm={7:F3} LLowArm={8:F3} RUpArm={9:F3} RLowArm={10:F3}",
                    Dist(1, 2), Dist(2, 3), Dist(4, 5), Dist(5, 6), Dist(0, 12), Dist(1, 4), Dist(14, 18), Dist(14, 15), Dist(15, 16), Dist(18, 19), Dist(19, 20)));

                sb.AppendLine();
                sb.AppendLine("--- 21 Built NM Parts (mass, radius, halfLen, live world pos/rot in NM space) ---");
                var mArr = new float[1];
                var rArr = new float[1];
                var hArr = new float[1];
                var pArr = new float[3];
                var qArr = new float[4];
                for (int k = 0; k < 21; k++)
                {
                    string pname = Str((b, n) => nm_part_name(k, b, n)) ?? $"part_{k}";
                    nm_build_part_info(ctrl.BuildPtr, k, mArr, rArr, hArr, out int parentPart);
                    bool hasLive = ctrl.Agent != IntPtr.Zero && nm_agent_get_part_transform(ctrl.Agent, k, pArr, qArr) >= 0;
                    int contact = ctrl.Agent != IntPtr.Zero ? nm_agent_get_part_contact(ctrl.Agent, k) : 0;
                    sb.AppendLine(string.Format(ci,
                        "  [{0,2}] {1,-16} parent={2,2} mass={3,5:F2} rad={4,5:F3} half={5,5:F3} | livePos=({6,6:F3},{7,6:F3},{8,6:F3}) rot=({9,6:F3},{10,6:F3},{11,6:F3},{12,6:F3}) contact=0x{13:X2}",
                        k, pname, parentPart, mArr[0], rArr[0], hArr[0],
                        hasLive ? pArr[0] : 0f, hasLive ? pArr[1] : 0f, hasLive ? pArr[2] : 0f,
                        hasLive ? qArr[0] : 0f, hasLive ? qArr[1] : 0f, hasLive ? qArr[2] : 0f, hasLive ? qArr[3] : 1f, contact));
                }

                sb.AppendLine();
                sb.AppendLine("--- Skeleton Bone Table (index, name, parent, part, restPos_NM_rel, liveUnityPos_rel) ---");
                Vector3 liveHip = snap.Hips != null ? snap.Hips.position : Vector3.zero;
                float r0x = snap.RestFrames[0], r0y = snap.RestFrames[1], r0z = snap.RestFrames[2];
                for (int i = 0; i < snap.Bones.Length; i++)
                {
                    var b = snap.Bones[i];
                    string bname = b != null ? b.name : "null";
                    int part = snap.Parts[i];
                    string pname = part >= 0 ? (Str((buf, len) => nm_part_name(part, buf, len)) ?? "?") : "-";
                    float rx = snap.RestFrames[i * 7] - r0x;
                    float ry = snap.RestFrames[i * 7 + 1] - r0y;
                    float rz = snap.RestFrames[i * 7 + 2] - r0z;
                    Vector3 lp = b != null ? (b.position - liveHip) : Vector3.zero;
                    sb.AppendLine(string.Format(ci,
                        "  {0,2}: {1,-26} par={2,2} part={3,2} ({4,-15}) restRelNM=({5,6:F3},{6,6:F3},{7,6:F3}) liveRelUnity=({8,6:F3},{9,6:F3},{10,6:F3})",
                        i, bname, snap.Parents[i], part, pname, rx, ry, rz, lp.x, lp.y, lp.z));
                }
            }

            sb.AppendLine();
            sb.AppendLine("--- Live Trace (t_sec, phase, bal, fall, relax, dead, com_z, speed, pelvis_z, chest_z, head_z, lfoot_z, rfoot_z, lhand_z, rhand_z) ---");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        public static void AppendTraceSample(string path, float elapsedSec, ActorRagdollController ctrl)
        {
            if (ctrl == null || ctrl.Agent == IntPtr.Zero || string.IsNullOrEmpty(path)) return;
            var ci = CultureInfo.InvariantCulture;
            var com = new float[3];
            var vel = new float[3];
            nm_agent_get_com(ctrl.Agent, com, vel);
            float speed = Mathf.Sqrt(vel[0] * vel[0] + vel[1] * vel[1] + vel[2] * vel[2]);

            float PartZ(int partIdx)
            {
                var p = new float[3];
                return nm_agent_get_part_transform(ctrl.Agent, partIdx, p, null) >= 0 ? p[2] : float.NaN;
            }

            string line = string.Format(ci,
                "  t={0,4:F2}s phase={1,-14} bal={2} fall={3} relax={4} dead={5} | comZ={6,6:F3} v={7,6:F3} | pelv={8,6:F3} chest={9,6:F3} head={10,6:F3} lft={11,6:F3} rft={12,6:F3} lhd={13,6:F3} rhd={14,6:F3}",
                elapsedSec, ctrl.Phase,
                nm_agent_is_balancing(ctrl.Agent), nm_agent_has_fallen(ctrl.Agent),
                nm_agent_is_relaxing(ctrl.Agent), nm_agent_is_dead(ctrl.Agent),
                com[2], speed,
                PartZ(0), PartZ(10), PartZ(12), PartZ(3), PartZ(6), PartZ(16), PartZ(20));

            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        private static string Fmt(Vector3 v, CultureInfo ci) => string.Format(ci, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
        private static string FmtQ(Quaternion q, CultureInfo ci) => string.Format(ci, "({0:F3},{1:F3},{2:F3},{3:F3})", q.x, q.y, q.z, q.w);
    }
}
