using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Gear;
using MelonLoader;
using TLDOverhaul.Building;
using TLDOverhaul.Core;
using TLDOverhaul.Crafting;
using UnityEngine;

namespace TLDOverhaul.Interactive
{
    public enum CraftKind { Smithing, Tanning, Tailoring }

    /// <summary>
    /// Replaces the progress bar's "wait and receive" with doing the thing. The vanilla crafting flow is left intact (menu,
    /// materials, preparation time); when the item is finished, a short hands-on session decides how WELL it was made. The
    /// session's score becomes the product's quality, and a botched session fails the craft (materials partly lost).
    ///  * Smithing - heat to colour, strike with timing and placement, quench at the right moment;
    ///  * Tanning - scrape, stretch, smoke;
    ///  * Tailoring - cut along the line, stitch with steady tension.
    /// Skill never gates the attempt; it widens the timing windows and sharpens the visual cues.
    /// </summary>
    public sealed class InteractiveSystem : OverhaulSystem
    {
        public static InteractiveSystem Instance { get; private set; }
        public override string Name { get { return "Interactive"; } }

        private sealed class Session
        {
            public string Title;
            public List<Stage> Stages = new List<Stage>();
            public List<float> Weights = new List<float>();
            public int Index;
            public float PauseUntil;           // brief result display between stages
            public bool BetweenStages;
            public Action<float> OnDone;
            public bool PrevFreeze;
            public float Lvl;
            public int Tier;
        }

        private Session _s;
        private Setting<bool> _smith, _tan, _tailor;
        private GUIStyle _title, _body;

        public bool Active { get { return _s != null; } }

        protected override void OnInit()
        {
            Instance = this;
            _smith = Cfg.B(Name, "Smithing", true, "Hands-on blacksmithing for forge recipes and hardware.");
            _tan = Cfg.B(Name, "Tanning", true, "Hands-on hide tanning.");
            _tailor = Cfg.B(Name, "Tailoring", true, "Hands-on tailoring of clothing.");

            CraftingSystem.InteractiveClaims = Claims;
            CraftingSystem.CraftedHooks.Add(OnCrafted);
            BuildingSystem.InteractiveForge = StartForgeHardware;

            DebugMenu.Register("Interactive", "Try smithing session", () => Start(CraftKind.Smithing, "Practice at the anvil", SkillId.Blacksmithing, 1, s => GameUtil.Hud(string.Format("Practice score {0:P0}", s))));
            DebugMenu.Register("Interactive", "Try tanning session", () => Start(CraftKind.Tanning, "Practice tanning", SkillId.Tanning, 1, s => GameUtil.Hud(string.Format("Practice score {0:P0}", s))));
            DebugMenu.Register("Interactive", "Try tailoring session", () => Start(CraftKind.Tailoring, "Practice tailoring", SkillId.Mending, 1, s => GameUtil.Hud(string.Format("Practice score {0:P0}", s))));
        }

        // ============================================================================ which recipes are hands-on

        public CraftKind? Detect(BlueprintData bp)
        {
            if (bp == null) return null;
            try
            {
                if (bp.m_CraftingResultType != CraftingResult.StandardGear || bp.m_CraftedResultGear == null) return null;
                var skill = BlueprintInfo.SkillFor(bp);
                if (bp.m_RequiredCraftingLocation == CraftingLocation.Forge && _smith.Value) return CraftKind.Smithing;
                if (skill == SkillId.Tanning && _tan.Value) return CraftKind.Tanning;
                if (skill == SkillId.Mending && bp.m_CraftedResultGear.m_ClothingItem != null && _tailor.Value) return CraftKind.Tailoring;
            }
            catch { }
            return null;
        }

        public bool Claims(BlueprintData bp) { return Enabled && !Active && Detect(bp).HasValue; }

        private static SkillId SkillOf(CraftKind k)
        {
            switch (k) { case CraftKind.Smithing: return SkillId.Blacksmithing; case CraftKind.Tanning: return SkillId.Tanning; default: return SkillId.Mending; }
        }

        // ============================================================================ hooks

        private void OnCrafted(CraftingOperation op, CraftingSystem.Outcome outcome, List<GearItem> made)
        {
            var bp = op.Blueprint;
            if (outcome != CraftingSystem.Outcome.Succeeded || made == null || made.Count == 0 || !Detect(bp).HasValue || Active) return;
            var kind = Detect(bp).Value;
            var csys = CraftingSystem.Instance;
            var req = csys != null ? csys.Requirement(bp) : default(RecipeReq);
            SkillId skill = req.Skill ?? SkillOf(kind);
            GearItem tool = null; try { tool = op.m_Tool; } catch { }
            var items = new List<GearItem>(made);
            string name = BlueprintInfo.ResultName(bp);

            Start(kind, "Making: " + name, skill, req.Tier, score =>
            {
                float perf = tool != null ? Services.Tools.GetPerformance(tool) : 1f;
                if (score < 0.12f)
                {
                    var inv = GameManager.GetInventoryComponent();
                    foreach (var gi in items) { try { if (gi != null) inv.DestroyGear(gi); } catch { } }
                    if (csys != null) csys.RefundFor(bp);
                    GameUtil.Hud("The " + name + " is ruined. You salvage some of the materials.", true);
                    Services.Skills.AddXp(skill, 0.5f + 0.25f * req.Tier);
                    return;
                }
                float q = Mathf.Clamp(Mathf.Lerp(0.2f, 1f, score) * Mathf.Lerp(0.85f, 1f, Mathf.Clamp01(perf)), 0.2f, 1f);
                foreach (var gi in items)
                {
                    try { if (gi != null && CraftingSystem.HasConditionPublic(gi)) gi.SetNormalizedHP(q, false); } catch { }
                }
                GameUtil.Hud(string.Format("{0}: {1} workmanship.", name, CraftingSystem.WorkmanshipPublic(q)), true);
                Services.Skills.AddXp(skill, 1.5f + 0.75f * req.Tier);
            });
        }

        private bool StartForgeHardware(string label, float minutes, Action<float> finish)
        {
            if (!_smith.Value || Active) return false;
            return Start(CraftKind.Smithing, label, SkillId.Blacksmithing, 0, score =>
            {
                try { GameManager.GetTimeOfDayComponent().AccelerateTime(minutes, Mathf.Clamp(minutes / 20f, 1.5f, 8f)); } catch { }
                finish(Mathf.Lerp(0.3f, 1.1f, score));
            });
        }

        // ============================================================================ sessions

        public bool Start(CraftKind kind, string title, SkillId skill, int tier, Action<float> onDone)
        {
            if (_s != null || !GameUtil.InGame) return false;
            float lvl = Services.Skills.GetLevel01(skill);
            var s = new Session { Title = title, OnDone = onDone, Lvl = lvl, Tier = tier };
            switch (kind)
            {
                case CraftKind.Smithing:
                    Add(s, new HeatStage(tier >= 3 ? 0.85f : 0.62f, tier >= 3 ? "white heat (forge welding)" : "cherry red (shaping)"), 0.30f);
                    Add(s, new SweepStage(4 + tier, "Strike the work"), 0.50f);
                    Add(s, new QuenchStage(), 0.20f);
                    break;
                case CraftKind.Tanning:
                    Add(s, new BeatStage("Scrape the hide clean", "Press SPACE on each stroke. A mistimed stroke can cut through the hide.", 6, 0.9f), 0.35f);
                    Add(s, new TrackStage("Stretch on the frame", "Hold A / D to keep the tension even.", false, 8f), 0.35f);
                    Add(s, new WindowStage("Smoke the hide", "Press SPACE to take it out when the smoke has set it.", 0.70f, 6f), 0.30f);
                    break;
                default:
                    Add(s, new TrackStage("Cut along the line", "Hold W / S to follow the line.", true, 8f), 0.5f);
                    Add(s, new BeatStage("Stitch", "Press SPACE with a steady rhythm. Even tension, even stitches.", 8, 0.8f), 0.5f);
                    break;
            }
            s.Stages[0].Begin(lvl, tier);
            try { var pm = GameManager.GetPlayerManagerComponent(); s.PrevFreeze = pm.m_FreezeMovement; pm.m_FreezeMovement = true; } catch { }
            _s = s;
            return true;
        }

        private static void Add(Session s, Stage st, float w) { s.Stages.Add(st); s.Weights.Add(w); }

        private void Finish(bool aborted)
        {
            var s = _s; _s = null;
            if (s == null) return;
            try { GameManager.GetPlayerManagerComponent().m_FreezeMovement = s.PrevFreeze; } catch { }
            if (aborted) return;
            float total = 0f, wsum = 0f;
            for (int i = 0; i < s.Stages.Count; i++) { total += s.Stages[i].Score * s.Weights[i]; wsum += s.Weights[i]; }
            float score = wsum > 0f ? total / wsum : 0f;
            try { s.OnDone?.Invoke(score); } catch (Exception e) { MelonLogger.Error("[Interactive] completion threw: " + e); }
        }

        public override void OnSceneLoaded(string sceneName) { if (_s != null) Finish(true); }

        public override void OnUpdate()
        {
            if (_s == null) return;
            float dt = Time.unscaledDeltaTime;
            var s = _s;
            if (s.BetweenStages)
            {
                if (Time.unscaledTime < s.PauseUntil) return;
                s.BetweenStages = false;
                s.Index++;
                if (s.Index >= s.Stages.Count) { Finish(false); return; }
                s.Stages[s.Index].Begin(s.Lvl, s.Tier);
                return;
            }
            if (s.Stages[s.Index].Tick(dt))
            {
                s.BetweenStages = true;
                s.PauseUntil = Time.unscaledTime + 1.3f;
            }
        }

        public override void OnGui()
        {
            if (_s == null) return;
            try
            {
                if (_title == null)
                {
                    _title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                    _body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };
                }
                float w = 760f, h = 300f;
                var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                Ui.Frame(box);
                var st = _s.Stages[_s.Index];
                GUI.Label(new Rect(box.x + 24, box.y + 14, w - 48, 28), _s.Title, _title);
                GUI.Label(new Rect(box.x + 24, box.y + 46, w - 48, 26), string.Format("Step {0}/{1}: {2}", _s.Index + 1, _s.Stages.Count, st.Title), _body);
                GUI.Label(new Rect(box.x + 24, box.y + 72, w - 48, 40), st.Hint, _body);
                var old = GUI.skin.label.fontSize; var oldc = GUI.skin.label.normal.textColor;
                GUI.skin.label.fontSize = 14; GUI.skin.label.normal.textColor = Color.white;
                st.Draw(box);
                GUI.skin.label.fontSize = old; GUI.skin.label.normal.textColor = oldc;
                if (_s.BetweenStages) GUI.Label(new Rect(box.x + 24, box.y + h - 40, w - 48, 28), st.Result, _title);
            }
            catch (Exception e) { PatchLog.Error("Interactive.OnGui", e); }
        }

        public override void DrawStatus(StatusWriter w)
        {
            w.Line("Hands-on crafting: smithing {0}, tanning {1}, tailoring {2}. {3}", _smith.Value ? "on" : "off", _tan.Value ? "on" : "off", _tailor.Value ? "on" : "off", Active ? "SESSION ACTIVE" : "");
        }
    }
}
