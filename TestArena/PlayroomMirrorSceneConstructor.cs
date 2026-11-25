using System;
using System.Collections.Generic;
using System.Linq;
using MapEditor;
using UnityEngine;

namespace DeliriumRavenfield.TestArena
{
    /// <summary>
    /// Procedural MapEditor ISceneConstructor for the dedicated "Delirium Playroom Mirror" map.
    /// Creates a clean flat ground at Y=10.0 with 1 Blue spawn (Player) and 1 Red spawn (Synced Bot),
    /// plus a dynamic stage container that mirrors Playroom's exact blocks/tilted/floor geometry 1:1.
    /// </summary>
    public sealed class PlayroomMirrorSceneConstructor : ISceneConstructor
    {
        private MapEditorGameAssistant assistant;

        public void StartSceneConstruction()
        {
            assistant = UnityEngine.Object.FindObjectOfType<MapEditorGameAssistant>();
            if (assistant != null && assistant.lateAwake != null)
            {
                assistant.lateAwake.SetActive(false);
            }
            Plugin.Log?.LogInfo("[PlayroomMirror] StartSceneConstruction");
        }

        public IEnumerable<SceneConstructionProgress> ConstructSceneAsync()
        {
            if (assistant == null)
                assistant = UnityEngine.Object.FindObjectOfType<MapEditorGameAssistant>();
            if (assistant == null)
            {
                Plugin.Log?.LogError("[PlayroomMirror] MapEditorGameAssistant not found!");
                yield break;
            }

            Transform lateAwakeTf = assistant.lateAwake != null ? assistant.lateAwake.transform : null;

            yield return new SceneConstructionProgress("Building clean Playroom Mirror floor", 0.25f);
            BuildFlatTerrain();
            BuildBaseGridFloor(lateAwakeTf);

            yield return new SceneConstructionProgress("Placing 1v1 Mirror spawn points", 0.55f);
            BuildCapturePoints(lateAwakeTf);
            ConfigureCameras();

            yield return new SceneConstructionProgress("Scanning Mirror NavMesh", 0.80f);
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
                    assistant.lateAwake.SetActive(true);
                try
                {
                    bool nightMode = GameManager.GameParameters() != null && GameManager.GameParameters().nightMode;
                    PostProcessingManager.FindAndApplyLevelColorGrading(nightMode);
                }
                catch { }
            }

            Plugin.PlayroomSync?.OnMirrorSceneLoaded();
            Plugin.Log?.LogInfo("[PlayroomMirror] EndSceneConstruction complete — Delirium Playroom Mirror ready!");
        }

        private void BuildFlatTerrain()
        {
            try
            {
                var bc = assistant.biomeContainer;
                if (bc == null) return;

                BiomeAsset biome = AssetTable.GetAllBiomes().FirstOrDefault(b => b.HasValue());
                var settings = new BiomeTerrainSettings(PlayroomMirrorDefinitions.TerrainSize, 129, 128, useMapGenerator: false);
                bc.Populate(biome, settings);

                Terrain terrain = bc.GetTerrain();
                if (terrain != null && terrain.terrainData != null)
                {
                    TerrainData td = terrain.terrainData;
                    terrain.transform.position = new Vector3(-td.size.x * 0.5f, 0f, -td.size.z * 0.5f);

                    int res = td.heightmapResolution;
                    float[,] heights = new float[res, res];
                    float heightScale = Mathf.Max(td.size.y, 1f);
                    // Put terrain slightly recessed under the Playroom floor pad (Y = OriginY - 2.0) so pits/pools work!
                    float norm = Mathf.Clamp01((PlayroomMirrorDefinitions.OriginY - 2.0f) / heightScale);
                    for (int hz = 0; hz < res; hz++)
                        for (int hx = 0; hx < res; hx++)
                            heights[hz, hx] = norm;

                    td.SetHeights(0, 0, heights);
                    terrain.Flush();
                    try { FixBundleShaders.ApplyTerrainFallbackMaterial(terrain); } catch { }
                }

                WaterLevel water = bc.GetWaterLevel();
                if (water != null)
                {
                    water.transform.position = new Vector3(0f, PlayroomMirrorDefinitions.OriginY - 25f, 0f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PlayroomMirror] BuildFlatTerrain warning: {ex.Message}");
            }
        }

        private static void BuildBaseGridFloor(Transform parent)
        {
            var root = new GameObject("PlayroomMirror_StaticRoot");
            if (parent != null) root.transform.SetParent(parent, false);

            // Playroom-style blue-grey flat concrete floor slab with top face at exact Y = OriginY (10.0)
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "PlayroomMirror_BaseFloor";
            floor.layer = 0;
            floor.transform.SetParent(root.transform, false);
            floor.transform.position = new Vector3(0f, PlayroomMirrorDefinitions.OriginY - 0.5f, 0f);
            floor.transform.localScale = new Vector3(100f, 1.0f, 100f);

            var rend = floor.GetComponent<Renderer>();
            if (rend != null)
            {
                Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Sprites/Default");
                var mat = new Material(shader) { color = new Color(0.24f, 0.36f, 0.56f, 1f) };
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.12f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.04f);
                rend.sharedMaterial = mat;
            }
        }

        private void BuildCapturePoints(Transform parent)
        {
            if (assistant.capturePointPrefab == null) return;

            var blueCp = CreateCapturePoint(
                parent,
                "Mirror Observer",
                "OBS",
                SpawnPoint.Team.Blue,
                PlayroomMirrorDefinitions.PlayerSpawnPos,
                PlayroomMirrorDefinitions.PlayerSpawnDir);

            var redCp = CreateCapturePoint(
                parent,
                "Mirror Subject",
                "SYNC",
                SpawnPoint.Team.Red,
                PlayroomMirrorDefinitions.BotSpawnPos,
                PlayroomMirrorDefinitions.BotSpawnDir);

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
            Vector3 pos,
            Vector3 dir)
        {
            var go = UnityEngine.Object.Instantiate(assistant.capturePointPrefab, parent);
            go.name = fullName;
            go.transform.position = pos;
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
            cp.protectRange = 40f;
            cp.captureRange = 2f;
            cp.captureCeiling = 8f;
            cp.captureFloor = 2f;
            cp.captureRate = 0f;
            cp.canCapture = false;

            var containerGo = new GameObject("Spawn Container");
            containerGo.transform.SetParent(cp.transform, false);
            cp.spawnpointContainer = containerGo.transform;

            var spGo = new GameObject("Spawn_0");
            spGo.transform.position = pos;
            Vector3 d = dir;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) d = Vector3.forward;
            spGo.transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            spGo.transform.SetParent(cp.spawnpointContainer, true);

            return cp;
        }

        private void ConfigureCameras()
        {
            try
            {
                if (assistant.minimapCamera != null)
                {
                    assistant.minimapCamera.transform.position = new Vector3(0f, 120f, 0f);
                    assistant.minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    var cam = assistant.minimapCamera.GetComponentInChildren<Camera>();
                    if (cam != null)
                    {
                        cam.orthographic = true;
                        cam.orthographicSize = 50f;
                    }
                }
                if (assistant.sceneryCamera != null)
                {
                    assistant.sceneryCamera.transform.position = new Vector3(4.5f, PlayroomMirrorDefinitions.OriginY + 3.5f, -6.5f);
                    assistant.sceneryCamera.transform.LookAt(new Vector3(0f, PlayroomMirrorDefinitions.OriginY + 1.0f, 0f));
                }
            }
            catch { }
        }

        private void BuildAndScanPathfinding(Transform parent)
        {
            try
            {
                var pfRoot = new GameObject("PlayroomMirror_PathfindingBoxes");
                if (parent != null) pfRoot.transform.SetParent(parent, false);

                var go = new GameObject("PF_MirrorInfantry");
                go.transform.SetParent(pfRoot.transform, false);
                go.transform.position = new Vector3(0f, PlayroomMirrorDefinitions.OriginY + 6f, 0f);
                go.transform.localScale = new Vector3(96f, 20f, 96f);

                var box = go.AddComponent<PathfindingBox>();
                box.type = PathfindingBox.Type.Infantry;
                box.tiled = false;
                box.cellSize = 0.5f;
                box.characterRadius = 0.4f;
                box.climbHeight = 0.45f;
                box.maxSlope = 45f;
                box.coverPointSpacing = 2.0f;

                Physics.SyncTransforms();
                if (assistant.pathfindingManager != null && assistant.pathfindingManager.gameObject.activeInHierarchy)
                {
                    assistant.pathfindingManager.FindAndScan();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PlayroomMirror] NavMesh scan warning (non-fatal): {ex.Message}");
            }
        }
    }
}
