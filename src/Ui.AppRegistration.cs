namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Text;
    using System.Windows.Forms;

    // Settings > Intune / cloud: why and how to use an app registration of the customer's own, and the button that makes one.
    public sealed partial class SettingsDialog
    {
        void AddAppRegistration(List<Control> l)
        {
            l.Add(new SectionTitle("Your own app registration"));
            l.Add(Bind("appreg", new SettingRow(Glyphs.Info, "Your own app registration", null, null)));
            l.Add(Bind("appreg.needs", new SettingRow(Glyphs.Shield, "What your own app needs", null, null)));
            l.Add(Bind("appreg.permissions", new SettingRow(Glyphs2.Code, "Permissions of the app", null, null)));
            FlatButton steps = SmallButton("Step by step", delegate
            {
                try { Process.Start(AddCustomerUi.AgentDoc + "#app-registration"); }
                catch (Exception) { Menus.Copy(AddCustomerUi.AgentDoc + "#app-registration"); }
            });
            FlatButton make = SmallButton("Make it…", delegate
            {
                using (AppRegistrationDialog d = AppRegistrationDialog.ForLive(theme, tenant.Text.Trim(), delegate (string id) { client.Text = id; })) d.ShowDialog(this);
            });
            l.Add(Bind("appreg.make", new SettingRow(Glyphs2.Sync, "Make the app registration", null, new ButtonStrip(steps, make))));
        }
    }

    // Makes the app registration of TrackVice in the customer's tenant: sign in, see what it makes, ask, make, and the consent link.
    sealed class AppRegistrationDialog : GuideForm
    {
        readonly TextBox tTenant;
        readonly FlatButton bIn, bMake, bUse;
        readonly Action<string> onClient;
        WebSignIn signIn;
        string signText = "";
        AppRegistrationResult found, made;

        public AppRegistrationDialog(Theme t, IWeb web, bool isDemo, string tenant, Action<string> onClient) : base("App registration", t, web, isDemo)
        {
            this.onClient = onClient;
            Size = new Size(Dpi.S(860), Dpi.S(760));
            tTenant = Field(300, false);
            tTenant.Text = tenant ?? "";
            bIn = Button("Sign in", delegate { SignInGraph(); });
            bMake = Button("Make", delegate { Make(); });
            bMake.Primary = true;
            bUse = Button("Use it", delegate { Make(); });
            Add(body);
            Rebuild();
        }

        public static AppRegistrationDialog ForLive(Theme t, string tenant, Action<string> onClient)
        {
            return new AppRegistrationDialog(t, YourStorageUi.Web, false, tenant, onClient);
        }

        // tab 0: before the sign-in, 1: signed in, 2: made, 3: signed in, and the app is there already.
        public static AppRegistrationDialog ForDemo(Theme t, int tab)
        {
            AppRegistrationDialog d = new AppRegistrationDialog(t, new DemoAppRegistrationWeb(tab == 3), true, "northwind.onmicrosoft.com", null);
            if (tab >= 1) d.SignInGraph();
            if (tab == 2) d.Make();
            d.note.Say("", false);
            return d;
        }

        bool SignedIn
        {
            get { return signIn != null && signIn.SignedIn; }
        }

        string AppName
        {
            get { return AppRegistrationMaker.DefaultName; }
        }

        AppRegistrationMaker Maker()
        {
            AppRegistrationMaker m = new AppRegistrationMaker(web, signIn.Token);
            if (demo) m.Pause = delegate { };
            return m;
        }

        void SignInGraph()
        {
            if (busy && pending != null && signIn != null && !signIn.SignedIn) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = new WebSignIn(web, null, tTenant.Text, AppRegistrationMaker.SignInScope);
            signIn = s;
            found = null;
            SignIn(s, delegate (string t) { signText = t; }, delegate { found = new AppRegistrationMaker(web, s.Token).Find(AppName); }, null, delegate { signText = ""; signIn = null; });
        }

        void Make()
        {
            if (busy || !SignedIn) return;
            string q = found != null
                ? "Use the app registration " + AppName + " with the Client ID " + found.ClientId + "?\n\nTrackVice does not change the app. Its enterprise application is made when it is missing."
                : "Make the app registration " + AppName + " in this tenant?\n\n" + string.Join("\n", AppRegistrationMaker.Plan(AppName).ToArray());
            if (!demo && MessageBox.Show(this, q, "App registration", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            AppRegistrationMaker m = Maker();
            Work("Making " + AppName + "...", delegate { made = m.Make(AppName); }, delegate
            {
                if (onClient != null) onClient(made.ClientId);
                note.Say("The Client ID is filled in on the settings page.", false);
            }, null);
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("App registration"));
            l.Add(Row(Glyphs.Info, "What this does", "Makes an app registration for TrackVice in the customer's tenant, so that TrackVice signs in with an app the customer controls. Signing in and looking change nothing. Nothing is made until you press Make.", null));
            if (made != null)
            {
                BuildDone(l);
                return;
            }
            l.Add(new SectionTitle("Sign in"));
            l.Add(Row(Glyphs.Shield, "Tenant", "The tenant ID or domain of the customer, such as contoso.onmicrosoft.com. Empty signs in to the tenant of your account.", tTenant));
            bIn.Text = SignedIn ? "Sign in again" : pending != null ? "Copy code and open" : "Sign in";
            bIn.AutoWidth();
            bIn.Width += Dpi.S(8);
            l.Add(Row(Glyphs2.Sync, "Sign in", signText.Length > 0 ? signText : "With a code in the browser, as Microsoft Graph Command Line Tools, for the permission Application.ReadWrite.All. The account needs the role Application Developer, Application Administrator or Cloud Application Administrator, or the tenant must let users register applications.", bIn));
            if (!SignedIn) return;
            if (found != null)
            {
                l.Add(new SectionTitle("Already there"));
                l.Add(Row(Glyphs.Info, "An app named " + AppName + " is in this tenant", "Client ID " + found.ClientId + ". TrackVice does not change it. Use it to fill in its Client ID, and check its permissions against the list in Settings.", bUse));
                return;
            }
            l.Add(new SectionTitle("What it makes"));
            l.Add(Row(Glyphs.Save, "What Make makes", string.Join("\n", AppRegistrationMaker.Plan(AppName).ToArray()), bMake));
            l.Add(Row(Glyphs.Shield, "What it does not make", "A secret, a certificate or application permissions. It does not give consent: an administrator does that with a link, after.", null));
        }

        void BuildDone(List<Control> l)
        {
            l.Add(new SectionTitle("Done"));
            StringBuilder b = new StringBuilder();
            b.Append(made.Existed ? "The app " + AppName + " was there. It is not changed." : "The app registration " + AppName + " with its permissions.");
            b.Append(made.MadePrincipal ? "\nIts enterprise application." : "");
            l.Add(Row(Glyphs.Save, made.Existed ? "Used" : "Made", b.ToString(), null));
            l.Add(Row(Glyphs2.Code, "Client ID", made.ClientId + ". It is filled in on the settings page.", CopyButton("Client ID", delegate { return made.ClientId; })));
            l.Add(Row(Glyphs.Shield, "Admin consent", "The permissions work only after an administrator gives consent in the tenant. " + SetupNeeds.ConsentWho + " can open this link, sign in and accept. Send the link to one of them if that is not you.", new ButtonStrip(SmallButton("Open the link", delegate { Open(made.ConsentLink); }), CopyButton("link", delegate { return made.ConsentLink; }))));
            l.Add(Row(Glyphs2.Help, "Next", "Sign out in Settings > Intune / cloud, then sign in again, so that TrackVice uses the new app.", null));
        }
    }
}
