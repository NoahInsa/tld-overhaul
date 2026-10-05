using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using TLDOverhaul.Forge;
using UnityEngine;

namespace TLDOverhaul.Building
{
    internal sealed class SalvageState
    {
        public int[] Original;
        public BuildRecord Record;
        public GearItem Tool;
    }

    // Taking a structure apart. PREFIX returns false for forges/ammo benches (fixed geography) and otherwise scales the yield
    // units by skill, tool condition and the piece's build quality for the duration of vanilla's call; POSTFIX restores the
    // prefab data and adds recovered hardware and carpentry XP.
    [HarmonyPatch(typeof(BreakDown), nameof(BreakDown.DoBreakDown))]
    internal static class BreakDown_Salvage
    {
        private const string Id = "Building.BreakDown_Salvage";

        // wooden-structure hardware content (units of nails/fittings); anything metal is handled by the Forge system's salvage
        private static readonly string[] WoodKeys = { "shelf", "table", "chair", "dresser", "cabinet", "bed", "crate", "pallet", "door", "desk", "bench", "stool", "wardrobe", "drawer", "frame", "board", "furniture", "box" };
        private static readonly float[] WoodHardware = { 6, 4, 3, 8, 8, 6, 3, 4, 4, 5, 4, 2, 9, 4, 3, 2, 5, 2 };

        private static bool Prefix(BreakDown __instance, out SalvageState __state)
        {
            PatchLog.Fire(Id);
            __state = null;
            try
            {
                var sys = BuildingSystem.Instance;
                if (sys == null) return true;

                // Fixed geography: stations are part of the world.
                bool station = false;
                try { station = __instance.GetComponentInParent<Il2Cpp.Forge>() != null || __instance.GetComponentInParent<AmmoWorkBench>() != null; } catch { }
                if (station || BuildingSystem.IsStationName((__instance.name ?? "").ToLowerInvariant()))
                {
                    GameUtil.Hud("This is part of the world. It can't be taken apart.", true);
                    return false;
                }

                GearItem tool = null;
                try { var p = InterfaceManager.GetPanel<Panel_BreakDown>(); if (p != null) tool = p.GetSelectedTool(); } catch { }
                var rec = sys.RecordFor(__instance);
                float mult = sys.SalvageMultiplier(tool, rec);

                var units = __instance.m_YieldObjectUnits;
                if (units != null && units.Length > 0)
                {
                    var st = new SalvageState { Original = new int[units.Length], Record = rec, Tool = tool };
                    for (int i = 0; i < units.Length; i++)
                    {
                        st.Original[i] = units[i];
                        // probabilistic rounding so a Beginner sometimes gets a plank and sometimes only firewood scraps
                        float scaled = units[i] * mult;
                        int n = Mathf.FloorToInt(scaled);
                        if (UnityEngine.Random.value < scaled - n) n++;
                        units[i] = Mathf.Max(0, n);
                    }
                    __state = st;
                }
                else __state = new SalvageState { Record = rec, Tool = tool };
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
            return true;
        }

        private static void Postfix(BreakDown __instance, SalvageState __state)
        {
            try
            {
                var sys = BuildingSystem.Instance;
                if (__state == null || sys == null) return;
                if (__state.Original != null)
                {
                    var units = __instance.m_YieldObjectUnits;
                    for (int i = 0; i < __state.Original.Length && units != null && i < units.Length; i++) units[i] = __state.Original[i];
                }

                string name = __instance.name ?? "";
                string lower = name.ToLowerInvariant();
                // metal objects are the Forge system's business
                float baseUnits = 0f;
                for (int i = 0; i < WoodKeys.Length; i++)
                    if (lower.Contains(WoodKeys[i])) { baseUnits = WoodHardware[i]; break; }
                if (baseUnits > 0f && !Forge_IsMetal(lower)) sys.SalvageHardware(name, baseUnits, __state.Tool);
                else Services.Skills.AddXp(SkillId.Carpentry, 0.5f);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }

        private static bool Forge_IsMetal(string lower)
        {
            return lower.Contains("metal") || lower.Contains("steel") || lower.Contains("iron") || lower.Contains("locker") || lower.Contains("filing");
        }
    }

    // A piece's build quality is part of its name: "Shelf [rough build]".
    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.GetCraftingDisplayName))]
    internal static class DecorationItem_DisplayName
    {
        private const string Id = "Building.DecorationItem_DisplayName";
        private static void Postfix(DecorationItem __instance, ref string __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = BuildingSystem.Instance;
                var rec = sys != null ? sys.RecordFor(__instance) : null;
                if (rec != null) __result = (__result ?? "") + " [" + BuildingSystem.Describe(rec.Quality) + " build]";
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
