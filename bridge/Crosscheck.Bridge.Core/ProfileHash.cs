using System.Security.Cryptography;

namespace Crosscheck.Bridge;

/// <summary>
/// The profile hash both pilots compare in preflight. Line endings are normalised first
/// (CRLF to LF), so the same profile gives the same hash on every machine, whatever git's
/// autocrlf setting did to the file on checkout.
/// </summary>
public static class ProfileHash
{
    public static string Of(byte[] fileBytes)
    {
        var lf = new byte[fileBytes.Length];
        var n = 0;
        for (var i = 0; i < fileBytes.Length; i++)
        {
            if (fileBytes[i] == '\r' && i + 1 < fileBytes.Length && fileBytes[i + 1] == '\n') continue;
            lf[n++] = fileBytes[i];
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(lf.AsSpan(0, n))).ToLowerInvariant();
    }
}
