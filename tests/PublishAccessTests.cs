namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class PublishAccessTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        const string Acct = "trackvicemansoft2026dlb1";
        const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==";
        const string SubA = "11111111-1111-1111-1111-111111111111";
        const string SubB = "22222222-2222-2222-2222-222222222222";
        static readonly string AcctId = "/subscriptions/" + SubB + "/resourceGroups/rg-trackvice/providers/Microsoft.Storage/storageAccounts/" + Acct;

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

            public int Writes()
            {
                int n = 0;
                foreach (WebCall c in Calls) if (c.Method != "GET" && !c.Url.Contains("/listKeys")) n++;
                return n;
            }
        }

        static string Account(string name, string sub, bool keyOn)
        {
            return "{\"id\":\"/subscriptions/" + sub + "/resourceGroups/rg-trackvice/providers/Microsoft.Storage/storageAccounts/" + name + "\",\"name\":\"" + name + "\",\"location\":\"westeurope\",\"kind\":\"StorageV2\","
                + "\"properties\":{\"allowSharedKeyAccess\":" + (keyOn ? "true" : "false") + ",\"primaryEndpoints\":{\"blob\":\"https://" + name + ".blob.core.windows.net/\"}}}";
        }

        // Two subscriptions; the account of the customer is in the second one.
        static FakeWeb World(bool keyOn)
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", "/subscriptions?api-version", 200, "{\"value\":[{\"subscriptionId\":\"" + SubA + "\",\"displayName\":\"Dev\",\"state\":\"Enabled\"},{\"subscriptionId\":\"" + SubB + "\",\"displayName\":\"Mansoft Prod\",\"state\":\"Enabled\"}]}");
            w.On("GET", "/subscriptions/" + SubA + "/providers/Microsoft.Storage/storageAccounts", 200, "{\"value\":[" + Account("someotheraccount", SubA, true) + "]}");
            w.On("GET", "/subscriptions/" + SubB + "/providers/Microsoft.Storage/storageAccounts", 200, "{\"value\":[" + Account(Acct, SubB, keyOn) + "]}");
            return w;
        }

        static UpdateLocation Place()
        {
            UpdateCustomer c = new UpdateCustomer("Mansoft_2026_DLB1", "https://" + Acct + ".blob.core.windows.net/trackvice?sv=2022-11-02&sp=rl&sig=x", "", "");
            return UpdateLocations.Derive(new UpdateCustomer[] { c }, null, Now)[0];
        }

        static AccessResult Run(FakeWeb w, UpdateLocation p, bool create, List<string> saved)
        {
            Arm arm = new Arm(w, delegate { return "token"; });
            return PublishAccess.Get(arm, "daniel@example.com", p, create, Now, delegate (UpdateLocation l, string link) { if (saved != null) saved.Add(link); });
        }

        public static void Run()
        {
            Found();
            Forbidden();
            NotFound();
            NoContainer();
            KeyOff();
            LinkChecks();
        }

        static void Found()
        {
            FakeWeb w = World(true);
            w.On("GET", "/containers/trackvice-agent", 200, "{}");
            w.On("POST", AcctId + "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"value\":\"" + Key + "\",\"permissions\":\"FULL\"}]}");
            UpdateLocation p = Place();
            T.Check(!p.CanWrite && p.Account == Acct && p.Container == "trackvice-agent", "the place starts with no write link");
            List<string> saved = new List<string>();
            AccessResult r = Run(w, p, false, saved);
            T.Eq(AccessOutcome.Saved, r.Outcome, "account found with keys: saved (" + r.Message + ")");
            T.Check(p.CanWrite && p.WriteLink == r.Link && p.CanRead && p.Missing.Length == 0, "the place can write and read now");
            T.Eq(1, saved.Count, "the link is saved once");
            T.Check(saved.Count == 1 && saved[0] == r.Link, "the saved link is the one made");
            T.Check(UpdateLocations.WriteLinkProblem(p, r.Link, Now) == null, "the made link passes the same check as a pasted one");
            T.Check(r.Link.StartsWith("https://" + Acct + ".blob.core.windows.net/trackvice-agent?") && r.Link.Contains("sp=rcw") && r.Link.Contains("sr=c"), "a container link with Read, Create and Write");
            string expect = SasLinks.Container("https://" + Acct + ".blob.core.windows.net/", Acct, "trackvice-agent", "rcw", Now.AddMinutes(-15), Now.AddDays(365), Key);
            T.Eq(expect, r.Link, "signed with the account key, valid for a year");
            T.Check(r.Message.Contains("2027-11-25"), "the message says when it ends");
            T.Eq(0, w.Writes(), "nothing is changed in Azure");
            T.Eq(1, w.Count("POST", "/listKeys"), "the key is read once");
            T.Check(w.Count("GET", "/subscriptions/" + SubA + "/providers") == 1 && w.Count("GET", "/subscriptions/" + SubB + "/providers") == 1, "both subscriptions are searched");
            T.Check(w.Calls[0].Headers.Count >= 0 && w.Calls[0].Url.StartsWith("https://management.azure.com/"), "Azure Resource Manager is used");
        }

        static void Forbidden()
        {
            FakeWeb w = World(true);
            w.On("GET", "/containers/trackvice-agent", 200, "{}");
            w.On("POST", AcctId + "/listKeys", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client does not have authorization to perform action Microsoft.Storage/storageAccounts/listKeys/action\"}}");
            UpdateLocation p = Place();
            List<string> saved = new List<string>();
            AccessResult r = Run(w, p, false, saved);
            T.Eq(AccessOutcome.Manual, r.Outcome, "403 on listKeys: manual");
            T.Check(r.Message.Contains("Storage Account Contributor") && r.Message.Contains(AcctId) && r.Message.Contains("Mansoft Prod"), "the role and the scope are named");
            T.Check(r.Message.Contains("Shared access tokens") && r.Message.Contains("Read, Create and Write") && r.Message.Contains(Acct + " > Containers > trackvice-agent"), "the steps of the manual link follow");
            T.Check(!p.CanWrite && saved.Count == 0 && w.Writes() == 0, "nothing is saved or changed");
        }

        static void NotFound()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", "/subscriptions?api-version", 200, "{\"value\":[{\"subscriptionId\":\"" + SubA + "\",\"displayName\":\"Dev\",\"state\":\"Enabled\"},{\"subscriptionId\":\"" + SubB + "\",\"displayName\":\"Other\",\"state\":\"Enabled\"}]}");
            w.On("GET", "/subscriptions/" + SubA + "/providers", 200, "{\"value\":[" + Account("someotheraccount", SubA, true) + "]}");
            w.On("GET", "/subscriptions/" + SubB + "/providers", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"no\"}}");
            UpdateLocation p = Place();
            AccessResult r = Run(w, p, false, null);
            T.Eq(AccessOutcome.NotFound, r.Outcome, "account in no subscription: not found");
            T.Check(r.Message.Contains(Acct) && r.Message.Contains("2 subscriptions") && r.Message.Contains("1 could not be read") && r.Message.Contains("tenant"), "it says what was searched and the tenant");
            T.Check(r.Message.Contains("Shared access tokens") && w.Count("POST", "/listKeys") == 0 && !p.CanWrite, "the manual steps follow and no key is asked for");
        }

        static void NoContainer()
        {
            FakeWeb w = World(true);
            w.On("GET", "/containers/trackvice-agent", 404, "{\"error\":{\"code\":\"ContainerNotFound\",\"message\":\"not found\"}}");
            w.On("PUT", "/containers/trackvice-agent", 201, "{}");
            w.On("POST", AcctId + "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"value\":\"" + Key + "\",\"permissions\":\"FULL\"}]}");
            UpdateLocation p = Place();
            AccessResult r = Run(w, p, false, null);
            T.Eq(AccessOutcome.AskContainer, r.Outcome, "missing container: asks first");
            T.Check(r.Message.Contains("trackvice-agent") && r.Message.Contains("private"), "the question names the container");
            T.Check(w.Writes() == 0 && w.Count("POST", "/listKeys") == 0 && !p.CanWrite, "nothing is created and no key is read before the answer");
            List<string> saved = new List<string>();
            r = Run(w, p, true, saved);
            T.Eq(AccessOutcome.Saved, r.Outcome, "after yes the container is made and the link saved");
            T.Eq(1, w.Count("PUT", "/containers/trackvice-agent"), "the container is created once");
            T.Check(w.Calls[w.Calls.Count - 1].Url.Contains("/listKeys") || saved.Count == 1, "then the link is made");

            FakeWeb w2 = World(true);
            w2.On("GET", "/containers/trackvice-agent", 404, "{}");
            w2.On("PUT", "/containers/trackvice-agent", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"no\"}}");
            AccessResult r2 = Run(w2, Place(), true, null);
            T.Check(r2.Outcome == AccessOutcome.Manual && r2.Message.Contains("create the container") && r2.Message.Contains("Storage Account Contributor"), "403 on creating the container names the role");
        }

        static void KeyOff()
        {
            FakeWeb w = World(false);
            w.On("GET", "/containers/trackvice-agent", 200, "{}");
            UpdateLocation p = Place();
            AccessResult r = Run(w, p, false, null);
            T.Check(r.Outcome == AccessOutcome.Manual && r.Message.Contains("account key access") && r.Message.Contains("User delegation key"), "an account without shared key access gets the manual steps");
            T.Eq(0, w.Count("POST", "/listKeys"), "no key is asked for");
        }

        static void LinkChecks()
        {
            UpdateLocation p = Place();
            string b = "https://" + Acct + ".blob.core.windows.net/";
            string ok = b + "trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&se=2027-01-01T00%3A00%3A00Z&sig=x";
            T.Check(UpdateLocations.WriteLinkProblem(p, ok, Now) == null, "a good link is accepted");
            string m = UpdateLocations.WriteLinkProblem(p, b + "trackvice-agent?sv=2022-11-02&sr=c&sp=rl&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("Read and List") && m.Contains("Create and Write"), "missing permissions are named: " + m);
            m = UpdateLocations.WriteLinkProblem(p, b + "trackvice-agent?sv=2022-11-02&sr=c&sp=cw&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("also needs Read"), "a link without Read is refused: " + m);
            m = UpdateLocations.WriteLinkProblem(p, b + "trackvice?sv=2022-11-02&sr=c&sp=rcw&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("container trackvice ") && m.Contains("trackvice-agent"), "another container is named: " + m);
            m = UpdateLocations.WriteLinkProblem(p, "https://other.blob.core.windows.net/trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("other") && m.Contains(Acct), "another account is named: " + m);
            m = UpdateLocations.WriteLinkProblem(p, ok.Replace("2027-01-01", "2026-11-01"), Now);
            T.Check(m != null && m.Contains("expired on 2026-11-01"), "an expired link says when: " + m);
            m = UpdateLocations.WriteLinkProblem(p, b + "trackvice-agent/TrackVice.exe?sv=2022-11-02&sr=b&sp=rcw&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("file"), "a link for one file is refused: " + m);
            m = UpdateLocations.WriteLinkProblem(p, b + "trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&st=2026-12-25T00%3A00%3A00Z&se=2027-01-01T00%3A00%3A00Z&sig=x", Now);
            T.Check(m != null && m.Contains("not valid before"), "a link that starts later is refused: " + m);
            string steps = PublishAccess.ManualSteps(p);
            T.Check(steps.Contains("Storage accounts > " + Acct + " > Containers > trackvice-agent") && steps.Contains("Shared access tokens") && steps.Contains("Read, Create and Write") && steps.Contains("Blob SAS URL"), "the steps have the account and container");
        }
    }
}
