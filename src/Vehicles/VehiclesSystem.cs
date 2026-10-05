using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Vehicles
{
    public enum VPart { Engine, Battery, Tires, Radiator, FuelLine, Electrical }

    public sealed class VehicleRecord
    {
        public string Id;
        public string Scene;
        public readonly Dictionary<VPart, float> Cond = new Dictionary<VPart, float>();
        public float Charge;            // battery charge 0..1
        public float Fuel;              // litres in the tank
        public float FuelQuality = 1f;  // gasoline degrades; stabilizer slows it
        public bool Stabilized;
        public bool Running;
        public float LastRunHours = -999f;
        public bool Stripped;
        public float C(VPart p) { float v; return Cond.TryGetValue(p, out v) ? v : 0f; }
    }

    /// <summary>
    /// Vehicles. TLD ships its vehicles as static, enterable wrecks (VehicleDoor with a built-in bed) - there is no driving
    /// model in the game. This system treats each wreck as a dead machine that can be brought back: six components with their
    /// own condition, diagnosed by mechanics skill, fixed with salvaged parts and scrap, fuelled with scarce, decaying
    /// gasoline, and started against the cold (a cold-soaked engine at -40 simply will not catch). A running vehicle heats the
    /// cabin you rest in, burns fuel, wears its engine, and its noise draws predators. Hauling by driving is NOT implemented:
    /// that needs vehicle physics and region transfer zones that a code-only mod cannot create.
    /// </summary>
    public sealed class VehiclesSystem : OverhaulSystem, ISaveSection, IBenchProvider
    {
        public static VehiclesSystem Instance { get; private set; }
        public override string Name { get { return "Vehicles"; } }
        string ISaveSection.Key { get { return "vehicles"; } }
        string IBenchProvider.Title { get { return "Vehicle"; } }

        private readonly Dictionary<string, VehicleRecord> _vehicles = new Dictionary<string, VehicleRecord>();
        private readonly Dictionary<string, int> _parts = new Dictionary<string, int>();    // Tire, Hose, Gasket
        private float _carriedFuel;                                                       // litres siphoned and carried
        private int _stabilizer;
        private float _noiseTimer;

        private Setting<float> _heater, _burnPerHour, _wearPerHour, _tank, _noiseChance, _noiseRange, _carryPerCan;

        public override IEnumerable<Type> PatchTypes
        {
            get { yield return typeof(PlayerInVehicle_GetTempIncrease); }
        }

        protected override void OnInit()
        {
            Instance = this;
            _heater = Cfg.F(Name, "CabinHeaterDegrees", 12f, "Extra cabin warmth (degrees C) while a running vehicle heats the cab you are resting in.");
            _burnPerHour = Cfg.F(Name, "IdleFuelPerHour", 0.8f, "Litres burned per hour with the engine running.");
            _wearPerHour = Cfg.F(Name, "EngineWearPerHour", 0.004f, "Engine condition lost per running hour (more with a bad radiator).");
            _tank = Cfg.F(Name, "TankLitres", 45f, "Tank size.");
            _noiseChance = Cfg.F(Name, "EngineNoiseAlertChance", 0.25f, "Chance per check that a predator in earshot of a running engine investigates.");
            _noiseRange = Cfg.F(Name, "EngineNoiseRange", 90f, "Metres within which a running engine can be heard.");
            _carryPerCan = Cfg.F(Name, "LitresPerJerrycan", 10f, "Fuel you can carry per jerrycan.");

            SaveManager.Register(this);
            BenchMenu.Register(this);

            DebugMenu.Register("Vehicles", "Add 4 tires, 2 hoses, 1 gasket", () => { AddPart("Tire", 4); AddPart("Hose", 2); AddPart("Gasket", 1); });
            DebugMenu.Register("Vehicles", "Add 15 L carried fuel + 1 stabilizer", () => { _carriedFuel += 15f; _stabilizer++; });
            DebugMenu.Register("Vehicles", "Give jerrycan + car battery", () => { GameUtil.GiveItem("JerrycanRusty"); GameUtil.GiveItem("CarBattery"); });
        }

        private void AddPart(string name, int n) { int x; _parts.TryGetValue(name, out x); _parts[name] = x + n; }
        private int Parts(string name) { int x; _parts.TryGetValue(name, out x); return x; }

        // ============================================================================ identity and generation

        private static string IdFor(BenchInfo bench)
        {
            Vector3 pos = bench.Transform.position;
            string best = null;
            foreach (var c in BenchLocator.All(BenchKind.Vehicle))
            {
                if (c == null) continue;
                Vector3 p;
                try { p = c.transform.position; } catch { continue; }
                if (Vector3.Distance(p, pos) > 4.5f) continue;
                string g = null;
                try { g = ObjectGuid.MaybeGetGuidFromGameObject(c.gameObject); } catch { }
                if (string.IsNullOrEmpty(g)) g = World.SceneName + ":" + Mathf.RoundToInt(p.x / 3f) + ":" + Mathf.RoundToInt(p.z / 3f);
                if (best == null || string.CompareOrdinal(g, best) < 0) best = g;
            }
            return best ?? (World.SceneName + ":" + Mathf.RoundToInt(pos.x / 3f) + ":" + Mathf.RoundToInt(pos.z / 3f));
        }

        private VehicleRecord Get(BenchInfo bench)
        {
            string id = IdFor(bench);
            VehicleRecord v;
            if (_vehicles.TryGetValue(id, out v)) return v;
            // First contact: a dead machine whose state is stable per save (derived from the id, not from Random).
            v = new VehicleRecord { Id = id, Scene = World.SceneName };
            foreach (VPart p in Enum.GetValues(typeof(VPart)))
                v.Cond[p] = Mathf.Lerp(0.08f, 0.60f, GameUtil.Hash01(id + ":" + p));
            v.Charge = 0f;
            v.Fuel = Mathf.Lerp(0f, 6f, GameUtil.Hash01(id + ":fuel"));
            v.FuelQuality = Mathf.Lerp(0.25f, 0.65f, GameUtil.Hash01(id + ":fq"));
            _vehicles[id] = v;
            return v;
        }

        // ============================================================================ diagnosis

        private static string Diagnose(float cond, int tier)
        {
            if (tier <= 0) return "can't tell";
            if (tier == 1) return cond < 0.35f ? "bad" : (cond < 0.7f ? "worn" : "seems fine");
            if (tier == 2) return string.Format("~{0}%", Mathf.RoundToInt(cond * 10f) * 10);
            return string.Format("{0:0}%", cond * 100f);
        }

        private static string Defect(VPart p, float cond, int tier)
        {
            if (tier < 3 || cond >= 0.3f) return "";
            switch (p)
            {
                case VPart.Engine: return " - cracked head gasket";
                case VPart.Battery: return " - sulphated plates";
                case VPart.Tires: return " - dry-rotted sidewalls";
                case VPart.Radiator: return " - split core";
                case VPart.FuelLine: return " - perished hose";
                default: return " - corroded wiring";
            }
        }

        // ============================================================================ start / run model

        private float StartChance(VehicleRecord v)
        {
            float t = GameUtil.AirTemperature;
            float cold = Mathf.Clamp01((5f - t) / 45f);                       // 0 at +5C, 1 at -40C
            float p = 0.97f - 0.85f * cold;
            p *= Mathf.Lerp(0.3f, 1f, v.C(VPart.Engine));
            p *= Mathf.Lerp(0.15f, 1f, Mathf.Clamp01(v.Charge) * Mathf.Sqrt(Mathf.Max(0.01f, v.C(VPart.Battery))));
            p *= Mathf.Lerp(0.6f, 1f, v.C(VPart.Electrical));
            p *= Mathf.Lerp(0.5f, 1f, v.FuelQuality);
            p *= Mathf.Lerp(0.2f, 1f, Mathf.Clamp01(v.C(VPart.FuelLine) / 0.3f));
            if (v.Fuel < 0.1f) return 0f;
            if (GameUtil.HoursPlayed - v.LastRunHours < 2f) p = Mathf.Min(1f, p + 0.25f * (1f - cold));
            return Mathf.Clamp01(p);
        }

        private void TryStart(VehicleRecord v)
        {
            float p = StartChance(v);
            if (UnityEngine.Random.value < p)
            {
                v.Running = true;
                GameUtil.Hud("The engine coughs, catches, and settles into a rough idle.", true);
                Services.Skills.AddXp(SkillId.Mechanics, 1.5f);
            }
            else
            {
                v.Charge = Mathf.Max(0f, v.Charge - 0.12f);
                string why = v.Fuel < 0.1f ? "There's no fuel in the tank."
                           : v.Charge < 0.1f ? "The starter clicks and gives up. The battery is flat."
                           : GameUtil.AirTemperature < -20f ? "The engine turns over slowly and won't catch. It's cold-soaked."
                           : "It turns over but won't catch.";
                GameUtil.Hud(why, true);
            }
        }

        public override void OnUpdate()
        {
            float h = GameUtil.HoursSince("vehicles");
            if (h <= 0f) return;
            float temp = GameUtil.AirTemperature;
            float cold = Mathf.Clamp01((5f - temp) / 45f);

            foreach (var v in _vehicles.Values)
            {
                if (v.Stripped) { v.Running = false; continue; }
                if (v.Running)
                {
                    v.Fuel -= _burnPerHour.Value * h;
                    float radiatorBad = 1f - v.C(VPart.Radiator);
                    v.Cond[VPart.Engine] = Mathf.Max(0f, v.C(VPart.Engine) - _wearPerHour.Value * h * (1f + 2f * radiatorBad * radiatorBad));
                    v.Charge = Mathf.Min(1f, v.Charge + 0.04f * h * v.C(VPart.Electrical));
                    v.LastRunHours = GameUtil.HoursPlayed;
                    if (v.Fuel <= 0f) { v.Fuel = 0f; v.Running = false; if (v.Scene == World.SceneName) GameUtil.Hud("The engine sputters and dies. The tank is dry.", true); }
                    else if (v.C(VPart.Radiator) < 0.15f && UnityEngine.Random.value < 0.5f * h)
                    {
                        v.Running = false; v.Cond[VPart.Engine] = Mathf.Max(0f, v.C(VPart.Engine) - 0.1f);
                        if (v.Scene == World.SceneName) GameUtil.Hud("The engine overheats and seizes up to a stall.", true);
                    }
                }
                else
                {
                    v.Charge = Mathf.Max(0f, v.Charge - 0.001f * h * (1f + 2f * cold));
                    if (temp < -25f && UnityEngine.Random.value < 0.02f * h) v.Cond[VPart.Radiator] = Mathf.Max(0f, v.C(VPart.Radiator) - 0.05f);
                }
                v.FuelQuality = Mathf.Max(0f, v.FuelQuality - (v.Stabilized ? 0.0001f : 0.0005f) * h);
                v.Cond[VPart.Tires] = Mathf.Max(0f, v.C(VPart.Tires) - 0.0002f * h * (1f + cold));
            }

            // a running engine is loud: predators in earshot may come to look
            _noiseTimer += h;
            if (_noiseTimer > 0.25f)
            {
                _noiseTimer = 0f;
                foreach (var v in _vehicles.Values)
                {
                    if (!v.Running || v.Scene != World.SceneName) continue;
                    try
                    {
                        foreach (var ai in AiUtils.GetAisWithinRange(GameUtil.PlayerPos, _noiseRange.Value))
                            if (ai != null && ai.m_AiType == AiType.Predator && UnityEngine.Random.value < _noiseChance.Value)
                                ai.ProcessGunshotAudioEvent(GameManager.GetPlayerObject(), GameUtil.PlayerPos);
                    }
                    catch (Exception e) { PatchLog.Error("Vehicles.Noise", e); }
                    break;
                }
            }
        }

        /// <summary>Cabin heat for the vehicle the player is sitting in (if its engine is running).</summary>
        public float CabinHeat()
        {
            try
            {
                var door = GameManager.GetPlayerInVehicle().m_VehicleDoorUsed;
                if (door == null) return 0f;
                var bench = new BenchInfo { Kind = BenchKind.Vehicle, Component = door, Transform = door.transform };
                VehicleRecord v;
                if (!_vehicles.TryGetValue(IdFor(bench), out v) || !v.Running) return 0f;
                return _heater.Value * Mathf.Lerp(0.5f, 1f, v.C(VPart.Engine));
            }
            catch { return 0f; }
        }

        // ============================================================================ bench entries

        IEnumerable<BenchEntry> IBenchProvider.Entries(BenchInfo bench)
        {
            if (bench.Kind != BenchKind.Vehicle) yield break;
            var v = Get(bench);
            int tier = Services.Skills.GetTier(SkillId.Mechanics);

            if (v.Stripped) { yield return new BenchEntry { Label = "Stripped wreck", Detail = "nothing left worth taking", Enabled = false }; yield break; }

            // read-only condition rows: how much you can see depends on mechanics skill
            yield return new BenchEntry { Label = "Condition (Mechanics " + SkillMap.Roman[tier] + ")", Detail = v.Running ? "ENGINE RUNNING" : "engine off", Enabled = false };
            foreach (VPart p in Enum.GetValues(typeof(VPart)))
                yield return new BenchEntry { Label = "  " + p, Detail = Diagnose(v.C(p), tier) + Defect(p, v.C(p), tier), Enabled = false };
            yield return new BenchEntry
            {
                Label = "  Battery charge / Fuel",
                Detail = string.Format("{0} / {1:0.0} L{2}", tier >= 1 ? Mathf.RoundToInt(v.Charge * 100f) + "%" : "?", v.Fuel, v.Stabilized ? " (stabilized)" : ""),
                Enabled = false,
            };

            // ---- operation
            if (v.Running)
                yield return new BenchEntry { Label = "Switch the engine off", Run = () => { v.Running = false; GameUtil.Hud("The engine winds down."); } };
            else
                yield return new BenchEntry
                {
                    Label = "Try to start the engine",
                    Detail = tier >= 2 ? string.Format("~{0:P0} at {1:0}C", StartChance(v), GameUtil.AirTemperature) : "the cold is not on your side",
                    Run = () => TryStart(v),
                };

            // ---- repairs
            foreach (VPart p in Enum.GetValues(typeof(VPart)))
            {
                var part = p;
                if (v.C(part) >= 0.92f) continue;
                yield return RepairEntry(v, part, tier);
            }

            // ---- fuel
            int cans = GameUtil.CountInInventory("JerrycanRusty");
            float cap = cans * _carryPerCan.Value;
            if (v.Fuel > 0.5f && Parts("Hose") > 0)
                yield return new BenchEntry
                {
                    Label = "Siphon the tank",
                    Detail = cans == 0 ? "needs a jerrycan" : string.Format("{0:0.0} L here, room for {1:0.0}", v.Fuel, Mathf.Max(0f, cap - _carriedFuel)),
                    Enabled = cans > 0 && _carriedFuel < cap,
                    Run = () =>
                    {
                        float take = Mathf.Min(v.Fuel, cap - _carriedFuel);
                        v.Fuel -= take; _carriedFuel += take;
                        // old gasoline is poor gasoline: carried fuel keeps the source's quality
                        _carriedQuality = _carriedFuel > 0.01f ? Mathf.Min(_carriedQuality, v.FuelQuality) : v.FuelQuality;
                        GameUtil.Hud(string.Format("Siphoned {0:0.0} L.", take));
                        Services.Skills.AddXp(SkillId.Mechanics, 1f);
                    },
                };
            else if (v.Fuel > 0.5f)
                yield return new BenchEntry { Label = "Siphon the tank", Detail = "needs a hose (salvage one from a wreck)", Enabled = false };
            if (_carriedFuel > 0.1f)
                yield return new BenchEntry
                {
                    Label = string.Format("Pour in carried fuel ({0:0.0} L)", _carriedFuel),
                    Detail = string.Format("tank {0:0.0}/{1:0} L", v.Fuel, _tank.Value),
                    Enabled = v.Fuel < _tank.Value - 0.1f,
                    Run = () =>
                    {
                        float put = Mathf.Min(_carriedFuel, _tank.Value - v.Fuel);
                        v.FuelQuality = (v.FuelQuality * v.Fuel + _carriedQuality * put) / Mathf.Max(0.01f, v.Fuel + put);
                        v.Fuel += put; _carriedFuel -= put;
                        GameUtil.Hud(string.Format("Added {0:0.0} L.", put));
                    },
                };
            if (_stabilizer > 0 && !v.Stabilized && v.Fuel > 0.5f)
                yield return new BenchEntry { Label = "Add fuel stabilizer", Detail = _stabilizer + " in stock", Run = () => { _stabilizer--; v.Stabilized = true; GameUtil.Hud("Fuel stabilized."); } };

            // ---- strip a wreck for parts you will not use here
            yield return new BenchEntry
            {
                Label = "Strip this wreck for parts",
                Detail = "tires, hoses, battery, scrap; the vehicle is gone for good",
                Run = () => Strip(v, tier),
            };
        }

        private float _carriedQuality = 1f;

        private BenchEntry RepairEntry(VehicleRecord v, VPart p, int tier)
        {
            int needTier; string need; Func<bool> have; Action consume;
            switch (p)
            {
                case VPart.Engine:
                    needTier = 2; need = "2 mid scrap" + (v.C(p) < 0.3f ? ", 1 gasket" : "");
                    have = () => Services.Scrap.GetScrap(ScrapGrade.Mid) >= 2 && (v.C(p) >= 0.3f || Parts("Gasket") > 0);
                    consume = () => { Services.Scrap.TryConsume(ScrapGrade.Mid, 2); if (v.C(p) < 0.3f) AddPart("Gasket", -1); };
                    break;
                case VPart.Battery:
                    needTier = 1; need = "a car battery";
                    have = () => GameUtil.CountInInventory("CarBattery") > 0;
                    consume = () => GameUtil.RemoveFromInventory("CarBattery", 1);
                    break;
                case VPart.Tires:
                    needTier = 0; need = "4 tires";
                    have = () => Parts("Tire") >= 4;
                    consume = () => AddPart("Tire", -4);
                    break;
                case VPart.Radiator:
                    needTier = 1; need = "2 mid scrap";
                    have = () => Services.Scrap.GetScrap(ScrapGrade.Mid) >= 2;
                    consume = () => Services.Scrap.TryConsume(ScrapGrade.Mid, 2);
                    break;
                case VPart.FuelLine:
                    needTier = 1; need = "1 hose";
                    have = () => Parts("Hose") >= 1;
                    consume = () => AddPart("Hose", -1);
                    break;
                default:
                    needTier = 2; need = "1 low scrap, 1 cloth";
                    have = () => Services.Scrap.GetScrap(ScrapGrade.Low) >= 1 && GameUtil.CountInInventory("Cloth") >= 1;
                    consume = () => { Services.Scrap.TryConsume(ScrapGrade.Low, 1); GameUtil.RemoveFromInventory("Cloth", 1); };
                    break;
            }
            // "A novice mechanic replacing a tire takes hours and might damage the rim."
            float minutes = Mathf.Lerp(150f, 45f, Services.Skills.GetLevel01(SkillId.Mechanics));
            GearItem tool = GameUtil.InventoryItems().Where(g => { var n = GameUtil.NameOf(g); return n == "HighQualityTools" || n == "SimpleTools"; })
                                    .OrderByDescending(g => Services.Tools.GetPerformance(g)).FirstOrDefault();
            bool skillOk = tier >= needTier;
            bool mats = have();
            return new BenchEntry
            {
                Label = "Repair " + p,
                Detail = !skillOk ? "needs Mechanics " + SkillMap.Roman[needTier] : tool == null ? "needs a toolkit" : !mats ? "needs " + need : string.Format("{0}, ~{1:0} min", need, minutes),
                Enabled = skillOk && tool != null && mats,
                Run = () =>
                {
                    consume();
                    float perf = Services.Tools.GetPerformance(tool);
                    TimedAction.Run("Repairing " + p, minutes / perf, ok =>
                    {
                        if (!ok) { GameUtil.Hud("You stop working; the part is spoiled."); return; }
                        float fail = Services.Crafting.FailureChance(SkillId.Mechanics, needTier, perf);
                        if (UnityEngine.Random.value < fail)
                        {
                            if (p == VPart.Tires) { v.Cond[p] = Mathf.Max(0f, v.C(p) - 0.1f); GameUtil.Hud("You mangle the rim getting the tire on.", true); }
                            else GameUtil.Hud("The repair doesn't hold. The part is wasted.", true);
                            Services.Skills.AddXp(SkillId.Mechanics, 1f);
                            return;
                        }
                        float q = Mathf.Lerp(0.55f, 0.95f, Services.Skills.GetLevel01(SkillId.Mechanics)) * Mathf.Lerp(0.85f, 1f, Mathf.Clamp01(perf));
                        v.Cond[p] = Mathf.Max(v.C(p), q);
                        if (p == VPart.Battery) v.Charge = Mathf.Max(v.Charge, 0.35f);
                        Services.Skills.AddXp(SkillId.Mechanics, 2f + needTier);
                        GameUtil.Hud(string.Format("{0} repaired ({1:0}%).", p, v.C(p) * 100f), true);
                    });
                },
            };
        }

        private void Strip(VehicleRecord v, int tier)
        {
            TimedAction.Run("Stripping the wreck", 120f, ok =>
            {
                if (!ok) return;
                float lvl = Services.Skills.GetLevel01(SkillId.Mechanics);
                float yield = Mathf.Lerp(0.5f, 1f, lvl);
                int tires = v.C(VPart.Tires) > 0.25f ? Mathf.RoundToInt(4 * yield) : 0;
                int hoses = Mathf.Max(1, Mathf.RoundToInt(2 * yield));
                AddPart("Tire", tires); AddPart("Hose", hoses);
                if (v.C(VPart.Engine) < 0.3f || UnityEngine.Random.value < 0.3f) AddPart("Gasket", 1);
                bool battery = v.C(VPart.Battery) > 0.2f;
                if (battery) GameUtil.GiveItem("CarBattery", Mathf.Clamp01(v.C(VPart.Battery)));
                Services.Scrap.Add(ScrapGrade.Mid, Mathf.RoundToInt(5 * yield));
                Services.Scrap.Add(ScrapGrade.Low, Mathf.RoundToInt(3 * yield));
                if (v.Fuel > 0f) GameUtil.Hud("There's still fuel in the tank - you should have siphoned it first.");
                v.Stripped = true; v.Running = false;
                Services.Skills.AddXp(SkillId.Mechanics, 3f);
                GameUtil.Hud(string.Format("Stripped: {0} tires, {1} hoses{2}, scrap.", tires, hoses, battery ? ", a battery" : ""), true);
            });
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { _vehicles.Clear(); _parts.Clear(); _carriedFuel = 0f; _stabilizer = 0; _carriedQuality = 1f; }

        JToken ISaveSection.Save()
        {
            var vs = new JObject();
            foreach (var kv in _vehicles)
            {
                var v = kv.Value;
                vs[kv.Key] = new JObject
                {
                    ["scene"] = v.Scene, ["charge"] = Math.Round(v.Charge, 3), ["fuel"] = Math.Round(v.Fuel, 2), ["fq"] = Math.Round(v.FuelQuality, 3),
                    ["stab"] = v.Stabilized, ["run"] = v.Running, ["last"] = Math.Round(v.LastRunHours, 2), ["stripped"] = v.Stripped,
                    ["cond"] = new JObject(v.Cond.Select(c => new JProperty(c.Key.ToString(), Math.Round(c.Value, 3)))),
                };
            }
            return new JObject
            {
                ["vehicles"] = vs, ["parts"] = new JObject(_parts.Select(p => new JProperty(p.Key, p.Value))),
                ["carried"] = Math.Round(_carriedFuel, 2), ["carriedQ"] = Math.Round(_carriedQuality, 3), ["stab"] = _stabilizer,
            };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            var vs = o["vehicles"] as JObject;
            if (vs != null)
                foreach (var p in vs.Properties())
                {
                    var j = p.Value as JObject; if (j == null) continue;
                    var v = new VehicleRecord
                    {
                        Id = p.Name, Scene = (string)j["scene"] ?? "", Charge = (float?)j["charge"] ?? 0f, Fuel = (float?)j["fuel"] ?? 0f, FuelQuality = (float?)j["fq"] ?? 1f,
                        Stabilized = (bool?)j["stab"] ?? false, Running = (bool?)j["run"] ?? false, LastRunHours = (float?)j["last"] ?? -999f, Stripped = (bool?)j["stripped"] ?? false,
                    };
                    var c = j["cond"] as JObject;
                    if (c != null) foreach (var cp in c.Properties()) { VPart part; if (Enum.TryParse(cp.Name, out part)) v.Cond[part] = (float)cp.Value; }
                    _vehicles[p.Name] = v;
                }
            var pt = o["parts"] as JObject; if (pt != null) foreach (var p in pt.Properties()) _parts[p.Name] = (int)p.Value;
            _carriedFuel = (float?)o["carried"] ?? 0f; _carriedQuality = (float?)o["carriedQ"] ?? 1f; _stabilizer = (int?)o["stab"] ?? 0;
            GameUtil.ResetClock("vehicles");
        }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Parts: tires {0}, hoses {1}, gaskets {2}   carried fuel {3:0.0} L   stabilizer {4}", Parts("Tire"), Parts("Hose"), Parts("Gasket"), _carriedFuel, _stabilizer);
            foreach (var v in _vehicles.Values.Where(x => x.Running || !x.Stripped).Take(6))
                w.Line("Vehicle {0}: {1}, fuel {2:0.0} L, charge {3:P0}, engine {4:P0}", Shorten(v.Id), v.Running ? "RUNNING" : "off", v.Fuel, v.Charge, v.C(VPart.Engine));
            w.Line("(Driving is not implemented; wrecks heat your cab and can be powered up.)");
        }

        private static string Shorten(string s) { return s.Length <= 14 ? s : s.Substring(0, 14); }
    }
}
