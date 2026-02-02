namespace Scale_Eye_Monitor
{
    internal static class Program
    {
        // Per-user/session instance (recommended for "Start with Windows" tray apps)
        private const string MutexName = @"Local\ScaleEyeMonitor_SingleInstance";
        private static Mutex? _mutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out createdNew);

            if (!createdNew)
            {
                // Another instance is already running — ask it to show itself
                SingleInstance.SignalFirstInstance();
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
            finally
            {
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
            }
        }
    }
}
