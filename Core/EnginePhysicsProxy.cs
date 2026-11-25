using System;
using System.Collections.Generic;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    /// <summary>
    /// Delirium EnginePhysics proxy: mirrors NM's 21 capsule/box parts as lightweight kinematic Unity
    /// rigidbodies driven via MovePosition/MoveRotation in FixedUpdate.
    /// Dying and dead ragdolls physically shove loose props, dropped weapons, grenades, and triggers
    /// while ignoring the owner's own hitboxes and the player's movement collider so walking over bodies never snags.
    /// </summary>
    public sealed class EnginePhysicsProxy : IDisposable
    {
        private GameObject rootObj;
        private Rigidbody[] bodies;
        private Collider[] colliders;
        private readonly float[] posBuf = new float[3];
        private readonly float[] rotBuf = new float[4];

        public bool IsActive => rootObj != null && bodies != null && bodies.Length > 0;
        public Transform RootTransform => rootObj != null ? rootObj.transform : null;

        public void Build(Actor owner, IntPtr agent, IntPtr build, float scale)
        {
            Dispose();
            if (owner == null || agent == IntPtr.Zero || build == IntPtr.Zero) return;

            int n = nm_agent_part_count(agent);
            if (n <= 0 || n > 32) return;

            rootObj = new GameObject($"DeliriumEnginePhys_{owner.name}");
            bodies = new Rigidbody[n];
            colliders = new Collider[n];

            // Use Unity's "Ignore Raycast" layer (layer 2):
            // 1. Ravenfield Projectile raycasts skip layer 2, so bullets pass cleanly to real Hitbox colliders.
            // 2. DeliriumRuntime static BVH ignores layer 2, so proxy colliders never pollute world geometry.
            // 3. Unity PhysX still collides layer 2 kinematic bodies against loose props, dropped weapons, grenades & triggers.
            int proxyLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (proxyLayer < 0) proxyLayer = 2;
            var massArr = new float[1];
            var inertiaArr = new float[3];
            var boxArr = new float[3];
            var radArr = new float[1];
            var halfArr = new float[1];

            for (int k = 0; k < n; k++)
            {
                if (nm_agent_get_part_transform(agent, k, posBuf, rotBuf) < 0)
                    continue;

                var go = new GameObject($"part_{k}");
                go.layer = proxyLayer;
                go.transform.SetPositionAndRotation(ToUnityVec(posBuf, 0), ToUnityQuat(rotBuf, 0));
                go.transform.SetParent(rootObj.transform, true);

                nm_build_part_info(build, k, null, radArr, halfArr, out _);
                int kind = nm_agent_get_part_body(agent, k, massArr, inertiaArr, boxArr);

                Collider col;
                if (kind == 2)
                {
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = new Vector3(
                        Mathf.Max(boxArr[0] * 2f * scale, 0.04f),
                        Mathf.Max(boxArr[2] * 2f * scale, 0.04f),
                        Mathf.Max(boxArr[1] * 2f * scale, 0.04f));
                    col = bc;
                }
                else
                {
                    var cc = go.AddComponent<CapsuleCollider>();
                    // NM capsules run along NM local Y, which maps to Unity local Z
                    cc.direction = 2;
                    cc.radius = Mathf.Max(radArr[0] * scale, 0.025f);
                    cc.height = Mathf.Max(2f * (halfArr[0] + radArr[0]) * scale, cc.radius * 2f);
                    col = cc;
                }

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Clamp(massArr[0], 0.3f, 25f);
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                bodies[k] = rb;
                colliders[k] = col;
            }

            Physics.SyncTransforms();

            // Ignore self-collisions among the 21 proxy parts
            for (int i = 0; i < n; i++)
            {
                if (colliders[i] == null) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (colliders[j] != null)
                        Physics.IgnoreCollision(colliders[i], colliders[j], true);
                }
            }

            // Ignore the owner's own colliders (animated + ragdoll hitboxes)
            IgnoreHierarchy(owner.transform);

            // Ignore the player's colliders / CharacterController so walking over dying/dead bodies never snags
            var player = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (player != null && player != owner)
            {
                IgnoreHierarchy(player.transform);
                var fps = player.controller as FpsActorController;
                if (fps != null)
                    IgnoreHierarchy(fps.transform);
            }
        }

        private void IgnoreHierarchy(Transform root)
        {
            if (root == null || colliders == null) return;
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var mine = colliders[i];
                if (mine == null) continue;
                for (int j = 0; j < cols.Length; j++)
                {
                    var other = cols[j];
                    if (other != null && other != mine)
                        Physics.IgnoreCollision(mine, other, true);
                }
            }
        }

        public void SyncFixedUpdate(IntPtr agent)
        {
            if (agent == IntPtr.Zero || bodies == null) return;
            for (int k = 0; k < bodies.Length; k++)
            {
                var rb = bodies[k];
                if (rb == null) continue;
                if (nm_agent_get_part_transform(agent, k, posBuf, rotBuf) < 0) continue;
                rb.MovePosition(ToUnityVec(posBuf, 0));
                rb.MoveRotation(ToUnityQuat(rotBuf, 0));
            }
        }

        public bool OwnsCollider(Collider col)
        {
            if (col == null || rootObj == null) return false;
            return col.transform.IsChildOf(rootObj.transform);
        }

        public void Dispose()
        {
            if (rootObj != null)
            {
                UnityEngine.Object.Destroy(rootObj);
                rootObj = null;
            }
            bodies = null;
            colliders = null;
        }
    }
}
