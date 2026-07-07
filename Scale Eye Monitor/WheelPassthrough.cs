using System.Runtime.InteropServices;

namespace Scale_Eye_Monitor
{
    /// <summary>
    /// Forwards mouse-wheel input from focused controls (TextBox/NumericUpDown/etc.)
    /// to a scrollable container (typically a Panel with AutoScroll=true), so the
    /// dialog scrolls instead of changing NumericUpDown values.
    /// </summary>
    internal static class WheelPassthrough
    {
        internal const int WmMouseWheel = 0x020A;
        private const int MK_SHIFT = 0x0004;
        private const int MK_CONTROL = 0x0008;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        internal static void Enable(Control input, Control scrollTarget)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(scrollTarget);

            PassWheelTo(input, scrollTarget);

            // NumericUpDown has inner controls that can receive the wheel; forward from them too.
            if (input is NumericUpDown nud)
            {
                foreach (Control child in nud.Controls)
                    PassWheelTo(child, scrollTarget);
            }
        }

        private static void PassWheelTo(Control source, Control scrollTarget)
        {
            source.MouseWheel += (_, e) =>
            {
                if (e is HandledMouseEventArgs h) h.Handled = true;

                if (scrollTarget.IsDisposed) return;
                if (!scrollTarget.IsHandleCreated) return;

                var screenPt = Control.MousePosition;
                int wParam = MakeWParamForWheel(e.Delta);
                int lParam = MakeLParamFromScreenPoint(screenPt);

                ForwardRawWheel(scrollTarget, (IntPtr)wParam, (IntPtr)lParam);
            };
        }

        internal static bool ForwardRawWheel(Control scrollTarget, IntPtr wParam, IntPtr lParam)
        {
            ArgumentNullException.ThrowIfNull(scrollTarget);

            if (scrollTarget.IsDisposed) return false;
            if (!scrollTarget.IsHandleCreated) return false;

            SendMessage(scrollTarget.Handle, WmMouseWheel, wParam, lParam);
            return true;
        }

        private static int MakeWParamForWheel(int delta)
        {
            int keyState = 0;
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) keyState |= MK_SHIFT;
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) keyState |= MK_CONTROL;

            // low word = keyState, high word = delta (signed)
            return (delta << 16) | (keyState & 0xFFFF);
        }

        private static int MakeLParamFromScreenPoint(Point screenPt)
        {
            // WM_MOUSEWHEEL expects screen coordinates in lParam
            return (screenPt.X & 0xFFFF) | ((screenPt.Y & 0xFFFF) << 16);
        }
    }
}
