using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

// ---------- Settings (saved to %AppData%\WinMac\settings.json) ----------
class Cfg
{
    public bool Dark = true, Blur = true, Dock = true, Magnify = true, MenuBar = true, Clock24, ShowDate = true, Startup = true;
    public bool WinKey = true;
    public Dictionary<string, string> Icons = new();
    public string Accent = "#0A84FF";
    public double Glass = 45, Radius = 22, Icon = 52;
    public List<string> Apps = new() { "explorer.exe", "notepad.exe", "calc.exe", "ms-settings:" };
    public static bool Fresh;
    static readonly string P = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinMac", "settings.json");
    static readonly JsonSerializerOptions O = new() { IncludeFields = true, WriteIndented = true };
    public static Cfg Load()
    {
        try { return JsonSerializer.Deserialize<Cfg>(File.ReadAllText(P), O) ?? new Cfg(); }
        catch { Fresh = true; return new Cfg(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(P)!); File.WriteAllText(P, JsonSerializer.Serialize(this, O)); } catch { }
    }
}

// ---------- Native helpers ----------
static class Nat
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct ABD { public int cb; public IntPtr hWnd; public uint msg, edge; public RECT rc; public IntPtr lp; }
    [DllImport("shell32.dll")] static extern uint SHAppBarMessage(uint m, ref ABD d);

    // Windows 11 22H2+: real acrylic (blur) or mica, rounded corners, dark title bar
    public static void Backdrop(Window w, bool dark, bool blur)
    {
        var h = new WindowInteropHelper(w).Handle;
        int d = dark ? 1 : 0, t = blur ? 3 : 2, c = 2;
        DwmSetWindowAttribute(h, 20, ref d, 4);
        DwmSetWindowAttribute(h, 38, ref t, 4);
        DwmSetWindowAttribute(h, 33, ref c, 4);
    }
    // Reserve screen space at the top so maximized windows don't go under the menu bar
    public static void Reserve(IntPtr h, int hpx, int wpx)
    {
        var a = new ABD { cb = Marshal.SizeOf<ABD>(), hWnd = h, edge = 1 };
        SHAppBarMessage(0, ref a);
        a.rc = new RECT { l = 0, t = 0, r = wpx, b = hpx };
        SHAppBarMessage(2, ref a);
        a.rc.b = a.rc.t + hpx;
        SHAppBarMessage(3, ref a);
    }
    delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr m, uint t);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr ex);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? n);
    static HookProc? hp; static IntPtr hh; static bool winDown, combo;
    public static Action? WinTap;
    public static void HookWin()
    {
        if (hh != IntPtr.Zero) return;
        hp = WinProc; hh = SetWindowsHookEx(13, hp, GetModuleHandle(null), 0);
    }
    static IntPtr WinProc(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0 && App.C.WinKey)
        {
            int vk = Marshal.ReadInt32(l), fl = Marshal.ReadInt32(l, 8), m = (int)w;
            bool down = m == 0x100 || m == 0x104;
            if ((fl & 0x10) == 0) // ignore keys we inject ourselves
            {
                if (vk == 0x5B || vk == 0x5C)
                {
                    if (down) { if (!winDown) { winDown = true; combo = false; } }
                    else
                    {
                        bool tap = winDown && !combo; winDown = false;
                        if (tap)
                        {
                            keybd_event(0xE8, 0, 0, UIntPtr.Zero); keybd_event(0xE8, 0, 2, UIntPtr.Zero); // cancels the Start menu
                            Application.Current.Dispatcher.BeginInvoke(() => WinTap?.Invoke());
                        }
                    }
                }
                else if (winDown && down) combo = true; // Win+E, Win+D etc. still work normally
            }
        }
        return CallNextHookEx(hh, n, w, l);
    }
    public static void Release(IntPtr h)
    {
        var a = new ABD { cb = Marshal.SizeOf<ABD>(), hWnd = h };
        SHAppBarMessage(1, ref a);
    }
}

// ---------- App ----------
class App : Application
{
    public static Cfg C = Cfg.Load();
    static DockWin? dock; static BarWin? bar; static SettingsWin? sw;
    static System.Windows.Forms.NotifyIcon? ni;

    [STAThread]
    static void Main()
    {
        using var m = new System.Threading.Mutex(true, "WinMac.Single", out var first);
        if (!first) return;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ni = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "WinMac", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => Open());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        ni.ContextMenuStrip = menu;
        ni.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Open(); };
        Apply();
        Nat.WinTap = Launcher.Toggle; Nat.HookWin();
        if (Cfg.Fresh) Open();
        app.Run();
    }

    public static void Open()
    {
        if (sw == null) { sw = new SettingsWin(); sw.Closed += (_, _) => sw = null; }
        sw.Show(); sw.Activate();
    }
    static void Quit()
    {
        ni!.Visible = false; ni.Dispose();
        bar?.Close(); dock?.Close(); Current.Shutdown();
    }
    static void AutoRun(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (on) k?.SetValue("WinMac", "\"" + Environment.ProcessPath + "\"");
            else k?.DeleteValue("WinMac", false);
        }
        catch { }
    }
    public static void Apply()
    {
        C.Save(); AutoRun(C.Startup);
        if (C.MenuBar) { bar ??= new BarWin(); bar.Show(); bar.Refresh(); } else if (bar != null) { bar.Close(); bar = null; }
        if (C.Dock) { dock ??= new DockWin(); dock.Show(); dock.Refresh(); } else if (dock != null) { dock.Close(); dock = null; }
    }

    public static Color Tint() => Color.FromArgb((byte)(235 - C.Glass * 1.8), (byte)(C.Dark ? 30 : 248), (byte)(C.Dark ? 30 : 248), (byte)(C.Dark ? 34 : 250));
    public static Brush GlassBrush() => new SolidColorBrush(Tint());
    public static Brush Rim() => new LinearGradientBrush(Color.FromArgb(150, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 90);
    public static Brush Fg() => C.Dark ? Brushes.White : Brushes.Black;
    public static Color Accent() => (Color)ColorConverter.ConvertFromString(C.Accent);
}

// ---------- Top menu bar ----------
class BarWin : Window
{
    readonly Border root = new();
    readonly TextBlock clk = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontSize = 12.5 };
    readonly TextBlock name = new() { Text = "  ◆  WinMac", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Cursor = Cursors.Hand };

    public BarWin()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Height = 28;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        var g = new Grid(); g.Children.Add(name); g.Children.Add(clk);
        root.Child = g; Content = root;
        name.MouseLeftButtonUp += (_, _) => App.Open();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (_, _) => Tick(); t.Start();
        Loaded += (_, _) =>
        {
            var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth;
            Nat.Reserve(new WindowInteropHelper(this).Handle, (int)(28 * s), (int)(Width * s));
        };
    }
    protected override void OnClosed(EventArgs e) { Nat.Release(new WindowInteropHelper(this).Handle); base.OnClosed(e); }
    void Tick() => clk.Text = DateTime.Now.ToString((App.C.ShowDate ? "ddd MMM d   " : "") + (App.C.Clock24 ? "HH:mm" : "h:mm tt"));
    public void Refresh()
    {
        root.Background = App.GlassBrush(); root.Foreground = App.Fg();
        root.BorderBrush = App.Rim(); root.BorderThickness = new Thickness(0, 0, 0, 1);
        Tick();
    }
}

// ---------- Dock ----------
class DockWin : Window
{
    readonly StackPanel sp = new() { Orientation = Orientation.Horizontal };
    readonly Border root = new();
    static readonly Dictionary<string, ImageSource?> cache = new();

    public DockWin()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        root.Child = sp; Content = root;
        SizeChanged += (_, _) =>
        {
            Left = (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 4;
        };
    }
    public static ImageSource? Icon(string a)
    {
        if (cache.TryGetValue(a, out var v)) return v;
        ImageSource? r = null;
        try
        {
            var f = File.Exists(a) ? a : new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
                .Select(d => Path.Combine(d, a)).FirstOrDefault(File.Exists);
            if (f != null)
            {
                using var i = System.Drawing.Icon.ExtractAssociatedIcon(f)!;
                r = Imaging.CreateBitmapSourceFromHIcon(i.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                r.Freeze();
            }
        }
        catch { }
        return cache[a] = r;
    }
    static UIElement Art(string a, double s)
    {
        if (App.C.Icons.TryGetValue(a, out var f) && File.Exists(f))
        {
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(f); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = (int)(s * 2); bi.EndInit(); bi.Freeze();
                return new Image { Source = bi, Width = s, Height = s };
            }
            catch { }
        }
        if (a == "explorer.exe") return Finder(s);
        if (a == "ms-settings:") return Gear(s);
        var ic = Icon(a);
        return ic != null ? new Image { Source = ic, Width = s, Height = s } : Gear(s);
    }
    // Original Finder-style icon (two-tone blue squircle with a face)
    static UIElement Finder(double s)
    {
        var g = new Grid { Width = s, Height = s };
        var bg = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, s, s), s * .23, s * .23) };
        bg.ColumnDefinitions.Add(new ColumnDefinition()); bg.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new Border { Background = new LinearGradientBrush(Color.FromRgb(140, 215, 255), Color.FromRgb(60, 160, 250), 90) };
        var r = new Border { Background = new LinearGradientBrush(Color.FromRgb(40, 120, 240), Color.FromRgb(20, 80, 200), 90) };
        Grid.SetColumn(r, 1); bg.Children.Add(l); bg.Children.Add(r); g.Children.Add(bg);
        var d = FormattableString.Invariant($"M{.32 * s},{.30 * s} L{.32 * s},{.46 * s} M{.68 * s},{.30 * s} L{.68 * s},{.46 * s} M{.50 * s},{.42 * s} L{.46 * s},{.60 * s} L{.53 * s},{.60 * s} M{.25 * s},{.68 * s} Q{.50 * s},{.88 * s} {.75 * s},{.68 * s}");
        g.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Stroke = new SolidColorBrush(Color.FromRgb(11, 42, 91)), StrokeThickness = s * .05, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = s, Height = s });
        return g;
    }
    // Gear on a grey squircle, like macOS System Settings
    static UIElement Gear(double s)
    {
        var g = new Grid { Width = s, Height = s };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(s * .23), Background = new LinearGradientBrush(Color.FromRgb(165, 170, 178), Color.FromRgb(88, 93, 101), 90) });
        g.Children.Add(new TextBlock { Text = "⚙", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = s * .72, Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 245)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }
    static void Zoom(ScaleTransform s, double v)
    {
        var an = new DoubleAnimation(v, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, an); s.BeginAnimation(ScaleTransform.ScaleYProperty, an);
    }
    public void Refresh()
    {
        var c = App.C;
        root.Background = App.GlassBrush(); root.BorderBrush = App.Rim(); root.BorderThickness = new Thickness(1);
        root.CornerRadius = new CornerRadius(c.Radius); root.Padding = new Thickness(8, 6, 8, 6);
        root.Margin = new Thickness(0, c.Icon * 0.45, 0, 0);
        sp.Children.Clear();
        foreach (var a in c.Apps)
        {
            var s = new ScaleTransform(1, 1);
            UIElement e = Art(a, c.Icon);
            var h = new Border { Child = e, Margin = new Thickness(5, 0, 5, 0), RenderTransform = s, RenderTransformOrigin = new Point(0.5, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            h.MouseEnter += (_, _) => Zoom(s, c.Magnify ? 1.4 : 1.08);
            h.MouseLeave += (_, _) => Zoom(s, 1);
            var p = a;
            h.MouseLeftButtonUp += (_, _) => { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } };
            sp.Children.Add(h);
        }
    }
}

// ---------- Settings window (macOS System Settings look) ----------
class SettingsWin : Window
{
    Cfg c => App.C;
    readonly StackPanel host = new() { Margin = new Thickness(30, 8, 30, 24) };
    readonly StackPanel side = new() { Margin = new Thickness(10, 40, 10, 10) };
    string cur = "General";
    static readonly string[] Panes = { "General", "Appearance", "Dock", "Menu Bar", "About" };
    static readonly string[] Icons = { "⚙", "◐", "▬", "▔", "ⓘ" };
    static readonly string[] Cols = { "#0A84FF", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#8E8E93" };

    public SettingsWin()
    {
        Title = "System Settings"; Width = 840; Height = 600; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome { GlassFrameThickness = new Thickness(-1), CaptionHeight = 36, ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = true });
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var sv = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 36, 0, 0) };
        Grid.SetColumn(sv, 1); g.Children.Add(side); g.Children.Add(sv); Content = g;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).CompositionTarget.BackgroundColor = Colors.Transparent;
            Draw();
        };
    }

    void Chg() { App.Apply(); Nat.Backdrop(this, c.Dark, c.Blur); }

    void Draw()
    {
        Nat.Backdrop(this, c.Dark, c.Blur);
        Foreground = App.Fg();
        side.Children.Clear();
        for (int i = 0; i < Panes.Length; i++)
        {
            var n = Panes[i]; bool sel = n == cur;
            var b = new Border
            {
                Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                Background = sel ? new SolidColorBrush(App.Accent()) : Brushes.Transparent,
                Child = new TextBlock { Text = Icons[i] + "   " + n, Foreground = sel ? Brushes.White : App.Fg(), FontSize = 14 }
            };
            b.MouseLeftButtonUp += (_, _) => { cur = n; Draw(); };
            side.Children.Add(b);
        }
        Pane();
    }

    void Pane()
    {
        host.Children.Clear();
        host.Children.Add(new TextBlock { Text = cur, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        switch (cur)
        {
            case "General":
                host.Children.Add(Card(Row("Open at login", Tg(c.Startup, v => c.Startup = v)), Row("Windows key opens Launcher", Tg(c.WinKey, v => c.WinKey = v))));
                host.Children.Add(Card(Row("Reset all settings", Btn("Reset", () => { App.C = new Cfg(); Chg(); Draw(); }))));
                break;
            case "Appearance":
                host.Children.Add(Card(
                    Row("Theme", Seg(new[] { "Light", "Dark" }, c.Dark ? 1 : 0, i => { c.Dark = i == 1; Chg(); Draw(); })),
                    Row("Accent color", Swatches())));
                host.Children.Add(Card(
                    Row("Liquid glass blur", Tg(c.Blur, v => c.Blur = v)),
                    Row("Glass transparency", Sl(c.Glass, 0, 100, v => c.Glass = v)),
                    Row("Corner radius", Sl(c.Radius, 6, 36, v => c.Radius = v))));
                break;
            case "Dock":
                host.Children.Add(Card(
                    Row("Show Dock", Tg(c.Dock, v => c.Dock = v)),
                    Row("Magnification", Tg(c.Magnify, v => c.Magnify = v)),
                    Row("Icon size", Sl(c.Icon, 32, 80, v => c.Icon = v))));
                var rows = c.Apps.Select(a =>
                {
                    var p = a; var bt = new StackPanel { Orientation = Orientation.Horizontal };
                    var ib = Btn("Icon…", () =>
                    {
                        var d = new OpenFileDialog { Filter = "Images|*.png;*.ico;*.jpg" };
                        if (d.ShowDialog() == true) { c.Icons[p] = d.FileName; Chg(); Pane(); }
                    });
                    ib.Margin = new Thickness(0, 0, 8, 0); bt.Children.Add(ib);
                    bt.Children.Add(Btn("Remove", () => { c.Apps.Remove(p); Chg(); Pane(); }));
                    var nm = p == "explorer.exe" ? "Finder" : p == "ms-settings:" ? "System Settings" : Path.GetFileNameWithoutExtension(p);
                    return (UIElement)Row(nm, bt);
                }).ToArray();
                host.Children.Add(Card(rows));
                var add = Btn("Add app…", () =>
                {
                    var d = new OpenFileDialog { Filter = "Apps|*.exe;*.lnk" };
                    if (d.ShowDialog() == true) { c.Apps.Add(d.FileName); Chg(); Pane(); }
                });
                add.HorizontalAlignment = HorizontalAlignment.Left; host.Children.Add(add);
                break;
            case "Menu Bar":
                host.Children.Add(Card(
                    Row("Show menu bar", Tg(c.MenuBar, v => c.MenuBar = v)),
                    Row("24-hour clock", Tg(c.Clock24, v => c.Clock24 = v)),
                    Row("Show date", Tg(c.ShowDate, v => c.ShowDate = v))));
                break;
            default:
                host.Children.Add(new TextBlock { Text = "WinMac 0.1\nA lightweight macOS-style layer for Windows.\nSettings are saved to %AppData%\\WinMac.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8, LineHeight = 22 });
                break;
        }
    }

    // ----- UI helpers -----
    Border Card(params UIElement[] rows)
    {
        var s = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) s.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) });
            s.Children.Add(rows[i]);
        }
        return new Border { Child = s, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 18), Background = new SolidColorBrush(c.Dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)) };
    }
    Grid Row(string l, FrameworkElement ctl)
    {
        var g = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        g.Children.Add(new TextBlock { Text = l, VerticalAlignment = VerticalAlignment.Center });
        ctl.HorizontalAlignment = HorizontalAlignment.Right; ctl.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(ctl); return g;
    }
    FrameworkElement Tg(bool v, Action<bool> f)
    {
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.White, Margin = new Thickness(2), HorizontalAlignment = v ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Brush On() => new SolidColorBrush(App.Accent());
        Brush Off() => new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
        var b = new Border { Width = 46, Height = 26, CornerRadius = new CornerRadius(13), Child = dot, Cursor = Cursors.Hand, Background = v ? On() : Off() };
        b.MouseLeftButtonUp += (_, _) =>
        {
            v = !v; dot.HorizontalAlignment = v ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            b.Background = v ? On() : Off(); f(v); Chg();
        };
        return b;
    }
    FrameworkElement Sl(double v, double lo, double hi, Action<double> f)
    {
        var s = new Slider { Minimum = lo, Maximum = hi, Value = v, Width = 190 };
        s.ValueChanged += (_, _) => { f(s.Value); Chg(); };
        return s;
    }
    FrameworkElement Seg(string[] o, int sel, Action<int> f)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < o.Length; i++)
        {
            int k = i;
            var b = new Border
            {
                Padding = new Thickness(16, 5, 16, 5), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(7), Margin = new Thickness(2, 0, 2, 0),
                Background = i == sel ? new SolidColorBrush(App.Accent()) : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                Child = new TextBlock { Text = o[i], Foreground = i == sel ? Brushes.White : App.Fg() }
            };
            b.MouseLeftButtonUp += (_, _) => f(k); s.Children.Add(b);
        }
        return s;
    }
    Border Btn(string t, Action a)
    {
        var b = new Border { Padding = new Thickness(14, 5, 14, 5), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128)), Child = new TextBlock { Text = t } };
        b.MouseLeftButtonUp += (_, _) => a(); return b;
    }
    FrameworkElement Swatches()
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var h in Cols)
        {
            var hh = h;
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = 22, Height = 22, Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, StrokeThickness = 2,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)),
                Stroke = h == c.Accent ? App.Fg() : Brushes.Transparent
            };
            e.MouseLeftButtonUp += (_, _) => { c.Accent = hh; Chg(); Draw(); };
            s.Children.Add(e);
        }
        return s;
    }
}

// ---------- Launcher (Windows key): Spotlight-style search for apps and files ----------
class Launcher : Window
{
    static Launcher? inst;
    static List<(string name, string path)>? apps;
    readonly TextBox q = new() { FontSize = 22, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };
    readonly StackPanel list = new() { Margin = new Thickness(0, 10, 0, 0) };
    List<(string name, string path)> hits = new();
    int sel;

    public static void Toggle()
    {
        if (inst != null) { inst.Close(); return; }
        inst = new Launcher(); inst.Show(); inst.Activate(); inst.q.Focus();
    }
    Launcher()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (SystemParameters.WorkArea.Width - 640) / 2; Top = SystemParameters.WorkArea.Height * 0.2;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); Foreground = App.Fg();
        q.Foreground = App.Fg(); q.CaretBrush = App.Fg();
        var top = new DockPanel(); top.Children.Add(new TextBlock { Text = "🔍", FontSize = 20, VerticalAlignment = VerticalAlignment.Center }); top.Children.Add(q);
        var sp = new StackPanel(); sp.Children.Add(top); sp.Children.Add(list);
        Content = new Border { CornerRadius = new CornerRadius(App.C.Radius), Background = App.GlassBrush(), BorderBrush = App.Rim(), BorderThickness = new Thickness(1), Padding = new Thickness(16), Child = sp };
        q.TextChanged += (_, _) => Search();
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Close();
    }
    protected override void OnClosed(EventArgs e) { inst = null; base.OnClosed(e); }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Down && hits.Count > 0) { sel = (sel + 1) % hits.Count; Render(); e.Handled = true; }
        else if (e.Key == Key.Up && hits.Count > 0) { sel = (sel + hits.Count - 1) % hits.Count; Render(); e.Handled = true; }
        else if (e.Key == Key.Enter && hits.Count > 0) Go(sel);
    }
    static List<(string name, string path)> Apps() => apps ??= new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) }
        .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories))
        .Select(p => (name: Path.GetFileNameWithoutExtension(p), path: p))
        .Where(a => !a.name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
        .GroupBy(a => a.name).Select(g => g.First()).ToList();
    static List<(string name, string path)> Files(string t)
    {
        var r = new List<(string name, string path)>();
        var o = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
        var dirs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
        foreach (var d in dirs)
        {
            try { foreach (var f in Directory.EnumerateFiles(d, "*" + t + "*", o)) { r.Add((Path.GetFileName(f), f)); if (r.Count >= 5) return r; } } catch { }
        }
        return r;
    }
    async void Search()
    {
        var t = q.Text.Trim(); hits = new(); sel = 0;
        if (t.Length > 0)
            hits.AddRange(Apps().Where(a => a.name.Contains(t, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => !a.name.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ThenBy(a => a.name.Length).Take(6));
        Render();
        if (t.Length < 2) return;
        var f = await Task.Run(() => Files(t));
        if (q.Text.Trim() != t) return;
        hits.AddRange(f); Render();
    }
    void Render()
    {
        list.Children.Clear();
        for (int i = 0; i < hits.Count; i++)
        {
            int k = i; bool on = i == sel;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = DockWin.Icon(hits[i].path);
            if (ic != null) row.Children.Add(new Image { Source = ic, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = hits[i].name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? Brushes.White : App.Fg() });
            var b = new Border { Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = on ? new SolidColorBrush(App.Accent()) : Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) => Go(k);
            list.Children.Add(b);
        }
    }
    void Go(int i) { try { Process.Start(new ProcessStartInfo(hits[i].path) { UseShellExecute = true }); } catch { } Close(); }
}
