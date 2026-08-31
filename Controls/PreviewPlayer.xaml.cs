using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipToGif.Localization;
using ClipToGif.Models;
using Unosquare.FFME.Common;

namespace ClipToGif.Controls;

public partial class PreviewPlayer : UserControl
{
    private bool _isMuted;
    private bool _hasMedia;
    private double _durationSeconds;
    private double _volumeBeforeMute = 0.8;
    private string? _currentPath;
    private int _openGeneration;
    private bool _preferHardware = true;
    private bool _softwareFallbackTried;

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? SeekToStartRequested;
    public event EventHandler? CropChanged;

    public bool HasMedia => _hasMedia && File.Exists(_currentPath ?? string.Empty);

    public bool IsPlaying => Media.IsPlaying;

    public double PositionSeconds => Media.Position.TotalSeconds;

    public double DurationSeconds => _durationSeconds;

    public PreviewPlayer()
    {
        InitializeComponent();
        PreviewKeyDown += PreviewPlayer_OnPreviewKeyDown;
        ApplyVolume();
    }

    public void ApplyLanguage()
    {
        if (EmptyHint.Visibility == Visibility.Visible &&
            string.IsNullOrWhiteSpace(_currentPath))
            EmptyHint.Text = Loc.Get("EmptyHint");
        UpdatePlayPauseCaption();
        ApplyVolume();
        UpdateCropCaption();
    }

    public void ShowEmpty()
    {
        _ = CloseMediaAsync();
        EmptyHint.Text = Loc.Get("EmptyHint");
        EmptyHint.Visibility = Visibility.Visible;
        MissingOverlay.Visibility = Visibility.Collapsed;
        HideCrop();
        SetChromeEnabled(false);
    }

    public void ShowMissing(string path)
    {
        _ = CloseMediaAsync();
        EmptyHint.Visibility = Visibility.Collapsed;
        MissingOverlay.Visibility = Visibility.Visible;
        MissingPathText.Text = path;
        HideCrop();
        SetChromeEnabled(false);
    }

    public void Open(string path) => _ = OpenAsync(path);

    private async Task OpenAsync(string path)
    {
        var gen = ++_openGeneration;
        EmptyHint.Visibility = Visibility.Collapsed;
        MissingOverlay.Visibility = Visibility.Collapsed;
        _currentPath = path;
        _hasMedia = true;
        SetChromeEnabled(true);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);

        _preferHardware = true;
        _softwareFallbackTried = false;

        try
        {
            await Media.Open(new Uri(path));
            if (gen != _openGeneration) return;
            await Media.Pause();
            ApplyVolume();
        }
        catch (Exception ex)
        {
            if (gen != _openGeneration) return;
            if (await TryOpenSoftwareAsync(path, gen))
                return;

            _hasMedia = false;
            EmptyHint.Text = Loc.Format("CannotOpenVideo", ex.Message);
            EmptyHint.Visibility = Visibility.Visible;
            HideCrop();
            SetChromeEnabled(false);
        }
    }

    private async Task<bool> TryOpenSoftwareAsync(string path, int gen)
    {
        if (_softwareFallbackTried || gen != _openGeneration)
            return false;

        _softwareFallbackTried = true;
        _preferHardware = false;
        try
        {
            try { await Media.Close(); } catch { /* ignore */ }
            if (gen != _openGeneration) return true;
            await Media.Open(new Uri(path));
            if (gen != _openGeneration) return true;
            await Media.Pause();
            ApplyVolume();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Play() => _ = PlayAsync();

    private async Task PlayAsync()
    {
        if (!_hasMedia) return;
        if (_durationSeconds > 0 && PositionSeconds >= _durationSeconds - 0.08)
            await Media.Seek(TimeSpan.Zero);
        await Media.Play();
        UpdatePlayPauseCaption();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() => _ = PauseAsync();

    private async Task PauseAsync()
    {
        await Media.Pause();
        UpdatePlayPauseCaption();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlay()
    {
        if (!_hasMedia) return;
        if (IsPlaying) Pause();
        else Play();
    }

    public void Stop() => _ = PauseAsync();

    public void Seek(double seconds) => _ = SeekAsync(seconds);

    private async Task SeekAsync(double seconds)
    {
        if (!_hasMedia || _durationSeconds <= 0) return;
        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        await Media.Seek(TimeSpan.FromSeconds(seconds));
        PositionChanged?.Invoke(this, seconds);
    }

    public void SeekToStart() => Seek(0);

    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (VolumeSlider.Value != volume)
            VolumeSlider.Value = volume;
        ApplyVolume();
    }

    public void CloseMedia() => _ = CloseMediaAsync();

    private async Task CloseMediaAsync()
    {
        _openGeneration++;
        _hasMedia = false;
        _currentPath = null;
        _durationSeconds = 0;
        HideCrop();
        try { await Media.Close(); } catch { /* ignore */ }
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetChromeEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = enabled;
        SeekStartButton.IsEnabled = enabled;
        MuteButton.IsEnabled = enabled;
        VolumeSlider.IsEnabled = enabled;
        if (CropLayer is not null)
            CropLayer.IsHitTestVisible = enabled;
        if (ResetCropButton is not null)
            ResetCropButton.IsEnabled = enabled && CropLayer is { HasCrop: true };
    }

    private void Media_OnMediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        if (!_preferHardware)
        {
            e.Options.VideoHardwareDevices = [];
            return;
        }

        var video = e.Info.Streams.Values.FirstOrDefault(stream => stream.HardwareDevices is { Count: > 0 });
        if (video is null)
            return;

        e.Options.VideoHardwareDevices = OrderHardwareDevices(video.HardwareDevices);
    }

    private void Media_OnMediaFailed(object? sender, MediaFailedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || _softwareFallbackTried)
            return;

        var path = _currentPath;
        var gen = _openGeneration;
        Dispatcher.BeginInvoke(async () =>
        {
            if (gen != _openGeneration || path is null) return;
            await TryOpenSoftwareAsync(path, gen);
        });
    }

    private static HardwareDeviceInfo[] OrderHardwareDevices(IReadOnlyList<HardwareDeviceInfo> devices)
    {
        string[] preferred = ["d3d11va", "dxva2", "cuda", "qsv", "vulkan"];
        return devices
            .OrderBy(device =>
            {
                var name = device.DeviceTypeName?.ToLowerInvariant() ?? string.Empty;
                var index = Array.FindIndex(preferred, item => name.Contains(item));
                return index < 0 ? preferred.Length : index;
            })
            .ToArray();
    }

    private void Media_OnMediaOpened(object sender, MediaOpenedEventArgs e)
    {
        var duration = Media.NaturalDuration;
        if (duration.HasValue && duration.Value.TotalSeconds > 0)
            _durationSeconds = duration.Value.TotalSeconds;
        else if (e.Info?.Duration.TotalSeconds > 0)
            _durationSeconds = e.Info.Duration.TotalSeconds;

        if ((CropLayer?.VideoWidth ?? 0) <= 0 &&
            Media.NaturalVideoWidth > 0 && Media.NaturalVideoHeight > 0)
            SetVideoSize(Media.NaturalVideoWidth, Media.NaturalVideoHeight);

        UpdatePlayPauseCaption();
        MediaOpened?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Media_OnMediaEnded(object? sender, EventArgs e)
    {
        MediaEnded?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Media_OnPositionChanged(object? sender, PositionChangedEventArgs e) =>
        PositionChanged?.Invoke(this, e.Position.TotalSeconds);

    private void Media_OnMediaStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        UpdatePlayPauseCaption();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void SeekStartButton_OnClick(object sender, RoutedEventArgs e) =>
        SeekToStartRequested?.Invoke(this, EventArgs.Empty);

    private void UpdatePlayPauseCaption()
    {
        if (PlayPauseButton is null) return;
        PlayPauseButton.Content = Loc.Get(IsPlaying ? "Pause" : "Play");
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue > 0.001 && _isMuted)
            _isMuted = false;
        ApplyVolume();
    }

    private void MuteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            VolumeSlider.Value = _volumeBeforeMute <= 0.01 ? 0.8 : _volumeBeforeMute;
        }
        else
        {
            _volumeBeforeMute = VolumeSlider.Value;
            _isMuted = true;
        }

        ApplyVolume();
    }

    private void ApplyVolume()
    {
        if (VolumeSlider is null || VolumeText is null || MuteButton is null || Media is null)
            return;

        var vol = VolumeSlider.Value;
        VolumeText.Text = $"{(int)Math.Round(vol * 100)}%";
        MuteButton.Content = Loc.Get(_isMuted || vol <= 0.001 ? "Unmute" : "Mute");
        Media.IsMuted = _isMuted;
        Media.Volume = _isMuted ? 0 : vol;
    }

    public VideoCrop? GetCrop() => CropLayer?.GetCrop();

    public void SetVideoSize(int width, int height)
    {
        if (CropLayer is null) return;
        CropLayer.SetVideoSize(width, height);
        UpdateCropCaption();
    }

    public void ResetCrop()
    {
        CropLayer?.ResetCrop();
        UpdateCropCaption();
    }

    private void HideCrop()
    {
        if (CropLayer is null) return;
        CropLayer.ResetCrop();
        CropLayer.Visibility = Visibility.Collapsed;
        UpdateCropCaption();
    }

    private void CropLayer_OnCropChanged(object? sender, EventArgs e)
    {
        UpdateCropCaption();
        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetCropButton_OnClick(object sender, RoutedEventArgs e) =>
        ResetCrop();

    private void UpdateCropCaption()
    {
        if (CropSizeText is null || ResetCropButton is null)
            return;

        var crop = CropLayer?.GetCrop();
        if (crop is null)
        {
            CropSizeText.Text = Loc.Get("CropHint");
            ResetCropButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CropSizeText.Text = Loc.Format("CropSize", crop.Width, crop.Height);
            ResetCropButton.Visibility = Visibility.Visible;
            ResetCropButton.IsEnabled = PlayPauseButton.IsEnabled;
        }
    }

    private void PreviewPlayer_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            Seek(PositionSeconds - 5);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Seek(PositionSeconds + 5);
            e.Handled = true;
        }
    }

    public static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
