using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// The move list: two columns of figurine notation, the current move highlighted, click a move to look at that position.
    /// Scrolls with the mouse wheel and has its own thin scrollbar so that it matches the theme.
    /// </summary>
    public sealed class MoveListControl : Control, IWheelTarget
    {
        IList<string> moves = new List<string>();
        int currentPly;
        readonly ThinScrollbar bar;
        int hoverPly = -1;

        /// <summary>The user picked a move: the number of plies to show (1 = after White's first move).</summary>
        public event Action<int> PlySelected;

        public MoveListControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // hovering or clicking must not take the focus from the chat box; the wheel comes through WheelRouter
            TabStop = false;
            bar = new ThinScrollbar(this, delegate { return ContentH; });
            ThemeWatch.Attach(this, delegate { Invalidate(); });
        }

        Theme T { get { return Theme.Current; } }
        int RowH { get { return Ui.S(30); } }
        int NumberW { get { return Ui.S(40); } }
        int Rows { get { return (moves.Count + 1) / 2; } }
        int ContentH { get { return Rows * RowH + Ui.S(6); } }

        public int CurrentPly
        {
            get { return currentPly; }
            set { if (value != currentPly) { currentPly = value; EnsureVisible(currentPly); Invalidate(); } }
        }

        /// <summary>Shows the given SAN list (the list object is read live; call again after it changes).</summary>
        public void SetMoves(IList<string> sans, int current)
        {
            bool atBottom = bar.Offset >= bar.MaxOffset - 2;
            moves = sans;
            currentPly = current;
            bar.Clamp();
            if (atBottom || current == moves.Count) bar.ScrollToEnd();
            else EnsureVisible(current);
            Invalidate();
        }

        void EnsureVisible(int ply)
        {
            if (ply <= 0) { bar.Offset = 0; return; }
            int row = (ply - 1) / 2;
            int top = row * RowH, bottom = top + RowH + Ui.S(6);
            if (top < bar.Offset) bar.Offset = top;
            else if (bottom > bar.Offset + Height) bar.Offset = bottom - Height;
            bar.Offset = Math.Max(0, Math.Min(bar.Offset, bar.MaxOffset));
        }

        // ------------------------------------------------------------ geometry

        int PlyAt(Point p)
        {
            if (p.X < 0 || p.X >= Width - Ui.S(10)) return -1;
            int row = (p.Y + bar.Offset) / RowH;
            if (p.Y < 0 || row >= Rows) return -1;
            int cellW = (Width - Ui.S(10) - NumberW) / 2;
            if (p.X < NumberW) return -1;
            int col = (p.X - NumberW) / Math.Max(1, cellW);
            int ply = row * 2 + (col > 0 ? 2 : 1);
            return ply <= moves.Count ? ply : -1;
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (bar.Dragging)
            {
                bar.DragTo(e.Y);
                Invalidate();
            }
            else
            {
                int ply = PlyAt(e.Location);
                bool overThumb = bar.IsOverThumb(e.Location);
                if (ply != hoverPly || overThumb != bar.Hover)
                {
                    hoverPly = ply;
                    bar.Hover = overThumb;
                    Cursor = ply > 0 ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverPly = -1;
            bar.Hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && bar.BeginDrag(e.Location)) OnMouseMove(e);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!bar.EndDrag() && e.Button == MouseButtons.Left)
            {
                int ply = PlyAt(e.Location);
                if (ply > 0 && PlySelected != null) PlySelected(ply);
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            bar.Wheel(e.Delta, RowH * 3);
            Invalidate();
            base.OnMouseWheel(e);
        }

        void IWheelTarget.Wheel(int delta)
        {
            OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, Width / 2, Height / 2, delta));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            bar.Clamp();
            base.OnSizeChanged(e);
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme t = T;
            Color bg = Parent != null ? Parent.BackColor : t.Card;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (moves.Count == 0)
            {
                Ui.Text(g, "Ходов пока нет", Ui.Font(10f), t.Muted, new Rectangle(0, 0, Width, Height - Ui.S(14)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Ui.Text(g, "Партия начнётся с хода белых", Ui.Font(8.5f), Ui.Mix(bg, t.Muted, 0.7f),
                    new Rectangle(0, Ui.S(16), Width, Height - Ui.S(2)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int listW = Width - Ui.S(10);
            int cellW = (listW - NumberW) / 2;
            int firstRow = Math.Max(0, bar.Offset / RowH);
            Font number = Ui.Font(9f);
            for (int row = firstRow; row < Rows; row++)
            {
                int y = row * RowH - bar.Offset;
                if (y > Height) break;
                if (row % 2 == 1)
                    using (SolidBrush b = new SolidBrush(Ui.Alpha(t.Field, t.IsDark ? 90 : 120)))
                        g.FillRectangle(b, 0, y, listW, RowH);
                Ui.Text(g, (row + 1) + ".", number, t.Muted, new Rectangle(Ui.S(8), y, NumberW - Ui.S(8), RowH),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                for (int c = 0; c < 2; c++)
                {
                    int ply = row * 2 + c + 1;
                    if (ply > moves.Count) break;
                    Rectangle cell = new Rectangle(NumberW + c * cellW, y + Ui.S(2), cellW - Ui.S(4), RowH - Ui.S(4));
                    bool current = ply == currentPly;
                    if (current) Ui.FillRound(g, Ui.Alpha(t.Accent, t.IsDark ? 70 : 60), cell, Ui.S(6f));
                    else if (ply == hoverPly) Ui.FillRound(g, t.Field, cell, Ui.S(6f));
                    DrawSan(g, moves[ply - 1], cell, current ? t.Text : Ui.Mix(t.Text, bg, 0.12f), current);
                }
            }

            bar.Paint(g, t);
        }

        /// <summary>Figurine algebraic notation: the first letter of a piece move becomes the piece symbol.</summary>
        static void DrawSan(Graphics g, string san, Rectangle cell, Color color, bool bold)
        {
            int type = san.Length > 0 ? " PNBRQK".IndexOf(san[0]) : -1;
            Font text = Ui.Font(10f, bold ? FontStyle.Bold : FontStyle.Regular);
            int x = cell.X + Ui.S(8);
            string rest = san;
            if (type >= Pc.Knight && type <= Pc.King && san[0] != 'P')
            {
                Font fig = SymbolFont();
                string glyph = PieceArt.Solid(type).ToString();   // the filled glyphs stay readable at this size
                Size gs = Ui.Measure(g, glyph, fig);
                Ui.Text(g, glyph, fig, color, new Rectangle(x, cell.Y, gs.Width + 2, cell.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                x += gs.Width + Ui.S(1);
                rest = san.Substring(1);
            }
            Ui.Text(g, rest, text, color, new Rectangle(x, cell.Y, Math.Max(0, cell.Right - x), cell.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        static Font symbolFont;

        static Font SymbolFont()
        {
            if (symbolFont == null) symbolFont = new Font(PieceArt.FontName, 13f, FontStyle.Regular, GraphicsUnit.Point);
            return symbolFont;
        }
    }
}
