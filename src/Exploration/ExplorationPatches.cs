using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;

namespace TLDOverhaul.Exploration
{
    // A container's contents have just been generated: the moment to slip a one-time find into it. POSTFIX so vanilla's own
    // loot is already there and we only ever ADD.
    [HarmonyPatch(typeof(Container), nameof(Container.InstantiateContents))]
    internal static class Container_InstantiateContents
    {
        private const string Id = "Exploration.Container_InstantiateContents";
        private static void Postfix(Container __instance)
        {
            PatchLog.Fire(Id);
            try { ExplorationSystem.Instance?.OnContainerStocked(__instance); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // A stand-in item reached the player's pack: schematic/unique finds convert on pickup (deferred a frame, see OnUpdate).
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddGear))]
    internal static class Inventory_AddGear_Carrier
    {
        private const string Id = "Exploration.Inventory_AddGear_Carrier";
        private static void Postfix(GearItem gi)
        {
            PatchLog.Fire(Id);
            try { ExplorationSystem.Instance?.OnItemAdded(gi); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // While a stand-in sits in a container, show what it really is.
    [HarmonyPatch(typeof(GearItem), nameof(GearItem.GetBasicDisplayNameForInventoryInterfaces))]
    internal static class GearItem_CarrierName
    {
        private const string Id = "Exploration.GearItem_CarrierName";
        private static void Postfix(GearItem __instance, ref string __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var label = ExplorationSystem.Instance?.CarrierLabel(__instance);
                if (label != null) __result = label;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // "A deer taken in Pleasant Valley's deep winter yields thicker, higher-quality hide than one taken in mild Coastal Highway
    // weather." PREFIX snapshots the pack, POSTFIX re-grades any hide or pelt that was just produced.
    [HarmonyPatch(typeof(Panel_BodyHarvest), nameof(Panel_BodyHarvest.HarvestSuccessful))]
    internal static class PanelBodyHarvest_HideQuality
    {
        private const string Id = "Exploration.PanelBodyHarvest_HideQuality";
        private static void Prefix(out HashSet<int> __state)
        {
            PatchLog.Fire(Id);
            __state = new HashSet<int>();
            try { foreach (var gi in GameUtil.InventoryItems()) __state.Add(gi.GetInstanceID()); }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }

        private static void Postfix(HashSet<int> __state)
        {
            try
            {
                var sys = ExplorationSystem.Instance;
                if (sys == null || __state == null) return;
                float q = sys.HideQuality();
                foreach (var gi in GameUtil.InventoryItems())
                {
                    if (__state.Contains(gi.GetInstanceID())) continue;
                    string n = GameUtil.NameOf(gi).ToLowerInvariant();
                    if ((n.Contains("hide") || n.Contains("pelt")) && !n.Contains("dried")) gi.SetNormalizedHP(q, false);
                }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
