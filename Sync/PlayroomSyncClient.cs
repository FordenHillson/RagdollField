using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DeliriumRavenfield.Config;
using DeliriumRavenfield.Core;
using DeliriumRavenfield.TestArena;
using UnityEngine;
using static Delirium.Native;

namespace DeliriumRavenfield.Sync
{
    public enum SyncParityMode
    {
        ExactHomer = 0,
        RavenfieldSoldier = 1
    }

    public struct DynamicSyncBox
    {
        public Vector3 CenterUnity;
        public Quaternion RotUnity;
        public Vector3 HalfUnity;
    }

    /// <summary>
    /// Live Lockstep Sync Client (Playroom Master Clock -> Ravenfield Slave) + Dynamic Mirror Stage Builder
    /// + Real-Time Diff Evaluator + Dual JSONL Logger.
    /// </summary>
    public sealed class PlayroomSyncClient : IDisposable
    {
        public const string DefaultHost = "127.0.0.1";
        public const int DefaultPort = 47291;

        private readonly DeliriumRuntime runtime;
        private readonly LodManager lod;
        private readonly ModConfig config;

        private TcpClient tcpClient;
        private NetworkStream netStream;
        private Thread readerThread;
        private readonly object writeLock = new object();
        private volatile bool running;
        private volatile bool connected;
        private readonly ConcurrentQueue<string> incomingLines = new ConcurrentQueue<string>();

        // Mirror map & stage state
        private GameObject dynamicStageRoot;
        private SharedWorldEntry mirrorWorldEntry;
        private int mirrorSceneHandle;
        private bool initialBotSetupDone;
        private bool autoConnectTried;
        private bool autoRecordRequested;
        private bool hideSoldierMesh;
        private bool syncShotActive;
        private int syncShotHitCount;
        private float botSetupRetryTimer;
        private bool lastWaterSet;
        private float lastWaterLevel = float.NaN;
        private bool hasPendingReset;
        private Dictionary<string, object> pendingResetPacket;

        // Reusable FFI buffers (zero GC at 60 Hz!)
        private readonly float[] comBuf = new float[3];
        private readonly float[] velBuf = new float[3];
        private readonly float[] effActual = new float[20 * 3];
        private readonly float[] effDesired = new float[20 * 3];
        private readonly float[] effStats = new float[20 * 4];
        private readonly int[] effDofs = new int[20];
        private readonly string[] jointNames = new string[20];
        private readonly byte[] nameBuf = new byte[64];
        private readonly float[] perJointDeltaDeg = new float[20];
        private readonly List<DynamicSyncBox> dynamicBoxes = new List<DynamicSyncBox>(16);

        // Dual JSONL logger (Ravenfield side)
        private StreamWriter logWriter;
        private string logDir;
        private string sessionId = "";
        private readonly StringBuilder jsonLineBuf = new StringBuilder(2048);

        // Public status & live diff telemetry
        public bool IsConnected => connected;
        public bool IsInMirrorMap { get; private set; }
        public int MirrorSceneHandle => mirrorSceneHandle;
        public bool HideSoldierMesh
        {
            get => hideSoldierMesh;
            set
            {
                if (hideSoldierMesh == value) return;
                hideSoldierMesh = value;
                ApplySoldierMeshVisibility();
            }
        }
        public bool IsRecording => logWriter != null;
        public bool AutoRecordOnSync { get; set; } = true;
        public string SessionId => sessionId;
        public string StatusText { get; private set; } = "Disconnected";
        public string CurrentStage { get; private set; } = "(none)";
        public SyncParityMode ParityMode { get; set; } = SyncParityMode.ExactHomer;
        public ActorRagdollController SyncedController { get; private set; }
        public SharedWorldEntry MirrorWorldEntry => mirrorWorldEntry;
        public IReadOnlyList<DynamicSyncBox> LastDynamicBoxes => dynamicBoxes;
        public float[] PerJointDeltaDeg => perJointDeltaDeg;

        public long LastStep { get; private set; }
        public float DeltaComMm { get; private set; }
        public float DeltaVelMs { get; private set; }
        public float DeltaKe { get; private set; }
        public float MaxJointErrDeg { get; private set; }
        public int WorstJointIndex { get; private set; } = -1;
        public string WorstJointName { get; private set; } = "";
        public bool FlagsMatch { get; private set; } = true;
        public bool ContactsMatch { get; private set; } = true;
        public long FirstDivergenceStep { get; private set; } = -1;
        public bool HasPlayroomRef { get; private set; }
        public Vector3 PlayroomGhostComUnity { get; private set; }

        public PlayroomSyncClient(DeliriumRuntime runtime, LodManager lod, ModConfig config)
        {
            this.runtime = runtime;
            this.lod = lod;
            this.config = config;
            logDir = Path.Combine(BepInEx.Paths.ConfigPath, "delirium-sync-logs");
        }

        public void LaunchPlayroomMirrorMap()
        {
            try
            {
                var entry = PlayroomMirrorDefinitions.CreateMapEntry();
                var parameters = GameModeParameters.GetDefault();
                parameters.playerHasAllWeapons = true;
                parameters.playerTeam = 0;
                parameters.nightMode = false;
                parameters.configFlags = false;
                if (parameters.configuration != null)
                {
                    parameters.configuration.SetInt("countTeam0", 1);
                    parameters.configuration.SetInt("countTeam1", 1);
                }
                Plugin.Log?.LogInfo($"[PlayroomSync] Launching '{entry.metaData.displayName}' (1v1 clean mirror map).");
                GameManager.StartLevel(entry, parameters);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[PlayroomSync] LaunchPlayroomMirrorMap failed: {ex}");
            }
        }

        public void OnMirrorSceneLoaded()
        {
            IsInMirrorMap = true;
            mirrorSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            initialBotSetupDone = false;
            autoConnectTried = false;
            botSetupRetryTimer = 0.25f;
            lod.BotMode = TestBotMode.PureDummy;
            Plugin.Log?.LogInfo("[PlayroomSync] Entered Delirium Playroom Mirror — BotMode set to PureDummy.");

            if (connected)
            {
                SendControlJson("{\"type\":\"hello\"}");
                StartAutoRecordingIfEnabled();
            }
        }

        public void OnSceneUnloaded()
        {
            bool wasInMirror = IsInMirrorMap;
            IsInMirrorMap = false;
            mirrorSceneHandle = 0;
            initialBotSetupDone = false;
            hasPendingReset = false;
            pendingResetPacket = null;
            if (SyncedController != null)
            {
                hideSoldierMesh = false;
                ApplySoldierMeshVisibility();
            }
            SyncedController = null;
            dynamicBoxes.Clear();
            HasPlayroomRef = false;
            DestroyDynamicStage();
            DestroyMirrorWorld();
            if (wasInMirror)
            {
                lod.BotMode = TestBotMode.Normal;
            }
        }

        public void Connect(string host = DefaultHost, int port = DefaultPort)
        {
            Disconnect();
            try
            {
                var client = new TcpClient();
                client.NoDelay = true;
                client.Connect(host, port);
                tcpClient = client;
                netStream = client.GetStream();
                running = true;
                connected = true;
                StatusText = $"Connected ({host}:{port})";
                Plugin.Log?.LogInfo($"[PlayroomSync] Connected to Playroom at {host}:{port}");

                readerThread = new Thread(ReaderLoop)
                {
                    Name = "DeliriumPlayroomSyncReader",
                    IsBackground = true
                };
                readerThread.Start();

                SendControlJson("{\"type\":\"hello\"}");
                StartAutoRecordingIfEnabled();
            }
            catch (Exception ex)
            {
                StatusText = $"Connect failed: {ex.Message}";
                Plugin.Log?.LogWarning($"[PlayroomSync] Connect failed: {ex.Message}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            running = false;
            connected = false;
            autoRecordRequested = false;
            StopRecordingLocal();
            try { netStream?.Close(); } catch { }
            try { tcpClient?.Close(); } catch { }
            netStream = null;
            tcpClient = null;
            while (incomingLines.TryDequeue(out _)) { }
            if (SyncedController != null)
            {
                hideSoldierMesh = false;
                ApplySoldierMeshVisibility();
                SyncedController.IsSyncLockstepControlled = false;
                SyncedController = null;
            }
            HasPlayroomRef = false;
            dynamicBoxes.Clear();
            if (!StatusText.StartsWith("Connect failed", StringComparison.Ordinal))
                StatusText = "Disconnected";
        }

        public void RequestResyncAndReset(bool stand = true)
        {
            if (!connected) return;
            if (SyncedController != null && SyncedController.Actor != null)
            {
                lod.ResetHealthState(SyncedController.Actor);
            }
            SendControlJson(stand ? "{\"type\":\"req_reset\",\"stand\":true}" : "{\"type\":\"req_reset\",\"stand\":false}");
        }

        public void ResetPlayerView()
        {
            var player = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (player == null || player.dead) return;
            try
            {
                player.SetPositionAndRotation(PlayroomMirrorDefinitions.PlayerSpawnPos,
                    Quaternion.LookRotation(PlayroomMirrorDefinitions.PlayerSpawnDir, Vector3.up));
            }
            catch { }
        }

        private void StartAutoRecordingIfEnabled()
        {
            if (!AutoRecordOnSync || !connected || IsRecording || autoRecordRequested) return;
            autoRecordRequested = true;
            string sid = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            SendControlJson($"{{\"type\":\"req_record\",\"active\":true,\"session\":\"{sid}\",\"dir\":\"{MiniJson.Escape(logDir)}\"}}");
        }

        private void ApplySoldierMeshVisibility()
        {
            var actor = SyncedController?.Actor;
            if (actor == null) return;
            bool show = !hideSoldierMesh;
            try
            {
                if (actor.skinnedRendererRagdoll != null)
                    actor.skinnedRendererRagdoll.enabled = show;
                if (actor.ragdoll != null && actor.ragdoll.ragdollObject != null)
                {
                    foreach (var smr in actor.ragdoll.ragdollObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr != null) smr.enabled = show;
                    }
                }
            }
            catch { }
        }

        public void RequestStage(string stage)
        {
            if (!connected || string.IsNullOrEmpty(stage)) return;
            SendControlJson($"{{\"type\":\"req_stage\",\"stage\":\"{MiniJson.Escape(stage)}\"}}");
        }

        public void ToggleRecording()
        {
            if (!connected)
            {
                if (IsRecording) StopRecordingLocal();
                return;
            }
            if (IsRecording)
            {
                SendControlJson("{\"type\":\"req_record\",\"active\":false}");
                StopRecordingLocal();
            }
            else
            {
                string sid = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                SendControlJson($"{{\"type\":\"req_record\",\"active\":true,\"session\":\"{sid}\",\"dir\":\"{MiniJson.Escape(logDir)}\"}}");
            }
        }

        public void SetParityMode(SyncParityMode mode)
        {
            if (ParityMode == mode) return;
            ParityMode = mode;
            if (connected)
            {
                RequestResyncAndReset();
            }
        }

        public string GetRavenfieldLogPath()
        {
            if (string.IsNullOrEmpty(sessionId)) return logDir;
            return Path.Combine(logDir, $"ravenfield_sync_{sessionId}.jsonl");
        }

        private void ReaderLoop()
        {
            try
            {
                using (var reader = new StreamReader(netStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true))
                {
                    while (running)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.Length > 0)
                            incomingLines.Enqueue(line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (running)
                    Plugin.Log?.LogWarning($"[PlayroomSync] Connection closed: {ex.Message}");
            }
            finally
            {
                connected = false;
                running = false;
                StatusText = "Disconnected";
            }
        }

        public void SendControlJson(string jsonLine)
        {
            if (!connected || netStream == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
                lock (writeLock)
                {
                    netStream?.Write(bytes, 0, bytes.Length);
                    netStream?.Flush();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PlayroomSync] Send failed: {ex.Message}");
                Disconnect();
            }
        }

        public void TickUpdate(float dt)
        {
            // Keep mirror map 1v1 cleanly snapped once spawned
            if (IsInMirrorMap && !initialBotSetupDone)
            {
                botSetupRetryTimer -= dt;
                if (botSetupRetryTimer <= 0f)
                {
                    if (TrySetupMirrorActors())
                    {
                        initialBotSetupDone = true;
                        if (hasPendingReset && pendingResetPacket != null)
                        {
                            var pkt = pendingResetPacket;
                            hasPendingReset = false;
                            pendingResetPacket = null;
                            HandleResetPacket(pkt);
                        }
                    }
                    else
                    {
                        botSetupRetryTimer = 0.35f;
                    }
                }
            }

            // Drain incoming Playroom packets on main thread (capped to keep Unity responsive on burst catch-up)
            int processed = 0;
            while (processed < 12 && incomingLines.TryDequeue(out string line))
            {
                processed++;
                ProcessPacket(line);
            }
        }

        private bool TrySetupMirrorActors()
        {
            var am = ActorManager.instance;
            if (am == null || am.actors == null || am.actors.Count == 0) return false;

            // Wait until player has deployed into the scene so our snap + bot spawn isn't overwritten by spawn wave
            var player = am.player;
            if (player == null || player.dead) return false;

            Actor redBot = FindMirrorTargetActor();
            if (redBot == null) return false;

            // Force-spawn the Red subject bot immediately if it hasn't spawned yet in wave 1!
            if (redBot.dead)
            {
                try
                {
                    redBot.SpawnAt(PlayroomMirrorDefinitions.BotSpawnPos,
                        Quaternion.LookRotation(PlayroomMirrorDefinitions.BotSpawnDir, Vector3.up));
                }
                catch { }
            }

            // Position the player nicely facing the stage origin (matching Playroom's default camera view)
            try
            {
                player.SetPositionAndRotation(PlayroomMirrorDefinitions.PlayerSpawnPos,
                    Quaternion.LookRotation(PlayroomMirrorDefinitions.PlayerSpawnDir, Vector3.up));
            }
            catch { }

            // Snap Red bot upright at MirrorOrigin facing back toward the player (NM -Y, matching Homer's eyes!)
            SnapAndFreezeMirrorBot(redBot, PlayroomMirrorDefinitions.BotSpawnPos, PlayroomMirrorDefinitions.BotSpawnDir);

            // Auto-connect to Playroom Sync server if already listening (zero-click setup!)
            if (!connected && !autoConnectTried)
            {
                autoConnectTried = true;
                Connect();
            }

            Plugin.Log?.LogInfo($"[PlayroomSync] Mirror actors ready (Target='{redBot.name}', connected={connected}).");
            return true;
        }

        private static void SnapAndFreezeMirrorBot(Actor bot, Vector3 pos, Vector3 dir)
        {
            if (bot == null) return;
            try
            {
                Vector3 flatDir = Vector3.ProjectOnPlane(dir, Vector3.up);
                if (flatDir.sqrMagnitude < 1e-4f) flatDir = Vector3.back;
                bot.SetPositionAndRotation(pos, Quaternion.LookRotation(flatDir.normalized, Vector3.up));
                bot.health = bot.maxHealth;
                bot.balance = bot.maxBalance;
                bot.ForceStance(Actor.Stance.Stand);
                bot.ForceStopAnimatorMovement();
                bot.ResetWeaponInputs();
                var ai = bot.controller as AiActorController;
                if (ai != null)
                {
                    ai.CancelPath(true);
                    ai.LeaveCover();
                    ai.LookAt(pos + flatDir.normalized * 10f);
                    ai.FreezeFacingDirection();
                }
            }
            catch { }
        }

        private Actor FindMirrorTargetActor()
        {
            var am = ActorManager.instance;
            if (am == null || am.actors == null) return null;

            // Keep existing valid synced actor if alive/present
            if (SyncedController != null && SyncedController.Actor != null)
                return SyncedController.Actor;

            Actor fallback = null;
            for (int i = 0; i < am.actors.Count; i++)
            {
                var a = am.actors[i];
                if (a == null || a == am.player) continue;
                if (a.team == 1 && !a.dead) return a;
                if (fallback == null) fallback = a;
            }
            return fallback;
        }

        private void ProcessPacket(string line)
        {
            try
            {
                var d = MiniJson.ParseObject(line);
                if (d == null) return;
                string type = MiniJson.GetString(d, "type");
                switch (type)
                {
                    case "scene":
                        HandleScenePacket(d);
                        break;
                    case "reset":
                        HandleResetPacket(d);
                        break;
                    case "step":
                        HandleStepPacket(d);
                        break;
                    case "record":
                        HandleRecordPacket(d);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PlayroomSync] Packet error: {ex.Message}");
            }
        }

        // =========================================================================
        // Scene Packet: Build 1:1 Unity Visual + BoxCollider Stage & Mirror NmWorld
        // =========================================================================
        private void HandleScenePacket(Dictionary<string, object> d)
        {
            CurrentStage = MiniJson.GetString(d, "stage", "room");
            bool pool = MiniJson.GetBool(d, "pool", false);
            float floorHalf = MiniJson.GetFloat(d, "floor_half", 50f);
            byte concreteRow = (byte)Mathf.Clamp(MiniJson.GetInt(d, "concrete_row", 0), 0, 255);

            // 1. Register exact Playroom surface materials in native SDK
            var surfaceNames = new Dictionary<int, string>();
            var surfaces = MiniJson.GetList(d, "surfaces");
            if (surfaces != null)
            {
                for (int i = 0; i < surfaces.Count; i++)
                {
                    if (!(surfaces[i] is Dictionary<string, object> s)) continue;
                    int row = MiniJson.GetInt(s, "row", i);
                    string name = MiniJson.GetString(s, "name", "CONCRETE");
                    float fric = MiniJson.GetFloat(s, "f", 1.0f);
                    float elas = MiniJson.GetFloat(s, "e", 0.1f);
                    nm_set_surface(row, fric, elas);
                    surfaceNames[row] = name;
                }
            }

            // 2. Rebuild Unity visual + collider stage shifted by +MirrorOrigin
            DestroyDynamicStage();
            Vector3 origin = PlayroomMirrorDefinitions.MirrorOrigin;
            dynamicStageRoot = new GameObject("PlayroomMirror_DynamicStage");

            var baseFloor = GameObject.Find("PlayroomMirror_BaseFloor");
            if (baseFloor != null)
            {
                // Hide solid base floor when Playroom stage has a pool cutout so the pool pit is open
                baseFloor.SetActive(!pool);
            }

            var floorRects = MiniJson.GetList(d, "floor_rects");
            if (pool && floorRects != null)
            {
                for (int i = 0; i < floorRects.Count; i++)
                {
                    if (!(floorRects[i] is List<object> r) || r.Count < 4) continue;
                    float ax = MiniJson.AsFloat(r[0]);
                    float ay = MiniJson.AsFloat(r[1]);
                    float bx = MiniJson.AsFloat(r[2]);
                    float by = MiniJson.AsFloat(r[3]);
                    float cx = 0.5f * (ax + bx);
                    float cy = 0.5f * (ay + by);
                    float sx = Mathf.Max(0.1f, Mathf.Abs(bx - ax));
                    float sy = Mathf.Max(0.1f, Mathf.Abs(by - ay));
                    SpawnMirrorBox(dynamicStageRoot.transform, $"FloorRect_{i}",
                        origin + new Vector3(cx, -0.25f, cy), Quaternion.identity,
                        new Vector3(sx, 0.5f, sy), new Color(0.24f, 0.42f, 0.68f, 1f));
                }
            }

            // 3. Build dedicated Mirror NmWorld at exact Playroom NM origin (z = 0)
            DestroyMirrorWorld();
            IntPtr worldPtr = nm_world_create();
            if (worldPtr == IntPtr.Zero)
                throw new Exception($"nm_world_create failed: {LastError()}");
            nm_world_set_scale(worldPtr, 1f);

            var verts = new List<float>(4096 * 3);
            var tris = new List<uint>(4096 * 3);
            var mats = new List<byte>(4096);
            var debugTris = new List<Vector3>(4096 * 3);

            // Add Playroom's EXACT 1m x 1m integer floor grid (matching world::floor_tris triangle-for-triangle!)
            var poolBounds = MiniJson.GetList(d, "pool_bounds");
            float poolAx = (poolBounds != null && poolBounds.Count >= 4) ? MiniJson.AsFloat(poolBounds[0]) : 0f;
            float poolAy = (poolBounds != null && poolBounds.Count >= 4) ? MiniJson.AsFloat(poolBounds[1]) : 0f;
            float poolBx = (poolBounds != null && poolBounds.Count >= 4) ? MiniJson.AsFloat(poolBounds[2]) : 0f;
            float poolBy = (poolBounds != null && poolBounds.Count >= 4) ? MiniJson.AsFloat(poolBounds[3]) : 0f;
            AddExactPlayroomFloorGrid(verts, tris, mats, debugTris, origin,
                pool && poolBounds != null && poolBounds.Count >= 4,
                poolAx, poolAy, poolBx, poolBy,
                Mathf.Clamp(Mathf.RoundToInt(floorHalf), 10, 50),
                concreteRow);

            // Axis-aligned blocks
            var blocks = MiniJson.GetList(d, "blocks");
            if (blocks != null)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (!(blocks[i] is Dictionary<string, object> b)) continue;
                    Vector3 cNm = MiniJson.GetNmVec3Raw(b, "c");
                    Vector3 hNm = MiniJson.GetNmVec3Raw(b, "h");
                    byte mat = (byte)Mathf.Clamp(MiniJson.GetInt(b, "m", concreteRow), 0, 255);
                    string sName = surfaceNames.TryGetValue(mat, out var sn) ? sn : "CONCRETE";

                    // Unity visual cube at MirrorOrigin + ToUnityVec(cNm)
                    Vector3 posU = origin + NmToUnity(cNm);
                    Vector3 sizeU = new Vector3(hNm.x * 2f, hNm.z * 2f, hNm.y * 2f);
                    SpawnMirrorBox(dynamicStageRoot.transform, $"Block_{i}_{sName}", posU, Quaternion.identity, sizeU, SurfaceColor(sName));

                    // Exact NM box triangles (12 tris)
                    AddBoxTrianglesNm(verts, tris, mats, debugTris, origin, cNm, Quaternion.identity, hNm, mat);
                }
            }

            // Oriented tilted blocks
            var tilted = MiniJson.GetList(d, "tilted");
            if (tilted != null)
            {
                for (int i = 0; i < tilted.Count; i++)
                {
                    if (!(tilted[i] is Dictionary<string, object> b)) continue;
                    Vector3 pNm = MiniJson.GetNmVec3Raw(b, "p");
                    var ql = MiniJson.GetList(b, "q");
                    Quaternion qNm = (ql != null && ql.Count >= 4)
                        ? new Quaternion(MiniJson.AsFloat(ql[0]), MiniJson.AsFloat(ql[1]), MiniJson.AsFloat(ql[2]), MiniJson.AsFloat(ql[3]))
                        : Quaternion.identity;
                    Vector3 hNm = MiniJson.GetNmVec3Raw(b, "h");
                    byte mat = (byte)Mathf.Clamp(MiniJson.GetInt(b, "m", concreteRow), 0, 255);
                    string sName = surfaceNames.TryGetValue(mat, out var sn) ? sn : "CONCRETE";

                    Vector3 posU = origin + NmToUnity(pNm);
                    Quaternion rotU = new Quaternion(-qNm.x, -qNm.z, -qNm.y, qNm.w);
                    Vector3 sizeU = new Vector3(hNm.x * 2f, hNm.z * 2f, hNm.y * 2f);
                    SpawnMirrorBox(dynamicStageRoot.transform, $"Tilted_{i}_{sName}", posU, rotU, sizeU, SurfaceColor(sName));

                    AddBoxTrianglesNm(verts, tris, mats, debugTris, origin, pNm, qNm, hNm, mat);
                }
            }

            int vertCount = verts.Count / 3;
            int triCount = tris.Count / 3;
            if (vertCount > 0 && triCount > 0)
            {
                Check(nm_world_add_mesh(worldPtr, verts.ToArray(), vertCount, tris.ToArray(), triCount, mats.ToArray()), "mirror nm_world_add_mesh");
            }

            mirrorWorldEntry = new SharedWorldEntry
            {
                World = worldPtr,
                Scale = 1f,
                Anchor = origin,
                SceneId = 0,
                RefCount = 1,
                TriangleCount = nm_world_triangle_count(worldPtr),
                UnreadableMeshCount = 0,
                DebugTriangles = debugTris.ToArray()
            };

            Physics.SyncTransforms();
            Plugin.Log?.LogInfo($"[PlayroomSync] Mirrored stage '{CurrentStage}' (pool={pool}, blocks={blocks?.Count ?? 0}, tilted={tilted?.Count ?? 0}, BVH tris={mirrorWorldEntry.TriangleCount}).");
        }

        // =========================================================================
        // Reset Packet: Initialize Lockstep Synced Agent (Exact Homer or Soldier)
        // =========================================================================
        private void HandleResetPacket(Dictionary<string, object> d)
        {
            CurrentStage = MiniJson.GetString(d, "stage", CurrentStage);
            if (mirrorWorldEntry == null || mirrorWorldEntry.World == IntPtr.Zero)
            {
                // Fallback flat world if reset arrived before scene
                IntPtr w = nm_world_create();
                nm_world_add_floor(w, 0f, 0f, 0f, 50f);
                mirrorWorldEntry = new SharedWorldEntry { World = w, Scale = 1f, Anchor = PlayroomMirrorDefinitions.MirrorOrigin, RefCount = 1, TriangleCount = nm_world_triangle_count(w) };
            }

            Actor target = FindMirrorTargetActor();
            if (target == null)
            {
                hasPendingReset = true;
                pendingResetPacket = d;
                return;
            }

            // Ensure target actor is alive, healthy, and cleanly reset before lockstep takeover
            lod.ResetHealthState(target);
            if (target.dead)
            {
                try
                {
                    target.SpawnAt(PlayroomMirrorDefinitions.BotSpawnPos,
                        Quaternion.LookRotation(PlayroomMirrorDefinitions.BotSpawnDir, Vector3.up));
                }
                catch { }
            }
            SnapAndFreezeMirrorBot(target, PlayroomMirrorDefinitions.BotSpawnPos, PlayroomMirrorDefinitions.BotSpawnDir);

            var ctrl = lod.GetOrCreateControllerForSync(target);
            if (ctrl == null) return;

            Vector3 startNm = MiniJson.GetNmVec3Raw(d, "start");
            float yaw = MiniJson.GetFloat(d, "yaw", 0f);
            float seed = MiniJson.GetFloat(d, "seed", 0.3f);
            float strength = MiniJson.GetFloat(d, "strength", 1f);
            bool stepLimit = MiniJson.GetBool(d, "step_limit", false);
            int reactions = MiniJson.GetInt(d, "reactions", 0);
            int parts = MiniJson.GetInt(d, "parts", 21);
            float[] frames = MiniJson.GetFloatArray(d, "frames");

            // If Playroom moved the character via spawn_at / reset_here, self.frames has the real XY while room.start stays [0,0,0].
            // Derive effectiveStartNm from frames[0] (pelvis XY) and min foot Z so RavenfieldSoldier spawns at the exact same spot!
            if (frames != null && parts >= 7 && frames.Length >= parts * 7)
            {
                float pelX = frames[0];
                float pelY = frames[1];
                float footZ = Mathf.Min(frames[3 * 7 + 2], frames[6 * 7 + 2]);
                float estFloorZ = Mathf.Max(startNm.z, footZ - 0.06f);
                // If pelvis XY shifted significantly from room.start, follow Playroom's actual placement
                if ((pelX - startNm.x) * (pelX - startNm.x) + (pelY - startNm.y) * (pelY - startNm.y) > 0.02f * 0.02f)
                {
                    startNm = new Vector3(pelX, pelY, estFloorZ);
                }
            }

            nm_set_step_limit(stepLimit ? 1 : 0);
            lastWaterSet = false;
            lastWaterLevel = float.NaN;
            dynamicBoxes.Clear();

            bool ok = ctrl.EnsureTakenOverForPlayroomSync(
                mirrorWorldEntry,
                PlayroomMirrorDefinitions.MirrorOrigin,
                ParityMode,
                startNm,
                yaw,
                seed,
                strength,
                reactions,
                frames,
                parts);

            if (!ok)
            {
                Plugin.Log?.LogWarning("[PlayroomSync] Failed lockstep takeover on target actor.");
                return;
            }

            SyncedController = ctrl;
            syncShotActive = false;
            syncShotHitCount = 0;
            ApplySoldierMeshVisibility();
            CacheJointNames(ctrl.Agent);
            ResetDiffMetrics();

            if (IsRecording)
            {
                WriteResetLogLine(d, ctrl.Agent);
            }
            else
            {
                StartAutoRecordingIfEnabled();
            }

            Plugin.Log?.LogInfo($"[PlayroomSync] Reset OK (stage={CurrentStage}, mode={ParityMode}, mass={nm_agent_get_mass(ctrl.Agent):F2}kg).");
        }

        // =========================================================================
        // 2-Way Ravenfield -> Playroom Command Forwarding (Shoot / Wound / Push / Blast)
        // =========================================================================
        private void SendRfCmds(List<string> cmdJsonItems)
        {
            if (!connected || cmdJsonItems == null || cmdJsonItems.Count == 0) return;
            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"rf_cmd\",\"cmds\":[");
            for (int i = 0; i < cmdJsonItems.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(cmdJsonItems[i]);
            }
            sb.Append("]}");
            SendControlJson(sb.ToString());
        }

        private static string CmdMsg(string line) =>
            "{\"k\":\"msg\",\"l\":\"" + MiniJson.Escape(line) + "\"}";

        private static string CmdImp(int part, float[] atNm, float[] impNm)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci,
                "{{\"k\":\"imp\",\"p\":{0},\"at\":[{1:F4},{2:F4},{3:F4}],\"v\":[{4:F3},{5:F3},{6:F3}]}}",
                part, atNm[0], atNm[1], atNm[2], impNm[0], impNm[1], impNm[2]);
        }

        private static string CmdKick(float[] dvNm)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci,
                "{{\"k\":\"kick\",\"dv\":[{0:F3},{1:F3},{2:F3}]}}",
                dvNm[0], dvNm[1], dvNm[2]);
        }

        private static string CmdDead(bool dead) =>
            dead ? "{\"k\":\"dead\",\"v\":true}" : "{\"k\":\"dead\",\"v\":false}";

        private static string NmVecStr(Vector3 nm)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci, "{0:F3},{1:F3},{2:F3}", nm.x, nm.y, nm.z);
        }

        private static Vector3 UnityToNm(Vector3 u) => new Vector3(u.x, u.z, u.y);

        public void ForwardShot(ActorRagdollController ctrl, DamageInfo info, bool fatal, HitOutcome? outcome, BodyHealthState health)
        {
            if (!connected || ctrl == null || ctrl.Agent == IntPtr.Zero) return;
            var ci = CultureInfo.InvariantCulture;
            var cmds = new List<string>(8);

            Vector3 visOff = ctrl.SyncVisualOffset;
            Vector3 fallbackDir = ctrl.Actor != null ? -ctrl.Actor.transform.forward : Vector3.forward;
            var shot = WeaponForceNormalizer.NormalizeShot(info, config, fallbackDir);

            Vector3 ptUnity = info.point != Vector3.zero ? info.point : (ctrl.Actor != null ? ctrl.Actor.CenterPosition() : visOff + Vector3.up);
            Vector3 ptLocalUnity = ptUnity - visOff;
            float[] ptNmArr = ToNmVec(ptLocalUnity);

            int part = outcome.HasValue
                ? Mathf.Clamp(outcome.Value.Part, 0, 20)
                : Mathf.Clamp(nm_agent_nearest_part(ctrl.Agent, ptNmArr), 0, 20);
            if (info.isCriticalHit || (outcome.HasValue && outcome.Value.InstantHeadshot))
                part = 12;

            // Playroom-style localized hit impulse: deliver 80-92% directly onto the struck part at hitPoint so every
            // rapid bullet whips/twists the struck shoulder, arm, leg, head, or chest just like Playroom!
            float totalImp = Mathf.Clamp(shot.TotalImpulse, 14f, 140f);
            bool isCore = part == 0 || (part >= 7 && part <= 10);
            float localFrac = part == 12 ? 0.78f : (isCore ? 0.92f : 0.84f);
            float localImp = totalImp * localFrac;
            float torsoImp = Mathf.Max(0f, totalImp - localImp);

            cmds.Add(CmdImp(part, ptNmArr, ToNmVec(shot.Direction * localImp)));
            if (torsoImp > 0.5f)
            {
                int corePart = part >= 9 ? 10 : 0;
                var pCore = new float[3];
                if (nm_agent_get_part_transform(ctrl.Agent, corePart, pCore, null) >= 0)
                    cmds.Add(CmdImp(corePart, pCore, ToNmVec(shot.Direction * torsoImp)));
            }

            // Determine exact NM behaviour commands for locational wounds or Playroom continuous ShotTask::next_hit
            bool isHeadshot = (outcome.HasValue && (outcome.Value.InstantHeadshot || outcome.Value.Condition == WoundedCondition.DeadHeadshot));
            bool isLethal = fatal || (outcome.HasValue && outcome.Value.Lethal);

            if (isHeadshot || isLethal)
            {
                syncShotActive = false;
                syncShotHitCount = 0;
                cmds.Add(CmdMsg("stopAllBehaviours"));
                cmds.Add(CmdMsg("bodyRelax start=true holdPose=true relaxation=95 damping=0.8"));
                cmds.Add(CmdDead(true));
                SendRfCmds(cmds);
                return;
            }

            // Progressive Playroom-style limb injury on every limb hit + full injury on ruined limbs
            if (part >= 13 && part <= 16) cmds.Add(CmdMsg("injury mask=ul amount=0.35"));
            else if (part >= 17 && part <= 20) cmds.Add(CmdMsg("injury mask=ur amount=0.35"));
            else if (part >= 1 && part <= 3) cmds.Add(CmdMsg("injury mask=ll amount=0.35"));
            else if (part >= 4 && part <= 6) cmds.Add(CmdMsg("injury mask=lr amount=0.35"));

            if (health != null)
            {
                if (health.LeftArmRuined) cmds.Add(CmdMsg("injury mask=ul amount=0.94"));
                if (health.RightArmRuined) cmds.Add(CmdMsg("injury mask=ur amount=0.94"));
                if (health.LeftLegRuined) cmds.Add(CmdMsg("injury mask=ll amount=0.94"));
                if (health.RightLegRuined) cmds.Add(CmdMsg("injury mask=lr amount=0.94"));
            }

            Vector3 comNm = Vector3.zero;
            if (nm_agent_get_com(ctrl.Agent, comBuf, velBuf) >= 0)
                comNm = new Vector3(comBuf[0], comBuf[1], comBuf[2]);

            Vector3 threatUnity = (info.sourceActor != null ? info.sourceActor.CenterPosition() : (ptUnity - shot.Direction * 4f));
            Vector3 threatNm = UnityToNm(threatUnity - visOff);
            Vector3 anchorNm = new Vector3(comNm.x, comNm.y, 0f);
            Vector3 bulletVelNm = UnityToNm(shot.Direction * Mathf.Max(shot.TaskForce, 240f));
            Vector3 normalNm = UnityToNm(-shot.Direction);

            bool newlyRuinedSpecial = outcome.HasValue && outcome.Value.NewlyRuined && health != null &&
                (health.Condition == WoundedCondition.Surrendering ||
                 health.Condition == WoundedCondition.SurrenderingGround ||
                 health.Condition == WoundedCondition.CrawlingOneLeg ||
                 health.Condition == WoundedCondition.CrawlingTwoLegs ||
                 health.Condition == WoundedCondition.HoppingOneLeg ||
                 health.Condition == WoundedCondition.FallenStomachClutch);

            if (newlyRuinedSpecial)
            {
                syncShotActive = false;
                switch (health.Condition)
                {
                    case WoundedCondition.Surrendering:
                    case WoundedCondition.SurrenderingGround:
                    {
                        bool onGround = health.Condition == WoundedCondition.SurrenderingGround;
                        cmds.Add(CmdMsg(string.Format(ci,
                            "surrender start=true threatPos={0} injuredArm=1 otherArmInjured={1} kneelAmount={2:F2} onGround={3} anchorPos={4} weakness={5:F2}",
                            NmVecStr(threatNm), health.LeftArmRuined ? "true" : "false",
                            health.SurrenderKneel01, onGround ? "true" : "false",
                            NmVecStr(anchorNm), health.Weakness01)));
                        break;
                    }
                    case WoundedCondition.CrawlingOneLeg:
                    case WoundedCondition.CrawlingTwoLegs:
                    {
                        Vector3 goalNm = anchorNm + new Vector3(0f, -4f, 0f);
                        cmds.Add(CmdMsg(string.Format(ci,
                            "woundedCrawl start=true deadLeftLeg={0} deadRightLeg={1} leftArmInjured={2} targetPosition={3} aimTarget={4} shooting=false speed={5:F2} weakness={6:F2} groundZ=0",
                            health.LeftLegRuined ? "true" : "false", health.RightLegRuined ? "true" : "false",
                            health.LeftArmRuined ? "true" : "false", NmVecStr(goalNm), NmVecStr(threatNm),
                            health.BothLegsRuined ? 0.75f : 0.95f, health.Weakness01)));
                        break;
                    }
                    case WoundedCondition.HoppingOneLeg:
                    {
                        int deadLeg = health.LeftLegRuined ? 0 : 1;
                        int clutchPart = (health.StomachDamaged || health.ChestDamaged) ? health.PrimaryTorsoWoundPart : -1;
                        cmds.Add(CmdMsg(string.Format(ci,
                            "woundedHop start=true deadLeg={0} anchorPos={1} weakness={2:F2} clutchPart={3} leftArmInjured={4}",
                            deadLeg, NmVecStr(anchorNm), health.Weakness01, clutchPart,
                            health.LeftArmRuined ? "true" : "false")));
                        break;
                    }
                    case WoundedCondition.FallenStomachClutch:
                    {
                        cmds.Add(CmdMsg(string.Format(ci,
                            "woundedClutch start=true woundPart={0} onGround=true anchorPos={1} aimTarget={2} hunchAmount=0.50 staggerAmount=0.25 weakness={3:F2} useLeftArm={4} groundZ=0",
                            health.PrimaryTorsoWoundPart, NmVecStr(anchorNm), NmVecStr(threatNm),
                            health.Weakness01, health.LeftArmRuined ? "false" : "true")));
                        break;
                    }
                }
            }
            else
            {
                // Playroom continuous ShotTask::next_hit parity:
                //   - First shot starts `shot start=true` + `stayUpright` + `leanInDirection`
                //   - Rapid follow-up shots send `shot` WITHOUT `start=true` so reachForWound, shockSpin,
                //     spinePain, and balance steps accumulate continuously without resetting the pose!
                syncShotHitCount++;
                bool firstShot = !syncShotActive;
                syncShotActive = true;

                float stiffness = Mathf.Lerp(10.0f, 7.4f, shot.Power01);
                float spinePain = Mathf.Lerp(1.2f, 2.5f, shot.Power01);
                string startToken = firstShot ? "start=true " : "";

                cmds.Add(CmdMsg(string.Format(ci,
                    "shot {0}bodyPart={1} hitPoint={2} normal={3} bulletVel={4} reachForWound=true addShockSpin=true shockSpinMin=16 shockSpinMax=52 shockSpinLiftForceMult=0 bodyStiffness={5:F2} spinePainMultiplier={6:F2} useCatchFallOnFall=true",
                    startToken, part, NmVecStr(UnityToNm(ptLocalUnity)), NmVecStr(normalNm), NmVecStr(bulletVelNm),
                    stiffness, spinePain)));

                if (syncShotHitCount >= 5)
                {
                    // After sustained rapid hits, start Playroom-style shotRelax so they buckle naturally
                    cmds.Add(CmdMsg("shotRelax relaxPeriod=0.85"));
                }
                else
                {
                    cmds.Add(CmdMsg(string.Format(ci, "stayUpright start=true stiffness={0:F2}", Mathf.Lerp(4.2f, 1.8f, shot.Power01))));
                }

                Vector3 flatDirNm = UnityToNm(Vector3.ProjectOnPlane(shot.Direction, Vector3.up).normalized);
                if (flatDirNm.sqrMagnitude > 1e-4f)
                {
                    cmds.Add(CmdMsg(string.Format(ci,
                        "leanInDirection start=true applyAsForce=true dir={0} leanAmount={1:F2}",
                        NmVecStr(flatDirNm), Mathf.Lerp(0.35f, 0.85f, shot.Power01))));
                }
            }

            SendRfCmds(cmds);
        }

        public void ForwardPush(ActorRagdollController ctrl, Vector3 unityImpulse)
        {
            if (!connected || ctrl == null || ctrl.Agent == IntPtr.Zero) return;
            var pChest = new float[3];
            if (nm_agent_get_part_transform(ctrl.Agent, 10, pChest, null) < 0)
            {
                pChest[0] = 0f; pChest[1] = 0f; pChest[2] = 1.25f;
            }
            var cmds = new List<string>
            {
                CmdMsg("bodyBalance start=true"),
                CmdImp(10, pChest, ToNmVec(unityImpulse))
            };
            SendRfCmds(cmds);
        }

        public void ForwardExplosion(ActorRagdollController ctrl, Vector3 unityOrigin, float radius, float rawForceOrSpeed, bool fatal, bool isAlreadyNormalizedSpeed)
        {
            if (!connected || ctrl == null || ctrl.Agent == IntPtr.Zero) return;
            Vector3 comNm = Vector3.zero;
            if (nm_agent_get_com(ctrl.Agent, comBuf, velBuf) >= 0)
                comNm = new Vector3(comBuf[0], comBuf[1], comBuf[2]);
            Vector3 orgNm = UnityToNm(unityOrigin - ctrl.SyncVisualOffset);
            Vector3 away = comNm - orgNm;
            float dist = away.magnitude;
            float speed = isAlreadyNormalizedSpeed
                ? Mathf.Clamp(rawForceOrSpeed * config.ExplosionForceScale.Value, 5f, config.MaxExplosionSpeed.Value)
                : WeaponForceNormalizer.NormalizeExplosionSpeed(rawForceOrSpeed, config);
            float falloff = Mathf.Clamp01(1f - dist / Mathf.Max(radius, 2f));
            Vector3 dir = (away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.up);
            dir.z = Mathf.Max(dir.z, 0.45f);
            dir.Normalize();
            Vector3 dv = dir * (speed * Mathf.Max(falloff, 0.35f));

            var cmds = new List<string>
            {
                CmdKick(new[] { dv.x, dv.y, dv.z }),
                CmdMsg("bodyRollUp start=true stiffness=7.5"),
                CmdMsg("catchFall start=true")
            };
            if (fatal) cmds.Add(CmdDead(true));
            SendRfCmds(cmds);
        }

        public void ForwardMelee(ActorRagdollController ctrl, DamageInfo info, bool fatal)
        {
            if (!connected || ctrl == null || ctrl.Agent == IntPtr.Zero) return;
            Vector3 dir = info.direction.sqrMagnitude > 1e-4f
                ? info.direction.normalized
                : (info.impactForce.sqrMagnitude > 1e-4f ? info.impactForce.normalized : Vector3.forward);
            Vector3 ptLocal = (info.point != Vector3.zero ? info.point : (ctrl.Actor != null ? ctrl.Actor.CenterPosition() : ctrl.SyncVisualOffset + Vector3.up)) - ctrl.SyncVisualOffset;
            float[] ptNm = ToNmVec(ptLocal);
            int part = Mathf.Clamp(nm_agent_nearest_part(ctrl.Agent, ptNm), 0, 20);
            float mag = WeaponForceNormalizer.NormalizeMeleeImpulse(info, config);
            Vector3 fromNm = UnityToNm(ptLocal - dir * 1.2f);
            var cmds = new List<string>
            {
                CmdImp(part, ptNm, ToNmVec(dir * mag)),
                CmdMsg($"upperBodyFlinch start=true pos={NmVecStr(fromNm)} newHit=true")
            };
            if (fatal)
            {
                cmds.Add(CmdMsg("bodyRelax start=true holdPose=true"));
                cmds.Add(CmdDead(true));
            }
            SendRfCmds(cmds);
        }

        // =========================================================================
        // Step Packet: Execute Commands, Step Strictly Once, Compute Live Diff & Log
        // =========================================================================
        private void HandleStepPacket(Dictionary<string, object> d)
        {
            if (SyncedController == null || SyncedController.Agent == IntPtr.Zero || mirrorWorldEntry == null || mirrorWorldEntry.World == IntPtr.Zero)
                return;

            IntPtr agent = SyncedController.Agent;
            IntPtr world = mirrorWorldEntry.World;
            long step = MiniJson.GetLong(d, "step", LastStep + 1);

            // 1. Apply exact Playroom tick commands
            var cmds = MiniJson.GetList(d, "cmds");
            if (cmds != null)
            {
                for (int i = 0; i < cmds.Count; i++)
                {
                    if (!(cmds[i] is Dictionary<string, object> c)) continue;
                    string kind = MiniJson.GetString(c, "k");
                    switch (kind)
                    {
                        case "msg":
                        {
                            string line = MiniJson.GetString(c, "l");
                            if (!string.IsNullOrEmpty(line))
                                nm_msg_send_text(agent, world, line);
                            break;
                        }
                        case "kick":
                        {
                            float[] dv = MiniJson.GetFloatArray(c, "dv");
                            if (dv != null && dv.Length >= 3)
                                nm_agent_add_velocity(agent, dv);
                            break;
                        }
                        case "imp":
                        {
                            int part = MiniJson.GetInt(c, "p", 10);
                            float[] at = MiniJson.GetFloatArray(c, "at");
                            float[] imp = MiniJson.GetFloatArray(c, "v");
                            if (at != null && at.Length >= 3 && imp != null && imp.Length >= 3)
                                nm_agent_apply_impulse(agent, part, at, imp);
                            break;
                        }
                        case "dead":
                        {
                            nm_agent_set_dead(agent, MiniJson.GetBool(c, "v", false) ? 1 : 0);
                            break;
                        }
                        case "str":
                        {
                            nm_agent_set_strength(agent, MiniJson.GetFloat(c, "v", 1f));
                            break;
                        }
                        case "slim":
                        {
                            nm_set_step_limit(MiniJson.GetBool(c, "v", false) ? 1 : 0);
                            break;
                        }
                        case "water":
                        {
                            if (MiniJson.TryGetFloat(c, "z", out float wz))
                            {
                                nm_agent_set_water(agent, world, wz);
                                lastWaterSet = true;
                                lastWaterLevel = wz;
                            }
                            else
                            {
                                nm_agent_set_water(agent, world, float.NaN);
                                lastWaterSet = false;
                                lastWaterLevel = float.NaN;
                            }
                            break;
                        }
                    }
                }
            }

            // 2. Sync water level if changed
            if (MiniJson.TryGetFloat(d, "water", out float waterZ))
            {
                if (!lastWaterSet || Mathf.Abs(waterZ - lastWaterLevel) > 1e-4f)
                {
                    nm_agent_set_water(agent, world, waterZ);
                    lastWaterSet = true;
                    lastWaterLevel = waterZ;
                }
            }
            else if (lastWaterSet)
            {
                nm_agent_set_water(agent, world, float.NaN);
                lastWaterSet = false;
                lastWaterLevel = float.NaN;
            }

            // 3. Sync dynamic moving obstacles (crates, barrels, balls, cars, quake/bull platforms)
            dynamicBoxes.Clear();
            var boxes = MiniJson.GetList(d, "boxes");
            if (boxes != null && boxes.Count > 0)
            {
                int n = boxes.Count;
                var boxFloats = new float[n * 13];
                var spinFloats = new float[n * 3];
                int valid = 0;
                Vector3 origin = PlayroomMirrorDefinitions.MirrorOrigin;
                for (int i = 0; i < n; i++)
                {
                    if (!(boxes[i] is Dictionary<string, object> b)) continue;
                    var bc = MiniJson.GetList(b, "c");
                    var bq = MiniJson.GetList(b, "q");
                    var bh = MiniJson.GetList(b, "h");
                    var bv = MiniJson.GetList(b, "v");
                    var bs = MiniJson.GetList(b, "s");
                    if (bc == null || bc.Count < 3 || bq == null || bq.Count < 4 || bh == null || bh.Count < 3) continue;

                    int o = valid * 13;
                    float cx = MiniJson.AsFloat(bc[0]), cy = MiniJson.AsFloat(bc[1]), cz = MiniJson.AsFloat(bc[2]);
                    float qx = MiniJson.AsFloat(bq[0]), qy = MiniJson.AsFloat(bq[1]), qz = MiniJson.AsFloat(bq[2]), qw = MiniJson.AsFloat(bq[3]);
                    float hx = MiniJson.AsFloat(bh[0]), hy = MiniJson.AsFloat(bh[1]), hz = MiniJson.AsFloat(bh[2]);
                    float vx = bv != null && bv.Count >= 3 ? MiniJson.AsFloat(bv[0]) : 0f;
                    float vy = bv != null && bv.Count >= 3 ? MiniJson.AsFloat(bv[1]) : 0f;
                    float vz = bv != null && bv.Count >= 3 ? MiniJson.AsFloat(bv[2]) : 0f;

                    boxFloats[o + 0] = cx; boxFloats[o + 1] = cy; boxFloats[o + 2] = cz;
                    boxFloats[o + 3] = qx; boxFloats[o + 4] = qy; boxFloats[o + 5] = qz; boxFloats[o + 6] = qw;
                    boxFloats[o + 7] = hx; boxFloats[o + 8] = hy; boxFloats[o + 9] = hz;
                    boxFloats[o + 10] = vx; boxFloats[o + 11] = vy; boxFloats[o + 12] = vz;

                    int so = valid * 3;
                    spinFloats[so + 0] = bs != null && bs.Count >= 3 ? MiniJson.AsFloat(bs[0]) : 0f;
                    spinFloats[so + 1] = bs != null && bs.Count >= 3 ? MiniJson.AsFloat(bs[1]) : 0f;
                    spinFloats[so + 2] = bs != null && bs.Count >= 3 ? MiniJson.AsFloat(bs[2]) : 0f;

                    dynamicBoxes.Add(new DynamicSyncBox
                    {
                        CenterUnity = origin + new Vector3(cx, cz, cy),
                        RotUnity = new Quaternion(-qx, -qz, -qy, qw),
                        HalfUnity = new Vector3(hx, hz, hy)
                    });
                    valid++;
                }
                if (valid > 0)
                {
                    nm_world_set_boxes(world, boxFloats, valid);
                    nm_world_set_box_spins(world, spinFloats, valid);
                }
                else
                {
                    nm_world_set_boxes(world, null, 0);
                }
            }
            else
            {
                nm_world_set_boxes(world, null, 0);
            }

            // 4. Deterministic Lockstep Tick (strictly 1 step per Playroom tick!)
            nm_agent_step(agent, world, 1f / 60f);
            LastStep = step;

            // 5. Read Ravenfield full state & compute live diff against Playroom ref
            nm_agent_get_com(agent, comBuf, velBuf);
            float rfKe = nm_agent_get_kinetic_energy(agent);
            bool rfBal = nm_agent_is_balancing(agent) == 1;
            bool rfFall = nm_agent_has_fallen(agent) == 1;
            bool rfCatch = nm_agent_is_catching_fall(agent) == 1;
            bool rfRelax = nm_agent_is_relaxing(agent) == 1;
            bool rfDead = nm_agent_is_dead(agent) == 1;
            uint rfCnt = nm_agent_contact_mask(agent);
            int effCount = nm_agent_get_all_effectors(agent, effActual, effDesired, effStats, effDofs, 20);

            var refObj = MiniJson.GetDict(d, "ref");
            if (refObj != null)
            {
                HasPlayroomRef = true;
                Vector3 prCom = MiniJson.GetNmVec3Raw(refObj, "com");
                Vector3 prVel = MiniJson.GetNmVec3Raw(refObj, "vel");
                float prKe = MiniJson.GetFloat(refObj, "ke", 0f);
                bool prBal = MiniJson.GetBool(refObj, "bal", false);
                bool prFall = MiniJson.GetBool(refObj, "fall", false);
                bool prCatch = MiniJson.GetBool(refObj, "catch", false);
                bool prRelax = MiniJson.GetBool(refObj, "relax", false);
                bool prDead = MiniJson.GetBool(refObj, "dead", false);
                uint prCnt = MiniJson.GetUInt(refObj, "cnt", 0);

                PlayroomGhostComUnity = PlayroomMirrorDefinitions.MirrorOrigin + NmToUnity(prCom);
                Vector3 rfCom = new Vector3(comBuf[0], comBuf[1], comBuf[2]);
                Vector3 rfVel = new Vector3(velBuf[0], velBuf[1], velBuf[2]);
                DeltaComMm = Vector3.Distance(rfCom, prCom) * 1000f;
                DeltaVelMs = Vector3.Distance(rfVel, prVel);
                DeltaKe = Mathf.Abs(rfKe - prKe);
                FlagsMatch = (rfBal == prBal && rfFall == prFall && rfCatch == prCatch && rfRelax == prRelax && rfDead == prDead);
                ContactsMatch = (rfCnt == prCnt);

                float maxErr = 0f;
                int worstIdx = -1;
                var prJoints = MiniJson.GetList(refObj, "j");
                for (int j = 0; j < 20; j++) perJointDeltaDeg[j] = 0f;
                if (prJoints != null && effCount > 0)
                {
                    int n = Math.Min(effCount, Math.Min(prJoints.Count, 20));
                    for (int j = 0; j < n; j++)
                    {
                        if (!(prJoints[j] is List<object> pj) || pj.Count < 6) continue;
                        int dof = Math.Max(1, Math.Min(3, effDofs[j]));
                        float jMaxRad = 0f;
                        for (int k = 0; k < dof; k++)
                        {
                            float diff = Mathf.Abs(effActual[j * 3 + k] - MiniJson.AsFloat(pj[k]));
                            if (diff > jMaxRad) jMaxRad = diff;
                        }
                        float deg = jMaxRad * Mathf.Rad2Deg;
                        perJointDeltaDeg[j] = deg;
                        if (deg > maxErr)
                        {
                            maxErr = deg;
                            worstIdx = j;
                        }
                    }
                }
                MaxJointErrDeg = maxErr;
                WorstJointIndex = worstIdx;
                WorstJointName = (worstIdx >= 0 && worstIdx < jointNames.Length && !string.IsNullOrEmpty(jointNames[worstIdx]))
                    ? jointNames[worstIdx]
                    : (worstIdx >= 0 ? $"joint_{worstIdx}" : "");

                if (FirstDivergenceStep < 0 && (DeltaComMm > 5.0f || MaxJointErrDeg > 2.0f || !FlagsMatch))
                {
                    FirstDivergenceStep = step;
                }
            }

            // 6. Write Ravenfield-side dual JSONL log record (exact same schema as Playroom!)
            if (IsRecording)
            {
                WriteStepLogLine(step, rfKe, rfBal, rfFall, rfCatch, rfRelax, rfDead, rfCnt, effCount);
            }
        }

        private void HandleRecordPacket(Dictionary<string, object> d)
        {
            bool active = MiniJson.GetBool(d, "active", false);
            if (active)
            {
                string sid = MiniJson.GetString(d, "session", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                string dir = MiniJson.GetString(d, "dir", logDir);
                StartRecordingLocal(sid, dir);
            }
            else
            {
                StopRecordingLocal();
            }
        }

        private void StartRecordingLocal(string sid, string dir)
        {
            StopRecordingLocal();
            try
            {
                if (!string.IsNullOrEmpty(dir)) logDir = dir;
                Directory.CreateDirectory(logDir);
                sessionId = sid;
                string path = Path.Combine(logDir, $"ravenfield_sync_{sid}.jsonl");
                logWriter = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false), bufferSize: 65536);
                Plugin.Log?.LogInfo($"[PlayroomSync] Recording Ravenfield log -> {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[PlayroomSync] Failed to open log file: {ex.Message}");
                logWriter = null;
            }
        }

        private void StopRecordingLocal()
        {
            autoRecordRequested = false;
            if (logWriter != null)
            {
                try { logWriter.Flush(); logWriter.Dispose(); } catch { }
                logWriter = null;
            }
        }

        private void WriteResetLogLine(Dictionary<string, object> resetPkt, IntPtr agent)
        {
            if (logWriter == null) return;
            var ci = CultureInfo.InvariantCulture;
            string massStr = nm_agent_get_mass(agent).ToString("F2", ci);
            logWriter.WriteLine(
                "{\"type\":\"reset\",\"side\":\"ravenfield\",\"stage\":\"" + MiniJson.Escape(CurrentStage) +
                "\",\"parity\":\"" + ParityMode + "\",\"mass\":" + massStr + "}");
        }

        private void WriteStepLogLine(long step, float ke, bool bal, bool fall, bool cf, bool relax, bool dead, uint cnt, int effCount)
        {
            if (logWriter == null) return;
            var ci = CultureInfo.InvariantCulture;
            var sb = jsonLineBuf;
            sb.Length = 0;
            sb.Append("{\"side\":\"ravenfield\",\"step\":");
            sb.Append(step);
            sb.Append(",\"com\":[");
            sb.Append(R5(comBuf[0]).ToString("0.#####", ci)).Append(',');
            sb.Append(R5(comBuf[1]).ToString("0.#####", ci)).Append(',');
            sb.Append(R5(comBuf[2]).ToString("0.#####", ci));
            sb.Append("],\"vel\":[");
            sb.Append(R4(velBuf[0]).ToString("0.####", ci)).Append(',');
            sb.Append(R4(velBuf[1]).ToString("0.####", ci)).Append(',');
            sb.Append(R4(velBuf[2]).ToString("0.####", ci));
            sb.Append("],\"ke\":");
            sb.Append(R2(ke).ToString("0.##", ci));
            sb.Append(",\"bal\":").Append(bal ? "true" : "false");
            sb.Append(",\"fall\":").Append(fall ? "true" : "false");
            sb.Append(",\"catch\":").Append(cf ? "true" : "false");
            sb.Append(",\"relax\":").Append(relax ? "true" : "false");
            sb.Append(",\"dead\":").Append(dead ? "true" : "false");
            sb.Append(",\"cnt\":").Append(cnt);
            sb.Append(",\"j\":[");
            int n = Math.Min(effCount, 20);
            for (int j = 0; j < n; j++)
            {
                if (j > 0) sb.Append(',');
                int o3 = j * 3;
                int o4 = j * 4;
                sb.Append('[');
                sb.Append(R4(effActual[o3]).ToString("0.####", ci)).Append(',');
                sb.Append(R4(effActual[o3 + 1]).ToString("0.####", ci)).Append(',');
                sb.Append(R4(effActual[o3 + 2]).ToString("0.####", ci)).Append(',');
                sb.Append(R4(effDesired[o3]).ToString("0.####", ci)).Append(',');
                sb.Append(R4(effDesired[o3 + 1]).ToString("0.####", ci)).Append(',');
                sb.Append(R4(effDesired[o3 + 2]).ToString("0.####", ci)).Append(',');
                sb.Append(R2(effStats[o4]).ToString("0.##", ci)).Append(',');
                sb.Append(R2(effStats[o4 + 1]).ToString("0.##", ci)).Append(',');
                sb.Append(effDofs[j]);
                sb.Append(']');
            }
            sb.Append("]}");
            logWriter.WriteLine(sb.ToString());
        }

        private void ResetDiffMetrics()
        {
            LastStep = 0;
            DeltaComMm = 0f;
            DeltaVelMs = 0f;
            DeltaKe = 0f;
            MaxJointErrDeg = 0f;
            WorstJointIndex = -1;
            WorstJointName = "";
            FlagsMatch = true;
            ContactsMatch = true;
            FirstDivergenceStep = -1;
            HasPlayroomRef = false;
            for (int i = 0; i < perJointDeltaDeg.Length; i++) perJointDeltaDeg[i] = 0f;
        }

        private void CacheJointNames(IntPtr agent)
        {
            if (agent == IntPtr.Zero) return;
            int n = Math.Min(nm_agent_effector_count(agent), jointNames.Length);
            for (int j = 0; j < n; j++)
            {
                int len = nm_agent_effector_name(agent, j, nameBuf, nameBuf.Length);
                jointNames[j] = len > 1 ? Encoding.UTF8.GetString(nameBuf, 0, Math.Min(len - 1, nameBuf.Length - 1)) : $"joint_{j}";
            }
        }

        private void DestroyDynamicStage()
        {
            if (dynamicStageRoot != null)
            {
                UnityEngine.Object.Destroy(dynamicStageRoot);
                dynamicStageRoot = null;
            }
        }

        private void DestroyMirrorWorld()
        {
            if (mirrorWorldEntry != null)
            {
                if (mirrorWorldEntry.World != IntPtr.Zero)
                {
                    nm_world_destroy(mirrorWorldEntry.World);
                    mirrorWorldEntry.World = IntPtr.Zero;
                }
                mirrorWorldEntry = null;
            }
        }

        // =========================================================================
        // Geometry Builders (Exact NM Box & Floor Triangles + Unity Visual Cubes)
        // =========================================================================
        private static Vector3 NmToUnity(Vector3 nm) => new Vector3(nm.x, nm.z, nm.y);
        private static float R2(float x) => Mathf.Round(x * 100f) * 0.01f;
        private static float R4(float x) => Mathf.Round(x * 10000f) * 0.0001f;
        private static float R5(float x) => Mathf.Round(x * 100000f) * 0.00001f;

        private static Color SurfaceColor(string name)
        {
            if (string.IsNullOrEmpty(name)) return new Color(0.76f, 0.78f, 0.80f, 1f);
            string u = name.ToUpperInvariant();
            if (u.Contains("BRICK")) return new Color(0.84f, 0.44f, 0.32f, 1f);
            if (u.Contains("ICE")) return new Color(0.68f, 0.90f, 0.98f, 1f);
            if (u.Contains("WOOD") || u.Contains("PLANK")) return new Color(0.62f, 0.44f, 0.28f, 1f);
            if (u.Contains("METAL") || u.Contains("CAR")) return new Color(0.42f, 0.46f, 0.52f, 1f);
            if (u.Contains("GRASS") || u.Contains("DIRT")) return new Color(0.36f, 0.56f, 0.34f, 1f);
            return new Color(0.78f, 0.80f, 0.82f, 1f);
        }

        private static void SpawnMirrorBox(Transform parent, string name, Vector3 pos, Quaternion rot, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = scale;
            go.isStatic = true;
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    var mat = new Material(shader) { color = color };
                    if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.12f);
                    rend.sharedMaterial = mat;
                }
            }
        }

        private static void AddExactPlayroomFloorGrid(
            List<float> verts, List<uint> tris, List<byte> mats, List<Vector3> debugTris,
            Vector3 visualOrigin, bool pool, float pax, float pay, float pbx, float pby, int half, byte mat)
        {
            for (int ix = -half; ix < half; ix++)
            {
                float x = ix;
                for (int iy = -half; iy < half; iy++)
                {
                    float y = iy;
                    if (pool && x >= pax && x + 1f <= pbx && y >= pay && y + 1f <= pby)
                        continue;

                    uint k = (uint)(verts.Count / 3);
                    verts.Add(x);      verts.Add(y);      verts.Add(0f);
                    verts.Add(x + 1f); verts.Add(y);      verts.Add(0f);
                    verts.Add(x + 1f); verts.Add(y + 1f); verts.Add(0f);
                    verts.Add(x);      verts.Add(y + 1f); verts.Add(0f);

                    tris.Add(k); tris.Add(k + 1); tris.Add(k + 2);
                    tris.Add(k); tris.Add(k + 2); tris.Add(k + 3);
                    mats.Add(mat);
                    mats.Add(mat);

                    // Draw nearby tiles in 3D World BVH overlay (every 2m keeps overlay crisp & fast)
                    if (ix >= -12 && ix < 12 && iy >= -12 && iy < 12 && ((ix & 1) == 0) && ((iy & 1) == 0))
                    {
                        Vector3 v0 = visualOrigin + new Vector3(x, 0f, y);
                        Vector3 v1 = visualOrigin + new Vector3(x + 2f, 0f, y);
                        Vector3 v2 = visualOrigin + new Vector3(x + 2f, 0f, y + 2f);
                        Vector3 v3 = visualOrigin + new Vector3(x, 0f, y + 2f);
                        debugTris.Add(v0); debugTris.Add(v1); debugTris.Add(v2);
                        debugTris.Add(v0); debugTris.Add(v2); debugTris.Add(v3);
                    }
                }
            }
        }

        // Exact Playroom box_tris winding (12 outward-facing triangles in NM Z-up right-handed space)
        private static readonly int[] BoxFaceIndices =
        {
            // +Z top
            4, 5, 6,  4, 6, 7,
            // -Z bottom
            0, 3, 2,  0, 2, 1,
            // +Y
            3, 7, 6,  3, 6, 2,
            // -Y
            0, 1, 5,  0, 5, 4,
            // +X
            1, 2, 6,  1, 6, 5,
            // -X
            0, 4, 7,  0, 7, 3
        };

        private static void AddBoxTrianglesNm(
            List<float> verts, List<uint> tris, List<byte> mats, List<Vector3> debugTris,
            Vector3 visualOrigin, Vector3 cNm, Quaternion qNm, Vector3 hNm, byte mat)
        {
            Vector3[] local =
            {
                new Vector3(-hNm.x, -hNm.y, -hNm.z),
                new Vector3( hNm.x, -hNm.y, -hNm.z),
                new Vector3( hNm.x,  hNm.y, -hNm.z),
                new Vector3(-hNm.x,  hNm.y, -hNm.z),
                new Vector3(-hNm.x, -hNm.y,  hNm.z),
                new Vector3( hNm.x, -hNm.y,  hNm.z),
                new Vector3( hNm.x,  hNm.y,  hNm.z),
                new Vector3(-hNm.x,  hNm.y,  hNm.z),
            };
            uint baseIdx = (uint)(verts.Count / 3);
            Vector3[] worldNm = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                // Rotate in NM space using exact quaternion formula v' = v + 2*q.xyz x (q.xyz x v + q.w * v)
                Vector3 v = RotateNm(qNm, local[i]) + cNm;
                worldNm[i] = v;
                verts.Add(v.x); verts.Add(v.y); verts.Add(v.z);
            }
            for (int t = 0; t < BoxFaceIndices.Length; t += 3)
            {
                int i0 = BoxFaceIndices[t];
                int i1 = BoxFaceIndices[t + 1];
                int i2 = BoxFaceIndices[t + 2];
                tris.Add(baseIdx + (uint)i0);
                tris.Add(baseIdx + (uint)i1);
                tris.Add(baseIdx + (uint)i2);
                mats.Add(mat);

                debugTris.Add(visualOrigin + NmToUnity(worldNm[i0]));
                debugTris.Add(visualOrigin + NmToUnity(worldNm[i1]));
                debugTris.Add(visualOrigin + NmToUnity(worldNm[i2]));
            }
        }

        private static Vector3 RotateNm(Quaternion q, Vector3 v)
        {
            Vector3 u = new Vector3(q.x, q.y, q.z);
            Vector3 t = 2f * Vector3.Cross(u, v);
            return v + q.w * t + Vector3.Cross(u, t);
        }

        public void Dispose()
        {
            Disconnect();
            DestroyDynamicStage();
            DestroyMirrorWorld();
        }
    }
}
