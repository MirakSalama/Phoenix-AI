using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace NpuChat;

public sealed partial class MainWindow
{
    private const uint DestroyMessage = 0x0082;
    private const uint GetMinMaxInfoMessage = 0x0024;
    private bool portrait;
    private int landscapeUnits;
    private int portraitUnits;
    private long lastRotation;
    private WindowSubclass? resizeSubclass;

    private (int Width, int Height) WindowRatio => portrait ? (9, 16) : (16, 9);

    private void InitializeMainLayout()
    {
        ConfigureMainPresenter();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        resizeSubclass = HandleWindowMessage;
        if (!SetWindowSubclass(hwnd, resizeSubclass, 1, 0))
            DiagnosticLog.Write("app", "Could not attach window size and tray handlers.");
        FitMainWindow(ScaleForDpi(65));
    }

    private void ConfigureMainPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = true;
    }

    private void Rotate_Click(object sender, RoutedEventArgs args)
    {
        if (lastRotation != 0 && Stopwatch.GetElapsedTime(lastRotation).TotalMilliseconds < 500) return;
        lastRotation = Stopwatch.GetTimestamp();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
            presenter.Restore();
        var size = AppWindow.Size;
        int currentUnits = Math.Max(size.Width, size.Height) / 16;
        if (portrait) portraitUnits = currentUnits;
        else landscapeUnits = currentUnits;
        portrait = !portrait;
        SetActionName(RotateButton, portrait ? "Switch to landscape (16:9)" : "Switch to portrait (9:16)");
        int preferredUnits = portrait ? portraitUnits : landscapeUnits;
        FitMainWindow(preferredUnits > 0 ? preferredUnits : currentUnits);
        DiagnosticLog.Write("app", portrait ? "Window layout: portrait." : "Window layout: landscape.");
        ScrollToEnd();
    }

    private void FitMainWindow(int preferredUnits)
    {
        var work = CurrentWorkArea();
        var (width, height) = WindowRatio;
        int units = Math.Clamp(preferredUnits, 1, MaxUnits(work, width, height));
        var current = AppWindow.Size;
        var position = AppWindow.Position;
        int left = Math.Clamp(position.X + (current.Width - width * units) / 2, work.X, work.X + work.Width - width * units);
        int top = Math.Clamp(position.Y + (current.Height - height * units) / 2, work.Y, work.Y + work.Height - height * units);
        AppWindow.MoveAndResize(new RectInt32(left, top, width * units, height * units));
    }

    private RectInt32 CurrentWorkArea()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var bounds = display.OuterBounds;
        return new RectInt32(bounds.X + work.X, bounds.Y + work.Y, work.Width, work.Height);
    }

    private int ScaleForDpi(int size)
    {
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (int)Math.Ceiling(size * Math.Max(dpi, 96u) / 96.0);
    }

    private static int MaxUnits(RectInt32 work, int width, int height) =>
        Math.Max(1, Math.Min(work.Width / width, work.Height / height));

    private nint HandleWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == DestroyMessage)
            RemoveWindowSubclass(hwnd, resizeSubclass!, id);
        if (HandleTrayMessage(message, wParam, lParam)) return 0;
        if (compact || message != GetMinMaxInfoMessage)
            return DefSubclassProc(hwnd, message, wParam, lParam);

        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        var work = CurrentWorkArea();
        // Keep each dimension usable while letting Windows size the two axes independently.
        // MINMAXINFO.ptMinTrackSize follows three POINT fields, each two 32-bit integers.
        Marshal.WriteInt32(lParam, 24, Math.Min(ScaleForDpi(378), work.Width));
        Marshal.WriteInt32(lParam, 28, Math.Min(ScaleForDpi(414), work.Height));
        return result;
    }

    private void FullView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width;
        HistoryView.DisplayMode = width >= 760 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        DesktopTitle.FontSize = width < 480 ? 15 : 19;
        bool sideBySide = width >= 660 && width >= args.NewSize.Height;
        BioMonitorGaugeColumn.Width = sideBySide ? new GridLength(272) : new GridLength(1, GridUnitType.Star);
        BioMonitorBrainColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(BrainMonitor, sideBySide ? 1 : 0);
        Grid.SetRow(BrainMonitor, sideBySide ? 0 : 1);
        BrainMonitor.BorderThickness = sideBySide ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        BrainMonitor.Padding = sideBySide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 6, 0, 0);
        double buttonWidth = width < 480 ? 28 : 34;
        foreach (var button in WindowTools.Children.OfType<Button>()) button.Width = buttonWidth;
        bool wrapTools = width < 510;
        Grid.SetRow(WindowTools, wrapTools ? 1 : 0);
        Grid.SetColumn(WindowTools, wrapTools ? 0 : 2);
        Grid.SetColumnSpan(WindowTools, wrapTools ? 3 : 1);
        UpdateHeaderInsets();
    }

    private void UpdateHeaderInsets()
    {
        var titleBar = AppWindow.TitleBar;
        double scale = Surface.XamlRoot?.RasterizationScale ?? 1;
        WindowHeader.Padding = ExtendsContentIntoTitleBar ?
            new Thickness(Math.Max(13, titleBar.LeftInset / scale), 0, Math.Max(13, titleBar.RightInset / scale + 5), 0) :
            new Thickness(13, 0, 13, 0);
    }

    private static void SetActionName(DependencyObject button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private static Button IconAction(string glyph, string name)
    {
        var button = new Button { Width = 36, Height = 32, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 16 } };
        SetActionName(button, name);
        return button;
    }

    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
