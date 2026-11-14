namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class TsAgentTests
    {
        sealed class FakeId : IDeviceIdSource
        {
            public string Uuid;
            public List<string> Macs = new List<string>();
            public string SmbiosUuid() { return Uuid; }
            public List<string> WiredMacs() { return Macs; }
        }

        sealed class FakeHost : IAgentHost
        {
            public bool IsRunning, StopsWhenAsked = true;
            public string Launched;
            public int Stops;
            public bool Running() { return IsRunning; }
            public void Launch(string args) { Launched = args; IsRunning = true; }
            public void RequestStop() { Stops++; if (StopsWhenAsked) IsRunning = false; }
            public bool WaitGone(int ms) { return !IsRunning; }
        }

        public static void Run()
        {
            Ids();
            Names();
            End();
            Control();
            Moves();
        }

        static void Ids()
        {
            FakeId pe = new FakeId();
            pe.Uuid = "4C4C4544-0042-3510-8051-B4C04F4A3732";
            pe.Macs.Add("00-15-5D-01-02-03");
            FakeId win = new FakeId();
            win.Uuid = "{4c4c4544-0042-3510-8051-b4c04f4a3732}";
            win.Macs.Add("00-15-5D-01-02-03");
            win.Macs.Add("00-15-5D-0A-0B-0C");
            string a = TsId.Derive(null, pe), b = TsId.Derive(null, win);
            T.Check(a != null && a.StartsWith("ts-"), "an ID comes from the SMBIOS UUID");
            T.Eq(a, b, "the same ID in WinPE and in Windows");
            T.Check(a.IndexOf("4C4C4544", StringComparison.OrdinalIgnoreCase) < 0, "the UUID is not in the ID as it is");

            FakeId bogus = new FakeId();
            bogus.Uuid = "03000200-0400-0500-0006-000700080009";
            bogus.Macs.Add("00:15:5D:0A:0B:0C");
            bogus.Macs.Add("00:15:5D:01:02:03");
            FakeId bogus2 = new FakeId();
            bogus2.Uuid = "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF";
            bogus2.Macs.Add("00-15-5D-01-02-03");
            string m1 = TsId.Derive(null, bogus), m2 = TsId.Derive(null, bogus2);
            T.Check(m1 != null && m1 != a, "a placeholder UUID falls back to the MAC address");
            T.Eq(m1, m2, "the lowest wired MAC gives the ID, whatever the order");

            T.Eq(null, TsId.Derive(null, new FakeId()), "no UUID and no MAC gives no ID");
            T.Eq("CONTOSO-OSD-0042", TsId.Derive(" CONTOSO-OSD-0042 ", pe), "/id wins over the device");
            T.Eq("P0120006-CONTOSO-PC01", TsId.Derive("P0120006 CONTOSO/PC01", pe), "/id is made safe for a file name");
            T.Eq(null, TsId.Clean("%%"), "an /id of only symbols is no ID");
        }

        static void Names()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-ts-" + Guid.NewGuid().ToString("N"));
            try
            {
                List<string> folders = new List<string> { Path.Combine(dir, "pe"), Path.Combine(dir, "os") };
                TsState pe = TsState.Load(folders);
                T.Eq(0, pe.Names.Count, "no state before the first start");
                pe.Id = "ts-0123456789abcdef0123";
                pe.Remember("MININT-ABC123");
                pe.Save(folders);
                // After the restart only the Windows being installed still has the state.
                Directory.Delete(folders[0], true);
                TsState win = TsState.Load(folders);
                T.Eq("ts-0123456789abcdef0123", win.Id, "the ID is read back after the restart");
                T.Check(win.Remember("CONTOSO-PC01"), "a new name is remembered");
                T.Check(!win.Remember("contoso-pc01"), "the same name is not added twice");
                T.Eq("MININT-ABC123", win.Earlier("CONTOSO-PC01"), "earlier names leave out the current one");
                win.Save(folders);
                T.Eq(2, TsState.Load(folders).Names.Count, "both names are saved");

                // Two agents, before and after the restart, upload to the same row.
                FolderStore store = new FolderStore(Path.Combine(dir, "share"));
                DateTime now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
                AgentOptions o = new AgentOptions();
                Session s1 = new Session();
                s1.Computer = "MININT-ABC123";
                T.Check(new Agent(store, win.Id, now).Cycle(s1, now, o, true), "the WinPE agent uploads");
                Session s2 = new Session();
                s2.Computer = "CONTOSO-PC01";
                s2.SetInfo("Earlier names", win.Earlier("CONTOSO-PC01"));
                T.Check(new Agent(store, win.Id, now.AddMinutes(20)).Cycle(s2, now.AddMinutes(20), o, true), "the Windows agent uploads");
                List<StoreEntry> list = store.List(AgentData.Prefix);
                T.Eq(1, list.Count, "one session across the restart and the rename");
                RemoteDeployment d = AgentData.ToDeployment(list[0], now.AddMinutes(21));
                T.Eq("CONTOSO-PC01", d.Device, "the list shows the current name");
                T.Check(d.Serial != null && d.Serial.IndexOf("MININT-ABC123", StringComparison.OrdinalIgnoreCase) >= 0, "searching for the WinPE name finds the device");
                T.Eq("MININT-ABC123", AgentData.Unpack(store.Get(list[0].Name)).GetInfo("Earlier names"), "the session keeps the earlier names");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void End()
        {
            Engine e = Engine.ForDemo("osd");
            DateTime start = e.Player.StartUtc;
            // Halfway through Install Applications.
            e.Apply(e.DemoTo(e.Player.Scenario.Length - 10 * 60));
            T.Eq(null, TsEnd.Check(e.S, e.Player.Clock, start, 30), "no end while a step runs");
            T.Eq(null, TsEnd.Check(e.S, e.Player.Clock.AddHours(3), start, 30), "a long step is not taken for the end");
            e.Apply(e.DemoTo(e.Player.Scenario.Length + 1));
            string why = TsEnd.Check(e.S, e.Player.Clock, start, 30);
            T.Check(why != null && why.StartsWith("The task sequence ended"), "the end of the task sequence is seen: " + why);

            // No end line: quiet for a while after the last step.
            Session s = new Session();
            DateTime t0 = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            Item step = new Item();
            step.Key = "ts:1:Partition Disk 0 - UEFI";
            step.Name = "Partition Disk 0 - UEFI";
            step.Kind = Kinds.Step;
            step.State = State.Succeeded;
            step.LastUtc = t0;
            s.Items.Add(step);
            T.Eq(null, TsEnd.Check(s, t0.AddMinutes(10), t0, 30), "ten quiet minutes are not the end");
            T.Check(TsEnd.Check(s, t0.AddMinutes(31), t0, 30) != null, "thirty quiet minutes are the end");
            T.Eq(null, TsEnd.Check(s, t0.AddMinutes(31), t0.AddMinutes(20), 30), "an agent that just started waits its own thirty minutes");
            T.Eq(null, TsEnd.Check(new Session(), t0.AddHours(5), t0, 30), "no end before any step was seen");
            s.SetInfo("Task sequence ended", t0.AddMinutes(-60).ToString("o"));
            T.Eq(null, TsEnd.Check(s, t0.AddMinutes(5), t0, 30), "an end from an earlier run does not count");
        }

        static void Control()
        {
            List<string> said = new List<string>();
            Action<string> say = delegate (string x) { said.Add(x); };
            AgentOptions o = new AgentOptions();
            o.Upload = "https://contoso.blob.core.windows.net/trackvice?sp=cw&sig=abc";
            o.Id = "CONTOSO-OSD-0042";
            FakeHost h = new FakeHost();
            T.Eq(0, AgentControl.Start(o, h, say), "start returns 0");
            T.Check(h.Launched != null && h.Launched.StartsWith("/agent /ts /upload \"https://contoso"), "start launches the agent: " + h.Launched);
            T.Check(h.Launched.Contains("/id \"CONTOSO-OSD-0042\""), "the ID is passed on");
            h.Launched = null;
            T.Eq(0, AgentControl.Start(o, h, say), "start again returns 0");
            T.Eq(null, h.Launched, "start again does nothing while it runs");

            T.Eq(0, AgentControl.Stop(h, 1000, say), "stop returns 0 when the agent stopped");
            T.Eq(1, h.Stops, "stop asks once");
            T.Eq(0, AgentControl.Stop(h, 1000, say), "stop when nothing runs returns 0");
            T.Eq(1, h.Stops, "stop when nothing runs asks nothing");
            FakeHost stuck = new FakeHost();
            stuck.IsRunning = true;
            stuck.StopsWhenAsked = false;
            T.Eq(1, AgentControl.Stop(stuck, 1000, say), "stop returns 1 when the agent does not stop");

            bool threw = false;
            try { AgentControl.Start(new AgentOptions(), new FakeHost(), say); }
            catch (ArgumentException) { threw = true; }
            T.Check(threw, "start without an upload location is refused");
            AgentOptions plain = new AgentOptions();
            plain.Upload = @"\\server\share\TrackVice";
            T.Eq("/agent /ts /upload \"\\\\server\\share\\TrackVice\"", AgentControl.ChildArgs(plain), "the plain child command line");
        }

        // smsts.log moves from X: to _SMSTaskSequence to the CCM log folder, rolls over, and the agent restarts in between.
        static void Moves()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-moves-" + Guid.NewGuid().ToString("N"));
            try
            {
                DemoScenario sc = Demo.Osd();
                DateTime start = DateTime.UtcNow.AddHours(-3);
                string demo = Path.Combine(dir, "demo");
                DemoPlayer.WriteLogs(sc, start, demo);
                List<string> pe = Lines(Path.Combine(demo, "pe/smsts.log")), os = Lines(Path.Combine(demo, "os/smsts.log")), ccm = Lines(Path.Combine(demo, "smsts.log"));
                T.Check(pe.Count > 10 && os.Count > 10 && ccm.Count > 3, "the demo has smsts.log lines for each place");

                string x = Path.Combine(dir, "X/Windows/Temp/SMSTSLog/smsts.log");
                string c = Path.Combine(dir, "C/_SMSTaskSequence/Logs/Smstslog/smsts.log");
                string cm = Path.Combine(dir, "C/Windows/CCM/Logs/SMSTSLog/smsts.log");
                string fin = Path.Combine(dir, "C/Windows/CCM/Logs/smsts.log");
                List<string> paths = new List<string> { x, c, cm, fin };
                foreach (string p in paths) Directory.CreateDirectory(Path.GetDirectoryName(p));

                // WinPE: the agent follows X: as lines arrive.
                Engine e1 = Follower(paths, start);
                LiveCollector l1 = Collector(paths, start);
                for (int i = 0; i < pe.Count; i += 7)
                {
                    Append(x, pe.GetRange(i, Math.Min(7, pe.Count - i)));
                    e1.Apply(l1.Read(DateTime.UtcNow));
                }
                T.Eq(6, Steps(e1.S), "WinPE: six steps");

                // Windows: X: is gone, the log goes on in _SMSTaskSequence and rolls over once.
                Directory.Delete(Path.Combine(dir, "X"), true);
                Append(c, pe);
                int third = os.Count / 3;
                Append(c, os.GetRange(0, third));
                Engine e2 = Follower(paths, start);
                LiveCollector l2 = Collector(paths, start);
                e2.Apply(l2.Read(DateTime.UtcNow));
                Append(c, os.GetRange(third, third));
                e2.Apply(l2.Read(DateTime.UtcNow));
                File.Move(c, Path.Combine(Path.GetDirectoryName(c), "smsts-20261114-091500.log"));
                Append(c, os.GetRange(2 * third, os.Count - 2 * third));
                for (int i = 0; i < 6; i++) e2.Apply(l2.Read(DateTime.UtcNow));
                T.Eq(11, Steps(e2.S), "Windows: the WinPE steps and the Windows steps, once each");
                T.Check(OneAttemptEach(e2.S), "Windows: no step counted twice");

                // After the restart the agent starts again; the old file only exists rolled over plus the rest.
                Engine e3 = Follower(paths, start);
                LiveCollector l3 = Collector(paths, start);
                e3.Apply(l3.Read(DateTime.UtcNow));
                T.Eq(11, Steps(e3.S), "after the restart: every step from the rolled-over copy and the current one");
                T.Check(OneAttemptEach(e3.S), "after the restart: no step counted twice");

                // The log moves to the CCM folder with everything so far, and goes on there.
                List<string> all = new List<string>(pe);
                all.AddRange(os);
                Append(cm, all);
                for (int i = 0; i < ccm.Count; i += 4)
                {
                    Append(cm, ccm.GetRange(i, Math.Min(4, ccm.Count - i)));
                    for (int k = 0; k < 5; k++) e3.Apply(l3.Read(DateTime.UtcNow));
                }
                // At the end it is copied once more and _SMSTaskSequence is removed.
                all.AddRange(ccm);
                Append(fin, all);
                Directory.Delete(Path.Combine(dir, "C/_SMSTaskSequence"), true);
                for (int k = 0; k < 6; k++) e3.Apply(l3.Read(DateTime.UtcNow));
                T.Eq(12, Steps(e3.S), "end: every step of the task sequence");
                T.Check(OneAttemptEach(e3.S), "end: no step counted twice across four copies of the log");
                T.Check(e3.S.GetInfo("Task sequence result") != null && e3.S.GetInfo("Task sequence result").StartsWith("Failed"), "end: the result is read");
                T.Check(TsEnd.Check(e3.S, DateTime.UtcNow, DateTime.UtcNow, 30) != null, "end: the agent sees the end");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static Engine Follower(List<string> paths, DateTime start)
        {
            Engine e = Engine.ForFolder(Path.GetDirectoryName(paths[0]));
            return e;
        }

        static LiveCollector Collector(List<string> paths, DateTime start)
        {
            LiveCollector l = new LiveCollector(start.AddHours(-1));
            l.UseWmi = false;
            l.UseEvents = false;
            l.SmstsSource = delegate { return paths; };
            return l;
        }

        static int Steps(Session s)
        {
            int n = 0;
            foreach (Item i in s.Items) if (i.Kind == Kinds.Step) n++;
            return n;
        }

        static bool OneAttemptEach(Session s)
        {
            foreach (Item i in s.Items)
                if (i.Kind == Kinds.Step && i.Attempts.Count > 1)
                {
                    Console.WriteLine("  twice: " + i.Name + " (" + i.Attempts.Count + ")");
                    return false;
                }
            return true;
        }

        static List<string> Lines(string path)
        {
            List<string> l = new List<string>();
            foreach (string s in File.ReadAllText(path).Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) l.Add(s);
            return l;
        }

        static void Append(string path, List<string> lines)
        {
            StringBuilder b = new StringBuilder();
            foreach (string s in lines) b.Append(s).Append("\r\n");
            File.AppendAllText(path, b.ToString(), new UTF8Encoding(false));
        }
    }
}
