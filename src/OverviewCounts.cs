namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Reads every run of a source, not only the newest of each device. Optional: a source without it counts what it lists.
    public interface IOverviewRuns
    {
        List<RemoteDeployment> Runs(DateTime sinceUtc);
    }

    // The deployments in one period: every run, and the devices they ran on.
    public sealed class DeployWindow
    {
        public string Id, Label;
        public int Runs, Devices;
        public readonly List<OverviewDevice> Rows = new List<OverviewDevice>();

        // "48 deployments on 41 devices".
        public string Summary
        {
            get { return Runs + (Runs == 1 ? " deployment on " : " deployments on ") + Devices + (Devices == 1 ? " device" : " devices"); }
        }
    }

    // A device with more than one run, newest run first.
    public sealed class RepeatDevice
    {
        public string Customer, Device;
        public int Times;
        public readonly List<OverviewDevice> Runs = new List<OverviewDevice>();

        public OverviewDevice Last
        {
            get { return Runs[0]; }
        }
    }

    public sealed class DeployCounts
    {
        public const string Today = "today", Week = "week", Month = "month", All = "all";
        public readonly List<DeployWindow> Windows = new List<DeployWindow>();
        public readonly List<RepeatDevice> Repeats = new List<RepeatDevice>();
        // How many devices were deployed more than once, whatever the list shows.
        public int RepeatDevices;

        public DeployWindow Find(string id)
        {
            foreach (DeployWindow w in Windows) if (w.Id == id) return w;
            return null;
        }
    }

    public static class OverviewCounts
    {
        public const int TopRepeats = 5;
        // SCCM keeps a row per step of every run, so it is read back this far only.
        public static readonly TimeSpan SccmHistory = TimeSpan.FromDays(365);

        // When a run began, or its last sign of life when the start is not known.
        public static DateTime StartOf(OverviewDevice o)
        {
            return o.D.StartUtc ?? o.D.LastUtc;
        }

        public static string DeviceKey(OverviewDevice o)
        {
            string name = string.IsNullOrEmpty(o.D.Device) ? o.D.Key : o.D.Device;
            return (o.Customer ?? "").ToLowerInvariant() + "|" + (name ?? "").ToLowerInvariant();
        }

        // Counts the runs of the chosen customer and side. todayStartUtc is the start of today where the user is.
        // Runs of the snapshot are used; for a snapshot made without them, its devices.
        public static DeployCounts Build(OverviewSnapshot snap, OverviewFilter f, DateTime nowUtc, DateTime todayStartUtc)
        {
            List<OverviewDevice> runs = new List<OverviewDevice>();
            if (snap != null)
            {
                IEnumerable<OverviewDevice> all = snap.Runs.Count > 0 ? snap.Runs : snap.Devices;
                foreach (OverviewDevice o in all) if (f == null || f.Shows(o)) runs.Add(o);
            }
            runs.Sort(delegate (OverviewDevice a, OverviewDevice b) { return StartOf(b).CompareTo(StartOf(a)); });

            DeployCounts c = new DeployCounts();
            DateTime[] from = { todayStartUtc, nowUtc.AddDays(-7), nowUtc.AddDays(-30), DateTime.MinValue };
            string[] ids = { DeployCounts.Today, DeployCounts.Week, DeployCounts.Month, DeployCounts.All };
            string[] labels = { "Today", "Last 7 days", "Last 30 days", "All" };
            for (int k = 0; k < ids.Length; k++)
            {
                DeployWindow w = new DeployWindow();
                w.Id = ids[k];
                w.Label = labels[k];
                HashSet<string> devices = new HashSet<string>();
                foreach (OverviewDevice o in runs)
                {
                    if (StartOf(o) < from[k]) continue;
                    w.Rows.Add(o);
                    devices.Add(DeviceKey(o));
                }
                w.Runs = w.Rows.Count;
                w.Devices = devices.Count;
                c.Windows.Add(w);
            }

            Dictionary<string, RepeatDevice> by = new Dictionary<string, RepeatDevice>();
            foreach (OverviewDevice o in runs)
            {
                RepeatDevice r;
                string key = DeviceKey(o);
                if (!by.TryGetValue(key, out r))
                {
                    r = new RepeatDevice();
                    r.Customer = o.Customer ?? "";
                    r.Device = o.D.Device ?? "";
                    by[key] = r;
                }
                r.Runs.Add(o);
            }
            foreach (RepeatDevice r in by.Values)
            {
                r.Times = r.Runs.Count;
                if (r.Times > 1) c.Repeats.Add(r);
            }
            c.RepeatDevices = c.Repeats.Count;
            c.Repeats.Sort(delegate (RepeatDevice a, RepeatDevice b)
            {
                int n = b.Times.CompareTo(a.Times);
                if (n != 0) return n;
                n = StartOf(b.Last).CompareTo(StartOf(a.Last));
                return n != 0 ? n : string.Compare(a.Device, b.Device, StringComparison.OrdinalIgnoreCase);
            });
            return c;
        }

        public static string Times(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " times";
        }
    }
}
