using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>
    /// A list that scrolls with the wheel while the pointer is over it, although it never takes the keyboard focus
    /// (so that hovering or clicking it does not pull the focus out of the chat box).
    /// </summary>
    internal interface IWheelTarget
    {
        /// <summary>One wheel notch: positive scrolls up, negative down. Ends in the control's OnMouseWheel.</summary>
        void Wheel(int delta);
    }

    /// <summary>
    /// Delivers the mouse wheel to the IWheelTarget under the pointer. Windows sends the wheel to the focused window, which for the
    /// chat box or the board is not the list the user is pointing at. One filter per UI thread, shared by every main window on it.
    /// The wheel is only taken over when the list is in the window that has the focus, that window is enabled (a modal dialog
    /// disables the main window) and the list is visible and enabled; in every other case Windows handles the message as usual.
    /// </summary>
    internal static class WheelRouter
    {
        const int WM_MOUSEWHEEL = 0x020A;
        const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint p);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);

        sealed class Filter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                try { return Route(m); }
                catch (Exception) { return false; }   // never break the message loop over a wheel notch
            }
        }

        [ThreadStatic] static int users;
        [ThreadStatic] static Filter filter;

        /// <summary>
        /// Keeps the filter installed on the owner's thread while the owner has a window handle. Several owners may share a thread:
        /// the filter goes away with the last of them.
        /// </summary>
        public static void Attach(Control owner)
        {
            bool on = false;
            owner.HandleCreated += delegate
            {
                if (on) return;
                on = true;
                Acquire();
            };
            owner.HandleDestroyed += delegate
            {
                if (!on) return;
                on = false;
                Release();
            };
            if (owner.IsHandleCreated) { on = true; Acquire(); }
        }

        static void Acquire()
        {
            if (users++ > 0) return;
            filter = new Filter();
            Application.AddMessageFilter(filter);
        }

        static void Release()
        {
            if (users == 0 || --users > 0) return;
            Application.RemoveMessageFilter(filter);
            filter = null;
        }

        static bool Route(Message m)
        {
            long lp = m.LParam.ToInt64();
            NativePoint pt = new NativePoint();
            pt.X = (short)(lp & 0xFFFF);
            pt.Y = (short)((lp >> 16) & 0xFFFF);
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (delta == 0) return false;

            IntPtr under = WindowFromPoint(pt);
            IntPtr focus = GetFocus();
            if (under == IntPtr.Zero || focus == IntPtr.Zero || under == focus) return false;   // the focused window scrolls itself
            if (under == m.HWnd) return false;                                                 // already addressed to the list

            Control c = Control.FromChildHandle(under);   // also finds the owner of a native child window such as an edit box
            IWheelTarget target = c as IWheelTarget;
            if (target == null || c.IsDisposed || !c.IsHandleCreated || c.InvokeRequired) return false;
            if (!c.Visible || !c.Enabled) return false;

            Control top = c.TopLevelControl;
            if (top == null || !top.IsHandleCreated || !IsWindowEnabled(top.Handle)) return false;
            if (GetAncestor(focus, GA_ROOT) != top.Handle) return false;   // another window (a menu, another dialog) is the active one

            target.Wheel(delta);
            return true;
        }
    }
}
