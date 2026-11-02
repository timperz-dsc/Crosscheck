using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Crosscheck.Bridge.App;

/// <summary>
/// The desktop side of Crosscheck: the app window (the local UI in WebView2) and the tray icon,
/// both on one STA thread with its own message loop. Closing the window hides it to the tray;
/// "Quit" stops the program (which leaves the session properly). Without the WebView2 runtime
/// the UI opens in the default browser instead.
/// </summary>
public sealed class DesktopShell : IDisposable
{
    private readonly string _url;
    private readonly bool _wantWindow;
    private readonly bool _wantTray;
    private readonly Action _quit;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private ApplicationContext? _context;
    private Control? _invoker;
    private NotifyIcon? _tray;
    private AppWindow? _window;
    private bool _quitting;

    private DesktopShell(string url, bool window, bool tray, Action quit, ILogger log)
    {
        _url = url;
        _wantWindow = window;
        _wantTray = tray;
        // Stopping the program waits for this thread, so never run it on this thread.
        _quit = () => Task.Run(quit);
        _log = log;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Desktop" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>window: open the app window (or the browser if WebView2 is missing). tray: show the tray icon.</summary>
    public static DesktopShell Start(string url, bool window, bool tray, Action quit, ILogger log) => new(url, window, tray, quit, log);

    private void Run(ManualResetEventSlim ready)
    {
        Application.EnableVisualStyles();
        _context = new ApplicationContext();
        _invoker = new Control();
        _ = _invoker.Handle; // created on this thread, so other threads can reach the loop
        if (_wantTray)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Crosscheck", null, (_, _) => Open());
            menu.Items.Add("Quit", null, (_, _) => _quit());
            _tray = new NotifyIcon { Icon = Mark.Icon(32), Text = "Crosscheck", ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (_, _) => Open();
        }
        if (_wantWindow) Open();
        ready.Set();
        Application.Run(_context);
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }

    /// <summary>Shows the window (creating it the first time), or the browser without WebView2.</summary>
    private void Open()
    {
        if (_window is { IsDisposed: false })
        {
            _window.Show();
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
            return;
        }
        if (!WebViewAvailable())
        {
            Browser.Open(_url, _log);
            return;
        }
        _window = new AppWindow(_url, _log, onClose: () =>
        {
            if (_quitting) return false;
            if (_wantTray) return true; // hide to the tray
            _quit(); // no tray: closing the window is quitting
            return false;
        }, onQuit: () =>
        {
            if (!_quitting) _quit();
            return false;
        }, onFailed: () => Browser.Open(_url, _log));
        _window.Show();
    }

    private bool WebViewAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            _log.LogWarning("The WebView2 runtime is not installed; opening Crosscheck in the default browser instead. ({Message})", e.Message);
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _invoker?.Invoke(() =>
            {
                _quitting = true;
                _window?.Close();
                _context?.ExitThread();
            });
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
        }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
    }
}

/// <summary>The app window: the local UI in WebView2. Outside links go to the default browser.</summary>
internal sealed class AppWindow : Form
{
    private readonly string _url;
    private readonly ILogger _log;
    private readonly Func<bool> _onClose;
    private readonly Func<bool> _onQuit;
    private readonly Action _onFailed;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };

    /// <param name="onClose">Called when the pilot closes the window: true = hide instead of closing.</param>
    /// <param name="onQuit">Called when Windows closes the window: always false (close).</param>
    public AppWindow(string url, ILogger log, Func<bool> onClose, Func<bool> onQuit, Action onFailed)
    {
        _url = url;
        _log = log;
        _onClose = onClose;
        _onQuit = onQuit;
        _onFailed = onFailed;
        Text = "Crosscheck";
        Icon = Mark.Icon(64);
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var data = AppPaths.WebView2Dir;
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsLocal(e.Uri)) core.Navigate(e.Uri);
                else Browser.Open(e.Uri, _log);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (IsLocal(e.Uri)) return;
                e.Cancel = true;
                Browser.Open(e.Uri, _log);
            };
            core.Navigate(_url);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            _log.LogWarning("The Crosscheck window could not start WebView2 ({Message}); opening the default browser instead.", e.Message);
            _onFailed();
            Hide();
        }
    }

    /// <summary>The bridge's own pages stay in the window; everything else is an outside site.</summary>
    private bool IsLocal(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && Uri.TryCreate(_url, UriKind.Absolute, out var home)
        && u.Scheme is "http" or "ws" && u.Host == home.Host && u.Port == home.Port;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button (UserClosing, or None for a plain WM_CLOSE): hide to the tray, or quit.
        // Windows shutting down or Task Manager: quit, which leaves the session properly.
        var hide = e.CloseReason is CloseReason.UserClosing or CloseReason.None ? _onClose() : _onQuit();
        if (hide)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

internal static class Browser
{
    public static void Open(string url, ILogger log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning("Could not open the browser ({Message}). Open {Url} yourself.", e.Message, url);
        }
    }
}

/// <summary>The Crosscheck mark from the design (short dark stroke, long green stroke), drawn at runtime.</summary>
internal static class Mark
{
    public static Icon Icon(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var s = size / 100f;
            using var dark = new Pen(Color.FromArgb(0x11, 0x13, 0x15), 13 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var green = new Pen(Color.FromArgb(0x13, 0x8A, 0x52), 13 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(dark, 20 * s, 50 * s, 36 * s, 66 * s);
            g.DrawLine(green, 50 * s, 65.5f * s, 82 * s, 30 * s);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
