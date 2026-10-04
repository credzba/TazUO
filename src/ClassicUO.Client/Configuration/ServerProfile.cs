// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.Configuration
{
    /// <summary>
    /// Data-driven description of the server-specific customizations shipped in an override
    /// directory (server pack). Loaded once at startup from &lt;override_directory&gt;/server_profile.json.
    /// All client implementations consume the same schema; sections a client does not implement
    /// are ignored with a warning.
    /// </summary>
    public class ServerProfile
    {
        private static readonly HashSet<string> _knownRootProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "profileVersion", "name", "assetPaths", "features", "spellSchools",
            "containers", "containerGraphics", "mounts", "chairs", "paperdoll", "cursor", "reagents", "strings"
        };

        public static ServerProfile Current { get; private set; }

        [JsonPropertyName("profileVersion")] public int ProfileVersion { get; set; } = 1;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("assetPaths")] public string[] AssetPaths { get; set; }
        [JsonPropertyName("features")] public ServerProfileFeatures Features { get; set; } = new ServerProfileFeatures();
        [JsonPropertyName("spellSchools")] public List<ServerSpellSchool> SpellSchools { get; set; } = new List<ServerSpellSchool>();
        [JsonPropertyName("containers")] public List<ServerContainerInfo> Containers { get; set; } = new List<ServerContainerInfo>();
        [JsonPropertyName("containerGraphics")] public Dictionary<string, string> ContainerGraphics { get; set; }
        [JsonPropertyName("mounts")] public ServerMountsInfo Mounts { get; set; }
        [JsonPropertyName("chairs")] public ServerChairsInfo Chairs { get; set; }
        [JsonPropertyName("paperdoll")] public ServerPaperdollInfo Paperdoll { get; set; }
        [JsonPropertyName("cursor")] public ServerCursorInfo Cursor { get; set; }
        [JsonPropertyName("reagents")] public List<string> Reagents { get; set; }
        [JsonPropertyName("strings")] public ServerStringsInfo Strings { get; set; }

        public static bool IsActive => Current != null;
        public static bool PaperdollEnabled => Current?.Features?.Paperdoll == true;
        public static bool MountsEnabled => Current?.Features?.Mounts == true;
        public static bool ContainersEnabled => Current?.Features?.Containers == true;
        public static bool ChairsEnabled => Current?.Features?.Chairs == true;
        public static bool SpellSchoolsEnabled => Current?.SpellSchools != null && Current.SpellSchools.Count > 0;
        public static bool TazuoIdentifierEnabled => Current?.Features?.Network?.TazuoIdentifier == true;
        public static bool DisableFeaturesEnabled => Current?.Features?.Network?.DisableFeatures == true;
        public static bool CustomOpenContainerEnabled => Current?.Features?.Network?.CustomOpenContainer == true;
        public static bool LoginBrandingEnabled => Current?.Features?.Login?.Branding == true;
        public static bool ClientVerifierEnabled => Current?.Features?.Network?.ClientVerifier != null;

        public ServerSpellSchool FindSpellSchool(string type)
        {
            if (SpellSchools == null || string.IsNullOrEmpty(type))
            {
                return null;
            }

            foreach (ServerSpellSchool school in SpellSchools)
            {
                if (string.Equals(school.Type, type, StringComparison.OrdinalIgnoreCase))
                {
                    return school;
                }
            }

            return null;
        }

        public static string[] ResolveAssetPaths(string overrideDirectory, string[] configuredPaths)
        {
            if (string.IsNullOrEmpty(overrideDirectory))
            {
                return null;
            }

            if (configuredPaths == null || configuredPaths.Length == 0)
            {
                return new[] { overrideDirectory };
            }

            var resolved = new List<string>(configuredPaths.Length);

            foreach (string path in configuredPaths)
            {
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                resolved.Add(Path.IsPathRooted(path) ? path : Path.Combine(overrideDirectory, path));
            }

            if (resolved.Count == 0)
            {
                resolved.Add(overrideDirectory);
            }

            return resolved.ToArray();
        }

        public static bool TryParseHexUShort(string value, out ushort result)
        {
            result = 0;

            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            value = value.Trim();

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(2);
            }

            try
            {
                result = Convert.ToUInt16(value, 16);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ushort ParseHexOr(string value, ushort fallback)
        {
            return TryParseHexUShort(value, out ushort parsed) ? parsed : fallback;
        }

        private static Dictionary<ushort, ushort> _containerGumpCache;

        /// <summary>
        ///     Resolves a container graphic to the gump graphic the pack wants it drawn with.
        ///     Returns false when the pack declares no mapping for the graphic, so callers can use their built-in table.
        /// </summary>
        public static bool TryGetContainerGumpOverride(ushort graphic, out ushort gumpGraphic)
        {
            gumpGraphic = 0;

            Dictionary<string, string> map = Current?.ContainerGraphics;

            if (map == null || map.Count == 0)
            {
                return false;
            }

            if (_containerGumpCache == null)
            {
                _containerGumpCache = new Dictionary<ushort, ushort>();

                foreach (KeyValuePair<string, string> entry in map)
                {
                    if (TryParseHexUShort(entry.Key, out ushort from) && TryParseHexUShort(entry.Value, out ushort to))
                    {
                        _containerGumpCache[from] = to;
                    }
                    else
                    {
                        Log.Warn($"Server profile containerGraphics entry '{entry.Key}': '{entry.Value}' is not a graphic id and was ignored.");
                    }
                }
            }

            return _containerGumpCache.TryGetValue(graphic, out gumpGraphic);
        }

        public static ServerProfile Load(string overrideDirectory)
        {
            Current = null;
            _containerGumpCache = null;

            if (string.IsNullOrEmpty(overrideDirectory))
            {
                return null;
            }

            string path = Path.Combine(overrideDirectory, "server_profile.json");

            if (!File.Exists(path))
            {
                Log.Trace($"No server profile found at '{path}', using vanilla behavior.");
                return null;
            }

            try
            {
                string json = File.ReadAllText(path);

                WarnAboutUnknownSections(json, path);

                ServerProfile profile = JsonSerializer.Deserialize(json, ServerProfileJsonContext.Default.ServerProfile);

                if (profile != null)
                {
                    if (profile.ProfileVersion > 1)
                    {
                        Log.Warn($"Server profile '{path}' uses profileVersion {profile.ProfileVersion}; this client supports version 1. Unknown sections will be ignored.");
                    }

                    Current = profile;
                    Log.Trace($"Loaded server profile '{profile.Name}' ({path}).");
                }

                return profile;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load server profile '{path}': {ex}");
                return null;
            }
        }

        private static void WarnAboutUnknownSections(string json, string path)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (!_knownRootProperties.Contains(property.Name))
                        {
                            Log.Warn($"Server profile '{path}' contains unknown section '{property.Name}' which will be ignored.");
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Log.Warn($"Server profile '{path}' is not valid JSON: {ex.Message}");
            }
        }
    }

    public class ServerProfileFeatures
    {
        [JsonPropertyName("network")] public ServerNetworkFeatures Network { get; set; }
        [JsonPropertyName("login")] public ServerLoginFeatures Login { get; set; }
        [JsonPropertyName("paperdoll")] public bool Paperdoll { get; set; }
        [JsonPropertyName("mounts")] public bool Mounts { get; set; }
        [JsonPropertyName("containers")] public bool Containers { get; set; }
        [JsonPropertyName("chairs")] public bool Chairs { get; set; }
    }

    public class ServerNetworkFeatures
    {
        [JsonPropertyName("tazuoIdentifier")] public bool TazuoIdentifier { get; set; }
        [JsonPropertyName("disableFeatures")] public bool DisableFeatures { get; set; }
        [JsonPropertyName("customOpenContainer")] public bool CustomOpenContainer { get; set; }
        [JsonPropertyName("clientVerifier")] public ServerClientVerifier ClientVerifier { get; set; }
    }

    public class ServerClientVerifier
    {
        [JsonPropertyName("packet")] public string Packet { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
    }

    public class ServerLoginFeatures
    {
        [JsonPropertyName("branding")] public bool Branding { get; set; }
        [JsonPropertyName("shardLabel")] public string ShardLabel { get; set; }
    }

    public class ServerSpellSchool
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("idBase")] public int IdBase { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("maxSpells")] public int MaxSpells { get; set; }
        [JsonPropertyName("bookGraphics")] public List<string> BookGraphics { get; set; } = new List<string>();
        [JsonPropertyName("bookGump")] public string BookGump { get; set; }
        [JsonPropertyName("minimizedGump")] public string MinimizedGump { get; set; }
        [JsonPropertyName("iconStart")] public string IconStart { get; set; }
        [JsonPropertyName("bookClilocBase")] public int BookClilocBase { get; set; }
        [JsonPropertyName("buttonClilocBase")] public int ButtonClilocBase { get; set; }
        [JsonPropertyName("macroGroup")] public int MacroGroup { get; set; }
        [JsonPropertyName("tithing")] public bool Tithing { get; set; }
        [JsonPropertyName("spells")] public List<ServerSpellInfo> Spells { get; set; } = new List<ServerSpellInfo>();
    }

    public class ServerSpellInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("words")] public string Words { get; set; } = string.Empty;
        [JsonPropertyName("icon")] public string Icon { get; set; }
        [JsonPropertyName("smallIcon")] public string SmallIcon { get; set; }
        [JsonPropertyName("mana")] public int Mana { get; set; }
        [JsonPropertyName("minSkill")] public int MinSkill { get; set; }
        [JsonPropertyName("tithing")] public int Tithing { get; set; }
        [JsonPropertyName("target")] public string Target { get; set; }
        [JsonPropertyName("reagents")] public List<string> Reagents { get; set; } = new List<string>();
    }

    public class ServerContainerInfo
    {
        [JsonPropertyName("graphic")] public string Graphic { get; set; }
        [JsonPropertyName("openSound")] public int OpenSound { get; set; }
        [JsonPropertyName("closeSound")] public int CloseSound { get; set; }
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("iconizedGraphic")] public string IconizedGraphic { get; set; }
        [JsonPropertyName("minimizerX")] public int MinimizerX { get; set; }
        [JsonPropertyName("minimizerY")] public int MinimizerY { get; set; }
    }

    public class ServerMountsInfo
    {
        [JsonPropertyName("defFile")] public string DefFile { get; set; } = "Mounts.def";
        [JsonPropertyName("columns")] public string Columns { get; set; } = "body,anim,offsetY";
        [JsonPropertyName("entries")] public List<ServerMountInfo> Entries { get; set; } = new List<ServerMountInfo>();
    }

    public class ServerMountInfo
    {
        [JsonPropertyName("body")] public string Body { get; set; }
        [JsonPropertyName("anim")] public string Anim { get; set; }
        [JsonPropertyName("offsetY")] public int OffsetY { get; set; }
        [JsonPropertyName("graphic")] public string Graphic { get; set; }
    }

    public class ServerChairsInfo
    {
        [JsonPropertyName("file")] public string File { get; set; } = "chair.txt";
    }

    public class ServerPaperdollInfo
    {
        [JsonPropertyName("layerOrders")] public Dictionary<string, List<string>> LayerOrders { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("layerOrderRules")] public List<ServerLayerOrderRule> LayerOrderRules { get; set; } = new List<ServerLayerOrderRule>();
        [JsonPropertyName("robeOverlay")] public ServerRobeOverlay RobeOverlay { get; set; }
        [JsonPropertyName("covered")] public ServerCoveredRules Covered { get; set; }
        [JsonPropertyName("renderOrder")] public ServerRenderOrderInfo RenderOrder { get; set; }
        [JsonPropertyName("backpackGraphics")] public Dictionary<string, string> BackpackGraphics { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Named layer orders and ordered selection rules used by the in-world render order
    ///     (as opposed to the paperdoll gump). Same shape as the paperdoll rules.
    /// </summary>
    public class ServerRenderOrderInfo
    {
        [JsonPropertyName("layerOrders")] public Dictionary<string, List<string>> LayerOrders { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("layerOrderRules")] public List<ServerLayerOrderRule> LayerOrderRules { get; set; } = new List<ServerLayerOrderRule>();
    }

    public class ServerLayerOrderRule
    {
        [JsonPropertyName("order")] public string Order { get; set; }
        [JsonPropertyName("cloakEquipped")] public bool? CloakEquipped { get; set; }
        [JsonPropertyName("cloakGraphic")] public string CloakGraphic { get; set; }
        [JsonPropertyName("cloakGraphics")] public List<string> CloakGraphics { get; set; }
        [JsonPropertyName("equippedCloakIsContainer")] public bool? EquippedCloakIsContainer { get; set; }
        [JsonPropertyName("robeGraphics")] public List<string> RobeGraphics { get; set; }
        [JsonPropertyName("draggedGraphics")] public List<string> DraggedGraphics { get; set; }
        [JsonPropertyName("draggedIsContainer")] public bool? DraggedIsContainer { get; set; }
        [JsonPropertyName("draggedLayer")] public string DraggedLayer { get; set; }
        [JsonPropertyName("helmetGraphics")] public List<string> HelmetGraphics { get; set; }
    }

    public class ServerRobeOverlay
    {
        [JsonPropertyName("layer")] public string Layer { get; set; }
        [JsonPropertyName("graphic")] public string Graphic { get; set; }
    }

    public class ServerCoveredRules
    {
        [JsonPropertyName("legsSkip")] public bool? LegsSkip { get; set; }
        [JsonPropertyName("robeGraphics")] public List<string> RobeGraphics { get; set; }
        [JsonPropertyName("robeExceptions")] public List<string> RobeExceptions { get; set; }
        [JsonPropertyName("pantsGraphics")] public List<string> PantsGraphics { get; set; }
        [JsonPropertyName("tunicGraphics")] public List<string> TunicGraphics { get; set; }
        [JsonPropertyName("torsoGraphics")] public List<string> TorsoGraphics { get; set; }
        [JsonPropertyName("helmetBypassGraphics")] public List<string> HelmetBypassGraphics { get; set; }
    }

    public class ServerCursorInfo
    {
        [JsonPropertyName("artOnlyGraphics")] public List<string> ArtOnlyGraphics { get; set; }
    }

    public class ServerStringsInfo
    {
        [JsonPropertyName("loginVersion")] public string LoginVersion { get; set; }
        [JsonPropertyName("urls")] public Dictionary<string, string> Urls { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(ServerProfile), GenerationMode = JsonSourceGenerationMode.Metadata)]
    sealed partial class ServerProfileJsonContext : JsonSerializerContext
    {
    }
}
