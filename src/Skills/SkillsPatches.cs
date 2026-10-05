using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Skills
{
    // The skill XP choke point (13 callers). PREFIX because XP must be scaled by rust and clamped to the tier ceiling BEFORE
    // vanilla applies it, and a fully blocked grant must also suppress vanilla's "skill increased" notification (return false).
    [HarmonyPatch(typeof(SkillsManager), nameof(SkillsManager.IncrementPointsAndNotify))]
    internal static class SkillsManager_IncrementPointsAndNotify
    {
        private const string Id = "Skills.SkillsManager_IncrementPointsAndNotify";
        private static bool Prefix(SkillType skillType, ref int numPoints)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkillsSystem.Instance;
                if (sys == null || SkillsSystem.Internal) return true;   // our own call: already filtered
                numPoints = sys.FilterVanillaIncrement(skillType, numPoints);
                return numPoints > 0;
            }
            catch (Exception e) { PatchLog.Error(Id, e); return true; }
        }
    }

    // Belt-and-braces for callers that bypass the manager (cc=0, possibly inlined). Only clamps; scaling happens once above.
    [HarmonyPatch(typeof(Skill), nameof(Skill.IncrementPoints))]
    internal static class Skill_IncrementPoints
    {
        private const string Id = "Skills.Skill_IncrementPoints";
        private static void Prefix(Skill __instance, ref int increase)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkillsSystem.Instance;
                if (sys == null || SkillsSystem.Internal) return;
                increase = sys.ClampToCap(__instance.m_SkillType, increase);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Finishing a book. PREFIX: the volume must be on record BEFORE vanilla grants points, and the book's own XP grant is
    // zeroed (a book is a key, not an XP potion). POSTFIX restores the field so the prefab data is unchanged.
    [HarmonyPatch(typeof(ResearchItem), nameof(ResearchItem.OnResearchComplete))]
    internal static class ResearchItem_OnResearchComplete
    {
        private const string Id = "Skills.ResearchItem_OnResearchComplete";

        private static void Prefix(ResearchItem __instance, out int __state)
        {
            PatchLog.Fire(Id);
            __state = -1;
            try
            {
                var sys = SkillsSystem.Instance;
                if (sys == null) return;
                var gi = GameUtil.GearFromGameObject(__instance.gameObject);
                BookTag tag;
                if (gi == null || !sys.TryGetTag(gi, out tag))
                {
                    // An unmapped readable: fall back to its vanilla skill as volume I.
                    SkillId sid;
                    if (SkillMap.TryFromVanilla(__instance.m_SkillType, out sid)) tag = new BookTag(sid, 1);
                    else return;
                }
                sys.MarkRead(tag);
                __state = __instance.m_SkillPoints;
                __instance.m_SkillPoints = 0;

                // Reading is time spent not being alone with your thoughts: it eases cabin fever (mental-health hook).
                try { GameManager.GetCabinFeverComponent().ApplyCabinFeverReductionBuff(__instance.m_TimeRequirementHours * 0.5f, 6f); } catch { }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }

        private static void Postfix(ResearchItem __instance, int __state)
        {
            try { if (__state >= 0) __instance.m_SkillPoints = __state; }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Vanilla refuses to let you read a book once you are past its level; here the gate is the volume. cc=0 (may be inlined).
    [HarmonyPatch(typeof(ResearchItem), nameof(ResearchItem.NoBenefitAtCurrentSkillLevel))]
    internal static class ResearchItem_NoBenefit
    {
        private const string Id = "Skills.ResearchItem_NoBenefit";
        private static void Postfix(ResearchItem __instance, ref bool __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkillsSystem.Instance;
                if (sys == null) return;
                var gi = GameUtil.GearFromGameObject(__instance.gameObject);
                BookTag tag;
                if (gi != null && sys.TryGetTag(gi, out tag)) __result = sys.HasReadBook(tag.Skill, tag.Volume);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Show the volume on the book: "Carpentry II". Tagged volumes replace the shell's name; vanilla books get a suffix.
    [HarmonyPatch(typeof(GearItem), nameof(GearItem.GetBasicDisplayNameForInventoryInterfaces))]
    internal static class GearItem_BasicDisplayName_Book
    {
        private const string Id = "Skills.GearItem_BasicDisplayName_Book";
        private static void Postfix(GearItem __instance, ref string __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkillsSystem.Instance;
                if (sys == null || __instance.m_ResearchItem == null) return;
                BookTag tag;
                if (!sys.TryGetTag(__instance, out tag)) return;
                __result = sys.IsTagged(__instance) ? tag.ToString() : (__result + " [" + tag + "]");
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
