using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json.Linq;

namespace TLDOverhaul.Core
{
    /// <summary>One section of the consolidated sidecar. Each system owns exactly one.</summary>
    public interface ISaveSection
    {
        string Key { get; }
        /// <summary>Return state to a fresh-game default.</summary>
        void Reset();
        JToken Save();
        void Load(JToken token);
    }

    /// <summary>
    /// ONE JSON sidecar per save slot: UserData/TLDOverhaul/saves/&lt;slot&gt;.json, shaped
    /// { "version": 1, "systems": { "&lt;key&gt;": {...}, ... } }. Systems register an <see cref="ISaveSection"/>;
    /// they never touch files themselves.
    /// </summary>
    public static class SaveManager
    {
        private const int FormatVersion = 1;
        private static readonly List<ISaveSection> Sections = new List<ISaveSection>();
        public static string CurrentSlot { get; private set; }

        /// <summary>Raised after a sidecar was read (or state was reset for a fresh game).</summary>
        public static event Action StateLoaded;

        public static void Register(ISaveSection s)
        {
            if (!Sections.Contains(s)) Sections.Add(s);
        }

        private static string Dir
        {
            get
            {
                var d = Path.Combine(MelonEnvironment.UserDataDirectory, "TLDOverhaul", "saves");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string PathFor(string slot)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) slot = slot.Replace(c, '_');
            return Path.Combine(Dir, slot + ".json");
        }

        public static void ResetAll()
        {
            foreach (var s in Sections)
            {
                try { s.Reset(); } catch (Exception e) { MelonLogger.Error($"[save] reset {s.Key}: {e.Message}"); }
            }
        }

        public static void Write(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return;
            try
            {
                var systems = new JObject();
                foreach (var s in Sections)
                {
                    try { systems[s.Key] = s.Save() ?? new JObject(); }
                    catch (Exception e) { MelonLogger.Error($"[save] {s.Key} failed to serialize: {e.Message}"); }
                }
                var root = new JObject { ["version"] = FormatVersion, ["systems"] = systems };
                var path = PathFor(slot);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.None));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                CurrentSlot = slot;
            }
            catch (Exception e) { MelonLogger.Error("[save] write failed: " + e.Message); }
        }

        /// <summary>Loads the sidecar for a slot. A missing sidecar (vanilla save, new game) just resets everything.</summary>
        public static void Read(string slot)
        {
            ResetAll();
            GameUtil.ResetAllClocks();   // game-hour deltas must restart from the loaded save's clock
            try { ReadInner(slot); }
            finally { try { StateLoaded?.Invoke(); } catch (Exception e) { MelonLogger.Error("[save] StateLoaded handler: " + e.Message); } }
        }

        private static void ReadInner(string slot)
        {
            CurrentSlot = slot;
            if (string.IsNullOrEmpty(slot)) return;
            var path = PathFor(slot);
            if (!File.Exists(path))
            {
                MelonLogger.Msg($"[save] no overhaul sidecar for '{slot}' - starting systems fresh.");
                return;
            }
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var systems = root["systems"] as JObject;
                if (systems == null) return;
                foreach (var s in Sections)
                {
                    var tok = systems[s.Key];
                    if (tok == null) continue;
                    try { s.Load(tok); }
                    catch (Exception e) { MelonLogger.Error($"[save] {s.Key} failed to load (state reset): {e.Message}"); s.Reset(); }
                }
                MelonLogger.Msg($"[save] loaded overhaul sidecar for '{slot}'.");
            }
            catch (Exception e) { MelonLogger.Error("[save] read failed: " + e.Message); }
        }

        public static void Delete(string slot)
        {
            try { var p = PathFor(slot); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public static void Copy(string from, string to)
        {
            try
            {
                var a = PathFor(from);
                if (File.Exists(a)) File.Copy(a, PathFor(to), true);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ hooks into TLD's save pipeline

    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.SaveGame))]
    internal static class SaveGame_Postfix
    {
        private const string Id = "Core.SaveGame_Postfix";
        private static void Postfix(string name)
        {
            PatchLog.Fire(Id);
            try { SaveManager.Write(name); } catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.RestoreGame))]
    internal static class RestoreGame_Prefix
    {
        private const string Id = "Core.RestoreGame_Prefix";
        private static void Prefix(string name)
        {
            PatchLog.Fire(Id);
            try { SaveManager.Read(name); } catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.DeleteSaveFiles))]
    internal static class DeleteSaveFiles_Postfix
    {
        private const string Id = "Core.DeleteSaveFiles_Postfix";
        private static void Postfix(string name)
        {
            PatchLog.Fire(Id);
            try { SaveManager.Delete(name); } catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }

    [HarmonyPatch(typeof(SaveGameSlots), nameof(SaveGameSlots.CopyData))]
    internal static class CopyData_Postfix
    {
        private const string Id = "Core.CopyData_Postfix";
        private static void Postfix(string sourceSlotname, string destSlotname)
        {
            PatchLog.Fire(Id);
            try { SaveManager.Copy(sourceSlotname, destSlotname); } catch (Exception e) { PatchLog.Error(Id, e); }
        }
    }
}
