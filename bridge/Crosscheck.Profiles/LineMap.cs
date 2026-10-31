namespace Crosscheck.Profiles;

/// <summary>Maps JSON pointers ("/vars/3/name") to the line of that key or item in the YAML file.</summary>
internal sealed class LineMap
{
    private readonly Dictionary<string, int> _lines = new();

    public void Set(string pointer, int line) => _lines[pointer] = line;

    public bool Has(string pointer) => _lines.ContainsKey(pointer);

    /// <summary>Line of the pointer, or of its nearest ancestor that has one. 1 if none.</summary>
    public int Find(string pointer)
    {
        var p = pointer;
        while (true)
        {
            if (_lines.TryGetValue(p, out var line)) return line;
            var cut = p.LastIndexOf('/');
            if (cut < 0) return 1;
            p = p[..cut];
        }
    }

    public static string Child(string pointer, string key) =>
        pointer + "/" + key.Replace("~", "~0").Replace("/", "~1");

    public static string Child(string pointer, int index) => pointer + "/" + index;
}
