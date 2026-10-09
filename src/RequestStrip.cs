using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>One question for the player ("Алиса хочет сесть за стол") with the answers offered under it.</summary>
    public sealed class RequestItem
    {
        public string Key;        // identity of the request: the strip repaints only when keys or texts change
        public string Text;
        public string YesText;
        public string NoText;     // null or empty: a single button
        public Action Yes;
        public Action No;
    }

    /// <summary>
    /// A non-modal list of requests that sits on a settings card. Every request is a small inner card with the wrapped text
    /// and one or two buttons under it. The control sizes itself (Height and GetPreferredSize) to what it shows, so an AutoSize
    /// table row can hold it, and hides itself when there is nothing to show. It never takes the keyboard focus.
    /// </summary>
    public sealed class RequestStrip : Control
    {
        const int MaxShown = 4;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.Left | TextFormatFlags.Top;

        sealed class Slot
        {
            public RequestItem Item;
            public string Shown;                 // the text with hard breaks inside words that are wider than the card
            public Rectangle Card, TextR, YesR, NoR;   // NoR is empty for a single button
        }

        sealed class Plan
        {
            public int Width, Height;
            public List<Slot> Slots = new List<Slot>();
            public int More;
            public Rectangle MoreR;
        }

        readonly List<RequestItem> items = new List<RequestItem>();
        string signature = "";
        Plan layout;
        int hotItem = -1, hotButton = -1;       // button under the mouse: item index and 0 = yes / 1 = no
        int downItem = -1, downButton = -1;     // button held down

        /// <summary>The height this strip wants changed (also applied to Height); use it when the parent does not auto-size.</summary>
        public event Action<int> HeightWanted;

        public RequestStrip()
        {
            // not Selectable: clicking a button must not steal the keyboard from the board
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Visible = false;
            Height = 0;
        }

        Theme T { get { return Theme.Current; } }

        public int Count { get { return items.Count; } }

        // ------------------------------------------------------------ content

        /// <summary>Replaces the requests. Cheap to call often: nothing is rebuilt or repainted when keys and texts are the same.</summary>
        public void SetItems(IList<RequestItem> list)
        {
            StringBuilder sb = new StringBuilder();
            List<RequestItem> fresh = new List<RequestItem>();
            if (list != null)
                foreach (RequestItem it in list)
                {
                    if (it == null) continue;
                    fresh.Add(it);
                    sb.Append(it.Key).Append('\u0001').Append(it.Text).Append('\u0001').Append(it.YesText).Append('\u0001')
                      .Append(it.NoText).Append('\u0002');
                }
            // the callbacks may be new delegates every time: always take the latest ones
            items.Clear();
            items.AddRange(fresh);
            string sig = sb.ToString();
            if (sig == signature) return;
            signature = sig;

            hotItem = hotButton = downItem = downButton = -1;
            Relayout();
            Visible = items.Count > 0;
            Invalidate();
        }

        // ------------------------------------------------------------ layout

        static Bitmap measureBmp;
        static Graphics measureG;

        /// <summary>Measured on a bitmap DC like the one painting uses: the screen DC wraps a pixel or two differently.</summary>
        static Size MeasureText(string text, Font font, int width)
        {
            if (measureG == null)
            {
                measureBmp = new Bitmap(1, 1);
                measureG = Graphics.FromImage(measureBmp);
            }
            return TextRenderer.MeasureText(measureG, text, font, new Size(width, 0), Wrap);
        }

        /// <summary>GDI cuts a single word wider than the line with "..."; break it here so that nothing is ever clipped.</summary>
        static string BreakLongWords(string text, Font font, int width)
        {
            StringBuilder sb = null;
            int pos = 0;
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                if (i - start < 4 || MeasureText(text.Substring(start, i - start), font, 10000).Width <= width) continue;

                if (sb == null) sb = new StringBuilder(text.Length + 8);
                sb.Append(text, pos, start - pos);
                int at = start;
                while (at < i)
                {
                    int lo = 1, hi = i - at;   // the longest piece that still fits (at least one char)
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (MeasureText(text.Substring(at, mid), font, 10000).Width <= width) lo = mid; else hi = mid - 1;
                    }
                    sb.Append(text, at, lo);
                    at += lo;
                    if (at < i) sb.Append('\n');
                }
                pos = i;
            }
            if (sb == null) return text;
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        Plan BuildLayout(int width)
        {
            Plan lay = new Plan();
            lay.Width = width;
            Font body = Ui.Font(9.5f), btnFont = Ui.Font(9.5f, FontStyle.Bold);
            int padX = Ui.S(12), padTop = Ui.S(10), padBottom = Ui.S(10);
            int btnH = Ui.S(32), btnGap = Ui.S(8), textGap = Ui.S(8), cardGap = Ui.S(8);
            int textW = Math.Max(Ui.S(40), width - padX * 2);
            int y = 0;

            int shown = Math.Min(items.Count, MaxShown);
            for (int i = 0; i < shown; i++)
            {
                RequestItem it = items[i];
                Slot s = new Slot();
                s.Item = it;
                string text = (it.Text ?? "").Trim();
                s.Shown = BreakLongWords(text.Length == 0 ? " " : text, body, textW);
                Size ts = MeasureText(s.Shown, body, textW);

                int top = y;
                s.TextR = new Rectangle(padX, top + padTop, textW, ts.Height + Ui.S(2));
                int by = s.TextR.Bottom + textGap;
                bool two = !string.IsNullOrEmpty(it.NoText);
                string yes = it.YesText ?? "";
                int inner = width - padX * 2;
                if (two)
                {
                    int half = (inner - btnGap) / 2;
                    int need = Math.Max(MeasureText(yes, btnFont, 10000).Width, MeasureText(it.NoText, Ui.Font(9.5f), 10000).Width) + Ui.S(24);
                    if (need <= half)
                    {
                        s.YesR = new Rectangle(padX, by, half, btnH);
                        s.NoR = new Rectangle(padX + half + btnGap, by, half, btnH);
                        by += btnH;
                    }
                    else
                    {
                        // labels too long for side by side: stack them, nothing gets cut
                        s.YesR = new Rectangle(padX, by, inner, btnH);
                        s.NoR = new Rectangle(padX, by + btnH + btnGap, inner, btnH);
                        by += btnH * 2 + btnGap;
                    }
                }
                else
                {
                    int w = Math.Min(inner, Math.Max(Ui.S(120), MeasureText(yes, btnFont, 10000).Width + Ui.S(32)));
                    s.YesR = new Rectangle(padX, by, w, btnH);
                    s.NoR = Rectangle.Empty;
                    by += btnH;
                }
                int bottom = by + padBottom;
                s.Card = new Rectangle(0, top, width, bottom - top);
                lay.Slots.Add(s);
                y = bottom + cardGap;
            }

            lay.More = items.Count - shown;
            if (lay.More > 0)
            {
                int h = MeasureText("Ag", Ui.Font(9f), 10000).Height;
                lay.MoreR = new Rectangle(padX, y, textW, h + Ui.S(2));
                y += lay.MoreR.Height + Ui.S(2);
            }
            else if (lay.Slots.Count > 0) y -= cardGap;   // no gap after the last card
            lay.Height = Math.Max(0, y);
            return lay;
        }

        int WorkWidth { get { return Width > 0 ? Width : Ui.S(300); } }

        void Relayout()
        {
            layout = BuildLayout(WorkWidth);
            if (Height != layout.Height) Height = layout.Height;
            if (HeightWanted != null) HeightWanted(layout.Height);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : WorkWidth;
            if (items.Count == 0) return new Size(w, 0);
            if (layout != null && layout.Width == w) return new Size(w, layout.Height);
            return new Size(w, BuildLayout(w).Height);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width > 0 && (layout == null || layout.Width != Width))
            {
                // a new width wraps differently, which changes the height; the width itself stays, so this cannot loop
                Plan old = layout;
                layout = BuildLayout(Width);
                if (Height != layout.Height) Height = layout.Height;
                if ((old == null || old.Height != layout.Height) && HeightWanted != null) HeightWanted(layout.Height);
            }
        }

        // ------------------------------------------------------------ input

        bool HitButton(Point p, out int item, out int button)
        {
            item = button = -1;
            if (layout == null) return false;
            for (int i = 0; i < layout.Slots.Count; i++)
            {
                Slot s = layout.Slots[i];
                if (s.YesR.Contains(p)) { item = i; button = 0; return true; }
                if (!s.NoR.IsEmpty && s.NoR.Contains(p)) { item = i; button = 1; return true; }
            }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i, b;
            HitButton(e.Location, out i, out b);
            if (i != hotItem || b != hotButton)
            {
                hotItem = i;
                hotButton = b;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hotItem = hotButton = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                HitButton(e.Location, out downItem, out downButton);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int di = downItem, db = downButton;
            downItem = downButton = -1;
            Invalidate();
            if (e.Button == MouseButtons.Left && di >= 0)
            {
                int i, b;
                if (HitButton(e.Location, out i, out b) && i == di && b == db && i < items.Count)
                {
                    // the caller removes the request and calls SetItems again; the action is only invoked here
                    Action a = b == 0 ? items[i].Yes : items[i].No;
                    if (a != null) a();
                }
            }
            base.OnMouseUp(e);
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            Color bg = Parent != null ? Parent.BackColor : t.Card;
            g.Clear(bg);
            if (layout == null || layout.Width != Width) layout = BuildLayout(Math.Max(1, Width));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Font body = Ui.Font(9.5f);
            for (int i = 0; i < layout.Slots.Count; i++)
            {
                Slot s = layout.Slots[i];
                RectangleF r = new RectangleF(s.Card.X + 0.5f, s.Card.Y + 0.5f, s.Card.Width - 1f, s.Card.Height - 1f);
                Color cardFill = Ui.Mix(bg, t.Warn, t.IsDark ? 0.12f : 0.09f);
                Ui.FillRound(g, cardFill, r, Ui.S(12f));
                Ui.DrawRound(g, Ui.Alpha(t.Warn, t.IsDark ? 130 : 150), r, Ui.S(12f), 1.2f);
                Ui.Text(g, s.Shown, body, t.Text, s.TextR, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

                DrawButton(g, s.YesR, s.Item.YesText, true, i, 0, cardFill);
                if (!s.NoR.IsEmpty) DrawButton(g, s.NoR, s.Item.NoText, false, i, 1, cardFill);
            }
            if (layout.More > 0)
                Ui.Text(g, "и ещё " + layout.More + "…", Ui.Font(9f), t.Muted, layout.MoreR, TextFormatFlags.Left | TextFormatFlags.Top);
        }

        /// <summary>Looks like FlatButton (Primary / Normal) but is hit-tested here, so no child control can grab the focus.</summary>
        void DrawButton(Graphics g, Rectangle rect, string label, bool primary, int item, int button, Color cardFill)
        {
            Theme t = T;
            bool hover = hotItem == item && hotButton == button;
            bool pressed = downItem == item && downButton == button && hover;
            Color fill, text;
            if (primary)
            {
                fill = pressed ? t.AccentPress : (hover ? t.AccentHover : t.Accent);
                text = t.OnAccent;
            }
            else
            {
                Color baseFill = t.Field;
                fill = pressed ? Ui.Mix(baseFill, t.Text, 0.14f) : (hover ? Ui.Mix(baseFill, t.Text, 0.07f) : baseFill);
                text = t.Text;
            }
            RectangleF r = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f);
            Ui.FillRound(g, fill, r, Ui.S(9f));
            if (!primary) Ui.DrawRound(g, Ui.Alpha(t.Border, 200), r, Ui.S(9f), 1f);
            Ui.Text(g, label ?? "", Ui.Font(9.5f, primary ? FontStyle.Bold : FontStyle.Regular), text, rect,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

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

        void OnThemeChanged() { if (!IsDisposed) Invalidate(); }
    }
}
