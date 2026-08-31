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
    public IProgress<string>? Status { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class GifConversionResult
{
    public required GifExportSettings Settings { get; init; }
    public required long FileSizeBytes { get; init; }
    public bool ExceededSizeLimit { get; init; }
    public int Attempts { get; init; }
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

    private static readonly Regex VideoSizeRegex = new(
        @"(?<w>\d{2,5})x(?<h>\d{2,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FpsValueRegex = new(
        @"(?<fps>\d+(?:\.\d+)?)\s*fps\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TbrValueRegex = new(
        @"(?<fps>\d+(?:\.\d+)?)\s*tbr\b",
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
            $"-v error -select_streams v " +
            $"-show_entries stream=width,height,avg_frame_rate,r_frame_rate,nb_frames,duration:stream_disposition=attached_pic:format=duration " +
            $"-of json \"{videoPath}\"";

        var psi = CreateProcess(ffprobe, args);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(Loc.Get("CannotStartFfprobe"));
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(Loc.Get("FfprobeFailed"));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidOperationException(Loc.Get("FfprobeFailed"));

        var stream = PickPrimaryVideoStream(streams);
        var width = stream.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var w) ? w : 0;
        var height = stream.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var h) ? h : 0;

        var durationSeconds = 0d;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durEl))
        {
            TryParseDurationSeconds(durEl, out durationSeconds);
        }

        if (durationSeconds <= 0 && stream.TryGetProperty("duration", out var streamDur))
            TryParseDurationSeconds(streamDur, out durationSeconds);

        return new VideoMediaInfo
        {
            Width = width,
            Height = height,
            Fps = ResolveFps(stream, durationSeconds),
            Duration = durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : TimeSpan.Zero
        };
    }

    private static JsonElement PickPrimaryVideoStream(JsonElement streams)
    {
        JsonElement? selected = null;
        var selectedWidth = -1;
        JsonElement? fallback = null;

        foreach (var stream in streams.EnumerateArray())
        {
            fallback ??= stream;
            if (IsAttachedPicture(stream))
                continue;

            var avg = ParseFrameRate(stream, "avg_frame_rate");
            var r = ParseFrameRate(stream, "r_frame_rate");
            if (avg <= 0 && r >= 1000)
                continue;

            var width = stream.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var w) ? w : 0;
            if (selected is null || width > selectedWidth)
            {
                selected = stream;
                selectedWidth = width;
            }
        }

        return selected ?? fallback ?? throw new InvalidOperationException(Loc.Get("FfprobeFailed"));
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("attached_pic", out var flag) &&
        flag.ValueKind == JsonValueKind.Number &&
        flag.GetInt32() == 1;

    private static double ResolveFps(JsonElement stream, double durationSeconds)
    {
        var avg = ParseFrameRate(stream, "avg_frame_rate");
        var r = ParseFrameRate(stream, "r_frame_rate");
        var fromFrames = 0d;
        if (TryParsePositiveLong(stream, "nb_frames", out var frames) && durationSeconds > 0.01)
            fromFrames = frames / durationSeconds;

        const double closeRatio = 0.03;
        if (IsPlausibleFps(avg) && IsPlausibleFps(r) &&
            Math.Abs(avg - r) / Math.Max(avg, r) <= closeRatio)
            return Math.Round(r, 3);
        if (IsPlausibleFps(avg))
            return Math.Round(avg, 3);
        if (IsPlausibleFps(fromFrames))
            return Math.Round(fromFrames, 3);
        if (IsPlausibleFps(r))
            return Math.Round(r, 3);
        return 10;
    }

    private static bool IsPlausibleFps(double fps) => fps is >= 1 and < 1000;

    private static double ParseFrameRate(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var el) || !TryGetNumberText(el, out var text))
            return 0;
        if (text is "0/0")
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

    private static bool TryParseDurationSeconds(JsonElement el, out double seconds)
    {
        seconds = 0;
        return TryGetNumberText(el, out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
               seconds > 0;
    }

    private static bool TryParsePositiveLong(JsonElement stream, string name, out long value)
    {
        value = 0;
        return stream.TryGetProperty(name, out var el) &&
               TryGetNumberText(el, out var text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

    private static bool TryGetNumberText(JsonElement el, out string text)
    {
        text = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(text) && text is not "N/A";
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

        foreach (var rawLine in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf("Video:", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (line.Contains("attached pic", StringComparison.OrdinalIgnoreCase))
                continue;

            var sizeMatch = VideoSizeRegex.Match(line);
            if (!sizeMatch.Success)
                continue;

            width = int.Parse(sizeMatch.Groups["w"].Value, CultureInfo.InvariantCulture);
            height = int.Parse(sizeMatch.Groups["h"].Value, CultureInfo.InvariantCulture);

            var fpsMatch = FpsValueRegex.Match(line);
            if (!fpsMatch.Success)
                fpsMatch = TbrValueRegex.Match(line);
            if (fpsMatch.Success &&
                double.TryParse(fpsMatch.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                IsPlausibleFps(parsed))
            {
                fps = parsed;
            }

            break;
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

        var crop = settings.Crop is { Width: > 0, Height: > 0 } c
            ? $"{c.ToFilter()},"
            : "";

        var filter =
            $"{crop}fps={fps},{extraFilters}{scale},split[s0][s1];" +
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

    public async Task<GifConversionResult> ConvertFittingAsync(GifConversionRequest request)
    {
        var settings = CloneSettings(request.Settings);
        var limit = settings.MaxBytes;
        var target = limit <= 0 ? 0 : (long)(limit * 0.97);
        const int maxAttempts = 6;

        if (target > 0)
            PreflightForSize(settings, request.End - request.Start, target);

        var attempts = 0;
        var lastSize = 0L;
        while (true)
        {
            attempts++;
            request.CancellationToken.ThrowIfCancellationRequested();

            if (target > 0)
            {
                request.Status?.Report(attempts == 1
                    ? Loc.Format("FittingGifStart", FormatFileSize(limit))
                    : Loc.Format("FittingGifRetry", FormatFileSize(limit), attempts));
            }

            var attempt = attempts;
            var innerProgress = request.Progress is null
                ? null
                : new Progress<double>(p =>
                {
                    var overall = target > 0
                        ? Math.Clamp((attempt - 1 + p) / maxAttempts, 0, 1)
                        : p;
                    request.Progress.Report(overall);
                });

            await ConvertAsync(new GifConversionRequest
            {
                VideoPath = request.VideoPath,
                OutputPath = request.OutputPath,
                Start = request.Start,
                End = request.End,
                Settings = settings,
                Progress = innerProgress,
                CancellationToken = request.CancellationToken
            });

            lastSize = new FileInfo(request.OutputPath).Length;
            if (target <= 0 || lastSize <= target)
            {
                request.Progress?.Report(1);
                return new GifConversionResult
                {
                    Settings = settings,
                    FileSizeBytes = lastSize,
                    Attempts = attempts
                };
            }

            if (attempts >= maxAttempts || !ShrinkForRetry(settings, lastSize, target))
            {
                request.Progress?.Report(1);
                return new GifConversionResult
                {
                    Settings = settings,
                    FileSizeBytes = lastSize,
                    ExceededSizeLimit = lastSize > limit,
                    Attempts = attempts
                };
            }
        }
    }

    private static GifExportSettings CloneSettings(GifExportSettings source) => new()
    {
        Width = source.Width,
        Height = source.Height,
        Fps = source.Fps,
        Quality = source.Quality,
        KeepAspectRatio = source.KeepAspectRatio,
        Compression = source.Compression,
        Crop = source.Crop,
        MaxBytes = source.MaxBytes
    };

    /// <summary>
    /// 体积预估很不准。只在极长/极高分辨率片段上预先缩小一点，避免第一次就编码一个明显会超限很多的文件。
    /// 不在这里降帧率、降质量或开强压缩，否则会一下子压到远小于 10MB。
    /// </summary>
    private static void PreflightForSize(GifExportSettings settings, TimeSpan duration, long targetBytes)
    {
        var height = EstimateHeight(settings);
        var frames = Math.Max(1, duration.TotalSeconds * settings.Fps);
        var estimated = settings.Width * (double)height * frames * 0.10;
        if (estimated <= targetBytes * 8)
            return;

        var scale = Math.Clamp(Math.Sqrt(targetBytes * 2.0 / estimated), 0.4, 0.9);
        ApplyScale(settings, scale);
    }

    /// <summary>
    /// 按上一次真实体积往上限靠：优先只改分辨率，一次只动一个主要杠杆，避免叠加上 fps/质量/有损后过冲到 2–3MB。
    /// </summary>
    private static bool ShrinkForRetry(GifExportSettings settings, long currentBytes, long targetBytes)
    {
        var beforeW = settings.Width;
        var beforeFps = settings.Fps;
        var beforeQ = settings.Quality;
        var beforeC = settings.Compression;

        var need = targetBytes / (double)currentBytes;
        var widthScale = Math.Sqrt(need) * 0.98;
        var proposedW = ToEvenWidth(settings.Width * widthScale);

        if (proposedW >= 400 && proposedW < settings.Width)
        {
            SetWidth(settings, proposedW);
            return true;
        }

        if (settings.Fps > 12.05 || settings.Width > 400)
        {
            var widthFactor = Math.Min(1, 400.0 / settings.Width);
            SetWidth(settings, Math.Min(settings.Width, 400));
            var fpsNeed = need / (widthFactor * widthFactor) * 0.98;
            var newFps = Math.Max(12, Math.Round(settings.Fps * Math.Min(1, fpsNeed), 2));
            if (newFps < settings.Fps - 0.05)
                settings.Fps = newFps;

            return settings.Width != beforeW || Math.Abs(settings.Fps - beforeFps) > 0.01;
        }

        if (settings.Width > 240)
        {
            var floorW = Math.Max(240, ToEvenWidth(settings.Width * Math.Max(widthScale, 0.72)));
            if (floorW < settings.Width)
            {
                SetWidth(settings, floorW);
                return true;
            }
        }

        if (settings.Quality < 8)
        {
            settings.Quality++;
            return true;
        }

        if (settings.Fps > 8)
        {
            settings.Fps = Math.Max(8, Math.Round(settings.Fps * 0.88, 2));
            return true;
        }

        if (settings.Compression is not GifCompressionMode.LossyBayer
            and not GifCompressionMode.LossyStrong)
        {
            settings.Compression = GifCompressionMode.LossyBayer;
            return true;
        }

        if (settings.Compression != GifCompressionMode.LossyStrong)
        {
            settings.Compression = GifCompressionMode.LossyStrong;
            return true;
        }

        if (settings.Width > 160)
        {
            SetWidth(settings, Math.Max(160, settings.Width - 40));
            return true;
        }

        return settings.Width != beforeW ||
               Math.Abs(settings.Fps - beforeFps) > 0.01 ||
               settings.Quality != beforeQ ||
               settings.Compression != beforeC;
    }

    private static void ApplyScale(GifExportSettings settings, double scale) =>
        SetWidth(settings, ToEvenWidth(settings.Width * scale));

    private static void SetWidth(GifExportSettings settings, int newW)
    {
        newW = Math.Max(16, ToEvenWidth(newW));
        if (newW >= settings.Width)
            return;
        if (settings.Height > 0)
            settings.Height = Math.Max(1, (int)Math.Round(settings.Height * (newW / (double)settings.Width)));
        settings.Width = newW;
    }

    private static int ToEvenWidth(double width) =>
        Math.Max(16, (int)Math.Round(width) / 2 * 2);

    private static int EstimateHeight(GifExportSettings settings)
    {
        if (settings.Height > 0)
            return settings.Height;
        if (settings.Crop is { Width: > 0, Height: > 0 } crop)
            return Math.Max(1, (int)Math.Round(settings.Width * (double)crop.Height / crop.Width));
        return settings.Width;
    }

    public static string FormatFileSize(long bytes) =>
        bytes < 1024 * 1024
            ? $"{bytes / 1024.0:0.#} KB"
            : $"{bytes / (1024.0 * 1024.0):0.##} MB";

    private static (int MaxColors, string Dither) ResolveQuality(int quality) => quality switch
    {
        1 => (256, "sierra2_4a"),
        2 => (224, "sierra2_4a"),
        3 => (192, "floyd_steinberg"),
        4 => (160, "floyd_steinberg"),
        5 => (128, "bayer:bayer_scale=2"),
        6 => (96, "bayer:bayer_scale=3"),
        7 => (64, "bayer:bayer_scale=4"),
        8 => (48, "bayer:bayer_scale=5"),
        9 => (32, "none"),
        _ => (16, "none")
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
        (maxColors, dither) = ResolveQuality(settings.Quality);
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
