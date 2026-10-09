using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>Base of the owner-drawn controls: double buffering, theme repaint, parent-colour backdrop.</summary>
    public abstract class ThemedControl : Control
    {
        protected bool hover;
        protected bool pressed;
        protected bool keyboardFocus;   // draw the focus ring only after keyboard use

        protected ThemedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected Theme T { get { return Theme.Current; } }

        /// <summary>The colour of whatever the control sits on.</summary>
        protected Color Backdrop { get { return Parent != null ? Parent.BackColor : T.Window; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Changed += OnThemeChanged;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= OnThemeChanged;
            base.OnHandleDestroyed(e);
        }

        void OnThemeChanged()
        {
            if (IsHandleCreated && !IsDisposed) Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { keyboardFocus = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected void DrawFocusRing(Graphics g, RectangleF r, float radius)
        {
            if (Focused && keyboardFocus)
            {
                RectangleF f = r;
                f.Inflate(-Ui.S(2), -Ui.S(2));
                Ui.DrawRound(g, T.Accent, f, radius, 1.6f);
            }
        }
    }

    public enum ButtonStyle { Normal, Primary, Danger, Ghost }

    /// <summary>Rounded flat button with optional icon glyph (Segoe MDL2 Assets) and hover / pressed / focus states.</summary>
    public sealed class FlatButton : ThemedControl
    {
        string glyph = "";
        string fallback = "";
        ButtonStyle style = ButtonStyle.Normal;
        bool active;

        public FlatButton()
        {
            Height = Ui.S(38);
            Cursor = Cursors.Hand;
        }

        /// <summary>Icon code point from Segoe MDL2 Assets.</summary>
        public string Glyph { get { return glyph; } set { glyph = value ?? ""; Invalidate(); } }

        /// <summary>Shown instead of the icon when the icon font is missing.</summary>
        public string Fallback { get { return fallback; } set { fallback = value ?? ""; Invalidate(); } }

        public ButtonStyle Style { get { return style; } set { style = value; Invalidate(); } }

        /// <summary>For toggle buttons: draws the accent colour.</summary>
        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        public string Tip { set { Ui.SetTip(this, value); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; keyboardFocus = false; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                keyboardFocus = true;
                e.Handled = true;
                Invalidate();
                OnClick(EventArgs.Empty);
            }
            else if (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) keyboardFocus = true;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            Color bg = Backdrop;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(bg);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Ui.S(9f);
            Color fill, text;
            switch (style)
            {
                case ButtonStyle.Primary:
                    fill = pressed ? t.AccentPress : (hover ? t.AccentHover : t.Accent);
                    text = t.OnAccent;
                    break;
                case ButtonStyle.Danger:
                    fill = pressed ? Ui.Mix(t.Danger, Color.Black, 0.2f) : (hover ? t.DangerHover : t.Danger);
                    text = Color.White;
                    break;
                case ButtonStyle.Ghost:
                    fill = pressed ? Ui.Mix(t.Field, t.Text, 0.12f) : (hover ? t.Field : bg);
                    text = t.Text;
                    break;
                default:
                    fill = pressed ? Ui.Mix(t.Field, t.Text, 0.14f) : (hover ? Ui.Mix(t.Field, t.Text, 0.07f) : t.Field);
                    text = t.Text;
                    break;
            }
            if (!Enabled)
            {
                fill = Ui.Mix(bg, fill, 0.45f);
                text = Ui.Mix(bg, text, 0.38f);
            }
            if (fill != bg) Ui.FillRound(g, fill, r, radius);
            // a toggled-on button gets an accent outline and icon, except on an accent fill where they would vanish
            bool outlined = active && Enabled && style != ButtonStyle.Primary;
            if (outlined) Ui.DrawRound(g, t.Accent, r, radius, 1.6f);
            Color iconColor = outlined ? t.Accent : text;

            bool useGlyph = glyph.Length > 0 && Ui.HasIcons;
            string label = Text;
            if (!useGlyph && label.Length == 0) label = fallback;

            Font textFont = Ui.Font(9.5f, style == ButtonStyle.Primary ? FontStyle.Bold : FontStyle.Regular);
            Font iconFont = Ui.Icons(11.5f);
            int gap = Ui.S(8);
            Size textSize = label.Length > 0 ? Ui.Measure(g, label, textFont) : Size.Empty;
            Size iconSize = useGlyph ? Ui.Measure(g, glyph, iconFont) : Size.Empty;
            int total = textSize.Width + iconSize.Width + (useGlyph && label.Length > 0 ? gap : 0);
            int x = (Width - total) / 2;
            if (useGlyph)
            {
                Rectangle ir = new Rectangle(x, 0, iconSize.Width + 2, Height);
                Ui.Text(g, glyph, iconFont, iconColor, ir, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                x += iconSize.Width + (label.Length > 0 ? gap : 0);
            }
            if (label.Length > 0)
            {
                Rectangle tr = new Rectangle(x, 0, Math.Max(0, Width - x - Ui.S(6)), Height);
                Ui.Text(g, label, textFont, text, tr, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            DrawFocusRing(g, r, radius);
        }
    }

    /// <summary>A pill-shaped selector (replaces radio buttons and drop-downs). The highlight slides to the chosen item.</summary>
    public sealed class Segmented : ThemedControl
    {
        string[] items = new string[0];
        int selected;
        float pillPos;
        int hoverIndex = -1;
        bool prominent;
        readonly Timer anim = new Timer();

        public event EventHandler SelectedIndexChanged;

        public Segmented()
        {
            Height = Ui.S(38);
            Cursor = Cursors.Hand;
            anim.Interval = 15;
            anim.Tick += delegate
            {
                float diff = selected - pillPos;
                if (Math.Abs(diff) < 0.01f) { pillPos = selected; anim.Stop(); }
                else pillPos += diff * 0.38f;
                Invalidate();
            };
        }

        public string[] Items
        {
            get { return items; }
            set { items = value ?? new string[0]; if (selected >= items.Length) selected = 0; pillPos = selected; Invalidate(); }
        }

        /// <summary>Accent-coloured highlight (for the main mode switch) instead of a neutral one.</summary>
        public bool Prominent { get { return prominent; } set { prominent = value; Invalidate(); } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= items.Length || value == selected) return;
                selected = value;
                if (IsHandleCreated && Visible) anim.Start(); else pillPos = selected;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Changes the selection without raising SelectedIndexChanged (used when applying saved settings).</summary>
        public void SelectSilently(int index)
        {
            if (index < 0 || index >= items.Length) return;
            selected = index;
            pillPos = index;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        int IndexAt(int x)
        {
            if (items.Length == 0) return -1;
            int pad = Ui.S(3);
            float w = (Width - pad * 2f) / items.Length;
            int i = (int)((x - pad) / w);
            return i < 0 || i >= items.Length ? -1 : i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverIndex = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            keyboardFocus = false;
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                int i = IndexAt(e.X);
                if (i >= 0) SelectedIndex = i;
            }
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { keyboardFocus = true; SelectedIndex = Math.Max(0, selected - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { keyboardFocus = true; SelectedIndex = Math.Min(items.Length - 1, selected + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Tab) keyboardFocus = true;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Backdrop);
            RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Ui.S(10f);
            Color trackColor = Enabled ? t.Field : Ui.Mix(Backdrop, t.Field, 0.5f);
            Ui.FillRound(g, trackColor, track, radius);
            if (items.Length == 0) return;

            float pad = Ui.S(3);
            float segW = (Width - pad * 2f) / items.Length;
            RectangleF pill = new RectangleF(pad + pillPos * segW, pad, segW, Height - pad * 2f);
            Color pillColor = prominent ? t.Accent : (t.IsDark ? Ui.Mix(t.Field, t.Text, 0.18f) : t.Card);
            if (!Enabled) pillColor = Ui.Mix(trackColor, pillColor, 0.5f);
            Ui.FillRound(g, pillColor, pill, radius - 2f);
            if (!prominent && !t.IsDark) Ui.DrawRound(g, t.Border, pill, radius - 2f, 1f);

            for (int i = 0; i < items.Length; i++)
            {
                bool sel = i == selected;
                Color c = sel ? (prominent ? t.OnAccent : t.Text) : (i == hoverIndex && Enabled ? t.Text : t.Muted);
                if (!Enabled) c = Ui.Mix(trackColor, c, 0.45f);
                Font f = Ui.Font(9.5f, sel ? FontStyle.Bold : FontStyle.Regular);
                Rectangle cell = new Rectangle((int)(pad + i * segW), 0, (int)segW, Height);
                Ui.Text(g, items[i], f, c, cell, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            DrawFocusRing(g, track, radius);
        }
    }

    /// <summary>A slider with a few labelled stops (difficulty level).</summary>
    public sealed class StepSlider : ThemedControl
    {
        string[] labels = new string[0];
        int value;
        bool dragging;

        public event EventHandler ValueChanged;

        public StepSlider()
        {
            Height = Ui.S(58);
            Cursor = Cursors.Hand;
        }

        public string[] Labels
        {
            get { return labels; }
            set { labels = value ?? new string[0]; if (this.value >= labels.Length) this.value = 0; Invalidate(); }
        }

        public int Value
        {
            get { return value; }
            set
            {
                if (value < 0 || value >= labels.Length || value == this.value) return;
                this.value = value;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilently(int v)
        {
            if (v < 0 || v >= labels.Length) return;
            value = v;
            Invalidate();
        }

        float TrackLeft { get { return Ui.S(10); } }
        float TrackRight { get { return Width - Ui.S(10); } }
        float StopX(int i) { return labels.Length <= 1 ? TrackLeft : TrackLeft + (TrackRight - TrackLeft) * i / (labels.Length - 1); }
        float TrackY { get { return Height - Ui.S(14); } }

        int NearestStop(int x)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < labels.Length; i++)
            {
                float d = Math.Abs(StopX(i) - x);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                dragging = true;
                keyboardFocus = false;
                Focus();
                Value = NearestStop(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) Value = NearestStop(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { keyboardFocus = true; Value = Math.Max(0, value - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { keyboardFocus = true; Value = Math.Min(labels.Length - 1, value + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Tab) keyboardFocus = true;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Backdrop;
            g.Clear(bg);
            if (labels.Length == 0) return;

            Color text = Enabled ? t.Text : Ui.Mix(bg, t.Text, 0.4f);
            Color muted = Enabled ? t.Muted : Ui.Mix(bg, t.Muted, 0.5f);
            Rectangle top = new Rectangle(Ui.S(2), 0, Width - Ui.S(4), Height - Ui.S(26));
            Ui.Text(g, labels[value], Ui.Font(10f, FontStyle.Bold), text, top, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Ui.Text(g, (value + 1) + " / " + labels.Length, Ui.Font(9f), muted, top, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            float y = TrackY;
            float th = Ui.S(4f);
            Ui.FillRound(g, t.Field, new RectangleF(TrackLeft, y - th / 2f, TrackRight - TrackLeft, th), th / 2f);
            Color accent = Enabled ? t.Accent : Ui.Mix(bg, t.Accent, 0.4f);
            float x = StopX(value);
            Ui.FillRound(g, accent, new RectangleF(TrackLeft, y - th / 2f, x - TrackLeft, th), th / 2f);
            float dot = Ui.S(3f);
            for (int i = 0; i < labels.Length; i++)
            {
                using (SolidBrush b = new SolidBrush(i <= value ? accent : Ui.Mix(t.Field, t.Muted, 0.55f)))
                    g.FillEllipse(b, StopX(i) - dot, y - dot, dot * 2, dot * 2);
            }
            float tr = Ui.S(8f);
            using (SolidBrush halo = new SolidBrush(Ui.Alpha(accent, hover || dragging ? 70 : 0)))
                g.FillEllipse(halo, x - tr - Ui.S(4), y - tr - Ui.S(4), (tr + Ui.S(4)) * 2, (tr + Ui.S(4)) * 2);
            using (SolidBrush b = new SolidBrush(t.IsDark ? Color.FromArgb(245, 247, 250) : Color.White))
                g.FillEllipse(b, x - tr, y - tr, tr * 2, tr * 2);
            using (Pen p = new Pen(accent, 2.2f))
                g.DrawEllipse(p, x - tr, y - tr, tr * 2, tr * 2);
            if (Focused && keyboardFocus)
                Ui.DrawRound(g, accent, new RectangleF(1, 1, Width - 2, Height - 2), Ui.S(8f), 1.4f);
        }
    }

    /// <summary>A rounded raised panel; children are placed with normal docking/anchoring inside its Padding.</summary>
    public class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Current.Card;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BackColor = Theme.Current.Card;
            Theme.Changed += OnThemeChanged;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= OnThemeChanged;
            base.OnHandleDestroyed(e);
        }

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            BackColor = Theme.Current.Card;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Window);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.FillRound(g, t.Card, r, Ui.S(14f));
            Ui.DrawRound(g, t.Border, r, Ui.S(14f), 1f);
        }
    }
}

namespace ChessGame
{
    /// <summary>A Panel without flicker (the stock one is not double buffered).</summary>
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    public class BufferedTable : TableLayoutPanel
    {
        public BufferedTable()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    /// <summary>Base of every dialog: theme colours, dark title bar, fixed size, centred over the owner.</summary>
    public class ThemedDialog : Form
    {
        public ThemedDialog()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);
            BackColor = Theme.Current.Window;
            ForeColor = Theme.Current.Text;
            KeyPreview = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme t = Theme.Current;
            NativeUi.StyleTitleBar(Handle, t.IsDark, t.Window, t.Text);
        }
    }

    /// <summary>A yes/no (or just OK) question in the program's own style instead of a grey MessageBox.</summary>
    public sealed class AskDialog : ThemedDialog
    {
        bool result;

        AskDialog(string title, string text, string yes, string no, ButtonStyle yesStyle)
        {
            Text = title;
            int width = Ui.S(440), pad = Ui.S(22);
            Size textSize = TextRenderer.MeasureText(text, Ui.Font(10f), new Size(width - pad * 2, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            Label body = new Label();
            body.Text = text;
            body.Font = Ui.Font(10f);
            body.ForeColor = Theme.Current.Text;
            body.BackColor = Color.Transparent;
            body.AutoSize = false;
            body.SetBounds(pad, pad, width - pad * 2, textSize.Height + Ui.S(4));
            Controls.Add(body);

            int btnH = Ui.S(38), btnW = Ui.S(130), gap = Ui.S(10);
            int y = body.Bottom + pad;
            ClientSize = new Size(width, y + btnH + pad);

            FlatButton yesButton = new FlatButton();
            yesButton.Text = yes;
            yesButton.Style = yesStyle;
            yesButton.SetBounds(width - pad - btnW, y, btnW, btnH);
            yesButton.Click += delegate { result = true; DialogResult = DialogResult.OK; };
            Controls.Add(yesButton);
            if (no != null)
            {
                FlatButton noButton = new FlatButton();
                noButton.Text = no;
                noButton.SetBounds(yesButton.Left - gap - btnW, y, btnW, btnH);
                noButton.Click += delegate { result = false; DialogResult = DialogResult.Cancel; };
                Controls.Add(noButton);
            }
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { result = false; DialogResult = DialogResult.Cancel; }
            };
        }

        static AskDialog current;

        /// <summary>Closes the question that is on screen right now (answer: no), e.g. when it became pointless.</summary>
        public static void CloseCurrent()
        {
            AskDialog d = current;
            if (d != null && !d.IsDisposed) { d.result = false; d.DialogResult = DialogResult.Cancel; }
        }

        public static bool Ask(IWin32Window owner, string title, string text, string yes, string no)
        {
            using (AskDialog d = new AskDialog(title, text, yes, no, ButtonStyle.Primary))
            {
                current = d;
                try { d.ShowDialog(owner); }
                finally { current = null; }
                return d.result;
            }
        }

        /// <summary>A message with a single OK button.</summary>
        public static void Info(IWin32Window owner, string title, string text)
        {
            using (AskDialog d = new AskDialog(title, text, "OK", null, ButtonStyle.Primary))
                d.ShowDialog(owner);
        }
    }
}
