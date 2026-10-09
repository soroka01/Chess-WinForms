using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// The participants of a room: avatar, name, role, host / seat / mute marks and a "talking" indicator.
    /// Owner-drawn; wheel scrolling and a thin own scrollbar like MoveListControl. A click on a row raises PersonClicked.
    /// </summary>
    public sealed class PeopleList : Control
    {
        public sealed class Person
        {
            public string Id, Name;
            public bool IsMe, IsHost;
            public int Seat;            // 0 = spectator, 1 = plays White, 2 = plays Black
            public bool Speaking;       // currently talking on the voice channel
            public bool ChatMuted, VoiceMuted;   // the host has switched this person's chat / voice off
            public bool Away;           // dimmed
        }

        sealed class Row
        {
            public Person Src;     // what the caller gave us (handed back in PersonClicked)
            public Person Snap;    // a private copy: what is on screen, and what the next SetPeople is compared with
        }

        static readonly Color[] Palette =
        {
            Color.FromArgb(214, 96, 88), Color.FromArgb(206, 128, 52), Color.FromArgb(176, 144, 36), Color.FromArgb(88, 160, 92),
            Color.FromArgb(48, 156, 148), Color.FromArgb(72, 140, 208), Color.FromArgb(124, 108, 208), Color.FromArgb(184, 96, 170)
        };

        readonly List<Row> rows = new List<Row>();
        readonly Timer speakTimer = new Timer();
        int speakPhase;
        int scroll;                 // pixels
        int hoverRow = -1;
        int pressedRow = -1;
        Point lastMouse = new Point(-1, -1);
        bool thumbDrag;
        int thumbGrab;
        bool thumbHover;

        /// <summary>A left click on a row. The point (screen coordinates) is just under the row: where a menu should open.</summary>
        public event Action<Person, Point> PersonClicked;

        public PeopleList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = false;
            speakTimer.Interval = 110;
            speakTimer.Tick += delegate { OnSpeakTick(); };
        }

        Theme T { get { return Theme.Current; } }
        int RowH { get { return Ui.S(52); } }
        int ContentH { get { return rows.Count * RowH + Ui.S(8); } }
        int MaxScroll { get { return Math.Max(0, ContentH - Height); } }
        int ListW { get { return Width - (MaxScroll > 0 ? Ui.S(10) : 0); } }

        public int Count { get { return rows.Count; } }

        // ------------------------------------------------------------ content

        /// <summary>
        /// Replaces the content. Cheap to call often: nothing is repainted unless something visible changed, and then only the rows
        /// that changed. The scroll position is kept.
        /// </summary>
        public void SetPeople(IList<Person> people)
        {
            int n = people == null ? 0 : people.Count;
            bool sameCount = n == rows.Count;
            bool all = !sameCount;
            List<int> changed = new List<int>();
            List<Row> next = new List<Row>(n);
            for (int i = 0; i < n; i++)
            {
                Person src = people[i];
                Row row = new Row();
                row.Src = src;
                row.Snap = Copy(src);
                if (sameCount && !Same(rows[i].Snap, row.Snap)) changed.Add(i);
                else if (sameCount) row.Snap = rows[i].Snap;
                next.Add(row);
            }
            rows.Clear();
            rows.AddRange(next);
            if (all || changed.Count > 0)
            {
                if (scroll > MaxScroll) scroll = MaxScroll;
                UpdateHover(lastMouse);
                if (all || changed.Count * 2 > rows.Count) Invalidate();
                else foreach (int i in changed) InvalidateRow(i);
            }
            UpdateTimer();
        }

        static Person Copy(Person p)
        {
            Person c = new Person();
            if (p == null) return c;
            c.Id = p.Id; c.Name = p.Name; c.IsMe = p.IsMe; c.IsHost = p.IsHost; c.Seat = p.Seat;
            c.Speaking = p.Speaking; c.ChatMuted = p.ChatMuted; c.VoiceMuted = p.VoiceMuted; c.Away = p.Away;
            return c;
        }

        static bool Same(Person a, Person b)
        {
            return a.Id == b.Id && a.Name == b.Name && a.IsMe == b.IsMe && a.IsHost == b.IsHost && a.Seat == b.Seat
                && a.Speaking == b.Speaking && a.ChatMuted == b.ChatMuted && a.VoiceMuted == b.VoiceMuted && a.Away == b.Away;
        }

        bool AnySpeaking()
        {
            foreach (Row r in rows) if (r.Snap.Speaking) return true;
            return false;
        }

        void UpdateTimer()
        {
            bool want = !IsDisposed && AnySpeaking();
            if (want && !speakTimer.Enabled) speakTimer.Start();
            else if (!want && speakTimer.Enabled) speakTimer.Stop();
        }

        void OnSpeakTick()
        {
            speakPhase = (speakPhase + 1) % 64;
            if (!IsHandleCreated || !Visible) return;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Snap.Speaking) InvalidateRow(i);
        }

        void InvalidateRow(int i)
        {
            Rectangle r = new Rectangle(0, i * RowH - scroll, Width, RowH);
            if (r.Bottom >= 0 && r.Top <= Height) Invalidate(r);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) speakTimer.Dispose();
            base.Dispose(disposing);
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

        // ------------------------------------------------------------ geometry

        RectangleF ThumbRect()
        {
            if (MaxScroll <= 0) return RectangleF.Empty;
            float trackH = Height - Ui.S(8);
            float thumbH = Math.Max(Ui.S(28), trackH * Height / ContentH);
            float y = Ui.S(4) + (trackH - thumbH) * scroll / MaxScroll;
            return new RectangleF(Width - Ui.S(8), y, Ui.S(5), thumbH);
        }

        int RowAt(Point p)
        {
            if (p.X < 0 || p.X >= ListW || p.Y < 0 || p.Y >= Height) return -1;
            int row = (p.Y + scroll) / RowH;
            return row >= 0 && row < rows.Count ? row : -1;
        }

        void UpdateHover(Point p)
        {
            int row = thumbDrag ? -1 : RowAt(p);
            bool overThumb = MaxScroll > 0 && p.X >= Width - Ui.S(12) && p.X < Width && p.Y >= 0 && p.Y < Height;
            if (row != hoverRow || overThumb != thumbHover)
            {
                hoverRow = row;
                thumbHover = overThumb;
                Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseEnter(EventArgs e)
        {
            if (!Focused) Focus();   // so that the wheel works without clicking first
            base.OnMouseEnter(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            lastMouse = e.Location;
            if (thumbDrag)
            {
                float trackH = Height - Ui.S(8);
                RectangleF th = ThumbRect();
                float range = trackH - th.Height;
                if (range > 0) scroll = (int)Math.Max(0, Math.Min(MaxScroll, (e.Y - thumbGrab - Ui.S(4)) / range * MaxScroll));
                Invalidate();
            }
            else UpdateHover(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            lastMouse = new Point(-1, -1);
            if (hoverRow != -1 || thumbHover || pressedRow != -1)
            {
                hoverRow = -1;
                pressedRow = -1;
                thumbHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (MaxScroll > 0 && e.X >= Width - Ui.S(12))
                {
                    RectangleF th = ThumbRect();
                    thumbDrag = true;
                    thumbGrab = th.Contains(e.Location) ? (int)(e.Y - th.Y) : (int)(th.Height / 2);
                    OnMouseMove(e);
                }
                else
                {
                    pressedRow = RowAt(e.Location);
                    if (pressedRow >= 0) InvalidateRow(pressedRow);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                thumbDrag = false;
                UpdateHover(e.Location);
            }
            else if (e.Button == MouseButtons.Left)
            {
                int pressed = pressedRow;
                pressedRow = -1;
                int row = RowAt(e.Location);
                if (pressed >= 0) InvalidateRow(pressed);
                if (row >= 0 && row == pressed && PersonClicked != null)
                {
                    int bottom = Math.Min(Height, (row + 1) * RowH - scroll);
                    PersonClicked(rows[row].Src, PointToScreen(new Point(Ui.S(10), bottom)));
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int old = scroll;
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - Math.Sign(e.Delta) * RowH));
            if (scroll != old)
            {
                UpdateHover(lastMouse);
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            if (scroll > MaxScroll) scroll = MaxScroll;
            base.OnSizeChanged(e);
        }

        // ------------------------------------------------------------ painting

        static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h;
        }

        /// <summary>The colour of a person's avatar: stable for an Id, a pleasant mid-tone that carries white text in both themes.</summary>
        static Color AvatarColor(Person p)
        {
            return Palette[Hash(!string.IsNullOrEmpty(p.Id) ? p.Id : p.Name) % (uint)Palette.Length];
        }

        static string Initial(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "?";
            return StringInfo.GetNextTextElement(name).ToUpper(CultureInfo.CurrentCulture);
        }

        static string RoleText(Person p)
        {
            string seat = p.Seat == 1 ? "играет белыми" : (p.Seat == 2 ? "играет чёрными" : null);
            if (p.IsHost) return seat == null ? "Хост" : "Хост · " + seat;
            return seat == null ? "Зритель" : char.ToUpper(seat[0], CultureInfo.CurrentCulture) + seat.Substring(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            Color bg = Parent != null ? Parent.BackColor : t.Card;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (rows.Count == 0)
            {
                Ui.Text(g, "Здесь пока никого", Ui.Font(10f), t.Muted, new Rectangle(0, 0, Width, Height),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int first = Math.Max(0, scroll / RowH);
            for (int i = first; i < rows.Count; i++)
            {
                int y = i * RowH - scroll;
                if (y > Height) break;
                if (y + RowH < e.ClipRectangle.Top || y > e.ClipRectangle.Bottom) continue;
                DrawRow(g, rows[i].Snap, new Rectangle(0, y, ListW, RowH), i == pressedRow && i == hoverRow, i == hoverRow, bg);
            }

            RectangleF thumb = ThumbRect();
            if (!thumb.IsEmpty)
            {
                float w = thumbHover || thumbDrag ? Ui.S(6) : Ui.S(4);
                RectangleF tr = new RectangleF(Width - Ui.S(6) - w / 2f, thumb.Y, w, thumb.Height);
                Ui.FillRound(g, Ui.Alpha(t.Muted, thumbHover || thumbDrag ? 200 : 120), tr, w / 2f);
            }
        }

        void DrawRow(Graphics g, Person p, Rectangle cell, bool pressed, bool hover, Color bg)
        {
            Theme t = T;
            Rectangle card = new Rectangle(cell.X + Ui.S(6), cell.Y + Ui.S(2), cell.Width - Ui.S(12), cell.Height - Ui.S(4));
            Color rowBg = bg;
            if (pressed) rowBg = Ui.Mix(t.Field, t.Text, 0.08f);
            else if (hover) rowBg = t.Field;
            if (rowBg != bg) Ui.FillRound(g, rowBg, card, Ui.S(10f));

            bool away = p.Away;
            Color text = away ? Ui.Mix(rowBg, t.Text, 0.5f) : t.Text;
            Color muted = away ? Ui.Mix(rowBg, t.Muted, 0.6f) : t.Muted;

            // avatar
            int av = Ui.S(36);
            Rectangle ar = new Rectangle(card.X + Ui.S(8), card.Y + (card.Height - av) / 2, av, av);
            Color avColor = AvatarColor(p);
            if (away) avColor = Ui.Mix(rowBg, avColor, 0.45f);
            using (SolidBrush b = new SolidBrush(avColor)) g.FillEllipse(b, ar);
            if (p.Speaking)
                using (Pen ring = new Pen(t.Accent, Ui.S(2f)))
                    g.DrawEllipse(ring, ar.X - Ui.S(3f), ar.Y - Ui.S(3f), ar.Width + Ui.S(6f), ar.Height + Ui.S(6f));
            Ui.Text(g, Initial(p.Name), Ui.Font(11.5f, FontStyle.Bold), away ? Ui.Mix(avColor, Color.White, 0.8f) : Color.White, ar,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // right-hand marks, packed from the right edge
            int right = card.Right - Ui.S(10);
            int mid = card.Y + card.Height / 2;
            int gap = Ui.S(6);
            if (p.Seat == 1 || p.Seat == 2)
            {
                int ps = Ui.S(26);
                Bitmap piece = PieceCache.Get(p.Seat == 1 ? Pc.King : -Pc.King, ps);
                if (away)
                {
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ColorMatrix cm = new ColorMatrix();
                        cm.Matrix33 = 0.5f;
                        ia.SetColorMatrix(cm);
                        g.DrawImage(piece, new Rectangle(right - ps, mid - ps / 2, ps, ps), 0, 0, ps, ps, GraphicsUnit.Pixel, ia);
                    }
                }
                else g.DrawImageUnscaled(piece, right - ps, mid - ps / 2);
                right -= ps + gap;
            }
            int iw = Ui.S(20);
            if (p.VoiceMuted)
            {
                DrawMuteMic(g, new Rectangle(right - iw, mid - iw / 2, iw, iw), away ? Ui.Mix(rowBg, t.Danger, 0.5f) : t.Danger, rowBg);
                right -= iw + gap;
            }
            else if (p.Speaking)
            {
                int bw = Ui.S(18);
                int baseline = mid + Ui.S(7);
                for (int i = 0; i < 3; i++)
                {
                    int h = Ui.S(4) + (int)((Math.Sin(speakPhase * 0.95 + i * 1.9) + 1.0) * Ui.S(4.5f));
                    Ui.FillRound(g, t.Accent, new RectangleF(right - bw + i * Ui.S(6), baseline - h, Ui.S(3.5f), h), Ui.S(1.5f));
                }
                right -= bw + gap;
            }
            if (p.ChatMuted)
            {
                DrawMuteChat(g, new Rectangle(right - iw, mid - iw / 2, iw, iw), away ? Ui.Mix(rowBg, t.Danger, 0.5f) : t.Danger, rowBg);
                right -= iw + gap;
            }
            if (p.IsHost)
            {
                DrawCrown(g, new Rectangle(right - iw, mid - iw / 2, iw, iw), away ? Ui.Mix(rowBg, t.Gold, 0.5f) : t.Gold);
                right -= iw + gap;
            }

            // texts
            int textX = ar.Right + Ui.S(12);
            int avail = Math.Max(0, right - textX);
            Font nameFont = Ui.Font(10f, FontStyle.Bold);
            string name = string.IsNullOrEmpty(p.Name) ? "?" : p.Name;
            int chipW = 0;
            Font chipFont = Ui.Font(8f, FontStyle.Bold);
            if (p.IsMe) chipW = Ui.Measure(g, "вы", chipFont).Width + Ui.S(12);
            int nameMax = Math.Max(0, avail - (p.IsMe ? chipW + Ui.S(6) : 0));
            int nameW = Math.Min(Ui.Measure(g, name, nameFont).Width + 2, nameMax);
            Ui.Text(g, name, nameFont, text, new Rectangle(textX, card.Y + Ui.S(6), nameMax, Ui.S(20)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (p.IsMe)
            {
                Rectangle chip = new Rectangle(textX + nameW + Ui.S(6), card.Y + Ui.S(6) + (Ui.S(20) - Ui.S(15)) / 2, chipW, Ui.S(15));
                Color accent = away ? Ui.Mix(rowBg, t.Accent, 0.5f) : t.Accent;
                Ui.FillRound(g, Ui.Mix(rowBg, accent, 0.2f), chip, Ui.S(7.5f));
                Ui.Text(g, "вы", chipFont, accent, chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            Ui.Text(g, RoleText(p), Ui.Font(8.5f), muted, new Rectangle(textX, card.Y + Ui.S(25), avail, Ui.S(18)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // ------------------------------------------------------------ little drawn icons

        static bool UseGlyphs { get { return Ui.HasIcons; } }

        static void DrawSlash(Graphics g, Rectangle r, Color c, Color gapColor)
        {
            PointF a = new PointF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.12f);
            PointF b = new PointF(r.Right - r.Width * 0.14f, r.Bottom - r.Height * 0.12f);
            using (Pen cut = new Pen(gapColor, Ui.S(4.2f))) { cut.StartCap = LineCap.Round; cut.EndCap = LineCap.Round; g.DrawLine(cut, a, b); }
            using (Pen p = new Pen(c, Ui.S(1.8f))) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, a, b); }
        }

        static void DrawMuteMic(Graphics g, Rectangle r, Color c, Color gapColor)
        {
            if (UseGlyphs)
                Ui.Text(g, "", Ui.Icons(11f), c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else
            {
                float cx = r.X + r.Width / 2f, w = Ui.S(6f), h = Ui.S(10f);
                Ui.FillRound(g, c, new RectangleF(cx - w / 2, r.Y + Ui.S(2f), w, h), w / 2);
                using (Pen p = new Pen(c, Ui.S(1.5f)))
                {
                    g.DrawArc(p, cx - Ui.S(5f), r.Y + Ui.S(4f), Ui.S(10f), Ui.S(9f), 0, 180);
                    g.DrawLine(p, cx, r.Y + Ui.S(13f), cx, r.Y + Ui.S(17f));
                }
            }
            DrawSlash(g, r, c, gapColor);
        }

        static void DrawMuteChat(Graphics g, Rectangle r, Color c, Color gapColor)
        {
            if (UseGlyphs)
                Ui.Text(g, "", Ui.Icons(11f), c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else
            {
                RectangleF body = new RectangleF(r.X + Ui.S(2f), r.Y + Ui.S(3f), r.Width - Ui.S(4f), Ui.S(11f));
                using (GraphicsPath path = Ui.RoundPath(body, Ui.S(3f)))
                using (Pen p = new Pen(c, Ui.S(1.5f)))
                    g.DrawPath(p, path);
                using (SolidBrush b = new SolidBrush(c))
                    g.FillPolygon(b, new PointF[] { new PointF(body.X + Ui.S(4f), body.Bottom - 0.5f), new PointF(body.X + Ui.S(4f), body.Bottom + Ui.S(4f)), new PointF(body.X + Ui.S(8.5f), body.Bottom - 0.5f) });
            }
            DrawSlash(g, r, c, gapColor);
        }

        static void DrawCrown(Graphics g, Rectangle r, Color c)
        {
            float w = Ui.S(15f), h = Ui.S(11f);
            float x = r.X + (r.Width - w) / 2f, y = r.Y + (r.Height - h) / 2f;
            float u = w / 14f, v = h / 10f;
            PointF[] pts =
            {
                new PointF(x + 0 * u, y + 2 * v), new PointF(x + 3.6f * u, y + 5.4f * v), new PointF(x + 7 * u, y + 0 * v),
                new PointF(x + 10.4f * u, y + 5.4f * v), new PointF(x + 14 * u, y + 2 * v),
                new PointF(x + 12.6f * u, y + 9.2f * v), new PointF(x + 1.4f * u, y + 9.2f * v)
            };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
            using (Pen p = new Pen(c, 1.2f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, pts); }
        }
    }

    /// <summary>A drop-down menu card: rounded, bordered, with a soft shadow. Only one is open at a time.</summary>
    public static class Popup
    {
        public sealed class Item
        {
            public string Text; public Action OnClick;
            public bool Danger;        // red text (kick)
            public bool Checked;       // shows a check mark
            public bool Disabled;
            public bool Separator;     // a thin line; Text/OnClick ignored
        }

        static PopupForm current;

        /// <summary>Opens a menu with its top-left corner at the screen point (moved or flipped to stay on the screen).</summary>
        public static void Show(Control owner, Point screen, IList<Item> items)
        {
            CloseAll();
            if (items == null || items.Count == 0) return;
            Form host = owner == null ? null : owner.FindForm();
            if (host != null && (host.IsDisposed || !host.IsHandleCreated)) host = null;
            PopupForm f = new PopupForm(items, screen);
            current = f;
            f.FormClosed += delegate { if (current == f) current = null; };
            if (host != null) f.Show(host); else f.Show();
        }

        /// <summary>Closes the menu that is open, if any.</summary>
        public static void CloseAll()
        {
            PopupForm f = current;
            current = null;
            if (f != null && !f.IsDisposed) f.Close();
        }

        /// <summary>The menu as a bitmap (card plus shadow), for tests: hover = index of the highlighted item or -1.</summary>
        internal static Bitmap Render(IList<Item> items, int hover)
        {
            using (PopupForm f = new PopupForm(items, Point.Empty))
                return f.RenderBitmap(hover, -1);
        }

        sealed class PopupForm : Form
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
            const int ULW_ALPHA = 2;

            [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

            [DllImport("user32.dll", SetLastError = true)]
            static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLEND blend, int flags);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

            readonly Item[] items;
            readonly Rectangle[] rects;      // in card coordinates
            readonly int margin;             // room around the card for the shadow
            readonly Size cardSize;
            readonly bool checkColumn;
            int active = -1;
            int pressed = -1;
            bool closing;

            public PopupForm(IList<Item> source, Point screen)
            {
                items = new Item[source.Count];
                for (int i = 0; i < items.Length; i++) items[i] = source[i] ?? new Item { Separator = true };
                margin = Ui.S(16);

                int pad = Ui.S(6), itemH = Ui.S(36), sepH = Ui.S(9), textPad = Ui.S(12);
                Font font = Ui.Font(10f);
                for (int i = 0; i < items.Length; i++) if (items[i].Checked && !items[i].Separator) checkColumn = true;
                int widest = 0;
                foreach (Item it in items)
                    if (!it.Separator) widest = Math.Max(widest, TextRenderer.MeasureText(it.Text ?? "", font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
                int w = Math.Min(Ui.S(420), Math.Max(Ui.S(200), widest + pad * 2 + textPad * 2 + (checkColumn ? Ui.S(22) : 0) + 2));
                rects = new Rectangle[items.Length];
                int y = pad;
                for (int i = 0; i < items.Length; i++)
                {
                    int h = items[i].Separator ? sepH : itemH;
                    rects[i] = new Rectangle(pad, y, w - pad * 2, h);
                    y += h;
                }
                cardSize = new Size(w, y + pad);

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                KeyPreview = false;
                AutoScaleMode = AutoScaleMode.None;

                Rectangle wa = Screen.GetWorkingArea(screen);
                int cx = screen.X, cy = screen.Y;
                if (cx + cardSize.Width > wa.Right) cx = wa.Right - cardSize.Width;
                if (cx < wa.Left) cx = wa.Left;
                if (cy + cardSize.Height > wa.Bottom)
                {
                    // no room below: open above, leaving the row that was clicked (about one list row) visible
                    int above = screen.Y - Ui.S(52) - cardSize.Height;
                    cy = above >= wa.Top ? above : Math.Max(wa.Top, wa.Bottom - cardSize.Height);
                }
                Bounds = new Rectangle(cx - margin, cy - margin, cardSize.Width + margin * 2, cardSize.Height + margin * 2);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            // ---- lifetime

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

            void OnThemeChanged() { if (!IsDisposed && !closing) Redraw(); }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible) Redraw();
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                CloseNow();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                closing = true;
                base.OnFormClosed(e);
            }

            void CloseNow()
            {
                if (closing || IsDisposed) return;
                closing = true;
                Close();   // a form shown with Show() is disposed by Close
            }

            // ---- items

            int IndexAt(Point clientPoint)
            {
                Point p = new Point(clientPoint.X - margin, clientPoint.Y - margin);
                for (int i = 0; i < rects.Length; i++)
                    if (rects[i].Contains(p) && Selectable(i)) return i;
                return -1;
            }

            bool Selectable(int i) { return !items[i].Separator && !items[i].Disabled; }

            void Step(int step)
            {
                int n = items.Length;
                int i = active;
                for (int k = 0; k < n; k++)
                {
                    i = i < 0 ? (step > 0 ? 0 : n - 1) : (i + step + n) % n;
                    if (Selectable(i)) { active = i; Redraw(); return; }
                }
            }

            void Activate(int i)
            {
                if (i < 0 || i >= items.Length || !Selectable(i)) return;
                Action act = items[i].OnClick;
                CloseNow();
                if (act != null) act();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Escape: CloseNow(); return true;
                    case Keys.Down: Step(1); return true;
                    case Keys.Up: Step(-1); return true;
                    case Keys.Home: active = -1; Step(1); return true;
                    case Keys.End: active = -1; Step(-1); return true;
                    case Keys.Enter: case Keys.Space: Activate(active); return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int i = IndexAt(e.Location);
                if (i != active) { active = i; Redraw(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                if (active != -1) { active = -1; Redraw(); }
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { pressed = IndexAt(e.Location); Redraw(); }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                int down = pressed;
                pressed = -1;
                if (e.Button == MouseButtons.Left && down >= 0 && down == IndexAt(e.Location)) Activate(down);
                else if (!closing && !IsDisposed) Redraw();
                base.OnMouseUp(e);
            }

            protected override void OnPaint(PaintEventArgs e) { }
            protected override void OnPaintBackground(PaintEventArgs e) { }

            // ---- drawing (a layered window: the whole card, shadow included, is one bitmap with per-pixel alpha)

            void Redraw()
            {
                if (!IsHandleCreated || IsDisposed) return;
                using (Bitmap bmp = RenderBitmap(active, pressed))
                {
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    IntPtr memDc = CreateCompatibleDC(screenDc);
                    IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    IntPtr old = SelectObject(memDc, hBmp);
                    try
                    {
                        POINT dst = new POINT { X = Left, Y = Top };
                        SIZE size = new SIZE { W = bmp.Width, H = bmp.Height };
                        POINT src = new POINT();
                        BLEND blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                        UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                    }
                    finally
                    {
                        SelectObject(memDc, old);
                        DeleteObject(hBmp);
                        DeleteDC(memDc);
                        ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
            }

            public Bitmap RenderBitmap(int hover, int press)
            {
                Theme t = Theme.Current;
                Color cardColor = t.IsDark ? Ui.Mix(t.Card, Color.White, 0.04f) : t.Card;

                // the card itself on an opaque bitmap (GDI text does not write alpha)
                using (Bitmap content = new Bitmap(cardSize.Width, cardSize.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(content))
                    {
                        g.Clear(cardColor);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        DrawItems(g, t, cardColor, hover, press);
                    }

                    int W = cardSize.Width + margin * 2, H = cardSize.Height + margin * 2;
                    Bitmap result = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(result))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        RectangleF card = new RectangleF(margin, margin, cardSize.Width, cardSize.Height);
                        float radius = Ui.S(12f);

                        // soft shadow: nested translucent rounded rects, a little lower than the card
                        int blur = margin - Ui.S(2);
                        int layerAlpha = t.IsDark ? 9 : 6;
                        for (int i = blur; i >= 1; i--)
                        {
                            RectangleF r = new RectangleF(card.X - i + 0, card.Y - i + Ui.S(4), card.Width + i * 2, card.Height + i * 2);
                            Ui.FillRound(g, Color.FromArgb(layerAlpha, 0, 0, 0), r, radius + i);
                        }

                        using (TextureBrush tb = new TextureBrush(content))
                        using (GraphicsPath path = Ui.RoundPath(card, radius))
                        {
                            tb.TranslateTransform(margin, margin);
                            g.FillPath(tb, path);
                        }
                        Ui.DrawRound(g, t.Border, new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1f, card.Height - 1f), radius, 1f);
                    }
                    return result;
                }
            }

            void DrawItems(Graphics g, Theme t, Color cardColor, int hover, int press)
            {
                Font font = Ui.Font(10f);
                int textPad = Ui.S(12);
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    Rectangle r = rects[i];
                    if (it.Separator)
                    {
                        using (Pen p = new Pen(t.Border))
                            g.DrawLine(p, r.X + Ui.S(8), r.Y + r.Height / 2, r.Right - Ui.S(8), r.Y + r.Height / 2);
                        continue;
                    }
                    Color fg = it.Danger ? t.Danger : t.Text;
                    Color rowBg = cardColor;
                    if (it.Disabled) fg = Ui.Mix(cardColor, fg, 0.38f);
                    else if (i == hover)
                    {
                        Color hl = it.Danger ? Ui.Mix(cardColor, t.Danger, t.IsDark ? 0.18f : 0.12f) : (t.IsDark ? t.Field : Ui.Mix(t.Field, t.Text, 0.03f));
                        if (i == press) hl = it.Danger ? Ui.Mix(cardColor, t.Danger, 0.28f) : Ui.Mix(hl, t.Text, 0.08f);
                        Ui.FillRound(g, hl, r, Ui.S(8f));
                        rowBg = hl;
                    }
                    int x = r.X + textPad;
                    if (it.Checked)
                    {
                        float cx = x + Ui.S(5f), cy = r.Y + r.Height / 2f;
                        Color tick = it.Disabled ? fg : t.Accent;
                        using (Pen p = new Pen(tick, Ui.S(1.9f)))
                        {
                            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                            g.DrawLines(p, new PointF[] { new PointF(cx - Ui.S(4f), cy + Ui.S(0.5f)), new PointF(cx - Ui.S(1.2f), cy + Ui.S(3.4f)), new PointF(cx + Ui.S(4.5f), cy - Ui.S(3.4f)) });
                        }
                    }
                    if (checkColumn) x += Ui.S(22);
                    Ui.Text(g, it.Text ?? "", font, fg, new Rectangle(x, r.Y, Math.Max(0, r.Right - textPad - x), r.Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
