using System.Security.Cryptography;
using System.Text;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>One instance for each normalized data profile, without exposing its path in the mutex name.</summary>
public static class SingleInstanceProfile
{
    public static string? DataDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--data");
        if (index < 0) return null;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--data requires a profile folder.");
        return Normalize(args[index + 1]);
    }

    public static string MutexName(string? dataDirectory)
    {
        const string prefix = @"Local\ALMARFELD.SubFlow.SingleInstance";
        if (dataDirectory is null) return prefix;
        var bytes = Encoding.UTF8.GetBytes(Normalize(dataDirectory).ToUpperInvariant());
        return prefix + "." + Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
