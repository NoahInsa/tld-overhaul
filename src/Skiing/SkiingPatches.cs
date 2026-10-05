using System;
using HarmonyLib;
using Il2Cpp;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Skiing
{
    // The speed hook. GetSnowDepthMovementMultiplier has a real caller (cc=1) and is exactly where deep snow slows you down;
    // POSTFIX replaces the "wading" penalty with the ski factor (which is <1 on bare ground, >1 on snow).
    [HarmonyPatch(typeof(PlayerMovement), nameof(PlayerMovement.GetSnowDepthMovementMultiplier))]
    internal static class PlayerMovement_GetSnowDepthMultiplier
    {
        private const string Id = "Skiing.PlayerMovement_GetSnowDepthMultiplier";
        private static void Postfix(ref float __result)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkiingSystem.Instance;
                if (sys == null || !sys.OnSkis) return;
                float f = sys.SpeedFactor;
                __result = f < 1f ? __result * f : Mathf.Max(__result, 1f) * f;
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    // "Locked out of climbing, rappelling." PREFIX returning false cancels the climb. cc=0 targets, so both entry points are covered.
    [HarmonyPatch(typeof(PlayerClimbRope), nameof(PlayerClimbRope.BeginClimbing))]
    internal static class PlayerClimbRope_BeginClimbing
    {
        private const string Id = "Skiing.PlayerClimbRope_BeginClimbing";
        private static bool Prefix()
        {
            PatchLog.Fire(Id);
            var sys = SkiingSystem.Instance;
            if (sys != null && sys.OnSkis) { GameUtil.Hud("You can't climb on skis. Take them off first.", true); return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(RopeClimbPoint), nameof(RopeClimbPoint.PerformInteraction))]
    internal static class RopeClimbPoint_PerformInteraction
    {
        private const string Id = "Skiing.RopeClimbPoint_PerformInteraction";
        private static bool Prefix()
        {
            PatchLog.Fire(Id);
            var sys = SkiingSystem.Instance;
            if (sys != null && sys.OnSkis) { GameUtil.Hud("You can't climb on skis. Take them off first.", true); return false; }
            return true;
        }
    }

    // Efficiency: experienced skiers "drain less stamina", beginners flail. PREFIX scales fatigue GAINS while on skis.
    [HarmonyPatch(typeof(Fatigue), nameof(Fatigue.AddFatigue), new[] { typeof(float), typeof(FatigueFlags) })]
    internal static class Fatigue_AddFatigue_Ski
    {
        private const string Id = "Skiing.Fatigue_AddFatigue_Ski";
        private static void Prefix(ref float fatigueValue)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = SkiingSystem.Instance;
                if (sys == null || !sys.OnSkis || fatigueValue <= 0f) return;
                fatigueValue *= Mathf.Lerp(1.35f, 0.6f, Services.Skills.GetLevel01(SkillId.Skiing));
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
