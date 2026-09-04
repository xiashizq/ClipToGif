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
    private double _selectionStart;
    private double _selectionEnd;
    private bool _rangePlayback;
    private bool _suppressRangeStop;
    private double _rangePlayEnd;
    private bool _isPlaying;
    private int _seekGeneration;
    private bool _engineSeeking;

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? SeekToStartRequested;
    public event EventHandler? CropChanged;

    public bool HasMedia => _hasMedia && File.Exists(_currentPath ?? string.Empty);

    public bool IsPlaying => _isPlaying;

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
        UpdatePlaybackCaptions();
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

    public void SetSelectionRange(double start, double end)
    {
        _selectionStart = Math.Min(start, end);
        _selectionEnd = Math.Max(start, end);
        if (!_rangePlayback)
            return;

        _rangePlayEnd = _durationSeconds > 0
            ? Math.Clamp(_selectionEnd, 0, _durationSeconds)
            : _selectionEnd;
        if (PositionSeconds >= _rangePlayEnd - 0.04)
            _ = StopAtRangeEndAsync();
    }

    public void Play() => _ = PlayAsync();

    private async Task PlayAsync()
    {
        if (!_hasMedia) return;
        SetPlaying(true, rangePlayback: false);
        try
        {
            ApplyVolume();
            if (_durationSeconds > 0 && PositionSeconds >= _durationSeconds - 0.08)
                await Media.Seek(TimeSpan.Zero);
            await Media.Play();
        }
        catch
        {
            SetPlaying(false);
        }
    }

    public void PlayRange() => _ = PlayRangeAsync();

    private async Task PlayRangeAsync()
    {
        if (!_hasMedia) return;

        var duration = Math.Max(0, _durationSeconds);
        var start = Math.Clamp(_selectionStart, 0, duration);
        var end = Math.Clamp(_selectionEnd, 0, duration);
        if (end - start < 0.05)
            end = Math.Min(duration, start + 0.05);

        _rangePlayEnd = end;
        SetPlaying(true, rangePlayback: true);
        try
        {
            var pos = PositionSeconds;
            if (pos < start - 0.04 || pos >= end - 0.08)
            {
                _suppressRangeStop = true;
                try
                {
                    await Media.Seek(TimeSpan.FromSeconds(start));
                    PositionChanged?.Invoke(this, start);
                }
                finally
                {
                    _suppressRangeStop = false;
                }
            }

            ApplyVolume();
            await Media.Play();
        }
        catch
        {
            SetPlaying(false);
        }
    }

    public void Pause() => _ = PauseAsync();

    private async Task PauseAsync()
    {
        SetPlaying(false);
        try { await Media.Pause(); } catch { /* ignore */ }
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
        if (_rangePlayback)
        {
            _rangePlayback = false;
            UpdatePlaybackCaptions();
        }

        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        var gen = ++_seekGeneration;
        var resume = _isPlaying;
        try
        {
            await Media.Seek(TimeSpan.FromSeconds(seconds));
            if (gen != _seekGeneration) return;

            if (!resume)
                await Media.Pause();

            PositionChanged?.Invoke(this, seconds);
        }
        catch
        {
            if (gen == _seekGeneration)
                PositionChanged?.Invoke(this, seconds);
        }
        finally
        {
            if (gen == _seekGeneration)
                ApplyVolume();
        }
    }

    private async Task StopAtRangeEndAsync()
    {
        if (_suppressRangeStop) return;
        _suppressRangeStop = true;
        SetPlaying(false);
        try
        {
            await Media.Pause();
            var end = _durationSeconds > 0
                ? Math.Clamp(_rangePlayEnd, 0, _durationSeconds)
                : Math.Max(0, _rangePlayEnd);
            await Media.Seek(TimeSpan.FromSeconds(end));
            PositionChanged?.Invoke(this, end);
        }
        finally
        {
            _suppressRangeStop = false;
        }
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
        _rangePlayback = false;
        _isPlaying = false;
        HideCrop();
        try { await Media.Close(); } catch { /* ignore */ }
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetChromeEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = enabled;
        PlayRangeButton.IsEnabled = enabled;
        SeekStartButton.IsEnabled = enabled;
        MuteButton.IsEnabled = enabled;
        VolumeSlider.IsEnabled = enabled;
        if (CropLayer is not null)
            CropLayer.IsHitTestVisible = enabled;
        if (ResetCropButton is not null)
            ResetCropButton.IsEnabled = enabled && CropLayer is { HasCrop: true };
    }

    private void Media_OnMediaInitializing(object? sender, MediaInitializingEventArgs e)
    {
        e.Configuration.GlobalOptions.FlagEnableFastSeek = false;
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

        UpdatePlaybackCaptions();
        MediaOpened?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Media_OnMediaEnded(object? sender, EventArgs e)
    {
        SetPlaying(false);
        MediaEnded?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Media_OnSeekingStarted(object? sender, EventArgs e) =>
        _engineSeeking = true;

    private void Media_OnSeekingEnded(object? sender, EventArgs e)
    {
        _engineSeeking = false;
        if (!_isPlaying)
            _ = Media.Pause();
        ApplyVolume();
    }

    private void Media_OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if ((_engineSeeking || Media.IsSeeking) && !_isPlaying)
            return;

        var seconds = e.Position.TotalSeconds;
        PositionChanged?.Invoke(this, seconds);
        if (_rangePlayback && !_suppressRangeStop && seconds >= _rangePlayEnd - 0.04)
            _ = StopAtRangeEndAsync();
    }

    private void Media_OnMediaStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_hasMedia) return;
        if (IsPlaying && _rangePlayback)
            Play();
        else
            TogglePlay();
    }

    private void PlayRangeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_hasMedia) return;
        if (IsPlaying && _rangePlayback)
            Pause();
        else
            PlayRange();
    }

    private void SeekStartButton_OnClick(object sender, RoutedEventArgs e) =>
        SeekToStartRequested?.Invoke(this, EventArgs.Empty);

    private void UpdatePlaybackCaptions()
    {
        var fullPlaying = _isPlaying && !_rangePlayback;
        var rangePlaying = _isPlaying && _rangePlayback;
        ApplyPlaybackButton(
            PlayPauseButton,
            Loc.Get(fullPlaying ? "Pause" : "Play"),
            Loc.Get(fullPlaying ? "PauseTooltip" : "PlayTooltip"),
            fullPlaying);
        ApplyPlaybackButton(
            PlayRangeButton,
            Loc.Get(rangePlaying ? "Pause" : "PlayRange"),
            Loc.Get(rangePlaying ? "PauseRangeTooltip" : "PlayRangeTooltip"),
            rangePlaying);
    }

    private void ApplyPlaybackButton(Button? button, string content, string toolTip, bool active)
    {
        if (button is null) return;
        button.Style = (Style)FindResource(active ? "PlayingButton" : "VolumeButton");
        button.Content = content;
        button.ToolTip = toolTip;
    }

    private void SetPlaying(bool playing, bool rangePlayback = false)
    {
        _isPlaying = playing;
        _rangePlayback = playing && rangePlayback;
        UpdatePlaybackCaptions();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
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
            ? ts.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
    }
}
