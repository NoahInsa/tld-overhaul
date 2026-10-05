using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppTLD.Gear;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using TLDOverhaul.Crafting;
using UnityEngine;

namespace TLDOverhaul.Building
{
    public sealed class BuildRecord
    {
        public float Quality;   // 0..1, set when the piece is built
        public int Day;
    }

    /// <summary>
    /// Building and construction on top of TLD's own furniture/decoration system:
    ///  * forges and ammo benches are world geography - they cannot be built, moved or taken apart;
    ///  * every furniture project costs hardware (nails, hinges, brackets) in addition to its vanilla materials. Hardware comes
    ///    from salvaging existing structures (skill and tool decide how much survives intact) or from the forge;
    ///  * lumber is processed from logs into planks at the workbench; novice cuts waste half the log;
    ///  * a piece's build quality follows carpentry skill and is shown on its name; sloppy builds salvage poorly;
    ///  * carpentry XP comes from building, taking apart and processing lumber.
    /// </summary>
    public sealed class BuildingSystem : OverhaulSystem, ISaveSection, IBenchProvider
    {
        public static BuildingSystem Instance { get; private set; }
        public override string Name { get { return "Building"; } }
        string ISaveSection.Key { get { return "building"; } }
        string IBenchProvider.Title { get { return "Hardware"; } }

        /// <summary>Set by the interactive-crafting system: runs a smithing mini-game. (label, minutes, onQuality).</summary>
        public static Func<string, float, Action<float>, bool> InteractiveForge;

        private int _hardware;
        private readonly Dictionary<string, BuildRecord> _builds = new Dictionary<string, BuildRecord>();
        private bool _chainLoaded;

        private Setting<float> _hardwarePerScrap, _salvageBase, _salvageMaster, _lumberLow, _lumberHigh;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(BreakDown_Salvage);
                yield return typeof(DecorationItem_DisplayName);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _hardwarePerScrap = Cfg.F(Name, "HardwarePerScrap", 10f, "Nails/hinges/brackets forged from one unit of low-grade scrap, before skill.");
            _salvageBase = Cfg.F(Name, "SalvageYieldBeginner", 0.45f, "Fraction of a structure's materials a Beginner recovers intact.");
            _salvageMaster = Cfg.F(Name, "SalvageYieldMaster", 1.10f, "Fraction at Master carpentry.");
            _lumberLow = Cfg.F(Name, "LumberYieldBeginner", 0.5f, "Fraction of the planks a log should give that a Beginner actually gets (the rest is waste).");
            _lumberHigh = Cfg.F(Name, "LumberYieldMaster", 1.1f, "Fraction at Master.");

            SaveManager.Register(this);
            BenchMenu.Register(this);
            CraftingSystem.ExtraGates.Add(Gate);
            CraftingSystem.CraftedHooks.Add(OnCrafted);

            DebugMenu.Register("Building", "Add 10 hardware", () => AddHardware(10));
            DebugMenu.Register("Building", "Give 4 softwood + 2 hardwood", () => { GameUtil.GiveItem("Softwood", 1f, 4); GameUtil.GiveItem("Hardwood", 1f, 2); });
        }

        public int Hardware { get { return _hardware; } }
        public void AddHardware(int n) { _hardware = Math.Max(0, _hardware + n); }

        // ============================================================================ fixed geography + hardware cost

        private static string ResultOf(BlueprintData bp) { return BlueprintInfo.ResultName(bp).ToLowerInvariant(); }

        public static bool IsStationName(string lower)
        {
            return lower.Contains("forge") || lower.Contains("ammobench") || lower.Contains("ammoworkbench") || lower.Contains("ammo_work")
                   || (lower.Contains("ammo") && lower.Contains("bench"));
        }

        public static bool IsDecoration(BlueprintData bp)
        {
            try { return bp.m_CraftingResultType == CraftingResult.Decoration; } catch { return false; }
        }

        public int HardwareNeed(BlueprintData bp)
        {
            int mats = 0;
            try { mats = bp.m_RequiredGear != null ? bp.m_RequiredGear.Length : 0; } catch { }
            float minutes = 60f;
            try { minutes = bp.m_DurationMinutes; } catch { }
            return Mathf.Clamp(Mathf.CeilToInt(minutes / 40f + mats * 0.5f), 1, 14);
        }

        private string Gate(BlueprintData bp)
        {
            string r = ResultOf(bp);
            if (IsStationName(r)) return "Forges and ammo benches are part of the world. They can't be built, moved or copied.";
            if (IsDecoration(bp))
            {
                int need = HardwareNeed(bp);
                if (_hardware < need)
                    return string.Format("This build needs {0} hardware (nails, hinges, brackets); you have {1}. Salvage it from old structures or forge it.", need, _hardware);
            }
            return null;
        }

        private void OnCrafted(CraftingOperation op, CraftingSystem.Outcome outcome, List<GearItem> made)
        {
            var bp = op.Blueprint;
            if (bp == null || outcome == CraftingSystem.Outcome.Unmanaged) return;

            if (IsDecoration(bp) && outcome == CraftingSystem.Outcome.Succeeded)
            {
                int need = HardwareNeed(bp);
                _hardware = Math.Max(0, _hardware - need);
                var req = CraftingSystem.Instance != null ? CraftingSystem.Instance.Requirement(bp) : default(RecipeReq);
                float q = Services.Crafting.RollQuality(SkillId.Carpentry, req.Tier);
                RecordPendingDecorations(q);
                Services.Skills.AddXp(SkillId.Carpentry, 2f + 1.5f * req.Tier);
                GameUtil.Hud(string.Format("Built with {0} workmanship. ({1} hardware used, {2} left)", Describe(q), need, _hardware));
                return;
            }

            // Lumber processing: planks from a log. Yield (and waste) follow carpentry skill and the saw's condition.
            string result = ResultOf(bp);
            if (result == "reclaimedwoodb" && outcome == CraftingSystem.Outcome.Succeeded)
                AdjustLumberYield(op, bp);
        }

        private void AdjustLumberYield(CraftingOperation op, BlueprintData bp)
        {
            int produced = 1;
            try { produced = Math.Max(1, bp.m_CraftedResultCount); } catch { }
            float perf = 1f;
            try { if (op.m_Tool != null) perf = Mathf.Clamp(Services.Tools.GetPerformance(op.m_Tool), 0.4f, 1.15f); } catch { }
            float lvl = Services.Skills.GetLevel01(SkillId.Carpentry);
            float mult = Mathf.Lerp(_lumberLow.Value, _lumberHigh.Value, lvl) * Mathf.Lerp(0.8f, 1f, Mathf.Clamp01(perf));
            int got = Mathf.Max(1, Mathf.RoundToInt(produced * mult + UnityEngine.Random.Range(-0.25f, 0.25f)));
            int delta = got - produced;
            if (delta > 0) GameUtil.GiveItem("ReclaimedWoodB", 1f, delta);
            else if (delta < 0) GameUtil.RemoveFromInventory("ReclaimedWoodB", -delta);
            GameUtil.Hud(string.Format("You get {0} usable plank{1} out of a possible {2}.", got, got == 1 ? "" : "s", produced));
            Services.Skills.AddXp(SkillId.Carpentry, 1f);
        }

        // ============================================================================ build quality

        public static string Describe(float q)
        {
            if (q < 0.40f) return "crude";
            if (q < 0.60f) return "rough";
            if (q < 0.80f) return "sound";
            if (q < 0.95f) return "fine";
            return "master-crafted";
        }

        private void RecordPendingDecorations(float quality)
        {
            try
            {
                var list = DecorationItem.s_DecorationItems;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var di = list[i];
                    if (di == null || !di.IsCraftedItemPlacementPending()) continue;
                    string g = ObjectGuid.MaybeGetGuidFromGameObject(di.gameObject);
                    if (string.IsNullOrEmpty(g) || _builds.ContainsKey(g)) continue;
                    _builds[g] = new BuildRecord { Quality = quality, Day = GameUtil.DayNumber };
                }
            }
            catch (Exception e) { PatchLog.Error("Building.RecordPending", e); }
        }

        public BuildRecord RecordFor(Component c)
        {
            if (c == null) return null;
            try
            {
                string g = ObjectGuid.MaybeGetGuidFromGameObject(c.gameObject);
                BuildRecord r;
                return !string.IsNullOrEmpty(g) && _builds.TryGetValue(g, out r) ? r : null;
            }
            catch { return null; }
        }

        // ============================================================================ salvage (called from the BreakDown patch)

        public float SalvageMultiplier(GearItem tool, BuildRecord rec)
        {
            float lvl = Services.Skills.GetLevel01(SkillId.Carpentry);
            float m = Mathf.Lerp(_salvageBase.Value, _salvageMaster.Value, lvl);
            if (tool != null) m *= Mathf.Lerp(0.8f, 1f, Mathf.Clamp01(Services.Tools.GetPerformance(tool)));
            if (rec != null) m *= Mathf.Lerp(0.6f, 1f, rec.Quality);
            return m;
        }

        /// <summary>Hardware recovered from taking a structure apart. "Skill determines how many nails you recover intact vs bent."</summary>
        public void SalvageHardware(string objectName, float baseUnits, GearItem tool)
        {
            float lvl = Services.Skills.GetLevel01(SkillId.Mechanics);
            float prying = 0.6f;     // bare hands bend most nails
            foreach (var t in new[] { "Prybar", "Hammer" })
                if (GameUtil.CountInInventory(t) > 0) { prying = 1f; break; }
            float intact = Mathf.Clamp01(Mathf.Lerp(0.35f, 0.95f, lvl) * prying);
            int total = Mathf.Max(1, Mathf.RoundToInt(baseUnits));
            int kept = 0;
            for (int i = 0; i < total; i++) if (UnityEngine.Random.value < intact) kept++;
            _hardware += kept;
            GameUtil.Hud(string.Format("Salvaged {0} of {1} nails and fittings intact from {2}.", kept, total, objectName));
            Services.Skills.AddXp(SkillId.Mechanics, 0.5f);
            Services.Skills.AddXp(SkillId.Carpentry, 1f);
        }

        // ============================================================================ plank-processing recipes (game's user-blueprint loader)

        private static readonly string[] ChainBlueprints =
        {
            // Saw softwood into planks: quick, wasteful.
            "{\"RequiredGear\":[{\"Item\":\"GEAR_Softwood\",\"Count\":1}],\"RequiredTool\":\"GEAR_Hacksaw\",\"OptionalTools\":[],\"CraftedResult\":\"GEAR_ReclaimedWoodB\",\"CraftedResultCount\":2,\"DurationMinutes\":45,\"RequiredCraftingLocation\":\"Workbench\",\"AppliedSkill\":\"None\",\"ImprovedSkill\":\"None\"}",
            // Saw hardwood into planks: more yield, longer.
            "{\"RequiredGear\":[{\"Item\":\"GEAR_Hardwood\",\"Count\":1}],\"RequiredTool\":\"GEAR_Hacksaw\",\"OptionalTools\":[],\"CraftedResult\":\"GEAR_ReclaimedWoodB\",\"CraftedResultCount\":3,\"DurationMinutes\":75,\"RequiredCraftingLocation\":\"Workbench\",\"AppliedSkill\":\"None\",\"ImprovedSkill\":\"None\"}",
        };

        public override void OnSceneLoaded(string sceneName)
        {
            if (_chainLoaded || sceneName == null) return;
            try
            {
                var bm = BlueprintManager.Instance;
                if (bm == null || !bm.m_IsInitialized) return;
                int ok = 0;
                foreach (var json in ChainBlueprints)
                    if (bm.LoadUserBlueprint(json)) ok++;
                _chainLoaded = ok > 0;
                MelonLogger.Msg("[Building] loaded " + ok + "/" + ChainBlueprints.Length + " lumber-processing blueprints via the game's user-blueprint loader.");
            }
            catch (Exception e) { MelonLogger.Warning("[Building] could not load lumber blueprints: " + e.Message); }
        }

        // ============================================================================ forge hardware (station menu)

        IEnumerable<BenchEntry> IBenchProvider.Entries(BenchInfo bench)
        {
            if (bench.Kind != BenchKind.Forge) yield break;
            int low = Services.Scrap.GetScrap(ScrapGrade.Low);
            float per = _hardwarePerScrap.Value * Mathf.Lerp(0.6f, 1.3f, Services.Skills.GetLevel01(SkillId.Blacksmithing));
            foreach (int n in new[] { 1, 3 })
            {
                int use = n;
                yield return new BenchEntry
                {
                    Label = string.Format("Forge hardware from {0} scrap", use),
                    Detail = string.Format("~{0:0} nails/hinges, {1} min; hardware now {2}", per * use, 20 * use, _hardware),
                    Enabled = bench.ForgeHot && low >= use,
                    Run = () => ForgeHardware(use, per, bench),
                };
            }
        }

        private void ForgeHardware(int scrapUnits, float perScrap, BenchInfo bench)
        {
            if (!Services.Scrap.TryConsume(ScrapGrade.Low, scrapUnits)) { BenchMenu.Say("You need " + scrapUnits + " low-grade scrap."); return; }
            Action<float> finish = quality =>
            {
                int made = Mathf.Max(1, Mathf.RoundToInt(perScrap * scrapUnits * Mathf.Clamp(quality, 0.2f, 1.2f)));
                _hardware += made;
                Services.Skills.AddXp(SkillId.Blacksmithing, 1f + scrapUnits);
                GameUtil.Hud("Forged " + made + " nails and fittings.", true);
            };
            float minutes = 20f * scrapUnits;
            if (InteractiveForge != null && InteractiveForge("Forging hardware", minutes, finish)) return;
            if (!TimedAction.Run("Forging hardware", minutes, ok =>
            {
                if (ok) finish(1f);
                else { Services.Scrap.Add(ScrapGrade.Low, scrapUnits); GameUtil.Hud("You stop; the scrap goes back in your pack."); }
            })) Services.Scrap.Add(ScrapGrade.Low, scrapUnits);
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { _hardware = 0; _builds.Clear(); }

        JToken ISaveSection.Save()
        {
            var o = new JObject { ["hardware"] = _hardware };
            var b = new JObject();
            foreach (var kv in _builds) b[kv.Key] = new JArray(Math.Round(kv.Value.Quality, 3), kv.Value.Day);
            o["builds"] = b;
            return o;
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            _hardware = (int?)o["hardware"] ?? 0;
            _builds.Clear();
            var b = o["builds"] as JObject;
            if (b != null)
                foreach (var p in b.Properties())
                {
                    var a = p.Value as JArray;
                    if (a != null && a.Count >= 2) _builds[p.Name] = new BuildRecord { Quality = (float)a[0], Day = (int)a[1] };
                }
        }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Hardware (nails, hinges, brackets): {0}   Planks: {1}   Softwood: {2}   Hardwood: {3}",
                _hardware, GameUtil.CountInInventory("ReclaimedWoodB"), GameUtil.CountInInventory("Softwood"), GameUtil.CountInInventory("Hardwood"));
            w.Line("Pieces built with recorded quality: {0}", _builds.Count);
        }
    }
}
