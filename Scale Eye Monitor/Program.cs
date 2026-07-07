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
            _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running - ask it to show itself
                SingleInstance.SignalFirstInstance();
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();
                using var form = new MainForm();

                if (form.StartupCanceled)
                    return;

                Application.Run(form);
            }
            finally
            {
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
            }
        }
    }
}
