namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    public static class DeviceLogTests
    {
        const string Blob = "sessions/3f2a9c4e-0b71-4d2e-9a55-6c1d8e7b4a10.json.gz";
        const string Work = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log";
        const string SmstsPe = @"X:\Windows\Temp\SMSTSLog\smsts.log";
        const string SmstsOs = @"C:\Windows\CCM\Logs\SMSTSLog\smsts.log";

        public static void Run()
        {
            Exists();
            DownloadAndMap();
            OlderBundle();
            Replace();
            CleanUp();
            Hostile();
            DemoStore();
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-devlogs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static Session Sample()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            s.Events.Add(new TEvent { Evidence = new Evidence(EvidenceKinds.LogLine, Work, 12, "x") });
            s.Events.Add(new TEvent { Evidence = new Evidence(EvidenceKinds.LogLine, SmstsPe, 3, "x") });
            s.Events.Add(new TEvent { Evidence = new Evidence(EvidenceKinds.LogLine, SmstsOs, 7, "x") });
            return s;
        }

        static string Local(string dir, string name, string text)
        {
            string p = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + name);
            File.WriteAllText(p, text);
            return p;
        }

        static byte[] SampleBundle(string dir)
        {
            List<KeyValuePair<string, string>> logs = new List<KeyValuePair<string, string>>();
            logs.Add(new KeyValuePair<string, string>(Work, Local(dir, "AppWorkload.log", "work")));
            logs.Add(new KeyValuePair<string, string>(SmstsPe, Local(dir, "smsts.log", "pe")));
            logs.Add(new KeyValuePair<string, string>(SmstsOs, Local(dir, "smsts.log", "os")));
            return DeviceLogs.Build(Sample(), logs);
        }

        static void Exists()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                AgentCatalog c = new AgentCatalog(st);
                T.Check(!c.HasLogs(Blob), "no bundle before the device uploads one");
                T.Check(c.GetLogs(Blob) == null, "no bytes before the device uploads a bundle");
                T.Check(DeviceLogs.Download(c, Blob, "CONTOSO-PC01", Path.Combine(dir, "local"), DateTime.UtcNow) == null, "download says there is nothing yet");
                st.Put(LogBundle.NameFor(Blob), new byte[] { 1, 2 }, null);
                T.Check(c.HasLogs(Blob), "bundle found after upload");
                T.Eq(2, c.GetLogs(Blob).Length, "bundle bytes");
                T.Check(!c.HasLogs("sessions/another.json.gz"), "another session has no bundle");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void DownloadAndMap()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(Path.Combine(dir, "store"));
                Directory.CreateDirectory(Path.Combine(dir, "store"));
                st.Put(LogBundle.NameFor(Blob), SampleBundle(dir), null);
                string root = Path.Combine(dir, "local");
                DeviceLogCopy c = DeviceLogs.Download(new AgentCatalog(st), Blob, "CONTOSO-PC01", root, DateTime.UtcNow);
                T.Check(c != null, "bundle downloaded");
                T.Eq(Path.Combine(root, "CONTOSO-PC01-3f2a9c4e-0b71-4d2e-9a55-6c1d8e7b4a10"), c.Folder, "folder is computer and session id");
                T.Eq(3, c.Logs, "three logs, not counting the session and the index");
                T.Eq("work", File.ReadAllText(DeviceLogs.Map(c, Work)), "AppWorkload.log maps to its file");
                T.Eq("pe", File.ReadAllText(DeviceLogs.Map(c, SmstsPe)), "the WinPE smsts.log maps to its own file");
                T.Eq("os", File.ReadAllText(DeviceLogs.Map(c, SmstsOs)), "the full OS smsts.log maps to its own file");
                T.Eq("work", File.ReadAllText(DeviceLogs.Map(c, @"c:\programdata\microsoft\intunemanagementextension\logs\appworkload.log")), "case does not matter");
                T.Eq("work", File.ReadAllText(DeviceLogs.Map(c, @"D:\Other\AppWorkload.log")), "a unique file name still maps");
                T.Check(DeviceLogs.Map(c, @"D:\Other\smsts.log") == null, "an ambiguous file name does not guess");
                T.Check(DeviceLogs.Map(c, @"C:\Windows\CCM\Logs\AppEnforce.log") == null, "a log not in the bundle maps to nothing");
                T.Check(!Directory.Exists(c.Folder + ".new"), "no half-done folder left");
            }
            finally { Directory.Delete(dir, true); }
        }

        // A bundle without the index, as Report.Bundle wrote them before: logs are named in the order the session first names them.
        static void OlderBundle()
        {
            string dir = TempDir();
            try
            {
                string json = Path.Combine(dir, "s.json");
                Sample().Save(json);
                byte[] zip;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        Entry(z, "session.json", File.ReadAllText(json));
                        Entry(z, "AppWorkload.log", "work");
                        Entry(z, "smsts.log", "pe");
                        Entry(z, "smsts (2).log", "os");
                    }
                    zip = ms.ToArray();
                }
                Directory.CreateDirectory(Path.Combine(dir, "store"));
                FolderStore st = new FolderStore(Path.Combine(dir, "store"));
                st.Put(LogBundle.NameFor(Blob), zip, null);
                DeviceLogCopy c = DeviceLogs.Download(new AgentCatalog(st), Blob, "CONTOSO-PC01", Path.Combine(dir, "local"), DateTime.UtcNow);
                T.Eq("pe", File.ReadAllText(DeviceLogs.Map(c, SmstsPe)), "older bundle: first smsts.log");
                T.Eq("os", File.ReadAllText(DeviceLogs.Map(c, SmstsOs)), "older bundle: second smsts.log");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void Replace()
        {
            string dir = TempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "store"));
                FolderStore st = new FolderStore(Path.Combine(dir, "store"));
                st.Put(LogBundle.NameFor(Blob), SampleBundle(dir), null);
                string root = Path.Combine(dir, "local");
                DeviceLogCopy a = DeviceLogs.Download(new AgentCatalog(st), Blob, "CONTOSO-PC01", root, DateTime.UtcNow);
                File.WriteAllText(Path.Combine(a.Folder, "stale.log"), "old");
                DeviceLogCopy b = DeviceLogs.Download(new AgentCatalog(st), Blob, "CONTOSO-PC01", root, DateTime.UtcNow);
                T.Eq(a.Folder, b.Folder, "the same session goes to the same folder");
                T.Check(!File.Exists(Path.Combine(b.Folder, "stale.log")), "the older copy is replaced, not added to");
                T.Check(!File.Exists(Path.Combine(b.Folder, "AppWorkload (2).log")), "files of the older copy do not push new ones aside");
                T.Eq(1, Directory.GetDirectories(root).Length, "one copy per session");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void CleanUp()
        {
            string root = TempDir();
            try
            {
                DateTime now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
                string old = Path.Combine(root, "CONTOSO-PC02-a");
                string recent = Path.Combine(root, "CONTOSO-PC03-b");
                string half = Path.Combine(root, "CONTOSO-PC04-c.new");
                foreach (string d in new string[] { old, recent, half }) { Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "x.log"), "x"); }
                Directory.SetLastWriteTimeUtc(old, now.AddDays(-31));
                Directory.SetLastWriteTimeUtc(recent, now.AddDays(-29));
                Directory.SetLastWriteTimeUtc(half, now);
                File.WriteAllText(Path.Combine(root, "loose.txt"), "keep");
                T.Eq(2, DeviceLogs.Remove(root, now), "two copies removed");
                T.Check(!Directory.Exists(old), "a copy older than 30 days is deleted");
                T.Check(Directory.Exists(recent), "a copy from 29 days ago stays");
                T.Check(!Directory.Exists(half), "a half-done download is deleted");
                T.Check(File.Exists(Path.Combine(root, "loose.txt")), "files beside the copies are left alone");
                T.Eq(0, DeviceLogs.Remove(Path.Combine(root, "missing"), now), "a missing folder is fine");
            }
            finally { Directory.Delete(root, true); }
        }

        static void Entry(ZipArchive z, string name, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            using (Stream w = z.CreateEntry(name).Open()) w.Write(b, 0, b.Length);
        }

        static void Hostile()
        {
            string dir = TempDir();
            try
            {
                string target = Path.Combine(dir, "a", "b");
                byte[] zip;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        Entry(z, @"..\..\evil1.txt", "1");
                        Entry(z, "../../evil2.txt", "2");
                        Entry(z, @"C:\Windows\System32\evil3.txt", "3");
                        Entry(z, "/etc/evil4.txt", "4");
                        Entry(z, "..", "5");
                        Entry(z, "sub/", "");
                        Entry(z, "same.log", "first");
                        Entry(z, "dir/same.log", "second");
                        Entry(z, "bad<name>?.log", "6");
                    }
                    zip = ms.ToArray();
                }
                Dictionary<string, string> written = new Dictionary<string, string>();
                int n = LogBundle.Unpack(zip, target, written);
                T.Eq(7, n, "hostile zip: every usable entry is written, inside the folder");
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    T.Check(Path.GetDirectoryName(f) == target, "hostile zip: nothing outside the folder (" + f + ")");
                T.Eq(0, Directory.GetDirectories(target).Length, "hostile zip: no folders made");
                T.Check(File.Exists(Path.Combine(target, "evil1.txt")), "..\\ names keep only the file name");
                T.Check(File.Exists(Path.Combine(target, "evil3.txt")), "a drive path keeps only the file name");
                T.Eq("first", File.ReadAllText(Path.Combine(target, "same.log")), "the first same.log keeps its name");
                T.Eq("second", File.ReadAllText(Path.Combine(target, "same (2).log")), "the second same.log is not overwritten");
                T.Eq(Path.Combine(target, "same (2).log"), written["dir/same.log"], "the written map names the renamed file");
                T.Check(File.Exists(Path.Combine(target, "bad_name__.log")), "characters Windows does not allow are replaced");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void DemoStore()
        {
            string dir = TempDir();
            try
            {
                AgentCatalog c = new AgentCatalog(new DemoAgentStore());
                T.Check(c.HasLogs(AgentData.BlobName("contoso-pc117")), "demo task sequence device has logs");
                T.Check(!c.HasLogs(AgentData.BlobName("contoso-lt0398")), "the newest demo device has not uploaded logs");
                DeviceLogCopy osd = DeviceLogs.Download(c, AgentData.BlobName("contoso-pc117"), "CONTOSO-PC117", dir, DateTime.UtcNow);
                T.Check(osd.Logs >= 3, "demo task sequence bundle has its logs (" + osd.Logs + ")");
                string pe = DeviceLogs.Map(osd, SmstsPe), os = DeviceLogs.Map(osd, @"C:\_SMSTaskSequence\Logs\Smstslog\smsts.log");
                T.Check(pe != null && os != null && pe != os, "demo: two smsts.log files map to two files");
                DeviceLogCopy ap = DeviceLogs.Download(c, AgentData.BlobName("contoso-lt0417"), "CONTOSO-LT0417", dir, DateTime.UtcNow);
                string work = DeviceLogs.Map(ap, Work);
                T.Check(work != null && File.ReadAllText(work).Contains("<![LOG["), "demo: AppWorkload.log is a real log");
                bool linesOk = true;
                Session s = c.Load(AgentData.BlobName("contoso-lt0417"));
                foreach (TEvent e in s.Events)
                {
                    if (e.Evidence == null || !e.Evidence.IsLogFile || e.Evidence.Line <= 0) continue;
                    string f = DeviceLogs.Map(ap, e.Evidence.Path);
                    if (f == null || File.ReadAllLines(f).Length < e.Evidence.Line) { linesOk = false; break; }
                }
                T.Check(linesOk, "demo: every evidence line is inside its unpacked log");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
