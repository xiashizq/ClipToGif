using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ClipToGif.Models;

public sealed class VideoItem : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private TimeSpan _duration;
    private bool _isSelected;
    private int _width;
    private int _height;
    private double _fps;
    private bool _isMissing;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string FilePath { get; init; } = string.Empty;

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public TimeSpan Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public double Fps
    {
        get => _fps;
        set => SetField(ref _fps, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>仅链接路径；源文件被移动/删除后为 true。</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (!SetField(ref _isMissing, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLine)));
        }
    }

    public string StatusLine => IsMissing
        ? "视频文件已不存在"
        : $"{Duration:mm\\:ss} · {GifCount} 个 GIF";

    public ObservableCollection<GifItem> Gifs { get; } = [];

    public int GifCount => Gifs.Count;

    public VideoItem()
    {
        Gifs.CollectionChanged += OnGifsChanged;
    }

    public void RefreshExists()
    {
        IsMissing = string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath);
    }

    private void OnGifsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GifCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLine)));
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
