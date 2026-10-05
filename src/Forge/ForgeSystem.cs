using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Forge
{
    /// <summary>What a piece of junk is worth at the forge.</summary>
    public sealed class Smeltable
    {
        public string Key;           // substring of the prefab name (no GEAR_), lower case
        public ScrapGrade Grade;
        public float Units;          // scrap units per item (a tin can is a fraction; a car hood is a lot)
        public bool Junk;            // true: always smeltable. false: only once the item is "past repair" (condition below WornOutBelow)
    }

    /// <summary>
    /// The forge as a recycling plant. TLD's map is finite, so the clutter of metal junk becomes the renewable-by-effort
    /// resource: carry it to a forge, smelt it into scrap. Low-grade scrap is the vanilla GEAR_ScrapMetal item (so every vanilla
    /// recipe that asks for scrap keeps working); Mid- and High-grade stock (tool steel, automotive metal) is kept in the
    /// overhaul ledger for weapon parts, vehicles and structural work. Smelting is only possible at a forge - fixed geography.
    /// </summary>
    public sealed class ForgeSystem : OverhaulSystem, ISaveSection, IScrapService, IBenchProvider
    {
        public static ForgeSystem Instance { get; private set; }
        public override string Name { get { return "Forge"; } }
        string ISaveSection.Key { get { return "forge"; } }
        string IBenchProvider.Title { get { return "Smelt junk into scrap"; } }

        // ---- persisted ledger (Low scrap is real inventory items; the fractions carry sub-unit remainders)
        /// <summary>Raised when metal is salvaged from the world (object name, grade, units). Exploration listens for regional materials.</summary>
        public static event Action<string, ScrapGrade, float> Salvaged;

        private int _mid, _high;
        private float _fracLow, _fracMid, _fracHigh;
        private float _smeltedTotal;

        private Setting<float> _smallCap, _industrialCap, _wornOutBelow, _skillYieldLow, _skillYieldHigh, _industrialBonus, _minutesPerUnit, _baseMinutes, _salvageYield;

        // Order matters: first match wins. Names are prefab names (without GEAR_) from the game catalog.
        public static readonly List<Smeltable> Table = new List<Smeltable>
        {
            new Smeltable { Key = "recycledcan",   Grade = ScrapGrade.Low,  Units = 0.25f, Junk = true  },
            new Smeltable { Key = "jerrycan",      Grade = ScrapGrade.Low,  Units = 1.5f,  Junk = true  },
            new Smeltable { Key = "keroseneLamp",  Grade = ScrapGrade.Low,  Units = 1.0f,  Junk = false },
            new Smeltable { Key = "cookingpot",    Grade = ScrapGrade.Low,  Units = 2.0f,  Junk = false },
            new Smeltable { Key = "canopener",     Grade = ScrapGrade.Low,  Units = 0.5f,  Junk = false },
            new Smeltable { Key = "carbattery",    Grade = ScrapGrade.Mid,  Units = 3.0f,  Junk = false },
            new Smeltable { Key = "prybar",        Grade = ScrapGrade.Mid,  Units = 2.0f,  Junk = false },
            new Smeltable { Key = "hammer",        Grade = ScrapGrade.Mid,  Units = 2.0f,  Junk = false },
            new Smeltable { Key = "boltcutters",   Grade = ScrapGrade.Mid,  Units = 2.5f,  Junk = false },
            new Smeltable { Key = "hacksaw",       Grade = ScrapGrade.High, Units = 1.5f,  Junk = false },
            new Smeltable { Key = "hatchet",       Grade = ScrapGrade.High, Units = 2.0f,  Junk = false },   // not HatchetImprovised: see Excluded
            new Smeltable { Key = "knife",         Grade = ScrapGrade.High, Units = 1.0f,  Junk = false },
            new Smeltable { Key = "crampons",      Grade = ScrapGrade.Mid,  Units = 1.5f,  Junk = false },
        };

        private static readonly string[] Excluded = { "improvised", "dummy", "ammo", "casing" };

        public override IEnumerable<Type> PatchTypes
        {
            get { yield return typeof(BreakDown_DoBreakDown); }
        }

        protected override void OnInit()
        {
            Instance = this;
            _smallCap = Cfg.F(Name, "SmallForgeBatchUnits", 10f, "Scrap units the small coastal forge can smelt in one session.");
            _industrialCap = Cfg.F(Name, "IndustrialForgeBatchUnits", 40f, "Scrap units the large industrial forge can smelt in one session (large-batch smelting).");
            _wornOutBelow = Cfg.F(Name, "PastRepairBelow", 0.30f, "Tools and cookware below this normalized condition count as scrap-worthy.");
            _skillYieldLow = Cfg.F(Name, "YieldAtBeginnerSmith", 0.6f, "Fraction of the scrap you recover at Beginner Blacksmithing.");
            _skillYieldHigh = Cfg.F(Name, "YieldAtMasterSmith", 1.15f, "Fraction at Master. A skilled smith gets more usable material from the same junk.");
            _industrialBonus = Cfg.F(Name, "IndustrialForgeBonus", 0.10f, "Extra yield at the large industrial forge (Forlorn Muskeg).");
            _minutesPerUnit = Cfg.F(Name, "MinutesPerScrapUnit", 8f, "Game minutes at the forge per scrap unit smelted. Scrap runs take hours.");
            _baseMinutes = Cfg.F(Name, "BatchSetupMinutes", 15f, "Fixed game minutes to charge the crucible.");
            _salvageYield = Cfg.F(Name, "BreakDownMetalYield", 1.0f, "Scale of the scrap units a metal object yields when broken down.");

            SaveManager.Register(this);
            Services.Scrap = this;
            BenchMenu.Register(this);

            DebugMenu.Register("Forge", "Add 5 Mid scrap", () => Add(ScrapGrade.Mid, 5));
            DebugMenu.Register("Forge", "Add 5 High scrap", () => Add(ScrapGrade.High, 5));
            DebugMenu.Register("Forge", "Give 8 tin cans (RecycledCan)", () => GameUtil.GiveItem("RecycledCan", 1f, 8));
            DebugMenu.Register("Forge", "Give rusty jerrycan", () => GameUtil.GiveItem("JerrycanRusty"));
            DebugMenu.Register("Forge", "Give ruined hatchet (5%)", () => GameUtil.GiveItem("Hatchet", 0.05f));
        }

        // ============================================================================ classification

        public static Smeltable Classify(GearItem gi)
        {
            string n = GameUtil.NameOf(gi).ToLowerInvariant();
            if (n.Length == 0) return null;
            foreach (var x in Excluded) if (n.Contains(x)) return null;
            foreach (var s in Table) if (n.Contains(s.Key.ToLowerInvariant())) return s;
            return null;
        }

        private bool Eligible(GearItem gi, Smeltable s)
        {
            if (s == null) return false;
            if (s.Junk) return true;
            try { return gi.GetNormalizedCondition() <= _wornOutBelow.Value; } catch { return false; }
        }

        private static int UnitsIn(GearItem gi)
        {
            try { return gi.m_StackableItem != null ? Math.Max(1, gi.m_StackableItem.m_Units) : 1; } catch { return 1; }
        }

        // ============================================================================ yield maths

        public float YieldMultiplier(BenchInfo bench)
        {
            float lvl = Services.Skills.GetLevel01(SkillId.Blacksmithing);
            float m = Mathf.Lerp(_skillYieldLow.Value, _skillYieldHigh.Value, lvl);
            if (bench != null && bench.ForgeClass == ForgeClass.Industrial) m += _industrialBonus.Value;
            return m;
        }

        /// <summary>The small coastal workshop cannot refine tool steel: High-grade sources come out as Mid-grade stock there.</summary>
        public static ScrapGrade EffectiveGrade(ScrapGrade g, ForgeClass fc)
        {
            return (g == ScrapGrade.High && fc == ForgeClass.Small) ? ScrapGrade.Mid : g;
        }

        private class Batch { public string Name; public List<GearItem> Items = new List<GearItem>(); public float Units; public ScrapGrade Grade; }

        private List<Batch> BuildBatches(BenchInfo bench)
        {
            var map = new Dictionary<string, Batch>();
            foreach (var gi in GameUtil.InventoryItems())
            {
                var s = Classify(gi);
                if (!Eligible(gi, s)) continue;
                string n = GameUtil.NameOf(gi);
                Batch b;
                if (!map.TryGetValue(n, out b)) { b = new Batch { Name = n, Grade = EffectiveGrade(s.Grade, bench.ForgeClass) }; map[n] = b; }
                b.Items.Add(gi);
                b.Units += s.Units * UnitsIn(gi);
            }
            return map.Values.OrderBy(b => b.Grade).ThenBy(b => b.Name).ToList();
        }

        // ============================================================================ IBenchProvider

        IEnumerable<BenchEntry> IBenchProvider.Entries(BenchInfo bench)
        {
            if (bench.Kind != BenchKind.Forge) yield break;
            var batches = BuildBatches(bench);
            float mult = YieldMultiplier(bench);

            if (batches.Count == 0)
            {
                yield return new BenchEntry { Label = "Nothing to smelt", Detail = "bring tin cans, rusty cans, ruined tools, car parts", Enabled = false };
                yield break;
            }

            foreach (var b in batches)
            {
                var batch = b;
                float units = batch.Units * mult;
                int mins = Mathf.RoundToInt(_baseMinutes.Value + batch.Units * _minutesPerUnit.Value);
                yield return new BenchEntry
                {
                    Label = string.Format("Smelt {0} x{1}", batch.Name, batch.Items.Sum(i => UnitsIn(i))),
                    Detail = string.Format("~{0:0.0} {1} scrap, {2} min", units, batch.Grade, mins),
                    Enabled = bench.ForgeHot,
                    Run = () => Smelt(new List<Batch> { batch }, bench),
                };
            }
            if (batches.Count > 1)
            {
                float total = batches.Sum(x => x.Units) * mult;
                int mins = Mathf.RoundToInt(_baseMinutes.Value + batches.Sum(x => x.Units) * _minutesPerUnit.Value);
                yield return new BenchEntry
                {
                    Label = "Smelt everything listed",
                    Detail = string.Format("~{0:0.0} scrap total, {1} min", total, mins),
                    Enabled = bench.ForgeHot,
                    Run = () => Smelt(batches, bench),
                };
            }
            if (!bench.ForgeHot)
                yield return new BenchEntry { Label = "The forge is cold", Detail = "light the forge fire first", Enabled = false };
        }

        // ============================================================================ smelting

        private void Smelt(List<Batch> batches, BenchInfo bench)
        {
            if (!bench.ForgeHot) { GameUtil.Hud("The forge isn't hot enough."); return; }
            // forge specialization: only the big industrial works takes large batches
            float cap = bench.ForgeClass == ForgeClass.Industrial ? _industrialCap.Value : _smallCap.Value;
            var trimmed = new List<Batch>(); float used = 0f;
            foreach (var b in batches)
            {
                if (used + b.Units > cap && trimmed.Count > 0) break;
                trimmed.Add(b); used += b.Units;
            }
            if (trimmed.Count < batches.Count) GameUtil.Hud("This forge can only take " + cap + " units of scrap at a time. Smelting what fits.");
            batches = trimmed;
            float units = batches.Sum(b => b.Units);
            int mins = Mathf.RoundToInt(_baseMinutes.Value + units * _minutesPerUnit.Value);
            var snapshot = batches.Select(b => new { b.Name, b.Grade, Items = b.Items.ToList() }).ToList();
            float mult = YieldMultiplier(bench);
            ForgeClass fc = bench.ForgeClass;

            bool started = TimedAction.Run("Smelting scrap", mins, ok =>
            {
                if (!ok) { GameUtil.Hud("You stop before the metal has run."); return; }
                var inv = GameManager.GetInventoryComponent();
                float low = 0, mid = 0, high = 0;
                foreach (var sb in snapshot)
                    foreach (var gi in sb.Items)
                    {
                        var s = Classify(gi);
                        if (gi == null || s == null) continue;
                        float u = s.Units * UnitsIn(gi) * mult;
                        ScrapGrade g = EffectiveGrade(s.Grade, fc);
                        try { inv.DestroyGear(gi); } catch { continue; }
                        if (g == ScrapGrade.Low) low += u; else if (g == ScrapGrade.Mid) mid += u; else high += u;
                    }
                Deposit(low, mid, high);
                Services.Skills.AddXp(SkillId.Blacksmithing, 0.5f + (low + mid + high) / 4f);
                _smeltedTotal += low + mid + high;
            });
            if (!started) BenchMenu.Say("You are busy.");
        }

        private void Deposit(float low, float mid, float high)
        {
            _fracLow += low; _fracMid += mid; _fracHigh += high;
            int l = (int)_fracLow, m = (int)_fracMid, h = (int)_fracHigh;
            _fracLow -= l; _fracMid -= m; _fracHigh -= h;
            if (l > 0) GameUtil.GiveItem("ScrapMetal", 1f, l);
            _mid += m; _high += h;
            GameUtil.Hud(string.Format("Smelted: +{0} low scrap, +{1} mid-grade stock, +{2} tool-steel stock.", l, m, h), true);
        }

        /// <summary>Metal salvaged from the world (break-down yields scrap by object, scaled by mechanics skill).</summary>
        public void AddSalvage(string what, ScrapGrade grade, float units)
        {
            units *= _salvageYield.Value * Mathf.Lerp(0.5f, 1.3f, Services.Skills.GetLevel01(SkillId.Mechanics));
            float l = grade == ScrapGrade.Low ? units : 0, m = grade == ScrapGrade.Mid ? units : 0, h = grade == ScrapGrade.High ? units : 0;
            Deposit(l, m, h);
            GameUtil.Hud("Salvaged scrap from " + what + ".");
            try { if (Salvaged != null) Salvaged(what, grade, units); } catch (Exception e) { PatchLog.Error("Forge.Salvaged", e); }
            Services.Skills.AddXp(SkillId.Mechanics, 1f);
        }

        // ============================================================================ IScrapService

        public int GetScrap(ScrapGrade grade)
        {
            switch (grade)
            {
                case ScrapGrade.Low: return GameUtil.CountInInventory("ScrapMetal");
                case ScrapGrade.Mid: return _mid;
                default: return _high;
            }
        }

        public bool TryConsume(ScrapGrade grade, int units)
        {
            if (units <= 0) return true;
            if (GetScrap(grade) < units) return false;
            switch (grade)
            {
                case ScrapGrade.Low: GameUtil.RemoveFromInventory("ScrapMetal", units); break;
                case ScrapGrade.Mid: _mid -= units; break;
                default: _high -= units; break;
            }
            return true;
        }

        public void Add(ScrapGrade grade, int units)
        {
            if (units <= 0) return;
            switch (grade)
            {
                case ScrapGrade.Low: GameUtil.GiveItem("ScrapMetal", 1f, units); break;
                case ScrapGrade.Mid: _mid += units; break;
                default: _high += units; break;
            }
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { _mid = _high = 0; _fracLow = _fracMid = _fracHigh = 0f; _smeltedTotal = 0f; }

        JToken ISaveSection.Save()
        {
            return new JObject
            {
                ["mid"] = _mid, ["high"] = _high,
                ["frac"] = new JArray(Math.Round(_fracLow, 3), Math.Round(_fracMid, 3), Math.Round(_fracHigh, 3)),
                ["smelted"] = Math.Round(_smeltedTotal, 2),
            };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            _mid = (int?)o["mid"] ?? 0; _high = (int?)o["high"] ?? 0;
            var f = o["frac"] as JArray;
            if (f != null && f.Count >= 3) { _fracLow = (float)f[0]; _fracMid = (float)f[1]; _fracHigh = (float)f[2]; }
            _smeltedTotal = (float?)o["smelted"] ?? 0f;
        }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Scrap: low {0} (inventory ScrapMetal)   mid-grade {1}   tool-steel {2}   smelted so far {3:0}", GetScrap(ScrapGrade.Low), _mid, _high, _smeltedTotal);
            var b = BenchLocator.Nearest(6f);
            if (b != null && b.Kind == BenchKind.Forge) w.Line("At a {0} forge ({1}). F7 to smelt.", b.ForgeClass, b.ForgeHot ? "hot" : "cold");
            int junk = 0; foreach (var gi in GameUtil.InventoryItems()) if (Eligible(gi, Classify(gi))) junk++;
            w.Line("Smeltable junk carried: {0} item(s).", junk);
        }
    }
}
