using System;
using System.IO;
using MapEditor;
using UnityEngine;

namespace DeliriumRavenfield.TestArena
{
    /// <summary>
    /// Dedicated empty virtual map ("Delirium Playroom Mirror") for 1:1 Playroom <-> Ravenfield lockstep sync.
    /// Zero extra stations, zero extra AI bots: 1 Player + 1 Red synced bot on a clean flat origin at Y=10.0.
    /// Stage geometry (room, bigstairs, rooftop, lowwall, rail, street, open, etc.) is dynamically spawned from Playroom.
    /// </summary>
    public static class PlayroomMirrorDefinitions
    {
        public const string VirtualMapPath = "DeliriumPlayroomMirror.rfld";
        public const string DisplayName = "Delirium Playroom Mirror";
        public const int TerrainSize = 256;
        public const float OriginY = 10.0f;

        public static readonly Vector3 MirrorOrigin = new Vector3(0f, OriginY, 0f);
        public static readonly Vector3 PlayerSpawnPos = new Vector3(0f, OriginY + 0.1f, -4.5f);
        public static readonly Vector3 PlayerSpawnDir = Vector3.forward;
        public static readonly Vector3 BotSpawnPos = new Vector3(0f, OriginY + 0.05f, 0f);
        public static readonly Vector3 BotSpawnDir = Vector3.back;

        private static Sprite cachedPreviewSprite;

        public static bool IsVirtualMirrorMapPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(VirtualMapPath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileName(path), VirtualMapPath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileNameWithoutExtension(path), "DeliriumPlayroomMirror", StringComparison.OrdinalIgnoreCase);
        }

        public static MapEntryData CreateMapEntry()
        {
            return new MapEntryData
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
                    suggestedBots = 2,
                    hasGroundVehicles = false,
                    hasNavalVehicles = false,
                    hasAirVehicles = false,
                    hasBuiltInGameMode = false,
                    usesTriggerSystem = false,
                    loadingScreenFOV = 60f
                }
            };
        }

        public static Sprite GetOrCreatePreviewSprite()
        {
            if (cachedPreviewSprite != null) return cachedPreviewSprite;

            const int w = 256;
            const int h = 144;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "DeliriumPlayroomMirrorPreview",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            UnityEngine.Object.DontDestroyOnLoad(tex);

            Color bgTop = new Color(0.08f, 0.14f, 0.24f, 1f);
            Color bgBot = new Color(0.14f, 0.26f, 0.44f, 1f);
            Color gridCol = new Color(0.24f, 0.44f, 0.72f, 1f);
            Color accent = new Color(0.18f, 0.85f, 0.98f, 1f);
            Color brick = new Color(0.84f, 0.42f, 0.30f, 1f);

            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float ty = (float)y / (h - 1);
                Color baseCol = Color.Lerp(bgBot, bgTop, ty);
                for (int x = 0; x < w; x++)
                {
                    Color c = baseCol;
                    if (y < 76 && (x % 14 == 0 || y % 14 == 0))
                        c = Color.Lerp(c, gridCol, 0.55f);
                    if (x < 3 || x >= w - 3 || y < 3 || y >= h - 3)
                        c = accent;
                    if (y >= 56 && y <= 84 && x >= 32 && x <= 224)
                        c = Color.Lerp(c, brick, 0.75f);
                    pixels[y * w + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);

            cachedPreviewSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            cachedPreviewSprite.name = "DeliriumPlayroomMirrorPreviewSprite";
            UnityEngine.Object.DontDestroyOnLoad(cachedPreviewSprite);
            return cachedPreviewSprite;
        }
    }
}
