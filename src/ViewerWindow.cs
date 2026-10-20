using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PixView
{
    partial class ViewerWindow : Window
    {
        const double FitPad = 16;
        const double ThumbW = 84, ThumbH = 60;
        const int ThumbBox = 168;
        const int ThumbCacheMax = 800;
        const double MaxScale = 32;
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // --- files and caches ---
        readonly string startPath;
        string[] files = new string[0];
        volatile int curIndex;
        string curPath;
        LoadedImage cur;
        string placeholderPath;
        int navDir = 1;
        int listGeneration;

        readonly Dictionary<string, LoadedImage> cache = new Dictionary<string, LoadedImage>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile HashSet<string> wantedFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, ThumbInfo> thumbs = new Dictionary<string, ThumbInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> thumbOrder = new Queue<string>();
        readonly HashSet<string> thumbPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile HashSet<string> wantedThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly StaWorker loader = new StaWorker("image-loader");
        readonly StaWorker thumbLoader = new StaWorker("thumb-loader");

        // --- view state: target (t*) and current/animated (c*) ---
        double dpi = 1;
        double imgW, imgH;                 // unrotated image size in DIPs (1 image px = 1 device px at scale 1)
        double tS = 1, tX, tY, tR, tO = 1;
        double cS = 1, cX, cY, cR, cO = 1;
        bool fitMode = true;
        bool animating;
        double lastFrameTime;
        List<double> frameLog;  // test hook only
        readonly List<double> scaleLog = new List<double>();
        readonly List<int> levelLog = new List<int>();
        readonly System.Text.StringBuilder anchorLog = new System.Text.StringBuilder();
        double lastUx = double.NaN, lastUy = double.NaN;
        bool zoomAnchored;      // keep the point under zoomAnchor fixed while the scale animates
        Point zoomAnchor;
        List<BitmapSource> curMips;
        int mipLevel;
        readonly Stopwatch clock = Stopwatch.StartNew();

        // --- input ---
        bool pressed, dragging;
        Point pressPoint;
        double pressX, pressY;

        // --- UI ---
        Grid root;
        Border dimLayer;
        Canvas stage;
        Image img;
        readonly MatrixTransform imgTransform = new MatrixTransform();
        TextBlock message, titleText, zoomText, toastText, infoText, playGlyph;
        Grid topBar;
        StackPanel bottomBar, filmstrip, stripRow;
        ButtonBase stripPrev, stripNext, navPrev, navNext;
        StackPanel pageGroup;
        ButtonBase maxButton;     // maximize / restore, glyph follows the window mode
        TextBlock pageText;
        int stripCenter;      // file index shown in the middle of the filmstrip
        Border infoPanel, toast;
        readonly List<Border> slots = new List<Border>();
        bool barsVisible = true, infoVisible, solidBackground;
        ControlTemplate buttonTemplate;

        readonly SolidColorBrush dimBrush = Frozen(Color.FromArgb(0xD8, 0, 0, 0));
        readonly SolidColorBrush solidBrush = Frozen(Color.FromRgb(0, 0, 0));
        readonly SolidColorBrush hoverBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        readonly SolidColorBrush accentBrush = Frozen(Color.FromRgb(0x4C, 0xA3, 0xFF));
        readonly SolidColorBrush slotBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        readonly DispatcherTimer gifTimer = new DispatcherTimer(DispatcherPriority.Render);
        readonly DispatcherTimer slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        List<BitmapSource> gifFrames;
        List<int> gifDelays;
        int gifFrame;

        public ViewerWindow(string startPath)
        {
            this.startPath = startPath;
            Title = "PixView";
            AllowsTransparency = false;   // see Backdrop: an opaque window renders much faster
            Background = Backdrop.Image != null ? (Brush)new ImageBrush(Backdrop.Image) { Stretch = Stretch.Fill } : Brushes.Black;
            SetupChrome();                // frameless look, but a real captioned window (see there)
            WindowStartupLocation = WindowStartupLocation.Manual;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // Cover the monitor that holds the mouse cursor.
            var bounds = Backdrop.Bounds;
            double sys = SystemDpiScale();
            Left = bounds.Left / sys;
            Top = bounds.Top / sys;
            Width = bounds.Width / sys;
            Height = bounds.Height / sys;

            BuildUi();

            gifTimer.Tick += OnGifTick;
            slideTimer.Tick += (s, e) => ShowIndex(curIndex < files.Length - 1 ? curIndex + 1 : 0, 1);
            Loaded += OnLoaded;
            PreviewKeyDown += OnKeyDown;
            PreviewMouseDown += OnWindowMouseDown;
            Closed += (s, e) => { gifTimer.Stop(); slideTimer.Stop(); };
        }

        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static double SystemDpiScale()
        {
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                return g.DpiX / 96.0;
        }

        // ================================================================ UI

        void BuildUi()
        {
            buttonTemplate = CreateButtonTemplate();

            root = new Grid();
            dimLayer = new Border { Background = dimBrush, IsHitTestVisible = false };
            root.Children.Add(dimLayer);
            Content = root;

            stage = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            img = new Image { Stretch = Stretch.Fill, RenderTransform = imgTransform, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            stage.Children.Add(img);
            stage.MouseWheel += OnWheel;
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            stage.SizeChanged += (s, e) => OnViewportChanged();
            root.Children.Add(stage);

            message = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                FontSize = 16,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 700,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(message);

            // --- top bar: file title + actions ---
            topBar = new Grid { VerticalAlignment = VerticalAlignment.Top, Height = 52, Background = VerticalGradient(0xB0, 0x00) };
            titleText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 395, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            topBar.Children.Add(titleText);
            var topButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            topButtons.Children.Add(IconButton("", L.T("open_file"), OpenDialog));
            topButtons.Children.Add(IconButton("", L.T("show_folder"), ShowInFolder));
            topButtons.Children.Add(IconButton("", L.T("copy_image"), CopyImage));
            topButtons.Children.Add(IconButton("\uE713", L.T("settings"), ShowSettings));
            topButtons.Children.Add(IconButton("", L.T("info"), ToggleInfo));
            var windowButtons = new StackPanel { Orientation = Orientation.Horizontal };
            windowButtons.Children.Add(IconButton("\uE921", L.T("minimize"), () => WindowState = WindowState.Minimized));
            maxButton = IconButton("\uE923", L.T("restore"), ToggleWindowed);
            windowButtons.Children.Add(maxButton);
            windowButtons.Children.Add(IconButton("", L.T("close"), Close));
            // window controls set apart from the viewer actions by a gap
            topButtons.Children.Add(new Border { Width = 14 });
            topButtons.Children.Add(windowButtons);
            topBar.Children.Add(topButtons);
            root.Children.Add(topBar);
            HookWindowDrag();

            // --- bottom bar: filmstrip + toolbar ---
            bottomBar = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Background = VerticalGradient(0x00, 0xC8) };
            // transparent background: the empty slots at the ends must still catch a drag
            filmstrip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            stripPrev = IconButton("", L.T("strip_left"), () => ScrollStrip(-Math.Max(1, slots.Count / 2)), 150);
            stripNext = IconButton("", L.T("strip_right"), () => ScrollStrip(Math.Max(1, slots.Count / 2)), 150);
            stripPrev.Height = stripNext.Height = ThumbH;
            stripPrev.Margin = new Thickness(0, 0, 8, 0);
            stripNext.Margin = new Thickness(8, 0, 0, 0);
            stripRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Height = ThumbH + 20,
                Margin = new Thickness(0, 12, 0, 0),
                Background = Brushes.Transparent
            };
            stripRow.Children.Add(stripPrev);
            // the clip keeps thumbnails that slide during a drag off the arrows
            stripRow.Children.Add(new Border { ClipToBounds = true, Child = filmstrip, Background = Brushes.Transparent });
            stripRow.Children.Add(stripNext);
            stripRow.MouseWheel += OnStripWheel;
            HookStripDrag();
            bottomBar.Children.Add(stripRow);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 12) };
            tools.Children.Add(IconButton("", L.T("zoom_out"), () => ZoomBy(1 / 1.25, ViewCenter())));
            zoomText = new TextBlock { Foreground = Brushes.White, FontSize = 13, Width = 54, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(zoomText);
            tools.Children.Add(IconButton("", L.T("zoom_in"), () => ZoomBy(1.25, ViewCenter())));
            tools.Children.Add(IconButton("", L.T("fit"), Fit));
            tools.Children.Add(TextButton("1:1", L.T("actual"), () => ZoomTo(1, ViewCenter())));
            tools.Children.Add(Separator());
            tools.Children.Add(IconButton("", L.T("prev"), () => Navigate(-1), 220));
            var play = IconButton("", L.T("slideshow"), ToggleSlideshow);
            playGlyph = (TextBlock)play.Content;
            tools.Children.Add(play);
            tools.Children.Add(IconButton("", L.T("next"), () => Navigate(1), 220));
            tools.Children.Add(Separator());
            var rotLeft = IconButton("", L.T("rot_left"), () => Rotate(-90));
            ((TextBlock)rotLeft.Content).RenderTransformOrigin = new Point(0.5, 0.5);
            ((TextBlock)rotLeft.Content).RenderTransform = new ScaleTransform(-1, 1);
            tools.Children.Add(rotLeft);
            tools.Children.Add(IconButton("", L.T("rot_right"), () => Rotate(90)));
            // pages of a multi-page TIFF; shown only for such files
            pageGroup = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
            pageGroup.Children.Add(Separator());
            pageGroup.Children.Add(IconButton("", L.T("page_prev"), () => ShowPage(-1), 250));
            pageText = new TextBlock { Foreground = Brushes.White, FontSize = 13, MinWidth = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pageGroup.Children.Add(pageText);
            pageGroup.Children.Add(IconButton("", L.T("page_next"), () => ShowPage(1), 250));
            tools.Children.Add(pageGroup);
            bottomBar.Children.Add(tools);
            bottomBar.SizeChanged += (s, e) => { if (fitMode) { ComputeFit(); StartAnim(); } };
            root.Children.Add(bottomBar);

            // --- big side arrows over the photo ---
            navPrev = SideButton("", L.T("prev"), HorizontalAlignment.Left, () => Navigate(-1));
            navNext = SideButton("", L.T("next"), HorizontalAlignment.Right, () => Navigate(1));
            root.Children.Add(navPrev);
            root.Children.Add(navNext);

            // The photo moving underneath would otherwise make WPF redraw these every frame
            // (14 high-quality thumbnails cost ~25 ms); cached, they are only re-rendered when
            // they change and are just composited while zooming or panning.
            foreach (UIElement e in new UIElement[] { topBar, bottomBar, navPrev, navNext })
                e.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

            // --- info panel ---
            infoText = new TextBlock { Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            infoPanel = new Border
            {
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 60, 16, 0),
                Padding = new Thickness(16, 12, 16, 14),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x22, 0x22, 0x22)),
                Child = infoText,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(infoPanel);

            // --- toast (zoom level etc.) ---
            toastText = new TextBlock { Foreground = Brushes.White, FontSize = 20 };
            toast = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0,
                Child = toastText
            };
            BuildOverlay();             // settings / first-run card, above everything but the toast
            root.Children.Add(toast);
        }

        static Brush VerticalGradient(byte topAlpha, byte bottomAlpha)
        {
            var b = new LinearGradientBrush(Color.FromArgb(topAlpha, 0, 0, 0), Color.FromArgb(bottomAlpha, 0, 0, 0), 90);
            b.Freeze();
            return b;
        }

        static ControlTemplate CreateButtonTemplate()
        {
            var t = new ControlTemplate(typeof(ButtonBase));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;
            return t;
        }

        // repeatMs > 0: holding the mouse button down keeps firing the action
        ButtonBase IconButton(string glyph, string tip, Action click, int repeatMs = 0)
        {
            var tb = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16, Foreground = Brushes.White };
            return MakeButton(tb, tip, click, repeatMs);
        }

        ButtonBase TextButton(string text, string tip, Action click)
        {
            var tb = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            return MakeButton(tb, tip, click, 0);
        }

        ButtonBase MakeButton(UIElement content, string tip, Action click, int repeatMs)
        {
            ButtonBase b = repeatMs > 0 ? (ButtonBase)new RepeatButton { Delay = 400, Interval = repeatMs } : new Button();
            b.Content = content;
            b.ToolTip = tip;
            b.Width = 40;
            b.Height = 36;
            b.Margin = new Thickness(2, 0, 2, 0);
            b.Focusable = false;
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.Template = buttonTemplate;
            // WPF swallows the second click of a double-click; fast clicking must count every click
            b.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount > 1 && b.IsEnabled) { click(); e.Handled = true; }
            };
            b.MouseEnter += (s, e) => b.Background = hoverBrush;
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.Click += (s, e) => click();
            return b;
        }

        ButtonBase SideButton(string glyph, string tip, HorizontalAlignment side, Action click)
        {
            var b = IconButton(glyph, tip, click, 220);
            ((TextBlock)b.Content).FontSize = 30;
            ((TextBlock)b.Content).Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9 };
            b.Width = 64;
            b.Height = 120;
            b.HorizontalAlignment = side;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(12, 0, 12, 0);
            b.Opacity = 0.6;
            b.MouseEnter += (s, e) => b.Opacity = 1;
            b.MouseLeave += (s, e) => b.Opacity = 0.6;
            return b;
        }

        void UpdateNavButtons()
        {
            navPrev.Visibility = barsVisible && curIndex > 0 && files.Length > 1 ? Visibility.Visible : Visibility.Hidden;
            navNext.Visibility = barsVisible && curIndex < files.Length - 1 ? Visibility.Visible : Visibility.Hidden;
        }

        static UIElement Separator()
        {
            return new Border { Width = 1, Height = 22, Margin = new Thickness(10, 0, 10, 0), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
        }

        // ================================================================ lifecycle

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            var ps = PresentationSource.FromVisual(this);
            if (ps != null && ps.CompositionTarget != null) dpi = ps.CompositionTarget.TransformToDevice.M11;
            OpenPath(startPath);
            root.Opacity = 0;
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            BringToFront();
            ContentRendered += (s, a) => BringToFront();
            RunTestHook();
        }

        // Take focus even if the launcher or the just-closed Open dialog left another app in front.
        void BringToFront()
        {
            Topmost = true;   // brief topmost toggle brings the window above the launcher
            Topmost = false;
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Activate();
            Focus();
            Keyboard.Focus(this);
        }

        // Debug aid: PIXVIEW_KEYS="Right,I" replays keys, PIXVIEW_SNAPSHOT=file.png renders the window and exits.
        void RunTestHook()
        {
            string snap = Environment.GetEnvironmentVariable("PIXVIEW_SNAPSHOT");
            if (string.IsNullOrEmpty(snap)) return;
            var keys = new Queue<string>((Environment.GetEnvironmentVariable("PIXVIEW_KEYS") ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) =>
            {
                if (keys.Count > 0)
                {
                    string token = keys.Dequeue().Trim();
                    if (token.StartsWith("Strip", StringComparison.OrdinalIgnoreCase))
                    {
                        ScrollStrip(int.Parse(token.Substring(5)));
                        return;
                    }
                    if (token.Equals("OpenDialog", StringComparison.OrdinalIgnoreCase))
                    {
                        OpenDialog();
                        return;
                    }
                    if (token.Equals("Nop", StringComparison.OrdinalIgnoreCase)) return;
                    if (token.Equals("Windowed", StringComparison.OrdinalIgnoreCase)) { ToggleWindowed(); return; }
                    if (token.Equals("Settings", StringComparison.OrdinalIgnoreCase)) { ShowSettings(); return; }
                    if (token.StartsWith("Lang=", StringComparison.OrdinalIgnoreCase)) { ChangeLanguage(token.Substring(5)); return; }
                    if (token.StartsWith("Page", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowPage(int.Parse(token.Substring(4)));
                        return;
                    }
                    if (token.Equals("Stay", StringComparison.OrdinalIgnoreCase))
                    {
                        t.Stop();   // leave the window open for external checks
                        return;
                    }
                    if (token.StartsWith("Drag", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = token.Substring(4).Split('/');
                        double bx = tX, by = tY;
                        pressed = true; dragging = true; pressX = tX; pressY = tY;
                        pressPoint = new Point(0, 0);
                        tX = pressX + double.Parse(parts[0]); tY = pressY + double.Parse(parts[1]);
                        KeepOnScreen(); fitMode = false; cX = tX; cY = tY; ApplyTransform();
                        pressed = dragging = false;
                        anchorLog.AppendFormat("moved={0:0}/{1:0} ", tX - bx, tY - by);
                        return;
                    }
                    if (token.StartsWith("Wheel", StringComparison.OrdinalIgnoreCase))
                    {
                        frameLog = frameLog ?? new List<double>();
                        var c = ViewCenter();
                        var p = new Point(c.X + 500, c.Y + double.Parse(Environment.GetEnvironmentVariable("PIXVIEW_ZOOM_DY") ?? "150"));
                        // image point under the cursor must stay the same: report drift in screen px
                        double ux = (p.X - cX) / cS, uy = (p.Y - cY) / cS;
                        if (anchorLog.Length > 0 || !double.IsNaN(lastUx))
                            anchorLog.AppendFormat("drift={0:0.0}px ", Math.Sqrt(Math.Pow((ux - lastUx) * cS, 2) + Math.Pow((uy - lastUy) * cS, 2)));
                        anchorLog.AppendFormat("[{0} tS={1:0.000} cS={2:0.000} fit={3:0.000} src={4}] ", token, tS, cS, FitScale(), img.Source == null ? "null" : ((BitmapSource)img.Source).PixelWidth.ToString());
                        ZoomBy(Math.Pow(1.2, int.Parse(token.Substring(5))), p);
                        lastUx = ux;
                        lastUy = uy;
                        return;
                    }
                    var k = (Key)Enum.Parse(typeof(Key), token, true);
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, k) { RoutedEvent = PreviewKeyDownEvent };
                    OnKeyDown(this, args);
                    return;
                }
                t.Stop();
                var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi), (int)(root.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(snap)) enc.Save(fs);
                File.WriteAllText(snap + ".status", cur == null ? "not loaded" : cur.Error != null ? "ERROR: " + cur.Error.Replace("\n", " ") : "OK " + cur.PixelWidth + "x" + cur.PixelHeight + " frames=" + (cur.Frames == null ? 1 : cur.Frames.Count) + " files=" + files.Length + " index=" + (curIndex + 1) + " zoom=" + Math.Round(tS / FitScale(), 3) + " strip=" + (stripCenter + 1));
                if (frameLog != null && frameLog.Count > 0)
                    File.WriteAllText(snap + ".txt", "frames=" + frameLog.Count + " avg_ms=" + (frameLog.Average() * 1000).ToString("0.0")
                        + " max_ms=" + (frameLog.Max() * 1000).ToString("0.0") + "\n" + anchorLog + "\n" + string.Join(" ", frameLog.Select((x, i) => (x * 1000).ToString("0") + "@" + scaleLog[i].ToString("0.00") + "L" + levelLog[i])));
                Close();
            };
            t.Start();
        }

        void OpenPath(string path)
        {
            StopGif();
            cache.Clear();
            pending.Clear();
            cur = null;
            int gen = ++listGeneration;
            string folder;
            string target = null;

            if (Directory.Exists(path))
            {
                folder = path;
                files = new string[0];
            }
            else
            {
                folder = Path.GetDirectoryName(Path.GetFullPath(path));
                target = Path.GetFullPath(path);
                files = new[] { target };
                ShowIndex(0, 1);
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var list = ListFolder(folder, target);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != listGeneration) return;
                    files = list;
                    if (files.Length == 0)
                    {
                        curPath = null;
                        img.Source = null;
                        ShowMessage(L.F("no_images", folder));
                        UpdateTitle();
                        UpdateFilmstrip();
                        return;
                    }
                    int i = target == null ? 0 : Array.FindIndex(files, f => string.Equals(f, curPath, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) i = 0;
                    if (curPath == null || target == null) ShowIndex(i, 1);
                    else
                    {
                        curIndex = i;
                        UpdateWanted();
                        Prefetch();
                        UpdateTitle();
                        UpdateFilmstrip();
                    }
                }));
            });
        }

        static string[] ListFolder(string folder, string mustInclude)
        {
            List<string> list;
            try
            {
                list = Directory.EnumerateFiles(folder).Where(Formats.IsSupported).ToList();
            }
            catch
            {
                list = new List<string>();
            }
            if (mustInclude != null && !list.Any(f => string.Equals(f, mustInclude, StringComparison.OrdinalIgnoreCase)))
                list.Add(mustInclude);
            list.Sort((a, b) => Native.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            return list.ToArray();
        }

        // ================================================================ navigation & loading

        void Navigate(int delta)
        {
            if (files.Length == 0) return;
            int i = Math.Max(0, Math.Min(files.Length - 1, curIndex + delta));
            if (i != curIndex) ShowIndex(i, Math.Sign(delta));
        }

        void ShowIndex(int i, int dir)
        {
            if (files.Length == 0) return;
            i = Math.Max(0, Math.Min(files.Length - 1, i));
            navDir = dir == 0 ? 1 : dir;
            curIndex = i;
            curPath = files[i];
            cur = null;
            placeholderPath = null;
            StopGif();
            HideMessage();
            tR = cR = 0;
            UpdateWanted();

            LoadedImage li;
            ThumbInfo th;
            if (cache.TryGetValue(curPath, out li))
            {
                Display(li);
            }
            else
            {
                if (thumbs.TryGetValue(curPath, out th)) ShowPlaceholder(th);
                else img.Source = null;
                RequestImage(curPath);
            }
            Prefetch();
            TrimCache();
            UpdateTitle();
            UpdateInfo();
            UpdateFilmstrip();
        }

        void UpdateWanted()
        {
            var w = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int d in new[] { 0, navDir, -navDir, 2 * navDir })
            {
                int j = curIndex + d;
                if (j >= 0 && j < files.Length) w.Add(files[j]);
            }
            wantedFull = w;
        }

        void Prefetch()
        {
            foreach (int d in new[] { navDir, -navDir, 2 * navDir })
            {
                int j = curIndex + d;
                if (j >= 0 && j < files.Length) RequestImage(files[j]);
            }
        }

        void TrimCache()
        {
            var w = wantedFull;
            foreach (var k in cache.Keys.ToList())
                if (!w.Contains(k)) cache.Remove(k);
        }

        void RequestImage(string p)
        {
            if (cache.ContainsKey(p) || pending.Contains(p)) return;
            pending.Add(p);
            loader.Post(() =>
            {
                LoadedImage li = wantedFull.Contains(p) ? ImageLoader.Load(p) : null;
                Dispatcher.BeginInvoke(new Action(() => OnImageLoaded(p, li)));
            });
        }

        void OnImageLoaded(string p, LoadedImage li)
        {
            pending.Remove(p);
            bool isCurrent = string.Equals(p, curPath, StringComparison.OrdinalIgnoreCase);
            if (li == null)
            {
                if (isCurrent) RequestImage(p); // skipped while the list was changing
                return;
            }
            if (!wantedFull.Contains(p)) return;
            cache[p] = li;
            if (isCurrent && cur == null) Display(li);
        }

        void Display(LoadedImage li)
        {
            cur = li;
            if (li.Error != null)
            {
                img.Source = null;
                ShowMessage(L.F("cant_open", Path.GetFileName(li.Path), li.Error));
            }
            else
            {
                HideMessage();
                bool keepView = string.Equals(placeholderPath, li.Path, StringComparison.OrdinalIgnoreCase)
                    && li.PixelWidth / dpi == imgW && li.PixelHeight / dpi == imgH;
                SetImage(li.Bitmap, li.PixelWidth, li.PixelHeight, keepView);
                curMips = li.Mips;
                MaybeAskDefault();
                if (li.Frames != null && li.Frames.Count > 1) StartGif(li);
            }
            placeholderPath = null;
            UpdateTitle();
            UpdateInfo();
            UpdatePages();
        }

        void ShowPlaceholder(ThumbInfo th)
        {
            placeholderPath = curPath;
            SetImage(th.Bitmap, th.FullWidth, th.FullHeight, false);
        }

        void SetImage(BitmapSource src, int pw, int ph, bool keepView)
        {
            curMips = null;
            zoomAnchored = false;
            img.Source = src;
            imgW = pw / dpi;
            imgH = ph / dpi;
            img.Width = imgW;
            img.Height = imgH;
            if (!keepView)
            {
                fitMode = true;
                ComputeFit();
                cS = tS * 0.97;
                cX = tX;
                cY = tY;
                cR = tR;
                cO = 0;
            }
            tO = 1;
            StartAnim();
        }

        // ================================================================ thumbnails / filmstrip

        void RebuildSlots()
        {
            int n = (int)Math.Floor((stage.ActualWidth - 40 - 2 * 48) / (ThumbW + 10));
            n = Math.Max(1, Math.Min(n, 41));
            if (n % 2 == 0) n--;
            if (n == slots.Count) return;

            filmstrip.Children.Clear();
            slots.Clear();
            for (int i = 0; i < n; i++)
            {
                var im = new Image { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                var b = new Border
                {
                    Width = ThumbW,
                    Height = ThumbH,
                    Margin = new Thickness(5, 0, 5, 0),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(3),
                    Background = slotBrush,
                    Cursor = Cursors.Hand,
                    Child = im,
                    Tag = -1,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                b.MouseEnter += (s, e) => b.Opacity = 1;
                b.MouseLeave += (s, e) => b.Opacity = (int)b.Tag == curIndex ? 1 : 0.55;
                b.MouseLeftButtonUp += (s, e) =>
                {
                    int idx = (int)b.Tag;
                    if (idx >= 0 && idx != curIndex) ShowIndex(idx, idx > curIndex ? 1 : -1);
                    e.Handled = true;
                };
                slots.Add(b);
                filmstrip.Children.Add(b);
            }
        }

        // Re-centers the filmstrip on the current image.
        void UpdateFilmstrip()
        {
            StopStripScroll();
            stripShift.X = 0;
            stripCenter = curIndex;
            RenderStrip();
            UpdateNavButtons();
        }

        // ------------------------------------------------------------------ scrolling (wheel, arrows, drag)
        //
        // The strip has one fractional position: stripCenter is the thumbnail in the middle slot and
        // stripShift the remaining fraction of a thumbnail. Dragging sets the position directly;
        // the wheel and the arrows set a target the strip glides to.

        const double SlotWidth = ThumbW + 10;   // thumbnail plus its 5 px margins
        readonly TranslateTransform stripShift = new TranslateTransform();
        bool stripPressed, stripDragging;
        double stripPressX, stripPressPos;
        bool stripScrolling;
        double stripTarget, stripPos, stripLastFrame;

        bool StripScrollable
        {
            get { return files.Length > 0 && files.Length > slots.Count; }   // else everything is visible
        }

        double StripVisualPos
        {
            get { return stripCenter - stripShift.X / SlotWidth; }
        }

        // Shows the strip at a fractional position; no partial shift past the ends of the list.
        void SetStripPos(double pos)
        {
            double clamped = Math.Max(0, Math.Min(files.Length - 1, pos));
            int c = (int)Math.Round(clamped);
            if (c != stripCenter) { stripCenter = c; RenderStrip(); }
            stripShift.X = (c - clamped) * SlotWidth;
        }

        void ScrollStrip(double delta)
        {
            if (!StripScrollable) return;
            GlideStripTo((stripScrolling ? stripTarget : StripVisualPos) + delta);
        }

        void GlideStripTo(double target)
        {
            stripTarget = Math.Max(0, Math.Min(files.Length - 1, target));
            if (stripScrolling) return;
            stripScrolling = true;
            stripPos = StripVisualPos;
            stripLastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnStripFrame;
        }

        void StopStripScroll()
        {
            if (!stripScrolling) return;
            stripScrolling = false;
            CompositionTarget.Rendering -= OnStripFrame;
        }

        void OnStripFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - stripLastFrame);
            stripLastFrame = now;
            stripPos += (stripTarget - stripPos) * (1 - Math.Exp(-dt * 14));
            if (Math.Abs(stripTarget - stripPos) < 0.004)
            {
                stripPos = stripTarget;
                // touchpads leave fractional targets: finish on a whole thumbnail
                double whole = Math.Round(stripTarget);
                if (Math.Abs(whole - stripTarget) > 1e-6) stripTarget = whole;
                else StopStripScroll();
            }
            SetStripPos(stripPos);
        }

        void OnStripWheel(object sender, MouseWheelEventArgs e)
        {
            // one notch (120) = 3 thumbnails; touchpad deltas scroll proportionally
            ScrollStrip(-e.Delta / 40.0);
            e.Handled = true;
        }

        // A press on the filmstrip is a click on a thumbnail until the mouse moves more than 6 px;
        // then the strip follows the cursor and the release opens nothing.
        void HookStripDrag()
        {
            filmstrip.RenderTransform = stripShift;
            filmstrip.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!StripScrollable) return;
                StopStripScroll();                 // grab the strip where it is right now
                stripPressed = true;
                stripDragging = false;
                stripPressX = e.GetPosition(stripRow).X;
                stripPressPos = StripVisualPos;
            };
            filmstrip.PreviewMouseMove += (s, e) =>
            {
                if (!stripPressed) return;
                if (e.LeftButton != MouseButtonState.Pressed) { EndStripDrag(); return; }
                double dx = e.GetPosition(stripRow).X - stripPressX;
                if (!stripDragging)
                {
                    if (Math.Abs(dx) < 6) return;
                    stripDragging = true;
                    filmstrip.CaptureMouse();
                }
                FollowStripDrag(dx);
                e.Handled = true;
            };
            filmstrip.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (stripDragging)
                {
                    // moves queued right before the release may not have been handled yet: use the
                    // release point itself, or a quick flick would stop short
                    FollowStripDrag(e.GetPosition(stripRow).X - stripPressX);
                    e.Handled = true;   // a drag is not a click on the thumbnail below
                }
                EndStripDrag();
            };
            filmstrip.LostMouseCapture += (s, e) => { if (stripDragging) EndStripDrag(); };
        }

        // dx: horizontal distance from the press point; dragging right brings earlier photos into view
        void FollowStripDrag(double dx)
        {
            SetStripPos(stripPressPos - dx / SlotWidth);
        }

        void EndStripDrag()
        {
            bool wasDragging = stripDragging;
            stripPressed = stripDragging = false;
            if (filmstrip.IsMouseCaptured) filmstrip.ReleaseMouseCapture();
            if (wasDragging) GlideStripTo(Math.Round(StripVisualPos));   // settle on the nearest thumbnail
        }
        void RenderStrip()
        {
            stripRow.Visibility = files.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (slots.Count == 0) RebuildSlots();
            int n = slots.Count, count = files.Length;
            // A folder that fits completely stays put (centred as a group, only the highlight moves);
            // a larger one keeps the current photo in the centre slot, empty slots at the ends.
            bool allFit = count <= n;
            int start = allFit ? 0 : stripCenter - n / 2;

            var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                var b = slots[i];
                var im = (Image)b.Child;
                if (idx < 0 || idx >= count)
                {
                    b.Visibility = allFit ? Visibility.Collapsed : Visibility.Hidden;
                    b.Tag = -1;
                    im.Source = null;
                    continue;
                }
                bool isCur = idx == curIndex;
                b.Visibility = Visibility.Visible;
                b.Tag = idx;
                b.BorderBrush = isCur ? accentBrush : Brushes.Transparent;
                b.Opacity = isCur || b.IsMouseOver ? 1 : 0.55;
                b.RenderTransform = isCur ? new ScaleTransform(1.14, 1.14) : null;
                Panel.SetZIndex(b, isCur ? 1 : 0);
                b.ToolTip = Path.GetFileName(files[idx]);
                ThumbInfo th;
                if (thumbs.TryGetValue(files[idx], out th)) im.Source = th.Bitmap;
                else { im.Source = null; wanted[files[idx]] = idx; }
            }
            wantedThumbs = new HashSet<string>(wanted.Keys, StringComparer.OrdinalIgnoreCase);
            stripPrev.Visibility = stripNext.Visibility = allFit ? Visibility.Hidden : Visibility.Visible;
            stripPrev.IsEnabled = stripCenter > 0;
            stripNext.IsEnabled = stripCenter < count - 1;
            stripPrev.Opacity = stripPrev.IsEnabled ? 1 : 0.3;
            stripNext.Opacity = stripNext.IsEnabled ? 1 : 0.3;
            // request the middle slot first, then outwards
            foreach (var kv in wanted.OrderBy(kv => Math.Abs(kv.Value - stripCenter)))
                RequestThumb(kv.Key);
        }

        void RequestThumb(string p)
        {
            if (thumbs.ContainsKey(p) || thumbPending.Contains(p)) return;
            thumbPending.Add(p);
            thumbLoader.Post(() =>
            {
                ThumbInfo th = null;
                if (wantedThumbs.Contains(p))
                {
                    try { th = ImageLoader.LoadThumb(p, (int)(ThumbBox * dpi)); }
                    catch { th = new ThumbInfo(); }
                }
                Dispatcher.BeginInvoke(new Action(() => OnThumbLoaded(p, th)));
            });
        }

        void OnThumbLoaded(string p, ThumbInfo th)
        {
            thumbPending.Remove(p);
            if (th == null) return;
            thumbs[p] = th;
            thumbOrder.Enqueue(p);
            while (thumbOrder.Count > ThumbCacheMax) thumbs.Remove(thumbOrder.Dequeue());

            foreach (var b in slots)
            {
                int idx = (int)b.Tag;
                if (idx >= 0 && idx < files.Length && string.Equals(files[idx], p, StringComparison.OrdinalIgnoreCase))
                    ((Image)b.Child).Source = th.Bitmap;
            }
            if (th.Bitmap != null && cur == null && img.Source == null && string.Equals(p, curPath, StringComparison.OrdinalIgnoreCase))
                ShowPlaceholder(th);
        }

        // ================================================================ view math

        int NormRot()
        {
            return (((int)Math.Round(tR)) % 360 + 360) % 360;
        }

        double RotW() { return NormRot() % 180 == 0 ? imgW : imgH; }
        double RotH() { return NormRot() % 180 == 0 ? imgH : imgW; }

        Rect FitArea()
        {
            double vw = stage.ActualWidth, vh = stage.ActualHeight;
            double top = barsVisible ? topBar.ActualHeight : 0;
            double bottom = barsVisible ? bottomBar.ActualHeight : 0;
            double pad = barsVisible ? FitPad : 0;
            return new Rect(pad, top + pad / 2, Math.Max(1, vw - 2 * pad), Math.Max(1, vh - top - bottom - pad));
        }

        double FitScale()
        {
            if (imgW <= 0 || imgH <= 0) return 1;
            var a = FitArea();
            return Math.Min(1.0, Math.Min(a.Width / RotW(), a.Height / RotH()));
        }

        Point ViewCenter()
        {
            var a = FitArea();
            return new Point(a.X + a.Width / 2, a.Y + a.Height / 2);
        }

        void ComputeFit()
        {
            tS = FitScale();
            var c = ViewCenter();
            tX = c.X;
            tY = c.Y;
            UpdateZoomText();
        }

        // A photo smaller than the free area stays inside it; a larger one keeps covering the
        // window without empty gaps. keepX/keepY widen the allowed range to include a position
        // the photo already has, so a drag never makes it jump.
        void ClampTarget(double keepX = double.NaN, double keepY = double.NaN)
        {
            var a = FitArea();
            double vw = stage.ActualWidth, vh = stage.ActualHeight;
            double dw = RotW() * tS, dh = RotH() * tS;
            tX = dw <= a.Width ? ClampAxis(tX, dw, a.X, a.Width, keepX) : ClampAxis(tX, dw, 0, vw, keepX);
            tY = dh <= a.Height ? ClampAxis(tY, dh, a.Y, a.Height, keepY) : ClampAxis(tY, dh, 0, vh, keepY);
        }

        void KeepOnScreen()
        {
            const double Visible = 80;
            double dw = RotW() * tS, dh = RotH() * tS;
            double mx = Math.Min(Visible, dw), my = Math.Min(Visible, dh);
            tX = Math.Max(mx - dw / 2, Math.Min(stage.ActualWidth - mx + dw / 2, tX));
            tY = Math.Max(my - dh / 2, Math.Min(stage.ActualHeight - my + dh / 2, tY));
        }

        static double ClampAxis(double c, double size, double start, double length, double keep)
        {
            double a = start + size / 2, b = start + length - size / 2;
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (!double.IsNaN(keep)) { lo = Math.Min(lo, keep); hi = Math.Max(hi, keep); }
            return Math.Max(lo, Math.Min(hi, c));
        }

        void ZoomBy(double factor, Point p)
        {
            ZoomTo(tS * factor, p);
        }

        void ZoomTo(double scale, Point p)
        {
            if (img.Source == null) return;
            double fit = FitScale();
            double ns = Math.Max(Math.Min(fit, 1.0), Math.Min(MaxScale, scale));
            // anchor on the photo itself: a cursor over the background zooms from the nearest edge
            double dw = RotW() * tS, dh = RotH() * tS;
            p = new Point(Math.Max(tX - dw / 2, Math.Min(tX + dw / 2, p.X)), Math.Max(tY - dh / 2, Math.Min(tY + dh / 2, p.Y)));
            zoomAnchor = p;
            zoomAnchored = true;
            // both directions: the point under the cursor stays exactly in place, nothing snaps
            // or recentres (the Fit button / 0 key recentre explicitly)
            tX = p.X - (p.X - tX) * (ns / tS);
            tY = p.Y - (p.Y - tY) * (ns / tS);
            tS = ns;
            var vc = ViewCenter();
            fitMode = Math.Abs(ns - fit) < 1e-4 && Math.Abs(tX - vc.X) < 1 && Math.Abs(tY - vc.Y) < 1;
            StartAnim();
            UpdateZoomText();
            ShowToast(Math.Round(ns * 100) + "%");
        }

        void Fit()
        {
            zoomAnchored = false;
            fitMode = true;
            ComputeFit();
            StartAnim();
        }

        void ToggleZoom(Point p)
        {
            double fit = FitScale();
            if (fitMode) ZoomTo(fit < 1 ? 1 : 2, p);
            else Fit();
        }

        void Rotate(int deg)
        {
            if (img.Source == null) return;
            zoomAnchored = false;
            tR += deg;
            if (fitMode) ComputeFit();
            else ClampTarget();
            StartAnim();
        }

        void OnViewportChanged()
        {
            RebuildSlots();
            UpdateFilmstrip();
            if (fitMode) ComputeFit();
            else ClampTarget();
            cS = tS; cX = tX; cY = tY;
            ApplyTransform();
        }

        void UpdateZoomText()
        {
            zoomText.Text = Math.Round(tS * 100) + "%";
        }

        // ================================================================ animation

        void StartAnim()
        {
            if (animating) return;
            animating = true;
            lastFrameTime = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }

        void OnRendering(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - lastFrameTime);
            lastFrameTime = now;
            if (frameLog != null) { frameLog.Add(dt); scaleLog.Add(cS); levelLog.Add(mipLevel); }
            double k = 1 - Math.Exp(-dt * 20);
            double kz = 1 - Math.Exp(-dt * 12);   // zoom eases out more gently

            double ns = Math.Exp(Math.Log(cS) + (Math.Log(tS) - Math.Log(cS)) * kz);
            if (zoomAnchored)
            {
                // move with the scale so the anchor stays put, then drift towards where the
                // target would be at this scale (differs from the anchor only after clamping)
                var a = zoomAnchor;
                double ax = a.X - (a.X - cX) * (ns / cS), ay = a.Y - (a.Y - cY) * (ns / cS);
                double ex = a.X - (a.X - tX) * (ns / tS), ey = a.Y - (a.Y - tY) * (ns / tS);
                cX = ax + (ex - ax) * kz;
                cY = ay + (ey - ay) * kz;
            }
            else
            {
                cX += (tX - cX) * k;
                cY += (tY - cY) * k;
            }
            cS = ns;
            cR += (tR - cR) * k;
            cO += (tO - cO) * Math.Min(1, k * 1.5);

            if (Math.Abs(cS / tS - 1) < 0.0015 && Math.Abs(cX - tX) < 0.3 && Math.Abs(cY - tY) < 0.3
                && Math.Abs(cR - tR) < 0.2 && Math.Abs(cO - tO) < 0.01)
            {
                cS = tS; cX = tX; cY = tY; cR = tR; cO = tO;
                animating = false;
                zoomAnchored = false;
                CompositionTarget.Rendering -= OnRendering;
            }
            ApplyTransform();
        }

        void ApplyTransform()
        {
            double ox = cX, oy = cY;
            if (!animating)
            {
                // snap the top-left corner to whole device pixels for crisp 1:1 output
                double dw = RotW() * cS, dh = RotH() * cS;
                ox = Math.Round((cX - dw / 2) * dpi) / dpi + dw / 2;
                oy = Math.Round((cY - dh / 2) * dpi) / dpi + dh / 2;
            }
            var m = Matrix.Identity;
            m.Translate(-imgW / 2, -imgH / 2);
            m.Rotate(cR);
            m.Scale(cS, cS);
            m.Translate(ox, oy);
            imgTransform.Matrix = m;
            img.Opacity = cO;

            // Always show the smallest pre-shrunk copy that still has enough pixels: small textures keep
            // every frame cheap, and the resting HighQuality pass no longer stalls on a huge original.
            if (curMips != null)
            {
                while (mipLevel + 1 < curMips.Count && Math.Pow(0.5, mipLevel + 1) >= cS) mipLevel++;
                while (mipLevel > 0 && Math.Pow(0.5, mipLevel) < cS) mipLevel--;
                if (img.Source != curMips[mipLevel]) img.Source = curMips[mipLevel];
            }
            else mipLevel = 0;
            // scale relative to the copy actually shown; HighQuality only when resting and shrinking
            double rel = cS / Math.Pow(0.5, mipLevel);
            var mode = cS >= 3 ? BitmapScalingMode.NearestNeighbor
                : animating || rel >= 0.999 ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality;
            if (RenderOptions.GetBitmapScalingMode(img) != mode) RenderOptions.SetBitmapScalingMode(img, mode);

            if (slideTimer.IsEnabled) stage.Cursor = Cursors.None;
            else if (stage.Cursor == Cursors.None) stage.Cursor = null;
        }

        bool IsOverImage(Point p)
        {
            if (img.Source == null) return false;
            double dw = RotW() * cS, dh = RotH() * cS;
            return Math.Abs(p.X - cX) <= dw / 2 && Math.Abs(p.Y - cY) <= dh / 2;
        }

        // ================================================================ GIF animation

        void StartGif(LoadedImage li)
        {
            gifFrames = li.Frames;
            gifDelays = li.Delays;
            gifFrame = 0;
            img.Source = gifFrames[0];
            gifTimer.Interval = TimeSpan.FromMilliseconds(gifDelays[0]);
            gifTimer.Start();
        }

        void StopGif()
        {
            gifTimer.Stop();
            gifFrames = null;
        }

        void OnGifTick(object sender, EventArgs e)
        {
            if (gifFrames == null) { gifTimer.Stop(); return; }
            gifFrame = (gifFrame + 1) % gifFrames.Count;
            img.Source = gifFrames[gifFrame];
            gifTimer.Interval = TimeSpan.FromMilliseconds(gifDelays[gifFrame]);
        }

        // ================================================================ input

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || img.Source == null)
            {
                Navigate(e.Delta > 0 ? -1 : 1);
                return;
            }
            ZoomBy(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(stage));
        }

        void OnStageDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(stage);
            if (e.ClickCount == 2)
            {
                pressed = false;
                if (IsOverImage(p)) ToggleZoom(p);
                return;
            }
            pressed = true;
            dragging = false;
            pressPoint = p;
            pressX = tX;
            pressY = tY;
            stage.CaptureMouse();
        }

        void OnStageMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(stage);
            if (!pressed)
            {
                if (!slideTimer.IsEnabled) stage.Cursor = IsOverImage(p) ? Cursors.SizeAll : null;
                return;
            }
            var d = p - pressPoint;
            if (!dragging && d.Length > 4) { dragging = true; zoomAnchored = false; }
            if (!dragging || img.Source == null) return;
            // free movement on both axes; only keep a strip of the photo on screen
            tX = pressX + d.X;
            tY = pressY + d.Y;
            KeepOnScreen();
            fitMode = false;
            cX = tX;
            cY = tY;
            if (!animating) ApplyTransform();
        }

        void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            stage.ReleaseMouseCapture();
            // Like Picasa: a click on the empty area around the photo closes the viewer;
            // with the details panel open, that click only closes the panel.
            if (!dragging && !IsOverImage(e.GetPosition(stage)))
            {
                if (infoVisible) ToggleInfo();
                else if (!windowed) Close();
            }
            dragging = false;
        }

        void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1) { Navigate(-1); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { Navigate(1); e.Handled = true; }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (OverlayOpen)
            {
                // a card is open: Esc closes it, other keys must not act on the photo behind
                if (e.Key == Key.Escape) CloseOverlay();
                e.Handled = e.Key != Key.System;
                return;
            }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Escape:
                    if (slideTimer.IsEnabled) ToggleSlideshow();
                    else if (infoVisible) ToggleInfo();
                    else Close();
                    break;
                case Key.PageDown:
                    if (ctrl) ShowPage(1); else Navigate(1);
                    break;
                case Key.PageUp:
                    if (ctrl) ShowPage(-1); else Navigate(-1);
                    break;
                case Key.Right: case Key.Space: case Key.D:
                    Navigate(1); break;
                case Key.Left: case Key.Back: case Key.A:
                    Navigate(-1); break;
                case Key.Up: case Key.W:
                    ZoomBy(1.25, ViewCenter()); break;
                case Key.Down: case Key.S:
                    ZoomBy(1 / 1.25, ViewCenter()); break;
                case Key.Home: ShowIndex(0, 1); break;
                case Key.End: ShowIndex(files.Length - 1, -1); break;
                case Key.Add: case Key.OemPlus: ZoomBy(1.25, ViewCenter()); break;
                case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.25, ViewCenter()); break;
                case Key.D0: case Key.NumPad0: case Key.Multiply: Fit(); break;
                case Key.D1: case Key.NumPad1: ZoomTo(1, ViewCenter()); break;
                case Key.Enter: ToggleZoom(ViewCenter()); break;
                case Key.R:
                    if (ctrl && shift) Rotate(-90); else Rotate(shift ? -90 : 90);
                    break;
                case Key.L: Rotate(-90); break;
                case Key.F5: ToggleSlideshow(); break;
                case Key.I: ToggleInfo(); break;
                case Key.B: ToggleBackground(); break;
                case Key.Tab: SetBars(!barsVisible); break;
                case Key.F11: ToggleWindowed(); break;
                case Key.C: if (ctrl) CopyImage(); else e.Handled = false; break;
                case Key.O: if (ctrl) OpenDialog(); else e.Handled = false; break;
                case Key.E: if (ctrl) ShowInFolder(); else e.Handled = false; break;
                default: e.Handled = false; break;
            }
        }

        // ================================================================ commands

        void ToggleSlideshow()
        {
            if (slideTimer.IsEnabled)
            {
                slideTimer.Stop();
                playGlyph.Text = "";
                SetBars(true);
            }
            else
            {
                if (files.Length < 2) return;
                slideTimer.Start();
                playGlyph.Text = "";
                SetBars(false);
                ShowToast(L.T("slideshow_on"));
            }
            ApplyTransform();
        }

        void SetBars(bool visible)
        {
            barsVisible = visible;
            topBar.Visibility = bottomBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavButtons();
            if (fitMode) ComputeFit(); else ClampTarget();
            StartAnim();
        }

        void ToggleInfo()
        {
            infoVisible = !infoVisible;
            infoPanel.Visibility = infoVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfo();
        }

        void ToggleBackground()
        {
            solidBackground = !solidBackground;
            dimLayer.Background = solidBackground ? solidBrush : dimBrush;
        }

        void CopyImage()
        {
            if (cur == null || cur.Bitmap == null) return;
            try
            {
                Clipboard.SetImage(cur.Bitmap);
                ShowToast(L.T("copied"));
            }
            catch (Exception ex)
            {
                ShowToast(L.F("copy_failed", ex.Message));
            }
        }

        void ShowInFolder()
        {
            if (curPath == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + curPath + "\""); }
            catch { }
        }

        void OpenDialog()
        {
            if (slideTimer.IsEnabled) ToggleSlideshow();
            var p = Program.AskForFile(this);
            BringToFront();
            if (p != null) OpenPath(p);
        }

        // ================================================================ text

        void UpdateTitle()
        {
            if (curPath == null) { titleText.Text = ""; return; }
            var parts = new List<string> { Path.GetFileName(curPath) };
            if (files.Length > 1) parts.Add((curIndex + 1) + " / " + files.Length);
            if (cur != null && cur.Error == null)
            {
                if (cur.PageCount > 1) parts.Add(L.F("page", cur.Page + 1, cur.PageCount));
                parts.Add(cur.PixelWidth + " × " + cur.PixelHeight);
                parts.Add(ImageLoader.FormatSize(cur.FileSize));
            }
            titleText.Text = string.Join("      ", parts);
            Title = Path.GetFileName(curPath) + " — PixView";
        }

        void UpdatePages()
        {
            bool multi = cur != null && cur.Error == null && cur.PageCount > 1;
            pageGroup.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (multi) pageText.Text = (cur.Page + 1) + " / " + cur.PageCount;
        }

        // Another page of the current multi-page file; decoded on the loader thread.
        void ShowPage(int delta)
        {
            if (cur == null || cur.PageCount < 2) return;
            int np = Math.Max(0, Math.Min(cur.PageCount - 1, cur.Page + delta));
            if (np == cur.Page) return;
            string p = curPath;
            loader.Post(() =>
            {
                var li = ImageLoader.Load(p, np);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.Equals(p, curPath, StringComparison.OrdinalIgnoreCase)) return;
                    if (np == 0) cache[p] = li;
                    Display(li);
                }));
            });
        }

        void UpdateInfo()
        {
            if (!infoVisible) return;
            infoText.Inlines.Clear();
            if (cur == null || cur.Info.Count == 0)
            {
                infoText.Inlines.Add(new Run(cur == null ? L.T("loading") : L.T("no_info")));
                return;
            }
            bool first = true;
            var gray = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
            foreach (var kv in cur.Info)
            {
                if (!first) infoText.Inlines.Add(new LineBreak());
                first = false;
                infoText.Inlines.Add(new Run(kv.Key) { Foreground = gray, FontSize = 11 });
                infoText.Inlines.Add(new LineBreak());
                infoText.Inlines.Add(new Run(kv.Value));
            }
        }

        void ShowMessage(string text)
        {
            message.Text = text;
            message.Visibility = Visibility.Visible;
        }

        void HideMessage()
        {
            message.Visibility = Visibility.Collapsed;
        }

        void ShowToast(string text)
        {
            toastText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
            toast.BeginAnimation(OpacityProperty, a);
        }
    }
}
