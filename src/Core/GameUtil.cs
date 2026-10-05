using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace TLDOverhaul.Core
{
    /// <summary>Small, defensive helpers around TLD's singletons. Everything here tolerates being called at the main menu.</summary>
    public static class GameUtil
    {
        public static bool InGame
        {
            get
            {
                try
                {
                    if (GameManager.IsMainMenuActive() || GameManager.IsEmptySceneActive()) return false;
                    return GameManager.GetPlayerManagerComponent() != null && GameManager.GetTimeOfDayComponent() != null;
                }
                catch { return false; }
            }
        }

        /// <summary>True while any menu/panel overlay is up (inventory, pause, crafting ...). Gameplay-time logic should not run then.</summary>
        public static bool OverlayActive
        {
            get { try { return InterfaceManager.IsOverlayActiveCached(); } catch { return false; } }
        }

        /// <summary>Monotonic in-game hours that do not advance while paused. Basis for every "per hour" decay in the mod.</summary>
        public static float HoursPlayed
        {
            get { try { return GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused(); } catch { return 0f; } }
        }

        public static int DayNumber
        {
            get { try { return GameManager.GetTimeOfDayComponent().GetDayNumber(); } catch { return 0; } }
        }

        private static readonly Dictionary<string, float> LastHours = new Dictionary<string, float>();

        /// <summary>Game-time delta in hours since the previous call with the same key (0 on first call).</summary>
        public static float HoursSince(string key)
        {
            float now = HoursPlayed;
            // right after a scene load / save restore the game clock may still be settling: re-baseline instead of integrating a jump
            if (Time.realtimeSinceStartup - _clockResetAt < 1.5f) { LastHours[key] = now; return 0f; }
            if (!LastHours.TryGetValue(key, out var last)) { LastHours[key] = now; return 0f; }
            LastHours[key] = now;
            float d = now - last;
            return d < 0f ? 0f : (d > 96f ? 96f : d);   // never integrate more than four days in one step
        }

        private static float _clockResetAt = -99f;
        public static void ResetClock(string key) { LastHours.Remove(key); }
        public static void ResetAllClocks() { LastHours.Clear(); _clockResetAt = Time.realtimeSinceStartup; }

        public static void Hud(string message, bool important = false)
        {
            try { HUDMessage.AddMessage(message, important, true); }
            catch (Exception e) { MelonLogger.Msg("[hud] " + message + " (HUD unavailable: " + e.Message + ")"); }
        }

        /// <summary>Prefab name without the GEAR_ prefix, "(Clone)" suffix or trailing "(n)" instance counters.</summary>
        public static string NameOf(GearItem gi)
        {
            if (gi == null) return "";
            try { return Normalize(gi.name); } catch { return ""; }
        }

        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int p = raw.IndexOf('(');
            if (p > 0) raw = raw.Substring(0, p);
            raw = raw.Trim();
            if (raw.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(5);
            return raw;
        }

        /// <summary>Stable per-item key that survives save/load, or null if the item has no GUID yet.</summary>
        public static string GuidOf(GearItem gi)
        {
            if (gi == null) return null;
            try
            {
                var g = gi.m_ObjectGuid;
                if (g != null)
                {
                    var s = g.Get();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                var s2 = ObjectGuid.MaybeGetGuidFromGameObject(gi.gameObject);
                return string.IsNullOrEmpty(s2) ? null : s2;
            }
            catch { return null; }
        }

        public static GearItem GearFromGameObject(GameObject go)
        {
            if (go == null) return null;
            try { return go.GetComponent<GearItem>(); } catch { return null; }
        }

        /// <summary>Snapshot of every gear item currently in the player's inventory (safe to mutate inventory while iterating).</summary>
        public static List<GearItem> InventoryItems()
        {
            var list = new List<GearItem>();
            try
            {
                var inv = GameManager.GetInventoryComponent();
                if (inv == null) return list;
                var items = inv.m_Items;
                for (int i = 0; i < items.Count; i++)
                {
                    var o = items[i];
                    var gi = o != null ? o.m_GearItem : null;
                    if (gi != null) list.Add(gi);
                }
            }
            catch (Exception e) { MelonLogger.Warning("InventoryItems: " + e.Message); }
            return list;
        }

        public static int CountInInventory(string gearNameNoPrefix)
        {
            try { return GameManager.GetInventoryComponent().NumGearInInventory("GEAR_" + gearNameNoPrefix, false); }
            catch { return 0; }
        }

        /// <summary>Spawn a vanilla gear prefab straight into the player's inventory. Returns null if the prefab is unknown.</summary>
        public static GearItem GiveItem(string gearNameNoPrefix, float conditionNormalized = 1f, int units = 1)
        {
            try
            {
                var gi = GearItem.InstantiateGearItem("GEAR_" + gearNameNoPrefix);
                if (gi == null) return null;
                if (conditionNormalized < 0.999f) gi.SetNormalizedHP(Mathf.Clamp01(conditionNormalized), false);
                if (units > 1 && gi.m_StackableItem != null) gi.m_StackableItem.m_Units = units;
                GameManager.GetInventoryComponent().AddGear(gi, true);
                return gi;
            }
            catch (Exception e) { MelonLogger.Warning("GiveItem(" + gearNameNoPrefix + "): " + e.Message); return null; }
        }

        public static void RemoveFromInventory(string gearNameNoPrefix, int units)
        {
            try { GameManager.GetInventoryComponent().RemoveGearFromInventory("GEAR_" + gearNameNoPrefix, units, false); }
            catch (Exception e) { MelonLogger.Warning("RemoveFromInventory(" + gearNameNoPrefix + "): " + e.Message); }
        }

        public static Vector3 PlayerPos
        {
            get { try { return GameManager.GetPlayerTransform().position; } catch { return Vector3.zero; } }
        }

        public static float AirTemperature
        {
            get { try { return GameManager.GetWeatherComponent().GetCurrentTemperature(); } catch { return 0f; } }
        }

        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }

        /// <summary>Deterministic 0..1 value for a string seed so "random" placement is stable per save.</summary>
        public static float Hash01(string seed)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in seed) { h ^= c; h *= 16777619; }
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }

    /// <summary>Where the player is, expressed in the design doc's terms (Mystery Lake, Forlorn Muskeg ...).</summary>
    public static class World
    {
        public static string SceneName
        {
            get { try { return GameManager.m_ActiveScene ?? ""; } catch { return ""; } }
        }

        private static readonly HashSet<string> Warned = new HashSet<string>();

        // substring of TLD scene name (lower-case) -> overhaul region key. First match wins.
        private static readonly KeyValuePair<string, string>[] Map =
        {
            new KeyValuePair<string,string>("lakeregion", "mystery_lake"),
            new KeyValuePair<string,string>("coastal", "coastal_highway"),
            new KeyValuePair<string,string>("crashmountain", "timberwolf_mountain"),
            new KeyValuePair<string,string>("dam", "mystery_lake"),
            new KeyValuePair<string,string>("rural", "pleasant_valley"),
            new KeyValuePair<string,string>("marsh", "forlorn_muskeg"),
            new KeyValuePair<string,string>("mountaintown", "mountain_town"),
            new KeyValuePair<string,string>("tracks", "broken_railroad"),
            new KeyValuePair<string,string>("riverv", "hushed_river_valley"),
            new KeyValuePair<string,string>("blackrock", "blackrock"),
            new KeyValuePair<string,string>("ashcanyon", "ash_canyon"),
            new KeyValuePair<string,string>("whaling", "desolation_point"),
            new KeyValuePair<string,string>("bleak", "bleak_inlet"),
            new KeyValuePair<string,string>("cannery", "bleak_inlet"),
            new KeyValuePair<string,string>("ravine", "ravine"),
            new KeyValuePair<string,string>("lake", "mystery_lake"),
        };

        /// <summary>Normalized key ("mystery_lake", "forlorn_muskeg", ...). "unknown" when the scene is not mapped (logged once).</summary>
        public static string RegionKey
        {
            get
            {
                string s = SceneName.ToLowerInvariant();
                if (s.Length == 0) return "unknown";
                foreach (var kv in Map)
                    if (s.Contains(kv.Key)) return kv.Value;
                if (Warned.Add(s)) MelonLogger.Msg("[world] scene '" + SceneName + "' is not mapped to a region key; treating as 'unknown'.");
                return "unknown";
            }
        }

        /// <summary>Coarse climate harshness 0 (mild coast) .. 1 (brutal interior) used for hide quality and similar.</summary>
        public static float Harshness
        {
            get
            {
                switch (RegionKey)
                {
                    case "coastal_highway": case "bleak_inlet": case "desolation_point": return 0.20f;
                    case "mystery_lake": case "ravine": return 0.40f;
                    case "mountain_town": case "forlorn_muskeg": case "broken_railroad": return 0.55f;
                    case "pleasant_valley": case "hushed_river_valley": return 0.80f;
                    case "timberwolf_mountain": case "ash_canyon": case "blackrock": return 0.95f;
                    default: return 0.5f;
                }
            }
        }
    }
}
