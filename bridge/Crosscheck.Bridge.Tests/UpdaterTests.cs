using Xunit;

namespace Crosscheck.Bridge.Tests;

public class UpdaterTests
{
    private sealed class FakeSource : IUpdateSource
    {
        public bool IsInstalled { get; set; } = true;
        public string? PendingVersion { get; set; }
        public string? Available { get; set; }
        public Exception? Fail { get; set; }
        public int Checks, Applies;
        public TaskCompletionSource? Gate;

        public async Task<string?> CheckAndDownloadAsync(CancellationToken ct)
        {
            Checks++;
            if (Gate is not null) await Gate.Task;
            if (Fail is not null) throw Fail;
            return Available;
        }

        public void ApplyAndRestart() => Applies++;
    }

    [Fact]
    public async Task Start_downloads_in_the_background_and_reports_ready()
    {
        var source = new FakeSource { Available = "0.4.0" };
        var updater = new Updater(source, () => false);
        await updater.StartAsync(default);
        await Task.Delay(10);
        Assert.Equal(UpdateState.Ready, updater.State);
        Assert.Equal("0.4.0", updater.Version);
        Assert.Equal(0, source.Applies); // downloaded only, not installed
    }

    [Fact]
    public async Task Nothing_happens_when_not_installed()
    {
        var source = new FakeSource { IsInstalled = false, Available = "0.4.0" };
        var updater = new Updater(source, () => false);
        await updater.StartAsync(default);
        await updater.CheckNowAsync("unsupported_version");
        Assert.Equal(0, source.Checks);
        Assert.Equal(UpdateState.None, updater.State);
    }

    [Fact]
    public async Task A_failed_check_is_quiet_and_does_not_throw()
    {
        var updater = new Updater(new FakeSource { Fail = new HttpRequestException("offline") }, () => false);
        await updater.StartAsync(default);
        Assert.Equal(UpdateState.Error, updater.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_is_refused_during_a_session_and_allowed_otherwise(bool session)
    {
        var source = new FakeSource { Available = "0.4.0" };
        var updater = new Updater(source, () => session);
        await updater.StartAsync(default);
        var result = updater.ApplyAndRestart();
        Assert.Equal(!session, result.Ok);
        Assert.Equal(session ? 0 : 1, source.Applies);
    }

    [Fact]
    public void Apply_without_a_downloaded_update_is_refused()
    {
        var source = new FakeSource();
        Assert.False(new Updater(source, () => false).ApplyAndRestart().Ok);
        Assert.Equal(0, source.Applies);
    }

    [Fact]
    public async Task A_pending_update_is_installed_at_start()
    {
        var source = new FakeSource { PendingVersion = "0.4.0" };
        await new Updater(source, () => false).StartAsync(default);
        Assert.Equal(1, source.Applies);
    }

    [Fact]
    public async Task A_pending_update_is_not_installed_at_start_when_a_session_is_active()
    {
        var source = new FakeSource { PendingVersion = "0.4.0" };
        var updater = new Updater(source, () => true);
        await updater.StartAsync(default);
        Assert.Equal(0, source.Applies);
        Assert.Equal(UpdateState.Ready, updater.State);
        Assert.False(updater.ApplyAndRestart().Ok);
    }

    [Fact]
    public async Task Unsupported_version_checks_at_once_and_says_why()
    {
        var source = new FakeSource { Available = "0.5.0" };
        var updater = new Updater(source, () => false);
        await updater.CheckNowAsync("unsupported_version");
        Assert.Equal(1, source.Checks);
        Assert.Equal(UpdateState.Ready, updater.State);
        Assert.Equal("unsupported_version", updater.ToJson()["reason"]!.GetValue<string>());
        Assert.Equal("ready", updater.ToJson()["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unsupported_version_during_a_running_check_does_not_start_a_second_one()
    {
        var source = new FakeSource { Available = "0.5.0", Gate = new TaskCompletionSource() };
        var updater = new Updater(source, () => false);
        var first = updater.StartAsync(default);
        var second = updater.CheckNowAsync("unsupported_version");
        source.Gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, source.Checks);
        Assert.Equal("unsupported_version", updater.Reason);
    }

    [Fact]
    public async Task A_server_refusing_our_version_makes_the_runtime_check_for_an_update_and_say_so()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            // Answers every message in a protocol version this bridge does not know.
            var ctx = await http.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[16384];
            await ws.ReceiveAsync(buffer, default);
            var reply = System.Text.Encoding.UTF8.GetBytes("""{"v":99,"type":"error","session":null,"ts":1,"body":{"code":"x","message":"x"}}""");
            await ws.SendAsync(reply, System.Net.WebSockets.WebSocketMessageType.Text, true, default);
            await Task.Delay(500);
        });

        var source = new FakeSource { Available = "0.5.0" };
        var dir = Repo.TempDir("crosscheck-updater");
        var runtime = new BridgeRuntime(new BridgeOptions
        {
            Server = new Uri($"ws://127.0.0.1:{port}/ws"), ProfilePath = Repo.FixtureProfile, Name = "t", ApiPort = -1,
            LogDir = Path.Combine(dir, "log"), VersionTimeout = TimeSpan.Zero, UiDir = Path.Combine(dir, "no-ui"),
        }, p => BridgeBackends.Fake(p));
        runtime.Updater = new Updater(source, () => runtime.SessionActive);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var result = await runtime.CreateAsync();
            Assert.False(result.Ok);
            Assert.Contains("out of date", result.Error!);
            await Wait.Until(() => runtime.Updater.State == UpdateState.Ready, "update found");
            Assert.Equal(1, source.Checks);
            Assert.Equal("unsupported_version", runtime.StatusJson()["update"]!["reason"]!.GetValue<string>());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }
}

public class DataFolderMigrationTests
{
    private static string Temp() => Directory.CreateTempSubdirectory("crosscheck-mig").FullName;

    [Fact]
    public void Moves_settings_logs_and_webview2_but_never_the_installer_files()
    {
        var root = Temp();
        var oldDir = Path.Combine(root, "Crosscheck");
        var newDir = Path.Combine(root, "CrosscheckData");
        Directory.CreateDirectory(Path.Combine(oldDir, "logs"));
        Directory.CreateDirectory(Path.Combine(oldDir, "WebView2"));
        Directory.CreateDirectory(Path.Combine(oldDir, "current"));
        File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(oldDir, "logs", "a.jsonl"), "x");
        File.WriteAllText(Path.Combine(oldDir, "Update.exe"), "x");

        var moved = AppPaths.MigrateOldData(oldDir, newDir);

        Assert.Equal(3, moved.Count);
        Assert.True(File.Exists(Path.Combine(newDir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(newDir, "logs", "a.jsonl")));
        Assert.True(Directory.Exists(Path.Combine(newDir, "WebView2")));
        Assert.True(File.Exists(Path.Combine(oldDir, "Update.exe")));
        Assert.True(Directory.Exists(Path.Combine(oldDir, "current")));
        Assert.False(File.Exists(Path.Combine(oldDir, "settings.json")));
    }

    [Fact]
    public void Does_not_overwrite_data_already_in_the_new_folder_and_can_run_twice()
    {
        var root = Temp();
        var oldDir = Path.Combine(root, "Crosscheck");
        var newDir = Path.Combine(root, "CrosscheckData");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(oldDir, "settings.json"), "old");
        File.WriteAllText(Path.Combine(newDir, "settings.json"), "new");

        Assert.Empty(AppPaths.MigrateOldData(oldDir, newDir));
        Assert.Empty(AppPaths.MigrateOldData(oldDir, newDir));
        Assert.Equal("new", File.ReadAllText(Path.Combine(newDir, "settings.json")));
    }

    [Fact]
    public void Missing_old_folder_is_fine()
    {
        var root = Temp();
        Assert.Empty(AppPaths.MigrateOldData(Path.Combine(root, "nope"), Path.Combine(root, "CrosscheckData")));
        Assert.False(Directory.Exists(Path.Combine(root, "CrosscheckData")));
    }
}
