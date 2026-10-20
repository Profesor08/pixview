using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PixView
{
    // Settings panel and the one-time "make default?" question, both shown as a card over the photo.
    partial class ViewerWindow
    {
        static readonly string[] LanguageNames =
        {
            "English", "Русский", "Українська", "Deutsch", "Français", "Español", "Italiano",
            "Português (Brasil)", "Polski", "Türkçe", "中文 (简体)", "日本語", "한국어"
        };

        Grid overlay;
        Border overlayCard;
        Action overlayRefresh;     // re-reads state when the window gets focus back (after Windows Settings)
        bool askedThisSession;

        readonly SolidColorBrush cardBrush = Frozen(Color.FromArgb(0xF4, 0x26, 0x26, 0x26));
        readonly SolidColorBrush mutedBrush = Frozen(Color.FromRgb(0xA8, 0xA8, 0xA8));
        readonly SolidColorBrush chipBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        readonly SolidColorBrush chipOnBrush = Frozen(Color.FromArgb(0x55, 0x3C, 0xB3, 0x71));
        readonly SolidColorBrush pillBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        bool OverlayOpen
        {
            get { return overlay != null && overlay.Visibility == Visibility.Visible; }
        }

        void BuildOverlay()
        {
            overlayCard = new Border
            {
                Background = cardBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(28, 22, 28, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 620
            };
            overlay = new Grid
            {
                Background = Frozen(Color.FromArgb(0x99, 0, 0, 0)),
                Visibility = Visibility.Collapsed
            };
            overlay.Children.Add(overlayCard);
            // a click on the dimmed area (not on the card) closes it
            overlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == overlay) { CloseOverlay(); e.Handled = true; } };
            overlay.MouseWheel += (s, e) => e.Handled = true;
            root.Children.Add(overlay);
            Activated += (s, e) => { if (OverlayOpen && overlayRefresh != null) overlayRefresh(); };
        }

        void ShowOverlay(UIElement content, double width, Action refresh)
        {
            if (slideTimer.IsEnabled) ToggleSlideshow();
            overlayCard.Width = width;
            overlayCard.Child = content;
            overlayRefresh = refresh;
            overlay.Visibility = Visibility.Visible;
        }

        void CloseOverlay()
        {
            overlay.Visibility = Visibility.Collapsed;
            overlayCard.Child = null;
            overlayRefresh = null;
            Keyboard.Focus(this);
        }

        // ------------------------------------------------------------------ settings

        void ShowSettings()
        {
            var panel = new StackPanel();
            Action fill = null;
            fill = () =>
            {
                panel.Children.Clear();
                panel.Children.Add(CardHeader(L.T("set_title")));

                // --- default program
                panel.Children.Add(SectionTitle(L.T("set_default")));
                int n = Registration.DefaultCount(), total = Registration.SupportedCount;
                panel.Children.Add(Paragraph(n == total ? L.F("st_all", total) : n == 0 ? L.T("st_none") : L.F("st_some", n, total), 14, Brushes.White));
                // every supported format: the popular ones first, then the rest alphabetically
                panel.Children.Add(FormatChips(Registration.PopularSupported, 12, new Thickness(0, 8, 0, 2)));
                panel.Children.Add(FormatChips(Registration.OtherTypes, 11, new Thickness(0, 6, 0, 4)));
                var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                buttons.Children.Add(PillButton(L.T("set_make_default"), true, MakeDefault));
                if (Registration.IsRegistered)
                    buttons.Children.Add(PillButton(L.T("set_remove"), false, () =>
                    {
                        try { Registration.UnregisterSilent(); ShowToast(L.T("removed")); }
                        catch (Exception ex) { ShowToast(L.F("error", ex.Message)); }
                        fill();
                    }));
                panel.Children.Add(buttons);
                panel.Children.Add(Paragraph(L.T("set_hint"), 12, mutedBrush));

                // --- language
                panel.Children.Add(SectionTitle(L.T("set_language")));
                var langs = new WrapPanel();
                string saved = AppSettings.Language ?? "";
                langs.Children.Add(PillButton(L.T("lang_auto"), saved == "", () => ChangeLanguage("")));
                for (int i = 0; i < L.Codes.Length; i++)
                {
                    string code = L.Codes[i];
                    langs.Children.Add(PillButton(LanguageNames[i], saved == code, () => ChangeLanguage(code)));
                }
                panel.Children.Add(langs);
            };
            fill();
            ShowOverlay(panel, 720, fill);
        }

        void MakeDefault()
        {
            try { Registration.RegisterSilent(); }
            catch (Exception ex) { ShowToast(L.F("reg_failed", ex.Message)); return; }
            // let the Settings app come to the front
            Topmost = false;
            Registration.OpenDefaultAppsPage();
            if (overlayRefresh != null) overlayRefresh();
        }

        // The whole UI is built once, so a new language takes effect through a quick restart.
        void ChangeLanguage(string code)
        {
            string before = L.Code;
            AppSettings.Language = code;
            string after = string.IsNullOrEmpty(code) ? EffectiveSystemLanguage() : code;
            if (after == before) { if (overlayRefresh != null) overlayRefresh(); return; }
            Hide();   // so the new instance's desktop snapshot does not contain this window
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    Process.Start(exe, curPath != null ? "\"" + curPath + "\"" : "");
                }
                catch { }
                Close();
            };
            t.Start();
        }

        static string EffectiveSystemLanguage()
        {
            string c = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Array.IndexOf(L.Codes, c) >= 0 ? c : "en";
        }

        // ------------------------------------------------------------------ first run

        // Once per user: offer to become the default viewer unless it already is.
        void MaybeAskDefault()
        {
            if (askedThisSession || AppSettings.DefaultAsked) return;
            askedThisSession = true;
            bool testRun = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PIXVIEW_SNAPSHOT"));
            if (testRun && Environment.GetEnvironmentVariable("PIXVIEW_FIRSTRUN") == null) return;
            bool force = Environment.GetEnvironmentVariable("PIXVIEW_FIRSTRUN") == "force";
            if (!force && (Registration.IsDefaultFor(".jpg") || Registration.IsDefaultFor(".png"))) { AppSettings.DefaultAsked = true; return; }

            // give the photo a moment on screen first
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (OverlayOpen) return;
                AppSettings.DefaultAsked = true;
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = L.T("first_title"),
                    Foreground = Brushes.White,
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                panel.Children.Add(Paragraph(L.T("first_text"), 14, mutedBrush));
                var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                buttons.Children.Add(PillButton(L.T("first_no"), false, CloseOverlay));
                buttons.Children.Add(PillButton(L.T("first_yes"), true, () => { CloseOverlay(); MakeDefault(); }));
                panel.Children.Add(buttons);
                ShowOverlay(panel, 520, null);
            };
            t.Start();
        }

        // ------------------------------------------------------------------ small building blocks

        UIElement CardHeader(string title)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var close = IconButton("", L.T("close"), CloseOverlay);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, -6, -12, 0);
            g.Children.Add(close);
            return g;
        }

        // one chip per extension, green with a tick when Windows opens it with PixView
        UIElement FormatChips(string[] exts, double size, Thickness margin)
        {
            var wrap = new WrapPanel { Margin = margin };
            foreach (var ext in exts)
            {
                bool ours = Registration.IsDefaultFor(ext);
                wrap.Children.Add(new Border
                {
                    Background = ours ? chipOnBrush : chipBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = size >= 12 ? new Thickness(8, 3, 8, 3) : new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 5, 5),
                    Child = new TextBlock { Text = ext + (ours ? " ✓" : ""), Foreground = ours ? Brushes.White : mutedBrush, FontSize = size }
                });
            }
            return wrap;
        }

        UIElement SectionTitle(string text)
        {
            return new TextBlock { Text = text, Foreground = mutedBrush, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
        }

        TextBlock Paragraph(string text, double size, Brush brush)
        {
            return new TextBlock { Text = text, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        }

        Button PillButton(string text, bool accent, Action click)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13 },
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 8),
                Focusable = false,
                Cursor = Cursors.Hand,
                Template = pillTemplate
            };
            Brush normal = accent ? (Brush)accentBrush : pillBrush;
            b.Background = normal;
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.Click += (s, e) => click();
            return b;
        }

        ControlTemplate pillTemplate = CreatePillTemplate();

        static ControlTemplate CreatePillTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;
            return t;
        }
    }
}
