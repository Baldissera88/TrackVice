namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public sealed class ShipVerdict
    {
        public bool Ready;
        public string Headline;
        public readonly List<string> Reasons = new List<string>();
        public int Failed, Running, Waiting, Missing;
    }

    // Whether a device is ready to go to its user after pre-provisioning or imaging.
    public static class ShipCheck
    {
        public const string ReadyText = "Ready to ship";

        static string Items(int n, string one, string many)
        {
            return n == 1 ? "1 item " + one : n + " items " + many;
        }

        // Not ready when anything failed, still runs or has not started, or an item the reference has is missing.
        // A reference is optional; without one only the device itself is judged.
        public static ShipVerdict Judge(Session s, ReferenceResult reference)
        {
            ShipVerdict v = new ShipVerdict();
            Item pp = s.Find(AutopilotEvents.PreProvKey);
            bool technicianDone = pp != null && pp.State == State.Succeeded;
            foreach (Item i in s.Items)
            {
                if (i.State == State.Failed) v.Failed++;
                else if (States.IsActive(i.State)) v.Running++;
                else if ((i.State == State.Pending || i.State == State.Unknown) && !(technicianDone && i.PhaseKey == Phases.AccountSetup)) v.Waiting++;
            }
            if (reference != null) v.Missing = reference.Missing.Count;
            if (s.Items.Count == 0) v.Reasons.Add("nothing was deployed");
            if (v.Failed > 0) v.Reasons.Add(Items(v.Failed, "failed", "failed"));
            else
            {
                string ts = s.GetInfo("Task sequence result");
                if (ts != null && ts.StartsWith("Failed", StringComparison.Ordinal)) v.Reasons.Add("the task sequence failed");
            }
            if (v.Running > 0) v.Reasons.Add(Items(v.Running, "is still running", "are still running"));
            if (v.Waiting > 0) v.Reasons.Add(Items(v.Waiting, "has not started", "have not started"));
            if (v.Missing > 0) v.Reasons.Add(Items(v.Missing, "from the reference is missing", "from the reference are missing"));
            v.Ready = v.Reasons.Count == 0;
            v.Headline = v.Ready ? ReadyText : "Not ready: " + Join(v.Reasons);
            return v;
        }

        static string Join(List<string> l)
        {
            if (l.Count == 1) return l[0];
            return string.Join(", ", l.GetRange(0, l.Count - 1).ToArray()) + " and " + l[l.Count - 1];
        }
    }

    public static partial class Report
    {
        const string ShipStyle =
            ".ship main{max-width:860px}.verdict{margin:22px 0 6px;padding:22px 26px;border-radius:10px;border:2px solid var(--okb);background:var(--card)}" +
            ".verdict b{display:block;font-size:34px;line-height:1.15;color:var(--ok)}.verdict p{margin:8px 0 0;color:var(--t2)}.verdict.no{border-color:var(--badb)}.verdict.no b{color:var(--bad)}" +
            ".grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}.facts th{width:38%}.facts tr:last-child th{border-bottom:0}.group{padding:10px 14px;border-bottom:1px solid var(--line)}.group:last-child{border-bottom:0}" +
            ".group h3{margin:0;font-size:14px;display:flex;justify-content:space-between;gap:12px}.group h3 span{font-weight:400;color:var(--t3);font-size:12.5px}.group p{margin:4px 0 0;font-size:12.5px;color:var(--t2)}" +
            ".sign{display:grid;grid-template-columns:1.3fr 1.3fr 1fr;gap:18px;margin-top:10px}.sign i{display:block;border-bottom:1px solid var(--text);height:46px}.sign span{display:block;font-size:12px;color:var(--t3);margin-top:4px}" +
            "@media (max-width:640px){.grid,.sign{grid-template-columns:1fr}}" +
            "@media print{.ship main{max-width:none}.verdict{margin-top:10px;padding:12px 18px}.verdict b{font-size:26px}h2{margin:14px 0 6px;font-size:15px}th,td{padding:4px 10px}.group{padding:7px 12px}.sign i{height:34px}.ship footer{margin-top:18px}.verdict,.group,.grid,.sign{break-inside:avoid}}";

        static readonly string[][] ShipGroups =
        {
            new string[] { "Apps", Kinds.Win32App, Kinds.MsiApp, Kinds.StoreApp, Kinds.Office, Kinds.Application, Kinds.Package },
            new string[] { "Scripts", Kinds.Script },
            new string[] { "Policies and profiles", Kinds.Policy, Kinds.Profile, Kinds.Certificate },
            new string[] { "Software updates", Kinds.Update },
            new string[] { "Task sequence steps", Kinds.Step },
        };

        // A one-page report that says whether a device can go to its user, with room for the technician to sign.
        public static string ShipHtml(Session s, DateTime nowUtc, Reference reference)
        {
            ReferenceResult rr = reference == null ? null : ReferenceCheck.Check(s, nowUtc, reference);
            ShipVerdict v = ShipCheck.Judge(s, rr);
            StringBuilder b = new StringBuilder();
            b.Append("<!doctype html><html lang=\"en\" class=\"ship\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            b.Append("<title>").Append(H("Ready to ship - " + s.Computer)).Append("</title><style>").Append(Style).Append(ShipStyle).Append("</style></head><body><main>");
            b.Append("<header><div class=\"kicker\">Ready to ship report</div><h1>").Append(H(s.Computer)).Append("</h1><div class=\"sub\">").Append(H(s.Items.Count > 0 ? s.Kind : "No deployment found"));
            string profile = s.GetInfo("Autopilot profile") ?? s.GetInfo("Task sequence");
            if (!string.IsNullOrEmpty(profile)) b.Append(" &middot; ").Append(H(profile));
            b.Append("</div></header>");

            b.Append("<div class=\"verdict").Append(v.Ready ? "" : " no").Append("\"><b>").Append(H(v.Headline)).Append("</b><p>");
            if (v.Ready) b.Append(H(s.Items.Count == 1 ? "The one item finished without errors." : "All " + s.Items.Count + " items finished without errors, or were skipped or not needed."));
            else b.Append(H("Fix what is listed below before the device goes to its user."));
            b.Append("</p></div>");

            b.Append("<div class=\"grid\"><div><h2>Device</h2><div class=\"card\"><table class=\"facts\"><tbody>");
            Fact(b, "Device", s.Computer);
            Fact(b, "Serial number", s.GetInfo("Serial number") ?? s.GetInfo("Serial") ?? "Not recorded");
            string model = s.GetInfo("Model");
            string maker = s.GetInfo("Manufacturer");
            Fact(b, "Model", model == null ? "Not recorded" : (maker != null && model.IndexOf(maker, StringComparison.OrdinalIgnoreCase) < 0 ? maker + " " + model : model));
            Fact(b, "Windows", string.IsNullOrEmpty(s.OsVersion) ? null : s.OsVersion);
            b.Append("</tbody></table></div></div><div><h2>Deployment</h2><div class=\"card\"><table class=\"facts\"><tbody>");
            Fact(b, "Deployment", s.Items.Count > 0 ? s.Kind : null);
            Fact(b, s.GetInfo("Task sequence") != null && s.GetInfo("Autopilot profile") == null ? "Task sequence" : "Profile", profile);
            DateTime? first = s.FirstUtc;
            if (first.HasValue) Fact(b, "Started", Txt.Local(first.Value));
            Item pp = s.Find(AutopilotEvents.PreProvKey);
            if (pp != null) Fact(b, "Technician phase", pp.State == State.Succeeded ? "Finished " + Txt.Local(pp.StateUtc) : States.Label(pp.State));
            else if (first.HasValue) Fact(b, "Ended", v.Running + v.Waiting > 0 ? "Still running" : Txt.Local(End(s, nowUtc)));
            if (first.HasValue)
            {
                DateTime end = pp != null && pp.State == State.Succeeded ? pp.StateUtc : End(s, nowUtc);
                if (end > first.Value) Fact(b, pp == null && v.Running + v.Waiting > 0 ? "Running for" : "Took", Txt.Duration(end - first.Value));
            }
            b.Append("</tbody></table></div></div></div>");

            b.Append("<h2>What was installed</h2><div class=\"card\">");
            int groups = 0;
            foreach (string[] g in ShipGroups)
            {
                List<Item> l = new List<Item>();
                foreach (Item i in s.Items) if (Array.IndexOf(g, i.Kind, 1) >= 1) l.Add(i);
                if (l.Count == 0) continue;
                groups++;
                int done = 0, bad = 0;
                foreach (Item i in l)
                {
                    if (States.IsComplete(i.State)) done++;
                    if (i.State == State.Failed) bad++;
                }
                b.Append("<div class=\"group\"><h3>").Append(H(g[0])).Append("<span>").Append(Int(l.Count)).Append(" in total, ").Append(Int(done)).Append(" done");
                if (bad > 0) b.Append(", <b class=\"bad\">").Append(Int(bad)).Append(" failed</b>");
                b.Append("</span></h3><p>");
                for (int k = 0; k < l.Count; k++)
                {
                    Item i = l[k];
                    if (k > 0) b.Append(", ");
                    if (i.State == State.Succeeded) { b.Append(H(i.DisplayName)); continue; }
                    b.Append("<span class=\"").Append(Css(i.State)).Append("\">").Append(H(i.DisplayName)).Append(" (").Append(H(AppGroups.StateLabel(i).ToLowerInvariant())).Append(")</span>");
                }
                b.Append("</p></div>");
            }
            if (groups == 0) b.Append("<div class=\"group\"><p>No apps, scripts or policies were found.</p></div>");
            b.Append("<div class=\"group\"><h3>Failed</h3><p>");
            if (v.Failed == 0) b.Append("None.");
            else
            {
                List<string> names = new List<string>();
                foreach (Item i in s.Items) if (i.State == State.Failed) names.Add(H(i.DisplayName) + (i.ExitCode.HasValue && i.ExitCode.Value != 0 ? " (" + H(Errors.Short(i.ExitCode)) + ")" : ""));
                b.Append("<span class=\"bad\">").Append(string.Join(", ", names.ToArray())).Append("</span>");
            }
            b.Append("</p></div></div>");

            b.Append("<h2>Reference check</h2><div class=\"card\"><div class=\"group\">");
            if (rr == null) b.Append("<p>No reference enrollment is set for this customer, so the device was not compared with one.</p>");
            else
            {
                b.Append("<h3><b class=\"").Append(rr.Matches ? "ok" : "warn").Append("\">").Append(H(rr.Badge)).Append("</b><span>Reference: ")
                 .Append(H(reference.Computer)).Append(", set ").Append(H(Txt.Local(reference.MarkedUtc))).Append("</span></h3>");
                ShipList(b, "Missing here", rr.Missing);
                ShipList(b, "Extra here", rr.Extra);
                ShipList(b, "Failed only here", rr.FailedHere);
                ShipList(b, "Clearly slower", rr.Slower);
                if (rr.Open && rr.Matches) b.Append("<p>Items are still running, so items the reference has may still come.</p>");
            }
            b.Append("</div></div>");

            b.Append("<h2>Technician</h2><div class=\"sign\"><div><i></i><span>Name</span></div><div><i></i><span>Signature</span></div><div><i></i><span>Date</span></div></div>");
            Foot(b, "Ready to ship report for " + s.Computer + ".", nowUtc);
            return b.ToString();
        }

        static void Fact(StringBuilder b, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            b.Append("<tr><th>").Append(H(label)).Append("</th><td>").Append(H(value)).Append("</td></tr>");
        }

        static void ShipList(StringBuilder b, string label, List<string> names)
        {
            if (names.Count == 0) return;
            b.Append("<p><b>").Append(H(label)).Append(":</b> ").Append(H(string.Join(", ", names.ToArray()))).Append("</p>");
        }
    }
}
