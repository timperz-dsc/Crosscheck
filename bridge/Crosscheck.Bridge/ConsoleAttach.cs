using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Crosscheck.Bridge.App;

/// <summary>
/// crosscheck.exe is a Windows (no console) program, so double-clicking it opens no black window.
/// Started from a terminal with arguments (create, join, --sim fake ...) it attaches to that
/// terminal so its output and Ctrl+C work as before. Output that is redirected to a file or pipe is left alone.
/// </summary>
internal static class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdInput = -10, StdOutput = -11, StdError = -12;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 3, OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Call first, before anything touches Console. Does nothing without a parent console.</summary>
    public static void AttachToParent()
    {
        if (!AttachConsole(AttachParentProcess)) return;
        Rebind(StdInput, "CONIN$", GenericRead);
        Rebind(StdOutput, "CONOUT$", GenericWrite);
        Rebind(StdError, "CONOUT$", GenericWrite);
    }

    private static void Rebind(int which, string device, uint access)
    {
        var current = GetStdHandle(which);
        if (current != IntPtr.Zero && current != new IntPtr(-1)) return; // redirected: keep it
        var handle = CreateFile(device, access, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) SetStdHandle(which, handle.DangerousGetHandle()); // handle stays open for the life of the process
    }
}
