// CRO-17 follower spike, Windows host. Offline use with your own sim only: see
// docs/spikes/CRO-17-follower.md. Not part of the bridge and never referenced by it.
//   follower-sim record <out.cxrec> [--note text]   record the user aircraft (read-only)
//   follower-sim play <rec.cxrec> [options]         play it into this sim as the follower
//   follower-sim restore                            switch every freeze and slew off

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Crosscheck.Follower;
using Crosscheck.Follower.Sim;
using Microsoft.FlightSimulator.SimConnect;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("follower-sim needs Windows and MSFS. For offline runs use `follower simulate`.");
    return 1;
}

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Usage();
        return 0;
    }
    var rest = args.Skip(1);
    switch (args[0])
    {
        case "record":
        {
            var a = new Args(rest, []);
            var note = a.Str("note") ?? "";
            a.EnsureAllUsed();
            var path = a.Positionals.SingleOrDefault() ?? throw new UsageException("record <out.cxrec>");
            if (File.Exists(path)) throw new UsageException($"{path} exists; choose another name");
            using var stop = StopSignal.Install();
            return new Recorder(path, note).Run(stop);
        }
        case "play":
        {
            var a = new Args(rest, PlaybackArgs.Flags);
            var settings = PlaybackArgs.Parse(a);
            var outRoot = a.Str("out") ?? "runs";
            a.EnsureAllUsed();
            var path = a.Positionals.SingleOrDefault() ?? throw new UsageException("play <rec.cxrec> [options]");
            var rec = RecordingFormat.Load(path);
            var session = new PlaybackSession(rec, settings); // refuses a recording that ends in the air unless allowed

            var clients = NetworkClients.Find(Process.GetProcesses().Select(p => p.ProcessName));
            if (clients.Count > 0)
            {
                Console.Error.WriteLine($"refusing to start: a network pilot client is running ({string.Join(", ", clients)}). Close it and make sure you are offline.");
                return 1;
            }
            var s = session.Start;
            Console.WriteLine($"recording: {path}, {session.Recording.Duration:0} s, \"{rec.Header.AircraftTitle}\"");
            Console.WriteLine($"your aircraft will be moved to {s.Lat:0.000000}, {s.Lon:0.000000}, {s.AltFt:0} ft, heading {s.HeadingDeg:0} (start of the recording)");
            Console.WriteLine($"method {settings.Method}: " + settings.Method switch
            {
                Method.A => "sets position, attitude and velocities every frame",
                Method.B => "as A, plus the sim's freeze lat/lon, altitude and attitude",
                _ => "slew mode, setting position and attitude every frame",
            });
            if (settings.Method != Method.C) Console.WriteLine($"velocities written: {settings.Velocities.ToString().ToLowerInvariant()}");
            Console.WriteLine($"clock: {session.Clock.Source} ({session.Clock.Why}); frames dropped: {session.Clock.DroppedDuplicates} repeated, {session.Clock.DroppedNotAdvancing} with a clock that did not move");
            if (session.HandoverAt is { } at)
            {
                var h = Interpolator.Sample(session.Recording.Frames, at);
                Console.WriteLine($"HANDOVER TEST at {settings.HandoverTestS:0.0} s: the freezes are released while taxiing at about {h.GroundSpeedKt:0} kt.");
                Console.WriteLine($"From then on the aircraft is YOURS and rolling: be ready to brake and steer. The run ends {HandoverTest.LogS:0} s later.");
            }
            else Console.WriteLine(settings.AllowAirEnd ? "the playback may END IN THE AIR (--allow-air-end): be ready to fly" : "the playback ends stopped on the ground");
            Console.WriteLine();
            Console.Write(OfflineConfirmation.Prompt);
            if (!OfflineConfirmation.Accept(Console.ReadLine()))
            {
                Console.WriteLine("not confirmed; nothing was sent to the sim");
                return 1;
            }
            using var stop = StopSignal.Install();
            return new Player(session, outRoot, Path.GetFileName(path)).Run(stop);
        }
        case "restore":
        {
            var a = new Args(rest, []);
            a.EnsureAllUsed();
            return Restore();
        }
        default:
            throw new UsageException($"unknown command {args[0]}");
    }
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}\nrun `follower-sim help` for usage");
    return 2;
}
catch (Exception e) when (e is RecordingFormatException or IOException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static int Restore()
{
    var wake = new AutoResetEvent(false);
    var sc = SimLink.TryConnect("Crosscheck Follower Restore", wake);
    if (sc is null)
    {
        Console.Error.WriteLine("sim not running; nothing to restore");
        return 1;
    }
    try
    {
        FlagsData? flags = null;
        sc.OnRecvSimobjectData += (_, d) =>
        {
            if (d.dwRequestID == (uint)Req.Flags) flags = (FlagsData)d.dwData[0];
        };
        SimLink.MapAllowedEvents(sc);
        SimLink.RequestFlags(sc);
        foreach (var (ev, data) in AllowedEvents.RestoreAll) SimLink.Send(sc, ev, data);
        Console.WriteLine($"sent: {string.Join(", ", AllowedEvents.RestoreAll.Select(r => r.Event))}");
        var deadline = Stopwatch.StartNew();
        FlagsData last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            wake.WaitOne(20);
            sc.ReceiveMessage();
            if (flags is { } f) last = f;
        }
        if (flags is null)
        {
            Console.WriteLine("could not read the freeze/slew state back");
            return 1;
        }
        var allOff = last.LatLon < 0.5 && last.Altitude < 0.5 && last.Attitude < 0.5 && last.Slew < 0.5;
        Console.WriteLine(allOff ? "sim reports every freeze and slew off" : $"still on: lat/lon {last.LatLon}, altitude {last.Altitude}, attitude {last.Attitude}, slew {last.Slew}");
        return allOff ? 0 : 1;
    }
    catch (COMException e)
    {
        Console.Error.WriteLine($"sim connection failed: {e.Message}");
        return 1;
    }
    finally
    {
        try { sc.Dispose(); } catch (COMException) { }
    }
}

static void Usage()
{
    Console.WriteLine("""
        follower-sim: CRO-17 follower spike on the sim PC (offline, your own sim only)
        Read docs/spikes/CRO-17-follower.md first.

          follower-sim record <out.cxrec> [--note text]
              Records your aircraft every sim frame. Read-only. Fly a full circuit and stop on the ground.
          follower-sim play <rec.cxrec> [options]
              Plays the recording into this sim as the follower. Asks you to confirm you are offline.
        """ + PlaybackArgs.Help + """

          follower-sim restore
              Switches every freeze and slew off (if a run was killed before it could).

        Stop a recording or playback with S or Esc, or Ctrl+C. Freeze and slew are always switched off again.
        """);
}

/// <summary>The stop key (S or Esc) and Ctrl+C. Set once; tells why.</summary>
sealed class StopSignal : IDisposable
{
    private readonly ManualResetEventSlim _set = new(false);
    private readonly CancellationTokenSource _keys = new();
    private volatile string _reason = "";

    public bool IsSet => _set.IsSet;
    public string Reason => _reason;

    public void Wait(TimeSpan t) => _set.Wait(t);

    public void Stop(string reason)
    {
        if (_set.IsSet) return;
        _reason = reason;
        _set.Set();
    }

    public static StopSignal Install()
    {
        var s = new StopSignal();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // end cleanly through the normal release path
            s.Stop("Ctrl+C");
        };
        if (!Console.IsInputRedirected)
        {
            var t = new Thread(() =>
            {
                while (!s._keys.IsCancellationRequested && !s.IsSet)
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(intercept: true).Key;
                        if (k is ConsoleKey.S or ConsoleKey.Escape) s.Stop($"{k} key");
                    }
                    else Thread.Sleep(30);
                }
            }) { IsBackground = true, Name = "stop key" };
            t.Start();
        }
        return s;
    }

    public void Dispose() => _keys.Cancel();
}
