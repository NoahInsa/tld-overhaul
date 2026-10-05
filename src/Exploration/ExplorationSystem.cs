using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using TLDOverhaul.Forge;
using TLDOverhaul.Skiing;
using TLDOverhaul.Skills;
using UnityEngine;

namespace TLDOverhaul.Exploration
{
    /// <summary>
    /// "Every region needs to pull the player toward it with something irreplaceable." Mostly data, driven by one table:
    ///  * skill-book volumes, schematics and one-of-a-kind capability items are scattered across the map (placed into containers
    ///    deterministically, once each, with a pity timer so each one DOES turn up if you search the region);
    ///  * metal salvaged in a region yields that region's own materials (dam copper and heavy steel, crash-site aluminium and
    ///    cable, farm cast iron and chain, mountaineering hardware) which specific recipes require;
    ///  * hides taken from animals are better in harsher climates;
    ///  * the two forges differ (see the Forge system: small coastal workshop vs. industrial works).
    /// </summary>
    public sealed class ExplorationSystem : OverhaulSystem, ISaveSection, IRegionalMaterials
    {
        public static ExplorationSystem Instance { get; private set; }
        public override string Name { get { return "Exploration"; } }
        string ISaveSection.Key { get { return "exploration"; } }

        // ------------------------------------------------------------------------------------------- data

        private sealed class Unique
        {
            public string Key, Region, Label, Carrier;       // Carrier = vanilla prefab name used as the physical stand-in
            public bool Real;                                // true: the carrier IS the item (no conversion on pickup)
            public int MinSeen;
            public Action<ExplorationSystem> Grant;
        }

        private sealed class Schematic { public string Key, Region, Label; public string[] Keywords; public int MinSeen; }

        private static readonly Dictionary<string, string[]> DefaultBooks = new Dictionary<string, string[]>
        {
            { "mystery_lake",        new[] { "Carpentry:1", "Mechanics:1", "Foraging:1", "FirstAid:1", "Skiing:1" } },
            { "coastal_highway",     new[] { "Cooking:2", "IceFishing:2", "Mechanics:2", "Blacksmithing:1" } },
            { "desolation_point",    new[] { "Gunsmithing:2", "Tanning:1", "Carpentry:2" } },
            { "forlorn_muskeg",      new[] { "Blacksmithing:2", "Carpentry:3", "Tanning:2" } },
            { "mountain_town",       new[] { "Cooking:3", "FirstAid:2", "Foraging:2", "Revolver:2" } },
            { "pleasant_valley",     new[] { "Mending:3", "Mending:5", "Cooking:4", "Tanning:3" } },     // Mending:5 = the master tailoring guide
            { "hushed_river_valley", new[] { "Foraging:3", "Skiing:2", "Archery:2", "IceFishing:3" } },
            { "timberwolf_mountain", new[] { "Blacksmithing:3", "Blacksmithing:4", "Archery:3", "Mechanics:3" } }, // the advanced blacksmithing manual
            { "ash_canyon",          new[] { "Skiing:4", "Mechanics:4", "FirstAid:3" } },
            { "bleak_inlet",         new[] { "Gunsmithing:3", "Carpentry:4", "Mechanics:5" } },
            { "broken_railroad",     new[] { "Rifle:3", "Revolver:3", "Firestarting:2" } },
            { "blackrock",           new[] { "Rifle:4", "Gunsmithing:4", "FirstAid:4", "Cooking:5" } },
        };

        private static readonly Dictionary<string, string[]> RegionMaterials = new Dictionary<string, string[]>
        {
            { "mystery_lake",        new[] { "copper", "heavy_steel", "electrical_parts" } },          // the dam
            { "timberwolf_mountain", new[] { "aircraft_aluminum", "aviation_cable", "hs_fasteners" } }, // crashed plane
            { "pleasant_valley",     new[] { "cast_iron", "heavy_chain", "equipment_parts" } },        // farmstead
            { "ash_canyon",          new[] { "climbing_hardware" } },                                  // carabiners, pitons, cable, crampon steel
        };

        private static readonly Dictionary<string, string> MaterialNames = new Dictionary<string, string>
        {
            { "copper", "industrial copper" }, { "heavy_steel", "heavy-gauge steel" }, { "electrical_parts", "electrical components" },
            { "aircraft_aluminum", "aircraft aluminium" }, { "aviation_cable", "aviation cable" }, { "hs_fasteners", "high-strength fasteners" },
            { "cast_iron", "agricultural cast iron" }, { "heavy_chain", "heavy chain" }, { "equipment_parts", "equipment parts" },
            { "climbing_hardware", "mountaineering hardware" },
        };

        private readonly List<Schematic> _schematicDefs = new List<Schematic>
        {
            new Schematic { Key = "hollow_point", Region = "mountain_town", Label = "Hollow-point swaging schematic", Keywords = new string[0], MinSeen = 4 },
            new Schematic { Key = "expedition_parka", Region = "ash_canyon", Label = "Expedition parka schematic (buried mountaineering cache)", Keywords = new[] { "parka" }, MinSeen = 5 },
            new Schematic { Key = "snow_pants", Region = "timberwolf_mountain", Label = "Snow pants schematic", Keywords = new[] { "insulatedpants", "snowpants" }, MinSeen = 6 },
            new Schematic { Key = "surgical_manual", Region = "hushed_river_valley", Label = "Surgical manual (remote clinic)", Keywords = new[] { "surgical", "suture", "emergencystim" }, MinSeen = 6 },
        };

        private readonly List<Unique> _uniques = new List<Unique>();

        // ------------------------------------------------------------------------------------------- state (persisted)

        private readonly Dictionary<string, int> _materials = new Dictionary<string, int>();
        private readonly HashSet<string> _placed = new HashSet<string>();            // "book:Skill:vol", "unique:key", "schem:key"
        private readonly HashSet<string> _schematics = new HashSet<string>();        // schematics the player has found
        private readonly Dictionary<string, int> _seen = new Dictionary<string, int>(); // containers stocked per region
        private readonly HashSet<string> _processed = new HashSet<string>();          // container guids already considered
        private readonly Dictionary<string, string> _carriers = new Dictionary<string, string>(); // carrier item guid -> "unique:key"/"schem:key"
        private Dictionary<string, string[]> _books;

        private readonly List<GearItem> _pendingConvert = new List<GearItem>();
        private Setting<float> _salvageChance;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(Container_InstantiateContents);
                yield return typeof(Inventory_AddGear_Carrier);
                yield return typeof(GearItem_CarrierName);
                yield return typeof(PanelBodyHarvest_HideQuality);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _salvageChance = Cfg.F(Name, "RegionalMaterialChance", 0.7f, "Chance a metal salvage in a special region also yields that region's own material.");
            LoadBooks();

            _uniques.Add(new Unique { Key = "xc_skis", Region = "mystery_lake", Label = "Cross-country skis (lakeside cabin)", Carrier = "Stick", MinSeen = 3, Grant = e => { if (SkiingSystem.Instance != null) SkiingSystem.Instance.OwnsXc = true; } });
            _uniques.Add(new Unique { Key = "at_skis", Region = "ash_canyon", Label = "AT touring skis with skins (mountaineering cache)", Carrier = "Stick", MinSeen = 8, Grant = e => { if (SkiingSystem.Instance != null) SkiingSystem.Instance.OwnsAt = true; } });
            _uniques.Add(new Unique { Key = "dam_toolkit", Region = "mystery_lake", Label = "Specialized tool kit (the dam)", Carrier = "HighQualityTools", Real = true, MinSeen = 6 });
            _uniques.Add(new Unique { Key = "ice_crampons", Region = "hushed_river_valley", Label = "Mountaineering crampons", Carrier = "Crampons", Real = true, MinSeen = 5 });

            SaveManager.Register(this);
            Services.Regional = this;
            Crafting.CraftingSystem.ExtraGates.Add(SchematicGate);
            Crafting.CraftingSystem.ExtraHidden.Add(SchematicHidden);
            ForgeSystem.Salvaged += OnSalvage;

            foreach (var kv in MaterialNames)
            {
                var key = kv.Key;
                DebugMenu.Register("Exploration: materials", kv.Value + " x2", () => { Grant(key, 2); });
            }
            foreach (var s in _schematicDefs)
            {
                var key = s.Key;
                DebugMenu.Register("Exploration: schematics", s.Label, () => { _schematics.Add(key); GameUtil.Hud("Schematic learned: " + s.Label); });
            }
        }

        // ------------------------------------------------------------------------------------------- config file

        private static string ConfigPath { get { return Path.Combine(MelonEnvironment.UserDataDirectory, "TLDOverhaul", "exploration.json"); } }

        private void LoadBooks()
        {
            _books = new Dictionary<string, string[]>(DefaultBooks);
            try
            {
                var path = ConfigPath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var books = new JObject();
                    foreach (var kv in DefaultBooks) { var arr = new JArray(); foreach (var bk in kv.Value) arr.Add(bk); books[kv.Key] = arr; }
                    var o = new JObject { ["books"] = books };
                    File.WriteAllText(path, o.ToString());
                    return;
                }
                var root = JObject.Parse(File.ReadAllText(path));
                var b = root["books"] as JObject;
                if (b != null) foreach (var p in b.Properties()) _books[p.Name] = ((JArray)p.Value).Select(x => (string)x).ToArray();
                MelonLogger.Msg("[Exploration] exploration.json: book placement for " + _books.Count + " regions.");
            }
            catch (Exception e) { MelonLogger.Warning("[Exploration] exploration.json unreadable (" + e.Message + "); using defaults."); }
        }

        // ------------------------------------------------------------------------------------------- scatter

        /// <summary>Called when a container's contents are generated. Adds at most a few one-time finds to it.</summary>
        public void OnContainerStocked(Container c)
        {
            if (c == null || !GameUtil.InGame) return;
            string region = World.RegionKey;
            if (region == "unknown") return;
            string guid = null;
            try { guid = ObjectGuid.MaybeGetGuidFromGameObject(c.gameObject); } catch { }
            if (string.IsNullOrEmpty(guid) || !_processed.Add(guid)) return;
            int seen; _seen.TryGetValue(region, out seen); seen++; _seen[region] = seen;

            // 1. books
            string[] list;
            if (_books.TryGetValue(region, out list))
            {
                var remaining = list.Where(b => !_placed.Contains("book:" + b)).ToList();
                if (remaining.Count > 0)
                {
                    float p = Mathf.Clamp(remaining.Count / Mathf.Max(4f, 30f - seen), 0.02f, 1f);
                    if (GameUtil.Hash01(guid + ":book") < p)
                    {
                        string pick = remaining[Mathf.Min(remaining.Count - 1, (int)(GameUtil.Hash01(guid + ":pick") * remaining.Count))];
                        PlaceBook(c, pick);
                    }
                }
            }
            // 2. schematics and uniques: appear once seen >= MinSeen with rising odds
            foreach (var s in _schematicDefs.Where(x => x.Region == region && !_placed.Contains("schem:" + x.Key)))
                if (RampRoll(guid, s.Key, seen, s.MinSeen)) PlaceCarrier(c, "schem:" + s.Key, "PaperStack", s.Label);
            foreach (var u in _uniques.Where(x => x.Region == region && !_placed.Contains("unique:" + x.Key)))
                if (RampRoll(guid, u.Key, seen, u.MinSeen)) PlaceCarrier(c, "unique:" + u.Key, u.Carrier, u.Label, u.Real);
        }

        private static bool RampRoll(string guid, string key, int seen, int minSeen)
        {
            if (seen < minSeen) return false;
            float p = Mathf.Clamp((seen - minSeen + 1) / 14f, 0.05f, 1f);
            return GameUtil.Hash01(guid + ":" + key) < p;
        }

        private void PlaceBook(Container c, string spec)
        {
            try
            {
                var parts = spec.Split(':');
                SkillId id;
                if (!Enum.TryParse(parts[0], out id) || SkillsSystem.Instance == null) return;
                var gi = SkillsSystem.Instance.CreateBook(id, int.Parse(parts[1]));
                if (gi == null) return;
                c.AddGear(gi);
                _placed.Add("book:" + spec);
                MelonLogger.Msg("[Exploration] placed book " + spec + " in a container in " + World.RegionKey);
            }
            catch (Exception e) { PatchLog.Error("Exploration.PlaceBook", e); }
        }

        private void PlaceCarrier(Container c, string placedKey, string carrierPrefab, string label, bool real = false)
        {
            try
            {
                var gi = GearItem.InstantiateGearItem("GEAR_" + carrierPrefab);
                if (gi == null) return;
                if (!real)
                {
                    try { gi.ForceGUIDSetup(); } catch { }
                    string g = GameUtil.GuidOf(gi);
                    if (g == null) return;
                    _carriers[g] = placedKey;
                }
                c.AddGear(gi);
                _placed.Add(placedKey);
                MelonLogger.Msg("[Exploration] placed " + placedKey + " in a container in " + World.RegionKey);
            }
            catch (Exception e) { PatchLog.Error("Exploration.PlaceCarrier", e); }
        }

        // ------------------------------------------------------------------------------------------- pickup conversion

        public string CarrierLabel(GearItem gi)
        {
            string g = GameUtil.GuidOf(gi), key;
            if (g == null || !_carriers.TryGetValue(g, out key)) return null;
            if (key.StartsWith("schem:")) { var s = _schematicDefs.FirstOrDefault(x => "schem:" + x.Key == key); return s != null ? s.Label : "Schematic"; }
            var u = _uniques.FirstOrDefault(x => "unique:" + x.Key == key);
            return u != null ? u.Label : "Rare find";
        }

        public void OnItemAdded(GearItem gi)
        {
            string g = GameUtil.GuidOf(gi);
            if (g != null && _carriers.ContainsKey(g)) _pendingConvert.Add(gi);
        }

        public override void OnUpdate()
        {
            if (_pendingConvert.Count == 0) return;
            var list = _pendingConvert.ToList(); _pendingConvert.Clear();
            foreach (var gi in list)
            {
                try
                {
                    string g = GameUtil.GuidOf(gi), key;
                    if (g == null || !_carriers.TryGetValue(g, out key)) continue;
                    _carriers.Remove(g);
                    GameManager.GetInventoryComponent().DestroyGear(gi);
                    if (key.StartsWith("schem:"))
                    {
                        var s = _schematicDefs.First(x => "schem:" + x.Key == key);
                        _schematics.Add(s.Key);
                        GameUtil.Hud("You study it: " + s.Label + ". You understand how it is done.", true);
                    }
                    else
                    {
                        var u = _uniques.First(x => "unique:" + x.Key == key);
                        if (u.Grant != null) u.Grant(this);
                        GameUtil.Hud("You find something that changes how you travel: " + u.Label + ".", true);
                    }
                }
                catch (Exception e) { PatchLog.Error("Exploration.Convert", e); }
            }
        }

        // ------------------------------------------------------------------------------------------- schematic gating

        private string SchematicFor(Il2CppTLD.Gear.BlueprintData bp)
        {
            string n = BlueprintInfo.ResultName(bp).ToLowerInvariant();
            foreach (var s in _schematicDefs)
                foreach (var k in s.Keywords)
                    if (n.Contains(k) && !_schematics.Contains(s.Key)) return s.Label;
            return null;
        }

        private string SchematicGate(Il2CppTLD.Gear.BlueprintData bp)
        {
            var s = SchematicFor(bp);
            return s == null ? null : "You don't know how to make this. It needs: " + s + ".";
        }

        private bool SchematicHidden(Il2CppTLD.Gear.BlueprintData bp) { return SchematicFor(bp) != null; }

        // ------------------------------------------------------------------------------------------- regional materials

        private void OnSalvage(string what, ScrapGrade grade, float units)
        {
            string[] mats;
            if (!RegionMaterials.TryGetValue(World.RegionKey, out mats)) return;
            if (UnityEngine.Random.value > _salvageChance.Value) return;
            Grant(mats[UnityEngine.Random.Range(0, mats.Length)], UnityEngine.Random.value < 0.3f ? 2 : 1);
        }

        private void Grant(string material, int n)
        {
            int x; _materials.TryGetValue(material, out x); _materials[material] = x + n;
            GameUtil.Hud(string.Format("Salvaged {0} x{1}.", Display(material), n));
        }

        public int Count(string material) { int x; _materials.TryGetValue(material, out x); return x; }
        public bool TryConsume(string material, int units)
        {
            int x; _materials.TryGetValue(material, out x);
            if (x < units) return false;
            _materials[material] = x - units; return true;
        }
        public string Display(string material) { string s; return MaterialNames.TryGetValue(material, out s) ? s : material; }
        public bool HasSchematic(string key) { return _schematics.Contains(key); }

        // ------------------------------------------------------------------------------------------- hide quality by climate

        /// <summary>Harsher regions and colder days yield thicker, better hides.</summary>
        public float HideQuality()
        {
            float cold = Mathf.Clamp01((5f - GameUtil.AirTemperature) / 40f);
            return Mathf.Clamp01(0.45f + 0.35f * World.Harshness + 0.20f * cold);
        }

        // ------------------------------------------------------------------------------------------- ISaveSection

        void ISaveSection.Reset() { _materials.Clear(); _placed.Clear(); _schematics.Clear(); _seen.Clear(); _processed.Clear(); _carriers.Clear(); _pendingConvert.Clear(); }

        JToken ISaveSection.Save()
        {
            return new JObject
            {
                ["materials"] = new JObject(_materials.Where(x => x.Value > 0).Select(x => new JProperty(x.Key, x.Value))),
                ["placed"] = new JArray(_placed), ["schematics"] = new JArray(_schematics),
                ["seen"] = new JObject(_seen.Select(x => new JProperty(x.Key, x.Value))),
                ["processed"] = new JArray(_processed),
                ["carriers"] = new JObject(_carriers.Select(x => new JProperty(x.Key, x.Value))),
            };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            var m = o["materials"] as JObject; if (m != null) foreach (var p in m.Properties()) _materials[p.Name] = (int)p.Value;
            var pl = o["placed"] as JArray; if (pl != null) foreach (var x in pl) _placed.Add((string)x);
            var sc = o["schematics"] as JArray; if (sc != null) foreach (var x in sc) _schematics.Add((string)x);
            var se = o["seen"] as JObject; if (se != null) foreach (var p in se.Properties()) _seen[p.Name] = (int)p.Value;
            var pr = o["processed"] as JArray; if (pr != null) foreach (var x in pr) _processed.Add((string)x);
            var ca = o["carriers"] as JObject; if (ca != null) foreach (var p in ca.Properties()) _carriers[p.Name] = (string)p.Value;
        }

        public override void DrawStatus(StatusWriter w)
        {
            string region = World.RegionKey;
            w.Line("Region: {0} ({1})   harshness {2:0.00}   forge here: {3}", region, World.SceneName, World.Harshness, BenchLocator.CurrentForgeClass);
            if (_materials.Values.Any(v => v > 0)) w.Line("Regional materials: " + string.Join(", ", _materials.Where(x => x.Value > 0).Select(x => Display(x.Key) + " x" + x.Value)));
            if (_schematics.Count > 0) w.Line("Schematics: " + string.Join(", ", _schematics));
            int left = _books.Values.Sum(v => v.Count(b => true)) - _placed.Count(p => p.StartsWith("book:"));
            w.Line("Containers searched in this region: {0}.  Scattered volumes still unplaced: {1}", _seen.ContainsKey(region) ? _seen[region] : 0, Math.Max(0, left));
        }
    }
}
