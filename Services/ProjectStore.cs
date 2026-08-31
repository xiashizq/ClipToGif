using System.IO;
using System.Text.Json;
using ClipToGif.Models;

namespace ClipToGif.Services;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _storePath;

    public ProjectStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipToGif");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "gifs"));
        _storePath = Path.Combine(root, "library.json");
        OutputDirectory = Path.Combine(root, "gifs");
    }

    public string OutputDirectory { get; }

    public LibraryData Load()
    {
        if (!File.Exists(_storePath))
            return new LibraryData();

        try
        {
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<LibraryData>(json, JsonOptions) ?? new LibraryData();
        }
        catch
        {
            return new LibraryData();
        }
    }

    public void Save(IEnumerable<VideoItem> videos)
    {
        var data = new LibraryData
        {
            Videos = videos.Select(v => new VideoRecord
            {
                Id = v.Id,
                FilePath = v.FilePath,
                DisplayName = v.DisplayName,
                DurationSeconds = v.Duration.TotalSeconds,
                Width = v.Width,
                Height = v.Height,
                Fps = v.Fps,
                Gifs = v.Gifs
                    .Where(g => File.Exists(g.FilePath))
                    .Select(g => new GifRecord
                    {
                        Id = g.Id,
                        VideoId = g.VideoId,
                        FilePath = g.FilePath,
                        DisplayName = g.DisplayName,
                        StartSeconds = g.Start.TotalSeconds,
                        EndSeconds = g.End.TotalSeconds,
                        Width = g.Width,
                        Height = g.Height,
                        Fps = g.Fps,
                        Quality = g.Quality,
                        CreatedAt = g.CreatedAt,
                        FileSizeBytes = g.FileSizeBytes
                    }).ToList()
            }).ToList()
        };

        File.WriteAllText(_storePath, JsonSerializer.Serialize(data, JsonOptions));
    }
}

public sealed class LibraryData
{
    public List<VideoRecord> Videos { get; set; } = [];
}

public sealed class VideoRecord
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public List<GifRecord> Gifs { get; set; } = [];
}

public sealed class GifRecord
{
    public Guid Id { get; set; }
    public Guid VideoId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int Quality { get; set; }
    public DateTime CreatedAt { get; set; }
    public long FileSizeBytes { get; set; }
}
