using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>A list with rounded, theme-coloured rows. Items are drawn with ToString().</summary>
    public sealed class ThemedListBox : ListBox
    {
        public ThemedListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            ItemHeight = Ui.S(42);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Changed += OnThemeChanged;
            BackColor = Theme.Current.Field;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= OnThemeChanged;
            base.OnHandleDestroyed(e);
        }

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            BackColor = Theme.Current.Field;
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(t.Field)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;

            bool selected = (e.State & DrawItemState.Selected) != 0;
            RectangleF row = new RectangleF(e.Bounds.X + Ui.S(4), e.Bounds.Y + Ui.S(2), e.Bounds.Width - Ui.S(8), e.Bounds.Height - Ui.S(4));
            if (selected) Ui.FillRound(g, Ui.Alpha(t.Accent, t.IsDark ? 70 : 55), row, Ui.S(8f));
            Rectangle text = new Rectangle((int)row.X + Ui.S(12), (int)row.Y, (int)row.Width - Ui.S(20), (int)row.Height);
            Ui.Text(g, Items[e.Index].ToString(), Ui.Font(10f, selected ? FontStyle.Bold : FontStyle.Regular), t.Text, text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>A single-line text box on a rounded field.</summary>
    public sealed class ThemedTextBox : Panel
    {
        readonly TextBox box = new TextBox();
        string cue = "";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, string lparam);

        /// <summary>Enter was pressed in the box (the key press itself is swallowed).</summary>
        public event Action EnterPressed;

        public int MaxLength { get { return box.MaxLength; } set { box.MaxLength = value; } }

        /// <summary>Grey hint shown while the box is empty.</summary>
        public string Cue
        {
            get { return cue; }
            set { cue = value ?? ""; ApplyCue(); }
        }

        void ApplyCue()
        {
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, cue);
        }

        public ThemedTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(40);
            box.BorderStyle = BorderStyle.None;
            box.Font = Ui.Font(10.5f);
            box.GotFocus += delegate { Invalidate(); };
            box.LostFocus += delegate { Invalidate(); };
            box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            box.HandleCreated += delegate { ApplyCue(); };
            box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    if (EnterPressed != null) EnterPressed();
                }
            };
            Controls.Add(box);
            ApplyColors();
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }

        public void SelectAllText() { box.SelectAll(); }

        public new bool Focus() { return box.Focus(); }

        void ApplyColors()
        {
            Theme t = Theme.Current;
            BackColor = t.Card;
            box.BackColor = t.Field;
            box.ForeColor = t.Text;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Changed += OnThemeChanged;
            ApplyColors();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= OnThemeChanged;
            base.OnHandleDestroyed(e);
        }

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            ApplyColors();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = box.PreferredHeight;
            box.SetBounds(Ui.S(12), (Height - h) / 2, Math.Max(10, Width - Ui.S(24)), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Window);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.FillRound(g, t.Field, r, Ui.S(9f));
            Ui.DrawRound(g, box.Focused ? t.Accent : t.Border, r, Ui.S(9f), box.Focused ? 1.6f : 1f);
        }
    }
}
