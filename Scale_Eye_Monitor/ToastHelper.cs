using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Scale_Eye_Monitor
{
    internal static class ToastHelper
    {
        /*
     * ToastHelper
     * -----------
     * Minimal helper for Windows toast notifications from a classic Win32/WinForms process.
     *
     * What it does:
     *   - Sets a process AppUserModelID (AUMID) for the current process.
     *   - Ensures a Start Menu shortcut exists and is stamped with the same AUMID.
     *     (Required for reliable toast delivery in many desktop contexts.)
     *   - Sends a ToastGeneric notification by building raw toast XML and using WinRT APIs:
     *       Windows.UI.Notifications / Windows.Data.Xml.Dom
     *
     * Usage:
     *   - MainForm calls EnsureShortcut(debugLogging) during startup.
     *   - MainForm.Ui calls ShowToast(...) for headline transitions (preferred),
     *     with balloon notifications as fallback if toast cannot be shown.
     *
     * Design notes:
     *   - Kept isolated because it contains COM interop + P/Invoke + WinRT toast APIs.
     *   - EnsureShortcut overwrites the .lnk target/AUMID so upgrades/moves keep toasts working.
     *   - COM objects are explicitly released to avoid long-lived RCW retention in a long-running app.
     *
     * Important:
     *   - Aumid must be stable and unique to this app. If you change it, the shortcut must match.
     */

        // =====================================================================
        //  App identity
        // =====================================================================
        private const string Aumid = "YourCompany.ScaleEyeMonitor"; // choose a unique string

        // =====================================================================
        //  Public API
        // =====================================================================

        public static void EnsureShortcut(bool debugLogging = false)
        {
            // Set process AUMID (recommended)
            try
            {
                int hr = SetCurrentProcessExplicitAppUserModelID(Aumid);
                if (debugLogging && hr < 0)
                    Debug.WriteLine($"[ToastHelper] SetCurrentProcessExplicitAppUserModelID failed: 0x{hr:X8}");
            }
            catch { }

            // Create Start Menu shortcut if missing
            string shortcutDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

            Directory.CreateDirectory(shortcutDir);

            string exePath = System.Windows.Forms.Application.ExecutablePath;
            string shortcutPath = Path.Combine(shortcutDir, "Scale Eye Monitor.lnk");

            // Always write/overwrite the shortcut so updates/moves keep the AUMID + target correct.
            IShellLinkW? link = null;
            IPropertyStore? propStore = null;
            IPersistFile? pf = null;

            try
            {
                // Create basic .lnk with IShellLinkW
                link = (IShellLinkW)new CShellLink();
                link.SetPath(exePath);
                link.SetArguments(""); // optional
                link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
                link.SetIconLocation(exePath, 0);

                // Write AppUserModelID to the shortcut using IPropertyStore
                propStore = (IPropertyStore)link;
                var pkey = PKEY_AppUserModel_ID;

                PropVariant pv = new PropVariant(Aumid);
                try
                {
                    propStore.SetValue(ref pkey, ref pv);
                    propStore.Commit();
                }
                finally
                {
                    pv.Dispose();
                }

                // Save .lnk (overwrite)
                pf = (IPersistFile)link;
                pf.Save(shortcutPath, true);
            }
            catch (Exception ex)
            {
                if (debugLogging)
                    Debug.WriteLine($"[ToastHelper] EnsureShortcut failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (pf is not null) Marshal.FinalReleaseComObject(pf);
                if (propStore is not null) Marshal.FinalReleaseComObject(propStore);
                if (link is not null) Marshal.FinalReleaseComObject(link);
            }
        }

        public static void ShowToast(string title, string text, string headerPngFullPath)
        {
            // Build ToastGeneric XML with an app-logo override image (header icon)
            // NOTE: Use absolute file URIs for images.
            string headerUri = new Uri(Path.GetFullPath(headerPngFullPath)).AbsoluteUri;

            string xml =
    $@"<toast activationType='foreground'>
  <visual>
    <binding template='ToastGeneric'>
      <image placement='appLogoOverride' hint-crop='circle' src='{XmlEscape(headerUri)}' />
      <text>{XmlEscape(title)}</text>
      <text>{XmlEscape(text)}</text>
      <!-- optional attribution line:
      <text placement='attribution'>ScaleEyeMonitor</text>
      -->
    </binding>
  </visual>
</toast>";

            // Load XML and raise toast
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc);

            // Important: notifier must be created with your AUMID
            var notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            notifier.Show(toast);
        }

        public static void ShowWarningToast(string title, string text)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ScaleEyeMonitor");
                Directory.CreateDirectory(dir);

                string pngPath = Path.Combine(dir, "toast_warning.png");
                if (!File.Exists(pngPath))
                {
                    using var bmp = SystemIcons.Warning.ToBitmap();
                    bmp.Save(pngPath, ImageFormat.Png);
                }

                ShowToast(title, text, pngPath);
            }
            catch { }
        }

        public static void ShowInfoToast(string title, string text)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ScaleEyeMonitor");
                Directory.CreateDirectory(dir);

                string pngPath = Path.Combine(dir, "toast_info.png");
                if (!File.Exists(pngPath))
                {
                    using var bmp = SystemIcons.Information.ToBitmap();
                    bmp.Save(pngPath, ImageFormat.Png);
                }

                ShowToast(title, text, pngPath);
            }
            catch { }
        }

        // =====================================================================
        //  Helpers
        // =====================================================================

        private static string XmlEscape(string s) =>
            s?.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;")
            ?? string.Empty;

        // =====================================================================
        //  P/Invoke & COM interop
        // =====================================================================

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PropVariant pv);
            void SetValue(ref PROPERTYKEY key, [In] ref PropVariant pv);
            void Commit();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // PKEY_AppUserModel_ID {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        // Minimal PROPVARIANT for VT_LPWSTR.
        // Use PropVariantClear to free correctly on both x86/x64.
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant : IDisposable
        {
            private ushort vt;
            private ushort wReserved1;
            private ushort wReserved2;
            private ushort wReserved3;
            private IntPtr p;
            private IntPtr p2;

            public PropVariant(string? value)
            {
                wReserved1 = wReserved2 = wReserved3 = 0;
                p2 = IntPtr.Zero;

                if (value is null)
                {
                    vt = (ushort)VarEnum.VT_EMPTY;
                    p = IntPtr.Zero;
                    return;
                }

                vt = (ushort)VarEnum.VT_LPWSTR;
                p = Marshal.StringToCoTaskMemUni(value);
            }

            public void Dispose()
            {
                PropVariantClear(ref this);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }
}
