using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DeliriumRavenfield.Config;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Core
{
    public sealed class SharedWorldEntry
    {
        public IntPtr World;
        public float Scale;
        public Vector3 Anchor;
        public int SceneId;
        public int RefCount;
    }

    internal sealed class TrackedVehicle
    {
        public Vehicle Vehicle;
        public Vector3 LastPos;
        public Quaternion LastRot;
        public Vector3 Vel;
        public Vector3 Spin;
        public Vector3 CenterLocal;
        public Vector3 HalfExtents;
        public bool Initialized;
    }

    public sealed class DeliriumRuntime : IDisposable
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        private readonly ModConfig config;
        private readonly List<SharedWorldEntry> worlds = new List<SharedWorldEntry>();
        private readonly List<(float friction, float bounce)> surfaceTable = new List<(float, float)>();
        private readonly Dictionary<Vehicle, TrackedVehicle> trackedVehicles = new Dictionary<Vehicle, TrackedVehicle>();
        private readonly List<TrackedVehicle> activeVehicleFrameList = new List<TrackedVehicle>();
        private readonly Stopwatch stepWatch = new Stopwatch();

        private float accumulator;
        private int ignoredStaticLayersMask;

        public bool IsReady { get; private set; }
        public IntPtr BaseBuild { get; private set; }
        public SkeletonMapper SkeletonMapper { get; }
        public float LastStepMs { get; private set; }
        public int ActiveWorldCount => worlds.Count;
        public int ActiveVehicleCount => activeVehicleFrameList.Count;

        public DeliriumRuntime(ModConfig config)
        {
            this.config = config;
            SkeletonMapper = new SkeletonMapper(config);
        }

        public bool Initialize(string pluginDirectory)
        {
            try
            {
                string dllPath = Path.Combine(pluginDirectory, "delirium_physics.dll");
                if (!File.Exists(dllPath))
                {
                    Plugin.Log.LogError($"[DeliriumRuntime] Native DLL not found at: {dllPath}");
                    return false;
                }

                IntPtr handle = LoadLibrary(dllPath);
                if (handle == IntPtr.Zero)
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    Plugin.Log.LogError($"[DeliriumRuntime] LoadLibrary failed for '{dllPath}' (Win32 error {win32Err}).");
                    return false;
                }

                // Crash report directory next to BepInEx config
                nm_crash_report(BepInEx.Paths.ConfigPath);

                // Pure No-Game initialization: built-in kv-own.txt + taskparams-own.txt + homer-own.json
                int missing = nm_unlock_own(null);
                if (missing < 0)
                {
                    Plugin.Log.LogError($"[DeliriumRuntime] nm_unlock_own failed: {LastError()}");
                    return false;
                }

                string ownCharJson = Str((buf, len) => nm_own_character(buf, len));
                if (string.IsNullOrEmpty(ownCharJson))
                {
                    Plugin.Log.LogError($"[DeliriumRuntime] nm_own_character returned empty JSON: {LastError()}");
                    return false;
                }

                Check(nm_init_character(ownCharJson), "nm_init_character");
                BaseBuild = nm_build_from_character(ownCharJson);
                if (BaseBuild == IntPtr.Zero)
                {
                    Plugin.Log.LogError($"[DeliriumRuntime] nm_build_from_character failed: {LastError()}");
                    return false;
                }

                string tuneCachePath = Path.Combine(BepInEx.Paths.ConfigPath, "delirium-ravenfield-tunes.txt");
                nm_tune_file(tuneCachePath);

                ApplyGlobalSettings();
                BuildIgnoredLayerMask();

                IsReady = true;
                Plugin.Log.LogInfo($"[DeliriumRuntime] Ready (No-Game mode, SDK v{nm_version()}, missing slots={missing}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DeliriumRuntime] Initialization exception: {ex}");
                return false;
            }
        }

        public void ApplyGlobalSettings()
        {
            if (BaseBuild == IntPtr.Zero) return;
            // Let Ravenfield itself decide fatal headshots via Actor.dead; if Rust sets a.dead=true on a
            // non-lethal head graze, alive bots drop instantly and nm_agent_should_get_up stays 0 forever.
            nm_set_headshot_kills(0);
            nm_set_step_limit(config.StepLimit.Value ? 1 : 0);
            nm_set_fire_reaction(1);
        }

        private void BuildIgnoredLayerMask()
        {
            int mask = 0;
            // Exclude Ravenfield hitbox, ragdoll, seated, vehicle, and water layers from static world BVH
            mask |= (1 << Hitbox.LAYER);
            mask |= (1 << Hitbox.RAGDOLL_LAYER);
            mask |= (1 << Hitbox.SEATED_LAYER);
            mask |= (1 << Vehicle.LAYER);
            mask |= (1 << Vehicle.FIRST_PERSON_LAYER);
            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer >= 0) mask |= (1 << waterLayer);
            int ignoreRay = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRay >= 0) mask |= (1 << ignoreRay);
            ignoredStaticLayersMask = mask;
        }

        public SharedWorldEntry AcquireWorld(Vector3 hipsPos, float groundY, Transform ignoreOwner, float scale, int sceneId)
        {
            var spot = new Vector3(hipsPos.x, groundY, hipsPos.z);
            float shareRadius = 8f * scale;
            float shareHeight = 1.0f * scale;

            for (int i = 0; i < worlds.Count; i++)
            {
                var w = worlds[i];
                if (w.SceneId == sceneId &&
                    Mathf.Abs(w.Scale - scale) < 0.01f &&
                    Mathf.Abs(w.Anchor.y - groundY) < shareHeight &&
                    Vector3.Distance(w.Anchor, spot) < shareRadius)
                {
                    w.RefCount++;
                    return w;
                }
            }

            IntPtr worldPtr = nm_world_create();
            if (worldPtr == IntPtr.Zero)
                throw new Exception($"nm_world_create failed: {LastError()}");

            Check(nm_world_set_scale(worldPtr, scale), "nm_world_set_scale");
            PopulateStaticGeometry(worldPtr, hipsPos, groundY, ignoreOwner, scale);

            var entry = new SharedWorldEntry
            {
                World = worldPtr,
                Scale = scale,
                Anchor = spot,
                SceneId = sceneId,
                RefCount = 1
            };
            worlds.Add(entry);
            return entry;
        }

        public void ReleaseWorld(SharedWorldEntry entry)
        {
            if (entry == null) return;
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                worlds.Remove(entry);
                if (entry.World != IntPtr.Zero)
                {
                    nm_world_destroy(entry.World);
                    entry.World = IntPtr.Zero;
                }
            }
        }

        public void ClearAllWorlds()
        {
            foreach (var w in worlds)
            {
                if (w.World != IntPtr.Zero) nm_world_destroy(w.World);
            }
            worlds.Clear();
            trackedVehicles.Clear();
            activeVehicleFrameList.Clear();
        }

        public void StepSimulation(IReadOnlyList<ActorRagdollController> activeControllers, float deltaTime)
        {
            if (!IsReady || activeControllers.Count == 0)
            {
                LastStepMs = 0f;
                return;
            }

            stepWatch.Restart();

            if (config.EnableVehicles.Value)
                SyncVehicles(activeControllers, deltaTime);

            // Fixed 60 Hz Delirium physics stepping (capped at 4 substeps per frame to prevent spiral of death)
            accumulator = Mathf.Min(accumulator + deltaTime, 4f / 60f);
            const float dt = 1f / 60f;

            while (accumulator >= dt)
            {
                accumulator -= dt;

                // Group active agents by shared NmWorld and step each world across CPU cores via rayon
                for (int wi = 0; wi < worlds.Count; wi++)
                {
                    var w = worlds[wi];
                    if (w.World == IntPtr.Zero) continue;

                    var agentList = new List<IntPtr>(8);
                    for (int ci = 0; ci < activeControllers.Count; ci++)
                    {
                        var ctrl = activeControllers[ci];
                        if (ctrl.WorldEntry == w && ctrl.Agent != IntPtr.Zero && ctrl.IsSimulating)
                            agentList.Add(ctrl.Agent);
                    }

                    if (agentList.Count == 0) continue;
                    int r = nm_world_step_agents(w.World, agentList.ToArray(), agentList.Count, dt, 0);
                    if (r < 0 && config.DebugLog.Value)
                        Plugin.Log.LogWarning($"[DeliriumRuntime] nm_world_step_agents error: {LastError()}");
                }
            }

            // Process vehicle impacts after stepping
            if (config.EnableVehicles.Value && activeVehicleFrameList.Count > 0)
            {
                ProcessVehicleHits(activeControllers);
            }

            stepWatch.Stop();
            LastStepMs = (float)stepWatch.Elapsed.TotalMilliseconds;
        }

        private void SyncVehicles(IReadOnlyList<ActorRagdollController> activeControllers, float dt)
        {
            activeVehicleFrameList.Clear();
            var am = ActorManager.instance;
            if (am == null || am.vehicles == null) return;

            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            float maxDistSqr = (config.ActiveDistance.Value + 25f) * (config.ActiveDistance.Value + 25f);

            // Prune destroyed vehicles
            var deadKeys = trackedVehicles.Keys.Where(v => v == null || !v.gameObject.activeInHierarchy).ToList();
            foreach (var k in deadKeys) trackedVehicles.Remove(k);

            foreach (var veh in am.vehicles)
            {
                if (veh == null || !veh.gameObject.activeInHierarchy || veh.isTurret) continue;
                if (cam != null && (veh.transform.position - camPos).sqrMagnitude > maxDistSqr) continue;

                if (!trackedVehicles.TryGetValue(veh, out var tv))
                {
                    tv = CreateTrackedVehicle(veh);
                    trackedVehicles[veh] = tv;
                }

                Transform t = veh.transform;
                Quaternion q = t.rotation;
                if (tv.Initialized && dt > 1e-4f)
                {
                    tv.Vel = veh.rigidbody != null ? veh.rigidbody.velocity : (t.position - tv.LastPos) / dt;
                    (q * Quaternion.Inverse(tv.LastRot)).ToAngleAxis(out float deg, out Vector3 axis);
                    if (deg > 180f) deg -= 360f;
                    tv.Spin = (float.IsInfinity(axis.x) || float.IsNaN(axis.x) || Mathf.Abs(deg) < 1e-4f)
                        ? Vector3.zero
                        : axis * (deg * Mathf.Deg2Rad / dt);
                }
                else
                {
                    tv.Vel = veh.rigidbody != null ? veh.rigidbody.velocity : Vector3.zero;
                    tv.Spin = Vector3.zero;
                    tv.Initialized = true;
                }

                tv.LastPos = t.position;
                tv.LastRot = q;
                activeVehicleFrameList.Add(tv);
            }

            if (activeVehicleFrameList.Count == 0) return;

            int count = activeVehicleFrameList.Count;
            var boxes = new float[count * 13];
            var kinds = new int[count];
            var spins = new float[count * 3];

            for (int k = 0; k < count; k++)
            {
                var tv = activeVehicleFrameList[k];
                Transform t = tv.Vehicle.transform;
                Quaternion q = t.rotation;
                Vector3 worldCenter = t.TransformPoint(tv.CenterLocal);

                WriteNmVec(worldCenter, boxes, k * 13);
                WriteNmQuat(q, boxes, k * 13 + 3);
                boxes[k * 13 + 7] = tv.HalfExtents.x;
                boxes[k * 13 + 8] = tv.HalfExtents.z;
                boxes[k * 13 + 9] = tv.HalfExtents.y;
                WriteNmVec(tv.Vel, boxes, k * 13 + 10);

                kinds[k] = NM_HIT_CAR | (tv.Vehicle.playerIsInside ? NM_HIT_PLAYER : 0);
                var sp = ToNmSpin(tv.Spin);
                spins[k * 3] = sp[0];
                spins[k * 3 + 1] = sp[1];
                spins[k * 3 + 2] = sp[2];

                // Pre-impact bracing check for standing bots directly ahead of a moving vehicle
                if (tv.Vel.magnitude >= 3.0f)
                {
                    CheckApproachingVehicleBrace(tv, k, worldCenter, q);
                }
            }

            for (int wi = 0; wi < worlds.Count; wi++)
            {
                var w = worlds[wi].World;
                if (w == IntPtr.Zero) continue;
                nm_world_set_boxes(w, boxes, count);
                nm_world_set_box_kinds(w, kinds, count);
                nm_world_set_box_spins(w, spins, count);
            }

            // Drive active brace controllers
            for (int ci = 0; ci < activeControllers.Count; ci++)
            {
                var ctrl = activeControllers[ci];
                if (ctrl.BracingVehicleIndex < 0 || !ctrl.IsSimulating || ctrl.Agent == IntPtr.Zero) continue;
                ctrl.BraceTimeLeft -= dt;
                if (ctrl.BraceTimeLeft <= 0f || ctrl.BracingVehicleIndex >= count)
                {
                    ctrl.BracingVehicleIndex = -1;
                    continue;
                }
                var tv = activeVehicleFrameList[ctrl.BracingVehicleIndex];
                var t = tv.Vehicle.transform;
                var m = new float[12];
                WriteNmVec(t.right, m, 0);
                WriteNmVec(t.forward, m, 3);
                WriteNmVec(t.up, m, 6);
                WriteNmVec(t.TransformPoint(tv.CenterLocal), m, 9);
                nm_agent_task_brace_control(ctrl.Agent, ctrl.WorldEntry.World, m, null, null);
            }
        }

        private void CheckApproachingVehicleBrace(TrackedVehicle tv, int boxIndex, Vector3 worldCenter, Quaternion rot)
        {
            var lod = Plugin.LodManager;
            if (lod == null) return;

            Vector3 lookAheadCenter = worldCenter + tv.Vel * 0.35f;
            Vector3 probeHalf = tv.HalfExtents + Vector3.one * 0.6f;
            var hits = Physics.OverlapBox(lookAheadCenter, probeHalf, rot, (1 << Hitbox.LAYER) | (1 << Hitbox.RAGDOLL_LAYER));
            for (int i = 0; i < hits.Length; i++)
            {
                var hb = hits[i].GetComponent<Hitbox>();
                var actor = hb != null ? hb.parent as Actor : hits[i].GetComponentInParent<Actor>();
                if (actor == null || actor.dead || actor.IsSeated() || actor == ActorManager.instance?.player) continue;
                if (actor.transform.IsChildOf(tv.Vehicle.transform)) continue;

                var ctrl = lod.TryAcquireForLiveReaction(actor);
                if (ctrl != null && ctrl.BracingVehicleIndex < 0 && ctrl.Agent != IntPtr.Zero && ctrl.IsSimulating)
                {
                    nm_agent_task_brace(ctrl.Agent, ctrl.WorldEntry.World, null);
                    ctrl.BracingVehicleIndex = boxIndex;
                    ctrl.BraceTimeLeft = 2.5f;
                }
            }
        }

        private void ProcessVehicleHits(IReadOnlyList<ActorRagdollController> activeControllers)
        {
            var dmgBuf = new float[1];
            for (int i = 0; i < activeControllers.Count; i++)
            {
                var ctrl = activeControllers[i];
                if (!ctrl.IsSimulating || ctrl.Agent == IntPtr.Zero || ctrl.Actor == null) continue;
                float nmHealth = ctrl.Actor.dead ? 0f : Mathf.Clamp((ctrl.Actor.health / Mathf.Max(ctrl.Actor.maxHealth, 1f)) * 200f, 1f, 200f);
                bool playerDriver = activeVehicleFrameList.Any(v => v.Vehicle.playerIsInside);
                if (nm_agent_car_hit(ctrl.Agent, ctrl.WorldEntry.World, nmHealth, 5, playerDriver ? 1 : 0, dmgBuf, out int hitBox) == 1)
                {
                    ctrl.BracingVehicleIndex = -1;
                    if (config.DebugLog.Value)
                        Plugin.Log.LogInfo($"[DeliriumRuntime] Vehicle impact on {ctrl.Actor.name} (box={hitBox}, nmDmg={dmgBuf[0]:F1}).");
                }
            }
        }

        private static TrackedVehicle CreateTrackedVehicle(Vehicle veh)
        {
            Vector3 center = veh.ramOffset;
            Vector3 half = veh.ramSize * 0.5f;

            if (half.sqrMagnitude < 0.05f)
            {
                var bounds = new Bounds(veh.transform.position, Vector3.zero);
                bool any = false;
                if (veh.colliders != null)
                {
                    foreach (var c in veh.colliders)
                    {
                        if (c == null || c.isTrigger) continue;
                        if (!any) { bounds = c.bounds; any = true; }
                        else bounds.Encapsulate(c.bounds);
                    }
                }
                if (any)
                {
                    center = veh.transform.InverseTransformPoint(bounds.center);
                    half = bounds.extents;
                }
                else
                {
                    center = Vector3.up * 0.8f;
                    half = new Vector3(1.1f, 0.8f, 2.2f);
                }
            }

            half = new Vector3(Mathf.Max(half.x, 0.3f), Mathf.Max(half.y, 0.3f), Mathf.Max(half.z, 0.3f));
            return new TrackedVehicle
            {
                Vehicle = veh,
                CenterLocal = center,
                HalfExtents = half
            };
        }

        private void PopulateStaticGeometry(IntPtr world, Vector3 center, float groundY, Transform ignoreOwner, float scale)
        {
            const float radius = 15f;
            float r = radius * scale;
            var pts = new List<float>(4096);
            var tris = new List<uint>(2048);
            var mats = new List<byte>(1024);
            byte mat = 0;

            void AddTri(Vector3 a, Vector3 b, Vector3 d, bool flipWinding = false)
            {
                uint baseIdx = (uint)(pts.Count / 3);
                // Left-handed -> right-handed winding flip: (a, d, b); invert again if collider has negative lossyScale
                pts.Add(a.x); pts.Add(a.z); pts.Add(a.y);
                if (flipWinding)
                {
                    pts.Add(b.x); pts.Add(b.z); pts.Add(b.y);
                    pts.Add(d.x); pts.Add(d.z); pts.Add(d.y);
                }
                else
                {
                    pts.Add(d.x); pts.Add(d.z); pts.Add(d.y);
                    pts.Add(b.x); pts.Add(b.z); pts.Add(b.y);
                }
                tris.Add(baseIdx);
                tris.Add(baseIdx + 1);
                tris.Add(baseIdx + 2);
                mats.Add(mat);
            }

            int layerMask = ~ignoredStaticLayersMask;
            var colliders = Physics.OverlapSphere(center, r, layerMask, QueryTriggerInteraction.Ignore);
            for (int ci = 0; ci < colliders.Length; ci++)
            {
                var col = colliders[ci];
                if (col == null || col.isTrigger) continue;
                if (ignoreOwner != null && col.transform.IsChildOf(ignoreOwner)) continue;
                if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) continue;
                if (col.GetComponentInParent<Actor>() != null || col.GetComponentInParent<Vehicle>() != null) continue;

                Transform t = col.transform;
                Vector3 ls = t.lossyScale;
                bool negScale = (ls.x * ls.y * ls.z) < 0f;
                mat = ResolveSurfaceMaterial(col);

                if (col is MeshCollider mc && mc.sharedMesh != null && mc.sharedMesh.isReadable)
                {
                    var mesh = mc.sharedMesh;
                    var rawVerts = mesh.vertices;
                    var indices = mesh.triangles;
                    var worldVerts = new Vector3[rawVerts.Length];
                    for (int vi = 0; vi < rawVerts.Length; vi++)
                        worldVerts[vi] = t.TransformPoint(rawVerts[vi]);

                    for (int ti = 0; ti + 2 < indices.Length; ti += 3)
                    {
                        Vector3 a = worldVerts[indices[ti]];
                        Vector3 b = worldVerts[indices[ti + 1]];
                        Vector3 d = worldVerts[indices[ti + 2]];
                        Vector3 mid = (a + b + d) * (1f / 3f);
                        float triRadius = Mathf.Max((a - mid).magnitude, Mathf.Max((b - mid).magnitude, (d - mid).magnitude));
                        if ((mid - center).magnitude < r + triRadius)
                            AddTri(a, b, d, negScale);
                    }
                }
                else if (col is BoxCollider box)
                {
                    Vector3 inside = t.InverseTransformPoint(center) - box.center;
                    if (Mathf.Abs(inside.x) < box.size.x * 0.5f &&
                        Mathf.Abs(inside.y) < box.size.y * 0.5f &&
                        Mathf.Abs(inside.z) < box.size.z * 0.5f)
                        continue;

                    var k = new Vector3[8];
                    Vector3 half = box.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                    {
                        k[i] = t.TransformPoint(box.center + Vector3.Scale(half,
                            new Vector3((i & 1) * 2 - 1, (i & 2) - 1, ((i >> 1) & 2) - 1)));
                    }

                    int[][] faces =
                    {
                        new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
                        new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 },
                        new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }
                    };
                    for (int fi = 0; fi < faces.Length; fi++)
                    {
                        var f = faces[fi];
                        AddTri(k[f[0]], k[f[1]], k[f[2]], negScale);
                        AddTri(k[f[0]], k[f[2]], k[f[3]], negScale);
                    }
                }
                else if (col is TerrainCollider tc && tc.terrainData != null)
                {
                    var td = tc.terrainData;
                    Vector3 origin = t.position;
                    float xMin = Mathf.Max(center.x - r, origin.x);
                    float xMax = Mathf.Min(center.x + r, origin.x + td.size.x - 1f);
                    float zMin = Mathf.Max(center.z - r, origin.z);
                    float zMax = Mathf.Min(center.z + r, origin.z + td.size.z - 1f);

                    Vector3 Sample(float x, float z) =>
                        new Vector3(x, origin.y + td.GetInterpolatedHeight((x - origin.x) / td.size.x, (z - origin.z) / td.size.z), z);

                    for (float x = xMin; x < xMax; x += 1f)
                    {
                        for (float z = zMin; z < zMax; z += 1f)
                        {
                            Vector3 p00 = Sample(x, z);
                            Vector3 p01 = Sample(x, z + 1f);
                            Vector3 p11 = Sample(x + 1f, z + 1f);
                            Vector3 p10 = Sample(x + 1f, z);
                            AddTri(p00, p01, p11);
                            AddTri(p00, p11, p10);
                        }
                    }
                }
            }

            if (tris.Count > 0)
            {
                nm_world_add_mesh(world, pts.ToArray(), pts.Count / 3, tris.ToArray(), tris.Count / 3, mats.ToArray());
            }

            // Add a safety floor below feet in case map meshes are marked non-readable.
            // Offset 2.5cm below groundY when real level triangles already exist so we never create
            // coplanar duplicate floor planes (which double contact impulses and cause ground launches!).
            float safetyFloorY = tris.Count > 0 ? (groundY - 0.025f) : groundY;
            byte floorMat = ResolveSurfaceMaterial(SampleGroundCollider(center, ignoreOwner));
            if (floorMat == 0)
            {
                nm_world_add_floor(world, center.x, center.z, safetyFloorY, 80f * scale);
            }
            else
            {
                float h = 80f * scale;
                var v = new[]
                {
                    center.x - h, center.z - h, safetyFloorY,
                    center.x + h, center.z - h, safetyFloorY,
                    center.x + h, center.z + h, safetyFloorY,
                    center.x - h, center.z + h, safetyFloorY
                };
                nm_world_add_mesh(world, v, 4, new uint[] { 0, 1, 2, 0, 2, 3 }, 2, new[] { floorMat, floorMat });
            }
        }

        public float SampleGroundHeight(Vector3 fromPos, Transform ignoreOwner, float fallbackY)
        {
            int mask = ~ignoredStaticLayersMask;
            var hits = Physics.RaycastAll(fromPos + Vector3.up * 0.2f, Vector3.down, 6f, mask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            float bestY = fallbackY;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignoreOwner != null && h.transform.IsChildOf(ignoreOwner)) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    bestY = h.point.y;
                }
            }
            return bestY;
        }

        private Collider SampleGroundCollider(Vector3 fromPos, Transform ignoreOwner)
        {
            int mask = ~ignoredStaticLayersMask;
            var hits = Physics.RaycastAll(fromPos + Vector3.up * 0.2f, Vector3.down, 6f, mask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            Collider best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignoreOwner != null && h.transform.IsChildOf(ignoreOwner)) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h.collider;
                }
            }
            return best;
        }

        private byte ResolveSurfaceMaterial(Collider col)
        {
            var m = col != null ? col.sharedMaterial : null;
            if (m == null || (Mathf.Approximately(m.dynamicFriction, 0.6f) && Mathf.Approximately(m.bounciness, 0f)))
                return 0;

            var key = (m.dynamicFriction / 0.6f, Mathf.Clamp01(m.bounciness));
            int idx = surfaceTable.IndexOf(key);
            if (idx < 0)
            {
                surfaceTable.Add(key);
                idx = surfaceTable.Count - 1;
                if (idx < 56) nm_set_surface(200 + idx, key.Item1, key.Item2);
            }
            return (byte)(200 + Math.Min(idx, 55));
        }

        public void Dispose()
        {
            ClearAllWorlds();
            SkeletonMapper.DisposeAllTunedBuilds();
            if (BaseBuild != IntPtr.Zero)
            {
                nm_build_destroy(BaseBuild);
                BaseBuild = IntPtr.Zero;
            }
            IsReady = false;
        }
    }
}
