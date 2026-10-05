using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace TLDOverhaul.Core
{
    public enum BenchKind { Forge, AmmoBench, Workbench, Field, Vehicle }

    public enum ForgeClass { Small, Industrial }

    /// <summary>A fixed production station near the player.</summary>
    public sealed class BenchInfo
    {
        public BenchKind Kind;
        public Component Component;
        public Transform Transform;
        public float Distance;
        /// <summary>Only meaningful for forges: Desolation Point's coastal workshop is Small, Forlorn Muskeg's Old Spence works is Industrial.</summary>
        public ForgeClass ForgeClass;

        public bool ForgeHot
        {
            get
            {
                if (Kind != BenchKind.Forge) return true;
                try { var f = Component as Il2Cpp.Forge; return f != null && f.ForgeHotEnoughForUse(); } catch { return true; }
            }
        }
    }

    /// <summary>
    /// Finds forges / ammo benches / workbenches. These are world geography (the design's "production stays where the world
    /// put it"), so the locator also tells every system which station - and which kind of forge - the player is standing at.
    /// </summary>
    public static class BenchLocator
    {
        private static readonly List<Component> Forges = new List<Component>();
        private static readonly List<Component> Ammo = new List<Component>();
        private static readonly List<Component> Work = new List<Component>();
        private static readonly List<Component> Cars = new List<Component>();
        private static float _nextRefresh;

        public static void Invalidate() { _nextRefresh = 0f; }

        private static void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 8f;
            Forges.Clear(); Ammo.Clear(); Work.Clear(); Cars.Clear();
            try
            {
                foreach (var f in UnityEngine.Object.FindObjectsOfType<Il2Cpp.Forge>()) Forges.Add(f);
                foreach (var a in UnityEngine.Object.FindObjectsOfType<AmmoWorkBench>()) Ammo.Add(a);
                foreach (var w in UnityEngine.Object.FindObjectsOfType<WorkBench>()) Work.Add(w);
                foreach (var v in UnityEngine.Object.FindObjectsOfType<VehicleDoor>()) Cars.Add(v);
            }
            catch (Exception e) { PatchLog.Error("Core.BenchLocator", e); }
        }

        public static ForgeClass CurrentForgeClass
        {
            get { return World.RegionKey == "forlorn_muskeg" ? ForgeClass.Industrial : ForgeClass.Small; }
        }

        public static BenchInfo Nearest(float maxDistance)
        {
            Refresh();
            BenchInfo best = null;
            Consider(Forges, BenchKind.Forge, maxDistance, ref best);
            Consider(Ammo, BenchKind.AmmoBench, maxDistance, ref best);
            Consider(Work, BenchKind.Workbench, maxDistance, ref best);
            Consider(Cars, BenchKind.Vehicle, maxDistance, ref best);
            return best;
        }

        /// <summary>All located components of a kind (cached for a few seconds).</summary>
        public static List<Component> All(BenchKind kind)
        {
            Refresh();
            switch (kind) { case BenchKind.Forge: return Forges; case BenchKind.AmmoBench: return Ammo; case BenchKind.Vehicle: return Cars; default: return Work; }
        }

        public static BenchInfo Nearest(BenchKind kind, float maxDistance)
        {
            Refresh();
            BenchInfo best = null;
            Consider(All(kind), kind, maxDistance, ref best);
            return best;
        }

        private static void Consider(List<Component> list, BenchKind kind, float maxDistance, ref BenchInfo best)
        {
            Vector3 p = GameUtil.PlayerPos;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                Transform t;
                try { t = c.transform; } catch { continue; }
                float d = Vector3.Distance(p, t.position);
                if (d > maxDistance || (best != null && d >= best.Distance)) continue;
                best = new BenchInfo { Kind = kind, Component = c, Transform = t, Distance = d, ForgeClass = CurrentForgeClass };
            }
        }
    }

    public sealed class BenchEntry
    {
        public string Label;
        public string Detail;
        public bool Enabled = true;
        public Action Run;
    }

    public interface IBenchProvider
    {
        string Title { get; }
        /// <summary>Entries this system offers at the given station. Empty = nothing to show.</summary>
        IEnumerable<BenchEntry> Entries(BenchInfo bench);
    }

    /// <summary>
    /// Keyboard-driven station menu (F7 near a station; Up/Down, Enter, Esc). TLD's real panels cannot be extended from a
    /// code-only mod, so station actions (smelting, ammo crafting, smithing, weapon mods) appear here instead.
    /// </summary>
    public static class BenchMenu
    {
        private static readonly List<IBenchProvider> Providers = new List<IBenchProvider>();
        private static readonly List<KeyValuePair<string, BenchEntry>> Rows = new List<KeyValuePair<string, BenchEntry>>();
        private static bool _open;
        private static int _sel;
        private static BenchInfo _bench;
        private static float _rebuildAt;
        private static string _message = "";
        private static float _messageUntil;
        private static GUIStyle _line, _head, _dim;

        public static bool IsOpen { get { return _open; } }
        public static void Register(IBenchProvider p) { Providers.Add(p); }

        public static void Say(string msg) { _message = msg; _messageUntil = Time.unscaledTime + 4f; }

        public static void Tick()
        {
            if (!GameUtil.InGame) { _open = false; return; }
            if (Keys.Down(KeyCode.F7))
            {
                if (_open) { _open = false; return; }
                // Near a station: station actions. Elsewhere: the "field" context (weapon upkeep and other personal actions).
                _bench = BenchLocator.Nearest(4f) ?? new BenchInfo { Kind = BenchKind.Field };
                _sel = 0; _open = true; Rebuild();
                return;
            }
            if (!_open) return;

            if (_bench != null && _bench.Kind != BenchKind.Field)
            {
                _bench = BenchLocator.Nearest(6f);
                if (_bench == null) { _open = false; return; }
            }
            if (GameUtil.OverlayActive || TimedAction.Busy) return;

            if (Time.unscaledTime >= _rebuildAt) Rebuild();
            if (Keys.Down(KeyCode.Escape)) { _open = false; return; }
            if (Rows.Count == 0) return;
            if (Keys.Down(KeyCode.DownArrow)) _sel = (_sel + 1) % Rows.Count;
            if (Keys.Down(KeyCode.UpArrow)) _sel = (_sel + Rows.Count - 1) % Rows.Count;
            if (Keys.Down(KeyCode.Return) || Keys.Down(KeyCode.KeypadEnter))
            {
                var e = Rows[Mathf.Clamp(_sel, 0, Rows.Count - 1)].Value;
                if (!e.Enabled) { Say(e.Detail ?? "Not available."); return; }
                try { e.Run(); } catch (Exception ex) { MelonLogger.Error("[bench] '" + e.Label + "' failed: " + ex); }
                _rebuildAt = 0f;
            }
        }

        private static void Rebuild()
        {
            _rebuildAt = Time.unscaledTime + 0.75f;
            Rows.Clear();
            if (_bench == null) return;
            foreach (var p in Providers)
            {
                try
                {
                    foreach (var e in p.Entries(_bench)) Rows.Add(new KeyValuePair<string, BenchEntry>(p.Title, e));
                }
                catch (Exception ex) { PatchLog.Error("Core.BenchMenu." + p.Title, ex); }
            }
            if (_sel >= Rows.Count) _sel = Math.Max(0, Rows.Count - 1);
        }

        public static void Draw()
        {
            if (!_open || _bench == null) return;
            try
            {
                if (_line == null)
                {
                    _line = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                    _head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
                    _dim = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                }
                float w = 520f;
                GUILayout.BeginArea(new Rect(24, Screen.height * 0.18f, w, Screen.height * 0.64f));
                GUILayout.BeginVertical(GUI.skin.box);
                string title = _bench.Kind == BenchKind.Field ? "Field actions" : _bench.Kind == BenchKind.Vehicle ? "Vehicle" : _bench.Kind == BenchKind.Forge ? "Forge (" + _bench.ForgeClass + ")" + (_bench.ForgeHot ? "" : " - COLD") : _bench.Kind.ToString();
                GUILayout.Label(title, _head);
                GUILayout.Label("Up/Down select    Enter confirm    Esc / F7 close", _dim);
                if (Rows.Count == 0) GUILayout.Label("Nothing to do here with what you are carrying.", _line);
                string group = null;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].Key != group) { group = Rows[i].Key; GUILayout.Label("-- " + group + " --", _head); }
                    var e = Rows[i].Value;
                    string text = (i == _sel ? "> " : "   ") + e.Label + (e.Detail != null && e.Enabled ? "   (" + e.Detail + ")" : "") + (e.Enabled ? "" : "   [" + (e.Detail ?? "unavailable") + "]");
                    GUILayout.Label(text, _line);
                }
                if (Time.unscaledTime < _messageUntil) GUILayout.Label(_message, _head);
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            catch (Exception ex) { PatchLog.Error("Core.BenchMenu.Draw", ex); }
        }
    }
}
