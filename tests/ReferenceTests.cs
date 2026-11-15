namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class ReferenceTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        const string Vpn = "a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a11", Zip = "6e2b9d14-7c8a-4b3f-a5d1-9e0f2c6b8d12", Teams = "9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f13";

        public static void Run()
        {
            Trimmed();
            ToSessionDurations();
            StorePerCustomer();
            StoreInFolder();
            StoreBadFile();
            Matches();
            Differences();
            OpenSessionIgnoresMissing();
            FindingReplaced();
            Badges();
            VerdictReady();
            VerdictFailed();
            VerdictRunning();
            VerdictNothing();
            VerdictMissingFromReference();
            VerdictPreProvisioning();
            ShipHtmlWellFormed();
            ShipHtmlEscaped();
            ShipHtmlNoReference();
        }

        static Session S(string computer)
        {
            Session s = new Session();
            s.Computer = computer;
            s.ClockUtc = T0.AddHours(2);
            s.SetInfo("Autopilot profile", "Contoso standard");
            Phase p = new Phase();
            p.Key = Phases.DeviceSetup;
            p.Name = Phases.Name(p.Key);
            p.Order = Phases.Order(p.Key);
            s.Phases.Add(p);
            return s;
        }

        static Item Add(Session s, string key, string name, string kind, State st, double startMin, double minutes)
        {
            Item i = new Item();
            i.Key = key;
            i.Name = name;
            i.Kind = kind;
            i.PhaseKey = Phases.DeviceSetup;
            i.State = st;
            i.StartUtc = T0.AddMinutes(startMin);
            i.FirstUtc = i.StartUtc.Value;
            i.LastUtc = i.StartUtc.Value;
            if (States.IsFinal(st))
            {
                i.EndUtc = i.StartUtc.Value.AddMinutes(minutes);
                i.LastUtc = i.EndUtc.Value;
            }
            i.Order = s.Items.Count;
            s.Items.Add(i);
            TEvent e = new TEvent();
            e.TimeUtc = i.LastUtc;
            e.Source = "IME";
            e.Text = name + " " + States.Label(st);
            e.ItemKey = key;
            s.Events.Add(e);
            s.Rebuild();
            return i;
        }

        static Session Base(string computer)
        {
            Session s = S(computer);
            Add(s, Vpn, "Contoso VPN Client", Kinds.Win32App, State.Succeeded, 0, 4);
            Add(s, Zip, "7-Zip", Kinds.Win32App, State.Succeeded, 4, 1);
            Add(s, Teams, "Microsoft Teams", Kinds.Win32App, State.Succeeded, 5, 10);
            return s;
        }

        static Reference Ref(Session s)
        {
            return Reference.From(s, s.ClockUtc, "Contoso", "this device");
        }

        static void Trimmed()
        {
            Session s = Base("CONTOSO-PC01");
            s.Items[0].Set("Command line", "setup.exe /quiet");
            Reference r = Ref(s);
            T.Eq(3, r.Items.Count, "reference: every item is kept");
            T.Eq("Contoso VPN Client", r.Items[0].Name, "reference: name");
            T.Eq(Kinds.Win32App, r.Items[0].Kind, "reference: kind");
            T.Eq("Succeeded", r.Items[0].State, "reference: state");
            T.Eq(240.0, r.Items[0].Seconds, "reference: duration in seconds");
            T.Eq("Contoso standard", r.Profile, "reference: profile");
            string json = r.ToJson();
            T.Check(json.IndexOf("setup.exe", StringComparison.Ordinal) < 0, "reference: item details are left out");
            T.Check(json.IndexOf("IME", StringComparison.Ordinal) < 0, "reference: events are left out");
            Reference back = Reference.FromJson(json);
            T.Check(back != null, "reference: reads back");
            T.Eq(3, back.Items.Count, "reference: items read back");
            T.Eq("CONTOSO-PC01", back.Computer, "reference: computer read back");
            T.Check(Reference.FromJson("{\"Kind\":\"Something else\"}") == null, "reference: other JSON is not a reference");
            T.Check(Reference.FromJson("not json") == null, "reference: broken text is not a reference");
        }

        static void ToSessionDurations()
        {
            Session b = Ref(Base("CONTOSO-PC01")).ToSession("Reference (CONTOSO-PC01)");
            T.Eq(3, b.Items.Count, "reference session: items");
            T.Eq("Reference (CONTOSO-PC01)", b.Computer, "reference session: name");
            T.Eq(TimeSpan.FromMinutes(10), b.Find(Teams).Duration(b.ClockUtc), "reference session: duration kept");
            T.Eq(TimeSpan.FromMinutes(15), SessionDiff.Total(b, b.ClockUtc), "reference session: total from the offsets");
            T.Check(b.FindPhase(Phases.DeviceSetup) != null && b.FindPhase(Phases.DeviceSetup).Items.Count == 3, "reference session: phase rebuilt");
        }

        static void StorePerCustomer()
        {
            MemoryReferenceFiles files = new MemoryReferenceFiles();
            ReferenceStore st = new ReferenceStore(files);
            T.Check(st.Get("Contoso") == null, "store: nothing at first");
            st.Set("Contoso", Ref(Base("CONTOSO-PC01")));
            st.Set("Fabrikam", Ref(Base("FABRIKAM-PC07")));
            T.Eq("CONTOSO-PC01", st.Get("contoso").Computer, "store: customer names ignore case");
            T.Eq("FABRIKAM-PC07", st.Get("Fabrikam").Computer, "store: one reference per customer");
            T.Eq("Contoso", st.Get("Contoso").Customer, "store: the customer is kept in the file");
            T.Eq(2, files.Files.Count, "store: one file per customer");
            st.Set("Contoso", Ref(Base("CONTOSO-PC02")));
            T.Eq("CONTOSO-PC02", st.Get("Contoso").Computer, "store: a new reference replaces the old");
            T.Check(st.Clear("Contoso"), "store: clear");
            T.Check(st.Get("Contoso") == null, "store: cleared");
            T.Check(!st.Clear("Contoso"), "store: clearing twice");
            T.Check(st.Get("Fabrikam") != null, "store: other customers keep theirs");
            st.Set(null, Ref(Base("CONTOSO-PC03")));
            T.Eq("CONTOSO-PC03", st.Get(CustomerBook.DefaultName).Computer, "store: no customer is Default");
            T.Check(ReferenceStore.FileName("A/B") != ReferenceStore.FileName("A_B"), "store: replaced characters do not share a file");
            T.Eq(ReferenceStore.FileName("Woodgrove Bank"), ReferenceStore.FileName("WOODGROVE BANK"), "store: file names ignore case");
            string f = ReferenceStore.FileName("..\\..\\x");
            T.Check(f.IndexOf('\\') < 0 && f.IndexOf('/') < 0 && !f.StartsWith("."), "store: file names stay in the folder");
        }

        static void StoreInFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "tv-reftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                ReferenceStore st = new ReferenceStore(new FolderReferenceFiles(root));
                st.Set("Contoso", Ref(Base("CONTOSO-PC01")));
                T.Eq(1, Directory.GetFiles(root).Length, "folder store: one file");
                T.Check(new FileInfo(Directory.GetFiles(root)[0]).Length < 4000, "folder store: the file is small");
                T.Eq("CONTOSO-PC01", new ReferenceStore(new FolderReferenceFiles(root)).Get("Contoso").Computer, "folder store: read by a new store");
                st.Clear("Contoso");
                T.Eq(0, Directory.GetFiles(root).Length, "folder store: cleared");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        static void StoreBadFile()
        {
            MemoryReferenceFiles files = new MemoryReferenceFiles();
            files.Write(ReferenceStore.FileName("Contoso"), "{ broken");
            T.Check(new ReferenceStore(files).Get("Contoso") == null, "store: a broken file is no reference");
        }

        static void Matches()
        {
            Reference r = Ref(Base("CONTOSO-PC01"));
            Session s = Base("CONTOSO-PC02");
            ReferenceResult res = ReferenceCheck.Apply(s, s.ClockUtc, r);
            T.Check(res.Matches, "check: the same enrollment matches");
            T.Eq("Matches the reference", res.Badge, "check: badge when it matches");
            foreach (Finding f in s.Findings) T.Check(f.Kind != ReferenceCheck.FindingKind, "check: no finding when it matches");
        }

        static void Differences()
        {
            Session refS = Base("CONTOSO-PC01");
            Add(refS, "c3d1e2f4-5a6b-4c7d-8e9f-0a1b2c3d4e14", "Contoso Print Client", Kinds.Win32App, State.Succeeded, 15, 2);
            Add(refS, "d4e2f3a5-6b7c-4d8e-9f0a-1b2c3d4e5f15", "Fabrikam Legacy Tool", Kinds.Win32App, State.NotApplicable, 17, 0);
            Reference r = Ref(refS);
            Session s = S("CONTOSO-PC02");
            Add(s, Vpn, "Contoso VPN Client", Kinds.Win32App, State.Failed, 0, 4).ExitCode = 1603;
            Add(s, Zip, "7-Zip", Kinds.Win32App, State.Succeeded, 4, 1.5);
            Add(s, Teams, "Microsoft Teams", Kinds.Win32App, State.Succeeded, 6, 25);
            Add(s, "e5f3a4b6-7c8d-4e9f-a0b1-2c3d4e5f6a16", "Notepad++", Kinds.Win32App, State.Succeeded, 31, 1);
            ReferenceResult res = ReferenceCheck.Apply(s, s.ClockUtc, r);
            T.Eq(1, res.Missing.Count, "check: one missing item");
            T.Eq("Contoso Print Client", res.Missing.Count > 0 ? res.Missing[0] : null, "check: the missing item");
            T.Eq(1, res.Extra.Count, "check: one extra item");
            T.Eq("Notepad++", res.Extra.Count > 0 ? res.Extra[0] : null, "check: the extra item");
            T.Eq(1, res.FailedHere.Count, "check: one item failed only here");
            T.Check(res.FailedHere.Count > 0 && res.FailedHere[0].StartsWith("Contoso VPN Client (1603"), "check: the failed item with its code");
            T.Eq(1, res.Slower.Count, "check: one clearly slower item; 7-Zip is only 30 s slower");
            T.Check(res.Slower.Count > 0 && res.Slower[0].StartsWith("Microsoft Teams"), "check: Teams is clearly slower");
            T.Eq(4, res.Count, "check: four differences");
            T.Eq("4 differences", res.Badge, "check: badge with the count");
            Finding f = null;
            foreach (Finding x in s.Findings) if (x.Kind == ReferenceCheck.FindingKind) f = x;
            T.Check(f != null, "check: a finding is added");
            if (f == null) return;
            T.Eq("Differs from the reference", f.Title, "check: finding title");
            T.Eq(1, f.Severity, "check: finding is a warning");
            T.Check(f.Message.Contains("Missing here: Contoso Print Client."), "check: finding lists the missing item");
            T.Check(f.Message.Contains("Extra here: Notepad++."), "check: finding lists the extra item");
            T.Check(f.Message.Contains("Failed only here: Contoso VPN Client"), "check: finding lists the failed item");
            T.Check(f.Message.Contains("Clearly slower: Microsoft Teams (25 min"), "check: finding lists the slower item");
            T.Check(f.Message.Contains("CONTOSO-PC01"), "check: finding names the reference device");
            T.Check(!f.Message.Contains("Fabrikam Legacy Tool"), "check: not applicable items in the reference are not missing");
        }

        static void OpenSessionIgnoresMissing()
        {
            Reference r = Ref(Base("CONTOSO-PC01"));
            Session s = S("CONTOSO-PC02");
            Add(s, Vpn, "Contoso VPN Client", Kinds.Win32App, State.Succeeded, 0, 4);
            Add(s, Zip, "7-Zip", Kinds.Win32App, State.Installing, 4, 0);
            ReferenceResult res = ReferenceCheck.Check(s, T0.AddMinutes(5), r);
            T.Check(res.Open, "check: something still runs");
            T.Eq(0, res.Missing.Count, "check: items not started yet are not missing while it runs");
            T.Eq("Matches the reference so far", res.Badge, "check: badge while it runs");
        }

        static void FindingReplaced()
        {
            Reference r = Ref(Base("CONTOSO-PC01"));
            Session s = Base("CONTOSO-PC02");
            s.Find(Zip).State = State.Failed;
            ReferenceCheck.Apply(s, s.ClockUtc, r);
            ReferenceCheck.Apply(s, s.ClockUtc, r);
            int n = 0;
            foreach (Finding f in s.Findings) if (f.Kind == ReferenceCheck.FindingKind) n++;
            T.Eq(1, n, "check: applying again keeps one finding");
            T.Check(ReferenceCheck.Apply(s, s.ClockUtc, null) == null, "check: no reference, no result");
            n = 0;
            foreach (Finding f in s.Findings) if (f.Kind == ReferenceCheck.FindingKind) n++;
            T.Eq(0, n, "check: no reference removes the finding");
        }

        static void Badges()
        {
            Reference r = Ref(Base("CONTOSO-PC01"));
            Session bad = Base("CONTOSO-PC03");
            bad.Find(Teams).State = State.Failed;
            List<Session> l = new List<Session> { Base("CONTOSO-PC02"), bad, new Session() };
            Dictionary<string, string> d = ReferenceCheck.Badges(r, l, new List<string> { "k1", "k2", "k3" });
            T.Eq("Matches the reference", d["k1"], "statistics: a matching enrollment");
            T.Eq("1 difference", d["k2"], "statistics: one difference");
            T.Check(!d.ContainsKey("k3"), "statistics: an empty session has no badge");
            T.Eq(0, ReferenceCheck.Badges(null, l, new List<string> { "k1" }).Count, "statistics: no reference, no badges");
        }

        static void VerdictReady()
        {
            Session s = Base("CONTOSO-PC01");
            ShipVerdict v = ShipCheck.Judge(s, ReferenceCheck.Check(s, s.ClockUtc, Ref(Base("CONTOSO-PC09"))));
            T.Check(v.Ready, "verdict: all done is ready");
            T.Eq("Ready to ship", v.Headline, "verdict: ready headline");
            T.Check(ShipCheck.Judge(s, null).Ready, "verdict: ready without a reference");
        }

        static void VerdictFailed()
        {
            Session s = Base("CONTOSO-PC01");
            s.Find(Vpn).State = State.Failed;
            s.Find(Zip).State = State.Failed;
            ShipVerdict v = ShipCheck.Judge(s, null);
            T.Check(!v.Ready, "verdict: failed items are not ready");
            T.Eq("Not ready: 2 items failed", v.Headline, "verdict: failed headline");
            s.Find(Zip).State = State.Succeeded;
            T.Eq("Not ready: 1 item failed", ShipCheck.Judge(s, null).Headline, "verdict: one failed item");
            Session ts = Base("CONTOSO-PC02");
            ts.SetInfo("Task sequence result", "Failed: 0x80004005");
            T.Eq("Not ready: the task sequence failed", ShipCheck.Judge(ts, null).Headline, "verdict: failed task sequence");
        }

        static void VerdictRunning()
        {
            Session s = Base("CONTOSO-PC01");
            s.Find(Teams).State = State.Installing;
            s.Find(Zip).State = State.Pending;
            s.Find(Vpn).State = State.Failed;
            ShipVerdict v = ShipCheck.Judge(s, null);
            T.Eq("Not ready: 1 item failed, 1 item is still running and 1 item has not started", v.Headline, "verdict: every reason");
            T.Eq(3, v.Reasons.Count, "verdict: three reasons");
        }

        static void VerdictNothing()
        {
            ShipVerdict v = ShipCheck.Judge(S("CONTOSO-PC01"), null);
            T.Check(!v.Ready, "verdict: nothing deployed is not ready");
            T.Eq("Not ready: nothing was deployed", v.Headline, "verdict: nothing headline");
        }

        static void VerdictMissingFromReference()
        {
            Session refS = Base("CONTOSO-PC01");
            Add(refS, "c3d1e2f4-5a6b-4c7d-8e9f-0a1b2c3d4e14", "Contoso Print Client", Kinds.Win32App, State.Succeeded, 15, 2);
            Session s = Base("CONTOSO-PC02");
            Add(s, "e5f3a4b6-7c8d-4e9f-a0b1-2c3d4e5f6a16", "Notepad++", Kinds.Win32App, State.Succeeded, 16, 1);
            ShipVerdict v = ShipCheck.Judge(s, ReferenceCheck.Check(s, s.ClockUtc, Ref(refS)));
            T.Eq("Not ready: 1 item from the reference is missing", v.Headline, "verdict: missing items block, extra ones do not");
        }

        static void VerdictPreProvisioning()
        {
            Session s = Base("CONTOSO-PC01");
            Phase acc = new Phase();
            acc.Key = Phases.AccountSetup;
            acc.Name = Phases.Name(acc.Key);
            acc.Order = Phases.Order(acc.Key);
            s.Phases.Add(acc);
            Item pp = Add(s, AutopilotEvents.PreProvKey, "Pre-provisioning", Kinds.Enrollment, State.Succeeded, 16, 0);
            pp.StateUtc = T0.AddMinutes(16);
            Item user = Add(s, "f6a4b5c7-8d9e-4f0a-b1c2-3d4e5f6a7b17", "User Policies", Kinds.Policy, State.Pending, 17, 0);
            user.PhaseKey = Phases.AccountSetup;
            user.StartUtc = null;
            s.Rebuild();
            T.Check(ShipCheck.Judge(s, null).Ready, "verdict: account setup waits for the user after the technician phase");
            pp.State = State.Installing;
            T.Check(!ShipCheck.Judge(s, null).Ready, "verdict: the technician phase still running is not ready");
        }

        // Every element that is opened is closed in order, apart from the void elements HTML has.
        static bool WellFormed(string html, out string why)
        {
            why = null;
            string[] voids = { "meta", "br", "hr", "img", "input", "link" };
            Stack<string> open = new Stack<string>();
            int i = 0;
            while ((i = html.IndexOf('<', i)) >= 0)
            {
                int end = html.IndexOf('>', i);
                if (end < 0) { why = "a tag is not closed"; return false; }
                string tag = html.Substring(i + 1, end - i - 1);
                i = end + 1;
                if (tag.StartsWith("!")) continue;
                bool closing = tag.StartsWith("/");
                string name = (closing ? tag.Substring(1) : tag).Split(' ', '/')[0].ToLowerInvariant();
                if (Array.IndexOf(voids, name) >= 0) continue;
                if (name == "style")
                {
                    int close = html.IndexOf("</style>", i, StringComparison.Ordinal);
                    if (close < 0) { why = "style is not closed"; return false; }
                    i = close + 8;
                    continue;
                }
                if (!closing) { open.Push(name); continue; }
                if (open.Count == 0 || open.Peek() != name) { why = "</" + name + "> closes " + (open.Count == 0 ? "nothing" : "<" + open.Peek() + ">"); return false; }
                open.Pop();
            }
            if (open.Count > 0) { why = "<" + open.Peek() + "> is not closed"; return false; }
            return true;
        }

        static void ShipHtmlWellFormed()
        {
            Session refS = Base("CONTOSO-PC01");
            Add(refS, "c3d1e2f4-5a6b-4c7d-8e9f-0a1b2c3d4e14", "Contoso Print Client", Kinds.Win32App, State.Succeeded, 15, 2);
            Session s = Base("CONTOSO-PC02");
            s.Find(Zip).State = State.Failed;
            s.Find(Zip).ExitCode = 1603;
            Add(s, "f6a4b5c7-8d9e-4f0a-b1c2-3d4e5f6a7b18", "Contoso Baseline", Kinds.Policy, State.Succeeded, 16, 0.2);
            s.SetInfo("Serial number", "CZC1234ABC");
            string html = Report.ShipHtml(s, s.ClockUtc, Ref(refS));
            string why;
            T.Check(WellFormed(html, out why), "ship report: well-formed (" + why + ")");
            T.Check(html.StartsWith("<!doctype html>"), "ship report: doctype");
            T.Check(html.Contains("Not ready: 1 item failed and 1 item from the reference is missing"), "ship report: verdict");
            T.Check(html.Contains("class=\"verdict no\""), "ship report: red verdict");
            T.Check(html.Contains("CZC1234ABC"), "ship report: serial number");
            T.Check(html.Contains("Model</th><td>Not recorded"), "ship report: model not recorded");
            T.Check(html.Contains("Policies and profiles"), "ship report: policies group");
            T.Check(html.Contains("Signature"), "ship report: room for a signature");
            T.Check(html.Contains("Missing here:</b> Contoso Print Client"), "ship report: reference check");
            T.Check(html.Contains("@media print"), "ship report: print styles");
            T.Check(html.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0 && html.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0, "ship report: self-contained");

            Session ok = Base("CONTOSO-PC03");
            string good = Report.ShipHtml(ok, ok.ClockUtc, Ref(Base("CONTOSO-PC01")));
            T.Check(WellFormed(good, out why), "ship report ready: well-formed (" + why + ")");
            T.Check(good.Contains("<div class=\"verdict\"><b>Ready to ship</b>"), "ship report ready: green verdict");
            T.Check(good.Contains("Failed</h3><p>None."), "ship report ready: failed none");
            T.Check(good.Contains("Matches the reference"), "ship report ready: matches");
        }

        static void ShipHtmlEscaped()
        {
            Session s = Base("<script>alert(1)</script>");
            s.Find(Zip).Name = "Tool \"x\" & <b>bold</b>";
            s.SetInfo("Autopilot profile", "<img src=x onerror=alert(2)>");
            s.SetInfo("Model", "Surface <Laptop>");
            string html = Report.ShipHtml(s, s.ClockUtc, null);
            string why;
            T.Check(WellFormed(html, out why), "ship report escaped: well-formed (" + why + ")");
            T.Check(html.IndexOf("<script>", StringComparison.OrdinalIgnoreCase) < 0, "ship report escaped: no script tag");
            T.Check(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "ship report escaped: computer name");
            T.Check(html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0, "ship report escaped: profile");
            T.Check(html.Contains("Tool &quot;x&quot; &amp; &lt;b&gt;bold&lt;/b&gt;"), "ship report escaped: item name");
            T.Check(html.Contains("Surface &lt;Laptop&gt;"), "ship report escaped: model");
        }

        static void ShipHtmlNoReference()
        {
            Session s = Base("CONTOSO-PC01");
            string html = Report.ShipHtml(s, s.ClockUtc, null);
            T.Check(html.Contains("No reference enrollment is set"), "ship report: says when there is no reference");
            T.Check(html.Contains("Ready to ship"), "ship report: ready without a reference");
        }
    }
}
