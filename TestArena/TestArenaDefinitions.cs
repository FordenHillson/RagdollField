using System;
using MapEditor;
using UnityEngine;

namespace DeliriumRavenfield.TestArena
{
    public enum TestBotMode
    {
        Normal = 0,
        PureDummy = 1,
        WoundedAiActive = 2
    }

    public readonly struct DummySlot
    {
        public readonly string StationName;
        public readonly Vector3 Position;
        public readonly Vector3 FacingDir;

        public DummySlot(string stationName, Vector3 position, Vector3 facingDir)
        {
            StationName = stationName;
            Position = position;
            FacingDir = facingDir.sqrMagnitude > 1e-4f ? facingDir.normalized : Vector3.back;
        }
    }

    public readonly struct StationTeleportPoint
    {
        public readonly string Name;
        public readonly Vector3 Position;
        public readonly Vector3 LookDir;

        public StationTeleportPoint(string name, Vector3 position, Vector3 lookDir)
        {
            Name = name;
            Position = position;
            LookDir = lookDir.sqrMagnitude > 1e-4f ? lookDir.normalized : Vector3.forward;
        }
    }

    public static class TestArenaConstants
    {
        public const string VirtualMapPath = "DeliriumTestRange.rfld";
        public const string DisplayName = "Delirium Test Range";
        public const int TerrainSize = 256;
        public const float GroundY = 10.0f;
        public const float WaterY = 8.2f;
        public const float PoolBottomY = 5.5f;

        // Carved water pool world bounds (centered around X=61, Z=-3)
        public const float PoolMinX = 53.0f;
        public const float PoolMaxX = 69.0f;
        public const float PoolMinZ = -11.0f;
        public const float PoolMaxZ = 5.0f;

        // Player spawn booth
        public static readonly Vector3 PlayerSpawnPos = new Vector3(0f, GroundY + 0.1f, -24f);
        public static readonly Vector3 PlayerSpawnDir = Vector3.forward;
        public static readonly Vector3 RedCapturePos = new Vector3(0f, GroundY, 24f);
        public static readonly Vector3 JeepSpawnPos = new Vector3(41f, GroundY + 0.35f, -22f);

        // 24 stationary dummy slots across all 6 physics stations
        public static readonly DummySlot[] DummySlots =
        {
            // 1. Main Shooting Lane — Row 1 (10m from booth, Z=-14)
            new DummySlot("Lane 10m", new Vector3(-4.5f, GroundY, -14f), Vector3.back),
            new DummySlot("Lane 10m", new Vector3(-1.5f, GroundY, -14f), Vector3.back),
            new DummySlot("Lane 10m", new Vector3( 1.5f, GroundY, -14f), Vector3.back),
            new DummySlot("Lane 10m", new Vector3( 4.5f, GroundY, -14f), Vector3.back),

            // 1. Main Shooting Lane — Row 2 (20m from booth, Z=-4)
            new DummySlot("Lane 20m", new Vector3(-5.25f, GroundY, -4f), Vector3.back),
            new DummySlot("Lane 20m", new Vector3(-1.75f, GroundY, -4f), Vector3.back),
            new DummySlot("Lane 20m", new Vector3( 1.75f, GroundY, -4f), Vector3.back),
            new DummySlot("Lane 20m", new Vector3( 5.25f, GroundY, -4f), Vector3.back),

            // 1. Main Shooting Lane — Row 3 (35m from booth, Z=+11)
            new DummySlot("Lane 35m", new Vector3(-2.5f, GroundY, 11f), Vector3.back),
            new DummySlot("Lane 35m", new Vector3( 2.5f, GroundY, 11f), Vector3.back),

            // 2. Wall & Corner Bracing Station (West-Near)
            new DummySlot("Wall Brace", new Vector3(-18.8f, GroundY, -8.5f), Vector3.right),
            new DummySlot("Wall Brace", new Vector3(-18.8f, GroundY, -5.5f), Vector3.right),
            new DummySlot("L-Corner",   new Vector3(-18.6f, GroundY,  1.6f), new Vector3(1f, 0f, -1f)),

            // 3. Stairs & Elevated Drop Tower Station (West-Far)
            new DummySlot("Stairs Top", new Vector3(-24.0f, GroundY + 3.0f, 15.3f), Vector3.back),
            new DummySlot("3m Ledge",   new Vector3(-21.6f, GroundY + 3.0f, 18.2f), Vector3.right),
            new DummySlot("6m Drop",    new Vector3(-33.4f, GroundY + 6.0f, 14.8f), Vector3.back),

            // 4. Cover & Wounded Crawl Station (East-Near)
            new DummySlot("Cover", new Vector3(17.5f, GroundY, -9.0f), Vector3.left),
            new DummySlot("Cover", new Vector3(22.5f, GroundY, -3.0f), Vector3.back),
            new DummySlot("Cover", new Vector3(18.5f, GroundY,  3.0f), Vector3.back),

            // 5. Vehicle Ramming Lane (East-Far)
            new DummySlot("Car Lane", new Vector3(41.0f, GroundY, -4.0f), Vector3.back),
            new DummySlot("Car Lane", new Vector3(41.0f, GroundY,  6.0f), Vector3.back),
            new DummySlot("Car Lane", new Vector3(41.0f, GroundY, 16.0f), Vector3.back),

            // 6. Water Pool & Diving Ledge Station (Far East)
            new DummySlot("Water Edge", new Vector3(53.0f, GroundY + 2.0f, -4.5f), Vector3.right),
            new DummySlot("Water Edge", new Vector3(53.0f, GroundY + 2.0f, -1.5f), Vector3.right),
        };

        public static readonly StationTeleportPoint[] TeleportPoints =
        {
            new StationTeleportPoint("Booth",      new Vector3(  0.0f, GroundY + 0.1f, -24.0f), Vector3.forward),
            new StationTeleportPoint("Walls",      new Vector3(-12.5f, GroundY + 0.1f,  -6.5f), Vector3.left),
            new StationTeleportPoint("Stairs",     new Vector3(-24.0f, GroundY + 0.1f,   6.0f), Vector3.forward),
            new StationTeleportPoint("Cover",      new Vector3( 12.5f, GroundY + 0.1f,  -8.0f), Vector3.right),
            new StationTeleportPoint("Car Lane",   new Vector3( 36.5f, GroundY + 0.1f, -23.0f), new Vector3(0.5f, 0f, 1f)),
            new StationTeleportPoint("Water Pool", new Vector3( 48.0f, GroundY + 0.1f,  -3.0f), Vector3.right),
        };

        private static Sprite cachedPreviewSprite;

        public static bool IsVirtualTestMapPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(VirtualMapPath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(System.IO.Path.GetFileName(path), VirtualMapPath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), "DeliriumTestRange", StringComparison.OrdinalIgnoreCase);
        }

        public static MapEntryData CreateMapEntry()
        {
            var entry = new MapEntryData
            {
                sceneName = VirtualMapPath,
                isCustomMap = true,
                hasLoadedMetaData = true,
                nightVersion = false,
                mapEditorMode = SceneConstructor.Mode.Play,
                sourceMod = ModInformation.OfficialContent,
                isNoGameplayScene = false,
                image = GetOrCreatePreviewSprite(),
                metaData = new MapMetaData
                {
                    displayName = DisplayName,
                    visibleInInstantAction = true,
                    suggestedBots = DummySlots.Length + 1,
                    hasGroundVehicles = true,
                    hasNavalVehicles = false,
                    hasAirVehicles = false,
                    hasBuiltInGameMode = false,
                    usesTriggerSystem = false,
                    loadingScreenFOV = 60f
                }
            };
            return entry;
        }

        public static Sprite GetOrCreatePreviewSprite()
        {
            if (cachedPreviewSprite != null) return cachedPreviewSprite;

            const int w = 256;
            const int h = 144;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "DeliriumTestRangePreview",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            UnityEngine.Object.DontDestroyOnLoad(tex);

            Color bgTop = new Color(0.11f, 0.16f, 0.23f, 1f);
            Color bgBot = new Color(0.06f, 0.09f, 0.14f, 1f);
            Color gridCol = new Color(0.20f, 0.31f, 0.43f, 1f);
            Color accent = new Color(0.95f, 0.56f, 0.16f, 1f);
            Color waterCol = new Color(0.16f, 0.48f, 0.72f, 1f);
            Color targetCol = new Color(0.92f, 0.28f, 0.24f, 1f);

            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float ty = (float)y / (h - 1);
                Color baseCol = Color.Lerp(bgBot, bgTop, ty);
                for (int x = 0; x < w; x++)
                {
                    Color c = baseCol;
                    if (x % 16 == 0 || y % 16 == 0)
                        c = Color.Lerp(c, gridCol, 0.55f);
                    // Border
                    if (x < 3 || x >= w - 3 || y < 3 || y >= h - 3)
                        c = accent;
                    // Water pool indicator (right)
                    if (x >= 200 && x <= 236 && y >= 50 && y <= 94)
                        c = Color.Lerp(c, waterCol, 0.75f);
                    // Shooting lane floor (center)
                    if (x >= 104 && x <= 152 && y >= 20 && y <= 124)
                    {
                        c = Color.Lerp(c, new Color(0.26f, 0.30f, 0.35f, 1f), 0.5f);
                        if (y == 44 || y == 68 || y == 92 || y == 112)
                            c = Color.Lerp(c, accent, 0.7f);
                    }
                    // Target reticle at center
                    float dx = x - 128f;
                    float dy = y - 72f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (Mathf.Abs(r - 22f) < 1.6f || Mathf.Abs(r - 11f) < 1.3f)
                        c = targetCol;
                    if ((Mathf.Abs(dx) < 1.2f && Mathf.Abs(dy) < 30f) ||
                        (Mathf.Abs(dy) < 1.2f && Mathf.Abs(dx) < 30f))
                        c = Color.Lerp(c, accent, 0.85f);

                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);

            cachedPreviewSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            cachedPreviewSprite.name = "DeliriumTestRangePreviewSprite";
            UnityEngine.Object.DontDestroyOnLoad(cachedPreviewSprite);
            return cachedPreviewSprite;
        }
    }
}
