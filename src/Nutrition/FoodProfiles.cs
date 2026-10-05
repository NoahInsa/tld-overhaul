using System;
using System.Collections.Generic;
using Il2Cpp;
using TLDOverhaul.Core;

namespace TLDOverhaul.Nutrition
{
    /// <summary>Share of an item's calories that come from each macronutrient (sums to ~1) plus a diet-variety family.</summary>
    public struct MacroProfile
    {
        public float Protein, Carbs, Fat;
        public string Family;
        public MacroProfile(float p, float c, float f, string family) { Protein = p; Carbs = c; Fat = f; Family = family; }
    }

    /// <summary>
    /// "The difference between a rabbit and a deer isn't just more calories, it's a fundamentally different nutritional
    /// profile." Profiles are by prefab name (substring, first match wins) with fallbacks from FoodItem flags, so every food
    /// in the game - including ones added by DLC or other mods - gets a sensible profile.
    /// </summary>
    public static class FoodProfiles
    {
        // Order matters: more specific first.
        private static readonly KeyValuePair<string, MacroProfile>[] Table =
        {
            // --- lean small game vs fattier large game
            P("rabbit",        0.72f, 0.00f, 0.28f, "rabbit"),
            P("deer",          0.56f, 0.00f, 0.44f, "deer"),
            P("stag",          0.56f, 0.00f, 0.44f, "deer"),
            P("moose",         0.62f, 0.00f, 0.38f, "moose"),
            P("wolf",          0.58f, 0.00f, 0.42f, "wolf"),
            P("bear",          0.32f, 0.00f, 0.68f, "bear"),
            // --- fish: lean white fish vs oily salmon/trout
            P("cohosalmon",    0.45f, 0.00f, 0.55f, "fish_oily"),
            P("rainbowtrout",  0.50f, 0.00f, 0.50f, "fish_oily"),
            P("burbot",        0.82f, 0.00f, 0.18f, "fish_lean"),
            P("goldeye",       0.78f, 0.00f, 0.22f, "fish_lean"),
            P("whitefish",     0.80f, 0.00f, 0.20f, "fish_lean"),
            P("redirishlord",  0.76f, 0.00f, 0.24f, "fish_lean"),
            P("rockfish",      0.78f, 0.00f, 0.22f, "fish_lean"),
            P("smallmouth",    0.78f, 0.00f, 0.22f, "fish_lean"),
            P("sardines",      0.45f, 0.00f, 0.55f, "canned_fish"),
            // --- foraged / plant
            P("cattail",       0.06f, 0.90f, 0.04f, "cattail"),
            P("acorn",         0.08f, 0.62f, 0.30f, "acorn"),
            P("burdock",       0.08f, 0.88f, 0.04f, "roots"),
            P("rosehip",       0.05f, 0.93f, 0.02f, "rosehip"),
            P("reishi",        0.18f, 0.78f, 0.04f, "mushroom"),
            P("maplesyrup",    0.00f, 1.00f, 0.00f, "sugar"),
            // --- packaged
            P("peanutbutter",  0.14f, 0.12f, 0.74f, "peanut"),
            P("granola",       0.10f, 0.66f, 0.24f, "bar"),
            P("energybar",     0.12f, 0.60f, 0.28f, "bar"),
            P("candybar",      0.05f, 0.62f, 0.33f, "bar"),
            P("crackers",      0.08f, 0.76f, 0.16f, "crackers"),
            P("beans",         0.24f, 0.62f, 0.14f, "canned_veg"),
            P("soup",          0.10f, 0.78f, 0.12f, "canned_veg"),
            P("peaches",       0.02f, 0.97f, 0.01f, "sugar"),
            P("condensedmilk", 0.14f, 0.54f, 0.32f, "dairy"),
            P("chips",         0.05f, 0.50f, 0.45f, "chips"),
            P("jerky",         0.78f, 0.04f, 0.18f, "jerky"),
            P("dogfood",       0.38f, 0.12f, 0.50f, "dogfood"),
            P("mre",           0.20f, 0.52f, 0.28f, "mre"),
            P("airlinefood",   0.22f, 0.54f, 0.24f, "mre"),
            P("soda",          0.00f, 1.00f, 0.00f, "sugar"),
            P("tea",           0.00f, 1.00f, 0.00f, "tea"),
            P("coffee",        0.00f, 1.00f, 0.00f, "tea"),
        };

        private static KeyValuePair<string, MacroProfile> P(string k, float p, float c, float f, string fam)
        {
            return new KeyValuePair<string, MacroProfile>(k, new MacroProfile(p, c, f, fam));
        }

        public static readonly MacroProfile Generic = new MacroProfile(0.22f, 0.50f, 0.28f, "mixed");

        public static MacroProfile For(GearItem gi, FoodItem fi)
        {
            string n = GameUtil.NameOf(gi).ToLowerInvariant();
            if (n.Length > 0)
                foreach (var kv in Table)
                    if (n.Contains(kv.Key)) return kv.Value;

            // Fallbacks from the flags TLD already ships on FoodItem.
            try
            {
                if (fi != null)
                {
                    if (fi.m_IsFish) return new MacroProfile(0.65f, 0f, 0.35f, "fish_other");
                    if (fi.m_IsMeat || fi.m_IsRawMeat) return new MacroProfile(0.58f, 0f, 0.42f, "meat_other");
                    if (fi.m_IsFat) return new MacroProfile(0.04f, 0.04f, 0.92f, "fat");
                    if (fi.m_IsNatural) return new MacroProfile(0.08f, 0.84f, 0.08f, "foraged");
                }
            }
            catch { }
            return Generic;
        }
    }
}
