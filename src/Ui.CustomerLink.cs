namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // Edit customer > Get it from Your storage: signs in to Azure when the account key is not kept, makes the
    // container and links of the customer and hands them to the editor. It is not modal, so the editor stays usable.
    sealed class CustomerLinkDialog : GuideForm
    {
        readonly OwnStorage own;
        readonly ISecretStore secrets;
        readonly string customer;
        readonly Action<CustomerStorageResult> onDone;
        readonly FlatButton bMake;
        string result = "";
        bool finished;

        public CustomerLinkDialog(Theme t, IWeb web, ISecretStore secrets, bool isDemo, string customer, Action<CustomerStorageResult> onDone) : base("Get it from Your storage", t, web, isDemo)
        {
            this.secrets = secrets;
            this.customer = customer;
            this.onDone = onDone;
            own = OwnStorage.Load(secrets);
            Size = new Size(Dpi.S(780), Dpi.S(460));
            bMake = Button("Make", delegate { Make(); });
            bMake.Primary = true;
            Add(body);
            Rebuild();
            if (own != null && !own.KeyKept)
                Shown += delegate { if (SignIns.Has(this.web, Arm.ClientId, own.Tenant, Arm.Scope)) SignInOwn(); };
        }

        public static void Open(Control owner, Theme t, string customer, Action<CustomerStorageResult> done)
        {
            CustomerLinkDialog d = new CustomerLinkDialog(t, YourStorageUi.Web, YourStorageUi.Secrets, YourStorageUi.Demo, customer, done);
            d.FormClosed += delegate { d.Dispose(); };
            d.Show(owner.FindForm());
        }

        // The edit dialog of a made-up customer with no link, after the container and links were made in the demo storage.
        public static CustomerEditor ForShot(Theme t)
        {
            YourStorageUi.UseDemo();
            CustomerBook b = CustomerBook.Demo();
            Customer c = new Customer();
            c.Name = "Fabrikam";
            c.Tenant = "fabrikam.onmicrosoft.com";
            CustomerEditor e = new CustomerEditor(t, b, c);
            e.Got(CustomerStorageLink.Make(YourStorageUi.Web, YourStorageUi.Load(), c.Name, DateTime.UtcNow));
            e.Shown += delegate { e.ScrollToEnd(); };
            return e;
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle(customer));
            if (own == null) { l.Add(Row(Glyphs.Info, "Your storage is not set up", "Set it up in Settings > Your storage first.", null)); return; }
            if (!own.KeyKept) l.Add(OwnSignInRow(own, "The account key of " + own.Account + " is not kept on this PC. Sign in to read it for this step; it is not kept."));
            string container = BlobContainers.NameFor(customer);
            string what = "Makes the private container " + container + " in " + own.Account + ", or uses the one that is there. Replaces TrackVice's access policies on it, " + CustomerAccess.Upload + " and " + CustomerAccess.Viewer + ", and signs a viewer link and an upload link, valid for 2 years. Other policies are kept. Links made for this customer before stop within 30 seconds.";
            l.Add(Row(Glyphs.Save, "Make the container and links", result.Length > 0 ? result : what, bMake));
            bMake.Enabled = !finished;
        }

        void Make()
        {
            if (busy || own == null || finished) return;
            if (CustomerStorageLink.NeedsSignIn(own, OwnSignedIn)) { note.Say("Sign in to Azure first.", true); return; }
            if (!demo && CustomerCodes.Load(secrets, customer) != null
                && MessageBox.Show(this, "TrackVice has a customer code for " + customer + ". Making the links again replaces its upload link, so devices with that code can no longer upload until they get a new one.\n\nContinue?", "Get it from Your storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            CustomerStorageResult r = null;
            Work("Making the container and links...", delegate
            {
                r = CustomerStorageLink.Make(web, OwnWithKey(own), customer, DateTime.UtcNow);
            }, delegate
            {
                finished = true;
                result = (r.ContainerWasThere ? "The container " + r.Container + " was there and is used." : "The container " + r.Container + " is made.") + " The viewer link is in the Upload location box. Azure puts the links in force within 30 seconds. You can close this window.";
                onDone(r);
            }, null);
        }
    }
}
