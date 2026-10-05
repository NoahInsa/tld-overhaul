using System;
using System.Collections.Generic;
using Il2Cpp;
using TLDOverhaul.Core;

namespace TLDOverhaul.Weapons
{
    public enum AmmoKind { Fmj = 0, HollowPoint = 1, SoftPoint = 2 }

    public enum ModId
    {
        Scope, LightStock, RecoilPad, ForgedReceiver, Suppressor, ExtendedCylinder,   // firearms
        ImprovedLimbs, ReinforcedString,                                              // bow
    }

    public enum WeaponClass { Rifle, Revolver, Bow }

    /// <summary>One weapon modification, matching the design table. Effects are expressed as multipliers on the weapon's own fields.</summary>
    public sealed class ModSpec
    {
        public ModId Id;
        public string Name;
        public WeaponClass[] Fits;
        public int Tier;                    // Gunsmithing / Archery tier needed to install (and to craft where craftable)
        public string Source;               // text for the status line
        public bool Craftable;
        public BenchKind Station;           // where it is crafted
        public SkillId CraftSkill;
        public int CraftTier;
        public bool NeedsIndustrialForge;
        public string[] Materials;          // "Name:count" gear materials
        public string[] Regional;           // "material:units" that only exist in particular regions (exploration)
        public ScrapGrade? Scrap; public int ScrapUnits;
    }

    public static class WeaponTables
    {
        public static readonly Dictionary<ModId, ModSpec> Mods = new Dictionary<ModId, ModSpec>
        {
            { ModId.Scope, new ModSpec { Id = ModId.Scope, Name = "Rifle scope", Fits = new[] { WeaponClass.Rifle }, Tier = 2, Source = "loot find (rare)" } },
            { ModId.LightStock, new ModSpec { Id = ModId.LightStock, Name = "Lighter stock", Fits = new[] { WeaponClass.Rifle }, Tier = 2, Craftable = true, Station = BenchKind.Workbench,
                CraftSkill = SkillId.Carpentry, CraftTier = 3, Materials = new[] { "Hardwood:2", "ReclaimedWoodB:2" }, Source = "crafted at high carpentry from quality wood" } },
            { ModId.RecoilPad, new ModSpec { Id = ModId.RecoilPad, Name = "Recoil pad", Fits = new[] { WeaponClass.Rifle, WeaponClass.Revolver }, Tier = 1, Craftable = true, Station = BenchKind.Workbench,
                CraftSkill = SkillId.Mending, CraftTier = 1, Materials = new[] { "Leather:1" }, Source = "loot, or crafted from leather" } },
            { ModId.ForgedReceiver, new ModSpec { Id = ModId.ForgedReceiver, Name = "Forged receiver", Fits = new[] { WeaponClass.Rifle }, Tier = 3, Craftable = true, Station = BenchKind.Forge,
                CraftSkill = SkillId.Blacksmithing, CraftTier = 3, NeedsIndustrialForge = true, Scrap = ScrapGrade.High, ScrapUnits = 3, Regional = new[] { "cast_iron:2" }, Source = "forged with high mechanics skill" } },
            { ModId.Suppressor, new ModSpec { Id = ModId.Suppressor, Name = "Improvised suppressor", Fits = new[] { WeaponClass.Rifle }, Tier = 3, Craftable = true, Station = BenchKind.Workbench,
                CraftSkill = SkillId.Mechanics, CraftTier = 3, Materials = new[] { "Cloth:2" }, Scrap = ScrapGrade.Mid, ScrapUnits = 2, Regional = new[] { "aircraft_aluminum:1" }, Source = "improvised at high mechanics; degrades fast" } },
            { ModId.ExtendedCylinder, new ModSpec { Id = ModId.ExtendedCylinder, Name = "Extended cylinder", Fits = new[] { WeaponClass.Revolver }, Tier = 2, Source = "loot find (very rare)" } },
            { ModId.ImprovedLimbs, new ModSpec { Id = ModId.ImprovedLimbs, Name = "Improved limbs", Fits = new[] { WeaponClass.Bow }, Tier = 2, Craftable = true, Station = BenchKind.Workbench,
                CraftSkill = SkillId.Archery, CraftTier = 3, Materials = new[] { "Hardwood:2", "GutDried:2" }, Source = "crafted at high archery + carpentry" } },
            { ModId.ReinforcedString, new ModSpec { Id = ModId.ReinforcedString, Name = "Reinforced string", Fits = new[] { WeaponClass.Bow }, Tier = 1, Craftable = true, Station = BenchKind.Workbench,
                CraftSkill = SkillId.Mending, CraftTier = 2, Materials = new[] { "GutDried:2" }, Source = "crafted from sinew" } },
        };

        public static WeaponClass? ClassOf(GearItem gi)
        {
            if (gi == null) return null;
            try
            {
                if (gi.m_GunItem != null)
                {
                    if (gi.m_GunItem.m_GunType == GunType.Rifle) return WeaponClass.Rifle;
                    if (gi.m_GunItem.m_GunType == GunType.Revolver) return WeaponClass.Revolver;
                    return null;     // flare gun / camera: no overhaul upkeep
                }
                if (gi.m_BowItem != null) return WeaponClass.Bow;
            }
            catch { }
            return null;
        }

        public static string AmmoName(AmmoKind k)
        {
            switch (k) { case AmmoKind.HollowPoint: return "hollow-point"; case AmmoKind.SoftPoint: return "soft-point"; default: return "FMJ"; }
        }

        // ---- ammunition vs. target. "Ammo selection becomes a real decision": burning hollow points on a bear is wasteful.
        // Damage multipliers by (ammo, victim). Rows: Fmj, HollowPoint, SoftPoint.  Columns: Rabbit, Wolf, Stag, Moose, Bear, Cougar.
        private static readonly float[][] BulletVs =
        {
            new[] { 0.90f, 0.95f, 1.00f, 1.00f, 1.00f, 1.00f },   // FMJ: good penetration, passes through small game wastefully
            new[] { 1.30f, 1.20f, 1.00f, 0.80f, 0.65f, 0.90f },   // hollow point: expands - ideal small/medium, useless against a skull
            new[] { 1.00f, 1.05f, 1.20f, 1.15f, 1.10f, 1.10f },   // soft point: controlled expansion, the large-game round
        };

        // Arrows: Arrow = broadhead (steel head), HardenedArrow = field point.
        private static readonly float[][] ArrowVs =
        {
            new[] { 1.00f, 1.10f, 1.25f, 1.25f, 1.20f, 1.15f },   // broadhead: more bleed damage on large game
            new[] { 1.00f, 0.75f, 0.65f, 0.60f, 0.60f, 0.65f },   // field point: small game and practice
        };

        private static int Col(AiSubType t)
        {
            switch (t)
            {
                case AiSubType.Rabbit: return 0;
                case AiSubType.Wolf: return 1;
                case AiSubType.Stag: return 2;
                case AiSubType.Moose: return 3;
                case AiSubType.Bear: return 4;
                case AiSubType.Cougar: return 5;
                default: return 2;
            }
        }

        public static float BulletFactor(AmmoKind k, AiSubType victim) { return BulletVs[(int)k][Col(victim)]; }
        public static float ArrowFactor(bool broadhead, AiSubType victim) { return ArrowVs[broadhead ? 0 : 1][Col(victim)]; }
    }
}
