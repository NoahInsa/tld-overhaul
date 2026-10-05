using Il2Cpp;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// Every skill the overhaul knows about. Where TLD already has a skill the id wraps it (state lives in the
    /// vanilla Skill component); otherwise the overhaul stores it in its own sidecar section. Tiers are I..V
    /// (Beginner..Master), matching TLD's SkillTiers.
    /// </summary>
    public enum SkillId
    {
        Firestarting, Cooking, Rifle, Revolver, Archery, Mending, ToolRepair, Gunsmithing, CarcassHarvesting, IceFishing,
        // overhaul-owned
        Carpentry, Mechanics, Blacksmithing, Tanning, Foraging, FirstAid, Skiing
    }

    public enum ToolGrade { Improvised = 0, Manufactured = 1, HighQuality = 2 }

    /// <summary>Skills + book gating (system 4). Consumed by crafting, forge, building, interactive, weapons, vehicles, skiing.</summary>
    public interface ISkillService
    {
        /// <summary>0..4 (Beginner..Master).</summary>
        int GetTier(SkillId id);
        /// <summary>Continuous 0..1 skill level (tier + progress through tier), used for interpolation.</summary>
        float GetLevel01(SkillId id);
        /// <summary>Practice-efficiency multiplier after skill rust (1.0 = no rust). Applies within the current tier only.</summary>
        float GetEfficiency(SkillId id);
        /// <summary>Practice XP. Cannot cross the current tier ceiling unless the matching book volume has been read.</summary>
        void AddXp(SkillId id, float amount);
        bool HasReadBook(SkillId id, int volume);
        /// <summary>Highest tier index the player may currently reach (book-gated).</summary>
        int GetTierCeiling(SkillId id);
    }

    /// <summary>Tool condition / quality (system 1), exposed to crafting and anything else that uses tools.</summary>
    public interface IToolService
    {
        ToolGrade GetGrade(GearItem tool);
        /// <summary>0..1.2 multiplicative performance of a tool at its current condition (speed + success).</summary>
        float GetPerformance(GearItem tool);
        /// <summary>Highest condition (0..100) that a repair performed with this tool can restore an item to.</summary>
        float GetRepairCeiling(GearItem tool);
    }

    /// <summary>Forge recycling (system 6): scrap stock consumed by building, weapons, vehicles.</summary>
    public interface IScrapService
    {
        int GetScrap(ScrapGrade grade);
        bool TryConsume(ScrapGrade grade, int units);
        void Add(ScrapGrade grade, int units);
    }

    public enum ScrapGrade { Low = 0, Mid = 1, High = 2 }

    /// <summary>Crafting (system 5): skill-scaled results shared by interactive crafting and building.</summary>
    public interface ICraftingService
    {
        /// <summary>Condition fraction (0..1) a freshly crafted item should have for a skill and recipe difficulty tier.</summary>
        float RollQuality(SkillId skill, int recipeTier);
        /// <summary>Probability (0..1) that an attempt fails for this skill/recipe pairing.</summary>
        float FailureChance(SkillId skill, int recipeTier, float toolPerformance);
    }

    /// <summary>Service locator. Consumers always get a working (neutral) implementation even when a system is disabled.</summary>
    public static class Services
    {
        public static ISkillService Skills = new NullSkills();
        public static IToolService Tools = new NullTools();
        public static IScrapService Scrap = new NullScrap();
        public static ICraftingService Crafting = new NullCrafting();

        private sealed class NullSkills : ISkillService
        {
            public int GetTier(SkillId id) => 4; // no skills system: gates must not block
            public float GetLevel01(SkillId id) => 0.5f;
            public float GetEfficiency(SkillId id) => 1f;
            public void AddXp(SkillId id, float amount) { }
            public bool HasReadBook(SkillId id, int volume) => true;
            public int GetTierCeiling(SkillId id) => 4;
        }
        private sealed class NullTools : IToolService
        {
            public ToolGrade GetGrade(GearItem tool) => ToolGrade.Manufactured;
            public float GetPerformance(GearItem tool) => 1f;
            public float GetRepairCeiling(GearItem tool) => 100f;
        }
        private sealed class NullScrap : IScrapService
        {
            public int GetScrap(ScrapGrade grade) => 0;
            public bool TryConsume(ScrapGrade grade, int units) => false;
            public void Add(ScrapGrade grade, int units) { }
        }
        private sealed class NullCrafting : ICraftingService
        {
            public float RollQuality(SkillId skill, int recipeTier) => 1f;
            public float FailureChance(SkillId skill, int recipeTier, float toolPerformance) => 0f;
        }
    }
}
