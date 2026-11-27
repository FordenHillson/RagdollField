using System;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    /// <summary>
    /// Renders Playroom's exact 21-part Homer capsule/box body directly in Unity,
    /// driven 1:1 from Delirium's live nm_agent_get_part_transform() poses.
    /// Zero colliders (pure visual), team-colored (Eagle Blue / Raven Red) with eyes on Head.
    /// </summary>
    public sealed class HomerVisualBody : IDisposable
    {
        private GameObject rootObj;
        private Transform[] partTransforms;
        private Actor ownerActor;
        private readonly float[] posBuf = new float[3];
        private readonly float[] rotBuf = new float[4];

        private static Material matEagleBody;
        private static Material matEagleDark;
        private static Material matRavenBody;
        private static Material matRavenDark;
        private static Material matHead;
        private static Material matEye;

        public bool IsActive => rootObj != null && partTransforms != null && partTransforms.Length > 0;

        private static Material MakeMat(Color c, float gloss = 0.25f)
        {
            Shader sh = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Unlit/Color");
            var m = new Material(sh);
            m.color = c;
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.05f);
            return m;
        }

        private static void EnsureMaterials()
        {
            if (matEagleBody == null) matEagleBody = MakeMat(new Color(0.22f, 0.48f, 0.88f, 1f), 0.30f);
            if (matEagleDark == null) matEagleDark = MakeMat(new Color(0.12f, 0.22f, 0.40f, 1f), 0.20f);
            if (matRavenBody == null) matRavenBody = MakeMat(new Color(0.84f, 0.20f, 0.20f, 1f), 0.30f);
            if (matRavenDark == null) matRavenDark = MakeMat(new Color(0.38f, 0.10f, 0.10f, 1f), 0.20f);
            if (matHead == null) matHead = MakeMat(new Color(0.92f, 0.78f, 0.66f, 1f), 0.22f);
            if (matEye == null) matEye = MakeMat(new Color(0.06f, 0.06f, 0.08f, 1f), 0.75f);
        }

        public void Build(Actor owner, IntPtr agent, IntPtr build, float scale)
        {
            Dispose();
            if (owner == null || agent == IntPtr.Zero || build == IntPtr.Zero) return;

            int n = nm_agent_part_count(agent);
            if (n <= 0 || n > 32) return;

            EnsureMaterials();
            ownerActor = owner;
            bool isRaven = owner.team == 1;
            Material bodyMat = isRaven ? matRavenBody : matEagleBody;
            Material darkMat = isRaven ? matRavenDark : matEagleDark;

            rootObj = new GameObject($"DeliriumHomerVisual_{owner.name}");
            partTransforms = new Transform[n];

            var massArr = new float[1];
            var inertiaArr = new float[3];
            var boxArr = new float[3];
            var radArr = new float[1];
            var halfArr = new float[1];

            for (int k = 0; k < n; k++)
            {
                if (nm_agent_get_part_transform(agent, k, posBuf, rotBuf) < 0)
                    continue;

                var holder = new GameObject($"homer_part_{k}");
                holder.transform.SetParent(rootObj.transform, false);
                holder.transform.SetPositionAndRotation(ToUnityVec(posBuf, 0), ToUnityQuat(rotBuf, 0));

                nm_build_part_info(build, k, null, radArr, halfArr, out _);
                int kind = nm_agent_get_part_body(agent, k, massArr, inertiaArr, boxArr);

                GameObject vis;
                if (kind == 2)
                {
                    vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    vis.transform.SetParent(holder.transform, false);
                    vis.transform.localScale = new Vector3(
                        Mathf.Max(boxArr[0] * 2f * scale, 0.03f),
                        Mathf.Max(boxArr[2] * 2f * scale, 0.03f),
                        Mathf.Max(boxArr[1] * 2f * scale, 0.03f));
                }
                else
                {
                    vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    vis.transform.SetParent(holder.transform, false);
                    // NM capsule axis is NM local Y -> Unity local Z; Unity Capsule primitive axis is local Y -> rotate +90 X
                    vis.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    float r = Mathf.Max(radArr[0] * scale, 0.022f);
                    float h = Mathf.Max(halfArr[0] * scale, 0.001f);
                    // Unity Capsule default: radius=0.5, total height=2.0 => scaleY = (h + r)
                    vis.transform.localScale = new Vector3(r * 2f, h + r, r * 2f);
                }

                // Pure visual: remove primitive collider so it never blocks bullets or physics
                var col = vis.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                var mr = vis.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    if (k == 12) mr.sharedMaterial = matHead;
                    else if (k == 3 || k == 6 || k == 16 || k == 20 || k == 9 || k == 10) mr.sharedMaterial = darkMat;
                    else mr.sharedMaterial = bodyMat;
                }

                // Add subtle Playroom-style eyes on Head (part 12) so facing is immediately clear
                if (k == 12)
                {
                    float r = Mathf.Max(radArr[0] * scale, 0.09f);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        var ec = eye.GetComponent<Collider>();
                        if (ec != null) UnityEngine.Object.Destroy(ec);
                        eye.transform.SetParent(holder.transform, false);
                        eye.transform.localPosition = new Vector3(side * r * 0.34f, r * 0.14f, r * 0.88f);
                        eye.transform.localScale = Vector3.one * (r * 0.24f);
                        var emr = eye.GetComponent<MeshRenderer>();
                        if (emr != null) emr.sharedMaterial = matEye;
                    }
                }

                partTransforms[k] = holder.transform;
            }

            SetSoldierMeshVisible(false);
        }

        public void SyncLateUpdate(IntPtr agent)
        {
            if (!IsActive || agent == IntPtr.Zero) return;
            SetSoldierMeshVisible(false);

            for (int k = 0; k < partTransforms.Length; k++)
            {
                var tf = partTransforms[k];
                if (tf == null) continue;
                if (nm_agent_get_part_transform(agent, k, posBuf, rotBuf) >= 0)
                {
                    tf.SetPositionAndRotation(ToUnityVec(posBuf, 0), ToUnityQuat(rotBuf, 0));
                }
            }
        }

        private void SetSoldierMeshVisible(bool visible)
        {
            if (ownerActor == null) return;
            if (ownerActor.skinnedRendererRagdoll != null && ownerActor.skinnedRendererRagdoll.enabled != visible)
                ownerActor.skinnedRendererRagdoll.enabled = visible;
        }

        public void Dispose()
        {
            SetSoldierMeshVisible(true);
            if (rootObj != null)
            {
                UnityEngine.Object.Destroy(rootObj);
                rootObj = null;
            }
            partTransforms = null;
            ownerActor = null;
        }
    }
}
