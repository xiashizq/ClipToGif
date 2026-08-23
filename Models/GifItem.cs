using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using ClipToGif.Localization;

namespace ClipToGif.Models;

public sealed class GifItem : INotifyPropertyChanged
{
    private string _statusKey = "StatusDone";
    private long _fileSizeBytes;
    private BitmapImage? _thumbnail;

    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid VideoId { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double Fps { get; init; }

    public int Quality { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set
        {
            if (!SetField(ref _fileSizeBytes, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSizeText)));
        }
    }

    public string StatusKey
    {
        get => _statusKey;
        set
        {
            if (!SetField(ref _statusKey, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public string Status => Loc.Get(_statusKey);

    /// <summary>内存中的缩略图，避免 Image 直接绑定路径导致文件被锁定。</summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null)
                TryLoadThumbnail();
            return _thumbnail;
        }
    }

    public string RangeText => $"{Start:mm\\:ss\\.f} → {End:mm\\:ss\\.f}";

    public string SizeText => Height > 0 ? $"{Width}×{Height}" : Loc.Format("GifWidthOnly", Width);

    public void NotifyLocalized()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetaText)));
    }

    public string MetaText => $"{SizeText} · {Fps:0.#} fps · Q{Quality}";

    public string FileSizeText =>
        FileSizeBytes <= 0 ? "-" :
        FileSizeBytes < 1024 * 1024 ? $"{FileSizeBytes / 1024.0:0.#} KB" :
        $"{FileSizeBytes / (1024.0 * 1024.0):0.##} MB";

    public void RefreshThumbnail()
    {
        _thumbnail = null;
        TryLoadThumbnail();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }

    public void ReleaseThumbnail()
    {
        _thumbnail = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }

    private void TryLoadThumbnail()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            // 读入内存后关闭文件流，避免锁定 GIF 导致无法删除
            var bytes = File.ReadAllBytes(FilePath);
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = ms;
            image.DecodePixelWidth = 144;
            image.EndInit();
            image.Freeze();
            _thumbnail = image;
        }
        catch
        {
            _thumbnail = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
