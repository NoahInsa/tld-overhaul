using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// F9 test menu. Systems register buttons ("give next skill book", "grant scrap", "wreck a vehicle part") so a playtest
    /// does not depend on finding the right loot. Disable via the Core/DebugMenu preference for a "clean" run.
    /// </summary>
    public static class DebugMenu
    {
        private sealed class Entry { public string Group, Label; public Action Run; }
        private static readonly List<Entry> Entries = new List<Entry>();
        private static bool _open;
        private static Vector2 _scroll;
        private static Setting<bool> _enabled;
        private static GUIStyle _btn;

        public static void Init()
        {
            _enabled = Cfg.B("Core", "DebugMenu", true, "F9 opens a menu of test actions (give books, grant parts...).");
        }

        public static void Register(string group, string label, Action run)
        {
            Entries.Add(new Entry { Group = group, Label = label, Run = run });
        }

        public static void Tick()
        {
            if (_enabled == null || !_enabled.Value) { _open = false; return; }
            if (Keys.Down(KeyCode.F9)) _open = !_open;
        }

        public static void Draw()
        {
            if (!_open || _enabled == null || !_enabled.Value) return;
            try
            {
                if (_btn == null) _btn = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
                float w = 360f;
                GUILayout.BeginArea(new Rect(Screen.width - w - 16, 16, w, Screen.height - 32));
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("TLD Overhaul debug (F9 to close)");
                _scroll = GUILayout.BeginScrollView(_scroll);
                string group = null;
                foreach (var e in Entries)
                {
                    if (e.Group != group) { group = e.Group; GUILayout.Label("-- " + group + " --"); }
                    if (GUILayout.Button(e.Label, _btn))
                    {
                        try { e.Run(); }
                        catch (Exception ex) { MelonLogger.Error("[debug] '" + e.Label + "' failed: " + ex); }
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            catch (Exception ex) { PatchLog.Error("Core.DebugMenu", ex); }
        }
    }
}
