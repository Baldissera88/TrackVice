namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    // A tiny tween helper for short UI transitions. One shared 15 ms timer drives every running animation on the UI thread.
    //
    //   Motion.Animate(shown, target, 180, Motion.EaseOut, delegate (float v) { shown = v; Invalidate(); }, this);
    //
    // - onStep gets the eased value on every tick; the last call always has v == to.
    // - With Windows "show animations" off, with ms <= 0 or with from == to, onStep is called once with to, at once.
    // - A key (any object, compared with Equals) cancels the run still going for the same key, so a value that
    //   changes again mid-way eases on from where it is instead of running two animations at once.
    // - An exception in onStep (such as a disposed control) ends that run quietly.
    // Keep durations short (120 to 200 ms) and ease out, so nothing waits on the motion.
    public static class Motion
    {
        public static readonly Func<float, float> Linear = delegate (float t) { return t; };
        public static readonly Func<float, float> EaseOut = delegate (float t) { float u = 1f - t; return 1f - u * u * u; };
        public static readonly Func<float, float> EaseInOut = delegate (float t) { return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f; };

        sealed class Run
        {
            public object Key;
            public float From, To;
            public int Ms;
            public DateTime Start;
            public Func<float, float> Ease;
            public Action<float> Step;
            public bool Dead;
        }

        static readonly List<Run> runs = new List<Run>();
        static Timer timer;
        static bool noSpi;

        const int SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(int action, int param, ref bool value, int winIni);

        // False when the user turned animations off in Windows (Settings > Accessibility > Visual effects).
        // Callers can also use it to skip decoration such as spinners.
        public static bool Enabled
        {
            get
            {
                if (!noSpi)
                {
                    try
                    {
                        bool on = true;
                        if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0)) return on;
                    }
                    catch (Exception) { noSpi = true; }
                }
                try { return SystemInformation.UIEffectsEnabled; }
                catch (Exception) { return true; }
            }
        }

        // Animates a value from..to over ms milliseconds. key may be null.
        public static void Animate(float from, float to, int ms, Func<float, float> ease, Action<float> onStep, object key)
        {
            if (onStep == null) return;
            if (key != null) Cancel(key);
            if (ms <= 0 || from == to || !Enabled)
            {
                onStep(to);
                return;
            }
            Run r = new Run();
            r.Key = key;
            r.From = from;
            r.To = to;
            r.Ms = ms;
            r.Start = DateTime.UtcNow;
            r.Ease = ease ?? EaseOut;
            r.Step = onStep;
            runs.Add(r);
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = 15;
                timer.Tick += Tick;
            }
            if (!timer.Enabled) timer.Start();
        }

        public static void Animate(float from, float to, int ms, Func<float, float> ease, Action<float> onStep)
        {
            Animate(from, to, ms, ease, onStep, null);
        }

        // Stops the run for the key where it is, without a last step.
        public static void Cancel(object key)
        {
            if (key == null) return;
            foreach (Run r in runs) if (Equals(r.Key, key)) r.Dead = true;
        }

        // True while a run for the key has not reached its end.
        public static bool Running(object key)
        {
            foreach (Run r in runs) if (!r.Dead && Equals(r.Key, key)) return true;
            return false;
        }

        static void Tick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            foreach (Run r in runs.ToArray())
            {
                if (r.Dead) continue;
                float t = (float)((now - r.Start).TotalMilliseconds / r.Ms);
                bool done = t >= 1f;
                float v = done ? r.To : r.From + (r.To - r.From) * r.Ease(Math.Max(0f, t));
                if (done) r.Dead = true;
                try { r.Step(v); }
                catch (Exception) { r.Dead = true; }
            }
            runs.RemoveAll(delegate (Run r) { return r.Dead; });
            if (runs.Count == 0) timer.Stop();
        }
    }
}
