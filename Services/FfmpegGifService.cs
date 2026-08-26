using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipToGif.Localization;
using ClipToGif.Models;

namespace ClipToGif.Services;

public sealed class GifConversionRequest
{
    public required string VideoPath { get; init; }
    public required string OutputPath { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required GifExportSettings Settings { get; init; }
    public IProgress<double>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class VideoMediaInfo
{
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
}

public sealed class FfmpegGifService
{
    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeRegex = new(
        @"time=(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VideoStreamRegex = new(
        @"Video:.*?(?<w>\d{2,5})x(?<h>\d{2,5}).*?(?<fps>\d+(?:\.\d+)?)\s*(?:fps|tbr)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task<TimeSpan> GetDurationAsync(string videoPath, CancellationToken ct = default)
    {
        var info = await GetMediaInfoAsync(videoPath, ct);
        return info.Duration;
    }

    public async Task<VideoMediaInfo> GetMediaInfoAsync(string videoPath, CancellationToken ct = default)
    {
        var ffprobe = FfmpegLocator.FindFfprobe();
        if (ffprobe is not null)
        {
            try
            {
                return await ProbeWithFfprobeAsync(ffprobe, videoPath, ct);
            }
            catch
            {
                // fall back to ffmpeg -i
            }
        }

        return await ProbeWithFfmpegAsync(videoPath, ct);
    }

    private static async Task<VideoMediaInfo> ProbeWithFfprobeAsync(
        string ffprobe, string videoPath, CancellationToken ct)
    {
        var args =
            $"-v error -select_streams v:0 " +
            $"-show_entries stream=width,height,avg_frame_rate,r_frame_rate:format=duration " +
            $"-of json \"{videoPath}\"";

        var psi = CreateProcess(ffprobe, args);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(Loc.Get("CannotStartFfprobe"));
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(Loc.Get("FfprobeFailed"));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var stream = root.GetProperty("streams")[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();

        var fps = ParseFrameRate(stream, "avg_frame_rate");
        if (fps <= 0)
            fps = ParseFrameRate(stream, "r_frame_rate");
        if (fps <= 0)
            fps = 10;

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durEl) &&
            double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            duration = TimeSpan.FromSeconds(secs);
        }

        return new VideoMediaInfo
        {
            Width = width,
            Height = height,
            Fps = Math.Round(fps, 3),
            Duration = duration
        };
    }

    private static double ParseFrameRate(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var el))
            return 0;
        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text) || text is "0/0" or "N/A")
            return 0;

        var parts = text.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den > 0)
        {
            return num / den;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
            ? fps
            : 0;
    }

    private async Task<VideoMediaInfo> ProbeWithFfmpegAsync(string videoPath, CancellationToken ct)
    {
        var ffmpeg = RequireFfmpeg();
        var psi = CreateProcess(ffmpeg, $"-i \"{videoPath}\"");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(Loc.Get("CannotStartFfmpeg"));
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var durationMatch = DurationRegex.Match(stderr);
        if (!durationMatch.Success)
            throw new InvalidOperationException(Loc.Get("CannotReadDuration"));

        var duration = ParseClock(durationMatch);
        var width = 480;
        var height = 0;
        var fps = 10.0;

        var videoMatch = VideoStreamRegex.Match(stderr);
        if (videoMatch.Success)
        {
            width = int.Parse(videoMatch.Groups["w"].Value, CultureInfo.InvariantCulture);
            height = int.Parse(videoMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            fps = double.Parse(videoMatch.Groups["fps"].Value, CultureInfo.InvariantCulture);
        }

        return new VideoMediaInfo
        {
            Duration = duration,
            Width = width,
            Height = height,
            Fps = Math.Round(fps, 3)
        };
    }

    public async Task ConvertAsync(GifConversionRequest request)
    {
        if (request.End <= request.Start)
            throw new ArgumentException("结束时间必须大于开始时间。");

        var ffmpeg = RequireFfmpeg();
        var settings = request.Settings;
        var duration = request.End - request.Start;
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        ResolveCompression(settings,
            out var maxColors,
            out var dither,
            out var statsMode,
            out var paletteUseExtra,
            out var extraFilters,
            out var outputFlags);

        var scale = settings.Height <= 0
            ? $"scale={settings.Width}:-1:flags=lanczos"
            : $"scale={settings.Width}:{settings.Height}:flags=lanczos";

        var fps = settings.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        var start = FormatSeconds(request.Start);
        var dur = FormatSeconds(duration);

        var filter =
            $"fps={fps},{extraFilters}{scale},split[s0][s1];" +
            $"[s0]palettegen=max_colors={maxColors}:stats_mode={statsMode}[p];" +
            $"[s1][p]paletteuse=dither={dither}{paletteUseExtra}";

        var args =
            $"-y -ss {start} -t {dur} -i \"{request.VideoPath}\" " +
            $"-vf \"{filter}\" {outputFlags}-loop 0 \"{request.OutputPath}\"";

        var psi = CreateProcess(ffmpeg, args);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(Loc.Get("CannotStartFfmpeg"));

        var stderrBuilder = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrBuilder.AppendLine(e.Data);
            var m = TimeRegex.Match(e.Data);
            if (!m.Success || request.Progress is null) return;

            var current = ParseClock(m);
            var ratio = Math.Clamp(current.TotalSeconds / Math.Max(duration.TotalSeconds, 0.001), 0, 1);
            request.Progress.Report(ratio);
        };
        proc.BeginErrorReadLine();

        await using (request.CancellationToken.Register(() =>
                     {
                         try
                         {
                             if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                         }
                         catch
                         {
                             // ignore
                         }
                     }))
        {
            try
            {
                await proc.WaitForExitAsync(request.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                throw;
            }
        }

        if (request.CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(request.CancellationToken);

        if (proc.ExitCode != 0)
        {
            var msg = stderrBuilder.ToString();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg)
                ? Loc.Format("FfmpegExitFailed", proc.ExitCode)
                : msg.Split('\n').Reverse().FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                  ?? Loc.Format("FfmpegExitFailed", proc.ExitCode));
        }

        if (!File.Exists(request.OutputPath))
            throw new InvalidOperationException(Loc.Get("GifNotCreated"));

        request.Progress?.Report(1);
    }

    private static int QualityToColors(int quality) => quality switch
    {
        <= 2 => 256,
        <= 4 => 192,
        <= 6 => 128,
        <= 8 => 96,
        _ => 64
    };

    private static void ResolveCompression(
        GifExportSettings settings,
        out int maxColors,
        out string dither,
        out string statsMode,
        out string paletteUseExtra,
        out string extraFilters,
        out string outputFlags)
    {
        maxColors = QualityToColors(settings.Quality);
        dither = settings.Quality <= 4 ? "sierra2_4a" : "bayer:bayer_scale=3";
        statsMode = "full";
        paletteUseExtra = "";
        extraFilters = "";
        outputFlags = "";

        switch (settings.Compression)
        {
            case GifCompressionMode.None:
                break;

            case GifCompressionMode.LosslessTransdiff:
                maxColors = 256;
                dither = "sierra2_4a";
                statsMode = "full";
                outputFlags = "-gifflags +transdiff ";
                break;

            case GifCompressionMode.LosslessRectangle:
                maxColors = 256;
                dither = "none";
                statsMode = "full";
                paletteUseExtra = ":diff_mode=rectangle";
                outputFlags = "-gifflags +transdiff ";
                break;

            case GifCompressionMode.LosslessPaletteDiff:
                maxColors = 256;
                dither = "none";
                statsMode = "diff";
                paletteUseExtra = ":diff_mode=rectangle";
                outputFlags = "-gifflags +transdiff ";
                break;

            case GifCompressionMode.LossyBayer:
                dither = "bayer:bayer_scale=5";
                statsMode = "diff";
                paletteUseExtra = ":diff_mode=rectangle";
                outputFlags = "-gifflags +transdiff ";
                break;

            case GifCompressionMode.LossyFloydSteinberg:
                dither = "floyd_steinberg";
                statsMode = "diff";
                outputFlags = "-gifflags +transdiff ";
                break;

            case GifCompressionMode.LossyStrong:
                dither = "bayer:bayer_scale=5";
                maxColors = settings.Quality switch
                {
                    <= 2 => 64,
                    <= 4 => 48,
                    <= 6 => 32,
                    <= 8 => 24,
                    _ => 16
                };
                statsMode = "diff";
                extraFilters = "mpdecimate,";
                paletteUseExtra = ":diff_mode=rectangle";
                outputFlags = "-gifflags +transdiff ";
                break;
        }
    }

    private static string RequireFfmpeg()
    {
        var path = FfmpegLocator.Find();
        if (path is null)
            throw new FileNotFoundException(Loc.Get("FfmpegExeMissing"));
        return path;
    }

    private static ProcessStartInfo CreateProcess(string exe, string args) => new()
    {
        FileName = exe,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static string FormatSeconds(TimeSpan ts) =>
        ts.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static TimeSpan ParseClock(Match match)
    {
        var h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var s = double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
    }
}
