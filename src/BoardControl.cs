using System;
using ChessMove = ChessGame.Move;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// The chess board: painting in the current theme, click-to-move and drag-and-drop input, move animation,
    /// a read-only "view" of an earlier position, a hint arrow and the end-of-game card.
    /// </summary>
    public sealed class BoardControl : Control
    {
        /// <summary>Raised when the user completes a legal move.</summary>
        public event Action<ChessMove> UserMove;

        /// <summary>The button on the end-of-game card was pressed.</summary>
        public event Action ResultActionClicked;

        Game game;
        bool flipped;
        int humanMask = 3;               // bit 0 = user controls white, bit 1 = user controls black
        int selected = -1;
        readonly List<ChessMove> selectedMoves = new List<ChessMove>();

        bool dragging;
        bool dragMoved;
        bool wasSelectedOnDown;
        int dragFrom = -1;
        Point dragStart;
        Point dragPos;

        int cell, originX, originY;
        Font coordFont;
        int coordFontCell;

        // an earlier position shown instead of the live one (set by the main window)
        Position viewPos;
        int viewFrom = -1, viewTo = -1;

        int hintFrom = -1, hintTo = -1;

        // a move queued while the opponent is to move; the main window plays it when it becomes our turn (premove)
        int premoveFrom = -1, premoveTo = -1;
        List<int> premoveTargets = new List<int>();

        /// <summary>The queued move was set or cancelled.</summary>
        public event Action PremoveChanged;

        public bool HasPremove { get { return premoveFrom >= 0; } }

        public string PremoveText { get { return premoveFrom < 0 ? "" : Sqr.Name(premoveFrom) + "-" + Sqr.Name(premoveTo); } }

        /// <summary>True while the user's side is waiting for the opponent: a move may be queued.</summary>
        public bool CanPremove()
        {
            if (game == null || viewPos != null || game.Status != GameStatus.Playing) return false;
            if (humanMask != 1 && humanMask != 2) return false;   // both sides at one screen: no waiting
            return game.Pos.WhiteToMove != (humanMask == 1);
        }

        /// <summary>Queues from -> to for later. Only the piece's geometry is checked now.</summary>
        public bool TrySetPremove(int from, int to)
        {
            if (!CanPremove()) return false;
            int p = game.Pos.Sq[from];
            if (p == 0 || (p > 0) != (humanMask == 1)) return false;
            if (!Premoves.Targets(game.Pos, from).Contains(to)) return false;
            premoveFrom = from;
            premoveTo = to;
            ResetSelection();
            if (PremoveChanged != null) PremoveChanged();
            return true;
        }

        public void ClearPremove()
        {
            if (premoveFrom < 0) return;
            premoveFrom = premoveTo = -1;
            Invalidate();
            if (PremoveChanged != null) PremoveChanged();
        }

        /// <summary>Hands the queued move over (and forgets it).</summary>
        public bool TakePremove(out int from, out int to)
        {
            from = premoveFrom;
            to = premoveTo;
            if (premoveFrom < 0) return false;
            premoveFrom = premoveTo = -1;
            Invalidate();
            if (PremoveChanged != null) PremoveChanged();
            return true;
        }

        // end-of-game card
        string resultTitle, resultSub, resultAction;
        bool resultDismissed;
        Rectangle actionRect, closeRect;
        int overlayHover;   // 0 none, 1 action, 2 close

        // move animation: pieces slide from their old square to the new one
        sealed class Slide
        {
            public int Piece, FromSq, ToSq;
        }
        readonly List<Slide> slides = new List<Slide>();
        int animCapturedPiece, animCapturedSq = -1;   // captured piece stays on its square until the slide ends
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();
        int animDurationMs;
        readonly Timer animTimer = new Timer();
        bool skipNextAnimation;

        /// <summary>Turn the slide animation off (for example in tests).</summary>
        public bool AnimationsEnabled = true;

        public BoardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = false;
            animTimer.Interval = 15;
            animTimer.Tick += delegate { OnAnimationTick(); };
        }

        static Theme T { get { return Theme.Current; } }

        public Game Game
        {
            get { return game; }
            set
            {
                if (game != null)
                {
                    game.MovePlayed -= OnMovePlayed;
                    game.PositionReset -= OnPositionReset;
                }
                game = value;
                if (game != null)
                {
                    game.MovePlayed += OnMovePlayed;
                    game.PositionReset += OnPositionReset;
                }
                StopAnimation();
                ResetSelection();
            }
        }

        public bool Flipped
        {
            get { return flipped; }
            set { flipped = value; Invalidate(); }
        }

        /// <summary>Which sides the user may move: 1 = white, 2 = black, 3 = both, 0 = none.</summary>
        public int HumanMask
        {
            get { return humanMask; }
            set { humanMask = value; ResetSelection(); ClearPremove(); }
        }

        public bool IsViewing { get { return viewPos != null; } }

        /// <summary>Shows an earlier position read-only (null = back to the live game).</summary>
        public void SetView(Position pos, int lastFrom, int lastTo)
        {
            viewPos = pos;
            viewFrom = lastFrom;
            viewTo = lastTo;
            StopAnimation();
            hintFrom = hintTo = -1;
            ResetSelection();
        }

        public void SetHint(int from, int to)
        {
            hintFrom = from;
            hintTo = to;
            Invalidate();
        }

        public void ClearHint()
        {
            if (hintFrom < 0) return;
            hintFrom = hintTo = -1;
            Invalidate();
        }

        /// <summary>Shows (or, with a null title, removes) the card that announces the result of a finished game.</summary>
        public void SetResult(string title, string subtitle, string actionText)
        {
            if (title != resultTitle) resultDismissed = false;
            resultTitle = title;
            resultSub = subtitle;
            resultAction = actionText;
            Invalidate();
        }

        public void ResetSelection()
        {
            selected = -1;
            selectedMoves.Clear();
            premoveTargets.Clear();
            dragging = false;
            dragMoved = false;
            Invalidate();
        }

        public int CellSize { get { return Math.Max(8, (Math.Min(ClientSize.Width, ClientSize.Height) - 2 * Margin8) / 8); } }

        static int Margin8 { get { return Ui.S(8); } }

        // ------------------------------------------------------------ animation

        void OnMovePlayed(ChessMove m, int captured)
        {
            hintFrom = hintTo = -1;
            if (premoveFrom >= 0)
            {
                // the piece we wanted to move may just have been captured
                int queued = game.Pos.Sq[premoveFrom];
                if (queued == 0 || (queued > 0) != (humanMask == 1)) ClearPremove();
            }
            if (viewPos != null) return;                                    // looking at an old position: nothing slides
            if (skipNextAnimation) { skipNextAnimation = false; return; }   // the user already dragged the piece there
            StartAnimation(m, captured);
        }

        void OnPositionReset()
        {
            hintFrom = hintTo = -1;
            ClearPremove();
            StopAnimation();
            Invalidate();
        }

        void StartAnimation(ChessMove m, int captured)
        {
            StopAnimation();
            if (!AnimationsEnabled || game == null || !IsHandleCreated) return;

            int[] sq = game.Pos.Sq;
            int piece = sq[m.To];
            if (piece == 0) return;
            bool white = piece > 0;
            if (m.Promo != 0) piece = white ? Pc.Pawn : -Pc.Pawn;   // the pawn slides, then turns into the new piece

            Slide main = new Slide();
            main.Piece = piece; main.FromSq = m.From; main.ToSq = m.To;
            slides.Add(main);

            if ((m.Flags & ChessMove.CastleK) != 0)
                slides.Add(new Slide { Piece = sq[m.To - 1], FromSq = m.To + 1, ToSq = m.To - 1 });
            else if ((m.Flags & ChessMove.CastleQ) != 0)
                slides.Add(new Slide { Piece = sq[m.To + 1], FromSq = m.To - 2, ToSq = m.To + 1 });

            if (captured != 0)
            {
                animCapturedPiece = captured;
                animCapturedSq = (m.Flags & ChessMove.EnPassant) != 0 ? (white ? m.To - 8 : m.To + 8) : m.To;
            }

            int dist = Math.Max(Math.Abs((m.From & 7) - (m.To & 7)), Math.Abs((m.From >> 3) - (m.To >> 3)));
            animDurationMs = 150 + 25 * dist;
            animClock.Reset();
            animClock.Start();
            animTimer.Start();
            Invalidate();
        }

        void StopAnimation()
        {
            animTimer.Stop();
            animClock.Stop();
            slides.Clear();
            animCapturedPiece = 0;
            animCapturedSq = -1;
        }

        void OnAnimationTick()
        {
            if (animClock.ElapsedMilliseconds >= animDurationMs) StopAnimation();
            Invalidate();
        }

        bool IsSlideTarget(int sq)
        {
            foreach (Slide s in slides)
                if (s.ToSq == sq) return true;
            return false;
        }

        // ------------------------------------------------------------ geometry

        void CalcLayout()
        {
            cell = CellSize;
            originX = (ClientSize.Width - cell * 8) / 2;
            originY = (ClientSize.Height - cell * 8) / 2;
        }

        Point SquareOrigin(int sq)
        {
            int f = sq & 7, r = sq >> 3;
            int col = flipped ? 7 - f : f;
            int row = flipped ? r : 7 - r;
            return new Point(originX + col * cell, originY + row * cell);
        }

        PointF SquareCenter(int sq)
        {
            Point o = SquareOrigin(sq);
            return new PointF(o.X + cell / 2f, o.Y + cell / 2f);
        }

        int HitTest(Point p)
        {
            if (p.X < originX || p.Y < originY) return -1;
            int col = (p.X - originX) / cell;
            int row = (p.Y - originY) / cell;
            if (col > 7 || row > 7) return -1;
            int f = flipped ? 7 - col : col;
            int r = flipped ? row : 7 - row;
            return r * 8 + f;
        }

        // ------------------------------------------------------------ theme

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Changed += OnStyleChanged;
            BoardSchemes.Changed += OnStyleChanged;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= OnStyleChanged;
            BoardSchemes.Changed -= OnStyleChanged;
            base.OnHandleDestroyed(e);
        }

        void OnStyleChanged()
        {
            if (!IsDisposed) Invalidate();
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme theme = T;
            g.Clear(theme.Window);
            if (game == null) return;

            CalcLayout();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Position pos = viewPos ?? game.Pos;
            BoardScheme scheme = BoardSchemes.Current;
            RectangleF boardRect = new RectangleF(originX, originY, cell * 8, cell * 8);
            float corner = Ui.S(8f);

            // soft shadow under the board
            for (int i = 6; i >= 1; i--)
            {
                RectangleF s = boardRect;
                s.Inflate(i * Ui.S(1.6f), i * Ui.S(1.6f));
                s.Offset(0, Ui.S(2f));
                Ui.FillRound(g, Color.FromArgb(theme.IsDark ? 16 : 10, 0, 0, 0), s, corner + i * Ui.S(1.6f));
            }

            int checkSq = -1;
            if (pos.InCheck(pos.WhiteToMove)) checkSq = pos.WhiteToMove ? pos.WhiteKing : pos.BlackKing;

            if (coordFont == null || coordFontCell != cell)
            {
                if (coordFont != null) coordFont.Dispose();
                coordFont = new Font("Segoe UI", Math.Max(7f, cell / 6.5f), FontStyle.Bold, GraphicsUnit.Pixel);
                coordFontCell = cell;
            }

            using (SolidBrush lightBrush = new SolidBrush(scheme.Light))
            using (SolidBrush darkBrush = new SolidBrush(scheme.Dark))
            {
                for (int sq = 0; sq < 64; sq++)
                {
                    Point o = SquareOrigin(sq);
                    bool light = (((sq & 7) + (sq >> 3)) & 1) == 1;
                    g.FillRectangle(light ? lightBrush : darkBrush, o.X, o.Y, cell, cell);
                }
            }

            // last move
            int lastFrom = -1, lastTo = -1;
            if (viewPos != null) { lastFrom = viewFrom; lastTo = viewTo; }
            else if (game.HasLastMove) { ChessMove lm = game.LastMove; lastFrom = lm.From; lastTo = lm.To; }
            if (lastFrom >= 0)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(120, 205, 210, 60)))
                {
                    Point a = SquareOrigin(lastFrom), c = SquareOrigin(lastTo);
                    g.FillRectangle(b, a.X, a.Y, cell, cell);
                    g.FillRectangle(b, c.X, c.Y, cell, cell);
                }
            }

            // selection
            if (selected >= 0)
            {
                Point o = SquareOrigin(selected);
                using (Brush b = new SolidBrush(Color.FromArgb(140, 90, 190, 60)))
                    g.FillRectangle(b, o.X, o.Y, cell, cell);
            }

            // queued move
            if (premoveFrom >= 0 && viewPos == null)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(125, 220, 70, 50)))
                {
                    Point a = SquareOrigin(premoveFrom), c = SquareOrigin(premoveTo);
                    g.FillRectangle(b, a.X, a.Y, cell, cell);
                    g.FillRectangle(b, c.X, c.Y, cell, cell);
                }
            }

            // king in check
            if (checkSq >= 0)
            {
                Point o = SquareOrigin(checkSq);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(o.X - cell * 0.1f, o.Y - cell * 0.1f, cell * 1.2f, cell * 1.2f);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterColor = Color.FromArgb(235, 235, 40, 30);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, 235, 40, 30) };
                        Region old = g.Clip;
                        g.SetClip(new Rectangle(o.X, o.Y, cell, cell));
                        g.FillPath(pg, gp);
                        g.Clip = old;
                    }
                }
            }

            // coordinates
            using (SolidBrush lightText = new SolidBrush(scheme.Light))
            using (SolidBrush darkText = new SolidBrush(scheme.Dark))
            {
                for (int i = 0; i < 8; i++)
                {
                    int fileOnBottom = flipped ? 7 - i : i;
                    int sqBottom = (flipped ? 7 : 0) * 8 + fileOnBottom;
                    Point ob = SquareOrigin(sqBottom);
                    bool lightB = (((sqBottom & 7) + (sqBottom >> 3)) & 1) == 1;
                    string s = ((char)('a' + fileOnBottom)).ToString();
                    SizeF sz = g.MeasureString(s, coordFont);
                    g.DrawString(s, coordFont, lightB ? darkText : lightText, ob.X + cell - sz.Width + 1, ob.Y + cell - sz.Height + 1);

                    int rankOnLeft = flipped ? i : 7 - i;
                    int fileLeft = flipped ? 7 : 0;
                    int sqLeft = rankOnLeft * 8 + fileLeft;
                    Point ol = SquareOrigin(sqLeft);
                    bool lightL = (((sqLeft & 7) + (sqLeft >> 3)) & 1) == 1;
                    g.DrawString((rankOnLeft + 1).ToString(), coordFont, lightL ? darkText : lightText, ol.X + 2, ol.Y + 1);
                }
            }

            // round the four outer corners of the board
            using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
            {
                RectangleF outer = boardRect;
                outer.Inflate(2, 2);
                ring.AddRectangle(outer);
                ring.AddPath(Ui.RoundPath(boardRect, corner), false);
                using (SolidBrush b = new SolidBrush(theme.Window)) g.FillPath(b, ring);
            }
            Ui.DrawRound(g, Ui.Alpha(theme.Border, 200), boardRect, corner, 1f);

            // pieces
            for (int sq = 0; sq < 64; sq++)
            {
                int p = pos.Sq[sq];
                if (p == 0) continue;
                if (dragging && dragMoved && sq == dragFrom && viewPos == null) continue;
                if (slides.Count > 0 && viewPos == null && IsSlideTarget(sq)) continue;   // drawn sliding below
                Point o = SquareOrigin(sq);
                g.DrawImageUnscaled(PieceCache.Get(p, cell), o.X, o.Y);
            }

            // sliding pieces (ease-out), plus the captured piece waiting on its square
            if (slides.Count > 0 && viewPos == null)
            {
                if (animCapturedSq >= 0)
                {
                    Point oc = SquareOrigin(animCapturedSq);
                    g.DrawImageUnscaled(PieceCache.Get(animCapturedPiece, cell), oc.X, oc.Y);
                }
                double t = Math.Min(1.0, animClock.ElapsedMilliseconds / (double)animDurationMs);
                double k = 1.0 - Math.Pow(1.0 - t, 3.0);
                foreach (Slide s in slides)
                {
                    Point a = SquareOrigin(s.FromSq), b = SquareOrigin(s.ToSq);
                    int x = a.X + (int)Math.Round((b.X - a.X) * k);
                    int y = a.Y + (int)Math.Round((b.Y - a.Y) * k);
                    g.DrawImageUnscaled(PieceCache.Get(s.Piece, cell), x, y);
                }
            }

            // the queued piece, faded, on its destination
            if (premoveFrom >= 0 && viewPos == null)
            {
                int ghost = pos.Sq[premoveFrom];
                if (ghost != 0)
                {
                    Point o = SquareOrigin(premoveTo);
                    DrawFaded(g, PieceCache.Get(ghost, cell), o.X, o.Y, 0.6f);
                }
            }

            // where the selected piece may be queued
            if (selected >= 0 && viewPos == null)
            {
                foreach (int t in premoveTargets)
                {
                    Point o = SquareOrigin(t);
                    if (pos.Sq[t] != 0)
                    {
                        float w = Math.Max(3f, cell / 11f);
                        using (Pen pen = new Pen(Color.FromArgb(150, 215, 90, 40), w))
                            g.DrawEllipse(pen, o.X + w / 2f, o.Y + w / 2f, cell - w, cell - w);
                    }
                    else
                    {
                        float d = cell * 0.30f;
                        using (Brush b = new SolidBrush(Color.FromArgb(140, 215, 90, 40)))
                            g.FillEllipse(b, o.X + (cell - d) / 2f, o.Y + (cell - d) / 2f, d, d);
                    }
                }
            }

            // legal move markers
            foreach (ChessMove m in selectedMoves)
            {
                if (m.Promo != 0 && m.Promo != Pc.Queen) continue;   // draw each target once
                Point o = SquareOrigin(m.To);
                bool capture = pos.Sq[m.To] != 0 || (m.Flags & ChessMove.EnPassant) != 0;
                if (capture)
                {
                    float w = Math.Max(3f, cell / 11f);
                    using (Pen pen = new Pen(Color.FromArgb(120, 20, 85, 30), w))
                        g.DrawEllipse(pen, o.X + w / 2f, o.Y + w / 2f, cell - w, cell - w);
                }
                else
                {
                    float d = cell * 0.30f;
                    using (Brush b = new SolidBrush(Color.FromArgb(110, 20, 85, 30)))
                        g.FillEllipse(b, o.X + (cell - d) / 2f, o.Y + (cell - d) / 2f, d, d);
                }
            }

            if (hintFrom >= 0 && viewPos == null) DrawArrow(g, hintFrom, hintTo);

            // piece being dragged
            if (dragging && dragMoved && dragFrom >= 0 && viewPos == null)
            {
                int p = pos.Sq[dragFrom];
                if (p != 0)
                    g.DrawImageUnscaled(PieceCache.Get(p, cell), dragPos.X - cell / 2, dragPos.Y - cell / 2);
            }

            if (OverlayVisible) DrawResultCard(g, boardRect, corner);
        }

        static void DrawFaded(Graphics g, Bitmap bmp, int x, int y, float alpha)
        {
            using (ImageAttributes attrs = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = alpha;
                attrs.SetColorMatrix(cm);
                g.DrawImage(bmp, new Rectangle(x, y, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            }
        }

        void DrawArrow(Graphics g, int from, int to)
        {
            PointF a = SquareCenter(from), b = SquareCenter(to);
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1f) return;
            float width = cell * 0.17f;
            // stop the shaft a little before the target centre so the head lands inside the square
            float ux = dx / len, uy = dy / len;
            PointF tip = new PointF(b.X - ux * cell * 0.08f, b.Y - uy * cell * 0.08f);
            PointF start = new PointF(a.X + ux * cell * 0.18f, a.Y + uy * cell * 0.18f);
            Color c = Color.FromArgb(215, 46, 134, 222);
            using (Pen pen = new Pen(c, width))
            using (AdjustableArrowCap cap = new AdjustableArrowCap(2.4f, 1.9f, true))
            {
                pen.CustomEndCap = cap;
                pen.StartCap = LineCap.Round;
                g.DrawLine(pen, start, tip);
            }
        }

        // ------------------------------------------------------------ end-of-game card

        bool OverlayVisible
        {
            get { return resultTitle != null && !resultDismissed && viewPos == null; }
        }

        void DrawResultCard(Graphics g, RectangleF boardRect, float corner)
        {
            Theme t = T;
            using (GraphicsPath p = Ui.RoundPath(boardRect, corner))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 8, 10, 14)))
                g.FillPath(dim, p);

            int cw = Math.Min(cell * 8 - Ui.S(32), Ui.S(380));
            int ch = Ui.S(176);
            Rectangle card = new Rectangle((int)(boardRect.X + (boardRect.Width - cw) / 2), (int)(boardRect.Y + (boardRect.Height - ch) / 2), cw, ch);
            Ui.FillRound(g, t.Card, card, Ui.S(16f));
            Ui.DrawRound(g, t.Border, card, Ui.S(16f), 1f);

            Ui.Text(g, resultTitle, Ui.Font(22f, FontStyle.Bold), t.Text,
                new Rectangle(card.X, card.Y + Ui.S(16), card.Width, Ui.S(44)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Ui.Text(g, resultSub ?? "", Ui.Font(11f), t.Muted,
                new Rectangle(card.X + Ui.S(12), card.Y + Ui.S(60), card.Width - Ui.S(24), Ui.S(32)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int bh = Ui.S(40), by = card.Bottom - Ui.S(18) - bh, gap = Ui.S(10), margin = Ui.S(20);
            if (resultAction != null)
            {
                int bw = (card.Width - margin * 2 - gap) / 2;
                actionRect = new Rectangle(card.X + margin, by, bw, bh);
                closeRect = new Rectangle(actionRect.Right + gap, by, bw, bh);
                DrawOverlayButton(g, actionRect, resultAction, true, overlayHover == 1);
            }
            else
            {
                actionRect = Rectangle.Empty;
                closeRect = new Rectangle(card.X + margin, by, card.Width - margin * 2, bh);
            }
            DrawOverlayButton(g, closeRect, "Закрыть", false, overlayHover == 2);
        }

        void DrawOverlayButton(Graphics g, Rectangle r, string text, bool primary, bool hot)
        {
            Theme t = T;
            Color fill = primary ? (hot ? t.AccentHover : t.Accent) : (hot ? Ui.Mix(t.Field, t.Text, 0.12f) : t.Field);
            Ui.FillRound(g, fill, r, Ui.S(10f));
            Ui.Text(g, text, Ui.Font(10f, primary ? FontStyle.Bold : FontStyle.Regular), primary ? t.OnAccent : t.Text, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (coordFont != null) coordFont.Dispose();
                animTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ input

        bool CanInteract()
        {
            if (game == null || viewPos != null || game.Status != GameStatus.Playing) return false;
            int bit = game.Pos.WhiteToMove ? 1 : 2;
            return (humanMask & bit) != 0;
        }

        bool IsOwnPiece(int sq)
        {
            int p = game.Pos.Sq[sq];
            if (p == 0) return false;
            bool white = CanPremove() ? humanMask == 1 : game.Pos.WhiteToMove;   // while waiting, "ours" is the human side
            return (p > 0) == white;
        }

        void SelectSquare(int sq)
        {
            selected = sq;
            selectedMoves.Clear();
            foreach (ChessMove m in game.Legal)
                if (m.From == sq) selectedMoves.Add(m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) Focus();   // so that the arrow keys step through the game after clicking the board
            if (e.Button == MouseButtons.Right) { ClearPremove(); ResetSelection(); return; }
            if (e.Button != MouseButtons.Left) return;

            if (OverlayVisible)
            {
                if (actionRect.Contains(e.Location)) { if (ResultActionClicked != null) ResultActionClicked(); return; }
                if (closeRect.Contains(e.Location)) { resultDismissed = true; overlayHover = 0; Invalidate(); return; }
            }
            if (CanPremove()) { PremoveMouseDown(e); return; }
            if (!CanInteract()) return;
            CalcLayout();
            int sq = HitTest(e.Location);
            if (sq < 0) { ResetSelection(); return; }

            ClearHint();
            if (selected >= 0 && selected != sq && TryMove(selected, sq, false)) return;

            if (IsOwnPiece(sq))
            {
                wasSelectedOnDown = selected == sq;
                SelectSquare(sq);
                dragging = true;
                dragMoved = false;
                dragFrom = sq;
                dragStart = e.Location;
                dragPos = e.Location;
            }
            else
            {
                ResetSelection();
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (OverlayVisible)
            {
                int hot = actionRect.Contains(e.Location) ? 1 : (closeRect.Contains(e.Location) ? 2 : 0);
                if (hot != overlayHover) { overlayHover = hot; Invalidate(); }
                Cursor = hot != 0 ? Cursors.Hand : Cursors.Default;
                return;
            }
            if (dragging)
            {
                dragPos = e.Location;
                if (!dragMoved && (Math.Abs(e.X - dragStart.X) > 4 || Math.Abs(e.Y - dragStart.Y) > 4))
                    dragMoved = true;
                if (dragMoved) Invalidate();
                return;
            }
            if (CanInteract() || CanPremove())
            {
                CalcLayout();
                int sq = HitTest(e.Location);
                Cursor = (sq >= 0 && IsOwnPiece(sq)) ? Cursors.Hand : Cursors.Default;
            }
            else if (Cursor != Cursors.Default)
            {
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            bool moved = dragMoved;
            int from = dragFrom;
            dragging = false;
            dragMoved = false;

            if (moved)
            {
                CalcLayout();
                int sq = HitTest(e.Location);
                if (sq >= 0 && sq != from)
                {
                    if (CanPremove()) TrySetPremove(from, sq);
                    else TryMove(from, sq, true);
                }
            }
            else if (wasSelectedOnDown)
            {
                ResetSelection();   // clicking the selected piece again deselects it
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (overlayHover != 0) { overlayHover = 0; Invalidate(); }
            if (!dragging) Cursor = Cursors.Default;
        }

        /// <summary>Mouse press while waiting for the opponent: choose a piece to queue, or the square to queue it to.</summary>
        void PremoveMouseDown(MouseEventArgs e)
        {
            CalcLayout();
            int sq = HitTest(e.Location);
            if (sq < 0) { ResetSelection(); return; }

            if (selected >= 0 && selected != sq && premoveTargets.Contains(sq))
            {
                TrySetPremove(selected, sq);
                return;
            }
            if (IsOwnPiece(sq))
            {
                wasSelectedOnDown = selected == sq;
                selected = sq;
                selectedMoves.Clear();
                premoveTargets = Premoves.Targets(game.Pos, sq);
                dragging = true;
                dragMoved = false;
                dragFrom = sq;
                dragStart = e.Location;
                dragPos = e.Location;
            }
            else
            {
                ResetSelection();
                ClearPremove();   // a click on an empty square cancels the queued move
            }
            Invalidate();
        }

        /// <summary>Attempts the move from -> to. Returns true if a move was made or the user was asked about it.</summary>
        bool TryMove(int from, int to, bool dragged)
        {
            List<ChessMove> matches = new List<ChessMove>();
            foreach (ChessMove m in game.Legal)
                if (m.From == from && m.To == to) matches.Add(m);
            if (matches.Count == 0) return false;

            ChessMove chosen = matches[0];
            if (matches.Count > 1)
            {
                bool white = game.Pos.WhiteToMove;
                int choice;
                using (PromotionDialog dlg = new PromotionDialog(white, CellSize))
                {
                    dlg.ShowDialog(FindForm());
                    choice = dlg.Choice;
                }
                if (choice == 0) { Invalidate(); return true; }   // cancelled
                foreach (ChessMove m in matches)
                    if (m.Promo == choice) chosen = m;
            }

            ResetSelection();
            Action<ChessMove> handler = UserMove;
            skipNextAnimation = dragged;   // a dragged piece is already at its destination
            try
            {
                if (handler != null) handler(chosen);
            }
            finally
            {
                skipNextAnimation = false;
            }
            return true;
        }
    }

    /// <summary>Modal dialog for choosing the promotion piece, in the program's theme.</summary>
    public sealed class PromotionDialog : ThemedDialog
    {
        sealed class PieceButton : Control
        {
            readonly int piece;
            readonly int size;
            readonly bool lightSquare;
            bool hot;

            public PieceButton(int piece, int size, bool lightSquare)
            {
                this.piece = piece;
                this.size = size;
                this.lightSquare = lightSquare;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Current.Window);
                BoardScheme s = BoardSchemes.Current;
                RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                Ui.FillRound(g, lightSquare ? s.Light : s.Dark, r, Ui.S(10f));
                if (hot) Ui.DrawRound(g, Theme.Current.Accent, r, Ui.S(10f), 3f);
                g.DrawImageUnscaled(PieceCache.Get(piece, size), (Width - size) / 2, (Height - size) / 2);
            }
        }

        /// <summary>Chosen piece type (Queen, Rook, Bishop or Knight), or 0 if cancelled.</summary>
        public int Choice;

        public PromotionDialog(bool white, int cell)
        {
            Text = "Превращение пешки";
            int size = Math.Min(Math.Max(cell, Ui.S(56)), Ui.S(96));
            int pad = Ui.S(12);
            int[] types = { Pc.Queen, Pc.Rook, Pc.Bishop, Pc.Knight };
            ClientSize = new Size(pad + types.Length * (size + pad), size + pad * 2);

            for (int i = 0; i < types.Length; i++)
            {
                int type = types[i];
                PieceButton btn = new PieceButton(white ? type : -type, size, (i & 1) == 0);
                btn.SetBounds(pad + i * (size + pad), pad, size, size);
                btn.Click += delegate
                {
                    Choice = type;
                    DialogResult = DialogResult.OK;
                };
                Controls.Add(btn);
            }
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                else if (e.KeyCode == Keys.Q) { Choice = Pc.Queen; DialogResult = DialogResult.OK; }
                else if (e.KeyCode == Keys.R) { Choice = Pc.Rook; DialogResult = DialogResult.OK; }
                else if (e.KeyCode == Keys.B) { Choice = Pc.Bishop; DialogResult = DialogResult.OK; }
                else if (e.KeyCode == Keys.N) { Choice = Pc.Knight; DialogResult = DialogResult.OK; }
            };
        }
    }
}
