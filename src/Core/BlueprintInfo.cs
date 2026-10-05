using System;
using Il2Cpp;
using Il2CppTLD.Gear;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// Answers "which trade is this blueprint, and how hard is it?" from the data TLD already ships on BlueprintData
    /// (applied skill, workstation, duration, material count, result type). The crafting overhaul skill-gates the whole
    /// recipe set from this, without needing a hand-written entry for every blueprint.
    /// </summary>
    public static class BlueprintInfo
    {
        public static string ResultName(BlueprintData bp)
        {
            try
            {
                if (bp.m_CraftedResultGear != null) return GameUtil.NameOf(bp.m_CraftedResultGear);
                return GameUtil.Normalize(bp.name);
            }
            catch { return ""; }
        }

        public static SkillId? SkillFor(BlueprintData bp)
        {
            if (bp == null) return null;
            try
            {
                SkillId id;
                if (bp.m_AppliedSkill != SkillType.None && SkillMap.TryFromVanilla(bp.m_AppliedSkill, out id)) return id;
                if (bp.m_ImprovedSkill != SkillType.None && SkillMap.TryFromVanilla(bp.m_ImprovedSkill, out id)) return id;

                string n = ResultName(bp).ToLowerInvariant();
                if (n.Contains("leather") || n.Contains("hide") || n.Contains("pelt")) return SkillId.Tanning;
                if (n.Contains("arrow") || n.Contains("bow")) return SkillId.Archery;
                if (n.Contains("bullet") || n.Contains("ammo") || n.Contains("cartridge")) return SkillId.Gunsmithing;
                if (bp.m_CraftedResultGear != null && bp.m_CraftedResultGear.m_ClothingItem != null) return SkillId.Mending;

                switch (bp.m_RequiredCraftingLocation)
                {
                    case CraftingLocation.Forge: return SkillId.Blacksmithing;
                    case CraftingLocation.AmmoWorkbench: return SkillId.Gunsmithing;
                    case CraftingLocation.Workbench: return SkillId.Carpentry;
                    case CraftingLocation.FurnitureWorkbench: return SkillId.Carpentry;
                    case CraftingLocation.Fire: return SkillId.Cooking;
                }
                if (bp.m_CraftedResultDecoration != null) return SkillId.Carpentry;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Difficulty tier 0..4 derived from duration and material count. Long, material-hungry recipes are higher tier.
        /// Callers may override per recipe name.
        /// </summary>
        public static int TierFor(BlueprintData bp)
        {
            if (bp == null) return 0;
            try
            {
                int minutes = bp.m_DurationMinutes;
                int mats = bp.m_RequiredGear != null ? bp.m_RequiredGear.Length : 0;
                float score = minutes / 90f + mats * 0.35f;
                if (bp.m_RequiredCraftingLocation == CraftingLocation.Forge || bp.m_RequiredCraftingLocation == CraftingLocation.AmmoWorkbench) score += 1f;
                if (bp.m_RequiredTool != null) score += 0.3f;
                int tier = (int)Math.Floor(score);
                return tier < 0 ? 0 : (tier > 4 ? 4 : tier);
            }
            catch { return 0; }
        }
    }
}
