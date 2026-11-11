namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;

    public static class Report
    {
        static string H(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        static string Css(State s)
        {
            switch (s)
            {
                case State.Failed: return "bad";
                case State.Succeeded: return "ok";
                case State.WaitingReboot: return "warn";
                case State.Downloading: case State.Installing: case State.Detecting: return "run";
                default: return "idle";
            }
        }

        public static string Html(Session s, DateTime nowUtc)
        {
            StringBuilder b = new StringBuilder();
            b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            b.Append("<title>TrackVice report - ").Append(H(s.Computer)).Append("</title><style>");
            b.Append(":root{--bg:#eef1f6;--card:#fff;--text:#111418;--t2:#454b54;--t3:#6e7580;--line:#e6e9ef;--accent:#005fb8;--ok:#0f7b0f;--bad:#c42b1c;--warn:#9d5d00;--run:#005fb8}");
            b.Append("@media (prefers-color-scheme:dark){:root{--bg:#191b20;--card:#24272d;--text:#f5f7fa;--t2:#d3d7de;--t3:#a2a8b2;--line:#363a42;--accent:#60cdff;--ok:#6ccb5f;--bad:#ff99a4;--warn:#fce100;--run:#60cdff}}");
            b.Append("body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 'Segoe UI',system-ui,sans-serif}main{max-width:1180px;margin:0 auto;padding:28px 20px 48px}");
            b.Append("h1{font-size:26px;margin:0}h2{font-size:17px;margin:28px 0 10px}.sub{color:var(--t3);font-size:12.5px;margin-top:4px}");
            b.Append(".chips{display:flex;flex-wrap:wrap;gap:10px;margin-top:18px}.chip{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px 14px;min-width:110px}.chip b{display:block;font-size:22px}.chip span{color:var(--t3);font-size:12px}");
            b.Append(".card{background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--t2);font-weight:600;background:var(--card)}tr:last-child td{border-bottom:0}");
            b.Append(".ok{color:var(--ok)}.bad{color:var(--bad)}.warn{color:var(--warn)}.run{color:var(--run)}.idle{color:var(--t3)}.mono{font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px}.t3{color:var(--t3)}");
            b.Append(".phase{display:flex;justify-content:space-between;align-items:baseline;padding:10px 12px;border-bottom:1px solid var(--line);font-weight:600}.phase span{font-weight:400;color:var(--t3);font-size:12.5px}");
            b.Append(".finding{padding:10px 14px;border-bottom:1px solid var(--line)}.finding:last-child{border-bottom:0}.finding p{margin:4px 0 0}.bar{height:4px;background:var(--line);border-radius:2px;margin-top:4px}.bar i{display:block;height:4px;border-radius:2px;background:var(--accent)}");
            b.Append("</style></head><body><main>");
            b.Append("<h1>").Append(H(s.Computer)).Append("</h1><div class=\"sub\">").Append(H(s.Kind)).Append(" &middot; ").Append(H(s.OsVersion));
            if (!string.IsNullOrEmpty(s.User)) b.Append(" &middot; ").Append(H(s.User));
            b.Append(" &middot; report from TrackVice ").Append(H(s.AppVersion)).Append(", ").Append(H(Txt.Local(nowUtc))).Append("</div>");

            b.Append("<div class=\"chips\">");
            Chip(b, s.Items.Count.ToString(CultureInfo.InvariantCulture), "Items", null);
            Chip(b, s.CountComplete.ToString(CultureInfo.InvariantCulture), "Done", "ok");
            Chip(b, s.CountActive.ToString(CultureInfo.InvariantCulture), "In progress", "run");
            Chip(b, s.Count(State.Failed).ToString(CultureInfo.InvariantCulture), "Failed", s.Count(State.Failed) > 0 ? "bad" : null);
            DateTime? first = s.FirstUtc;
            if (first.HasValue) Chip(b, Txt.DurationShort(nowUtc - first.Value), "Elapsed", null);
            b.Append("</div>");

            if (s.Info.Count > 0)
            {
                b.Append("<h2>Deployment</h2><div class=\"card\"><table>");
                foreach (Pair p in s.Info) b.Append("<tr><th style=\"width:220px\">").Append(H(p.Key)).Append("</th><td>").Append(H(p.Value)).Append("</td></tr>");
                b.Append("</table></div>");
            }

            if (s.Findings.Count > 0)
            {
                b.Append("<h2>Findings</h2><div class=\"card\">");
                foreach (Finding f in s.Findings)
                {
                    b.Append("<div class=\"finding\"><b class=\"").Append(f.Severity >= 2 ? "bad" : f.Severity == 1 ? "warn" : "t3").Append("\">").Append(H(f.SeverityName)).Append("</b> &nbsp;<b>").Append(H(f.Title)).Append("</b>");
                    b.Append("<p>").Append(H(f.Message)).Append("</p>");
                    if (!string.IsNullOrEmpty(f.Hint)) b.Append("<p class=\"t3\">").Append(H(f.Hint)).Append("</p>");
                    if (f.Evidence != null) b.Append("<p class=\"mono t3\">").Append(H(f.Evidence.Where)).Append(": ").Append(H(Txt.Short(f.Evidence.Text, 300))).Append("</p>");
                    b.Append("</div>");
                }
                b.Append("</div>");
            }

            b.Append("<h2>Progress</h2>");
            foreach (Phase p in s.OrderedPhases)
            {
                b.Append("<div class=\"card\" style=\"margin-bottom:12px\"><div class=\"phase\"><div class=\"").Append(Css(p.State)).Append("\">").Append(H(p.Name)).Append("</div><span>")
                 .Append(p.Done).Append(" of ").Append(p.Items.Count).Append(" done");
                if (p.FailedCount > 0) b.Append(", ").Append(p.FailedCount).Append(" failed");
                TimeSpan? d = p.Duration(nowUtc);
                if (d.HasValue) b.Append(" &middot; ").Append(H(Txt.Duration(d)));
                b.Append("</span></div><table><tr><th>Item</th><th style=\"width:110px\">Kind</th><th style=\"width:130px\">State</th><th style=\"width:110px\">Duration</th><th style=\"width:120px\">Exit code</th><th style=\"width:70px\">Tries</th></tr>");
                foreach (Item i in p.Items)
                {
                    b.Append("<tr><td>").Append(H(i.DisplayName));
                    if (States.IsActive(i.State) && i.Percent.HasValue) b.Append("<div class=\"bar\"><i style=\"width:").Append(i.Percent.Value).Append("%\"></i></div>");
                    b.Append("</td><td class=\"t3\">").Append(H(i.Kind)).Append("</td><td class=\"").Append(Css(i.State)).Append("\">").Append(H(States.Label(i.State)));
                    if (States.IsActive(i.State) && i.Percent.HasValue) b.Append(" ").Append(i.Percent.Value).Append(" %");
                    b.Append("</td><td>").Append(H(Txt.Duration(i.Duration(nowUtc)))).Append("</td><td class=\"mono\">").Append(H(Errors.Short(i.ExitCode))).Append("</td><td>").Append(i.Attempts.Count > 1 ? i.Attempts.Count.ToString(CultureInfo.InvariantCulture) : "").Append("</td></tr>");
                }
                b.Append("</table></div>");
            }

            b.Append("<h2>Timeline</h2><div class=\"card\"><table><tr><th style=\"width:150px\">Time</th><th style=\"width:130px\">Source</th><th style=\"width:220px\">Item</th><th>Event</th></tr>");
            int n = 0;
            for (int k = s.Events.Count - 1; k >= 0 && n < 3000; k--, n++)
            {
                TEvent e = s.Events[k];
                Item it = s.Find(e.ItemKey);
                b.Append("<tr><td class=\"mono\">").Append(H(Txt.Local(e.TimeUtc))).Append("</td><td class=\"t3\">").Append(H(e.Source)).Append("</td><td>").Append(H(it != null ? it.DisplayName : "")).Append("</td><td class=\"")
                 .Append(e.Severity >= 2 ? "bad" : e.Severity == 1 ? "warn" : "").Append("\">").Append(H(Txt.Short(e.Text, 400))).Append("</td></tr>");
            }
            b.Append("</table></div>");
            if (s.Events.Count > 3000) b.Append("<p class=\"t3\">Only the newest 3,000 of ").Append(s.Events.Count).Append(" events are shown. The CSV export has all of them.</p>");
            if (s.Notes.Count > 0)
            {
                b.Append("<h2>Notes</h2><ul>");
                foreach (string note in s.Notes) b.Append("<li>").Append(H(note)).Append("</li>");
                b.Append("</ul>");
            }
            b.Append("<p class=\"t3\" style=\"margin-top:32px\">TrackVice by Daniel L. Baldissera &middot; MIT license</p></main></body></html>");
            return b.ToString();
        }

        static void Chip(StringBuilder b, string value, string label, string css)
        {
            b.Append("<div class=\"chip\"><b").Append(css != null ? " class=\"" + css + "\"" : "").Append(">").Append(H(value)).Append("</b><span>").Append(H(label)).Append("</span></div>");
        }

        public static void Csv(Session s, string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Time (local),Source,Severity,Phase,Item,Kind,State,Event,Evidence");
                foreach (TEvent e in s.Events)
                {
                    Item i = s.Find(e.ItemKey);
                    Phase p = i != null ? s.FindPhase(i.PhaseKey) : null;
                    w.WriteLine(string.Join(",", new string[]
                    {
                        Q(Txt.Local(e.TimeUtc)), Q(e.Source), Q(e.Severity >= 2 ? "Error" : e.Severity == 1 ? "Warning" : "Info"),
                        Q(p != null ? p.Name : ""), Q(i != null ? i.DisplayName : ""), Q(i != null ? i.Kind : ""),
                        Q(e.StateName != null ? States.Label(e.State) : ""), Q(e.Text), Q(e.Evidence != null ? e.Evidence.Where : "")
                    }));
                }
            }
        }

        static string Q(string v)
        {
            v = v ?? "";
            // Keep spreadsheets from running a value as a formula.
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        // A zip with the session and the log files its evidence points to, for sending to someone else.
        public static int Bundle(Session s, string zipPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "TrackVice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            int files = 0;
            try
            {
                s.Save(Path.Combine(tmp, "session.json"));
                HashSet<string> logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TEvent e in s.Events) if (e.Evidence != null && e.Evidence.IsLogFile) logs.Add(e.Evidence.Path);
                foreach (string log in logs)
                {
                    try
                    {
                        if (!File.Exists(log)) continue;
                        string name = Path.GetFileName(log);
                        string dest = Path.Combine(tmp, name);
                        int k = 2;
                        while (File.Exists(dest)) dest = Path.Combine(tmp, Path.GetFileNameWithoutExtension(name) + " (" + k++ + ")" + Path.GetExtension(name));
                        using (FileStream src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (FileStream dst = File.Create(dest)) src.CopyTo(dst);
                        files++;
                    }
                    catch (Exception) { }
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                Zip.FromFolder(tmp, zipPath);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }
            return files;
        }
    }

    // Writes a folder's files into a zip with System.IO.Compression, which ships with .NET Framework 4.5.
    public static class Zip
    {
        public static void FromFolder(string folder, string zipPath)
        {
            using (FileStream fs = File.Create(zipPath))
            using (System.IO.Compression.ZipArchive z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    System.IO.Compression.ZipArchiveEntry e = z.CreateEntry(Path.GetFileName(f), System.IO.Compression.CompressionLevel.Optimal);
                    using (Stream w = e.Open())
                    using (FileStream r = File.OpenRead(f)) r.CopyTo(w);
                }
            }
        }
    }
}
