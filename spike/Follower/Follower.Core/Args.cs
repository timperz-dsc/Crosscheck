using System.Globalization;

namespace Crosscheck.Follower;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal command line: positionals, `--key value` options and `--flag` switches. Unknown options are an error.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public Args(IEnumerable<string> argv, IReadOnlyCollection<string> flagNames)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                Positionals.Add(a);
                continue;
            }
            var key = a[2..];
            if (flagNames.Contains(key))
            {
                _flags.Add(key);
                continue;
            }
            if (i + 1 >= list.Count) throw new UsageException($"--{key} needs a value");
            _options[key] = list[++i];
        }
    }

    public List<string> Positionals { get; } = [];

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    public string? Str(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var v) ? v : null;
    }

    public double Num(string name, double def, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        var s = Str(name);
        if (s is null) return def;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            throw new UsageException($"--{name}: not a number: {s}");
        if (v < min || v > max) throw new UsageException($"--{name}: {v} is outside {min}..{max}");
        return v;
    }

    public double? OptNum(string name, double min = double.NegativeInfinity, double max = double.PositiveInfinity) =>
        Str(name) is null ? null : Num(name, 0, min, max);

    public T Enum<T>(string name, T def) where T : struct, Enum
    {
        var s = Str(name);
        if (s is null) return def;
        if (s.Equals("fd", StringComparison.OrdinalIgnoreCase) && typeof(T) == typeof(Tangents)) return (T)(object)Tangents.FiniteDifference;
        return System.Enum.TryParse<T>(s, ignoreCase: true, out var v) && System.Enum.IsDefined(v)
            ? v
            : throw new UsageException($"--{name}: one of {string.Join(", ", System.Enum.GetNames<T>()).ToLowerInvariant()}");
    }

    /// <summary>Throws if an option was given that the command never asked for (a typo would otherwise be silently ignored).</summary>
    public void EnsureAllUsed()
    {
        var unknown = _options.Keys.Concat(_flags).Where(k => !_used.Contains(k)).ToArray();
        if (unknown.Length > 0) throw new UsageException($"unknown option(s): {string.Join(", ", unknown.Select(k => "--" + k))}");
    }
}

public static class PlaybackArgs
{
    public static readonly string[] Flags = ["allow-air-end", "reorder"];

    public const string Help = """
          --method A|B|C            A: set position/attitude/velocity; B: A + freeze; C: slew (default A)
          --rate 20|30              stream rate in Hz (default 30; 1..60 accepted)
          --latency <ms>            one-way network latency (default 35)
          --jitter <ms>             jitter around the latency (default 10)
          --jitter-shape uniform|normal   (default uniform: latency ± jitter)
          --loss <percent>          lost packets (default 0)
          --burst <percent>         chance per packet that the link stalls for --burst-ms (default 0)
          --burst-ms <ms>           length of a stall; everything sent in it arrives at its end (default 150)
          --reorder                 let packets overtake each other (UDP-like; default: in order, like the WebSocket)
          --seed <n>                network random seed (default 1)
          --buffer <ms>             playout buffer (default 100)
          --extrapolate <ms>        longest dead reckoning when late, then hold (default 250)
          --blend <ms>              smooth the way back after a late stretch (default 200)
          --tangents velocity|fd    position curve from velocities or neighbouring frames (default velocity)
          --settle <s>              hold at the first frame before playing (default 3)
          --from <s> / --to <s>     play only part of the recording
          --allow-air-end           allow a playback that ends in the air
          --velocities body|world|none    velocities written with the position (default body; sim only)
          --spike-accel <m/s2>      jitter spike threshold (default 2)
          --spike-ang <deg/s2>      angular jitter spike threshold (default 20)
          --stick-neutral <x>       follower stick counts as neutral below this (default 0.05)
          --fenix-stick-scale <x>   full deflection of the Fenix N_FC_SIDESTICK_* vars (default 1)
          --surface-reaction <deg>  surface difference counted as the aircraft's own reaction (default 2)
          --out <dir>               where run folders go (default runs)
        """;

    public static PlaybackSettings Parse(Args a)
    {
        var settings = new PlaybackSettings
        {
            Method = a.Enum("method", Method.A),
            RateHz = a.Num("rate", 30, 1, 60),
            Network = new NetworkOptions
            {
                LatencyMs = a.Num("latency", 35, 0, 2000),
                JitterMs = a.Num("jitter", 10, 0, 1000),
                Shape = a.Enum("jitter-shape", JitterShape.Uniform),
                LossPct = a.Num("loss", 0, 0, 100),
                BurstPct = a.Num("burst", 0, 0, 100),
                BurstMs = a.Num("burst-ms", 150, 0, 5000),
                Seed = (int)a.Num("seed", 1, int.MinValue, int.MaxValue),
                AllowReorder = a.Flag("reorder"),
            },
            Playout = new PlayoutOptions
            {
                BufferMs = a.Num("buffer", 100, 0, 2000),
                MaxExtrapolationMs = a.Num("extrapolate", 250, 0, 5000),
                BlendMs = a.Num("blend", 200, 0, 5000),
                Tangents = a.Enum("tangents", Tangents.Velocity),
            },
            Metrics = new MetricsOptions
            {
                SpikeAccel = a.Num("spike-accel", 2, 0),
                SpikeAngAccel = a.Num("spike-ang", 20, 0),
                StickNeutral = a.Num("stick-neutral", 0.05, 0),
                FenixStickScale = a.Num("fenix-stick-scale", 1, 1e-9),
                SurfaceReactionDeg = a.Num("surface-reaction", 2, 0),
            },
            SettleS = a.Num("settle", 3, 0, 60),
            AllowAirEnd = a.Flag("allow-air-end"),
            FromS = a.OptNum("from", 0),
            ToS = a.OptNum("to", 0),
            Velocities = a.Enum("velocities", VelocityMode.Body),
        };
        return settings;
    }
}
