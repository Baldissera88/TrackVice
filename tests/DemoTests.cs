namespace TrackVice.Tests
{
    using System;

    public static class DemoTests
    {
        public static void Dump(string name)
        {
            Engine e = Engine.ForDemo(name);
            e.Apply(e.DemoTo(e.Player.Scenario.Length + 1));
            Session s = e.S;
            Console.WriteLine(s.Computer + " | " + s.Kind + " | items " + s.Items.Count + " | events " + s.Events.Count);
            foreach (Pair p in s.Info) Console.WriteLine("  info " + p.Key + " = " + p.Value);
            foreach (Phase p in s.OrderedPhases)
            {
                Console.WriteLine("[" + p.Name + "] " + States.Label(p.State) + " " + p.Done + "/" + p.Items.Count + " " + Txt.Duration(p.Duration(e.Now)));
                foreach (Item i in p.Items)
                    Console.WriteLine("   " + States.Label(i.State).PadRight(15) + " " + i.DisplayName.PadRight(48) + " " + (i.Kind ?? "").PadRight(12) + " tries " + i.Attempts.Count + " exit " + Errors.Short(i.ExitCode) + " " + Txt.Duration(i.Duration(e.Now)) + " ev " + i.Events.Count);
            }
            foreach (Finding f in s.Findings) Console.WriteLine("! " + f.SeverityName + " " + f.Kind + ": " + f.Title + " | " + f.Message);
        }

        static Session Play(string name)
        {
            Engine e = Engine.ForDemo(name);
            e.Apply(e.DemoTo(e.Player.Scenario.Length + 1));
            return e.S;
        }

        static Item ByName(Session s, string name)
        {
            foreach (Item i in s.Items) if (i.DisplayName == name) return i;
            return null;
        }

        static bool Has(Session s, string kind)
        {
            foreach (Finding f in s.Findings) if (f.Kind == kind) return true;
            return false;
        }

        public static void Run()
        {
            Session a = Play("autopilot");
            T.Eq(12, a.Items.Count, "Autopilot demo items");
            T.Eq("Autopilot", a.Kind, "Autopilot demo kind");
            T.Eq(1, a.Count(State.Failed), "Autopilot demo failed items");
            Item vpn = ByName(a, "Contoso VPN Client 5.2");
            T.Check(vpn != null && vpn.State == State.Succeeded && vpn.Attempts.Count == 3, "VPN client succeeded on the third attempt");
            Item acro = ByName(a, "Adobe Acrobat Reader");
            T.Check(acro != null && acro.State == State.Installing && acro.PhaseKey == Phases.AccountSetup, "Acrobat still installing in account setup");
            T.Check(acro != null && !acro.Percent.HasValue, "download percent is not shown as install progress");
            T.Check(Has(a, FindingKinds.Retries), "Autopilot demo retry finding");
            T.Check(Has(a, FindingKinds.EspTimeLimit), "Autopilot demo ESP time limit finding");
            Item sheet = ByName(a, "Contoso Timesheet");
            T.Check(sheet != null && sheet.ExitCode == 1603, "Timesheet failed with 1603");

            Session o = Play("osd");
            T.Eq(22, o.Items.Count, "task sequence demo items");
            T.Check(Has(o, FindingKinds.TaskSequence), "task sequence failure finding");
            T.Check(Has(o, FindingKinds.NotDetected), "not detected finding");
            T.Check(Has(o, FindingKinds.Reboot), "restart finding");
            T.Eq(6, o.FindPhase(Phases.WinPE).Items.Count, "six Windows PE steps");

            // The same task sequence written to log files and read as a folder gives the same result.
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tv-demo-" + Guid.NewGuid().ToString("N"));
            try
            {
                DemoScenario sc = Demo.Osd();
                DemoPlayer.WriteLogs(sc, DateTime.UtcNow.AddHours(-2), dir);
                Engine f = Engine.ForFolder(dir);
                f.Apply(f.Poll());
                T.Eq(o.Items.Count, f.S.Items.Count, "folder reading gives the same items");
                T.Eq(o.Count(State.Failed), f.S.Count(State.Failed), "folder reading gives the same failures");
                T.Check(Has(f.S, FindingKinds.TaskSequence), "folder reading finds the task sequence failure");
                T.Check(f.Sources.Count >= 4, "folder sources listed");
                string json = f.S.ToJson();
                Session back = Session.FromJson(json);
                Finder.Run(back, DateTime.UtcNow, false);
                T.Eq(f.S.Findings.Count, back.Findings.Count, "findings the same after save and load");
                string html = Report.Html(f.S, DateTime.UtcNow);
                T.Check(html.Contains("Contoso CRM Client") && html.Contains("Findings"), "HTML report");
                string csv = System.IO.Path.Combine(dir, "events.csv");
                Report.Csv(f.S, csv);
                T.Eq(f.S.Events.Count + 1, System.IO.File.ReadAllLines(csv).Length, "CSV has a line per event");
                string zip = System.IO.Path.Combine(dir, "bundle.zip");
                int logs = Report.Bundle(f.S, zip);
                T.Check(logs >= 4 && System.IO.File.Exists(zip), "bundle has the session and its logs");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
