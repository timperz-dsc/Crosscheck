// deflimit (CRO-40, owner approved): how many SimConnect data definitions does the sim accept from one client?
// Registration only: it adds one existing read-only Fenix lamp var to definitions 0, 1, 2 … and collects the
// exceptions SimConnect sends back. It never writes a value, never requests data, sends no events and maps
// nothing; afterwards it clears every definition it made and disconnects. Gate test 3 cause A: the bridge's
// writes past roughly 900–1,190 definitions never landed; this finds the exact number.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ProfileProbe;

/// <summary>The only SimConnect calls deflimit makes: define, clear, and collect the answers.</summary>
interface IDefinitionRegistrar
{
    /// <summary>Adds the var to this definition; returns the call's send ID.</summary>
    uint Add(int definition);
    uint Clear(int definition);
    /// <summary>Exceptions SimConnect sent back within <paramref name="wait"/>: (send ID, exception name).</summary>
    IReadOnlyList<(uint SendId, string Exception)> Drain(TimeSpan wait);
}

sealed record DefLimitResult(int Tried, int? FirstRefused, int Refused, IReadOnlyDictionary<string, int> Exceptions, int Cleared);

static class DefLimit
{
    /// <summary>A Fenix lamp that exists in every cockpit state (EXT PWR ON); only registered, never written or read.</summary>
    public const string Var = "I_OH_ELEC_EXT_PWR_L";
    public const int MaxDefinitions = 2000;

    /// <summary>Registers definitions 0 … max-1, then clears them. The first refused definition is the limit.</summary>
    public static DefLimitResult Probe(IDefinitionRegistrar r, int max, TimeSpan settle)
    {
        var sends = new Dictionary<uint, int>();
        var refused = new List<(int Definition, string Exception)>();
        void Collect(TimeSpan wait)
        {
            foreach (var (id, ex) in r.Drain(wait))
                if (sends.TryGetValue(id, out var def)) refused.Add((def, ex));
        }
        for (var def = 0; def < max; def++)
        {
            sends[r.Add(def)] = def;
            if (def % 50 == 49) Collect(TimeSpan.Zero);
        }
        Collect(settle);
        var cleared = 0;
        for (var def = 0; def < max; def++)
        {
            r.Clear(def);
            cleared++;
            if (def % 50 == 49) r.Drain(TimeSpan.Zero);
        }
        r.Drain(TimeSpan.FromMilliseconds(200));
        return new DefLimitResult(max, refused.Count > 0 ? refused.Min(x => x.Definition) : null, refused.Count,
            refused.GroupBy(x => x.Exception).ToDictionary(g => g.Key, g => g.Count()), cleared);
    }

    public static string Report(DefLimitResult r) =>
        r.FirstRefused is { } first
            ? $"SimConnect accepted {first} data definitions from this client; definition {first} and {r.Refused - 1} after it were refused " +
              $"({string.Join(", ", r.Exceptions.Select(e => $"{e.Key} ×{e.Value}"))}). Tried {r.Tried}; all {r.Cleared} cleared again."
            : $"SimConnect accepted all {r.Tried} data definitions (no exception). All {r.Cleared} cleared again.";

    public static async Task<int> Run(string resultsPath, TextWriter output, CancellationToken ct)
    {
        output.WriteLine($"""

            deflimit: registration only. It adds the lamp var {Var} to data definitions 0 … {MaxDefinitions - 1}, waits for
            SimConnect's answers, then clears every definition and disconnects. Nothing is written to the sim, no data is
            requested, no event is sent. The sim should be at the gate with the Fenix loaded (any state).
            Enter = start, q = stop
            """);
        output.Write("> ");
        if ((await Task.Run(Console.ReadLine, ct))?.Trim().ToLowerInvariant() is null or "q") { output.WriteLine("stopped"); return 0; }
        using var registrar = SimConnectRegistrar.Connect(TimeSpan.FromSeconds(60), ct);
        if (registrar is null) { output.WriteLine("the sim (SimConnect) did not answer within 60 s; nothing done"); return 1; }
        output.WriteLine($"connected to {registrar.Application}; registering...");
        var result = Probe(registrar, MaxDefinitions, TimeSpan.FromSeconds(3));
        var line = Report(result);
        output.WriteLine(line);
        Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
        File.AppendAllText(resultsPath, $"# SimConnect definition limit, {DateTime.Now:yyyy-MM-dd HH:mm}\n\n{line}\n\n");
        output.WriteLine($"written: {resultsPath}");
        return 0;
    }
}

/// <summary>A SimConnect client of its own that can only define and clear data definitions.</summary>
sealed class SimConnectRegistrar : IDefinitionRegistrar, IDisposable
{
    readonly SC _sc;
    readonly AutoResetEvent _wake;
    readonly ConcurrentQueue<(uint, string)> _exceptions = new();
    public string Application { get; private set; } = "?";
    bool _open;

    SimConnectRegistrar(SC sc, AutoResetEvent wake)
    {
        _sc = sc;
        _wake = wake;
        sc.OnRecvOpen += (_, d) => { Application = $"{d.szApplicationName} {d.dwApplicationVersionMajor}.{d.dwApplicationVersionMinor}"; _open = true; };
        sc.OnRecvException += (_, d) => _exceptions.Enqueue((d.dwSendID, ((SIMCONNECT_EXCEPTION)d.dwException).ToString()));
    }

    public static SimConnectRegistrar? Connect(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            var wake = new AutoResetEvent(false);
            try
            {
                var r = new SimConnectRegistrar(new SC("Crosscheck Profile Probe deflimit (registration only)", IntPtr.Zero, 0, wake, 0), wake);
                r.Drain(TimeSpan.FromSeconds(5));
                if (r._open) return r;
                r.Dispose();
            }
            catch (COMException) { Thread.Sleep(2000); }
        }
        return null;
    }

    public uint Add(int definition)
    {
        _sc.AddToDataDefinition((Def)definition, "L:" + DefLimit.Var, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        return _sc.GetLastSentPacketID();
    }

    public uint Clear(int definition)
    {
        _sc.ClearDataDefinition((Def)definition);
        return _sc.GetLastSentPacketID();
    }

    public IReadOnlyList<(uint SendId, string Exception)> Drain(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        do
        {
            // ReceiveMessage hands over one message per call; up to a thousand exceptions may be queued.
            for (var i = 0; i < 200; i++) _sc.ReceiveMessage();
            _wake.WaitOne(10);
        } while (DateTime.UtcNow < until);
        var list = new List<(uint, string)>();
        while (_exceptions.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public void Dispose()
    {
        try { _sc.Dispose(); } catch (COMException) { }
        _wake.Dispose();
    }

    enum Def { }
}
