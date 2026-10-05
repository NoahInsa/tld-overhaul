using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Weapons
{
    // Arrow type and bow mods against the animal that was actually hit. PREFIX on the damage scalar: vanilla then applies it
    // exactly as it would a heavier or lighter hit. Arrow = broadhead, HardenedArrow = field point.
    [HarmonyPatch(typeof(ArrowItem), nameof(ArrowItem.InflictDamage))]
    internal static class ArrowItem_InflictDamage
    {
        private const string Id = "Weapons.ArrowItem_InflictDamage";
        private static void Prefix(ArrowItem __instance, GameObject victim, ref float damageScalar)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = WeaponsSystem.Instance;
                if (sys != null) damageScalar = sys.ArrowDamageScalar(__instance, victim, damageScalar);
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // Suppressor: "less predator attraction". Wildlife learn of a shot through this call; a suppressed shot is mostly unheard.
    // PREFIX returning false drops the event for that animal.
    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.ProcessGunshotAudioEvent))]
    internal static class BaseAi_ProcessGunshotAudioEvent
    {
        private const string Id = "Weapons.BaseAi_ProcessGunshotAudioEvent";
        private static bool Prefix()
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = WeaponsSystem.Instance;
                if (sys != null && sys.RecentlySuppressed && UnityEngine.Random.value < 0.8f) return false;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
            return true;
        }
    }

    // Vanilla reload tops the clip up from your pack with standard rounds; refuse while special rounds are loaded so a clip is never mixed.
    [HarmonyPatch(typeof(GunItem), nameof(GunItem.PressReloadAmmo))]
    internal static class GunItem_PressReloadAmmo
    {
        private const string Id = "Weapons.GunItem_PressReloadAmmo";
        private static bool Prefix(GunItem __instance)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = WeaponsSystem.Instance;
                var gi = __instance.m_GearItem;
                var rec = sys != null && gi != null ? sys.Rec(gi, false) : null;
                if (rec != null && rec.Loaded != AmmoKind.Fmj && __instance.m_RoundsInClip > 0 && __instance.m_RoundsInClip < __instance.m_ClipSize)
                {
                    GameUtil.Hud("You have " + WeaponTables.AmmoName(rec.Loaded) + " rounds loaded. Clear the weapon first (F7).");
                    return false;
                }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
            return true;
        }
    }
}
