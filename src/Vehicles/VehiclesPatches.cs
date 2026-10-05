using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;

namespace TLDOverhaul.Vehicles
{
    // Resting inside a vehicle already adds warmth in vanilla (the cab keeps the wind out). A running engine adds its heater on
    // top of that. POSTFIX: add to vanilla's value. cc=0 target (may be inlined; harmless if it never fires).
    [HarmonyPatch(typeof(PlayerInVehicle), nameof(PlayerInVehicle.GetTempIncrease))]
    internal static class PlayerInVehicle_GetTempIncrease
    {
        private const string Id = "Vehicles.PlayerInVehicle_GetTempIncrease";
        private static void Postfix(ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = VehiclesSystem.Instance;
                if (sys != null) __result += sys.CabinHeat();
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
