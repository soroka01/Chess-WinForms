using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>The colour palette of the whole program. Switch with Theme.Set; controls repaint on Theme.Changed.</summary>
    public sealed class Theme
    {
        public bool IsDark;
        public string Name;
        public Color Window;       // behind everything
        public Color Card;         // raised panels
        public Color CardHover;
        public Color Field;        // buttons, inputs, tracks on a card
        public Color Border;
        public Color Text;
        public Color Muted;
        public Color Accent;
        public Color AccentHover;
        public Color AccentPress;
        public Color OnAccent;     // text on accent fills
        public Color Danger;
        public Color DangerHover;
        public Color Warn;
        public Color Gold;

        public static readonly Theme Dark = new Theme
        {
            IsDark = true, Name = "dark",
            Window = Color.FromArgb(21, 23, 27),
            Card = Color.FromArgb(31, 34, 40),
            CardHover = Color.FromArgb(40, 44, 52),
            Field = Color.FromArgb(44, 48, 57),
            Border = Color.FromArgb(52, 57, 67),
            Text = Color.FromArgb(235, 237, 241),
            Muted = Color.FromArgb(140, 148, 162),
            Accent = Color.FromArgb(112, 190, 98),
            AccentHover = Color.FromArgb(132, 207, 118),
            AccentPress = Color.FromArgb(92, 166, 80),
            OnAccent = Color.FromArgb(14, 26, 10),
            Danger = Color.FromArgb(214, 84, 78),
            DangerHover = Color.FromArgb(230, 104, 98),
            Warn = Color.FromArgb(232, 172, 62),
            Gold = Color.FromArgb(240, 204, 96)
        };

        public static readonly Theme Light = new Theme
        {
            IsDark = false, Name = "light",
            Window = Color.FromArgb(236, 239, 243),
            Card = Color.FromArgb(255, 255, 255),
            CardHover = Color.FromArgb(245, 247, 250),
            Field = Color.FromArgb(238, 241, 245),
            Border = Color.FromArgb(220, 225, 232),
            Text = Color.FromArgb(28, 32, 38),
            Muted = Color.FromArgb(104, 113, 127),
            Accent = Color.FromArgb(58, 140, 84),
            AccentHover = Color.FromArgb(70, 156, 98),
            AccentPress = Color.FromArgb(46, 120, 70),
            OnAccent = Color.FromArgb(255, 255, 255),
            Danger = Color.FromArgb(200, 64, 58),
            DangerHover = Color.FromArgb(218, 84, 78),
            Warn = Color.FromArgb(214, 140, 20),
            Gold = Color.FromArgb(196, 146, 20)
        };

        static Theme current = Dark;

        public static Theme Current { get { return current; } }

        public static event Action Changed;

        public static void Set(Theme t)
        {
            if (t == null || t == current) return;
            current = t;
            if (Changed != null) Changed();
        }

        public static Theme FromName(string name)
        {
            return name == "light" ? Light : Dark;
        }
    }

    /// <summary>Colours of the 64 squares.</summary>
    public sealed class BoardScheme
    {
        public string Name;
        public Color Light;
        public Color Dark;
    }

    public static class BoardSchemes
    {
        public static readonly BoardScheme[] All =
        {
            new BoardScheme { Name = "Классика", Light = Color.FromArgb(240, 217, 181), Dark = Color.FromArgb(181, 136, 99) },
            new BoardScheme { Name = "Зелёная", Light = Color.FromArgb(238, 238, 210), Dark = Color.FromArgb(118, 150, 86) },
            new BoardScheme { Name = "Синяя", Light = Color.FromArgb(222, 227, 230), Dark = Color.FromArgb(140, 162, 173) },
            new BoardScheme { Name = "Графит", Light = Color.FromArgb(205, 206, 211), Dark = Color.FromArgb(110, 114, 124) }
        };

        static int index;

        public static int Index { get { return index; } }
        public static BoardScheme Current { get { return All[index]; } }
        public static event Action Changed;

        public static void Set(int i)
        {
            i = ((i % All.Length) + All.Length) % All.Length;
            if (i == index) return;
            index = i;
            if (Changed != null) Changed();
        }
    }

    /// <summary>Scaling, fonts and drawing helpers shared by the owner-drawn controls.</summary>
    public static class Ui
    {
        public static readonly float Scale = ComputeScale();
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static readonly string iconFontName = FindIconFont();

        static float ComputeScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f);
            }
            catch (Exception) { return 1f; }
        }

        /// <summary>Logical pixels (at 96 DPI) to device pixels.</summary>
        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float S(float px) { return px * Scale; }

        /// <summary>A cached UI font. Never dispose these.</summary>
        public static Font Font(float points, FontStyle style)
        {
            string key = points + "/" + (int)style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font("Segoe UI", points, style, GraphicsUnit.Point);
                fonts[key] = f;
            }
            return f;
        }

        public static Font Font(float points) { return Font(points, FontStyle.Regular); }

        /// <summary>True when an icon font (Segoe MDL2 Assets / Segoe Fluent Icons) is installed.</summary>
        public static bool HasIcons { get { return iconFontName != null; } }

        public static Font Icons(float points)
        {
            string key = "icons/" + points;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(iconFontName ?? "Segoe UI", points, FontStyle.Regular, GraphicsUnit.Point);
                fonts[key] = f;
            }
            return f;
        }

        static string FindIconFont()
        {
            try
            {
                using (InstalledFontCollection installed = new InstalledFontCollection())
                {
                    foreach (string wanted in new string[] { "Segoe MDL2 Assets", "Segoe Fluent Icons" })
                        foreach (FontFamily family in installed.Families)
                            if (string.Equals(family.Name, wanted, StringComparison.OrdinalIgnoreCase)) return family.Name;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static GraphicsPath RoundPath(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = RoundPath(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width)
        {
            using (GraphicsPath p = RoundPath(r, radius))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        public static void Text(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | extra);
        }

        public static Size Measure(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // ---- shared tooltip in the current theme
        static ToolTip tip;

        public static void SetTip(Control c, string text)
        {
            if (tip == null)
            {
                tip = new ToolTip();
                tip.OwnerDraw = true;
                tip.InitialDelay = 450;
                tip.Draw += delegate(object s, DrawToolTipEventArgs e)
                {
                    Theme t = Theme.Current;
                    using (SolidBrush b = new SolidBrush(t.CardHover)) e.Graphics.FillRectangle(b, e.Bounds);
                    using (Pen p = new Pen(t.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                    TextRenderer.DrawText(e.Graphics, e.ToolTipText, Font(9f), e.Bounds, t.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                };
                tip.Popup += delegate(object s, PopupEventArgs e)
                {
                    Size sz = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl), Font(9f));
                    e.ToolTipSize = new Size(sz.Width + S(18), sz.Height + S(10));
                };
            }
            tip.SetToolTip(c, text);
        }
    }
}
