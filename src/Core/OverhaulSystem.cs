using System;
using System.Collections.Generic;
using MelonLoader;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// Base class for each of the twelve systems. A system owns: a patch-class list, one save section, optional
    /// services it exposes, and optional per-frame / GUI work. Disabling a system (preference) skips its patches.
    /// </summary>
    public abstract class OverhaulSystem
    {
        public abstract string Name { get; }

        /// <summary>Harmony patch classes belonging to this system. Applied one by one with error isolation.</summary>
        public virtual IEnumerable<Type> PatchTypes { get { return Array.Empty<Type>(); } }

        private Setting<bool> _enabled;
        public bool Enabled { get { return _enabled == null || _enabled.Value; } }

        internal void Initialize(HarmonyLib.Harmony harmony)
        {
            _enabled = Cfg.B(Name, "Enabled", true, "Turn this whole system off without removing the mod.");
            if (!_enabled.Value)
            {
                MelonLogger.Msg("[" + Name + "] disabled by preference.");
                return;
            }
            try { OnInit(); }
            catch (Exception e) { MelonLogger.Error("[" + Name + "] OnInit failed: " + e); return; }
            PatchRunner.Apply(harmony, Name, PatchTypes);
            MelonLogger.Msg("[" + Name + "] ready.");
        }

        /// <summary>Register settings, save section and services here. Runs once, before patches are applied.</summary>
        protected virtual void OnInit() { }

        /// <summary>Per frame, only while a game scene is active.</summary>
        public virtual void OnUpdate() { }

        /// <summary>A scene finished initializing (main menu included).</summary>
        public virtual void OnSceneLoaded(string sceneName) { }

        /// <summary>Called after a sidecar was read (or reset for a fresh game) so systems can rebuild caches.</summary>
        public virtual void OnStateLoaded() { }

        /// <summary>Immediate-mode GUI work that should always run (mini-games, prompts).</summary>
        public virtual void OnGui() { }

        /// <summary>Lines shown in the F8 status overlay.</summary>
        public virtual void DrawStatus(StatusWriter w) { }
    }

    /// <summary>Tiny abstraction so systems describe their status without touching IMGUI directly.</summary>
    public sealed class StatusWriter
    {
        public readonly List<string> Lines = new List<string>();
        public void Line(string s) { Lines.Add(s); }
        public void Line(string fmt, params object[] args) { Lines.Add(string.Format(fmt, args)); }
    }
}
