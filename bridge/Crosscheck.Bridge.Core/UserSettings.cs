using System.Text;
using System.Text.Json.Nodes;

namespace Crosscheck.Bridge;

/// <summary>
/// What the pilot sets in the UI and Crosscheck remembers: %LOCALAPPDATA%\Crosscheck\settings.json.
/// A missing or broken file means defaults; reading never throws.
/// </summary>
public sealed record UserSettings(string? Server)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crosscheck", "settings.json");

    public static UserSettings Load(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o ? new UserSettings(o["server"]?.GetValue<string>()) : new UserSettings((string?)null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new UserSettings((string?)null);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject { ["server"] = Server };
        File.WriteAllText(path, json.ToJsonString(new() { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }
}

/// <summary>The session server address: default, and turning what a pilot types into a usable URL.</summary>
public static class ServerAddress
{
    public const string Default = "ws://127.0.0.1:8787/ws";

    /// <summary>
    /// Accepts ws://, wss://, http://, https:// or just a host name (wss, except localhost: ws).
    /// An empty path becomes /ws.
    /// </summary>
    public static bool TryNormalize(string? input, out Uri uri, out string error)
    {
        uri = null!;
        error = "Type the address of the Crosscheck server, for example wss://crosscheck-production.up.railway.app/ws";
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var local = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.", StringComparison.Ordinal);
            text = (local ? "ws://" : "wss://") + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            error = "This is not a valid address.";
            return false;
        }
        var scheme = parsed.Scheme switch { "http" => "ws", "https" => "wss", var s => s };
        if (scheme is not ("ws" or "wss"))
        {
            error = "The address must start with wss:// (or ws:// for a server on this computer).";
            return false;
        }
        var builder = new UriBuilder(parsed) { Scheme = scheme, Port = parsed.IsDefaultPort ? -1 : parsed.Port };
        if (parsed.AbsolutePath == "/") builder.Path = "/ws";
        uri = builder.Uri;
        return true;
    }
}
