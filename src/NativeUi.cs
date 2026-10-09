using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ChessGame
{
    /// <summary>Small Win32 touches that make the window feel native in the dark theme.</summary>
    static class NativeUi
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Dark (or light) title bar; on Windows 11 also paints the caption in the window colour.</summary>
        public static void StyleTitleBar(IntPtr hwnd, bool dark, Color caption, Color captionText)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int on = dark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / 11); 19 = the same flag in older 10 builds
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int cap = ColorRef(caption);
                DwmSetWindowAttribute(hwnd, 35, ref cap, 4);          // DWMWA_CAPTION_COLOR (Windows 11)
                int txt = ColorRef(captionText);
                DwmSetWindowAttribute(hwnd, 36, ref txt, 4);          // DWMWA_TEXT_COLOR (Windows 11)
                int border = ColorRef(caption);
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);       // DWMWA_BORDER_COLOR (Windows 11)
                // make an already visible window redraw its frame with the new colours
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0020);   // NOMOVE | NOSIZE | NOZORDER | FRAMECHANGED
            }
            catch (Exception) { /* older Windows: keep the default title bar */ }
        }

        static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }
}
