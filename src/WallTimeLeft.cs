namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // The time left of a device on the wall, learned from the devices on the same wall that finished the same deployment.
    // The wall reads only the list of uploads, so this is the usual time of the others, not an estimate from the items.
    public sealed class WallTimeLeft
    {
        public TimeSpan Left, Usual;
        // Running longer than the devices that finished the same deployment took.
        public bool Over;
        public DateTime FinishUtc;
        public int Runs;

        // "about 20 min left", or "Longer than usual".
        public string Words
        {
            get { return Over ? "Longer than usual" : TimeLeft.Words(Left); }
        }

        // "done ~21:05", or "usually 50 min".
        public string Detail
        {
            get
            {
                if (Over) return "usually " + Txt.DurationShort(Usual);
                return "done ~" + FinishUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        }

        // The usual time, the median of the devices on the wall that finished the same kind and profile without failures. Null when none did.
        public static TimeSpan? UsualTime(IEnumerable<WallTile> tiles, WallTile like, DateTime nowUtc, out int runs)
        {
            runs = 0;
            if (tiles == null || like == null) return null;
            List<double> took = new List<double>();
            foreach (WallTile t in tiles)
            {
                if (t == like || t.State != WallState.Finished || !t.Final) continue;
                if (!Same(t.Kind, like.Kind) || !Same(t.Profile, like.Profile)) continue;
                TimeSpan? d = t.Elapsed(nowUtc);
                if (d.HasValue && d.Value > TimeSpan.Zero) took.Add(d.Value.TotalSeconds);
            }
            runs = took.Count;
            if (took.Count == 0) return null;
            took.Sort();
            int m = took.Count / 2;
            double s = took.Count % 2 == 1 ? took[m] : (took[m - 1] + took[m]) / 2;
            return TimeSpan.FromSeconds(s);
        }

        // Null when the device no longer runs, has not said when it started, or no device like it finished on the wall.
        public static WallTimeLeft For(WallTile t, IEnumerable<WallTile> tiles, DateTime nowUtc)
        {
            if (t == null || t.Final || t.State == WallState.Finished || t.State == WallState.Silent || !t.StartUtc.HasValue) return null;
            int runs;
            TimeSpan? usual = UsualTime(tiles, t, nowUtc, out runs);
            if (!usual.HasValue) return null;
            TimeSpan? el = t.Elapsed(nowUtc);
            if (!el.HasValue) return null;
            WallTimeLeft w = new WallTimeLeft();
            w.Usual = usual.Value;
            w.Runs = runs;
            w.Left = usual.Value - el.Value;
            w.Over = w.Left <= TimeSpan.Zero;
            if (w.Left < DeploymentEstimate.MinLeft) w.Left = DeploymentEstimate.MinLeft;
            w.FinishUtc = nowUtc + w.Left;
            return w;
        }

        static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
