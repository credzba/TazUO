// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    ///     Interprets the active server profile's <c>paperdoll</c> section. Every lookup falls back to the
    ///     built-in tables, so a profile without a paperdoll section (or no profile at all) behaves exactly
    ///     like the client did before the section existed.
    /// </summary>
    internal static class PaperdollRules
    {
        // Built-in fallbacks. A profile can override these by name through paperdoll.layerOrders.
        public static readonly Layer[] DefaultOrder =
        [
            Layer.Cloak,
            Layer.Shirt,
            Layer.Pants,
            Layer.Shoes,
            Layer.Legs,
            Layer.Arms,
            Layer.Torso,
            Layer.Tunic,
            Layer.Ring,
            Layer.Bracelet,
            Layer.Face,
            Layer.Gloves,
            Layer.Skirt,
            Layer.Robe,
            Layer.Waist,
            Layer.Neck,
            Layer.Hair,
            Layer.Beard,
            Layer.Earrings,
            Layer.Helmet,
            Layer.OneHanded,
            Layer.TwoHanded,
            Layer.Talisman
        ];

        public static readonly Layer[] QuiverFixOrder =
        [
            Layer.Shirt,
            Layer.Pants,
            Layer.Shoes,
            Layer.Legs,
            Layer.Arms,
            Layer.Torso,
            Layer.Tunic,
            Layer.Ring,
            Layer.Bracelet,
            Layer.Face,
            Layer.Gloves,
            Layer.Skirt,
            Layer.Robe,
            Layer.Cloak,
            Layer.Waist,
            Layer.Neck,
            Layer.Hair,
            Layer.Beard,
            Layer.Earrings,
            Layer.Helmet,
            Layer.OneHanded,
            Layer.TwoHanded,
            Layer.Talisman
        ];

        public static readonly Layer[] ParrotFixOrder =
        [
            Layer.Shirt,
            Layer.Pants,
            Layer.Shoes,
            Layer.Legs,
            Layer.Arms,
            Layer.Torso,
            Layer.Tunic,
            Layer.Cloak,
            Layer.Ring,
            Layer.Bracelet,
            Layer.Face,
            Layer.Gloves,
            Layer.Skirt,
            Layer.Waist,
            Layer.Neck,
            Layer.Hair,
            Layer.Beard,
            Layer.Earrings,
            Layer.Helmet,
            Layer.OneHanded,
            Layer.TwoHanded,
            Layer.Talisman,
            Layer.Robe
        ];

        public const ushort DefaultCloakGraphic = 0xA413;
        public const ushort DefaultRobeOverlayGraphic = 0xA413;
        public const Layer DefaultRobeOverlayLayer = Layer.Robe;

        private static bool _compiled;

        private static Dictionary<string, Layer[]> _orders;
        private static List<Rule> _rules;
        private static Layer _robeOverlayLayer = DefaultRobeOverlayLayer;
        private static ushort _robeOverlayGraphic = DefaultRobeOverlayGraphic;
        private static bool? _legsSkip;
        private static HashSet<ushort> _robeGraphics;
        private static HashSet<ushort> _robeExceptions;
        private static HashSet<ushort> _pantsGraphics;
        private static HashSet<ushort> _tunicGraphics;
        private static HashSet<ushort> _torsoGraphics;

        public static bool HasLayerRules
        {
            get
            {
                Compile();
                return _rules != null && _rules.Count > 0;
            }
        }

        public static Layer RobeOverlayLayer
        {
            get
            {
                Compile();
                return _robeOverlayLayer;
            }
        }

        public static ushort RobeOverlayGraphic
        {
            get
            {
                Compile();
                return _robeOverlayGraphic;
            }
        }

        /// <summary>
        ///     When true the vanilla "Legs hides Pants and Shoes" rule is skipped. Defaults to the built-in
        ///     behavior (skipped while the paperdoll feature is enabled).
        /// </summary>
        public static bool LegsSkip
        {
            get
            {
                Compile();
                return _legsSkip ?? true;
            }
        }

        public static bool IsBuiltInOrder(Layer[] order) =>
            ReferenceEquals(order, DefaultOrder) || ReferenceEquals(order, QuiverFixOrder) || ReferenceEquals(order, ParrotFixOrder);

        public static bool IsCoveringRobe(ushort graphic)
        {
            Compile();
            return _robeGraphics.Contains(graphic);
        }

        public static bool IsRobeException(ushort graphic)
        {
            Compile();
            return _robeExceptions.Contains(graphic);
        }

        public static bool IsCoveringPants(ushort graphic)
        {
            Compile();
            return _pantsGraphics.Contains(graphic);
        }

        public static bool IsTunicSpecial(ushort graphic)
        {
            Compile();
            return _tunicGraphics.Contains(graphic);
        }

        public static bool IsTorsoSpecial(ushort graphic)
        {
            Compile();
            return _torsoGraphics.Contains(graphic);
        }

        /// <summary>
        ///     Resolves an order name to a layer array. Profile <c>layerOrders</c> win; otherwise the built-in
        ///     names <c>default</c>, <c>quiverFix</c> and <c>parrotFix</c> are used. Returns null for unknown names.
        /// </summary>
        public static Layer[] GetOrder(string name)
        {
            Compile();

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (_orders != null && _orders.TryGetValue(name, out Layer[] profileOrder))
            {
                return profileOrder;
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "default": return DefaultOrder;
                case "quiverfix": return QuiverFixOrder;
                case "parrotfix": return ParrotFixOrder;
            }

            return null;
        }

        /// <summary>
        ///     Evaluates the profile's <c>layerOrderRules</c> in order and returns the first matching order.
        ///     When the profile defines rules but none match, the <c>default</c> order is returned. Returns false
        ///     when the profile defines no rules, so the caller can use the built-in cascade.
        /// </summary>
        public static bool TrySelectOrder(Mobile mobile, ItemHold dragged, out Layer[] order)
        {
            Compile();

            order = null;

            if (mobile == null || _rules == null || _rules.Count == 0)
            {
                return false;
            }

            Item cloak = mobile.FindItemByLayer(Layer.Cloak);
            Item robe = mobile.FindItemByLayer(Layer.Robe);
            Item helmet = mobile.FindItemByLayer(Layer.Helmet);

            foreach (Rule rule in _rules)
            {
                if (!rule.Matches(cloak, robe, helmet, dragged))
                {
                    continue;
                }

                Layer[] selected = GetOrder(rule.Order);

                if (selected != null)
                {
                    order = selected;
                    return true;
                }

                Log.Warn($"Server profile paperdoll rule references unknown layer order '{rule.Order}'.");
            }

            order = GetOrder("default");

            if (order == null)
            {
                Log.Warn("Server profile paperdoll defines layerOrderRules but no usable 'default' order; using the built-in cascade.");
            }

            return order != null;
        }

        private static void Compile()
        {
            if (_compiled)
            {
                return;
            }

            _compiled = true;

            // Built-in covered rules. A profile can replace any list by providing it (an empty array means "none").
            _robeGraphics = new HashSet<ushort> { 0x0504 };
            _robeExceptions = new HashSet<ushort> { 0x9985, 0x9986, 0xA412, 0xA2CA, 0xA2CB };
            _pantsGraphics = new HashSet<ushort> { 0x1411 };
            _tunicGraphics = new HashSet<ushort> { 0x0238 };
            _torsoGraphics = new HashSet<ushort> { 0x782A, 0x782B };

            ServerPaperdollInfo paperdoll = ServerProfile.Current?.Paperdoll;

            if (paperdoll == null)
            {
                return;
            }

            if (paperdoll.LayerOrders != null && paperdoll.LayerOrders.Count > 0)
            {
                _orders = new Dictionary<string, Layer[]>(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, List<string>> entry in paperdoll.LayerOrders)
                {
                    if (TryParseOrder(entry.Value, out Layer[] layers))
                    {
                        _orders[entry.Key] = layers;
                    }
                    else
                    {
                        Log.Warn($"Server profile paperdoll layerOrders['{entry.Key}'] contains unknown layers and was ignored.");
                    }
                }
            }

            if (paperdoll.LayerOrderRules != null && paperdoll.LayerOrderRules.Count > 0)
            {
                _rules = new List<Rule>();

                foreach (ServerLayerOrderRule rule in paperdoll.LayerOrderRules)
                {
                    Rule compiled = Rule.Compile(rule);

                    if (compiled != null)
                    {
                        _rules.Add(compiled);
                    }
                }
            }

            if (paperdoll.RobeOverlay != null)
            {
                if (!string.IsNullOrWhiteSpace(paperdoll.RobeOverlay.Layer))
                {
                    if (Enum.TryParse(paperdoll.RobeOverlay.Layer, true, out Layer layer))
                    {
                        _robeOverlayLayer = layer;
                    }
                    else
                    {
                        Log.Warn($"Server profile paperdoll robeOverlay.layer '{paperdoll.RobeOverlay.Layer}' is not a valid layer; using {_robeOverlayLayer}.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(paperdoll.RobeOverlay.Graphic))
                {
                    if (ServerProfile.TryParseHexUShort(paperdoll.RobeOverlay.Graphic, out ushort graphic))
                    {
                        _robeOverlayGraphic = graphic;
                    }
                    else
                    {
                        Log.Warn($"Server profile paperdoll robeOverlay.graphic '{paperdoll.RobeOverlay.Graphic}' is not a graphic id; using 0x{_robeOverlayGraphic:X4}.");
                    }
                }
            }

            if (paperdoll.Covered != null)
            {
                if (paperdoll.Covered.LegsSkip.HasValue)
                {
                    _legsSkip = paperdoll.Covered.LegsSkip.Value;
                }

                _robeGraphics = ParseGraphicSet(paperdoll.Covered.RobeGraphics, _robeGraphics, "covered.robeGraphics");
                _robeExceptions = ParseGraphicSet(paperdoll.Covered.RobeExceptions, _robeExceptions, "covered.robeExceptions");
                _pantsGraphics = ParseGraphicSet(paperdoll.Covered.PantsGraphics, _pantsGraphics, "covered.pantsGraphics");
                _tunicGraphics = ParseGraphicSet(paperdoll.Covered.TunicGraphics, _tunicGraphics, "covered.tunicGraphics");
                _torsoGraphics = ParseGraphicSet(paperdoll.Covered.TorsoGraphics, _torsoGraphics, "covered.torsoGraphics");
            }
        }

        private static bool TryParseOrder(List<string> values, out Layer[] layers)
        {
            layers = null;

            if (values == null || values.Count == 0)
            {
                return false;
            }

            var parsed = new List<Layer>(values.Count);

            foreach (string value in values)
            {
                if (!Enum.TryParse(value, true, out Layer layer))
                {
                    return false;
                }

                parsed.Add(layer);
            }

            layers = parsed.ToArray();
            return true;
        }

        private static HashSet<ushort> ParseGraphicSet(List<string> values, HashSet<ushort> fallback, string section)
        {
            if (values == null)
            {
                return fallback;
            }

            var set = new HashSet<ushort>();

            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (TryParseGraphics(value, set))
                {
                    continue;
                }

                Log.Warn($"Server profile paperdoll {section} entry '{value}' is not a graphic id and was ignored.");
            }

            return set;
        }

        /// <summary>Parses a single graphic id or an inclusive range such as <c>0x0504-0x0510</c>.</summary>
        private static bool TryParseGraphics(string value, HashSet<ushort> set)
        {
            int dash = value.IndexOf('-', 1);

            if (dash < 0)
            {
                if (!ServerProfile.TryParseHexUShort(value, out ushort single))
                {
                    return false;
                }

                set.Add(single);
                return true;
            }

            if (!ServerProfile.TryParseHexUShort(value.Substring(0, dash), out ushort low)
                || !ServerProfile.TryParseHexUShort(value.Substring(dash + 1), out ushort high)
                || low > high)
            {
                return false;
            }

            for (int graphic = low; graphic <= high; graphic++)
            {
                set.Add((ushort)graphic);
            }

            return true;
        }

        private sealed class Rule
        {
            public string Order;
            public bool? CloakEquipped;
            public ushort? CloakGraphic;
            public bool? CloakIsContainer;
            public HashSet<ushort> RobeGraphics;
            public bool? DraggedIsContainer;
            public HashSet<ushort> DraggedGraphics;
            public HashSet<ushort> HelmetGraphics;

            public static Rule Compile(ServerLayerOrderRule source)
            {
                if (string.IsNullOrWhiteSpace(source.Order))
                {
                    Log.Warn("Server profile paperdoll layerOrderRules entry has no 'order' and was ignored.");
                    return null;
                }

                var rule = new Rule { Order = source.Order };

                if (!string.IsNullOrWhiteSpace(source.CloakGraphic))
                {
                    if (ServerProfile.TryParseHexUShort(source.CloakGraphic, out ushort cloakGraphic))
                    {
                        rule.CloakGraphic = cloakGraphic;
                    }
                    else
                    {
                        Log.Warn($"Server profile paperdoll layerOrderRules '{source.Order}' cloakGraphic is not a graphic id.");
                        return null;
                    }
                }

                rule.CloakEquipped = source.CloakEquipped;
                rule.CloakIsContainer = source.EquippedCloakIsContainer;
                rule.DraggedIsContainer = source.DraggedIsContainer;
                rule.RobeGraphics = ParseGraphicSet(source.RobeGraphics, null, $"layerOrderRules '{source.Order}' robeGraphics");
                rule.DraggedGraphics = ParseGraphicSet(source.DraggedGraphics, null, $"layerOrderRules '{source.Order}' draggedGraphics");
                rule.HelmetGraphics = ParseGraphicSet(source.HelmetGraphics, null, $"layerOrderRules '{source.Order}' helmetGraphics");

                return rule;
            }

            public bool Matches(Item cloak, Item robe, Item helmet, ItemHold dragged)
            {
                if (CloakEquipped.HasValue && (cloak != null) != CloakEquipped.Value)
                {
                    return false;
                }

                if (CloakGraphic.HasValue && (cloak == null || cloak.Graphic != CloakGraphic.Value))
                {
                    return false;
                }

                if (CloakIsContainer.HasValue && (cloak == null || cloak.ItemData.IsContainer != CloakIsContainer.Value))
                {
                    return false;
                }

                if (RobeGraphics != null && (robe == null || !RobeGraphics.Contains(robe.Graphic)))
                {
                    return false;
                }

                if (HelmetGraphics != null && (helmet == null || !HelmetGraphics.Contains(helmet.Graphic)))
                {
                    return false;
                }

                if (DraggedGraphics != null && (dragged == null || !DraggedGraphics.Contains(dragged.Graphic)))
                {
                    return false;
                }

                if (DraggedIsContainer.HasValue && (dragged == null || dragged.ItemData.IsContainer != DraggedIsContainer.Value))
                {
                    return false;
                }

                return true;
            }
        }
    }
}
