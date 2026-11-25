namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using TrackVice;

    public static class IntuneScriptCheckTests
    {
        sealed class Web : IWeb
        {
            public readonly List<WebCall> Calls = new List<WebCall>();
            public readonly List<KeyValuePair<string, KeyValuePair<int, string>>> Answers = new List<KeyValuePair<string, KeyValuePair<int, string>>>();

            public void On(string urlPart, int status, string body)
            {
                Answers.Add(new KeyValuePair<string, KeyValuePair<int, string>>(urlPart, new KeyValuePair<int, string>(status, body)));
            }

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                foreach (KeyValuePair<string, KeyValuePair<int, string>> a in Answers)
                    if (c.Url.Contains(a.Key)) return new WebReply(a.Value.Key, a.Value.Value);
                return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"none\"}}");
            }

            public WebCall Find(string method, string part)
            {
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(part)) return c;
                return null;
            }
        }

        const string Dev = "11111111-1111-1111-1111-111111111111";
        const string Usr = "22222222-2222-2222-2222-222222222222";
        const string Dyn = "33333333-3333-3333-3333-333333333333";
        const string Ex = "44444444-4444-4444-4444-444444444444";

        static GroupFacts G(string id, string name, bool? users)
        {
            GroupFacts g = new GroupFacts();
            g.Id = id;
            g.Name = name;
            g.HasUsers = users;
            return g;
        }

        static ScriptAssignment A(TargetKind k, string group)
        {
            ScriptAssignment a = new ScriptAssignment();
            a.Kind = k;
            a.GroupId = group ?? "";
            return a;
        }

        static ScriptSettings S(string runAs)
        {
            ScriptSettings s = new ScriptSettings();
            s.Id = "s1";
            s.Name = "TrackVice agent";
            s.RunAsAccount = runAs;
            return s;
        }

        public static void Run()
        {
            Rules();
            Evaluate();
            Graph();
        }

        static void Rules()
        {
            T.Eq(true, ScriptChecker.RuleHasUsers("(user.department -eq \"IT\")"), "script check: a user rule has users");
            T.Eq(true, ScriptChecker.RuleHasUsers("(device.deviceOwnership -eq \"Company\") and (user.city -eq \"x\")"), "script check: a mixed rule has users");
            T.Eq(false, ScriptChecker.RuleHasUsers("(device.devicePhysicalIds -any (_ -contains \"[ZTDId]\"))"), "script check: a device rule has no users");
            T.Check(ScriptChecker.RuleHasUsers("") == null, "script check: an empty rule is not known");
            T.Eq(false, ScriptChecker.RuleHasUsers("(device.displayName -startsWith \"superuser.x\")"), "script check: user. inside a word is not a user property");
        }

        static void Evaluate()
        {
            Dictionary<string, GroupFacts> groups = new Dictionary<string, GroupFacts>();
            groups[Dev] = G(Dev, "Autopilot devices", false);
            groups[Usr] = G(Usr, "All staff", true);
            groups[Ex] = G(Ex, "Kiosks", false);
            groups[Dyn] = G(Dyn, "Mystery", null);

            ScriptCheck ok = ScriptChecker.Evaluate(S("system"), new List<ScriptAssignment> { A(TargetKind.Group, Dev) }, groups);
            T.Check(!ok.RunsAsUser && !ok.UsersTargeted, "script check: system and a device group is fine");
            T.Check(ok.Text.Contains("Runs as: System.") && ok.Text.Contains("Assigned to: Autopilot devices (devices only)."), "script check: plain lines for a device group");
            T.Check(!ok.Text.Contains(ScriptChecker.UsersLine) && !ok.CanFix, "script check: no warning, no fix");

            ScriptCheck bad = ScriptChecker.Evaluate(S("system"), new List<ScriptAssignment> { A(TargetKind.Group, Dev), A(TargetKind.Group, Usr), A(TargetKind.AllUsers, null), A(TargetKind.Exclusion, Ex) }, groups);
            T.Check(bad.UsersTargeted && bad.CanFix, "script check: users targeted and a device group stays");
            T.Check(bad.Text.Contains("Assigned to: All staff (contains users).") && bad.Text.Contains("Assigned to: All users (users).") && bad.Text.Contains("Excluded: Kiosks."), "script check: user, all users and exclusion lines");
            T.Check(bad.Text.Contains(ScriptChecker.UsersLine), "script check: the sentence for users");
            T.Eq("Assign only to Autopilot devices", bad.FixButton, "script check: the button names the device group");
            T.Eq(2, bad.UserTargets.Count, "script check: two user targets go");
            T.Eq(2, bad.Keep.Count, "script check: the device group and the exclusion stay");
            T.Check(bad.FixQuestion.Contains("Removed: All staff, All users") && bad.FixQuestion.Contains("Kept: Autopilot devices"), "script check: the question shows what changes");

            ScriptCheck user = ScriptChecker.Evaluate(S("user"), new List<ScriptAssignment> { A(TargetKind.AllDevices, null) }, groups);
            T.Check(user.RunsAsUser && user.Text.Contains("Runs as: the signed-in user."), "script check: runs as the user");
            T.Check(user.Text.Contains("Assigned to: All devices (devices only)."), "script check: all devices");

            ScriptCheck onlyUsers = ScriptChecker.Evaluate(S("system"), new List<ScriptAssignment> { A(TargetKind.AllUsers, null) }, groups);
            T.Check(onlyUsers.UsersTargeted && !onlyUsers.CanFix, "script check: no device assignment to keep, no fix");

            ScriptCheck unknown = ScriptChecker.Evaluate(S("system"), new List<ScriptAssignment> { A(TargetKind.Group, Dyn), A(TargetKind.Group, "gone") }, groups);
            T.Check(unknown.Text.Contains("Mystery (could not tell if it contains users)") && unknown.Text.Contains("the group was not read"), "script check: unknown groups are said, not guessed");
            T.Check(!unknown.UsersTargeted, "script check: unknown is not removed");

            ScriptCheck none = ScriptChecker.Evaluate(S("system"), new List<ScriptAssignment>(), groups);
            T.Check(none.Text.Contains("Assigned to: nothing."), "script check: no assignments");

            string body = SetupJson.Write(ScriptChecker.AssignBody(bad.Keep));
            T.Check(body.Contains("groupAssignmentTarget") && body.Contains(Dev) && body.Contains("exclusionGroupAssignmentTarget") && !body.Contains(Usr) && !body.Contains("allLicensedUsers"), "script check: the assign body keeps only the device group and the exclusion");
            T.Check(SetupJson.Write(ScriptChecker.AssignBody(user.Keep)).Contains("allDevicesAssignmentTarget"), "script check: all devices is kept as all devices");
        }

        static Web Tenant()
        {
            Web w = new Web();
            w.On("deviceManagementScripts?$filter=", 200, "{\"value\":[{\"id\":\"s1\",\"displayName\":\"TrackVice agent\",\"runAsAccount\":\"system\",\"enforceSignatureCheck\":false,\"runAs32Bit\":false}]}");
            w.On("/s1/assignments", 200, "{\"value\":[" +
                "{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"" + Dev + "\"}}," +
                "{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"" + Usr + "\"}}," +
                "{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"" + Dyn + "\"}}," +
                "{\"target\":{\"@odata.type\":\"#microsoft.graph.allLicensedUsersAssignmentTarget\"}}]}");
            w.On("/s1/groupAssignments", 200, "{\"value\":[{\"targetGroupId\":\"" + Dev + "\"}]}");
            w.On("groups/" + Dev + "/members/microsoft.graph.user/$count", 200, "0");
            w.On("groups/" + Usr + "/members/microsoft.graph.user/$count", 200, "3");
            w.On("groups/" + Dev + "?", 200, "{\"displayName\":\"Autopilot devices\",\"groupTypes\":[],\"membershipRule\":null}");
            w.On("groups/" + Usr + "?", 200, "{\"displayName\":\"All staff\",\"groupTypes\":[]}");
            w.On("groups/" + Dyn + "?", 200, "{\"displayName\":\"Dynamic users\",\"groupTypes\":[\"DynamicMembership\"],\"membershipRule\":\"(user.accountEnabled -eq true)\"}");
            w.On("/s1/assign", 204, "");
            return w;
        }

        static void Graph()
        {
            Web w = Tenant();
            ScriptCheckApi api = new ScriptCheckApi(w, delegate { return "tok"; }, delegate { return "gtok"; });
            ScriptCheck c = api.Check("TrackVice agent");
            T.Check(c.Text.Contains("Runs as: System.") && c.Text.Contains("Autopilot devices (devices only)") && c.Text.Contains("All staff (contains users)") && c.Text.Contains("Dynamic users (contains users)") && c.Text.Contains("All users (users)"), "script check graph: the findings of the tenant");
            T.Eq(1, w.Calls.FindAll(delegate (WebCall x) { return x.Url.Contains("groups/" + Dev + "?"); }).Count, "script check graph: a group in both lists is read once");
            WebCall count = w.Find("GET", "groups/" + Usr + "/members/microsoft.graph.user/$count");
            T.Check(count != null && count.Headers["ConsistencyLevel"] == "eventual", "script check graph: the member count asks with ConsistencyLevel eventual");
            T.Check(w.Find("GET", "groups/" + Dyn + "/members") == null, "script check graph: a dynamic group is judged by its rule");
            T.Check(w.Find("GET", "groups/" + Dev + "?").Headers["Authorization"] == "Bearer gtok" && w.Find("GET", "/s1/assignments").Headers["Authorization"] == "Bearer tok", "script check graph: groups use the group token, the script the script token");
            T.Check(w.Find("GET", "$filter=").Url.Contains("displayName%20eq%20%27TrackVice%20agent%27"), "script check graph: the script is found by its name");
            T.Check(w.Calls.TrueForAll(delegate (WebCall x) { return x.Method == "GET"; }), "script check graph: the check only reads");

            api.Fix(c);
            WebCall post = w.Find("POST", "/s1/assign");
            T.Check(post != null && post.Text.Contains(Dev) && !post.Text.Contains(Usr) && !post.Text.Contains(Dyn) && !post.Text.Contains("allLicensedUsers"), "script check graph: the fix assigns only the device group");

            // No permission for groups: said, and the rest is checked.
            Web noGroups = Tenant();
            ScriptCheck n = new ScriptCheckApi(noGroups, delegate { return "tok"; }, null).Check("TrackVice agent");
            T.Check(n.Text.Contains("GroupMember.Read.All") && n.Text.Contains("Runs as: System.") && !n.UsersTargeted == false, "script check graph: without the group permission the groups are not guessed");
            T.Check(!n.CanFix || n.UsersTargeted, "script check graph: all users still counts");

            Web denied = Tenant();
            denied.Answers.Insert(0, new KeyValuePair<string, KeyValuePair<int, string>>("groups/" + Usr + "?", new KeyValuePair<int, string>(403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"x\"}}")));
            ScriptCheck d = new ScriptCheckApi(denied, delegate { return "tok"; }, delegate { return "g"; }).Check("TrackVice agent");
            T.Check(d.Text.Contains("could not tell if it contains users: TrackVice has no permission to read groups"), "script check graph: a refused group is said");

            // The $count is refused: one user is asked for.
            Web top = Tenant();
            top.Answers.Insert(0, new KeyValuePair<string, KeyValuePair<int, string>>("groups/" + Usr + "/members/microsoft.graph.user/$count", new KeyValuePair<int, string>(400, "")));
            top.On("groups/" + Usr + "/members/microsoft.graph.user?$top=1", 200, "{\"value\":[{\"id\":\"u\"}]}");
            T.Check(new ScriptCheckApi(top, delegate { return "t"; }, delegate { return "g"; }).Check("TrackVice agent").Text.Contains("All staff (contains users)"), "script check graph: one user asked for when the count fails");

            Web missing = new Web();
            missing.On("deviceManagementScripts?$filter=", 200, "{\"value\":[]}");
            string msg = null;
            try { new ScriptCheckApi(missing, delegate { return "t"; }, null).Check("TrackVice agent"); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.Contains("no platform script called TrackVice agent"), "script check graph: no script is said");
        }
    }
}
