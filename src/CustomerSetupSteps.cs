namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // The values the guided setup writes into Install-TrackViceAgent.ps1.
    public sealed class ScriptValues
    {
        public string Download = "", Sha256 = "", Upload = "", Alert = "", UpdateKey = "", UpdateLink = "";
        public int KeepHours = 48, MaxDays = 30, MaxAgeHours = 24;
    }

    public static class AgentScript
    {
        public static string Quote(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        // Replaces the value on the line that sets $name, and keeps the comment after it.
        public static string Set(string script, string name, string literal)
        {
            Regex rx = new Regex(@"^(\$" + Regex.Escape(name) + @"\s*=\s*)('(?:[^']|'')*'|-?\d+)", RegexOptions.Multiline);
            MatchCollection m = rx.Matches(script);
            if (m.Count != 1) throw new SetupException("The script has no line that sets $" + name + ". Use the Install-TrackViceAgent.ps1 of this version of TrackVice.");
            Match x = m[0];
            Group v = x.Groups[2];
            return script.Substring(0, v.Index) + literal + script.Substring(v.Index + v.Length);
        }

        public static string Fill(string template, ScriptValues v)
        {
            string s = template;
            s = Set(s, "Download", Quote(v.Download));
            s = Set(s, "Sha256", Quote(v.Sha256));
            s = Set(s, "Upload", Quote(v.Upload));
            s = Set(s, "KeepHours", v.KeepHours.ToString(CultureInfo.InvariantCulture));
            s = Set(s, "MaxDays", v.MaxDays.ToString(CultureInfo.InvariantCulture));
            s = Set(s, "MaxAgeHours", v.MaxAgeHours.ToString(CultureInfo.InvariantCulture));
            s = Set(s, "Alert", Quote(v.Alert));
            s = Set(s, "UpdateKey", Quote(v.UpdateKey));
            s = Set(s, "UpdateLink", Quote(v.UpdateLink));
            return s;
        }

        public static string FileName(string customer)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in customer ?? "") if (char.IsLetterOrDigit(c) || c == '-' || c == '_') b.Append(c);
            return "Install-TrackViceAgent" + (b.Length > 0 ? "-" + b : "") + ".ps1";
        }
    }

    public sealed class DirectoryGroup
    {
        public string Id, Name;

        public override string ToString() { return Name; }
    }

    // Adds the filled script to Intune as a platform script and assigns it to a device group. The only step of the setup that changes Intune.
    public sealed class IntuneScripts
    {
        public const string ScriptScope = "https://graph.microsoft.com/DeviceManagementScripts.ReadWrite.All";
        public const string GroupScope = "https://graph.microsoft.com/Group.ReadBasic.All";
        public const string ScriptsUrl = "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts";
        public const string Description = "Installs the TrackVice agent during enrollment. It uploads the device's deployment progress to the trackvice storage container until 48 hours after the deployment is done, then removes itself.";

        readonly IWeb web;
        readonly Func<string> token;

        public IntuneScripts(IWeb web, Func<string> token)
        {
            this.web = web;
            this.token = token;
        }

        static SetupException Fail(WebReply r, string doing)
        {
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string msg = GraphClient.Clean(Http.Str(e, "message"));
            string text;
            if (r.Status == 401) text = "The Intune sign-in has expired. Sign in again.";
            else if (r.Status == 403) text = "Access denied: the account may not " + doing + ". It needs an Intune role that can add scripts, and consent for DeviceManagementScripts.ReadWrite.All and Group.ReadBasic.All.";
            else text = "Microsoft Graph could not " + doing + ".";
            if (!string.IsNullOrEmpty(msg)) text += " (" + msg + ")";
            else if (r.Status >= 400 && r.Status != 401 && r.Status != 403) text += " (HTTP " + r.Status + ")";
            return new SetupException(text, r.Status, Http.Str(e, "code"));
        }

        public static string SearchUrl(string text)
        {
            string t = (text ?? "").Replace("\"", "").Trim();
            return "https://graph.microsoft.com/v1.0/groups?$search=" + Uri.EscapeDataString("\"displayName:" + t + "\"") + "&$select=id,displayName&$orderby=displayName&$top=25";
        }

        public List<DirectoryGroup> FindGroups(string text)
        {
            if ((text ?? "").Trim().Length == 0) throw new SetupException("Enter a part of the group's name.");
            WebCall c = WebCall.Json("GET", SearchUrl(text), token(), null);
            // $search needs this header.
            c.Headers["ConsistencyLevel"] = "eventual";
            WebReply r = web.Send(c);
            if (r.Status != 200) throw Fail(r, "search the groups");
            List<DirectoryGroup> l = new List<DirectoryGroup>();
            List<object> v = ImeJson.Get(r.Json, "value") as List<object>;
            if (v != null)
                foreach (object o in v)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    DirectoryGroup g = new DirectoryGroup();
                    g.Id = Http.Str(d, "id");
                    g.Name = Http.Str(d, "displayName") ?? g.Id;
                    if (!string.IsNullOrEmpty(g.Id)) l.Add(g);
                }
            return l;
        }

        public static Dictionary<string, object> ScriptBody(string displayName, string fileName, string content)
        {
            return SetupJson.Obj(
                "@odata.type", "#microsoft.graph.deviceManagementScript",
                "displayName", displayName,
                "description", Description,
                "scriptContent", Convert.ToBase64String(new UTF8Encoding(false).GetBytes(content)),
                "runAsAccount", "system",
                "enforceSignatureCheck", false,
                "runAs32Bit", false,
                "fileName", fileName);
        }

        // Returns the ID of the new platform script.
        public string Create(string displayName, string fileName, string content)
        {
            WebReply r = web.Send(WebCall.Json("POST", ScriptsUrl, token(), SetupJson.Write(ScriptBody(displayName, fileName, content))));
            if (r.Status != 201 && r.Status != 200) throw Fail(r, "add the platform script");
            string id = Http.Str(r.Json, "id");
            if (string.IsNullOrEmpty(id)) throw new SetupException("Intune did not return the ID of the new script.");
            return id;
        }

        public static Dictionary<string, object> AssignBody(string groupId)
        {
            return SetupJson.Obj("deviceManagementScriptAssignments", SetupJson.List(
                SetupJson.Obj("target", SetupJson.Obj("@odata.type", "#microsoft.graph.groupAssignmentTarget", "groupId", groupId))));
        }

        public void Assign(string scriptId, string groupId)
        {
            WebReply r = web.Send(WebCall.Json("POST", ScriptsUrl + "/" + Uri.EscapeDataString(scriptId) + "/assign", token(), SetupJson.Write(AssignBody(groupId))));
            if (r.Status != 204 && r.Status != 200) throw Fail(r, "assign the platform script");
        }
    }

    // What the guided setup chose and made, step by step. The window shows it; nothing in it is the account key.
    public sealed class NewCustomerRun
    {
        public string Customer = "", Tenant = "";
        public AzureSubscription Subscription;
        public string ResourceGroup = "", Region = Arm.DefaultRegion, AccountName = "";
        public bool NewGroup, NewAccount = true;
        public int RetentionDays = 30;
        public AzureAccount Account;
        public bool StorageDone;
        public AgentLinks Links;
        public int LinkDays = 365;
        public string ExePath = "", ExeSha256 = "";
        public long ExeSize;
        public bool DownloadChecked;
        public string Alert = "";
        public bool AlertTested;
        public int MaxAgeHours = 24;
        public string UpdateKey = "";
        public string ScriptPath = "";
        public string IntuneScriptId = "";
        public DirectoryGroup Group;
        public readonly List<string> Done = new List<string>();

        public bool StorageReady
        {
            get { return StorageDone && Account != null; }
        }

        // Creates what is missing: the resource group, the account, both containers and the lifecycle rule.
        public void SetUpStorage(Arm arm, Action<string> progress)
        {
            if (Subscription == null) throw new SetupException("Pick a subscription.");
            if (NewAccount)
            {
                if (ResourceGroup.Trim().Length == 0) throw new SetupException("Pick or enter a resource group.");
                if (NewGroup)
                {
                    progress("Creating the resource group " + ResourceGroup + "...");
                    arm.CreateResourceGroup(Subscription.Id, ResourceGroup, Region);
                    Done.Add("Resource group " + ResourceGroup + " in " + Region);
                    NewGroup = false;
                }
            }
            if (NewAccount && Account == null)
            {
                progress("Creating the storage account " + AccountName + ". This takes up to a minute...");
                string problem = arm.NameProblem(Subscription.Id, AccountName);
                if (problem != null) throw new SetupException(problem);
                Account = arm.CreateAccount(Subscription.Id, ResourceGroup, AccountName, Region);
                Done.Add("Storage account " + Account.Name + ": StorageV2, Standard_LRS, HTTPS only, TLS 1.2, no anonymous access");
            }
            else if (Account == null) throw new SetupException("Pick a storage account.");
            foreach (string c in new string[] { Arm.Sessions, Arm.AgentFiles })
            {
                progress("Creating the container " + c + "...");
                bool made = arm.CreateContainer(Account, c);
                Done.Add("Container " + c + (made ? "" : ", there already"));
            }
            progress("Adding the lifecycle rule...");
            arm.SetCleanup(Account, RetentionDays);
            StorageDone = true;
            Done.Add("Lifecycle rule " + Arm.RuleName + ": deletes " + Arm.Sessions + "/sessions/ and " + Arm.Sessions + "/logs/ after " + RetentionDays + " days");
        }

        public void MakeLinks(Arm arm, DateTime nowUtc)
        {
            if (Account == null) throw new SetupException("Set up the storage first.");
            string key = arm.Key(Account);
            Links = AgentLinks.Make(Account, key, nowUtc, LinkDays);
            key = null;
            Done.Add("Links valid until " + Links.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        public void Upload(IWeb web, byte[] exe)
        {
            if (Links == null) throw new SetupException("Make the links first.");
            ExeSha256 = AgentUpdate.Sha256Hex(exe).ToUpperInvariant();
            ExeSize = exe.Length;
            BlobUpload.Put(web, Links.Publish, "TrackVice.exe", exe);
            DownloadChecked = BlobUpload.CanRead(web, Links.Download);
            Done.Add("TrackVice.exe in " + Arm.AgentFiles + ", SHA-256 " + ExeSha256);
        }

        public ScriptValues Values()
        {
            ScriptValues v = new ScriptValues();
            if (Links != null)
            {
                v.Download = Links.Download;
                v.Upload = Links.Upload;
                if (UpdateKey.Trim().Length > 0) v.UpdateLink = Links.UpdateRead;
            }
            v.Sha256 = ExeSha256;
            v.Alert = Alert.Trim();
            v.MaxAgeHours = MaxAgeHours;
            v.UpdateKey = UpdateKey.Trim();
            return v;
        }

        public string Script()
        {
            return AgentScript.Fill(AgentScriptTemplate.Links, Values());
        }

        public void AddToIntune(IntuneScripts intune)
        {
            if (Group == null) throw new SetupException("Search for the device group and pick it.");
            string content = Script();
            if (IntuneScriptId.Length == 0)
            {
                IntuneScriptId = intune.Create("TrackVice agent", "Install-TrackViceAgent.ps1", content);
                Done.Add("Intune platform script TrackVice agent");
            }
            intune.Assign(IntuneScriptId, Group.Id);
            Done.Add("Assigned to the device group " + Group.Name);
        }
    }

    // Answers every request of the guided setup with made-up data, for the demo and for screenshots.
    public sealed class DemoSetupWeb : IWeb
    {
        public const string Sub = "4f6c2e1a-9b3d-4c7e-8a51-2d9f0b6e3c71";
        public readonly List<WebCall> Calls = new List<WebCall>();

        static string Jwt()
        {
            string p = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"upn\":\"admin@northwind.onmicrosoft.com\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "eyJhbGciOiJub25lIn0." + p + ".demo";
        }

        static string Account(string name, string group)
        {
            return "{\"id\":\"/subscriptions/" + Sub + "/resourceGroups/" + group + "/providers/Microsoft.Storage/storageAccounts/" + name + "\",\"name\":\"" + name
                + "\",\"location\":\"westeurope\",\"kind\":\"StorageV2\",\"properties\":{\"provisioningState\":\"Succeeded\",\"primaryEndpoints\":{\"blob\":\"https://" + name + ".blob.core.windows.net/\"}}}";
        }

        public WebReply Send(WebCall c)
        {
            Calls.Add(c);
            string u = c.Url;
            if (u.Contains("/oauth2/v2.0/devicecode"))
                return new WebReply(200, "{\"device_code\":\"demo\",\"user_code\":\"CQ7D-4KPM\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"interval\":0,\"expires_in\":900}");
            if (u.Contains("/oauth2/v2.0/token"))
                return new WebReply(200, "{\"access_token\":\"" + Jwt() + "\",\"refresh_token\":\"demo\",\"expires_in\":3600}");
            if (u.StartsWith(Arm.Base + "/subscriptions?"))
                return new WebReply(200, "{\"value\":[{\"subscriptionId\":\"" + Sub + "\",\"displayName\":\"Northwind Production\",\"state\":\"Enabled\"},{\"subscriptionId\":\"8a2e5c19-3f7b-4d60-b1e4-7c9a0d2f5b38\",\"displayName\":\"Northwind Test\",\"state\":\"Enabled\"}]}");
            if (u.Contains("/resourcegroups?"))
                return new WebReply(200, "{\"value\":[{\"name\":\"rg-intune\",\"location\":\"westeurope\"},{\"name\":\"rg-monitoring\",\"location\":\"northeurope\"}]}");
            if (u.Contains("/resourcegroups/") && c.Method == "PUT") return new WebReply(201, "{}");
            if (u.Contains("/checkNameAvailability")) return new WebReply(200, "{\"nameAvailable\":true}");
            if (u.Contains("/providers/Microsoft.Storage/storageAccounts?"))
                return new WebReply(200, "{\"value\":[" + Account("northwindlogs", "rg-monitoring") + "]}");
            if (u.Contains("/blobServices/default/containers/")) return new WebReply(201, "{}");
            if (u.Contains("/managementPolicies/default")) return c.Method == "GET" ? new WebReply(404, "{}") : new WebReply(200, "{}");
            if (u.Contains("/listKeys"))
                return new WebReply(200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("made-up key for the TrackVice demo, not a real one")) + "\"}]}");
            Match m = Regex.Match(u, "/storageAccounts/([a-z0-9]+)\\?", RegexOptions.IgnoreCase);
            if (m.Success && (c.Method == "PUT" || c.Method == "GET")) return new WebReply(200, Account(m.Groups[1].Value, Regex.Match(u, "/resourceGroups/([^/]+)/").Groups[1].Value));
            if (u.Contains(".blob.core.windows.net/")) return new WebReply(c.Method == "PUT" ? 201 : 200, "");
            if (u.Contains("graph.microsoft.com/v1.0/groups"))
                return new WebReply(200, "{\"value\":[{\"id\":\"b3d1f6a2-5c8e-4e09-9a7b-1f2c3d4e5f60\",\"displayName\":\"Autopilot devices\"},{\"id\":\"c7e2a9b4-1d3f-4a68-8b5c-6e0f1a2b3c4d\",\"displayName\":\"Autopilot test devices\"}]}");
            if (u.EndsWith("/assign")) return new WebReply(204, "");
            if (u.StartsWith(IntuneScripts.ScriptsUrl)) return new WebReply(201, "{\"id\":\"5e9b1c3d-7a2f-4b86-9d0e-3f4a5b6c7d8e\"}");
            return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"The demo has no answer for this.\"}}");
        }
    }
}
