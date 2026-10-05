using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Clothing
{
    /// <summary>Per-garment state TLD does not track: how dirty and how bloody a specific piece of clothing is.</summary>
    public sealed class Garment
    {
        public float Dirt;   // 0..1
        public float Blood;  // 0..1
        public bool IsClean { get { return Dirt < 0.02f && Blood < 0.02f; } }
    }

    /// <summary>
    /// Individual garment tracking. Every garment (by ObjectGuid) carries its own condition effect, dirt and blood. Each has
    /// a gameplay consequence: worn garments insulate and wind-proof worse, dirt abrades cloth, blood is smelled by predators.
    /// </summary>
    public sealed class ClothingSystem : OverhaulSystem, ISaveSection
    {
        public static ClothingSystem Instance { get; private set; }
        public override string Name { get { return "Clothing"; } }
        string ISaveSection.Key { get { return "clothing"; } }

        private readonly Dictionary<string, Garment> _byGuid = new Dictionary<string, Garment>();
        private readonly Dictionary<int, Garment> _byInstance = new Dictionary<int, Garment>();
        private float _bloodScent;
        private float _scentTimer;

        // tuning
        private Setting<float> _wornKnee, _wornFloor, _windKnee, _windFloor, _dirtWarmth, _bloodWarmth, _dirtDecay, _dirtPerHour, _bloodFadePerHour,
            _bloodScentScale, _rinsePerHour, _repairTierWarmthStep;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(ClothingItem_GetWarmth);
                yield return typeof(ClothingItem_GetWindproof);
                yield return typeof(ClothingItem_GetDailyHPDecay);
                yield return typeof(Inventory_GetExtraScentIntensity);
                yield return typeof(GearItem_GetItemPostFix);
                yield return typeof(Condition_AddHealth_Blood);
                yield return typeof(PanelBodyHarvest_HarvestSuccessful);
                yield return typeof(PanelBodyHarvest_QuarterSuccessful);
                yield return typeof(PanelRepair_GetChanceSuccess_Clothing);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _wornKnee = Cfg.F(Name, "WornKnee", 0.75f, "Normalized condition below which a garment starts insulating worse.");
            _wornFloor = Cfg.F(Name, "WornWarmthFloor", 0.5f, "Warmth multiplier of a garment at 0% condition.");
            _windKnee = Cfg.F(Name, "WindKnee", 0.6f, "Condition below which a garment lets wind through (holes, torn seams).");
            _windFloor = Cfg.F(Name, "WindFloor", 0.35f, "Windproof multiplier at 0% condition.");
            _dirtWarmth = Cfg.F(Name, "DirtWarmthLoss", 0.20f, "Warmth lost at fully dirty (matted, compressed insulation).");
            _bloodWarmth = Cfg.F(Name, "BloodWarmthLoss", 0.10f, "Warmth lost at fully bloody (damp, stiff).");
            _dirtDecay = Cfg.F(Name, "DirtWearExtra", 0.6f, "Extra daily condition decay of a fully dirty garment.");
            _dirtPerHour = Cfg.F(Name, "DirtPerHourOutdoors", 0.005f, "Dirt gained per game hour worn outdoors (outermost layer, walking).");
            _bloodFadePerHour = Cfg.F(Name, "BloodFadePerHour", 0.004f, "Blood that dries/weathers away per hour.");
            _bloodScentScale = Cfg.F(Name, "BloodScentIntensity", 12f, "Scent intensity a fully bloody outfit adds to your pack scent (predator detection).");
            _rinsePerHour = Cfg.F(Name, "RinseDirtPerHourWet", 0.01f, "Dirt washed out per hour while a garment is soaked by rain/snow.");
            _repairTierWarmthStep = Cfg.F(Name, "RepairTierWarmthStep", 3f, "Garment warmth per Tailoring tier needed to repair it without penalty (a down parka needs a master).");
            SaveManager.Register(this);
        }

        // ------------------------------------------------------------------ lookup

        public Garment Get(GearItem gi, bool create = true)
        {
            if (gi == null) return null;
            int iid;
            try { iid = gi.GetInstanceID(); } catch { return null; }
            Garment g;
            if (_byInstance.TryGetValue(iid, out g)) return g;
            string guid = GameUtil.GuidOf(gi);
            if (guid != null)
            {
                if (!_byGuid.TryGetValue(guid, out g))
                {
                    if (!create) return null;
                    g = new Garment();
                    _byGuid[guid] = g;
                }
            }
            else
            {
                if (!create) return null;
                g = new Garment(); // no GUID yet: keep per-instance only
            }
            _byInstance[iid] = g;
            return g;
        }

        public override void OnSceneLoaded(string sceneName) { _byInstance.Clear(); }
        public override void OnStateLoaded() { _byInstance.Clear(); }

        // ------------------------------------------------------------------ garment math (used by patches)

        public float WarmthFactor(GearItem gi)
        {
            float cond = Mathf.Clamp01(gi.GetNormalizedCondition());
            float knee = _wornKnee.Value;
            float f = cond >= knee ? 1f : Mathf.Lerp(_wornFloor.Value, 1f, cond / knee);
            var g = Get(gi, false);
            if (g != null) f *= (1f - _dirtWarmth.Value * g.Dirt) * (1f - _bloodWarmth.Value * g.Blood);
            return f;
        }

        public float WindFactor(GearItem gi)
        {
            float cond = Mathf.Clamp01(gi.GetNormalizedCondition());
            float knee = _windKnee.Value;
            float f = cond >= knee ? 1f : Mathf.Lerp(_windFloor.Value, 1f, cond / knee);
            var g = Get(gi, false);
            if (g != null) f *= (1f - 0.10f * g.Dirt);
            return f;
        }

        public float DecayFactor(GearItem gi)
        {
            var g = Get(gi, false);
            return g == null ? 1f : 1f + _dirtDecay.Value * g.Dirt;
        }

        public float BloodScent { get { return _bloodScent; } }

        public string Label(GearItem gi)
        {
            var g = Get(gi, false);
            if (g == null || g.IsClean) return "";
            var sb = new StringBuilder();
            if (g.Blood >= 0.15f) sb.Append(g.Blood > 0.6f ? " [soaked in blood]" : " [bloody]");
            if (g.Dirt >= 0.25f) sb.Append(g.Dirt > 0.7f ? " [filthy]" : " [dirty]");
            return sb.ToString();
        }

        /// <summary>Repairing high-grade garments takes real tailoring skill: warmer/more technical garments need a higher tier.</summary>
        public float RepairChanceFactor(GearItem gi)
        {
            if (gi == null || gi.m_ClothingItem == null) return 1f;
            float warmth = Mathf.Max(0f, gi.m_ClothingItem.m_Warmth);
            int needed = Mathf.Clamp(Mathf.FloorToInt(warmth / Mathf.Max(0.5f, _repairTierWarmthStep.Value)), 0, 4);
            int have = Services.Skills.GetTier(SkillId.Mending);
            int gap = needed - have;
            return gap <= 0 ? 1f : Mathf.Pow(0.5f, gap);
        }

        // ------------------------------------------------------------------ blood / dirt sources

        private static readonly ClothingLayer[] Layers = { ClothingLayer.Base, ClothingLayer.Mid, ClothingLayer.Top, ClothingLayer.Top2 };

        /// <summary>Visit every worn garment with its region and layer.</summary>
        public static void ForEachWorn(Action<GearItem, ClothingRegion, ClothingLayer> visit)
        {
            var pm = GameManager.GetPlayerManagerComponent();
            if (pm == null) return;
            for (int r = 0; r < (int)ClothingRegion.NumRegions; r++)
            {
                var region = (ClothingRegion)r;
                if (region == ClothingRegion.Accessory) continue;
                foreach (var layer in Layers)
                {
                    GearItem gi = null;
                    try { gi = pm.GetClothingInSlot(region, layer); } catch { }
                    if (gi != null) visit(gi, region, layer);
                }
            }
        }

        private static float Exposure(ClothingLayer l)
        {
            switch (l) { case ClothingLayer.Top2: return 1f; case ClothingLayer.Top: return 0.9f; case ClothingLayer.Mid: return 0.45f; default: return 0.2f; }
        }

        /// <summary>Splash blood onto garments in a region. Outer layers take the brunt; inner layers get a fraction.</summary>
        public void AddBlood(ClothingRegion region, float amount)
        {
            ForEachWorn((gi, r, l) =>
            {
                if (r != region) return;
                var g = Get(gi);
                if (g != null) g.Blood = Mathf.Clamp01(g.Blood + amount * Exposure(l));
            });
        }

        public void AddBloodHarvest(float scale)
        {
            AddBlood(ClothingRegion.Hands, 0.50f * scale);
            AddBlood(ClothingRegion.Chest, 0.25f * scale);
            AddBlood(ClothingRegion.Legs, 0.10f * scale);
        }

        private static ClothingRegion RegionOf(AfflictionBodyArea a)
        {
            switch (a)
            {
                case AfflictionBodyArea.Head: return ClothingRegion.Head;
                case AfflictionBodyArea.Neck: return ClothingRegion.Neck;
                case AfflictionBodyArea.HandLeft: case AfflictionBodyArea.HandRight: return ClothingRegion.Hands;
                case AfflictionBodyArea.LegLeft: case AfflictionBodyArea.LegRight: return ClothingRegion.Legs;
                case AfflictionBodyArea.FootLeft: case AfflictionBodyArea.FootRight: return ClothingRegion.Feet;
                default: return ClothingRegion.Chest;
            }
        }

        public override void OnUpdate()
        {
            float h = GameUtil.HoursSince("clothing");
            if (h <= 0f) return;
            if (h > 2f) h = 2f; // sleeping/waiting: cap so a night does not instantly soil everything

            var pm = GameManager.GetPlayerManagerComponent();
            bool outdoors = pm != null && pm.m_IndoorSpaceTrigger == null;
            bool running = pm != null && pm.m_InRunMode;
            float baseDirt = _dirtPerHour.Value * h * (outdoors ? 1f : 0.2f) * (running ? 1.5f : 1f);

            float bloodSum = 0f;
            ForEachWorn((gi, region, layer) =>
            {
                var g = Get(gi);
                if (g == null) return;
                float wet = 0f;
                try { wet = gi.m_ClothingItem != null ? gi.m_ClothingItem.GetWetnessNormalized() : 0f; } catch { }
                g.Dirt = Mathf.Clamp01(g.Dirt + baseDirt * Exposure(layer) * (1f + 0.5f * wet));
                if (wet > 0.5f) g.Dirt = Mathf.Clamp01(g.Dirt - _rinsePerHour.Value * h * wet);
                g.Blood = Mathf.Clamp01(g.Blood - _bloodFadePerHour.Value * h);
                bloodSum += g.Blood * Exposure(layer);
            });

            // bleeding wounds soak whatever covers the wound
            try
            {
                var bl = GameManager.GetBloodLossComponent();
                if (bl != null && bl.HasBloodLoss())
                {
                    int n = bl.GetAfflictionsCount();
                    for (int i = 0; i < n; i++) AddBlood(RegionOf(bl.GetLocation(i)), 0.06f * h);
                }
            }
            catch (Exception e) { PatchLog.Error("Clothing.Bleeding", e); }

            _scentTimer += h;
            _bloodScent = bloodSum * _bloodScentScale.Value / 3f; // ~3 layers-worth of full coverage is a "fully bloody outfit"
        }

        // ------------------------------------------------------------------ ISaveSection

        void ISaveSection.Reset() { _byGuid.Clear(); _byInstance.Clear(); _bloodScent = 0f; }

        JToken ISaveSection.Save()
        {
            // Make sure in-memory (GUID-less until now) instances are flushed by re-keying through GUID.
            var o = new JObject();
            foreach (var kv in _byGuid)
            {
                if (kv.Value.IsClean) continue;
                o[kv.Key] = new JArray(Math.Round(kv.Value.Dirt, 3), Math.Round(kv.Value.Blood, 3));
            }
            return o;
        }

        void ISaveSection.Load(JToken t)
        {
            _byGuid.Clear(); _byInstance.Clear();
            var o = t as JObject; if (o == null) return;
            foreach (var p in o.Properties())
            {
                var a = p.Value as JArray;
                if (a == null || a.Count < 2) continue;
                _byGuid[p.Name] = new Garment { Dirt = (float)a[0], Blood = (float)a[1] };
            }
        }

        // ------------------------------------------------------------------ status

        public override void DrawStatus(StatusWriter w)
        {
            int n = 0;
            ForEachWorn((gi, region, layer) =>
            {
                var g = Get(gi, false);
                w.Line("{0,-7} {1,-4} {2}: cond {3:0}%  warmth x{4:0.00}  wind x{5:0.00}{6}", region, layer, GameUtil.NameOf(gi),
                    gi.GetNormalizedCondition() * 100f, WarmthFactor(gi), WindFactor(gi),
                    g != null ? string.Format("  dirt {0:P0} blood {1:P0}", g.Dirt, g.Blood) : "");
                n++;
            });
            if (n == 0) w.Line("Nothing worn.");
            if (_bloodScent > 0.1f) w.Line("Blood scent adds {0:0.0} to predator-detectable scent.", _bloodScent);
        }
    }
}
