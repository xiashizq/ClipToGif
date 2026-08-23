using System.Diagnostics;
using System.IO;

namespace ClipToGif.Services;

public static class FfmpegLocator
{
    private static string? _cachedFfmpeg;
    private static string? _cachedFfprobe;

    public static string? Find() => FindTool("ffmpeg.exe", ref _cachedFfmpeg);

    public static string? FindFfprobe()
    {
        if (!string.IsNullOrWhiteSpace(_cachedFfprobe) && File.Exists(_cachedFfprobe))
            return _cachedFfprobe;

        var ffmpeg = Find();
        if (ffmpeg is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            if (File.Exists(sibling))
            {
                _cachedFfprobe = sibling;
                return sibling;
            }
        }

        return FindTool("ffprobe.exe", ref _cachedFfprobe);
    }

    private static string? FindTool(string fileName, ref string? cache)
    {
        if (!string.IsNullOrWhiteSpace(cache) && File.Exists(cache))
            return cache;

        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(baseDir, "ffmpeg", fileName));
        candidates.Add(Path.Combine(baseDir, fileName));
        candidates.Add(Path.Combine(baseDir, "tools", fileName));

        var envKey = fileName.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase)
            ? "FFPROBE_PATH"
            : "FFMPEG_PATH";
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ffmpeg", fileName));
        candidates.Add(projectDir);

        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        candidates.Add(Path.Combine(repoRoot, "tools", fileName));
        candidates.Add(Path.Combine(repoRoot, "tools", "ffmpeg", "bin", fileName));

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                cache = path;
                return path;
            }
        }

        try
        {
            var toolName = Path.GetFileNameWithoutExtension(fileName);
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(p => p.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (first is not null && File.Exists(first))
                {
                    cache = first;
                    return first;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static void ClearCache()
    {
        _cachedFfmpeg = null;
        _cachedFfprobe = null;
    }
}
