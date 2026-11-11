namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Reflection;
    using System.Text;
    using System.Windows.Forms;

    public interface IThemed
    {
        void ApplyTheme(Theme t);
    }

    // Catches exceptions from event handlers and reports them in the window instead of failing.
    public static class Safe
    {
        public static Action<Exception> Reporter;
        public static Action<string> Status;

        public static void Report(Exception ex)
        {
            try { if (Reporter != null && ex != null) Reporter(ex); } catch (Exception) { }
        }

        public static void Say(string text)
        {
            try { if (Status != null) Status(text); } catch (Exception) { }
        }

        public static EventHandler Wrap(EventHandler h)
        {
            return delegate (object s, EventArgs e)
            {
                try { h(s, e); }
                catch (Exception ex) { Report(ex); }
            };
        }

        public static void Run(Action a)
        {
            try { a(); }
            catch (Exception ex) { Report(ex); }
        }
    }

    public static class Menus
    {
        public static ContextMenuStrip New(Theme t)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Prepare(m, t);
            return m;
        }

        public static void Prepare(ContextMenuStrip m, Theme t)
        {
            m.Items.Clear();
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors(t));
            m.BackColor = t.Card;
            m.ForeColor = t.Text;
            m.Font = Fonts.Ui;
            m.ShowImageMargin = false;
        }

        public static ToolStripMenuItem Add(ContextMenuStrip m, string text, EventHandler click)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, Safe.Wrap(click));
            i.ForeColor = m.ForeColor;
            i.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3));
            m.Items.Add(i);
            return i;
        }

        public static void Separator(ContextMenuStrip m)
        {
            if (m.Items.Count > 0 && !(m.Items[m.Items.Count - 1] is ToolStripSeparator)) m.Items.Add(new ToolStripSeparator());
        }

        public static bool Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetText(text);
                Safe.Say("Copied to the clipboard.");
                return true;
            }
            catch (Exception)
            {
                Safe.Say("The clipboard is busy. Try again.");
                return false;
            }
        }
    }

    // A slim bar that slides while TrackVice reads the device.
    public sealed class BusyBar : FlatControl
    {
        readonly Timer timer = new Timer();
        float pos;
        bool running;

        public BusyBar()
        {
            Height = Dpi.S(3);
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            timer.Interval = 30;
            timer.Tick += delegate
            {
                pos += 0.012f;
                if (pos > 1.2f) pos = -0.3f;
                Invalidate();
            };
        }

        public bool Running
        {
            get { return running; }
            set
            {
                if (running == value) return;
                running = value;
                pos = -0.3f;
                if (value) timer.Start(); else timer.Stop();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            if (!running) return;
            int w = Math.Max(Dpi.S(60), Width / 4);
            int x = (int)(pos * Width);
            using (SolidBrush b = new SolidBrush(T.Accent)) g.FillRectangle(b, x, 0, w, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    static class Shapes
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            if (d <= 0 || r.Width < d || r.Height < d)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }
    }

    public class FlatControl : Control, IThemed
    {
        protected Theme T = Theme.Light();
        protected bool hover, down;

        public FlatControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Fonts.Ui;
        }

        public virtual void ApplyTheme(Theme t)
        {
            T = t;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    }

    public sealed class FlatButton : FlatControl
    {
        public string Glyph;
        public bool Primary;
        public bool Checked;
        public bool Compact;

        public FlatButton(string text, string glyph)
        {
            Text = text;
            Glyph = glyph;
            Cursor = Cursors.Hand;
            Height = Dpi.S(32);
            TabStop = true;
            AutoWidth();
        }

        public void AutoWidth()
        {
            int w = Dpi.S(Compact ? 10 : 14) * 2;
            if (!string.IsNullOrEmpty(Text)) w += TextRenderer.MeasureText(Text, Fonts.Ui).Width;
            if (HasGlyph) w += Dpi.S(string.IsNullOrEmpty(Text) ? 18 : 24);
            Width = Math.Max(w, Dpi.S(32));
        }

        bool HasGlyph
        {
            get { return Fonts.Icons != null && !string.IsNullOrEmpty(Glyph); }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AutoWidth();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fg = T.Text;
            if (Primary)
            {
                Shapes.Fill(g, r, Dpi.S(5), !Enabled ? T.Divider : hover ? T.AccentHover : T.Accent);
                fg = Enabled ? T.OnAccent : T.Text3;
            }
            else
            {
                Color bg = Checked ? T.AccentSoft : down ? T.Pressed : hover ? T.Hover : Color.Empty;
                if (!bg.IsEmpty) Shapes.Fill(g, r, Dpi.S(5), bg);
                if (Checked) Shapes.Stroke(g, r, Dpi.S(5), T.Accent, 1f);
                if (!Enabled) fg = T.Text3;
            }
            int x = Dpi.S(Compact ? 10 : 14);
            if (HasGlyph)
            {
                Rectangle gr = new Rectangle(x - Dpi.S(2), 0, Dpi.S(20), Height);
                TextRenderer.DrawText(g, Glyph, Fonts.Icons, gr, Checked && !Primary ? T.Accent : fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += Dpi.S(24);
            }
            if (!string.IsNullOrEmpty(Text))
            {
                Rectangle tr = new Rectangle(x, 0, Width - x, Height);
                if (!HasGlyph) tr = new Rectangle(0, 0, Width, Height);
                TextRenderer.DrawText(g, Text, Fonts.Ui, tr, fg, (HasGlyph ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            if (Focused && ShowFocusCues) Shapes.Stroke(g, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Dpi.S(5), T.Accent, 1.5f);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // A clickable count that also works as a filter.
    public sealed class StatChip : FlatControl
    {
        public int Count;
        public string Label;
        public int Tone;
        public bool Selected;

        public StatChip(string label, int tone)
        {
            Label = label;
            Tone = tone;
            Cursor = Cursors.Hand;
            Size = new Size(Math.Max(Dpi.S(124), TextRenderer.MeasureText(label, Fonts.Small).Width + Dpi.S(30)), Dpi.S(58));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(7), Selected ? T.AccentSoft : hover ? T.Hover : T.Card);
            Shapes.Stroke(g, r, Dpi.S(7), Selected ? T.Accent : T.Divider, 1f);
            Color num = T.Text;
            if (Count > 0 && Tone == 2) num = T.ErrFg;
            if (Count > 0 && Tone == 1) num = T.WarnFg;
            int pad = Dpi.S(12);
            TextRenderer.DrawText(g, Count.ToString(), Fonts.Title, new Rectangle(pad, Dpi.S(6), Width - pad, Dpi.S(28)), num, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Label, Fonts.Small, new Rectangle(pad, Dpi.S(34), Width - pad, Dpi.S(18)), T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    public sealed class TabStrip : FlatControl
    {
        readonly List<string> tabs = new List<string>();
        readonly List<Rectangle> hits = new List<Rectangle>();
        int selected, hot = -1;
        public event EventHandler SelectedChanged;

        public TabStrip()
        {
            Height = Dpi.S(38);
            Cursor = Cursors.Hand;
        }

        public void SetTabs(params string[] names)
        {
            tabs.Clear();
            tabs.AddRange(names);
            Invalidate();
        }

        public void SetText(int i, string text)
        {
            if (i >= 0 && i < tabs.Count && tabs[i] != text)
            {
                tabs[i] = text;
                Invalidate();
            }
        }

        public int Selected
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                selected = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            hits.Clear();
            int x = Dpi.S(16);
            int total = x, pad = Dpi.S(20);
            for (int i = 0; i < tabs.Count; i++) total += TextRenderer.MeasureText(tabs[i], i == selected ? Fonts.UiSemibold : Fonts.Ui).Width + pad + Dpi.S(4);
            if (total > Width) pad = Dpi.S(8);
            for (int i = 0; i < tabs.Count; i++)
            {
                Font f = i == selected ? Fonts.UiSemibold : Fonts.Ui;
                int w = TextRenderer.MeasureText(tabs[i], f).Width + pad;
                Rectangle r = new Rectangle(x, 0, w, Height);
                hits.Add(r);
                if (i == hot && i != selected) Shapes.Fill(g, new RectangleF(r.X, Dpi.S(4), r.Width, Height - Dpi.S(10)), Dpi.S(5), T.Hover);
                TextRenderer.DrawText(g, tabs[i], f, new Rectangle(r.X, 0, r.Width, Height - Dpi.S(4)), i == selected ? T.Text : T.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (i == selected) Shapes.Fill(g, new RectangleF(r.X + Dpi.S(10), Height - Dpi.S(5), r.Width - Dpi.S(20), Dpi.S(3)), Dpi.S(1.5f), T.Accent);
                x += w + Dpi.S(4);
            }
            using (Pen p = new Pen(T.Divider)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < hits.Count; i++) if (hits[i].Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int i = 0; i < hits.Count; i++) if (hits[i].Contains(e.Location)) Selected = i;
            base.OnMouseClick(e);
        }
    }

    public sealed class Column<T>
    {
        public string Header;
        public Func<T, string> Text;
        public int Width;
        public bool Fill;
        public bool Mono;

        public Column(string header, int width, Func<T, string> text)
        {
            Header = header;
            Width = width;
            Text = text;
        }
    }

    // A themed, virtual, sortable grid over a list.
    public sealed class Table<T> : IThemed where T : class
    {
        public readonly DataGridView Grid = new DataGridView();
        readonly List<Column<T>> columns = new List<Column<T>>();
        List<T> rows = new List<T>();
        int sortCol = -1;
        bool sortDesc;
        Theme theme = Theme.Light();
        public Func<T, Color> ForeColor;
        public event EventHandler SelectionChanged;
        // Adds the items for the selected row after Copy cell and Copy row.
        public Action<ContextMenuStrip, T> MenuExtras;
        // Shown in the grid when it has no rows.
        public string EmptyText;
        int menuCol = -1;

        public Table()
        {
            try
            {
                PropertyInfo db = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (db != null) db.SetValue(Grid, true, null);
            }
            catch (Exception) { }
            Grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            Grid.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    T r = Current;
                    if (r != null) Menus.Copy(RowText(r));
                }
            };
            Grid.CellMouseDown += delegate (object s, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= rows.Count || e.ColumnIndex < 0) return;
                menuCol = e.ColumnIndex;
                try { Grid.CurrentCell = Grid.Rows[e.RowIndex].Cells[e.ColumnIndex]; } catch (Exception) { }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Opening += delegate (object s, CancelEventArgs e)
            {
                try
                {
                    BuildMenu(menu);
                    if (menu.Items.Count == 0) e.Cancel = true;
                }
                catch (Exception ex)
                {
                    e.Cancel = true;
                    Safe.Report(ex);
                }
            };
            Grid.ContextMenuStrip = menu;
            Grid.Paint += delegate (object s, PaintEventArgs e)
            {
                if (rows.Count > 0 || string.IsNullOrEmpty(EmptyText)) return;
                Rectangle r = new Rectangle(Dpi.S(24), Grid.ColumnHeadersHeight + Dpi.S(28), Math.Max(0, Grid.ClientSize.Width - Dpi.S(48)), Dpi.S(90));
                TextRenderer.DrawText(e.Graphics, EmptyText, Fonts.Ui, r, theme.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            };
            Grid.VirtualMode = true;
            Grid.ReadOnly = true;
            Grid.AllowUserToAddRows = false;
            Grid.AllowUserToDeleteRows = false;
            Grid.AllowUserToResizeRows = false;
            Grid.AllowUserToOrderColumns = true;
            Grid.RowHeadersVisible = false;
            Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Grid.MultiSelect = false;
            Grid.BorderStyle = BorderStyle.None;
            Grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            Grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            Grid.EnableHeadersVisualStyles = false;
            Grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            Grid.ColumnHeadersHeight = Dpi.S(32);
            Grid.RowTemplate.Height = Dpi.S(28);
            Grid.Dock = DockStyle.Fill;
            Grid.StandardTab = true;
            Grid.ShowCellToolTips = true;
            Grid.CellValueNeeded += delegate (object s, DataGridViewCellValueEventArgs e)
            {
                e.Value = Short(Cell(e.RowIndex, e.ColumnIndex), 300);
            };
            Grid.CellToolTipTextNeeded += delegate (object s, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                string v = Cell(e.RowIndex, e.ColumnIndex);
                if (v != null && v.Length > 40) e.ToolTipText = Txt.Short(v, 1000);
            };
            Grid.CellFormatting += delegate (object s, DataGridViewCellFormattingEventArgs e)
            {
                if (ForeColor == null || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                try
                {
                    Color c = ForeColor(rows[e.RowIndex]);
                    if (!c.IsEmpty)
                    {
                        e.CellStyle.ForeColor = c;
                        e.CellStyle.SelectionForeColor = c;
                    }
                }
                catch (Exception) { }
            };
            Grid.ColumnHeaderMouseClick += delegate (object s, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                try
                {
                    T keep = Current;
                    if (sortCol == e.ColumnIndex) sortDesc = !sortDesc;
                    else { sortCol = e.ColumnIndex; sortDesc = false; }
                    Sort();
                    for (int i = 0; i < Grid.Columns.Count; i++)
                        Grid.Columns[i].HeaderCell.SortGlyphDirection = i == sortCol ? (sortDesc ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
                    Grid.Invalidate();
                    Select(keep);
                }
                catch (Exception ex) { Safe.Report(ex); }
            };
            Grid.SelectionChanged += delegate { RaiseSelection(); };
            Grid.DataError += delegate (object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
        }

        void RaiseSelection()
        {
            try { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
            catch (Exception ex) { Safe.Report(ex); }
        }

        string Cell(int row, int col)
        {
            if (row < 0 || row >= rows.Count || col < 0 || col >= columns.Count) return "";
            try { return columns[col].Text(rows[row]) ?? ""; }
            catch (Exception) { return ""; }
        }

        static string Short(string v, int max)
        {
            return string.IsNullOrEmpty(v) ? "" : Txt.Short(v, max);
        }

        // The row as tab-separated text, in the column order on screen.
        public string RowText(T r)
        {
            int i = rows.IndexOf(r);
            List<string> parts = new List<string>();
            foreach (DataGridViewColumn c in Ordered()) parts.Add(Cell(i, c.Index).Replace("\t", " ").Replace("\r", " ").Replace("\n", " "));
            return string.Join("\t", parts.ToArray());
        }

        List<DataGridViewColumn> Ordered()
        {
            List<DataGridViewColumn> list = new List<DataGridViewColumn>();
            foreach (DataGridViewColumn c in Grid.Columns) if (c.Visible) list.Add(c);
            list.Sort(delegate (DataGridViewColumn a, DataGridViewColumn b) { return a.DisplayIndex.CompareTo(b.DisplayIndex); });
            return list;
        }

        public string AllText()
        {
            StringBuilder b = new StringBuilder();
            List<string> head = new List<string>();
            foreach (DataGridViewColumn c in Ordered()) head.Add(c.HeaderText);
            b.Append(string.Join("\t", head.ToArray())).Append("\r\n");
            foreach (T r in rows) b.Append(RowText(r)).Append("\r\n");
            return b.ToString();
        }

        void BuildMenu(ContextMenuStrip m)
        {
            Menus.Prepare(m, theme);
            T row = Current;
            int col = menuCol >= 0 ? menuCol : Grid.CurrentCell != null ? Grid.CurrentCell.ColumnIndex : 0;
            menuCol = -1;
            if (row != null)
            {
                string cell = Cell(rows.IndexOf(row), col);
                Menus.Add(m, "Copy cell", delegate { Menus.Copy(cell); }).Enabled = cell.Length > 0;
                Menus.Add(m, "Copy row  (Ctrl+C)", delegate { Menus.Copy(RowText(row)); });
            }
            if (rows.Count > 0) Menus.Add(m, "Copy all rows", delegate { Menus.Copy(AllText()); });
            if (row != null && MenuExtras != null)
            {
                Menus.Separator(m);
                MenuExtras(m, row);
            }
            if (m.Items.Count > 0 && m.Items[m.Items.Count - 1] is ToolStripSeparator) m.Items.RemoveAt(m.Items.Count - 1);
        }

        public void Add(Column<T> c)
        {
            columns.Add(c);
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.HeaderText = c.Header;
            col.SortMode = DataGridViewColumnSortMode.Programmatic;
            col.Width = Dpi.S(c.Width);
            col.MinimumWidth = Dpi.S(40);
            if (c.Fill)
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.FillWeight = c.Width;
            }
            if (c.Mono) col.DefaultCellStyle.Font = Fonts.Mono;
            Grid.Columns.Add(col);
        }

        public Column<T> Col(string header, int width, Func<T, string> text)
        {
            Column<T> c = new Column<T>(header, width, text);
            Add(c);
            return c;
        }

        public List<T> Rows
        {
            get { return rows; }
        }

        public void SetRows(List<T> list)
        {
            T keep = Current;
            rows = list ?? new List<T>();
            Sort();
            Grid.RowCount = 0;
            Grid.RowCount = rows.Count;
            Grid.Invalidate();
            if (keep != null && rows.Contains(keep)) Select(keep);
            else RaiseSelection();
        }

        void Sort()
        {
            if (sortCol < 0 || sortCol >= columns.Count) return;
            Func<T, string> f = columns[sortCol].Text;
            bool desc = sortDesc;
            List<KeyValuePair<string, T>> keyed = new List<KeyValuePair<string, T>>(rows.Count);
            foreach (T r in rows)
            {
                string k;
                try { k = f(r) ?? ""; } catch (Exception) { k = ""; }
                keyed.Add(new KeyValuePair<string, T>(k, r));
            }
            keyed.Sort(delegate (KeyValuePair<string, T> a, KeyValuePair<string, T> b)
            {
                int c = Natural(a.Key, b.Key);
                return desc ? -c : c;
            });
            rows = new List<T>(rows.Count);
            foreach (KeyValuePair<string, T> kv in keyed) rows.Add(kv.Value);
        }

        static int Natural(string a, string b)
        {
            double x, y;
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) return x.CompareTo(y);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public T Current
        {
            get
            {
                if (Grid.CurrentCell == null) return null;
                int i = Grid.CurrentCell.RowIndex;
                return i >= 0 && i < rows.Count ? rows[i] : null;
            }
        }

        public void Select(T item)
        {
            int i = item == null ? -1 : rows.IndexOf(item);
            if (i < 0 || Grid.ColumnCount == 0) return;
            try
            {
                int col = 0;
                while (col < Grid.ColumnCount - 1 && !Grid.Columns[col].Visible) col++;
                Grid.CurrentCell = Grid.Rows[i].Cells[col];
                if (i < Grid.FirstDisplayedScrollingRowIndex || i >= Grid.FirstDisplayedScrollingRowIndex + Grid.DisplayedRowCount(false))
                    Grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
            }
            catch (Exception) { }
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            Grid.BackgroundColor = t.Card;
            Grid.GridColor = t.Divider;
            Grid.DefaultCellStyle.BackColor = t.Card;
            Grid.DefaultCellStyle.ForeColor = t.Text;
            Grid.DefaultCellStyle.SelectionBackColor = t.AccentSoft;
            Grid.DefaultCellStyle.SelectionForeColor = t.Text;
            Grid.DefaultCellStyle.Font = Fonts.Ui;
            Grid.DefaultCellStyle.Padding = new Padding(Dpi.S(6), 0, Dpi.S(4), 0);
            Grid.AlternatingRowsDefaultCellStyle.BackColor = t.Zebra;
            Grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = t.AccentSoft;
            Grid.ColumnHeadersDefaultCellStyle.BackColor = t.Header;
            Grid.ColumnHeadersDefaultCellStyle.ForeColor = t.Text2;
            Grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = t.Header;
            Grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = t.Text2;
            Grid.ColumnHeadersDefaultCellStyle.Font = Fonts.UiSemibold;
            Grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(Dpi.S(6), 0, Dpi.S(4), 0);
            Grid.Invalidate();
        }

        public Theme CurrentTheme
        {
            get { return theme; }
        }
    }
}
