using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Gear;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Crafting
{
    // ---- gating -------------------------------------------------------------------------------------------------------
    // Three independent reads of "can this be crafted" exist in the UI, so all three are POSTFIXed to AND our skill gate onto
    // vanilla's material/tool/location answer. If vanilla already said no we never override it.

    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.CanCraftBlueprint))]
    internal static class PanelCrafting_CanCraftBlueprint
    {
        private const string Id = "Crafting.PanelCrafting_CanCraftBlueprint";
        private static void Postfix(BlueprintData bpi, ref bool __result)
        {
            PatchLog.Fire(Id);
            try
            {
                if (!__result) return;
                string reason;
                var sys = CraftingSystem.Instance;
                if (sys != null && sys.Blocked(bpi, out reason)) __result = false;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.CanCraftSelectedBlueprint))]
    internal static class PanelCrafting_CanCraftSelectedBlueprint
    {
        private const string Id = "Crafting.PanelCrafting_CanCraftSelectedBlueprint";
        private static void Postfix(Panel_Crafting __instance, ref bool __result)
        {
            PatchLog.Fire(Id);
            try
            {
                if (!__result) return;
                string reason;
                var sys = CraftingSystem.Instance;
                if (sys != null && sys.Blocked(__instance.SelectedBPI, out reason)) __result = false;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(BlueprintData), nameof(BlueprintData.CanCraftBlueprint))]
    internal static class BlueprintData_CanCraftBlueprint
    {
        private const string Id = "Crafting.BlueprintData_CanCraftBlueprint";
        private static void Postfix(BlueprintData __instance, ref bool __result)
        {
            PatchLog.Fire(Id);
            try
            {
                if (!__result) return;
                string reason;
                var sys = CraftingSystem.Instance;
                if (sys != null && sys.Blocked(__instance, out reason)) __result = false;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Recipe discovery: blueprints two or more tiers above your skill are not listed at all.
    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.ItemPassesFilter))]
    internal static class PanelCrafting_ItemPassesFilter
    {
        private const string Id = "Crafting.PanelCrafting_ItemPassesFilter";
        private static void Postfix(BlueprintData bpi, ref bool __result)
        {
            PatchLog.Fire(Id);
            try
            {
                if (!__result) return;
                var sys = CraftingSystem.Instance;
                if (sys != null && sys.Hidden(bpi)) __result = false;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Tell the player WHY nothing happens. PREFIX returning false cancels the start; vanilla's button is already disabled by
    // the postfixes above, this covers controller/hotkey paths.
    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.OnBeginCrafting))]
    internal static class PanelCrafting_OnBeginCrafting
    {
        private const string Id = "Crafting.PanelCrafting_OnBeginCrafting";
        private static bool Prefix(Panel_Crafting __instance)
        {
            PatchLog.Fire(Id);
            try
            {
                string reason;
                var sys = CraftingSystem.Instance;
                if (sys != null && sys.Blocked(__instance.SelectedBPI, out reason))
                {
                    GameUtil.Hud(reason, true);
                    return false;
                }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
            return true;
        }
    }

    // The craft button just goes grey when a gate blocks the recipe; say why when the player highlights it. POSTFIX, throttled.
    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.RefreshSelectedBlueprint))]
    internal static class PanelCrafting_RefreshSelectedBlueprint
    {
        private const string Id = "Crafting.PanelCrafting_RefreshSelectedBlueprint";
        private static string _lastShown = "";
        private static float _lastTime;

        private static void Postfix(Panel_Crafting __instance)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = CraftingSystem.Instance;
                var bp = __instance.SelectedBPI;
                string reason;
                if (sys == null || bp == null || !sys.Blocked(bp, out reason)) return;
                string key = BlueprintInfo.ResultName(bp);
                if (key == _lastShown && Time.unscaledTime - _lastTime < 8f) return;
                _lastShown = key; _lastTime = Time.unscaledTime;
                GameUtil.Hud(reason);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // ---- time ---------------------------------------------------------------------------------------------------------
    // Crafting time reaches the player through two vanilla entry points (the operation and the panel's preview). They may
    // call each other, so a shared depth guard makes sure the multiplier is applied exactly once, by the outermost call.
    internal static class DurationGuard
    {
        private static int _depth;
        private static int _frame = -1;

        public static void Enter()
        {
            if (_frame != Time.frameCount) { _depth = 0; _frame = Time.frameCount; }   // heal from a swallowed exception
            _depth++;
        }

        /// <summary>Returns true when this is the outermost call (the one that should scale).</summary>
        public static bool Exit() { _depth = Math.Max(0, _depth - 1); return _depth == 0; }
    }

    [HarmonyPatch(typeof(CraftingOperation), nameof(CraftingOperation.GetModifiedCraftingDuration))]
    internal static class CraftingOperation_GetModifiedCraftingDuration
    {
        private const string Id = "Crafting.CraftingOperation_GetModifiedCraftingDuration";
        private static void Prefix() { PatchLog.Fire(Id); DurationGuard.Enter(); }
        private static void Postfix(CraftingOperation __instance, ref int __result)
        {
            try
            {
                if (!DurationGuard.Exit()) return;
                var sys = CraftingSystem.Instance;
                if (sys == null) return;
                float scale = sys.TimeScale(sys.Requirement(__instance.Blueprint).Skill, __instance.m_Tool);
                __result = Mathf.Max(1, Mathf.RoundToInt(__result * scale));
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.GetFinalCraftingTimeWithAllModifiers))]
    internal static class PanelCrafting_GetFinalCraftingTime
    {
        private const string Id = "Crafting.PanelCrafting_GetFinalCraftingTime";
        private static void Prefix() { PatchLog.Fire(Id); DurationGuard.Enter(); }
        private static void Postfix(Panel_Crafting __instance, ref int __result)
        {
            try
            {
                if (!DurationGuard.Exit()) return;
                var sys = CraftingSystem.Instance;
                if (sys == null) return;
                GearItem tool = null;
                var rc = __instance.m_RequirementContainer;
                if (rc != null) tool = rc.GetSelectedTool();
                float scale = sys.TimeScale(sys.Requirement(__instance.SelectedBPI).Skill, tool);
                __result = Mathf.Max(1, Mathf.RoundToInt(__result * scale));
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // ---- outcome ------------------------------------------------------------------------------------------------------
    // HandleSuccess is invoked through a delegate (cc=0) once per finished unit. PREFIX snapshots the inventory so POSTFIX can
    // tell which items vanilla just created, then rolls failure (destroy + partial refund) or sets the product's quality.
    [HarmonyPatch(typeof(CraftingOperation), nameof(CraftingOperation.HandleSuccess))]
    internal static class CraftingOperation_HandleSuccess
    {
        private const string Id = "Crafting.CraftingOperation_HandleSuccess";
        private static void Prefix(out HashSet<int> __state)
        {
            PatchLog.Fire(Id);
            __state = null;
            try { var sys = CraftingSystem.Instance; if (sys != null) __state = sys.SnapshotInventory(); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }

        private static void Postfix(CraftingOperation __instance, HashSet<int> __state)
        {
            try
            {
                var sys = CraftingSystem.Instance;
                if (sys != null && __state != null) sys.AfterCraft(__instance, __state);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
