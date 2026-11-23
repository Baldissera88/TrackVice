namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The sign-ins of this run, so that a person signs in once and not in every dialog. Kept in memory only, never on disk.
    // The owner's Azure account and a customer's Graph account are kept apart, as are tenants and apps.
    public static class SignIns
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, WebSignIn> kept = new Dictionary<string, WebSignIn>();

        static string Key(WebSignIn s)
        {
            bool azure = (s.Scopes ?? "").IndexOf("management.azure.com", StringComparison.OrdinalIgnoreCase) >= 0;
            return (azure ? "azure|" : "graph|") + s.ClientId.ToLowerInvariant() + "|" + s.Tenant.ToLowerInvariant();
        }

        // The sign-in for this app and tenant: the one that is there, with a token for the scopes when it has one,
        // or a new one that is not signed in yet. Pass the result to the dialog's sign-in step.
        public static WebSignIn Get(IWeb web, string clientId, string tenant, string scopes)
        {
            WebSignIn fresh = new WebSignIn(web, clientId, tenant, scopes);
            string key = Key(fresh);
            lock (gate)
            {
                WebSignIn have;
                if (kept.TryGetValue(key, out have) && have.UsesWeb(web))
                {
                    if (have.Scopes == scopes) return have;
                    if (have.Has("")) return have.For(scopes);
                }
                kept[key] = fresh;
                return fresh;
            }
        }

        // True when a sign-in is there for this app and tenant, so that a dialog can show it at once.
        public static bool Has(IWeb web, string clientId, string tenant, string scopes)
        {
            WebSignIn fresh = new WebSignIn(web, clientId, tenant, scopes);
            lock (gate)
            {
                WebSignIn have;
                return kept.TryGetValue(Key(fresh), out have) && have.UsesWeb(web) && have.Has("");
            }
        }

        public static void Clear()
        {
            lock (gate) kept.Clear();
        }

        // Another customer was picked: the customers' accounts go, the owner's Azure account stays.
        public static void ClearGraph()
        {
            lock (gate)
            {
                List<string> drop = new List<string>();
                foreach (string k in kept.Keys) if (k.StartsWith("graph|", StringComparison.Ordinal)) drop.Add(k);
                foreach (string k in drop) kept.Remove(k);
            }
        }
    }
}
