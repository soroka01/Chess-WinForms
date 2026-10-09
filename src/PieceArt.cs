using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ChessGame
{
    /// <summary>Renders chess pieces from the Unicode chess glyphs of a system font.</summary>
    public static class PieceArt
    {
        public static readonly string FontName = PickFont();

        static string PickFont()
        {
            string[] candidates = { "Segoe UI Symbol", "Arial Unicode MS", "DejaVu Sans", "Segoe UI Emoji" };
            try
            {
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    foreach (string name in candidates)
                        foreach (FontFamily f in fonts.Families)
                            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                                return f.Name;
                }
            }
            catch (Exception) { }
            return "Segoe UI Symbol";
        }

        /// <summary>Filled ("black") glyph for a piece type 1..6.</summary>
        public static char Solid(int type)
        {
            return (char)(0x2660 - type);
        }

        /// <summary>Outlined ("white") glyph for a piece type 1..6.</summary>
        public static char Outline(int type)
        {
            return (char)(0x265A - type);
        }

        static GraphicsPath Glyph(FontFamily family, char ch, float emSize)
        {
            GraphicsPath path = new GraphicsPath(FillMode.Winding);
            path.AddString(ch.ToString(), family, (int)FontStyle.Regular, emSize, new PointF(0, 0), StringFormat.GenericTypographic);
            return path;
        }

        /// <summary>Draws a piece (positive = white, negative = black) into a transparent square bitmap.</summary>
        public static Bitmap Render(int piece, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            int type = Math.Abs(piece);
            bool white = piece > 0;

            using (Graphics g = Graphics.FromImage(bmp))
            using (FontFamily family = new FontFamily(FontName))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // scale so that the king fills roughly 80% of the square; other pieces share the same scale
                float em = size * 0.8f;
                using (GraphicsPath kingPath = Glyph(family, Solid(Pc.King), 100f))
                {
                    float kh = kingPath.GetBounds().Height;
                    if (kh > 1f) em = size * 0.8f * 100f / kh;
                }

                using (GraphicsPath solid = Glyph(family, Solid(type), em))
                using (GraphicsPath outline = Glyph(family, Outline(type), em))
                {
                    RectangleF b = solid.GetBounds();
                    using (Matrix m = new Matrix())
                    {
                        m.Translate(size / 2f - (b.X + b.Width / 2f), size * 0.92f - b.Bottom);
                        solid.Transform(m);
                        outline.Transform(m);
                    }

                    if (white)
                    {
                        g.FillPath(Brushes.White, solid);
                        using (Brush ink = new SolidBrush(Color.FromArgb(30, 30, 30)))
                            g.FillPath(ink, outline);
                    }
                    else
                    {
                        using (Brush body = new SolidBrush(Color.FromArgb(40, 40, 40)))
                            g.FillPath(body, solid);
                        using (Pen rim = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, size / 70f)))
                        {
                            rim.LineJoin = LineJoin.Round;
                            g.DrawPath(rim, solid);
                        }
                    }
                }
            }
            return bmp;
        }
    }
}
