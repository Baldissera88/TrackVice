namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class CompareTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Identical();
            OnlyOnOneSide();
            FailedOnOneSide();
            Longer();
            SameIdOtherName();
            ExitCodesAndAttempts();
            FindingsOnOneSide();
            Ordering();
            Summary();
            DemoPair();
        }

        static Session S(string computer)
        {
            Session s = new Session();
            s.Computer = computer;
            s.ClockUtc = T0.AddHours(2);
            return s;
        }

        static Item Add(Session s, string key, string name, State st, double startMin, double minutes)
        {
            Item i = new Item();
            i.Key = key;
            i.Name = name;
            i.Kind = Kinds.Win32App;
            i.State = st;
            i.StartUtc = T0.AddMinutes(startMin);
            i.FirstUtc = i.StartUtc.Value;
            i.EndUtc = i.StartUtc.Value.AddMinutes(minutes);
            i.LastUtc = i.EndUtc.Value;
            s.Items.Add(i);
            return i;
        }

        static Session Base(string computer)
        {
            Session s = S(computer);
            Add(s, "a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02", "Contoso VPN Client", State.Succeeded, 0, 4);
            Add(s, "6e2b9d14-7c8a-4b3f-a5d1-9e0f2c6b8d03", "7-Zip", State.Succeeded, 4, 1);
            Add(s, "9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04", "Microsoft Teams", State.Succeeded, 5, 10);
            return s;
        }

        static DiffRow Find(List<DiffRow> rows, string name)
        {
            foreach (DiffRow r in rows) if (r.Name == name) return r;
            return null;
        }

        static int Weighted(List<DiffRow> rows)
        {
            int n = 0;
            foreach (DiffRow r in rows) if (r.Weight > 0) n++;
            return n;
        }

        static void Identical()
        {
            List<DiffRow> rows = SessionDiff.Compare(Base("CONTOSO-PC01"), Base("CONTOSO-PC02"));
            T.Eq(3, rows.Count, "diff identical: one row per item");
            T.Eq(0, Weighted(rows), "diff identical: no weighted rows");
            DiffSummary m = SessionDiff.Summarize(Base("CONTOSO-PC01"), T0.AddHours(2), Base("CONTOSO-PC02"), T0.AddHours(2), rows);
            T.Eq("Both took about as long. No item differs.", m.Sentence, "diff identical: sentence");
        }

        static void OnlyOnOneSide()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            Add(b, "4d7f1a3c-8e2b-4c6d-9a5f-0b3e7d1c5a05", "Adobe Acrobat Reader", State.Succeeded, 15, 3);
            Add(a, "c5e8a2d6-1f4b-4a9c-8d3e-6b0f2a7c4e06", "Contoso Timesheet", State.NotApplicable, 15, 0);
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffRow r = Find(rows, "Adobe Acrobat Reader");
            T.Check(r != null && r.Weight == 2 && r.Note == "Only on CONTOSO-PC02" && r.StateA == "" && r.ItemA == null, "diff: only on B is weight 2");
            DiffRow n = Find(rows, "Contoso Timesheet");
            T.Check(n != null && n.Weight == 1 && n.Note == "Only on CONTOSO-PC01", "diff: not applicable on one side only is weight 1");
            List<DiffRow> same = SessionDiff.Compare(Base("CONTOSO-PC01"), Base("CONTOSO-PC01"));
            T.Eq(0, Weighted(same), "diff: same computer twice");
            Session c = Base("CONTOSO-PC01");
            Add(c, "x1", "Contoso Tools", State.Succeeded, 20, 1);
            T.Eq("Only on CONTOSO-PC01 (B)", Find(SessionDiff.Compare(Base("CONTOSO-PC01"), c), "Contoso Tools").Note, "diff: one device twice is told apart as A and B");
        }

        static void FailedOnOneSide()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            b.Items[1].State = State.Failed;
            b.Items[1].ExitCode = 1603;
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffRow r = Find(rows, "7-Zip");
            T.Eq(2, r.Weight, "diff: failed on one side weight");
            T.Eq("Failed on CONTOSO-PC02, 1603", r.Note, "diff: failed on one side note");
            T.Eq("Done", r.StateA, "diff: state A");
            b.Items[1].State = State.Installing;
            r = Find(SessionDiff.Compare(a, b), "7-Zip");
            T.Check(r.Weight == 1 && r.Note == "Still running on CONTOSO-PC02", "diff: still running on one side");
        }

        static void Longer()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            b.Items[2].EndUtc = b.Items[2].StartUtc.Value.AddMinutes(24);
            b.Items[0].EndUtc = b.Items[0].StartUtc.Value.AddMinutes(4.5);
            b.Items[1].EndUtc = b.Items[1].StartUtc.Value.AddSeconds(100);
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffRow r = Find(rows, "Microsoft Teams");
            T.Check(r.Weight == 1 && r.Note == "14 min longer on CONTOSO-PC02", "diff: clearly longer is flagged: " + r.Note);
            T.Eq(0, Find(rows, "Contoso VPN Client").Weight, "diff: 30 s longer is not flagged");
            T.Eq(0, Find(rows, "7-Zip").Weight, "diff: 40 s longer on a short item is not flagged");
            T.Eq(600.0, r.SecondsA, "diff: seconds A");
            T.Eq(1440.0, r.SecondsB, "diff: seconds B");
        }

        static void SameIdOtherName()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            b.Items[2].Name = "Microsoft Teams (new)";
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            T.Eq(3, rows.Count, "diff: same ID with another name is one row");
            DiffRow r = Find(rows, "Microsoft Teams");
            T.Check(r != null && r.Weight == 1 && r.Note == "Named Microsoft Teams (new) on CONTOSO-PC02", "diff: other name is noted");
            b.Items[2].Name = null;
            rows = SessionDiff.Compare(a, b);
            r = Find(rows, "Microsoft Teams");
            T.Check(r != null && r.Weight == 0, "diff: a missing name is not a difference");
            rows = SessionDiff.Compare(b, a);
            T.Check(Find(rows, "Microsoft Teams") != null, "diff: the name is taken from the side that has it");
        }

        static void ExitCodesAndAttempts()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            a.Items[1].State = State.Failed;
            a.Items[1].ExitCode = 1603;
            b.Items[1].State = State.Failed;
            b.Items[1].ExitCode = 1618;
            for (int k = 1; k <= 3; k++) { Attempt t = new Attempt(); t.Index = k; t.State = State.Failed; b.Items[0].Attempts.Add(t); }
            Attempt one = new Attempt();
            one.Index = 1;
            a.Items[0].Attempts.Add(one);
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffRow r = Find(rows, "7-Zip");
            T.Check(r.Weight == 1 && r.Note == "Exit code 1603 on CONTOSO-PC01, 1618 on CONTOSO-PC02", "diff: different exit codes: " + r.Note);
            r = Find(rows, "Contoso VPN Client");
            T.Check(r.Weight == 1 && r.Note == "3 attempts on CONTOSO-PC02, 1 on CONTOSO-PC01", "diff: attempt counts: " + r.Note);
        }

        static void FindingsOnOneSide()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            a.Rebuild();
            b.Rebuild();
            Finding f = new Finding();
            f.Kind = FindingKinds.Retries;
            f.Severity = 1;
            f.ItemKey = b.Items[0].Key;
            f.Title = "Contoso VPN Client was retried";
            b.Findings.Add(f);
            Finding same = new Finding();
            same.Kind = FindingKinds.Reboot;
            same.Title = "Restart needed";
            a.Findings.Add(same);
            b.Findings.Add(same);
            Finding failed = new Finding();
            failed.Kind = FindingKinds.Failed;
            failed.Title = "7-Zip failed";
            b.Findings.Add(failed);
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            int n = 0;
            foreach (DiffRow r in rows) if (r.IsFinding) n++;
            T.Eq(1, n, "diff: one finding only on one side");
            DiffRow x = Find(rows, "Contoso VPN Client was retried");
            T.Check(x != null && x.Weight == 1 && x.StateB == "Warning" && x.StateA == "" && x.Note == "Finding only on CONTOSO-PC02", "diff: finding row");
        }

        static void Ordering()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC02");
            Add(a, "z1", "Zoom", State.Succeeded, 20, 1);
            b.Items[2].EndUtc = b.Items[2].StartUtc.Value.AddMinutes(30);
            Finding f = new Finding();
            f.Kind = FindingKinds.Stuck;
            f.Title = "Another stall";
            b.Findings.Add(f);
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            T.Eq("Zoom", rows[0].Name, "diff order: weight 2 first");
            T.Eq("Microsoft Teams", rows[1].Name, "diff order: weighted item next");
            T.Eq("Another stall", rows[2].Name, "diff order: findings after items of the same weight");
            T.Eq("7-Zip", rows[3].Name, "diff order: same rows by name");
            T.Eq("Contoso VPN Client", rows[4].Name, "diff order: same rows by name, second");
        }

        static void Summary()
        {
            Session a = Base("CONTOSO-PC01"), b = Base("CONTOSO-PC204");
            b.Items[2].EndUtc = b.Items[2].StartUtc.Value.AddMinutes(24);
            b.Items[1].State = State.Failed;
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffSummary m = SessionDiff.Summarize(a, a.ClockUtc, b, b.ClockUtc, rows);
            T.Eq(TimeSpan.FromMinutes(15), m.TotalA.Value, "diff summary: total A");
            T.Eq(TimeSpan.FromMinutes(29), m.TotalB.Value, "diff summary: total B");
            T.Eq(3, m.DoneA, "diff summary: done A");
            T.Eq(1, m.FailedB, "diff summary: failed B");
            T.Eq("CONTOSO-PC204 took 14 min longer. 2 items differ, 1 failed only there.", m.Sentence, "diff summary: sentence");
            a.Items[0].State = State.Failed;
            rows = SessionDiff.Compare(a, b);
            m = SessionDiff.Summarize(a, a.ClockUtc, b, b.ClockUtc, rows);
            T.Eq("CONTOSO-PC204 took 14 min longer. 3 items differ, 1 failed only on CONTOSO-PC01, 1 failed only there.", m.Sentence, "diff summary: failures on both sides");
            string csv = SessionDiff.ToCsv(rows, m);
            T.Check(csv.StartsWith("Item;Kind;State on CONTOSO-PC01;Seconds on CONTOSO-PC01;State on CONTOSO-PC204"), "diff csv header");
            T.Check(csv.Contains("\"Microsoft Teams\";\"Win32 app\";\"Done\";600;\"Done\";1440;\"14 min longer on CONTOSO-PC204\";1"), "diff csv row");
        }

        static void DemoPair()
        {
            Session a = DemoCompare.A(), b = DemoCompare.B();
            T.Eq(DemoCompare.ComputerB, b.Computer, "demo compare: B is another device");
            List<DiffRow> rows = SessionDiff.Compare(a, b);
            DiffRow zip = Find(rows, "7-Zip 24.08");
            T.Check(zip != null && zip.Weight == 2 && zip.StateB == "Failed", "demo compare: 7-Zip failed on B");
            DiffRow m365 = Find(rows, "Microsoft 365 Apps for enterprise");
            T.Check(m365 != null && m365.Weight == 1 && m365.Note.Contains("longer on " + DemoCompare.ComputerB), "demo compare: Microsoft 365 Apps longer on B");
            DiffSummary m = SessionDiff.Summarize(a, a.ClockUtc, b, b.ClockUtc, rows);
            T.Check(m.Sentence.StartsWith(DemoCompare.ComputerB + " took 14 min longer."), "demo compare sentence: " + m.Sentence);
        }
    }
}
