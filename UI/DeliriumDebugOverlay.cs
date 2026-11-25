using System;
using System.Collections.Generic;
using DeliriumRavenfield.Core;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.UI
{
    /// <summary>
    /// Playroom-style 3D GL debug overlay for Ravenfield:
    /// - Draws Delirium's 21-part active ragdoll skeleton, joint pivots, foot boxes, COM marker,
    ///   red contact highlights, and sampled Unity ground marker directly in world space.
    /// - Optionally draws the exact static BVH triangles (stairs, floors, walls) loaded into Delirium's NmWorld.
    /// </summary>
    public sealed class DeliriumDebugOverlay : MonoBehaviour
    {
        public bool ShowSkeletonOverlay;
        public bool ShowWorldBvhOverlay;
        public Actor FocusedActor;

        private DeliriumRuntime runtime;
        private LodManager lod;
        private Material lineMat;

        private readonly float[] posBuf = new float[3];
        private readonly float[] rotBuf = new float[4];
        private readonly float[] comBuf = new float[3];
        private readonly float[] velBuf = new float[3];
        private readonly float[] massBuf = new float[1];
        private readonly float[] inertiaBuf = new float[3];
        private readonly float[] boxBuf = new float[3];
        private readonly float[] pivotBuf = new float[3];
        private readonly Vector3[] partPositions = new Vector3[21];
        private readonly Quaternion[] partRotations = new Quaternion[21];
        private readonly Vector3[] jointPivots = new Vector3[21];
        private readonly bool[] hasJointPivot = new bool[21];
        private readonly int[] partContacts = new int[21];
        private readonly int[] partParents = new int[21];
        private readonly bool[] partValid = new bool[21];

        public void Initialize(DeliriumRuntime rt, LodManager lodManager)
        {
            runtime = rt;
            lod = lodManager;
        }

        private void OnEnable()
        {
            Camera.onPostRender += HandlePostRender;
        }

        private void OnDisable()
        {
            Camera.onPostRender -= HandlePostRender;
        }

        private void OnDestroy()
        {
            Camera.onPostRender -= HandlePostRender;
            if (lineMat != null)
            {
                Destroy(lineMat);
                lineMat = null;
            }
        }

        private void EnsureMaterial()
        {
            if (lineMat != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored")
                            ?? Shader.Find("Sprites/Default")
                            ?? Shader.Find("Unlit/Color");
            if (shader == null) return;
            lineMat = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMat.SetInt("_ZWrite", 0);
            lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void HandlePostRender(Camera cam)
        {
            if ((!ShowSkeletonOverlay && !ShowWorldBvhOverlay) || runtime == null || lod == null || !runtime.IsReady)
                return;
            if (cam == null || cam != Camera.main)
                return;

            EnsureMaterial();
            if (lineMat == null) return;

            GL.PushMatrix();
            lineMat.SetPass(0);
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;

            // 1. World BVH static geometry wireframe (stairs, floors, walls Delirium actually collides with)
            if (ShowWorldBvhOverlay)
            {
                DrawWorldBvhOverlay();
            }

            // 2. Active Delirium ragdoll 21-part skeleton, contacts, foot boxes, COM, and ground marker
            if (ShowSkeletonOverlay)
            {
                DrawSkeletonsOverlay();
            }

            GL.PopMatrix();
        }

        private void DrawWorldBvhOverlay()
        {
            var worlds = runtime.Worlds;
            var mirrorWorld = Plugin.PlayroomSync?.MirrorWorldEntry;
            if ((worlds == null || worlds.Count == 0) && mirrorWorld == null) return;

            // Prefer the focused actor's world if available so the screen stays readable
            SharedWorldEntry focusWorld = null;
            if (FocusedActor != null && lod.TryGetController(FocusedActor, out var fc) && fc.WorldEntry != null)
            {
                focusWorld = fc.WorldEntry;
            }
            else if (mirrorWorld != null)
            {
                focusWorld = mirrorWorld;
            }

            GL.Begin(GL.LINES);
            if (mirrorWorld != null && mirrorWorld.DebugTriangles != null && mirrorWorld.DebugTriangles.Length >= 3 &&
                (focusWorld == null || focusWorld == mirrorWorld))
            {
                DrawWorldTriangles(mirrorWorld.DebugTriangles);
            }
            if (worlds != null)
            {
                for (int wi = 0; wi < worlds.Count; wi++)
                {
                    var w = worlds[wi];
                    if (w == null || w.DebugTriangles == null || w.DebugTriangles.Length < 3) continue;
                    if (focusWorld != null && w != focusWorld) continue;
                    DrawWorldTriangles(w.DebugTriangles);
                }
            }
            GL.End();
        }

        private static void DrawWorldTriangles(Vector3[] tris)
        {
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = tris[i];
                Vector3 b = tris[i + 1];
                Vector3 c = tris[i + 2];

                // Color floors/stairs cyan-blue and vertical walls warm orange (matching Playroom!)
                Vector3 n = Vector3.Cross(b - a, c - a);
                float ny = n.sqrMagnitude > 1e-8f ? Mathf.Abs(n.normalized.y) : 1f;
                Color col = ny > 0.55f
                    ? new Color(0.18f, 0.72f, 1.0f, 0.36f)
                    : new Color(1.0f, 0.52f, 0.20f, 0.38f);
                GL.Color(col);

                GL.Vertex(a); GL.Vertex(b);
                GL.Vertex(b); GL.Vertex(c);
                GL.Vertex(c); GL.Vertex(a);
            }
        }

        private void DrawSkeletonsOverlay()
        {
            var active = lod.ActiveControllers;
            var sync = Plugin.PlayroomSync;
            bool hasSyncBoxes = sync != null && sync.IsConnected && sync.LastDynamicBoxes.Count > 0;
            bool hasGhostCom = sync != null && sync.IsConnected && sync.HasPlayroomRef;
            if ((active == null || active.Count == 0) && !hasSyncBoxes && !hasGhostCom) return;

            GL.Begin(GL.LINES);
            if (active != null)
            {
                for (int ci = 0; ci < active.Count; ci++)
                {
                    var ctrl = active[ci];
                    if (ctrl == null || !ctrl.IsSimulating || ctrl.Agent == IntPtr.Zero) continue;
                    DrawSingleAgentSkeleton(ctrl);
                }
            }
            if (hasSyncBoxes)
            {
                var boxes = sync.LastDynamicBoxes;
                Color boxCol = new Color(1.0f, 0.22f, 0.88f, 0.92f);
                for (int i = 0; i < boxes.Count; i++)
                {
                    DrawAxisBox(boxes[i].CenterUnity, boxes[i].RotUnity, boxes[i].HalfUnity, boxCol);
                }
            }
            if (hasGhostCom)
            {
                // Playroom reference COM crosshair (bright cyan) right beside Ravenfield COM (orange)
                DrawCross(sync.PlayroomGhostComUnity, 0.12f, new Color(0.05f, 0.95f, 1.0f, 1.0f));
            }
            GL.End();
        }

        private void DrawSingleAgentSkeleton(ActorRagdollController ctrl)
        {
            IntPtr agent = ctrl.Agent;
            int n = Math.Min(nm_agent_part_count(agent), 21);
            if (n <= 0) return;
            Vector3 visOffset = ctrl.IsSyncLockstepControlled ? ctrl.SyncVisualOffset : Vector3.zero;

            for (int k = 0; k < n; k++)
            {
                partValid[k] = nm_agent_get_part_transform(agent, k, posBuf, rotBuf) >= 0;
                hasJointPivot[k] = false;
                if (partValid[k])
                {
                    partPositions[k] = ToUnityVec(posBuf, 0) + visOffset;
                    partRotations[k] = ToUnityQuat(rotBuf, 0);
                    partContacts[k] = nm_agent_get_part_contact(agent, k);
                    if (k > 0 && nm_agent_get_joint(agent, k, out int parent, pivotBuf, null, null, null) > 0)
                    {
                        partParents[k] = parent;
                        jointPivots[k] = ToUnityVec(pivotBuf, 0) + visOffset;
                        hasJointPivot[k] = true;
                    }
                    else
                    {
                        partParents[k] = -1;
                        jointPivots[k] = partPositions[k];
                    }
                }
                else
                {
                    partParents[k] = -1;
                    partContacts[k] = 0;
                }
            }

            // Bone links connecting true Playroom joint pivots (white, or red when in contact)
            for (int k = 1; k < n; k++)
            {
                int p = partParents[k];
                if (!partValid[k] || p < 0 || p >= n || !partValid[p]) continue;
                Vector3 a = hasJointPivot[p] ? jointPivots[p] : partPositions[p];
                Vector3 b = hasJointPivot[k] ? jointPivots[k] : partPositions[k];
                bool touched = partContacts[k] != 0;
                GL.Color(touched ? new Color(1f, 0.25f, 0.20f, 0.95f) : new Color(0.95f, 0.98f, 1f, 0.92f));
                GL.Vertex(a);
                GL.Vertex(b);

                // For terminal extremities (Head=12, Feet=3,6, Hands=16,20), also extend from joint pivot to part center
                if (k == 12 || k == 3 || k == 6 || k == 16 || k == 20)
                {
                    GL.Vertex(b);
                    GL.Vertex(partPositions[k]);
                }
            }

            // All 20 joint pivots + root pelvis + contact cubes + foot & hand boxes (matching Playroom's 3D skeleton!)
            for (int k = 0; k < n; k++)
            {
                if (!partValid[k]) continue;
                Color jCol = (k >= 1 && k <= 3) || (k >= 13 && k <= 16)
                    ? new Color(0.25f, 0.65f, 1.0f, 0.95f)
                    : ((k >= 4 && k <= 6) || (k >= 17 && k <= 20)
                        ? new Color(1.0f, 0.35f, 0.35f, 0.95f)
                        : new Color(1.0f, 0.88f, 0.20f, 0.95f));

                // Draw true joint pivot marker (or pelvis root)
                Vector3 pivotPos = hasJointPivot[k] ? jointPivots[k] : partPositions[k];
                DrawCross(pivotPos, 0.048f, jCol);

                // Contact marker (Playroom-style red cube on touching parts)
                if (partContacts[k] != 0)
                {
                    DrawAxisBox(partPositions[k], Quaternion.identity, Vector3.one * 0.055f, new Color(1f, 0.15f, 0.12f, 0.95f));
                }

                // Draw foot wire boxes (3, 6) and hand wire boxes (16, 20)
                if (k == 3 || k == 6 || k == 16 || k == 20)
                {
                    int kind = nm_agent_get_part_body(agent, k, massBuf, inertiaBuf, boxBuf);
                    Vector3 half = kind == 2
                        ? new Vector3(Mathf.Max(boxBuf[0], 0.03f), Mathf.Max(boxBuf[2], 0.03f), Mathf.Max(boxBuf[1], 0.03f))
                        : new Vector3(0.045f, 0.035f, 0.075f);
                    Color boxCol = (k == 3 || k == 16)
                        ? new Color(0.30f, 0.85f, 1.0f, 0.95f)
                        : new Color(1.0f, 0.45f, 0.35f, 0.95f);
                    DrawAxisBox(partPositions[k], partRotations[k], half, boxCol);
                }
            }

            // Center of Mass crosshair (orange) + velocity vector (lime) + ground height plumb line (green)
            if (nm_agent_get_com(agent, comBuf, velBuf) >= 0)
            {
                Vector3 com = ToUnityVec(comBuf, 0) + visOffset;
                Vector3 vel = ToUnityVec(velBuf, 0);
                DrawCross(com, 0.09f, new Color(1.0f, 0.55f, 0.05f, 1.0f));
                if (vel.sqrMagnitude > 0.01f)
                {
                    GL.Color(new Color(0.30f, 1.0f, 0.35f, 0.95f));
                    GL.Vertex(com);
                    GL.Vertex(com + vel * 0.35f);
                }

                if (ctrl.Actor != null)
                {
                    float groundY = runtime.SampleGroundHeight(com + Vector3.up * 0.25f, ctrl.Actor.transform, com.y - 1f);
                    Vector3 gPt = new Vector3(com.x, groundY, com.z);
                    GL.Color(new Color(0.25f, 0.95f, 0.50f, 0.65f));
                    GL.Vertex(com);
                    GL.Vertex(gPt);
                    DrawCross(gPt, 0.10f, new Color(0.25f, 1.0f, 0.50f, 0.95f));
                }
            }
        }

        private static void DrawCross(Vector3 p, float r, Color col)
        {
            GL.Color(col);
            GL.Vertex(p - Vector3.right * r); GL.Vertex(p + Vector3.right * r);
            GL.Vertex(p - Vector3.up * r); GL.Vertex(p + Vector3.up * r);
            GL.Vertex(p - Vector3.forward * r); GL.Vertex(p + Vector3.forward * r);
        }

        private static void DrawAxisBox(Vector3 center, Quaternion rot, Vector3 half, Color col)
        {
            GL.Color(col);
            Vector3 c000 = center + rot * new Vector3(-half.x, -half.y, -half.z);
            Vector3 c100 = center + rot * new Vector3( half.x, -half.y, -half.z);
            Vector3 c010 = center + rot * new Vector3(-half.x,  half.y, -half.z);
            Vector3 c110 = center + rot * new Vector3( half.x,  half.y, -half.z);
            Vector3 c001 = center + rot * new Vector3(-half.x, -half.y,  half.z);
            Vector3 c101 = center + rot * new Vector3( half.x, -half.y,  half.z);
            Vector3 c011 = center + rot * new Vector3(-half.x,  half.y,  half.z);
            Vector3 c111 = center + rot * new Vector3( half.x,  half.y,  half.z);

            GL.Vertex(c000); GL.Vertex(c100); GL.Vertex(c100); GL.Vertex(c110); GL.Vertex(c110); GL.Vertex(c010); GL.Vertex(c010); GL.Vertex(c000);
            GL.Vertex(c001); GL.Vertex(c101); GL.Vertex(c101); GL.Vertex(c111); GL.Vertex(c111); GL.Vertex(c011); GL.Vertex(c011); GL.Vertex(c001);
            GL.Vertex(c000); GL.Vertex(c001); GL.Vertex(c100); GL.Vertex(c101); GL.Vertex(c110); GL.Vertex(c111); GL.Vertex(c010); GL.Vertex(c011);
        }
    }
}
