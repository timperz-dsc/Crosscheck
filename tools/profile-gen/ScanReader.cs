// Reads the scanner's CSV: "# comment" header lines, then name,value,prefix,class,class_reason.

namespace ProfileGen;

sealed record ScanVar(string Name, string Value, string ScanClass)
{
    /// <summary>The scan value as a number, if it is one.</summary>
    public double? Number =>
        double.TryParse(Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}

static class ScanReader
{
    public static List<ScanVar> Read(string path)
    {
        var result = new List<ScanVar>();
        var header = true;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            if (header) { header = false; continue; } // name,value,prefix,class,class_reason
            var cols = line.Split(',', 5);
            if (cols.Length < 4) continue;
            result.Add(new ScanVar(cols[0], cols[1], cols[3]));
        }
        return result;
    }
}
