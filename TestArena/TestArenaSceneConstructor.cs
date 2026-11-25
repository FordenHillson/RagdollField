using System;
using System.Collections.Generic;
using System.Linq;
using MapEditor;
using UnityEngine;

namespace DeliriumRavenfield.TestArena
{
    /// <summary>
    /// Procedural MapEditor ISceneConstructor that builds the "Delirium Test Range" arena inside
    /// Ravenfield's native MapEditorGameScene with terrain, water pool, 6 physics test stations,
    /// vehicle spawner, non-capturable CapturePoints, cameras, lighting, and A* NavMesh.
    /// </summary>
    public sealed class TestArenaSceneConstructor : ISceneConstructor
    {
        private MapEditorGameAssistant assistant;
        private Transform arenaRoot;

        public void StartSceneConstruction()
        {
            assistant = UnityEngine.Object.FindObjectOfType<MapEditorGameAssistant>();
            if (assistant != null && assistant.lateAwake != null)
            {
                assistant.lateAwake.SetActive(false);
            }
            Plugin.Log?.LogInfo("[TestArena] StartSceneConstruction");
        }

        public IEnumerable<SceneConstructionProgress> ConstructSceneAsync()
        {
            if (assistant == null)
                assistant = UnityEngine.Object.FindObjectOfType<MapEditorGameAssistant>();

            if (assistant == null)
            {
                Plugin.Log?.LogError("[TestArena] MapEditorGameAssistant not found in MapEditorGameScene!");
                yield break;
            }

            Transform lateAwakeTf = assistant.lateAwake != null ? assistant.lateAwake.transform : null;

            yield return new SceneConstructionProgress("Building Delirium Test Range terrain", 0.15f);
            BuildFlatTerrainWithWaterPool();

            yield return new SceneConstructionProgress("Constructing 6 physics test stations", 0.40f);
            BuildTestStations(lateAwakeTf);

            yield return new SceneConstructionProgress("Placing vehicles & capture points", 0.60f);
            BuildVehicleSpawners(lateAwakeTf);
            BuildCapturePoints(lateAwakeTf);
            ConfigureCameras();

            yield return new SceneConstructionProgress("Scanning A* NavMesh", 0.80f);
            BuildAndScanPathfinding(lateAwakeTf);

            yield return new SceneConstructionProgress("Done", 1.0f);
        }

        public void EndSceneConstruction()
        {
            if (assistant == null)
                assistant = UnityEngine.Object.FindObjectOfType<MapEditorGameAssistant>();

            if (assistant != null)
            {
                if (assistant.lateAwake != null)
                {
                    assistant.lateAwake.SetActive(true);
                }

                try
                {
                    bool nightMode = GameManager.GameParameters() != null && GameManager.GameParameters().nightMode;
                    PostProcessingManager.FindAndApplyLevelColorGrading(nightMode);
                }
                catch { }
            }

            Plugin.TestArenaManager?.OnTestArenaSceneLoaded();
            Plugin.Log?.LogInfo("[TestArena] EndSceneConstruction complete — Delirium Test Range ready!");
        }

        private void BuildFlatTerrainWithWaterPool()
        {
            try
            {
                var bc = assistant.biomeContainer;
                if (bc == null) return;

                BiomeAsset biome = AssetTable.GetAllBiomes().FirstOrDefault(b => b.HasValue());
                var settings = new BiomeTerrainSettings(TestArenaConstants.TerrainSize, 257, 256, useMapGenerator: false);
                bc.Populate(biome, settings);

                Terrain terrain = bc.GetTerrain();
                if (terrain != null && terrain.terrainData != null)
                {
                    TerrainData td = terrain.terrainData;
                    float half = td.size.x * 0.5f;
                    terrain.transform.position = new Vector3(-half, 0f, -td.size.z * 0.5f);

                    int res = td.heightmapResolution;
                    float[,] heights = new float[res, res];
                    Vector3 origin = terrain.transform.position;
                    float heightScale = Mathf.Max(td.size.y, 1f);
                    float flatNorm = Mathf.Clamp01((TestArenaConstants.GroundY - origin.y) / heightScale);
                    float bottomNorm = Mathf.Clamp01((TestArenaConstants.PoolBottomY - origin.y) / heightScale);

                    for (int hz = 0; hz < res; hz++)
                    {
                        float wz = origin.z + ((float)hz / (res - 1)) * td.size.z;
                        for (int hx = 0; hx < res; hx++)
                        {
                            float wx = origin.x + ((float)hx / (res - 1)) * td.size.x;
                            float h = flatNorm;

                            // Carve smooth recessed basin for Station 6 Water Pool
                            if (wx >= TestArenaConstants.PoolMinX - 2f && wx <= TestArenaConstants.PoolMaxX + 2f &&
                                wz >= TestArenaConstants.PoolMinZ - 2f && wz <= TestArenaConstants.PoolMaxZ + 2f)
                            {
                                float dx = Mathf.Min(wx - TestArenaConstants.PoolMinX, TestArenaConstants.PoolMaxX - wx);
                                float dz = Mathf.Min(wz - TestArenaConstants.PoolMinZ, TestArenaConstants.PoolMaxZ - wz);
                                float edge = Mathf.Min(dx, dz);
                                if (edge >= 1.5f)
                                {
                                    h = bottomNorm;
                                }
                                else if (edge > -1.5f)
                                {
                                    float t = Mathf.InverseLerp(-1.5f, 1.5f, edge);
                                    t = t * t * (3f - 2f * t);
                                    h = Mathf.Lerp(flatNorm, bottomNorm, t);
                                }
                            }

                            heights[hz, hx] = h;
                        }
                    }

                    td.SetHeights(0, 0, heights);
                    terrain.Flush();
                    try { FixBundleShaders.ApplyTerrainFallbackMaterial(terrain); } catch { }
                }

                WaterLevel water = bc.GetWaterLevel();
                if (water != null)
                {
                    water.transform.position = new Vector3(0f, TestArenaConstants.WaterY, 0f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[TestArena] BuildFlatTerrainWithWaterPool failed: {ex}");
            }
        }

        private void BuildTestStations(Transform parent)
        {
            var rootGo = new GameObject("DeliriumTestArena_Geometry");
            if (parent != null) rootGo.transform.SetParent(parent, false);
            arenaRoot = rootGo.transform;

            Material matConcrete = CreateMat(new Color(0.56f, 0.58f, 0.60f));
            Material matDarkConcrete = CreateMat(new Color(0.32f, 0.34f, 0.38f));
            Material matAsphalt = CreateMat(new Color(0.20f, 0.21f, 0.23f));
            Material matOrange = CreateMat(new Color(0.92f, 0.45f, 0.12f));
            Material matYellow = CreateMat(new Color(0.92f, 0.78f, 0.16f));
            Material matBlue = CreateMat(new Color(0.22f, 0.46f, 0.80f));
            Material matRed = CreateMat(new Color(0.78f, 0.22f, 0.22f));
            Material matWood = CreateMat(new Color(0.55f, 0.38f, 0.22f));
            Material matWhite = CreateMat(new Color(0.88f, 0.88f, 0.88f));

            float gy = TestArenaConstants.GroundY;

            // =========================================================================
            // 1. MAIN SHOOTING LANE & PLAYER BOOTH (Center, X=-9..+9, Z=-27..+22)
            // =========================================================================
            // Main flat concrete slab slightly recessed so top surface is exactly gy + 0.02m
            AddBox("ShootingLane_Pad", new Vector3(0f, gy - 0.08f, -2f), new Vector3(18f, 0.20f, 52f), matConcrete);

            // Player Booth low side desks (open front)
            AddBox("Booth_Base", new Vector3(0f, gy - 0.06f, -24.5f), new Vector3(7f, 0.20f, 4.5f), matDarkConcrete);
            AddBox("Booth_LeftWall", new Vector3(-3.3f, gy + 0.55f, -24.5f), new Vector3(0.35f, 1.1f, 4.5f), matBlue);
            AddBox("Booth_RightWall", new Vector3(3.3f, gy + 0.55f, -24.5f), new Vector3(0.35f, 1.1f, 4.5f), matBlue);
            AddBox("Booth_BackWall", new Vector3(0f, gy + 0.55f, -26.6f), new Vector3(7.0f, 1.1f, 0.35f), matBlue);

            // Distance strip markers at 5m, 10m, 15m, 25m, 40m from player booth (Z=-24)
            float[] distMarks = { 5f, 10f, 15f, 25f, 40f };
            for (int i = 0; i < distMarks.Length; i++)
            {
                float d = distMarks[i];
                float z = -24f + d;
                Material lineMat = (i % 2 == 0) ? matOrange : matYellow;
                AddBox($"RangeLine_{d:0}m", new Vector3(0f, gy + 0.025f, z), new Vector3(16f, 0.02f, 0.22f), lineMat, withCollider: false);
                // Side range marker posts
                AddBox($"RangePostL_{d:0}m", new Vector3(-8.5f, gy + 0.7f, z), new Vector3(0.28f, 1.4f, 0.28f), lineMat);
                AddBox($"RangePostR_{d:0}m", new Vector3( 8.5f, gy + 0.7f, z), new Vector3(0.28f, 1.4f, 0.28f), lineMat);
            }

            // Backstop berm behind 40m line
            AddBox("Lane_Backstop", new Vector3(0f, gy + 2.0f, 22.5f), new Vector3(20f, 4.0f, 1.2f), matDarkConcrete);

            // =========================================================================
            // 2. WALL & CORNER BRACING STATION (West-Near, X=-24..-12, Z=-16..+4)
            // =========================================================================
            AddBox("WallStation_Pad", new Vector3(-17.5f, gy - 0.08f, -5.5f), new Vector3(11f, 0.20f, 20f), matConcrete);
            // Main bracing wall (dummies stand at X=-18.8 facing +X, wall face at X=-19.75)
            AddBox("BraceWall_Main", new Vector3(-20.0f, gy + 1.7f, -7.0f), new Vector3(0.5f, 3.4f, 10.0f), matDarkConcrete);
            AddBox("BraceWall_Stripe", new Vector3(-19.73f, gy + 1.2f, -7.0f), new Vector3(0.06f, 0.18f, 9.8f), matOrange, withCollider: false);
            // L-Corner (dummies inside corner at X=-18.6, Z=+1.6)
            AddBox("BraceCorner_West", new Vector3(-20.0f, gy + 1.7f, 0.75f), new Vector3(0.5f, 3.4f, 5.5f), matDarkConcrete);
            AddBox("BraceCorner_North", new Vector3(-17.25f, gy + 1.7f, 3.25f), new Vector3(6.0f, 3.4f, 0.5f), matDarkConcrete);
            // Waist-high rail for trip / drape over rail testing
            AddBox("BraceWall_LowRail", new Vector3(-15.0f, gy + 0.52f, -13.5f), new Vector3(5.0f, 1.04f, 0.28f), matYellow);

            // =========================================================================
            // 3. STAIRS & ELEVATED DROP TOWER STATION (West-Far, X=-36..-19, Z=+8..+24)
            // =========================================================================
            // Flight 1: 12 steps ascending +Z from Z=+10.0 to Z=+14.8 -> height +3.0m
            const int steps1 = 12;
            const float stepRise = 0.25f;
            const float stepTread = 0.40f;
            for (int s = 0; s < steps1; s++)
            {
                float h = (s + 1) * stepRise;
                float zCenter = 10.0f + s * stepTread + stepTread * 0.5f;
                AddBox($"Stairs1_Step{s + 1}", new Vector3(-24.0f, gy + h * 0.5f, zCenter), new Vector3(3.8f, h, stepTread), (s % 2 == 0) ? matConcrete : matDarkConcrete);
            }
            // 3.0m Landing Deck (X=-27..-21, Z=+14.8..+21.2) — open +X ledge at X=-21 for NM_XR_LEDGE!
            AddBox("Tower_3mDeck", new Vector3(-24.0f, gy + 1.5f, 18.0f), new Vector3(6.0f, 3.0f, 6.4f), matDarkConcrete);
            AddBox("Tower_3mLedgeStripe", new Vector3(-21.12f, gy + 3.01f, 18.0f), new Vector3(0.24f, 0.03f, 6.2f), matOrange, withCollider: false);
            // Back guardrail on north side of 3m deck
            AddBox("Tower_3mNorthRail", new Vector3(-24.0f, gy + 3.55f, 21.05f), new Vector3(6.0f, 1.1f, 0.25f), matYellow);

            // Flight 2: 12 steps ascending -X from X=-27.0 to X=-31.8 at Z=+19.4 -> height +3.0m to +6.0m
            for (int s = 0; s < steps1; s++)
            {
                float topY = 3.0f + (s + 1) * stepRise;
                float xCenter = -27.0f - s * stepTread - stepTread * 0.5f;
                AddBox($"Stairs2_Step{s + 1}", new Vector3(xCenter, gy + topY * 0.5f, 19.4f), new Vector3(stepTread, topY, 2.8f), (s % 2 == 0) ? matConcrete : matDarkConcrete);
            }
            // 6.0m High-Fall Deck (X=-35.4..-31.8, Z=+14.0..+21.0) — open -Z drop edge at Z=+14.0!
            AddBox("Tower_6mDeck", new Vector3(-33.6f, gy + 3.0f, 17.5f), new Vector3(3.6f, 6.0f, 7.0f), matDarkConcrete);
            AddBox("Tower_6mLedgeStripe", new Vector3(-33.6f, gy + 6.01f, 14.12f), new Vector3(3.5f, 0.03f, 0.24f), matRed, withCollider: false);
            AddBox("Tower_6mWestRail", new Vector3(-35.28f, gy + 6.55f, 17.5f), new Vector3(0.24f, 1.1f, 7.0f), matYellow);
            AddBox("Tower_6mNorthRail", new Vector3(-33.6f, gy + 6.55f, 20.88f), new Vector3(3.6f, 1.1f, 0.24f), matYellow);
            // Landing mat under 6m drop
            AddBox("Tower_6mDropZone", new Vector3(-33.6f, gy + 0.02f, 11.2f), new Vector3(4.5f, 0.04f, 4.5f), matRed, withCollider: false);

            // =========================================================================
            // 4. COVER & WOUNDED CRAWL STATION (East-Near, X=+13..+31, Z=-14..+8)
            // =========================================================================
            AddBox("CoverStation_Pad", new Vector3(21.5f, gy - 0.08f, -3.0f), new Vector3(16f, 0.20f, 22f), matConcrete);
            // Waist-high concrete barriers (1.1m tall)
            AddBox("Cover_Barrier1", new Vector3(15.5f, gy + 0.55f, -9.0f), new Vector3(0.55f, 1.1f, 3.6f), matDarkConcrete);
            AddBox("Cover_Barrier2", new Vector3(21.5f, gy + 0.55f, -6.0f), new Vector3(3.6f, 1.1f, 0.55f), matDarkConcrete);
            AddBox("Cover_Barrier3", new Vector3(26.0f, gy + 0.55f, -1.5f), new Vector3(0.55f, 1.1f, 3.8f), matDarkConcrete);
            AddBox("Cover_Barrier4", new Vector3(18.5f, gy + 0.55f,  0.5f), new Vector3(3.8f, 1.1f, 0.55f), matDarkConcrete);
            // Tall cover pillars
            AddBox("Cover_Pillar1", new Vector3(24.5f, gy + 1.4f, 4.5f), new Vector3(1.6f, 2.8f, 1.6f), matWood);
            AddBox("Cover_Pillar2", new Vector3(16.0f, gy + 1.4f, 4.5f), new Vector3(1.6f, 2.8f, 1.6f), matWood);

            // =========================================================================
            // 5. VEHICLE RAMMING LANE (East-Far, X=+35..+47, Z=-28..+26)
            // =========================================================================
            AddBox("CarLane_Asphalt", new Vector3(41f, gy - 0.07f, -1f), new Vector3(11.5f, 0.20f, 54f), matAsphalt);
            AddBox("CarLane_CurbL", new Vector3(35.0f, gy + 0.20f, -1f), new Vector3(0.5f, 0.45f, 54f), matYellow);
            AddBox("CarLane_CurbR", new Vector3(47.0f, gy + 0.20f, -1f), new Vector3(0.5f, 0.45f, 54f), matYellow);
            // Dashed center line
            for (float z = -22f; z <= 22f; z += 6f)
            {
                AddBox($"CarLane_Dash_{z:0}", new Vector3(41f, gy + 0.04f, z), new Vector3(0.22f, 0.02f, 2.8f), matWhite, withCollider: false);
            }
            AddBox("CarLane_Backstop", new Vector3(41f, gy + 1.5f, 26.5f), new Vector3(12.5f, 3.0f, 1.0f), matOrange);

            // =========================================================================
            // 6. WATER POOL & DIVING LEDGE (Far East, X=+51..+71, Z=-13..+7)
            // =========================================================================
            // Concrete rim curbs framing the carved water pool
            AddBox("Pool_RimNorth", new Vector3(61f, gy + 0.15f, TestArenaConstants.PoolMaxZ + 1.4f), new Vector3(19f, 0.5f, 1.2f), matConcrete);
            AddBox("Pool_RimEast",  new Vector3(TestArenaConstants.PoolMaxX + 1.4f, gy + 0.15f, -3f), new Vector3(1.2f, 0.5f, 19f), matConcrete);
            AddBox("Pool_RimWestN", new Vector3(TestArenaConstants.PoolMinX - 1.4f, gy + 0.15f, 2.5f), new Vector3(1.2f, 0.5f, 7.5f), matConcrete);
            AddBox("Pool_RimWestS", new Vector3(TestArenaConstants.PoolMinX - 1.4f, gy + 0.15f, -8.5f), new Vector3(1.2f, 0.5f, 6.5f), matConcrete);

            // Diving Ledge Platform (+2.0m above ground -> 3.8m above water) with stairs on west side
            AddBox("Pool_DivingDeck", new Vector3(52.2f, gy + 1.0f, -3.0f), new Vector3(3.2f, 2.0f, 5.2f), matBlue);
            AddBox("Pool_DivingEdge", new Vector3(53.7f, gy + 2.01f, -3.0f), new Vector3(0.22f, 0.03f, 5.0f), matYellow, withCollider: false);
            for (int s = 0; s < 8; s++)
            {
                float h = (s + 1) * 0.25f;
                float xCenter = 48.0f + s * 0.32f + 0.16f;
                AddBox($"Pool_Step{s + 1}", new Vector3(xCenter, gy + h * 0.5f, -3.0f), new Vector3(0.32f, h, 3.0f), matConcrete);
            }
        }

        private void BuildVehicleSpawners(Transform parent)
        {
            if (assistant.vehicleSpawnerPrefab == null) return;
            try
            {
                var go = UnityEngine.Object.Instantiate(assistant.vehicleSpawnerPrefab, parent);
                go.name = "TestRange_JeepSpawner";
                go.transform.position = TestArenaConstants.JeepSpawnPos;
                go.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

                var spawner = go.GetComponent<VehicleSpawner>();
                if (spawner != null)
                {
                    spawner.typeToSpawn = VehicleSpawner.VehicleSpawnType.Jeep;
                    spawner.spawnBehaviour = VehicleSpawner.SpawnBehaviour.Default;
                    spawner.respawnType = VehicleSpawner.RespawnType.AfterDestroyed;
                    spawner.spawnTime = 3.0f;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[TestArena] Failed to place Jeep spawner: {ex.Message}");
            }
        }

        private void BuildCapturePoints(Transform parent)
        {
            if (assistant.capturePointPrefab == null)
            {
                Plugin.Log?.LogError("[TestArena] assistant.capturePointPrefab is null!");
                return;
            }

            // Blue CapturePoint (Player Shooting Booth)
            CapturePoint blueCp = CreateCapturePoint(
                parent,
                "Shooting Booth",
                "BOOTH",
                SpawnPoint.Team.Blue,
                TestArenaConstants.PlayerSpawnPos,
                new[] { (TestArenaConstants.PlayerSpawnPos, TestArenaConstants.PlayerSpawnDir) });

            // Red CapturePoint (Target Range) — spawns distributed at dummy slots
            var redSpawns = new (Vector3, Vector3)[TestArenaConstants.DummySlots.Length];
            for (int i = 0; i < TestArenaConstants.DummySlots.Length; i++)
            {
                var slot = TestArenaConstants.DummySlots[i];
                redSpawns[i] = (slot.Position + Vector3.up * 0.1f, slot.FacingDir);
            }

            CapturePoint redCp = CreateCapturePoint(
                parent,
                "Target Range",
                "RANGE",
                SpawnPoint.Team.Red,
                TestArenaConstants.RedCapturePos,
                redSpawns);

            if (assistant.neighbourManager != null && blueCp != null && redCp != null)
            {
                assistant.neighbourManager.neighbors = new[]
                {
                    new SpawnPointNeighborManager.SpawnPointNeighbor
                    {
                        a = blueCp,
                        b = redCp,
                        landConnection = true,
                        waterConnection = false,
                        oneWay = false
                    }
                };
            }
        }

        private CapturePoint CreateCapturePoint(
            Transform parent,
            string fullName,
            string shortName,
            SpawnPoint.Team team,
            Vector3 position,
            (Vector3 pos, Vector3 dir)[] spawns)
        {
            var go = UnityEngine.Object.Instantiate(assistant.capturePointPrefab, parent);
            go.name = fullName;
            go.transform.position = position;
            go.transform.rotation = Quaternion.identity;

            var cp = go.GetComponent<CapturePoint>();
            if (cp == null) return null;

            if (cp.spawnpointContainer != null)
            {
                UnityEngine.Object.Destroy(cp.spawnpointContainer.gameObject);
                cp.spawnpointContainer = null;
            }
            if (cp.contestedSpawnpointContainer != null)
            {
                UnityEngine.Object.Destroy(cp.contestedSpawnpointContainer.gameObject);
                cp.contestedSpawnpointContainer = null;
            }

            cp.defaultOwner = team;
            cp.shortName = shortName;
            cp.protectRange = 45f;
            cp.captureRange = 4f;
            cp.captureCeiling = 8f;
            cp.captureFloor = 2f;
            cp.captureRate = 0f;
            cp.canCapture = false;

            var containerGo = new GameObject("Spawn Container");
            containerGo.transform.SetParent(cp.transform, false);
            containerGo.transform.localPosition = Vector3.zero;
            containerGo.transform.localRotation = Quaternion.identity;
            containerGo.transform.localScale = Vector3.one;
            cp.spawnpointContainer = containerGo.transform;

            for (int i = 0; i < spawns.Length; i++)
            {
                var spGo = new GameObject($"Spawn_{i}");
                spGo.transform.position = spawns[i].pos;
                Vector3 d = spawns[i].dir;
                d.y = 0f;
                if (d.sqrMagnitude < 1e-4f) d = Vector3.forward;
                spGo.transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                spGo.transform.SetParent(cp.spawnpointContainer, true);
            }

            return cp;
        }

        private void ConfigureCameras()
        {
            try
            {
                if (assistant.minimapCamera != null)
                {
                    assistant.minimapCamera.transform.position = new Vector3(16f, 130f, 0f);
                    assistant.minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    var cam = assistant.minimapCamera.GetComponentInChildren<Camera>();
                    if (cam != null)
                    {
                        cam.orthographic = true;
                        cam.orthographicSize = 70f;
                        cam.nearClipPlane = 1f;
                        cam.farClipPlane = 260f;
                    }
                }

                if (assistant.sceneryCamera != null)
                {
                    assistant.sceneryCamera.transform.position = new Vector3(-22f, TestArenaConstants.GroundY + 18f, -38f);
                    assistant.sceneryCamera.transform.LookAt(new Vector3(12f, TestArenaConstants.GroundY + 1f, 2f));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[TestArena] ConfigureCameras warning: {ex.Message}");
            }
        }

        private void BuildAndScanPathfinding(Transform parent)
        {
            try
            {
                var pfRoot = new GameObject("TestArena_PathfindingBoxes");
                if (parent != null) pfRoot.transform.SetParent(parent, false);

                // Infantry NavMesh box covering all 6 stations
                CreatePathfindingBox(pfRoot.transform, "PF_Infantry", PathfindingBox.Type.Infantry,
                    new Vector3(18f, TestArenaConstants.GroundY + 8f, 0f), new Vector3(130f, 24f, 90f));

                // Car NavMesh box covering the car lane
                CreatePathfindingBox(pfRoot.transform, "PF_Car", PathfindingBox.Type.Car,
                    new Vector3(41f, TestArenaConstants.GroundY + 4f, 0f), new Vector3(24f, 12f, 70f));

                Physics.SyncTransforms();

                if (assistant.pathfindingManager != null)
                {
                    assistant.pathfindingManager.FindAndScan();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[TestArena] NavMesh scan warning (non-fatal): {ex.Message}");
            }
        }

        private static void CreatePathfindingBox(Transform parent, string name, PathfindingBox.Type type, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.transform.localScale = size;

            var box = go.AddComponent<PathfindingBox>();
            box.type = type;
            box.tiled = false;
            box.cellSize = 0.5f;
            box.characterRadius = 0.4f;
            box.climbHeight = 0.45f;
            box.maxSlope = 45f;
            box.coverPointSpacing = 2.0f;
        }

        private GameObject AddBox(string name, Vector3 position, Vector3 scale, Material mat, bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.layer = 0; // Default layer -> hit by bullets, player physics, AND exported to Delirium static BVH!
            go.transform.SetParent(arenaRoot, false);
            go.transform.position = position;
            go.transform.localScale = scale;

            if (!withCollider)
            {
                var col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
            }

            var rend = go.GetComponent<Renderer>();
            if (rend != null && mat != null)
            {
                rend.sharedMaterial = mat;
            }
            return go;
        }

        private static Material CreateMat(Color color)
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { color = color };
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.05f);
            return mat;
        }
    }
}
