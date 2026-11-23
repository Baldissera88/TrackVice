namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;

    public static class SignInsTests
    {
        sealed class FakeWeb : IWeb
        {
            public readonly List<WebCall> Calls = new List<WebCall>();
            public bool RefreshNeedsConsent;

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                string body = c.Body == null ? "" : Encoding.UTF8.GetString(c.Body);
                if (c.Url.EndsWith("/devicecode"))
                    return new WebReply(200, "{\"device_code\":\"dc\",\"user_code\":\"ABC123\",\"verification_uri\":\"https://login.example/device\",\"interval\":0,\"expires_in\":60}");
                if (body.Contains("grant_type=refresh_token"))
                {
                    if (RefreshNeedsConsent) return new WebReply(400, "{\"error\":\"interaction_required\",\"error_description\":\"AADSTS65001: consent is needed.\"}");
                    return new WebReply(200, Token("refreshed", 3600));
                }
                return new WebReply(200, Token("first", 3600));
            }

            string Token(string kind, int seconds)
            {
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"upn\":\"anna@contoso.example\"}")).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                return "{\"access_token\":\"h." + payload + "." + kind + "\",\"refresh_token\":\"rt-" + kind + "\",\"expires_in\":" + seconds + "}";
            }

            public int Count(string urlEnd, string bodyPart)
            {
                int n = 0;
                foreach (WebCall c in Calls)
                    if (c.Url.EndsWith(urlEnd) && (bodyPart == null || Encoding.UTF8.GetString(c.Body ?? new byte[0]).Contains(bodyPart))) n++;
                return n;
            }
        }

        const string Client = "6b0f2a1c-3d4e-4f50-8a6b-7c8d9e0f1a2b";
        const string TenantA = "contoso.onmicrosoft.com";
        const string ScopeA = "https://graph.microsoft.com/DeviceManagementScripts.ReadWrite.All";
        const string ScopeB = "https://graph.microsoft.com/Group.ReadWrite.All";

        static WebSignIn SignedIn(FakeWeb w, string client, string tenant, string scopes)
        {
            WebSignIn s = SignIns.Get(w, client, tenant, scopes);
            DeviceCode d = s.Start();
            s.Wait(d, new ManualResetEvent(false));
            return s;
        }

        public static void Run()
        {
            SignIns.Clear();
            SameClientAndTenant();
            OtherTenant();
            AzureAndGraphApart();
            ExtraScope();
            ConsentNeeded();
            Cleared();
            SignIns.Clear();
        }

        static void SameClientAndTenant()
        {
            FakeWeb w = new FakeWeb();
            WebSignIn a = SignedIn(w, Client, TenantA, ScopeA);
            WebSignIn b = SignIns.Get(w, Client, TenantA.ToUpperInvariant(), ScopeA);
            T.Check(b.SignedIn, "the same client and tenant is signed in already");
            T.Eq("anna@contoso.example", b.Account, "the account is shown");
            T.Eq(a.Token(), b.Token(), "the same token");
            WebSignIn c = SignIns.Get(w, Client, "", ScopeA);
            T.Check(!c.SignedIn, "an empty tenant is organizations, not contoso");
            SignIns.Clear();
            SignedIn(w, Client, "", ScopeA);
            WebSignIn o2 = SignIns.Get(w, Client, "organizations", ScopeA);
            T.Check(o2.SignedIn, "empty and organizations are the same tenant");
            SignIns.Clear();
        }

        static void OtherTenant()
        {
            FakeWeb w = new FakeWeb();
            SignedIn(w, Client, TenantA, ScopeA);
            T.Check(!SignIns.Get(w, Client, "fabrikam.onmicrosoft.com", ScopeA).SignedIn, "another tenant asks for its own sign-in");
            T.Check(!SignIns.Get(w, "1b2c3d4e-5f60-4718-9a2b-3c4d5e6f7a8b", TenantA, ScopeA).SignedIn, "another client id asks for its own sign-in");
            T.Check(SignIns.Has(w, Client, TenantA, ScopeA), "Has sees the sign-in");
            T.Check(!SignIns.Has(w, Client, "fabrikam.onmicrosoft.com", ScopeA), "Has does not see another tenant");
            SignIns.Clear();
        }

        static void AzureAndGraphApart()
        {
            FakeWeb w = new FakeWeb();
            SignedIn(w, Arm.ClientId, TenantA, Arm.Scope);
            T.Check(!SignIns.Get(w, Arm.ClientId, TenantA, ScopeA).SignedIn, "a Graph sign-in is not the Azure one");
            SignIns.Clear();
            SignedIn(w, Arm.ClientId, TenantA, ScopeA);
            T.Check(!SignIns.Get(w, Arm.ClientId, TenantA, Arm.Scope).SignedIn, "an Azure sign-in is not the Graph one");
            SignIns.Clear();
            SignedIn(w, Arm.ClientId, TenantA, Arm.Scope);
            SignedIn(w, Client, TenantA, ScopeA);
            SignIns.ClearGraph();
            T.Check(SignIns.Get(w, Arm.ClientId, TenantA, Arm.Scope).SignedIn, "another customer keeps the Azure account");
            T.Check(!SignIns.Get(w, Client, TenantA, ScopeA).SignedIn, "another customer drops the Graph account");
            SignIns.Clear();
        }

        static void ExtraScope()
        {
            FakeWeb w = new FakeWeb();
            WebSignIn first = SignedIn(w, Client, TenantA, ScopeA);
            T.Eq(1, w.Count("/devicecode", null), "one device code");
            WebSignIn more = SignIns.Get(w, Client, TenantA, ScopeB);
            T.Check(!more.SignedIn, "no token for the other scope yet");
            T.Check(more.Quiet(), "the refresh token gives the other scope");
            T.Check(more.SignedIn, "the other scope is signed in");
            T.Eq(1, w.Count("/devicecode", null), "no second device code");
            T.Eq(1, w.Count("/token", "grant_type=refresh_token"), "one refresh");
            T.Check(w.Count("/token", "Group.ReadWrite.All") == 1, "the refresh asked for the new scope");
            T.Check(more.Token().EndsWith("refreshed"), "the new token is for the new scope");
            T.Check(first.Token().EndsWith("first"), "the first token is kept");
            T.Check(SignIns.Get(w, Client, TenantA, ScopeB).SignedIn, "the extra scope is found next time");
            T.Eq(1, w.Count("/token", "grant_type=refresh_token"), "no more refreshes");
            SignIns.Clear();
        }

        static void ConsentNeeded()
        {
            FakeWeb w = new FakeWeb();
            SignedIn(w, Client, TenantA, ScopeA);
            w.RefreshNeedsConsent = true;
            WebSignIn more = SignIns.Get(w, Client, TenantA, ScopeB);
            T.Check(!more.Quiet(), "consent is needed, so the code is the way");
            DeviceCode d = more.Start();
            T.Eq(2, w.Count("/devicecode", null), "a second device code");
            T.Check(w.Count("/devicecode", "Group.ReadWrite.All") == 1, "the code is for the new scope");
            T.Eq(1, w.Count("/devicecode", ScopeA.Replace(":", "%3A").Replace("/", "%2F")), "the first scope is not asked again");
            w.RefreshNeedsConsent = false;
            more.Wait(d, new ManualResetEvent(false));
            T.Check(more.SignedIn, "signed in for the new scope");
            T.Check(SignIns.Get(w, Client, TenantA, ScopeA).SignedIn, "the first scope still works");
            SignIns.Clear();
        }

        static void Cleared()
        {
            FakeWeb w = new FakeWeb();
            SignedIn(w, Client, TenantA, ScopeA);
            SignedIn(w, Arm.ClientId, TenantA, Arm.Scope);
            SignIns.Clear();
            T.Check(!SignIns.Get(w, Client, TenantA, ScopeA).SignedIn, "Clear forgets the Graph account");
            T.Check(!SignIns.Get(w, Arm.ClientId, TenantA, Arm.Scope).SignedIn, "Clear forgets the Azure account");
            T.Check(!SignIns.Has(w, Client, TenantA, ScopeA), "Has is false after Clear");
            SignIns.Clear();
        }
    }
}
