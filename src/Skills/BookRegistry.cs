using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using TLDOverhaul.Core;

namespace TLDOverhaul.Skills
{
    /// <summary>Which skill and volume a particular book instance teaches.</summary>
    public struct BookTag
    {
        public SkillId Skill;
        public int Volume;     // 1..5
        public BookTag(SkillId s, int v) { Skill = s; Volume = v; }
        public override string ToString() { return MapName(Skill) + " " + SkillMap.Roman[Math.Max(0, Math.Min(4, Volume - 1))]; }
        private static string MapName(SkillId s) { return SkillMap.Display(s); }
    }

    /// <summary>
    /// Skill books follow PZ's convention: sequential volumes per skill (Cooking I..V). TLD already ships readable books
    /// (ResearchItem); the vanilla prefabs are mapped to volume I/II of their skill. Any further volume is a vanilla
    /// readable prefab tagged by ObjectGuid in the sidecar, so books placed by the exploration system read like real ones.
    /// </summary>
    public static class BookRegistry
    {
        // vanilla prefab name (no GEAR_) -> (skill, volume)
        public static readonly Dictionary<string, BookTag> Vanilla = new Dictionary<string, BookTag>(StringComparer.OrdinalIgnoreCase)
        {
            { "BookFireStarting", new BookTag(SkillId.Firestarting, 1) },
            { "BookCooking", new BookTag(SkillId.Cooking, 1) },
            { "BookCarcassHarvesting", new BookTag(SkillId.CarcassHarvesting, 1) },
            { "BookIceFishing", new BookTag(SkillId.IceFishing, 1) },
            { "BookArchery", new BookTag(SkillId.Archery, 1) },
            { "BookRifleFirearm", new BookTag(SkillId.Rifle, 1) },
            { "BookRifleFirearmAdvanced", new BookTag(SkillId.Rifle, 2) },
            { "BookRevolverFirearm", new BookTag(SkillId.Revolver, 1) },
            { "BookGunsmithing", new BookTag(SkillId.Gunsmithing, 1) },
            { "BookMending", new BookTag(SkillId.Mending, 1) },
            { "BookManual", new BookTag(SkillId.ToolRepair, 1) },
        };

        /// <summary>Vanilla prefab that carries the matching skill's ResearchItem, used as the physical shell of tagged volumes.</summary>
        private static readonly Dictionary<SkillId, string> Shell = new Dictionary<SkillId, string>
        {
            { SkillId.Firestarting, "BookFireStarting" }, { SkillId.Cooking, "BookCooking" }, { SkillId.CarcassHarvesting, "BookCarcassHarvesting" },
            { SkillId.IceFishing, "BookIceFishing" }, { SkillId.Archery, "BookArchery" }, { SkillId.Rifle, "BookRifleFirearm" },
            { SkillId.Revolver, "BookRevolverFirearm" }, { SkillId.Gunsmithing, "BookGunsmithing" }, { SkillId.Mending, "BookMending" },
            { SkillId.ToolRepair, "BookManual" },
        };

        private const string GenericShell = "BookManual";

        public static string ShellFor(SkillId s)
        {
            string n;
            return Shell.TryGetValue(s, out n) ? n : GenericShell;
        }

        public static bool TryVanilla(GearItem gi, out BookTag tag)
        {
            return Vanilla.TryGetValue(GameUtil.NameOf(gi), out tag);
        }
    }
}
