using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using TLDOverhaul.Weapons;
using UnityEngine;

namespace TLDOverhaul.Skiing
{
    public enum SkiType { None, CrossCountry, Touring }

    /// <summary>
    /// Skiing and winter traversal. TLD's ski boots become the foundation of a traversal system with progression:
    ///  * cross-country skis massively speed up flat snow, frozen lakes and gentle slopes (ski boots give the best bonus; other boots
    ///    work, slower); they are useless - worse than walking - on bare ground;
    ///  * AT/touring skis add skins: uphill travel without taking the skis off, and a climb/descent mode;
    ///  * skis lock you out of climbing, rappelling and indoors, take real time to put on and take off, and make shooting wobbly;
    ///  * a Skiing skill (practice + books) decides speed, stamina drain, and how often you fall (sprains, fatigue spikes). The jump
    ///    from cross-country to AT technique is gated by skill.
    /// Skis themselves are overhaul-owned items (TLD has none): they are granted by the exploration system or the debug menu.
    /// </summary>
    public sealed class SkiingSystem : OverhaulSystem, ISaveSection, IBenchProvider
    {
        public static SkiingSystem Instance { get; private set; }
        public override string Name { get { return "Skiing"; } }
        string ISaveSection.Key { get { return "skiing"; } }
        string IBenchProvider.Title { get { return "Skis"; } }

        // ---- persisted
        public bool OwnsXc, OwnsAt;
        public SkiType Equipped = SkiType.None;
        public bool SkinsOn;                 // AT climb mode
        private float _metres;               // total distance skied (stat)

        // ---- live
        private float _factor = 1f;          // current speed factor consumed by the movement patch
        private Vector3 _lastPos;
        private float _lastTime;
        private float _xpMetres;
        private float _fallTimer;
        private readonly HashSet<string> _seenTags = new HashSet<string>();
        public bool OnSkis { get { return Equipped != SkiType.None; } }

        private Setting<float> _xcBonus, _atBonus, _bootsOffFraction, _skillFloor, _fallBase, _putOnMin, _takeOffMin, _atTier, _drag, _metresPerXp;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(PlayerMovement_GetSnowDepthMultiplier);
                yield return typeof(PlayerClimbRope_BeginClimbing);
                yield return typeof(RopeClimbPoint_PerformInteraction);
                yield return typeof(Fatigue_AddFatigue_Ski);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _xcBonus = Cfg.F(Name, "CrossCountrySpeed", 1.85f, "Speed multiplier of cross-country skis on flat snow (full skill, ski boots).");
            _atBonus = Cfg.F(Name, "TouringDescentSpeed", 1.65f, "Speed multiplier of touring skis in descent mode.");
            _bootsOffFraction = Cfg.F(Name, "OtherBootsBonusFraction", 0.70f, "Fraction of the ski bonus you keep in ordinary boots.");
            _skillFloor = Cfg.F(Name, "BeginnerBonusFraction", 0.55f, "Fraction of the ski bonus a Beginner achieves.");
            _fallBase = Cfg.F(Name, "FallChancePerSecond", 0.012f, "Chance per second of a fall for an unskilled skier at speed.");
            _putOnMin = Cfg.F(Name, "PutOnMinutes", 4f, "Game minutes to put on skis. You commit to ski travel for stretches.");
            _takeOffMin = Cfg.F(Name, "TakeOffMinutes", 2f, "Game minutes to take them off.");
            _atTier = Cfg.F(Name, "TouringSkillTier", 2f, "Skiing tier (0-4) needed to handle touring bindings competently.");
            _drag = Cfg.F(Name, "BareGroundDrag", 0.55f, "Speed multiplier of skis on bare ground.");
            _metresPerXp = Cfg.F(Name, "MetresPerXp", 120f, "Distance skied per point of Skiing practice XP.");

            SaveManager.Register(this);
            BenchMenu.Register(this);
            WeaponsSystem.SwayScale = () => OnSkis ? 2.5f : 1f;     // "combat is severely penalized while equipped"

            DebugMenu.Register("Skiing", "Grant cross-country skis", () => { OwnsXc = true; GameUtil.Hud("You now own cross-country skis."); });
            DebugMenu.Register("Skiing", "Grant AT touring skis", () => { OwnsAt = true; GameUtil.Hud("You now own AT touring skis."); });
        }

        // ============================================================================ speed model

        private static bool WearingSkiBoots()
        {
            try { return GameManager.GetPlayerManagerComponent().IsWearingClothingName("GEAR_SkiBoots"); } catch { return false; }
        }

        private bool Skiable(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return true;      // unknown surface: assume snow rather than punish
            string t = tag.ToLowerInvariant();
            return t.Contains("snow") || t.Contains("ice") || t.Contains("frozen");
        }

        private void RecomputeFactor()
        {
            if (!OnSkis) { _factor = 1f; return; }
            var pm = GameManager.GetPlayerManagerComponent();
            string tag = null;
            try { tag = pm.m_LastPlayerGroundMaterialTag; } catch { }
            if (tag != null && _seenTags.Add(tag)) MelonLogger.Msg("[Skiing] ground material tag seen: '" + tag + "' (skiable=" + Skiable(tag) + ")");
            if (!Skiable(tag)) { _factor = _drag.Value; return; }

            float lvl = Services.Skills.GetLevel01(SkillId.Skiing);
            int tier = Services.Skills.GetTier(SkillId.Skiing);
            float bonus = (Equipped == SkiType.Touring ? _atBonus.Value : _xcBonus.Value) - 1f;
            bonus *= Mathf.Lerp(_skillFloor.Value, 1f, lvl);
            if (!WearingSkiBoots()) bonus *= _bootsOffFraction.Value;
            if (Equipped == SkiType.Touring && tier < _atTier.Value) bonus *= 0.6f;   // you don't know these bindings yet

            // slope, from where the player is actually going
            float slope = 0f;
            try
            {
                Vector3 v = GameManager.GetPlayerMovementComponent().GetVelocity();
                float h = new Vector2(v.x, v.z).magnitude;
                slope = h < 0.1f ? 0f : Mathf.Atan2(v.y, h) * Mathf.Rad2Deg;
            }
            catch { }
            float f = 1f + bonus;
            if (slope > 8f)  // uphill
            {
                if (Equipped == SkiType.Touring && SkinsOn) f = Mathf.Max(f * 0.8f, 1.05f);   // skins: climb on the skis
                else if (Equipped == SkiType.Touring) f *= 0.55f;                              // descent mode, no skins: slides back
                else f *= 0.65f;                                                               // cross-country skis are for flats and gentle slopes
            }
            else if (slope < -8f) f *= 1.25f;                                                  // downhill: fast, and risky
            else if (Equipped == SkiType.Touring && SkinsOn) f *= 0.8f;                        // skins drag on the flat
            _factor = Mathf.Max(0.3f, f);
        }

        public float SpeedFactor { get { return _factor; } }

        // ============================================================================ per-frame

        public override void OnUpdate()
        {
            if (!OnSkis) { _lastTime = 0f; return; }

            // skis do not go indoors
            var pm = GameManager.GetPlayerManagerComponent();
            if (pm != null && pm.m_IndoorSpaceTrigger != null)
            {
                Equipped = SkiType.None; SkinsOn = false; _factor = 1f;
                GameUtil.Hud("You can't ski in here. You take the skis off.", true);
                return;
            }

            RecomputeFactor();

            Vector3 pos = GameUtil.PlayerPos;
            float now = Time.time;
            if (_lastTime > 0f)
            {
                float dist = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(_lastPos.x, 0f, _lastPos.z));
                float dt = Mathf.Max(0.001f, now - _lastTime);
                if (dist < 20f)    // ignore teleports / scene loads
                {
                    _metres += dist; _xpMetres += dist;
                    if (_xpMetres >= _metresPerXp.Value) { _xpMetres = 0f; Services.Skills.AddXp(SkillId.Skiing, 1f); }
                    if (dist / dt > 2.5f) MaybeFall(dist / dt);
                }
            }
            _lastPos = pos; _lastTime = now;
        }

        /// <summary>Low-level skiers are "slow, clumsy, and fall often".</summary>
        private void MaybeFall(float speed)
        {
            _fallTimer += Time.deltaTime;
            if (_fallTimer < 1f) return;
            _fallTimer = 0f;
            float lvl = Services.Skills.GetLevel01(SkillId.Skiing);
            float p = _fallBase.Value * (1f - lvl) * (speed / 5f);
            if (Equipped == SkiType.Touring && Services.Skills.GetTier(SkillId.Skiing) < _atTier.Value) p *= 2f;
            if (_factor > 1.5f) p *= 1.3f;
            if (UnityEngine.Random.value >= Mathf.Clamp(p, 0f, 0.5f)) return;   // p is per second; this runs once per second

            GameUtil.Hud("You catch an edge and wipe out.", true);
            try { GameManager.GetFatigueComponent().AddFatigue(5f, FatigueFlags.None); } catch { }
            try { if (UnityEngine.Random.value < Mathf.Lerp(0.35f, 0.05f, lvl)) GameManager.GetFallDamageComponent().MaybeSprainAnkle(); } catch { }
            Services.Skills.AddXp(SkillId.Skiing, 0.5f);
        }

        // ============================================================================ equip / unequip

        private void PutOn(SkiType type)
        {
            if (OnSkis) { BenchMenu.Say("Take your current skis off first."); return; }
            TimedAction.Run("Putting on skis", _putOnMin.Value, ok =>
            {
                if (!ok) return;
                Equipped = type; SkinsOn = false; _lastTime = 0f;
                GameUtil.Hud(type == SkiType.Touring ? "You step into the touring bindings." : "You clip into the cross-country skis.");
            });
        }

        private void TakeOff()
        {
            TimedAction.Run("Taking off skis", _takeOffMin.Value, ok =>
            {
                if (!ok) return;
                Equipped = SkiType.None; SkinsOn = false; _factor = 1f;
                GameUtil.Hud("Skis off.");
            });
        }

        IEnumerable<BenchEntry> IBenchProvider.Entries(BenchInfo bench)
        {
            if (!OwnsXc && !OwnsAt) yield break;
            if (OnSkis)
            {
                yield return new BenchEntry { Label = "Take off your skis", Detail = _takeOffMin.Value + " min", Run = TakeOff };
                if (Equipped == SkiType.Touring)
                    yield return new BenchEntry
                    {
                        Label = SkinsOn ? "Remove skins (descent mode)" : "Fit climbing skins",
                        Detail = "2 min",
                        Run = () => TimedAction.Run("Changing mode", 2f, ok => { if (ok) { SkinsOn = !SkinsOn; GameUtil.Hud(SkinsOn ? "Skins on: you can climb." : "Skins off: ready to descend."); } }),
                    };
                yield break;
            }
            bool indoors = false;
            try { indoors = GameManager.GetPlayerManagerComponent().m_IndoorSpaceTrigger != null; } catch { }
            if (OwnsXc) yield return new BenchEntry { Label = "Put on cross-country skis", Detail = indoors ? "not indoors" : _putOnMin.Value + " min; boots: " + (WearingSkiBoots() ? "ski boots (best)" : "other boots (slower)"), Enabled = !indoors, Run = () => PutOn(SkiType.CrossCountry) };
            if (OwnsAt) yield return new BenchEntry { Label = "Put on AT touring skis", Detail = indoors ? "not indoors" : _putOnMin.Value + " min", Enabled = !indoors, Run = () => PutOn(SkiType.Touring) };
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { OwnsXc = OwnsAt = false; Equipped = SkiType.None; SkinsOn = false; _metres = 0f; _factor = 1f; _lastTime = 0f; }

        JToken ISaveSection.Save()
        {
            return new JObject { ["xc"] = OwnsXc, ["at"] = OwnsAt, ["eq"] = (int)Equipped, ["skins"] = SkinsOn, ["m"] = Math.Round(_metres, 0) };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            OwnsXc = (bool?)o["xc"] ?? false; OwnsAt = (bool?)o["at"] ?? false;
            Equipped = (SkiType)((int?)o["eq"] ?? 0); SkinsOn = (bool?)o["skins"] ?? false; _metres = (float?)o["m"] ?? 0f; _lastTime = 0f;
        }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Skis owned: cross-country {0}, touring {1}.   Equipped: {2}{3}", OwnsXc ? "yes" : "no", OwnsAt ? "yes" : "no", Equipped, SkinsOn ? " (skins on)" : "");
            if (OnSkis) w.Line("Speed x{0:0.00}   boots: {1}   skied {2:0} m", _factor, WearingSkiBoots() ? "ski boots" : "other", _metres);
        }
    }
}
