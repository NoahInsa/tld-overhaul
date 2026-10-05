using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Clothing
{
    // Per-garment warmth. GetWarmth has 8 callers (not inlined) and feeds the region/body warmth sums, so scaling its result
    // changes exactly that garment's contribution - "your boot at 40% means that leg gets colder faster". POSTFIX: we adjust
    // vanilla's value rather than recompute it.
    [HarmonyPatch(typeof(ClothingItem), nameof(ClothingItem.GetWarmth))]
    internal static class ClothingItem_GetWarmth
    {
        private const string Id = "Clothing.ClothingItem_GetWarmth";
        private static void Postfix(ClothingItem __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                var gi = __instance.m_GearItem;
                if (sys != null && gi != null && __result > 0f) __result *= sys.WarmthFactor(gi);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(ClothingItem), nameof(ClothingItem.GetWindproof))]
    internal static class ClothingItem_GetWindproof
    {
        private const string Id = "Clothing.ClothingItem_GetWindproof";
        private static void Postfix(ClothingItem __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                var gi = __instance.m_GearItem;
                if (sys != null && gi != null && __result > 0f) __result *= sys.WindFactor(gi);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Dirt abrades cloth. cc=0 target (may be inlined); harmless if it never fires.
    [HarmonyPatch(typeof(ClothingItem), nameof(ClothingItem.GetDailyHPDecay))]
    internal static class ClothingItem_GetDailyHPDecay
    {
        private const string Id = "Clothing.ClothingItem_GetDailyHPDecay";
        private static void Postfix(ClothingItem __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                var gi = __instance.m_GearItem;
                if (sys != null && gi != null && __result > 0f) __result *= sys.DecayFactor(gi);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Blood on your clothes attracts predators: vanilla predators already react to pack scent, so bloody clothing adds to it.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetExtraScentIntensity))]
    internal static class Inventory_GetExtraScentIntensity
    {
        private const string Id = "Clothing.Inventory_GetExtraScentIntensity";
        private static void Postfix(ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                if (sys != null) __result += sys.BloodScent;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Surface the garment state in the inventory name: "Wool Sweater [bloody] [dirty]". POSTFIX appends to vanilla's suffix.
    [HarmonyPatch(typeof(GearItem), nameof(GearItem.GetItemPostFixForInventoryInterfaces))]
    internal static class GearItem_GetItemPostFix
    {
        private const string Id = "Clothing.GearItem_GetItemPostFix";
        private static void Postfix(GearItem __instance, ref string __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                if (sys == null || __instance.m_ClothingItem == null) return;
                var label = sys.Label(__instance);
                if (label.Length > 0) __result = (__result ?? "") + label;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Being mauled soaks your outer layers. PREFIX: only reads the arguments. The 3-arg overload is the one with 34 callers.
    [HarmonyPatch(typeof(Condition), nameof(Condition.AddHealth), new[] { typeof(float), typeof(DamageSource), typeof(bool) })]
    internal static class Condition_AddHealth_Blood
    {
        private const string Id = "Clothing.Condition_AddHealth_Blood";
        private static void Prefix(float hp, DamageSource cause)
        {
            PatchLog.Fire(Id);
            try
            {
                if (hp >= 0f) return;
                var sys = ClothingSystem.Instance;
                if (sys == null) return;
                if (cause != DamageSource.Wolf && cause != DamageSource.Bear && cause != DamageSource.Cougar && cause != DamageSource.BulletWound) return;
                float amt = Mathf.Clamp(-hp / 30f, 0.10f, 0.60f);
                sys.AddBlood(ClothingRegion.Chest, amt);
                sys.AddBlood(ClothingRegion.Hands, amt * 0.5f);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Field dressing an animal: hands and chest take the blood.
    [HarmonyPatch(typeof(Panel_BodyHarvest), nameof(Panel_BodyHarvest.HarvestSuccessful))]
    internal static class PanelBodyHarvest_HarvestSuccessful
    {
        private const string Id = "Clothing.PanelBodyHarvest_HarvestSuccessful";
        private static void Postfix()
        {
            PatchLog.Fire(Id);
            try { ClothingSystem.Instance?.AddBloodHarvest(1f); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(Panel_BodyHarvest), nameof(Panel_BodyHarvest.QuarterSuccessful))]
    internal static class PanelBodyHarvest_QuarterSuccessful
    {
        private const string Id = "Clothing.PanelBodyHarvest_QuarterSuccessful";
        private static void Postfix()
        {
            PatchLog.Fire(Id);
            try { ClothingSystem.Instance?.AddBloodHarvest(1.4f); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // "Repairing requires matching materials and skill": warmer, more technical garments need a higher Tailoring tier.
    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.GetChanceSuccess))]
    internal static class PanelRepair_GetChanceSuccess_Clothing
    {
        private const string Id = "Clothing.PanelRepair_GetChanceSuccess_Clothing";
        private static void Postfix(Panel_Repair __instance, ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ClothingSystem.Instance;
                if (sys == null) return;
                var item = GameUtil.GearFromGameObject(__instance.GetSelectedRepairableItem());
                if (item != null) __result *= sys.RepairChanceFactor(item);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
