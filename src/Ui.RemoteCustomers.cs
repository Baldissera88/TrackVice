namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // The customer selector at the top of every device list.
    public sealed partial class RemoteDialog
    {
        CustomerPicker picker;
        CustomerBook book;
        string listKind;
        // Counts customer switches, so an answer to a connect started before a switch is ignored.
        int gen;

        void BuildCustomers(string kind)
        {
            listKind = kind;
            book = isDemo ? CustomerBook.Demo() : CustomerUi.Book;
            picker = new CustomerPicker();
            picker.Value = book.Current == null ? "" : book.Current.Name;
            picker.Enabled = !isDemo;
            picker.Click += Safe.Wrap(delegate { CustomerMenu(); });
            picker.Resize += delegate { PlacePicker(); };
            head.Resize += delegate { PlacePicker(); };
            themed.Add(picker);
            head.Controls.Add(picker);
            PlacePicker();
            // A SAS link in the Upload location box is a credential: it is not shown. What is typed into an emptied box is.
            Shown += delegate
            {
                if (listKind != Agents || server == null) return;
                server.UseSystemPasswordChar = Stores.IsUrl(server.Text);
                server.TextChanged += delegate { if (server.Text.Length <= 1) server.UseSystemPasswordChar = false; };
            };
        }

        void PlacePicker()
        {
            picker.Location = new Point(head.ClientSize.Width - Dpi.S(24) - picker.Width, Dpi.S(10));
        }

        void CustomerMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            m.ShowCheckMargin = true;
            Customer cur = book.Current;
            foreach (Customer c in book.All)
            {
                Customer one = c;
                ToolStripMenuItem i = Menus.Add(m, c.Name, delegate { SwitchCustomer(one); });
                i.Checked = c == cur;
            }
            if (book.Count == 0)
            {
                ToolStripMenuItem none = Menus.Add(m, "No customers yet", delegate { });
                none.Enabled = false;
            }
            Menus.Separator(m);
            Menus.Add(m, "Manage customers…", delegate { ManageCustomers(); });
            m.Show(picker, new Point(0, picker.Height));
        }

        void ManageCustomers()
        {
            Customer before = book.Current == null ? null : book.Current.Copy();
            using (CustomersPage p = new CustomersPage(theme, book)) p.ShowDialog(this);
            Customer now = book.Current;
            picker.Value = now == null ? "" : now.Name;
            if (now != null && (before == null || !CustomerUi.Same(before, now))) SwitchCustomer(now);
        }

        // Disconnects, fills in the details of the customer and connects with them.
        void SwitchCustomer(Customer c)
        {
            if (isDemo) return;
            gen++;
            if (attempt != null) attempt.Set();
            code = null;
            bool keep = CustomerUi.Switch(c);
            picker.Value = c.Name;
            auto.Stop();
            Catalog = null;
            all = new List<RemoteDeployment>();
            Bind();
            Status("");
            if (listKind == Agents)
            {
                server.Text = c.AgentLocation;
                server.UseSystemPasswordChar = Stores.IsUrl(c.AgentLocation);
                bConnect.Enabled = true;
                if (c.AgentLocation.Length > 0) ConnectAgents();
                else
                {
                    table.EmptyText = c.Name + " has no upload location yet.\n\nEnter it and connect: it is saved for " + c.Name + ".";
                    Status("");
                    server.Focus();
                }
            }
            else if (listKind == Sccm)
            {
                server.Text = c.SccmServer;
                site.Text = c.SccmSite;
                bConnect.Enabled = true;
                if (c.SccmServer.Length > 0) ConnectSccm();
                else
                {
                    table.EmptyText = c.Name + " has no SMS Provider yet.\n\nEnter it and connect: it is saved for " + c.Name + ".";
                    server.Focus();
                }
            }
            else if (keep && Graph != null && Graph.SignedIn)
            {
                Catalog = new IntuneCatalog(Graph);
                Connected();
            }
            else
            {
                // Another tenant: the sign-in of the customer before is dropped, never reused.
                Graph = null;
                codeText.Text = "Signed in to Intune, read-only.";
                signIn.Show(SignInPanel.Start);
                ShowSignIn(true);
                SignIn();
            }
        }
    }
}
