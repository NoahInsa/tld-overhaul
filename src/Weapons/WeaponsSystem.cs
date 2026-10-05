using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppTLD.Gear;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TLDOverhaul.Core;
using TLDOverhaul.Degradation;
using UnityEngine;

namespace TLDOverhaul.Weapons
{
    /// <summary>Everything the overhaul remembers about one weapon.</summary>
    public sealed class WeaponRecord
    {
        public float Fouling;                                        // 0..1, how dirty the action is
        public readonly Dictionary<ModId, float> Mods = new Dictionary<ModId, float>(); // installed mod -> install quality 0..1
        public AmmoKind Loaded = AmmoKind.Fmj;                       // kind of the rounds currently in the clip
        public int SuppressorShotsLeft;
        public int Shots;
        public bool Has(ModId id) { return Mods.ContainsKey(id); }
        public float Q(ModId id) { float q; return Mods.TryGetValue(id, out q) ? q : 0f; }
    }

    /// <summary>
    /// Weapons keep TLD's roster (rifle, revolver, bow) and gain maintenance depth: fouling that causes jams and costs accuracy
    /// before usability, skill-gated cleaning with the real consequence of a negligent discharge, physical mods that make sense
    /// for each weapon, and ammunition types that matter against different game.
    /// </summary>
    public sealed class WeaponsSystem : OverhaulSystem, ISaveSection, IBenchProvider
    {
        public static WeaponsSystem Instance { get; private set; }
        public override string Name { get { return "Weapons"; } }
        string ISaveSection.Key { get { return "weapons"; } }
        string IBenchProvider.Title { get { return "Weapons"; } }

        private readonly Dictionary<string, WeaponRecord> _records = new Dictionary<string, WeaponRecord>();
        private readonly Dictionary<ModId, int> _modStock = new Dictionary<ModId, int>();
        private readonly Dictionary<string, int> _ammoStock = new Dictionary<string, int>();   // "Rifle:HollowPoint" -> rounds

        // base field values of a gun instance (to re-derive stats from mods + fouling without compounding)
        private sealed class BaseStats { public float Acc, Sway, PMin, PMax, YMin, YMax, Damage; public int Clip; }
        private readonly Dictionary<int, BaseStats> _base = new Dictionary<int, BaseStats>();

        /// <summary>Other systems can make aiming harder (skis): multiplier on sway.</summary>
        public static Func<float> SwayScale;

        private float _statTimer;
        private float _lastSuppressedShot = -99f;
        private static Action<GameObject> _firedAction;
        private bool _registered;

        private Setting<float> _foulRifle, _foulRevolver, _jamBase, _jamFouling, _jamCondition, _ndRifle, _ndRevolver, _cleanMinutes, _suppShots, _accFoul, _swayFoul;

        public override IEnumerable<Type> PatchTypes
        {
            get
            {
                yield return typeof(ArrowItem_InflictDamage);
                yield return typeof(BaseAi_ProcessGunshotAudioEvent);
                yield return typeof(GunItem_PressReloadAmmo);
            }
        }

        protected override void OnInit()
        {
            Instance = this;
            _foulRifle = Cfg.F(Name, "FoulingPerShotRifle", 0.035f, "Fouling (0..1) added by each rifle shot.");
            _foulRevolver = Cfg.F(Name, "FoulingPerShotRevolver", 0.045f, "Fouling added by each revolver shot.");
            _jamBase = Cfg.F(Name, "JamBase", 0.003f, "Base chance of a jam on any shot.");
            _jamFouling = Cfg.F(Name, "JamFromFouling", 0.55f, "Extra jam chance at fully fouled (scaled by fouling^2.2).");
            _jamCondition = Cfg.F(Name, "JamFromWear", 0.35f, "Extra jam chance at 0% condition (scaled by wear^2.5).");
            _ndRifle = Cfg.F(Name, "NegligentDischargeRifle", 0.10f, "Chance of a fatal negligent discharge when a low-skill shooter cleans a loaded rifle.");
            _ndRevolver = Cfg.F(Name, "NegligentDischargeRevolver", 0.14f, "Same, for the revolver.");
            _cleanMinutes = Cfg.F(Name, "CleaningMinutes", 25f, "Game minutes to clean a weapon.");
            _suppShots = Cfg.F(Name, "SuppressorLifeShots", 40f, "Shots a fresh improvised suppressor lasts (scaled by install quality).");
            _accFoul = Cfg.F(Name, "AccuracyLossAtFullFouling", 0.30f, "Accuracy range lost when fully fouled.");
            _swayFoul = Cfg.F(Name, "SwayGainAtFullFouling", 0.60f, "Extra sway when fully fouled.");

            SaveManager.Register(this);
            BenchMenu.Register(this);
            DegradationSystem.ExtraWear = WearMultiplier;

            DebugMenu.Register("Weapons", "Add scope, recoil pad, extended cylinder, suppressor to stock", () =>
            { AddMod(ModId.Scope); AddMod(ModId.RecoilPad); AddMod(ModId.ExtendedCylinder); AddMod(ModId.Suppressor); });
            DebugMenu.Register("Weapons", "Add improved limbs + reinforced string", () => { AddMod(ModId.ImprovedLimbs); AddMod(ModId.ReinforcedString); });
            DebugMenu.Register("Weapons", "Add 10 rifle soft points + 6 revolver soft points", () => { AddAmmo(WeaponClass.Rifle, AmmoKind.SoftPoint, 10); AddAmmo(WeaponClass.Revolver, AmmoKind.SoftPoint, 6); });
            DebugMenu.Register("Weapons", "Foul held weapon (80%)", () => { var gi = Held(); var r = Rec(gi, true); if (r != null) r.Fouling = 0.8f; });
        }

        public void AddMod(ModId m) { int n; _modStock.TryGetValue(m, out n); _modStock[m] = n + 1; GameUtil.Hud("Added " + WeaponTables.Mods[m].Name + " to stock."); }
        public void AddAmmo(WeaponClass c, AmmoKind k, int n) { string key = c + ":" + k; int x; _ammoStock.TryGetValue(key, out x); _ammoStock[key] = x + n; }
        private int AmmoCount(WeaponClass c, AmmoKind k) { int x; _ammoStock.TryGetValue(c + ":" + k, out x); return x; }

        // ============================================================================ records

        private static GearItem Held()
        {
            try { return GameManager.GetPlayerManagerComponent().m_ItemInHands; } catch { return null; }
        }

        public WeaponRecord Rec(GearItem gi, bool create)
        {
            string g = GameUtil.GuidOf(gi);
            if (g == null) return null;
            WeaponRecord r;
            if (_records.TryGetValue(g, out r)) return r;
            if (!create) return null;
            r = new WeaponRecord(); _records[g] = r; return r;
        }

        // ============================================================================ events and per-frame upkeep

        public override void OnSceneLoaded(string sceneName)
        {
            _base.Clear();
            TryRegister();
        }

        private void TryRegister()
        {
            if (_registered || !GameUtil.InGame) return;
            try
            {
                _firedAction = new Action<GameObject>(OnFired);
                GunItem.RegisterOnFiredAction(_firedAction, GunType.Rifle);
                GunItem.RegisterOnFiredAction(_firedAction, GunType.Revolver);
                _registered = true;
                PatchLog.Fire("Weapons.OnFiredAction");   // the vanilla "gun fired" event is our hook for shots
            }
            catch (Exception e) { MelonLogger.Warning("[Weapons] could not register fired action: " + e.Message); }
        }

        private void OnFired(GameObject projectile)
        {
            try
            {
                var gi = Held();
                var cls = WeaponTables.ClassOf(gi);
                if (!cls.HasValue || cls == WeaponClass.Bow) return;
                var rec = Rec(gi, true);
                if (rec == null) return;
                rec.Shots++;
                float cond = Mathf.Clamp01(gi.GetNormalizedCondition());
                rec.Fouling = Mathf.Clamp01(rec.Fouling + (cls == WeaponClass.Rifle ? _foulRifle.Value : _foulRevolver.Value) * (1f + 0.5f * (1f - cond)));

                if (rec.Has(ModId.Suppressor))
                {
                    _lastSuppressedShot = Time.unscaledTime;
                    if (--rec.SuppressorShotsLeft <= 0)
                    {
                        rec.Mods.Remove(ModId.Suppressor);
                        GameUtil.Hud("The improvised suppressor burns out and falls apart.", true);
                    }
                }

                // "A dirty rifle jams at the worst moment."
                float p = _jamBase.Value + _jamFouling.Value * Mathf.Pow(rec.Fouling, 2.2f) + _jamCondition.Value * Mathf.Pow(1f - cond, 2.5f);
                if (rec.Has(ModId.ForgedReceiver)) p *= 1f - 0.5f * rec.Q(ModId.ForgedReceiver);
                if (UnityEngine.Random.value < Mathf.Clamp(p, 0f, 0.5f))
                {
                    gi.m_GunItem.SetJammed(true);
                    GameUtil.Hud("The action jams!", true);
                }
            }
            catch (Exception e) { PatchLog.Error("Weapons.OnFired", e); }
        }

        public override void OnUpdate()
        {
            if (!_registered) TryRegister();   // the scene may have initialised before the player existed
            _statTimer += Time.unscaledDeltaTime;
            if (_statTimer < 0.4f) return;
            _statTimer = 0f;
            var gi = Held();
            var cls = WeaponTables.ClassOf(gi);
            if (!cls.HasValue || cls == WeaponClass.Bow || gi.m_GunItem == null) return;
            var rec = Rec(gi, true);
            if (rec == null) return;
            ApplyStats(gi, gi.m_GunItem, rec, cls.Value);
        }

        /// <summary>Re-derives the gun's live numbers from its base values, fouling, wear, mods and the target under the crosshair.</summary>
        private void ApplyStats(GearItem gi, GunItem gun, WeaponRecord rec, WeaponClass cls)
        {
            int iid = gi.GetInstanceID();
            BaseStats b;
            if (!_base.TryGetValue(iid, out b))
            {
                b = new BaseStats { Acc = gun.m_AccuracyRange, Sway = gun.m_SwayIncreasePerSecond, PMin = gun.m_PitchRecoilMin, PMax = gun.m_PitchRecoilMax,
                                    YMin = gun.m_YawRecoilMin, YMax = gun.m_YawRecoilMax, Damage = gun.m_DamageHP, Clip = gun.m_ClipSize };
                _base[iid] = b;
            }
            if (gun.m_RoundsInClip == 0) rec.Loaded = AmmoKind.Fmj;

            float cond = Mathf.Clamp01(gi.GetNormalizedCondition());
            float wear = Mathf.Pow(1f - cond, 2f);
            float scopeQ = rec.Q(ModId.Scope), padQ = rec.Q(ModId.RecoilPad), stockQ = rec.Q(ModId.LightStock);

            // accuracy degrades before usability: dirt and wear cost range and steadiness long before the gun stops working
            float acc = b.Acc * (1f - _accFoul.Value * rec.Fouling) * (1f - 0.25f * wear);
            float sway = b.Sway * (1f + _swayFoul.Value * rec.Fouling + 0.5f * wear);
            if (SwayScale != null) { try { sway *= SwayScale(); } catch { } }
            float recoil = 1f;

            if (rec.Has(ModId.Scope))
            {
                acc *= 1f + 0.35f * scopeQ;
                // "A novice-installed scope wobbles. A skilled installation is rock solid."
                sway *= scopeQ >= 0.6f ? 1f - 0.20f * scopeQ : 1f + 0.30f * (0.6f - scopeQ);
            }
            if (rec.Has(ModId.RecoilPad)) { recoil *= 1f - 0.22f * padQ; sway *= 1f - 0.08f * padQ; }
            if (rec.Has(ModId.LightStock)) recoil *= 1f + 0.12f * (0.5f + 0.5f * stockQ);

            gun.m_AccuracyRange = acc; gun.m_SwayIncreasePerSecond = sway;
            gun.m_PitchRecoilMin = b.PMin * recoil; gun.m_PitchRecoilMax = b.PMax * recoil;
            gun.m_YawRecoilMin = b.YMin * recoil; gun.m_YawRecoilMax = b.YMax * recoil;
            gun.m_ClipSize = b.Clip + (rec.Has(ModId.ExtendedCylinder) ? 2 : 0);

            // ammunition against whatever the crosshair is on
            float dmg = b.Damage;
            if (gun.m_RoundsInClip > 0 || rec.Loaded != AmmoKind.Fmj)
            {
                AiSubType victim = AiSubType.Stag;   // generic large-game default
                if (gun.IsAiming()) { var ai = AimTarget(); if (ai != null) victim = ai.m_AiSubType; }
                dmg *= WeaponTables.BulletFactor(rec.Loaded, victim);
            }
            gun.m_DamageHP = dmg;
        }

        private static BaseAi AimTarget()
        {
            try
            {
                var cam = GameManager.GetMainCamera();
                RaycastHit hit;
                if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, 300f))
                    return hit.collider != null ? hit.collider.GetComponentInParent<BaseAi>() : null;
            }
            catch { }
            return null;
        }

        /// <summary>Weapon mods that reduce wear on the weapon (forged receiver, reinforced string).</summary>
        private float WearMultiplier(GearItem gi)
        {
            if (gi == null || (gi.m_GunItem == null && gi.m_BowItem == null)) return 1f;
            var rec = Rec(gi, false);
            if (rec == null) return 1f;
            float m = 1f;
            if (rec.Has(ModId.ForgedReceiver)) m *= 1f - 0.4f * rec.Q(ModId.ForgedReceiver);
            if (rec.Has(ModId.ReinforcedString)) m *= 1f - 0.4f * rec.Q(ModId.ReinforcedString);
            return m;
        }

        /// <summary>Called by the arrow patch: bow mods and arrow-vs-game multipliers.</summary>
        public float ArrowDamageScalar(ArrowItem arrow, GameObject victim, float scalar)
        {
            try
            {
                AiSubType sub = AiSubType.Stag;
                var ai = victim != null ? victim.GetComponentInParent<BaseAi>() : null;
                if (ai != null) sub = ai.m_AiSubType;
                bool broadhead = arrow.m_ArrowType != WeaponSource.HardenedArrow;
                scalar *= WeaponTables.ArrowFactor(broadhead, sub);
                var bow = Held();
                var rec = bow != null && bow.m_BowItem != null ? Rec(bow, false) : null;
                if (rec != null && rec.Has(ModId.ImprovedLimbs)) scalar *= 1f + 0.20f * rec.Q(ModId.ImprovedLimbs);
            }
            catch (Exception e) { PatchLog.Error("Weapons.Arrow", e); }
            return scalar;
        }

        public bool RecentlySuppressed { get { return Time.unscaledTime - _lastSuppressedShot < 2f; } }

        // ============================================================================ firearms skill and cleaning

        private static SkillId FireSkill(WeaponClass c) { return c == WeaponClass.Revolver ? SkillId.Revolver : SkillId.Rifle; }

        private void Unload(GearItem gi, WeaponRecord rec, WeaponClass cls)
        {
            var gun = gi.m_GunItem;
            int rounds = gun.NumRoundsInClip();
            if (rounds <= 0) return;
            int cond = 100;
            try { cond = Mathf.Clamp(gun.GetAmmoCondition(0), 1, 100); } catch { }
            string ammoName = cls == WeaponClass.Revolver ? "RevolverAmmoSingle" : "RifleAmmoSingle";
            try { GearItemData d; if (gun.TryGetSelectedAmmo(out d) && d != null && d.name.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0) ammoName = GameUtil.Normalize(d.name); } catch { }
            AmmoKind kind = rec != null ? rec.Loaded : AmmoKind.Fmj;
            gun.EmptyClip();
            if (kind != AmmoKind.Fmj) AddAmmo(cls, kind, rounds);
            else GameUtil.GiveItem(ammoName, cond / 100f, rounds);
            if (rec != null) rec.Loaded = AmmoKind.Fmj;
        }

        private void CleanWeapon(GearItem gi, WeaponClass cls)
        {
            var rec = Rec(gi, true);
            var gun = gi.m_GunItem;
            var kit = GameUtil.InventoryItems().FirstOrDefault(x => GameUtil.NameOf(x) == "RifleCleaningKit");
            if (kit == null) { BenchMenu.Say("You need a cleaning kit."); return; }

            SkillId fs = FireSkill(cls);
            int tier = Services.Skills.GetTier(fs);
            bool loaded = gun.NumRoundsInClip() > 0;
            string note = null;
            if (loaded)
            {
                if (tier >= 3) { Unload(gi, rec, cls); note = "You clear the weapon out of habit."; }
                else if (tier == 2) { Unload(gi, rec, cls); note = "You remember to clear the weapon before cleaning it."; }
                // tier 0-1: you don't think to check. The roll happens while you work.
            }
            if (note != null) GameUtil.Hud(note);

            bool stillLoaded = gun.NumRoundsInClip() > 0;
            TimedAction.Run("Cleaning " + GameUtil.NameOf(gi), _cleanMinutes.Value, ok =>
            {
                if (!ok) return;
                if (stillLoaded && gun.NumRoundsInClip() > 0)
                {
                    float nd = cls == WeaponClass.Revolver ? _ndRevolver.Value : _ndRifle.Value;
                    if (UnityEngine.Random.value < nd)
                    {
                        try { GameManager.GetConditionComponent().KillPlayer(DamageSource.BulletWound, "Negligent discharge while cleaning a loaded weapon."); }
                        catch (Exception e) { PatchLog.Error("Weapons.ND", e); }
                        return;
                    }
                    GameUtil.Hud("The weapon was loaded the whole time. You were lucky.", true);
                }
                float resid = Mathf.Lerp(0.25f, 0f, Services.Skills.GetLevel01(SkillId.Gunsmithing));
                rec.Fouling = resid;
                try { gun.SetJammed(false); } catch { }
                try { kit.Degrade(6f); } catch { }
                Services.Skills.AddXp(SkillId.Gunsmithing, 1f);
                GameUtil.Hud(GameUtil.NameOf(gi) + " is clean.");
            });
        }

        // ============================================================================ bench entries

        private static IEnumerable<GearItem> Armory()
        {
            return GameUtil.InventoryItems().Where(g => WeaponTables.ClassOf(g).HasValue);
        }

        IEnumerable<BenchEntry> IBenchProvider.Entries(BenchInfo bench)
        {
            // ---- upkeep: available everywhere
            foreach (var gi in Armory().ToList())
            {
                var gun = gi; var cls = WeaponTables.ClassOf(gun).Value;
                var rec = Rec(gun, true);
                if (cls == WeaponClass.Bow)
                {
                    yield return new BenchEntry { Label = "Inspect " + GameUtil.NameOf(gun), Detail = Describe(gun, rec), Run = () => GameUtil.Hud(GameUtil.NameOf(gun) + ": " + Describe(gun, rec), true) };
                    continue;
                }
                int tier = Services.Skills.GetTier(FireSkill(cls));
                bool loaded = gun.m_GunItem.NumRoundsInClip() > 0;
                yield return new BenchEntry
                {
                    Label = "Clean " + GameUtil.NameOf(gun),
                    Detail = string.Format("fouling {0:P0}{1}", rec.Fouling, loaded ? (tier >= 2 ? ", loaded: you will clear it first" : ", LOADED") : ""),
                    Run = () => CleanWeapon(gun, cls),
                };
                if (loaded)
                    yield return new BenchEntry { Label = "Clear " + GameUtil.NameOf(gun), Detail = gun.m_GunItem.NumRoundsInClip() + " round(s)", Run = () => { Unload(gun, rec, cls); GameUtil.Hud("You clear the weapon."); } };
                yield return new BenchEntry { Label = "Inspect " + GameUtil.NameOf(gun), Detail = Describe(gun, rec), Run = () => GameUtil.Hud(GameUtil.NameOf(gun) + ": " + Describe(gun, rec), true) };

                // load special rounds into a held, empty weapon
                if (gun == Held())
                    foreach (AmmoKind k in new[] { AmmoKind.HollowPoint, AmmoKind.SoftPoint })
                    {
                        int have = AmmoCount(cls, k);
                        if (have <= 0) continue;
                        var kind = k;
                        bool empty = gun.m_GunItem.m_RoundsInClip == 0;
                        yield return new BenchEntry
                        {
                            Label = string.Format("Load {0} rounds", WeaponTables.AmmoName(kind)),
                            Detail = empty ? string.Format("{0} in stock", have) : "unload the weapon first",
                            Enabled = empty,
                            Run = () =>
                            {
                                int n = Math.Min(have, gun.m_GunItem.m_ClipSize);
                                gun.m_GunItem.AddRoundsToClip(n, 100);
                                rec.Loaded = kind;
                                _ammoStock[cls + ":" + kind] = have - n;
                                GameUtil.Hud(string.Format("Loaded {0} {1} rounds.", n, WeaponTables.AmmoName(kind)));
                            },
                        };
                    }
            }

            // ---- ammo bench: hollow points
            if (bench.Kind == BenchKind.AmmoBench)
                foreach (var cls in new[] { WeaponClass.Rifle, WeaponClass.Revolver })
                {
                    var c = cls;
                    string baseName = c == WeaponClass.Rifle ? "RifleAmmoSingle" : "RevolverAmmoSingle";
                    int have = GameUtil.CountInInventory(baseName);
                    int tier = Services.Skills.GetTier(SkillId.Gunsmithing);
                    int n = Math.Min(5, have);
                    bool schem = Services.Regional.HasSchematic("hollow_point");
                    bool ok = schem && tier >= 1 && have >= 5 && Services.Scrap.GetScrap(ScrapGrade.Low) >= 1;
                    yield return new BenchEntry
                    {
                        Label = string.Format("Swage 5 {0} hollow points", c),
                        Detail = ok ? "5 rounds + 1 low scrap, 55 min" : (!schem ? "needs the hollow-point schematic (somewhere in the world)" : tier < 1 ? "needs Gunsmithing II" : "needs 5 rounds and 1 low scrap"),
                        Enabled = ok,
                        Run = () => CraftHollowPoints(c, baseName),
                    };
                }

            // ---- workbench / forge / ammo bench: mods
            foreach (var spec in WeaponTables.Mods.Values)
            {
                if (spec.Craftable && spec.Station == bench.Kind) yield return CraftModEntry(spec, bench);
            }
            if (bench.Kind == BenchKind.Workbench)
            {
                bool tools = GameUtil.CountInInventory("SimpleTools") + GameUtil.CountInInventory("HighQualityTools") > 0;
                foreach (var gi in Armory().ToList())
                {
                    var gun = gi; var cls = WeaponTables.ClassOf(gun).Value; var rec = Rec(gun, true);
                    foreach (var kv in _modStock.ToList())
                    {
                        if (kv.Value <= 0) continue;
                        var spec = WeaponTables.Mods[kv.Key];
                        if (!spec.Fits.Contains(cls) || rec.Has(spec.Id)) continue;
                        int need = spec.Tier;
                        SkillId sk = cls == WeaponClass.Bow ? SkillId.Archery : SkillId.Gunsmithing;
                        bool okSkill = Services.Skills.GetTier(sk) >= need;
                        yield return new BenchEntry
                        {
                            Label = string.Format("Install {0} on {1}", spec.Name, GameUtil.NameOf(gun)),
                            Detail = okSkill ? (tools ? "20 min" : "needs a toolkit") : "needs " + SkillMap.Display(sk) + " " + SkillMap.Roman[need],
                            Enabled = okSkill && tools,
                            Run = () => Install(gun, rec, spec, sk),
                        };
                    }
                    foreach (var m in rec.Mods.Keys.ToList())
                    {
                        var spec = WeaponTables.Mods[m];
                        SkillId sk = cls == WeaponClass.Bow ? SkillId.Archery : SkillId.Gunsmithing;
                        yield return new BenchEntry
                        {
                            Label = string.Format("Remove {0} from {1}", spec.Name, GameUtil.NameOf(gun)),
                            Detail = string.Format("risk of damage {0:P0}", Mathf.Lerp(0.35f, 0.02f, Services.Skills.GetLevel01(sk))),
                            Enabled = tools,
                            Run = () => Remove(gun, rec, spec, sk),
                        };
                    }
                }
            }
        }

        private string Describe(GearItem gi, WeaponRecord rec)
        {
            var mods = rec.Mods.Count == 0 ? "no mods" : string.Join(", ", rec.Mods.Select(kv => WeaponTables.Mods[kv.Key].Name + string.Format(" ({0:P0})", kv.Value)));
            string loaded = gi.m_GunItem != null && gi.m_GunItem.m_RoundsInClip > 0 ? ", loaded " + WeaponTables.AmmoName(rec.Loaded) : "";
            return string.Format("condition {0:0}%, fouling {1:P0}, {2}{3}", gi.GetNormalizedCondition() * 100f, rec.Fouling, mods, loaded);
        }

        private void CraftHollowPoints(WeaponClass c, string baseName)
        {
            if (GameUtil.CountInInventory(baseName) < 5 || !Services.Scrap.TryConsume(ScrapGrade.Low, 1)) { BenchMenu.Say("You are short of materials."); return; }
            GameUtil.RemoveFromInventory(baseName, 5);
            TimedAction.Run("Swaging hollow points", 55f, ok =>
            {
                if (!ok) { GameUtil.GiveItem(baseName, 1f, 5); Services.Scrap.Add(ScrapGrade.Low, 1); return; }
                float fail = Services.Crafting.FailureChance(SkillId.Gunsmithing, 1, 1f);
                if (UnityEngine.Random.value < fail)
                {
                    GameUtil.GiveItem(baseName, 0.6f, 2);
                    GameUtil.Hud("The swaging goes wrong. You salvage two rounds.", true);
                    Services.Skills.AddXp(SkillId.Gunsmithing, 0.5f);
                    return;
                }
                AddAmmo(c, AmmoKind.HollowPoint, 5);
                Services.Skills.AddXp(SkillId.Gunsmithing, 1.5f);
                GameUtil.Hud("Five hollow-point rounds ready.", true);
            });
        }

        private BenchEntry CraftModEntry(ModSpec spec, BenchInfo bench)
        {
            int tier = Services.Skills.GetTier(spec.CraftSkill);
            bool skill = tier >= spec.CraftTier;
            bool forgeOk = !spec.NeedsIndustrialForge || bench.ForgeClass == ForgeClass.Industrial;
            bool mats = spec.Materials == null || spec.Materials.All(m => { var p = m.Split(':'); return GameUtil.CountInInventory(p[0]) >= int.Parse(p[1]); });
            bool scrap = !spec.Scrap.HasValue || Services.Scrap.GetScrap(spec.Scrap.Value) >= spec.ScrapUnits;
            bool regional = spec.Regional == null || spec.Regional.All(m => { var p = m.Split(':'); return Services.Regional.Count(p[0]) >= int.Parse(p[1]); });
            string why = !skill ? "needs " + SkillMap.Display(spec.CraftSkill) + " " + SkillMap.Roman[spec.CraftTier]
                       : !forgeOk ? "needs the industrial forge"
                       : !mats ? "needs " + string.Join(", ", spec.Materials)
                       : !scrap ? "needs " + spec.ScrapUnits + " " + spec.Scrap + " scrap"
                       : !regional ? "needs " + string.Join(", ", spec.Regional.Select(m => { var p = m.Split(':'); return p[1] + " " + Services.Regional.Display(p[0]); }))
                       : "~60 min";
            return new BenchEntry
            {
                Label = "Make " + spec.Name,
                Detail = why,
                Enabled = skill && forgeOk && mats && scrap && regional && (spec.Station != BenchKind.Forge || bench.ForgeHot),
                Run = () =>
                {
                    if (spec.Scrap.HasValue && !Services.Scrap.TryConsume(spec.Scrap.Value, spec.ScrapUnits)) { BenchMenu.Say("Not enough scrap."); return; }
                    if (spec.Materials != null) foreach (var m in spec.Materials) { var p = m.Split(':'); GameUtil.RemoveFromInventory(p[0], int.Parse(p[1])); }
                    if (spec.Regional != null) foreach (var m in spec.Regional) { var p = m.Split(':'); Services.Regional.TryConsume(p[0], int.Parse(p[1])); }
                    TimedAction.Run("Making " + spec.Name, 60f, ok =>
                    {
                        if (!ok) { GameUtil.Hud("You give up; the materials are wasted."); return; }
                        float fail = Services.Crafting.FailureChance(spec.CraftSkill, spec.CraftTier, 1f);
                        if (UnityEngine.Random.value < fail) { GameUtil.Hud("The " + spec.Name + " doesn't come out right.", true); Services.Skills.AddXp(spec.CraftSkill, 1f); return; }
                        AddMod(spec.Id);
                        Services.Skills.AddXp(spec.CraftSkill, 3f);
                    });
                },
            };
        }

        private void Install(GearItem gun, WeaponRecord rec, ModSpec spec, SkillId sk)
        {
            int n; _modStock.TryGetValue(spec.Id, out n);
            if (n <= 0) return;
            TimedAction.Run("Fitting " + spec.Name, 20f, ok =>
            {
                if (!ok) return;
                _modStock[spec.Id] = n - 1;
                float q = Mathf.Clamp(Mathf.Lerp(0.45f, 1f, Services.Skills.GetLevel01(sk)) + UnityEngine.Random.Range(-0.05f, 0.05f), 0.3f, 1f);
                rec.Mods[spec.Id] = q;
                if (spec.Id == ModId.Suppressor) rec.SuppressorShotsLeft = Mathf.Max(5, Mathf.RoundToInt(_suppShots.Value * q));
                _base.Remove(gun.GetInstanceID());
                Services.Skills.AddXp(sk, 2f);
                GameUtil.Hud(string.Format("{0} fitted to the {1}: {2} installation.", spec.Name, GameUtil.NameOf(gun), q < 0.6f ? "sloppy" : (q < 0.85f ? "sound" : "rock-solid")), true);
            });
        }

        private void Remove(GearItem gun, WeaponRecord rec, ModSpec spec, SkillId sk)
        {
            TimedAction.Run("Removing " + spec.Name, 15f, ok =>
            {
                if (!ok) return;
                float risk = Mathf.Lerp(0.35f, 0.02f, Services.Skills.GetLevel01(sk));
                rec.Mods.Remove(spec.Id);
                _base.Remove(gun.GetInstanceID());
                if (UnityEngine.Random.value < risk) { GameUtil.Hud("You damage the " + spec.Name + " getting it off. It's ruined.", true); return; }
                int n; _modStock.TryGetValue(spec.Id, out n); _modStock[spec.Id] = n + 1;
                GameUtil.Hud(spec.Name + " removed intact.");
            });
        }

        // ============================================================================ ISaveSection

        void ISaveSection.Reset() { _records.Clear(); _modStock.Clear(); _ammoStock.Clear(); _base.Clear(); }

        JToken ISaveSection.Save()
        {
            var recs = new JObject();
            foreach (var kv in _records)
            {
                var r = kv.Value;
                if (r.Fouling < 0.005f && r.Mods.Count == 0 && r.Shots == 0) continue;
                recs[kv.Key] = new JObject
                {
                    ["f"] = Math.Round(r.Fouling, 3), ["s"] = r.Shots, ["a"] = (int)r.Loaded, ["ss"] = r.SuppressorShotsLeft,
                    ["m"] = new JObject(r.Mods.Select(m => new JProperty(m.Key.ToString(), Math.Round(m.Value, 3)))),
                };
            }
            return new JObject
            {
                ["records"] = recs,
                ["modStock"] = new JObject(_modStock.Where(x => x.Value > 0).Select(x => new JProperty(x.Key.ToString(), x.Value))),
                ["ammoStock"] = new JObject(_ammoStock.Where(x => x.Value > 0).Select(x => new JProperty(x.Key, x.Value))),
            };
        }

        void ISaveSection.Load(JToken t)
        {
            var o = t as JObject; if (o == null) return;
            var recs = o["records"] as JObject;
            if (recs != null)
                foreach (var p in recs.Properties())
                {
                    var j = p.Value as JObject; if (j == null) continue;
                    var r = new WeaponRecord { Fouling = (float?)j["f"] ?? 0f, Shots = (int?)j["s"] ?? 0, Loaded = (AmmoKind)((int?)j["a"] ?? 0), SuppressorShotsLeft = (int?)j["ss"] ?? 0 };
                    var m = j["m"] as JObject;
                    if (m != null) foreach (var mp in m.Properties()) { ModId id; if (Enum.TryParse(mp.Name, out id)) r.Mods[id] = (float)mp.Value; }
                    _records[p.Name] = r;
                }
            var ms = o["modStock"] as JObject;
            if (ms != null) foreach (var p in ms.Properties()) { ModId id; if (Enum.TryParse(p.Name, out id)) _modStock[id] = (int)p.Value; }
            var asx = o["ammoStock"] as JObject;
            if (asx != null) foreach (var p in asx.Properties()) _ammoStock[p.Name] = (int)p.Value;
        }

        public override void DrawStatus(StatusWriter w)
        {
            var h = Held();
            var c = WeaponTables.ClassOf(h);
            if (c.HasValue) { var r = Rec(h, false); w.Line("Held {0}: {1}", GameUtil.NameOf(h), r != null ? Describe(h, r) : "no history yet"); }
            if (_modStock.Values.Any(v => v > 0)) w.Line("Mod stock: " + string.Join(", ", _modStock.Where(x => x.Value > 0).Select(x => WeaponTables.Mods[x.Key].Name + " x" + x.Value)));
            if (_ammoStock.Values.Any(v => v > 0)) w.Line("Special ammo: " + string.Join(", ", _ammoStock.Where(x => x.Value > 0).Select(x => x.Key + " x" + x.Value)));
            w.Line("F7 anywhere = field actions (clean, clear, load); at a workbench/ammo bench/forge = mods and ammo.");
        }
    }
}
