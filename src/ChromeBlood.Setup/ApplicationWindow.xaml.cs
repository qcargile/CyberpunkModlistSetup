using System.Windows;
using System.ComponentModel;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace ChromeBlood.Setup;

public partial class ApplicationWindow : Window
{
    private HwndSource? _source;

    private bool _allowClose;
    private bool _closing;

    public ApplicationWindow()
    {
        InitializeComponent();
        Closing += DrainBeforeClose;
    }

    private async void DrainBeforeClose(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        if (RouteContent.Content is MainWindow view) await view.CancelAndDrainAsync();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this);
        _source.AddHook(WindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowMessage);
        _source = null;
        base.OnClosed(e);
    }

    private nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024)
        {
            return 0;
        }

        nint monitor = MonitorFromWindow(handle, 2);
        MonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return 0;
        }

        MinMaxInfo bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new NativePoint(
            monitorInfo.Work.Left - monitorInfo.Monitor.Left,
            monitorInfo.Work.Top - monitorInfo.Monitor.Top);
        bounds.MaxSize = new NativePoint(
            monitorInfo.Work.Right - monitorInfo.Work.Left,
            monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    public FrameworkElement? CurrentView => RouteContent.Content as FrameworkElement;

    public void ShowView(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        DataContext = view.DataContext;
        RouteContent.Content = view;
    }
}
