using System;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// Lets a control follow Theme.Changed (and, for the board, BoardSchemes.Changed) only while it has a window handle:
    /// it subscribes when the handle is created and unsubscribes when it is destroyed, so that a static event never keeps a
    /// closed window alive. The callback is not called once the control is disposed.
    /// </summary>
    internal static class ThemeWatch
    {
        /// <summary>Calls onChanged whenever the theme changes. Call it once, from the constructor.</summary>
        public static void Attach(Control c, Action onChanged)
        {
            Watch(c, onChanged, false);
        }

        /// <summary>The same for a control that also depends on the colours of the board squares.</summary>
        public static void AttachWithBoardScheme(Control c, Action onChanged)
        {
            Watch(c, onChanged, true);
        }

        static void Watch(Control c, Action onChanged, bool boardScheme)
        {
            Action handler = delegate { if (!c.IsDisposed) onChanged(); };
            bool on = false;
            c.HandleCreated += delegate
            {
                if (on) return;
                on = true;
                Theme.Changed += handler;
                if (boardScheme) BoardSchemes.Changed += handler;
            };
            c.HandleDestroyed += delegate
            {
                if (!on) return;
                on = false;
                Theme.Changed -= handler;
                if (boardScheme) BoardSchemes.Changed -= handler;
            };
        }
    }
}
