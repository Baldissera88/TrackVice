namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    // Asks for the password of a customers file. When it locks a file, the password is asked for twice.
    public sealed class CustomerPasswordDialog : PageForm
    {
        readonly TextBox first, second;
        readonly bool confirm;
        readonly FooterNote note = new FooterNote();
        string result;

        public CustomerPasswordDialog(Theme t, bool confirm) : base(confirm ? "Protect the file" : "Open the file", t)
        {
            this.confirm = confirm;
            Size = new Size(Dpi.S(640), Dpi.S(confirm ? 380 : 290));
            MinimumSize = new Size(Dpi.S(480), Dpi.S(confirm ? 340 : 260));
            first = Field();
            Add(new SettingRow(Glyphs.Shield, "Password",
                confirm ? "At least " + CustomerFile.MinPassword + " characters. It is not saved. Without it the file cannot be opened." : "The password this file was protected with.", first));
            if (confirm)
            {
                second = Field();
                Add(new SettingRow(Glyphs.Shield, "Repeat the password", "Type it again to be sure.", second));
            }
            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.ApplyTheme(t);
            FlatButton ok = new FlatButton(confirm ? "Protect" : "Open", null);
            ok.Primary = true;
            ok.Width = Math.Max(ok.Width, Dpi.S(96));
            ok.Dock = DockStyle.Right;
            ok.Click += Safe.Wrap(delegate { Accept(); });
            ok.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            note.Dock = DockStyle.Fill;
            note.ApplyTheme(t);
            footer.Controls.Add(gap);
            footer.Controls.Add(ok);
            footer.Controls.Add(note);
            note.BringToFront();
            AcceptButton = null;
            Shown += delegate { first.Focus(); };
            FormClosed += delegate { Clear(); };
        }

        TextBox Field()
        {
            TextBox b = new TextBox();
            b.Width = Dpi.S(300);
            b.Font = Fonts.Ui;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.BackColor = theme.Field;
            b.ForeColor = theme.Text;
            b.UseSystemPasswordChar = true;
            b.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); } };
            return b;
        }

        void Accept()
        {
            string problem = null;
            if (confirm)
            {
                problem = CustomerFile.PasswordProblem(first.Text);
                if (problem == null && first.Text != second.Text) problem = "The two passwords are not the same.";
            }
            else if (first.Text.Length == 0) problem = "Type the password.";
            if (problem != null) { note.Say(problem, true); return; }
            result = first.Text;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Clear()
        {
            first.Text = "";
            if (second != null) second.Text = "";
        }

        // Returns the password, or null when the user cancels.
        public static string Ask(IWin32Window owner, Theme t, bool confirm)
        {
            using (CustomerPasswordDialog d = new CustomerPasswordDialog(t, confirm))
            {
                string p = d.ShowDialog(owner) == DialogResult.OK ? d.result : null;
                d.result = null;
                return p;
            }
        }
    }
}
