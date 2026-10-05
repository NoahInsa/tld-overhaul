using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using TLDOverhaul.Core;
using UnityEngine;

[assembly: MelonInfo(typeof(OverhaulMod), "TLD Overhaul", "0.1.0", "NoahInsa")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
// We apply patches ourselves, class by class, so one bad target cannot abort the rest (see PatchRunner).
[assembly: HarmonyDontPatchAll]

namespace TLDOverhaul.Core
{
    /// <summary>
    /// TLD x PZ overhaul. Single mod, twelve systems, built in the dependency order from the playbook:
    /// degradation -> nutrition -> clothing -> skills -> crafting -> forge -> building -> interactive ->
    /// weapons -> vehicles -> skiing -> exploration.
    /// </summary>
    public class OverhaulMod : MelonMod
    {
        public static OverhaulMod Instance { get; private set; }
        public static readonly List<OverhaulSystem> Systems = new List<OverhaulSystem>();

        private bool _overlay;
        private GUIStyle _lineStyle, _headStyle;

        private static IEnumerable<OverhaulSystem> BuildSystems()
        {
            // Order == dependency order. A system may consume services of any system listed before it.
            // (Systems are appended here as each one lands.)
            yield return new TLDOverhaul.Degradation.DegradationSystem();
            yield return new TLDOverhaul.Nutrition.NutritionSystem();
            yield return new TLDOverhaul.Clothing.ClothingSystem();
            yield return new TLDOverhaul.Skills.SkillsSystem();
            yield return new TLDOverhaul.Crafting.CraftingSystem();
            yield return new TLDOverhaul.Forge.ForgeSystem();
            yield return new TLDOverhaul.Building.BuildingSystem();
            yield return new TLDOverhaul.Interactive.InteractiveSystem();
            yield return new TLDOverhaul.Weapons.WeaponsSystem();
            yield return new TLDOverhaul.Vehicles.VehiclesSystem();
            yield return new TLDOverhaul.Skiing.SkiingSystem();
            yield return new TLDOverhaul.Exploration.ExplorationSystem();
        }

        public override void OnInitializeMelon()
        {
            Instance = this;
            LoggerInstance.Msg("TLD Overhaul 0.1.0 starting (MelonLoader " + MelonLoader.Properties.BuildInfo.Version + ")");

            // Core patches: the consolidated save sidecar.
            PatchRunner.Apply(HarmonyInstance, "Core", new[]
            {
                typeof(SaveGame_Postfix), typeof(RestoreGame_Prefix), typeof(DeleteSaveFiles_Postfix), typeof(CopyData_Postfix),
            });

            DebugMenu.Init();
            SaveManager.StateLoaded += () =>
            {
                foreach (var sys in Systems)
                {
                    if (!sys.Enabled) continue;
                    try { sys.OnStateLoaded(); } catch (Exception e) { LoggerInstance.Error("[" + sys.Name + "] OnStateLoaded: " + e.Message); }
                }
            };
            foreach (var s in BuildSystems())
            {
                Systems.Add(s);
                s.Initialize(HarmonyInstance);
            }

            Cfg.SaveAll();
            LoggerInstance.Msg("Ready. F7 = station menu, F8 = status overlay, F9 = debug menu, F10 = patch status. Patch failures: " + PatchLog.FailedCount);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            try
            {
                if (string.Equals(sceneName, "MainMenu", StringComparison.OrdinalIgnoreCase) || string.Equals(sceneName, "Empty", StringComparison.OrdinalIgnoreCase))
                {
                    SaveManager.ResetAll();
                    GameUtil.ResetAllClocks();
                }
                GameUtil.ResetAllClocks();
                BenchLocator.Invalidate();
                foreach (var s in Systems)
                {
                    if (!s.Enabled) continue;
                    try { s.OnSceneLoaded(sceneName); } catch (Exception e) { LoggerInstance.Error("[" + s.Name + "] OnSceneLoaded: " + e.Message); }
                }
            }
            catch (Exception e) { LoggerInstance.Error("OnSceneWasInitialized: " + e); }
        }

        public override void OnUpdate()
        {
            if (Keys.Down(KeyCode.F8)) _overlay = !_overlay;
            if (Keys.Down(KeyCode.F10)) PatchLog.DumpStatus();
            DebugMenu.Tick();
            if (GameUtil.InGame) BenchMenu.Tick();

            if (!GameUtil.InGame) return;
            foreach (var s in Systems)
            {
                if (!s.Enabled) continue;
                try { s.OnUpdate(); }
                catch (Exception e) { PatchLog.Error(s.Name + ".OnUpdate", e); }
            }
        }

        public override void OnGUI()
        {
            foreach (var s in Systems)
            {
                if (!s.Enabled) continue;
                try { s.OnGui(); }
                catch (Exception e) { PatchLog.Error(s.Name + ".OnGui", e); }
            }
            if (_overlay) DrawOverlay();
            DebugMenu.Draw();
            BenchMenu.Draw();
        }

        private void DrawOverlay()
        {
            try
            {
                if (_lineStyle == null)
                {
                    _lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
                    _headStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                }
                GUILayout.BeginArea(new Rect(16, 16, 460, Screen.height - 32));
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("TLD Overhaul  -  F8 close, F10 patch status", _headStyle);
                foreach (var s in Systems)
                {
                    if (!s.Enabled) continue;
                    var w = new StatusWriter();
                    try { s.DrawStatus(w); } catch (Exception e) { w.Line("status error: " + e.Message); }
                    if (w.Lines.Count == 0) continue;
                    GUILayout.Label(s.Name, _headStyle);
                    foreach (var l in w.Lines) GUILayout.Label(l, _lineStyle);
                }
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            catch (Exception e) { PatchLog.Error("Core.Overlay", e); }
        }
    }
}
