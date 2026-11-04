using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// The thin themed scrollbar of the owner-drawn lists (MoveListControl, ChatControl, PeopleList). The control owns one and
    /// forwards its mouse events: the helper keeps the scroll offset, the thumb geometry, the thumb drag and hover state and paints
    /// the thumb. What differs between the lists (the wheel step, the width of the list, what else happens on hover) stays in them.
    /// </summary>
    internal sealed class ThinScrollbar
    {
        readonly Control host;
        readonly Func<int> contentHeight;   // the height of everything the list could show
        bool dragging;
        int grab;                           // where inside the thumb it was grabbed

        /// <summary>Scroll position in pixels; the owner may assign it, Clamp() brings it back into range.</summary>
        public int Offset;

        /// <summary>The pointer is over the thumb or its gutter (the thumb is drawn wider).</summary>
        public bool Hover;

        public ThinScrollbar(Control host, Func<int> contentHeight)
        {
            this.host = host;
            this.contentHeight = contentHeight;
        }

        public int MaxOffset { get { return Math.Max(0, contentHeight() - host.Height); } }

        public bool Dragging { get { return dragging; } }

        /// <summary>The strip along the right edge where a press grabs the thumb.</summary>
        public bool InGutter(int x) { return MaxOffset > 0 && x >= host.Width - Ui.S(12); }

        public RectangleF ThumbRect()
        {
            if (MaxOffset <= 0) return RectangleF.Empty;
            float trackH = host.Height - Ui.S(8);
            float thumbH = Math.Max(Ui.S(28), trackH * host.Height / contentHeight());
            float y = Ui.S(4) + (trackH - thumbH) * Offset / MaxOffset;
            return new RectangleF(host.Width - Ui.S(8), y, Ui.S(5), thumbH);
        }

        /// <summary>The pointer is on the thumb or in the gutter.</summary>
        public bool IsOverThumb(Point p)
        {
            return ThumbRect().Contains(p) || InGutter(p.X);
        }

        public void Clamp()
        {
            int max = MaxOffset;
            if (Offset > max) Offset = max;
        }

        public void ScrollToEnd() { Offset = MaxOffset; }

        /// <summary>Scrolls by one step against the wheel direction. Returns true if the position changed.</summary>
        public bool Wheel(int delta, int step)
        {
            int old = Offset;
            Offset = Math.Max(0, Math.Min(MaxOffset, Offset - Math.Sign(delta) * step));
            return Offset != old;
        }

        /// <summary>A left press at p: starts the drag when it hit the gutter. The owner then handles it as a move to p.</summary>
        public bool BeginDrag(Point p)
        {
            if (!InGutter(p.X)) return false;
            RectangleF th = ThumbRect();
            dragging = true;
            grab = th.Contains(p) ? (int)(p.Y - th.Y) : (int)(th.Height / 2);
            return true;
        }

        /// <summary>The pointer moved to y while the thumb is held.</summary>
        public void DragTo(int y)
        {
            float trackH = host.Height - Ui.S(8);
            RectangleF th = ThumbRect();
            float range = trackH - th.Height;
            int max = MaxOffset;
            if (range > 0) Offset = (int)Math.Max(0, Math.Min(max, (y - grab - Ui.S(4)) / range * max));
        }

        /// <summary>The button was released. Returns true if a thumb drag has just ended.</summary>
        public bool EndDrag()
        {
            bool was = dragging;
            dragging = false;
            return was;
        }

        public void Paint(Graphics g, Theme t)
        {
            RectangleF thumb = ThumbRect();
            if (thumb.IsEmpty) return;
            bool bold = Hover || dragging;
            float w = bold ? Ui.S(6) : Ui.S(4);
            RectangleF tr = new RectangleF(host.Width - Ui.S(6) - w / 2f, thumb.Y, w, thumb.Height);
            Ui.FillRound(g, Ui.Alpha(t.Muted, bold ? 200 : 120), tr, w / 2f);
        }
    }
}
