using System;
using System.Collections.Generic;
using TLDOverhaul.Core;
using UnityEngine;

namespace TLDOverhaul.Interactive
{
    /// <summary>
    /// "Skill level changes the difficulty of the interaction, not whether you can attempt it." Every stage receives the
    /// player's skill level (0..1) and derives its timing windows, speeds and how readable the visual cues are from it.
    /// A novice has tiny windows and vague cues; an expert has generous windows and obvious colour reads.
    /// </summary>
    public abstract class Stage
    {
        public string Title = "";
        public string Hint = "";
        public string Result = "";
        public float Score;               // 0..1
        protected float Lvl;
        protected int Tier;
        /// <summary>0 = vague, 1 = faint guide, 2 = exact guide.</summary>
        protected int Clarity { get { return Lvl < 0.25f ? 0 : (Lvl < 0.6f ? 1 : 2); } }

        public void Begin(float lvl, int tier) { Lvl = lvl; Tier = tier; OnBegin(); }
        protected abstract void OnBegin();
        /// <summary>Advance by dt seconds (unscaled). Return true when the stage is finished.</summary>
        public abstract bool Tick(float dt);
        public abstract void Draw(Rect area);

        // ---- shared input
        protected static bool Hit() { return Keys.Down(KeyCode.Space) || Keys.Down(KeyCode.Mouse0); }
        protected static bool Confirm() { return Keys.Down(KeyCode.Return) || Keys.Down(KeyCode.KeypadEnter); }
        protected static float Axis(bool vertical)
        {
            float v = 0f;
            if (vertical) { if (Keys.Held(KeyCode.W) || Keys.Held(KeyCode.UpArrow)) v += 1f; if (Keys.Held(KeyCode.S) || Keys.Held(KeyCode.DownArrow)) v -= 1f; }
            else { if (Keys.Held(KeyCode.D) || Keys.Held(KeyCode.RightArrow)) v += 1f; if (Keys.Held(KeyCode.A) || Keys.Held(KeyCode.LeftArrow)) v -= 1f; }
            return v;
        }
    }

    /// <summary>Tiny IMGUI drawing helpers (solid rectangles only; no assets required).</summary>
    public static class Ui
    {
        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        public static Rect Bar(Rect area, float y, float height) { return new Rect(area.x + 24, area.y + y, area.width - 48, height); }

        public static void Frame(Rect r) { Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0.05f, 0.05f, 0.05f, 0.95f)); Fill(r, new Color(0.18f, 0.18f, 0.2f, 1f)); }

        public static void Band(Rect bar, float center, float half, Color c)
        {
            float x0 = Mathf.Clamp01(center - half), x1 = Mathf.Clamp01(center + half);
            Fill(new Rect(bar.x + bar.width * x0, bar.y, bar.width * (x1 - x0), bar.height), c);
        }

        public static void Marker(Rect bar, float pos, Color c)
        {
            float x = bar.x + bar.width * Mathf.Clamp01(pos);
            Fill(new Rect(x - 3, bar.y - 6, 6, bar.height + 12), c);
        }

        /// <summary>Metal colour from temperature: black -> dull red -> cherry -> orange -> yellow -> white.</summary>
        public static Color HeatColor(float t)
        {
            t = Mathf.Clamp01(t);
            if (t < 0.25f) return Color.Lerp(new Color(0.12f, 0.12f, 0.12f), new Color(0.45f, 0.05f, 0.03f), t / 0.25f);
            if (t < 0.55f) return Color.Lerp(new Color(0.45f, 0.05f, 0.03f), new Color(0.85f, 0.12f, 0.05f), (t - 0.25f) / 0.30f);
            if (t < 0.75f) return Color.Lerp(new Color(0.85f, 0.12f, 0.05f), new Color(1f, 0.55f, 0.08f), (t - 0.55f) / 0.20f);
            if (t < 0.90f) return Color.Lerp(new Color(1f, 0.55f, 0.08f), new Color(1f, 0.92f, 0.45f), (t - 0.75f) / 0.15f);
            return Color.Lerp(new Color(1f, 0.92f, 0.45f), Color.white, (t - 0.90f) / 0.10f);
        }
    }

    // ===================================================================================================== smithing

    /// <summary>Bring the metal to colour. SPACE = bellows (heats), release = cools. ENTER pulls it from the forge.</summary>
    public sealed class HeatStage : Stage
    {
        private float _t, _elapsed, _target;
        private bool _done, _brittle;
        private const float Limit = 30f;

        public HeatStage(float target, string what) { _target = target; Title = "Heat the metal to " + what; }

        protected override void OnBegin()
        {
            _t = 0.08f; _elapsed = 0f; _done = false; _brittle = false;
            Hint = "Hold SPACE to work the bellows. Release to let it cool. ENTER pulls it from the forge.";
        }

        private float Half { get { return Mathf.Lerp(0.045f, 0.14f, Lvl); } }

        public override bool Tick(float dt)
        {
            if (_done) return true;
            _elapsed += dt;
            if (Keys.Held(KeyCode.Space) || Keys.Held(KeyCode.Mouse0)) _t += 0.30f * dt; else _t -= 0.10f * dt;
            _t = Mathf.Clamp01(_t);
            if (Confirm() || _elapsed > Limit)
            {
                float err = Mathf.Abs(_t - _target);
                Score = Mathf.Clamp01(1f - err / (Half * 2.5f));
                _brittle = _t > 0.92f;
                if (_brittle) Score *= 0.4f;
                Result = _brittle ? "Overheated - the steel has gone brittle." : (Score > 0.8f ? "Right on colour." : (_t < _target ? "Pulled too early." : "A little hot."));
                _done = true;
            }
            return _done;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            // the bar IS the metal: its colour is the only temperature readout at low skill
            Ui.Fill(new Rect(bar.x, bar.y, bar.width * _t, bar.height), Ui.HeatColor(_t));
            if (Clarity >= 1) Ui.Band(bar, _target, Half, new Color(1f, 1f, 1f, Clarity == 2 ? 0.30f : 0.12f));
            if (Clarity == 2) Ui.Marker(bar, _target, new Color(1f, 1f, 1f, 0.8f));
            GUI.Label(new Rect(bar.x, bar.y + 44, bar.width, 24), _done ? Result : "Colour: " + (_t < 0.3f ? "black" : _t < 0.5f ? "dull red" : _t < 0.65f ? "cherry red" : _t < 0.8f ? "orange" : _t < 0.92f ? "yellow-white" : "white-hot"));
        }
    }

    /// <summary>Strike the work. A marker sweeps the anvil face; hit SPACE when it crosses the glowing spot.</summary>
    public sealed class SweepStage : Stage
    {
        private readonly int _hits;
        private int _n;
        private float _pos, _dir = 1f, _target, _lastScore, _flash;
        private readonly List<float> _scores = new List<float>();

        public SweepStage(int hits, string what) { _hits = hits; Title = what; }

        protected override void OnBegin()
        {
            _n = 0; _pos = 0.1f; _dir = 1f; _scores.Clear(); NewTarget();
            Hint = "Press SPACE as the marker crosses the glowing spot. Timing and placement both count.";
        }

        private float Speed { get { return Mathf.Lerp(1.35f, 0.8f, Lvl); } }
        private float ZoneHalf { get { return Mathf.Lerp(0.04f, 0.11f, Lvl); } }
        private void NewTarget() { _target = UnityEngine.Random.Range(0.15f, 0.85f); }

        public override bool Tick(float dt)
        {
            _pos += _dir * Speed * dt;
            if (_pos > 1f) { _pos = 1f; _dir = -1f; } else if (_pos < 0f) { _pos = 0f; _dir = 1f; }
            _flash = Mathf.Max(0f, _flash - dt);
            if (Hit())
            {
                float err = Mathf.Abs(_pos - _target);
                _lastScore = Mathf.Clamp01(1f - err / (ZoneHalf * 3f));
                _scores.Add(_lastScore);
                _flash = 0.5f; _n++;
                Result = _lastScore > 0.8f ? "Solid blow." : (_lastScore > 0.4f ? "Glancing." : "Wasted the metal.");
                NewTarget();
            }
            if (_n >= _hits) { float s = 0f; foreach (var x in _scores) s += x; Score = s / Math.Max(1, _scores.Count); return true; }
            return false;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            // spot: faint at low skill (hard to read), exact at high skill
            Ui.Band(bar, _target, ZoneHalf, new Color(1f, 0.6f, 0.15f, Clarity == 0 ? 0.18f : (Clarity == 1 ? 0.4f : 0.7f)));
            Ui.Marker(bar, _pos, Color.white);
            GUI.Label(new Rect(bar.x, bar.y + 44, bar.width, 24), string.Format("Blow {0}/{1}   {2}", Math.Min(_n + 1, _hits), _hits, _flash > 0f ? Result : ""));
        }
    }

    /// <summary>Quench at the right moment. The metal cools on its own; hit SPACE inside the window.</summary>
    public sealed class QuenchStage : Stage
    {
        private float _t, _center;
        private bool _done;
        public QuenchStage() { Title = "Quench"; }

        protected override void OnBegin()
        {
            _t = 1f; _done = false; _center = UnityEngine.Random.Range(0.28f, 0.42f);
            Hint = "The steel cools by itself. Press SPACE to quench when it reaches the right colour.";
        }

        private float Half { get { return Mathf.Lerp(0.05f, 0.16f, Lvl); } }

        public override bool Tick(float dt)
        {
            if (_done) return true;
            _t -= dt / 4.5f;
            if (Hit() || _t <= 0f)
            {
                float err = Mathf.Abs(_t - _center);
                Score = Mathf.Clamp01(1f - err / (Half * 2.2f));
                if (_t > _center + Half * 2f) { Score *= 0.35f; Result = "Quenched too hot - the piece cracks."; }
                else Result = Score > 0.75f ? "Clean quench." : "Quenched a little off.";
                _done = true;
            }
            return _done;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            Ui.Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(_t), bar.height), Ui.HeatColor(_t));
            if (Clarity >= 1) Ui.Band(bar, _center, Half, new Color(0.4f, 0.7f, 1f, Clarity == 2 ? 0.35f : 0.15f));
            if (_done) GUI.Label(new Rect(bar.x, bar.y + 44, bar.width, 24), Result);
        }
    }

    // ===================================================================================================== shared by tanning / tailoring

    /// <summary>Keep a drifting marker inside a band using two keys, for a fixed time. Used for stretching a hide and cutting cloth.</summary>
    public sealed class TrackStage : Stage
    {
        private readonly bool _vertical;
        private readonly float _duration;
        private float _pos = 0.5f, _drift, _elapsed, _inBand;

        public TrackStage(string title, string hint, bool vertical, float duration) { Title = title; Hint = hint; _vertical = vertical; _duration = duration; }

        protected override void OnBegin() { _pos = 0.5f; _elapsed = 0f; _inBand = 0f; _drift = UnityEngine.Random.Range(-1f, 1f) * DriftSpeed; }

        private float Half { get { return Mathf.Lerp(0.08f, 0.17f, Lvl); } }
        private float DriftSpeed { get { return Mathf.Lerp(0.5f, 0.25f, Lvl); } }

        public override bool Tick(float dt)
        {
            _elapsed += dt;
            if (UnityEngine.Random.value < 1.8f * dt) _drift = UnityEngine.Random.Range(-1f, 1f) * DriftSpeed;
            _pos += (_drift + Axis(_vertical) * 0.85f) * dt;
            _pos = Mathf.Clamp01(_pos);
            if (Mathf.Abs(_pos - 0.5f) <= Half) _inBand += dt;
            if (_elapsed >= _duration) { Score = Mathf.Clamp01(_inBand / _duration); Result = Score > 0.8f ? "Even and steady." : (Score > 0.5f ? "A little uneven." : "Badly uneven."); return true; }
            return false;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            Ui.Band(bar, 0.5f, Half, new Color(0.3f, 0.8f, 0.4f, Clarity == 0 ? 0.2f : 0.45f));
            Ui.Marker(bar, _pos, Color.white);
            Ui.Fill(new Rect(bar.x, bar.y + 52, bar.width * Mathf.Clamp01(_elapsed / _duration), 6), new Color(0.8f, 0.8f, 0.8f, 0.8f));
        }
    }

    /// <summary>Press SPACE on a steady beat (scraping strokes, stitches). Consistent rhythm scores best.</summary>
    public sealed class BeatStage : Stage
    {
        private readonly int _beats;
        private readonly float _period;
        private float _t;
        private int _next;
        private readonly List<float> _scores = new List<float>();
        private bool _awaiting;
        private float _flash;

        public BeatStage(string title, string hint, int beats, float period) { Title = title; Hint = hint; _beats = beats; _period = period; }

        protected override void OnBegin() { _t = 0f; _next = 0; _scores.Clear(); _awaiting = true; }

        private float Window { get { return Mathf.Lerp(0.10f, 0.28f, Lvl); } }

        public override bool Tick(float dt)
        {
            _t += dt; _flash = Mathf.Max(0f, _flash - dt);
            float beatTime = (_next + 1) * _period;
            if (Hit() && _next < _beats)
            {
                float err = Mathf.Abs(_t - beatTime);
                float s = Mathf.Clamp01(1f - err / (Window * 2f));
                _scores.Add(s); _next++; _flash = 0.4f;
                Result = s > 0.75f ? "Good." : (s > 0.35f ? "Ragged." : "Slipped!");
            }
            else if (_next < _beats && _t > beatTime + Window * 2f) { _scores.Add(0f); _next++; _flash = 0.4f; Result = "Missed."; }
            if (_next >= _beats) { float sum = 0f; foreach (var x in _scores) sum += x; Score = sum / Math.Max(1, _scores.Count); return true; }
            return false;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            float beatTime = (_next + 1) * _period;
            float phase = Mathf.Clamp01(1f - Mathf.Abs(beatTime - _t) / _period);
            // the pulse grows toward the beat and fades after it
            Ui.Fill(new Rect(bar.x + 6, bar.y + 6, (bar.width - 12) * phase, bar.height - 12), new Color(0.85f, 0.75f, 0.4f, Clarity == 0 ? 0.35f : 0.8f));
            if (Clarity >= 1) Ui.Marker(bar, 1f, new Color(1f, 1f, 1f, 0.7f));
            GUI.Label(new Rect(bar.x, bar.y + 44, bar.width, 24), string.Format("{0}/{1}   {2}", Math.Min(_next + 1, _beats), _beats, _flash > 0f ? Result : ""));
        }
    }

    /// <summary>Let a gauge fill and stop it inside the window (smoking a hide: under-smoked rots, over-smoked is brittle).</summary>
    public sealed class WindowStage : Stage
    {
        private float _v, _center;
        private bool _done;
        private readonly float _seconds;

        public WindowStage(string title, string hint, float center, float seconds) { Title = title; Hint = hint; _center = center; _seconds = seconds; }

        protected override void OnBegin() { _v = 0f; _done = false; }
        private float Half { get { return Mathf.Lerp(0.06f, 0.17f, Lvl); } }

        public override bool Tick(float dt)
        {
            if (_done) return true;
            _v += dt / _seconds;
            if (Hit() || _v >= 1f)
            {
                float err = Mathf.Abs(_v - _center);
                Score = Mathf.Clamp01(1f - err / (Half * 2.4f));
                Result = _v < _center - Half ? "Under-smoked - it will rot." : (_v > _center + Half ? "Over-smoked - brittle." : "Cured well.");
                _done = true;
            }
            return _done;
        }

        public override void Draw(Rect a)
        {
            var bar = Ui.Bar(a, 120, 34);
            Ui.Frame(bar);
            Ui.Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(_v), bar.height), new Color(0.5f, 0.45f, 0.4f, 1f));
            if (Clarity >= 1) Ui.Band(bar, _center, Half, new Color(0.3f, 0.8f, 0.4f, Clarity == 2 ? 0.4f : 0.18f));
            if (_done) GUI.Label(new Rect(bar.x, bar.y + 44, bar.width, 24), Result);
        }
    }
}
