using System.Diagnostics;
using Xunit;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// crosscheck.exe is a Windows program without a console window. Started with arguments it must still
/// parse them and print to the terminal (here: to the pipes of the test).
/// </summary>
public class CliProcessTests
{
    private static string Exe()
    {
        var bin = Path.Combine(Repo.Root, "bridge", "Crosscheck.Bridge", "bin");
        return Directory.EnumerateFiles(bin, "crosscheck.exe", SearchOption.AllDirectories)
                   .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
               ?? throw new FileNotFoundException("crosscheck.exe not built; run dotnet build bridge/Crosscheck.sln");
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var info = new ProcessStartInfo(Exe()) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(30000), "crosscheck.exe did not exit");
        return (p.ExitCode, output.Result, err.Result);
    }

    [Fact]
    public void Is_a_windows_program_without_a_console()
    {
        using var stream = File.OpenRead(Exe());
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        Assert.Equal(System.Reflection.PortableExecutable.Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
    }

    [Fact]
    public void A_bad_command_prints_the_error_and_usage_and_exits_with_2()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("Unknown command frobnicate", err);
        Assert.Contains("crosscheck create", err);
    }

    [Fact]
    public void Join_without_a_code_is_reported()
    {
        var (code, _, err) = Run("join");
        Assert.Equal(2, code);
        Assert.Contains("join needs the 6-character code", err);
    }

    [Fact]
    public void Headless_create_with_the_fake_sim_runs_the_real_startup_and_reports_an_unreachable_server()
    {
        var (code, output, err) = Run("create", "--sim", "fake", "--server", "ws://127.0.0.1:9/ws", "--api-port", "-1", "--no-tray", "--log-dir", Repo.TempDir("crosscheck-cli"));
        Assert.Equal(1, code);
        Assert.Contains("Profile ", output + err);
    }
}
