using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Degradation
{
    /// <summary>Per-item degradation history ("companion class tracking per-item degradation history").</summary>
    public sealed class ItemRecord
    {
        public int Repairs;
        /// <summary>Permanent loss to maximum condition, in percentage points (a field repair holds, but costs max durability).</summary>
        public float MaxLoss;
        /// <summary>Total condition points lost to use, after all multipliers. Informational.</summary>
        public float LifetimeWear;
    }

    /// <summary>
    /// Which trade a piece of wear belongs to. Set by the higher-level prefixes (crafting, repair, break-down) so that
    /// the universal GearItem.Degrade patch knows whose skill to apply. Valid for one frame only so it can never leak.
    /// </summary>
    public static class WearContext
    {
        private static SkillId? _skill;
        private static int _frame = -1;

        public static void Set(SkillId? skill) { _skill = skill; _frame = Time.frameCount; }
        public static void Clear() { _skill = null; _frame = -1; }
        public static SkillId? Current { get { return _frame == Time.frameCount ? _skill : null; } }
    }

    public sealed class DegradationSystem : OverhaulSystem, ISaveSection, IToolService
    {
        public static DegradationSystem Instance { get; private set; }
        public override string Name { get { return "Degradation"; } }
        string ISaveSection.Key { get { return "degradation"; } }

        private readonly Dictionary<string, ItemRecord> _records = new Dictionary<string, ItemRecord>();

        // ---- tuning (MelonPreferences)
        private Setting<float> _curveK, _curvePower, _misuse, _skillWorst, _skillBest, _lossBase, _lossBest, _minCap, _repairSkillWorst, _repairSkillBest;

        public override System.Collections.Generic.IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(GearItem_Degrade);
                yield return typeof(CraftingOperation_DegradeTools);
                yield return typeof(Panel_Repair_RepairSuccessful);
                yield return typeof(Repairable_GetRepairConditionCap);
                yield return typeof(Panel_Repair_GetChanceSuccess);
                yield return typeof(Panel_Repair_GetModifiedRepairDuration);
                yield return typeof(Panel_BreakDown_DegradeToolUsed);
                yield return typeof(Panel_Repair_DegradeToolUsedForRepair);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _curveK = Cfg.F(Name, "WearCurveK", 1.2f, "Extra wear multiplier at 0% condition is 1+K. Wear accelerates as an item degrades.");
            _curvePower = Cfg.F(Name, "WearCurvePower", 2f, "Shape of the acceleration (1 = linear ramp, 2 = back-loaded).");
            _misuse = Cfg.F(Name, "MisuseMultiplier", 1.6f, "Wear multiplier when a cutting tool is used outside its trade (e.g. a knife to pry or chop).");
            _skillWorst = Cfg.F(Name, "SkillWearBeginner", 1.5f, "Wear multiplier at the lowest skill level (a novice dulls a knife faster).");
            _skillBest = Cfg.F(Name, "SkillWearMaster", 0.7f, "Wear multiplier at Master.");
            _lossBase = Cfg.F(Name, "RepairMaxLossBeginner", 6f, "Percentage points of max condition permanently lost per repair at Beginner skill.");
            _lossBest = Cfg.F(Name, "RepairMaxLossMaster", 1.5f, "Percentage points of max condition permanently lost per repair at Master skill.");
            _minCap = Cfg.F(Name, "MaxConditionFloor", 25f, "An item can never lose more max condition than this floor allows (percent).");
            _repairSkillWorst = Cfg.F(Name, "RepairGainBeginner", 0.7f, "Fraction of the normal repair gain you actually get at Beginner.");
            _repairSkillBest = Cfg.F(Name, "RepairGainMaster", 1.1f, "Fraction of the normal repair gain you get at Master.");

            SaveManager.Register(this);
            Services.Tools = this;
        }

        // ------------------------------------------------------------------------------- IToolService

        public ToolGrade GetGrade(GearItem tool) { return ToolProfiles.GradeOf(tool); }

        public float GetPerformance(GearItem tool)
        {
            if (tool == null) return 1f;
            var p = ToolProfiles.For(GetGrade(tool));
            float cond = Mathf.Clamp01(tool.GetNormalizedCondition());
            // Full performance above the grade's knee, then a smooth fall to the floor at 0%.
            float t = p.FullPerformanceAbove <= 0.001f ? 1f : Mathf.Clamp01(cond / p.FullPerformanceAbove);
            t = t * t * (3f - 2f * t); // smoothstep
            float perf = Mathf.Lerp(p.PerformanceFloor, 1f, t);
            return perf * p.PerformanceScale;
        }

        public float GetRepairCeiling(GearItem tool)
        {
            if (tool == null) return 100f;
            var p = ToolProfiles.For(GetGrade(tool));
            // A beaten-up tool cannot do work better than itself.
            float perf = Mathf.Clamp(GetPerformance(tool) / Mathf.Max(0.01f, p.PerformanceScale), 0.5f, 1f);
            return Mathf.Lerp(p.RepairCeiling * 0.7f, p.RepairCeiling, (perf - 0.5f) * 2f);
        }

        // ------------------------------------------------------------------------------- wear model

        /// <summary>Rewrites a point of wear. Called from the GearItem.Degrade prefix.</summary>
        /// <summary>Other systems can scale wear on items this system does not manage (weapon mods slow gun/bow wear).</summary>
        public static Func<GearItem, float> ExtraWear;

        public float AdjustWear(GearItem item, float hp)
        {
            if (hp <= 0f || item == null || !GameUtil.InGame) return hp;
            if (ExtraWear != null) { try { hp *= ExtraWear(item); } catch { } }
            if (!ToolProfiles.IsManagedTool(item)) return hp;

            var grade = ToolProfiles.For(GetGrade(item));
            float cond = Mathf.Clamp01(item.GetNormalizedCondition());
            float curve = 1f + _curveK.Value * Mathf.Pow(1f - cond, _curvePower.Value);

            SkillId? activity = WearContext.Current ?? ToolProfiles.DefaultActivity(item);
            float skillMult = 1f;
            float usage = 1f;
            if (activity.HasValue)
            {
                float lvl = Services.Skills.GetLevel01(activity.Value);
                skillMult = Mathf.Lerp(_skillWorst.Value, _skillBest.Value, lvl);
                usage = ToolProfiles.UsageMultiplier(item, activity, _misuse.Value);
            }

            float adjusted = hp * grade.WearMultiplier * curve * skillMult * usage;

            var rec = Record(item, true);
            if (rec != null) rec.LifetimeWear += adjusted;
            return adjusted;
        }

        public float MaxConditionPercent(GearItem item)
        {
            var rec = Record(item, false);
            if (rec == null) return 100f;
            return Mathf.Max(_minCap.Value, 100f - rec.MaxLoss);
        }

        /// <summary>Called after a successful repair has been applied by vanilla: rescale the gain, then apply caps and history.</summary>
        public void ApplyRepairOutcome(GearItem item, GearItem tool, float hpBefore)
        {
            if (item == null) return;
            float maxHp = item.GearItemData != null ? item.GearItemData.m_MaxHP : 100f;
            if (maxHp <= 0.01f) maxHp = 100f;

            SkillId skill = (item.m_ClothingItem != null) ? SkillId.Mending : SkillId.ToolRepair;
            float lvl = Services.Skills.GetLevel01(skill);
            float toolPerf = tool != null ? Mathf.Clamp(GetPerformance(tool), 0.3f, 1.15f) : 1f;

            float gain = item.m_CurrentHP - hpBefore;
            if (gain > 0f)
            {
                gain *= Mathf.Lerp(_repairSkillWorst.Value, _repairSkillBest.Value, lvl);
                gain *= Mathf.Lerp(0.8f, 1f, Mathf.Clamp01(toolPerf));
            }

            float ceilingPct = tool != null ? GetRepairCeiling(tool) : 100f;
            var rec = Record(item, true);
            float capPct = Mathf.Min(ceilingPct, rec != null ? Mathf.Max(_minCap.Value, 100f - rec.MaxLoss) : 100f);

            float newHp = Mathf.Min(hpBefore + gain, maxHp * capPct / 100f);
            // Never make an item worse than it was before the repair.
            newHp = Mathf.Max(newHp, Mathf.Min(hpBefore, item.m_CurrentHP));
            item.m_CurrentHP = newHp;

            if (rec != null)
            {
                rec.Repairs++;
                float loss = Mathf.Lerp(_lossBase.Value, _lossBest.Value, lvl);
                // Improvised kit repairs hold less well.
                if (tool != null && GetGrade(tool) == ToolGrade.Improvised) loss *= 1.5f;
                rec.MaxLoss += loss;
                float newCapPct = Mathf.Max(_minCap.Value, 100f - rec.MaxLoss);
                if (item.m_CurrentHP > maxHp * newCapPct / 100f) item.m_CurrentHP = maxHp * newCapPct / 100f;
                GameUtil.Hud(string.Format("Field repair holds. {0}: best possible condition is now {1:0}%.", GameUtil.NameOf(item), newCapPct));
            }
            try { item.UpdateDamageShader(); } catch { }
            Services.Skills.AddXp(skill, 4f);
        }

        // ------------------------------------------------------------------------------- records

        private ItemRecord Record(GearItem item, bool create)
        {
            string g = GameUtil.GuidOf(item);
            if (g == null) return null;
            ItemRecord r;
            if (_records.TryGetValue(g, out r)) return r;
            if (!create) return null;
            r = new ItemRecord();
            _records[g] = r;
            return r;
        }

        public ItemRecord PeekRecord(GearItem item) { return Record(item, false); }

        // ------------------------------------------------------------------------------- ISaveSection

        void ISaveSection.Reset() { _records.Clear(); }

        JToken ISaveSection.Save()
        {
            var o = new JObject();
            foreach (var kv in _records)
            {
                var r = kv.Value;
                if (r.Repairs == 0 && r.MaxLoss <= 0f && r.LifetimeWear < 0.5f) continue;
                o[kv.Key] = new JArray(r.Repairs, Math.Round(r.MaxLoss, 2), Math.Round(r.LifetimeWear, 2));
            }
            return o;
        }

        void ISaveSection.Load(JToken t)
        {
            _records.Clear();
            var o = t as JObject;
            if (o == null) return;
            foreach (var p in o.Properties())
            {
                var a = p.Value as JArray;
                if (a == null || a.Count < 3) continue;
                _records[p.Name] = new ItemRecord { Repairs = (int)a[0], MaxLoss = (float)a[1], LifetimeWear = (float)a[2] };
            }
        }

        // ------------------------------------------------------------------------------- status

        public override void DrawStatus(StatusWriter w)
        {
            int shown = 0;
            foreach (var gi in GameUtil.InventoryItems())
            {
                if (!ToolProfiles.IsManagedTool(gi)) continue;
                var rec = Record(gi, false);
                w.Line("{0}: {1}, cond {2:0}%, perf {3:0.00}{4}", GameUtil.NameOf(gi), GetGrade(gi), gi.GetNormalizedCondition() * 100f, GetPerformance(gi),
                    rec != null && rec.Repairs > 0 ? string.Format(", repaired {0}x (max {1:0}%)", rec.Repairs, MaxConditionPercent(gi)) : "");
                if (++shown >= 12) break;
            }
            if (shown == 0) w.Line("No tools in inventory.");
        }
    }
}
