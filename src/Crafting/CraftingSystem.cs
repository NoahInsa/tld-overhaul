using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Gear;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Crafting
{
    public struct RecipeReq
    {
        public SkillId? Skill;
        public int Tier;
    }

    /// <summary>
    /// Crafting as a process. TLD's recipe menu stays, but every blueprint is now a skill-and-tool problem:
    ///  * the recipe belongs to a trade and has a difficulty tier (derived from the blueprint's own data, tunable in recipes.json);
    ///  * you need that tier in that skill (earned by practice and skill books); recipes two tiers above you are not even listed;
    ///  * time scales with tool performance (from the degradation system) and skill (and rust);
    ///  * quality of the product scales with skill, tool and how many times you have made it ("your first bow is garbage,
    ///    your twentieth is reliable"); attempts can fail and then waste part of the materials.
    /// </summary>
    public sealed class CraftingSystem : OverhaulSystem, ICraftingService, ISaveSection
    {
        public static CraftingSystem Instance { get; private set; }
        public override string Name { get { return "Crafting"; } }
        string ISaveSection.Key { get { return "crafting"; } }

        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, RecipeReq> _reqCache = new Dictionary<int, RecipeReq>();
        private string _lastSummary = "";

        /// <summary>Other systems (building) can add requirements. Return a reason string to block the recipe, or null to allow it.</summary>
        public static readonly List<Func<BlueprintData, string>> ExtraGates = new List<Func<BlueprintData, string>>();

        /// <summary>The interactive-crafting system claims recipes it will judge by hand: no random failure roll or quality here.</summary>
        public static Func<BlueprintData, bool> InteractiveClaims;

        public enum Outcome { Unmanaged, Failed, Succeeded }

        /// <summary>Called after every finished craft unit (including furniture). Receives the outcome and any new gear items found in inventory.</summary>
        public static readonly List<Action<CraftingOperation, Outcome, List<GearItem>>> CraftedHooks = new List<Action<CraftingOperation, Outcome, List<GearItem>>>();

        private Setting<float> _failBase, _failPerTier, _failSkillFalloff, _wasteFraction, _qualityJitter, _timeBeginner, _timeMaster, _familiarityHalf;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(PanelCrafting_CanCraftBlueprint);
                yield return typeof(PanelCrafting_CanCraftSelectedBlueprint);
                yield return typeof(BlueprintData_CanCraftBlueprint);
                yield return typeof(PanelCrafting_ItemPassesFilter);
                yield return typeof(PanelCrafting_OnBeginCrafting);
                yield return typeof(PanelCrafting_RefreshSelectedBlueprint);
                yield return typeof(CraftingOperation_GetModifiedCraftingDuration);
                yield return typeof(PanelCrafting_GetFinalCraftingTime);
                yield return typeof(CraftingOperation_HandleSuccess);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _failBase = Cfg.F(Name, "FailureBase", 0.04f, "Failure chance of a tier-0 recipe made at exactly the required skill.");
            _failPerTier = Cfg.F(Name, "FailurePerRecipeTier", 0.04f, "Extra failure chance per tier of recipe difficulty.");
            _failSkillFalloff = Cfg.F(Name, "FailureFalloffPerExtraTier", 0.55f, "Failure is multiplied by this for every tier your skill exceeds the recipe's.");
            _wasteFraction = Cfg.F(Name, "FailedCraftWaste", 0.5f, "Fraction of the materials lost when a craft fails (the rest is salvaged).");
            _qualityJitter = Cfg.F(Name, "QualityJitter", 0.06f, "Random +/- applied to product quality.");
            _timeBeginner = Cfg.F(Name, "TimeScaleBeginner", 1.5f, "Crafting time multiplier at Beginner.");
            _timeMaster = Cfg.F(Name, "TimeScaleMaster", 0.7f, "Crafting time multiplier at Master.");
            _familiarityHalf = Cfg.F(Name, "FamiliarityHalfLifeCrafts", 8f, "Crafts of the same recipe to reach ~63% of the familiarity bonus.");

            RecipeTable.Load();
            SaveManager.Register(this);
            Services.Crafting = this;
        }

        // ============================================================================ requirements

        public RecipeReq Requirement(BlueprintData bp)
        {
            if (bp == null) return default(RecipeReq);
            int key;
            try { key = bp.GetInstanceID(); } catch { return default(RecipeReq); }
            RecipeReq r;
            if (_reqCache.TryGetValue(key, out r)) return r;

            r.Skill = BlueprintInfo.SkillFor(bp);
            r.Tier = BlueprintInfo.TierFor(bp);
            RecipeOverride ov;
            if (RecipeTable.TryGet(BlueprintInfo.ResultName(bp), out ov))
            {
                if (ov.Skill.HasValue) r.Skill = ov.Skill;
                if (ov.HasTier) r.Tier = ov.Tier;
            }
            _reqCache[key] = r;
            return r;
        }

        /// <summary>True when the player's skill is below the recipe tier. The reason string says what is missing.</summary>
        public bool Blocked(BlueprintData bp, out string reason)
        {
            reason = null;
            var req = Requirement(bp);
            if (req.Skill.HasValue)
            {
                int have = Services.Skills.GetTier(req.Skill.Value);
                if (have < req.Tier)
                {
                    reason = string.Format("You are not skilled enough: this needs {0} {1}.", SkillMap.Display(req.Skill.Value), SkillMap.Roman[req.Tier]);
                    return true;
                }
            }
            foreach (var gate in ExtraGates)
            {
                string r;
                try { r = gate(bp); } catch (Exception e) { PatchLog.Error("Crafting.ExtraGate", e); r = null; }
                if (r != null) { reason = r; return true; }
            }
            return false;
        }

        /// <summary>Recipes two or more tiers above you are not yet "discovered".</summary>
        public bool Hidden(BlueprintData bp)
        {
            var req = Requirement(bp);
            if (!req.Skill.HasValue) return false;
            return req.Tier > Services.Skills.GetTier(req.Skill.Value) + 1;
        }

        // ============================================================================ time

        public float TimeScale(SkillId? skill, GearItem tool)
        {
            float perf = tool != null ? Mathf.Clamp(Services.Tools.GetPerformance(tool), 0.3f, 1.2f) : 1f;
            float s = 1f / perf;
            if (skill.HasValue)
            {
                float lvl = Services.Skills.GetLevel01(skill.Value);
                s *= Mathf.Lerp(_timeBeginner.Value, _timeMaster.Value, lvl) / Mathf.Max(0.5f, Services.Skills.GetEfficiency(skill.Value));
            }
            return Mathf.Clamp(s, 0.4f, 3.5f);
        }

        // ============================================================================ ICraftingService

        private float Familiarity(string recipe)
        {
            int n; _counts.TryGetValue(recipe ?? "", out n);
            return 1f - Mathf.Exp(-n / Mathf.Max(1f, _familiarityHalf.Value));
        }

        public float RollQuality(SkillId skill, int recipeTier) { return Quality(skill, recipeTier, 1f, 0f, true); }

        private float Quality(SkillId skill, int recipeTier, float toolPerf, float familiarity, bool jitter)
        {
            int tier = Services.Skills.GetTier(skill);
            float lvl = Services.Skills.GetLevel01(skill);
            float q = 0.40f + 0.45f * lvl + 0.15f * familiarity;
            q -= 0.10f * Mathf.Max(0, recipeTier - tier);
            q *= Mathf.Lerp(0.85f, 1f, Mathf.Clamp01(toolPerf));
            if (jitter) q += UnityEngine.Random.Range(-_qualityJitter.Value, _qualityJitter.Value);
            return Mathf.Clamp(q, 0.2f, 1f);
        }

        public float FailureChance(SkillId skill, int recipeTier, float toolPerformance) { return Failure(skill, recipeTier, toolPerformance, 0f); }

        private float Failure(SkillId skill, int recipeTier, float toolPerf, float familiarity)
        {
            int tier = Services.Skills.GetTier(skill);
            float p = _failBase.Value + _failPerTier.Value * recipeTier;
            int surplus = tier - recipeTier;
            if (surplus > 0) p *= Mathf.Pow(_failSkillFalloff.Value, surplus);
            p += 0.20f * (1f - Mathf.Clamp01(toolPerf));
            p *= 1f - 0.5f * familiarity;
            p /= Mathf.Max(0.5f, Services.Skills.GetEfficiency(skill));
            return Mathf.Clamp(p, 0f, 0.65f);
        }

        // ============================================================================ completion

        /// <summary>Only plain gear products: not food (vanilla cooking already models quality), not furniture (building system).</summary>
        private static bool ManagedResult(BlueprintData bp)
        {
            try
            {
                if (bp.m_CraftingResultType != CraftingResult.StandardGear || bp.m_CraftedResultGear == null) return false;
                if (bp.m_CraftedResultGear.m_FoodItem != null) return false;
                if (bp.m_RequiredCraftingLocation == CraftingLocation.Fire) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool HasCondition(GearItem gi)
        {
            return gi.m_ToolsItem != null || gi.m_ClothingItem != null || gi.m_BowItem != null || gi.m_ArrowItem != null || gi.m_DegradeOnUse != null || gi.m_Bed != null
                   || gi.m_FireStarterItem != null || gi.m_SnareItem != null || gi.m_RopeItem != null;
        }

        public HashSet<int> SnapshotInventory()
        {
            var set = new HashSet<int>();
            foreach (var gi in GameUtil.InventoryItems()) { try { set.Add(gi.GetInstanceID()); } catch { } }
            return set;
        }

        public void AfterCraft(CraftingOperation op, HashSet<int> before)
        {
            var bp = op.Blueprint;
            if (bp == null || before == null) return;

            var made = new List<GearItem>();
            string resultName = BlueprintInfo.ResultName(bp);
            foreach (var gi in GameUtil.InventoryItems())
            {
                int id; try { id = gi.GetInstanceID(); } catch { continue; }
                if (!before.Contains(id) && GameUtil.NameOf(gi).Equals(resultName, StringComparison.OrdinalIgnoreCase)) made.Add(gi);
            }

            Outcome outcome = Outcome.Unmanaged;
            if (ManagedResult(bp) && made.Count > 0) outcome = ProcessManaged(op, bp, resultName, made);
            else if (!ManagedResult(bp)) outcome = Outcome.Succeeded;   // furniture / food: no failure rolls here

            foreach (var h in CraftedHooks)
            {
                try { h(op, outcome, made); }
                catch (Exception e) { PatchLog.Error("Crafting.CraftedHook", e); }
            }
        }

        private Outcome ProcessManaged(CraftingOperation op, BlueprintData bp, string resultName, List<GearItem> made)
        {
            if (InteractiveClaims != null && InteractiveClaims(bp))
            {
                int c; _counts.TryGetValue(resultName, out c); _counts[resultName] = c + 1;
                return Outcome.Succeeded;
            }

            var req = Requirement(bp);
            float fam = Familiarity(resultName);
            GearItem tool = null; try { tool = op.m_Tool; } catch { }
            float perf = tool != null ? Services.Tools.GetPerformance(tool) : 1f;

            SkillId skill = req.Skill ?? SkillId.Carpentry;
            bool hasSkill = req.Skill.HasValue;
            bool failed = hasSkill && UnityEngine.Random.value < Failure(skill, req.Tier, perf, fam);

            if (failed)
            {
                var inv = GameManager.GetInventoryComponent();
                foreach (var gi in made) { try { inv.DestroyGear(gi); } catch (Exception e) { PatchLog.Error("Crafting.destroy", e); } }
                Refund(bp);
                GameUtil.Hud("The " + resultName + " comes out wrong. You salvage some of the materials.", true);
                _lastSummary = resultName + ": failed";
                AwardXp(bp, skill, 0.5f + 0.25f * req.Tier);
                made.Clear();
                return Outcome.Failed;
            }

            float q = hasSkill ? Quality(skill, req.Tier, perf, fam, true) : 1f;
            if (hasSkill)
                foreach (var gi in made)
                    if (HasCondition(gi)) { try { gi.SetNormalizedHP(q, false); } catch { } }

            int n; _counts.TryGetValue(resultName, out n); _counts[resultName] = n + 1;
            if (hasSkill && HasCondition(made[0]))
            {
                GameUtil.Hud(string.Format("{0}: {1} workmanship.", resultName, Workmanship(q)));
                _lastSummary = string.Format("{0}: {1} ({2:0}%)", resultName, Workmanship(q), q * 100f);
            }
            if (hasSkill) AwardXp(bp, skill, 1f + 0.5f * req.Tier);
            return Outcome.Succeeded;
        }

        public void RefundFor(BlueprintData bp) { Refund(bp); }
        public static bool HasConditionPublic(GearItem gi) { return HasCondition(gi); }
        public static string WorkmanshipPublic(float q) { return Workmanship(q); }

        private static string Workmanship(float q)
        {
            if (q < 0.40f) return "crude";
            if (q < 0.60f) return "rough";
            if (q < 0.80f) return "serviceable";
            if (q < 0.95f) return "well-made";
            return "masterwork";
        }

        private static void AwardXp(BlueprintData bp, SkillId skill, float amount)
        {
            // Vanilla's UpdateSkillAfterCrafting already trains the blueprint's vanilla skill; do not double-dip.
            bool vanillaHandles = false;
            try { vanillaHandles = bp.m_ImprovedSkill != SkillType.None && SkillMap.IsVanilla(skill); } catch { }
            if (!vanillaHandles) Services.Skills.AddXp(skill, amount);
        }

        private void Refund(BlueprintData bp)
        {
            try
            {
                if (bp.m_RequiredGear == null) return;
                float keep = 1f - _wasteFraction.Value;
                for (int i = 0; i < bp.m_RequiredGear.Length; i++)
                {
                    var rg = bp.m_RequiredGear[i];
                    if (rg == null || rg.m_Item == null) continue;
                    if (rg.m_Units != BlueprintData.RequiredGearItem.Units.Count) continue; // by-weight materials: no refund
                    int back = Mathf.FloorToInt(rg.m_Count * keep);
                    if (back > 0) GameUtil.GiveItem(GameUtil.NameOf(rg.m_Item), 1f, back);
                }
            }
            catch (Exception e) { PatchLog.Error("Crafting.refund", e); }
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { _counts.Clear(); _reqCache.Clear(); _lastSummary = ""; }

        JToken ISaveSection.Save()
        {
            var o = new JObject();
            foreach (var kv in _counts) o[kv.Key] = kv.Value;
            return o;
        }

        void ISaveSection.Load(JToken t)
        {
            _counts.Clear(); _reqCache.Clear();
            var o = t as JObject; if (o == null) return;
            foreach (var p in o.Properties()) _counts[p.Name] = (int)p.Value;
        }

        public override void OnSceneLoaded(string sceneName) { _reqCache.Clear(); }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Recipes made: {0} kinds, {1} crafts total.", _counts.Count, SumCounts());
            if (_lastSummary.Length > 0) w.Line("Last craft: " + _lastSummary);
        }

        private int SumCounts() { int s = 0; foreach (var v in _counts.Values) s += v; return s; }
    }
}
