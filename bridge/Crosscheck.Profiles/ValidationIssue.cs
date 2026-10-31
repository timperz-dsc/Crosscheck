namespace Crosscheck.Profiles;

/// <summary>One problem in a profile file. <see cref="Line"/> is 1-based.</summary>
public sealed record ValidationIssue(int Line, string Path, string Message)
{
    public string Format(string source) =>
        string.IsNullOrEmpty(Path)
            ? $"{source}:{Line}: {Message}"
            : $"{source}:{Line}: {Path}: {Message}";

    public override string ToString() => Format("profile");
}
