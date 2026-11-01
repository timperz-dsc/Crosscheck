using System.Collections.Concurrent;
using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Fenix;

/// <summary>
/// Fake Fenix backend on top of <see cref="FakeSimAdapter"/>: system.switches / indicators /
/// numerical mirror the sim's L: vars, system.analog shows the value x 1024, MCDU screens
/// come from the fake sim. aircraft.* datarefs come from <see cref="Datarefs"/>.
/// </summary>
public sealed class FakeFenixGraphQl : IFenixGraphQl
{
    public const string FakeSoftwareVersion = "3.0.0.330-fake";
    private readonly FakeSimAdapter _sim;

    public FakeFenixGraphQl(FakeSimAdapter sim)
    {
        _sim = sim;
        _sim.McduChanged += cdu => McduChanged?.Invoke(cdu);
    }

    /// <summary>false = Fenix not reachable (every read answers null).</summary>
    public bool Available { get; set; } = true;
    public string? SoftwareVersion { get; set; } = FakeSoftwareVersion;
    public ConcurrentDictionary<string, object?> Datarefs { get; } = new() { ["aircraft.fuel.left.amount.kg"] = 1500.0 };

    public event Action<int>? McduChanged;

    public Task StartAsync(IReadOnlyCollection<string> pollPaths, CancellationToken ct) => Task.CompletedTask;

    public object? Cached(string path) => Read(path);

    public Task<object?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult(Read(path));

    public Task<string?> SoftwareVersionAsync(CancellationToken ct = default) => Task.FromResult(Available ? SoftwareVersion : null);

    public McduScreen? Mcdu(int cdu) => Available ? McduScreen.FromText(_sim.McduText(cdu)) : null;

    private object? Read(string path)
    {
        if (!Available) return null;
        if (Datarefs.TryGetValue(path, out var v)) return v;
        var parts = path.Split('.');
        if (parts is ["system", var kind, var name])
        {
            try
            {
                var value = _sim.Get(name);
                return kind == "analog" ? value * 1024 : value;
            }
            catch (KeyNotFoundException) { return null; }
            catch (ArgumentException) { return null; }
        }
        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
