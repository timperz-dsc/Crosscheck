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
builder.Services.AddSingleton(cli);
builder.Services.AddHostedService<BridgeService>();
var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;

namespace Crosscheck.Bridge.App
{
    public sealed record Cli(
        SessionMode Mode, string? Code, Uri Server, string ProfilePath, bool FakeSim, int ApiPort,
        string? LogDir, double GqlPollHz, string? Airac, string? Name, bool Verbose, SeatSide? Side = null)
    {
        public const string Usage = """
            Usage:
              crosscheck create [options]
              crosscheck join <CODE> [options]

            Options:
              --server <url>        session server (default ws://127.0.0.1:8787/ws)
              --profile <file>      aircraft profile (default profiles/fenix-a320/profile.yaml)
              --sim simconnect|fake real sim (default) or an in-memory fake sim
              --name <text>         your name shown to the other pilot
              --api-port <n>        local API port on 127.0.0.1 (default 8790, -1 = off)
              --log-dir <dir>       bridge log folder (default %LOCALAPPDATA%\Crosscheck\logs)
              --gql-poll-hz <n>     Fenix GraphQL poll rate (default 2)
              --airac <cycle>       AIRAC cycle of your nav data, e.g. 2611
              --seat-side <side>    your cockpit seat: captain or fo (default: host captain, guest fo)
              --verbose             more console output
            """;

        public static bool TryParse(string[] args, out Cli cli, out string error)
        {
            cli = null!;
            error = "";
            if (args.Length == 0 || args[0] is not ("create" or "join"))
            {
                error = "First argument must be create or join.";
                return false;
            }
            var mode = args[0] == "create" ? SessionMode.Create : SessionMode.Join;
            var i = 1;
            string? code = null;
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
            var opts = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Unexpected argument {key}";
                    return false;
                }
                if (key == "--verbose")
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
            var known = new[] { "--server", "--profile", "--sim", "--api-port", "--log-dir", "--gql-poll-hz", "--airac", "--name", "--verbose", "--seat-side" };
            if (opts.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown)
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
                opts.GetValueOrDefault("--airac"), opts.GetValueOrDefault("--name"), opts.ContainsKey("--verbose"), side);
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

    /// <summary>Runs one bridge until Ctrl+C; then leaves the session.</summary>
    public sealed class BridgeService(Cli cli, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
    {
        private BridgeRuntime? _runtime;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var log = loggers.CreateLogger("crosscheck");
            var options = new BridgeOptions
            {
                Server = cli.Server, ProfilePath = cli.ProfilePath, Mode = cli.Mode, JoinCode = cli.Code, Name = cli.Name,
                ApiPort = cli.ApiPort, LogDir = cli.LogDir, Airac = cli.Airac,
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
                else
                    log.LogInformation("Joined the session.");
                if (_runtime.Api is { } api) log.LogInformation("Local API: http://127.0.0.1:{Port}/status", api.Port);
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
                        case ["control"]: runtime.Engine.RequestControl(); break;
                        case ["control", .. var confirm]: runtime.Engine.RequestControl(confirm); break;
                        case ["give"]: runtime.Engine.GiveControl(); break;
                        case ["status"]: log.LogInformation("{Status}", runtime.Status.ToJson(runtime.Engine.Counters).ToJsonString()); break;
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
        }

        public override async Task StopAsync(CancellationToken ct)
        {
            if (_runtime is not null) await _runtime.StopAsync(); // leave: frees the slot for the other pilot
            await base.StopAsync(ct);
        }
    }
}
