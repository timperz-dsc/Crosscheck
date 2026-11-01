// Progress file and raw results, so a run can resume after an interruption, and a var that was
// being probed when the process died is restored first on the next start.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProfileProbe;

enum RestoreKind { Absolute, PressAgain, Counter }

/// <summary>The var being probed right now and how to put it back.</summary>
sealed record Pending(string Var, Phase Phase, double Original, RestoreKind Kind, Dictionary<string, double> EffectStart);

sealed class CheckpointData
{
    public Pending? Pending { get; set; }
    public Dictionary<string, List<string>> Done { get; set; } = [];
    public List<string> NotRestored { get; set; } = [];
}

sealed class Checkpoint
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    readonly string _path;
    readonly object _gate = new();
    public CheckpointData Data { get; }

    public Checkpoint(string path)
    {
        _path = path;
        Data = File.Exists(path) ? JsonSerializer.Deserialize<CheckpointData>(File.ReadAllText(path), Json) ?? new() : new();
    }

    public bool IsDone(Phase phase, string var) => Data.Done.TryGetValue(phase.ToString(), out var l) && l.Contains(var);

    public void SetPending(Pending p) { lock (_gate) { Data.Pending = p; Save(); } }

    public void Finish(Phase phase, string var, bool restored)
    {
        lock (_gate)
        {
            Data.Pending = null;
            if (!Data.Done.TryGetValue(phase.ToString(), out var l)) Data.Done[phase.ToString()] = l = [];
            if (!l.Contains(var)) l.Add(var);
            if (!restored && !Data.NotRestored.Contains(var)) Data.NotRestored.Add(var);
            if (restored) Data.NotRestored.Remove(var);
            Save();
        }
    }

    public void ClearPending(bool restored, string var)
    {
        lock (_gate)
        {
            Data.Pending = null;
            if (!restored && !Data.NotRestored.Contains(var)) Data.NotRestored.Add(var);
            if (restored) Data.NotRestored.Remove(var);
            Save();
        }
    }

    /// <summary>The restore failed: keep the pending entry for the next start, and list the var.</summary>
    public void KeepPending(string var)
    {
        lock (_gate)
        {
            if (!Data.NotRestored.Contains(var)) Data.NotRestored.Add(var);
            Save();
        }
    }

    public void ResetPhase(Phase phase) { lock (_gate) { Data.Done.Remove(phase.ToString()); Save(); } }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
        // Another program reading the file (an editor, a monitor) can block the replace for a
        // moment. Retry instead of stopping the run in the middle of a var.
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, _path, overwrite: true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(50); }
        }
    }
}

/// <summary>One probe outcome. The proposal fields are what step 3 may apply.</summary>
sealed record ProbeResult
{
    public required string Var { get; init; }
    public required Phase Phase { get; init; }
    public required string Test { get; init; }
    /// <summary>Short outcome code, e.g. confirmed, rejected, pulse, latching, positions, unresolved.</summary>
    public required string Result { get; init; }
    public bool? StoredOk { get; init; }
    public bool? EffectOk { get; init; }
    public double? LatencyMs { get; init; }
    public string Notes { get; init; } = "";
    public double? Original { get; init; }
    public bool Restored { get; set; } = true;

    // proposal
    public string? ProposedClass { get; init; }
    public double[]? ProposedValues { get; init; }
    public string? ProposedVerifyLvar { get; init; }
    public string? ProposedVerifyGraphql { get; init; }
    public string? ProposedDisplay { get; init; }
    public bool ProposedCounter { get; init; }
    public Dictionary<string, string>? ProposedValueLabels { get; init; }

    /// <summary>Raw observations (lamps that changed, GraphQL values per position, ...).</summary>
    public Dictionary<string, string>? Data { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
}

/// <summary>Append-only JSON lines file. The last result per (var, test) wins.</summary>
sealed class ResultLog(string path)
{
    readonly object _gate = new();

    public void Add(ProbeResult r)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(r, Checkpoint.JsonLine) + "\n");
        }
    }

    public List<ProbeResult> ReadLatest()
    {
        if (!File.Exists(path)) return [];
        var all = File.ReadLines(path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<ProbeResult>(l, Checkpoint.Json)!).ToList();
        // A var that was probed and later put on a skip list keeps its probe result.
        return all.GroupBy(r => (r.Var, r.Test)).Select(g => g.LastOrDefault(r => r.Result != "skipped") ?? g.Last()).OrderBy(r => r.Phase).ThenBy(r => r.Var, StringComparer.Ordinal).ToList();
    }
}
