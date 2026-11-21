using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using ZevnShell.Views;

namespace ZevnShell;

public sealed partial class MainWindow : Window
{
    private FrameworkElement? _current;
    private Phase? _shownPhase;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon("Assets/zevn.ico");
        // Ukuran AppWindow dalam piksel fisik: kalikan skala layar (mis. 200%), batasi ke area kerja, taruh di tengah
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = Math.Min((int)(1280 * scale), area.Width), h = Math.Min((int)(820 * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = Math.Min((int)(980 * scale), area.Width);
            p.PreferredMinimumHeight = Math.Min((int)(620 * scale), area.Height);
        }
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Closed += (_, _) => { App.Model.Tunnels.StopAll(); foreach (var s in App.Model.Sessions) s.Terminate(); };

        App.Model.Changed += what =>
        {
            if (what == "phase") Route();
            else if (what == "theme") ApplyTheme(true);
        };
        AddAccelerators();
        ApplyTheme(false);
#if DEBUG
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO") == "1")
        {
            try { App.Model.LoadDemo(); }
            catch (Exception e) { System.IO.File.AppendAllText(System.IO.Path.Combine(Config.LocalDir, "error.log"), $"demo: {e}\n"); }
        }
#endif
        Route();
    }

    private void ApplyTheme(bool rebuild)
    {
        var dark = App.Model.IsDark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Root.Background = Theme.Bg;
        TitleBar.Background = Theme.Bg;
        TitleBar.BorderBrush = Theme.Hairline;
        BrandMark.Background = Theme.BrandBrush;
        BrandText.Foreground = Theme.Text;
        BrandText2.Foreground = Theme.Text3;
        TitleText.Foreground = Theme.Text3;
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(30, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(120, 255, 255, 255) : Windows.UI.Color.FromArgb(120, 0, 0, 0);
        // Shell menggambar ulang sendiri (view terminal tidak boleh dibuat ulang); onboarding dibuat ulang
        if (rebuild && _current is OnboardingPage) { _shownPhase = null; Route(); }
    }

    /// <summary>Terkunci / onboarding → layar sambutan; terbuka → shell utama.</summary>
    private void Route()
    {
        var phase = App.Model.Phase;
        if (_shownPhase == phase && _current != null) return;
        if (_current is ShellPage oldShell) oldShell.Detach();
        if (_current is OnboardingPage oldOnb) oldOnb.Detach();
        _shownPhase = phase;
        FrameworkElement next = phase == Phase.Main ? new ShellPage() : new OnboardingPage();
        PageHost.Children.Clear();
        PageHost.Children.Add(next);
        _current = next;
        TitleText.Text = phase == Phase.Main ? "Ctrl+Shift+L · kunci vault" : "Vault terenkripsi lokal";
    }

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action a)
        {
            var k = new KeyboardAccelerator { Key = key, Modifiers = mods };
            k.Invoked += (_, e) => { if (App.Model.Phase == Phase.Main) { a(); e.Handled = true; } };
            Root.KeyboardAccelerators.Add(k);
        }
        var m = App.Model;
        Add(VirtualKey.N, VirtualKeyModifiers.Control, () => { m.Navigate(Section.Hosts); m.Edit(HostExt.Blank()); });
        Add(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, m.Lock);
        Add(VirtualKey.W, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, m.CloseActive);
        Add(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => m.OpenLocalTerminal());
        Add(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => { if (m.ActiveSession is { IsLocal: false }) m.ToggleServerInfo(); });
        Add(VirtualKey.Tab, VirtualKeyModifiers.Control, () => m.CycleTab(1));
        Add(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => m.CycleTab(-1));
        for (int i = 0; i <= 9; i++)
        {
            var n = i;
            Add(VirtualKey.Number0 + i, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => m.FocusTab(n));
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void BringToFront()
    {
        ShowWindow(Hwnd, 9);
        SetForegroundWindow(Hwnd);
    }
}
