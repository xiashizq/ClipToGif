using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipToGif.Models;

public sealed class GifExportSettings : INotifyPropertyChanged
{
    private int _width = 480;
    private int _height; // 0 = 按比例
    private double _fps = 10;
    private int _quality = 5; // 1(高) ~ 10(小)
    private bool _keepAspectRatio = true;

    public int Width
    {
        get => _width;
        set => SetField(ref _width, Math.Clamp(value, 16, 4096));
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, Math.Clamp(value, 0, 4096));
    }

    public double Fps
    {
        get => _fps;
        set => SetField(ref _fps, Math.Clamp(value, 1, 60));
    }

    /// <summary>质量档位：1 最高清晰，10 体积最小。</summary>
    public int Quality
    {
        get => _quality;
        set => SetField(ref _quality, Math.Clamp(value, 1, 10));
    }

    public bool KeepAspectRatio
    {
        get => _keepAspectRatio;
        set => SetField(ref _keepAspectRatio, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
