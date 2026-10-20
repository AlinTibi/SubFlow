using ALMARFELD.SubFlow.Core.Jobs;

namespace ALMARFELD.SubFlow.Core.Processing;

public static class OutputPlanner
{
    public const string DefaultOutputFolderName = "SubFlow Output";
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm"];

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder that receives the output for a source file (created on demand by the caller).</summary>
    public static string GetOutputDirectory(string sourcePath, string rootPath, ProcessingOptions options)
    {
        var source = Path.GetFullPath(sourcePath);
        var root = Path.GetFullPath(rootPath);
        var baseFolder = string.IsNullOrWhiteSpace(options.OutputFolder)
            ? Path.Combine(root, DefaultOutputFolderName)
            : Path.GetFullPath(options.OutputFolder!);

        var sourceDir = Path.GetDirectoryName(source)!;
        var relative = "";
        if (options.PreserveFolderStructure && IsUnder(sourceDir, root))
        {
            relative = Path.GetRelativePath(root, sourceDir);
            if (relative == ".") relative = "";
        }
        // Re-processing files that already live in the output folder must not nest "SubFlow Output" again.
        if (IsUnder(sourceDir, baseFolder)) return sourceDir;
        return string.IsNullOrEmpty(relative) ? baseFolder : Path.Combine(baseFolder, relative);
    }

    /// <summary>
    /// The MKV output path. Guaranteed to differ from the source path: if the user picked the source
    /// folder as output, a ".subflow" suffix is added so the original is never overwritten.
    /// </summary>
    public static string GetOutputVideoPath(string sourcePath, string rootPath, ProcessingOptions options)
    {
        var dir = GetOutputDirectory(sourcePath, rootPath, options);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var output = Path.Combine(dir, name + ".mkv");
        if (PathsEqual(output, sourcePath)) output = Path.Combine(dir, name + ".subflow.mkv");
        return output;
    }

    public static string GetSubtitleFilePath(string outputDirectory, string sourcePath, string languageCode, string extension) =>
        Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{languageCode}{extension}");

    /// <summary>"Episode.mkv" → "Episode (2).mkv", "Episode (3).mkv", … until free.</summary>
    public static string NextFreeName(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Expands dropped files and folders (recursively) into video files with their import root.</summary>
    public static IEnumerable<(string File, string Root)> ExpandInputs(IEnumerable<string> paths, bool recursive = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = recursive,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System,
                    });
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var f in files.Where(IsVideoFile).Order(StringComparer.OrdinalIgnoreCase))
                {
                    // Skip our own outputs and temporary files.
                    if (f.Contains(".subflow-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.Split(Path.DirectorySeparatorChar).Contains(DefaultOutputFolderName, StringComparer.OrdinalIgnoreCase)) continue;
                    if (seen.Add(f)) yield return (f, path);
                }
            }
            else if (File.Exists(path) && IsVideoFile(path))
            {
                if (seen.Add(path)) yield return (path, Path.GetDirectoryName(path)!);
            }
        }
    }
}
