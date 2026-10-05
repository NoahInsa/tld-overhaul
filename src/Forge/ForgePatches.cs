using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using TLDOverhaul.Core;

namespace TLDOverhaul.Forge
{
    // Metal objects broken down in the world (car parts, appliances, shelving...) also yield scrap: "the map is littered with
    // this stuff - it's set dressing right now". POSTFIX: vanilla finishes its own yield first; we add scrap on top.
    // The object's name is logged once per distinct name so the keyword list below can be tuned from real data.
    [HarmonyPatch(typeof(BreakDown), nameof(BreakDown.DoBreakDown))]
    internal static class BreakDown_DoBreakDown
    {
        private const string Id = "Forge.BreakDown_DoBreakDown";
        private static readonly HashSet<string> Seen = new HashSet<string>();

        // keyword -> (grade, units). First match wins.
        private static readonly KeyValuePair<string, KeyValuePair<ScrapGrade, float>>[] Metal =
        {
            K("engine", ScrapGrade.High, 6f), K("radiator", ScrapGrade.Mid, 3f), K("hood", ScrapGrade.Mid, 5f), K("fender", ScrapGrade.Mid, 3f),
            K("bumper", ScrapGrade.Mid, 3f), K("cardoor", ScrapGrade.Mid, 4f), K("truck", ScrapGrade.Mid, 8f), K("wheel", ScrapGrade.Mid, 2f),
            K("stove", ScrapGrade.Low, 4f), K("oven", ScrapGrade.Low, 4f), K("fridge", ScrapGrade.Low, 5f), K("freezer", ScrapGrade.Low, 5f),
            K("locker", ScrapGrade.Low, 3f), K("filing", ScrapGrade.Low, 3f), K("shelving", ScrapGrade.Low, 2f), K("shelf_metal", ScrapGrade.Low, 2f),
            K("barrel", ScrapGrade.Low, 2f), K("drum", ScrapGrade.Low, 2f), K("tank", ScrapGrade.Low, 3f), K("pipe", ScrapGrade.Low, 1f),
            K("bedframe", ScrapGrade.Low, 3f), K("metal", ScrapGrade.Low, 2f), K("steel", ScrapGrade.Low, 2f), K("iron", ScrapGrade.Low, 2f),
        };

        private static KeyValuePair<string, KeyValuePair<ScrapGrade, float>> K(string k, ScrapGrade g, float u)
        {
            return new KeyValuePair<string, KeyValuePair<ScrapGrade, float>>(k, new KeyValuePair<ScrapGrade, float>(g, u));
        }

        private static void Postfix(BreakDown __instance)
        {
            PatchLog.Fire(Id);
            try
            {
                var sys = ForgeSystem.Instance;
                if (sys == null) return;
                string n = __instance.name ?? "";
                string lower = n.ToLowerInvariant();
                if (Seen.Add(lower)) MelonLogger.Msg("[Forge] break-down object name: '" + n + "'");
                foreach (var m in Metal)
                    if (lower.Contains(m.Key))
                    {
                        sys.AddSalvage(n, m.Value.Key, m.Value.Value);
                        return;
                    }
            }
            catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
