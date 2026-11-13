namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class AgentTests
    {
        public static void Run()
        {
            Folder();
            AzureList();
            Links();
            UploadAndFollow();
            ListStates();
            Task();
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-agent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Folder()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                Dictionary<string, string> meta = new Dictionary<string, string>();
                meta["computer"] = "PC-\u00C6\u00D8\u00C5 = 1";
                meta["empty"] = null;
                st.Put("sessions/a.json.gz", new byte[] { 1, 2, 3 }, meta);
                st.Put("sessions/a.json.gz", new byte[] { 4, 5, 6, 7 }, meta);
                List<StoreEntry> l = st.List("sessions/");
                T.Eq(1, l.Count, "folder store lists one session, not its meta file");
                T.Eq("sessions/a.json.gz", l[0].Name, "folder store name");
                T.Eq("PC-\u00C6\u00D8\u00C5 = 1", l[0].Meta["computer"], "folder store metadata survives");
                T.Eq(4, st.Get("sessions/a.json.gz").Length, "folder store replaces a session");
                T.Eq(0, new FolderStore(dir).List("other/").Count, "folder store, nothing uploaded yet");
                bool threw = false;
                try { new FolderStore(Path.Combine(dir, "missing")).List("sessions/"); } catch (StoreException) { threw = true; }
                T.Check(threw, "a folder that is not there is an error, not an empty list");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void AzureList()
        {
            string xml = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ServiceEndpoint=\"https://x.blob.core.windows.net/\" ContainerName=\"trackvice\"><Prefix>sessions/</Prefix><Blobs>"
                + "<Blob><Name>sessions/abc.json.gz</Name><Properties><Last-Modified>Fri, 13 Nov 2026 10:15:30 GMT</Last-Modified><Content-Length>4711</Content-Length></Properties>"
                + "<Metadata><computer>DESKTOP-1</computer><current>Installing%20Company%20Portal</current><items>12</items></Metadata></Blob>"
                + "<Blob><Name>sessions/def.json.gz</Name><Properties><Last-Modified>Fri, 13 Nov 2026 09:00:00 GMT</Last-Modified><Content-Length>10</Content-Length></Properties><Metadata /></Blob>"
                + "</Blobs><NextMarker>next-page</NextMarker></EnumerationResults>";
            string marker;
            List<StoreEntry> l = AzureStore.ParseList(xml, out marker);
            T.Eq(2, l.Count, "azure list: two blobs");
            T.Eq("next-page", marker, "azure list: next marker");
            T.Eq("sessions/abc.json.gz", l[0].Name, "azure list: name");
            T.Eq(new DateTime(2026, 11, 13, 10, 15, 30, DateTimeKind.Utc), l[0].ModifiedUtc, "azure list: modified time in UTC");
            T.Eq(4711L, l[0].Size, "azure list: size");
            T.Eq("Installing Company Portal", l[0].Meta["current"], "azure list: metadata is decoded");
            T.Eq(0, l[1].Meta.Count, "azure list: blob without metadata");
            AzureStore.ParseList("<EnumerationResults><Blobs /><NextMarker /></EnumerationResults>", out marker);
            T.Check(marker == null, "azure list: no next marker on the last page");
        }

        static bool Refused(string link)
        {
            try { Stores.Open(link); return false; }
            catch (StoreException) { return true; }
        }

        static void Links()
        {
            T.Check(Stores.Open("https://acct.blob.core.windows.net/trackvice?sv=2022-11-02&sp=cw&sig=abc") is AzureStore, "a container SAS link opens Azure storage");
            T.Eq("https://acct.blob.core.windows.net/trackvice", Stores.Open(" https://acct.blob.core.windows.net/trackvice/?sv=1&sig=abc ").Title, "the title never holds the token");
            T.Check(Stores.Open(@"\\server\share\trackvice") is FolderStore, "a share opens a folder");
            T.Check(Refused("https://acct.blob.core.windows.net/trackvice"), "a link without a token is refused");
            T.Check(Refused("https://acct.blob.core.windows.net/?sv=1&sig=abc"), "an account link is refused");
            T.Check(Refused("https://acct.blob.core.windows.net/trackvice/TrackVice.exe?sv=1&sig=abc"), "a link to a file is refused");
            T.Check(Refused("http://acct.blob.core.windows.net/trackvice?sv=1&sig=abc"), "http is refused");
            T.Check(Refused(""), "nothing is refused");
        }

        // A device uploads while its deployment moves on, and the viewer follows it.
        static void UploadAndFollow()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                Engine device = Engine.ForDemo("autopilot");
                device.Apply(device.DemoTo(10 * 60));
                AgentOptions o = new AgentOptions();
                DateTime now = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
                Agent a = new Agent(st, "dev1", now);
                T.Check(a.Cycle(device.S, now, o, false), "the first cycle uploads");
                T.Check(!a.Cycle(device.S, now.AddSeconds(5), o, false), "nothing changed, nothing uploaded");
                T.Check(a.Cycle(device.S, now.AddSeconds(31), o, false), "a heartbeat upload when nothing changed");

                AgentCatalog cat = new AgentCatalog(st);
                List<RemoteDeployment> l = cat.List(DateTime.UtcNow.AddYears(-5));
                T.Eq(1, l.Count, "the catalog lists the uploaded session");
                T.Eq("CONTOSO-LT0417", l[0].Device, "the list shows the device name");
                T.Eq("Contoso standard user-driven", l[0].Profile, "the list shows the Autopilot profile");
                T.Check(l[0].StartUtc.HasValue, "the list shows when the deployment started");

                Engine viewer = Engine.ForRemote(cat.Open(l[0]), l[0]);
                viewer.Apply(viewer.Poll());
                T.Eq(Modes.Agent, viewer.Mode, "the viewer is in agent mode");
                T.Eq(device.S.Items.Count, viewer.S.Items.Count, "the viewer has every item");
                T.Eq(device.S.Events.Count, viewer.S.Events.Count, "the viewer has every event");
                T.Check(viewer.S.Events.Count > 0 && viewer.S.Events[0].Evidence != null, "the events carry their evidence");
                T.Check(viewer.CanWatch && viewer.IsRemote, "an uploaded session can be watched");
                Session before = viewer.S;
                viewer.Apply(viewer.Poll());
                T.Check(object.ReferenceEquals(before, viewer.S), "an unchanged upload keeps the session");

                device.Apply(device.DemoTo(20 * 60));
                T.Check(!a.Cycle(device.S, now.AddSeconds(35), o, false), "a change right after an upload waits");
                T.Check(a.Cycle(device.S, now.AddSeconds(42), o, false), "and is uploaded when the interval has passed");
                viewer.Apply(viewer.Poll());
                T.Check(!object.ReferenceEquals(before, viewer.S), "a new upload replaces the session");
                T.Eq(device.S.CountComplete, viewer.S.CountComplete, "the viewer follows the device");
                T.Eq(device.S.Findings.Count, viewer.S.Findings.Count, "the viewer has the same findings");

                // Idle: nothing active and nothing new for the idle time.
                Engine done = Engine.ForDemo("osd");
                done.Apply(done.DemoTo(100000));
                Agent b = new Agent(st, "dev2", now);
                b.Cycle(done.S, now, o, false);
                T.Check(!b.Idle(done.S, now.AddMinutes(29), o.IdleMinutes), "not idle before the idle time");
                T.Check(done.S.CountActive > 0 || b.Idle(done.S, now.AddMinutes(31), o.IdleMinutes), "idle when nothing runs and nothing changes");

                // A store that fails does not stop the agent.
                Agent c = new Agent(new FolderStore(Path.Combine(dir, "a-file")), "dev3", now);
                File.WriteAllText(Path.Combine(dir, "a-file"), "x");
                T.Check(!c.Cycle(device.S, now, o, false) && c.LastError != null, "a failed upload is kept as an error, not thrown");
            }
            finally { Directory.Delete(dir, true); }
        }

        static RemoteDeployment Dep(int items, int done, int failed, int active, bool idle, int minutesAgo)
        {
            DateTime now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            StoreEntry e = new StoreEntry();
            e.Name = "sessions/x.json.gz";
            e.Meta["computer"] = "PC1";
            e.Meta["items"] = items.ToString();
            e.Meta["done"] = done.ToString();
            e.Meta["failed"] = failed.ToString();
            e.Meta["active"] = active.ToString();
            e.Meta["idle"] = idle ? "1" : "0";
            e.Meta["current"] = "Installing Office";
            e.Meta["started"] = "2026-11-13T11:00:00Z";
            e.Meta["updated"] = now.AddMinutes(-minutesAgo).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
            return AgentData.ToDeployment(e, now);
        }

        static void ListStates()
        {
            RemoteDeployment d = Dep(10, 4, 0, 1, false, 0);
            T.Eq(State.Installing, d.State, "running");
            T.Check(d.StateText.Contains("4 of 10 done") && d.StateText.Contains("Installing Office"), "running shows progress and the current item");
            T.Check(!d.EndUtc.HasValue, "running has no end");
            T.Eq(State.Succeeded, Dep(10, 10, 0, 0, true, 40).State, "all done");
            T.Eq(State.Failed, Dep(10, 9, 1, 0, true, 40).State, "one failed");
            T.Eq(State.Failed, Dep(10, 4, 1, 2, false, 0).State, "failed while still running");
            T.Eq(State.Pending, Dep(0, 0, 0, 0, false, 0).State, "nothing yet");
            d = Dep(10, 4, 0, 1, false, 25);
            T.Eq(State.Unknown, d.State, "a running device that stopped uploading");
            T.Check(d.StateText.StartsWith("No upload for"), "and the list says so");
            T.Check(!Dep(10, 10, 0, 0, true, 300).StateText.StartsWith("No upload"), "an idle device is not called silent");
            T.Eq(new DateTime(2026, 11, 13, 11, 0, 0, DateTimeKind.Utc), d.StartUtc.Value, "start time is read as UTC");
        }

        static void Task()
        {
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.LoadXml(Agent.TaskXml(@"C:\Program Files\TrackVice Agent\R&D\TrackVice.exe").Replace("encoding=\"UTF-16\"", ""));
            System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            T.Eq(@"C:\Program Files\TrackVice Agent\R&D\TrackVice.exe", doc.SelectSingleNode("//t:Command", ns).InnerText, "task: the command is escaped");
            T.Eq("/agent", doc.SelectSingleNode("//t:Arguments", ns).InnerText, "task: runs the agent");
            T.Eq("S-1-5-18", doc.SelectSingleNode("//t:UserId", ns).InnerText, "task: runs as SYSTEM");
            T.Eq("false", doc.SelectSingleNode("//t:DisallowStartIfOnBatteries", ns).InnerText, "task: also starts on battery");
        }
    }
}
