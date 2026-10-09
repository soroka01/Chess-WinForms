using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// The chat message list: bubbles (ours on the right, the opponent's on the left) and centred system notices.
    /// Wheel scrolling and a thin own scrollbar like MoveListControl; follows new messages unless the user scrolled up.
    /// Right-click or double-click on a bubble copies its text.
    /// </summary>
    public sealed class ChatControl : Control
    {
        const int MaxMessages = 500;   // the oldest are dropped beyond this
        const int MaxChars = 2000;     // one message never gets longer than this
        const int Theirs = 0, Mine = 1, Notice = 2;

        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.Left | TextFormatFlags.Top;

        sealed class Msg
        {
            public int Kind;
            public string Text;
            public string Shown;        // Text with hard breaks inside words that are wider than the bubble
            public string Time;
            public string Author;       // the sender's name above the text (opponent's bubbles only), or null
            public int AuthorW, AuthorH;   // measured name line; AuthorH is 0 without a name
            public int LaidW = -1;      // the control width the fields below were measured for
            public int Y, H;            // in content coordinates
            public int BX, BW;          // bubble (or notice text) column
            public int TW, TH;          // wrapped text size
            public bool Inline;         // the time sits at the end of the single text line
        }

        readonly List<Msg> msgs = new List<Msg>();
        int contentH;
        int scroll;                 // pixels
        bool thumbDrag;
        int thumbGrab;
        bool thumbHover;

        // metrics of the current layout
        int colLeft, colRight, maxText, lineH, timeW, timeH, noticeW;

        public ChatControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = false;
            contentH = Ui.S(16);
        }

        Theme T { get { return Theme.Current; } }
        int MaxScroll { get { return Math.Max(0, contentH - Height); } }
        int EdgePad { get { return Ui.S(8); } }

        public int Count { get { return msgs.Count; } }

        /// <summary>A message from us (mine) or from the opponent.</summary>
        public void Add(bool mine, string text, DateTime time)
        {
            Push(mine ? Mine : Theirs, text, time.ToString("HH:mm", CultureInfo.InvariantCulture), null);
        }

        /// <summary>
        /// A message with the sender's name: for somebody else's bubble the name is drawn as a small coloured line above the text
        /// (the colour is stable per name); for our own the author is ignored.
        /// </summary>
        public void Add(bool mine, string text, DateTime time, string author)
        {
            Push(mine ? Mine : Theirs, text, time.ToString("HH:mm", CultureInfo.InvariantCulture), mine ? null : author);
        }

        /// <summary>A centred, muted notice without a bubble.</summary>
        public void AddSystem(string text)
        {
            Push(Notice, text, "", null);
        }

        public void Clear()
        {
            msgs.Clear();
            scroll = 0;
            contentH = EdgePad * 2;
            Invalidate();
        }

        void Push(int kind, string text, string time, string author)
        {
            if (text == null) text = "";
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            // our own message always scrolls into view; the opponent's only when we were already at the end
            bool follow = kind == Mine || scroll >= MaxScroll - 2;

            Msg m = new Msg();
            m.Kind = kind;
            m.Text = text;
            m.Time = time;
            if (author != null)
            {
                // one line only; the same cleaning as a name coming from the network
                author = author.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
                if (author.Length > 40) author = author.Substring(0, 40);
                if (author.Length > 0) m.Author = author;
            }
            int shift = 0;
            if (msgs.Count >= MaxMessages)
            {
                shift = msgs[1].Y - EdgePad;   // how far the content moves up when the first message goes
                msgs.RemoveAt(0);
            }
            msgs.Add(m);
            Arrange();
            if (follow) scroll = MaxScroll;
            else scroll = Math.Max(0, Math.Min(MaxScroll, scroll - shift));
            Invalidate();
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

        /// <summary>GDI does not break a single word wider than the line (it cuts it with "..."), so do it here.</summary>
        static string BreakLongWords(string text, Font font, int width)
        {
            StringBuilder sb = null;
            int pos = 0;   // text up to pos is already copied to sb
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                if (i - start < 6 || MeasureText(text.Substring(start, i - start), font, 10000).Width <= width) continue;

                if (sb == null) sb = new StringBuilder(text.Length + 16);
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

        /// <summary>Positions every message; only the ones not yet measured for this width are measured again.</summary>
        void Arrange()
        {
            int listW = Width - Ui.S(10);   // without the scrollbar gutter
            colLeft = Ui.S(10);
            colRight = Math.Max(colLeft + Ui.S(60), Width - Ui.S(14));
            int maxBubble = (int)((colRight - colLeft) * 0.78f);
            maxText = Math.Max(Ui.S(24), maxBubble - Ui.S(10) * 2);
            noticeW = Math.Max(Ui.S(40), listW - Ui.S(32));
            lineH = MeasureText("Ag", Ui.Font(10f), 10000).Height;
            Size ts = MeasureText("00:00", Ui.Font(7.5f), 10000);
            timeW = ts.Width + Ui.S(1);
            timeH = ts.Height;

            int y = EdgePad;
            for (int i = 0; i < msgs.Count; i++)
            {
                Msg m = msgs[i];
                if (m.LaidW != Width) MeasureMsg(m);
                if (i > 0)
                {
                    Msg prev = msgs[i - 1];
                    if (m.Kind == Notice || prev.Kind == Notice) y += Ui.S(12);
                    else y += m.Kind == prev.Kind ? Ui.S(3) : Ui.S(10);   // a change of side gets more air
                }
                m.Y = y;
                y += m.H;
            }
            contentH = y + EdgePad;
        }

        void MeasureMsg(Msg m)
        {
            m.LaidW = Width;
            if (m.Kind == Notice)
            {
                m.Shown = BreakLongWords(m.Text, Ui.Font(8.5f), noticeW);
                Size ns = MeasureText(m.Shown, Ui.Font(8.5f), noticeW);
                m.BX = Ui.S(16);
                m.BW = noticeW;
                m.TW = noticeW;
                m.TH = ns.Height;
                m.H = ns.Height;
                return;
            }

            int padX = Ui.S(10), padTop = Ui.S(6), padBottom = Ui.S(6);
            m.AuthorW = 0;
            m.AuthorH = 0;
            if (m.Kind == Theirs && m.Author != null)
            {
                Size an = MeasureText(m.Author, Ui.Font(8.5f, FontStyle.Bold), 10000);
                m.AuthorW = Math.Min(maxText, an.Width + Ui.S(2));
                m.AuthorH = an.Height + Ui.S(1);
            }
            // an empty text still gets one line
            m.Shown = BreakLongWords(m.Text.Length == 0 ? " " : m.Text, Ui.Font(10f), maxText);
            Size sz = MeasureText(m.Shown, Ui.Font(10f), maxText);
            m.TW = Math.Min(maxText, sz.Width + Ui.S(2));
            m.TH = sz.Height;

            int gap = Ui.S(8);
            m.Inline = m.Shown.IndexOf('\n') < 0 && sz.Height <= lineH + 2 && m.TW + gap + timeW <= maxText;
            if (m.Inline)
            {
                m.BW = Math.Max(m.TW + gap + timeW, m.AuthorW) + padX * 2;
                m.H = padTop + m.AuthorH + lineH + padBottom;
            }
            else
            {
                m.BW = Math.Max(Math.Max(m.TW, timeW), m.AuthorW) + padX * 2;
                m.H = padTop + m.AuthorH + m.TH + Ui.S(2) + timeH + padBottom - Ui.S(1);
            }
            m.BX = m.Kind == Mine ? colRight - m.BW : colLeft;
        }

        /// <summary>Index of the first message that ends below the given content offset.</summary>
        int FirstVisible(int offset)
        {
            int lo = 0, hi = msgs.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (msgs[mid].Y + msgs[mid].H > offset) hi = mid; else lo = mid + 1;
            }
            return Math.Min(lo, Math.Max(0, msgs.Count - 1));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            if (msgs.Count > 0)
            {
                bool atBottom = scroll >= MaxScroll - 2;
                int first = FirstVisible(scroll);
                int into = scroll - msgs[first].Y;   // keep the same message at the top of the view
                Arrange();
                scroll = atBottom ? MaxScroll : Math.Max(0, Math.Min(MaxScroll, msgs[first].Y + into));
            }
            else if (scroll > MaxScroll) scroll = MaxScroll;
            base.OnSizeChanged(e);
        }

        // ------------------------------------------------------------ geometry

        RectangleF ThumbRect()
        {
            if (MaxScroll <= 0) return RectangleF.Empty;
            float trackH = Height - Ui.S(8);
            float thumbH = Math.Max(Ui.S(28), trackH * Height / contentH);
            float y = Ui.S(4) + (trackH - thumbH) * scroll / MaxScroll;
            return new RectangleF(Width - Ui.S(8), y, Ui.S(5), thumbH);
        }

        Rectangle HitRect(Msg m)
        {
            return new Rectangle(m.BX, m.Y - scroll, m.BW, m.H);
        }

        Msg MessageAt(Point p)
        {
            if (p.X >= Width - Ui.S(12) && MaxScroll > 0) return null;
            for (int i = FirstVisible(scroll); i < msgs.Count; i++)
            {
                if (msgs[i].Y - scroll > Height) break;
                if (HitRect(msgs[i]).Contains(p)) return msgs[i];
            }
            return null;
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseEnter(EventArgs e)
        {
            if (!Focused) Focus();   // so that the wheel works without clicking first
            base.OnMouseEnter(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                float trackH = Height - Ui.S(8);
                RectangleF th = ThumbRect();
                float range = trackH - th.Height;
                if (range > 0) scroll = (int)Math.Max(0, Math.Min(MaxScroll, (e.Y - thumbGrab - Ui.S(4)) / range * MaxScroll));
                Invalidate();
            }
            else
            {
                bool overThumb = ThumbRect().Contains(e.Location) || (MaxScroll > 0 && e.X >= Width - Ui.S(12));
                if (overThumb != thumbHover)
                {
                    thumbHover = overThumb;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            thumbHover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                RectangleF th = ThumbRect();
                if (MaxScroll > 0 && e.X >= Width - Ui.S(12))
                {
                    thumbDrag = true;
                    thumbGrab = th.Contains(e.Location) ? (int)(e.Y - th.Y) : (int)(th.Height / 2);
                    OnMouseMove(e);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (thumbDrag) thumbDrag = false;
            else if (e.Button == MouseButtons.Right) CopyAt(e.Location);
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !thumbDrag) CopyAt(e.Location);
            base.OnMouseDoubleClick(e);
        }

        void CopyAt(Point p)
        {
            Msg m = MessageAt(p);
            if (m == null || m.Text.Length == 0) return;
            try { Clipboard.SetText(m.Text); }
            catch (Exception) { }   // the clipboard may be locked by another program
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - Math.Sign(e.Delta) * lineH * 4));
            Invalidate();
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------ author colours

        // eight hues that stay readable on the opponent's bubble: lighter ones for the dark theme, deeper ones for the light
        static readonly Color[] AuthorDark =
        {
            Color.FromArgb(240, 150, 120), Color.FromArgb(232, 190, 90), Color.FromArgb(140, 205, 110), Color.FromArgb(90, 200, 190),
            Color.FromArgb(110, 170, 240), Color.FromArgb(170, 150, 240), Color.FromArgb(230, 130, 190), Color.FromArgb(190, 200, 100)
        };
        static readonly Color[] AuthorLight =
        {
            Color.FromArgb(196, 84, 52), Color.FromArgb(160, 108, 8), Color.FromArgb(52, 130, 60), Color.FromArgb(20, 128, 122),
            Color.FromArgb(36, 100, 190), Color.FromArgb(108, 76, 190), Color.FromArgb(180, 60, 130), Color.FromArgb(104, 120, 10)
        };

        /// <summary>The same name always gets the same colour (FNV-1a over the characters, not string.GetHashCode).</summary>
        static Color AuthorColor(string name, bool dark)
        {
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++) h = (h ^ (uint)name[i]) * 16777619u;
            return (dark ? AuthorDark : AuthorLight)[(int)(h % 8)];
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            Color bg = Parent != null ? Parent.BackColor : t.Card;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (msgs.Count == 0)
            {
                Ui.Text(g, "Сообщений пока нет", Ui.Font(10f), t.Muted, new Rectangle(0, 0, Width, Height - Ui.S(14)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Ui.Text(g, "Напишите сопернику — он увидит сразу", Ui.Font(8.5f), Ui.Mix(bg, t.Muted, 0.7f),
                    new Rectangle(0, Ui.S(16), Width, Height - Ui.S(2)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // ours: accent tinted into the card colour; the opponent's: the neutral field colour
            Color mineFill = Ui.Mix(bg, t.Accent, t.IsDark ? 0.30f : 0.24f);
            Color theirFill = t.IsDark ? t.Field : Ui.Mix(t.Field, t.Border, 0.35f);   // a bit stronger on white
            Font body = Ui.Font(10f), small = Ui.Font(7.5f), note = Ui.Font(8.5f), authorFont = Ui.Font(8.5f, FontStyle.Bold);
            Color noteColor = Ui.Mix(bg, t.Muted, 0.9f);
            float radius = Ui.S(12f);
            int padX = Ui.S(10), padTop = Ui.S(6);

            for (int i = FirstVisible(scroll); i < msgs.Count; i++)
            {
                Msg m = msgs[i];
                int y = m.Y - scroll;
                if (y > Height) break;

                if (m.Kind == Notice)
                {
                    Ui.Text(g, m.Shown, note, noteColor, new Rectangle(m.BX, y, m.BW, m.H + Ui.S(2)),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.Top);
                    continue;
                }

                bool mine = m.Kind == Mine;
                Color fill = mine ? mineFill : theirFill;
                Ui.FillRound(g, fill, new RectangleF(m.BX, y, m.BW, m.H), radius);
                int ty0 = y + padTop;
                if (m.AuthorH > 0)
                {
                    Ui.Text(g, m.Author, authorFont, AuthorColor(m.Author, t.IsDark),
                        new Rectangle(m.BX + padX, ty0, m.BW - padX * 2, m.AuthorH), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                    ty0 += m.AuthorH;
                }
                Ui.Text(g, m.Shown, body, t.Text, new Rectangle(m.BX + padX, ty0, m.TW, m.TH + Ui.S(2)),
                    TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.Top);

                Color timeColor = Ui.Mix(fill, t.Muted, 0.9f);
                int tx = m.BX + m.BW - padX - timeW;
                int ty = m.Inline ? y + padTop + m.AuthorH + (lineH - timeH) / 2 + Ui.S(1) : y + m.H - Ui.S(5) - timeH;
                Ui.Text(g, m.Time, small, timeColor, new Rectangle(tx, ty, timeW + Ui.S(2), timeH),
                    TextFormatFlags.Right | TextFormatFlags.Top);
            }

            RectangleF thumb = ThumbRect();
            if (!thumb.IsEmpty)
            {
                float w = thumbHover || thumbDrag ? Ui.S(6) : Ui.S(4);
                RectangleF tr = new RectangleF(Width - Ui.S(6) - w / 2f, thumb.Y, w, thumb.Height);
                Ui.FillRound(g, Ui.Alpha(t.Muted, thumbHover || thumbDrag ? 200 : 120), tr, w / 2f);
            }
        }
    }
}
