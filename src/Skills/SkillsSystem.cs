using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Skills
{
    /// <summary>
    /// Practice + books. Practice earns XP inside the current tier; XP cannot pass the tier ceiling. The ceiling only rises
    /// when the matching skill-book volume has been read (volume N lets you advance out of tier N). Volume V is the capstone
    /// that unlocks the last quarter of Master XP. Skills you stop practising rust: tiers are kept, but efficiency inside the
    /// tier decays.
    /// </summary>
    public sealed class SkillsSystem : OverhaulSystem, ISaveSection, ISkillService
    {
        public static SkillsSystem Instance { get; private set; }
        public override string Name { get { return "Skills"; } }
        string ISaveSection.Key { get { return "skills"; } }

        // ---- persisted state
        private readonly Dictionary<SkillId, float> _customXp = new Dictionary<SkillId, float>();
        private readonly Dictionary<SkillId, HashSet<int>> _volumes = new Dictionary<SkillId, HashSet<int>>();
        private readonly Dictionary<SkillId, float> _lastPractice = new Dictionary<SkillId, float>();
        private readonly Dictionary<SkillId, int> _grandfather = new Dictionary<SkillId, int>();
        private readonly Dictionary<SkillId, float> _frac = new Dictionary<SkillId, float>();
        private readonly Dictionary<string, BookTag> _tags = new Dictionary<string, BookTag>();

        // ---- not persisted
        public static bool Internal;      // true while we call vanilla's IncrementPointsAndNotify ourselves
        private readonly Dictionary<SkillId, float> _lastGateMsg = new Dictionary<SkillId, float>();
        private int[] _customThr = { 0, 40, 110, 220, 400 };
        private int _customMax = 600;

        private Setting<string> _thrSetting;
        private Setting<int> _maxSetting;
        private Setting<float> _graceHours, _fullRustHours, _rustFloor, _capstoneFraction, _baseReadHours, _hoursPerVolume;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(SkillsManager_IncrementPointsAndNotify);
                yield return typeof(Skill_IncrementPoints);
                yield return typeof(ResearchItem_OnResearchComplete);
                yield return typeof(ResearchItem_NoBenefit);
                yield return typeof(GearItem_BasicDisplayName_Book);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _thrSetting = Cfg.S(Name, "OverhaulSkillTierXp", "0,40,110,220,400", "XP needed to enter tiers I..V for the overhaul-owned skills (Carpentry, Mechanics, Blacksmithing, Tanning, Foraging, First Aid, Skiing).");
            _maxSetting = Cfg.I(Name, "OverhaulSkillMaxXp", 600, "XP cap of the Master tier for overhaul-owned skills.");
            _graceHours = Cfg.F(Name, "RustGraceHours", 168f, "Hours without practice before a skill starts to rust.");
            _fullRustHours = Cfg.F(Name, "RustFullHours", 720f, "Further hours until rust reaches its floor.");
            _rustFloor = Cfg.F(Name, "RustEfficiencyFloor", 0.55f, "Efficiency of a fully rusted skill (tiers are never lost).");
            _capstoneFraction = Cfg.F(Name, "MasterXpWithoutVolumeV", 0.75f, "Fraction of the Master tier's XP reachable without Volume V.");
            _baseReadHours = Cfg.F(Name, "BookReadHoursBase", 4f, "Reading time of volume I of a tagged book, in game hours.");
            _hoursPerVolume = Cfg.F(Name, "BookReadHoursPerVolume", 2f, "Extra reading hours per volume number.");
            ParseThresholds();
            SaveManager.Register(this);
            Services.Skills = this;

            foreach (SkillId id in Enum.GetValues(typeof(SkillId)))
            {
                var captured = id;
                DebugMenu.Register("Skills: give next book", SkillMap.Display(id), () => GiveNextVolume(captured));
            }
            foreach (SkillId id in Enum.GetValues(typeof(SkillId)))
            {
                var captured = id;
                DebugMenu.Register("Skills: +25 practice XP", SkillMap.Display(id), () => AddXp(captured, 25f));
            }
        }

        private void ParseThresholds()
        {
            try
            {
                var parts = _thrSetting.Value.Split(',');
                if (parts.Length == 5)
                {
                    var t = parts.Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    _customThr = t;
                }
            }
            catch { MelonLogger.Warning("[Skills] OverhaulSkillTierXp is not 5 comma-separated integers; using defaults."); }
            _customMax = Math.Max(_customThr[4] + 1, _maxSetting.Value);
        }

        // ============================================================================ raw access

        private Skill Vanilla(SkillId id)
        {
            SkillType t;
            if (!SkillMap.TryToVanilla(id, out t)) return null;
            try { var m = GameManager.GetSkillsManager(); return m != null ? m.GetSkill(t) : null; } catch { return null; }
        }

        private int[] Thresholds(SkillId id)
        {
            var s = Vanilla(id);
            if (s == null) return _customThr;
            try
            {
                var arr = s.m_TierPoints;
                if (arr != null && arr.Length >= 5)
                    return new[] { arr[0], arr[1], arr[2], arr[3], arr[4] };
            }
            catch { }
            return _customThr;
        }

        private int MaxPoints(SkillId id, int[] thr)
        {
            var s = Vanilla(id);
            if (s == null) return _customMax;
            try { return Math.Max(thr[4] + 1, s.GetMaxPoints()); } catch { return thr[4] + 1; }
        }

        public int Points(SkillId id)
        {
            var s = Vanilla(id);
            if (s != null) { try { return s.GetPoints(); } catch { return 0; } }
            float x; _customXp.TryGetValue(id, out x);
            return (int)x;
        }

        private static int TierFor(int[] thr, int pts)
        {
            int t = 0;
            for (int i = 1; i < 5; i++) if (pts >= thr[i]) t = i;
            return t;
        }

        // ============================================================================ ISkillService

        public int GetTier(SkillId id) { Touch(id, false); return TierFor(Thresholds(id), Points(id)); }

        public float GetLevel01(SkillId id)
        {
            var thr = Thresholds(id);
            int pts = Points(id);
            int tier = TierFor(thr, pts);
            float progress;
            if (tier < 4) progress = (pts - thr[tier]) / (float)Math.Max(1, thr[tier + 1] - thr[tier]);
            else progress = (pts - thr[4]) / (float)Math.Max(1, MaxPoints(id, thr) - thr[4]);
            progress = Mathf.Clamp01(progress);
            // Rust bites the within-tier part only: you never lose a tier.
            return Mathf.Clamp01((tier + progress * GetEfficiency(id)) / 5f);
        }

        public float GetEfficiency(SkillId id)
        {
            Touch(id, false);
            float last;
            if (!_lastPractice.TryGetValue(id, out last)) return 1f;
            float idle = GameUtil.HoursPlayed - last - _graceHours.Value;
            if (idle <= 0f) return 1f;
            return Mathf.Lerp(1f, _rustFloor.Value, Mathf.Clamp01(idle / Mathf.Max(1f, _fullRustHours.Value)));
        }

        public bool HasReadBook(SkillId id, int volume)
        {
            HashSet<int> v; return _volumes.TryGetValue(id, out v) && v.Contains(volume);
        }

        public int GetTierCeiling(SkillId id)
        {
            int c = 0;
            for (int v = 1; v <= 4; v++) { if (HasReadBook(id, v)) c = v; else break; }
            int gf; if (_grandfather.TryGetValue(id, out gf)) c = Math.Max(c, gf);
            return c;
        }

        private int PointCap(SkillId id)
        {
            var thr = Thresholds(id);
            int c = GetTierCeiling(id);
            if (c < 4) return thr[c + 1] - 1;
            int max = MaxPoints(id, thr);
            if (HasReadBook(id, 5)) return max;
            return thr[4] + (int)((max - thr[4]) * _capstoneFraction.Value);
        }

        /// <summary>Record that the skill was just practised (resets rust). Also lazily seeds per-skill records.</summary>
        private void Touch(SkillId id, bool practised)
        {
            float now = GameUtil.HoursPlayed;
            if (!_lastPractice.ContainsKey(id)) _lastPractice[id] = now;
            else if (practised) _lastPractice[id] = now;
            if (!_grandfather.ContainsKey(id) && GameUtil.InGame)
            {
                // Existing saves keep the tier they already earned; new games start at 0.
                _grandfather[id] = TierFor(Thresholds(id), Points(id));
            }
        }

        public void AddXp(SkillId id, float amount)
        {
            if (amount <= 0f || !GameUtil.InGame) return;
            Touch(id, true);
            float scaled = amount * GetEfficiency(id);
            float f; _frac.TryGetValue(id, out f);
            f += scaled;
            int whole = (int)Math.Floor(f);
            _frac[id] = f - whole;
            if (whole > 0) AddPoints(id, whole);
        }

        private void AddPoints(SkillId id, int pts)
        {
            int cap = PointCap(id);
            int cur = Points(id);
            int room = cap - cur;
            if (room <= 0) { NotifyGated(id); return; }
            pts = Math.Min(pts, room);

            var vs = Vanilla(id);
            if (vs != null)
            {
                SkillType t; SkillMap.TryToVanilla(id, out t);
                Internal = true;
                try { GameManager.GetSkillsManager().IncrementPointsAndNotify(t, pts, SkillsManager.PointAssignmentMode.AssignInAnyMode); }
                finally { Internal = false; }
            }
            else
            {
                int before = TierFor(_customThr, cur);
                float x; _customXp.TryGetValue(id, out x);
                _customXp[id] = x + pts;
                int after = TierFor(_customThr, (int)_customXp[id]);
                if (after > before) GameUtil.Hud(string.Format("{0} improves to tier {1}.", SkillMap.Display(id), SkillMap.Roman[after]), true);
            }
        }

        // ---- hooks from patches ---------------------------------------------------------------------------------

        /// <summary>Called by the IncrementPointsAndNotify prefix. Returns the number of points to really add (0 = block).</summary>
        public int FilterVanillaIncrement(SkillType type, int points)
        {
            SkillId id;
            if (!SkillMap.TryFromVanilla(type, out id) || points <= 0) return points;
            Touch(id, true);
            float scaled = points * GetEfficiency(id);
            float f; _frac.TryGetValue(id, out f);
            f += scaled;
            int whole = (int)Math.Floor(f);
            _frac[id] = f - whole;
            if (whole <= 0) return 0;
            int room = PointCap(id) - Points(id);
            if (room <= 0) { NotifyGated(id); return 0; }
            return Math.Min(whole, room);
        }

        public int ClampToCap(SkillType type, int points)
        {
            SkillId id;
            if (!SkillMap.TryFromVanilla(type, out id) || points <= 0) return points;
            int room = PointCap(id) - Points(id);
            if (room <= 0) { NotifyGated(id); return 0; }
            return Math.Min(points, room);
        }

        private void NotifyGated(SkillId id)
        {
            float now = GameUtil.HoursPlayed, last;
            if (_lastGateMsg.TryGetValue(id, out last) && now - last < 6f) return;
            _lastGateMsg[id] = now;
            int c = GetTierCeiling(id);
            string msg = c < 4
                ? string.Format("You can't get further in {0} by practice alone. You need {0} {1}.", SkillMap.Display(id), SkillMap.Roman[c])
                : string.Format("You've learned all you can of {0} without {0} V.", SkillMap.Display(id));
            GameUtil.Hud(msg);
        }

        // ============================================================================ books

        public bool TryGetTag(GearItem gi, out BookTag tag)
        {
            tag = default(BookTag);
            string g = GameUtil.GuidOf(gi);
            if (g != null && _tags.TryGetValue(g, out tag)) return true;
            return BookRegistry.TryVanilla(gi, out tag);
        }

        public bool IsTagged(GearItem gi)
        {
            string g = GameUtil.GuidOf(gi);
            return g != null && _tags.ContainsKey(g);
        }

        public void MarkRead(BookTag tag)
        {
            HashSet<int> set;
            if (!_volumes.TryGetValue(tag.Skill, out set)) { set = new HashSet<int>(); _volumes[tag.Skill] = set; }
            bool first = set.Add(tag.Volume);
            Touch(tag.Skill, true);
            int c = GetTierCeiling(tag.Skill);
            if (first)
                GameUtil.Hud(tag.Volume == 5
                    ? string.Format("You finish {0}. You now understand the last of {1}.", tag, SkillMap.Display(tag.Skill))
                    : (tag.Volume <= c
                        ? string.Format("You finish {0}. You can now advance to tier {1}.", tag, SkillMap.Roman[Math.Min(4, c)])
                        : string.Format("You finish {0}. It won't make full sense until you've read the earlier volumes.", tag)), true);
            else
                GameUtil.Hud(string.Format("You've read {0} before. You learn nothing new.", tag));
        }

        /// <summary>Reading time for a tagged book (hours).</summary>
        public int ReadHours(int volume) { return Mathf.RoundToInt(_baseReadHours.Value + _hoursPerVolume.Value * (volume - 1)); }

        /// <summary>Builds a readable book of a given skill and volume from a vanilla readable shell. Not placed anywhere.</summary>
        public GearItem CreateBook(SkillId skill, int volume)
        {
            try
            {
                var gi = GearItem.InstantiateGearItem("GEAR_" + BookRegistry.ShellFor(skill));
                if (gi == null) { MelonLogger.Warning("[Skills] could not instantiate book shell for " + skill); return null; }
                try { gi.ForceGUIDSetup(); } catch { }
                string g = GameUtil.GuidOf(gi);
                if (g == null) { MelonLogger.Warning("[Skills] book has no GUID; cannot tag volume."); return null; }
                _tags[g] = new BookTag(skill, volume);
                var r = gi.m_ResearchItem;
                if (r != null)
                {
                    r.m_TimeRequirementHours = ReadHours(volume);
                    r.m_NoBenefitAtSkillLevel = 99;   // always readable: the gate is the volume, not the vanilla skill level
                    r.m_SkillPoints = 0;              // a book is a key, not an XP potion
                }
                else MelonLogger.Warning("[Skills] shell '" + BookRegistry.ShellFor(skill) + "' has no ResearchItem; the volume will not be readable.");
                return gi;
            }
            catch (Exception e) { MelonLogger.Error("[Skills] CreateBook failed: " + e.Message); return null; }
        }

        public GearItem GiveBook(SkillId skill, int volume)
        {
            var gi = CreateBook(skill, volume);
            if (gi != null) GameManager.GetInventoryComponent().AddGear(gi, true);
            return gi;
        }

        private void GiveNextVolume(SkillId id)
        {
            for (int v = 1; v <= 5; v++)
            {
                if (HasReadBook(id, v)) continue;
                var b = GiveBook(id, v);
                GameUtil.Hud(b != null ? "Added " + new BookTag(id, v) : "Could not create book for " + id);
                return;
            }
            GameUtil.Hud("All volumes of " + SkillMap.Display(id) + " already read.");
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset()
        {
            _customXp.Clear(); _volumes.Clear(); _lastPractice.Clear(); _grandfather.Clear(); _frac.Clear(); _tags.Clear(); _lastGateMsg.Clear();
        }

        JToken ISaveSection.Save()
        {
            var o = new JObject();
            o["xp"] = new JObject(_customXp.Select(kv => new JProperty(kv.Key.ToString(), Math.Round(kv.Value, 2))));
            o["volumes"] = new JObject(_volumes.Select(kv => new JProperty(kv.Key.ToString(), new JArray(kv.Value.OrderBy(x => x)))));
            o["practice"] = new JObject(_lastPractice.Select(kv => new JProperty(kv.Key.ToString(), Math.Round(kv.Value, 2))));
            o["grandfather"] = new JObject(_grandfather.Select(kv => new JProperty(kv.Key.ToString(), kv.Value)));
            o["frac"] = new JObject(_frac.Select(kv => new JProperty(kv.Key.ToString(), Math.Round(kv.Value, 3))));
            o["tags"] = new JObject(_tags.Select(kv => new JProperty(kv.Key, new JArray(kv.Value.Skill.ToString(), kv.Value.Volume))));
            return o;
        }

        private static bool TryId(string s, out SkillId id) { return Enum.TryParse(s, out id); }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            SkillId id;
            var xp = o["xp"] as JObject; if (xp != null) foreach (var p in xp.Properties()) if (TryId(p.Name, out id)) _customXp[id] = (float)p.Value;
            var vol = o["volumes"] as JObject;
            if (vol != null) foreach (var p in vol.Properties()) if (TryId(p.Name, out id)) _volumes[id] = new HashSet<int>(((JArray)p.Value).Select(x => (int)x));
            var pr = o["practice"] as JObject; if (pr != null) foreach (var p in pr.Properties()) if (TryId(p.Name, out id)) _lastPractice[id] = (float)p.Value;
            var gf = o["grandfather"] as JObject; if (gf != null) foreach (var p in gf.Properties()) if (TryId(p.Name, out id)) _grandfather[id] = (int)p.Value;
            var fr = o["frac"] as JObject; if (fr != null) foreach (var p in fr.Properties()) if (TryId(p.Name, out id)) _frac[id] = (float)p.Value;
            var tg = o["tags"] as JObject;
            if (tg != null)
                foreach (var p in tg.Properties())
                {
                    var a = p.Value as JArray;
                    if (a != null && a.Count >= 2 && TryId((string)a[0], out id)) _tags[p.Name] = new BookTag(id, (int)a[1]);
                }
        }

        // ============================================================================ status

        public override void DrawStatus(StatusWriter w)
        {
            foreach (SkillId id in Enum.GetValues(typeof(SkillId)))
            {
                int pts = Points(id);
                HashSet<int> v; _volumes.TryGetValue(id, out v);
                bool any = pts > 0 || (v != null && v.Count > 0);
                if (!any) continue;
                int tier = GetTier(id);
                int cap = PointCap(id);
                float eff = GetEfficiency(id);
                w.Line("{0} {1}  {2}/{3} xp  ceiling {4}  volumes [{5}]{6}", SkillMap.Display(id), SkillMap.Roman[tier], pts, cap, SkillMap.Roman[Math.Min(4, GetTierCeiling(id))],
                    v == null ? "" : string.Join(",", v.OrderBy(x => x).Select(x => SkillMap.Roman[x - 1])),
                    eff < 0.995f ? string.Format("  rusty x{0:0.00}", eff) : "");
            }
            w.Line("(F9 debug menu can give books / XP for testing)");
        }
    }
}
