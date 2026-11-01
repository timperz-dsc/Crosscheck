using System.Text;
using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Logging;

/// <summary>
/// Local JSONL log in the same line format as the server's session log
/// (t, time, dir in|out|event, msg or event). Ping, pong and stream are not logged; resume
/// tokens are redacted. Writing never throws: logging must not break a flight.
/// </summary>
public sealed class BridgeLog
{
    private static readonly HashSet<string> NotLogged = ["ping", "pong", "stream"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private bool _failed;

    public string? Path { get; }

    public BridgeLog(string? directory)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"bridge-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
    }

    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crosscheck", "logs");

    public void Message(string dir, JsonObject msg)
    {
        if (msg["type"]?.GetValue<string>() is { } type && NotLogged.Contains(type)) return;
        Write(new JsonObject { ["dir"] = dir, ["msg"] = Redact(msg) });
    }

    public void Event(string name, JsonObject? data = null)
    {
        var line = new JsonObject { ["dir"] = "event", ["event"] = name };
        if (data is not null)
            foreach (var (k, v) in data) line[k] = v?.DeepClone();
        Write(line);
    }

    private void Write(JsonObject line)
    {
        if (Path is null) return;
        var now = DateTimeOffset.UtcNow;
        var record = new JsonObject { ["t"] = now.ToUnixTimeMilliseconds(), ["time"] = now.ToString("O") };
        foreach (var (k, v) in line) record[k] = v?.DeepClone();
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path, record.ToJsonString() + "\n", Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!_failed) Console.Error.WriteLine($"bridge log {Path}: {e.Message}");
                _failed = true;
            }
        }
    }

    private static JsonObject Redact(JsonObject msg)
    {
        var copy = (JsonObject)msg.DeepClone();
        if (copy["body"] is JsonObject body && body.ContainsKey("resume_token")) body["resume_token"] = "<redacted>";
        return copy;
    }
}
