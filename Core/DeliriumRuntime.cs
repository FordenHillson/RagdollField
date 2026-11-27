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
        public int TriangleCount;
        public int UnreadableMeshCount;
        /// <summary>Flat array of Unity world-space triangle vertices (3 per tri) for optional 3D BVH debug overlay.</summary>
        public Vector3[] DebugTriangles;
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
        private readonly HashSet<Rigidbody> looseMoverSet = new HashSet<Rigidbody>();
        private readonly List<Rigidbody> looseMoverList = new List<Rigidbody>();
        private int lastSyncedBoxCount;
        private readonly Stopwatch stepWatch = new Stopwatch();

        private float accumulator;
        private int ignoredStaticLayersMask;

        public bool IsReady { get; private set; }
        public IntPtr BaseBuild { get; private set; }
        public SkeletonMapper SkeletonMapper { get; }
        public float LastStepMs { get; private set; }
        public int ActiveWorldCount => worlds.Count;
        public int ActiveVehicleCount => activeVehicleFrameList.Count;
        public IReadOnlyList<SharedWorldEntry> Worlds => worlds;

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
            float shareRadius = 6f * scale;
            // Share within 1.5m vertical so bots on adjacent stair steps collide with each other,
            // while bots on separate building stories (~3m apart) still get their own ceiling-aware world.
            float shareHeight = 1.5f * scale;

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
            var entry = new SharedWorldEntry
            {
                World = worldPtr,
                Scale = scale,
                Anchor = spot,
                SceneId = sceneId,
                RefCount = 1
            };
            PopulateStaticGeometry(entry, hipsPos, groundY, ignoreOwner, scale);
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

            if (config.EnableVehicles.Value || config.EnableEnginePhysicsColliders.Value)
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
                        if (ctrl.IsSyncLockstepControlled) continue;
                        if (ctrl.WorldEntry == w && ctrl.Agent != IntPtr.Zero && ctrl.IsSimulating)
                            agentList.Add(ctrl.Agent);
                    }

                    if (agentList.Count == 0) continue;
                    int r = nm_world_step_agents(w.World, agentList.ToArray(), agentList.Count, dt, 0);
                    if (r < 0 && config.DebugLog.Value)
                        Plugin.Log.LogWarning($"[DeliriumRuntime] nm_world_step_agents error: {LastError()}");
                }
            }

            // Process vehicle / dynamic object impacts after stepping
            if ((config.EnableVehicles.Value || config.EnableEnginePhysicsColliders.Value) &&
                (activeVehicleFrameList.Count > 0 || looseMoverList.Count > 0))
            {
                ProcessVehicleHits(activeControllers);
            }

            stepWatch.Stop();
            LastStepMs = (float)stepWatch.Elapsed.TotalMilliseconds;
        }

        private void SyncVehicles(IReadOnlyList<ActorRagdollController> activeControllers, float dt)
        {
            activeVehicleFrameList.Clear();
            looseMoverSet.Clear();
            looseMoverList.Clear();

            var am = ActorManager.instance;
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            float maxDistSqr = (config.ActiveDistance.Value + 25f) * (config.ActiveDistance.Value + 25f);

            if (config.EnableVehicles.Value && am != null && am.vehicles != null)
            {
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
            }

            // Also gather nearby loose dynamic Unity rigidbodies (crates, barrels, debris, physics props) as NM_HIT_OBJECT movers
            if (config.EnableEnginePhysicsColliders.Value && worlds.Count > 0)
            {
                int mask = ~ignoredStaticLayersMask;
                for (int wi = 0; wi < worlds.Count; wi++)
                {
                    var w = worlds[wi];
                    if (w.World == IntPtr.Zero) continue;
                    var cols = Physics.OverlapSphere(w.Anchor, 6f * w.Scale, mask, QueryTriggerInteraction.Ignore);
                    for (int ci = 0; ci < cols.Length; ci++)
                    {
                        var col = cols[ci];
                        if (col == null || col.isTrigger) continue;
                        var rb = col.attachedRigidbody;
                        if (rb == null || rb.isKinematic || rb.IsSleeping()) continue;
                        if (rb.GetComponentInParent<Actor>() != null || rb.GetComponentInParent<Vehicle>() != null) continue;
                        if (looseMoverSet.Add(rb) && looseMoverList.Count < 24)
                            looseMoverList.Add(rb);
                    }
                }
            }

            int vehCount = activeVehicleFrameList.Count;
            int moverCount = looseMoverList.Count;
            int count = vehCount + moverCount;
            if (count == 0)
            {
                if (lastSyncedBoxCount > 0)
                {
                    var empty = Array.Empty<float>();
                    for (int wi = 0; wi < worlds.Count; wi++)
                    {
                        var w = worlds[wi].World;
                        if (w != IntPtr.Zero) nm_world_set_boxes(w, empty, 0);
                    }
                    lastSyncedBoxCount = 0;
                }
                return;
            }
            lastSyncedBoxCount = count;

            var boxes = new float[count * 13];
            var kinds = new int[count];
            var spins = new float[count * 3];

            for (int k = 0; k < vehCount; k++)
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

            for (int m = 0; m < moverCount; m++)
            {
                int k = vehCount + m;
                var rb = looseMoverList[m];
                var col = rb.GetComponentInChildren<Collider>();
                Vector3 half = col != null ? col.bounds.extents : Vector3.one * 0.25f;
                Vector3 center = col != null ? col.bounds.center : rb.worldCenterOfMass;
                half = new Vector3(
                    Mathf.Clamp(half.x, 0.08f, 3f),
                    Mathf.Clamp(half.y, 0.08f, 3f),
                    Mathf.Clamp(half.z, 0.08f, 3f));

                WriteNmVec(center, boxes, k * 13);
                WriteNmQuat(rb.rotation, boxes, k * 13 + 3);
                boxes[k * 13 + 7] = half.x;
                boxes[k * 13 + 8] = half.z;
                boxes[k * 13 + 9] = half.y;
                WriteNmVec(rb.velocity, boxes, k * 13 + 10);

                kinds[k] = NM_HIT_OBJECT;
                var sp = ToNmSpin(rb.angularVelocity);
                spins[k * 3] = sp[0];
                spins[k * 3 + 1] = sp[1];
                spins[k * 3 + 2] = sp[2];
            }

            for (int wi = 0; wi < worlds.Count; wi++)
            {
                var w = worlds[wi].World;
                if (w == IntPtr.Zero) continue;
                nm_world_set_boxes(w, boxes, count);
                nm_world_set_box_kinds(w, kinds, count);
                nm_world_set_box_spins(w, spins, count);
            }

            // Drive active brace controllers (only for vehicle indices)
            for (int ci = 0; ci < activeControllers.Count; ci++)
            {
                var ctrl = activeControllers[ci];
                if (ctrl.BracingVehicleIndex < 0 || !ctrl.IsSimulating || ctrl.Agent == IntPtr.Zero) continue;
                ctrl.BraceTimeLeft -= dt;
                if (ctrl.BraceTimeLeft <= 0f || ctrl.BracingVehicleIndex >= vehCount)
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

        private void PopulateStaticGeometry(SharedWorldEntry entry, Vector3 center, float groundY, Transform ignoreOwner, float scale)
        {
            IntPtr world = entry.World;
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

            Collider groundCol = SampleGroundCollider(center, ignoreOwner);
            bool addedGroundCol = false;
            var unreadableMeshes = new List<MeshCollider>();

            int layerMask = ~ignoredStaticLayersMask;
            var colliders = Physics.OverlapSphere(center, r, layerMask, QueryTriggerInteraction.Ignore);
            for (int ci = 0; ci < colliders.Length; ci++)
            {
                var col = colliders[ci];
                if (col == null || col.isTrigger) continue;
                if (ignoreOwner != null && col.transform.IsChildOf(ignoreOwner)) continue;
                if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) continue;
                if (col.GetComponentInParent<Actor>() != null || col.GetComponentInParent<Vehicle>() != null) continue;

                int trisBefore = tris.Count;
                Transform t = col.transform;
                Vector3 ls = t.lossyScale;
                bool negScale = (ls.x * ls.y * ls.z) < 0f;
                mat = ResolveSurfaceMaterial(col);

                if (col is MeshCollider mc && mc.sharedMesh != null)
                {
                    if (mc.sharedMesh.isReadable)
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
                    else
                    {
                        unreadableMeshes.Add(mc);
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
                else if (col is SphereCollider sph)
                {
                    Vector3 worldCenter = t.TransformPoint(sph.center);
                    float maxScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                    float sr = sph.radius * maxScale;
                    if (sr < 0.04f || (worldCenter - center).magnitude < sr * 0.9f) continue;

                    const int segs = 10;
                    const int rings = 5;
                    Vector3 top = worldCenter + Vector3.up * sr;
                    Vector3 bot = worldCenter - Vector3.up * sr;
                    var ringVerts = new Vector3[rings - 1, segs];
                    for (int ri = 1; ri < rings; ri++)
                    {
                        float phi = Mathf.PI * ri / rings;
                        float y = Mathf.Cos(phi) * sr;
                        float rr = Mathf.Sin(phi) * sr;
                        for (int si = 0; si < segs; si++)
                        {
                            float theta = (Mathf.PI * 2f * si) / segs;
                            ringVerts[ri - 1, si] = worldCenter + new Vector3(Mathf.Cos(theta) * rr, y, Mathf.Sin(theta) * rr);
                        }
                    }
                    for (int si = 0; si < segs; si++)
                    {
                        int sn = (si + 1) % segs;
                        AddTri(top, ringVerts[0, sn], ringVerts[0, si]);
                        AddTri(bot, ringVerts[rings - 2, si], ringVerts[rings - 2, sn]);
                        for (int ri = 0; ri < rings - 2; ri++)
                        {
                            AddTri(ringVerts[ri, si], ringVerts[ri, sn], ringVerts[ri + 1, sn]);
                            AddTri(ringVerts[ri, si], ringVerts[ri + 1, sn], ringVerts[ri + 1, si]);
                        }
                    }
                }
                else if (col is CapsuleCollider cap)
                {
                    Vector3 localAxis = cap.direction == 0 ? Vector3.right : (cap.direction == 2 ? Vector3.forward : Vector3.up);
                    Vector3 localU = cap.direction == 0 ? Vector3.up : Vector3.right;
                    Vector3 localV = Vector3.Cross(localAxis, localU).normalized;

                    float axisScale = cap.direction == 0 ? Mathf.Abs(ls.x) : (cap.direction == 2 ? Mathf.Abs(ls.z) : Mathf.Abs(ls.y));
                    float radScale = cap.direction == 0
                        ? Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z))
                        : (cap.direction == 2 ? Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y)) : Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z)));

                    float cr = cap.radius * radScale;
                    float halfCyl = Mathf.Max(0f, cap.height * axisScale * 0.5f - cr);
                    if (cr < 0.04f) continue;

                    Vector3 worldCenter = t.TransformPoint(cap.center);
                    Vector3 wAxis = (t.rotation * localAxis).normalized;
                    Vector3 wU = (t.rotation * localU).normalized;
                    Vector3 wV = (t.rotation * localV).normalized;
                    Vector3 topCap = worldCenter + wAxis * halfCyl;
                    Vector3 botCap = worldCenter - wAxis * halfCyl;

                    const int segs = 10;
                    var topRing = new Vector3[segs];
                    var botRing = new Vector3[segs];
                    for (int si = 0; si < segs; si++)
                    {
                        float theta = (Mathf.PI * 2f * si) / segs;
                        Vector3 off = (wU * Mathf.Cos(theta) + wV * Mathf.Sin(theta)) * cr;
                        topRing[si] = topCap + off;
                        botRing[si] = botCap + off;
                    }
                    Vector3 topTip = topCap + wAxis * cr;
                    Vector3 botTip = botCap - wAxis * cr;
                    for (int si = 0; si < segs; si++)
                    {
                        int sn = (si + 1) % segs;
                        if (halfCyl > 0.01f)
                        {
                            AddTri(topRing[si], topRing[sn], botRing[sn]);
                            AddTri(topRing[si], botRing[sn], botRing[si]);
                        }
                        AddTri(topTip, topRing[sn], topRing[si]);
                        AddTri(botTip, botRing[si], botRing[sn]);
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

                if (col == groundCol && tris.Count > trisBefore)
                    addedGroundCol = true;
            }

            // Reconstruct non-readable MeshColliders (common in standard & Workshop Ravenfield maps for stairs,
            // buildings, bridges, and rocks) using PhysX Collider.Raycast so ragdolls roll accurately down stairs
            // and drop off ledges instead of hitting a flat invisible safety floor!
            int sampledTrisBefore = tris.Count;
            for (int ui = 0; ui < unreadableMeshes.Count; ui++)
            {
                var mc = unreadableMeshes[ui];
                mat = ResolveSurfaceMaterial(mc);
                int before = tris.Count;
                SampleColliderViaRaycast(mc, center, groundY, scale, AddTri);
                if (mc == groundCol && tris.Count > before)
                    addedGroundCol = true;
            }

            // Fallback: if the specific ground collider under the bot wasn't captured by any primitive above,
            // sample it directly via Collider.Raycast (without spawning an infinite flat plane over drops/stairs).
            if (groundCol != null && !addedGroundCol)
            {
                mat = ResolveSurfaceMaterial(groundCol);
                int before = tris.Count;
                SampleColliderViaRaycast(groundCol, center, groundY, scale, AddTri);
                if (tris.Count > before)
                    addedGroundCol = true;
            }

            if (tris.Count > 0)
            {
                nm_world_add_mesh(world, pts.ToArray(), pts.Count / 3, tris.ToArray(), tris.Count / 3, mats.ToArray());
                entry.TriangleCount = tris.Count / 3;
                entry.UnreadableMeshCount = unreadableMeshes.Count;

                // Cache up to 3500 nearby triangles in Unity space for the live 3D BVH wireframe overlay
                int triCount = tris.Count / 3;
                int maxKeep = Mathf.Min(triCount, 3500);
                var dbg = new List<Vector3>(maxKeep * 3);
                float dbgRadiusSqr = (11f * scale) * (11f * scale);
                for (int ti = 0; ti < triCount && dbg.Count < maxKeep * 3; ti++)
                {
                    int i0 = (int)tris[ti * 3] * 3;
                    int i1 = (int)tris[ti * 3 + 1] * 3;
                    int i2 = (int)tris[ti * 3 + 2] * 3;
                    var a = new Vector3(pts[i0], pts[i0 + 2], pts[i0 + 1]);
                    var b = new Vector3(pts[i1], pts[i1 + 2], pts[i1 + 1]);
                    var c = new Vector3(pts[i2], pts[i2 + 2], pts[i2 + 1]);
                    Vector3 mid = (a + b + c) * (1f / 3f);
                    if ((mid - center).sqrMagnitude <= dbgRadiusSqr)
                    {
                        dbg.Add(a);
                        dbg.Add(b);
                        dbg.Add(c);
                    }
                }
                entry.DebugTriangles = dbg.ToArray();
            }
            else
            {
                // Only add a flat fallback floor when literally zero static world triangles exist nearby.
                // NEVER add an infinite horizontal floor at groundY when real geometry exists — doing so
                // puts an invisible glass floor across stairs, ledges, roofs, and balconies!
                byte floorMat = ResolveSurfaceMaterial(groundCol);
                float h = 24f * scale;
                if (floorMat == 0)
                {
                    nm_world_add_floor(world, center.x, center.z, groundY, h);
                }
                else
                {
                    var v = new[]
                    {
                        center.x - h, center.z - h, groundY,
                        center.x + h, center.z - h, groundY,
                        center.x + h, center.z + h, groundY,
                        center.x - h, center.z + h, groundY
                    };
                    nm_world_add_mesh(world, v, 4, new uint[] { 0, 1, 2, 0, 2, 3 }, 2, new[] { floorMat, floorMat });
                }
                entry.TriangleCount = 2;
                entry.UnreadableMeshCount = unreadableMeshes.Count;
                float dh = 6f * scale;
                Vector3 p0 = new Vector3(center.x - dh, groundY, center.z - dh);
                Vector3 p1 = new Vector3(center.x + dh, groundY, center.z - dh);
                Vector3 p2 = new Vector3(center.x + dh, groundY, center.z + dh);
                Vector3 p3 = new Vector3(center.x - dh, groundY, center.z + dh);
                entry.DebugTriangles = new[] { p0, p1, p2, p0, p2, p3 };
            }

            if (config.DebugLog.Value)
            {
                int totalTris = tris.Count / 3;
                int sampledTris = (tris.Count - sampledTrisBefore) / 3;
                Plugin.Log.LogInfo($"[DeliriumRuntime] Built world at {center:F1} (groundY={groundY:F2}): tris={totalTris} (unreadableMeshes={unreadableMeshes.Count}, sampledTris={sampledTris}, addedGround={addedGroundCol})");
            }
        }

        private static void SampleColliderViaRaycast(
            Collider col,
            Vector3 center,
            float groundY,
            float scale,
            Action<Vector3, Vector3, Vector3, bool> addTri)
        {
            if (col == null) return;
            Bounds b = col.bounds;
            float maxR = 13.0f * scale;
            float padX = Mathf.Min(0.01f, b.extents.x * 0.1f);
            float padZ = Mathf.Min(0.01f, b.extents.z * 0.1f);
            float minX = Mathf.Max(b.min.x + padX, center.x - maxR);
            float maxX = Mathf.Min(b.max.x - padX, center.x + maxR);
            float minZ = Mathf.Max(b.min.z + padZ, center.z - maxR);
            float maxZ = Mathf.Min(b.max.z - padZ, center.z + maxR);
            if (maxX <= minX + 0.02f || maxZ <= minZ + 0.02f) return;

            // Detect whether this collider has a ceiling/roof directly above the actor so indoor floors/stairs aren't occluded
            float rayStartY;
            bool hasCeiling = col.Raycast(new Ray(new Vector3(center.x, groundY + 0.45f, center.z), Vector3.up), out RaycastHit ceilHit, 8f * scale);
            if (hasCeiling)
                rayStartY = Mathf.Clamp(ceilHit.point.y - 0.12f, groundY + 1.45f * scale, groundY + 3.2f * scale);
            else
                rayStartY = Mathf.Min(b.max.y + 0.5f, groundY + 3.5f * scale);

            float rayMinY = Mathf.Max(b.min.y - 0.5f, groundY - 12.0f * scale);
            if (b.min.y > rayStartY || b.max.y < rayMinY) return;
            float rayLen = Mathf.Max(rayStartY - rayMinY, 1.0f);

            // Graded tensor-product grid: fine 0.25m step near actor (captures every stair tread crisply),
            // 0.50m mid-range (long stair flights & ramps), 1.25m outer ring — zero T-junctions!
            List<float> xs = BuildGradedAxis(center.x, minX, maxX, scale);
            List<float> zs = BuildGradedAxis(center.z, minZ, maxZ, scale);
            int nx = xs.Count;
            int nz = zs.Count;
            if (nx < 2 || nz < 2) return;

            var hits = new Vector3[nx, nz];
            var valid = new bool[nx, nz];
            float overhangThresholdY = groundY + 2.15f * scale;

            for (int ix = 0; ix < nx; ix++)
            {
                float x = xs[ix];
                for (int iz = 0; iz < nz; iz++)
                {
                    float z = zs[iz];
                    var ray = new Ray(new Vector3(x, rayStartY, z), Vector3.down);
                    if (!col.Raycast(ray, out RaycastHit h, rayLen)) continue;

                    // If we hit a roof/porch overhang well above head height, check for a walkable surface beneath it
                    if (h.point.y > overhangThresholdY)
                    {
                        float subStartY = h.point.y - 0.15f;
                        float subLen = subStartY - rayMinY;
                        if (subLen > 0.5f &&
                            col.Raycast(new Ray(new Vector3(x, subStartY, z), Vector3.down), out RaycastHit h2, subLen) &&
                            (h.point.y - h2.point.y) >= 1.55f * scale &&
                            Mathf.Abs(h2.point.y - groundY) <= 1.5f * scale)
                        {
                            h = h2;
                        }
                    }

                    hits[ix, iz] = h.point;
                    valid[ix, iz] = true;
                }
            }

            float maxJump = 4.0f * scale;
            for (int ix = 0; ix < nx - 1; ix++)
            {
                for (int iz = 0; iz < nz - 1; iz++)
                {
                    bool v00 = valid[ix, iz];
                    bool v01 = valid[ix, iz + 1];
                    bool v11 = valid[ix + 1, iz + 1];
                    bool v10 = valid[ix + 1, iz];

                    if (v00 && v01 && v11)
                    {
                        Vector3 p00 = hits[ix, iz];
                        Vector3 p01 = hits[ix, iz + 1];
                        Vector3 p11 = hits[ix + 1, iz + 1];
                        float yMin = Mathf.Min(p00.y, Mathf.Min(p01.y, p11.y));
                        float yMax = Mathf.Max(p00.y, Mathf.Max(p01.y, p11.y));
                        if (yMax - yMin <= maxJump)
                            addTri(p00, p01, p11, false);
                    }
                    if (v00 && v11 && v10)
                    {
                        Vector3 p00 = hits[ix, iz];
                        Vector3 p11 = hits[ix + 1, iz + 1];
                        Vector3 p10 = hits[ix + 1, iz];
                        float yMin = Mathf.Min(p00.y, Mathf.Min(p11.y, p10.y));
                        float yMax = Mathf.Max(p00.y, Mathf.Max(p11.y, p10.y));
                        if (yMax - yMin <= maxJump)
                            addTri(p00, p11, p10, false);
                    }
                }
            }

            // Also cast 16 lightweight horizontal rays at chest height so vertical walls inside non-readable
            // MeshColliders still trigger NM_XR_WALL bracing and stop ragdolls from clipping into walls.
            float wallProbeY = groundY + 1.0f * scale;
            if (b.min.y <= wallProbeY && b.max.y >= wallProbeY)
            {
                float wallProbeDist = 5.5f * scale;
                Vector3 probeOrigin = new Vector3(center.x, wallProbeY, center.z);
                const int rays = 16;
                var usedCells = new HashSet<long>();

                for (int ri = 0; ri < rays; ri++)
                {
                    float ang = (Mathf.PI * 2f * ri) / rays;
                    Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                    if (!col.Raycast(new Ray(probeOrigin, dir), out RaycastHit wh, wallProbeDist)) continue;
                    if (Mathf.Abs(wh.normal.y) > 0.45f) continue;

                    Vector3 n = new Vector3(wh.normal.x, 0f, wh.normal.z);
                    if (n.sqrMagnitude < 1e-4f) continue;
                    n.Normalize();

                    // Deduplicate nearby wall hits on the same 0.6m cell
                    int cx = Mathf.RoundToInt(wh.point.x / 0.6f);
                    int cz = Mathf.RoundToInt(wh.point.z / 0.6f);
                    long key = ((long)cx << 32) ^ (uint)cz;
                    if (!usedCells.Add(key)) continue;

                    Vector3 right = Vector3.Cross(Vector3.up, n).normalized;
                    float hw = 0.65f * scale;
                    float hBot = 1.05f * scale;
                    float hTop = 1.35f * scale;
                    Vector3 bl = wh.point - right * hw - Vector3.up * hBot;
                    Vector3 tl = wh.point - right * hw + Vector3.up * hTop;
                    Vector3 tr = wh.point + right * hw + Vector3.up * hTop;
                    Vector3 br = wh.point + right * hw - Vector3.up * hBot;

                    // Orient outward normal along `n` (AddTri(a, b, d) produces Unity normal Cross(b - a, d - a))
                    bool flip = Vector3.Dot(Vector3.Cross(tl - bl, tr - bl), n) < 0f;
                    addTri(bl, tl, tr, flip);
                    addTri(bl, tr, br, flip);
                }
            }
        }

        private static List<float> BuildGradedAxis(float centerCoord, float minCoord, float maxCoord, float scale)
        {
            var coords = new List<float>(60);
            float span = maxCoord - minCoord;
            float nearHalf = 3.5f * scale;
            float midHalf = 8.0f * scale;
            float stepNear = Mathf.Max(0.05f, Mathf.Min(0.25f * scale, span * 0.25f));
            float stepMid = 0.50f * scale;
            float stepFar = 1.25f * scale;

            float x = minCoord;
            coords.Add(x);
            while (x < maxCoord - 1e-3f)
            {
                float d = Mathf.Abs(x - centerCoord);
                float step = d < nearHalf ? stepNear : (d < midHalf ? stepMid : stepFar);
                x = Mathf.Min(x + step, maxCoord);
                coords.Add(x);
            }
            return coords;
        }

        public float SampleGroundHeight(Vector3 fromPos, Transform ignoreOwner, float fallbackY)
        {
            int mask = ~ignoredStaticLayersMask;
            Vector3 origin = fromPos + Vector3.up * 0.6f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 10f, mask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            float bestY = fallbackY;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignoreOwner != null && h.transform.IsChildOf(ignoreOwner)) continue;
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                if (h.collider.GetComponentInParent<Actor>() != null || h.collider.GetComponentInParent<Vehicle>() != null) continue;
                if (h.point.y > fromPos.y + 0.35f) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    bestY = h.point.y;
                }
            }
            return bestY;
        }

        /// <summary>
        /// Strict floor sampler for corpse-settling checks: casts straight down from just above the pelvis
        /// and ignores steep/vertical wall surfaces (normal.y &lt; 0.55) so a ragdoll pressed against a wall
        /// or ledge is never misclassified as lying on the floor and frozen mid-air.
        /// </summary>
        public float SampleFloorHeightBelow(Vector3 fromPos, Transform ignoreOwner, float fallbackY)
        {
            int mask = ~ignoredStaticLayersMask;
            Vector3 origin = fromPos + Vector3.up * 0.18f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 12f, mask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            float bestY = fallbackY;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignoreOwner != null && h.transform.IsChildOf(ignoreOwner)) continue;
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                if (h.collider.GetComponentInParent<Actor>() != null || h.collider.GetComponentInParent<Vehicle>() != null) continue;
                if (h.normal.y < 0.55f) continue;
                if (h.point.y > fromPos.y + 0.15f) continue;
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
            Vector3 origin = fromPos + Vector3.up * 0.6f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 10f, mask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            Collider best = null;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignoreOwner != null && h.transform.IsChildOf(ignoreOwner)) continue;
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                if (h.collider.GetComponentInParent<Actor>() != null || h.collider.GetComponentInParent<Vehicle>() != null) continue;
                if (h.point.y > fromPos.y + 0.35f) continue;
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
