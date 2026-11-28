namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // An estimate of how long the deployment still takes, from the reference enrollment of the customer or from
    // the enrollments of the last 7 days. Null when there is too little to go on.
    public sealed class TimeLeftEstimate
    {
        public TimeSpan Left;
        public bool Over;
        // Where the estimate comes from, in words, for the tooltip.
        public string Basis;

        public string Text
        {
            get { return Over ? "longer than usual" : TimeLeft.Words(Left); }
        }
    }

    public static class TimeLeft
    {
        // At least this many finished enrollments of the same profile before the last 7 days are used.
        public const int MinRuns = 3;

        public static TimeLeftEstimate Estimate(Session s, DateTime nowUtc, TimeSpan notCounted, Reference r, IEnumerable<FleetRun> recent)
        {
            if (s == null || s.Items.Count == 0 || !StillRuns(s)) return null;
            DateTime? first = s.FirstUtc;
            if (!first.HasValue) return null;
            TimeSpan elapsed = nowUtc - first.Value - notCounted;
            if (elapsed < TimeSpan.FromMinutes(1)) return null;
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            string basis = null;
            double total = ReferenceSeconds(r, profile);
            if (total > 0) basis = "From the reference enrollment of " + r.Computer + ", which took " + Txt.DurationShort(TimeSpan.FromSeconds(total)) + ".";
            else
            {
                List<double> times = new List<double>();
                if (recent != null)
                    foreach (FleetRun run in recent)
                    {
                        if (run == null || !run.Finished || run.Failed > 0 || run.Seconds < 60) continue;
                        if (profile != null && !string.Equals(profile, run.Profile, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(run.Computer, s.Computer, StringComparison.OrdinalIgnoreCase) && run.StartUtc == first) continue;
                        times.Add(run.Seconds);
                    }
                if (times.Count < MinRuns) return null;
                times.Sort();
                total = times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
                basis = "From the middle time of " + times.Count + " enrollments" + (profile != null ? " of " + profile : "") + " that finished in the last 7 days: " + Txt.DurationShort(TimeSpan.FromSeconds(total)) + ".";
            }
            TimeLeftEstimate e = new TimeLeftEstimate();
            e.Basis = basis;
            double left = total - elapsed.TotalSeconds;
            if (left <= 0) e.Over = true;
            else e.Left = TimeSpan.FromSeconds(left);
            return e;
        }

        // Something still runs or waits, and nothing failed with nothing left running.
        public static bool StillRuns(Session s)
        {
            if (s.CountActive > 0) return true;
            if (s.Count(State.Failed) > 0) return false;
            if ((s.GetInfo("Task sequence result") ?? "").Length > 0) return false;
            return s.CountComplete < s.Items.Count;
        }

        // How long the reference took from its first item to its last end, when it fits this enrollment.
        static double ReferenceSeconds(Reference r, string profile)
        {
            if (r == null || r.Items.Count < 3) return 0;
            if (profile != null && r.Profile != null && !string.Equals(profile, r.Profile, StringComparison.OrdinalIgnoreCase)) return 0;
            double end = 0;
            foreach (ReferenceItem x in r.Items)
            {
                // As in Statistics: restarts, policies and profiles say nothing about time, and items that never ended have no end.
                if (!x.Offset.HasValue || x.Phase == Phases.Restarts || x.Phase == Phases.Policies || x.Kind == Kinds.Policy || x.Kind == Kinds.Profile) continue;
                State st = States.Parse(x.State);
                if (st != State.Succeeded && st != State.Failed && st != State.WaitingReboot) continue;
                end = Math.Max(end, x.Offset.Value + (x.Seconds ?? 0));
            }
            return end >= 60 ? end : 0;
        }

        // "about 1 min left", "about 7 min left", "about 25 min left", "about 1 h 10 min left".
        public static string Words(TimeSpan left)
        {
            int min = (int)Math.Ceiling(left.TotalMinutes);
            if (min <= 1) return "about 1 min left";
            if (min > 10) min = (int)(Math.Round(min / 5.0) * 5);
            if (min < 60) return "about " + min.ToString(CultureInfo.InvariantCulture) + " min left";
            int h = min / 60, m = min % 60;
            return "about " + h.ToString(CultureInfo.InvariantCulture) + " h" + (m > 0 ? " " + m.ToString(CultureInfo.InvariantCulture) + " min" : "") + " left";
        }
    }

    // Masking for every text TrackVice writes out for other people: reports, CSV files, tickets and bug reports.
    // Secrets.Mask, and also customer codes, which hold the upload link of a customer and show up in the command line
    // TrackVice.exe /agent-install /customer "tv1:..." of a task sequence step or a script.
    public static class ShareMask
    {
        static readonly System.Text.RegularExpressions.Regex RxCode = new System.Text.RegularExpressions.Regex(
            Secrets.CodePattern, System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        public static string Text(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return s;
            string r = Secrets.Mask(s);
            // Secrets.Mask hides codes too, with the same pattern; this second pass stays for safety.
            try
            {
                if (r.IndexOf("tv1:", StringComparison.OrdinalIgnoreCase) >= 0) r = RxCode.Replace(r, "${n}" + Secrets.Hidden);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return Secrets.Unchecked; }
            return r;
        }
    }

    // A short plain summary of a device to paste into a support ticket. Secrets are masked.
    public static class Ticket
    {
        public const int MaxFailed = 5;

        public static string Text(Session s, DateTime nowUtc, TimeSpan notCounted, string timeLeft, string note)
        {
            StringBuilder b = new StringBuilder();
            string hw = null;
            foreach (Pair p in DeviceFacts.Card(s)) if (p.Key == "Maker and model") hw = p.Value;
            string serial = s.GetInfo(DeviceFacts.Serial);
            Line(b, "Device", s.Computer + (serial != null ? ", serial " + serial : "") + (hw != null ? ", " + hw : ""));
            Line(b, "User", Who.Label(s));
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            Line(b, "Deployment", s.Items.Count > 0 ? s.Kind + (profile != null ? ", " + profile : "") : profile);
            State st;
            string text;
            RecentStore.StateOf(s, out st, out text);
            Line(b, "State", (st == State.Unknown ? "" : States.Label(st) + ", ") + text);
            int n = 0, failed = 0;
            foreach (Item i in s.Items)
            {
                if (i.State != State.Failed) continue;
                failed++;
                if (n++ >= MaxFailed) continue;
                Line(b, "Failed", i.DisplayName + (i.ExitCode.HasValue ? ": " + Errors.Describe(i.ExitCode.Value) : ""));
            }
            if (failed > MaxFailed) Line(b, "Failed", (failed - MaxFailed) + " more");
            string ts = s.GetInfo("Task sequence result");
            if (ts != null && ts.StartsWith("Failed", StringComparison.Ordinal) && failed == 0) Line(b, "Failed", "Task sequence: " + ts.Substring(6).TrimStart(':', ' '));
            List<string> now = new List<string>();
            foreach (Item i in s.Items) if (States.IsActive(i.State) && i.Key != SccmFeed.RunKey) now.Add(i.DisplayName + " (" + States.Label(i.State).ToLowerInvariant() + ")");
            if (now.Count > 3) { int more = now.Count - 3; now.RemoveRange(3, more); now.Add(more + " more"); }
            Line(b, "Waits for", now.Count > 0 ? string.Join(", ", now.ToArray()) : null);
            DateTime? first = s.FirstUtc;
            if (first.HasValue)
            {
                bool running = s.CountActive > 0 || (s.Count(State.Failed) == 0 && s.CountComplete < s.Items.Count);
                DateTime end = running ? nowUtc : (LastEnd(s) ?? nowUtc);
                Line(b, "Started", Txt.Local(first.Value) + ", " + (running ? "running for " : "took ") + Txt.DurationShort(end - first.Value - notCounted));
            }
            Line(b, "Time left", timeLeft);
            Line(b, DeviceFacts.IntuneId, s.GetInfo(DeviceFacts.IntuneId));
            Line(b, DeviceFacts.EntraId, s.GetInfo(DeviceFacts.EntraId));
            Line(b, "Note", note);
            return b.ToString().TrimEnd();
        }

        static DateTime? LastEnd(Session s)
        {
            DateTime? last = null;
            foreach (Item i in s.Items)
            {
                DateTime t = i.EndUtc ?? i.LastUtc;
                if (t.Year > 2000 && (!last.HasValue || t > last.Value)) last = t;
            }
            return last;
        }

        static void Line(StringBuilder b, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            // Masked before it is cut, so a cut cannot leave the start of a secret behind without the name that marks it.
            b.Append(label).Append(": ").Append(Txt.Short(ShareMask.Text(value), 300)).Append("\r\n");
        }
    }

    // The device's page in the Intune admin center. Entra ID's device page needs the object ID of the device, which the
    // device itself does not know, so there is no link to it; the Entra device ID is shown to search for instead.
    public static class PortalLinks
    {
        public const string IntuneDevice = "https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/";

        public static string Intune(string intuneDeviceId)
        {
            Guid g;
            if (string.IsNullOrEmpty(intuneDeviceId) || !Guid.TryParse(intuneDeviceId.Trim().Trim('{', '}'), out g) || g == Guid.Empty) return null;
            return IntuneDevice + g.ToString("D");
        }
    }

    // Short notes per device, kept for this Windows user next to the recent devices in %LOCALAPPDATA%\TrackVice\notes.txt.
    // A note belongs to the device name, so it shows wherever that device does. With no path they are kept in memory.
    public sealed class DeviceNotes
    {
        public const int MaxLength = 200;
        const string Header = "#TrackVice device notes 1";
        readonly string path;
        readonly object gate = new object();
        Dictionary<string, string> notes;

        // The notes the window uses, so lists can show them.
        public static DeviceNotes Current;

        public DeviceNotes(string path)
        {
            this.path = path;
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "notes.txt"); }
        }

        // The note of a device in the notes the window uses, or "".
        public static string Of(string device)
        {
            DeviceNotes n = Current;
            return n == null ? "" : n.Get(device);
        }

        public string Get(string device)
        {
            if (string.IsNullOrEmpty(device)) return "";
            lock (gate)
            {
                Ensure();
                string v;
                return notes.TryGetValue(device.Trim(), out v) ? v : "";
            }
        }

        public int Count
        {
            get { lock (gate) { Ensure(); return notes.Count; } }
        }

        // An empty note removes it.
        public void Set(string device, string note)
        {
            if (string.IsNullOrEmpty(device) || device.Trim().Length == 0) return;
            string d = Clean(device);
            string n = Clean(note);
            lock (gate)
            {
                Ensure();
                if (n.Length == 0) notes.Remove(d);
                else notes[d] = n;
                Save();
            }
        }

        // One line, without tabs, at most MaxLength characters, with secrets masked.
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\r\n", " ").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            t = ShareMask.Text(t);
            return t.Length <= MaxLength ? t : t.Substring(0, MaxLength).TrimEnd();
        }

        void Ensure()
        {
            if (notes != null) return;
            notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (path == null) return;
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    string d = Clean(p[0]), n = Clean(p[1]);
                    if (d.Length > 0 && n.Length > 0) notes[d] = n;
                }
            }
            catch (Exception) { }
        }

        void Save()
        {
            if (path == null) return;
            try
            {
                StringBuilder b = new StringBuilder(Header).Append("\r\n");
                List<string> keys = new List<string>(notes.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string k in keys) b.Append(k).Append('\t').Append(notes[k]).Append("\r\n");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, b.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception) { }
        }
    }
}
