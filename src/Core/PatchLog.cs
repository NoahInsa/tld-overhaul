using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MelonLoader;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// Every Harmony patch calls <see cref="Fire"/> as its first statement. The first call per id logs a line so the
    /// MelonLoader console confirms the patch is live. Patches that were applied but never fire are listed by
    /// <see cref="DumpStatus"/> (F10 in-game) - the quickest way to spot an IL2CPP-inlined target.
    /// </summary>
    public static class PatchLog
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Fired = new HashSet<string>();
        private static readonly List<string> Applied = new List<string>();
        private static readonly List<string> Failed = new List<string>();
        private static readonly Dictionary<string, int> ErrorCounts = new Dictionary<string, int>();

        public static void Fire(string id)
        {
            lock (Gate)
            {
                if (!Fired.Add(id)) return;
            }
            MelonLogger.Msg("[patch fired] " + id);
        }

        public static void Error(string id, Exception e)
        {
            int n;
            lock (Gate)
            {
                ErrorCounts.TryGetValue(id, out n);
                ErrorCounts[id] = ++n;
            }
            // Log the first few, then every 500th, so a bad patch in a hot path can't flood the log.
            if (n <= 3 || n % 500 == 0)
                MelonLogger.Error($"[patch error] {id} (x{n}): {e}");
        }

        internal static void MarkApplied(string id) { lock (Gate) Applied.Add(id); }
        internal static void MarkFailed(string id) { lock (Gate) Failed.Add(id); }

        public static string DumpStatus()
        {
            lock (Gate)
            {
                var unfired = Applied.Where(a => !Fired.Contains(a)).ToList();
                var msg = $"Patches applied: {Applied.Count}, fired so far: {Fired.Count(f => Applied.Contains(f))}, failed to apply: {Failed.Count}";
                MelonLogger.Msg("[patch status] " + msg);
                foreach (var f in Failed) MelonLogger.Warning("[patch status] FAILED: " + f);
                foreach (var u in unfired) MelonLogger.Msg("[patch status] not fired yet: " + u);
                return msg;
            }
        }

        public static int FailedCount { get { lock (Gate) return Failed.Count; } }
    }

    /// <summary>Applies patch classes one at a time so a single bad target cannot take the whole mod down.</summary>
    public static class PatchRunner
    {
        public static void Apply(HarmonyLib.Harmony harmony, string system, IEnumerable<Type> patchTypes)
        {
            foreach (var t in patchTypes)
            {
                string id = system + "." + t.Name;
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                    PatchLog.MarkApplied(id);
                }
                catch (Exception e)
                {
                    PatchLog.MarkFailed(id);
                    MelonLogger.Error($"[patch apply] {id} FAILED: {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }
}
