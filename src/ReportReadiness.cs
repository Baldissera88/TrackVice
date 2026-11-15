namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // The report of a check before enrollment: one card per device, each check with its result.
    public static partial class Report
    {
        public static string ReadinessHtml(List<ReadyDevice> devices, string source, DateTime nowUtc)
        {
            StringBuilder b = new StringBuilder();
            Head(b, "TrackVice - Check before enrollment");
            int ready = 0, problems = 0;
            foreach (ReadyDevice d in devices)
            {
                if (d.Worst == ReadyLevel.Ok) ready++;
                if (d.Worst == ReadyLevel.Problem) problems++;
            }
            b.Append("<header><div class=\"kicker\">Check before enrollment</div><h1>").Append(Int(devices.Count)).Append(devices.Count == 1 ? " device" : " devices")
             .Append("</h1><div class=\"sub\">").Append(H(source)).Append(" &middot; ").Append(H(Txt.Local(nowUtc))).Append("</div></header>");
            b.Append("<div class=\"chips\">");
            Chip(b, Int(devices.Count), "Devices", null);
            Chip(b, Int(ready), "Ready", "ok");
            Chip(b, Int(devices.Count - ready - problems), "With warnings", devices.Count - ready - problems > 0 ? "warn" : null);
            Chip(b, Int(problems), "With problems", problems > 0 ? "bad" : null);
            b.Append("</div>");
            foreach (ReadyDevice d in devices)
            {
                string css = d.Worst == ReadyLevel.Problem ? "bad" : d.Worst == ReadyLevel.Warning ? "warn" : "ok";
                b.Append("<h2>").Append(H(d.Serial)).Append(d.Title != d.Serial ? " <span class=\"t3\">" + H(d.Title) + "</span>" : "").Append(" <span class=\"").Append(css).Append("\">").Append(H(d.Summary)).Append("</span></h2>");
                b.Append("<div class=\"card keep\"><div class=\"tw\"><table><thead><tr><th style=\"width:22%\">Check</th><th style=\"width:10%\">Result</th><th>Details</th></tr></thead><tbody>");
                string group = null;
                foreach (ReadyLine l in d.Lines)
                {
                    if (l.Group != group)
                    {
                        group = l.Group;
                        b.Append("<tr><td colspan=\"3\"><b>").Append(H(group)).Append("</b></td></tr>");
                    }
                    string lc = l.Level == ReadyLevel.Problem ? "bad" : l.Level == ReadyLevel.Warning ? "warn" : "ok";
                    b.Append("<tr><td>").Append(H(l.Check)).Append("</td><td class=\"").Append(lc).Append(" nw\">").Append(H(l.LevelText)).Append("</td><td>").Append(H(l.Text)).Append("</td></tr>");
                }
                b.Append("</tbody></table></div></div>");
            }
            Foot(b, "Read from Intune through Microsoft Graph before the devices were enrolled. It shows what Intune had at that time; assignments and groups can change before the devices start.", nowUtc);
            return b.ToString();
        }
    }
}
