using System.Diagnostics;
using Crosscheck.Bridge;
using Crosscheck.Bridge.App;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.SimConnect;
using Crosscheck.Bridge.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!Cli.TryParse(args, out var cli, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(Cli.Usage);
    return 2;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(cli.Verbose ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddSingleton(cli);
builder.Services.AddHostedService<BridgeService>();
var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;

namespace Crosscheck.Bridge.App
{
    /// <summary>Command line. No command = UI mode (app window + tray); create/join = headless.</summary>
    public sealed record Cli(
        SessionMode? Mode, string? Code, Uri Server, string ProfilePath, bool FakeSim, int ApiPort,
        string? LogDir, double GqlPollHz, string? Airac, string? Name, bool Verbose, SeatSide? Side = null,
        bool NoBrowser = false, bool NoTray = false, string? UiDir = null)
    {
        public const string Usage = """
            Usage:
              crosscheck [options]              open the Crosscheck window (create or join there)
              crosscheck create [options]       headless: create a session
              crosscheck join <CODE> [options]  headless: join a session

            Options:
              --server <url>        session server (default ws://127.0.0.1:8787/ws)
              --profile <file>      aircraft profile (default profiles/fenix-a320/profile.yaml)
              --sim simconnect|fake real sim (default) or an in-memory fake sim
              --name <text>         your name shown to the other pilot
              --api-port <n>        local API and UI port on 127.0.0.1 (default 8790, -1 = off)
              --log-dir <dir>       bridge log folder (default %LOCALAPPDATA%\Crosscheck\logs)
              --gql-poll-hz <n>     Fenix GraphQL poll rate (default 2)
              --airac <cycle>       AIRAC cycle of your nav data, e.g. 2611
              --seat-side <side>    your cockpit seat: captain or fo (default: host captain, guest fo)
              --ui-dir <dir>        folder with the built UI (default wwwroot next to the program)
              --no-browser          no app window and no browser on start (tests)
              --no-tray             no tray icon
              --verbose             more console output
            """;

        private static readonly string[] Flags = ["--verbose", "--no-browser", "--no-tray"];

        public static bool TryParse(string[] args, out Cli cli, out string error)
        {
            cli = null!;
            error = "";
            SessionMode? mode = null;
            var i = 0;
            string? code = null;
            if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[0] is not ("create" or "join"))
                {
                    error = $"Unknown command {args[0]}. Use create, join, or no command for the UI.";
                    return false;
                }
                mode = args[0] == "create" ? SessionMode.Create : SessionMode.Join;
                i = 1;
                if (mode == SessionMode.Join)
                {
                    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "join needs the 6-character code: crosscheck join <CODE>";
                        return false;
                    }
                    code = args[1];
                    i = 2;
                }
            }
            var opts = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Unexpected argument {key}";
                    return false;
                }
                if (Flags.Contains(key))
                {
                    opts[key] = "true";
                    continue;
                }
                if (i + 1 >= args.Length)
                {
                    error = $"{key} needs a value";
                    return false;
                }
                opts[key] = args[++i];
            }
            var known = new[] { "--server", "--profile", "--sim", "--api-port", "--log-dir", "--gql-poll-hz", "--airac", "--name", "--seat-side", "--ui-dir" };
            if (opts.Keys.FirstOrDefault(k => !known.Contains(k) && !Flags.Contains(k)) is { } unknown)
            {
                error = $"Unknown option {unknown}";
                return false;
            }
            var sim = opts.GetValueOrDefault("--sim") ?? "simconnect";
            if (sim is not ("simconnect" or "fake"))
            {
                error = "--sim must be simconnect or fake";
                return false;
            }
            SeatSide? side = opts.GetValueOrDefault("--seat-side") switch
            {
                null => null,
                "captain" => SeatSide.Captain,
                "fo" => SeatSide.Fo,
                _ => (SeatSide)(-1),
            };
            if (side is (SeatSide)(-1))
            {
                error = "--seat-side must be captain or fo";
                return false;
            }
            if (!Uri.TryCreate(opts.GetValueOrDefault("--server") ?? "ws://127.0.0.1:8787/ws", UriKind.Absolute, out var server))
            {
                error = "--server is not a valid URL";
                return false;
            }
            cli = new Cli(
                mode, code, server, ResolveProfile(opts.GetValueOrDefault("--profile")), sim == "fake",
                int.Parse(opts.GetValueOrDefault("--api-port") ?? "8790", System.Globalization.CultureInfo.InvariantCulture),
                opts.GetValueOrDefault("--log-dir"),
                double.Parse(opts.GetValueOrDefault("--gql-poll-hz") ?? "2", System.Globalization.CultureInfo.InvariantCulture),
                opts.GetValueOrDefault("--airac"), opts.GetValueOrDefault("--name"), opts.ContainsKey("--verbose"), side,
                opts.ContainsKey("--no-browser"), opts.ContainsKey("--no-tray"), opts.GetValueOrDefault("--ui-dir"));
            return true;
        }

        /// <summary>A given path as is; else the repo's Fenix profile, searched from here and from the program folder.</summary>
        private static string ResolveProfile(string? given)
        {
            if (given is not null) return Path.GetFullPath(given);
            const string relative = "profiles/fenix-a320/profile.yaml";
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return Path.GetFullPath(relative);
        }
    }

    /// <summary>Runs one bridge until Ctrl+C or Quit in the tray; then leaves the session.</summary>
    public sealed class BridgeService(Cli cli, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
    {
        private BridgeRuntime? _runtime;
        private DesktopShell? _shell;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var log = loggers.CreateLogger("crosscheck");
            var options = new BridgeOptions
            {
                Server = cli.Server, ProfilePath = cli.ProfilePath, Mode = cli.Mode, JoinCode = cli.Code, Name = cli.Name,
                ApiPort = cli.ApiPort, LogDir = cli.LogDir, Airac = cli.Airac, UiDir = cli.UiDir,
                Engine = new EngineOptions { SeatSide = cli.Side },
                VersionTimeout = cli.FakeSim ? TimeSpan.Zero : TimeSpan.FromSeconds(30),
            };
            _runtime = new BridgeRuntime(options, profile => cli.FakeSim
                ? BridgeBackends.Fake(profile)
                : new BridgeBackends(new SimConnectAdapter(profile, log), new FenixGraphQlClient(cli.GqlPollHz, log), new FenixManifestReader()), loggers);
            _runtime.Events.Published += e => log.LogInformation("[{Type}] {Message}", e.Type, e.Message);
            try
            {
                log.LogInformation("Profile {Profile}; server {Server}; sim {Sim}", cli.ProfilePath, cli.Server, cli.FakeSim ? "fake" : "SimConnect");
                await _runtime.StartAsync(ct);
                if (cli.Mode == SessionMode.Create)
                    log.LogInformation("Session created. Join code: {Code}   (other pilot: crosscheck join {Code})", _runtime.JoinCode, _runtime.JoinCode);
                else if (cli.Mode == SessionMode.Join)
                    log.LogInformation("Joined the session.");
                if (_runtime.Api is { } api)
                {
                    var url = $"http://127.0.0.1:{api.Port}/";
                    log.LogInformation("Crosscheck: {Url}", url);
                    // UI mode: the app window (WebView2), or the browser if WebView2 is missing.
                    // The URL also works in any normal browser on this computer (for debugging).
                    var window = cli.Mode is null && !cli.NoBrowser;
                    if (window || !cli.NoTray) _shell = DesktopShell.Start(url, window, !cli.NoTray, lifetime.StopApplication, log);
                }
                log.LogInformation("Bridge log: {Path}", _runtime.Log.Path);
                log.LogInformation("Commands: control | control @peer_lost | give | status{Fake}", cli.FakeSim ? " | set <VAR> <VALUE> | turn <VAR> <CLICKS> | press <VAR>" : "");
                await ConsoleLoop(log, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is ProfileInvalidException or JoinRejectedException or InvalidOperationException or IOException
                                          or System.Net.WebSockets.WebSocketException or TimeoutException)
            {
                log.LogError("{Message}", e.Message);
                Environment.ExitCode = 1;
                lifetime.StopApplication();
            }
        }

        private async Task ConsoleLoop(ILogger log, CancellationToken ct)
        {
            var runtime = _runtime!;
            while (!ct.IsCancellationRequested)
            {
                var line = await Task.Run(Console.ReadLine, ct).WaitAsync(ct);
                if (line is null)
                {
                    await Task.Delay(Timeout.Infinite, ct); // no console input (e.g. redirected): just keep running
                    return;
                }
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    switch (parts)
                    {
                        case ["control"]: Report(runtime.TakeControl(false)); break;
                        case ["control", "@peer_lost"]: Report(runtime.TakeControl(true)); break;
                        case ["give"]: Report(runtime.GiveControl()); break;
                        case ["status"]: log.LogInformation("{Status}", runtime.StatusJson().ToJsonString()); break;
                        case ["set", var name, var value] when runtime.Sim is FakeSimAdapter f: f.PilotSet(name, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case ["turn", var name, var clicks] when runtime.Sim is FakeSimAdapter f: f.PilotTurn(name, double.Parse(clicks, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case ["press", var name] when runtime.Sim is FakeSimAdapter f: _ = f.PilotPress(name); break;
                        case []: break;
                        default: log.LogInformation("Unknown command."); break;
                    }
                }
                catch (Exception e) when (e is FormatException or ArgumentException or KeyNotFoundException)
                {
                    log.LogWarning("{Message}", e.Message);
                }
            }

            void Report(Crosscheck.Bridge.Api.ApiResult r)
            {
                if (!r.Ok) log.LogWarning("{Error}", r.Error);
            }
        }

        public override async Task StopAsync(CancellationToken ct)
        {
            _shell?.Dispose();
            if (_runtime is not null) await _runtime.StopAsync(); // leave: frees the slot for the other pilot
            await base.StopAsync(ct);
        }
    }
}
