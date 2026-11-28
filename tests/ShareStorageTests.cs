namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public static class ShareStorageTests
    {
        sealed class Plain : ICustomerProtector
        {
            public string Protect(string plain) { return "p:" + plain; }
            public string Unprotect(string stored) { return stored.Substring(2); }
        }

        const string Share = @"\\fs01\TrackVice\Contoso";

        public static void Run()
        {
            Paths();
            Permissions();
            Script();
            Saving();
            CheckAgainstFolder();
            PublishToFolder();
            AzureUnchanged();
        }

        static void Paths()
        {
            T.Eq(SharePathKind.Unc, ShareStorage.KindOf(Share), "Share: a UNC path");
            T.Eq(SharePathKind.Unc, ShareStorage.KindOf(@"\\fs01\TrackVice$"), "Share: a hidden share, no folder");
            T.Eq(SharePathKind.Unc, ShareStorage.KindOf("\"\\\\fs01\\Track Vice\\Northwind Traders\\\""), "Share: quotes, spaces and a trailing slash");
            T.Eq(@"\\fs01\Track Vice\Northwind Traders", ShareStorage.Normalize("  \"\\\\fs01\\Track Vice\\Northwind Traders\\\" "), "Share: normalized");
            T.Eq(SharePathKind.Local, ShareStorage.KindOf(@"D:\TrackVice"), "Share: a drive letter is local");
            T.Eq(SharePathKind.Local, ShareStorage.KindOf(@"Z:\"), "Share: a drive root is local");
            T.Check(ShareStorage.PathProblem(Share) == null, "Share: a UNC path has no problem");
            T.Check(ShareStorage.PathProblem(@"D:\TrackVice") == null, "Share: a local path is allowed here, with a warning from the check");
            T.Check(ShareStorage.LocalWarning(@"D:\TrackVice").Contains("UNC"), "Share: the local warning names UNC");
            string[] bad = { "", "  ", @"\\fs01", @"\\fs01\", @"\\\share", "fs01\\share", "TrackVice", "https://a.blob.core.windows.net/trackvice?sig=x", @"\\fs01\share\a|b", @"\\fs01\sh*re" };
            foreach (string b in bad) T.Check(ShareStorage.PathProblem(b) != null, "Share: not a share path: " + b);
            T.Check(ShareStorage.PathProblem("").Contains(ShareStorage.Example), "Share: the empty message shows an example");
            T.Eq(@"\\fs01\TrackVice\Contoso\agent\TrackVice.exe", ShareStorage.ExePath(Share + "\\"), "Share: the exe path on the share");
        }

        static void Permissions()
        {
            List<string> l = ShareStorage.Icacls(Share);
            string all = ShareStorage.IcaclsText(Share);
            T.Check(all.Contains("icacls \"" + Share + "\" /inheritance:r"), "icacls: the folder is cut off from its parent");
            T.Check(all.Contains("/grant \"BUILTIN\\Administrators:(OI)(CI)F\"") && all.Contains("/grant \"NT AUTHORITY\\SYSTEM:(OI)(CI)F\""), "icacls: administrators and SYSTEM have full control");
            T.Check(all.Contains("\"Domain Computers:(CI)(X,RA,WD,AD)\""), "icacls: Domain Computers create files and write, and cannot read");
            T.Check(all.Contains("\"CREATOR OWNER:(OI)(CI)(IO)M\""), "icacls: a device can replace its own files");
            T.Check(all.IndexOf("Domain Computers:(CI)(X,RA,WD,AD)", StringComparison.Ordinal) == all.LastIndexOf("Domain Computers:(CI)(X,RA,WD,AD)", StringComparison.Ordinal), "icacls: the write right is given once");
            string agent = Share + "\\agent";
            T.Check(all.Contains("icacls \"" + agent + "\" /inheritance:r /grant:r \"NT AUTHORITY\\SYSTEM:(OI)(CI)F\" /grant:r \"BUILTIN\\Administrators:(OI)(CI)F\" /grant:r \"Domain Computers:(OI)(CI)RX\""), "icacls: the agent folder is read only for the devices");
            T.Check(all.Contains("mkdir \"" + agent + "\""), "icacls: the agent folder is made first");
            T.Check(l[0].Contains("mkdir") && l[3].Contains("/inheritance:r"), "icacls: order, folders then rights");
            foreach (string s in l) T.Check(s.StartsWith("icacls \"") || s.StartsWith("if not exist"), "icacls: each line is a command: " + s);
            string spaced = ShareStorage.IcaclsText("\"\\\\fs01\\Track Vice\\Northwind Traders\\\"");
            T.Check(spaced.Contains("icacls \"\\\\fs01\\Track Vice\\Northwind Traders\" /inheritance:r") && spaced.IndexOf("\"\"", StringComparison.Ordinal) < 0, "icacls: a path with spaces is quoted once");
            T.Check(ShareStorage.PermissionsText.Contains("Domain Computers") && ShareStorage.PermissionsText.Contains("administrators"), "Permissions text");
            T.Check(ShareStorage.LimitsText.Contains("task sequences") && ShareStorage.LimitsText.Contains("Hybrid Autopilot") && ShareStorage.LimitsText.Contains("cloud-only") && ShareStorage.LimitsText.Contains("Windows PE"), "Limits text");
            PermissionsAreTight();
        }

        // What a device with write access to the share can do to the others.
        static void PermissionsAreTight()
        {
            List<string> l = ShareStorage.Icacls(Share);
            string all = ShareStorage.IcaclsText(Share);
            // The write right of Domain Computers is on folders only: with (OI) on the files, a device could write over the session of another.
            foreach (string s in l)
            {
                int at = s.IndexOf("\"Domain Computers:", StringComparison.Ordinal);
                if (at < 0) continue;
                string ace = s.Substring(at, s.IndexOf('"', at + 1) - at);
                if (ace.Contains("WD")) T.Check(!ace.Contains("(OI)"), "icacls secure: Domain Computers cannot write into the files of other devices: " + ace);
            }
            // The upload folders are made by an administrator and owned by administrators, so no device owns them and can open them up.
            string sessions = Share + "\\" + ShareStorage.SessionsFolder, logs = Share + "\\" + ShareStorage.LogsFolder;
            T.Eq("sessions/", AgentData.Prefix, "icacls secure: the sessions folder is the one the agent writes to");
            T.Eq("logs/", LogBundle.Prefix, "icacls secure: the logs folder is the one the agent writes to");
            foreach (string f in new string[] { sessions, logs })
            {
                int made = all.IndexOf("mkdir \"" + f + "\"", StringComparison.Ordinal), rights = all.IndexOf("icacls \"" + Share + "\" /inheritance:r", StringComparison.Ordinal);
                T.Check(made >= 0 && made < rights, "icacls secure: " + f + " is made by the administrator before the devices get rights");
                T.Check(all.Contains("icacls \"" + f + "\" /setowner \"BUILTIN\\Administrators\"") && all.Contains("icacls \"" + f + "\" /reset"), "icacls secure: " + f + " is owned by administrators with the rights of the share folder");
            }
            // A file or folder a device put in the agent folder before is taken from it.
            string agent = Share + "\\agent";
            int owner = all.IndexOf("icacls \"" + agent + "\" /setowner \"BUILTIN\\Administrators\" /T /C", StringComparison.Ordinal);
            int reset = all.IndexOf("icacls \"" + agent + "\" /reset /T /C", StringComparison.Ordinal);
            int grant = all.IndexOf("icacls \"" + agent + "\" /inheritance:r", StringComparison.Ordinal);
            T.Check(owner >= 0 && owner < reset && reset < grant, "icacls secure: the agent folder gets its owner, then its rights from nothing");
            T.Check(all.IndexOf("CREATOR OWNER", grant, StringComparison.Ordinal) < 0, "icacls secure: no device can change a file in the agent folder");
            // The lines are pasted into a prompt as administrator: a path that would break out of the quotes is refused.
            string[] bad = { "\\\\fs01\\Track\"Vice\\x", "\\\\fs01\\a\\%COMSPEC%", "\\\\fs01\\a\\b\r\ndel /q C:\\x", "\\\\fs01\\a\\b\ncalc", "not a path", "" };
            foreach (string b in bad)
            {
                bool refused = false;
                try { ShareStorage.Icacls(b); }
                catch (ArgumentException) { refused = true; }
                T.Check(refused, "icacls secure: refused: " + b.Replace("\r", "\\r").Replace("\n", "\\n"));
            }
        }

        static void Script()
        {
            NewCustomerRun run = new NewCustomerRun();
            run.Customer = "Contoso";
            run.UseShare = true;
            run.Share = Share;
            run.ExeSha256 = "ABCD";
            run.Alert = "https://contoso.example/hook";
            run.MaxAgeHours = 12;
            string noKey = run.Script();
            T.Check(noKey.Contains("$Upload    = '" + Share + "'"), "Share script: $Upload is the share");
            T.Check(noKey.Contains("$Download  = '" + Share + "\\agent\\TrackVice.exe'"), "Share script: $Download is the exe on the share");
            T.Check(noKey.Contains("$Sha256    = 'ABCD'") && noKey.Contains("$MaxAgeHours = 12") && noKey.Contains("$Alert     = 'https://contoso.example/hook'"), "Share script: SHA-256, age and alert");
            T.Check(noKey.Contains("$UpdateKey = ''") && noKey.Contains("$UpdateLink = ''"), "Share script: no update without a key");
            T.Check(noKey.Contains("Copy-Item -LiteralPath $Download") && noKey.IndexOf("Invoke-WebRequest", StringComparison.Ordinal) < 0, "Share script: copies from the share, no download");
            T.Check(noKey.IndexOf("blob.core.windows.net", StringComparison.Ordinal) < 0 && noKey.IndexOf("sig=", StringComparison.Ordinal) < 0, "Share script: no Azure link");
            run.UpdateKey = "KEYTEXT";
            string withKey = run.Script();
            T.Check(withKey.Contains("$UpdateKey = 'KEYTEXT'") && withKey.Contains("$UpdateLink = '" + Share + "'"), "Share script: the update source is the share folder, which holds agent\\latest.json");
            ScriptValues v = run.Values();
            T.Eq(Share, v.Upload, "Share values: upload");
            T.Eq(Share, v.UpdateLink, "Share values: update source");
            T.Eq(Share, run.UploadLocation, "Share run: upload location");
            T.Eq(Share, run.PublishLocation, "Share run: publish location");
            T.Eq(@"TrackVice.exe /agent-start /upload ""\\fs01\TrackVice\Contoso""", ShareStorage.TaskSequenceCommand(Share, ""), "Task sequence command");
            T.Eq(@"TrackVice.exe /agent-start /upload ""\\fs01\TrackVice\Contoso"" /alert ""https://x.example/h""", ShareStorage.TaskSequenceCommand(Share + "\\", " https://x.example/h "), "Task sequence command with an alert");
            // The step runs as SYSTEM: a quote in a value must not end it and add an argument such as /update.
            string evil = ShareStorage.TaskSequenceCommand(Share, "https://x.example/h\" /update \"\\\\evil\\share");
            T.Eq(@"TrackVice.exe /agent-start /upload ""\\fs01\TrackVice\Contoso"" /alert ""https://x.example/h%22 /update %22\\evil\share""", evil, "Task sequence command: a quote in the alert stays in the link");
            T.Eq(4, evil.Split('"').Length - 1,"Task sequence command: only the quotes around the two values");
            T.Check(!ShareStorage.TaskSequenceCommand("\\\\fs01\\a\" /update \"\\\\evil\\b", "").Contains("\" /update \""), "Task sequence command: a quote in the share does not end it");
            // The command is what the agent already takes: /upload with a folder.
            AgentOptions o = new AgentOptions();
            o.Upload = Share;
            T.Check(Stores.Open(Share) is FolderStore, "Share: the agent opens the share as a folder store");
            // The intune script body carries the content.
            Dictionary<string, object> body = IntuneScripts.ScriptBody("TrackVice agent", "Install-TrackViceAgent.ps1", withKey);
            string content = Encoding.UTF8.GetString(Convert.FromBase64String((string)body["scriptContent"]));
            T.Check(content.Contains("$Upload    = '" + Share + "'"), "Share script: the Intune platform script has the share");
            T.Check(AgentScriptTemplate.Share.Contains("$Download  = ''") && AgentScriptTemplate.Share.Contains("$UpdateLink = ''"), "Share template keeps its values");
            string file = Path.Combine("agent", "Install-TrackViceAgent-Share.ps1");
            T.Check(File.Exists(file) && File.ReadAllText(file).Replace("\r\n", "\n") == AgentScriptTemplate.Share.Replace("\r\n", "\n"), "Share template: the copy in the exe is the same as agent/Install-TrackViceAgent-Share.ps1");
        }

        static void Saving()
        {
            MemoryCustomerStorage st = new MemoryCustomerStorage();
            CustomerBook b = new CustomerBook(st, new Plain());
            Customer c = new Customer();
            c.Name = "Contoso";
            c.Tenant = "contoso.onmicrosoft.com";
            b.Add(c);
            NewCustomerRun run = new NewCustomerRun();
            run.UseShare = true;
            run.Share = Share;
            Customer d = b.Find("Contoso").Copy();
            d.AgentLocation = run.UploadLocation;
            b.Update("Contoso", d);
            b.Save();
            CustomerBook r = new CustomerBook(st, new Plain());
            T.Check(r.Load(), "Share customer: read back");
            T.Eq(Share, r.Find("Contoso").AgentLocation, "Share customer: the share is the agent location after a round trip");
            T.Eq("contoso.onmicrosoft.com", r.Find("Contoso").Tenant, "Share customer: tenant kept");
            // Edit customer shows the text as it is: it is only hidden for https links.
            T.Check(!r.Find("Contoso").AgentLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase), "Share customer: Edit customer shows the share as text, it only hides https links");
            T.Check(Stores.Open(r.Find("Contoso").AgentLocation) is FolderStore, "Share customer: the viewer lists sessions from a folder store");
            string v = VaultCustomers.ToValue(r.Find("Contoso"), null);
            T.Check(v.Contains("fs01") && v.Contains("TrackVice") && VaultCustomers.NameIn(v) == "Contoso", "Share customer: the Key Vault value holds the share");
        }

        static string Temp()
        {
            string d = Path.Combine(Path.GetTempPath(), "tv-share-" + Guid.NewGuid().ToString("N"));
            return d;
        }

        static void CheckAgainstFolder()
        {
            string dir = Temp();
            try
            {
                ShareCheck r = ShareStorage.Check(dir);
                T.Check(r.Ok, "Check: a folder that is made: " + r.Message);
                T.Check(Directory.Exists(dir) && r.Message.StartsWith("Made the folder"), "Check: the folder was made and says so");
                T.Eq(SharePathKind.Local, r.Kind, "Check: a local folder is flagged");
                T.Check(r.Warning.Contains("UNC"), "Check: a local folder warns that devices need a UNC path");
                string left = Directory.Exists(Path.Combine(dir, "sessions")) ? string.Join(",", Directory.GetFiles(Path.Combine(dir, "sessions"))) : "";
                T.Eq("", left, "Check: the test file and its .meta are deleted");
                ShareCheck again = ShareStorage.Check(dir + Path.DirectorySeparatorChar);
                T.Check(again.Ok && again.Message.StartsWith("Found the folder"), "Check: an existing folder is found: " + again.Message);
                // The folder store reads what the check made possible.
                FolderStore fs = new FolderStore(dir);
                fs.Put("sessions/x.json.gz", new byte[] { 1, 2, 3 }, null);
                T.Eq(1, fs.List("sessions").Count, "Check: the folder works as a store");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

            ShareCheck bad = ShareStorage.Check("not a path");
            T.Check(!bad.Ok && bad.Message.Contains("UNC"), "Check: a bad path is refused with the reason");
            // A file where the folder should be: the exact error comes back.
            string file = Temp();
            File.WriteAllText(file, "x");
            try
            {
                ShareCheck blocked = ShareStorage.Check(file);
                T.Check(!blocked.Ok && blocked.Message.Length > 20, "Check: a path that is a file fails with an error: " + blocked.Message);
            }
            finally { File.Delete(file); }
        }

        static void PublishToFolder()
        {
            string dir = Temp();
            try
            {
                NewCustomerRun run = new NewCustomerRun();
                run.UseShare = true;
                T.Check(!run.ShareChecked, "Share run: not checked at first");
                bool threw = false;
                try { run.UploadToShare(new byte[] { 1 }, "1.2.3", new MemoryKeyStore(), DateTime.UtcNow); }
                catch (SetupException) { threw = true; }
                T.Check(threw, "Share run: no upload before the check");
                T.Check(run.CheckShare(dir).Ok && run.ShareChecked, "Share run: checked");
                byte[] exe = Encoding.ASCII.GetBytes("MZ pretend TrackVice.exe");
                MemoryKeyStore keys = new MemoryKeyStore();
                run.UploadToShare(exe, "1.2.3", keys, new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc));
                string agent = Path.Combine(dir, "agent");
                T.Check(File.Exists(Path.Combine(agent, "TrackVice.exe")), "Upload: TrackVice.exe is in agent");
                T.Check(File.Exists(Path.Combine(agent, "TrackVice-1.2.3.exe")) && File.Exists(Path.Combine(agent, "latest.json")), "Upload: the signed version and latest.json are in agent");
                T.Check(run.UpdateKey.Length > 0 && keys.Blob != null, "Upload: a key was made for this PC");
                UpdateManifest m = UpdateManifest.Parse(File.ReadAllBytes(Path.Combine(agent, "latest.json")));
                T.Check(m.Verify(run.UpdateKey), "Upload: latest.json is signed with the key of this PC");
                T.Eq("1.2.3", m.Version, "Upload: version");
                T.Eq(AgentUpdate.Sha256Hex(exe), m.Sha256, "Upload: SHA-256 in the manifest");
                T.Eq(AgentUpdate.Sha256Hex(exe).ToUpperInvariant(), run.ExeSha256, "Upload: SHA-256 for the script");
                // An agent with the key and the share as its update source finds and accepts it.
                string stage = Path.Combine(dir, "stage");
                Directory.CreateDirectory(stage);
                UpdateCheck c = AgentUpdate.Check(Stores.Open(dir), run.UpdateKey, "1.0.0", stage);
                T.Check(c.Decision == UpdateDecision.Newer, "Upload: an agent updates from the share root: " + c.Message);
                // A second version over the first with the same key.
                run.UploadToShare(exe, "1.2.4", keys, DateTime.UtcNow);
                T.Eq("1.2.4", UpdateManifest.Parse(File.ReadAllBytes(Path.Combine(agent, "latest.json"))).Version, "Upload: a new version replaces latest.json");
                // Another PC with another key must not publish over it.
                threw = false;
                try { run.UploadToShare(exe, "1.2.5", new MemoryKeyStore(), DateTime.UtcNow); }
                catch (SetupException ex) { threw = ex.Message.Contains("another update key"); }
                T.Check(threw, "Upload: another key is refused");
                T.Check(run.Done.Exists(delegate (string s) { return s.Contains("TrackVice.exe and TrackVice 1.2.3"); }), "Upload: the summary names it");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void AzureUnchanged()
        {
            NewCustomerRun run = new NewCustomerRun();
            T.Check(!run.UseShare && run.Share.Length == 0, "Azure flow: a share is off by default");
            T.Check(!run.StorageReady, "Azure flow: storage is not ready before it is set up");
            run.ExeSha256 = "ABCD";
            run.Links = new AgentLinks();
            run.Links.Upload = "https://a.blob.core.windows.net/trackvice?sp=cw&sig=u";
            run.Links.Viewer = "https://a.blob.core.windows.net/trackvice?sp=rl&sig=v";
            run.Links.Download = "https://a.blob.core.windows.net/trackvice-agent/TrackVice.exe?sp=r&sig=d";
            run.Links.Publish = "https://a.blob.core.windows.net/trackvice-agent?sp=cw&sig=p";
            run.Links.UpdateRead = "https://a.blob.core.windows.net/trackvice-agent?sp=rl&sig=r";
            T.Eq(run.Links.Viewer, run.UploadLocation, "Azure flow: the viewer link is the upload location");
            T.Eq(run.Links.Publish, run.PublishLocation, "Azure flow: the publish link");
            string s = run.Script();
            T.Check(s.Contains("$Download  = '" + run.Links.Download + "'") && s.Contains("$Upload    = '" + run.Links.Upload + "'"), "Azure flow: the script has the links");
            T.Check(s.Contains("Invoke-WebRequest -Uri $Download") && s.Contains("Tls12"), "Azure flow: the links script is unchanged");
            T.Eq(AgentScript.Fill(AgentScriptTemplate.Links, run.Values()), s, "Azure flow: the links template is used");
            T.Check(s.Contains("$UpdateLink = ''"), "Azure flow: no update link without a key");
            run.UpdateKey = "K";
            T.Check(run.Script().Contains("$UpdateLink = '" + run.Links.UpdateRead + "'"), "Azure flow: the update link is the read link of the agent container");
        }
    }
}
