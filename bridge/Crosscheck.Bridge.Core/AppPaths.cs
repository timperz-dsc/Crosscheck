namespace Crosscheck.Bridge;

/// <summary>
/// Where Crosscheck keeps the pilot's own data: settings, logs, the WebView2 profile.
/// This is %LOCALAPPDATA%\CrosscheckData, on purpose not %LOCALAPPDATA%\Crosscheck: the installer
/// (Velopack) owns that folder (current\, packages\, Update.exe) and removes it on uninstall.
/// </summary>
public static class AppPaths
{
    public const string DataFolderName = "CrosscheckData";
    public const string InstallFolderName = "Crosscheck";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDir => Path.Combine(LocalAppData, DataFolderName);
    public static string OldDataDir => Path.Combine(LocalAppData, InstallFolderName);
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string WebView2Dir => Path.Combine(DataDir, "WebView2");

    /// <summary>
    /// One-time move of settings.json, logs and WebView2 from the old data folder (%LOCALAPPDATA%\Crosscheck)
    /// into <paramref name="newDir"/>. Only these three items are moved, never the installer's own files.
    /// Safe to repeat; an item already present in the new folder is left alone. Never throws.
    /// </summary>
    public static IReadOnlyList<string> MigrateOldData(string oldDir, string newDir)
    {
        var moved = new List<string>();
        try
        {
            if (!Directory.Exists(oldDir)) return moved;
            Directory.CreateDirectory(newDir);
            foreach (var name in new[] { "settings.json", "logs", "WebView2" })
            {
                var from = Path.Combine(oldDir, name);
                var to = Path.Combine(newDir, name);
                try
                {
                    if (Directory.Exists(to) || File.Exists(to)) continue;
                    if (File.Exists(from)) File.Move(from, to);
                    else if (Directory.Exists(from)) Directory.Move(from, to);
                    else continue;
                    moved.Add(name);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked or in use (for example WebView2 of a running copy): leave it, start fresh.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return moved;
    }

    public static void MigrateOldData() => MigrateOldData(OldDataDir, DataDir);
}
