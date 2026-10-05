using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;

namespace TLDOverhaul.Crafting
{
    public struct RecipeOverride
    {
        public SkillId? Skill;   // null = keep the skill derived from the blueprint
        public int Tier;         // 0..4
        public bool HasTier;
    }

    /// <summary>
    /// Difficulty/skill overrides by crafted-item name. Everything not listed here is derived from the blueprint's own data
    /// (BlueprintInfo). The file lives at UserData/TLDOverhaul/recipes.json and is written on first run so it can be tuned
    /// without recompiling: { "Bow": { "skill": "Archery", "tier": 2 } }.
    /// </summary>
    public static class RecipeTable
    {
        private static readonly Dictionary<string, RecipeOverride> Map = new Dictionary<string, RecipeOverride>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath
        {
            get { return Path.Combine(MelonEnvironment.UserDataDirectory, "TLDOverhaul", "recipes.json"); }
        }

        // name -> (skill, tier). Names are crafted-result prefab names without the GEAR_ prefix.
        private static readonly object[][] Defaults =
        {
            new object[] { "Torch", null, 0 }, new object[] { "Firelog", null, 0 }, new object[] { "Charcoal", null, 0 },
            new object[] { "Snare", SkillId.Carpentry, 0 },
            new object[] { "HatchetImprovised", SkillId.Carpentry, 0 }, new object[] { "KnifeImprovised", SkillId.CarcassHarvesting, 0 },
            new object[] { "ImprovisedHat", SkillId.Mending, 0 }, new object[] { "ImprovisedMittens", SkillId.Mending, 0 },
            new object[] { "ImprovisedCrampons", SkillId.Mechanics, 1 },
            new object[] { "Arrow", SkillId.Archery, 1 }, new object[] { "ArrowShaft", SkillId.Carpentry, 0 }, new object[] { "ArrowHead", SkillId.Blacksmithing, 1 },
            new object[] { "ArrowHardened", SkillId.Archery, 2 },
            new object[] { "Bow", SkillId.Archery, 2 }, new object[] { "ReclaimedWoodB", SkillId.Carpentry, 0 },
            new object[] { "RabbitskinHat", SkillId.Mending, 1 }, new object[] { "RabbitSkinMittens", SkillId.Mending, 1 },
            new object[] { "DeerSkinBoots", SkillId.Mending, 2 }, new object[] { "DeerSkinPants", SkillId.Mending, 2 },
            new object[] { "WolfSkinCape", SkillId.Mending, 2 }, new object[] { "MooseHideBag", SkillId.Mending, 2 },
            new object[] { "BearSkinCoat", SkillId.Mending, 3 }, new object[] { "MooseHideCloak", SkillId.Mending, 3 }, new object[] { "BearSkinBedRoll", SkillId.Mending, 3 },
            new object[] { "RifleAmmoSingle", SkillId.Gunsmithing, 1 }, new object[] { "RevolverAmmoSingle", SkillId.Gunsmithing, 1 },
            new object[] { "SimpleTools", SkillId.Mechanics, 2 }, new object[] { "WoodworkingTools", SkillId.Carpentry, 3 }, new object[] { "HighQualityTools", SkillId.Mechanics, 4 },
        };

        public static void Load()
        {
            Map.Clear();
            foreach (var d in Defaults)
                Map[(string)d[0]] = new RecipeOverride { Skill = d[1] == null ? (SkillId?)null : (SkillId)d[1], Tier = (int)d[2], HasTier = true };

            try
            {
                var path = FilePath;
                if (!File.Exists(path)) { WriteDefaults(path); return; }
                var root = JObject.Parse(File.ReadAllText(path));
                foreach (var p in root.Properties())
                {
                    var o = p.Value as JObject; if (o == null) continue;
                    var ov = new RecipeOverride();
                    Map.TryGetValue(p.Name, out ov);
                    var s = (string)o["skill"];
                    SkillId sid;
                    if (!string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out sid)) ov.Skill = sid;
                    if (o["tier"] != null) { ov.Tier = Math.Max(0, Math.Min(4, (int)o["tier"])); ov.HasTier = true; }
                    Map[p.Name] = ov;
                }
                MelonLogger.Msg("[Crafting] recipes.json: " + Map.Count + " recipe overrides.");
            }
            catch (Exception e) { MelonLogger.Warning("[Crafting] recipes.json unreadable (" + e.Message + "); using built-in defaults."); }
        }

        private static void WriteDefaults(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var o = new JObject();
                foreach (var kv in Map)
                    o[kv.Key] = new JObject { ["skill"] = kv.Value.Skill.HasValue ? kv.Value.Skill.Value.ToString() : null, ["tier"] = kv.Value.Tier };
                File.WriteAllText(path, o.ToString());
            }
            catch { }
        }

        public static bool TryGet(string resultName, out RecipeOverride ov) { return Map.TryGetValue(resultName ?? "", out ov); }
    }
}
