using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace TLDOverhaul.Core
{
    /// <summary>
    /// Runs a game-time action behind TLD's own generic progress bar (the same one used for eating, reading, etc.),
    /// so overhaul actions (reading a skill volume, cleaning a weapon, setting an anchor) accelerate time, can be
    /// cancelled, and block other input exactly like vanilla actions.
    /// </summary>
    public static class TimedAction
    {
        public static bool Busy { get; private set; }

        public static bool Run(string label, float gameMinutes, Action<bool> onDone, float failureThreshold = 0f)
        {
            if (Busy) return false;
            try
            {
                var panel = InterfaceManager.GetPanel<Panel_GenericProgressBar>();
                if (panel == null) return false;
                float realSeconds = Mathf.Clamp(gameMinutes / 6f, 2f, 14f);
                Busy = true;
                panel.Launch(label, realSeconds, gameMinutes, failureThreshold, false, new Action<bool, bool, float>((success, cancelled, progress) =>
                {
                    Busy = false;
                    try { onDone?.Invoke(success && !cancelled); }
                    catch (Exception e) { MelonLogger.Error("[TimedAction] completion handler threw: " + e); }
                }));
                return true;
            }
            catch (Exception e)
            {
                Busy = false;
                MelonLogger.Warning("[TimedAction] could not launch progress bar: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>Key polling that survives builds where the legacy Input class is unavailable.</summary>
    public static class Keys
    {
        private static bool _broken;

        public static bool Down(KeyCode k)
        {
            if (_broken) return false;
            try { return Input.GetKeyDown(k); }
            catch (Exception e) { _broken = true; MelonLogger.Warning("[input] legacy Input unavailable: " + e.Message); return false; }
        }

        public static bool Held(KeyCode k)
        {
            if (_broken) return false;
            try { return Input.GetKey(k); }
            catch { _broken = true; return false; }
        }

        public static bool Shift { get { return Held(KeyCode.LeftShift) || Held(KeyCode.RightShift); } }
    }
}
