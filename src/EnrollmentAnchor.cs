namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // When this deployment really began, from the things that only a deployment leaves: the Autopilot and ESP steps, the agent
    // of Intune, task sequence steps and the device's own policies. Apps are left out of that, because an app can carry a time
    // from the Windows image or template it was installed on (the registry write time of an Office, MSI or Win32 app key, an
    // install date, an old Intune Management Extension log). Such a time is older than the deployment and must not become its
    // start. Anything that begins more than an hour before the anchor is treated as already there before the deployment: its
    // own event times stay in the details, but it counts from the anchor for the start and for every duration.
    public static class EnrollmentAnchor
    {
        public static readonly TimeSpan Grace = TimeSpan.FromHours(1);
        // Evidence further apart than this is not one deployment: the newest cluster is the deployment.
        public static readonly TimeSpan Gap = TimeSpan.FromDays(3);
        public const string OlderInfo = "Recorded earlier";

        // Items that prove the deployment began. Apps, scripts and restarts do not: their times can come from the image.
        public static bool IsEvidence(string kind)
        {
            return kind == Kinds.Enrollment || kind == Kinds.Esp || kind == Kinds.Agent || kind == Kinds.Step
                || kind == Kinds.Policy || kind == Kinds.Profile || kind == Kinds.Certificate;
        }

        static bool Valid(DateTime t)
        {
            return t.Year > 2000;
        }

        // The earliest time of the newest cluster, or null when there are no times.
        public static DateTime? Cluster(List<DateTime> times)
        {
            if (times == null || times.Count == 0) return null;
            List<DateTime> l = new List<DateTime>(times);
            l.Sort();
            int from = l.Count - 1;
            while (from > 0 && l[from] - l[from - 1] <= Gap) from--;
            return l[from];
        }

        // From observations not applied yet. Null when nothing shows when the deployment began.
        public static DateTime? Of(IEnumerable<Obs> found, DateTime sinceUtc, DateTime nowUtc)
        {
            List<DateTime> evidence = new List<DateTime>(), all = new List<DateTime>();
            foreach (Obs o in found)
            {
                if (!Valid(o.TimeUtc) || o.TimeUtc < sinceUtc || o.TimeUtc > nowUtc.AddMinutes(5)) continue;
                if (o.ItemKind == Kinds.Restart) continue;
                all.Add(o.TimeUtc);
                if (IsEvidence(o.ItemKind)) evidence.Add(o.TimeUtc);
            }
            return Pick(evidence, all);
        }

        public static DateTime? Of(Session s)
        {
            List<DateTime> evidence = new List<DateTime>(), all = new List<DateTime>();
            foreach (Item i in s.Items)
            {
                if (i.Kind == Kinds.Restart || i.PhaseKey == Phases.Restarts) continue;
                DateTime t = i.StartUtc ?? i.FirstUtc;
                if (!Valid(t)) continue;
                all.Add(t);
                if (IsEvidence(i.Kind)) evidence.Add(t);
            }
            return Pick(evidence, all);
        }

        static DateTime? Pick(List<DateTime> evidence, List<DateTime> all)
        {
            if (evidence.Count > 0) return Cluster(evidence);
            // No step, policy or agent to go by: only an older minority far from the rest is taken as left over.
            DateTime? c = Cluster(all);
            if (!c.HasValue) return null;
            int older = 0;
            foreach (DateTime t in all) if (t < c.Value) older++;
            return older * 2 < all.Count ? c : null;
        }

        // Moves every time that is more than an hour before the anchor to where the item really began. Safe to run again.
        public static int Apply(Session s)
        {
            if (s == null || s.Items.Count == 0) return 0;
            DateTime? a = Of(s);
            if (!a.HasValue) return 0;
            DateTime anchor = a.Value, cut = anchor - Grace;
            int n = 0;
            DateTime firstOld = DateTime.MaxValue;
            foreach (Item i in s.Items)
            {
                if (i.Kind == Kinds.Restart || i.PhaseKey == Phases.Restarts) continue;
                bool old = (Valid(i.FirstUtc) && i.FirstUtc < cut) || (i.StartUtc.HasValue && i.StartUtc.Value < cut)
                    || (i.EndUtc.HasValue && i.EndUtc.Value < cut) || (Valid(i.LastUtc) && i.LastUtc < cut);
                if (!old) continue;
                DateTime orig = Valid(i.FirstUtc) ? i.FirstUtc : (i.StartUtc ?? cut);
                if (i.StartUtc.HasValue && i.StartUtc.Value < orig) orig = i.StartUtc.Value;
                // Where it really began: its first event of this deployment, else the anchor.
                DateTime from = anchor;
                foreach (TEvent e in i.Events)
                    if (e.TimeUtc >= cut && (from == anchor || e.TimeUtc < from)) from = e.TimeUtc;
                DateTime? end = i.EndUtc.HasValue && i.EndUtc.Value >= cut ? i.EndUtc : null;
                if (end.HasValue && from > end.Value) from = end.Value;
                if (Valid(i.FirstUtc) && i.FirstUtc < cut) i.FirstUtc = from;
                if (i.StartUtc.HasValue && i.StartUtc.Value < cut) i.StartUtc = from;
                if (i.EndUtc.HasValue && i.EndUtc.Value < cut) i.EndUtc = from;
                if (Valid(i.LastUtc) && i.LastUtc < cut) i.LastUtc = from;
                if (i.LastUtc < i.FirstUtc) i.LastUtc = i.FirstUtc;
                foreach (Attempt at in i.Attempts)
                {
                    if (at.StartUtc < cut) at.StartUtc = from;
                    if (at.EndUtc.HasValue && at.EndUtc.Value < cut) at.EndUtc = from;
                }
                if (i.Get(OlderInfo) == null) i.Set(OlderInfo, Txt.Local(orig) + ", before this deployment began. It does not count towards the start or the durations.");
                if (orig < firstOld) firstOld = orig;
                n++;
            }
            if (n > 0)
            {
                string note = n + (n == 1 ? " item has" : " items have") + " a time from before this deployment began (" + Txt.Local(firstOld)
                    + "), most likely from the Windows image or template. They count from the start of the deployment, " + Txt.Local(anchor) + ".";
                if (!s.Notes.Exists(delegate (string x) { return x.IndexOf("from before this deployment began", StringComparison.Ordinal) >= 0; })) s.Notes.Add(note);
            }
            return n;
        }
    }
}
