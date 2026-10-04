using ClassicUO.Configuration;
using ClassicUO.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Managers
{
    [Serializable]
    internal record struct TileLocation
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Map { get; set; }

        public TileLocation(int x, int y, int map)
        {
            X = x;
            Y = y;
            Map = map;
        }
    }

    [Serializable]
    internal struct TileMarkerEntry
    {
        public TileLocation Location { get; set; }
        public ushort Hue { get; set; }
        public string Label { get; set; }
    }

    internal struct TileMarkerData
    {
        public ushort Hue;
        public string Label;
    }

    /// <summary>Server-scoped save shape, matching TazUO's <c>TileMarkerConfig</c>.</summary>
    internal sealed class TileMarkerConfig
    {
        public List<TileMarkerEntry> Markers { get; set; } = new List<TileMarkerEntry>();
    }

    [JsonSerializable(typeof(List<TileMarkerEntry>))]
    [JsonSerializable(typeof(TileMarkerConfig))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class TileMarkerJsonContext : JsonSerializerContext
    {
    }

    internal class TileMarkerManager
    {
        public static TileMarkerManager Instance
        {
            get
            {
                if (field == null)
                    field = new TileMarkerManager();
                return field;
            }
        }

        // Read from Chunk.Load while markers are added/removed, and enumerated during save;
        // a plain Dictionary both races and invalidates its enumerator when mutated mid-loop.
        private ConcurrentDictionary<TileLocation, TileMarkerData> markedTiles = new ConcurrentDictionary<TileLocation, TileMarkerData>();

        private TileMarkerManager()
        {
            if (!string.IsNullOrEmpty(ProfileManager.CurrentProfile?.ServerName))
            {
                Load();
            }
        }

        private string SavePath => Path.Combine(CUOEnviroment.ExecutablePath, "Data", FileSystemHelper.RemoveInvalidChars(ServerName), "TileMarkers.json");

        /// <summary>Where TazUO (newer) used to keep the old profile-scoped save: the char profile folder.</summary>
        private static string CharProfileSavePath => ProfileManager.ProfilePath != null ? Path.Combine(ProfileManager.ProfilePath, "TileMarkers.json") : null;

        /// <summary>Where this client used to keep the old profile-scoped save: the server folder above the char profile.</summary>
        private static string LegacyProfileSavePath => ProfileManager.ProfilePath != null ? Path.Combine(Path.GetDirectoryName(ProfileManager.ProfilePath), "TileMarkers.json") : null;

        private static string ServerName
        {
            get
            {
                string server = ProfileManager.CurrentProfile?.ServerName;

                if (string.IsNullOrEmpty(server))
                    server = World.ServerName;

                if (string.IsNullOrEmpty(server))
                    server = "Server";

                return server;
            }
        }

        public void AddTile(int x, int y, int map, ushort hue)
        {
            AddTile(x, y, map, hue, null);
        }

        public void AddTile(int x, int y, int map, ushort hue, string label)
        {
            var location = new TileLocation(x, y, map);
            markedTiles[location] = new TileMarkerData { Hue = hue, Label = label };

            // Update all live tiles at this location
            UpdateLiveTilesAt(x, y, map, hue);
        }

        public void RemoveTile(int x, int y, int map)
        {
            var location = new TileLocation(x, y, map);

            if (markedTiles.TryRemove(location, out _))
            {
                // Reset hue to 0 for all live tiles at this location
                UpdateLiveTilesAt(x, y, map, 0);
            }
        }

        public bool IsTileMarked(int x, int y, int map, out ushort hue)
        {
            if (markedTiles.TryGetValue(new TileLocation(x, y, map), out TileMarkerData data))
            {
                hue = data.Hue;
                return true;
            }

            hue = 0;
            return false;
        }

        public IEnumerable<KeyValuePair<TileLocation, TileMarkerData>> GetMarkedTilesForMap(int mapIndex)
        {
            foreach (var kvp in markedTiles)
            {
                if (kvp.Key.Map == mapIndex)
                    yield return kvp;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));

                var config = new TileMarkerConfig
                {
                    Markers = markedTiles.Select(kvp => new TileMarkerEntry { Location = kvp.Key, Hue = kvp.Value.Hue, Label = kvp.Value.Label }).ToList()
                };

                string json = JsonSerializer.Serialize(config, TileMarkerJsonContext.Default.TileMarkerConfig);
                File.WriteAllText(SavePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save marked tile data: {ex.Message}");
            }
        }

        private void Load()
        {
            try
            {
                // Shared with newer TazUO: Data/<server>/TileMarkers.json
                if (TryLoadConfigFile(SavePath))
                    return;

                // One-time migration from this client's old profile-scoped save.
                if (TryLoadArrayFile(LegacyProfileSavePath))
                {
                    Save();
                    File.Delete(LegacyProfileSavePath);
                    return;
                }

                // Read-only fallback for the char-profile save that newer TazUO migrates from.
                if (TryLoadArrayFile(CharProfileSavePath))
                {
                    Save();
                    return;
                }

                // Try to migrate from old binary format
                MigrateFromLegacyFormat();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load marked tile data: {ex.Message}");
                markedTiles = new ConcurrentDictionary<TileLocation, TileMarkerData>();
            }
        }

        private bool TryLoadConfigFile(string path)
        {
            if (path == null || !File.Exists(path))
                return false;

            string json = File.ReadAllText(path);
            TileMarkerConfig config = JsonSerializer.Deserialize(json, TileMarkerJsonContext.Default.TileMarkerConfig);

            markedTiles = new ConcurrentDictionary<TileLocation, TileMarkerData>(
                (config?.Markers ?? new List<TileMarkerEntry>()).ToDictionary(e => e.Location, e => new TileMarkerData { Hue = e.Hue, Label = e.Label })
            );

            return true;
        }

        private bool TryLoadArrayFile(string path)
        {
            if (path == null || !File.Exists(path))
                return false;

            string json = File.ReadAllText(path);
            List<TileMarkerEntry> entries = JsonSerializer.Deserialize(json, TileMarkerJsonContext.Default.ListTileMarkerEntry);

            if (entries == null)
                return false;

            markedTiles = new ConcurrentDictionary<TileLocation, TileMarkerData>(
                entries.ToDictionary(e => e.Location, e => new TileMarkerData { Hue = e.Hue, Label = e.Label })
            );

            return true;
        }

        private void MigrateFromLegacyFormat()
        {
            string legacyPath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles", "TileMarkers.bin");
            if (File.Exists(legacyPath))
            {
                try
                {
                    using (FileStream fs = File.OpenRead(legacyPath))
                    {
                        var bf = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                        var oldData = (Dictionary<string, ushort>)bf.Deserialize(fs);

                        foreach (var kvp in oldData)
                        {
                            // Parse old string key format "x.y.map"
                            var parts = kvp.Key.Split('.');
                            if (parts.Length == 3 &&
                                int.TryParse(parts[0], out int x) &&
                                int.TryParse(parts[1], out int y) &&
                                int.TryParse(parts[2], out int map))
                            {
                                markedTiles[new TileLocation(x, y, map)] = new TileMarkerData { Hue = kvp.Value };
                            }
                        }

                        // Save in new format and delete old file
                        Save();
                        File.Delete(legacyPath);
                    }
                }
                catch
                {
                    // Migration failed, start fresh
                }
            }
        }

        private void UpdateLiveTilesAt(int x, int y, int map, ushort hue)
        {
            if (World.Map == null || World.Map.Index != map) return;

            var chunk = World.Map.GetChunk(x, y, false);
            if (chunk == null) return;

            // Get all tiles at this location and update their hue
            for (GameObject obj = chunk.GetHeadObject(x % 8, y % 8); obj != null; obj = obj.TNext)
            {
                // Update both Land and Static tiles
                if (obj is Land || obj is Static)
                {
                    obj.Hue = hue;
                }
            }
        }
    }
}
