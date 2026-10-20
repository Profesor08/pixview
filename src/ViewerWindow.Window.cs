using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PixView
{
    // Two layouts: "full" covers the monitor's work area (the taskbar stays visible) over a dimmed
    // desktop snapshot; "windowed" is a movable window sized to the photo. Dragging the top bar
    // switches to windowed; a double-click on it (or F11) goes back to full.
    partial class ViewerWindow
    {
        const double MinWindowW = 760, MinWindowH = 480;   // toolbar and filmstrip still fit
        const double WindowedScreenShare = 0.85;           // largest windowed size, of the work area

        bool windowed;
        bool barPressed;
        Point barPressPoint;
        readonly SolidColorBrush windowedBrush = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1C));

        // A WindowStyle=None window has no WS_CAPTION / WS_MINIMIZEBOX, so Windows neither animates
        // minimize/restore nor lets the taskbar button minimize it. Instead the window keeps the
        // normal style (CanMinimize = minimize box, no resize border) and WindowChrome shrinks the
        // caption and frame to nothing, so it still looks frameless.
        void SetupChrome()
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanMinimize;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            // DWM resets the attributes set before the window is first shown, so apply them again then
            SourceInitialized += (s, e) => UpdateCorners();
            ContentRendered += (s, e) => UpdateCorners();
        }

        // Windows 11 rounds captioned windows and draws a light 1 px frame: fine (with a dark frame)
        // for the photo-sized window, but full screen should meet the screen edges square and bare.
        void UpdateCorners()
        {
            Native.SetWindowShape(new WindowInteropHelper(this).Handle, windowed);
        }

        void HookWindowDrag()
        {

            topBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { ToggleWindowed(); e.Handled = true; return; }
                barPressed = true;
                barPressPoint = e.GetPosition(this);
                topBar.CaptureMouse();
                e.Handled = true;
            };
            topBar.MouseMove += (s, e) =>
            {
                if (!barPressed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if ((p - barPressPoint).Length < 6) return;
                barPressed = false;
                topBar.ReleaseMouseCapture();
                if (!windowed) EnterWindowed(barPressPoint);
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            topBar.MouseLeftButtonUp += (s, e) =>
            {
                barPressed = false;
                topBar.ReleaseMouseCapture();
            };
        }

        // Full screen shows "restore" (to a window), windowed shows "maximize", like normal windows.
        void UpdateMaxButton()
        {
            var glyph = (System.Windows.Controls.TextBlock)maxButton.Content;
            glyph.Text = windowed ? "\uE922" : "\uE923";
            maxButton.ToolTip = L.T(windowed ? "maximize" : "restore");
        }

        void ToggleWindowed()
        {
            if (windowed) EnterFull();
            else EnterWindowed(new Point(ActualWidth / 2, ActualHeight / 2));
        }

        // Work area of the monitor the window is on, in DIPs.
        Rect WorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var scr = hwnd != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(hwnd) : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            double s = SystemDpiScale();
            var wa = scr.WorkingArea;
            return new Rect(wa.Left / s, wa.Top / s, wa.Width / s, wa.Height / s);
        }

        // Shrinks the window to the photo (fitted into most of the screen) plus the bars, keeping the
        // point that was under the cursor (anchor, in window coordinates) under the cursor.
        void EnterWindowed(Point anchor)
        {
            var wa = WorkArea();
            double bars = topBar.ActualHeight + bottomBar.ActualHeight + FitPad;
            double maxW = wa.Width * WindowedScreenShare, maxH = wa.Height * WindowedScreenShare;
            double cw = RotW(), ch = RotH();
            double w, h;
            if (img.Source != null && cw > 0 && ch > 0)
            {
                double s = Math.Min(1.0, Math.Min((maxW - 2 * FitPad) / cw, (maxH - bars) / ch));
                w = cw * s + 2 * FitPad;
                h = ch * s + bars;
            }
            else { w = maxW * 0.7; h = maxH * 0.7; }
            w = Math.Max(MinWindowW, Math.Min(maxW, w));
            h = Math.Max(MinWindowH, Math.Min(maxH, h));

            // keep the grab point at the same relative x and the same y offset inside the top bar
            double fx = ActualWidth > 0 ? anchor.X / ActualWidth : 0.5;
            var screenAnchor = new Point(Left + anchor.X, Top + anchor.Y);
            double left = screenAnchor.X - fx * w, top = screenAnchor.Y - Math.Min(anchor.Y, topBar.ActualHeight / 2);
            left = Math.Max(wa.Left, Math.Min(wa.Right - w, left));
            top = Math.Max(wa.Top, Math.Min(wa.Bottom - h, top));

            windowed = true;
            Background = windowedBrush;
            UpdateMaxButton();
            UpdateCorners();
            Left = left; Top = top; Width = w; Height = h;
            fitMode = true;
            ComputeFit();
            StartAnim();
        }

        void EnterFull()
        {
            var wa = WorkArea();
            windowed = false;
            UpdateMaxButton();
            UpdateCorners();
            // the desktop snapshot only matches the monitor it was taken on
            double s = SystemDpiScale();
            var b = Backdrop.Bounds;
            bool same = Backdrop.Image != null && Math.Abs(b.Left / s - wa.Left) < 1 && Math.Abs(b.Top / s - wa.Top) < 1
                && Math.Abs(b.Width / s - wa.Width) < 1 && Math.Abs(b.Height / s - wa.Height) < 1;
            Background = same ? (Brush)new ImageBrush(Backdrop.Image) { Stretch = Stretch.Fill } : windowedBrush;
            Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
            fitMode = true;
            ComputeFit();
            StartAnim();
        }
    }
}
