using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace PixView
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // --lang=xx forces the UI language; everything else is the file to open
            string lang = null;
            var rest = args.Where(a =>
            {
                string s = a.TrimStart('-', '/');
                if (!s.StartsWith("lang=", StringComparison.OrdinalIgnoreCase)) return true;
                lang = s.Substring(5);
                return false;
            }).ToArray();
            args = rest;
            L.Init(lang);

            if (args.Length > 0)
            {
                string a = args[0].ToLowerInvariant().TrimStart('-', '/');
                if (a == "register") return Registration.Register();
                if (a == "unregister") return Registration.Unregister();
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, "PixView", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            // before the Open dialog or our window can appear on screen
            Backdrop.Capture();

            string path = args.Length > 0 ? string.Join(" ", args).Trim('"') : null;
            if (path == null || (!File.Exists(path) && !Directory.Exists(path)))
            {
                path = AskForFile(null);
                if (path == null) return 0;
            }

            app.Run(new ViewerWindow(path));
            return 0;
        }

        public static string AskForFile(Window owner)
        {
            var exts = string.Join(";", Formats.Extensions.Select(x => "*" + x));
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("dlg_title"),
                Filter = L.T("dlg_images") + "|" + exts + "|" + L.T("dlg_all") + "|*.*"
            };
            bool? ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            return ok == true ? dlg.FileName : null;
        }
    }
}
