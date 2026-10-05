using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Gear;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Degradation
{
    // GearItem.Degrade(float hp) has ~30 callers: it is the one choke point every source of wear passes through, so it is
    // the primary hook. PREFIX because we rewrite the incoming argument before vanilla applies it.
    [HarmonyPatch(typeof(GearItem), nameof(GearItem.Degrade))]
    internal static class GearItem_Degrade
    {
        private const string Id = "Degradation.GearItem_Degrade";
        private static void Prefix(GearItem __instance, ref float hp)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = DegradationSystem.Instance;
                if (sys != null) hp = sys.AdjustWear(__instance, hp);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Tag wear that happens while crafting with the blueprint's trade so the skill multiplier uses the right skill.
    // PREFIX sets the context; vanilla DegradeTools then calls GearItem.Degrade within the same frame.
    [HarmonyPatch(typeof(CraftingOperation), nameof(CraftingOperation.DegradeTools))]
    internal static class CraftingOperation_DegradeTools
    {
        private const string Id = "Degradation.CraftingOperation_DegradeTools";
        private static void Prefix(CraftingOperation __instance)
        {
            PatchLog.Fire(Id);
            try { WearContext.Set(BlueprintInfo.SkillFor(__instance.Blueprint)); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
        private static void Postfix() { WearContext.Clear(); }
    }

    // Break-down tool wear (furniture salvage) belongs to carpentry. cc=0 target: may be inlined; harmless if it never fires.
    [HarmonyPatch(typeof(Panel_BreakDown), nameof(Panel_BreakDown.DegradeToolUsed))]
    internal static class Panel_BreakDown_DegradeToolUsed
    {
        private const string Id = "Degradation.Panel_BreakDown_DegradeToolUsed";
        private static void Prefix()
        {
            PatchLog.Fire(Id);
            WearContext.Set(SkillId.Carpentry);
        }
        private static void Postfix() { WearContext.Clear(); }
    }

    // Repair tool wear belongs to tool/clothing repair. cc=0 target: may be inlined; harmless if it never fires.
    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.DegradeToolUsedForRepair))]
    internal static class Panel_Repair_DegradeToolUsedForRepair
    {
        private const string Id = "Degradation.Panel_Repair_DegradeToolUsedForRepair";
        private static void Prefix(Panel_Repair __instance)
        {
            PatchLog.Fire(Id);
            try
            {
                var item = GameUtil.GearFromGameObject(__instance.GetSelectedRepairableItem());
                WearContext.Set(item != null && item.m_ClothingItem != null ? SkillId.Mending : SkillId.ToolRepair);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
        private static void Postfix() { WearContext.Clear(); }
    }

    internal sealed class RepairState
    {
        public GearItem Item;
        public GearItem Tool;
        public float HpBefore;
    }

    // PREFIX captures item + tool + HP, POSTFIX (after vanilla applied its flat gain) rescales the gain by skill and
    // tool performance, enforces the ceiling/max-loss caps and records the repair in the item's history.
    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.RepairSuccessful))]
    internal static class Panel_Repair_RepairSuccessful
    {
        private const string Id = "Degradation.Panel_Repair_RepairSuccessful";
        private static void Prefix(Panel_Repair __instance, out RepairState __state)
        {
            PatchLog.Fire(Id);
            __state = null;
            try
            {
                var item = GameUtil.GearFromGameObject(__instance.GetSelectedRepairableItem());
                if (item == null) return;
                __state = new RepairState
                {
                    Item = item,
                    Tool = GameUtil.GearFromGameObject(__instance.GetSelectedTool()),
                    HpBefore = item.m_CurrentHP,
                };
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }

        private static void Postfix(RepairState __state)
        {
            try
            {
                if (__state == null || DegradationSystem.Instance == null) return;
                DegradationSystem.Instance.ApplyRepairOutcome(__state.Item, __state.Tool, __state.HpBefore);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Repair ceiling: the item's own max-condition loss and the tool's ceiling both cap what a repair can restore.
    // POSTFIX because we only ever lower vanilla's answer.
    [HarmonyPatch(typeof(Repairable), nameof(Repairable.GetRepairConditionCap))]
    internal static class Repairable_GetRepairConditionCap
    {
        private const string Id = "Degradation.Repairable_GetRepairConditionCap";
        private static void Postfix(Repairable __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = DegradationSystem.Instance;
                if (sys == null || __result <= 0f) return;
                var gi = __instance.m_GearItem;
                if (gi == null) return;
                float cap = sys.MaxConditionPercent(gi);
                var panel = InterfaceManager.GetPanel<Panel_Repair>();
                var tool = panel != null ? GameUtil.GearFromGameObject(panel.GetSelectedTool()) : null;
                if (tool != null) cap = Mathf.Min(cap, sys.GetRepairCeiling(tool));

                bool fraction = __result <= 1.0001f;
                float pct = fraction ? __result * 100f : __result;
                pct = Mathf.Min(pct, cap);
                __result = fraction ? pct / 100f : pct;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Tool performance: a worn or improvised tool makes a repair less likely to succeed...
    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.GetChanceSuccess))]
    internal static class Panel_Repair_GetChanceSuccess
    {
        private const string Id = "Degradation.Panel_Repair_GetChanceSuccess";
        private static void Postfix(Panel_Repair __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = DegradationSystem.Instance;
                if (sys == null) return;
                var tool = GameUtil.GearFromGameObject(__instance.GetSelectedTool());
                if (tool == null) return;
                float perf = Mathf.Clamp(sys.GetPerformance(tool), 0.3f, 1.15f);
                __result *= Mathf.Lerp(0.7f, 1f, Mathf.Clamp01(perf));
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // ...and slower.
    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.GetModifiedRepairDuration))]
    internal static class Panel_Repair_GetModifiedRepairDuration
    {
        private const string Id = "Degradation.Panel_Repair_GetModifiedRepairDuration";
        private static void Postfix(Panel_Repair __instance, ref int __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = DegradationSystem.Instance;
                if (sys == null) return;
                var tool = GameUtil.GearFromGameObject(__instance.GetSelectedTool());
                if (tool == null) return;
                float perf = Mathf.Clamp(sys.GetPerformance(tool), 0.4f, 1.15f);
                __result = Mathf.Max(1, Mathf.RoundToInt(__result / perf));
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
