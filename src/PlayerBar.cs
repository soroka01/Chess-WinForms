using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>Rendered piece bitmaps, cached by (piece, size). Pieces are tiny and few, so they live for the whole run.</summary>
    static class PieceCache
    {
        const int MaxEntries = 96;   // 12 pieces x a handful of sizes; resizing the window must not make this grow for ever
        static readonly Dictionary<long, Bitmap> cache = new Dictionary<long, Bitmap>();
        static readonly List<long> order = new List<long>();

        public static Bitmap Get(int piece, int size)
        {
            long key = ((long)(piece + 8) << 32) | (uint)size;
            Bitmap b;
            if (cache.TryGetValue(key, out b)) return b;

            if (cache.Count >= MaxEntries)
            {
                // forget the oldest sizes; every caller draws the bitmap right after asking for it
                for (int i = 0; i < 24 && order.Count > 0; i++)
                {
                    Bitmap old;
                    if (cache.TryGetValue(order[0], out old)) { cache.Remove(order[0]); old.Dispose(); }
                    order.RemoveAt(0);
                }
            }
            b = PieceArt.Render(piece, size);
            cache[key] = b;
            order.Add(key);
            return b;
        }
    }

    /// <summary>One player's strip next to the board: avatar, name, captured pieces with material advantage, whose turn it is.</summary>
    public sealed class PlayerBar : Control
    {
        string playerName = "";
        string subtitle = "";
        bool isWhite;
        bool active;
        bool vacant;
        bool thinking;
        bool speaking;
        int speakPhase;
        readonly Timer speakTimer = new Timer();
        int[] captured = new int[7];     // counts of captured pieces by type (the opponent's pieces)
        int advantage;
        int dots;
        readonly Timer thinkTimer = new Timer();

        public PlayerBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(56);
            thinkTimer.Interval = 380;
            thinkTimer.Tick += delegate { dots = (dots + 1) % 4; Invalidate(); };
            speakTimer.Interval = 110;
            speakTimer.Tick += delegate { speakPhase = (speakPhase + 1) % 64; Invalidate(); };
        }

        /// <summary>The player is talking on the voice channel right now.</summary>
        public bool Speaking
        {
            get { return speaking; }
            set
            {
                if (speaking == value) return;
                speaking = value;
                if (value) speakTimer.Start(); else speakTimer.Stop();
                Invalidate();
            }
        }

        public string PlayerName { get { return playerName; } set { playerName = value ?? ""; Invalidate(); } }
        public string Subtitle { get { return subtitle; } set { subtitle = value ?? ""; Invalidate(); } }
        public bool IsWhite { get { return isWhite; } set { isWhite = value; Invalidate(); } }
        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        /// <summary>Nobody sits on this side: an empty dashed avatar, muted name, no turn ring.</summary>
        public bool Vacant { get { return vacant; } set { if (vacant == value) return; vacant = value; Invalidate(); } }

        public bool Thinking
        {
            get { return thinking; }
            set
            {
                if (thinking == value) return;
                thinking = value;
                dots = 0;
                if (value) thinkTimer.Start(); else thinkTimer.Stop();
                Invalidate();
            }
        }

        /// <summary>Counts of pieces this player has captured, indexed by piece type 1..6, and the material lead.</summary>
        public void SetCaptured(int[] counts, int materialAdvantage)
        {
            captured = counts;
            advantage = materialAdvantage;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) { thinkTimer.Dispose(); speakTimer.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Window);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.FillRound(g, t.Card, r, Ui.S(12f));
            bool ring = active && !vacant;
            Ui.DrawRound(g, ring ? Ui.Alpha(t.Accent, 150) : t.Border, r, Ui.S(12f), ring ? 1.5f : 1f);

            // avatar
            int av = Height - Ui.S(16);
            Rectangle ar = new Rectangle(Ui.S(10), (Height - av) / 2, av, av);
            if (vacant)
            {
                using (SolidBrush b = new SolidBrush(Ui.Mix(t.Card, t.Field, 0.5f))) g.FillEllipse(b, ar);
                using (Pen p = new Pen(Ui.Mix(t.Card, t.Muted, 0.6f), 1.6f))
                {
                    p.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(p, ar.X + 0.8f, ar.Y + 0.8f, ar.Width - 1.6f, ar.Height - 1.6f);
                }
            }
            else
            {
                Color avBg = isWhite ? Color.FromArgb(236, 232, 224) : Color.FromArgb(58, 60, 66);
                using (SolidBrush b = new SolidBrush(avBg)) g.FillEllipse(b, ar);
                if (active)
                    using (Pen p = new Pen(t.Accent, 2.4f)) g.DrawEllipse(p, ar.X - 1.5f, ar.Y - 1.5f, ar.Width + 3f, ar.Height + 3f);
                int pieceSize = (int)(av * 0.92f);
                g.DrawImageUnscaled(PieceCache.Get(isWhite ? Pc.King : -Pc.King, pieceSize),
                    ar.X + (av - pieceSize) / 2, ar.Y + (av - pieceSize) / 2 + 1);
            }

            // names
            int textX = ar.Right + Ui.S(12);
            int rightReserve = Ui.S(8);
            bool hasSubtitle = subtitle.Length > 0;
            Rectangle nameRect = hasSubtitle
                ? new Rectangle(textX, Ui.S(8), Math.Max(0, Width / 2 - textX), Ui.S(22))
                : new Rectangle(textX, 0, Math.Max(0, Width / 2 - textX), Height);
            Font nameFont = vacant ? Ui.Font(10.5f) : Ui.Font(10.5f, FontStyle.Bold);
            Ui.Text(g, playerName, nameFont, vacant ? t.Muted : t.Text, nameRect, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (thinking)
            {
                Size ns = Ui.Measure(g, playerName, nameFont);
                string d = new string('.', dots);
                Ui.Text(g, "думает" + d, Ui.Font(9f), t.Accent,
                    new Rectangle(textX + Math.Min(ns.Width, nameRect.Width) + Ui.S(8), nameRect.Y, Ui.S(90), nameRect.Height),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            if (speaking && !thinking)
            {
                Size ns = Ui.Measure(g, playerName, nameFont);
                int bx = textX + Math.Min(ns.Width, nameRect.Width) + Ui.S(10);
                int baseline = hasSubtitle ? nameRect.Y + Ui.S(17) : Height / 2 + Ui.S(8);
                using (SolidBrush bars = new SolidBrush(t.Accent))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        int h = Ui.S(4) + (int)((Math.Sin(speakPhase * 0.95 + i * 1.9) + 1.0) * Ui.S(4.5f));
                        Ui.FillRound(g, t.Accent, new RectangleF(bx + i * Ui.S(6), baseline - h, Ui.S(3.5f), h), Ui.S(1.5f));
                    }
                }
            }
            Ui.Text(g, subtitle, Ui.Font(8.5f), t.Muted, new Rectangle(textX, Ui.S(29), Math.Max(0, Width / 2 - textX), Ui.S(18)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // captured pieces (right aligned), drawn in the colour of the pieces that were taken
            int small = Ui.S(30);
            int step = Ui.S(13), groupGap = Ui.S(8);
            int total = 0;
            int[] order = { Pc.Pawn, Pc.Knight, Pc.Bishop, Pc.Rook, Pc.Queen };
            foreach (int type in order)
                if (captured[type] > 0) total += step * (captured[type] - 1) + small + groupGap;
            string adv = advantage > 0 ? "+" + advantage : "";
            Size advSize = adv.Length > 0 ? Ui.Measure(g, adv, Ui.Font(9.5f, FontStyle.Bold)) : Size.Empty;
            int x = Width - rightReserve - Ui.S(6) - advSize.Width - total + groupGap;
            if (adv.Length > 0) x -= Ui.S(4);
            int py = (Height - small) / 2;
            foreach (int type in order)
            {
                int n = captured[type];
                if (n == 0) continue;
                Bitmap bmp = PieceCache.Get(isWhite ? -type : type, small);
                for (int i = 0; i < n; i++)
                {
                    g.DrawImageUnscaled(bmp, x, py);
                    x += (i == n - 1) ? small : step;
                }
                x += groupGap;
            }
            if (adv.Length > 0)
                Ui.Text(g, adv, Ui.Font(9.5f, FontStyle.Bold), t.Muted,
                    new Rectangle(Width - rightReserve - Ui.S(6) - advSize.Width, 0, advSize.Width + 2, Height),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    public enum StatusKind { Normal, Check, Over, Waiting }

    /// <summary>The headline of the side panel: whose move it is, check, the result; with a thinking animation.</summary>
    public sealed class StatusCard : Control
    {
        string title = "";
        string subtitle = "";
        StatusKind kind;
        bool busy;
        int phase;
        readonly Timer pulse = new Timer();

        public StatusCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(74);
            pulse.Interval = 90;
            pulse.Tick += delegate { phase = (phase + 1) % 24; Invalidate(); };
        }

        public string Title { get { return title; } }
        public string Subtitle { get { return subtitle; } }

        /// <summary>Title plus subtitle on a second line; handy for tests and accessibility.</summary>
        public string FullText { get { return subtitle.Length > 0 ? title + "\n" + subtitle : title; } }

        public void Set(StatusKind newKind, string newTitle, string newSubtitle, bool isBusy)
        {
            kind = newKind;
            title = newTitle ?? "";
            subtitle = newSubtitle ?? "";
            if (busy != isBusy)
            {
                busy = isBusy;
                if (busy) pulse.Start(); else pulse.Stop();
            }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) pulse.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Window);

            Color tone;
            switch (kind)
            {
                case StatusKind.Check: tone = t.Danger; break;
                case StatusKind.Over: tone = t.Gold; break;
                case StatusKind.Waiting: tone = t.Warn; break;
                default: tone = t.Accent; break;
            }
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.FillRound(g, Ui.Mix(t.Card, tone, t.IsDark ? 0.10f : 0.08f), r, Ui.S(14f));
            Ui.DrawRound(g, Ui.Alpha(tone, 120), r, Ui.S(14f), 1.2f);

            // indicator dot (pulses while busy)
            float cx = Ui.S(22), cy = Height / 2f, radius = Ui.S(6f);
            if (busy)
            {
                float k = (float)(0.5 + 0.5 * Math.Sin(phase / 24.0 * 2 * Math.PI));
                using (SolidBrush halo = new SolidBrush(Ui.Alpha(tone, (int)(40 + 80 * k))))
                    g.FillEllipse(halo, cx - radius - 6 * k - 3, cy - radius - 6 * k - 3, (radius + 6 * k + 3) * 2, (radius + 6 * k + 3) * 2);
            }
            using (SolidBrush b = new SolidBrush(tone)) g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);

            int x = Ui.S(40);
            int w = Math.Max(0, Width - x - Ui.S(12));
            if (subtitle.Length > 0)
            {
                Ui.Text(g, title, Ui.Font(14f, FontStyle.Bold), t.Text, new Rectangle(x, Ui.S(10), w, Ui.S(28)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Ui.Text(g, subtitle, Ui.Font(9.5f), t.Muted, new Rectangle(x, Ui.S(38), w, Height - Ui.S(46)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            }
            else
            {
                Ui.Text(g, title, Ui.Font(14f, FontStyle.Bold), t.Text, new Rectangle(x, 0, w, Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
