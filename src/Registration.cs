using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace PixView
{
    // Per-user (HKCU) registration: no admin rights needed.
    // Windows 10/11 does not let programs make themselves the default app (the UserChoice key is
    // hash-protected), so registering only offers PixView; the user confirms the choice on
    // the Settings > Default apps page that we open for them.
    static class Registration
    {
        const string ProgId = "PixView.Image";
        const string AppName = "PixView";
        const string AppKey = @"Software\PixView";
        const string CapKey = AppKey + @"\Capabilities";

        static readonly string[] Exts = Formats.Extensions.ToArray();

        // shown first in Settings, in this order; the rest of the supported list follows alphabetically
        public static readonly string[] PopularTypes =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".tif", ".tiff",
            ".jxl", ".ico", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2"
        };

        // popular types this machine can actually open (RAW needs the Raw Image Extension)
        public static string[] PopularSupported
        {
            get { return PopularTypes.Where(e => Exts.Contains(e, StringComparer.OrdinalIgnoreCase)).ToArray(); }
        }

        public static string[] OtherTypes
        {
            get { return Exts.Where(e => Array.IndexOf(PopularTypes, e) < 0).OrderBy(e => e, StringComparer.Ordinal).ToArray(); }
        }

        public static int SupportedCount
        {
            get { return Exts.Length; }
        }

        static string ExePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        // ------------------------------------------------------------------ command line

        public static int Register()
        {
            try
            {
                RegisterSilent();
                MessageBox.Show(L.T("reg_done"), AppName, MessageBoxButton.OK, MessageBoxImage.Information);
                OpenDefaultAppsPage();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.F("reg_failed", ex.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        public static int Unregister()
        {
            try
            {
                UnregisterSilent();
                MessageBox.Show(L.T("unreg_done"), AppName, MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.F("error", ex.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        // ------------------------------------------------------------------ used by the viewer

        public static bool IsRegistered
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                    return k != null;
            }
        }

        // Adds PixView to "Open with" and to Default apps; also refreshes the path if the exe moved.
        public static void RegisterSilent()
        {
            string exe = ExePath;
            string exeName = Path.GetFileName(exe);
            string cmd = "\"" + exe + "\" \"%1\"";
            var hk = Registry.CurrentUser;

            using (var k = hk.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", L.T("reg_type"));
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", "\"" + exe + "\",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
            }

            using (var k = hk.CreateSubKey(@"Software\Classes\Applications\" + exeName))
            {
                k.SetValue("FriendlyAppName", AppName);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
                using (var st = k.CreateSubKey("SupportedTypes"))
                    foreach (var e in Exts) st.SetValue(e, "");
            }

            using (var cap = hk.CreateSubKey(CapKey))
            {
                cap.SetValue("ApplicationName", AppName);
                cap.SetValue("ApplicationDescription", L.T("reg_desc"));
                cap.SetValue("ApplicationIcon", "\"" + exe + "\",0");
                using (var fa = cap.CreateSubKey("FileAssociations"))
                    foreach (var e in Exts) fa.SetValue(e, ProgId);
            }

            foreach (var e in Exts)
                using (var k = hk.CreateSubKey(@"Software\Classes\" + e + @"\OpenWithProgids"))
                    k.SetValue(ProgId, "");

            using (var k = hk.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, CapKey);

            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Removes everything RegisterSilent added; the viewer's own settings stay.
        public static void UnregisterSilent()
        {
            string exeName = Path.GetFileName(ExePath);
            var hk = Registry.CurrentUser;
            hk.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
            hk.DeleteSubKeyTree(@"Software\Classes\Applications\" + exeName, false);
            hk.DeleteSubKeyTree(CapKey, false);
            foreach (var e in Exts)
                using (var k = hk.OpenSubKey(@"Software\Classes\" + e + @"\OpenWithProgids", true))
                    if (k != null) k.DeleteValue(ProgId, false);
            using (var k = hk.OpenSubKey(@"Software\RegisteredApplications", true))
                if (k != null) k.DeleteValue(AppName, false);
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Settings > Default apps, scrolled to PixView
        public static void OpenDefaultAppsPage()
        {
            try { Process.Start("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName)); }
            catch { Process.Start("ms-settings:defaultapps"); }
        }

        // Does PixView currently open this type? Asks Windows instead of reading UserChoice keys:
        // since the UserChoiceLatest scheme those keys can keep a stale ProgId (seen for .heic).
        public static bool IsDefaultFor(string ext)
        {
            string id = Native.Assoc(ext, Native.ASSOCSTR_PROGID);
            if (id != null && (id == ProgId || id.Equals(@"Applications\" + Path.GetFileName(ExePath), StringComparison.OrdinalIgnoreCase)))
                return true;
            string exe = Native.Assoc(ext, Native.ASSOCSTR_EXECUTABLE);
            return exe != null && string.Equals(Path.GetFullPath(exe), ExePath, StringComparison.OrdinalIgnoreCase);
        }

        public static int DefaultCount()
        {
            return Exts.Count(IsDefaultFor);
        }
    }

    // Viewer preferences, HKCU\Software\PixView\Settings
    static class AppSettings
    {
        const string Key = @"Software\PixView\Settings";

        static object Get(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        static void Set(string name, object value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value == null) k.DeleteValue(name, false);
                    else k.SetValue(name, value);
                }
            }
            catch { }
        }

        // "" or null = follow Windows
        public static string Language
        {
            get { return Get("Language") as string; }
            set { Set("Language", string.IsNullOrEmpty(value) ? null : value); }
        }

        // the "make default?" question was already shown once
        public static bool DefaultAsked
        {
            get { var v = Get("DefaultAsked"); return v is int && (int)v != 0; }
            set { Set("DefaultAsked", value ? (object)1 : null); }
        }
    }
}
