using System;
using System.Collections.Generic;
using Il2Cpp;
using TLDOverhaul.Core;

namespace TLDOverhaul.Degradation
{
    /// <summary>
    /// How a grade of tool behaves. "Finding a high-quality axe is a genuine event because its performance curve is
    /// meaningfully different from the hatchet you carved yourself": the three profiles differ in wear rate, in how long
    /// they stay at full performance as they degrade, in the floor they sink to, and in the best repair they can reach.
    /// </summary>
    public sealed class GradeProfile
    {
        public float WearMultiplier;        // multiplies every point of wear
        public float PerformanceScale;      // multiplies performance at any condition (HighQuality can exceed 1)
        public float FullPerformanceAbove;  // normalized condition at/above which the tool performs at 100% of its scale
        public float PerformanceFloor;      // performance at 0% condition (relative to scale)
        public float RepairCeiling;         // highest condition (percent) a repair made WITH this tool can restore to
    }

    public static class ToolProfiles
    {
        public static readonly GradeProfile Improvised = new GradeProfile
        { WearMultiplier = 1.8f, PerformanceScale = 0.85f, FullPerformanceAbove = 0.85f, PerformanceFloor = 0.25f, RepairCeiling = 70f };
        public static readonly GradeProfile Manufactured = new GradeProfile
        { WearMultiplier = 1.0f, PerformanceScale = 1.0f, FullPerformanceAbove = 0.70f, PerformanceFloor = 0.35f, RepairCeiling = 90f };
        public static readonly GradeProfile HighQuality = new GradeProfile
        { WearMultiplier = 0.55f, PerformanceScale = 1.15f, FullPerformanceAbove = 0.45f, PerformanceFloor = 0.55f, RepairCeiling = 100f };

        public static GradeProfile For(ToolGrade g)
        {
            switch (g)
            {
                case ToolGrade.Improvised: return Improvised;
                case ToolGrade.HighQuality: return HighQuality;
                default: return Manufactured;
            }
        }

        // Names are prefab names without GEAR_. Substring match, case-insensitive.
        private static readonly string[] ImprovisedHints = { "improvised" };
        private static readonly string[] HighQualityHints = { "highquality", "fireaxe", "fellingaxe", "felling", "bucksaw" };

        public static ToolGrade GradeOf(GearItem gi)
        {
            string n = GameUtil.NameOf(gi).ToLowerInvariant();
            foreach (var h in HighQualityHints) if (n.Contains(h)) return ToolGrade.HighQuality;
            foreach (var h in ImprovisedHints) if (n.Contains(h)) return ToolGrade.Improvised;
            return ToolGrade.Manufactured;
        }

        /// <summary>
        /// Items whose wear the system manages: anything with a ToolsItem component plus a few consumable-tool prefabs
        /// that vanilla does not tag as tools.
        /// </summary>
        public static bool IsManagedTool(GearItem gi)
        {
            if (gi == null) return false;
            try
            {
                if (gi.m_ToolsItem != null) return true;
                if (gi.m_Sharpenable != null && gi.m_DegradeOnUse != null) return true;
            }
            catch { }
            string n = GameUtil.NameOf(gi).ToLowerInvariant();
            return n.Contains("sharpeningstone") || n.Contains("sewingkit") || n.Contains("crampons") || n.Contains("firestriker");
        }

        // ---- "what you use them for": a tool used outside its intended trade wears faster.

        private static readonly Dictionary<ToolsItem.CuttingToolType, SkillId[]> Domains =
            new Dictionary<ToolsItem.CuttingToolType, SkillId[]>
            {
                { ToolsItem.CuttingToolType.Knife, new[] { SkillId.CarcassHarvesting, SkillId.Tanning, SkillId.Mending, SkillId.Foraging, SkillId.Firestarting, SkillId.Cooking, SkillId.FirstAid, SkillId.IceFishing } },
                { ToolsItem.CuttingToolType.Hatchet, new[] { SkillId.Carpentry, SkillId.CarcassHarvesting, SkillId.Firestarting } },
                { ToolsItem.CuttingToolType.Hammer, new[] { SkillId.Carpentry, SkillId.Blacksmithing, SkillId.Mechanics, SkillId.ToolRepair } },
                { ToolsItem.CuttingToolType.HackSaw, new[] { SkillId.Mechanics, SkillId.Carpentry, SkillId.Blacksmithing, SkillId.ToolRepair, SkillId.Gunsmithing } },
            };

        /// <summary>1.0 when the tool is used within its trade (or the trade is unknown), otherwise the misuse multiplier.</summary>
        public static float UsageMultiplier(GearItem tool, SkillId? activity, float misuse)
        {
            if (!activity.HasValue || tool == null || tool.m_ToolsItem == null) return 1f;
            SkillId[] domain;
            if (!Domains.TryGetValue(tool.m_ToolsItem.m_CuttingToolType, out domain)) return 1f;
            return Array.IndexOf(domain, activity.Value) >= 0 ? 1f : misuse;
        }

        /// <summary>Default trade for a tool when the call site gives no context (hatchet in hand while breaking down = carpentry ...).</summary>
        public static SkillId? DefaultActivity(GearItem tool)
        {
            if (tool == null || tool.m_ToolsItem == null) return null;
            switch (tool.m_ToolsItem.m_CuttingToolType)
            {
                case ToolsItem.CuttingToolType.Knife: return SkillId.CarcassHarvesting;
                case ToolsItem.CuttingToolType.Hatchet: return SkillId.Carpentry;
                case ToolsItem.CuttingToolType.Hammer: return SkillId.Carpentry;
                case ToolsItem.CuttingToolType.HackSaw: return SkillId.Mechanics;
                default: return null;
            }
        }
    }
}
