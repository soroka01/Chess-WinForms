using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>Measuring and breaking wrapped text for the owner-drawn controls that lay out their own text (chat bubbles, requests).</summary>
    internal static class TextWrap
    {
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.Left | TextFormatFlags.Top;

        static Bitmap measureBmp;
        static Graphics measureG;

        /// <summary>Measured on a bitmap DC like the one painting uses: the screen DC wraps a pixel or two differently.</summary>
        public static Size MeasureText(string text, Font font, int width)
        {
            if (measureG == null)
            {
                measureBmp = new Bitmap(1, 1);
                measureG = Graphics.FromImage(measureBmp);
            }
            return TextRenderer.MeasureText(measureG, text, font, new Size(width, 0), Wrap);
        }

        /// <summary>
        /// GDI does not break a single word wider than the line (it cuts it with "..."), so do it here: such a word gets hard line
        /// breaks. Words shorter than minLength are never measured or broken.
        /// </summary>
        public static string BreakLongWords(string text, Font font, int width, int minLength)
        {
            StringBuilder sb = null;
            int pos = 0;   // text up to pos is already copied to sb
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                if (i - start < minLength || MeasureText(text.Substring(start, i - start), font, 10000).Width <= width) continue;

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
    }
}
