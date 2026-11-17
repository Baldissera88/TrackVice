namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // Add customer: the question for a name that exists, the assignment of the platform script, and the message of the script.
    public static class AddCustomerGroupTests
    {
        sealed class FakeWeb : IWeb
        {
            public readonly List<WebCall> Calls = new List<WebCall>();
            public readonly List<Func<WebCall, WebReply>> Handlers = new List<Func<WebCall, WebReply>>();

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                foreach (Func<WebCall, WebReply> h in Handlers)
                {
                    WebReply r = h(c);
                    if (r != null) return r;
                }
                return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"no fake answer\"}}");
            }

            public void On(string method, string urlPart, int status, string body)
            {
                Handlers.Add(delegate (WebCall c) { return c.Method == method && c.Url.Contains(urlPart) ? new WebReply(status, body) : null; });
            }

            public int Count(string method, string urlPart)
            {
                int n = 0;
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(urlPart)) n++;
                return n;
            }

            public WebCall Last(string method, string urlPart)
            {
                for (int i = Calls.Count - 1; i >= 0; i--) if (Calls[i].Method == method && Calls[i].Url.Contains(urlPart)) return Calls[i];
                return null;
            }
        }

        const string Chosen = "7e3d1a90-5b2c-4d8e-9f10-2a3b4c5d6e01";
        const string Other = "7e3d1a90-5b2c-4d8e-9f10-2a3b4c5d6e02";
        const string NewScript = "7e3d1a90-5b2c-4d8e-9f10-2a3b4c5d6e03";

        public static void Run()
        {
            ExistingNames();
            SearchRequest();
            SearchAnswer();
            AssignToChosen();
            AssignNowhere();
            Plans();
            ScriptMessage();
        }

        static void ExistingNames()
        {
            CustomerBook book = CustomerBook.Demo();
            T.Check(ExistingName.MustAsk(book, "contoso", ""), "existing name: asked for a customer that exists, ignoring case");
            T.Check(ExistingName.MustAsk(book, " Contoso ", null), "existing name: spaces around the name are left out");
            T.Check(!ExistingName.MustAsk(book, "Northwind Traders", ""), "existing name: a new name is not asked");
            T.Check(!ExistingName.MustAsk(book, "CONTOSO", "Contoso"), "existing name: not asked again after Update");
            T.Check(ExistingName.MustAsk(book, "Fabrikam", "Contoso"), "existing name: asked again for another customer that exists");
            T.Eq("Contoso", ExistingName.Find(book, "CONTOSO").Name, "existing name: found ignoring case");
            T.Eq("Contoso already exists. Update Contoso: new links are made and the old ones stop, so update the script in Intune too.", ExistingName.Question("Contoso"), "existing name: the question");
            T.Eq("Update Contoso", ExistingName.UpdateButton("Contoso"), "existing name: the update button");
            T.Eq("Choose another name", ExistingName.ChooseAnother, "existing name: the other button");
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            T.Eq(null, r.Problem(book), "existing name: still a valid name for an update");
        }

        static void SearchRequest()
        {
            string u = GroupSearch.Url("Test devices");
            T.Check(u.StartsWith("https://graph.microsoft.com/v1.0/groups?"), "group search: GET /groups");
            T.Check(u.Contains("$search=" + Uri.EscapeDataString("\"displayName:Test devices\"")), "group search: $search on displayName, in double quotes");
            T.Check(u.Contains("$filter=" + Uri.EscapeDataString("securityEnabled eq true")), "group search: security groups only");
            T.Check(u.Contains("$select=id,displayName,groupTypes"), "group search: reads the member type");
            T.Check(u.Contains("$top=50") && GroupSearch.Max == 50, "group search: at most 50");
            T.Check(u.Contains("$count=true") && u.Contains("$orderby=displayName"), "group search: sorted by name, with $count for the advanced query");
            T.Check(!GroupSearch.Url("  ").Contains("$search"), "group search: empty text lists without $search");
            T.Check(GroupSearch.Url("a\"b").Contains(Uri.EscapeDataString("\"displayName:a\\\"b\"")), "group search: a double quote is escaped");
            T.Check(GroupSearch.Url("R&D").Contains("R%26D"), "group search: other characters are URL encoded");
            WebCall c = GroupSearch.Call("Test", "token");
            T.Eq("GET", c.Method, "group search: GET");
            T.Eq("eventual", c.Headers["ConsistencyLevel"], "group search: ConsistencyLevel eventual");
            T.Eq("Bearer token", c.Headers["Authorization"], "group search: the token");
            T.Check(IntuneSetup.Scopes.Contains("Group.ReadWrite.All"), "group search: the sign-in scope Group.ReadWrite.All also lists groups");
        }

        static void SearchAnswer()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", IntuneSetup.GroupsUrl + "?", 200, "{\"value\":[" +
                "{\"id\":\"" + Chosen + "\",\"displayName\":\"Contoso test devices\",\"groupTypes\":[]}," +
                "{\"id\":\"" + Other + "\",\"displayName\":\"Contoso Autopilot devices\",\"groupTypes\":[\"Unified\",\"DynamicMembership\"]}," +
                "{\"displayName\":\"No id\"}]}");
            List<TenantGroup> l = GroupSearch.Find(w, delegate { return "token"; }, "Contoso");
            T.Eq(2, l.Count, "group search: groups without an id are left out");
            T.Eq("Contoso test devices", l[0].Name, "group search: name");
            T.Eq("Assigned", l[0].Members, "group search: assigned members");
            T.Eq("Dynamic", l[1].Members, "group search: dynamic members");
            T.Eq(1, w.Calls.Count, "group search: one request");

            StringBuilder b = new StringBuilder("{\"value\":[");
            for (int i = 0; i < 60; i++) b.Append(i > 0 ? "," : "").Append("{\"id\":\"g" + i + "\",\"displayName\":\"Group " + i + "\"}");
            b.Append("]}");
            T.Eq(50, GroupSearch.Parse(new WebReply(200, b.ToString())).Count, "group search: at most 50 shown");

            FakeWeb denied = new FakeWeb();
            denied.On("GET", IntuneSetup.GroupsUrl, 403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges\"}}");
            string msg = null;
            try { GroupSearch.Find(denied, delegate { return "token"; }, "x"); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.StartsWith("Access denied: the account may not search the groups"), "group search: access denied says so");
        }

        static FakeWeb Intune(bool scriptThere)
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", IntuneScripts.ScriptsUrl + "?$select", 200, scriptThere
                ? "{\"value\":[{\"id\":\"" + NewScript + "\",\"displayName\":\"TrackVice agent\"}]}"
                : "{\"value\":[]}");
            w.On("GET", "/assignments", 200, "{\"value\":[{\"target\":{\"groupId\":\"" + Other + "\"}}]}");
            w.On("PATCH", IntuneScripts.ScriptsUrl + "/", 204, "");
            w.On("POST", "/assign", 204, "");
            w.On("POST", IntuneScripts.ScriptsUrl, 201, "{\"id\":\"" + NewScript + "\"}");
            return w;
        }

        static AddCustomerRun NewRun()
        {
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.Script = "# made-up script";
            return r;
        }

        static TenantGroup Group()
        {
            TenantGroup g = new TenantGroup();
            g.Id = Chosen;
            g.Name = "Contoso test devices";
            return g;
        }

        static void AssignToChosen()
        {
            FakeWeb w = Intune(false);
            AddCustomerRun r = NewRun();
            r.Target = ScriptTarget.ChosenGroup;
            r.Chosen = Group();
            T.Eq(null, r.CreateProblem(true), "chosen group: Create can start");
            r.ToIntune(new IntuneSetup(w, delegate { return "token"; }), delegate { return true; });
            T.Eq(0, w.Count("POST", IntuneSetup.GroupsUrl), "chosen group: no group is made");
            T.Eq(0, w.Count("GET", IntuneSetup.GroupsUrl), "chosen group: the group TrackVice - Autopilot devices is not looked for");
            WebCall a = w.Last("POST", "/assign");
            T.Check(a != null && a.Url.Contains(NewScript), "chosen group: the script is assigned");
            T.Check(a.Text.Contains("\"groupId\":\"" + Chosen + "\"") && a.Text.Contains("#microsoft.graph.groupAssignmentTarget"), "chosen group: to the chosen group id");
            T.Check(r.Done.Contains("Assigned to Contoso test devices"), "chosen group: the result names the group");

            FakeWeb w2 = Intune(true);
            AddCustomerRun r2 = NewRun();
            r2.Target = ScriptTarget.ChosenGroup;
            r2.Chosen = Group();
            r2.ToIntune(new IntuneSetup(w2, delegate { return "token"; }), delegate { return false; });
            WebCall a2 = w2.Last("POST", "/assign");
            T.Check(a2.Text.Contains(Chosen) && a2.Text.Contains(Other), "chosen group: the assignments the script had are kept");

            AddCustomerRun r3 = NewRun();
            r3.Target = ScriptTarget.ChosenGroup;
            T.Check(r3.CreateProblem(true) != null, "chosen group: Create waits for a group");
            T.Eq(null, r3.CreateProblem(false), "chosen group: no group needed without Intune");
            FakeWeb w3 = Intune(false);
            bool threw = false;
            try { r3.ToIntune(new IntuneSetup(w3, delegate { return "token"; }), delegate { return true; }); }
            catch (SetupException) { threw = true; }
            T.Check(threw && w3.Calls.Count == 0, "chosen group: nothing is sent without a group");
        }

        static void AssignNowhere()
        {
            FakeWeb w = Intune(false);
            AddCustomerRun r = NewRun();
            r.Target = ScriptTarget.None;
            r.ToIntune(new IntuneSetup(w, delegate { return "token"; }), delegate { return true; });
            int added = 0;
            foreach (WebCall c in w.Calls) if (c.Method == "POST" && c.Url == IntuneScripts.ScriptsUrl) added++;
            T.Eq(1, added, "do not assign: the script is added");
            T.Eq(0, w.Count("POST", "/assign"), "do not assign: nothing is assigned");
            T.Eq(0, w.Count("GET", "/assignments"), "do not assign: the assignments are not read");
            T.Eq(0, w.Count("POST", IntuneSetup.GroupsUrl) + w.Count("GET", IntuneSetup.GroupsUrl), "do not assign: no group is read or made");
            T.Check(r.Group == null && r.IntuneDone, "do not assign: done without a group");
            T.Check(r.Done.Contains("Not assigned. Assign it in Intune"), "do not assign: the result says so");
            T.Check(!r.MakeGroup, "do not assign: MakeGroup is off");
            r.MakeGroup = true;
            T.Check(r.Target == ScriptTarget.NewGroup, "MakeGroup on is the new group");
        }

        static void Plans()
        {
            AddCustomerRun r = NewRun();
            T.Check(r.Target == ScriptTarget.NewGroup, "plan: the new group is the default");
            string p = string.Join("\n", r.Plan(null, true).ToArray());
            T.Check(p.Contains("dynamic device group TrackVice - Autopilot devices") && p.Contains("assigned to that group"), "plan: new group");
            r.Target = ScriptTarget.ChosenGroup;
            T.Check(string.Join("\n", r.Plan(null, true).ToArray()).Contains("assigned to the group you pick below"), "plan: chosen group, not picked yet");
            r.Chosen = Group();
            p = string.Join("\n", r.Plan(null, true).ToArray());
            T.Check(p.Contains("assigned to the group Contoso test devices. No group is made.") && !p.Contains("dynamic device group"), "plan: chosen group");
            r.Target = ScriptTarget.None;
            p = string.Join("\n", r.Plan(null, true).ToArray());
            T.Check(p.Contains("not assigned") && !p.Contains("dynamic device group"), "plan: not assigned");
            r.AddScript = false;
            T.Check(string.Join("\n", r.Plan(null, true).ToArray()).Contains("Nothing in Intune"), "plan: nothing in Intune without the script");
        }

        static void ScriptMessage()
        {
            string s = AgentScriptTemplate.Text;
            T.Check(s.Contains("throw \"`$Customer holds '$Customer', which looks like a customer name. Paste the customer code from TrackVice > Settings > Customers > Customer code..., which starts with tv1:\""), "script: a customer name gets its own message");
            T.Check(s.Contains("if ($Customer.Length -lt 60 -and $Customer -notmatch '://') {"), "script: a name is short and not a link");
            T.Check(s.Contains("throw 'This is not a TrackVice customer code. It starts with tv1:'"), "script: other values keep the message");
            int name = s.IndexOf("looks like a customer name", StringComparison.Ordinal);
            int tv1 = s.IndexOf("if (-not $Customer.StartsWith('tv1:'))", StringComparison.Ordinal);
            int empty = s.IndexOf("Fill in $Customer", StringComparison.Ordinal);
            T.Check(empty < tv1 && tv1 < name, "script: checked after the empty code and only for values without tv1:");
            foreach (char c in s) if (c > 127) { T.Check(false, "script: ASCII only, for Windows PowerShell 5.1"); break; }
        }
    }
}
