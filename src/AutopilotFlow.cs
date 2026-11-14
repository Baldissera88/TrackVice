namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Pre-provisioning, hybrid join and Autopilot device preparation, judged over the whole session after every change.
    public static class AutopilotFlow
    {
        public const string HybridJoin = "Hybrid join";
        public const string PreProvInfo = "Pre-provisioning";
        public const string DevicePrepInfo = "Autopilot device preparation";
        // A silence this long after the technician phase is taken as the time the device was resealed and boxed.
        public static TimeSpan BoxedAfter = TimeSpan.FromMinutes(10);

        // The time between the end of the technician phase and the user's first start. Open (to is null) while the user has not started yet.
        public static bool Boxed(Session s, DateTime nowUtc, out DateTime from, out DateTime? to)
        {
            from = DateTime.MinValue;
            to = null;
            Item pp = s.Find(AutopilotEvents.PreProvKey);
            if (pp == null || pp.State != State.Succeeded) return false;
            DateTime last = pp.StateUtc;
            foreach (TEvent e in s.Events)
            {
                if (e.TimeUtc < pp.StateUtc) continue;
                if (e.TimeUtc - last >= BoxedAfter)
                {
                    from = last;
                    to = e.TimeUtc;
                    return true;
                }
                if (e.TimeUtc > last) last = e.TimeUtc;
            }
            if (nowUtc - last < BoxedAfter) return false;
            from = last;
            return true;
        }

        // How long the device was boxed, to leave out of the running time.
        public static TimeSpan BoxedTime(Session s, DateTime nowUtc)
        {
            DateTime from;
            DateTime? to;
            if (!Boxed(s, nowUtc, out from, out to)) return TimeSpan.Zero;
            DateTime end = to ?? nowUtc;
            return end > from ? end - from : TimeSpan.Zero;
        }

        public static void Apply(Session s, List<Finding> list, DateTime nowUtc)
        {
            PreProvisioning(s, list, nowUtc);
            Hybrid(s, list);
            DevicePreparation(s);
        }

        static void PreProvisioning(Session s, List<Finding> list, DateTime nowUtc)
        {
            Item pp = s.Find(AutopilotEvents.PreProvKey);
            if (pp == null || pp.State != State.Succeeded) return;
            DateTime from;
            DateTime? to;
            bool boxed = Boxed(s, nowUtc, out from, out to);
            if (boxed && to.HasValue) s.SetInfo(PreProvInfo, "Technician phase finished " + Txt.Local(pp.StateUtc) + ", user phase started " + Txt.Local(to.Value));
            else s.SetInfo(PreProvInfo, "Technician phase finished, waiting for the user");
            if (!boxed) return;
            DateTime end = to ?? nowUtc;
            // Nothing can make progress while the device is off, so that time does not count as silence.
            list.RemoveAll(delegate (Finding f)
            {
                if (f.Kind != FindingKinds.Stuck || f.ItemKey == null) return false;
                Item i = s.Find(f.ItemKey);
                if (i == null) return false;
                TimeSpan quiet = nowUtc - i.LastUtc - Overlap(i.LastUtc, nowUtc, from, end);
                return quiet <= Finder.StuckAfter;
            });
        }

        static TimeSpan Overlap(DateTime a1, DateTime a2, DateTime b1, DateTime b2)
        {
            DateTime x = a1 > b1 ? a1 : b1, y = a2 < b2 ? a2 : b2;
            return y > x ? y - x : TimeSpan.Zero;
        }

        static void Hybrid(Session s, List<Finding> list)
        {
            foreach (string key in new string[] { AutopilotEvents.OdjKey, AutopilotEvents.DcKey })
            {
                Item i = s.Find(key);
                if (i == null || i.State != State.Failed) continue;
                list.RemoveAll(delegate (Finding x) { return x.ItemKey == i.Key && x.Kind == FindingKinds.Failed; });
                TEvent last = null;
                foreach (TEvent e in i.Events) if (e.Severity >= 2) last = e;
                Finding f = new Finding();
                f.Kind = HybridJoin;
                f.Severity = 2;
                f.ItemKey = i.Key;
                f.TimeUtc = last != null ? last.TimeUtc : i.StateUtc;
                f.Evidence = last != null && last.Evidence != null ? last.Evidence : i.LastEvidence;
                // learn.microsoft.com/autopilot/troubleshoot-oobe and the hybrid join tutorial for the connector and the connectivity check.
                if (key == AutopilotEvents.OdjKey)
                {
                    f.Title = "Hybrid join: the domain join blob did not arrive or could not be applied";
                    f.Message = (last != null ? last.Text + " " : "") + "The Intune Connector for Active Directory creates the computer account and sends the blob through Intune.";
                    f.Hint = "Check that the connector service runs and that its account can create computer objects in the organizational unit of the domain join profile. Its log is in Applications and Services Logs > Microsoft > Intune > ODJConnectorService.";
                }
                else
                {
                    f.Title = "Hybrid join: no domain controller could be reached";
                    f.Message = (last != null ? last.Text + " " : "") + "Setup waits for a domain controller after the domain join blob is applied.";
                    f.Hint = "The device needs a network path to a domain controller during setup, on the local network or by VPN. The Autopilot profile setting Skip AD connectivity check turns this check off.";
                }
                list.Add(f);
            }
        }

        // Autopilot device preparation does not use the ESP, so ESP items mean the original Autopilot is running.
        // learn.microsoft.com/autopilot/device-preparation/troubleshooting-faq
        static void DevicePreparation(Session s)
        {
            if (s.GetInfo(AutopilotEvents.DppInfo) == null) return;
            foreach (string key in new string[] { Phases.DeviceSetup, Phases.AccountSetup })
            {
                Phase p = s.FindPhase(key);
                if (p != null && p.Items.Count > 0) return;
            }
            s.SetInfo(DevicePrepInfo, "Probably in use. Its setup page keeps its progress in no documented place, so TrackVice shows its events, apps, scripts and policies, but not the page's own progress.");
        }
    }
}
