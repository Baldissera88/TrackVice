namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Made-up Intune and SCCM data for the demo and the tests. It answers the same calls as Microsoft Graph and the
    // SMS Provider, and goes through the same code, so the remote views can be tried without a tenant or a site.
    // The running deployments move on while the demo is open.
    public sealed class DemoGraph : IGraph
    {
        readonly DateTime t0;
        readonly DateTime opened = DateTime.UtcNow;
        public double Speed = 10;

        public DemoGraph(DateTime nowUtc)
        {
            t0 = nowUtc;
        }

        // Minutes into the demo: starts at 0 and moves at Speed while it runs.
        double Elapsed
        {
            get { return (DateTime.UtcNow - opened).TotalMinutes * Speed; }
        }

        static string Iso(DateTime t)
        {
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        static string Dur(TimeSpan t)
        {
            return System.Xml.XmlConvert.ToString(t);
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        Dictionary<string, object> Event(string id, string device, string serial, string user, double startMinAgo, string state, string dev, double devMin, string acc, double accMin, string failure)
        {
            DateTime start = t0.AddMinutes(-startMinAgo);
            DateTime? end = state == "inProgress" ? (DateTime?)null : start.AddMinutes(devMin + accMin);
            return D("id", id, "managedDeviceName", device, "deviceSerialNumber", serial, "userPrincipalName", user,
                "windowsAutopilotDeploymentProfileDisplayName", "Contoso standard user-driven",
                "windows10EnrollmentCompletionPageConfigurationDisplayName", "Contoso ESP - block until required apps",
                "enrollmentType", "azureADJoinedWithAutopilotProfile", "osVersion", "10.0.26100.7171",
                "deploymentState", state, "deviceSetupStatus", dev, "accountSetupStatus", acc,
                "deviceSetupDuration", devMin > 0 ? Dur(TimeSpan.FromMinutes(devMin)) : "PT0S",
                "accountSetupDuration", accMin > 0 ? Dur(TimeSpan.FromMinutes(accMin)) : "PT0S",
                "deploymentStartDateTime", Iso(start), "deploymentEndDateTime", end.HasValue ? Iso(end.Value) : "0001-01-01T00:00:00Z",
                "eventDateTime", Iso(end ?? t0.AddMinutes(-1)), "deviceRegisteredDateTime", Iso(start.AddDays(-12)),
                "enrollmentFailureDetails", failure ?? "");
        }

        List<Dictionary<string, object>> Events()
        {
            double e = Elapsed;
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            // The one running now: device setup is done, account setup runs and finishes while the demo plays.
            bool accDone = e > 9;
            l.Add(Event("a1f0c3e2-0001-4b7e-9d2a-000000000421", "CONTOSO-LT0421", "5CG4321ABC", "mads.jensen@contoso.com", 31 - e,
                accDone ? "success" : "inProgress", "success", 19, accDone ? "success" : "inProgress", accDone ? 21 : 0, null));
            l.Add(Event("a1f0c3e2-0002-4b7e-9d2a-000000000422", "CONTOSO-LT0422", "5CG4321ABD", "sofie.lund@contoso.com", 7 - e,
                "inProgress", e > 14 ? "success" : "inProgress", e > 14 ? 21 : 0, e > 14 ? "inProgress" : "notAttempted", 0, null));
            l.Add(Event("a1f0c3e2-0003-4b7e-9d2a-000000000417", "CONTOSO-LT0417", "5CG4321AB1", "anna.hansen@contoso.com", 95, "successWithTimeout", "success", 24, "successWithTimeout", 60, null));
            l.Add(Event("a1f0c3e2-0004-4b7e-9d2a-000000000399", "CONTOSO-LT0399", "5CG4321AA7", "peter.nielsen@contoso.com", 180, "failure", "failure", 41, "notAttempted", 0,
                "Device setup failed: the Win32 app Contoso VPN Client 5.2 returned error 0x80070643 (fatal error during installation)."));
            l.Add(Event("a1f0c3e2-0005-4b7e-9d2a-000000000388", "CONTOSO-LT0388", "5CG4321AA2", "lars.berg@contoso.com", 26 * 60, "success", "success", 18, "success", 14, null));
            l.Add(Event("a1f0c3e2-0006-4b7e-9d2a-000000000371", "CONTOSO-LT0371", "5CG4321A99", "maria.holm@contoso.com", 50 * 60, "success", "success", 22, "success", 11, null));
            l.Add(Event("a1f0c3e2-0007-4b7e-9d2a-000000000366", "CONTOSO-LT0366", "5CG4321A90", "jonas.krogh@contoso.com", 75 * 60, "successOnRetry", "success", 35, "success", 12, null));
            return l;
        }

        List<Dictionary<string, object>> Details(string id)
        {
            double e = Elapsed;
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            Dictionary<string, object> ev = null;
            foreach (Dictionary<string, object> x in Events()) if ((string)x["id"] == id) ev = x;
            if (ev == null) return l;
            DateTime start = IntuneCatalog.Time(ev, "deploymentStartDateTime") ?? t0;
            Action<string, string, string, string, double, long, bool> add = delegate (string pid, string name, string type, string status, double min, long err, bool tracked)
            {
                l.Add(D("id", pid, "displayName", name, "policyType", type, "complianceStatus", status, "trackedOnEnrollmentStatus", tracked,
                    "lastReportedDateTime", Iso(start.AddMinutes(min)), "errorCode", err));
            };
            if (id.EndsWith("0421"))
            {
                add("c0000001-0000-4000-8000-000000000001", "Contoso security baseline", "configurationPolicy", "compliant", 3, 0, true);
                add("c0000001-0000-4000-8000-000000000002", "Wi-Fi Contoso Corp", "configurationPolicy", "compliant", 4, 0, true);
                add("c0000001-0000-4000-8000-000000000003", "Contoso device certificate", "configurationPolicy", "compliant", 5, 0, true);
                add("3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01", "Microsoft 365 Apps for enterprise", "application", "installed", 17, 0, true);
                add("a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02", "Contoso VPN Client 5.2", "application", "installed", 18, 0, true);
                add("e7b1d4f8-3c6a-4e2d-a9b5-8f0c1d3e6a07", "Company Portal", "application", "installed", 19, 0, true);
                add("9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04", "Microsoft Teams", "application", e > 4 ? "installed" : "notInstalled", e > 4 ? 26 : 20, 0, true);
                add("4d7f1a3c-8e2b-4c6d-9a5f-0b3e7d1c5a05", "Adobe Acrobat Reader", "application", e > 9 ? "installed" : "notInstalled", e > 9 ? 40 : 20, 0, true);
                add("c5e8a2d6-1f4b-4a9c-8d3e-6b0f2a7c4e06", "Contoso Timesheet", "application", "notInstalled", 20, 0, false);
                add("c0000001-0000-4000-8000-000000000009", "Edge settings", "configurationPolicy", "compliant", 21, 0, false);
            }
            else if (id.EndsWith("0399"))
            {
                add("c0000001-0000-4000-8000-000000000001", "Contoso security baseline", "configurationPolicy", "compliant", 3, 0, true);
                add("3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01", "Microsoft 365 Apps for enterprise", "application", "installed", 22, 0, true);
                add("a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02", "Contoso VPN Client 5.2", "application", "error", 40, unchecked((int)0x80070643), true);
                add("e7b1d4f8-3c6a-4e2d-a9b5-8f0c1d3e6a07", "Company Portal", "application", "notInstalled", 40, 0, true);
            }
            else
            {
                add("c0000001-0000-4000-8000-000000000001", "Contoso security baseline", "configurationPolicy", "compliant", 3, 0, true);
                add("3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01", "Microsoft 365 Apps for enterprise", "application", "installed", 18, 0, true);
                add("9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04", "Microsoft Teams", "application", "installed", 28, 0, true);
                if (id.EndsWith("0417")) add("4d7f1a3c-8e2b-4c6d-9a5f-0b3e7d1c5a05", "Adobe Acrobat Reader", "application", "notInstalled", 84, 0, true);
            }
            return l;
        }

        public Dictionary<string, object> Get(string url)
        {
            string events = IntuneCatalog.EventsUrl;
            if (url == events)
            {
                List<object> v = new List<object>();
                foreach (Dictionary<string, object> x in Events()) v.Add(x);
                return D("value", v);
            }
            string rest = url.Substring(events.Length + 1);
            int slash = rest.IndexOf('/');
            string id = Uri.UnescapeDataString(slash < 0 ? rest : rest.Substring(0, slash));
            if (slash < 0)
            {
                foreach (Dictionary<string, object> x in Events()) if ((string)x["id"] == id) return x;
                throw new GraphException("Not found", 404);
            }
            List<object> d = new List<object>();
            foreach (Dictionary<string, object> x in Details(id)) d.Add(x);
            return D("value", d);
        }
    }

    public sealed class DemoSccm : ISccmQuery
    {
        readonly DateTime t0;
        readonly DateTime opened = DateTime.UtcNow;
        public double Speed = 10;
        readonly List<Dictionary<string, object>> all = new List<Dictionary<string, object>>();

        public DemoSccm(DateTime nowUtc)
        {
            t0 = nowUtc;
            Run(16777301, "P0120006", "P0100231", 23, true, false);
            Run(16777288, "P0120006", "P0100231", 80, true, false);
            Run(16777290, "P0120006", "P0100231", 140, true, true);
            Run(16777245, "P0120006", "P0100231", 26 * 60, true, false);
        }

        static readonly string[][] Steps =
        {
            new string[] { "Partition Disk 0 - UEFI", "Prepare disk", "0.6" },
            new string[] { "Apply Operating System", "Install Windows", "7.8" },
            new string[] { "Apply Windows Settings", "Install Windows", "0.2" },
            new string[] { "Apply Device Drivers - Dell Latitude 7450", "Install Windows", "3.4" },
            new string[] { "Setup Windows and Configuration Manager", "Install Windows", "8.1" },
            new string[] { "Install Microsoft 365 Apps", "Applications", "12.1" },
            new string[] { "Install Language Pack da-DK", "Applications", "0.8" },
            new string[] { "Install Language Pack fr-FR", "Applications", "0" },
            new string[] { "Install Software Updates", "Updates", "17.2" },
            new string[] { "Restart Computer", "Updates", "3.4" },
            new string[] { "Install Applications", "Applications", "24.8" },
        };

        void Row(long res, string adv, string pkg, DateTime t, long msg, string name, string action, int step, string group, long exit, string output)
        {
            Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            r["ResourceID"] = res;
            r["AdvertisementID"] = adv;
            r["PackageID"] = pkg;
            r["ExecutionTime"] = t;
            r["LastStatusMsgID"] = msg;
            r["LastStatusMsgName"] = name;
            r["ActionName"] = action;
            r["Step"] = (long)step;
            r["GroupName"] = group;
            r["ExitCode"] = exit;
            r["ActionOutput"] = output;
            all.Add(r);
        }

        void Run(long res, string adv, string pkg, double startMinAgo, bool finish, bool fail)
        {
            DateTime t = t0.AddMinutes(-startMinAgo);
            Row(res, adv, pkg, t, 11140, "The task sequence execution engine started execution of a task sequence.", "", 0, "", 0, null);
            for (int i = 0; i < Steps.Length; i++)
            {
                double min = double.Parse(Steps[i][2], CultureInfo.InvariantCulture);
                if (min == 0)
                {
                    Row(res, adv, pkg, t, 11130, "The task sequence execution engine skipped the action because the condition was evaluated to be false.", Steps[i][0], i + 1, Steps[i][1], 0, null);
                    continue;
                }
                t = t.AddMinutes(min);
                if (fail && i == Steps.Length - 1)
                {
                    Row(res, adv, pkg, t, 11135, "The task sequence execution engine failed executing an action.", Steps[i][0], i + 1, Steps[i][1], unchecked((uint)0x87D00324),
                        "Install Policy for ScopeId_7E3A/Application_3d4e failed: the application was not detected after installation completed (0x87D00324)");
                    Row(res, adv, pkg, t.AddSeconds(2), 11171, "The task sequence execution engine failed execution of a task sequence.", "", 0, "", unchecked((uint)0x87D00324), null);
                    return;
                }
                Row(res, adv, pkg, t, 11134, "The task sequence execution engine successfully completed an action.", Steps[i][0], i + 1, Steps[i][1], 0, null);
                if (Steps[i][0] == "Restart Computer") Row(res, adv, pkg, t.AddSeconds(5), 11144, "The task sequence execution engine performed a reboot.", "", 0, "", 0, null);
            }
            if (finish) Row(res, adv, pkg, t.AddSeconds(4), 11143, "The task sequence execution engine successfully completed the task sequence.", "", 0, "", 0, null);
        }

        // Rows up to the demo's clock, so the running task sequence moves on while the demo is open.
        DateTime Clock
        {
            get { return t0.AddMinutes((DateTime.UtcNow - opened).TotalMinutes * Speed); }
        }

        public List<Dictionary<string, object>> Query(string wql)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            if (wql.Contains("FROM SMS_R_System"))
            {
                string[][] names = { new string[] { "16777301", "CONTOSO-PC121" }, new string[] { "16777288", "CONTOSO-PC118" }, new string[] { "16777290", "CONTOSO-PC117" }, new string[] { "16777245", "CONTOSO-PC109" } };
                foreach (string[] n in names)
                {
                    Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    x["ResourceID"] = long.Parse(n[0], CultureInfo.InvariantCulture);
                    x["Name"] = n[1];
                    r.Add(x);
                }
                return r;
            }
            if (wql.Contains("FROM SMS_TaskSequencePackage"))
            {
                Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                x["PackageID"] = "P0100231";
                x["Name"] = "Windows 11 24H2 Standard";
                r.Add(x);
                return r;
            }
            long only = 0;
            int i = wql.IndexOf("ResourceID = ", StringComparison.Ordinal);
            if (i >= 0)
            {
                string tail = wql.Substring(i + 13);
                int sp = tail.IndexOf(' ');
                only = long.Parse(sp < 0 ? tail : tail.Substring(0, sp), CultureInfo.InvariantCulture);
            }
            DateTime now = Clock;
            foreach (Dictionary<string, object> x in all)
            {
                if (only != 0 && (long)x["ResourceID"] != only) continue;
                if ((DateTime)x["ExecutionTime"] > now) continue;
                r.Add(new Dictionary<string, object>(x, StringComparer.OrdinalIgnoreCase));
            }
            return r;
        }
    }
}
