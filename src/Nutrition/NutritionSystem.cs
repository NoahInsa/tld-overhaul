using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Nutrition
{
    /// <summary>
    /// PZ-style nutrition layered over TLD's calorie bar. TLD keeps its single calorie reserve (hunger, starvation damage);
    /// this system adds three body stores - protein, carbohydrate, lipid - plus body weight and a diet-variety history, and
    /// turns imbalances into consequences: cold, fatigue, slower healing, wasting, lost calorie absorption.
    /// </summary>
    public sealed class NutritionSystem : OverhaulSystem, ISaveSection
    {
        public static NutritionSystem Instance { get; private set; }
        public override string Name { get { return "Nutrition"; } }
        string ISaveSection.Key { get { return "nutrition"; } }

        // ---- state (persisted)
        public float Protein, Carbs, Lipids;        // grams relative to baseline 0 (negative = deficit)
        public float Weight;                        // kg
        private readonly List<string> _meals = new List<string>(); // diet-variety families of recent meals, oldest first
        private string _lastMealName = "";
        private float _lastIntakeHours = -999f;

        // ---- derived (not persisted)
        public float ProteinDef, FatDef, CarbDef, Underweight, Overweight, MonotonyPenalty;
        public float ColdScale = 1f, FatigueScale = 1f, RegenScale = 1f, MaxConditionPct = 100f;
        private float _baseRegen = -1f;
        private FoodItem _lastFood; private GearItem _lastFoodGear; private float _lastFoodSeenHours = -999f;
        private readonly Dictionary<string, float> _lastWarned = new Dictionary<string, float>();

        // ---- tuning
        private Setting<float> _baseWeight, _proteinCap, _carbCap, _fatCap, _shareC, _shareF, _shareP, _starveKgH, _gainKgH, _monoThreshold, _monoMaxPenalty, _mealGroupHours;
        private Setting<int> _historyLen;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(Hunger_Update);
                yield return typeof(Hunger_AddReserveCalories);
                yield return typeof(Fatigue_AddFatigue);
                yield return typeof(Freezing_AddFreezing);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _baseWeight = Cfg.F(Name, "BaseWeightKg", 80f, "Starting body weight.");
            _proteinCap = Cfg.F(Name, "ProteinStoreCapG", 600f, "Protein store limit, +/- grams.");
            _carbCap = Cfg.F(Name, "CarbStoreCapG", 800f, "Carbohydrate store limit (upper; the lower limit is half of it).");
            _fatCap = Cfg.F(Name, "LipidStoreCapG", 2400f, "Lipid store limit (upper; the lower limit is half of it).");
            _shareC = Cfg.F(Name, "BurnShareCarbs", 0.50f, "Fraction of calories burned from carbohydrate.");
            _shareF = Cfg.F(Name, "BurnShareLipids", 0.40f, "Fraction of calories burned from lipid.");
            _shareP = Cfg.F(Name, "BurnShareProtein", 0.10f, "Fraction of calories burned from protein.");
            _starveKgH = Cfg.F(Name, "StarvationKgPerHour", 0.045f, "Weight lost per hour when the calorie reserve is empty.");
            _gainKgH = Cfg.F(Name, "FullGainKgPerHour", 0.012f, "Weight gained per hour when the calorie reserve is full.");
            _monoThreshold = Cfg.F(Name, "MonotonyThreshold", 0.65f, "Share of recent meals from one food family beyond which the diet counts as monotonous.");
            _monoMaxPenalty = Cfg.F(Name, "MonotonyMaxCalorieLoss", 0.25f, "Fraction of eaten calories lost to a fully monotonous diet.");
            _mealGroupHours = Cfg.F(Name, "MealGroupingHours", 0.15f, "Intake events closer together than this (same food) count as one meal.");
            _historyLen = Cfg.I(Name, "DietHistoryMeals", 12, "How many recent meals the variety check looks at.");

            Weight = _baseWeight.Value;
            SaveManager.Register(this);
        }

        // ============================================================================ intake

        /// <summary>Called by the Hunger patches whenever calories enter the body.</summary>
        public void OnIntake(float calories, GearItem gi, FoodItem fi)
        {
            if (calories <= 0.01f) return;
            var prof = FoodProfiles.For(gi, fi);
            Protein = Mathf.Min(Protein + calories * prof.Protein / 4f, _proteinCap.Value);
            Carbs = Mathf.Min(Carbs + calories * prof.Carbs / 4f, _carbCap.Value);
            Lipids = Mathf.Min(Lipids + calories * prof.Fat / 9f, _fatCap.Value);

            string name = GameUtil.NameOf(gi);
            float now = GameUtil.HoursPlayed;
            if (name != _lastMealName || now - _lastIntakeHours > _mealGroupHours.Value)
            {
                _meals.Add(prof.Family);
                while (_meals.Count > _historyLen.Value) _meals.RemoveAt(0);
                _lastMealName = name;
            }
            _lastIntakeHours = now;
            RecomputeDerived();
        }

        public void NoteFoodBeingEaten(FoodItem fi)
        {
            if (fi == null) return;
            _lastFood = fi;
            _lastFoodGear = fi.m_GearItem;
            _lastFoodSeenHours = GameUtil.HoursPlayed;
        }

        /// <summary>For instant (non-progressive) intake we credit the food most recently seen, if it was seconds ago; otherwise a generic mixed profile.</summary>
        public void OnInstantIntake(float calories)
        {
            bool recent = GameUtil.HoursPlayed - _lastFoodSeenHours < 0.05f;
            OnIntake(calories, recent ? _lastFoodGear : null, recent ? _lastFood : null);
        }

        // ============================================================================ metabolism

        public override void OnUpdate()
        {
            float dt = GameUtil.HoursSince("nutrition");
            if (dt <= 0f) return;
            // Long jumps (sleep, travel, waiting) are integrated in <= 1h steps so thresholds behave the same as real-time.
            while (dt > 0f)
            {
                float step = Mathf.Min(dt, 1f);
                Step(step);
                dt -= step;
            }
            ApplyConditionEffects();
        }

        private void Step(float hours)
        {
            var hunger = GameManager.GetHungerComponent();
            if (hunger == null) return;

            float burn = Mathf.Max(0f, hunger.GetCurrentCalorieBurnPerHour()) * hours;
            float capC = _carbCap.Value, capF = _fatCap.Value, capP = _proteinCap.Value;
            float lowC = -capC * 0.5f, lowF = -capF * 0.5f, lowP = -capP;

            Carbs = Mathf.Clamp(Carbs - burn * _shareC.Value / 4f, lowC, capC);
            Lipids = Mathf.Clamp(Lipids - burn * _shareF.Value / 9f, lowF, capF);
            Protein = Mathf.Clamp(Protein - burn * _shareP.Value / 4f, lowP, capP);

            RecomputeDerived();

            // Weight follows the calorie bar's state, sped up by whatever the stores are short of.
            float max = Mathf.Max(1f, hunger.GetAdjustedMaxReserveCalories());
            float r = Mathf.Clamp01(hunger.GetCalorieReserves() / max);
            float dW = 0f;
            if (r < 0.20f)
                dW -= (0.20f - r) / 0.20f * _starveKgH.Value * (1f + 0.8f * ProteinDef + 0.4f * FatDef);
            else if (r > 0.85f)
                dW += (r - 0.85f) / 0.15f * _gainKgH.Value;
            dW -= 0.004f * (ProteinDef + 0.5f * FatDef);
            Weight = Mathf.Clamp(Weight + dW * hours, _baseWeight.Value * 0.55f, _baseWeight.Value * 1.5f);
        }

        private void RecomputeDerived()
        {
            float w = Weight / Mathf.Max(1f, _baseWeight.Value);
            ProteinDef = Mathf.Clamp01((-_proteinCap.Value * 0.5f - Protein) / (_proteinCap.Value * 0.5f));
            FatDef = Mathf.Clamp01((-_fatCap.Value * 0.2f - Lipids) / (_fatCap.Value * 0.3f));
            CarbDef = Mathf.Clamp01((-_carbCap.Value * 0.15f - Carbs) / (_carbCap.Value * 0.35f));
            Underweight = Mathf.Clamp01((0.90f - w) / 0.20f);
            Overweight = Mathf.Clamp01((w - 1.10f) / 0.20f);

            // Monotony: one food family dominating the recent meal history.
            MonotonyPenalty = 0f;
            if (_meals.Count >= 6)
            {
                var counts = new Dictionary<string, int>();
                int best = 0;
                foreach (var m in _meals)
                {
                    int c; counts.TryGetValue(m, out c); c++; counts[m] = c; if (c > best) best = c;
                }
                float dominance = best / (float)_meals.Count;
                float th = _monoThreshold.Value;
                MonotonyPenalty = Mathf.Clamp01((dominance - th) / Mathf.Max(0.01f, 1f - th)) * _monoMaxPenalty.Value;
            }

            ColdScale = 1f + 0.35f * FatDef + 0.25f * Underweight;
            FatigueScale = 1f + 0.40f * CarbDef + 0.25f * ProteinDef + 0.20f * Overweight + 0.20f * Underweight;
            RegenScale = Mathf.Max(0.2f, 1f - 0.5f * ProteinDef - 0.3f * Underweight);
            MaxConditionPct = 100f - 35f * Underweight - 10f * ProteinDef;

            Warn("protein", ProteinDef > 0.2f, "You feel your strength wasting. Your body needs protein.");
            Warn("fat", FatDef > 0.2f, "You feel the cold more. You are running short on fat.");
            Warn("carbs", CarbDef > 0.2f, "You tire quickly. Your body is short on carbohydrate.");
            Warn("under", Underweight > 0.2f, "You have lost a worrying amount of weight.");
            Warn("mono", MonotonyPenalty > 0.05f, "You are sick of eating the same thing; your body gets less out of it.");
        }

        private readonly HashSet<string> _activeFlags = new HashSet<string>();

        private void Warn(string key, bool active, string message)
        {
            if (!active) { _activeFlags.Remove(key); return; }
            if (!_activeFlags.Add(key)) return;                   // already active, already told
            if (!GameUtil.InGame) return;
            float now = GameUtil.HoursPlayed, last;
            if (_lastWarned.TryGetValue(key, out last) && now - last < 12f) return;
            _lastWarned[key] = now;
            GameUtil.Hud(message);
        }

        // ============================================================================ consequences on vanilla components

        private float _effectTimer;
        private void ApplyConditionEffects()
        {
            _effectTimer += Time.unscaledDeltaTime;
            if (_effectTimer < 1f) return;
            _effectTimer = 0f;

            var cond = GameManager.GetConditionComponent();
            if (cond == null) return;
            if (_baseRegen < 0f) _baseRegen = cond.m_HPIncreasePerDayWhileHealthy;
            cond.m_HPIncreasePerDayWhileHealthy = _baseRegen * RegenScale;

            // Wasting caps how healthy the body can be.
            float cap = cond.m_MaxHP * MaxConditionPct / 100f;
            if (MaxConditionPct < 99.5f && cond.m_CurrentHP > cap) cond.m_CurrentHP = cap;
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset()
        {
            Protein = Carbs = Lipids = 0f;
            Weight = _baseWeight != null ? _baseWeight.Value : 80f;
            _meals.Clear(); _lastMealName = ""; _lastIntakeHours = -999f; _lastWarned.Clear(); _activeFlags.Clear(); _baseRegen = -1f;
            RecomputeDerivedSafe();
        }

        private void RecomputeDerivedSafe() { try { if (_proteinCap != null) RecomputeDerived(); } catch { } }

        JToken ISaveSection.Save()
        {
            return new JObject
            {
                ["protein"] = Math.Round(Protein, 1),
                ["carbs"] = Math.Round(Carbs, 1),
                ["lipids"] = Math.Round(Lipids, 1),
                ["weight"] = Math.Round(Weight, 3),
                ["meals"] = new JArray(_meals),
            };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            Protein = (float?)o["protein"] ?? 0f;
            Carbs = (float?)o["carbs"] ?? 0f;
            Lipids = (float?)o["lipids"] ?? 0f;
            Weight = (float?)o["weight"] ?? _baseWeight.Value;
            _meals.Clear();
            var a = o["meals"] as JArray;
            if (a != null) foreach (var m in a) _meals.Add((string)m);
            GameUtil.ResetClock("nutrition");
            RecomputeDerived();
        }

        // ============================================================================ status

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Weight {0:0.0} kg   Protein {1:+0;-0} g   Carbs {2:+0;-0} g   Lipids {3:+0;-0} g", Weight, Protein, Carbs, Lipids);
            w.Line("Deficits  protein {0:P0}  fat {1:P0}  carbs {2:P0}   underweight {3:P0}", ProteinDef, FatDef, CarbDef, Underweight);
            w.Line("Cold x{0:0.00}  Fatigue x{1:0.00}  Healing x{2:0.00}  Max condition {3:0}%", ColdScale, FatigueScale, RegenScale, MaxConditionPct);
            w.Line("Diet: {0} recent meals, monotony calorie loss {1:P0}", _meals.Count, MonotonyPenalty);
        }
    }
}
