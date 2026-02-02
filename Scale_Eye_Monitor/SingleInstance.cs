using System.Runtime.InteropServices;

namespace Scale_Eye_Monitor
{
    internal static class SingleInstance
    {
        // A unique message name for this app
        private const string ShowMeMessageName = "ScaleEyeMonitor.ShowMe";

        public static readonly int ShowMeMessage = NativeMethods.RegisterWindowMessage(ShowMeMessageName);

        public static void SignalFirstInstance()
        {
            // Broadcast message to all top-level windows in this session
            NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, ShowMeMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static class NativeMethods
        {
            public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int RegisterWindowMessage(string lpString);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
