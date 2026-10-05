using System;
using System.Collections.Generic;
using MelonLoader;

namespace TLDOverhaul.Core
{
    /// <summary>Thin wrapper over a MelonPreferences entry so tuning knobs live in UserData/MelonPreferences.cfg.</summary>
    public sealed class Setting<T>
    {
        private readonly MelonPreferences_Entry<T> _entry;
        internal Setting(MelonPreferences_Entry<T> entry) { _entry = entry; }
        public T Value => _entry.Value;
        public static implicit operator T(Setting<T> s) => s.Value;
    }

    /// <summary>
    /// Central tuning surface. Constants "come from you playing it" (playbook), so every number a designer
    /// might want to touch is a preference rather than a literal.
    /// </summary>
    public static class Cfg
    {
        private static readonly Dictionary<string, MelonPreferences_Category> Cats = new Dictionary<string, MelonPreferences_Category>();

        private static MelonPreferences_Category Cat(string name)
        {
            if (!Cats.TryGetValue(name, out var c))
            {
                c = MelonPreferences.CreateCategory("TLDOverhaul_" + name, "TLD Overhaul - " + name);
                Cats[name] = c;
            }
            return c;
        }

        public static Setting<float> F(string category, string key, float def, string desc = null)
            => new Setting<float>(Cat(category).CreateEntry(key, def, null, desc));

        public static Setting<int> I(string category, string key, int def, string desc = null)
            => new Setting<int>(Cat(category).CreateEntry(key, def, null, desc));

        public static Setting<bool> B(string category, string key, bool def, string desc = null)
            => new Setting<bool>(Cat(category).CreateEntry(key, def, null, desc));

        public static Setting<string> S(string category, string key, string def, string desc = null)
            => new Setting<string>(Cat(category).CreateEntry(key, def, null, desc));

        public static void SaveAll()
        {
            try { MelonPreferences.Save(); } catch (Exception e) { MelonLogger.Warning("Could not save preferences: " + e.Message); }
        }
    }
}
