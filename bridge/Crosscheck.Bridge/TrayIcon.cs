using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Crosscheck.Bridge.App;

/// <summary>
/// Windows tray icon with "Open Crosscheck" and "Quit". Runs its own message loop on an STA
/// thread. The icon is the Crosscheck mark, drawn at runtime (no image files).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Thread _thread;
    private ApplicationContext? _context;
    private NotifyIcon? _icon;
    private Control? _invoker;

    private TrayIcon(Action open, Action quit)
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Crosscheck", null, (_, _) => open());
            menu.Items.Add("Quit", null, (_, _) => quit());
            _icon = new NotifyIcon { Icon = DrawMark(), Text = "Crosscheck", ContextMenuStrip = menu, Visible = true };
            _icon.DoubleClick += (_, _) => open();
            _context = new ApplicationContext();
            _invoker = new Control();
            _ = _invoker.Handle; // created on this thread, so Dispose can reach the loop from any thread
            ready.Set();
            Application.Run(_context);
            _icon.Visible = false;
            _icon.Dispose();
        }) { IsBackground = true, Name = "Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public static TrayIcon Start(Action open, Action quit) => new(open, quit);

    /// <summary>The mark from the design: a short dark stroke and a long green stroke.</summary>
    private static Icon DrawMark()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            const float s = 32f / 100f;
            using var dark = new Pen(Color.FromArgb(0x11, 0x13, 0x15), 13 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var green = new Pen(Color.FromArgb(0x13, 0x8A, 0x52), 13 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(dark, 20 * s, 50 * s, 36 * s, 66 * s);
            g.DrawLine(green, 50 * s, 65.5f * s, 82 * s, 30 * s);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        try
        {
            _invoker?.BeginInvoke(() => _context?.ExitThread());
        }
        catch (InvalidOperationException)
        {
        }
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
