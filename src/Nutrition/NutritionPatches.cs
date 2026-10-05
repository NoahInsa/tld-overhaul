using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Nutrition
{
    // Hunger.Update is a Unity message, so it is reliably callable (unlike inlined helpers). Progressive eating is
    // detected as m_CaloriesLeftToAdd shrinking while a FoodItem is providing calories. PREFIX records the starting
    // value and marks "inside Hunger.Update" so the AddReserveCalories hook does not double count; POSTFIX credits the
    // macronutrients and takes back the calories lost to a monotonous diet.
    [HarmonyPatch(typeof(Hunger), nameof(Hunger.Update))]
    internal static class Hunger_Update
    {
        private const string Id = "Nutrition.Hunger_Update";
        internal static bool Inside;
        private static float _leftBefore;

        private static void Prefix(Hunger __instance)
        {
            PatchLog.Fire(Id);
            Inside = true;
            try { _leftBefore = __instance.m_CaloriesLeftToAdd; } catch { _leftBefore = 0f; }
        }

        private static void Postfix(Hunger __instance)
        {
            Inside = false;
            try
            {
                var sys = NutritionSystem.Instance;
                if (sys == null || !GameUtil.InGame) return;

                var fi = __instance.m_FoodItemProvidingCalories;
                if (fi != null) sys.NoteFoodBeingEaten(fi);

                float delivered = _leftBefore - __instance.m_CaloriesLeftToAdd;
                if (fi != null && delivered > 0.001f)
                {
                    sys.OnIntake(delivered, fi.m_GearItem, fi);
                    if (sys.MonotonyPenalty > 0.001f)
                        __instance.RemoveReserveCalories(delivered * sys.MonotonyPenalty);
                }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Instant intake (energy bars, drinks, anything that bypasses the over-time path). PREFIX: we only read the argument.
    [HarmonyPatch(typeof(Hunger), nameof(Hunger.AddReserveCalories))]
    internal static class Hunger_AddReserveCalories
    {
        private const string Id = "Nutrition.Hunger_AddReserveCalories";
        private static void Prefix(Hunger __instance, float calories)
        {
            PatchLog.Fire(Id);
            try
            {
                if (calories <= 0f || Hunger_Update.Inside) return;
                if (__instance.IsAddingCaloriesOverTime()) return;      // progressive path is handled in Update
                var sys = NutritionSystem.Instance;
                if (sys != null && GameUtil.InGame) sys.OnInstantIntake(calories);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Carbohydrate/protein shortage and body weight make you tire faster. PREFIX scales only fatigue GAINS (positive).
    [HarmonyPatch(typeof(Fatigue), nameof(Fatigue.AddFatigue), new[] { typeof(float), typeof(FatigueFlags) })]
    internal static class Fatigue_AddFatigue
    {
        private const string Id = "Nutrition.Fatigue_AddFatigue";
        private static void Prefix(ref float fatigueValue)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = NutritionSystem.Instance;
                if (sys != null && fatigueValue > 0f) fatigueValue *= sys.FatigueScale;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Lean bodies run cold. PREFIX scales only freezing GAINS (positive), never warming.
    [HarmonyPatch(typeof(Freezing), nameof(Freezing.AddFreezing))]
    internal static class Freezing_AddFreezing
    {
        private const string Id = "Nutrition.Freezing_AddFreezing";
        private static void Prefix(ref float freezeValue)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = NutritionSystem.Instance;
                if (sys != null && freezeValue > 0f) freezeValue *= sys.ColdScale;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
